# L.A. Noire Advanced Launcher

A Windows launcher for the PC version of L.A. Noire. It sets up V-Patch, optional Vulkan rendering (DXVK), graphics settings and launch options in one window, and starts the game.

It works only on **your own** L.A. Noire install. It never ships, copies or repackages game files, and it does not touch or bypass any DRM; it just starts `LaNoire.exe`.

## How it works

1. **Find the game.** On start the launcher reuses the folder it remembers, or looks in the usual Rockstar and Steam locations. If it finds nothing, use **Browse...** and pick `LaNoire.exe`.
2. **Check the install.** It reads the exe's version/build, 32/64-bit type (from the PE header) and SHA-256, and warns if the build is not one V-Patch 2.0 supports (2617, 2663, 2675).
3. **Load current settings.** Values from `plugins/lanvp.ini` (V-Patch) and the game's `settings.ini` are shown in the form.
4. **You change things.** Nothing is written until you press **Save Options** or **PLAY (and Save)**.
5. **Apply.** The launcher validates the form, backs up each file it is about to change, then writes the values. It refuses to apply while the game is running.
6. **Play.** **PLAY (and Save)** applies everything, then starts `LaNoire.exe` with your launch options (and the DXVK environment variables when Vulkan is on).

## Features

### Play tab
- **Game folder:** Auto-detect, Browse..., build/version/SHA-256 readout and V-Patch support check.
- **Launch options:**
  - Force resolution (`-res W H`); leave empty to use `settings.ini`.
  - Keep running when alt-tabbed (`-ignorefocus`).
  - Extra command-line arguments.
