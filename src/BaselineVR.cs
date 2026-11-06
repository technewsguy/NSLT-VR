// Stripped-down troubleshooting mode (Debug.BaselineStage > 0). None of the full mod's machinery runs.
//   1 = VR on, nothing else: the game's own cameras render to the headset as Unity does by default.
//   2 = game cameras made non-stereo + ONE plain VR camera (both eyes) following the game camera.
//   3 = same as 2 but one VR camera per eye.
// Takes SteamVR mirror screenshots every 10 s and logs every camera's setup, so each stage can be judged
// without anyone wearing the headset.
using System;
using System.Text;
using UnityEngine;
using UnityEngine.XR;
using Valve.VR;

namespace NSLTVR
{
    internal class BaselineVR : MonoBehaviour
    {
        private int _stage;
        private GameObject _root;
        private Camera[] _cams = new Camera[0];
        private Camera _game;
        private float _nextShot = 20f, _nextScan, _nextLog = 15f;
        private int _shotIndex;
        private int _renders;

        private void Start()
        {
            _stage = Plugin.BaselineStage.Value;
            Plugin.Log.LogInfo("BASELINE stage " + _stage + " (full mod disabled)");
            if (_stage < 2) return;

            _root = new GameObject("NSLTVR_BaselineRoot");
            DontDestroyOnLoad(_root);
            int n = _stage == 2 ? 1 : 2;
            _cams = new Camera[n];
            for (int i = 0; i < n; i++)
            {
                var go = new GameObject(n == 1 ? "NSLTVR_BaseCam" : (i == 0 ? "NSLTVR_BaseLeft" : "NSLTVR_BaseRight"));
                go.transform.SetParent(_root.transform, false);
                Camera c = go.AddComponent<Camera>();
                c.stereoTargetEye = n == 1 ? StereoTargetEyeMask.Both : (i == 0 ? StereoTargetEyeMask.Left : StereoTargetEyeMask.Right);
                c.depth = 100 + i;
                c.nearClipPlane = 0.05f;
                _cams[i] = c;
            }
            Camera.onPostRender += OnAnyPostRender;
        }

        private void OnAnyPostRender(Camera c)
        {
            if (Array.IndexOf(_cams, c) >= 0) _renders++;
        }

        private void Update()
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            Application.runInBackground = true;
            if (!XRSettings.enabled && XRSettings.loadedDeviceName == "OpenVR") XRSettings.enabled = true;
            int fix = Plugin.BaselineTracking.Value;
            if (fix == 1 && XRDevice.GetTrackingSpaceType() != TrackingSpaceType.RoomScale)
            {
                bool ok = XRDevice.SetTrackingSpaceType(TrackingSpaceType.RoomScale);
                Plugin.Log.LogInfo("BASELINE tracking space -> RoomScale: " + ok + " now " + XRDevice.GetTrackingSpaceType());
            }
            if (fix == 2 && OpenVR.Compositor != null)
            {
                var want = XRDevice.GetTrackingSpaceType() == TrackingSpaceType.RoomScale
                    ? ETrackingUniverseOrigin.TrackingUniverseStanding : ETrackingUniverseOrigin.TrackingUniverseSeated;
                if (OpenVR.Compositor.GetTrackingSpace() != want)
                {
                    Plugin.Log.LogInfo("BASELINE compositor tracking space was " + OpenVR.Compositor.GetTrackingSpace() + ", setting " + want);
                    OpenVR.Compositor.SetTrackingSpace(want);
                }
            }
            if (Time.frameCount % 900 == 0 && OpenVR.Compositor != null)
                Plugin.Log.LogInfo("BASELINE compositor space=" + OpenVR.Compositor.GetTrackingSpace() + " unity space=" + XRDevice.GetTrackingSpaceType());

            float t = Time.realtimeSinceStartup;
            if (_stage >= 2 && t >= _nextScan)
            {
                _nextScan = t + 0.5f;
                Plugin.DemoteAllGameCameras();
                Camera best = null;
                foreach (var c in Camera.allCameras)
                {
                    if (c.name.StartsWith("NSLTVR_") || c.targetTexture != null || (c.cullingMask & 1) == 0) continue;
                    if (best == null || c.depth > best.depth) best = c;
                }
                if (best != _game && best != null) Plugin.Log.LogInfo("BASELINE game camera: " + best.name);
                if (best != null) _game = best;
            }

            if (Input.GetKeyDown(Plugin.KeySnapshot.Value)) Shot("F12");
            if (t >= _nextShot && t < Plugin.HeadsetScreenshotMinutes.Value * 60f) { _nextShot = t + 10f; Shot("auto"); }
            if (t >= _nextLog) { _nextLog = t + 10f; LogCameras(); }
        }

