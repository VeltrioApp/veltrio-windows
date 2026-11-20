# Veltrio for Windows

WinUI 3 (Windows App SDK 2.6) + C# / .NET 10, on top of
[veltrio-core](https://github.com/VeltrioApp/veltrio-core), ported from
the macOS app: same editorial look (true black, tungsten amber, serif
titles), French UI.

- **Accueil**: hero, Continue watching, one numbered row per catalog of
  the enabled addons, "See all" grid.
- **Title page**: details and credits; seasons and episodes for a series;
  streams from every addon, ranked like macOS, filterable by quality and
  source.
- **Recherche**: across the addons' searchable catalogs and the IPTV channels.
- **Collection**: saved and started titles, filters, watched / remove; and
  each connected media server's libraries, a page at a time. Titles the
  server identified open on their title page; the others play directly,
  and unidentified series list their episodes (with resume).
- **Direct**: Xtream Codes accounts (live, films, series and series pages,
  replay), M3U playlists by URL or local file (a provider's get.php link is
  added as Xtream). Search covers Xtream films and series on the provider's
  server.
  TV layout: numbered channel list with what's on, the selected channel
  large with now and up next; a guide grid. The XMLTV guide comes from
  xmltv.php or the playlist's url-tvg header (gzip handled, channels
  matched by id or name). In the player: now/next cards on tune-in and a
  now/next line, the channel list to zap through (Page Up / Page Down), a
  replay panel of past programmes.
- **Sources**: install from URL, enable, reorder, remove, suggestions;
  IPTV playlists; the addon catalogue (Stremio's official collection and
  the community site stremio-addons.net, one-click install); media
  servers: Jellyfin and Emby (address, user, password; the server's kind
  is checked first so a wrong address isn't reported as a wrong
  password), Plex (PIN at plex.tv/link; the server is found through
  plex.tv, local connections first).
- **Direct copies**: on a title page, the copies of that film or episode on
  the connected media servers (matched on the IMDB id the server stores,
  else title and year) and on every Xtream provider (title and year, with
  the provider's "FR | … (2021)" dressing stripped) are listed first among
  the streams, with their video and audio languages. They're looked up
  alongside the addons and don't hold the list back.
- **Debrid** (Settings): Real-Debrid (device code), AllDebrid (PIN, through
  the core), TorBox and Premiumize (API key). Cached torrents get a ⚡ badge
  and go first; playing a torrent goes through debrid before the local
  engine. Keys are encrypted with DPAPI (`secrets.dat`). Real-Debrid no
  longer answers availability checks, so its torrents are tried at
  playback instead (added, played if already downloaded, else removed).
- **Accounts** (Settings): a Stremio account (email and password) imports
  its library (saved titles, watched films, film progress) and installs
  its addons; Trakt (device code) and SIMKL (PIN) import their lists and
  get start / pause / stop scrobbles from the player. Imports only add:
  they never unsave, unwatch or rewind. Trakt and SIMKL need app
  credentials at build time (see Build); without them their cards say so.
- **Player**: libmpv, like macOS. mpv renders through its OpenGL API into
  ANGLE (GL ES over Direct3D 11), on the same D3D11 device as a
  composition swapchain shown by a `SwapChainPanel`; the controls are
  XAML on top: scrubber, ±10/30 s, volume, audio and subtitle tracks,
  subtitles from addons (OpenSubtitles…, the preferred language added
  automatically when the file has none) or a local file, speed, framing
  and zoom, subtitle/audio delay, HDR tone mapping, playback profiles,
  passthrough, media info (I), skip intro from chapters (S), next episode
  with a countdown (N), full screen. Resume and progress. A log goes to
  `player.log` in the data folder.

Not yet on Windows: casting — its client lives in Swift on macOS, not in
the core.

- **Downloads**: right-click a stream on a title page to save it for
  offline (resolved like playback: direct, debrid or the local torrent
  engine), with pause and resume (HTTP ranges). A finished copy is listed
  first among the title's streams and plays from disk with the title's
  progress; the list (play, pause, resume, delete) is in Settings.
- **Settings**: playback defaults (preferred audio and subtitle language,
  decoder, network buffer, next-episode countdown), theme (dark, a warm
  light one, or Windows' mode; applied at launch, with a restart button),
  storage (data folder, the torrent engine's cache with its size and a
  button to clear it).

Data lives in `%LOCALAPPDATA%\Veltrio` (JSON, same shapes as macOS).

Unpackaged and self-contained for now (no MSIX, no separate Windows App
SDK runtime install).

## Build

Windows 10 1904+ / 11, the .NET 10 SDK, and `gh` signed in with access to
`VeltrioApp/veltrio-core`:

```bash
./scripts/fetch-core.sh     # Git Bash: core release from core.version → core/
./scripts/fetch-mpv.sh      # libmpv (LGPL) + CA bundle from mpv.version → mpv/ (needs 7-Zip)
dotnet build Veltrio.Windows.sln -c Debug -p:Platform=x64
```

Trakt and SIMKL are off unless these environment variables are set when
building (they're baked into the app):

- `VELTRIO_TRAKT_CLIENT_ID`, `VELTRIO_TRAKT_CLIENT_SECRET` — an app
  registered at trakt.tv/oauth/applications (redirect URI
  `urn:ietf:wg:oauth:2.0:oob`);
- `VELTRIO_SIMKL_CLIENT_ID` — from simkl.com/settings/developer.

Or open `Veltrio.Windows.sln` in Visual Studio 2022+ (with the WinUI
workload) and run.

## Testing from a Mac

Wine / CrossOver can't run it: Windows App SDK calls an NT API Wine
doesn't implement (`NtCreateWaitCompletionPacket`) and aborts at
startup. A Parallels Windows VM works, Windows on ARM included (the x64
build runs under emulation):

```bash
./scripts/fetch-core.sh
./scripts/fetch-mpv.sh
./scripts/parallels-run.sh     # VM="Windows 11" by default
```

It builds the working tree inside the VM (installing the .NET 10 SDK on
first run) and starts the app on the VM's desktop.

## The core

`core.version` names the veltrio-core release to use;
`scripts/fetch-core.sh` downloads its `veltrio-core-windows-x64.zip` into `core/`
(gitignored). Needs [`gh`](https://cli.github.com) (signed in), or `VELTRIO_CORE_DIR=../veltrio-core` to
build from a local checkout.

The core release ships the DLL with C# bindings (`core/Bindings/veltrio.cs`,
generated by uniffi-bindgen-cs); the project compiles them in.

## CI/CD

- `ci.yml` — pushes to main, pull requests, manual runs: fetches the core, then builds and tests once
  a `.sln` at the repository root exists. Until then it only checks the core download.
- `release.yml` — tag `vX.Y.Z`: Release build zipped onto the GitHub release (MSIX + signing to add). Skipped until the project exists.

Organisation secrets used: `CORE_RELEASES_TOKEN` (optional: the repositories are public, so the workflow token can read core releases); `VELTRIO_TRAKT_CLIENT_ID`, `VELTRIO_TRAKT_CLIENT_SECRET` and `VELTRIO_SIMKL_CLIENT_ID` for releases (optional: Trakt / SIMKL are off without them) (signing secrets come with the packaging choice).
