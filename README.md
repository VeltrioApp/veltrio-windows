# Veltrio for Windows

Not started yet. This repository holds the CI/CD and the pinned
[veltrio-core](https://github.com/VeltrioApp/veltrio-core) release the
app will build on.

**Planned stack:** To decide — WinUI 3 / C# is the native option. UniFFI has no first-party C# generator; uniffi-bindgen-cs (NordSecurity) is the usual choice and must support the core's UniFFI version.

## The core

`core.version` names the veltrio-core release to use;
`scripts/fetch-core.sh` downloads its `veltrio-core-windows-x64.zip` into `core/`
(gitignored). Needs [`gh`](https://cli.github.com) signed in with access
to `VeltrioApp/veltrio-core`, or `VELTRIO_CORE_DIR=../veltrio-core` to
build from a local checkout.

The core release ships the DLL without bindings until the stack is decided.

## CI/CD

- `ci.yml` — pushes to main, pull requests, manual runs: fetches the core, then builds and tests once
  a `.sln` at the repository root exists. Until then it only checks the core download.
- `release.yml` — tag `vX.Y.Z`: Release build zipped onto the GitHub release (MSIX + signing to add). Skipped until the project exists.

Organisation secrets used: `CORE_RELEASES_TOKEN` (signing secrets come with the packaging choice).
