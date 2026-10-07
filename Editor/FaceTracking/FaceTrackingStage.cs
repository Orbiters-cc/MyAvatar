using System;
using System.Linq;
using Orbiters.Toolkit.Editor.Photoshoot;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.FaceTracking
{
    /// <summary>
    /// A close-up of the built avatar's face in its preview scene, lit like the photoshoot's "Studio Soft" light, rendered
    /// on demand into a texture. The camera turns around the face and zooms; nothing of it reaches the open scenes.
    /// </summary>
    internal sealed class FaceTrackingStage : IDisposable
    {
        internal const float MaxYaw = 75f, MaxPitch = 35f, MinZoom = .6f, MaxZoom = 3f;
        internal static readonly Color Background = new Color32(14, 16, 21, 255);

        private readonly GameObject copy;
        private readonly Transform head;
        private readonly Vector3 viewOffset;
        private readonly float eyeHeight;
        private readonly Camera camera;
        private readonly GameObject[] lights;
        private readonly (SkinnedMeshRenderer renderer, float[] applied)[] faces;
        private RenderTexture texture;

        public float Yaw, Pitch = 3f, Zoom = 1f;
        public Texture Texture => texture;

        public FaceTrackingStage(Scene scene, GameObject copy, VRCAvatarDescriptor descriptor)
        {
            this.copy = copy;
            var animator = copy.GetComponent<Animator>();
            head = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Head) : null;
            var view = descriptor != null ? descriptor.ViewPosition : new Vector3(0f, 1.6f, .1f);
            eyeHeight = Mathf.Max(.3f, view.y);
            // Where the eyes are, from the head bone: it follows the head in any pose.
            viewOffset = head != null ? head.InverseTransformPoint(copy.transform.TransformPoint(view)) : view;

            var cameraObject = Hidden("Face tracking camera", scene);
            camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.scene = scene;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Background;
            camera.fieldOfView = 24f;
            camera.nearClipPlane = .01f;
            camera.farClipPlane = 50f;
            camera.allowHDR = false;
            camera.allowMSAA = true;

            // The photoshoot's first light set, so the face looks the same as in thumbnails.
            var preset = PhotoshootService.CreateLightPresets()[0];
            lights = new[]
            {
                Light("Key light", scene, LightType.Directional, preset.keyColor, preset.keyIntensity, Quaternion.Euler(preset.keyRotation), Vector3.zero),
                Light("Fill light", scene, LightType.Point, preset.fillColor, preset.fillIntensity, Quaternion.identity, preset.fillPosition + Vector3.up * (eyeHeight - 1.35f)),
                Light("Rim light", scene, LightType.Directional, preset.rimColor, preset.rimIntensity, Quaternion.Euler(preset.rimRotation), Vector3.zero),
            };
            Ambient(copy, preset.ambientColor);
            var skinned = copy.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (var renderer in skinned)
            {
                renderer.updateWhenOffscreen = true;
                // Rendered right after the layers move the bones (the eyes), outside Unity's frame.
                renderer.forceMatrixRecalculationPerRender = true;
            }
            faces = skinned.Where(r => r.sharedMesh != null && r.sharedMesh.blendShapeCount > 0)
                .Select(r => (r, Enumerable.Repeat(float.NaN, r.sharedMesh.blendShapeCount).ToArray())).ToArray();
        }

        private static GameObject Hidden(string name, Scene scene)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            SceneManager.MoveGameObjectToScene(go, scene);
            return go;
        }

        private static GameObject Light(string name, Scene scene, LightType type, Color color, float intensity, Quaternion rotation, Vector3 position)
        {
            var go = Hidden(name, scene);
            go.transform.SetPositionAndRotation(position, rotation);
            var light = go.AddComponent<Light>();
            light.type = type; light.color = color; light.intensity = intensity; light.shadows = LightShadows.None;
            if (type == LightType.Point) light.range = 6f;
            return go;
        }

        // The ambient light reaches the copy through its own probe data: the user's scene lighting is never touched.
        private static void Ambient(GameObject avatar, Color ambient)
        {
            var probe = new SphericalHarmonicsL2();
            probe.AddAmbientLight(QualitySettings.activeColorSpace == ColorSpace.Linear ? ambient.linear : ambient);
            var probes = new[] { probe };
            var block = new MaterialPropertyBlock();
            foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true))
            {
                renderer.lightProbeUsage = LightProbeUsage.CustomProvided;
                renderer.GetPropertyBlock(block);
                block.CopySHCoefficientArraysFrom(probes);
                renderer.SetPropertyBlock(block);
            }
        }

        /// <summary>The middle of the face: between the eyes, a little toward the muzzle.</summary>
        private Vector3 Focus()
        {
            var eyes = head != null ? head.TransformPoint(viewOffset) : copy.transform.TransformPoint(viewOffset);
            var below = head != null ? head.position : eyes - Vector3.up * eyeHeight * .06f;
            return Vector3.Lerp(below, eyes, .4f) + copy.transform.forward * eyeHeight * .04f;
        }

        public void Render(int width, int height)
        {
            if (copy == null || width < 8 || height < 8) return;
            width = Mathf.Min(width, 1600); height = Mathf.Min(height, 1600);
            if (texture == null || texture.width != width || texture.height != height)
            {
                if (texture != null) { camera.targetTexture = null; Object.DestroyImmediate(texture); }
                texture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
                {
                    name = "Face tracking test", hideFlags = HideFlags.HideAndDontSave, antiAliasing = 4,
                };
                texture.Create();
            }
            // A face is about a fifth of the eye height tall: framed with room around it.
            float span = eyeHeight * .2f / Zoom;
            float distance = span * .5f / Mathf.Tan(camera.fieldOfView * .5f * Mathf.Deg2Rad);
            var focus = Focus();
            var turn = copy.transform.rotation * Quaternion.Euler(Pitch, 180f + Yaw, 0f);
            camera.transform.SetPositionAndRotation(focus - turn * Vector3.forward * distance, turn);
            camera.aspect = width / (float)height;
            camera.targetTexture = texture;
            // Weights the layers wrote reach a renderer of a preview scene only when set through it: set those that moved.
            foreach (var (renderer, applied) in faces)
            {
                if (renderer == null) continue;
                for (int i = 0; i < applied.Length; i++)
                {
                    float weight = renderer.GetBlendShapeWeight(i);
                    if (weight == applied[i]) continue;
                    renderer.SetBlendShapeWeight(i, weight);
                    applied[i] = weight;
                }
            }
            // Shaders still compiling would draw as cyan placeholders.
            bool async = ShaderUtil.allowAsyncCompilation;
            ShaderUtil.allowAsyncCompilation = false;
            try { camera.Render(); }
            finally { ShaderUtil.allowAsyncCompilation = async; }
        }

        public void Dispose()
        {
            if (camera != null) camera.targetTexture = null;
            if (texture != null) Object.DestroyImmediate(texture);
            if (camera != null) Object.DestroyImmediate(camera.gameObject);
            foreach (var light in lights) if (light != null) Object.DestroyImmediate(light);
        }
    }
}
