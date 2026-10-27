# L.A. Noire Advanced Launcher

A dark, noir-themed Windows launcher for the PC version of L.A. Noire. It sets up V-Patch, optional Vulkan rendering (DXVK), optional ReShade (with RTGI and presets), graphics settings and launch options in one window, and starts the game.

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

### Enhancements tab
Copies selected values from what the PS4 remaster uses into the game's attribute database (`out/attribute/root.atb.pc`, stored uncompressed inside `final\pc\out.wad.pc`). The values were found by comparing the PC and PS4 attribute files of the user's own copies; no game data is shipped.

Three independent sets (tick the ones you want, then **Apply selected**; exactly those sets end up patched and the others are set back to original):
- **Fog and draw distance (38 values):** fog start/end, horizon fog, high-LOD draw distance, depth-of-field far plane. Subtle.
- **Remaster lighting values (66 values), experimental:** bloom threshold and intensity (`BloomThresholdFactor` is 4 times higher on PS4), light radius modifier, brightness/contrast, ambient falloff. These are only the lighting values that changed; they may not look better.
- **More vehicles in high detail (2 values):** high-LOD vehicles 5 to 10 and medium-LOD vehicles 12 to 24.

How it stays safe: the file size never changes, every value is checked against the expected original before anything is written (a mismatch aborts with nothing changed), and a journal in `%AppData%\LANoireLauncher\atb-journal` lets **Revert all to original** restore the exact bytes. The tab is independent of **Save Options** and profiles, only acts when you press Apply or Revert, and refuses to run while the game is running.
- **Use the exact PS4 fog values:** by default, where the remaster sets fog start equal to fog end, the end is set to start + 1 to avoid a possible divide-by-zero in the older PC shader.
- **Also keep a full copy:** copies `out.wad.pc` to `out.wad.pc.orig` once before patching (about 150 MB).
- A file-verification in the Rockstar launcher may flag the modified file: revert before verifying.

**About neon signs:** the neon light definitions and all glow values are identical on PC and PS4, so there is nothing in the data to copy for them. The remaster look comes mostly from rendering code and shaders that the PC executable does not have, which a data patch cannot port.

### ReShade tab
ReShade works with DXVK **on or off**. It is loaded through the ASI loader that V-Patch installs (`dinput8.dll`): the launcher copies the ReShade dll to `plugins\ReShade32.asi` (or `ReShade64.asi`, matched to the exe). That avoids every name ReShade normally installs under (`dxgi.dll` and `d3d11.dll` belong to DXVK, `dinput8.dll` to V-Patch), so nothing changes between the two modes. V-Patch must be installed.

- **Runtime:** **Install from setup...** takes `ReShade32.dll` / `ReShade64.dll` out of the ReShade setup exe (or a zip, or a bare dll) you downloaded from reshade.me; **Download latest** fetches the setup from reshade.me. The dlls are cached in `%AppData%\LANoireLauncher\reshade\runtime`.
- **Enable ReShade** (checkbox, applied with Save Options / PLAY): copies the `.asi` into `plugins\` and points `ReShade.ini` at the launcher's shader, texture and preset folders. Unticking removes only the `.asi`; `ReShade.ini`, shaders and presets stay. A ReShade you installed yourself (`dxgi.dll`, `d3d11.dll`, `dinput8.dll`, ...) is detected and blocks enabling, because two copies conflict; use **Import from game folder** to keep its shaders and presets, then remove it.
- **Shaders:** **Add from folder... / Add from zip...** sorts a shader pack into place (`Shaders` / `Textures` folders, loose `.fx` / `.fxh` files, preset `.ini` files). **Download standard effects** fetches `crosire/reshade-shaders` (slim branch). Everything lives in `%AppData%\LANoireLauncher\reshade`; the game folder only gets `ReShade.ini`, `ReShade.log` and the `.asi`.
- **Presets:** import ReShade presets, pick the active one (written to `ReShade.ini` on apply; "(leave as is)" keeps ReShade's own choice), remove or open the folder. The preset is part of a profile.
- **Missing-file check and one-click download:** when you import or pick a preset, tick **Enable ReShade**, or press Save Options / PLAY with ReShade ticked (or press **Check & download missing files** any time), the launcher works out everything that is missing and offers to download all of it in one go: the ReShade runtime, V-Patch's ASI loader (`dinput8.dll`), and every effect file and `#include` header the preset's enabled techniques name. It reads the preset's `Techniques=` line and sorts each file to the pack it normally comes from: `MartysMods_*` from [iMMERSE](https://github.com/martymcmodding/iMMERSE), `lumenite_*` from [LumeniteFX](https://github.com/umar-afzaal/LumeniteFX), `qUINT_*` from [qUINT](https://github.com/martymcmodding/qUINT), everything else from ReShade's standard effects. Each pack is downloaded straight from its author's repository into `%AppData%\LANoireLauncher\reshade`; an effect that is already in the store is never replaced or duplicated, and a preset you already have is never overwritten. Declining is remembered for the session (the button always asks again), and Save Options / PLAY never block on it. Anything the downloads do not cover (RTGI, or a file that no longer exists upstream) is listed afterwards with the reason, so you can add it with **Add from folder / zip**.
- **RTGI:** RTGI is a paid shader from its author's Patreon and is not downloaded or bundled. Add your own RTGI files, then **Make RTGI preset** creates a preset that switches on the RTGI technique(s) with ReShade's default settings (an existing preset of that name is never overwritten). RTGI needs the game's depth buffer: **Make depth-check preset** (needs `DisplayDepth.fx` from the standard effects) shows ReShade's depth view so you can set the `RESHADE_DEPTH_INPUT_*` options in the in-game overlay (Home).
- If ReShade does not appear in the game, open `ReShade.log` in the game folder: it shows whether ReShade loaded and which graphics API it hooked.

