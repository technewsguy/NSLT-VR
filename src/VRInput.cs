using System;
using InControl;
using UnityEngine;
using UnityEngine.XR;
using Valve.VR;

namespace NSLTVR
{
    // Reads both motion controllers through OpenVR's legacy controller-state API (the same path Unity 2017 uses),
    // which SteamVR provides for every controller type (Index, Touch, Vive wands, ...). Button layout can be
    // remapped per controller in SteamVR's "Controller Bindings" UI for this game.
    internal static class VRControllers
    {
        public struct Hand
        {
            public bool Connected;
            public uint Index;
            public string Type;
            public Vector2 Stick;
            public float Trigger;
            public bool A;          // lower face button (A / X)
            public bool B;          // upper face button (B / Y; "menu" on Vive wands)
            public bool Grip;
            public bool StickClick;
            public bool TriggerClick;
            public bool ClickBit, ClickGenuine;
            public ulong RawPressed;
            public VRControllerState_t Raw;
        }

        public static Hand Left;
        public static Hand Right;

        private static int _lastPollFrame = -1;
        private static bool _failed;
        private static bool _loggedSystem;
        private static float _rumbleLeft, _rumbleRight;
        private static readonly uint StateSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(VRControllerState_t));

        private const ulong MaskMenu = 1UL << (int)EVRButtonId.k_EButton_ApplicationMenu;
        private const ulong MaskGrip = 1UL << (int)EVRButtonId.k_EButton_Grip;
        private const ulong MaskA = 1UL << (int)EVRButtonId.k_EButton_A;
        private const ulong MaskAxis0 = 1UL << (int)EVRButtonId.k_EButton_Axis0;
        private const ulong MaskAxis1 = 1UL << (int)EVRButtonId.k_EButton_Axis1;

        public static bool AnyConnected { get { return Left.Connected || Right.Connected; } }

