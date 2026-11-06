using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using CameraManager = Playful.SuperLucky.Camera.CameraManager;
using FlatscreenHUDCamera = Playful.SuperLucky.Camera.FlatscreenHUDCamera;
using CameraFadeController = Playful.SuperLucky.Camera.CameraFadeController;
using LodSettings = Playful.SuperLucky.Utilities.LodSettings;

namespace NSLTVR
{
    // VR rendering for New Super Lucky's Tale.
    //
    // How it works (and why), verified with SteamVR's captures of the frames it actually receives:
    //  * In this Unity 2017 build, the VR (stereo) camera must stay exactly at the real tracked head pose.
    //    If its world position is moved away from the head (parented to a rig at the game camera, or offset
    //    via stereo view matrices) SteamVR shows nothing but black.
    //  * So two ordinary (non-VR) "eye" cameras render the game world from a virtual rig that follows the game's
    //    camera, into one texture per eye, using the headset's exact per-eye projections. The real VR camera
    //    ("presenter") sits at the head pose and only draws, for each eye, a quad that exactly fills that eye's
    //    view with that eye's texture.
    //  * The HUD and screen-space menus are captured into a texture shown on a floating panel in the virtual
    //    world; the game's screen fades become a head-locked fade; controllers are drawn as simple boxes.
    public class VRController : MonoBehaviour
    {
        private const int UiTexWidth = 1920;
        private const int UiTexHeight = 1080;
        private const float CutDistance = 10f;
        private static readonly int[] PresentLayers = { 31, 30 };
        private const float PresentDistance = 4f;

        // Game cameras
        private Camera _flatCam;
        private Camera _hudCam;

        // Virtual rig: follows the game camera; eye cameras and overlay objects live in its space.
        private GameObject _rig;
        private readonly Camera[] _eyeCams = new Camera[2];
        private readonly RenderTexture[] _eyeTex = new RenderTexture[2];

        // Presenters: the actual VR cameras (one per eye), at the real head pose.
        private GameObject _trackingRoot;
        private Camera _vrCam;   // = left presenter; used to query the headset's per-eye projections
        private readonly Camera[] _presenters = new Camera[2];
        private Camera _presentClear;
        private readonly Material[] _presentMats = new Material[2];
        private readonly Mesh[] _presentMeshes = new Mesh[2];

        // UI capture
        private Camera _uiClearCam;
        private Camera _uiCanvasCam;
        private RenderTexture _uiTex;

        // Overlay objects (in the virtual rig)
        private Mesh _quad;
        private Material _panelMat;
        private Material _fadeMat;
        private GameObject _panelGo;
        private GameObject _fadeGo;
        private float _fadeAlpha;
        private Color _fadeColor = Color.black;

        // Rig state
        private bool _rigPoseValid;
        private Vector3 _centerPos;   // head position (tracking space) captured at recenter
        private float _centerYaw;     // head yaw (tracking space) captured at recenter
        private Vector3 _rigPos;
        private float _rigYaw;

        // Panel placement (rig-local, lazily follows the head)
        private Vector3 _panelAnchor;
        private float _panelYaw;
        private float _panelTargetYaw;
        private bool _panelInit;

        // Misc state
        private float _nextScan;
        private float _recenterAt = -1f;
        private bool _flatMaskHidden;
        private int _flatSavedMask;
        private readonly HashSet<int> _convertedCanvases = new HashSet<int>();
        private readonly HashSet<int> _demotedCameras = new HashSet<int>();
        private readonly List<Camera> _uiCams = new List<Camera>();
        private bool _nativeMode;
        private readonly VRInputManager _input = new VRInputManager();
        private UserPresenceState _lastPresence = UserPresenceState.Unknown;
        private float _smoothedFrameTime = 0.011f;

        // Health counters
        private int _eyeRenders, _presentRenders, _presentLeft, _presentRight;
        private float _nextHealthLog;

        private void Start()
        {
            _nativeMode = Plugin.ExperimentalNativeVRCamera.Value;
            XRSettings.eyeTextureResolutionScale = Mathf.Clamp(Plugin.EyeResolutionScale.Value, 0.3f, 3f);
            Plugin.Log.LogInfo("Tracking space: " + XRDevice.GetTrackingSpaceType());
            _recenterAt = Time.realtimeSinceStartup + 3f;

            if (_nativeMode)
            {
                Plugin.Log.LogWarning("ExperimentalNativeVRCamera is ON: using the game's own leftover VR camera. The mod rig is disabled.");
                return;
            }

            XRSettings.showDeviceView = Plugin.DesktopView.Value == DesktopViewMode.Mirror;

            CreateUiCapture();
            CreateRig();

            Application.onBeforeRender += OnBeforeRender;
            Camera.onPreCull += OnAnyPreCull;
            Camera.onPostRender += OnAnyPostRender;
            Plugin.Log.LogInfo("VR rig created. Device: " + XRDevice.model + " @ " + XRDevice.refreshRate + " Hz");
        }

        private void OnDestroy()
        {
            Application.onBeforeRender -= OnBeforeRender;
            Camera.onPreCull -= OnAnyPreCull;
            Camera.onPostRender -= OnAnyPostRender;
        }

        // ------------------------------------------------------------------ setup

