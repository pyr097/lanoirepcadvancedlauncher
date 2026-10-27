# L.A. Noire Advanced Launcher

A dark, noir-themed Windows launcher for the PC version of L.A. Noire. It sets up V-Patch, optional Vulkan rendering (DXVK), optional ReShade with presets, graphics settings and launch options in one window, and starts the game.

It works only on **your own** install. It never ships or repackages game files and does not touch or bypass any DRM; it just starts `LaNoire.exe`.

## How it works

1. **Find the game.** It reuses the remembered folder or looks in the usual Rockstar and Steam locations; otherwise use **Browse...** and pick `LaNoire.exe`.
2. **Check the install.** It reads the exe's build, 32/64-bit type and SHA-256, and warns if the build is not one V-Patch 2.0 supports (2617, 2663, 2675).
3. **Load settings** from `plugins/lanvp.ini` (V-Patch) and the game's `settings.ini`.
4. **Change things.** Nothing is written until you press **Save Options** or **PLAY (and Save)**.
5. **Apply.** The form is validated, each file is backed up, then written. It refuses to apply while the game is running.
6. **Play.** **PLAY (and Save)** applies everything and starts `LaNoire.exe` with your launch options (plus the DXVK environment variables when Vulkan is on).

## Tabs

### Play
- Game folder: auto-detect, Browse..., build/version/SHA-256 readout and V-Patch support check.
- Launch options: force resolution (`-res W H`), keep running when alt-tabbed (`-ignorefocus`), extra arguments.
- **FPS counter** (see below), **PLAY (and Save)**, **Save Options** (apply only).
- **Restore everything to stock...** (see below).

### V-Patch
- **Install from zip...** (a release zip you provide) or **Download latest** (GitHub).
- **Uninstall** removes only what the launcher added; a different `dinput8.dll` that was there before is backed up and restored.
- Shows the install status (recognises V-Patch 2.0, flags unknown versions or a foreign loader).
- `lanvp.ini` settings: FPS unlock, any resolution, aspect ratio / FoV / UI correction, FPS limit (needs FPS unlock), FoV multiplier (needs aspect correction), borderless window, skip logos and legal screens. A warning appears if the FPS limit or FoV multiplier would have no effect.
- V-Patch creates `lanvp.ini` when the game first runs; if it is missing, run the game once.

### Vulkan (DXVK)
- The game always runs on DirectX 11; DXVK optionally translates it to Vulkan (needs a Vulkan 1.3 driver for current DXVK).
- **Install from archive...** or **Download** puts DXVK's `d3d11.dll` and `dxgi.dll` next to `LaNoire.exe` (matched to the exe's bitness). Version box: `latest`, `v1.10.3` (older GPUs without Vulkan 1.3) or any release tag.
- **Render with Vulkan (DXVK)** switches the installed files on or off when you apply; applying never installs new files.
- **Uninstall** removes the DXVK files; foreign DLLs (ReShade, SpecialK, another DXVK) are backed up and restored.
- Advanced: log level (`%AppData%\LANoireLauncher\dxvk-logs`), extra `dxvk.conf` lines (any `maxFrameRate` is dropped), clear the shader/pipeline cache.
- Options are passed as environment variables, so only the two DLLs go into the game folder. DXVK's own frame cap is always 0; the only limiter is V-Patch's `fps_limit`.

### ReShade
Works with DXVK on or off. ReShade loads through the ASI loader V-Patch installs (`dinput8.dll`): the launcher copies the ReShade dll to `plugins\ReShade32.asi` (or `ReShade64.asi`), so it avoids `dxgi.dll` and `d3d11.dll` (DXVK's) and `dinput8.dll` (V-Patch's). V-Patch must be installed.

