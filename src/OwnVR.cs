// Baseline stage 4: bypass Unity's built-in VR completely (it hands SteamVR a black frame in this game).
// The mod talks to SteamVR itself, like the original SteamVR plugin for Unity 5.0-5.3 did:
//   main thread:   read the latest head pose, render each eye with an ordinary camera into its own texture
//   render thread: Submit both eye textures to SteamVR, then WaitGetPoses (paces the game to the headset)
// Submitting from Unity's render thread (via GL.IssuePluginEvent) keeps us off the D3D11 context Unity is using.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using Valve.VR;
using CameraManager = Playful.SuperLucky.Camera.CameraManager;
using FlatscreenHUDCamera = Playful.SuperLucky.Camera.FlatscreenHUDCamera;
using LodSettings = Playful.SuperLucky.Utilities.LodSettings;

namespace NSLTVR
{
    internal class OwnVR : MonoBehaviour
    {
        private delegate void RenderEventFn(int eventId);
        private static readonly RenderEventFn s_fn = OnRenderEvent;
        private static readonly IntPtr s_fnPtr = Marshal.GetFunctionPointerForDelegate(s_fn);
        private static IntPtr[] s_tex = new IntPtr[2];
        private static VRTextureBounds_t s_bounds;
        private static int s_submitErrors, s_submits, s_lateEvents;
        private static volatile int s_phase;   // 0 idle, 1 submit, 2 handoff, 3 WaitGetPoses (render thread)
        private static EVRCompositorError s_lastErr;

        private readonly TrackedDevicePose_t[] _render = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        private readonly TrackedDevicePose_t[] _game = new TrackedDevicePose_t[0];
        private GameObject _root;
        private Camera[] _eyes = new Camera[2];
        private RenderTexture[] _rt = new RenderTexture[2];
        private Vector3[] _eyePos = new Vector3[2];
        private Quaternion[] _eyeRot = { Quaternion.identity, Quaternion.identity };
        private float[][] _tan = new float[2][];
        private Camera _game3d;
        private float _nextScan, _nextLog = 10f, _nextShot = 20f;
        private int _shot, _frames;
        private bool _ok;

        // Connecting to SteamVR waits until the game's startup loading is over. Connected but frozen for seconds
        // (the "bootstrapper" scenes stall 3-4 s each), SteamVR treated the game as stalled and opened its
        // "Resume Game" menu over it at every launch.
        private bool _initTried;
        private float _calmSince;
        private int _firstFrames, _depthLogs;
        private float _initAt = -100f;
        private bool _lastDash;
        private int _dashLogs;

        private void TryDeferredInit()
        {
            if (_initTried) return;
            string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            bool loading = scene.StartsWith("bootstrapper", StringComparison.OrdinalIgnoreCase) || scene == "BlankScene" || Time.unscaledDeltaTime > 0.1f;
            float now = Time.realtimeSinceStartup;
            if (loading) _calmSince = now;
            if (now - _calmSince < 2f && now < 90f) return;   // 2 s without a freeze; give up waiting after 90 s
            _initTried = true;
            Plugin.Log.LogInfo("OWNVR: startup loading done (scene '" + scene + "'), connecting to SteamVR.");
            InitVR();
        }

        private void InitVR()
        {
            var err = EVRInitError.None;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            OpenVR.Init(ref err, EVRApplicationType.VRApplication_Scene);
            long tInit = sw.ElapsedMilliseconds;
            if (err == EVRInitError.None && Plugin.RegisterInSteamVR.Value) RegisterInSteamVRLibrary();
            if (err == EVRInitError.None)
            {
                _initAt = Time.realtimeSinceStartup;
                var key = new System.Text.StringBuilder(256);
                var aerr = OpenVR.Applications.GetApplicationKeyByProcessId((uint)System.Diagnostics.Process.GetCurrentProcess().Id, key, 256);
                Plugin.Log.LogInfo("DASHTRACE after Init: appKey='" + key + "' (" + aerr + ") dashboard=" + OpenVR.Overlay.IsDashboardVisible() +
                    " sceneFocus=" + OpenVR.Compositor.GetCurrentSceneFocusProcess());
            }
            if (err != EVRInitError.None) { Plugin.Log.LogError("OWNVR: OpenVR.Init failed: " + err); return; }
            OpenVR.Compositor.SetTrackingSpace(ETrackingUniverseOrigin.TrackingUniverseSeated);
            if (Plugin.EnableControllers.Value)
                VRControllers.InitActions(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "input\\actions.json"));
            long tInput = sw.ElapsedMilliseconds;

            uint w = 0, h = 0;
            OpenVR.System.GetRecommendedRenderTargetSize(ref w, ref h);
            // "(Clone)" + LodSettings(IgnoreMe): keeps the game's GlobalLod system from switching our panel off.
            _root = new GameObject("NSLTVR_OwnRoot(Clone)");
            DontDestroyOnLoad(_root);
            _root.AddComponent<LodSettings>().SizeOverride = LodSettings.SizeCategory.IgnoreMe;
            CreateUi();
            Plugin.Log.LogInfo("OWNVR overlay layer " + OverlayLayer + " is named '" + LayerMask.LayerToName(OverlayLayer) + "'");
            _hands = new OwnHands(_root.transform);
            CreateLoadingOverlay();
            for (int i = 0; i < 2; i++)
            {
                var eye = i == 0 ? EVREye.Eye_Left : EVREye.Eye_Right;
                _rt[i] = new RenderTexture((int)w, (int)h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
                _rt[i].name = "NSLTVR_Own" + eye;
                _rt[i].antiAliasing = 1;
                _rt[i].Create();
                s_tex[i] = _rt[i].GetNativeTexturePtr();

                var go = new GameObject("NSLTVR_Own" + eye);
                go.transform.SetParent(_root.transform, false);
                Camera c = go.AddComponent<Camera>();
                c.enabled = false;               // rendered manually
                c.stereoTargetEye = StereoTargetEyeMask.None;
                c.targetTexture = _rt[i];
                c.nearClipPlane = 0.05f;
                go.AddComponent<OwnOpaqueGrab>();   // per-eye scene copy for water/glass refraction
                _eyes[i] = c;

                float l = 0, r = 0, t = 0, b = 0;
                OpenVR.System.GetProjectionRaw(eye, ref l, ref r, ref t, ref b);
                _tan[i] = new[] { l, r, t, b };
                var m = OpenVR.System.GetEyeToHeadTransform(eye);
                ToUnity(m, out _eyePos[i], out _eyeRot[i]);
                Plugin.Log.LogInfo(string.Format("OWNVR eye {0}: tan l={1:0.000} r={2:0.000} t={3:0.000} b={4:0.000} pos={5} rot={6} raw=[{7:0.000} {8:0.000} {9:0.000} | {10:0.000} {11:0.000} {12:0.000} | {13:0.000} {14:0.000} {15:0.000}]",
                    eye, l, r, t, b, _eyePos[i].ToString("F4"), _eyeRot[i].eulerAngles, m.m0, m.m1, m.m2, m.m4, m.m5, m.m6, m.m8, m.m9, m.m10));
            }
            // Unity renders into textures bottom-up on D3D; SteamVR reads them top-down.
            s_bounds = new VRTextureBounds_t { uMin = 0f, uMax = 1f, vMin = Plugin.OwnFlipV.Value ? 1f : 0f, vMax = Plugin.OwnFlipV.Value ? 0f : 1f };
            _ok = true;
            Plugin.Log.LogInfo("OWNVR init timing ms: OpenVR.Init=" + tInit + " input=" + (tInput - tInit) + " rest=" + (sw.ElapsedMilliseconds - tInput));
            Plugin.Log.LogInfo(string.Format("OWNVR: ready. eye {0}x{1} tanL=({2:0.00},{3:0.00},{4:0.00},{5:0.00}) ipd={6:0.000}",
                w, h, _tan[0][0], _tan[0][1], _tan[0][2], _tan[0][3], _eyePos[1].x - _eyePos[0].x));
        }