        private void CreateUiCapture()
        {
            _uiTex = new RenderTexture(UiTexWidth, UiTexHeight, 24, RenderTextureFormat.ARGB32);
            _uiTex.name = "NSLTVR_UI";
            _uiTex.Create();

            // Clears the UI texture to transparent each frame (lowest depth of the UI cameras).
            var clearGo = new GameObject("NSLTVR_UIClearCam");
            DontDestroyOnLoad(clearGo);
            clearGo.transform.position = new Vector3(0f, -5000f, 0f);
            _uiClearCam = clearGo.AddComponent<Camera>();
            _uiClearCam.stereoTargetEye = StereoTargetEyeMask.None;
            _uiClearCam.targetTexture = _uiTex;
            _uiClearCam.clearFlags = CameraClearFlags.SolidColor;
            _uiClearCam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            _uiClearCam.cullingMask = 0;
            _uiClearCam.depth = -100f;
            _uiClearCam.allowHDR = false;
            _uiClearCam.allowMSAA = false;
            _uiClearCam.useOcclusionCulling = false;

            // Renders the screen-space canvases we re-point at it. Parked far below the world so it sees nothing else.
            var canvasGo = new GameObject("NSLTVR_UICanvasCam");
            DontDestroyOnLoad(canvasGo);
            canvasGo.transform.position = new Vector3(0f, -5000f, 0f);
            _uiCanvasCam = canvasGo.AddComponent<Camera>();
            _uiCanvasCam.stereoTargetEye = StereoTargetEyeMask.None;
            _uiCanvasCam.targetTexture = _uiTex;
            _uiCanvasCam.clearFlags = CameraClearFlags.Depth;
            _uiCanvasCam.cullingMask = ~0;
            _uiCanvasCam.nearClipPlane = 0.05f;
            _uiCanvasCam.farClipPlane = 50f;
            _uiCanvasCam.depth = -98f;
            _uiCanvasCam.allowHDR = false;
            _uiCanvasCam.useOcclusionCulling = false;
        }

