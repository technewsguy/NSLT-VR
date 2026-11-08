// Faint "ghost" motion controllers, like the original Lucky's Tale: SteamVR's own 3D model of each controller,
// drawn additively with a dim tint so it is barely visible over the game and never hides anything.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using Valve.VR;

namespace NSLTVR
{
    internal class OwnHands
    {
        private class HandVis
        {
            public ETrackedControllerRole Role;
            public uint Index = OpenVR.k_unTrackedDeviceIndexInvalid;
            public string ModelName;
            public List<string> Pending;
            public List<CombineInstance> Parts;
            public GameObject Go;
            public bool Failed;
            public GameObject Guide;
            public Dictionary<string, string> PartNames = new Dictionary<string, string>();       // render model name -> component name
            public Dictionary<string, Vector3> PartCenters = new Dictionary<string, Vector3>();   // component name -> centre (controller space)
        }

        private readonly HandVis[] _hands =
        {
            new HandVis { Role = ETrackedControllerRole.LeftHand },
            new HandVis { Role = ETrackedControllerRole.RightHand },
        };
        private readonly Transform _parent;
        private Material _mat;

        public OwnHands(Transform parent)
        {
            _parent = parent;
            Shader s = null;
            foreach (var n in new[] { "Legacy Shaders/Particles/Additive", "Particles/Additive", "Mobile/Particles/Additive", "UI/Default" })
            {
                s = Shader.Find(n);
                if (s != null) break;
            }
            Plugin.Log.LogInfo("OWNVR controller shader: " + (s != null ? s.name : "<none>"));
            if (s == null) return;
            _mat = new Material(s);
            _mat.renderQueue = 4002;   // after the menu panel, so a hand in front of it still shows
            ApplyTint();
        }

        private float _appliedVis = -1f;
        private bool _lastGuide;

        private void ApplyTint() { ApplyTint(Plugin.ControllerVisibility.Value); }

        private void ApplyTint(float visibility)
        {
            _appliedVis = visibility;
            float v = Mathf.Clamp01(visibility);
            var c = new Color(0.55f * v, 0.75f * v, 1f * v, 1f);
            if (_mat.HasProperty("_TintColor")) _mat.SetColor("_TintColor", c * 0.5f);   // particle shaders double the tint
            _mat.color = c;
        }

        // ---- Controls guide (like the original Lucky's Tale): labels next to each button, shown on the title screen.
        private class Label { public string Part; public Vector3 Offset; public string Text; }

        private static readonly Label[] LeftLabels =
        {
            new Label { Part = "button_b",   Offset = new Vector3( 0.075f,  0.075f, 0f), Text = "B  =  Interact (Y)" },
            new Label { Part = "button_a",   Offset = new Vector3( 0.075f,  0.035f, 0f), Text = "A  =  Tail swipe (X)" },
            new Label { Part = "thumbstick", Offset = new Vector3(-0.075f,  0.065f, 0f), Text = "Stick  =  Move\nClick  =  Center camera" },
            new Label { Part = "trigger",    Offset = new Vector3(-0.075f, -0.035f, 0f), Text = "Trigger  =  Tail swipe (X)" },
        };
        private static readonly Label[] RightLabels =
        {
            new Label { Part = "button_b",   Offset = new Vector3(-0.075f,  0.075f, 0f), Text = "B  =  Back (B)" },
            new Label { Part = "button_a",   Offset = new Vector3(-0.075f,  0.035f, 0f), Text = "A  =  Jump / Select" },
            new Label { Part = "thumbstick", Offset = new Vector3( 0.075f,  0.065f, 0f), Text = "Stick  =  Camera\nClick  =  Pause" },
            new Label { Part = "trigger",    Offset = new Vector3( 0.075f, -0.035f, 0f), Text = "Trigger  =  Burrow" },
        };

        private Font _font;
        private Material _textMat, _shadowMat, _lineMat;