        // Launched by Steam, the game is "Steam app 1038300": a flat game, so Steam opens SteamVR's dashboard
        // ("Resume Game" menu) over it at every launch, and nothing a running app can call closes it. Launched from
        // SteamVR's own library as a VR app, SteamVR clears its dashboard itself (tested). So every run registers
        // "New Super Lucky's Tale VR" in SteamVR's library, which starts nslt.exe directly; steam_appid.txt stops the
        // game from restarting itself through Steam when started that way (Steam features such as cloud saves still work).
        internal const string VRAppKey = "nsltvr.mod.game";

        // Local copy of one of Steam's cached images for the game (JSON-escaped path), or the same image on Steam's CDN.
        private static string SteamArt(string dir, string cacheName, string localName, string cdnName)
        {
            try
            {
                string local = System.IO.Path.Combine(dir, localName);
                if (!System.IO.File.Exists(local))
                {
                    string steam = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
                    string cached = steam != null ? System.IO.Path.Combine(steam, @"appcache\librarycache\1038300\" + cacheName) : null;
                    if (cached != null && System.IO.File.Exists(cached)) System.IO.File.Copy(cached, local);
                }
                if (System.IO.File.Exists(local)) return local.Replace("\\", "\\\\");
            }
            catch (Exception) { }
            return "https://shared.steamstatic.com/store_item_assets/steam/apps/1038300/" + cdnName;
        }

        // Writes the SteamVR library entry file (no SteamVR needed): starts nslt.exe with -nsltvr, which is what turns
        // VR on (see Plugin.LaunchedFromVRShortcut). SteamVR re-reads registered manifests when it starts.
        internal static string WriteVRManifest()
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
                string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                string gameDir = System.IO.Path.GetDirectoryName(exePath);
                string appId = System.IO.Path.Combine(gameDir, "steam_appid.txt");
                if (!System.IO.File.Exists(appId)) System.IO.File.WriteAllText(appId, "1038300");
                string manifest = System.IO.Path.Combine(dir, "nsltvr.vrmanifest");
                // The launcher starts the game itself (so Steam Input gamepads work when Steam started it) and only asks
                // SteamVR to "launch" this entry, with --noop, because that makes SteamVR close its dashboard.
                // Without the launcher, the entry starts the game directly in VR.
                string launchBin = System.IO.Path.Combine(gameDir, @"NSLTVR-launcher\nsltvr-launch.exe");
                string launchDir = System.IO.Path.GetDirectoryName(launchBin), launchArgs = "--noop";
                if (!System.IO.File.Exists(launchBin)) { launchBin = exePath; launchDir = gameDir; launchArgs = Plugin.VRArgument; }
                // Artwork, the same two images Steam gives its own VR games: the wide header (SteamVR's "Up Next" launch
                // screen and wide tiles) and the tall 600x900 capsule (library tiles). Copied from Steam's local cache.
                string image = SteamArt(dir, "library_header.jpg", "nsltvr_header.jpg", "header.jpg");
                string capsule = SteamArt(dir, "library_600x900.jpg", "nsltvr_capsule.jpg", "library_600x900.jpg");
                System.IO.File.WriteAllText(manifest,
                    "{\n  \"source\": \"builtin\",\n  \"applications\": [{\n" +
                    "    \"app_key\": \"" + VRAppKey + "\",\n" +
                    "    \"launch_type\": \"binary\",\n" +
                    "    \"binary_path_windows\": \"" + launchBin.Replace("\\", "\\\\") + "\",\n" +
                    "    \"working_directory\": \"" + launchDir.Replace("\\", "\\\\") + "\",\n" +
                    "    \"arguments\": \"" + launchArgs + "\",\n" +
                    "    \"is_dashboard_overlay\": false,\n" +
                    "    \"image_path\": \"" + image + "\",\n" +
                    "    \"image_path_capsule\": \"" + capsule + "\",\n" +
                    "    \"strings\": { \"en_us\": { \"name\": \"New Super Lucky's Tale VR\", \"description\": \"New Super Lucky's Tale with the NSLT-VR mod\" } }\n" +
                    "  }]\n}\n");
                return manifest;
            }
            catch (Exception ex) { Plugin.Log.LogWarning("OWNVR writing the SteamVR library entry failed: " + ex.Message); return null; }
        }

        // Flat runs: if SteamVR happens to be running, (re)register the library entry from a background thread as a
        // background app (which never starts SteamVR), so the VR entry exists without a VR run first.
        internal static void RegisterFromFlatRun(string manifest)
        {
            if (manifest == null || System.Diagnostics.Process.GetProcessesByName("vrserver").Length == 0) return;
            var t = new System.Threading.Thread(() =>
            {
                try
                {
                    var err = EVRInitError.None;
                    OpenVR.Init(ref err, EVRApplicationType.VRApplication_Background);
                    if (err != EVRInitError.None) return;
                    try { Plugin.Log.LogInfo("SteamVR library entry registered: " + OpenVR.Applications.AddApplicationManifest(manifest, false)); }
                    finally { OpenVR.Shutdown(); }
                }
                catch (Exception e) { Plugin.Log.LogWarning("SteamVR library registration (flat run) failed: " + e.Message); }
            });
            t.IsBackground = true;
            t.Start();
        }

        private static void RegisterInSteamVRLibrary()
        {
            try
            {
                string manifest = WriteVRManifest();
                if (manifest == null) return;
                var e = OpenVR.Applications.AddApplicationManifest(manifest, false);
                var key = new System.Text.StringBuilder(256);
                OpenVR.Applications.GetApplicationKeyByProcessId((uint)System.Diagnostics.Process.GetCurrentProcess().Id, key, 256);
                Plugin.Log.LogInfo("OWNVR SteamVR library entry '" + VRAppKey + "': " + e + ". This run was launched as '" + key + "'.");
            }
            catch (Exception ex) { Plugin.Log.LogWarning("OWNVR SteamVR library registration failed: " + ex.Message); }
        }

