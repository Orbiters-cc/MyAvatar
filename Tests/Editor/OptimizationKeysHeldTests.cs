using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Orbiters.MyAvatar.Editor.Tests
{
    // The Optimization card asks for the estimate's key several times per refresh (architecture review MAC-26): within
    // KeysHeld each key is built once, outside it the key follows the avatar again.
    public sealed class OptimizationKeysHeldTests
    {
        [Test] public void KeysAreBuiltOnceWhileHeld()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var root = new GameObject("Avatar");
                SceneManager.MoveGameObjectToScene(root, scene);
                var avatar = root.AddComponent<MyAvatar>();
                var hat = new GameObject("Hat");
                hat.transform.SetParent(root.transform, false);
                string before = Optimization.OptimizationEstimate.Fingerprint(avatar);
                using (Optimization.OptimizationEstimate.KeysHeld())
                {
                    Assert.AreEqual(before, Optimization.OptimizationEstimate.Fingerprint(avatar));
                    Assert.IsNull(Optimization.OptimizationEstimate.KeysHeld(), "nested scopes share the outer one");
                    hat.SetActive(false);
                    Assert.AreEqual(before, Optimization.OptimizationEstimate.Fingerprint(avatar), "held");
                }
                Assert.AreNotEqual(before, Optimization.OptimizationEstimate.Fingerprint(avatar), "built again once released");
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }

        // Without any event (a script without Undo), the card's poll still sees what the estimate's key reads: the signature
        // moves with it, and is read at most every SignatureEvery seconds.
        [Test] public void ThePollSeesChangesMadeWithoutUndo()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var root = new GameObject("Avatar");
                SceneManager.MoveGameObjectToScene(root, scene);
                var avatar = root.AddComponent<MyAvatar>();
                var hat = new GameObject("Hat"); hat.transform.SetParent(root.transform, false);
                hat.AddComponent<MeshFilter>();
                var renderer = hat.AddComponent<MeshRenderer>();
                var material = new Material(Shader.Find("Standard"));
                renderer.sharedMaterial = material;
                var light = hat.AddComponent<Light>();
                long seen = OptimizationSection.Signature(avatar);
                Assert.AreEqual(seen, OptimizationSection.Signature(avatar), "unchanged");
                void Moved(string what) { long now = OptimizationSection.Signature(avatar); Assert.AreNotEqual(seen, now, what); seen = now; }
                renderer.enabled = false; Moved("a renderer hidden");
                renderer.sharedMaterial = new Material(Shader.Find("Standard")); Moved("a material replaced");
                hat.transform.localScale = new Vector3(2, 2, 2); Moved("scaled");
                light.enabled = false; Moved("a component disabled");
                hat.SetActive(false); Moved("an object hidden");
                var face = new GameObject("Face"); face.transform.SetParent(root.transform, false);
                var mesh = new Mesh();
                mesh.SetVertices(new System.Collections.Generic.List<Vector3> { Vector3.zero, Vector3.up, Vector3.right });
                mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
                mesh.AddBlendShapeFrame("Smile", 100, new[] { Vector3.up, Vector3.up, Vector3.up }, null, null);
                var skinned = face.AddComponent<SkinnedMeshRenderer>(); skinned.sharedMesh = mesh;
                seen = OptimizationSection.Signature(avatar);
                skinned.SetBlendShapeWeight(0, 50); Moved("a blendshape weight");
                Object.DestroyImmediate(mesh);

                double next = 0;
                bool asked = false;
                long Read() { asked = true; return seen + 1; }
                Assert.IsTrue(OptimizationSection.ShouldRefresh(false, 10, ref next, Read, seen), "the signature moved");
                Assert.IsTrue(asked);
                asked = false;
                Assert.IsFalse(OptimizationSection.ShouldRefresh(false, 10 + OptimizationSection.SignatureEvery / 2, ref next, Read, seen), "not read again so soon");
                Assert.IsFalse(asked);
                Assert.IsTrue(OptimizationSection.ShouldRefresh(true, 10 + OptimizationSection.SignatureEvery / 2, ref next, Read, seen), "an event refreshes at once");
                Assert.IsTrue(OptimizationSection.ShouldRefresh(false, 10 + OptimizationSection.SignatureEvery, ref next, Read, seen), "read again later");
                Assert.IsFalse(OptimizationSection.ShouldRefresh(false, 20 + OptimizationSection.SignatureEvery, ref next, () => seen, seen), "unchanged");
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }
    }
}