        private void BuildGuide(HandVis h)
        {
            if (_font == null)
            {
                _font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                if (_font == null) return;
                _textMat = new Material(_font.material);
                _textMat.SetFloat("unity_GUIZTestMode", (float)UnityEngine.Rendering.CompareFunction.Always);
                _textMat.renderQueue = 4004;
                _shadowMat = new Material(_textMat);
                _shadowMat.renderQueue = 4003;
                _lineMat = new Material(_mat.shader);
                _lineMat.renderQueue = 4003;
                if (_lineMat.HasProperty("_TintColor")) _lineMat.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
            }
            bool left = h.Role == ETrackedControllerRole.LeftHand;
            var guide = new GameObject("NSLTVR_Guide");
            guide.layer = OwnVR.OverlayLayer;
            guide.transform.SetParent(h.Go.transform, false);
            h.Guide = guide;
            foreach (var l in left ? LeftLabels : RightLabels)
            {
                Vector3 at;
                if (!h.PartCenters.TryGetValue(l.Part, out at)) continue;   // not an Index controller part: skip
                Vector3 pos = at + l.Offset;

                var t = new GameObject("NSLTVR_Label_" + l.Part);
                t.layer = OwnVR.OverlayLayer;
                t.transform.SetParent(guide.transform, false);
                t.transform.localPosition = pos;
                var tm = t.AddComponent<TextMesh>();
                tm.font = _font;
                tm.text = l.Text;
                tm.fontSize = 64;
                tm.characterSize = 0.0022f;
                tm.anchor = l.Offset.x > 0f ? TextAnchor.MiddleLeft : TextAnchor.MiddleRight;
                tm.alignment = l.Offset.x > 0f ? TextAlignment.Left : TextAlignment.Right;
                tm.color = new Color(1f, 0.95f, 0.75f, 1f);
                var mr = t.GetComponent<MeshRenderer>();
                mr.sharedMaterial = _textMat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

                // Dark drop shadow (drawn first) so the labels stay readable over the bright title screen.
                var s = new GameObject("NSLTVR_LabelShadow");
                s.layer = OwnVR.OverlayLayer;
                s.transform.SetParent(t.transform, false);
                s.transform.localPosition = new Vector3(0.0009f, -0.0009f, 0.0005f);
                var sm = s.AddComponent<TextMesh>();
                sm.font = _font; sm.text = tm.text; sm.fontSize = tm.fontSize; sm.characterSize = tm.characterSize;
                sm.anchor = tm.anchor; sm.alignment = tm.alignment;
                sm.color = new Color(0f, 0f, 0f, 0.85f);
                var smr = s.GetComponent<MeshRenderer>();
                smr.sharedMaterial = _shadowMat;
                smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

                var lineGo = new GameObject("NSLTVR_Line_" + l.Part);
                lineGo.layer = OwnVR.OverlayLayer;
                lineGo.transform.SetParent(guide.transform, false);
                var lr = lineGo.AddComponent<LineRenderer>();
                lr.useWorldSpace = false;
                lr.positionCount = 2;
                lr.SetPosition(0, at);
                lr.SetPosition(1, pos - new Vector3(Mathf.Sign(l.Offset.x) * 0.004f, 0f, 0f));
                lr.startWidth = lr.endWidth = 0.0012f;
                lr.sharedMaterial = _lineMat;
                lr.startColor = lr.endColor = Color.white;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;
            }
        }

        // Is the game's pause menu (the book) open? Checked a few times a second.
        private static bool s_pauseOpen;
        private static float s_nextPauseCheck;

        private static bool PauseMenuOpen()
        {
            if (Time.unscaledTime >= s_nextPauseCheck)
            {
                s_nextPauseCheck = Time.unscaledTime + 0.25f;
                var pm = UnityEngine.Object.FindObjectOfType<Playful.SuperLucky.UI.PauseScreen.UIPauseMenuController>();
                s_pauseOpen = pm != null && pm.isActiveAndEnabled;
            }
            return s_pauseOpen;
        }

