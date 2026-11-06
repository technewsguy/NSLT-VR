// Per-eye copy of the opaque scene, like the game's PostProcessOpaque does for the desktop camera.
// The game's water/glass shaders refract _PlayfulGrabTexture / _BlurredScene / _DownsampleScene and read
// _PlayfulDepthTexture. Only the desktop camera produced them, so in VR the water showed a ghost of the desktop
// picture (Lucky included), placed differently in each eye. Each VR eye now makes its own after its opaque pass;
// OwnVR puts the desktop camera's textures back afterwards.
using UnityEngine;

namespace NSLTVR
{
    internal class OwnOpaqueGrab : MonoBehaviour
    {
        private RenderTexture _grab, _small;
        internal static int Logs;

        [ImageEffectOpaque]
        private void OnRenderImage(RenderTexture src, RenderTexture dst)
        {
            if (_grab == null || _grab.width != src.width || _grab.height != src.height)
            {
                if (_grab != null) { _grab.Release(); Destroy(_grab); }
                _grab = new RenderTexture(src.width, src.height, 0, src.format);
                _grab.name = "NSLTVR_EyeGrab";
            }
            int sw = Mathf.Max(16, src.width / 4), sh = Mathf.Max(16, src.height / 4);
            if (_small == null || _small.width != sw || _small.height != sh)
            {
                if (_small != null) { _small.Release(); Destroy(_small); }
                _small = new RenderTexture(sw, sh, 0, src.format);
                _small.name = "NSLTVR_EyeGrabSmall";
            }
            Graphics.Blit(src, _grab);
            Graphics.Blit(src, _small);   // quarter size, bilinear: stands in for the game's blurred/downsampled copies
            Shader.SetGlobalTexture("_PlayfulGrabTexture", _grab);
            Shader.SetGlobalTexture("_DownsampleScene", _small);
            Shader.SetGlobalTexture("_BlurredScene", _small);
            var depth = Shader.GetGlobalTexture("_CameraDepthTexture");
            if (depth != null) Shader.SetGlobalTexture("_PlayfulDepthTexture", depth);
            if (Logs < 2 && depth != null)
            {
                Logs++;
                Plugin.Log.LogInfo("OWNVR eye opaque copy " + src.width + "x" + src.height + ", depth texture during eye render: " +
                    (depth != null ? depth.name + " " + depth.width + "x" + depth.height : "none"));
            }
            Graphics.Blit(src, dst);
        }

        private void OnDestroy()
        {
            if (_grab != null) _grab.Release();
            if (_small != null) _small.Release();
        }
    }
}