        // ---- SteamVR Input (action manifest). Used when the mod drives SteamVR itself (OwnVR).
        // Only sticks, triggers and A/B are bound by default, so grips and trackpads do nothing, and stick
        // clicks are real clicks (the legacy mode fakes a click whenever a stick is pushed far).
        private static bool s_actions;
        private static ulong s_set;
        private static readonly ulong[] s_stick = new ulong[2], s_click = new ulong[2], s_trig = new ulong[2], s_a = new ulong[2], s_b = new ulong[2], s_hap = new ulong[2];
        private static VRActiveActionSet_t[] s_active;
        private static readonly uint DigitalSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(InputDigitalActionData_t));
        private static readonly uint AnalogSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(InputAnalogActionData_t));
        private static readonly uint ActiveSetSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(VRActiveActionSet_t));

        public static void InitActions(string manifestPath)
        {
            try
            {
                var input = OpenVR.Input;
                if (input == null || !System.IO.File.Exists(manifestPath)) { Plugin.Log.LogWarning("SteamVR Input unavailable (" + manifestPath + "); using legacy controller input."); return; }
                var e = input.SetActionManifestPath(manifestPath);
                if (e != EVRInputError.None) { Plugin.Log.LogWarning("SetActionManifestPath failed: " + e + "; using legacy controller input."); return; }
                input.GetActionSetHandle("/actions/nslt", ref s_set);
                string[] side = { "l", "r" };
                for (int i = 0; i < 2; i++)
                {
                    input.GetActionHandle("/actions/nslt/in/stick_" + side[i], ref s_stick[i]);
                    input.GetActionHandle("/actions/nslt/in/stickclick_" + side[i], ref s_click[i]);
                    input.GetActionHandle("/actions/nslt/in/trigger_" + side[i], ref s_trig[i]);
                    input.GetActionHandle("/actions/nslt/in/a_" + side[i], ref s_a[i]);
                    input.GetActionHandle("/actions/nslt/in/b_" + side[i], ref s_b[i]);
                    input.GetActionHandle("/actions/nslt/out/haptic_" + side[i], ref s_hap[i]);
                }
                s_active = new[] { new VRActiveActionSet_t { ulActionSet = s_set } };
                s_actions = true;
                Plugin.Log.LogInfo("SteamVR Input active (" + manifestPath + ").");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("SteamVR Input init failed, using legacy controller input: " + ex.Message);
            }
        }

        private static bool Digital(ulong h)
        {
            var d = new InputDigitalActionData_t();
            return OpenVR.Input.GetDigitalActionData(h, ref d, DigitalSize, 0) == EVRInputError.None && d.bActive && d.bState;
        }

        private static InputAnalogActionData_t Analog(ulong h)
        {
            var d = new InputAnalogActionData_t();
            OpenVR.Input.GetAnalogActionData(h, ref d, AnalogSize, 0);
            return d;
        }

        private static void ReadHandActions(CVRSystem sys, ETrackedControllerRole role, int i, ref Hand h, string tag)
        {
            uint idx = sys.GetTrackedDeviceIndexForControllerRole(role);
            bool connected = idx != OpenVR.k_unTrackedDeviceIndexInvalid && sys.IsTrackedDeviceConnected(idx);
            if (!connected)
            {
                if (h.Connected) Plugin.Log.LogInfo("Controller " + tag + " disconnected.");
                h = new Hand();
                return;
            }
            if (!h.Connected || h.Index != idx)
            {
                h.Index = idx;
                h.Type = GetString(sys, idx, ETrackedDeviceProperty.Prop_ControllerType_String);
                Plugin.Log.LogInfo(string.Format("Controller {0} connected: index={1} type='{2}' (SteamVR Input)", tag, idx, h.Type));
            }
            h.Connected = true;
            var st = Analog(s_stick[i]);
            h.Stick = st.bActive ? new Vector2(st.x, st.y) : Vector2.zero;
            var tr = Analog(s_trig[i]);
            h.Trigger = tr.bActive ? Mathf.Clamp01(tr.x) : 0f;
            h.TriggerClick = h.Trigger > 0.8f;
            bool a = Digital(s_a[i]), b = Digital(s_b[i]), c = Digital(s_click[i]);
            if (Plugin.LogControllerButtons.Value && (a != h.A || b != h.B || c != h.StickClick))
                Plugin.Log.LogInfo(string.Format("[{0}] A={1} B={2} stickClick={3} stick=({4:0.00},{5:0.00}) trigger={6:0.00}", tag, a, b, c, h.Stick.x, h.Stick.y, h.Trigger));
            h.A = a; h.B = b; h.StickClick = c;
            h.Grip = false;
        }

        public static void Poll()
        {
            if (_lastPollFrame == Time.frameCount) return;
            _lastPollFrame = Time.frameCount;
            if (_failed) return;
            try
            {
                CVRSystem sys = OpenVR.System;
                if (!_loggedSystem)
                {
                    _loggedSystem = true;
                    if (sys == null) Plugin.Log.LogWarning("OpenVR system interface unavailable; motion controllers won't work.");
                    else
                    {
                        var sb = new System.Text.StringBuilder();
                        for (uint i = 0; i < OpenVR.k_unMaxTrackedDeviceCount; i++)
                        {
                            if (!sys.IsTrackedDeviceConnected(i)) continue;
                            sb.Append(" [" + i + ":" + sys.GetTrackedDeviceClass(i) + " " + GetString(sys, i, ETrackedDeviceProperty.Prop_ControllerType_String) + "]");
                        }
                        Plugin.Log.LogInfo("OpenVR interface OK (" + OpenVR.IVRSystem_Version + "). Tracked devices:" + sb);
                    }
                }
                if (sys == null) { Left.Connected = Right.Connected = false; return; }
                if (s_actions)
                {
                    OpenVR.Input.UpdateActionState(s_active, ActiveSetSize);
                    ReadHandActions(sys, ETrackedControllerRole.LeftHand, 0, ref Left, "L");
                    ReadHandActions(sys, ETrackedControllerRole.RightHand, 1, ref Right, "R");
                    ApplyHaptics(sys);
                    return;
                }
                ReadHand(sys, ETrackedControllerRole.LeftHand, ref Left, "L");
                ReadHand(sys, ETrackedControllerRole.RightHand, ref Right, "R");
                ApplyHaptics(sys);
            }
            catch (Exception e)
            {
                _failed = true;
                Plugin.Log.LogError("Motion controller polling failed, controllers disabled: " + e);
            }
        }

        private static void ReadHand(CVRSystem sys, ETrackedControllerRole role, ref Hand h, string tag)
        {
            uint idx = sys.GetTrackedDeviceIndexForControllerRole(role);
            bool connected = idx != OpenVR.k_unTrackedDeviceIndexInvalid && sys.IsTrackedDeviceConnected(idx);
            if (!connected)
            {
                if (h.Connected) Plugin.Log.LogInfo("Controller " + tag + " disconnected.");
                h = new Hand();
                return;
            }
            if (!h.Connected || h.Index != idx)
            {
                h.Index = idx;
                h.Type = GetString(sys, idx, ETrackedDeviceProperty.Prop_ControllerType_String);
                Plugin.Log.LogInfo(string.Format("Controller {0} connected: index={1} type='{2}' model='{3}'",
                    tag, idx, h.Type, GetString(sys, idx, ETrackedDeviceProperty.Prop_ModelNumber_String)));
            }
            h.Connected = true;

            var s = new VRControllerState_t();
            if (!sys.GetControllerState(idx, ref s, StateSize)) return;

            int stickAxis = Mathf.Clamp(Plugin.StickAxis.Value, 0, 4);
            VRControllerAxis_t stick = Axis(s, stickAxis);
            h.Stick = new Vector2(stick.x, stick.y);
            h.Trigger = Mathf.Clamp01(Axis(s, 1).x);
            h.B = (s.ulButtonPressed & MaskMenu) != 0;
            if (h.Type == "knuckles")
            {
                // SteamVR's legacy binding for Index (legacy_bindings_index_controller.json) reports the A button as
                // "grip" (same bit as the physical grip) and B as "application menu". There is no separate A bit.
                h.A = (s.ulButtonPressed & MaskGrip) != 0;
                h.Grip = false;
            }
            else
            {
                h.A = (s.ulButtonPressed & MaskA) != 0;
                h.Grip = (s.ulButtonPressed & MaskGrip) != 0;
            }
            // SteamVR's legacy Index binding also reports the stick as "pressed" whenever it is pushed past ~85%
            // (released only once it springs back to the centre). A click counts only if the press began with
            // the stick centred, i.e. a real press straight down.
            bool bit = (s.ulButtonPressed & MaskAxis0) != 0;
            if (bit && !h.ClickBit) h.ClickGenuine = h.Stick.magnitude < 0.5f;
            h.ClickBit = bit;
            h.StickClick = bit && h.ClickGenuine;
            h.TriggerClick = (s.ulButtonPressed & MaskAxis1) != 0 || h.Trigger > 0.8f;

            if (Plugin.LogControllerButtons.Value && s.ulButtonPressed != h.RawPressed)
            {
                Plugin.Log.LogInfo(string.Format("[{0}] buttons=0x{1:X} axes: 0=({2:0.00},{3:0.00}) 1=({4:0.00},{5:0.00}) 2=({6:0.00},{7:0.00}) 3=({8:0.00},{9:0.00})",
                    tag, s.ulButtonPressed, s.rAxis0.x, s.rAxis0.y, s.rAxis1.x, s.rAxis1.y, s.rAxis2.x, s.rAxis2.y, s.rAxis3.x, s.rAxis3.y));
            }
            h.RawPressed = s.ulButtonPressed;
            h.Raw = s;
        }

        private static VRControllerAxis_t Axis(VRControllerState_t s, int i)
        {
            switch (i)
            {
                case 0: return s.rAxis0;
                case 1: return s.rAxis1;
                case 2: return s.rAxis2;
                case 3: return s.rAxis3;
                default: return s.rAxis4;
            }
        }

        private static string GetString(CVRSystem sys, uint idx, ETrackedDeviceProperty prop)
        {
            var err = ETrackedPropertyError.TrackedProp_Success;
            var sb = new System.Text.StringBuilder(256);
            sys.GetStringTrackedDeviceProperty(idx, prop, sb, 256, ref err);
            return err == ETrackedPropertyError.TrackedProp_Success ? sb.ToString() : "?";
        }

        public static void SetRumble(float left, float right)
        {
            _rumbleLeft = left;
            _rumbleRight = right;
        }

        private static void ApplyHaptics(CVRSystem sys)
        {
            float k = Mathf.Clamp01(Plugin.HapticStrength.Value);
            if (s_actions)
            {
                if (Left.Connected && _rumbleLeft * k > 0.05f) OpenVR.Input.TriggerHapticVibrationAction(s_hap[0], 0f, 0.02f, 160f, Mathf.Clamp01(_rumbleLeft * k), 0);
                if (Right.Connected && _rumbleRight * k > 0.05f) OpenVR.Input.TriggerHapticVibrationAction(s_hap[1], 0f, 0.02f, 160f, Mathf.Clamp01(_rumbleRight * k), 0);
                return;
            }
            if (Left.Connected && _rumbleLeft * k > 0.05f)
                sys.TriggerHapticPulse(Left.Index, 0, (ushort)Mathf.Clamp(_rumbleLeft * k * 3000f, 0f, 3999f));
            if (Right.Connected && _rumbleRight * k > 0.05f)
                sys.TriggerHapticPulse(Right.Index, 0, (ushort)Mathf.Clamp(_rumbleRight * k * 3000f, 0f, 3999f));
        }
    }

    // An InControl device that looks like an Xbox pad to the game, driven by the two motion controllers.
    // The game follows InControl's "active device", i.e. whatever was used last, so gamepad and controllers
    // can both stay connected.
    //
    //   Left stick  -> move            Right stick  -> camera
    //   Right A     -> jump (A)        Right B      -> B (jump / back in menus)
    //   Right trig. -> tail swipe (X)  Left A (X)   -> interact (Y)
    //   Left trig.  -> burrow (LT)     Grips        -> burrow (RT)
    //   Left B (Y)  -> pause (Start)   Left stick click -> Back / map
    //   Right stick click -> recenter game camera
    internal class VRControllerDevice : InputDevice
    {
        private const float Lower = 0.2f;
        private const float Upper = 0.9f;

        public VRControllerDevice()
            : base("NSLT-VR Motion Controllers")
        {
            Meta = "OpenVR motion controllers";
            DeviceClass = InputDeviceClass.Controller;
            DeviceStyle = InputDeviceStyle.XboxOne;   // so on-screen prompts show gamepad icons
            SortOrder = 100;

            AddControl(InputControlType.LeftStickLeft, "Left Stick Left", Lower, Upper);
            AddControl(InputControlType.LeftStickRight, "Left Stick Right", Lower, Upper);
            AddControl(InputControlType.LeftStickUp, "Left Stick Up", Lower, Upper);
            AddControl(InputControlType.LeftStickDown, "Left Stick Down", Lower, Upper);
            AddControl(InputControlType.RightStickLeft, "Right Stick Left", Lower, Upper);
            AddControl(InputControlType.RightStickRight, "Right Stick Right", Lower, Upper);
            AddControl(InputControlType.RightStickUp, "Right Stick Up", Lower, Upper);
            AddControl(InputControlType.RightStickDown, "Right Stick Down", Lower, Upper);
            AddControl(InputControlType.LeftTrigger, "Left Trigger", Lower, Upper);
            AddControl(InputControlType.RightTrigger, "Right Trigger", Lower, Upper);
            AddControl(InputControlType.DPadUp, "DPad Up", Lower, Upper);
            AddControl(InputControlType.DPadDown, "DPad Down", Lower, Upper);
            AddControl(InputControlType.DPadLeft, "DPad Left", Lower, Upper);
            AddControl(InputControlType.DPadRight, "DPad Right", Lower, Upper);
            AddControl(InputControlType.Action1, "A");
            AddControl(InputControlType.Action2, "B");
            AddControl(InputControlType.Action3, "X");
            AddControl(InputControlType.Action4, "Y");
            AddControl(InputControlType.LeftBumper, "Left Bumper");
            AddControl(InputControlType.RightBumper, "Right Bumper");
            AddControl(InputControlType.LeftStickButton, "Left Stick Button");
            AddControl(InputControlType.RightStickButton, "Right Stick Button");
            AddControl(InputControlType.Start, "Start");
            AddControl(InputControlType.Back, "Back");
        }

        public override void Update(ulong tick, float dt)
        {
            VRControllers.Poll();
            VRControllers.Hand l = VRControllers.Left;
            VRControllers.Hand r = VRControllers.Right;

            Stick(InputControlType.LeftStickLeft, InputControlType.LeftStickRight, InputControlType.LeftStickUp, InputControlType.LeftStickDown, l.Stick, tick, dt);
            Stick(InputControlType.RightStickLeft, InputControlType.RightStickRight, InputControlType.RightStickUp, InputControlType.RightStickDown, r.Stick, tick, dt);

            // Layout (user-tuned): right trigger = gamepad left trigger, left trigger / left A = X, left B = Y,
            // left stick click = Start (pause).
            UpdateWithValue(InputControlType.LeftTrigger, r.Trigger, tick, dt);
            UpdateWithValue(InputControlType.RightTrigger, (l.Grip || r.Grip) ? 1f : 0f, tick, dt);

            UpdateWithState(InputControlType.Action1, r.A, tick, dt);
            UpdateWithState(InputControlType.Action2, r.B, tick, dt);
            UpdateWithState(InputControlType.Action3, l.TriggerClick || l.A, tick, dt);
            UpdateWithState(InputControlType.Action4, l.B, tick, dt);
            UpdateWithState(InputControlType.Start, l.StickClick && !r.StickClick, tick, dt);
            UpdateWithState(InputControlType.Back, false, tick, dt);
            UpdateWithState(InputControlType.RightStickButton, r.StickClick && !l.StickClick, tick, dt);

            UpdateWithState(InputControlType.DPadUp, false, tick, dt);
            UpdateWithState(InputControlType.DPadDown, false, tick, dt);
            UpdateWithState(InputControlType.DPadLeft, false, tick, dt);
            UpdateWithState(InputControlType.DPadRight, false, tick, dt);
            UpdateWithState(InputControlType.LeftBumper, false, tick, dt);
            UpdateWithState(InputControlType.RightBumper, false, tick, dt);
            UpdateWithState(InputControlType.LeftStickButton, false, tick, dt);

            Commit(tick, dt);
        }

        private void Stick(InputControlType left, InputControlType right, InputControlType up, InputControlType down, Vector2 v, ulong tick, float dt)
        {
            UpdateWithValue(left, Mathf.Max(0f, -v.x), tick, dt);
            UpdateWithValue(right, Mathf.Max(0f, v.x), tick, dt);
            float y = InputManager.InvertYAxis ? -v.y : v.y;
            UpdateWithValue(up, Mathf.Max(0f, y), tick, dt);
            UpdateWithValue(down, Mathf.Max(0f, -y), tick, dt);
        }

        protected override void Vibrate(float leftMotor, float rightMotor)
        {
            VRControllers.SetRumble(leftMotor, rightMotor);
        }
    }

    // Keeps the controller device attached to InControl (which can reset between scenes),
    // handles the controller recenter gesture, and draws simple controller shapes in VR.
    internal class VRInputManager
    {
        private VRControllerDevice _device;
        private bool _loggedAttach;
        private bool _bothClicksWereDown;
        private Mesh _box;
        private Material _mat;

        private int _dashState = -1, _focusState = -1;

        // Logs SteamVR dashboard / input-focus changes and long frame stalls, to diagnose "stuck behind the dashboard".
        private void LogSteamVRState()
        {
            float dt = Time.unscaledDeltaTime;
            if (dt > 0.5f)
            {
                Plugin.Log.LogInfo(string.Format("Frame stall: {0:0.0}s (scene '{1}'). SteamVR may show its dashboard/last frame during this.",
                    dt, UnityEngine.SceneManagement.SceneManager.GetActiveScene().name));
            }
            try
            {
                CVRSystem sys = OpenVR.System;
                CVROverlay ov = OpenVR.Overlay;
                if (sys == null || ov == null) return;
                int dash = ov.IsDashboardVisible() ? 1 : 0;
                int focus = sys.IsInputAvailable() ? 1 : 0;
                if (dash != _dashState || focus != _focusState)
                {
                    Plugin.Log.LogInfo(string.Format("SteamVR: dashboard {0}, game has input focus: {1}, should pause: {2}",
                        dash == 1 ? "OPEN" : "closed", focus == 1, sys.ShouldApplicationPause()));
                    _dashState = dash;
                    _focusState = focus;
                }
            }
            catch (Exception) { }
        }

        private float _nextHeadsetShot = 20f;
        private int _headsetShotIndex;

        // Asks SteamVR for a screenshot of the final composited headset view (game + any SteamVR UI on top).
        // Taken automatically every 15 s for the first few minutes (UI.HeadsetScreenshotMinutes) and on F12.
        public void TakeHeadsetScreenshot(string reason)
        {
            TakeHeadsetScreenshot(reason, true);
        }

        // mirrorOnly: just grab SteamVR's mirror image (what the headset shows); the stereo screenshot stalls the game.
        public void TakeHeadsetScreenshot(string reason, bool full)
        {
            try
            {
                CVRScreenshots shots = OpenVR.Screenshots;
                if (shots == null) return;
                string dir = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "nsltvr_snapshots");
                System.IO.Directory.CreateDirectory(dir);
                string baseName = System.IO.Path.Combine(dir, "headset_" + (_headsetShotIndex++).ToString("000"));
                EVRScreenshotError e = EVRScreenshotError.None;
                if (full)
                {
                    uint handle = 0;
                    e = shots.TakeStereoScreenshot(ref handle, baseName + "_preview", baseName + "_vr");
                }
                CVRCompositor comp = OpenVR.Compositor;
                SaveMirrorTexture(comp, baseName + "_eye.png");
                Plugin.Log.LogInfo(string.Format("Headset screenshot ({0}): {1} -> {2}  sceneFocusPid={3} ourPid={4} lastRenderer={5}",
                    reason, e, baseName, comp != null ? comp.GetCurrentSceneFocusProcess() : 0,
                    System.Diagnostics.Process.GetCurrentProcess().Id, comp != null ? comp.GetLastFrameRenderer() : 0));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Headset screenshot failed: " + ex.Message);
            }
        }

        // Ground truth: the left-eye image exactly as SteamVR's compositor received it from the game.
        internal static void SaveMirrorTexture(CVRCompositor comp, string path)
        {
            if (comp == null) return;
            IntPtr srv = IntPtr.Zero;
            EVRCompositorError err = comp.GetMirrorTextureD3D11(EVREye.Eye_Left, Texture2D.whiteTexture.GetNativeTexturePtr(), ref srv);
            if (err != EVRCompositorError.None || srv == IntPtr.Zero)
            {
                Plugin.Log.LogInfo("Mirror texture unavailable: " + err);
                return;
            }
            Texture2D ext = null;
            RenderTexture rt = null;
            try
            {
                int w = Math.Max(16, XRSettings.eyeTextureWidth), h = Math.Max(16, XRSettings.eyeTextureHeight);
                ext = Texture2D.CreateExternalTexture(w, h, TextureFormat.RGBA32, false, false, srv);
                rt = RenderTexture.GetTemporary(1024, 1024, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(ext, rt);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(1024, 1024, TextureFormat.ARGB32, false);
                tex.ReadPixels(new Rect(0, 0, 1024, 1024), 0, 0);
                var px = tex.GetPixels32();
                for (int i = 0; i < px.Length; i++) px[i].a = 255;
                tex.SetPixels32(px);
                tex.Apply();
                RenderTexture.active = prev;
                System.IO.File.WriteAllBytes(path, ImageConversion.EncodeToPNG(tex));
                UnityEngine.Object.Destroy(tex);
            }
            finally
            {
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (ext != null) UnityEngine.Object.Destroy(ext);
                comp.ReleaseMirrorTextureD3D11(srv);
            }
        }

        public void Update(Action recenter)
        {
            LogSteamVRState();
            float minutes = Plugin.HeadsetScreenshotMinutes.Value;
            if (Input.GetKeyDown(Plugin.KeySnapshot.Value)) TakeHeadsetScreenshot("F12");
            if (minutes > 0f && Time.realtimeSinceStartup >= _nextHeadsetShot && Time.realtimeSinceStartup < minutes * 60f)
            {
                _nextHeadsetShot = Time.realtimeSinceStartup + 10f;
                TakeHeadsetScreenshot("auto", false);
            }
            if (!Plugin.EnableControllers.Value) return;

            if (InputManager.IsSetup)
            {
                if (_device == null) _device = new VRControllerDevice();
                if (!InputManager.Devices.Contains(_device))
                {
                    InputManager.AttachDevice(_device);
                    if (!_loggedAttach) { Plugin.Log.LogInfo("Motion controller input device attached to InControl."); _loggedAttach = true; }
                }
            }

            // Recenter: click both thumbsticks together.
            VRControllers.Poll();
            bool both = VRControllers.Left.StickClick && VRControllers.Right.StickClick;
            if (both && !_bothClicksWereDown) recenter();
            _bothClicksWereDown = both;
        }

        private GameObject _leftGo, _rightGo;
        private Material _matLeft;

        // Controller shapes are ordinary scene objects under the rig (see VRController for why), drawn on top.
        public void CreateControllerObjects(Transform rig, Shader shader)
        {
            if (shader == null) return;
            _box = BuildBox();
            _matLeft = VRController.OverlayMaterial(shader, Texture2D.whiteTexture, 4002);
            _mat = VRController.OverlayMaterial(shader, Texture2D.whiteTexture, 4002);
            _leftGo = MakeHandObject(rig, "NSLTVR_LeftHand", _matLeft);
            _rightGo = MakeHandObject(rig, "NSLTVR_RightHand", _mat);
        }

        private GameObject MakeHandObject(Transform rig, string name, Material mat)
        {
            var root = new GameObject(name);
            root.transform.SetParent(rig, false);
            var box = new GameObject(name + "_Box");
            box.transform.SetParent(root.transform, false);
            box.transform.localPosition = new Vector3(0f, -0.01f, 0.02f);
            box.transform.localScale = new Vector3(0.035f, 0.025f, 0.11f);
            box.AddComponent<MeshFilter>().sharedMesh = _box;
            var r = box.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            root.SetActive(false);
            return root;
        }

        public void UpdateControllerObjects()
        {
            bool show = Plugin.EnableControllers.Value && Plugin.ShowControllers.Value;
            UpdateHand(_leftGo, _matLeft, XRNode.LeftHand, VRControllers.Left, show, new Color(0.35f, 0.65f, 1f, 1f));
            UpdateHand(_rightGo, _mat, XRNode.RightHand, VRControllers.Right, show, new Color(1f, 0.55f, 0.15f, 1f));
        }

        private static void UpdateHand(GameObject go, Material mat, XRNode node, VRControllers.Hand hand, bool show, Color c)
        {
            if (go == null) return;
            Vector3 p = InputTracking.GetLocalPosition(node);
            Quaternion q = InputTracking.GetLocalRotation(node);
            bool on = show && hand.Connected && !(p == Vector3.zero && q == Quaternion.identity);
            if (go.activeSelf != on) go.SetActive(on);
            if (!on) return;
            go.transform.localPosition = p;
            go.transform.localRotation = q;
            if (hand.TriggerClick || hand.A || hand.B) c = Color.Lerp(c, Color.white, 0.5f);
            mat.color = c;
            VRController.EnsureEnabled(go.transform.GetChild(0).gameObject);
        }

        // Unit cube with baked per-face shading (vertex colours), since the UI shader is unlit.
        private static Mesh BuildBox()
        {
            var v = new System.Collections.Generic.List<Vector3>();
            var col = new System.Collections.Generic.List<Color>();
            var tri = new System.Collections.Generic.List<int>();
            Vector3[] n = { Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back };
            float[] shade = { 1f, 0.45f, 0.7f, 0.8f, 0.9f, 0.6f };
            for (int f = 0; f < 6; f++)
            {
                Vector3 nn = n[f];
                Vector3 a = Vector3.Cross(nn, Mathf.Abs(nn.y) > 0.5f ? Vector3.forward : Vector3.up);
                Vector3 b = Vector3.Cross(nn, a);
                int s = v.Count;
                v.Add((nn - a - b) * 0.5f); v.Add((nn + a - b) * 0.5f); v.Add((nn + a + b) * 0.5f); v.Add((nn - a + b) * 0.5f);
                for (int i = 0; i < 4; i++) col.Add(new Color(shade[f], shade[f], shade[f], 1f));
                tri.Add(s); tri.Add(s + 1); tri.Add(s + 2); tri.Add(s); tri.Add(s + 2); tri.Add(s + 3);
            }
            var m = new Mesh();
            m.name = "NSLTVR_Box";
            m.SetVertices(v);
            m.SetColors(col);
            m.SetTriangles(tri, 0);
            m.uv = new Vector2[v.Count];
            m.RecalculateBounds();
            return m;
        }
    }
}
