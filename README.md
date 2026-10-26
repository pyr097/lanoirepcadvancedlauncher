# L.A. Noire Launcher

Dark, noir-themed WPF launcher. Icon: `Assets/lanoire.ico` (original artwork, not Rockstar's).

A launcher that works only on **your own** L.A. Noire install. It never ships, copies or repackages game files.

## What it does
- Validates your install (LaNoire.exe version/build + SHA-256) and warns on builds V-Patch does not support.
- Installs / uninstalls V-Patch 2.0 (imports the release zip you provide, or downloads the latest from GitHub at runtime). Only files it added are removed; an existing foreign `dinput8.dll` is backed up and restored.
- Edits `plugins/lanvp.ini` and the game's `settings.ini` with a comment- and order-preserving writer.
- **DirectX 11 always.** The launcher forces DirectX 11 in `lanvp.ini` (and `Renderer` in `settings.ini` if present) on every Apply. There is no DirectX 9 mode.
- **Vulkan via DXVK (optional):** installs DXVK's `d3d11.dll` + `dxgi.dll` next to LaNoire.exe (32/64-bit picked from the exe's PE header), from an archive you pick or from GitHub at runtime. Foreign DLLs (ReShade etc.) are backed up and restored on uninstall. DXVK's own HUD is never enabled and its frame cap is always 0; the only frame limiter is V-Patch's `fps_limit`. Log level and extra dxvk.conf lines (any `maxFrameRate` line is dropped) go in as environment variables, so nothing but the DLLs is written to the game folder. The pipeline cache lives in `%AppData%\\LANoireLauncher\\dxvk-cache`.
- **FPS overlay (optional):** a small click-through badge in the top-right corner of the game window showing FPS and frame time in ms. It is fed by Intel's PresentMon (MIT, downloaded at runtime from GitHub or picked by you), which reads Windows' present events: nothing is injected into the game, so the performance cost is negligible and it works with and without DXVK. It only shows while the game window is in front; Ctrl+Shift+F hides/shows it. Use windowed or borderless mode (V-Patch's `borderless_window`); an overlay window cannot draw over exclusive fullscreen.
- Saves profiles, backs up every file before writing, launches `LaNoire.exe` with `-res W H`, `-ignorefocus`, extra args.
- Detects the borderless/fullscreen conflict (borderless needs `Fullscreen:NO`).

Launcher data (config, profiles, backups, manifests): `%AppData%\LANoireLauncher`. It does not touch or bypass any DRM; it just starts the exe.

## Build
Requires the .NET 8 SDK on Windows.

    dotnet build -c Release
    dotnet run

## Using DXVK with V-Patch
- V-Patch loads through `dinput8.dll`; DXVK uses `d3d11.dll` + `dxgi.dll`. They do not overlap.
- Needs a Vulkan 1.3 driver for current DXVK; use v1.10.3 for older GPUs.
- Frame limit: V-Patch `fps_limit` only.

## About the overlay and administrator rights
PresentMon reads ETW events, which Windows only allows for administrators or members of "Performance Log Users". The launcher first tries without elevation; if PresentMon refuses, Windows shows one UAC prompt each time you press Play with the overlay on. Add your account to "Performance Log Users" to avoid the prompt. An elevated PresentMon cannot be closed by the launcher; it exits by itself when the game exits.

## Known unknowns (verify on your machine)
- Whether V-Patch's borderless mode and FPS unlock behave identically under DXVK (untested).
- The overlay targets the PresentMon 2.x console (`--process_name`, `--output_file`, `FrameTime` / `msBetweenPresents` column). Another version may need different options.
- Location of `settings.ini` for your install (the launcher checks the game folder, Documents, LocalAppData, AppData; use Browse if not found).
- Allowed values for the graphics dropdowns are guesses; the boxes are editable. Confirm the real options from the game's own menu.
- Not compiled/tested in the environment where it was written. Expect to fix a few compile errors on first build.

## Licences
V-Patch (c) VaanaCZ, CC BY-NC 4.0, non-commercial. Ultimate ASI Loader (c) ThirteenAG, MIT. Not affiliated with Rockstar Games or Take-Two.
