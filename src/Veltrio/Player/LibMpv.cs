using System.Runtime.InteropServices;

namespace Veltrio.Player;

/// <summary>The slice of libmpv's C API the player uses (client.h,
/// render.h, render_gl.h), from libmpv-2.dll next to the app.</summary>
internal static unsafe partial class LibMpv
{
    private const string Lib = "libmpv-2.dll";

    public const int FormatNone = 0, FormatString = 1, FormatFlag = 3, FormatInt64 = 4, FormatDouble = 5;

    public const int EventNone = 0, EventShutdown = 1, EventLog = 2, EventStartFile = 6, EventEnd = 7,
        EventFileLoaded = 8, EventPlaybackRestart = 21, EventPropertyChange = 22;

    public const int EndFileEof = 0, EndFileStop = 2, EndFileQuit = 3, EndFileError = 4;

    public const int RenderParamInvalid = 0, RenderParamApiType = 1, RenderParamOpenGlInitParams = 2,
        RenderParamOpenGlFbo = 3, RenderParamFlipY = 4;

    public const ulong RenderUpdateFrame = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct Event
    {
        public int EventId;
        public int Error;
        public ulong ReplyUserdata;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventProperty
    {
        public IntPtr Name;
        public int Format;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventEndFile
    {
        public int Reason;
        public int Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventLogMessage
    {
        public IntPtr Prefix;
        public IntPtr Level;
        public IntPtr Text;
        public int LogLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RenderParam
    {
        public int Type;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OpenGlInitParams
    {
        public delegate* unmanaged<IntPtr, IntPtr, IntPtr> GetProcAddress;
        public IntPtr GetProcAddressCtx;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OpenGlFbo
    {
        public int Fbo;
        public int W;
        public int H;
        public int InternalFormat;
    }

    [LibraryImport(Lib, EntryPoint = "mpv_create")]
    public static partial IntPtr Create();

    [LibraryImport(Lib, EntryPoint = "mpv_initialize")]
    public static partial int Initialize(IntPtr ctx);

    [LibraryImport(Lib, EntryPoint = "mpv_terminate_destroy")]
    public static partial void TerminateDestroy(IntPtr ctx);

    [LibraryImport(Lib, EntryPoint = "mpv_set_option_string", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SetOptionString(IntPtr ctx, string name, string data);

    [LibraryImport(Lib, EntryPoint = "mpv_set_property_string", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SetPropertyString(IntPtr ctx, string name, string data);

    [LibraryImport(Lib, EntryPoint = "mpv_get_property_string", StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr GetPropertyString(IntPtr ctx, string name);

    [LibraryImport(Lib, EntryPoint = "mpv_get_property", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int GetProperty(IntPtr ctx, string name, int format, void* data);

    [LibraryImport(Lib, EntryPoint = "mpv_command")]
    public static partial int Command(IntPtr ctx, IntPtr* args);

    [LibraryImport(Lib, EntryPoint = "mpv_observe_property", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int ObserveProperty(IntPtr ctx, ulong userdata, string name, int format);

    [LibraryImport(Lib, EntryPoint = "mpv_request_log_messages", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int RequestLogMessages(IntPtr ctx, string minLevel);

    [LibraryImport(Lib, EntryPoint = "mpv_wait_event")]
    public static partial Event* WaitEvent(IntPtr ctx, double timeout);

    [LibraryImport(Lib, EntryPoint = "mpv_wakeup")]
    public static partial void Wakeup(IntPtr ctx);

    [LibraryImport(Lib, EntryPoint = "mpv_free")]
    public static partial void Free(IntPtr data);

    [LibraryImport(Lib, EntryPoint = "mpv_error_string")]
    public static partial IntPtr ErrorString(int error);

    [LibraryImport(Lib, EntryPoint = "mpv_render_context_create")]
    public static partial int RenderContextCreate(out IntPtr res, IntPtr mpv, RenderParam* parameters);

    [LibraryImport(Lib, EntryPoint = "mpv_render_context_set_update_callback")]
    public static partial void RenderContextSetUpdateCallback(IntPtr ctx, delegate* unmanaged<IntPtr, void> callback, IntPtr callbackCtx);

    [LibraryImport(Lib, EntryPoint = "mpv_render_context_update")]
    public static partial ulong RenderContextUpdate(IntPtr ctx);

    [LibraryImport(Lib, EntryPoint = "mpv_render_context_render")]
    public static partial int RenderContextRender(IntPtr ctx, RenderParam* parameters);

    [LibraryImport(Lib, EntryPoint = "mpv_render_context_report_swap")]
    public static partial void RenderContextReportSwap(IntPtr ctx);

    [LibraryImport(Lib, EntryPoint = "mpv_render_context_free")]
    public static partial void RenderContextFree(IntPtr ctx);

    public static string? Utf8(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);

    public static string Error(int code) => Utf8(ErrorString(code)) ?? $"mpv error {code}";

    /// <summary>mpv_command with a NULL-terminated UTF-8 argv.</summary>
    public static int Command(IntPtr ctx, params string[] args)
    {
        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (var i = 0; i < args.Length; i++) ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            fixed (IntPtr* argv = ptrs) return Command(ctx, argv);
        }
        finally
        {
            foreach (var p in ptrs) if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
        }
    }
}

/// <summary>EGL from ANGLE (Avalonia's build, av_libglesv2.dll, which
/// exports EGL as EGL_* and GL ES through eglGetProcAddress).</summary>
internal static unsafe partial class Egl
{
    public const string Lib = "av_libglesv2.dll";

    public const int None = 0x3038, RedSize = 0x3024, GreenSize = 0x3023, BlueSize = 0x3022, AlphaSize = 0x3021,
        DepthSize = 0x3025, StencilSize = 0x3026, SurfaceType = 0x3033, PbufferBit = 0x0001,
        RenderableType = 0x3040, OpenGlEs3Bit = 0x0040, ContextClientVersion = 0x3098,
        Width = 0x3057, Height = 0x3056, TextureFormat = 0x3080, TextureRgba = 0x305E,
        TextureTarget = 0x3081, Texture2D = 0x305F,
        PlatformDeviceExt = 0x313F, D3D11DeviceAngle = 0x33A1, D3DTextureAngle = 0x33A3;

    [LibraryImport(Lib, EntryPoint = "EGL_GetProcAddress", StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr GetProcAddress(string name);

    [LibraryImport(Lib, EntryPoint = "EGL_CreateDeviceANGLE")]
    public static partial IntPtr CreateDeviceANGLE(int deviceType, IntPtr nativeDevice, IntPtr attribs);

    [LibraryImport(Lib, EntryPoint = "EGL_ReleaseDeviceANGLE")]
    [return: MarshalAs(UnmanagedType.I4)]
    public static partial bool ReleaseDeviceANGLE(IntPtr device);

    [LibraryImport(Lib, EntryPoint = "EGL_GetPlatformDisplayEXT")]
    public static partial IntPtr GetPlatformDisplayEXT(int platform, IntPtr nativeDisplay, int* attribs);

    [LibraryImport(Lib, EntryPoint = "EGL_Initialize")]
    [return: MarshalAs(UnmanagedType.I4)]
    public static partial bool Initialize(IntPtr display, out int major, out int minor);

    [LibraryImport(Lib, EntryPoint = "EGL_Terminate")]
    [return: MarshalAs(UnmanagedType.I4)]
    public static partial bool Terminate(IntPtr display);

    [LibraryImport(Lib, EntryPoint = "EGL_ChooseConfig")]
    [return: MarshalAs(UnmanagedType.I4)]
    public static partial bool ChooseConfig(IntPtr display, int* attribs, IntPtr* configs, int size, out int count);

    [LibraryImport(Lib, EntryPoint = "EGL_CreateContext")]
    public static partial IntPtr CreateContext(IntPtr display, IntPtr config, IntPtr share, int* attribs);

    [LibraryImport(Lib, EntryPoint = "EGL_DestroyContext")]
    [return: MarshalAs(UnmanagedType.I4)]
    public static partial bool DestroyContext(IntPtr display, IntPtr context);

    [LibraryImport(Lib, EntryPoint = "EGL_CreatePbufferFromClientBuffer")]
    public static partial IntPtr CreatePbufferFromClientBuffer(IntPtr display, int bufferType, IntPtr buffer, IntPtr config, int* attribs);

    [LibraryImport(Lib, EntryPoint = "EGL_DestroySurface")]
    [return: MarshalAs(UnmanagedType.I4)]
    public static partial bool DestroySurface(IntPtr display, IntPtr surface);

    [LibraryImport(Lib, EntryPoint = "EGL_MakeCurrent")]
    [return: MarshalAs(UnmanagedType.I4)]
    public static partial bool MakeCurrent(IntPtr display, IntPtr draw, IntPtr read, IntPtr context);

    [LibraryImport(Lib, EntryPoint = "EGL_GetError")]
    public static partial int GetError();
}