- **FPS counter:** one checkbox, see [FPS counter](#fps-counter).
- **PLAY (and Save)** (apply + launch) and **Save Options** (apply only, does not start the game).

### V-Patch tab
- **Install from zip...** imports a V-Patch release zip you provide.
- **Download latest** fetches the newest release from GitHub at runtime.
- **Uninstall** removes only the files this launcher added. A different `dinput8.dll` that was already there (ReShade or another mod) is backed up first and restored.
- Shows the install status (recognises V-Patch 2.0, flags unknown versions or a foreign loader).
- **Settings (`lanvp.ini`):**
  - Unlock FPS cap and high-FPS fixes.
  - Allow any resolution.
  - Aspect ratio / FoV / UI correction.
  - FPS limit (0 = none; needs FPS unlock on).
  - FoV multiplier (needs aspect correction on).
  - Borderless window.
  - Skip company logos and legal screens.
- Heads-up messages appear if the FPS limit or FoV multiplier would have no effect.
- `lanvp.ini` is created by V-Patch when the game first runs; if it is missing, run the game once.

### Vulkan (DXVK) tab
- The game always runs on DirectX 11. DXVK optionally translates it to Vulkan.
- **Install from archive...** or **Download** installs DXVK's `d3d11.dll` and `dxgi.dll` next to `LaNoire.exe` (32 or 64-bit, matched to the exe). Version box: `latest`, `v1.10.3` (older GPUs without Vulkan 1.3), or any release tag you type.
- **Render with Vulkan (DXVK)** checkbox: ticking it and applying switches DXVK on; unticking it and applying removes the DXVK files again. Applying never installs new files, only switches the installed set on or off.
- **Uninstall** removes the DXVK files; foreign DLLs (ReShade, SpecialK, another DXVK) are backed up and restored.
- **Advanced:**
  - Log level (logs go to `%AppData%\LANoireLauncher\dxvk-logs`).
  - Extra `dxvk.conf` lines (any `maxFrameRate` line is dropped).
  - Clear the shader/pipeline cache.
- Options are passed as environment variables, so nothing but the two DLLs is written to the game folder. The pipeline cache lives in `%AppData%\LANoireLauncher\dxvk-cache`.
- DXVK's own frame cap is always 0. The only frame limiter is V-Patch's `fps_limit`.
- Needs a Vulkan 1.3 driver for current DXVK.

### Graphics tab
Edits the game's `settings.ini` (path auto-found in the game folder, Documents, LocalAppData or AppData; use **Browse...** otherwise): resolution (`WIDTHxHEIGHTxHZ`), antialiasing, anisotropic filtering, SSAO, facial quality, texture, shadow, lighting, environment map and LOD detail, interface size, threading and the game's own command line. The dropdowns are editable, so you can type a value that is not listed.

- **DirectX 11 always.** Every Apply forces DirectX 11 in `lanvp.ini` (and `Renderer` in `settings.ini` if present). There is no DirectX 9 mode.
- **Windowed or borderless only.** Apply always writes `Fullscreen: NO`; the "Borderless window" checkbox chooses between a regular window and borderless.

### Profiles
The profile box at the top right saves, loads and deletes named profiles. A profile stores the V-Patch and graphics fields, resolution, `-ignorefocus`, extra arguments, and the DXVK on/off, log level and conf lines. **Load** only fills the form; press Save Options to write it.

### FPS counter
- **DXVK on:** the launcher starts the game with `DXVK_HUD=fps` and DXVK draws its own small counter, and the launcher adds a small "DXVK" label in the top-right corner of the game window (click-through, same style).
- **DXVK off:** click-through "FPS: 120.0" text in the same style as DXVK's counter (updated 10 times a second) sits in the top-left corner of the game window, fed by Windows' own DXGI present events (ETW). Nothing is injected into the game. It only shows while the game window is in front; **Ctrl+Shift+F** hides/shows it.
- Reading those events needs administrator rights (or membership of "Performance Log Users"). At startup the launcher offers to restart as administrator and explains why; if you decline, the launcher says so and the game starts normally. A game started from an elevated launcher also runs elevated. With DXVK on, no rights are needed.

## Safety and data
- Every file is backed up to `%AppData%\LANoireLauncher\backups\<timestamp>` before it is changed.
- Only files the launcher added are removed on uninstall (tracked in `%AppData%\LANoireLauncher\manifests`).
- V-Patch and DXVK are never bundled. They come from an archive you pick or from GitHub at runtime.
- Launcher data (config, profiles, backups, manifests, DXVK cache and logs) lives in `%AppData%\LANoireLauncher`, never inside the game folder.

## Build
Requires the .NET 8 SDK on Windows. Double-click `build.bat`: it publishes a single exe, copies it to `dist\L.A. Noire Advanced Launcher.exe` and removes `bin` and `obj`. Running it needs the .NET 8 Desktop Runtime (already present if you build on the same PC). For a runtime-free exe (much larger), set `SelfContained` to `true` in `LANoireLauncher.csproj`.

For development: `dotnet run`.

## Project layout
- `MainWindow.xaml` / `.xaml.cs`: the UI, apply/launch logic and profiles.
- `Theme.xaml`: colours and control styles.
- `App.xaml` / `.xaml.cs`: startup and the administrator prompt.
- `Core/GameInstall.cs`: install detection and build/bitness check.
- `Core/PatchManager.cs`: V-Patch install, uninstall and download.
- `Core/DxvkManager.cs`: DXVK install, uninstall, download and environment.
- `Core/IniDocument.cs`: comment- and order-preserving INI reader/writer.
- `Core/Field.cs`: one editable setting and its control.
- `Core/LauncherConfig.cs`: config, profiles and backups.
- `Core/FpsCounter.cs`, `Overlay/`: the FPS badge.

## Credits and licences
- **V-Patch** by VaanaCZ, [github.com/VaanaCZ/LANVP](https://github.com/VaanaCZ/LANVP), licensed CC BY-NC 4.0 (non-commercial use only). The launcher installs it on request but does not include it.
- **Ultimate ASI Loader** by ThirteenAG, MIT licence. It is the `dinput8.dll` that ships inside the V-Patch release.
- **DXVK** by Philip Rebohle and contributors, [github.com/doitsujin/dxvk](https://github.com/doitsujin/dxvk), zlib licence. Downloaded on request, not included.
- **Microsoft.Diagnostics.Tracing.TraceEvent** by Microsoft, MIT licence, used for the FPS badge.
- **.NET 8 and WPF** by Microsoft.

L.A. Noire is a trademark of Rockstar Games / Take-Two Interactive. This launcher is not affiliated with or endorsed by Rockstar Games, Take-Two, V-Patch, DXVK or ThirteenAG.
