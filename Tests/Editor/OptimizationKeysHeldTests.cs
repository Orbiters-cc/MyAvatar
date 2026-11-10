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
    }
}
