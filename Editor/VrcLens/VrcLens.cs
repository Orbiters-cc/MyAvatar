using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.Storage;
using Orbiters.Toolkit.Editor.VRChat;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.Editor.VRChat.Parameters;
using Orbiters.Toolkit.Editor.VRChat.Posing;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor
{
    /// <summary>
    /// VRCLens, Hirabiki's avatar camera, on an avatar in one drop of its package (or of its folder or prefab once in the
    /// project). My Avatar imports the user's own copy when it is not in the project yet (VRCLens 1.10 and later need VRLabs'
    /// Avatars 3.0 Manager first: offered as a download, then the drop goes on), then runs VRCLens's own setup as its
    /// window's Apply VRCLens does: VRCLens's Auto Arrange places the camera in a hand, the pen in the other and the rest by
    /// the head, its setup puts its objects on the avatar and writes its animator, menu and parameters. With the avatar's
    /// own controllers, menu and parameters out of its way, VRCLens writes them as for an avatar without any: a VRCLens
    /// instance of their own, in their folder of VRCLens's User folder, that VRCFury merges at build. The avatar's files
    /// never change. On the avatar it is an item like the accessories (its card: the camera's hand, the drone controls,
    /// Remove); dropping VRCLens again sets it up again with that version, its camera staying where it was (VRCLens's Load
    /// Avatar Setup). Nothing of VRCLens is copied outside its own folder: Orbiters ships none of it.
    /// </summary>
    internal static class VrcLens
    {
        // VRCLens's files keep their GUIDs from version to version.
        internal const string SetupScriptGuid = "f991aa6629eb5b94487cdb2ba0dd11ba";
        internal const string PrefabGuid = "576bef3e836e26c438f5ccaa610b1079";
        private const string CameraModelGuid = "3bcfefd9ff1a42a4599d149a334a5562";
        private const string SetupTypeName = "Hirabiki.AV3.Works.VRCLens.VRCLensSetup";
        private const string InspectorTypeName = "Hirabiki.AV3.Works.VRCLens.ConfigInspector";
        private const string ManagerTypeName = "VRLabs.AV3Manager.AnimatorCloner";
        internal const string ManagerId = "dev.vrlabs.av3manager";
        /// <summary>The object VRCLens puts under the avatar root: its animations find their objects below it by this name.</summary>
        internal const string ObjectName = "VRCLens";
        // Synced bits: its feature switch and two floats (8 each) and its Enabled toggle (1); the drone controls' two puppet floats.
        internal const int Bits = 25, DroneBits = 16;
        private const string SetupScriptEnd = "/VRCLens/Resource/VRCLensSetup.cs", ReadmeEnd = "/VRCLens/readme_DoNotDelete.txt";
        private static readonly Regex VersionLine = new Regex(@"VRCLens version ([0-9]+(?:\.[0-9]+)*)", RegexOptions.CultureInvariant);

        /// <summary>The download VRCLens 1.10 and later need before their import, and the drops waiting for it.</summary>
        internal static readonly PackageSetup Downloads = new PackageSetup("VRCLens", "VRCLens", "Avatars 3.0 Manager", () => ManagerInstalled, Continue);
        private const string DropKey = "Orbiters.MyAvatar.VRCLens.Drop.";

        [InitializeOnLoadMethod]
        private static void Register()
        {
            AttachmentHooks.RegisterOptions(Options);
            // After the download reloaded Unity's scripts: the drop goes on once the package is in.
            Downloads.Resume();
        }

        // ---- VRCLens in the project ------------------------------------------------------------------------------

        private static readonly Dictionary<string, Type> Types = new Dictionary<string, Type>();

        // VRCLens's scripts compile into the project's own assemblies (Assembly-CSharp): reached by name.
        private static Type Find(string name)
        {
            if (Types.TryGetValue(name, out var known)) return known;
            Type found = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { found = assembly.GetType(name, false); }
                catch (Exception ex) when (ex is FileLoadException || ex is BadImageFormatException) { }
                if (found != null) break;
            }
            Types[name] = found;
            return found;
        }

        internal static bool ManagerInstalled => Find(ManagerTypeName) != null;

        /// <summary>VRCLens's folder in the project (its readme, Prefabs and Resource), or null.</summary>
        internal static string Root
        {
            get
            {
                string script = AssetDatabase.GUIDToAssetPath(SetupScriptGuid);
                const string tail = "/Resource/VRCLensSetup.cs";
                return !string.IsNullOrEmpty(script) && script.EndsWith(tail, StringComparison.Ordinal) ? script.Substring(0, script.Length - tail.Length) : null;
            }
        }

        /// <summary>The version of VRCLens in the project (its readme's first line), or null.</summary>
        internal static string InstalledVersion
        {
            get
            {
                string root = Root;
                if (root == null) return null;
                string readme = Path.Combine(AccessoryImport.ProjectRoot, root, "readme_DoNotDelete.txt");
                try { return File.Exists(readme) ? Version(File.ReadLines(readme).FirstOrDefault()) : null; }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return null; }
            }
        }

        internal static string Version(string readme)
        {
            var match = VersionLine.Match(readme ?? "");
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>
        /// VRCLens's setup prefab when a drop's prefabs and models are VRCLens or part of it (its package, its folder, its
        /// prefab), else null.
        /// </summary>
        internal static string SetupPrefab(IEnumerable<string> assets)
        {
            string root = Root;
            var setupType = Find(SetupTypeName);
            if (root == null || setupType == null) return null;
            string prefab = AssetDatabase.GUIDToAssetPath(PrefabGuid);
            if (string.IsNullOrEmpty(prefab)) prefab = root + "/Prefabs/VRCLens.prefab";
            var list = assets.Where(a => !string.IsNullOrEmpty(a)).ToList();
            // A copy of its prefab kept elsewhere is set up as itself.
            string own = list.FirstOrDefault(a => a.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) &&
                AssetDatabase.LoadAssetAtPath<GameObject>(a) is GameObject go && go.GetComponent(setupType) != null);
            if (own != null) return own;
            bool part = list.Any(a => a.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase));
            return part && AssetDatabase.LoadAssetAtPath<GameObject>(prefab) is GameObject setup && setup.GetComponent(setupType) != null ? prefab : null;
        }

        // ---- Its package -------------------------------------------------------------------------------------------

        internal sealed class PackageInfo
        {
            public string version;
            /// <summary>Its scripts use VRLabs' Avatars 3.0 Manager, a VPM package (VRCLens 1.10 and later).</summary>
            public bool needsManager;
        }

        /// <summary>What a .unitypackage holds of VRCLens, or null for another package. Background thread.</summary>
        internal static PackageInfo ReadPackage(string path)
        {
            var index = UnityPackageIndex.Read(path, p => IsSetupScript(p) || IsReadme(p), maxContentBytes: 1024 * 1024,
                maxExpandedBytes: AccessoryImport.MaxArchiveBytes, maxEntries: AccessoryImport.MaxPackageRecords, maxPathnameTotalBytes: AccessoryImport.MaxPackageNameBytes);
            var script = index.Entries.FirstOrDefault(e => string.Equals(e.Guid, SetupScriptGuid, StringComparison.OrdinalIgnoreCase) || IsSetupScript(e.Path));
            if (script == null) return null;
            var readme = index.Entries.FirstOrDefault(e => IsReadme(e.Path) && e.Content != null);
            return new PackageInfo
            {
                version = readme != null ? Version(Encoding.UTF8.GetString(readme.Content)) : null,
                // 1.10 left the animator merging it uses to the VPM package; older versions carried their own copy of it.
                needsManager = script.Content != null ? Encoding.UTF8.GetString(script.Content).Contains(ManagerTypeName)
                    : !index.Paths.Any(p => p.EndsWith("/AnimatorCloner.cs", StringComparison.OrdinalIgnoreCase)),
            };
        }

        private static bool IsSetupScript(string path) => path != null && path.Replace('\\', '/').EndsWith(SetupScriptEnd, StringComparison.OrdinalIgnoreCase);
        private static bool IsReadme(string path) => path != null && path.Replace('\\', '/').EndsWith(ReadmeEnd, StringComparison.OrdinalIgnoreCase);

        [Serializable] private sealed class Paths { public List<string> list = new List<string>(); }

        /// <summary>
        /// Before a drop imports anything: a VRCLens whose scripts need Avatars 3.0 Manager is not imported while it is
        /// missing (the project would stop compiling). The drop then waits for its download (<see cref="AwaitsManager"/>) and
        /// starts again once it is in. Returns the status saying so, or null when nothing waits.
        /// </summary>
        internal static async Task<string> CheckAsync(MyAvatar avatar, IEnumerable<AccessoryImport.Review> reviews, List<string> dropped, CancellationToken cancellation)
        {
            if (ManagerInstalled) return null;
            foreach (var review in reviews.Where(r => r.package != null))
            {
                var path = review.package.path;
                var info = await Task.Run(() => ReadPackage(path), cancellation);
                if (info == null || !info.needsManager) continue;
                SessionState.SetString(DropKey + avatar.GetInstanceID(), JsonUtility.ToJson(new Paths { list = dropped ?? new List<string>() }));
                return "VRCLens " + (info.version ?? "") + (info.version != null ? " " : "") + "needs VRLabs' Avatars 3.0 Manager in the project before its import " +
                       "(without it, its scripts don't compile): download it below, then VRCLens is imported and set up.";
            }
            return null;
        }

        /// <summary>A VRCLens drop on <paramref name="avatar"/> waits for Avatars 3.0 Manager.</summary>
        internal static bool AwaitsManager(MyAvatar avatar) => avatar && !string.IsNullOrEmpty(SessionState.GetString(DropKey + avatar.GetInstanceID(), null));

        /// <summary>The download was declined or failed: the waiting drop is dropped.</summary>
        internal static void Forget(MyAvatar avatar)
        {
            if (!avatar) return;
            SessionState.EraseString(DropKey + avatar.GetInstanceID());
            Downloads.Forget(avatar);
        }

        // Avatars 3.0 Manager is in: the waiting drop starts again (a frame later, outside the download's Undo step).
        private static string Continue(GameObject root)
        {
            var avatar = root != null ? root.GetComponentInChildren<MyAvatar>(true) : null;
            string key = avatar ? DropKey + avatar.GetInstanceID() : null;
            var paths = key != null ? JsonUtility.FromJson<Paths>(SessionState.GetString(key, "{}"))?.list : null;
            if (key != null) SessionState.EraseString(key);
            if (paths == null || paths.Count == 0) return "the drop that waited for it was forgotten: drop VRCLens again.";
            void Once()
            {
                EditorApplication.update -= Once;
                Redrop(avatar, paths.ToArray());
            }
            EditorApplication.update += Once;
            return null;
        }

        private static async void Redrop(MyAvatar avatar, string[] paths)
        {
            if (!avatar) return;
            // Its Inspector shows the drop as if it had just been made there.
            var editor = Resources.FindObjectsOfTypeAll<MyAvatarEditor>().FirstOrDefault(e => e != null && e.target == avatar && e.Accessories != null);
            if (editor != null) { editor.DropFromElsewhere(paths); return; }
            try { await AccessoryService.DropAsync(avatar, paths, (_, __) => { }, CancellationToken.None); }
            catch (Exception ex) { AccessoryService.Status(avatar, ex.Message, true, avatar.accessoryNotes); }
        }

        // ---- On the avatar ---------------------------------------------------------------------------------------

        internal sealed class Settings
        {
            /// <summary>The camera in the left hand (VRCLens's "Camera Handedness"), the pen in the right.</summary>
            public bool leftHand;
            /// <summary>VRCLens's drone and HUD puppets ("Enable drone controls"), 16 more synced bits.</summary>
            public bool drone;
        }

        internal static bool IsItem(OrbitersAttachment attachment) =>
            attachment && attachment.source == PrefabGuid && attachment.name == ObjectName && attachment.transform.parent != null;

        /// <summary>VRCLens set up by My Avatar on the avatar of <paramref name="root"/>, or null.</summary>
        internal static OrbitersAttachment Item(Transform root)
        {
            var lens = root ? root.Find(ObjectName) : null;
            var attachment = lens ? lens.GetComponent<OrbitersAttachment>() : null;
            return IsItem(attachment) ? attachment : null;
        }

        // VRCLens's own test for its setup on an avatar (VRCLensSetup.IsAlreadySetup).
        private static bool HasSetup(Transform root) => root && root.Find(ObjectName + "/WorldA/AnimDummy") != null;

        // What VRCLens's setup puts on the avatar's bones: its pickups at the camera's hand (PickupA), the pen's (PickupB) and
        // the head (PickupC), each known by the object VRCLens puts under it.
        private static List<GameObject> Pickups(Transform root)
        {
            var found = new List<GameObject>();
            var animator = root ? root.GetComponent<Animator>() : null;
            if (!animator || !animator.isHuman) return found;
            void Look(HumanBodyBones bone, string name, string marker)
            {
                var parent = animator.GetBoneTransform(bone);
                var pickup = parent ? parent.Find(name) : null;
                if (pickup && pickup.Find(marker)) found.Add(pickup.gameObject);
            }
            foreach (var hand in new[] { HumanBodyBones.LeftHand, HumanBodyBones.RightHand })
            {
                Look(hand, "PickupA", "DynVR");
                Look(hand, "PickupB", "DynVRAlt");
            }
            Look(HumanBodyBones.Head, "PickupC", "DynDesktop");
            return found;
        }

        /// <summary>How VRCLens is set up on the avatar now: the camera's hand, and its drone controls.</summary>
        internal static Settings Current(OrbitersAttachment item)
        {
            var root = item.transform.parent;
            var animator = root.GetComponent<Animator>();
            var left = animator && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.LeftHand) : null;
            var camera = left ? left.Find("PickupA") : null;
            var parameters = Parameters(item);
            return new Settings
            {
                leftHand = camera && camera.Find("DynVR"),
                drone = parameters != null && parameters.parameters != null && parameters.parameters.Any(p => p != null && p.name == "VRCFaceBlendH"),
            };
        }

        /// <summary>
        /// For an avatar getting VRCLens: the camera in the right hand (VRCLens's default), and its drone controls while the
        /// avatar's synced parameters fit VRChat's 256 bits with them, without VRCFury having to compress them.
        /// </summary>
        internal static Settings Defaults(Transform root)
        {
            int used = AvatarParameterBudget.Estimate(root.gameObject).TotalBeforeCompression;
            return new Settings { leftHand = false, drone = used + Bits + DroneBits <= VRCExpressionParameters.MAX_PARAMETER_COST };
        }

        // The parameters of VRCLens's instance, as its VRCFury Full Controller merges them.
        private static VRCExpressionParameters Parameters(OrbitersAttachment item)
        {
            foreach (var (component, feature, kind) in VrcFury.Features(item.gameObject))
            {
                if (kind != "FullController" || component == null || component.gameObject != item.gameObject) continue;
                if (VrcFury.Field(feature, "prms") is System.Collections.IEnumerable entries)
                    foreach (var entry in entries)
                        if (VrcFury.ObjectReference(entry?.GetType().GetField("parameters")?.GetValue(entry)) is VRCExpressionParameters parameters) return parameters;
            }
            return null;
        }

        /// <summary>
        /// Sets VRCLens up on the avatar at <paramref name="root"/>, or again with <paramref name="settings"/> (null: as it is,
        /// or the defaults), with VRCLens's own setup, as one Undo step. An update keeps where its camera, pen and head camera
        /// are (VRCLens's Load Avatar Setup) and swaps them to the other hand as VRCLens's Auto Swap does. Throws with what to
        /// do when it can't.
        /// </summary>
        internal static OrbitersAttachment Apply(Transform root, Settings settings = null, string prefabPath = null)
        {
            var setupType = Find(SetupTypeName);
            var inspectorType = Find(InspectorTypeName);
            if (setupType == null || inspectorType == null) throw new InvalidOperationException("VRCLens is not in this project: drop its package on the avatar.");
            if (!VrcFury.Installed || VrcFury.Writer == null) throw new InvalidOperationException("VRCLens goes on through VRCFury: install VRCFury, then drop VRCLens again.");
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            var animator = root.GetComponent<Animator>();
            if (!descriptor || !animator || !animator.isHuman) throw new InvalidOperationException("VRCLens goes on a humanoid avatar with an avatar descriptor.");
            var missing = new[] { HumanBodyBones.Head, HumanBodyBones.Neck, HumanBodyBones.LeftHand, HumanBodyBones.RightHand, HumanBodyBones.LeftLowerArm,
                HumanBodyBones.RightLowerArm, HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot }
                .Where(b => animator.GetBoneTransform(b) == null).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException("VRCLens needs the avatar's head, neck, arms, hands and feet in its humanoid rig; missing: " +
                    string.Join(", ", missing.Select(b => ObjectNames.NicifyVariableName(b.ToString()))) + ".");
            var scale = root.localScale;
            if (!Mathf.Approximately(scale.x, scale.y) || !Mathf.Approximately(scale.y, scale.z))
                throw new InvalidOperationException("VRCLens needs the avatar's scale to be the same on every axis.");
            var existing = Item(root);
            if (existing == null && HasSetup(root))
                throw new InvalidOperationException("This avatar already has VRCLens, set up with VRCLens's own window (its layers are in the avatar's FX). " +
                    "Remove it there first (Remove VRCLens), then drop VRCLens again.");
            prefabPath = string.IsNullOrEmpty(prefabPath) ? AssetDatabase.GUIDToAssetPath(PrefabGuid) : prefabPath;
            var prefab = string.IsNullOrEmpty(prefabPath) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null || prefab.GetComponent(setupType) == null) throw new InvalidOperationException("VRCLens's prefab is missing from its folder: drop its package again.");
            settings = settings ?? (existing != null ? Current(existing) : Defaults(root));

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            string step = (existing != null ? "Update VRCLens on " : "Add VRCLens to ") + root.name;
            Undo.SetCurrentGroupName(step);
            var selection = Selection.objects;
            var activeScene = SceneManager.GetActiveScene();
            GameObject setupObject = null;
            UnityEditor.Editor inspector = null;
            var made = new List<GameObject>();
            // What is on the avatar before: never taken for what VRCLens makes now.
            var earlier = new HashSet<GameObject>(Pickups(root));
            if (existing != null) earlier.Add(existing.gameObject);
            (RuntimeAnimatorController controller, VRCExpressionsMenu menu, VRCExpressionParameters parameters) instance = default;
            try
            {
                using (AccessoryPoseSync.Pause(root))
                {
                    setupObject = NewSetupObject(prefab, root.gameObject.scene);
                    var setup = setupObject.GetComponent(setupType);
                    Set(setup, "avatarDescriptor", descriptor);
                    Set(setup, "targetSubMenu", null);
                    Set(setup, "setupMode", settings.leftHand ? 0 : 1);
                    Set(setup, "av3Puppet", settings.drone);
                    // As its window offers when they differ: VRCLens's states write defaults as the avatar's own FX does.
                    Set(setup, "useWriteDefaults", (bool)Call(setup, "ContainsWriteDefaults"));
                    Call(setup, "UpdateCustomRes");

                    // VRCLens sets up an avatar standing at the origin (its window asks you to move it there): it stands there
                    // while VRCLens works, then goes back.
                    var position = root.localPosition;
                    var rotation = root.localRotation;
                    string name = root.name;
                    var saved = new DescriptorState(descriptor);
                    try
                    {
                        root.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                        if (existing != null)
                        {
                            // VRCLens's Load Avatar Setup, then its Auto Swap when the camera changes hands.
                            Call(setup, "PreserveSetupOnReset");
                            if (!(bool)Call(setup, "IsInCorrectHand")) Call(setup, "SwapCameraHand");
                            foreach (var pickup in Pickups(root)) Undo.DestroyObjectImmediate(pickup);
                            Undo.DestroyObjectImmediate(existing.gameObject);
                        }
                        else Call(setup, "AutoArrangeSetup");

                        // VRCLens writes its files in its User folder, in a folder named after the avatar: each setup gets
                        // its own, so avatars of the same name never share them, and an Undo finds the earlier ones.
                        root.name = name + " (My Avatar " + Guid.NewGuid().ToString("N").Substring(0, 8) + ")";
                        // Without the avatar's own controllers, menu and parameters, VRCLens writes its own instance of them.
                        saved.Clear(descriptor);
                        // Objects it makes on the way land in the active scene: the avatar's, while it works.
                        var scene = root.gameObject.scene;
                        if (scene != activeScene && scene.isLoaded && !EditorSceneManager.IsPreviewScene(scene)) SceneManager.SetActiveScene(scene);
                        inspector = UnityEditor.Editor.CreateEditor(setup, inspectorType);
                        // Its Apply VRCLens button, from its window.
                        Call(setup, "RefreshCopyFolderPath");
                        Call(setup, "RefreshAssetReserializeList");
                        CallPrivate(inspector, "ApplySetup");
                        CallPrivate(inspector, "ApplyAnimations");
                        Call(setup, "ReserializeAssetList");
                        if (setupType.GetField("_hasNoErrors", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(setup) is bool ok && !ok)
                            throw new InvalidOperationException("VRCLens's setup stopped with an error: see the Console.");
                    }
                    finally
                    {
                        EditorUtility.ClearProgressBar();
                        var lens = root.Find(ObjectName);
                        if (lens != null && !earlier.Contains(lens.gameObject)) made.Add(lens.gameObject);
                        made.AddRange(Pickups(root).Where(p => !earlier.Contains(p)));
                        instance = saved.Instance(descriptor);
                        saved.Restore(descriptor);
                        root.name = name;
                        root.localPosition = position;
                        root.localRotation = rotation;
                        if (SceneManager.GetActiveScene() != activeScene && activeScene.isLoaded) SceneManager.SetActiveScene(activeScene);
                    }
                    if (instance.controller != null) Track(AssetDatabase.GetAssetPath(instance.controller));
                    if (!(instance.controller is AnimatorController fx) || !fx.layers.Any(l => l.name.StartsWith("vCN", StringComparison.Ordinal)) ||
                        instance.menu == null || instance.parameters == null || made.Count == 0 || made[0].name != ObjectName)
                        throw new InvalidOperationException("VRCLens's setup made no camera for this avatar: see the Console.");
                    var item = Finish(made, instance.controller, instance.menu, instance.parameters, prefabPath);
                    // Named last: Edit › Undo shows the step's name, not its last operation's.
                    Undo.SetCurrentGroupName(step);
                    Undo.CollapseUndoOperations(group);
                    Undo.IncrementCurrentGroup();
                    return item;
                }
            }
            catch
            {
                // The earlier VRCLens comes back, then what VRCLens put on the avatar before it stopped goes (it is not in Undo).
                Undo.CollapseUndoOperations(group);
                Undo.RevertAllDownToGroup(group);
                Undo.IncrementCurrentGroup();
                foreach (var go in made.Where(g => g != null)) Object.DestroyImmediate(go);
                throw;
            }
            finally
            {
                if (inspector != null) Object.DestroyImmediate(inspector);
                if (setupObject != null) Object.DestroyImmediate(setupObject);
                // VRCLens's window changes the selection as it closes.
                if (!Selection.objects.SequenceEqual(selection)) Selection.objects = selection;
                GeneratedAssets.SweepSoon();
            }
        }

        // VRCLens's prefab as its "Add To Scene" makes it, in the avatar's scene from the start (the scene shown stays as it was).
        private static GameObject NewSetupObject(GameObject prefab, Scene scene)
        {
            var holder = EditorUtility.CreateGameObjectWithHideFlags("VRCLens setup", HideFlags.HideAndDontSave);
            try
            {
                SceneManager.MoveGameObjectToScene(holder, scene);
                var setup = Object.Instantiate(prefab, holder.transform);
                setup.name = prefab.name;
                setup.transform.SetParent(null, false);
                if (setup.scene != scene) SceneManager.MoveGameObjectToScene(setup, scene);
                setup.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                setup.transform.localScale = Vector3.one;
                return setup;
            }
            finally { Object.DestroyImmediate(holder); }
        }

        // VRCLens's objects on the avatar become the item: in Undo, merged by VRCFury, on the card.
        private static OrbitersAttachment Finish(List<GameObject> made, RuntimeAnimatorController controller, VRCExpressionsMenu menu,
            VRCExpressionParameters parameters, string prefabPath)
        {
            foreach (var go in made) Undo.RegisterCreatedObjectUndo(go, "Add VRCLens");
            var lens = made[0];
            // Its parameters keep their names, as VRCLens's own setup leaves them (also for tools that read them by name).
            var names = parameters.parameters.Where(p => p != null && !string.IsNullOrEmpty(p.name)).Select(p => p.name).Distinct().ToList();
            if (VrcFury.Writer.FullController(lens, controller, menu, parameters, names) == null)
                throw new InvalidOperationException("VRCFury did not add its Full Controller to VRCLens.");
            var item = Undo.AddComponent<OrbitersAttachment>(lens);
            item.created = true;
            item.mode = OrbitersAttachment.AttachMode.Configured;
            item.source = PrefabGuid;
            item.variant = prefabPath;
            item.syncBlendShapes = false;
            item.displayName = ObjectName;
            item.displayNameFor = lens.name;
            EditorUtility.SetDirty(item);
            if (lens.scene.IsValid() && !EditorSceneManager.IsPreviewScene(lens.scene)) EditorSceneManager.MarkSceneDirty(lens.scene);
            return item;
        }

        // VRCLens's files for this setup go once nothing uses them (VRCLens removed or set up again; an Undo brings them back).
        private static void Track(string controllerPath)
        {
            string folder = string.IsNullOrEmpty(controllerPath) ? null : Path.GetDirectoryName(controllerPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(folder) || !AssetDatabase.IsValidFolder(folder) || Root == null || !folder.StartsWith(Root + "/User/", StringComparison.Ordinal)) return;
            GeneratedAssets.Track(AssetDatabase.FindAssets("", new[] { folder }).Select(AssetDatabase.GUIDToAssetPath).Append(folder));
        }

        /// <summary>Takes VRCLens off the avatar: its objects and pickups (one Undo step); its files go once nothing uses them.</summary>
        internal static void Remove(OrbitersAttachment item)
        {
            if (!IsItem(item)) return;
            var root = item.transform.parent;
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            foreach (var pickup in Pickups(root)) Undo.DestroyObjectImmediate(pickup);
            Undo.DestroyObjectImmediate(item.gameObject);
            Undo.SetCurrentGroupName("Remove VRCLens from " + root.name);
            Undo.CollapseUndoOperations(group);
            Undo.IncrementCurrentGroup();
            GeneratedAssets.SweepSoon();
        }

        /// <summary>A drop held VRCLens: sets it up (or up again) on the avatar and says so in the drop field.</summary>
        internal static void Install(MyAvatar avatar, string prefab, List<MyAvatar.AccessoryNote> notes)
        {
            var root = TextureOptimization.AvatarRoot(avatar).transform;
            bool update = Item(root) != null;
            OrbitersAttachment item;
            try { item = Apply(root, null, prefab); }
            catch (Exception ex)
            {
                notes.Add(new MyAvatar.AccessoryNote { text = "VRCLens was not set up: " + ex.Message, warning = true, vrcFury = !VrcFury.Installed });
                AccessoryService.Status(avatar, "VRCLens was not set up: " + ex.Message, true, notes);
                return;
            }
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            if (descriptor != null && descriptor.expressionsMenu != null && descriptor.expressionsMenu.controls.Count >= VRCExpressionsMenu.MAX_CONTROLS)
                notes.Add(new MyAvatar.AccessoryNote { accessory = item, text = "The Action Menu's first page is full: VRCFury puts VRCLens on its Next page." });
            AccessoryService.Status(avatar, Summary(item, update), notes.Any(n => n.warning), notes);
            EditorGUIUtility.PingObject(item.gameObject);
        }

        internal static string Summary(OrbitersAttachment item, bool update)
        {
            var settings = Current(item);
            string version = InstalledVersion;
            return (update ? "VRCLens set up again" : "VRCLens is on the avatar") + (version != null ? " (" + version + ")" : "") +
                   ": its camera in the " + (settings.leftHand ? "left" : "right") + " hand, its menu in the Action Menu, " +
                   (settings.drone ? "with" : "without") + " its drone controls. VRCLens's own setup made it; VRCFury merges it at build.";
        }

        /// <summary>Its line on the card.</summary>
        internal static string Describe(OrbitersAttachment item)
        {
            var settings = Current(item);
            string version = InstalledVersion;
            var parameters = Parameters(item);
            int bits = parameters != null ? parameters.CalcTotalCost() : Bits + (settings.drone ? DroneBits : 0);
            return "VRCLens" + (version != null ? " " + version : "") + " · camera in the " + (settings.leftHand ? "left" : "right") + " hand · " +
                   bits + " synced bits · merged by VRCFury";
        }

        /// <summary>Its picture on the card: VRCLens's own camera model (the camera on the avatar shows only in game).</summary>
        internal static Texture2D Picture()
        {
            string path = AssetDatabase.GUIDToAssetPath(CameraModelGuid);
            var model = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(path);
            return model != null ? ModelThumbnails.Get(model, ModelThumbnails.KeyFor(path)) : null;
        }

        // Its settings on the card: the camera's hand and the drone controls, each set up again by VRCLens when changed.
        private static IEnumerable<AttachmentOption> Options(GameObject placed)
        {
            var item = placed != null ? placed.GetComponent<OrbitersAttachment>() : null;
            if (!IsItem(item)) yield break;
            var current = Current(item);
            yield return new AttachmentOption
            {
                Label = "Camera hand",
                Tooltip = "The hand holding VRCLens's camera in VR; the other holds its focus pen. Desktop players get it by the head.",
                Choices = new[] { "Left", "Right" },
                Current = current.leftHand ? 0 : 1,
                Choose = index => Change(item, s => s.leftHand = index == 0),
            };
            yield return new AttachmentOption
            {
                Label = "Drone controls",
                Tooltip = "VRCLens's drone and the puppets moving its HUD in VR: " + DroneBits + " more synced parameter bits.",
                IsToggle = true,
                Choices = new[] { "Off", "On" },
                Current = current.drone ? 1 : 0,
                Choose = index => Change(item, s => s.drone = index == 1),
            };
        }

        private static void Change(OrbitersAttachment item, Action<Settings> change)
        {
            if (!IsItem(item)) throw new InvalidOperationException("VRCLens is no longer on this avatar.");
            var root = item.transform.parent;
            var avatar = root.GetComponentInChildren<MyAvatar>(true);
            if (avatar && AccessoryService.Busy(avatar)) throw new InvalidOperationException("My Avatar is still adding an accessory to this avatar: try again when it is done.");
            var settings = Current(item);
            change(settings);
            var updated = Apply(root, settings);
            if (avatar) AccessoryService.Status(avatar, Summary(updated, true), false, avatar.accessoryNotes);
        }

        // ---- VRCLens's own setup, reached by name ------------------------------------------------------------------

        private static void Set(object target, string field, object value)
        {
            var info = target.GetType().GetField(field, BindingFlags.Public | BindingFlags.Instance)
                       ?? throw new MissingFieldException("This VRCLens has no “" + field + "” setting My Avatar knows: update My Avatar or VRCLens.");
            info.SetValue(target, info.FieldType.IsEnum && value is int number ? Enum.ToObject(info.FieldType, number) : value);
        }

        private static object Call(object target, string method) => Invoke(target, method, BindingFlags.Public | BindingFlags.Instance);

        private static object CallPrivate(object target, string method) => Invoke(target, method, BindingFlags.NonPublic | BindingFlags.Instance);

        private static object Invoke(object target, string method, BindingFlags flags)
        {
            var info = target.GetType().GetMethod(method, flags, null, Type.EmptyTypes, null)
                       ?? throw new MissingMethodException("This VRCLens has no “" + method + "” step My Avatar knows: update My Avatar or VRCLens.");
            try { return info.Invoke(target, null); }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                Debug.LogException(ex.InnerException);
                throw new InvalidOperationException("VRCLens's " + method + " failed: " + ex.InnerException.Message, ex.InnerException);
            }
        }

        // What VRCLens's setup reads and writes on the descriptor, kept aside while it works on an avatar without them.
        private sealed class DescriptorState
        {
            private readonly bool customExpressions, customizeLayers;
            private readonly VRCExpressionsMenu menu;
            private readonly VRCExpressionParameters parameters;
            private readonly VRCAvatarDescriptor.CustomAnimLayer[] baseLayers, specialLayers;

            public DescriptorState(VRCAvatarDescriptor descriptor)
            {
                customExpressions = descriptor.customExpressions;
                customizeLayers = descriptor.customizeAnimationLayers;
                menu = descriptor.expressionsMenu;
                parameters = descriptor.expressionParameters;
                baseLayers = descriptor.baseAnimationLayers;
                specialLayers = descriptor.specialAnimationLayers;
            }

            public void Clear(VRCAvatarDescriptor descriptor)
            {
                descriptor.customExpressions = false;
                descriptor.expressionsMenu = null;
                descriptor.expressionParameters = null;
                descriptor.customizeAnimationLayers = false;
                descriptor.baseAnimationLayers = new[]
                {
                    Layer(VRCAvatarDescriptor.AnimLayerType.Base), Layer(VRCAvatarDescriptor.AnimLayerType.Additive), Layer(VRCAvatarDescriptor.AnimLayerType.Gesture),
                    Layer(VRCAvatarDescriptor.AnimLayerType.Action), Layer(VRCAvatarDescriptor.AnimLayerType.FX),
                };
                descriptor.specialAnimationLayers = new[]
                {
                    Layer(VRCAvatarDescriptor.AnimLayerType.Sitting), Layer(VRCAvatarDescriptor.AnimLayerType.TPose), Layer(VRCAvatarDescriptor.AnimLayerType.IKPose),
                };
            }

            /// <summary>The FX controller, menu and parameters VRCLens wrote in place of the avatar's.</summary>
            public (RuntimeAnimatorController, VRCExpressionsMenu, VRCExpressionParameters) Instance(VRCAvatarDescriptor descriptor)
            {
                if (ReferenceEquals(descriptor.expressionsMenu, menu) && ReferenceEquals(descriptor.baseAnimationLayers, baseLayers)) return default;
                var fx = (descriptor.baseAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>()).FirstOrDefault(l => l.type == VRCAvatarDescriptor.AnimLayerType.FX);
                return (fx.isDefault ? null : fx.animatorController, descriptor.expressionsMenu, descriptor.expressionParameters);
            }

            public void Restore(VRCAvatarDescriptor descriptor)
            {
                descriptor.customExpressions = customExpressions;
                descriptor.customizeAnimationLayers = customizeLayers;
                descriptor.expressionsMenu = menu;
                descriptor.expressionParameters = parameters;
                descriptor.baseAnimationLayers = baseLayers;
                descriptor.specialAnimationLayers = specialLayers;
            }

            private static VRCAvatarDescriptor.CustomAnimLayer Layer(VRCAvatarDescriptor.AnimLayerType type) =>
                new VRCAvatarDescriptor.CustomAnimLayer { type = type, isDefault = true };
        }
    }
}
