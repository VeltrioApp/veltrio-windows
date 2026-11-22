using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Veltrio.Player;

/// <summary>Puts mpv's video in a XAML <see cref="SwapChainPanel"/>, so the
/// player's controls are ordinary XAML on top of it.
///
/// mpv only renders through OpenGL (its render API), and WinUI composes
/// Direct3D. ANGLE bridges the two: on one D3D11 device, mpv draws with GL
/// ES into a D3D11 texture wrapped as an EGL pbuffer; each frame is copied
/// into a composition swapchain that the panel shows. Everything GL/D3D
/// happens on one render thread, woken by mpv's update callback.</summary>
public sealed unsafe class VideoSurface : IDisposable
{
    private static readonly Guid SwapChainPanelNativeIid = new("63AAD0B8-7C24-40FF-85A8-640D944CC325");
    private static IntPtr _angle;

    private readonly SwapChainPanel _panel;
    private readonly MpvPlayer _player;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private GCHandle _self;
    private volatile bool _stopping;

    // Wanted size in pixels, set from the UI thread.
    private int _wantWidth = 16, _wantHeight = 16;
    private float _scaleX = 1, _scaleY = 1;
    private volatile bool _resize = true;

    // Render-thread state.
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain1? _swapChain;
    private ID3D11Texture2D? _target;
    private IntPtr _eglDevice, _display, _config, _glContext, _surface;
    private IntPtr _renderContext;
    private delegate* unmanaged<void> _glFlush;
    private int _width, _height;
    private long _frames;
    private delegate* unmanaged<int> _glGetError;

    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once mpv has its render context — loading a file
    /// before that leaves mpv with no video output. Faults if the
    /// pipeline can't start.</summary>
    public Task Ready => _ready.Task;

    public VideoSurface(SwapChainPanel panel, MpvPlayer player)
    {
        _panel = panel;
        _player = player;
        _self = GCHandle.Alloc(this);
        panel.SizeChanged += (_, _) => UpdateSize();
        panel.CompositionScaleChanged += (_, _) => UpdateSize();
        UpdateSize();
        _thread = new Thread(Run) { IsBackground = true, Name = "mpv render" };
        _thread.Start();
    }

    private void UpdateSize()
    {
        _scaleX = _panel.CompositionScaleX;
        _scaleY = _panel.CompositionScaleY;
        _wantWidth = Math.Max(16, (int)Math.Round(_panel.ActualWidth * _scaleX));
        _wantHeight = Math.Max(16, (int)Math.Round(_panel.ActualHeight * _scaleY));
        _resize = true;
        _wake.Set();
    }

    // -------------------- render thread --------------------

    private void Run()
    {
        try
        {
            InitGraphics();
            PlayerLog.Write($"[video] D3D11 + ANGLE ready, {_width}x{_height}");
            InitMpv();
            PlayerLog.Write("[video] mpv render context ready");
            _ready.TrySetResult();
            while (!_stopping)
            {
                _wake.WaitOne(250);
                if (_stopping) break;
                if (_resize)
                {
                    try
                    {
                        Resize();
                    }
                    catch (Exception ex)
                    {
                        PlayerLog.Write($"[video] resize error (will retry): {ex.Message}");
                        _resize = true;
                        Thread.Sleep(50);
                        continue;
                    }
                }
                if (_surface == IntPtr.Zero || _target is null) continue;
                var flags = LibMpv.RenderContextUpdate(_renderContext);
                if ((flags & LibMpv.RenderUpdateFrame) != 0) RenderFrame();
            }
        }
        catch (Exception ex)
        {
            PlayerLog.Write($"[video] {ex}");
            _ready.TrySetException(ex);
        }
        finally
        {
            Teardown();
        }
    }

