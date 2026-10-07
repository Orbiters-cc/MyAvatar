using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor.Tests
{
    public sealed class AvatarMaterialsTests
    {
        private readonly List<Object> owned = new List<Object>();

        [TearDown]
        public void Clean()
        {
            foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
            owned.Clear();
        }

        [Test]
        public void StandardMaterialsAndHiddenBacksOfOpenSurfacesAreAdvised()
        {
            var root = new GameObject("Avatar"); owned.Add(root);
            var standardCard = Material("Standard", "Feathers");
            var standardBody = Material("Standard", "Body");
            var lens = Material("Standard", "Lens");
            lens.SetFloat("_Mode", 3);
            var culledFur = Material("VRChat/Mobile/Toon Standard", "Fur");
            var tail = Material("VRChat/Mobile/Toon Standard", "Tail");
            tail.SetFloat("_Culling", 0);
            Part(root, "Feathers", Card(), standardCard);
            Part(root, "Body", Box(), standardBody);
            Part(root, "Eyes", Card(), lens);
            Part(root, "Fur", Card(), culledFur);
            Part(root, "Tail", Card(), tail);
            Part(root, "Preview", Card(), Material("Standard", "Helper")).tag = "EditorOnly";

            var advice = AvatarMaterials.Inspect(root).ToDictionary(a => a.Material.name);

            CollectionAssert.AreEquivalent(new[] { "Body", "Feathers", "Fur" }, advice.Keys, "transparent Standard, double-sided tails and editor helpers stay as they are");
            Assert.True(advice["Feathers"].Standard && advice["Feathers"].HiddenBack);
            Assert.True(advice["Body"].Standard);
            Assert.False(advice["Body"].HiddenBack, "a closed body never shows its inside");
            Assert.False(advice["Fur"].Standard);
            Assert.True(advice["Fur"].HiddenBack);
            Assert.False(advice["Fur"].Locked);
            Assert.AreEqual("Fur", advice["Fur"].Renderers.Single().name);
        }

        private Material Material(string shader, string name)
        {
            var material = new Material(Shader.Find(shader)) { name = name }; owned.Add(material);
            return material;
        }

        private static GameObject Part(GameObject root, string name, Mesh mesh, Material material)
        {
            var part = new GameObject(name);
            part.transform.SetParent(root.transform, false);
            part.AddComponent<MeshFilter>().sharedMesh = mesh;
            part.AddComponent<MeshRenderer>().sharedMaterial = material;
            return part;
        }

        private Mesh Card()
        {
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.up, Vector3.right, new Vector3(1, 1, 0) }, triangles = new[] { 0, 1, 2, 2, 1, 3 } };
            owned.Add(mesh);
            return mesh;
        }

        private Mesh Box()
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            foreach (var normal in new[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back })
            {
                var u = Vector3.Cross(normal, Mathf.Abs(normal.y) > .5f ? Vector3.forward : Vector3.up);
                var v = Vector3.Cross(normal, u);
                int first = vertices.Count;
                vertices.Add((normal - u - v) * .5f); vertices.Add((normal + u - v) * .5f);
                vertices.Add((normal + u + v) * .5f); vertices.Add((normal - u + v) * .5f);
                triangles.AddRange(new[] { first, first + 1, first + 2, first, first + 2, first + 3 });
            }
            var mesh = new Mesh { vertices = vertices.ToArray(), triangles = triangles.ToArray() }; owned.Add(mesh);
            return mesh;
        }
    }
}
