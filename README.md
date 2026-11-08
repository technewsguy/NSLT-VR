# NSLT-VR: a VR mod for New Super Lucky's Tale

Play **New Super Lucky's Tale** (Steam, Windows) in a SteamVR headset, in the style of the original *Lucky's Tale* VR:
you look down on a living diorama in true 3D while Lucky runs around in front of you.

- True stereoscopic 3D, head-tracked, at the headset's full frame rate
- Menus, HUD, dialogue and loading tips on a floating screen in front of you
- Motion-controller support (Index, Touch, Vive wands), or a normal gamepad
- Faint "ghost" controllers in VR, with button labels on the title screen and in the pause menu, like the original game
- Its own **New Super Lucky's Tale VR** shortcut; Steam's normal entry still plays the flat game

> Fan-made and unofficial. Not affiliated with or endorsed by Playful. You need your own copy of the game on Steam.
> No game files are included in this repository.

[![Support me on Ko-fi](https://img.shields.io/badge/Ko--fi-Support%20this%20mod-FF5E5B?logo=ko-fi&logoColor=white)](https://ko-fi.com/technewsguy)

## Requirements

- Windows 10/11 (64-bit), New Super Lucky's Tale from **Steam**
- SteamVR and a PC VR headset. Tested on a Bigscreen Beyond with Valve Index controllers.

## Install

### Easiest: the release download

1. Download `NSLT-VR-vX.Y.Z.zip` from the [latest release](https://github.com/technewsguy/NSLT-VR/releases/latest).
   Before unzipping, right-click it, choose **Properties**, tick **Unblock**, then click **OK**.
2. Unzip it anywhere and close the game.
3. Open PowerShell in the unzipped folder and run:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install.ps1
   ```
   Everything is prebuilt, so nothing is downloaded or compiled.
4. Optional: `powershell -ExecutionPolicy Bypass -File .\add-steam-vr-shortcut.ps1` adds the VR entry to Steam
   (see step 3 below).

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

It finds the game through Steam and runs the same installer. **NSLT-VR - Add to Steam** in the Start menu adds the
Steam entry (step 4 above). Uninstalling the package removes the mod again.

### From source

1. Download this repository: **Code > Download ZIP**, then unzip it, or `git clone` it.
2. Close the game. Open PowerShell in the folder and run:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\build.ps1
   powershell -ExecutionPolicy Bypass -File .\install.ps1
   ```
   - `build.ps1` finds the game through Steam and downloads [BepInEx 5.4.23.6](https://github.com/BepInEx/BepInEx) and Valve's
     [openvr_api.dll 1.14.15](https://github.com/ValveSoftware/openvr) into `deps\`. It then compiles the mod with the
     C# compiler built into Windows, so no SDK is needed.
   - `install.ps1` installs BepInEx and the mod into the game folder and turns on the game's dormant OpenVR support
     (a small patch to `nslt_Data\globalgamemanagers`; a backup is kept). It also creates a
     **New Super Lucky's Tale VR** shortcut on your desktop and in the Start menu.
   - If the game isn't found automatically, add `-GameDir "X:\...\steamapps\common\New Super Luckys Tale"`.
3. Optional: run `add-steam-vr-shortcut.ps1` to add **New Super Lucky's Tale VR** to your **Steam** library, with the
   game's artwork. Because it is marked for the VR library, it also appears in **SteamVR's** library. This script closes
   and restarts Steam, since Steam only reads its non-Steam games at startup. Your other shortcuts are kept, and a
   backup is saved as `shortcuts.vdf.nsltvr-backup`.

To remove everything, run `uninstall.ps1`. Start SteamVR first so it can also remove the SteamVR library entry.

## Playing

| Start from | You get |
|---|---|
| **New Super Lucky's Tale VR** (desktop, Steam or SteamVR library) | VR. SteamVR is started if needed |
| **New Super Lucky's Tale** (Steam's normal entry) | The normal flat game |

- Recenter the view: click both thumbsticks, or press **F8**.
- A gamepad works for the game, but SteamVR's own menus (the dashboard) can only be clicked with VR controllers.
  Launching through the **New Super Lucky's Tale VR** entry closes the dashboard for you.
- The floating screen is about 1.4 m in front of you and turns to face you again if you look well away.
- The VR mode starts the game with `-nsltvr`. That's the only thing that switches VR on.

### Controls (motion controllers)

The controllers act as a gamepad, and a real gamepad also works. Grips and trackpads are deliberately unbound.

| Left controller | | Right controller | |
|---|---|---|---|
| Stick | Move | Stick | Camera |
| Stick click | Pause (Start) | Stick click | Center camera |
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
| `[General] VROnlyFromVRShortcut` | true | `false` = always start in VR |

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
- **Launching**: Steam treats the game as flat and would open SteamVR's "Resume Game" menu over it. The mod registers
  a SteamVR app (`nsltvr.mod.game`), and the shortcuts start the game through SteamVR or as a Steam VR-library entry,
  which avoids that menu. `steam_appid.txt` lets the game start directly without restarting through Steam.
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
| `launcher\` | The desktop shortcut's launcher |
| `patcher\` | `globalgamemanagers` patcher |
| `tools\` | Steam shortcut writer, SteamVR library cleanup |
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