### Graphics tab
Edits the game's `settings.ini` (path auto-found in the game folder, Documents, LocalAppData or AppData; use **Browse...** otherwise): resolution (`WIDTHxHEIGHTxHZ`), antialiasing, anisotropic filtering, SSAO, facial quality, texture, shadow, lighting, environment map and LOD detail, interface size, threading and the game's own command line. The dropdowns are editable, so you can type a value that is not listed.

- **DirectX 11 always.** Every Apply forces DirectX 11 in `lanvp.ini` (and `Renderer` in `settings.ini` if present). There is no DirectX 9 mode.
- **Windowed or borderless only.** Apply always writes `Fullscreen: NO`; the "Borderless window" checkbox chooses between a regular window and borderless.

### Restore to stock
**Restore everything to stock...** (Play tab) undoes every change to the game folder, in this order: ReShade (its `.asi`, and `ReShade.ini` / `ReShade.log` / `ReShadePreset.ini` unless they existed before; a `ReShade.ini` you had before is put back), DXVK (foreign `d3d11.dll` / `dxgi.dll` restored), Enhancements (exact revert, then the `out.wad.pc.orig` copy is deleted), V-Patch (a previous `dinput8.dll` restored) and `settings.ini` (back to the first version the launcher saw). Only files the launcher installed are touched; each step also exists as its own button (ReShade: **Remove from game**). Shaders, presets, profiles and backups in `%AppData%` are kept.

### Profiles
The profile box at the top right saves, loads and deletes named profiles. A profile stores the V-Patch and graphics fields, resolution, `-ignorefocus`, extra arguments, the DXVK on/off, log level and conf lines, and ReShade on/off with the chosen preset. **Load** only fills the form; press Save Options to write it.

### FPS counter
- **DXVK on:** the launcher starts the game with `DXVK_HUD=fps` and DXVK draws its own small counter, and the launcher adds a small "DXVK" label in the top-right corner of the game window (click-through, same style).
- **DXVK off:** click-through "FPS: 120.0" text in the same style as DXVK's counter (updated 10 times a second) sits in the top-left corner of the game window, fed by Windows' own DXGI present events (ETW). Nothing is injected into the game. It only shows while the game window is in front; **Ctrl+Shift+F** hides/shows it.
- Reading those events needs administrator rights (or membership of "Performance Log Users"). At startup the launcher offers to restart as administrator and explains why; if you decline, the launcher says so and the game starts normally. A game started from an elevated launcher also runs elevated. With DXVK on, no rights are needed.

## Safety and data
- Every file is backed up to `%AppData%\LANoireLauncher\backups\<timestamp>` before it is changed.
- Only files the launcher added are removed on uninstall (tracked in `%AppData%\LANoireLauncher\manifests`); with no record, nothing is deleted.
- The first version of `settings.ini` is kept once in `%AppData%\LANoireLauncher\original`.
- V-Patch, DXVK, ReShade and shaders are never bundled. They come from an archive you pick or from GitHub at runtime.
- Launcher data (config, profiles, backups, manifests, DXVK cache and logs, ReShade runtime, shaders and presets) lives in `%AppData%\LANoireLauncher`. The game folder only gets the DLLs/files described above (V-Patch, DXVK, `plugins\ReShade32.asi` and `ReShade.ini`).

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
- `Core/ReShadeManager.cs`, `Core/ReShadeManager.Dependencies.cs`, `MainWindow.ReShade.cs`: ReShade runtime, shader/preset store, `.asi` install, the missing-file planner and pack downloads, and the ReShade tab.
- `Core/FpsCounter.cs`, `Overlay/`: the FPS badge.

## Credits and licences
- **V-Patch** by VaanaCZ, [github.com/VaanaCZ/LANVP](https://github.com/VaanaCZ/LANVP), licensed CC BY-NC 4.0 (non-commercial use only). The launcher installs it on request but does not include it.
- **Ultimate ASI Loader** by ThirteenAG, MIT licence. It is the `dinput8.dll` that ships inside the V-Patch release.
- **DXVK** by Philip Rebohle and contributors, [github.com/doitsujin/dxvk](https://github.com/doitsujin/dxvk), zlib licence. Downloaded on request, not included.
- **ReShade** by crosire, [reshade.me](https://reshade.me), BSD 3-Clause. Downloaded on request, not included. Shaders (including RTGI) keep their authors' licences and are not included.
- **iMMERSE** and **qUINT** by Pascal Gilcher (Marty McFly), **LumeniteFX** by Umar Afzaal. Downloaded from their own repositories on request, never included; each keeps its author's licence (see the repository).
- **Microsoft.Diagnostics.Tracing.TraceEvent** by Microsoft, MIT licence, used for the FPS badge.
- **.NET 8 and WPF** by Microsoft.

L.A. Noire is a trademark of Rockstar Games / Take-Two Interactive. This launcher is not affiliated with or endorsed by Rockstar Games, Take-Two, V-Patch, DXVK or ThirteenAG.