    private void InitGraphics()
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
            out _device, out _context).CheckError();
        // mpv's decoder threads may share the device with ANGLE.
        using (var mt = _device!.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);

        using (var dxgiDevice = _device.QueryInterface<IDXGIDevice>())
        using (var adapter = dxgiDevice.GetAdapter())
        using (var factory = adapter.GetParent<IDXGIFactory2>())
        {
            _width = _wantWidth;
            _height = _wantHeight;
            _swapChain = factory.CreateSwapChainForComposition(_device, new SwapChainDescription1
            {
                Width = (uint)_width,
                Height = (uint)_height,
                Format = Format.B8G8R8A8_UNorm,
                BufferCount = 2,
                BufferUsage = Usage.RenderTargetOutput,
                SampleDescription = new SampleDescription(1, 0),
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = AlphaMode.Ignore,
                Scaling = Scaling.Stretch,
            });
        }
        ApplyScale();

        // The panel only takes its swapchain on the UI thread.
        var swapChain = _swapChain.NativePointer;
        Marshal.AddRef(swapChain);
        _panel.DispatcherQueue.TryEnqueue(() =>
        {
            try { SetPanelSwapChain(_panel, swapChain); }
            finally { Marshal.Release(swapChain); }
        });

        if (_angle == IntPtr.Zero) _angle = NativeLibrary.Load(Egl.Lib, typeof(VideoSurface).Assembly, null);
        _eglDevice = Egl.CreateDeviceANGLE(Egl.D3D11DeviceAngle, _device.NativePointer, IntPtr.Zero);
        if (_eglDevice == IntPtr.Zero) throw new InvalidOperationException($"ANGLE : eglCreateDeviceANGLE (0x{Egl.GetError():X})");
        _display = Egl.GetPlatformDisplayEXT(Egl.PlatformDeviceExt, _eglDevice, null);
        if (_display == IntPtr.Zero || !Egl.Initialize(_display, out _, out _))
            throw new InvalidOperationException($"ANGLE : eglInitialize (0x{Egl.GetError():X})");

        var configAttribs = stackalloc int[]
        {
            Egl.RedSize, 8, Egl.GreenSize, 8, Egl.BlueSize, 8, Egl.AlphaSize, 8,
            Egl.SurfaceType, Egl.PbufferBit, Egl.RenderableType, Egl.OpenGlEs3Bit, Egl.None,
        };
        IntPtr config;
        if (!Egl.ChooseConfig(_display, configAttribs, &config, 1, out var count) || count == 0)
            throw new InvalidOperationException($"ANGLE : eglChooseConfig (0x{Egl.GetError():X})");
        _config = config;

        var contextAttribs = stackalloc int[] { Egl.ContextClientVersion, 3, Egl.None };
        _glContext = Egl.CreateContext(_display, _config, IntPtr.Zero, contextAttribs);
        if (_glContext == IntPtr.Zero) throw new InvalidOperationException($"ANGLE : eglCreateContext (0x{Egl.GetError():X})");

        CreateTarget();
        _glFlush = (delegate* unmanaged<void>)Egl.GetProcAddress("glFlush");
        _glGetError = (delegate* unmanaged<int>)Egl.GetProcAddress("glGetError");
    }

    /// <summary>The D3D11 texture mpv draws into, wrapped as the GL surface.</summary>
    private void CreateTarget()
    {
        _target = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        var attribs = stackalloc int[] { Egl.Width, _width, Egl.Height, _height, Egl.None };
        _surface = Egl.CreatePbufferFromClientBuffer(_display, Egl.D3DTextureAngle, _target.NativePointer, _config, attribs);
        if (_surface == IntPtr.Zero) throw new InvalidOperationException($"ANGLE : eglCreatePbufferFromClientBuffer (0x{Egl.GetError():X})");
        if (!Egl.MakeCurrent(_display, _surface, _surface, _glContext))
            throw new InvalidOperationException($"ANGLE : eglMakeCurrent (0x{Egl.GetError():X})");
    }

    private void DestroyTarget()
    {
        Egl.MakeCurrent(_display, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_surface != IntPtr.Zero) Egl.DestroySurface(_display, _surface);
        _surface = IntPtr.Zero;
        _target?.Dispose();
        _target = null;
    }

    private void Resize()
    {
        _resize = false;
        var w = _wantWidth;
        var h = _wantHeight;
        if (w < 16 || h < 16) return;
        if (w == _width && h == _height)
        {
            ApplyScale();
            return;
        }
        _width = w;
        _height = h;
        DestroyTarget();
        _context!.ClearState();
        _context.Flush();
        _swapChain!.ResizeBuffers(2, (uint)_width, (uint)_height, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
        // ResizeBuffers resets the DXGI swapchain transform on some drivers; always re-apply scale after.
        ApplyScale();
        CreateTarget();
        RenderFrame(); // don't leave a stretched old frame
    }

    /// <summary>The swapchain is in pixels; the panel lays out in DIPs.</summary>
    private void ApplyScale()
    {
        if (!(_scaleX > 0 && _scaleY > 0)) return;
        using var swapChain2 = _swapChain!.QueryInterfaceOrNull<IDXGISwapChain2>();
        if (swapChain2 is not null)
            swapChain2.MatrixTransform = Matrix3x2.CreateScale(1f / _scaleX, 1f / _scaleY);
    }

    private void InitMpv()
    {
        var api = Marshal.StringToCoTaskMemUTF8("opengl");
        try
        {
            var init = new LibMpv.OpenGlInitParams { GetProcAddress = &GetProcAddress, GetProcAddressCtx = IntPtr.Zero };
            var parameters = stackalloc LibMpv.RenderParam[]
            {
                new() { Type = LibMpv.RenderParamApiType, Data = api },
                new() { Type = LibMpv.RenderParamOpenGlInitParams, Data = (IntPtr)(&init) },
                new() { Type = LibMpv.RenderParamInvalid, Data = IntPtr.Zero },
            };
            var error = LibMpv.RenderContextCreate(out _renderContext, _player.Handle, parameters);
            if (error < 0) throw new InvalidOperationException($"mpv (rendu) : {LibMpv.Error(error)}");
        }
        finally
        {
            Marshal.FreeCoTaskMem(api);
        }
        LibMpv.RenderContextSetUpdateCallback(_renderContext, &OnMpvUpdate, GCHandle.ToIntPtr(_self));
    }

    private void RenderFrame()
    {
        var fbo = new LibMpv.OpenGlFbo { Fbo = 0, W = _width, H = _height };
        // The pbuffer is a top-down D3D texture: no flip.
        var flipY = 0;
        var parameters = stackalloc LibMpv.RenderParam[]
        {
            new() { Type = LibMpv.RenderParamOpenGlFbo, Data = (IntPtr)(&fbo) },
            new() { Type = LibMpv.RenderParamFlipY, Data = (IntPtr)(&flipY) },
            new() { Type = LibMpv.RenderParamInvalid, Data = IntPtr.Zero },
        };
        var error = LibMpv.RenderContextRender(_renderContext, parameters);
        if (error < 0 || _frames++ == 0) PlayerLog.Write($"[video] frame {_frames} {_width}x{_height} render={error} gl=0x{_glGetError():X}");
        _glFlush();
        using (var back = _swapChain!.GetBuffer<ID3D11Texture2D>(0))
            _context!.CopyResource(back, _target!);
        _swapChain.Present(1, PresentFlags.None);
        LibMpv.RenderContextReportSwap(_renderContext);
    }

    private void Teardown()
    {
        if (_renderContext != IntPtr.Zero)
        {
            LibMpv.RenderContextFree(_renderContext);
            _renderContext = IntPtr.Zero;
        }
        if (_display != IntPtr.Zero)
        {
            DestroyTarget();
            Egl.MakeCurrent(_display, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_glContext != IntPtr.Zero) Egl.DestroyContext(_display, _glContext);
            Egl.Terminate(_display);
        }
        if (_eglDevice != IntPtr.Zero) Egl.ReleaseDeviceANGLE(_eglDevice);
        _target?.Dispose();
        _swapChain?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }

    // -------------------- callbacks --------------------

    [UnmanagedCallersOnly]
    private static IntPtr GetProcAddress(IntPtr ctx, IntPtr name)
    {
        var symbol = Marshal.PtrToStringUTF8(name)!;
        var address = Egl.GetProcAddress(symbol);
        if (address == IntPtr.Zero && _angle != IntPtr.Zero) NativeLibrary.TryGetExport(_angle, symbol, out address);
        return address;
    }

    /// <summary>mpv has a new frame (any thread): wake the render thread.</summary>
    [UnmanagedCallersOnly]
    private static void OnMpvUpdate(IntPtr ctx)
    {
        if (GCHandle.FromIntPtr(ctx).Target is VideoSurface surface) surface._wake.Set();
    }

    /// <summary>ISwapChainPanelNative::SetSwapChain (WinUI 3's IID).</summary>
    private static void SetPanelSwapChain(SwapChainPanel panel, IntPtr swapChain)
    {
        var unknown = WinRT.MarshalInspectable<SwapChainPanel>.FromManaged(panel);
        try
        {
            var iid = SwapChainPanelNativeIid;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, ref iid, out var native));
            try
            {
                var setSwapChain = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)(*(*(void***)native + 3));
                Marshal.ThrowExceptionForHR(setSwapChain(native, swapChain));
            }
            finally
            {
                Marshal.Release(native);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    /// <summary>Stops rendering and frees the GL/D3D side. Must run before
    /// the <see cref="MpvPlayer"/> is disposed.</summary>
    public void Dispose()
    {
        if (_stopping) return;
        _stopping = true;
        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(3));
        if (_self.IsAllocated) _self.Free();
        try { SetPanelSwapChain(_panel, IntPtr.Zero); } catch { /* panel already gone */ }
    }
}
