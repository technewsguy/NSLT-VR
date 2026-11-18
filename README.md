# NSLT-VR: a VR mod for New Super Lucky's Tale

Play **New Super Lucky's Tale** (Steam, Windows) in a SteamVR headset, in the style of the original *Lucky's Tale* VR:
you look down on a living diorama in true 3D while Lucky runs around in front of you.

- True stereoscopic 3D, head-tracked, at the headset's full frame rate
- Menus, HUD, dialogue and loading tips on a floating screen in front of you
- Motion-controller support (Index, Touch, Vive wands), or a normal gamepad
- Faint "ghost" controllers in VR, with button labels on the title screen and in the pause menu, like the original game
- One-click installer. Afterwards you play from Steam's normal **New Super Lucky's Tale** entry, which also shows
  under **VR** in the SteamVR library

> Fan-made and unofficial. Not affiliated with or endorsed by Playful. You need your own copy of the game on Steam.
> No game files are included in this repository.

[![Support me on Ko-fi](https://img.shields.io/badge/Ko--fi-Support%20this%20mod-FF5E5B?logo=ko-fi&logoColor=white)](https://ko-fi.com/technewsguy)

## Requirements

- New Super Lucky's Tale from **Steam**, on Windows 10/11 (64-bit) or Linux (Proton)
- SteamVR and a PC VR headset. Tested on a Bigscreen Beyond with Valve Index controllers.

## Install

### Windows

1. Download `NSLT-VR-Setup-vX.Y.Z.exe` from the [latest release](https://github.com/technewsguy/NSLT-VR/releases/latest).
2. Close the game and double-click the installer. It finds the game in your Steam libraries (or use **Browse...**)
   and installs with one click. If Windows SmartScreen warns about an unknown app, click **More info > Run anyway**.
3. If Steam is open, the installer offers to restart it so the game shows up in SteamVR's **VR** library tab.
   If you say no, it's listed the next time Steam closes.

The installer puts the BepInEx mod loader and the mod into the game folder and turns on the game's dormant OpenVR
support (a small patch to `nslt_Data\globalgamemanagers`; a backup is kept).

To remove the mod, run **NSLT-VR Uninstall.exe** in the game folder (or the setup exe again and click **Uninstall**).
It puts back everything it changed.

### Linux / Steam Deck

1. Download the same `NSLT-VR-Setup-vX.Y.Z.exe`. In Steam, use **Add a Non-Steam Game** to add it, then in its
   **Properties > Compatibility**, choose Proton. Start it from Steam, and it finds the game in your Linux Steam
   libraries.
2. In Steam, right-click **New Super Lucky's Tale > Properties > Launch Options** and enter
   (the installer has a **Copy launch option** button for this):
   ```
   WINEDLLOVERRIDES="winhttp=n,b" %command%
   ```
   Without it, Proton doesn't load the mod and the game starts flat.

VR needs SteamVR for Linux. The listing under SteamVR's **VR** library tab is Windows only.

### With UniGetUI or Scoop

NSLT-VR is also in the [technewsguy/scoop-vr-mods](https://github.com/technewsguy/scoop-vr-mods) bucket, so
[UniGetUI](https://github.com/marticliment/UniGetUI) (formerly WingetUI) or [Scoop](https://scoop.sh) can install it and keep it up to date:

- **UniGetUI:** Settings > Package managers > Scoop > Buckets, add `vrmods` with the URL
  `https://github.com/technewsguy/scoop-vr-mods`, then search for `nslt-vr` and install it.
- **Command line:**
  ```powershell
  scoop bucket add vrmods https://github.com/technewsguy/scoop-vr-mods
  scoop install vrmods/nslt-vr
  ```

It runs the same installer silently. Uninstalling the package removes the mod again.

### Command line

`NSLT-VR-Setup.exe /S` installs silently (log in `%TEMP%\nsltvr-setup.log`). Add `/UNINSTALL` to remove the mod, and
`/GAMEDIR="X:\...\steamapps\common\New Super Luckys Tale"` if the game isn't found.

### From source

1. Download this repository: **Code > Download ZIP**, then unzip it, or `git clone` it.
2. Open PowerShell in the folder and run `powershell -ExecutionPolicy Bypass -File .\build.ps1`.
   - It finds the game through Steam and downloads [BepInEx 5.4.23.6](https://github.com/BepInEx/BepInEx) and Valve's
     [openvr_api.dll 1.14.15](https://github.com/ValveSoftware/openvr) into `deps\`. It then compiles the mod and
     `build\NSLT-VR-Setup.exe` with the C# compiler built into Windows, so no SDK is needed.
   - If the game isn't found automatically, add `-GameDir "X:\...\steamapps\common\New Super Luckys Tale"`.
3. Run `build\NSLT-VR-Setup.exe`. `make-release.ps1` makes the release files in `dist\`.

## Playing

Start **New Super Lucky's Tale** from Steam, on the desktop or in the headset, and it plays in VR. SteamVR is
started if needed. To play flat again, set `[General] EnableVR = false` in the settings, or uninstall the mod.

- Recenter the view: click both thumbsticks, or press **F8**.
- A gamepad works for the game, but SteamVR's own menus (the dashboard) can only be clicked with VR controllers.
- The floating screen is about 1.4 m in front of you and turns to face you again if you look well away.

### Controls (motion controllers)

The controllers act as a gamepad, and a real gamepad also works. Grips and trackpads are deliberately unbound.

| Left controller | | Right controller | |
|---|---|---|---|
| Stick | Move | Stick | Camera |
| Stick click | Center camera | Stick click | Pause (Start) |
| A | Tail swipe (X) | A | Jump / select (A) |
| B | Interact (Y) | B | Back (B) |
| Trigger | Tail swipe (X) | Trigger | Burrow |

Click both sticks together to recenter the VR view. The buttons can be remapped in SteamVR's controller bindings for the game.

## Settings

`<game>\BepInEx\config\nsltvr.mod.cfg` is created on first run. The most useful settings:

| Setting | Default | |
|---|---|---|
| `[UI] Width` / `Distance` / `VerticalOffset` | 1.6 / 1.4 / -0.1 | Size and position of the floating screen (meters) |
| `[UI] TitleScreenScale` | 1.75 | Floating screen size on the title screen |
| `[UI] HubNameSeconds` | 5 | How long a hub's name stays in the HUD after arriving (-1 = always) |
| `[Controllers] ShowControllers` / `ControllerVisibility` | true / 0.35 | Ghost controllers and how visible they are |
| `[Controllers] ControlsGuide` | TitleScreenAndPause | When the button labels show: `TitleScreen`, `Always`, `Never` |
| `[Camera] SharedReflectionView` | true | Uses the original Lucky's Tale VR shader mode for consistent reflections |
| `[General] EnableVR` | true | `false` = the normal flat game, with the mod still installed |

## How it works

New Super Lucky's Tale runs on Unity 2017.4. Unity's built-in VR turned out to hand SteamVR black frames in this game,
so the mod does what the SteamVR plugin did before Unity had VR built in:

- **Rendering** (`src\OwnVR.cs`): each eye is rendered by an ordinary camera into its own texture, using the
  headset's per-eye projection. The camera follows the game's camera plus your head pose. On Unity's render thread
  the frames go to SteamVR (`IVRCompositor.Submit`), followed by `WaitGetPoses`.
- **Menus and HUD**: the game's UI cameras and screen-space canvases are redirected into one texture, shown on a
  floating panel in VR. During loading freezes it switches to a SteamVR overlay, so loading tips stay visible.
- **Game-specific fixes**:
  - Each eye gets its own copy of the scene for the water and glass shaders, which otherwise refracted the desktop
    view as a ghost.
  - Screen-copy effects are hidden from VR, along with the x-ray silhouette that was positioned for the desktop camera.
  - Lucky's own VR shader mode (`_VRCamPos`) is switched on.
  - Menu layering is preserved.
- **Input** (`src\VRInput.cs`, `input\*.json`): SteamVR Input actions feed an InControl device that looks like an
  Xbox pad to the game.
- **Steam's VR library**: Steam decides whether a game is a VR game from the type of its launch options. The
  installer marks the game's launch option as a VR launch (`type = vr`) in Steam's local copy of the game's info
  (`appcache\appinfo.vdf`, edited while Steam is closed, with a backup). When Valve updates the game's store info,
  Steam resets it; the mod notices at launch and marks it again after Steam closes.
- **Quitting**: after VR frames have been submitted, this Unity version hangs during engine shutdown, after the game
  has already saved. A small external command closes the process 6 seconds after quitting if it is still running.

| Path | What |
|---|---|
| `src\Plugin.cs` | BepInEx entry point, settings, Harmony patches |
| `src\OwnVR.cs` | Renderer, SteamVR submission, floating screen, loading overlay, launch/quit handling |
| `src\OwnHands.cs` | Ghost controllers (SteamVR render models) and button labels |
| `src\OwnOpaqueGrab.cs` | Per-eye scene copy for water/glass |
| `src\VRInput.cs` | Controllers → gamepad, haptics |
| `src\VRController.cs`, `src\BaselineVR.cs` | Old Unity-XR renderer and troubleshooting stages (`[Debug] BaselineStage`) |
| `input\` | SteamVR Input action manifest and default bindings |
| `installer\` | Installer/uninstaller (`Setup.cs`) and the SteamVR library listing (`SteamVRTag.cs`) |
| `patcher\` | `globalgamemanagers` patcher (built into the installer) |
| `deps\openvr\openvr_api.cs` | Valve's OpenVR C# bindings (BSD-3-Clause) |

## Support

If you enjoy the mod, you can buy me a coffee on **[Ko-fi](https://ko-fi.com/technewsguy)**. Thank you!

## Credits

- [BepInEx](https://github.com/BepInEx/BepInEx) and [Harmony](https://github.com/pardeike/Harmony) for mod loading and patching
- Valve's [OpenVR SDK](https://github.com/ValveSoftware/openvr)
- [VookaRaylee](https://github.com/Eusth/VookaRaylee) / [VRGIN](https://github.com/Eusth/VRGIN) by Eusth, the inspiration and reference
- Written with [Claude Code](https://claude.com/claude-code)

## License

MIT for this mod's code, see [LICENSE](LICENSE). `deps\openvr\openvr_api.cs` is Valve's (BSD-3-Clause).
New Super Lucky's Tale and Lucky's Tale belong to Playful.
