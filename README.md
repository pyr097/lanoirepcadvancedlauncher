# L.A. Noire Launcher

A launcher that works only on **your own** L.A. Noire install. It never ships, copies or repackages game files.

## What it does
- Validates your install (LaNoire.exe version/build + SHA-256) and warns on builds V-Patch does not support.
- Installs / uninstalls V-Patch 2.0 (imports the release zip you provide, or downloads the latest from GitHub at runtime). Only files it added are removed; an existing foreign `dinput8.dll` is backed up and restored.
- Edits `plugins/lanvp.ini` and the game's `settings.ini` with a comment- and order-preserving writer.
- **Vulkan via DXVK** (new tab): installs DXVK's d3d9.dll, or d3d11.dll + dxgi.dll, next to LaNoire.exe (32/64-bit picked from the exe's PE header), from an archive you pick or from GitHub at runtime. Foreign DLLs (ReShade etc.) are backed up and restored on uninstall. HUD, frame cap, log level and extra dxvk.conf lines are passed as environment variables at launch, so nothing but the DLLs goes in the game folder. The pipeline cache lives in `%AppData%\LANoireLauncher\dxvk-cache`.
- Saves profiles, backs up every file before writing, launches `LaNoire.exe` with `-res W H`, `-ignorefocus`, extra args.
- Detects the borderless/fullscreen conflict (borderless needs `Fullscreen:NO`).

Launcher data (config, profiles, backups, manifests): `%AppData%\LANoireLauncher`. It does not touch or bypass any DRM; it just starts the exe.

## Build
Requires the .NET 8 SDK on Windows.

    dotnet build -c Release
    dotnet run

## Using DXVK with V-Patch
- V-Patch loads through `dinput8.dll`; DXVK uses `d3d9.dll` / `d3d11.dll` + `dxgi.dll`. They do not overlap.
- The game has a D3D9 and a D3D11 renderer. Pick the same API in the Vulkan tab as the game really uses. On Apply, the launcher looks in `lanvp.ini` for a key naming DirectX 11 and sets it to match (it reports if none is found).
- Needs a Vulkan 1.3 driver for current DXVK; use v1.10.3 for older GPUs.
- Use one frame limiter: V-Patch `fps_limit` or the DXVK cap, not both.

## Known unknowns (verify on your machine)
- Whether V-Patch's borderless mode and FPS unlock behave identically under DXVK (untested).
- Location of `settings.ini` for your install (the launcher checks the game folder, Documents, LocalAppData, AppData; use Browse if not found).
- Allowed values for the graphics dropdowns are guesses; the boxes are editable. Confirm the real options from the game's own menu.
- Not compiled/tested in the environment where it was written. Expect to fix a few compile errors on first build.

## Licences
V-Patch (c) VaanaCZ, CC BY-NC 4.0, non-commercial. Ultimate ASI Loader (c) ThirteenAG, MIT. Not affiliated with Rockstar Games or Take-Two.