        private void CreateRig()
        {
            // "(Clone)" suffix + LodSettings(IgnoreMe): both make the game's GlobalLod system leave our renderers alone.
            _rig = new GameObject("NSLTVR_Rig(Clone)");
            DontDestroyOnLoad(_rig);
            _rig.AddComponent<LodSettings>().SizeOverride = LodSettings.SizeCategory.IgnoreMe;

            if (Plugin.DirectCamera.Value)
            {
                // VRGIN-style (as used by VookaRaylee): VR cameras inside the rig that follows the game camera.
                // One camera per eye: in this single-pass build a both-eyes camera never reaches SteamVR (only the
                // compositor's empty grid shows), while Left/Right cameras are delivered. Unity applies the head pose
                // and eye offset to each, so this is real head-tracked stereo.
                for (int i = 0; i < 2; i++)
                {
                    var eye = new GameObject(i == 0 ? "NSLTVR_DirectLeft" : "NSLTVR_DirectRight");
                    eye.transform.SetParent(_rig.transform, false);
                    Camera c = eye.AddComponent<Camera>();
                    c.stereoTargetEye = i == 0 ? StereoTargetEyeMask.Left : StereoTargetEyeMask.Right;
                    c.depth = 100f + i;
                    c.allowHDR = false;
                    c.nearClipPlane = 0.05f;
                    // Deferred shading on per-eye VR cameras drew only a few objects and never cleared.
                    c.renderingPath = RenderingPath.Forward;
                    _eyeCams[i] = c;
                }
                _direct = _eyeCams[0];
                _vrCam = _direct;
                CreateOverlayObjects();
                Plugin.Log.LogInfo("Render mode: direct (per-eye VR cameras in the rig).");
                return;
            }

            // Eye cameras: ordinary cameras rendering the world into per-eye textures (matrices set every frame).
            for (int i = 0; i < 2; i++)
            {
                var go = new GameObject(i == 0 ? "NSLTVR_EyeLeft" : "NSLTVR_EyeRight");
                DontDestroyOnLoad(go);
                Camera c = go.AddComponent<Camera>();
                c.stereoTargetEye = StereoTargetEyeMask.None;
                c.depth = 50f;
                c.allowHDR = false;
                _eyeCams[i] = c;
            }
            EnsureEyeTextures();

            // Presenters: the real VR cameras, one per eye (the build uses single-pass stereo, so one camera can't
            // show a different texture per eye). They stay at the tracked head pose under a root that never moves.
            _trackingRoot = new GameObject("NSLTVR_TrackingRoot(Clone)");
            DontDestroyOnLoad(_trackingRoot);
            Shader blit = FindShader("Hidden/BlitCopy", "UI/Default");
            Plugin.Log.LogInfo("Present shader: " + (blit != null ? blit.name : "<none!>"));
            for (int i = 0; i < 2; i++)
            {
                var head = new GameObject(i == 0 ? "NSLTVR_PresenterLeft" : "NSLTVR_PresenterRight");
                head.transform.SetParent(_trackingRoot.transform, false);
                Camera pc = head.AddComponent<Camera>();
                pc.stereoTargetEye = i == 0 ? StereoTargetEyeMask.Left : StereoTargetEyeMask.Right;
                pc.clearFlags = CameraClearFlags.SolidColor;
                pc.backgroundColor = Color.black;
                pc.cullingMask = 1 << PresentLayers[i];
                pc.nearClipPlane = 0.05f;
                pc.farClipPlane = 100f;
                pc.depth = 100f + i;
                pc.allowHDR = false;
                pc.useOcclusionCulling = false;
                var hook = head.AddComponent<PresenterHook>();
                hook.Owner = this;
                hook.EyeIndex = i;
                _presenters[i] = pc;

                var mat = new Material(blit);
                mat.mainTexture = _eyeTex[i];
                mat.renderQueue = 4000;
                var mesh = new Mesh();
                mesh.name = "NSLTVR_PresentQuad" + i;
                mesh.vertices = new Vector3[4];
                mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) };
                mesh.colors = new[] { Color.white, Color.white, Color.white, Color.white };
                mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1000f);
                var quad = new GameObject("NSLTVR_PresentQuad" + i);
                quad.layer = PresentLayers[i];
                quad.transform.SetParent(head.transform, false);
                quad.AddComponent<MeshFilter>().sharedMesh = mesh;
                var pr = quad.AddComponent<MeshRenderer>();
                pr.sharedMaterial = mat;
                pr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                pr.receiveShadows = false;
                _presentMats[i] = mat;
                _presentMeshes[i] = mesh;

            }
            _vrCam = _presenters[0];

            // Per-eye cameras don't clear their half of the single-pass eye buffer, so old frames piled up (seen in
            // SteamVR's mirror). A both-eyes camera that renders nothing clears the whole buffer first each frame.
            var clearHead = new GameObject("NSLTVR_PresenterClear");
            clearHead.transform.SetParent(_trackingRoot.transform, false);
            _presentClear = clearHead.AddComponent<Camera>();
            _presentClear.enabled = false;   // disabled: a both-eyes camera here stopped frames reaching SteamVR
            _presentClear.stereoTargetEye = StereoTargetEyeMask.Both;
            _presentClear.clearFlags = CameraClearFlags.SolidColor;
            _presentClear.backgroundColor = Color.black;
            _presentClear.cullingMask = 0;
            _presentClear.depth = 99f;
            _presentClear.allowHDR = false;
            _presentClear.useOcclusionCulling = false;

            CreateOverlayObjects();
        }

        private Camera _direct;

        // Overlay objects in the virtual rig: drawn by the world camera(s) on top of the world.
        private void CreateOverlayObjects()
        {
            Shader ui = FindShader("UI/Default", "Sprites/Default", "Unlit/Transparent");
            Plugin.Log.LogInfo("Overlay shader: " + (ui != null ? ui.name : "<none!>"));
            _quad = BuildQuad();
            _fadeMat = OverlayMaterial(ui, Texture2D.whiteTexture, 4000);
            _panelMat = OverlayMaterial(ui, _uiTex, 4001);
            _fadeGo = OverlayObject("NSLTVR_Fade", _quad, _fadeMat);
            _panelGo = OverlayObject("NSLTVR_Panel", _quad, _panelMat);
            _input.CreateControllerObjects(_rig.transform, ui);
        }

        // SteamVR's recommended per-eye size (2688x2688 on the Beyond) x EyeResolutionScale.
        private static void RecommendedEyeSize(out int w, out int h)
        {
            uint rw = 0, rh = 0;
            try
            {
                var sys = Valve.VR.OpenVR.System;
                if (sys != null) sys.GetRecommendedRenderTargetSize(ref rw, ref rh);
            }
            catch (Exception) { }
            float s = Mathf.Clamp(Plugin.EyeResolutionScale.Value, 0.3f, 2f);
            if (rw > 0 && rh > 0) { w = Mathf.RoundToInt(rw * s); h = Mathf.RoundToInt(rh * s); }
            else { w = 2048; h = 2048; }
        }

        // Unity's own eye buffer (what the presenters draw into) should match too, or the image gets downsampled.
        private bool _eyeBufferMatched;

        // Done ONCE: Unity reports the eye buffer width inconsistently (per-eye vs double-wide), and re-applying the
        // scale made it rebuild the textures it shares with SteamVR over and over (which can black out the headset).
        private void MatchUnityEyeBuffer()
        {
            if (_eyeBufferMatched) return;
            if (!XRSettings.enabled || XRSettings.loadedDeviceName != "OpenVR" || Valve.VR.OpenVR.System == null) return;
            int w, h;
            RecommendedEyeSize(out w, out h);
            int cur = XRSettings.eyeTextureWidth;
            if (cur <= 0) return;
            _eyeBufferMatched = true;
            float scale = XRSettings.eyeTextureResolutionScale * w / cur;
            if (Mathf.Abs(scale - XRSettings.eyeTextureResolutionScale) > 0.02f)
            {
                XRSettings.eyeTextureResolutionScale = Mathf.Clamp(scale, 0.3f, 4f);
                Plugin.Log.LogInfo(string.Format("Unity eye buffer {0}px -> scale {1:0.00} to match SteamVR's {2}px", cur, XRSettings.eyeTextureResolutionScale, w));
            }
        }

        private void EnsureEyeTextures()
        {
            int w, h;
            RecommendedEyeSize(out w, out h);
            for (int i = 0; i < 2; i++)
            {
                RenderTexture t = _eyeTex[i];
                if (t != null && t.width == w && t.height == h) continue;
                if (t != null) { _eyeCams[i].targetTexture = null; t.Release(); Destroy(t); }
                // No alpha channel: SteamVR honours the alpha of what we present, and the game writes alpha 0 in places
                // (e.g. the sky), which let old frames show through as smeared trails while the head moved.
                // No alpha channel: SteamVR treats the presented image's alpha as transparency, and the game writes
                // alpha 0 in places (sky etc.), which showed SteamVR's grid and old frames through the game.
                RenderTextureFormat fmt = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGB111110Float)
                    ? RenderTextureFormat.RGB111110Float : RenderTextureFormat.RGB565;
                t = new RenderTexture(w, h, 24, fmt);
                t.name = i == 0 ? "NSLTVR_EyeLeftTex" : "NSLTVR_EyeRightTex";
                t.antiAliasing = Mathf.Clamp(QualitySettings.antiAliasing, 1, 8);
                t.Create();
                _eyeTex[i] = t;
                _eyeCams[i].targetTexture = t;
                Plugin.Log.LogInfo("Eye texture " + i + ": " + w + "x" + h + " msaa=" + t.antiAliasing);
            }
        }

        internal static Material OverlayMaterial(Shader shader, Texture tex, int queue)
        {
            var m = new Material(shader);
            m.mainTexture = tex;
            m.SetFloat("unity_GUIZTestMode", (float)UnityEngine.Rendering.CompareFunction.Always);
            m.renderQueue = queue;
            return m;
        }

        private GameObject OverlayObject(string name, Mesh mesh, Material mat)
        {
            var go = new GameObject(name);
            go.layer = 0;
            go.transform.SetParent(_rig.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go;
        }

        private static Texture2D _opaqueBlack;

        // Texture2D.blackTexture is transparent (alpha 0); we need alpha 1.
        private static Texture2D OpaqueBlack()
        {
            if (_opaqueBlack == null)
            {
                _opaqueBlack = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                _opaqueBlack.SetPixel(0, 0, new Color(0f, 0f, 0f, 1f));
                _opaqueBlack.Apply();
            }
            return _opaqueBlack;
        }

        private static Shader FindShader(params string[] names)
        {
            foreach (var n in names)
            {
                var s = Shader.Find(n);
                if (s != null) return s;
            }
            return null;
        }

        private static Mesh BuildQuad()
        {
            var m = new Mesh();
            m.name = "NSLTVR_Quad";
            m.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f) };
            m.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
            m.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            m.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            m.RecalculateBounds();
            return m;
        }

        internal static void EnsureEnabled(GameObject go)
        {
            var r = go.GetComponent<Renderer>();
            if (r != null && !r.enabled) r.enabled = true;
        }

        // ------------------------------------------------------------------ presenter (per eye)

        private readonly Vector3[] _corners = new Vector3[4];
        private readonly float[] _nextCornerLog = new float[2];

        // Called from each presenter camera's OnPreRender: show that eye's texture on a quad that exactly covers
        // that eye's frustum.
        internal void OnPresenterPreRender(Camera cam, int i)
        {
            _presentRenders++;
            if (i == 0) _presentLeft++; else _presentRight++;
            if (_presentMats[i].mainTexture != _eyeTex[i]) _presentMats[i].mainTexture = _eyeTex[i];
            Camera.MonoOrStereoscopicEye eye = i == 0 ? Camera.MonoOrStereoscopicEye.Left : Camera.MonoOrStereoscopicEye.Right;

            cam.CalculateFrustumCorners(new Rect(0f, 0f, 1f, 1f), PresentDistance, eye, _corners);
            // Corners are relative to the eye; the quad is a child of the (center) head, so shift by the eye offset.
            Vector3 headPos = InputTracking.GetLocalPosition(XRNode.Head);
            Quaternion headRot = InputTracking.GetLocalRotation(XRNode.Head);
            Vector3 eyePos = InputTracking.GetLocalPosition(i == 0 ? XRNode.LeftEye : XRNode.RightEye);
            Vector3 offset = Quaternion.Inverse(headRot) * (eyePos - headPos);
            if (Time.realtimeSinceStartup >= _nextCornerLog[i])
            {
                _nextCornerLog[i] = Time.realtimeSinceStartup + 10f;
                Plugin.Log.LogInfo(string.Format("[present{0}] corners {1} {2} {3} {4} offset={5} camLocal={6}/{7} camWorld={8} fov={9:0.0} aspect={10:0.00}",
                    i, _corners[0].ToString("F2"), _corners[1].ToString("F2"), _corners[2].ToString("F2"), _corners[3].ToString("F2"),
                    offset.ToString("F3"), cam.transform.localPosition.ToString("F2"), cam.transform.localEulerAngles.ToString("F0"),
                    cam.transform.position.ToString("F2"), cam.fieldOfView, cam.aspect));
            }
            for (int k = 0; k < 4; k++) _corners[k] += offset;
            _presentMeshes[i].vertices = _corners;
        }

        // ------------------------------------------------------------------ per frame

        private void Update()
        {
            if (Plugin.VRActive && !XRSettings.enabled)
            {
                XRSettings.enabled = true;
            }
            if (Plugin.VRActive)
            {
                // Same as the SteamVR Unity plugin / VRGIN (VookaRaylee) do every frame. The game turns desktop vsync on
                // (GraphicsSettings: vSyncCount = 1), which ties frame presentation to the monitor and breaks the
                // timing SteamVR uses to match frames to head poses. And the game must keep running without window focus.
                if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
                if (Application.targetFrameRate != -1) Application.targetFrameRate = -1;
                if (!Application.runInBackground) Application.runInBackground = true;
            }

            if (_recenterAt > 0f && Time.realtimeSinceStartup >= _recenterAt)
            {
                _recenterAt = -1f;
                Recenter();
            }
            HandleHotkeys();
            _input.Update(Recenter);

            if (_nativeMode) return;

            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + 0.5f;
                ScanCanvases();
                if (_direct == null) EnsureEyeTextures();
                MatchUnityEyeBuffer();
            }
        }

        private void LateUpdate()
        {
            _smoothedFrameTime = Mathf.Lerp(_smoothedFrameTime, Time.unscaledDeltaTime, 0.05f);
            if (_nativeMode) return;

            AcquireGameCameras();
            SyncCameraSettings();
            UpdateFade();
            UpdatePanelPlacement();
            AutoRecenterOnPresence();
        }

        private void OnBeforeRender()
        {
            // Runs after every LateUpdate (so after Cinemachine has moved the game camera) and right before rendering.
            if (_nativeMode) return;
            // Must run every frame before anything renders: if a game camera renders even once as a stereo camera,
            // Unity overwrites its field of view with the headset's, permanently (this shrank the pause book ~4x).
            ScanCameras();
            UpdateRigPose();
            if (_direct == null) UpdateEyeCameras();
            UpdateOverlayObjects();
            LogHealth();
        }

        // Each eye camera renders from (virtual rig * tracked eye pose) with the headset's projection for that eye.
        private void UpdateEyeCameras()
        {
            if (_rig == null || _vrCam == null) return;
            Matrix4x4 rigM = _rig.transform.localToWorldMatrix;
            Matrix4x4 flipZ = Matrix4x4.Scale(new Vector3(1f, 1f, -1f));
            float scale = _rig.transform.lossyScale.x;

            for (int i = 0; i < 2; i++)
            {
                Camera c = _eyeCams[i];
                XRNode node = i == 0 ? XRNode.LeftEye : XRNode.RightEye;
                Vector3 p = InputTracking.GetLocalPosition(node);
                Quaternion q = InputTracking.GetLocalRotation(node);
                Matrix4x4 eyeWorld = rigM * Matrix4x4.TRS(p, q, Vector3.one);

                // Transform = eye pose in the world (used for LOD, culling helpers, _WorldSpaceCameraPos).
                c.transform.position = eyeWorld.MultiplyPoint3x4(Vector3.zero);
                c.transform.rotation = _rig.transform.rotation * q;
                c.worldToCameraMatrix = flipZ * eyeWorld.inverse;

                // Headset projection for this eye, with this camera's near/far (scaled with the rig).
                Matrix4x4 proj = _vrCam.GetStereoProjectionMatrix(i == 0 ? Camera.StereoscopicEye.Left : Camera.StereoscopicEye.Right);
                float n = Mathf.Max(0.01f, c.nearClipPlane) * scale, f = Mathf.Max(n + 1f, c.farClipPlane * scale);
                proj[2, 2] = -(f + n) / (f - n);
                proj[2, 3] = -2f * f * n / (f - n);
                c.projectionMatrix = proj;
            }
        }

        // Positions the overlay objects (rig-local) right before rendering; also keeps their renderers switched on.
        private void UpdateOverlayObjects()
        {
            if (_rig == null || _panelGo == null) return;

            bool fadeOn = _fadeAlpha > 0.001f;
            if (_fadeGo.activeSelf != fadeOn) _fadeGo.SetActive(fadeOn);
            if (fadeOn)
            {
                Color c = _fadeColor;
                c.a = _fadeAlpha;
                _fadeMat.color = c;
                Vector3 hp = InputTracking.GetLocalPosition(XRNode.Head);
                Quaternion hr = InputTracking.GetLocalRotation(XRNode.Head);
                _fadeGo.transform.localPosition = hp + hr * new Vector3(0f, 0f, 0.1f);
                _fadeGo.transform.localRotation = hr;
                _fadeGo.transform.localScale = Vector3.one * 2f;
                EnsureEnabled(_fadeGo);
            }

            float w = Mathf.Max(0.1f, Plugin.UiWidth.Value);
            Quaternion yaw = Quaternion.Euler(0f, _panelYaw, 0f);
            _panelGo.transform.localPosition = _panelAnchor + yaw * new Vector3(0f, Plugin.UiHeight.Value, Mathf.Max(0.2f, Plugin.UiDistance.Value));
            _panelGo.transform.localRotation = yaw;
            _panelGo.transform.localScale = new Vector3(w, w * UiTexHeight / UiTexWidth, 1f);
            EnsureEnabled(_panelGo);

            _input.UpdateControllerObjects();
        }

        // The HUD/menu panel is anchored at the head (in rig space) and lazily turns to face where you look,
        // so it can't end up behind you or at the wrong height if the view was centered while the headset was off.
        private void UpdatePanelPlacement()
        {
            Vector3 headPos = InputTracking.GetLocalPosition(XRNode.Head);
            float headYaw = InputTracking.GetLocalRotation(XRNode.Head).eulerAngles.y;
            float dt = Time.unscaledDeltaTime;
            if (!_panelInit)
            {
                _panelAnchor = headPos;
                _panelYaw = _panelTargetYaw = headYaw;
                _panelInit = true;
                return;
            }
            if (Mathf.Abs(Mathf.DeltaAngle(_panelTargetYaw, headYaw)) > Plugin.UiFollowAngle.Value) _panelTargetYaw = headYaw;
            _panelYaw = Mathf.LerpAngle(_panelYaw, _panelTargetYaw, Sharp(4f, dt));
            _panelAnchor = Vector3.Lerp(_panelAnchor, headPos, Sharp(2f, dt));
        }

        // Recenter when the headset is put on, so the view lines up with the game camera from where you are.
        private int _lastActivity = -1;
        private float _nextActivityCheck;

        private void AutoRecenterOnPresence()
        {
            // Unity doesn't report presence for the Beyond; ask SteamVR whether the headset is being worn.
            if (Time.realtimeSinceStartup >= _nextActivityCheck)
            {
                _nextActivityCheck = Time.realtimeSinceStartup + 0.5f;
                try
                {
                    var sys = Valve.VR.OpenVR.System;
                    if (sys != null)
                    {
                        int act = (int)sys.GetTrackedDeviceActivityLevel(0);
                        bool worn = act == (int)Valve.VR.EDeviceActivityLevel.k_EDeviceActivityLevel_UserInteraction;
                        bool wasWorn = _lastActivity == (int)Valve.VR.EDeviceActivityLevel.k_EDeviceActivityLevel_UserInteraction;
                        if (worn && !wasWorn && _lastActivity != -1)
                        {
                            Plugin.Log.LogInfo("Headset put on (SteamVR); recentering.");
                            Recenter();
                        }
                        _lastActivity = act;
                    }
                }
                catch (Exception) { }
            }
            UserPresenceState p = XRDevice.userPresence;
            if (p == UserPresenceState.Present && _lastPresence != UserPresenceState.Present && _lastPresence != UserPresenceState.Unknown)
            {
                Plugin.Log.LogInfo("Headset put on; recentering.");
                Recenter();
            }
            _lastPresence = p;
        }

        private void AcquireGameCameras()
        {
            Camera found = null;
            var cm = CameraManager.Instance;
            if (cm != null && cm.GetCameraGameObject() != null)
            {
                found = cm.GetCameraGameObject().GetComponent<Camera>();
            }
            if (found == null)
            {
                var main = Camera.main;
                if (main != null && !IsOurs(main)) found = main;
            }
            if (found == null)
            {
                // No CameraManager / MainCamera (e.g. title screen): follow the highest-depth camera that draws the world.
                foreach (var c in Camera.allCameras)
                {
                    if (c == null || IsOurs(c) || _uiCams.Contains(c) || c.targetTexture != null) continue;
                    if ((c.cullingMask & 1) == 0) continue;
                    if (found == null || c.depth > found.depth) found = c;
                }
            }
            if (found != _flatCam)
            {
                // New level / camera: recenter so "forward" is wherever you're looking now.
                if (found != null && _flatCam != null) Recenter();
                _flatCam = found;
                _rigPoseValid = false;
                if (_flatCam != null)
                {
                    Plugin.Log.LogInfo("Game camera: " + _flatCam.name);
                    var sky = _flatCam.GetComponent<Skybox>();
                    foreach (var ec in _eyeCams)
                    {
                        var mySky = ec.GetComponent<Skybox>();
                        if (sky != null)
                        {
                            if (mySky == null) mySky = ec.gameObject.AddComponent<Skybox>();
                            mySky.material = sky.material;
                        }
                        else if (mySky != null)
                        {
                            Destroy(mySky);
                        }
                    }
                }
            }

            var hud = FlatscreenHUDCamera.Instance;
            _hudCam = hud != null ? hud.Camera : null;
            if (_hudCam != null) RegisterUiCam(_hudCam);
        }

        private void RegisterUiCam(Camera c)
        {
            if (c == null || _uiCams.Contains(c)) return;
            _uiCams.Add(c);
            Plugin.Log.LogInfo("UI camera '" + c.name + "' -> VR panel");
        }

        private void SyncCameraSettings()
        {
            foreach (var ec in _eyeCams)
            {
                if (_flatCam != null)
                {
                    _flatCam.stereoTargetEye = StereoTargetEyeMask.None;
                    ec.cullingMask = _flatCam.cullingMask | 1;   // Default layer holds our overlay objects
                    // Always clear: some game cameras clear only depth (another camera clears colour first), which
                    // in our eye textures left every previous frame smeared underneath.
                    if (_flatCam.clearFlags == CameraClearFlags.Skybox && RenderSettings.skybox != null)
                    {
                        ec.clearFlags = CameraClearFlags.Skybox;
                    }
                    else
                    {
                        ec.clearFlags = CameraClearFlags.SolidColor;
                    }
                    Color bg = _flatCam.backgroundColor;
                    bg.a = 1f;
                    // Pure black backgrounds (title screen) become a faint dark blue, so "game showing but empty" is
                    // distinguishable from "nothing displayed" in the headset.
                    if (bg.r + bg.g + bg.b < 0.03f) bg = new Color(0.04f, 0.05f, 0.08f, 1f);
                    ec.backgroundColor = bg;
                    ec.nearClipPlane = Mathf.Min(_flatCam.nearClipPlane, 0.3f);
                    ec.farClipPlane = _flatCam.farClipPlane;
                    ec.useOcclusionCulling = _flatCam.useOcclusionCulling;
                    ec.depthTextureMode = _flatCam.depthTextureMode;
                    ec.layerCullDistances = _flatCam.layerCullDistances;
                    ec.layerCullSpherical = _flatCam.layerCullSpherical;
                    if (_direct == null) ec.renderingPath = _flatCam.renderingPath;
                }
                else
                {
                    // Nothing to follow (menus): dark void with just the floating panel.
                    ec.clearFlags = CameraClearFlags.SolidColor;
                    ec.backgroundColor = new Color(0.04f, 0.05f, 0.08f, 1f);
                    ec.cullingMask = 1;
                }
                if (Plugin.TestPattern.Value)
                {
                    ec.clearFlags = CameraClearFlags.SolidColor;
                    ec.backgroundColor = Color.magenta;
                    ec.cullingMask = 1;
                }
            }

            for (int i = _uiCams.Count - 1; i >= 0; i--)
            {
                Camera c = _uiCams[i];
                if (c == null) { _uiCams.RemoveAt(i); continue; }
                if (c.targetTexture != _uiTex) c.targetTexture = _uiTex;
                c.stereoTargetEye = StereoTargetEyeMask.None;
                if (c.clearFlags != CameraClearFlags.Depth && c.clearFlags != CameraClearFlags.Nothing)
                {
                    c.clearFlags = CameraClearFlags.Depth;
                }
                c.depth = -99f;
            }
        }

        private static float Sharp(float k, float dt)
        {
            return k <= 0f ? 1f : 1f - Mathf.Exp(-k * dt);
        }

        private void UpdateRigPose()
        {
            if (_rig == null || _flatCam == null) return;

            Transform src = _flatCam.transform;
            float dt = Time.unscaledDeltaTime;
            float targetYaw = src.eulerAngles.y;
            Vector3 offset = new Vector3(0f, Plugin.OffsetUp.Value, -Plugin.OffsetBack.Value);
            Vector3 targetPos = src.position + Quaternion.Euler(0f, targetYaw, 0f) * offset;

            bool cut = !_rigPoseValid || (targetPos - _rigPos).sqrMagnitude > CutDistance * CutDistance;
            if (cut)
            {
                _rigPos = targetPos;
                if (!_rigPoseValid || Plugin.YawMode.Value != YawFollowMode.Fixed) _rigYaw = targetYaw;
                _rigPoseValid = true;
            }
            else
            {
                _rigPos = Vector3.Lerp(_rigPos, targetPos, Sharp(Plugin.PositionSharpness.Value, dt));
                switch (Plugin.YawMode.Value)
                {
                    case YawFollowMode.Smooth:
                        _rigYaw = Mathf.LerpAngle(_rigYaw, targetYaw, Sharp(Plugin.YawSharpness.Value, dt));
                        break;
                    case YawFollowMode.Snap:
                        float step = Mathf.Max(5f, Plugin.SnapAngle.Value);
                        float delta = Mathf.DeltaAngle(_rigYaw, targetYaw);
                        if (Mathf.Abs(delta) >= step) _rigYaw += step * Mathf.Round(delta / step);
                        break;
                    case YawFollowMode.Fixed:
                        break;
                }
            }

            float pitch = Plugin.KeepGamePitch.Value ? src.eulerAngles.x : 0f;
            float scale = Mathf.Clamp(Plugin.WorldScale.Value, 0.05f, 50f);
            // Put the recentered head pose (not the tracking origin) at the game camera, facing its direction.
            Quaternion rot = Quaternion.Euler(0f, _rigYaw, 0f) * Quaternion.Euler(pitch, 0f, 0f) * Quaternion.Euler(0f, -_centerYaw, 0f);
            _rig.transform.rotation = rot;
            _rig.transform.localScale = Vector3.one * scale;
            _rig.transform.position = _rigPos - rot * (_centerPos * scale);
        }

        private void UpdateFade()
        {
            float alpha = 0f;
            Color color = Color.black;
            if (Plugin.HeadLockedFade.Value)
            {
                try
                {
                    var cm = CameraManager.Instance;
                    CameraFadeController fc = cm != null ? cm.GetTopLevelFadeController() : null;
                    if (fc != null)
                    {
                        alpha = Mathf.Clamp01(Mathf.Max(fc.CurrentFadeIntensity, fc.CurrentMinFade));
                        color = fc.FadeColor;
                    }
                }
                catch (Exception)
                {
                    alpha = 0f;
                }
            }
            _fadeAlpha = alpha;
            _fadeColor = color;
        }

        // ------------------------------------------------------------------ scans

        private bool IsOurs(Camera c)
        {
            return c == _presenters[0] || c == _presenters[1] || c == _presentClear || c == _eyeCams[0] || c == _eyeCams[1] || c == _uiClearCam || c == _uiCanvasCam;
        }

        // Cameras that only draw UI layers (no Default layer) are captured into the UI texture; any other camera
        // that would render straight into the headset (e.g. the "player on top" death camera) is demoted to desktop-only.
        private Camera[] _camBuf = new Camera[32];

        private void ScanCameras()
        {
            int n = Camera.allCamerasCount;
            if (n > _camBuf.Length) _camBuf = new Camera[n * 2];
            n = Camera.GetAllCameras(_camBuf);
            for (int i = 0; i < n; i++)
            {
                Camera c = _camBuf[i];
                _camBuf[i] = null;
                if (c == null || IsOurs(c) || _uiCams.Contains(c)) continue;
                if (c == _flatCam)
                {
                    if (c.stereoTargetEye != StereoTargetEyeMask.None) c.stereoTargetEye = StereoTargetEyeMask.None;
                    continue;
                }
                if (c.targetTexture != null) continue;
                if (c.cullingMask != 0 && (c.cullingMask & 1) == 0 && c != DeathCamera())
                {
                    RegisterUiCam(c);
                    continue;
                }
                if (c.stereoTargetEye != StereoTargetEyeMask.None)
                {
                    c.stereoTargetEye = StereoTargetEyeMask.None;
                    if (_demotedCameras.Add(c.GetInstanceID()))
                    {
                        Plugin.Log.LogInfo("Camera '" + c.name + "' set to desktop-only.");
                    }
                }
            }
        }

        // CameraManager's "show the player on top while dying" camera: renders the Player layer only, from the
        // flat camera's viewpoint. It must not be mistaken for a UI camera.
        private static readonly System.Reflection.FieldInfo DeathCamField =
            HarmonyLib.AccessTools.Field(typeof(CameraManager), "_deathCamera");

        internal static Camera DeathCamera()
        {
            var cm = CameraManager.Instance;
            if (cm == null || DeathCamField == null) return null;
            return DeathCamField.GetValue(cm) as Camera;
        }

        // Screen-space menus are re-pointed at our UI capture camera so they end up on the floating panel.
        private void ScanCanvases()
        {
            var canvases = FindObjectsOfType<Canvas>();
            for (int i = 0; i < canvases.Length; i++)
            {
                Canvas c = canvases[i];
                if (c == null || !c.isRootCanvas) continue;
                if (c.renderMode == RenderMode.WorldSpace) continue;

                if (c.renderMode == RenderMode.ScreenSpaceOverlay)
                {
                    c.renderMode = RenderMode.ScreenSpaceCamera;
                    c.worldCamera = _uiCanvasCam;
                    c.planeDistance = 1f;
                    if (_convertedCanvases.Add(c.GetInstanceID()))
                    {
                        Plugin.Log.LogInfo("Canvas '" + c.name + "' (overlay) -> VR panel");
                    }
                    continue;
                }

                // ScreenSpaceCamera
                Camera wc = c.worldCamera;
                if (wc == _uiCanvasCam) continue;
                if (wc != null && _uiCams.Contains(wc)) continue;        // already captured via its UI camera
                if (wc != null && wc.targetTexture != null && wc.targetTexture != _uiTex) continue; // renders to some other texture on purpose
                if (wc != null && wc != _flatCam && !IsOurs(wc) && (wc.cullingMask & 1) == 0)
                {
                    // A dedicated UI camera (doesn't draw the Default layer): capture the camera itself, so the game
                    // can still hide this UI by switching the camera off.
                    RegisterUiCam(wc);
                    continue;
                }
                c.worldCamera = _uiCanvasCam;
                c.planeDistance = Mathf.Clamp(c.planeDistance, 0.1f, 40f);
                if (_convertedCanvases.Add(c.GetInstanceID()))
                {
                    Plugin.Log.LogInfo("Canvas '" + c.name + "' (camera: " + (wc != null ? wc.name : "none") + ") -> VR panel");
                }
            }
        }

        // ------------------------------------------------------------------ desktop view + health

        private void LogHealth()
        {
            if (Time.realtimeSinceStartup < _nextHealthLog || _rig == null) return;
            _nextHealthLog = Time.realtimeSinceStartup + 10f;
            Plugin.Log.LogInfo(string.Format("[health] per 10s: eyeCamRenders={0} presenterEyes={1} (L={2} R={3}) rigPos={4} fps={5:0}",
                _eyeRenders, _presentRenders, _presentLeft, _presentRight, _rig.transform.position.ToString("F2"),
                1f / Mathf.Max(0.0001f, _smoothedFrameTime)));
            _eyeRenders = _presentRenders = _presentLeft = _presentRight = 0;
            if (_presenters[0] != null) Plugin.Log.LogInfo(string.Format("[health] head={0}/{1} presenterL={2}/{3} presenterR={4}/{5} clear={6}/{7}",
                InputTracking.GetLocalPosition(XRNode.Head).ToString("F3"), InputTracking.GetLocalRotation(XRNode.Head).eulerAngles.ToString("F0"),
                _presenters[0].transform.localPosition.ToString("F3"), _presenters[0].transform.localEulerAngles.ToString("F0"),
                _presenters[1].transform.localPosition.ToString("F3"), _presenters[1].transform.localEulerAngles.ToString("F0"),
                _presentClear.transform.localPosition.ToString("F3"), _presentClear.transform.localEulerAngles.ToString("F0")));
        }

        private void OnAnyPreCull(Camera cam)
        {
            if (cam == _eyeCams[0] || cam == _eyeCams[1]) _eyeRenders++;
            if (cam == _flatCam && cam != null && Plugin.DesktopView.Value == DesktopViewMode.Mirror && !_flatMaskHidden)
            {
                _flatSavedMask = cam.cullingMask;
                cam.cullingMask = 0;
                _flatMaskHidden = true;
            }
        }

        private void OnAnyPostRender(Camera cam)
        {
            if (cam == _flatCam && _flatMaskHidden)
            {
                if (cam != null) cam.cullingMask = _flatSavedMask;
                _flatMaskHidden = false;
            }
        }

        private void OnGUI()
        {
            if (_nativeMode || _uiTex == null) return;
            if (Plugin.DesktopView.Value == DesktopViewMode.Spectator && Event.current.type == EventType.Repaint)
            {
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _uiTex, ScaleMode.ScaleToFit, true);
            }
        }

        // ------------------------------------------------------------------ input

        private void Recenter()
        {
            _centerPos = InputTracking.GetLocalPosition(XRNode.Head);
            _centerYaw = InputTracking.GetLocalRotation(XRNode.Head).eulerAngles.y;
            _rigPoseValid = false;
            _panelInit = false;
            Plugin.Log.LogInfo("Recentered.");
        }

        private void HandleHotkeys()
        {
            if (!_nativeMode && Input.GetKeyDown(Plugin.KeySnapshot.Value)) TakeSnapshot();

            bool padRecenter = Input.GetKey(KeyCode.JoystickButton6) && Input.GetKeyDown(KeyCode.JoystickButton4);
            if (Input.GetKeyDown(Plugin.KeyRecenter.Value) || padRecenter)
            {
                Recenter();
            }
            if (Input.GetKeyDown(Plugin.KeyScaleUp.Value))
            {
                Plugin.WorldScale.Value = Mathf.Min(50f, Plugin.WorldScale.Value * 1.15f);
                Plugin.Log.LogInfo("WorldScale = " + Plugin.WorldScale.Value.ToString("0.00"));
            }
            if (Input.GetKeyDown(Plugin.KeyScaleDown.Value))
            {
                Plugin.WorldScale.Value = Mathf.Max(0.05f, Plugin.WorldScale.Value / 1.15f);
                Plugin.Log.LogInfo("WorldScale = " + Plugin.WorldScale.Value.ToString("0.00"));
            }
            if (Input.GetKeyDown(Plugin.KeyFarther.Value))
            {
                Plugin.OffsetBack.Value = Plugin.OffsetBack.Value + 0.5f;
                Plugin.Log.LogInfo("OffsetBack = " + Plugin.OffsetBack.Value.ToString("0.0"));
            }
            if (Input.GetKeyDown(Plugin.KeyCloser.Value))
            {
                Plugin.OffsetBack.Value = Plugin.OffsetBack.Value - 0.5f;
                Plugin.Log.LogInfo("OffsetBack = " + Plugin.OffsetBack.Value.ToString("0.0"));
            }
            if (Input.GetKeyDown(Plugin.KeyYawMode.Value))
            {
                Plugin.YawMode.Value = (YawFollowMode)(((int)Plugin.YawMode.Value + 1) % 3);
                Plugin.Log.LogInfo("YawMode = " + Plugin.YawMode.Value);
            }
        }

        // ------------------------------------------------------------------ debug snapshots

        private int _snapshotIndex;

        // Saves the left eye texture and the UI texture to BepInEx\nsltvr_snapshots.
        private void TakeSnapshot()
        {
            try
            {
                string dir = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "nsltvr_snapshots");
                System.IO.Directory.CreateDirectory(dir);
                string stamp = (_snapshotIndex++).ToString("000");
                Plugin.Log.LogInfo(string.Format(
                    "[snapshot {0}] scene='{1}' presence={2} flatCam={3} rigPos={4} uiCams={5} fade={6:0.00} eyeTex={7}x{8} fps={9:0}",
                    stamp, UnityEngine.SceneManagement.SceneManager.GetActiveScene().name, XRDevice.userPresence,
                    _flatCam != null ? _flatCam.name : "null", _rig != null ? _rig.transform.position.ToString() : "-",
                    _uiCams.Count, _fadeAlpha, XRSettings.eyeTextureWidth, XRSettings.eyeTextureHeight,
                    1f / Mathf.Max(0.0001f, _smoothedFrameTime)));
                // Headset view first: the full-size readbacks below stall the game briefly, and SteamVR shows its
                // waiting environment during a stall.
                _input.TakeHeadsetScreenshot("F12");
                if (_eyeTex[0] != null) SaveRT(_eyeTex[0], System.IO.Path.Combine(dir, stamp + "_lefteye.png"), true);
                if (_uiTex != null) SaveRT(_uiTex, System.IO.Path.Combine(dir, stamp + "_ui.png"), false);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Snapshot failed: " + e);
            }
        }

        internal static void SaveRT(RenderTexture rt, string path, bool opaque)
        {
            RenderTexture src = rt;
            RenderTexture tmp = null;
            if (rt.antiAliasing > 1)
            {
                tmp = RenderTexture.GetTemporary(rt.width, rt.height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(rt, tmp);
                src = tmp;
            }
            var prev = RenderTexture.active;
            RenderTexture.active = src;
            var tex = new Texture2D(src.width, src.height, TextureFormat.ARGB32, false);
            tex.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
            if (opaque)
            {
                var px = tex.GetPixels32();
                for (int i = 0; i < px.Length; i++) px[i].a = 255;
                tex.SetPixels32(px);
            }
            tex.Apply();
            RenderTexture.active = prev;
            if (tmp != null) RenderTexture.ReleaseTemporary(tmp);
            System.IO.File.WriteAllBytes(path, ImageConversion.EncodeToPNG(tex));
            Destroy(tex);
        }
    }

    // Hook on the presenter (VR) camera: picks the eye texture and quad shape for each eye as it renders.
    internal class PresenterHook : MonoBehaviour
    {
        internal VRController Owner;
        internal int EyeIndex;
        private Camera _cam;

        private void OnPreRender()
        {
            if (_cam == null) _cam = GetComponent<Camera>();
            if (Owner != null) Owner.OnPresenterPreRender(_cam, EyeIndex);
        }
    }
}