        private static bool GuideWanted()
        {
            switch (Plugin.ControlsGuide.Value)
            {
                case ControlsGuideMode.Never: return false;
                case ControlsGuideMode.Always: return true;
                case ControlsGuideMode.TitleScreen:
                    return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "TitleScreen";
                default:   // TitleScreenAndPause
                    return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "TitleScreen" || PauseMenuOpen();
            }
        }

        public void Update(TrackedDevicePose_t[] poses, Vector3 headWorld)
        {
            if (_mat == null || OpenVR.System == null) return;
            bool guide = Plugin.EnableControllers.Value && GuideWanted();
            if (guide != _lastGuide) { _lastGuide = guide; Plugin.Log.LogInfo("OWNVR controls guide " + (guide ? "shown" : "hidden")); }
            bool show = Plugin.EnableControllers.Value && (Plugin.ShowControllers.Value || guide);
            // Brighter controllers while the guide is up, so you can see what is being labelled.
            float vis = guide ? Mathf.Max(0.8f, Plugin.ControllerVisibility.Value) : Plugin.ControllerVisibility.Value;
            if (!Mathf.Approximately(vis, _appliedVis)) ApplyTint(vis);
            foreach (var h in _hands)
            {
                uint idx = OpenVR.System.GetTrackedDeviceIndexForControllerRole(h.Role);
                if (idx != h.Index) Reset(h, idx);
                if (idx == OpenVR.k_unTrackedDeviceIndexInvalid || h.Failed) continue;
                if (h.Go == null) { Load(h); continue; }

                bool valid = show && idx < poses.Length && poses[idx].bPoseIsValid;
                if (h.Go.activeSelf != valid) h.Go.SetActive(valid);
                if (!valid) continue;
                Vector3 p; Quaternion q;
                OwnVR.ToUnity(poses[idx].mDeviceToAbsoluteTracking, out p, out q);
                h.Go.transform.localPosition = p;
                h.Go.transform.localRotation = q;
                var r = h.Go.GetComponent<Renderer>();
                if (!r.enabled) r.enabled = true;   // the game's LOD system switches renderers off

                if (guide && h.Guide == null) BuildGuide(h);
                if (h.Guide != null)
                {
                    if (h.Guide.activeSelf != guide) h.Guide.SetActive(guide);
                    if (guide)
                        foreach (Transform child in h.Guide.transform)
                        {
                            var tm = child.GetComponent<TextMesh>();
                            if (tm == null) continue;
                            // face the viewer (TextMesh reads along +x with +z pointing away from the reader)
                            child.rotation = Quaternion.LookRotation(child.position - headWorld, Vector3.up);
                            var mr = child.GetComponent<Renderer>();
                            if (!mr.enabled) mr.enabled = true;
                        }
                }
            }
        }

        private void Reset(HandVis h, uint idx)
        {
            if (h.Go != null) UnityEngine.Object.Destroy(h.Go);
            h.Go = null;
            h.Index = idx;
            h.ModelName = null;
            h.Pending = null;
            h.Parts = null;
            h.Failed = false;
        }

