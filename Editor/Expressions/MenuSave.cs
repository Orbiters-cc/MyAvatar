using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.Storage;
using Orbiters.Toolkit.Editor.VRChat.Parameters;
using Orbiters.Toolkit.Storage;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VrcFury = Orbiters.Toolkit.Editor.VRChat.VrcFury;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.Expressions
{
    /// <summary>
    /// Writes menu edits where they belong, in one Undo step:
    /// <list type="bullet">
    /// <item>the avatar's own menu assets are edited (renamed, reordered, given icons, items moved between them), copied first
    /// when they are not the avatar's alone (<see cref="Writer"/>);</item>
    /// <item>the avatar's own VRCFury Toggles and Puppets get their new menu path and icon;</item>
    /// <item>everything else (Full Controller menus, prefabs from packages, folders VRCFury makes for menu paths) is changed
    /// with VRCFury's own Move Menu Item, Set Icon, Reorder Menu Item and Override Menu Settings, on one object at the end
    /// of the avatar, "Menu edits (My Avatar)", rewritten at every Save.</item>
    /// </list>
    /// Items' settings (type, parameter, value; their parameter's default, saved and synced) are written into the avatar's own
    /// menu and parameter assets (copied the same way), or its own VRCFury toggles; what the upload leaves out is saved on its
    /// My Avatar component. The result is checked by building the menu again: the avatar must build exactly the edited menu,
    /// with the edited settings, else nothing is kept.
    /// </summary>
    internal static partial class MenuSave
    {
        public const string EditsName = "Menu edits (My Avatar)";
        public const string CopiesFolder = "Assets/Orbiters/MyAvatar/Menus";
        /// <summary>Told why each way of saving was given up (tests and diagnostics).</summary>
        internal static Action<string> Diagnostics;

        public sealed class Outcome
        {
            public bool Saved;
            public string Message;
            public int Assets, Components, Features;
            /// <summary>Menu and parameters copies, and new menus, made by this save: deleted again when it fails.</summary>
            public readonly List<string> Created = new List<string>();
            /// <summary>Every asset the save wrote or made (menus, copies, imported icons), to be saved to disk with the scene.</summary>
            public readonly HashSet<string> Touched = new HashSet<string>();
            /// <summary>
            /// The ids the save kept for the avatar's menu controls it renamed, copied or made (an item of the avatar's menu is
            /// known by its name): the editor finds the items it showed under their new ids with them.
            /// </summary>
            public IReadOnlyDictionary<VRCExpressionsMenu.Control, string> Pins;
        }

        private sealed class Failure : Exception { public Failure(string message) : base(message) { } }

        /// <summary>The My Avatar component holding what the avatar's upload leaves out of the menu (the editor, the card and the upload read the same one).</summary>
        public static MyAvatar SettingsOf(GameObject root) => root != null ? root.GetComponentInChildren<MyAvatar>(true) : null;

        /// <summary>The object holding My Avatar's saved menu edits, if the avatar has one.</summary>
        public static GameObject EditsObject(GameObject root)
        {
            if (root == null) return null;
            foreach (Transform child in root.transform)
                if (child.name == EditsName && OnlyVrcFury(child.gameObject)) return child.gameObject;
            return null;
        }

        /// <summary>
        /// Whether menu changes of an object put after the saved edits since (Move Menu Item, Reorder Menu Item, Set Icon) now
        /// run after them, and may move, reorder or change the icons of what was saved. Items are created before any of those
        /// run, wherever they are, and the first Override Menu Settings wins: objects with only those change nothing.
        /// </summary>
        public static bool EditsOutrun(GameObject root)
        {
            var edits = EditsObject(root);
            if (edits == null || edits.transform.GetSiblingIndex() == root.transform.childCount - 1) return false;
            var specs = MenuInputs.Read(root).Specs;
            bool Saved(MenuSpec spec) => spec.Component != null && spec.Component.transform.IsChildOf(edits.transform);
            int last = specs.Where(Saved).Select(s => s.Number).DefaultIfEmpty(-1).Max();
            return specs.Any(s => !Saved(s) && s.Number > last && (s is MoveSpec || s is ReorderSpec || s is SetIconSpec));
        }

        // VRCFury allows its root-only features on a child of the avatar holding nothing but VRCFury components.
        private static bool OnlyVrcFury(GameObject go)
        {
            var type = VrcFury.Find("VF.Component.VRCFuryComponent");
            return type != null && go.GetComponentsInChildren<Component>(true).All(c => c == null || c is Transform || type.IsInstanceOfType(c));
        }

        public static Func<Component, bool> SkipEdits(GameObject root)
        {
            var edits = EditsObject(root);
            return c => edits != null && c != null && c.transform.IsChildOf(edits.transform);
        }

        /// <summary>The menu the edits make, from the avatar as it is now.</summary>
        public static MenuDraft Draft(GameObject root, IReadOnlyList<MenuEdit> edits, IReadOnlyDictionary<VRCExpressionsMenu.Control, string> pins, out MenuBuildResult built)
        {
            built = MenuBuild.Run(MenuInputs.Read(root), new MenuBuildOptions { Pins = pins });
            return MenuDraft.Apply(built, edits);
        }

        /// <param name="removals">What the upload leaves out once saved (<see cref="Removals.Of"/> of the editor's draft); null keeps it.</param>
        public static Outcome Save(GameObject root, IReadOnlyList<MenuEdit> edits, MenuEditState state = null, Removals removals = null)
        {
            if (root == null) return new Outcome { Message = "The avatar is no longer open." };
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Save menu");
            var outcome = new Outcome();
            var imported = new List<string>();
            // The menu the avatar builds now: the first attempt starts from it (the others start again after an Undo).
            var now = MenuBuild.Run(MenuInputs.Read(root));
            // Removals no item answers to now (an accessory's, while it is left out of the build) are kept as they are.
            var kept = removals != null ? Unresolved(root, now) : null;
            // What the avatar shares is looked up once for every attempt.
            var sharing = new Lazy<Sharing>(() => new Sharing(root));
            // Settings that change the synced bits are checked against the budget the avatar has now.
            bool budgeted = edits.Any(e => e.kind == MenuEditKind.Synced || e.kind == MenuEditKind.Type || e.kind == MenuEditKind.Parameter);
            var budget = budgeted ? AvatarParameterBudget.Estimate(root) : default;
            try
            {
                var list = ImportFiles(edits, imported);
                // The avatar's own assets and components first; when VRCFury would then build something else, more is left to
                // VRCFury's features, and at last everything.
                string first = null, last = null;
                foreach (var strategy in new[] { Strategy.Direct, Strategy.Careful, Strategy.Features })
                {
                    string problem = TrySave(root, list, strategy, outcome, now, sharing, budgeted ? budget : (ParameterBudget?)null);
                    now = null;
                    if (problem == null)
                    {
                        if (removals != null) WriteRemovals(root, removals, kept);
                        outcome.Touched.UnionWith(imported);
                        outcome.Touched.UnionWith(outcome.Created);
                        state?.Clear("Save menu");
                        // Pending records join the group now, so Ctrl+Z undoes the whole save at once.
                        Undo.FlushUndoRecordObjects();
                        Undo.CollapseUndoOperations(group);
                        Undo.IncrementCurrentGroup();
                        outcome.Saved = true;
                        // Copies, new menus and pictures this save made are generated files: one an Undo of the save leaves
                        // unused goes (kept aside for the session: a Redo brings it back). Not for an avatar of a preview scene
                        // (tests, hidden copies): what made it cleans up.
                        if (!UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(root.scene)) GeneratedAssets.Track(imported.Concat(outcome.Created));
                        return outcome;
                    }
                    first ??= problem;
                    last = problem;
                    Diagnostics?.Invoke(strategy + ": " + problem);
                    Undo.FlushUndoRecordObjects();
                    Undo.RevertAllDownToGroup(group);
                    Delete(outcome);
                    outcome = new Outcome();
                    Undo.IncrementCurrentGroup();
                    group = Undo.GetCurrentGroup();
                    Undo.SetCurrentGroupName("Save menu");
                }
                Delete(imported);
                // The last way tried (VRCFury's features only) says best what can't be done, unless VRCFury is missing.
                return new Outcome { Message = last != null && VrcFury.Component != null ? last : first };
            }
            catch (Exception ex)
            {
                Undo.FlushUndoRecordObjects();
                Undo.RevertAllDownToGroup(group);
                Delete(outcome);
                Delete(imported);
                if (ex is Failure) return new Outcome { Message = ex.Message };
                Debug.LogException(ex);
                return new Outcome { Message = "The menu could not be saved: " + ex.Message };
            }
        }

        /// <summary>How much Save writes into the avatar's own assets and components before VRCFury's features do the rest.</summary>
        private enum Strategy
        {
            /// <summary>Menu assets and the user's toggles, rewriting toggle paths into renamed folders.</summary>
            Direct,
            /// <summary>Only what no menu path names.</summary>
            Careful,
            /// <summary>Nothing: VRCFury's features only (items' settings still go into the avatar's own assets).</summary>
            Features,
        }

        private static void Delete(Outcome outcome)
        {
            Delete(outcome.Created);
            outcome.Created.Clear();
        }

        private static void Delete(List<string> paths)
        {
            foreach (var path in paths) AssetDatabase.DeleteAsset(path);
            paths.Clear();
        }

        // One attempt: the reason it failed, or null when the avatar now builds the edited menu with the edited settings.
        // <paramref name="now"/> is the menu the avatar builds as the attempt starts, when known.
        private static string TrySave(GameObject root, List<MenuEdit> edits, Strategy strategy, Outcome outcome, MenuBuildResult now, Lazy<Sharing> sharing, ParameterBudget? budget)
        {
            try
            {
                now ??= MenuBuild.Run(MenuInputs.Read(root));
                var pins = new Dictionary<VRCExpressionsMenu.Control, string>(now.ControlIds);
                // Without pins, the ids are the ones pinned: the menu as it builds now.
                var draft = MenuDraft.Apply(now, edits);
                var writer = new Writer(root, pins, outcome, sharing);
                outcome.Pins = pins;
                bool vrcFuryInstalled = VrcFury.Component != null;
                if (strategy != Strategy.Features)
                {
                    var skip = SkipEdits(root);
                    var start = Snapshot(root, skip, pins);
                    var refs = References(MenuInputs.Read(root, skip), start, ownToggles: strategy == Strategy.Careful);
                    var made = new Dictionary<string, (VRCExpressionsMenu Menu, VRCExpressionsMenu.Control Control)>();
                    Materialize(root, draft, made, pins);
                    if (EnsureWritable(root, draft, start, refs, writer))
                    {
                        draft = Draft(root, edits, pins, out _);
                        Materialize(root, draft, made, pins);
                    }
                    WriteAssets(root, draft, start, refs, outcome, writer, made.Values.Select(m => m.Menu).ToList());
                    if (vrcFuryInstalled) WriteComponents(root, draft, Snapshot(root, skip, pins), outcome);
                }
                WriteSettings(root, edits, draft, writer, outcome);
                if (budget != null)
                {
                    // The same rule as the editor's: over VRChat's synced bits once VRCFury compressed what it can.
                    var after = AvatarParameterBudget.Estimate(root);
                    if (after.OverBudget && (!budget.Value.OverBudget || after.BuiltBits > budget.Value.BuiltBits))
                        throw new Failure("These settings need " + after.BuiltBits + " synced bits, more than VRChat's " + ParameterBudget.MaxSyncedBits + ": sync fewer parameters.");
                }
                var specs = Features(root, draft, pins);
                if (specs.Count > 0 && !vrcFuryInstalled) throw new Failure("These changes need VRCFury: add it to the project with the Creator Companion.");
                WriteEditsObject(root, specs);
                outcome.Features = specs.Count;
                var built = MenuBuild.Run(MenuInputs.Read(root), new MenuBuildOptions { Pins = pins });
                string difference = Difference(draft.Root, built.Root);
                if (difference == null && (built.NextText != draft.NextText || built.NextIcon != draft.NextIcon)) difference = "the “Next” items";
                if (difference != null) return "VRCFury would build a different menu (" + difference + ").";
                string settings = SettingsDifference(root, edits, draft, built);
                return settings == null ? null : "The settings saved are not the ones edited (" + settings + ").";
            }
            catch (Failure failure) { return failure.Message; }
        }

        // ---- Icons -----------------------------------------------------------------------------------------------------------

        // Image files become textures in the project now (a picture imported before is used again); edits refer to them from
        // here on. The files this import made are listed in <paramref name="created"/> (removed again when the save fails).
        private static List<MenuEdit> ImportFiles(IReadOnlyList<MenuEdit> edits, List<string> created)
        {
            var imported = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
            var list = new List<MenuEdit>();
            foreach (var edit in edits)
            {
                var copy = edit.Copy();
                if (copy.icon == null && !string.IsNullOrEmpty(copy.file))
                {
                    if (!imported.TryGetValue(copy.file, out var texture))
                    {
                        imported[copy.file] = texture = MenuIcons.Import(copy.file, out bool made);
                        if (texture == null) throw new Failure("The picture “" + System.IO.Path.GetFileName(copy.file) + "” is no longer there: choose it again.");
                        if (made) created.Add(AssetDatabase.GetAssetPath(texture));
                    }
                    copy.icon = texture; copy.file = null;
                }
                list.Add(copy);
            }
            return list;
        }

        // ---- What the avatar builds before the moves ------------------------------------------------------------------------

        // Each item's path (folder names) once every item is created and before any is moved: where menu paths find them.
        private sealed class Start
        {
            public readonly Dictionary<string, List<string>> Paths = new Dictionary<string, List<string>>();
            public readonly Dictionary<string, MenuTree.Entry> Entries = new Dictionary<string, MenuTree.Entry>();
            public MenuPage Root;
        }

        private static Start Snapshot(GameObject root, Func<Component, bool> skip, IReadOnlyDictionary<VRCExpressionsMenu.Control, string> pins)
        {
            var start = new Start();
            var inputs = MenuInputs.Read(root, skip);
            void Capture(MenuPage page)
            {
                start.Root = page;
                var parents = new Dictionary<MenuPage, List<string>> { [page] = new List<string>() };
                foreach (var entry in MenuTree.Walk(page))
                {
                    var path = new List<string>(parents[entry.Parent]) { entry.Node.Name };
                    if (!start.Paths.ContainsKey(entry.Node.Id)) { start.Paths[entry.Node.Id] = path; start.Entries[entry.Node.Id] = entry; }
                    if (entry.Node.IsFolder && entry.Node.Page != null && !parents.ContainsKey(entry.Node.Page)) parents[entry.Node.Page] = path;
                }
            }
            var result = MenuBuild.Run(inputs, new MenuBuildOptions { Pins = pins, Inspect = (stage, builder) => { if (stage == MenuBuildStage.BeforeMoves) Capture(MenuTree.Copy(builder.Root)); } });
            if (start.Root == null) Capture(result.Root);
            return start;
        }

        // Items other features name by path (once every item is created, before the moves): such an item keeps its name and
        // place, or the path would miss it. A path's folders are found one by one (".dup.N" picking among same-named ones);
        // its last name finds every item called so (Move, Set Icon, Reorder), or none when it names a new item (Toggle).
        private static HashSet<string> References(MenuInputs inputs, Start start, bool ownToggles = true)
        {
            var ids = new HashSet<string>();
            void Add(string path, bool leaf = true, bool folder = false)
            {
                var split = MenuRules.SplitPath(path);
                var page = start.Root;
                for (int i = 0; i < split.Count && page != null; i++)
                {
                    string name = split[i];
                    bool last = i == split.Count - 1;
                    if (last && !folder)
                    {
                        if (leaf) foreach (var node in MenuRules.Find(page, name)) ids.Add(node.Id);
                        break;
                    }
                    int offset = 0, dup = name.IndexOf(".dup.", StringComparison.Ordinal);
                    if (dup >= 0 && int.TryParse(name.Substring(dup + 5), out offset)) name = name.Substring(0, dup);
                    else offset = 0;
                    var folders = MenuRules.Find(page, name, MenuRules.IsFolder);
                    if (offset >= folders.Count) break;
                    ids.Add(folders[offset].Id);
                    page = folders[offset].Page;
                }
            }
            void Submenus(List<string> prefix, VRCExpressionsMenu menu, HashSet<VRCExpressionsMenu> seen)
            {
                if (menu == null || !seen.Add(menu)) return;
                foreach (var control in menu.controls)
                {
                    if (control == null || control.type != VRCExpressionsMenu.Control.ControlType.SubMenu || control.subMenu == null) continue;
                    var path = new List<string>(prefix) { control.name ?? "" };
                    Add(MenuRules.JoinPath(path), folder: true);
                    Submenus(path, control.subMenu, seen);
                }
            }
            foreach (var spec in inputs.Specs)
                switch (spec)
                {
                    // The user's own toggles and puppets get their new path written too: their paths don't hold assets back.
                    case ToggleSpec t: if (t.AddItem && (ownToggles || !Rewritable(t))) Add(t.Path, leaf: false); break;
                    case PuppetSpec p: if (p.AddItem && (ownToggles || !Rewritable(p))) Add(p.Path, leaf: false); break;
                    case GestureLockSpec g: foreach (var path in g.Paths) Add(path, leaf: false); break;
                    case FullControllerSpec f:
                        // A merged menu's submenus merge into folders of the same name: those names are references too.
                        foreach (var (menu, prefix) in f.Menus)
                        {
                            Add(prefix, folder: true);
                            Submenus(MenuRules.SplitPath(prefix).ToList(), menu, new HashSet<VRCExpressionsMenu>());
                        }
                        break;
                    case MoveSpec m: Add(m.From); Add(m.To); break;
                    case SetIconSpec s: Add(s.Path); break;
                    case ReorderSpec r: Add(r.Path); break;
                    case SpsOptionsSpec o: Add(o.MenuPath); break;
                    case SecurityLockSpec _: Add("Security"); break;
                }
            Add("Holes"); Add("Sockets"); Add("SPS");
            // SPS creates its toggles by path too.
            foreach (var item in MenuBuild.SpsItems(inputs, inputs.Specs.OfType<SpsOptionsSpec>().FirstOrDefault())) Add(item.Path, leaf: false);
            return ids;
        }

        // ---- The avatar's menu assets ---------------------------------------------------------------------------------------

        // The asset a page of the draft shows: the descriptor's menu for the main menu, a submenu asset for its folders.
        private static Dictionary<MenuPage, VRCExpressionsMenu> AssetPages(GameObject root, MenuDraft draft)
        {
            var map = new Dictionary<MenuPage, VRCExpressionsMenu>();
            var menu = MenuInputs.DescriptorMenu(root.GetComponent<VRCAvatarDescriptor>());
            if (menu == null) return map;
            map[draft.Root] = menu;
            foreach (var entry in MenuTree.Walk(draft.Root))
            {
                var node = entry.Node;
                if (node.IsFolder && node.Page != null && node.Origin?.Kind == MenuOriginKind.AvatarMenu && node.Origin.Control?.subMenu != null && !map.ContainsKey(node.Page))
                    map[node.Page] = node.Origin.Control.subMenu;
            }
            return map;
        }

        private static bool AssetItem(MenuNode node) => node.Origin?.Kind == MenuOriginKind.AvatarMenu && node.Origin.Control != null && node.Origin.Asset != null;

        // How each of the avatar's own menu items is saved: written into the assets, or left to VRCFury's features.
        private sealed class AssetPlan
        {
            public readonly Dictionary<VRCExpressionsMenu, List<VRCExpressionsMenu.Control>> Lists = new Dictionary<VRCExpressionsMenu, List<VRCExpressionsMenu.Control>>();
            public readonly Dictionary<VRCExpressionsMenu.Control, (string Name, Texture2D Icon)> Looks = new Dictionary<VRCExpressionsMenu.Control, (string, Texture2D)>();
        }

        private static AssetPlan PlanAssets(GameObject root, MenuDraft draft, Start start, HashSet<string> refs)
        {
            var plan = new AssetPlan();
            var pages = AssetPages(root, draft);
            if (pages.Count == 0) return plan;
            var assets = new HashSet<VRCExpressionsMenu>(pages.Values);
            var homes = new Dictionary<VRCExpressionsMenu.Control, VRCExpressionsMenu>();
            var placed = new HashSet<VRCExpressionsMenu.Control>();
            foreach (var asset in assets) plan.Lists[asset] = new List<VRCExpressionsMenu.Control>();
            foreach (var entry in MenuTree.Walk(draft.Root))
            {
                var node = entry.Node;
                if (!AssetItem(node) || !assets.Contains(node.Origin.Asset)) continue;
                bool free = start.Paths.ContainsKey(node.Id) && !refs.Contains(node.Id);
                bool moved = !pages.TryGetValue(entry.Parent, out var target) || target != node.Origin.Asset;
                if (moved && (!free || target == null || placed.Contains(node.Origin.Control))) continue;
                if (placed.Add(node.Origin.Control)) plan.Lists[target].Add(node.Origin.Control);
                if (free) plan.Looks[node.Origin.Control] = (node.Name, node.Icon);
            }
            // Items saved with features stay in their asset, after the others.
            foreach (var asset in assets)
                foreach (var control in asset.controls)
                    if (control != null && placed.Add(control)) plan.Lists[asset].Add(control);
            return plan;
        }

        private static bool Changes(AssetPlan plan, VRCExpressionsMenu asset)
        {
            var list = plan.Lists[asset];
            if (list.Count != asset.controls.Count || list.Where((c, i) => !ReferenceEquals(c, asset.controls[i])).Any()) return true;
            return list.Any(c => plan.Looks.TryGetValue(c, out var look) && (c.name != look.Name || c.icon != look.Icon));
        }

        // Assets the save changes that are not the avatar's alone are copied first (with their parents up to the descriptor).
        private static bool EnsureWritable(GameObject root, MenuDraft draft, Start start, HashSet<string> refs, Writer writer)
        {
            var plan = PlanAssets(root, draft, start, refs);
            bool copied = false;
            foreach (var asset in plan.Lists.Keys.ToList())
                if (Changes(plan, asset) && writer.Own(asset) != asset) copied = true;
            return copied;
        }

        private static void WriteAssets(GameObject root, MenuDraft draft, Start start, HashSet<string> refs, Outcome outcome, Writer writer, List<VRCExpressionsMenu> fresh)
        {
            var plan = PlanAssets(root, draft, start, refs);
            var changed = plan.Lists.Keys.Where(a => Changes(plan, a)).ToList();
            foreach (var asset in changed)
                if (!writer.Writable(asset)) throw new Failure("“" + asset.name + "” can't be edited.");
            CreateMenus(root, plan, fresh, outcome);
            // Every asset is recorded before any changes: a control moving between two assets is one object, and Undo must
            // see it as it was in both.
            if (changed.Count > 0) Undo.RecordObjects(changed.ToArray<Object>(), "Save menu");
            foreach (var asset in changed)
            {
                asset.controls = plan.Lists[asset];
                foreach (var control in asset.controls)
                    if (control != null && plan.Looks.TryGetValue(control, out var look)) { control.name = look.Name; control.icon = look.Icon; }
                EditorUtility.SetDirty(asset);
                outcome.Assets++;
                Touch(outcome, asset);
            }
        }

        // ---- Folders made in the editor ----------------------------------------------------------------------------------------

        /// <summary>
        /// New folders placed in one of the avatar's menu assets become a submenu control there, opening a new menu (made
        /// in the same Undo-less way as copies, when written). The control keeps the folder's id, so the menu then built from
        /// the assets is the edited one. Folders elsewhere are left to VRCFury's Move Menu Item (<see cref="Adopt"/>).
        /// </summary>
        private static void Materialize(GameObject root, MenuDraft draft, Dictionary<string, (VRCExpressionsMenu Menu, VRCExpressionsMenu.Control Control)> made,
            Dictionary<VRCExpressionsMenu.Control, string> pins)
        {
            for (bool again = true; again;)
            {
                again = false;
                var pages = AssetPages(root, draft);
                foreach (var entry in MenuTree.Walk(draft.Root))
                {
                    var node = entry.Node;
                    if (node.Origin?.Kind != MenuOriginKind.NewFolder || !pages.TryGetValue(entry.Parent, out var parent)) continue;
                    if (!made.TryGetValue(node.Id, out var pair))
                    {
                        var menu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
                        menu.controls = new List<VRCExpressionsMenu.Control>();
                        var control = new VRCExpressionsMenu.Control
                        {
                            type = VRCExpressionsMenu.Control.ControlType.SubMenu, subMenu = menu, value = 1,
                            parameter = new VRCExpressionsMenu.Control.Parameter { name = "" },
                            subParameters = Array.Empty<VRCExpressionsMenu.Control.Parameter>(), labels = Array.Empty<VRCExpressionsMenu.Control.Label>(),
                        };
                        made[node.Id] = pair = (menu, control);
                    }
                    pair.Menu.name = node.Name;
                    pair.Control.name = node.Name;
                    pair.Control.icon = node.Icon;
                    pins[pair.Control] = node.Id;
                    node.Origin = new MenuOrigin { Kind = MenuOriginKind.AvatarMenu, Asset = parent, Index = -1, Control = pair.Control };
                    // A new folder inside this one is placed on the next pass.
                    again = true;
                    break;
                }
            }
        }

        // The new menus a save writes become assets: next to the menu holding them, else with My Avatar's menu copies. One
        // holding only items VRCFury's features move into it is one too (its menu holds it whatever is in it). A menu held by
        // a menu that is not an asset (a test avatar's) stays in memory like it.
        private static void CreateMenus(GameObject root, AssetPlan plan, List<VRCExpressionsMenu> fresh, Outcome outcome)
        {
            VRCExpressionsMenu Holder(VRCExpressionsMenu menu) => plan.Lists.FirstOrDefault(l => l.Value.Any(c => c != null && c.subMenu == menu)).Key;
            var waiting = fresh.Where(m => m != null && !EditorUtility.IsPersistent(m) && Holder(m) != null).Distinct().ToList();
            for (int pass = 0; pass < 8 && waiting.Count > 0; pass++)
                foreach (var menu in waiting.ToList())
                {
                    var holder = Holder(menu);
                    if (fresh.Contains(holder) && waiting.Contains(holder)) continue;
                    waiting.Remove(menu);
                    string holderPath = AssetDatabase.GetAssetPath(holder);
                    if (string.IsNullOrEmpty(holderPath)) continue;
                    string folder = holderPath.StartsWith("Assets/", StringComparison.Ordinal) && AssetDatabase.IsOpenForEdit(holderPath)
                        ? System.IO.Path.GetDirectoryName(holderPath).Replace('\\', '/')
                        : Folder(CopiesFolder + "/" + SafePaths.FileName(root.name, "Avatar"));
                    string path = NewAssetPath(folder, menu.name, "Menu");
                    AssetDatabase.CreateAsset(menu, path);
                    outcome.Created.Add(path);
                    outcome.Touched.Add(path);
                }
        }

        /// <summary>
        /// New folders VRCFury's Move Menu Item made (moving their items to their path) take the id of the folder made, so the
        /// moves, the reorders and the check that follow find them.
        /// </summary>
        private static void Adopt(MenuBuilder builder, MenuDraft draft)
        {
            var draftIds = new HashSet<string>(MenuTree.Walk(draft.Root).Select(e => e.Node.Id));
            var live = new Dictionary<MenuPage, MenuPage> { [draft.Root] = builder.Root };
            foreach (var entry in MenuTree.Walk(draft.Root))
            {
                var node = entry.Node;
                if (!node.IsFolder || node.Page == null || live.ContainsKey(node.Page) || !live.TryGetValue(entry.Parent, out var here) || here == null) continue;
                var found = here.Items.FirstOrDefault(n => n.Id == node.Id && n.IsFolder);
                if (found == null && node.Origin?.Kind == MenuOriginKind.NewFolder)
                {
                    found = here.Items.FirstOrDefault(n => n.IsFolder && n.Name == node.Name && !draftIds.Contains(n.Id));
                    if (found != null) { draftIds.Remove(node.Id); node.Id = found.Id; draftIds.Add(found.Id); }
                }
                if (found?.Page != null) live[node.Page] = found.Page;
            }
        }

        private static void Touch(Outcome outcome, Object asset)
        {
            string path = AssetDatabase.GetAssetPath(asset);
            if (!string.IsNullOrEmpty(path)) outcome.Touched.Add(path);
        }

        // ---- Copies before writing ----------------------------------------------------------------------------------------------

        /// <summary>
        /// One save's way into the avatar's own menu and parameters assets: one that is not the avatar's alone (in a package,
        /// or used by another avatar, a prefab, another scene, another menu or a Full Controller) is copied into the avatar's
        /// folder of <see cref="CopiesFolder"/> first and the copy put in its place (with the menus holding it, up to the
        /// descriptor). Structural edits and settings both go through it.
        /// </summary>
        private sealed class Writer
        {
            private readonly GameObject root;
            private readonly VRCAvatarDescriptor descriptor;
            private readonly Dictionary<VRCExpressionsMenu.Control, string> pins;
            private readonly Outcome outcome;
            private readonly Dictionary<Object, Object> copies = new Dictionary<Object, Object>();
            private readonly Dictionary<VRCExpressionsMenu, List<VRCExpressionsMenu>> parents;
            private readonly Lazy<Sharing> sharing;

            public Writer(GameObject root, Dictionary<VRCExpressionsMenu.Control, string> pins, Outcome outcome, Lazy<Sharing> sharing)
            {
                this.root = root;
                this.pins = pins;
                this.outcome = outcome;
                this.sharing = sharing;
                descriptor = root.GetComponent<VRCAvatarDescriptor>();
                parents = Parents(descriptor != null ? descriptor.expressionsMenu : null);
            }

            private Sharing Sharing => sharing.Value;

            /// <summary>Whether the save may write <paramref name="asset"/> as it is.</summary>
            public bool Writable(Object asset)
            {
                if (asset == null) return false;
                // Its own copies, and assets it found writable already.
                if (copies.TryGetValue(asset, out var known) && known == asset) return true;
                string path = AssetDatabase.GetAssetPath(asset);
                if (path.Length > 0 && (!path.StartsWith("Assets/", StringComparison.Ordinal) || !AssetDatabase.IsOpenForEdit(path))) return false;
                return !Sharing.Shared(asset);
            }

            /// <summary>The menu to write instead of <paramref name="asset"/>: itself, or its copy (made now, its controls keeping their ids).</summary>
            public VRCExpressionsMenu Own(VRCExpressionsMenu asset)
            {
                if (copies.TryGetValue(asset, out var done)) return (VRCExpressionsMenu)done;
                if (Writable(asset)) { copies[asset] = asset; return asset; }
                var copy = Object.Instantiate(asset);
                copy.name = asset.name;
                CreateCopy(copy, asset.name, "Menu");
                for (int i = 0; i < asset.controls.Count && i < copy.controls.Count; i++)
                    if (asset.controls[i] != null && pins.TryGetValue(asset.controls[i], out var id)) pins[copy.controls[i]] = id;
                copies[asset] = copies[copy] = copy;
                if (descriptor != null && descriptor.expressionsMenu == asset)
                {
                    Undo.RecordObject(descriptor, "Save menu");
                    descriptor.expressionsMenu = copy;
                    EditorUtility.SetDirty(descriptor);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(descriptor);
                }
                if (parents.TryGetValue(asset, out var holders))
                    foreach (var parent in holders.ToList())
                    {
                        var owned = Own(parent);
                        Undo.RecordObject(owned, "Save menu");
                        foreach (var control in owned.controls) if (control != null && control.subMenu == asset) control.subMenu = copy;
                        EditorUtility.SetDirty(owned);
                        Touch(outcome, owned);
                    }
                return copy;
            }

            /// <summary>The avatar's parameters asset to write: its own, or a copy put in its place (the rule My Avatar's looks share, <see cref="AvatarParameters"/>).</summary>
            public VRCExpressionParameters OwnParameters()
            {
                var parameters = descriptor != null ? descriptor.expressionParameters : null;
                if (parameters == null) throw new Failure("The avatar has no expression parameters.");
                if (copies.TryGetValue(parameters, out var done)) return (VRCExpressionParameters)done;
                var own = AvatarParameters.Own(descriptor, "Save menu", out string created);
                copies[parameters] = copies[own] = own;
                if (created != null) { outcome.Created.Add(created); outcome.Assets++; }
                return own;
            }

            private void CreateCopy(Object copy, string name, string fallback)
            {
                string path = NewAssetPath(Folder(CopiesFolder + "/" + SafePaths.FileName(root.name, "Avatar")), name, fallback);
                AssetDatabase.CreateAsset(copy, path);
                outcome.Created.Add(path);
                outcome.Assets++;
            }

            /// <summary>The menu as the avatar builds it now, by id (copies keep their controls' ids).</summary>
            public Dictionary<string, MenuTree.Entry> Live() => MenuTree.Index(MenuBuild.Run(MenuInputs.Read(root), new MenuBuildOptions { Pins = pins }).Root);
        }

        /// <summary>
        /// The menus and parameters the avatar shares with something else, so writing them would change that too: other
        /// avatars, VRCFury Full Controllers and Modular Avatar Menu Installers in the open scenes (this avatar's own too: they
        /// merge their menu as it is), and any prefab, scene or menu of the project using them, besides the avatar's own scene
        /// and menus.
        /// </summary>
        private sealed class Sharing
        {
            private static readonly string[] Users = { "t:Prefab", "t:Scene", "t:VRCExpressionsMenu" };
            private readonly HashSet<Object> used = new HashSet<Object>();
            private readonly HashSet<string> own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly List<string> candidates = new List<string>();
            private readonly Dictionary<VRCExpressionsMenu, List<VRCExpressionsMenu>> parents;
            private readonly Dictionary<Object, bool> known = new Dictionary<Object, bool>();
            private readonly string ownPrefab;
            private Dictionary<string, List<string>> users;

            public Sharing(GameObject root)
            {
                for (int s = 0; s < UnityEngine.SceneManagement.SceneManager.sceneCount; s++)
                {
                    var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(s);
                    if (!scene.isLoaded) continue;
                    foreach (var top in scene.GetRootGameObjects())
                    foreach (var other in top.GetComponentsInChildren<VRCAvatarDescriptor>(true))
                    {
                        if (other.gameObject == root) continue;
                        AddTree(other.expressionsMenu, used);
                        if (other.expressionParameters != null) used.Add(other.expressionParameters);
                        Merged(other.gameObject);
                    }
                }
                Merged(root);
                // The avatar's own scene and menus use them without sharing them.
                var descriptor = root.GetComponent<VRCAvatarDescriptor>();
                own.Add(root.scene.path ?? "");
                var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetPrefabStage(root);
                if (stage != null) own.Add(stage.assetPath);
                // The prefab the avatar is an instance of uses the copies My Avatar made for it once its overrides are applied:
                // those stay the avatar's own (else every save would copy them again). Anything else that prefab uses (the
                // creator's menus) is shared with its other instances and variants, and copied once.
                if (PrefabUtility.IsPartOfPrefabInstance(root)) ownPrefab = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root);
                foreach (var menu in MenuPaths.Menus(descriptor != null ? descriptor.expressionsMenu : null))
                {
                    string path = AssetDatabase.GetAssetPath(menu);
                    if (path.Length > 0) { own.Add(path); candidates.Add(path); }
                }
                if (descriptor != null && descriptor.expressionParameters != null) candidates.Add(AssetDatabase.GetAssetPath(descriptor.expressionParameters));
                parents = Parents(descriptor != null ? descriptor.expressionsMenu : null);
            }

            // What an avatar's VRCFury Full Controllers and Modular Avatar Menu Installers merge.
            private void Merged(GameObject avatar)
            {
                foreach (var (_, feature, kind) in VrcFury.Features(avatar))
                {
                    if (kind != "FullController") continue;
                    if (VrcFury.Field(feature, "menus") is System.Collections.IEnumerable menus)
                        foreach (var entry in menus) AddTree(VrcFury.ObjectReference(VrcFury.Field(entry, "menu")) as VRCExpressionsMenu, used);
                    if (VrcFury.Field(feature, "prms") is System.Collections.IEnumerable prms)
                        foreach (var entry in prms)
                            if (VrcFury.ObjectReference(VrcFury.Field(entry, "parameters")) is VRCExpressionParameters asset) used.Add(asset);
                }
                foreach (var installer in MaMenus.Read(avatar)?.Installers ?? new List<MaInstaller>()) AddTree(installer.Menu, used);
            }

            public bool Shared(Object asset)
            {
                if (asset == null) return false;
                if (known.TryGetValue(asset, out bool shared)) return shared;
                // (A menu holding itself is answered by its other holders.)
                known[asset] = false;
                // Whatever uses a menu holding it reaches it too.
                shared = used.Contains(asset) || UsedByFiles(asset) ||
                         asset is VRCExpressionsMenu menu && parents.TryGetValue(menu, out var holders) && holders.Any(Shared);
                return known[asset] = shared;
            }

            private bool UsedByFiles(Object asset)
            {
                string path = AssetDatabase.GetAssetPath(asset);
                if (path.Length == 0) return false;
                if (users == null || !users.ContainsKey(path))
                {
                    if (!candidates.Contains(path)) candidates.Add(path);
                    users = ProjectReferences.DirectUsers(candidates, Users);
                }
                bool copy = path.StartsWith(CopiesFolder + "/", StringComparison.Ordinal);
                return users.TryGetValue(path, out var list) && list.Any(u => !own.Contains(u) && !(copy && string.Equals(u, ownPrefab, StringComparison.OrdinalIgnoreCase)));
            }
        }

        private static void AddTree(VRCExpressionsMenu menu, HashSet<Object> set)
        {
            if (menu == null || !set.Add(menu)) return;
            foreach (var control in menu.controls ?? new List<VRCExpressionsMenu.Control>()) if (control != null && control.subMenu != null) AddTree(control.subMenu, set);
        }

        // For each submenu asset of the avatar's menu, the menus holding it.
        private static Dictionary<VRCExpressionsMenu, List<VRCExpressionsMenu>> Parents(VRCExpressionsMenu menu)
        {
            var parents = new Dictionary<VRCExpressionsMenu, List<VRCExpressionsMenu>>();
            var seen = new HashSet<VRCExpressionsMenu>();
            void Visit(VRCExpressionsMenu m)
            {
                if (m == null || !seen.Add(m)) return;
                foreach (var control in m.controls ?? new List<VRCExpressionsMenu.Control>())
                {
                    if (control?.subMenu == null) continue;
                    if (!parents.TryGetValue(control.subMenu, out var list)) parents[control.subMenu] = list = new List<VRCExpressionsMenu>();
                    if (!list.Contains(m)) list.Add(m);
                    Visit(control.subMenu);
                }
            }
            Visit(menu);
            return parents;
        }

        private static string Folder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return path;
            string parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
            Folder(parent);
            AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
            return path;
        }

        // A new asset's path in a folder, named after what it holds (as a plain file name), short enough for Unity.
        private static string NewAssetPath(string folder, string name, string fallback)
        {
            string path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + SafePaths.FileName(name, fallback) + ".asset");
            SafePaths.RequireLength(System.IO.Path.GetFullPath(path));
            return path;
        }

        // ---- The avatar's own VRCFury Toggles and Puppets ------------------------------------------------------------------

        private static bool Rewritable(MenuSpec spec) => spec.Component != null && spec.Feature == 0 && VrcFury.Content(spec.Component) != null && UsersOwn(spec.Component);

        // A component the user made in their scene, not one from a package's prefab.
        public static bool UsersOwn(Component component)
        {
            if (component == null || EditorUtility.IsPersistent(component)) return false;
            if (!PrefabUtility.IsPartOfPrefabInstance(component)) return true;
            string path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(component);
            return string.IsNullOrEmpty(path) || path.StartsWith("Assets/", StringComparison.Ordinal);
        }

        private static void WriteComponents(GameObject root, MenuDraft draft, Start start, Outcome outcome)
        {
            var inputs = MenuInputs.Read(root, SkipEdits(root));
            var refs = References(inputs, start);
            var specs = inputs.Specs;
            var own = new Dictionary<Component, MenuSpec>();
            foreach (var spec in specs)
                if ((spec is ToggleSpec || spec is PuppetSpec) && Rewritable(spec)) own[spec.Component] = spec;
            var startPages = new Dictionary<string, List<string>>();
            foreach (var entry in MenuTree.Walk(draft.Root))
            {
                var node = entry.Node;
                var kind = node.Origin?.Kind;
                if (kind == MenuOriginKind.MaItem)
                {
                    if (!start.Paths.ContainsKey(node.Id) || refs.Contains(node.Id)) continue;
                    WriteMenuItem(node, outcome);
                    continue;
                }
                if ((kind != MenuOriginKind.Toggle && kind != MenuOriginKind.Puppet) || node.Origin.Component == null || !own.TryGetValue(node.Origin.Component, out var spec)) continue;
                string declared = spec is ToggleSpec t ? t.Path : ((PuppetSpec)spec).Path;
                if (!start.Paths.ContainsKey(node.Id) || refs.Contains(node.Id)) continue;
                // Where its folder is once every item is created: the toggle declares that path and lands in it.
                List<string> folder;
                if (entry.Folder == null) folder = new List<string>();
                else if (!FolderPath(start, entry.Folder.Id, out folder)) continue;
                string path = string.Join("/", folder.Concat(new[] { MenuRules.Escape(node.Name) }));
                var model = VrcFury.Content(node.Origin.Component);
                bool icon = (VrcFury.Field(model, "enableIcon") as bool? ?? false ? VrcFury.ObjectReference(VrcFury.Field(model, "icon")) as Texture2D : null) != node.Icon;
                if (path == declared && !icon) continue;
                var serialized = new SerializedObject(node.Origin.Component);
                var name = serialized.FindProperty("content.name");
                if (name == null) continue;
                name.stringValue = path;
                if (icon)
                {
                    serialized.FindProperty("content.enableIcon").boolValue = node.Icon != null;
                    var reference = serialized.FindProperty("content.icon.objRef");
                    var id = serialized.FindProperty("content.icon.id");
                    if (reference != null) reference.objectReferenceValue = node.Icon;
                    if (id != null) id.stringValue = "";
                }
                serialized.ApplyModifiedProperties();
                outcome.Components++;
            }
        }

        // A Modular Avatar Menu Item of the user's gets its new name (its label) and icon; moves are left to VRCFury's features.
        private static void WriteMenuItem(MenuNode node, Outcome outcome)
        {
            var component = node.Origin.Component;
            if (component == null || !UsersOwn(component)) return;
            string label = VrcFury.Field(component, "label") as string;
            var control = VrcFury.Field(component, "Control") as VRCExpressionsMenu.Control;
            string name = string.IsNullOrEmpty(label) ? component.gameObject.name : label;
            var icon = control?.icon;
            if (node.Name == name && node.Icon == icon) return;
            var serialized = new SerializedObject(component);
            var labelProperty = serialized.FindProperty("label");
            var iconProperty = serialized.FindProperty("Control.icon");
            if (labelProperty == null || iconProperty == null) return;
            if (node.Name != name) labelProperty.stringValue = node.Name;
            if (node.Icon != icon) iconProperty.objectReferenceValue = node.Icon;
            serialized.ApplyModifiedProperties();
            outcome.Components++;
        }

        // A folder's path for menu paths, with ".dup.N" when an earlier folder of that name would be found first.
        private static bool FolderPath(Start start, string folderId, out List<string> path)
        {
            path = null;
            if (!start.Entries.TryGetValue(folderId, out var entry) || !start.Paths.TryGetValue(folderId, out var names)) return false;
            var parts = new List<string>();
            var page = start.Root;
            foreach (var name in names)
            {
                var folders = MenuRules.Find(page, name, MenuRules.IsFolder);
                var node = page.Items.FirstOrDefault(n => n.IsFolder && n.Name == name && folders.Contains(n));
                int at = node != null ? folders.IndexOf(node) : -1;
                if (at < 0) return false;
                parts.Add(MenuRules.Escape(name) + (at > 0 ? ".dup." + at : ""));
                page = node.Page;
                if (page == null) return false;
            }
            path = parts;
            return true;
        }

        // ---- VRCFury features for the rest ----------------------------------------------------------------------------------

        private static string LivePath(MenuPage root, MenuNode target, bool leaf)
        {
            var chain = new List<(MenuPage Page, MenuNode Node)>();
            bool Find(MenuPage page, HashSet<MenuPage> seen)
            {
                if (page == null || !seen.Add(page)) return false;
                foreach (var node in page.Items)
                {
                    chain.Add((page, node));
                    if (node == target) return true;
                    if (node.IsFolder && Find(node.Page, seen)) return true;
                    chain.RemoveAt(chain.Count - 1);
                }
                return false;
            }
            if (!Find(root, new HashSet<MenuPage>())) throw new Failure("“" + target.Name + "” is no longer in the menu.");
            var parts = new List<string>();
            for (int i = 0; i < chain.Count; i++)
            {
                var (page, node) = chain[i];
                bool last = i == chain.Count - 1;
                if (last && leaf)
                {
                    var same = MenuRules.Find(page, node.Name);
                    if (same.Count != 1 || same[0] != node) throw new Failure("Two items in one menu have the same name, “" + Plain(node.Name) + "”: rename one first.");
                    parts.Add(MenuRules.Escape(node.Name));
                    continue;
                }
                var folders = MenuRules.Find(page, node.Name, MenuRules.IsFolder);
                int at = folders.IndexOf(node);
                if (at < 0) throw new Failure("“" + node.Name + "” can't be found by its name.");
                parts.Add(MenuRules.Escape(node.Name) + (at > 0 ? ".dup." + at : ""));
            }
            return string.Join("/", parts);
        }

        // The features that turn what the avatar builds (after its own direct edits) into the draft.
        private static List<MenuSpec> Features(GameObject root, MenuDraft draft, IReadOnlyDictionary<VRCExpressionsMenu.Control, string> pins)
        {
            var skip = SkipEdits(root);
            var inputs = MenuInputs.Read(root, skip);
            var specs = new List<MenuSpec>();
            int number = inputs.NextNumber;
            var startPaths = new Dictionary<string, string>();
            Failure failure = null;
            var target = MenuTree.Walk(draft.Root);
            var options = new MenuBuildOptions
            {
                Pins = pins,
                Inspect = (stage, builder) =>
                {
                    if (failure != null) return;
                    try
                    {
                        if (stage == MenuBuildStage.BeforeMoves)
                            foreach (var entry in MenuTree.Walk(builder.Root))
                            {
                                try { startPaths[entry.Node.Id] = LivePath(builder.Root, entry.Node, leaf: true); }
                                catch (Failure) { }
                            }
                        else if (stage == MenuBuildStage.BeforeExtraMoves) Moves(builder, target, draft, specs, ref number);
                        else if (stage == MenuBuildStage.BeforeExtraReorders) Reorders(builder, draft, specs, ref number);
                    }
                    catch (Failure f) { failure = f; }
                },
            };
            MenuBuild.Run(inputs, options);
            if (failure != null) throw failure;
            // Icons: named where the item is before any move, when that finds it alone; else where it ends up.
            var icons = new List<MenuSpec>();
            var built = MenuBuild.Run(inputs, new MenuBuildOptions { Pins = pins, Extra = specs });
            var now = MenuTree.Index(built.Root);
            foreach (var entry in target)
            {
                if (!now.TryGetValue(entry.Node.Id, out var current) || current.Node.Icon == entry.Node.Icon) continue;
                string path = startPaths.TryGetValue(entry.Node.Id, out var before) ? before : LivePath(built.Root, current.Node, leaf: true);
                icons.Add(new SetIconSpec { Path = path, Icon = entry.Node.Icon });
            }
            foreach (var icon in icons) icon.Number = number++;
            // Icons go first so VRCFury looks for them before any move.
            specs.InsertRange(0, icons);
            if (draft.NextTextChanged || draft.NextIconChanged)
            {
                var existing = inputs.Specs.OfType<MenuSettingsSpec>().FirstOrDefault();
                if (existing == null) specs.Add(new MenuSettingsSpec { NextText = draft.NextText == "Next" ? "" : draft.NextText, NextIcon = draft.NextIcon, Number = number++ });
                else WriteMenuSettings(existing, draft);
            }
            return specs;
        }

        private static void Moves(MenuBuilder builder, List<MenuTree.Entry> target, MenuDraft draft, List<MenuSpec> specs, ref int number)
        {
            var wantedIndex = new Dictionary<string, MenuTree.Entry>();
            foreach (var entry in target) if (!wantedIndex.ContainsKey(entry.Node.Id)) wantedIndex[entry.Node.Id] = entry;
            // A folder's path for a move's destination: where it is now, or, for a folder made of menu paths that is gone
            // (its toggles were moved), where it will be made again by the first item moved into it.
            string Where(string id, Dictionary<string, MenuTree.Entry> live)
            {
                if (id == MenuTree.RootId) return "";
                if (live.TryGetValue(id, out var here) && here.Node.IsFolder) return LivePath(builder.Root, here.Node, leaf: false);
                if (!wantedIndex.TryGetValue(id, out var wanted) || (wanted.Node.Origin?.Kind != MenuOriginKind.Folder && wanted.Node.Origin?.Kind != MenuOriginKind.NewFolder))
                    throw new Failure("A folder can't be found in what VRCFury builds.");
                string parent = Where(wanted.Folder?.Id ?? MenuTree.RootId, live);
                return (parent.Length > 0 ? parent + "/" : "") + MenuRules.Escape(wanted.Node.Name);
            }
            foreach (var wanted in target)
            {
                var live = MenuTree.Index(builder.Root);
                if (!live.TryGetValue(wanted.Node.Id, out var current))
                {
                    if (wanted.Node.Origin?.Kind == MenuOriginKind.NewFolder)
                    {
                        // Made by moving its items to its path: a folder of that name there already would take them instead.
                        var place = MenuTree.PageOf(builder.Root, wanted.Folder?.Id ?? MenuTree.RootId, live);
                        if (place != null && MenuRules.Find(place, wanted.Node.Name, MenuRules.IsFolder).Count > 0)
                            throw new Failure("A folder named “" + Plain(wanted.Node.Name) + "” is already there: give the new folder another name.");
                        continue;
                    }
                    if (wanted.Node.Origin?.Kind == MenuOriginKind.Folder) continue;
                    throw new Failure("“" + Plain(wanted.Node.Name) + "” can't be found in what VRCFury builds.");
                }
                string wantedFolder = wanted.Folder?.Id ?? MenuTree.RootId, currentFolder = current.Folder?.Id ?? MenuTree.RootId;
                if (wantedFolder == currentFolder && current.Node.Name == wanted.Node.Name) continue;
                var destination = MenuTree.PageOf(builder.Root, wantedFolder, live);
                if (destination != null && current.Node.IsFolder && current.Node.Page != null && MenuRules.Find(destination, wanted.Node.Name, MenuRules.IsFolder).Any(n => n != current.Node))
                    throw new Failure("A folder with the same name, “" + Plain(wanted.Node.Name) + "”, is already there: VRCFury would merge their contents.");
                string from = LivePath(builder.Root, current.Node, leaf: true);
                string folder = Where(wantedFolder, live);
                string to = (folder.Length > 0 ? folder + "/" : "") + MenuRules.Escape(wanted.Node.Name);
                if (MenuTree.OpenedElsewhere(builder.Root, current.Node))
                    throw new Failure("“" + Plain(current.Node.Name) + "” opens the same menu as another folder: VRCFury would copy that menu instead of moving it.");
                if (builder.IntoItself(current.Node, to))
                    throw new Failure("“" + Plain(current.Node.Name) + "” opens the menu it is in, so VRCFury can't rename or move it: it would end up inside itself.");
                if (!builder.Move(from, to)) throw new Failure("“" + Plain(wanted.Node.Name) + "” can't be moved.");
                specs.Add(new MoveSpec { From = from, To = to, Number = number++ });
            }
            Adopt(builder, draft);
            wantedIndex.Clear();
            foreach (var entry in target) if (!wantedIndex.ContainsKey(entry.Node.Id)) wantedIndex[entry.Node.Id] = entry;
            // Folders made of menu paths that end up empty and aren't in the edited menu go, as they would without their items.
            for (bool removed = true; removed;)
            {
                removed = false;
                foreach (var entry in MenuTree.Walk(builder.Root))
                {
                    var folder = entry.Node;
                    if (folder.Origin?.Kind != MenuOriginKind.Folder || !folder.IsFolder || folder.Page == null || folder.Page.Items.Count > 0 || wantedIndex.ContainsKey(folder.Id)) continue;
                    string from = LivePath(builder.Root, folder, leaf: true);
                    if (!builder.Move(from, "")) continue;
                    specs.Add(new MoveSpec { From = from, To = "", Number = number++ });
                    removed = true;
                    break;
                }
            }
        }

        private static void Reorders(MenuBuilder builder, MenuDraft draft, List<MenuSpec> specs, ref int number)
        {
            var folders = new List<(string Id, MenuPage Page)> { (MenuTree.RootId, draft.Root) };
            folders.AddRange(MenuTree.Walk(draft.Root).Where(e => e.Node.IsFolder && e.Node.Page != null).Select(e => (e.Node.Id, e.Node.Page)));
            var done = new HashSet<MenuPage>();
            foreach (var (id, wantedPage) in folders)
            {
                if (!done.Add(wantedPage)) continue;
                var page = MenuTree.PageOf(builder.Root, id);
                if (page == null) throw new Failure("A folder can't be found in what VRCFury builds.");
                var wanted = wantedPage.Items.Select(n => n.Id).ToList();
                var order = page.Items.Select(n => n.Id).ToList();
                if (wanted.SequenceEqual(order)) continue;
                if (wanted.Count != order.Count || wanted.Except(order).Any())
                {
                    string Names(IEnumerable<string> ids, MenuPage from) => string.Join(", ", ids.Select(i => "“" + Plain(from.Items.FirstOrDefault(n => n.Id == i)?.Name) + "”"));
                    var missing = wanted.Except(order).ToList(); var extra = order.Except(wanted).ToList();
                    throw new Failure("VRCFury would build a different menu (" + (missing.Count > 0 ? "missing " + Names(missing, wantedPage) : "") +
                                      (missing.Count > 0 && extra.Count > 0 ? "; " : "") + (extra.Count > 0 ? "also " + Names(extra, page) : "") + ").");
                }
                bool Unique(string item)
                {
                    var node = page.Items.First(n => n.Id == item);
                    return MenuRules.Find(page, node.Name).Count == 1;
                }
                var steps = Steps(order, wanted, Unique) ?? throw new Failure("Two items with the same name in one menu can't change places: rename one first.");
                foreach (var (item, position) in steps)
                {
                    var node = page.Items.First(n => n.Id == item);
                    string path = LivePath(builder.Root, node, leaf: true);
                    builder.Reorder(path, position);
                    specs.Add(new ReorderSpec { Path = path, Position = position, Number = number++ });
                }
            }
        }

        /// <summary>
        /// "Put this item at that place" steps (Reorder Menu Item) turning <paramref name="order"/> into <paramref name="wanted"/>,
        /// as few as it can: fixed from the front, or from the back. Reorder Menu Item finds items by name, so an item sharing
        /// its name with another (<paramref name="movable"/> false) is never moved itself: the items around it are. Null when
        /// that can't give the order (same-named items to swap).
        /// </summary>
        public static List<(string Item, int Position)> Steps(IList<string> order, IList<string> wanted, Func<string, bool> movable = null)
        {
            movable ??= _ => true;
            int n = wanted.Count;
            List<(string, int)> Forward()
            {
                var list = order.ToList(); var steps = new List<(string, int)>();
                for (int i = 0; i < n; i++)
                {
                    int guard = 0;
                    while (list[i] != wanted[i])
                    {
                        if (++guard > n * 2 + 2) return null;
                        if (movable(wanted[i])) { list.Remove(wanted[i]); list.Insert(i, wanted[i]); steps.Add((wanted[i], i)); }
                        else if (movable(list[i])) { var item = list[i]; list.RemoveAt(i); list.Add(item); steps.Add((item, 999)); }
                        else return null;
                    }
                }
                return steps;
            }
            List<(string, int)> Backward()
            {
                if (!wanted.All(movable)) return null;
                var list = order.ToList(); var steps = new List<(string, int)>();
                for (int i = n - 1; i >= 0; i--)
                {
                    if (list[i] == wanted[i]) continue;
                    list.Remove(wanted[i]); list.Insert(i, wanted[i]);
                    steps.Add((wanted[i], i));
                }
                return steps;
            }
            var forward = Forward(); var backward = Backward();
            if (forward == null) return backward;
            return backward != null && backward.Count < forward.Count ? backward : forward;
        }

        private static void WriteMenuSettings(MenuSettingsSpec existing, MenuDraft draft)
        {
            var serialized = new SerializedObject(existing.Component);
            var text = serialized.FindProperty("content.nextText");
            var icon = serialized.FindProperty("content.nextIcon.objRef");
            var id = serialized.FindProperty("content.nextIcon.id");
            if (text != null) text.stringValue = draft.NextText == "Next" ? "" : draft.NextText;
            if (icon != null) icon.objectReferenceValue = draft.NextIcon;
            if (id != null) id.stringValue = "";
            serialized.ApplyModifiedProperties();
        }

        // ---- The edits object -------------------------------------------------------------------------------------------------

        private static void WriteEditsObject(GameObject root, List<MenuSpec> specs)
        {
            var old = EditsObject(root);
            if (old != null) Undo.DestroyObjectImmediate(old);
            if (specs.Count == 0) return;
            var type = VrcFury.Component ?? throw new Failure("These changes need VRCFury.");
            var go = new GameObject(EditsName);
            Undo.RegisterCreatedObjectUndo(go, "Save menu");
            Undo.SetTransformParent(go.transform, root.transform, "Save menu");
            go.transform.localPosition = Vector3.zero; go.transform.localRotation = Quaternion.identity; go.transform.localScale = Vector3.one;
            go.transform.SetAsLastSibling();
            foreach (var spec in specs)
            {
                var component = Undo.AddComponent(go, type);
                type.GetField("content").SetValue(component, Model(spec));
                EditorUtility.SetDirty(component);
            }
        }

        private static object Model(MenuSpec spec)
        {
            object New(string name) => Activator.CreateInstance(VrcFury.Find("VF.Model.Feature." + name) ?? throw new Failure("This VRCFury version has no " + name + "."), true);
            void Set(object model, string field, object value)
            {
                for (var type = model.GetType(); type != null; type = type.BaseType)
                {
                    var f = type.GetField(field, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (f == null) continue;
                    f.SetValue(model, value);
                    return;
                }
                throw new Failure("This VRCFury version has no " + model.GetType().Name + "." + field + ".");
            }
            object Texture(Texture2D icon)
            {
                var wrapper = Activator.CreateInstance(VrcFury.Find("VF.Model.GuidTexture2d"), true);
                Set(wrapper, "objRef", icon);
                Set(wrapper, "id", "");
                return wrapper;
            }
            switch (spec)
            {
                case MoveSpec move: { var m = New("MoveMenuItem"); Set(m, "fromPath", move.From); Set(m, "toPath", move.To); return m; }
                case SetIconSpec icon: { var m = New("SetIcon"); Set(m, "path", icon.Path); Set(m, "icon", Texture(icon.Icon)); return m; }
                case ReorderSpec reorder: { var m = New("ReorderMenuItem"); Set(m, "path", reorder.Path); Set(m, "position", reorder.Position); return m; }
                case MenuSettingsSpec settings: { var m = New("OverrideMenuSettings"); Set(m, "nextText", settings.NextText ?? ""); Set(m, "nextIcon", Texture(settings.NextIcon)); return m; }
            }
            throw new Failure("Unknown feature.");
        }

        private static string Plain(string name) => System.Text.RegularExpressions.Regex.Replace(name ?? "", "<.*?>", "").Replace("\n", " ").Trim();

        // ---- Checking the result ------------------------------------------------------------------------------------------------

        /// <summary>The first difference between two menus (ids, names, types, icons, order, contents), or null when equal.</summary>
        public static string Difference(MenuPage expected, MenuPage actual)
        {
            var seen = new HashSet<MenuPage>();
            string Compare(MenuPage a, MenuPage b, string where)
            {
                if (a == null || b == null) return a == b ? null : where + ": a submenu is missing";
                if (!seen.Add(a)) return null;
                if (a.Items.Count != b.Items.Count)
                    return where + ": " + b.Items.Count + " items instead of " + a.Items.Count + " (" + string.Join(", ", b.Items.Select(n => n.Name)) + ")";
                for (int i = 0; i < a.Items.Count; i++)
                {
                    MenuNode x = a.Items[i], y = b.Items[i];
                    string here = where + "/" + x.Name;
                    if (x.Id != y.Id || x.Name != y.Name) return where + ": “" + y.Name + "” where “" + x.Name + "” should be";
                    if (x.Type != y.Type) return here + ": " + y.Type + " instead of " + x.Type;
                    if (x.Icon != y.Icon) return here + ": a different icon";
                    if (x.IsFolder)
                    {
                        string inner = Compare(x.Page, y.Page, here);
                        if (inner != null) return inner;
                    }
                }
                return null;
            }
            return Compare(expected, actual, "");
        }
    }
}