        private void LateUpdate()
        {
            if (_root == null || _game == null) return;
            _root.transform.position = _game.transform.position;
            _root.transform.rotation = Quaternion.Euler(0f, _game.transform.eulerAngles.y, 0f);
            foreach (var c in _cams)
            {
                c.cullingMask = _game.cullingMask;
                c.clearFlags = _game.clearFlags == CameraClearFlags.Skybox ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
                Color bg = _game.backgroundColor; bg.a = 1f;
                c.backgroundColor = bg;
                c.farClipPlane = _game.farClipPlane;
                if (Plugin.TestPattern.Value)
                {
                    c.cullingMask = 0;
                    c.clearFlags = CameraClearFlags.SolidColor;
                    c.backgroundColor = Color.magenta;
                }
            }
        }

        private static string Hooks(Delegate d)
        {
            if (d == null) return "none";
            var sb = new StringBuilder();
            foreach (var x in d.GetInvocationList())
                sb.Append((x.Method.DeclaringType != null ? x.Method.DeclaringType.FullName : "?") + "." + x.Method.Name + ";");
            return sb.ToString();
        }

        private void Shot(string reason)
        {
            try
            {
                string dir = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "nsltvr_snapshots");
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, "base" + _stage + "_" + (_shotIndex++).ToString("000") + ".png");
                VRInputManager.SaveMirrorTexture(OpenVR.Compositor, path);
                if (_shotIndex <= 6 && OpenVR.Screenshots != null)
                {
                    // Stereo screenshot = the exact texture the game submitted (not the compositor's output).
                    uint handle = 0;
                    string b = path.Substring(0, path.Length - 4);
                    var err = OpenVR.Screenshots.TakeStereoScreenshot(ref handle, b + "_submitted_preview", b + "_submitted_vr");
                    Plugin.Log.LogInfo("BASELINE stereo screenshot: " + err);
                }
                var comp = OpenVR.Compositor;
                Plugin.Log.LogInfo(string.Format("BASELINE shot ({0}) {1} scene='{2}' lastRenderer={3} ourPid={4} dashboard={5}",
                    reason, System.IO.Path.GetFileName(path), UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                    comp != null ? comp.GetLastFrameRenderer() : 0, System.Diagnostics.Process.GetCurrentProcess().Id,
                    OpenVR.Overlay != null && OpenVR.Overlay.IsDashboardVisible()));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("BASELINE shot failed: " + e.Message);
            }
        }

        private void LogCameras()
        {
            var sb = new StringBuilder();
            sb.AppendFormat("BASELINE fps={0:0} ourRenders/10s={1} xr={2} eyeTex={3}x{4} scale={5:0.00} cameras:",
                1f / Mathf.Max(0.0001f, Time.smoothDeltaTime), _renders, XRSettings.enabled,
                XRSettings.eyeTextureWidth, XRSettings.eyeTextureHeight, XRSettings.eyeTextureResolutionScale);
            _renders = 0;
            sb.AppendFormat(" viewportScale={0} screen={1}x{2}", XRSettings.renderViewportScale, Screen.width, Screen.height);
            foreach (var c in _cams)
                sb.AppendFormat(" [{0} rect={1} pixelRect={2} stereo={3} fov={4:0.0} aspect={5:0.00} scale={6}]", c.name, c.rect, c.pixelRect,
                    c.stereoEnabled, c.fieldOfView, c.aspect, c.transform.lossyScale);
            try
            {
                var poses = new TrackedDevicePose_t[1];
                OpenVR.System.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, 0f, poses);
                var m = poses[0].mDeviceToAbsoluteTracking;
                sb.AppendFormat(" hmdValid={0} connected={1} result={2} pos=({3:0.00},{4:0.00},{5:0.00}) activity={6} unityHead={7} {8}",
                    poses[0].bPoseIsValid, poses[0].bDeviceIsConnected, poses[0].eTrackingResult, m.m3, m.m7, m.m11,
                    OpenVR.System.GetTrackedDeviceActivityLevel(0),
                    InputTracking.GetLocalPosition(XRNode.Head), InputTracking.GetLocalRotation(XRNode.Head).eulerAngles);
            }
            catch (Exception e) { sb.Append(" pose err " + e.Message); }
            foreach (var c in Camera.allCameras)
            {
                sb.AppendFormat("\n   '{0}' depth={1} eye={2} rt={3} path={4} clear={5} mask=0x{6:X}",
                    c.name, c.depth, c.stereoTargetEye, c.targetTexture != null ? c.targetTexture.name : "-",
                    c.actualRenderingPath, c.clearFlags, c.cullingMask);
            }
            sb.Append("\n   global hooks: preCull=" + Hooks(Camera.onPreCull) + " preRender=" + Hooks(Camera.onPreRender) + " postRender=" + Hooks(Camera.onPostRender));
            foreach (var c in _cams)
            {
                foreach (var comp in c.GetComponents<Component>()) sb.Append(" comp:" + comp.GetType().Name);
                foreach (UnityEngine.Rendering.CameraEvent ev in Enum.GetValues(typeof(UnityEngine.Rendering.CameraEvent)))
                    if (c.GetCommandBuffers(ev).Length > 0) sb.Append(" cmdbuf@" + ev + "=" + c.GetCommandBuffers(ev).Length);
            }
            Plugin.Log.LogInfo(sb.ToString());
        }
    }
}