        // Runs on Unity's render thread, after the eye cameras' rendering has been processed.
        private static void OnRenderEvent(int id)
        {
            if (id == 2)
            {
                // Final event: release SteamVR's shared textures on the same thread that created them.
                try
                {
                    if (OpenVR.Compositor != null) OpenVR.Compositor.ClearLastSubmittedFrame();
                    OpenVR.Shutdown();
                    QuitTrace("render thread: OpenVR shut down");
                }
                catch (Exception e) { QuitTrace("render thread shutdown failed: " + e.Message); }
                return;
            }
            if (s_quitting) { if (s_lateEvents++ < 5) QuitTrace("render event after quit"); return; }
            if (id == 3)
            {
                try
                {
                    if (s_overlay != 0 && OpenVR.Overlay != null)
                    {
                        var ot = new Texture_t { handle = s_uiTexPtr, eType = ETextureType.DirectX, eColorSpace = EColorSpace.Auto };
                        OpenVR.Overlay.SetOverlayTexture(s_overlay, ref ot);
                    }
                }
                catch (Exception) { }
                return;
            }
            try
            {
                var comp = OpenVR.Compositor;
                if (comp == null) return;
                s_phase = 1;
                for (int i = 0; i < 2; i++)
                {
                    var tex = new Texture_t { handle = s_tex[i], eType = ETextureType.DirectX, eColorSpace = EColorSpace.Auto };
                    var e = comp.Submit(i == 0 ? EVREye.Eye_Left : EVREye.Eye_Right, ref tex, ref s_bounds, EVRSubmitFlags.Submit_Default);
                    if (e != EVRCompositorError.None) { s_submitErrors++; s_lastErr = e; } else s_submits++;
                }
                s_phase = 2;
                comp.PostPresentHandoff();
                s_phase = 3;
                comp.WaitGetPoses(s_poseBuf, s_gameBuf);
                s_phase = 0;
            }
            catch (Exception) { s_submitErrors++; }
        }
        private static readonly TrackedDevicePose_t[] s_poseBuf = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        private static readonly TrackedDevicePose_t[] s_gameBuf = new TrackedDevicePose_t[0];