        // SteamVR loads models asynchronously: keep asking each frame until every part is ready.
        private void Load(HandVis h)
        {
            var rm = OpenVR.RenderModels;
            if (rm == null) return;
            if (h.ModelName == null)
            {
                var err = ETrackedPropertyError.TrackedProp_Success;
                var sb = new StringBuilder(512);
                OpenVR.System.GetStringTrackedDeviceProperty(h.Index, ETrackedDeviceProperty.Prop_RenderModelName_String, sb, 512, ref err);
                if (err != ETrackedPropertyError.TrackedProp_Success || sb.Length == 0) return;
                h.ModelName = sb.ToString();
                h.Pending = new List<string>();
                h.Parts = new List<CombineInstance>();
                uint n = rm.GetComponentCount(h.ModelName);
                for (uint i = 0; i < n; i++)
                {
                    var cn = new StringBuilder(256);
                    rm.GetComponentName(h.ModelName, i, cn, 256);
                    var crm = new StringBuilder(512);
                    if (rm.GetComponentRenderModelName(h.ModelName, cn.ToString(), crm, 512) > 0 && crm.Length > 0)
                    {
                        h.Pending.Add(crm.ToString());
                        h.PartNames[crm.ToString()] = cn.ToString();
                    }
                }
                if (h.Pending.Count == 0) h.Pending.Add(h.ModelName);   // single-piece model (e.g. Vive wand)
                Plugin.Log.LogInfo("OWNVR loading controller model '" + h.ModelName + "' (" + h.Pending.Count + " parts)");
            }

            for (int i = h.Pending.Count - 1; i >= 0; i--)
            {
                IntPtr ptr = IntPtr.Zero;
                var e = rm.LoadRenderModel_Async(h.Pending[i], ref ptr);
                if (e == EVRRenderModelError.Loading) continue;
                if (e == EVRRenderModelError.None && ptr != IntPtr.Zero)
                {
                    var mesh = ToMesh((RenderModel_t)Marshal.PtrToStructure(ptr, typeof(RenderModel_t)));
                    rm.FreeRenderModel(ptr);
                    if (mesh != null)
                    {
                        h.Parts.Add(new CombineInstance { mesh = mesh, transform = Matrix4x4.identity });
                        string part;
                        if (!h.PartNames.TryGetValue(h.Pending[i], out part)) part = h.Pending[i];
                        h.PartCenters[part] = mesh.bounds.center;
                        Plugin.Log.LogInfo(string.Format("OWNVR {0} part '{1}' center={2} size={3}", h.Role, part, mesh.bounds.center.ToString("F3"), mesh.bounds.size.ToString("F3")));
                    }
                }
                h.Pending.RemoveAt(i);
            }
            if (h.Pending.Count > 0) return;
            if (h.Parts.Count == 0) { h.Failed = true; Plugin.Log.LogWarning("OWNVR: no controller model for " + h.Role); return; }

            var combined = new Mesh();
            combined.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            combined.CombineMeshes(h.Parts.ToArray(), true, true);
            h.Go = new GameObject("NSLTVR_Hand" + h.Role);
            h.Go.layer = OwnVR.OverlayLayer;   // VR only, not the desktop window
            h.Go.transform.SetParent(_parent, false);
            h.Go.AddComponent<MeshFilter>().sharedMesh = combined;
            var mr = h.Go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            Plugin.Log.LogInfo("OWNVR controller model ready for " + h.Role + ": " + combined.vertexCount + " vertices");
        }

        // OpenVR meshes are right-handed: flip z and reverse the triangle winding for Unity.
        private static Mesh ToMesh(RenderModel_t m)
        {
            if (m.unVertexCount == 0 || m.unTriangleCount == 0) return null;
            int vs = Marshal.SizeOf(typeof(RenderModel_Vertex_t));
            var verts = new Vector3[m.unVertexCount];
            var norms = new Vector3[m.unVertexCount];
            for (int i = 0; i < verts.Length; i++)
            {
                var v = (RenderModel_Vertex_t)Marshal.PtrToStructure(new IntPtr(m.rVertexData.ToInt64() + (long)i * vs), typeof(RenderModel_Vertex_t));
                verts[i] = new Vector3(v.vPosition.v0, v.vPosition.v1, -v.vPosition.v2);
                norms[i] = new Vector3(v.vNormal.v0, v.vNormal.v1, -v.vNormal.v2);
            }
            int n = (int)m.unTriangleCount * 3;
            var raw = new short[n];
            Marshal.Copy(m.rIndexData, raw, 0, n);
            var tris = new int[n];
            for (int i = 0; i < n; i += 3)
            {
                tris[i] = (ushort)raw[i];
                tris[i + 1] = (ushort)raw[i + 2];
                tris[i + 2] = (ushort)raw[i + 1];
            }
            var mesh = new Mesh();
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = verts;
            mesh.normals = norms;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
