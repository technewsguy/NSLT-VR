// NSLT-VR: VR mod for New Super Lucky's Tale (Unity 2017.4, Mono, BepInEx 5).
// Written in C# 5 so it builds with the .NET Framework csc against the game's .NET 3.5 profile.
using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;
using GameConfiguration = Playful.SuperLucky.Global.GameConfiguration;
using CameraManager = Playful.SuperLucky.Camera.CameraManager;

namespace NSLTVR
{
    public enum YawFollowMode { Smooth, Snap, Fixed }
    public enum DesktopViewMode { Mirror, Spectator }
    public enum ControlsGuideMode { TitleScreenAndPause, TitleScreen, Always, Never }

    [BepInPlugin(Guid, "NSLT-VR", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "nsltvr.mod";
        public const string Version = "1.0.0";

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        // General
        internal static ConfigEntry<bool> EnableVR;
        internal static ConfigEntry<bool> ExperimentalNativeVRCamera;
        internal static ConfigEntry<float> EyeResolutionScale;
        internal static ConfigEntry<DesktopViewMode> DesktopView;
        // Camera
        internal static ConfigEntry<float> WorldScale;
        internal static ConfigEntry<float> OffsetBack;
        internal static ConfigEntry<float> OffsetUp;
        internal static ConfigEntry<bool> KeepGamePitch;
        internal static ConfigEntry<YawFollowMode> YawMode;
        internal static ConfigEntry<float> SnapAngle;
        internal static ConfigEntry<float> YawSharpness;
        internal static ConfigEntry<float> PositionSharpness;
        // UI
        internal static ConfigEntry<float> UiDistance;
        internal static ConfigEntry<float> UiWidth;
        internal static ConfigEntry<float> UiHeight;
        internal static ConfigEntry<bool> HeadLockedFade;
        internal static ConfigEntry<float> UiFollowAngle;
        internal static ConfigEntry<float> HeadsetScreenshotMinutes;
        internal static ConfigEntry<bool> TestPattern;
        internal static ConfigEntry<bool> DirectCamera;
        internal static ConfigEntry<int> BaselineStage;
        internal static ConfigEntry<int> BaselineTracking;
        internal static ConfigEntry<bool> OwnFlipV;
        internal static ConfigEntry<bool> SharedReflectionView;
        internal static ConfigEntry<bool> SeeThroughSilhouette;
        internal static ConfigEntry<bool> HideScreenGrabEffects;
        internal static ConfigEntry<float> TitleScreenScale;
        internal static ConfigEntry<float> HubNameSeconds;
        internal static ConfigEntry<bool> RecordUi;
        internal static ConfigEntry<bool> RegisterInSteamVR;
        internal static ConfigEntry<bool> VROnlyFromVRShortcut;
        // Keys
        internal static ConfigEntry<KeyCode> KeyRecenter;
        internal static ConfigEntry<KeyCode> KeyScaleDown;
        internal static ConfigEntry<KeyCode> KeyScaleUp;
        internal static ConfigEntry<KeyCode> KeyCloser;
        internal static ConfigEntry<KeyCode> KeyFarther;
        internal static ConfigEntry<KeyCode> KeyYawMode;
        internal static ConfigEntry<KeyCode> KeySnapshot;
        // Controllers
        internal static ConfigEntry<bool> EnableControllers;
        internal static ConfigEntry<bool> ShowControllers;
        internal static ConfigEntry<float> ControllerVisibility;
        internal static ConfigEntry<ControlsGuideMode> ControlsGuide;
        internal static ConfigEntry<int> StickAxis;
        internal static ConfigEntry<float> HapticStrength;
        internal static ConfigEntry<bool> LogControllerButtons;
        // Debug

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            EnableVR = Config.Bind("General", "EnableVR", true, "Master switch. false = play the game normally on the monitor.");
            ExperimentalNativeVRCamera = Config.Bind("General", "ExperimentalNativeVRCamera", false,
                "EXPERIMENTAL: re-enable the game's leftover original-Lucky's-Tale VR camera code instead of the mod's camera rig. Likely to break in many levels.");
            EyeResolutionScale = Config.Bind("General", "EyeResolutionScale", 1.0f, "Per-eye render resolution multiplier (on top of SteamVR's own supersampling).");
            DesktopView = Config.Bind("General", "DesktopView", DesktopViewMode.Mirror,
                "Mirror = monitor shows the headset view (fastest). Spectator = monitor shows the normal flat game camera + HUD (costs an extra scene render).");

            WorldScale = Config.Bind("Camera", "WorldScale", 1.0f, "Size of you relative to the world. Bigger = the world looks more like a miniature diorama. Hotkeys adjust it live.");
            OffsetBack = Config.Bind("Camera", "OffsetBack", 0.0f, "Extra distance (game units) to pull the VR viewpoint back from the game camera.");
            OffsetUp = Config.Bind("Camera", "OffsetUp", 0.0f, "Extra height (game units) to raise the VR viewpoint above the game camera.");
            KeepGamePitch = Config.Bind("Camera", "KeepGamePitch", false, "Follow the game camera's up/down tilt. Off (recommended) keeps the horizon level; you look down at Lucky yourself.");
            YawMode = Config.Bind("Camera", "YawMode", YawFollowMode.Smooth,
                "How the view turns when the game camera orbits. Smooth = follow smoothly. Snap = turn in steps (comfort). Fixed = never turn (use your head / recenter).");
            SnapAngle = Config.Bind("Camera", "SnapAngle", 30f, "Degrees per step in Snap yaw mode.");
            YawSharpness = Config.Bind("Camera", "YawSharpness", 6f, "How quickly Smooth yaw catches up (higher = snappier, 0 = instant).");
            PositionSharpness = Config.Bind("Camera", "PositionSharpness", 12f, "How quickly the viewpoint catches up to the game camera (higher = tighter, 0 = instant). Smooths out camera shake.");

            UiDistance = Config.Bind("UI", "Distance", 1.4f, "Distance of the floating HUD/menu panel from your head (meters, before world scale).");
            UiWidth = Config.Bind("UI", "Width", 1.6f, "Width of the floating HUD/menu panel (meters, before world scale).");
            UiHeight = Config.Bind("UI", "VerticalOffset", -0.1f, "Vertical offset of the HUD/menu panel relative to eye level.");
            UiFollowAngle = Config.Bind("UI", "FollowAngle", 35f, "When you look more than this many degrees away from the HUD/menu panel, it turns to face you again.");
            TitleScreenScale = Config.Bind("UI", "TitleScreenScale", 1.75f, "Size of the floating screen on the title screen, relative to Width (1 = same as in game).");
            HubNameSeconds = Config.Bind("UI", "HubNameSeconds", 5f, "How long the hub's name (e.g. Veggie Village) stays in the HUD corner after you arrive, in seconds. -1 = always (game default), 0 = never.");
            RecordUi = Config.Bind("Debug", "RecordUi", false, "Troubleshooting: log each new camera/menu and save pictures of the VR panel and desktop window to BepInEx\\nsltvr_uirec.");
            HeadLockedFade = Config.Bind("UI", "HeadLockedFade", true, "Fade the VR view to black during the game's screen fades / level transitions.");

            KeyRecenter = Config.Bind("Keys", "Recenter", KeyCode.F8, "Recenter the headset. (Gamepad: hold Back/View and press Left Bumper.)");
            KeyScaleDown = Config.Bind("Keys", "WorldScaleDown", KeyCode.F9, "Decrease world scale.");
            KeyScaleUp = Config.Bind("Keys", "WorldScaleUp", KeyCode.F10, "Increase world scale.");
            KeyCloser = Config.Bind("Keys", "MoveCloser", KeyCode.F6, "Move the viewpoint closer to the game camera target.");
            KeyFarther = Config.Bind("Keys", "MoveFarther", KeyCode.F7, "Pull the viewpoint farther back.");
            KeyYawMode = Config.Bind("Keys", "CycleYawMode", KeyCode.F11, "Cycle yaw mode Smooth -> Snap -> Fixed.");
            KeySnapshot = Config.Bind("Keys", "DebugSnapshot", KeyCode.F12, "Save PNGs of the VR eye view and the UI panel to BepInEx\\nsltvr_snapshots (for bug reports).");

            EnableControllers = Config.Bind("Controllers", "Enable", true,
                "Play with motion controllers (they act as a gamepad; a real gamepad keeps working too). Layout: left stick move, right stick camera, " +
                "right A = A (jump), right B = B, right trigger = gamepad left trigger, left trigger or left A = X, left B = Y, " +
                "left stick click = Start (pause), right stick click recenter game camera, both stick clicks recenter VR view.");
            ShowControllers = Config.Bind("Controllers", "ShowControllers", true, "Draw faint ghost versions of your motion controllers in VR.");
            ControlsGuide = Config.Bind("Controllers", "ControlsGuide", ControlsGuideMode.TitleScreenAndPause,
                "Labels next to each controller button saying what it does. TitleScreenAndPause = on the title screen and whenever the pause menu is open; TitleScreen; Always; Never.");
            ControllerVisibility = Config.Bind("Controllers", "ControllerVisibility", 0.35f,
                "How visible the ghost controllers are (0.05 = barely there, 1 = bright).");
            StickAxis = Config.Bind("Controllers", "StickAxis", 0,
                "Which OpenVR controller axis (0-4) is used as the thumbstick. 0 works for Index/Touch/Vive under SteamVR's legacy bindings; change only if the stick doesn't move Lucky (see the [L]/[R] lines in the log).");
            HapticStrength = Config.Bind("Controllers", "HapticStrength", 1f, "Controller vibration strength (0 = off, 1 = full).");
            LogControllerButtons = Config.Bind("Controllers", "LogButtons", true, "Log raw controller button/axis state whenever a button changes (for troubleshooting mappings).");

            HeadsetScreenshotMinutes = Config.Bind("Debug", "HeadsetScreenshotMinutes", 4f,
                "For troubleshooting: save a SteamVR screenshot of the real headset view every 15 s for this many minutes after launch (0 = off). Saved to BepInEx\\nsltvr_snapshots.");
            DirectCamera = Config.Bind("General", "DirectCamera", true, "true = one VR camera inside the rig (like VRGIN/VookaRaylee). false = render each eye to a texture and present it at the head pose.");
            TestPattern = Config.Bind("Debug", "TestPattern", false, "For troubleshooting: the VR eye camera shows solid magenta instead of the game.");
            BaselineStage = Config.Bind("Debug", "BaselineStage", 4,
                "Renderer. 4 = normal (the mod renders each eye and hands the frames to SteamVR itself). " +
                "0 = old renderer through Unity's built-in VR (shows black in this game). 1-3 = troubleshooting stages.");
            BaselineTracking = Config.Bind("Debug", "BaselineTracking", 0,
                "Troubleshooting only. 0 = leave tracking space alone. 1 = Unity room-scale. 2 = force SteamVR's compositor to Unity's tracking space every frame.");
            SharedReflectionView = Config.Bind("Camera", "SharedReflectionView", true,
                "Use the original Lucky's Tale VR shader mode: both eyes share one viewpoint for shine and reflections (false = each eye computes its own).");
            SeeThroughSilhouette = Config.Bind("Camera", "SeeThroughSilhouette", false,
                "Show the game's x-ray silhouette of Lucky through walls in VR. The game decides it from the desktop camera's view, so in VR it shows as a faint ghost in the wrong place.");
            HideScreenGrabEffects = Config.Bind("Camera", "HideScreenGrabEffects", true,
                "Hide effects that copy the screen (e.g. in Veggie Village) from the VR view. They reuse the desktop camera's picture, which shows as a faint ghost over everything in VR.");
            VROnlyFromVRShortcut = Config.Bind("General", "VROnlyFromVRShortcut", true,
                "true = VR only when started from 'New Super Lucky's Tale VR' in SteamVR's library (Steam's normal entry plays flat). false = always VR.");
            RegisterInSteamVR = Config.Bind("General", "RegisterInSteamVRLibrary", true,
                "Add \"New Super Lucky's Tale VR\" to SteamVR's library. Starting it from there (instead of from Steam) skips SteamVR's \"Resume Game\" menu at launch.");
            OwnFlipV = Config.Bind("Debug", "OwnFlipV", true, "Stage 4: flip the eye images vertically when handing them to SteamVR (set false if the view is upside down).");

            try
            {
                new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
            }
            catch (Exception e)
            {
                Log.LogError("Harmony patching failed: " + e);
            }

            Log.LogInfo(string.Format("XR supported devices: [{0}], loaded: '{1}', present: {2}, enabled: {3}",
                string.Join(", ", XRSettings.supportedDevices), XRSettings.loadedDeviceName, XRDevice.isPresent, XRSettings.enabled));

            if (!EnableVR.Value)
            {
                if (XRSettings.loadedDeviceName != "None" && XRSettings.loadedDeviceName != "")
                {
                    XRSettings.LoadDeviceByName("None");
                }
                Log.LogInfo("EnableVR=false; running flat.");
                return;
            }

            if (BaselineStage.Value == 4 && VROnlyFromVRShortcut.Value && !LaunchedFromVRShortcut())
            {
                // Started from Steam's normal entry: the flat game. VR is the "New Super Lucky's Tale VR" entry in
                // SteamVR's library, which starts the game with -nsltvr. Keep that entry's manifest file up to date.
                if (XRSettings.loadedDeviceName != "None" && XRSettings.loadedDeviceName != "") XRSettings.LoadDeviceByName("None");
                if (RegisterInSteamVR.Value) OwnVR.RegisterFromFlatRun(OwnVR.WriteVRManifest());
                Log.LogInfo("Started without -nsltvr: playing flat. (Use 'New Super Lucky's Tale VR' in SteamVR's library to play in VR.)");
                return;
            }

            if (BaselineStage.Value == 4)
            {
                if (XRSettings.loadedDeviceName != "None" && XRSettings.loadedDeviceName != "") XRSettings.LoadDeviceByName("None");
                var o = new GameObject("NSLTVR_OwnVR");
                DontDestroyOnLoad(o);   // not HideAndDontSave: hidden objects don't get OnApplicationQuit
                o.AddComponent<OwnVR>();
                Log.LogInfo("OWNVR: Unity VR off, mod submits frames to SteamVR itself.");
                return;
            }

            if (Array.IndexOf(XRSettings.supportedDevices, "OpenVR") < 0)
            {
                Log.LogError("OpenVR is not in the build's VR device list. Run the installer (it patches globalgamemanagers) and make sure nslt_Data\\Plugins\\openvr_api.dll exists.");
                return;
            }
            if (XRSettings.loadedDeviceName != "OpenVR")
            {
                Log.LogInfo("Loading OpenVR device...");
                XRSettings.LoadDeviceByName("OpenVR");
            }

            if (BaselineStage.Value > 0)
            {
                var b = new GameObject("NSLTVR_Baseline");
                DontDestroyOnLoad(b);
                b.hideFlags = HideFlags.HideAndDontSave;
                b.AddComponent<BaselineVR>();
                return;
            }

            if (!ExperimentalNativeVRCamera.Value)
            {
                // Before a scene's cameras ever render, make them all non-stereo. If a camera renders even once as a
                // stereo camera, Unity permanently overwrites its field of view with the headset's (broke the pause book).
                UnityEngine.SceneManagement.SceneManager.sceneLoaded += (scene, mode) => DemoteAllGameCameras();
                DemoteAllGameCameras();
            }

            var go = new GameObject("NSLTVR_Controller");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<VRController>();
        }

        internal const string VRArgument = "-nsltvr";

        internal static bool LaunchedFromVRShortcut()
        {
            foreach (var a in Environment.GetCommandLineArgs())
                if (string.Equals(a, VRArgument, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal static void DemoteAllGameCameras()
        {
            foreach (var c in Resources.FindObjectsOfTypeAll<Camera>())
            {
                if (c == null || c.name.StartsWith("NSLTVR_")) continue;
                if (c.stereoTargetEye != StereoTargetEyeMask.None) c.stereoTargetEye = StereoTargetEyeMask.None;
            }
        }

        internal static bool VRActive
        {
            get { return EnableVR.Value && XRSettings.loadedDeviceName == "OpenVR"; }
        }
    }

    // Exit menu -> Application.Quit(): stop handing frames to SteamVR before Unity starts tearing down.
    [HarmonyPatch(typeof(Application), "Quit", new Type[0])]
    internal static class Patch_ApplicationQuit
    {
        private static void Prefix()
        {
            OwnVR.BeginQuit("Application.Quit");
        }
    }

    // Re-enables the game's leftover VR camera mode when the experimental option is on.
    [HarmonyPatch(typeof(GameConfiguration), "CalculateShouldBeInVR")]
    internal static class Patch_CalculateShouldBeInVR
    {
        private static void Postfix(ref bool __result)
        {
            if (Plugin.EnableVR.Value && Plugin.ExperimentalNativeVRCamera.Value)
            {
                __result = true;
            }
        }
    }

    // In flat mode the game turns XR off when the camera is created; turn it straight back on.
    [HarmonyPatch(typeof(CameraManager), "EnableCamera")]
    internal static class Patch_EnableCamera
    {
        private static System.Collections.Generic.IEnumerable<CodeInstruction> Transpiler(System.Collections.Generic.IEnumerable<CodeInstruction> code)
        {
            return XRGuard.Rewrite(code, "CameraManager.EnableCamera");
        }

        private static void Postfix()
        {
            if (Plugin.VRActive && !XRSettings.enabled)
            {
                XRSettings.enabled = true;
            }
        }
    }

    // Changing resolution / fullscreen mode makes Unity 2017 tear down and recreate its VR device; afterwards the
    // frames it hands SteamVR are black. The desktop window doesn't matter in VR, so skip those changes.
    [HarmonyPatch(typeof(Playful.Unity.Utility.GraphicsSettings), "SetResolution")]
    internal static class Patch_GraphicsSetResolution
    {
        private static bool Prefix()
        {
            if (!Plugin.VRActive) return true;
            Plugin.Log.LogInfo("Skipped the game's resolution change (VR active).");
            return false;
        }
    }

    [HarmonyPatch(typeof(Playful.Unity.Utility.GraphicsSettings), "ApplyAllSettings")]
    internal static class Patch_GraphicsApplyAll
    {
        private static readonly System.Reflection.MethodInfo FullScreenSetter = AccessTools.PropertySetter(typeof(Screen), "fullScreen");
        private static readonly System.Reflection.MethodInfo Replacement = AccessTools.Method(typeof(Patch_GraphicsApplyAll), "SetFullScreen");

        public static void SetFullScreen(bool value)
        {
            if (Plugin.VRActive) return;
            Screen.fullScreen = value;
        }

        private static System.Collections.Generic.IEnumerable<CodeInstruction> Transpiler(System.Collections.Generic.IEnumerable<CodeInstruction> code)
        {
            foreach (var ins in code)
            {
                if ((ins.opcode == System.Reflection.Emit.OpCodes.Call || ins.opcode == System.Reflection.Emit.OpCodes.Callvirt) && Equals(ins.operand, FullScreenSetter))
                {
                    ins.opcode = System.Reflection.Emit.OpCodes.Call;
                    ins.operand = Replacement;
                }
                yield return ins;
            }
        }
    }

    [HarmonyPatch(typeof(StaticCamera), "Awake")]
    internal static class Patch_StaticCameraAwake
    {
        private static System.Collections.Generic.IEnumerable<CodeInstruction> Transpiler(System.Collections.Generic.IEnumerable<CodeInstruction> code)
        {
            return XRGuard.Rewrite(code, "StaticCamera.Awake");
        }
    }

    // The game (flat mode) turns XR off with "XRSettings.enabled = false" when cameras are created. Turning XR off and
    // back on makes Unity 2017 disconnect from SteamVR and reconnect, which SteamVR treats as the game exiting:
    // its dashboard then stays stuck in front of the game ("Resume Game" does nothing). These patches route those
    // calls through SetEnabled below, which ignores "off" while the mod's VR is active.
    internal static class XRGuard
    {
        private static readonly System.Reflection.MethodInfo Setter = AccessTools.PropertySetter(typeof(XRSettings), "enabled");
        private static readonly System.Reflection.MethodInfo Replacement = AccessTools.Method(typeof(XRGuard), "SetEnabled");

        public static void SetEnabled(bool value)
        {
            if (!value && Plugin.VRActive)
            {
                Plugin.Log.LogInfo("Blocked the game from turning XR off.");
                return;
            }
            XRSettings.enabled = value;
        }

        public static System.Collections.Generic.IEnumerable<CodeInstruction> Rewrite(System.Collections.Generic.IEnumerable<CodeInstruction> code, string where)
        {
            int n = 0;
            foreach (var ins in code)
            {
                if ((ins.opcode == System.Reflection.Emit.OpCodes.Call || ins.opcode == System.Reflection.Emit.OpCodes.Callvirt) && Equals(ins.operand, Setter))
                {
                    ins.opcode = System.Reflection.Emit.OpCodes.Call;
                    ins.operand = Replacement;
                    n++;
                }
                yield return ins;
            }
            Plugin.Log.LogInfo("XR guard: patched " + n + " XRSettings.enabled call(s) in " + where);
        }
    }
}
