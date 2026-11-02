using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.MyAvatar.Editor.Optimization;
using Orbiters.Toolkit.Editor.VRChat.Optimization;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

// What merging toggled meshes adds to the download, as the download estimate counts it: the blendshape hiding a part
// weighs what the merge was told it would (DownloadSize.CollapseBytes), and a part hidden by a bone adds no blendshape.
public sealed class MergeToggledDownloadTests
{
    private readonly List<Object> owned = new List<Object>();
    private Scene scene;
    private GameObject root;
    private Transform[] bones;

    [SetUp] public void SetUp()
    {
        scene = EditorSceneManager.NewPreviewScene();
        root = new GameObject("Avatar");
        SceneManager.MoveGameObjectToScene(root, scene);
        var fx = Own(new AnimatorController());
        var descriptor = root.AddComponent<VRCAvatarDescriptor>();
        descriptor.customizeAnimationLayers = true;
        descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = fx } };
        bones = new Transform[2];
        for (int i = 0; i < 2; i++)
        {
            bones[i] = new GameObject("Bone" + i).transform;
            bones[i].SetParent(i == 0 ? root.transform : bones[0], false);
            bones[i].localPosition = i == 0 ? Vector3.zero : Vector3.up;
        }
        Skinned("Body", root.transform, 24, new[] { 0, 1 });
        var outfit = new GameObject("Outfit").transform;
        outfit.SetParent(root.transform, false);
        Skinned("Sleeve", outfit, 40, new[] { 0, 1 });
        var cap = Skinned("Cap", root.transform, 12, new[] { 1 });
        cap.enabled = false;
        fx.AddLayer("Toggles");
        foreach (var (path, type, property) in new[] { ("Outfit", typeof(GameObject), "m_IsActive"), ("Cap", typeof(SkinnedMeshRenderer), "m_Enabled") })
        {
            var clip = Own(new AnimationClip { name = path });
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, type, property), AnimationCurve.Constant(0, 1, 1));
            fx.layers[0].stateMachine.AddState(path).motion = clip;
        }
    }

    [TearDown] public void TearDown()
    {
        EditorSceneManager.ClosePreviewScene(scene);
        foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
        owned.Clear();
    }

    [Test] public void TheDownloadGrowsByWhatTheMergeWasToldItWould()
    {
        var before = DownloadSize.Of(root);
        var policy = new MeshMerge.ToggledPolicy
        {
            SkinnedLimits = new[] { 1, 2, 8, 16 }, MeshLimits = new[] { 1, 4, 8, 16 }, BoneLimits = new[] { 75, 150, 256, 400 }, CollapseBytes = DownloadSize.CollapseBytes,
        };
        // An estimate's copy (nothing plays it): the same merge as at upload, without the animations.
        var result = MeshMerge.MergeToggled(new AvatarUsage(root), null, policy);
        owned.AddRange(result.Meshes);
        var after = DownloadSize.Of(root);

        Assert.That(result.Merged, Is.EqualTo(2), string.Join("\n", result.Skipped));
        Assert.That(result.CollapseShapes, Is.EqualTo(1), "The sleeve follows two bones: a blendshape.");
        Assert.That(result.HideBones, Is.EqualTo(1), "The cap follows one: a bone.");
        Assert.That(result.AddedBytes, Is.EqualTo(DownloadSize.CollapseBytes(40 * 4) + 64));
        Assert.That(after.BlendShapes - before.BlendShapes, Is.EqualTo(DownloadSize.CollapseBytes(40 * 4)).Within(1),
            "The estimate counts the hiding blendshape as the merge weighed it.");
        Debug.Log("Merging 2 toggled meshes: download " + before.Total + " -> " + after.Total + " bytes (blendshapes " + before.BlendShapes + " -> " +
                  after.BlendShapes + ", meshes " + before.Meshes + " -> " + after.Meshes + ").");
    }

    private T Own<T>(T o) where T : Object { owned.Add(o); return o; }

    // A strip of quads up the bones, each quad on one bone, in the avatar's space.
    private SkinnedMeshRenderer Skinned(string name, Transform parent, int quads, int[] on)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var mesh = Own(new Mesh { name = name });
        var vertices = new List<Vector3>(); var weights = new List<BoneWeight>(); var indices = new List<int>();
        for (int q = 0; q < quads; q++)
        {
            int bone = on[q % on.Length];
            var at = new Vector3(q * .05f, bone + .1f, name.Length * .01f);
            int first = vertices.Count;
            vertices.AddRange(new[] { at, at + Vector3.right * .04f, at + Vector3.up * .04f, at + new Vector3(.04f, .04f, 0) });
            for (int c = 0; c < 4; c++) weights.Add(new BoneWeight { boneIndex0 = bone, weight0 = 1 });
            indices.AddRange(new[] { first, first + 2, first + 1, first + 1, first + 2, first + 3 });
        }
        mesh.SetVertices(vertices);
        mesh.boneWeights = weights.ToArray();
        mesh.SetTriangles(indices, 0);
        mesh.RecalculateNormals();
        mesh.bindposes = bones.Select(b => b.worldToLocalMatrix).ToArray();
        var renderer = go.AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMesh = mesh; renderer.bones = bones; renderer.rootBone = bones[0];
        renderer.sharedMaterial = Own(new Material(Shader.Find("Standard")) { name = name });
        return renderer;
    }
}