        private void Update()
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            Application.runInBackground = true;
            TryDeferredInit();
            if (!_ok) return;
            float t = Time.realtimeSinceStartup;
            if (t - _initAt < 20f && OpenVR.Overlay != null)
            {
                bool dash = OpenVR.Overlay.IsDashboardVisible();
                if (dash != _lastDash || _dashLogs == 0)
                {
                    _dashLogs++;
                    Plugin.Log.LogInfo(string.Format("DASHTRACE t+{0:0.00}s dashboard={1} submits={2} frames={3}", t - _initAt, dash, s_submits, _firstFrames));
                }
                _lastDash = dash;
            }
            if (t >= _nextScan)
            {
                _nextScan = t + 0.5f;
                Camera best = null;
                var cm = CameraManager.Instance;
                if (cm != null && cm.GetCameraGameObject() != null) best = cm.GetCameraGameObject().GetComponent<Camera>();
                if (best == null)
                {
                    foreach (var c in Camera.allCameras)
                    {
                        if (c.name.StartsWith("NSLTVR_") || c.targetTexture != null || (c.cullingMask & 1) == 0) continue;
                        if (best == null || c.depth > best.depth) best = c;
                    }
                }
                if (best != null && best != _game3d)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var comp in best.GetComponents<Component>()) sb.Append(comp.GetType().FullName).Append(' ');
                    Plugin.Log.LogInfo("OWNVR game camera: " + best.name + " depthTex=" + best.depthTextureMode + " path=" + best.actualRenderingPath + " components: " + sb);
                }
                if (best != null) _game3d = best;
                ScanUi();
                KeepOverlayOffDesktop();
                RecordUi();
            }
            if (Input.GetKeyDown(Plugin.KeyRecenter.Value)) Recenter();
            if (Environment.GetEnvironmentVariable("NSLTVR_DIALOGTEST") != null && Input.GetKeyDown(KeyCode.F5))
            {
                Plugin.Log.LogInfo("DIALOGTEST: opening exit confirmation");
                Playful.SuperLucky.Ui.UiDialogueBox.ShowDialogue("dialog_warning", "Menu_Warning", "dialog_yes", "dialog_no", null);
                _dialogTraceUntil = t + 4f;
            }
            if (Environment.GetEnvironmentVariable("NSLTVR_DIALOGTEST") != null && Input.GetKeyDown(KeyCode.F3))
            {
                // Photograph each controller (with its guide labels) from 35 cm above/behind, as a player would see it.
                foreach (var handName in new[] { "NSLTVR_HandLeftHand", "NSLTVR_HandRightHand" })
                {
                    var hand = GameObject.Find(handName);
                    if (hand == null) { Plugin.Log.LogInfo("GUIDETEST " + handName + " not found"); continue; }
                    var go = new GameObject("NSLTVR_GuideShot");
                    var cam = go.AddComponent<Camera>();
                    cam.enabled = false;
                    cam.cullingMask = 1 << OverlayLayer;
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = new Color(0.35f, 0.3f, 0.25f, 1f);
                    cam.nearClipPlane = 0.01f;
                    cam.fieldOfView = 50f;
                    var rt = new RenderTexture(1024, 768, 24);
                    cam.targetTexture = rt;
                    Vector3 target = hand.transform.TransformPoint(new Vector3(0f, 0.02f, -0.06f));
                    go.transform.position = target + hand.transform.TransformDirection(new Vector3(0f, 0.25f, -0.25f));
                    go.transform.rotation = Quaternion.LookRotation(target - go.transform.position, hand.transform.up);
                    var hands = _hands;
                    if (hands != null) hands.Update(_render, go.transform.position);   // labels face this camera
                    cam.Render();
                    VRController.SaveRT(rt, System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "nsltvr_snapshots\\guide_" + handName + ".png"), true);
                    cam.targetTexture = null;
                    Destroy(rt);
                    Destroy(go);
                    Plugin.Log.LogInfo("GUIDETEST saved " + handName);
                }
            }
            if (Environment.GetEnvironmentVariable("NSLTVR_DIALOGTEST") != null && Input.GetKeyDown(KeyCode.F4))
            {
                VRController.SaveRT(_uiTex, System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "nsltvr_snapshots\\panel_play.png"), false);
                if (_game3d != null)
                    foreach (var r in _game3d.GetComponentsInChildren<Renderer>(true))
                        Plugin.Log.LogInfo(string.Format("GHOSTTEST camera child '{0}' active={1} enabled={2} layer={3} shader={4} localPos={5}", r.name,
                            r.gameObject.activeInHierarchy, r.enabled, r.gameObject.layer, r.sharedMaterial != null ? r.sharedMaterial.shader.name : "-", r.transform.localPosition));
                foreach (var r in FindObjectsOfType<Renderer>())
                {
                    if (r.sharedMaterial == null || r.sharedMaterial.shader.name.IndexOf("Water", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var sb = new System.Text.StringBuilder("GHOSTTEST water '" + r.name + "' enabled=" + r.enabled + " shader=" + r.sharedMaterial.shader.name + " comps:");
                    foreach (var comp in r.GetComponents<Component>()) sb.Append(' ').Append(comp.GetType().FullName);
                    foreach (var mat in r.sharedMaterials)
                    {
                        if (mat == null) continue;
                        sb.Append(" | mat '" + mat.name + "' kw=" + string.Join(",", mat.shaderKeywords) + " tex:");
                        foreach (var p in new[] { "_MainTex", "_ReflectionTex", "_ReflectionTexture", "_RefractionTex", "_GrabTexture", "_BackgroundTexture", "_CameraOpaqueTexture",
                            "_ScreenTex", "_DistortTex", "_Cube", "_ReflectionCube", "_BumpMap", "_NormalMap", "_DepthTex" })
                            if (mat.HasProperty(p)) sb.Append(' ').Append(p).Append('=').Append(mat.GetTexture(p) != null ? mat.GetTexture(p).name : "null");
                    }
                    Plugin.Log.LogInfo(sb.ToString());
                }
                foreach (var gname in new[] { "_GrabTexture", "_ReflectionTex", "_RefractionTex", "_CameraDepthTexture", "_GrabPassTex", "_WaterGrab", "_BackgroundTexture" })
                {
                    var gt = Shader.GetGlobalTexture(gname);
                    Plugin.Log.LogInfo("GHOSTTEST global " + gname + " = " + (gt != null ? gt.name + " " + gt.width + "x" + gt.height : "null"));
                }
                Vector3 eye = _eyes[0] != null ? _eyes[0].transform.position : Vector3.zero;
                foreach (var r in FindObjectsOfType<Renderer>())
                {
                    if (!r.enabled || r.sharedMaterial == null) continue;
                    float dist = Mathf.Sqrt(r.bounds.SqrDistance(eye));
                    string sh = r.sharedMaterial.shader.name;
                    if (r.sharedMaterial.renderQueue >= 2450 || sh.IndexOf("See", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        sh.IndexOf("Ghost", StringComparison.OrdinalIgnoreCase) >= 0 || sh.IndexOf("Outline", StringComparison.OrdinalIgnoreCase) >= 0)
                        if (dist < 40f)
                            Plugin.Log.LogInfo(string.Format("GHOSTTEST transparent '{0}' (root '{1}') shader={2} queue={3} dist={4:0.0} layer={5} mats={6}",
                                r.name, r.transform.root.name, sh, r.sharedMaterial.renderQueue, dist, r.gameObject.layer, r.sharedMaterials.Length));
                }
                foreach (var cv in FindObjectsOfType<Canvas>())
                    if (cv.isRootCanvas && cv.enabled && cv.gameObject.activeInHierarchy)
                        Plugin.Log.LogInfo("GHOSTTEST canvas '" + cv.name + "' mode=" + cv.renderMode + " cam=" + (cv.worldCamera != null ? cv.worldCamera.name : "-") + " layer=" + cv.gameObject.layer);
            }
            if (t < _dialogTraceUntil && t >= _nextDialogTrace)
            {
                _nextDialogTrace = t + 0.25f;
                if (_dialogTraceUntil - t < 2.6f && _dialogTraceUntil - t > 2.3f)
                    VRController.SaveRT(_uiTex, System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "nsltvr_snapshots\\dialog_panel.png"), false);
                foreach (var box in Resources.FindObjectsOfTypeAll<Playful.SuperLucky.Ui.UiDialogueBox>())
                {
                    if (!box.gameObject.scene.IsValid()) continue;
                    var sb = new System.Text.StringBuilder("DIALOGTEST " + box.name + " active=" + box.gameObject.activeInHierarchy + " scale=" + box.transform.localScale.ToString("F2") + " layer=" + box.gameObject.layer);
                    foreach (var cam in box.transform.root.GetComponentsInChildren<Camera>(true))
                        sb.AppendFormat(" | cam '{0}' en={1} act={2} rt={3} mask=0x{4:X} depth={5} clear={6} inUi={7}", cam.name, cam.enabled, cam.gameObject.activeInHierarchy,
                            cam.targetTexture != null ? cam.targetTexture.name : "-", cam.cullingMask, cam.depth, cam.clearFlags, _uiCams.Contains(cam));
                    foreach (var cv in box.transform.root.GetComponentsInChildren<Canvas>(true))
                        sb.AppendFormat(" | canvas '{0}' mode={1} cam={2} root={3}", cv.name, cv.renderMode, cv.worldCamera != null ? cv.worldCamera.name : "-", cv.isRootCanvas);
                    Plugin.Log.LogInfo(sb.ToString());
                }
            }
            _input.Update(Recenter);   // motion controllers -> InControl gamepad; both stick clicks recenter; F12 screenshot
            UpdateHubName();
            KeepUiCamerasOnPanel();
            if (t >= _nextShot && t < Plugin.HeadsetScreenshotMinutes.Value * 60f) { _nextShot = t + 10f; Shot("auto"); }
            if (t >= _nextLog)
            {
                _nextLog = t + 10f;
                Plugin.Log.LogInfo(string.Format("OWNVR fps={0:0} frames/10s={1} submits={2} submitErrors={3} lastErr={4} game={5}",
                    1f / Mathf.Max(0.0001f, Time.smoothDeltaTime), _frames, s_submits, s_submitErrors, s_lastErr,
                    _game3d != null ? _game3d.name : "none"));
                _frames = 0;
            }
        }

        // Render at the very end of the frame so the game's camera has its final pose.
        private void LateUpdate()
        {
            if (!_ok) return;
            StartCoroutineOnce();
        }

        private readonly VRInputManager _input = new VRInputManager();
        private OwnHands _hands;
        private float _dialogTraceUntil, _nextDialogTrace;

        private void Recenter()
        {
            if (OpenVR.Chaperone != null) OpenVR.Chaperone.ResetZeroPose(ETrackingUniverseOrigin.TrackingUniverseSeated);
            _panelInit = false;
            Plugin.Log.LogInfo("OWNVR recentered.");
        }

        private bool _loop;
        private void StartCoroutineOnce()
        {
            if (_loop) return;
            _loop = true;
            StartCoroutine(RenderLoop());
        }

        private System.Collections.IEnumerator RenderLoop()
        {
            var eof = new WaitForEndOfFrame();
            while (true)
            {
                yield return eof;
                RenderFrame();
            }
        }

        private void RenderFrame()
        {
            if (!_ok || s_quitting) return;
            OpenVR.Compositor.GetLastPoses(_render, _game);
            Vector3 headPos = Vector3.zero;
            Quaternion headRot = Quaternion.identity;
            if (_render[0].bPoseIsValid) ToUnity(_render[0].mDeviceToAbsoluteTracking, out headPos, out headRot);

            if (_game3d != null)
            {
                _root.transform.position = _game3d.transform.position;
                _root.transform.rotation = Quaternion.Euler(0f, _game3d.transform.eulerAngles.y, 0f);
            }

            HideGrabPassEffects(true);
            try { RenderEyes(headPos, headRot); }
            finally { HideGrabPassEffects(false); RestoreDesktopOpaqueTextures(); }
            UpdateLoadingOverlay(headPos, headRot);
        }

        // ---- Loading overlay. The tip screen between worlds appears just as the game freezes to load, so no frame
        // carrying it ever reached the headset. A SteamVR overlay keeps showing the panel image on its own while the
        // game is frozen. Shown only while the game's loading screen is up.
        private static ulong s_overlay;
        private static IntPtr s_uiTexPtr;
        private bool _overlayShown;

        private void CreateLoadingOverlay()
        {
            try
            {
                var e = OpenVR.Overlay.CreateOverlay("nsltvr.loading", "NSLT-VR loading screen", ref s_overlay);
                if (e != EVROverlayError.None) { Plugin.Log.LogWarning("OWNVR loading overlay: " + e); s_overlay = 0; return; }
                OpenVR.Overlay.SetOverlayWidthInMeters(s_overlay, Mathf.Max(0.1f, Plugin.UiWidth.Value) * Mathf.Clamp(Plugin.TitleScreenScale.Value, 0.5f, 4f));
                var b = new VRTextureBounds_t { uMin = 0f, uMax = 1f, vMin = 1f, vMax = 0f };   // Unity textures are bottom-up
                OpenVR.Overlay.SetOverlayTextureBounds(s_overlay, ref b);
                s_uiTexPtr = _uiTex.GetNativeTexturePtr();
            }
            catch (Exception ex) { Plugin.Log.LogWarning("OWNVR loading overlay failed: " + ex.Message); s_overlay = 0; }
        }

        private static bool LoadingScreenUp()
        {
            var ls = Playful.SuperLucky.Ui.UiLoadingScreen.Instance;
            return ls != null && ls.gameObject.activeInHierarchy;
        }

        private void UpdateLoadingOverlay(Vector3 headPos, Quaternion headRot)
        {
            if (s_overlay == 0) return;
            bool want = LoadingScreenUp();
            if (want && !_overlayShown)
            {
                // Place it in front of you (seated tracking space, same as our eyes), level, at panel distance.
                Quaternion yaw = Quaternion.Euler(0f, headRot.eulerAngles.y, 0f);
                Vector3 p = headPos + yaw * new Vector3(0f, Plugin.UiHeight.Value, Mathf.Max(0.5f, Plugin.UiDistance.Value) * 1.2f);
                var m = Matrix4x4.TRS(p, yaw, Vector3.one);
                var hm = new HmdMatrix34_t
                {
                    m0 = m.m00, m1 = m.m01, m2 = -m.m02, m3 = m.m03,
                    m4 = m.m10, m5 = m.m11, m6 = -m.m12, m7 = m.m13,
                    m8 = -m.m20, m9 = -m.m21, m10 = m.m22, m11 = -m.m23,
                };
                OpenVR.Overlay.SetOverlayTransformAbsolute(s_overlay, ETrackingUniverseOrigin.TrackingUniverseSeated, ref hm);
                var se = OpenVR.Overlay.ShowOverlay(s_overlay);
                _overlayShown = true;
                Plugin.Log.LogInfo("OWNVR loading overlay shown: " + se);
            }
            else if (!want && _overlayShown)
            {
                OpenVR.Overlay.HideOverlay(s_overlay);
                _overlayShown = false;
                Plugin.Log.LogInfo("OWNVR loading overlay hidden");
            }
            if (_overlayShown) GL.IssuePluginEvent(s_fnPtr, 3);   // refresh the overlay image (render thread)
        }

        // Put the desktop camera's scene copies back (our eyes replaced the globals while they rendered).
        private static readonly string[] OpaqueGlobals = { "_PlayfulGrabTexture", "_DownsampleScene", "_BlurredScene", "_PlayfulDepthTexture" };
        private static readonly string[] OpaqueFields = { "_playfulGrabTexture", "sceneTexture", "sceneTextureBlur", "_playfulDepthTexture" };
        private static System.Reflection.FieldInfo[] s_opaqueFields;

        private void RestoreDesktopOpaqueTextures()
        {
            if (_game3d == null) return;
            var pp = _game3d.GetComponent("PostProcessOpaque");
            if (pp == null) return;
            if (s_opaqueFields == null)
            {
                s_opaqueFields = new System.Reflection.FieldInfo[OpaqueFields.Length];
                for (int i = 0; i < OpaqueFields.Length; i++)
                    s_opaqueFields[i] = pp.GetType().GetField(OpaqueFields[i], System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            }
            for (int i = 0; i < OpaqueGlobals.Length; i++)
            {
                var tex = s_opaqueFields[i] != null ? s_opaqueFields[i].GetValue(pp) as Texture : null;
                if (tex != null) Shader.SetGlobalTexture(OpaqueGlobals[i], tex);
            }
        }

        // Effects whose shaders "grab" the screen (e.g. Veggie Village's ProjectedTexture_Grabpass, which surrounds
        // the camera) reuse the copy Unity grabbed for the *desktop* camera, so the VR view showed a faint ghost of
        // the desktop picture over everything. They are hidden while the eyes render; the desktop keeps them.
        private readonly List<Renderer> _grabPass = new List<Renderer>();
        private readonly List<Renderer> _grabPassHidden = new List<Renderer>();
        private float _nextGrabScan;

        private void HideGrabPassEffects(bool hide)
        {
            if (!Plugin.HideScreenGrabEffects.Value) return;
            if (hide)
            {
                if (Time.realtimeSinceStartup >= _nextGrabScan)
                {
                    _nextGrabScan = Time.realtimeSinceStartup + 2f;
                    _grabPass.Clear();
                    foreach (var r in FindObjectsOfType<Renderer>())
                    {
                        var m = r.sharedMaterial;
                        if (m != null && m.shader != null && m.shader.name.IndexOf("grabpass", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            _grabPass.Add(r);
                            Note(r, "hiding screen-grab effect '" + r.name + "' (" + m.shader.name + ") in VR");
                        }
                    }
                }
                _grabPassHidden.Clear();
                foreach (var r in _grabPass)
                    if (r != null && r.enabled) { r.enabled = false; _grabPassHidden.Add(r); }
            }
            else
            {
                foreach (var r in _grabPassHidden) if (r != null) r.enabled = true;
                _grabPassHidden.Clear();
            }
        }

        private void RenderEyes(Vector3 headPos, Quaternion headRot)
        {
            PlacePanel(headPos, headRot);
            if (_hands != null) _hands.Update(_render, _root.transform.TransformPoint(headPos));
            // The game's shaders have a VR path (from the original Lucky's Tale VR): with _VRCamPos.w = 1 they use
            // one shared viewpoint for shine/reflections instead of each eye's own, so both eyes agree. The game's
            // VRCameraPos script zeroes it every LateUpdate in flat mode; we render after that, so this wins for our eyes.
            if (Plugin.SharedReflectionView.Value)
            {
                Vector3 c = _root.transform.TransformPoint(headPos);
                Shader.SetGlobalVector("_VRCamPos", new Vector4(c.x, c.y, c.z, 1f));
            }
            // The game draws an x-ray silhouette of Lucky through walls when the *desktop* camera can't see him
            // (GlobalSeeThroughValue). From the VR viewpoint that showed as a faint ghost through everything, in a
            // different place in each eye. Off for the VR eyes only; the game sets it again every Update.
            if (!Plugin.SeeThroughSilhouette.Value) Shader.SetGlobalFloat("_GlobalSeeThroughValue", 0f);
            for (int i = 0; i < 2; i++)
            {
                Camera c = _eyes[i];
                c.transform.localPosition = headPos + headRot * _eyePos[i];
                c.transform.localRotation = headRot * _eyeRot[i];   // some headsets angle each eye's view outward
                if (_game3d != null)
                {
                    c.cullingMask = _game3d.cullingMask | (1 << OverlayLayer);
                    c.clearFlags = _game3d.clearFlags == CameraClearFlags.Skybox ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
                    Color bg = _game3d.backgroundColor; bg.a = 1f; c.backgroundColor = bg;
                    c.farClipPlane = _game3d.farClipPlane;
                    c.depthTextureMode = _game3d.depthTextureMode;   // water/shiny shaders read the depth texture
                }
                else
                {
                    c.cullingMask = 1 << OverlayLayer;   // just the panel and controllers
                    c.clearFlags = CameraClearFlags.SolidColor;
                    c.backgroundColor = new Color(0.1f, 0.12f, 0.2f, 1f);
                }
                if (Plugin.TestPattern.Value)
                {
                    c.cullingMask = 0;
                    c.clearFlags = CameraClearFlags.SolidColor;
                    c.backgroundColor = i == 0 ? Color.magenta : Color.cyan;
                }
                c.projectionMatrix = Projection(_tan[i], c.nearClipPlane, c.farClipPlane);
                c.Render();
                if (_depthLogs < 4 && _game3d != null && _game3d.name == "Flat Screen Camera")
                {
                    _depthLogs++;
                    var dt = Shader.GetGlobalTexture("_CameraDepthTexture");
                    Plugin.Log.LogInfo("DEPTHTEST eye " + i + " depthTextureMode=" + c.depthTextureMode + " game=" + _game3d.depthTextureMode +
                        " after render _CameraDepthTexture=" + (dt != null ? dt.name + " " + dt.width + "x" + dt.height : "null"));
                }
            }
            _frames++;
            GL.IssuePluginEvent(s_fnPtr, 1);
            if (_firstFrames < 5)
            {
                _firstFrames++;
                Plugin.Log.LogInfo(string.Format("OWNVR frame {0}: dt={1:0}ms", _firstFrames, Time.unscaledDeltaTime * 1000f));
            }
        }

        // ------------------------------------------------------------------ menus / HUD panel
        private const int UiW = 1920, UiH = 1080;
        // Layer for the panel, ghost controllers and labels: one the game doesn't use (layer 29 "OffCameraFX" turned
        // out to feed the water-ripple camera). The desktop camera has it removed from its mask (KeepOverlayOffDesktop).
        private static int s_overlayLayer = -1;
        internal static int OverlayLayer
        {
            get
            {
                if (s_overlayLayer < 0)
                {
                    s_overlayLayer = 29;
                    for (int i = 31; i >= 3; i--)   // includes Unity's unnamed spare layers 3, 6 and 7
                        if (string.IsNullOrEmpty(LayerMask.LayerToName(i))) { s_overlayLayer = i; break; }
                    Plugin.Log.LogInfo("OWNVR overlay layer: " + s_overlayLayer + " ('" + LayerMask.LayerToName(s_overlayLayer) + "')");
                }
                return s_overlayLayer;
            }
        }

        private void KeepOverlayOffDesktop()
        {
            int bit = 1 << OverlayLayer;
            foreach (var c in Camera.allCameras)
                if (!c.name.StartsWith("NSLTVR_") && (c.cullingMask & bit) != 0) c.cullingMask &= ~bit;
        }
        private RenderTexture _uiTex;
        private Camera _uiClear, _uiCanvasCam;
        private readonly List<Camera> _uiCams = new List<Camera>();
        private readonly HashSet<int> _logged = new HashSet<int>();
        private GameObject _panel;
        private bool _panelInit;
        private Vector3 _panelAnchor;
        private float _panelYaw, _panelTargetYaw;

        private void CreateUi()
        {
            _uiTex = new RenderTexture(UiW, UiH, 24, RenderTextureFormat.ARGB32);
            _uiTex.name = "NSLTVR_UI";
            _uiTex.Create();
            _uiClear = UiCamera("NSLTVR_UIClearCam", -100f, CameraClearFlags.SolidColor, 0);
            _uiClear.backgroundColor = new Color(0f, 0f, 0f, 0f);
            // Screen-space canvases are re-pointed at this camera. Parked far below the world so it sees nothing else.
            _uiCanvasCam = UiCamera("NSLTVR_UICanvasCam", -98f, CameraClearFlags.Depth, ~0);
            _uiCanvasCam.nearClipPlane = 0.05f;
            _uiCanvasCam.farClipPlane = 50f;

            Shader ui = Shader.Find("UI/Default");
            var mat = VRController.OverlayMaterial(ui, _uiTex, 4001);   // drawn on top of the world
            var mesh = new Mesh();
            mesh.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(0.5f, -0.5f, 0f) };
            mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) };
            mesh.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();
            _panel = new GameObject("NSLTVR_Panel");
            _panel.layer = OverlayLayer;
            _panel.transform.SetParent(_root.transform, false);
            _panel.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = _panel.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        private Camera UiCamera(string name, float depth, CameraClearFlags clear, int mask)
        {
            var go = new GameObject(name);
            DontDestroyOnLoad(go);
            go.transform.position = new Vector3(0f, -5000f, 0f);
            var c = go.AddComponent<Camera>();
            c.stereoTargetEye = StereoTargetEyeMask.None;
            c.targetTexture = _uiTex;
            c.clearFlags = clear;
            c.cullingMask = mask;
            c.depth = depth;
            c.allowHDR = false;
            c.allowMSAA = false;
            c.useOcclusionCulling = false;
            return c;
        }

        private void Note(UnityEngine.Object o, string msg)
        {
            if (_logged.Add(o.GetInstanceID())) Plugin.Log.LogInfo("OWNVR " + msg);
        }

        // ---- UI recorder (Debug.RecordUi): logs each new camera/canvas and saves the panel and desktop images,
        // to find screens that don't reach the VR panel.
        private readonly HashSet<int> _seenCams = new HashSet<int>(), _seenCanvases = new HashSet<int>();
        private float _nextUiShot;
        private int _uiShots;

        private static string PathOf(Transform t)
        {
            string p = t.name;
            for (var x = t.parent; x != null && p.Length < 160; x = x.parent) p = x.name + "/" + p;
            return p;
        }

        private void RecordUi()
        {
            if (!Plugin.RecordUi.Value) return;
            bool changed = false;
            foreach (var c in Camera.allCameras)
            {
                if (c.name.StartsWith("NSLTVR_") || !_seenCams.Add(c.GetInstanceID())) continue;
                changed = true;
                Plugin.Log.LogInfo(string.Format("UIREC camera '{0}' depth={1} mask=0x{2:X} rt={3} clear={4} handled={5} scene={6}", PathOf(c.transform), c.depth, c.cullingMask,
                    c.targetTexture != null ? c.targetTexture.name : "-", c.clearFlags, c == _game3d ? "game view" : _uiCams.Contains(c) ? "panel" : "NOT SHOWN IN VR",
                    UnityEngine.SceneManagement.SceneManager.GetActiveScene().name));
            }
            foreach (var cv in FindObjectsOfType<Canvas>())
            {
                if (!cv.isRootCanvas || !_seenCanvases.Add(cv.GetInstanceID())) continue;
                changed = true;
                Plugin.Log.LogInfo(string.Format("UIREC canvas '{0}' mode={1} cam={2} layer={3}", PathOf(cv.transform), cv.renderMode,
                    cv.worldCamera != null ? cv.worldCamera.name : "-", LayerMask.LayerToName(cv.gameObject.layer)));
            }
            if (changed && Time.realtimeSinceStartup >= _nextUiShot && _uiShots < 40)
            {
                _nextUiShot = Time.realtimeSinceStartup + 1.5f;
                StartCoroutine(UiShotsSoon(_uiShots++));
            }
        }

        private System.Collections.IEnumerator UiShotsSoon(int n)
        {
            yield return new WaitForSecondsRealtime(1f);   // let the screen finish appearing
            string dir = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "nsltvr_uirec");
            System.IO.Directory.CreateDirectory(dir);
            string stamp = n.ToString("00") + "_" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            VRController.SaveRT(_uiTex, System.IO.Path.Combine(dir, stamp + "_panel.png"), true);
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, stamp + "_desktop.png"));
            Plugin.Log.LogInfo("UIREC pictures " + stamp);
        }

        // ---- Hub name ("Veggie Village" in the HUD corner): shown for a few seconds after arriving, then hidden.
        private Component _hubTitle;
        private System.Reflection.FieldInfo _hubTitleImage;
        private string _hubScene;
        private float _hubArrived;

        private void UpdateHubName()
        {
            float secs = Plugin.HubNameSeconds.Value;
            if (secs < 0f) return;   // -1 = always show (game default)
            string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (scene != _hubScene) { _hubScene = scene; _hubArrived = Time.realtimeSinceStartup; _hubTitle = null; }
            if (_hubTitle == null)
            {
                _hubTitle = FindObjectOfType<Playful.SuperLucky.Ui.UIHubTitleCard>();
                if (_hubTitle == null) return;
                if (_hubTitleImage == null)
                    _hubTitleImage = _hubTitle.GetType().GetField("_targetImage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            }
            var img = _hubTitleImage != null ? _hubTitleImage.GetValue(_hubTitle) as UnityEngine.UI.Image : null;
            if (img == null || img.sprite == null) return;
            bool inHub = scene.Contains("_Hub_");
            float a = 1f;
            if (inHub)
            {
                float t = Time.realtimeSinceStartup - _hubArrived;
                a = Mathf.Clamp01(1f - (t - secs));   // full for `secs`, then a 1 s fade
            }
            if (!Mathf.Approximately(img.canvasRenderer.GetAlpha(), a))
            {
                img.canvasRenderer.SetAlpha(a);
                if (a == 0f) Plugin.Log.LogInfo("OWNVR hub name hidden after " + secs + " s");
            }
        }

        // Menus/HUD end up in _uiTex: dedicated UI cameras (no Default layer) are pointed at it, and
        // screen-space canvases are moved onto our canvas camera.
        private void ScanUi()
        {
            if (_uiTex == null) return;
            Camera death = VRController.DeathCamera();
            var hud = FlatscreenHUDCamera.Instance;
            if (hud != null && hud.Camera != null && !_uiCams.Contains(hud.Camera)) _uiCams.Add(hud.Camera);
            foreach (var c in Camera.allCameras)
            {
                if (c.name.StartsWith("NSLTVR_") || c == _game3d || c == death || _uiCams.Contains(c) || c.targetTexture != null) continue;
                if (c.cullingMask != 0 && (c.cullingMask & 1) == 0) _uiCams.Add(c);
            }
            KeepUiCamerasOnPanel();
            foreach (var cv in FindObjectsOfType<Canvas>())
            {
                if (cv == null || !cv.isRootCanvas || cv.renderMode == RenderMode.WorldSpace) continue;
                if (cv.renderMode == RenderMode.ScreenSpaceOverlay)
                {
                    cv.renderMode = RenderMode.ScreenSpaceCamera;
                    cv.worldCamera = _uiCanvasCam;
                    cv.planeDistance = 1f;
                    Note(cv, "canvas '" + cv.name + "' (overlay) -> panel");
                    continue;
                }
                Camera wc = cv.worldCamera;
                if (wc == _uiCanvasCam || (wc != null && _uiCams.Contains(wc))) continue;
                if (wc != null && wc.targetTexture != null && wc.targetTexture != _uiTex) continue;
                cv.worldCamera = _uiCanvasCam;
                cv.planeDistance = Mathf.Clamp(cv.planeDistance, 0.1f, 40f);
                Note(cv, "canvas '" + cv.name + "' (camera) -> panel");
            }
        }

        // Runs every frame: the game re-points some UI cameras at the screen whenever their menu opens (the exit
        // confirmation does). Their drawing order is kept as the game set it (dialog 15 over pause book over HUD 3),
        // squeezed between our UI clear camera (-100) and our canvas camera (-98); giving them all one depth let the
        // pause book draw over the confirmation dialog.
        private readonly Dictionary<int, float> _uiOrigDepth = new Dictionary<int, float>();

        private void KeepUiCamerasOnPanel()
        {
            for (int i = _uiCams.Count - 1; i >= 0; i--)
            {
                Camera c = _uiCams[i];
                if (c == null) { _uiCams.RemoveAt(i); continue; }
                float orig;
                if (!_uiOrigDepth.TryGetValue(c.GetInstanceID(), out orig)) { orig = c.depth; _uiOrigDepth[c.GetInstanceID()] = orig; }
                if (c.targetTexture != _uiTex) c.targetTexture = _uiTex;
                if (c.clearFlags != CameraClearFlags.Depth && c.clearFlags != CameraClearFlags.Nothing) c.clearFlags = CameraClearFlags.Depth;
                float d = -99f + Mathf.Clamp(orig, -40f, 40f) * 0.02f;   // -99.8 .. -98.2, order preserved
                if (c.depth != d) c.depth = d;
                Note(c, "UI camera '" + c.name + "' (depth " + orig + ") -> panel");
            }
        }

        // The panel floats in front of you at eye level and turns to face you again when you look well away from it.
        private void PlacePanel(Vector3 headPos, Quaternion headRot)
        {
            if (_panel == null) return;
            float headYaw = headRot.eulerAngles.y;
            float dt = Time.unscaledDeltaTime;
            if (!_panelInit)
            {
                _panelAnchor = headPos;
                _panelYaw = _panelTargetYaw = headYaw;
                _panelInit = true;
            }
            if (Mathf.Abs(Mathf.DeltaAngle(_panelTargetYaw, headYaw)) > Plugin.UiFollowAngle.Value) _panelTargetYaw = headYaw;
            _panelYaw = Mathf.LerpAngle(_panelYaw, _panelTargetYaw, 1f - Mathf.Exp(-4f * dt));
            _panelAnchor = Vector3.Lerp(_panelAnchor, headPos, 1f - Mathf.Exp(-2f * dt));
            float w = Mathf.Max(0.1f, Plugin.UiWidth.Value);
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "TitleScreen")
                w *= Mathf.Clamp(Plugin.TitleScreenScale.Value, 0.5f, 4f);   // the title menu has no 3D world around it
            Quaternion yaw = Quaternion.Euler(0f, _panelYaw, 0f);
            _panel.transform.localPosition = _panelAnchor + yaw * new Vector3(0f, Plugin.UiHeight.Value, Mathf.Max(0.2f, Plugin.UiDistance.Value));
            _panel.transform.localRotation = yaw;
            _panel.transform.localScale = new Vector3(w, w * UiH / UiW, 1f);
            var rend = _panel.GetComponent<Renderer>();
            bool panelOn = !_overlayShown;   // while the loading overlay shows the same image, hide the in-world panel
            if (_panel.activeSelf != panelOn) _panel.SetActive(panelOn);
            if (panelOn && !rend.enabled) rend.enabled = true;   // the game's LOD system likes to switch renderers off
        }

        // OpenVR's raw tangents, used the way Valve's own samples do (ComposeProjection in hellovr):
        // in Unity's y-up projection the "bottom" value is the upper edge and "top" the lower edge.
        private static Matrix4x4 Projection(float[] tan, float n, float f)
        {
            float l = tan[0], r = tan[1], top = tan[3], bottom = tan[2];
            var m = new Matrix4x4();
            m[0, 0] = 2f / (r - l);
            m[0, 2] = (r + l) / (r - l);
            m[1, 1] = 2f / (top - bottom);
            m[1, 2] = (top + bottom) / (top - bottom);
            m[2, 2] = -(f + n) / (f - n);
            m[2, 3] = -2f * f * n / (f - n);
            m[3, 2] = -1f;
            return m;
        }

        // OpenVR is right-handed (-z forward); Unity is left-handed (+z forward).
        internal static void ToUnity(HmdMatrix34_t p, out Vector3 pos, out Quaternion rot)
        {
            var m = Matrix4x4.identity;
            m[0, 0] = p.m0; m[0, 1] = p.m1; m[0, 2] = -p.m2; m[0, 3] = p.m3;
            m[1, 0] = p.m4; m[1, 1] = p.m5; m[1, 2] = -p.m6; m[1, 3] = p.m7;
            m[2, 0] = -p.m8; m[2, 1] = -p.m9; m[2, 2] = p.m10; m[2, 3] = -p.m11;
            pos = new Vector3(m[0, 3], m[1, 3], m[2, 3]);
            rot = Quaternion.LookRotation(m.GetColumn(2), m.GetColumn(1));
        }

        private void Shot(string reason)
        {
            try
            {
                string dir = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "nsltvr_snapshots");
                System.IO.Directory.CreateDirectory(dir);
                string b = System.IO.Path.Combine(dir, "own_" + (_shot++).ToString("000"));
                uint handle = 0;
                var e = OpenVR.Screenshots != null ? OpenVR.Screenshots.TakeStereoScreenshot(ref handle, b + "_submitted_preview", b + "_submitted_vr") : EVRScreenshotError.RequestFailed;
                VRInputManager.SaveMirrorTexture(OpenVR.Compositor, b + "_mirror.png");
                Plugin.Log.LogInfo("OWNVR shot (" + reason + "): " + e + " scene=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name +
                    " lastRenderer=" + OpenVR.Compositor.GetLastFrameRenderer() + " ourPid=" + System.Diagnostics.Process.GetCurrentProcess().Id);
            }
            catch (Exception ex) { Plugin.Log.LogError("OWNVR shot failed: " + ex.Message); }
        }

        // Quitting: stop rendering and submitting first. Shutting OpenVR down here raced with the render thread
        // (still in Submit/WaitGetPoses) and hung or crashed the game on exit; the connection closes with the process.
        private static volatile bool s_quitting;
        private static OwnVR s_instance;

        internal static void BeginQuit(string why)
        {
            if (s_quitting) return;
            s_quitting = true;
            QuitTrace("BeginQuit " + why);
            if (s_instance == null || !s_instance._ok) return;   // never connected to SteamVR: normal exit, no safety net
            StartExitSafetyNet();
            Plugin.Log.LogInfo("OWNVR: quitting (" + why + "), stopped submitting frames.");
            s_instance._ok = false;
            s_instance.StopAllCoroutines();
            try { if (s_overlay != 0) OpenVR.Overlay.HideOverlay(s_overlay); } catch (Exception) { }
            for (int i = 0; i < 2; i++)
                if (s_instance._eyes[i] != null) s_instance._eyes[i].targetTexture = null;
            // Queue the shutdown on the render thread, then wait for it (GetNativeTexturePtr syncs with that thread).
            GL.IssuePluginEvent(s_fnPtr, 2);
            if (s_instance._rt[0] != null) s_instance._rt[0].GetNativeTexturePtr();
            QuitTrace("render thread synced");
            for (int i = 0; i < 2; i++)
                if (s_instance._rt[i] != null) { s_instance._rt[i].Release(); s_instance._rt[i] = null; }
            QuitTrace("eye textures released");
        }

        private void Awake() { s_instance = this; }

        // Written straight to disk (BepInEx's log is buffered and lost if the process hangs and gets killed).
        internal static void QuitTrace(string msg)
        {
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "nsltvr_quit.txt"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " [" + System.Threading.Thread.CurrentThread.ManagedThreadId + "] " + msg + "\r\n");
            }
            catch (Exception) { }
        }

        // By now no frames have been queued for a while, so the render thread is no longer inside OpenVR.
        private void OnDestroy()
        {
            QuitTrace("OwnVR.OnDestroy, render thread phase=" + s_phase);
        }

        // Once the render thread has run our (managed) submit callback, Unity's final engine shutdown deadlocks
        // (flat mode exits in ~1.5 s; without submitting, VR mode does too). Managed threads are frozen by then, so
        // the safety net is a tiny external command: 6 s after quitting starts (the game's save, an 11 KB write
        // through Steam, is long done), force-close this exact nslt.exe process if it is still running.
        private static void StartExitSafetyNet()
        {
            try
            {
                int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe",
                    "/c ping -n 7 127.0.0.1 >nul & taskkill /F /FI \"PID eq " + pid + "\" /FI \"IMAGENAME eq nslt.exe\"");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
                System.Diagnostics.Process.Start(psi);
                QuitTrace("exit safety net armed for pid " + pid);
            }
            catch (Exception e) { QuitTrace("exit safety net failed: " + e.Message); }
        }

        private void OnApplicationQuit() { BeginQuit("OnApplicationQuit"); }
    }
}