- **Runtime:** **Install from setup...** takes the dlls from a ReShade setup exe, zip or bare dll; **Download latest** fetches the setup **with add-on support** from reshade.me (the only build it downloads). A runtime not known to be the add-on build is flagged and the launcher offers to replace it. Cached in `%AppData%\LANoireLauncher\reshade\runtime`.
- **Enable ReShade** (applied with Save Options / PLAY) copies the `.asi` into `plugins\` and points `ReShade.ini` at the launcher's shader, texture and preset folders. Unticking removes only the `.asi`. A ReShade you installed yourself is detected and blocks enabling (two copies conflict); use **Import from game folder** to keep its shaders and presets, then remove it. **Remove from game** puts the game folder back as it was.
- **Shaders:** **Add from folder / zip...** sorts a pack into place; **Download standard effects** fetches `crosire/reshade-shaders` (slim). Everything lives in `%AppData%\LANoireLauncher\reshade`; the game folder only gets `ReShade.ini`, `ReShade.log` and the `.asi`.
- **Default preset:** the launcher carries `LA Noire Next Gen 2026 - realistic.ini`. It is copied into the Presets folder right after the ReShade runtime is installed, and only then; an existing preset of that name is never overwritten. It is selected as the active preset when no other preset is chosen or set in `ReShade.ini`.
- **Presets:** import, pick the active one (written to `ReShade.ini` on apply; "(leave as is)" keeps ReShade's choice), remove, or open the folder. The chosen preset is part of a profile.
- **Missing files:** when you pick or import a preset, tick Enable ReShade, press Save Options / PLAY with ReShade ticked, or press **Check & download missing files**, the launcher lists what is missing (runtime, V-Patch's loader, every effect and header the preset's `Techniques=` names) and offers to download it in one go. Each file is fetched from the pack it normally comes from: `MartysMods_*` from [iMMERSE](https://github.com/martymcmodding/iMMERSE), `lumenite_*` from [LumeniteFX](https://github.com/umar-afzaal/LumeniteFX), `qUINT_*` from [qUINT](https://github.com/martymcmodding/qUINT), CeeJay.dk's classics (`FXAA`, `SMAA`, `Levels`, `LumaSharpen`, ...) from [SweetFX](https://github.com/CeeJayDK/SweetFX), the rest from ReShade's standard effects, trying the next pack if a file is not found. Existing effects and presets are never replaced. Declining is remembered for the session; Save Options / PLAY never block on it. Anything it cannot fetch (RTGI, or files removed upstream) is listed with the reason.
- **RTGI** is a paid shader and is not downloaded or bundled. Add your own files, then **Make RTGI preset** creates a preset with its technique(s) on. **Make depth-check preset** (needs `DisplayDepth.fx`) shows ReShade's depth view so you can set the `RESHADE_DEPTH_INPUT_*` options in the overlay (Home).
- If ReShade does not appear, check `ReShade.log` in the game folder.

### Graphics
Edits the game's `settings.ini` (found in the game folder, Documents, LocalAppData or AppData; or use **Browse...**): resolution, antialiasing, anisotropic filtering, SSAO, facial quality, texture/shadow/lighting/environment/LOD detail, interface size, threading and the game's own command line. Dropdowns are editable.

- **DirectX 11 always.** Every Apply forces it in `lanvp.ini` (and `Renderer` in `settings.ini` if present).
- **Windowed or borderless only.** Apply writes `Fullscreen: NO`; the "Borderless window" checkbox picks which.

### Enhancements (work in progress)
Experimental and still being developed. It copies selected values the PS4 remaster uses into the game's attribute database inside `final\pc\out.wad.pc`. The values come from comparing the PC and PS4 files of your own copies; no game data is shipped. Tick the sets you want and press **Apply selected**: exactly those sets end up patched, the others are set back to original.

- **Fog and draw distance (38 values):** subtle.
- **Remaster lighting values (66 values), experimental:** bloom, light radius, brightness/contrast. They may not look better, and neon signs are unaffected (their data is identical on PC and PS4; the remaster look comes from rendering code a data patch cannot port).
- **More vehicles in high detail (2 values).**
- Options: exact PS4 fog values (by default fog end is set to start + 1 to avoid a divide-by-zero), and a full `out.wad.pc.orig` copy (about 150 MB).

Safety: the file size never changes, every value is checked first (a mismatch aborts with nothing changed), and a journal in `%AppData%\LANoireLauncher\atb-journal` lets **Revert all to original** restore the exact bytes. The tab is independent of Save Options and profiles and refuses to run while the game is running. A file check in the Rockstar launcher may flag the change: revert before verifying.

### About (updates)
- **Check for updates** reads the newest release of this repository; on startup it does so in the background and shows an **Update available** button (silent when offline). Both can be turned off; **Include pre-releases** also offers beta tags.
- **Download and install** asks first, downloads `LANoireLauncher.exe`, verifies its SHA-256 and size, then replaces itself (the old exe is kept as `LANoireLauncher.exe.old` and deleted on the next start). On any failure the old launcher stays; if the folder is not writable, the release page opens instead.
- **Skip this version** hides a release from the automatic check.

## Restore to stock
**Restore everything to stock...** undoes every launcher change to the game folder, in order: ReShade, DXVK, Enhancements (exact revert, then `out.wad.pc.orig` is deleted), V-Patch, then `settings.ini` (back to the first version the launcher saw). Only files the launcher installed are touched, and files that existed before (such as a previous `dinput8.dll` or `ReShade.ini`) are put back. Shaders, presets, profiles and backups in `%AppData%` are kept.

## Profiles
The profile box (top right) saves, loads and deletes named profiles: V-Patch and graphics fields, resolution, `-ignorefocus`, extra arguments, DXVK settings, and ReShade on/off with the chosen preset. **Load** only fills the form; press Save Options to write it.

## FPS counter
- **DXVK on:** the game starts with `DXVK_HUD=fps`, and the launcher adds a small click-through "DXVK" label in the top-right corner of the game window.
- **DXVK off:** a click-through "FPS: 120.0" text in the top-left corner of the game window, fed by Windows' DXGI present events (ETW). Nothing is injected. It shows only while the game window is in front; **Ctrl+Shift+F** hides/shows it.
- With DXVK off it needs administrator rights (or "Performance Log Users" membership); the launcher offers to restart as administrator at startup. A game started from an elevated launcher also runs elevated.

## Safety and data
- Every file is backed up to `%AppData%\LANoireLauncher\backups\<timestamp>` before it is changed; the first `settings.ini` is kept in `...\original`.
- Only files the launcher added are removed on uninstall (tracked in `...\manifests`); with no record, nothing is deleted.
- V-Patch, DXVK, ReShade and shaders are never bundled; they come from an archive you pick or from GitHub at runtime. Two small files are embedded: the default preset (copied only after ReShade is installed) and `Fxaa3_11.h` (NVIDIA FXAA 3.11 by Timothy Lottes, which `FXAA.fx` includes and ReShade's slim branch omits), written next to `FXAA.fx` only if no copy exists.
- Launcher data (config, profiles, backups, manifests, caches, logs, ReShade runtime, shaders, presets) lives in `%AppData%\LANoireLauncher`. The game folder only gets the files described above (V-Patch, DXVK, `plugins\ReShade32.asi`, `ReShade.ini`).

## Build and release
Requires the .NET 8 SDK on Windows. `dotnet run` for development; `dotnet publish LANoireLauncher.csproj -c Release -o dist` builds a single exe (needs the .NET 8 Desktop Runtime to run; set `SelfContained` to `true` for a runtime-free, much larger exe).

The version comes from a git tag: pushing one runs `.github/workflows/release.yml`, which builds the exe, writes a SHA-256 file and publishes a GitHub release (a tag with a dash, such as `v1.2.0-beta.1`, becomes a pre-release). Local builds use `<Version>` in `LANoireLauncher.csproj`.

```
git tag v2.5.4
git push origin v2.5.4
```

## Project layout
- `MainWindow.xaml` / `.xaml.cs`, `MainWindow.ReShade.cs`, `MainWindow.Update.cs`, `MainWindow.Restore.cs`: the UI and its logic.
- `Theme.xaml`, `App.xaml` / `.xaml.cs`: styles, startup and the administrator prompt.
- `Core/`: `GameInstall`, `PatchManager` (V-Patch), `DxvkManager`, `ReShadeManager` (+ `.Dependencies`), `AtbPatcher` (Enhancements), `UpdateManager`, `IniDocument`, `Field`, `LauncherConfig`, `FpsCounter`.
- `Overlay/`: the FPS text.

## Credits and licences
- **V-Patch** by VaanaCZ, [github.com/VaanaCZ/LANVP](https://github.com/VaanaCZ/LANVP), CC BY-NC 4.0 (non-commercial). Installed on request, not included.
- **Ultimate ASI Loader** by ThirteenAG, MIT; it is the `dinput8.dll` inside the V-Patch release.
- **DXVK** by Philip Rebohle and contributors, [github.com/doitsujin/dxvk](https://github.com/doitsujin/dxvk), zlib. Downloaded on request.
- **ReShade** by crosire, [reshade.me](https://reshade.me), BSD 3-Clause. Downloaded on request. Shaders (including RTGI) keep their authors' licences and are not included.
- **iMMERSE** and **qUINT** by Pascal Gilcher (Marty McFly), **LumeniteFX** by Umar Afzaal, **SweetFX** by CeeJay.dk. Downloaded from their repositories on request; each keeps its author's licence.
- **Microsoft.Diagnostics.Tracing.TraceEvent** (MIT) for the FPS counter; **.NET 8 and WPF** by Microsoft.

Made by pyr097. L.A. Noire is a trademark of Rockstar Games / Take-Two Interactive. This launcher is not affiliated with or endorsed by Rockstar Games, Take-Two, V-Patch, DXVK or ThirteenAG.
