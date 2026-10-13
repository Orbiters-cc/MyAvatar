using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using ControlType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType;

namespace Orbiters.MyAvatar.Editor.Expressions
{
    /// <summary>The page a menu view shows.</summary>
    internal sealed class MenuViewData
    {
        public MenuPage Page;
        public string Title = "Expressions";
        /// <summary>The main menu: VRChat shows its own buttons there (Reset Avatar; Quick Actions in the Action Menu).</summary>
        public bool Root;
        public string Selected;
        public HashSet<string> Changed = new HashSet<string>();
        /// <summary>VRCFury pages menus of more than 8 items; without it a full menu takes no more.</summary>
        public bool Pages = true;
        /// <summary>Items ticked with their select box (several at once, folders included).</summary>
        public HashSet<string> Checked = new HashSet<string>();
    }

    /// <summary>Where a dragged item is dropped.</summary>
    internal enum MenuDropKind { Index, Into, Parent, Folder }

    internal struct MenuDrop
    {
        public MenuDropKind Kind;
        /// <summary>Index: the place in the shown page (counting the dragged item where it was).</summary>
        public int Index;
        /// <summary>Into: the folder item; Folder: a folder id (breadcrumb).</summary>
        public MenuNode Folder;
        public string FolderId;
    }

    /// <summary>What the menu views ask the editor to do.</summary>
    internal interface IMenuEditor
    {
        void Select(MenuNode node);
        void Open(MenuNode folder);
        void Back();
        bool CanBack { get; }
        void Rename(MenuNode node, string name);
        void SetIcon(MenuNode node, Texture2D texture, string file);
        bool CanMove(MenuNode node);
        bool CanRename(MenuNode node);
        void Move(MenuNode node, MenuDrop drop);
        /// <summary>The breadcrumb folder under a pointer (world position) while dragging, highlighted; null when none.</summary>
        string CrumbAt(Vector2 world);
        void EndCrumbHover();
        void Hint(string text);
        /// <summary>Whether holding <paramref name="dragged"/> on <paramref name="target"/> can make a folder of the two.</summary>
        bool CanFold(MenuNode target, MenuNode dragged);
        /// <summary>A new folder holding <paramref name="items"/>, at the first one's place: its id, or null when refused.</summary>
        string CreateFolder(IList<MenuNode> items);
        /// <summary>Ticks or unticks an item's select box.</summary>
        void Check(MenuNode node, bool on);
        bool CanCheck(MenuNode node);
        /// <summary>Whether an item can be left out of the upload (its red cross), and doing it.</summary>
        bool CanDelete(MenuNode node);
        void Delete(MenuNode node);
    }

    /// <summary>
    /// The red cross at an item's top right: it leaves the item out of the upload (once saved) at once on pointer down, never
    /// starting a drag or a click on its item.
    /// </summary>
    internal static class MenuDelete
    {
        public const string Class = "me-delete";

        public static VisualElement Create(IMenuEditor editor, MenuNode node)
        {
            var cross = new VisualElement { tooltip = "Leave out of the upload (with what only it switches). It waits under the Action Menu; a click puts it back." };
            cross.AddToClassList(Class);
            var mark = new VectorIcon(IconGlyph.Close); mark.AddToClassList("me-delete__mark"); cross.Add(mark);
            cross.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0) return;
                cross.AddToClassList("me-delete--pressed");
                evt.StopPropagation();
                editor.Delete(node);
            });
            cross.RegisterCallback<PointerUpEvent>(evt => evt.StopPropagation());
            cross.RegisterCallback<ClickEvent>(evt => evt.StopPropagation());
            return cross;
        }
    }

    /// <summary>
    /// The small select box at the top left of an item (a Quick Menu tile, an Action Menu slice): ticking it selects the item,
    /// folders included, without opening it. It answers on pointer down and never starts a drag or a click on its item.
    /// </summary>
    internal static class MenuCheck
    {
        public const string Class = "me-check", On = "me-check--on";

        public static VisualElement Create(IMenuEditor editor, MenuNode node, bool on)
        {
            var box = new VisualElement { tooltip = "Select (several items can be selected; folders too, without opening them)" };
            box.AddToClassList(Class);
            box.EnableInClassList(On, on);
            box.userData = node.Id;
            var mark = new VectorIcon(IconGlyph.Check); mark.AddToClassList("me-check__mark"); box.Add(mark);
            box.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0) return;
                bool now = !box.ClassListContains(On);
                box.EnableInClassList(On, now);
                evt.StopPropagation();
                editor.Check(node, now);
            });
            // Its item never sees the press or the release.
            box.RegisterCallback<PointerUpEvent>(evt => evt.StopPropagation());
            box.RegisterCallback<ClickEvent>(evt => evt.StopPropagation());
            return box;
        }

        /// <summary>Ticks the boxes under <paramref name="root"/> of the ticked items, unticks the others.</summary>
        public static void Restyle(VisualElement root, HashSet<string> ticked)
        {
            root.Query<VisualElement>(className: Class).ForEach(box => box.EnableInClassList(On, box.userData is string id && ticked != null && ticked.Contains(id)));
        }
    }

    /// <summary>The icons VRChat and Gesture Manager draw (Gesture Manager's, MIT licence, see THIRD_PARTY_NOTICES.md).</summary>
    internal static class MenuArt
    {
        private const string Folder = "Packages/orbiters.myavatar/Editor/Expressions/Icons/BSX_GM_";
        private static readonly Dictionary<string, Texture2D> Cache = new Dictionary<string, Texture2D>();

        public static Texture2D Get(string name)
        {
            if (Cache.TryGetValue(name, out var texture) && texture != null) return texture;
            return Cache[name] = AssetDatabase.LoadAssetAtPath<Texture2D>(Folder + name + ".png");
        }

        public static Texture2D Person => Get("Default");
        public static Texture2D Reset => Get("Reset");
        public static Texture2D Back => Get("Back");
        public static Texture2D BackHome => Get("BackHome");
        public static Texture2D Gear => Get("Gear");

        /// <summary>The badge of a control type (null for buttons).</summary>
        public static Texture2D Badge(ControlType type)
        {
            switch (type)
            {
                case ControlType.Toggle: return Get("Toggle");
                case ControlType.SubMenu: return Get("Option");
                case ControlType.TwoAxisPuppet: return Get("2_Axis");
                case ControlType.FourAxisPuppet: return Get("4_Axis");
                case ControlType.RadialPuppet: return Get("Radial");
                default: return null;
            }
        }

        /// <summary>Whether a picture is being dragged in (a texture or sprite of the project, an image file).</summary>
        public static bool CanDrop() =>
            DragAndDrop.objectReferences.Any(r => r is Texture2D || r is Sprite s && s.texture != null) || (DragAndDrop.paths ?? Array.Empty<string>()).Any(MenuIcons.IsImage);

        /// <summary>
        /// The picture dropped: a texture of the project (also one dropped from the computer's file browser), a sprite's part of
        /// its sheet, or an image file imported on Save.
        /// </summary>
        public static bool Dragged(out Texture2D texture, out string file)
        {
            texture = null; file = null;
            foreach (var reference in DragAndDrop.objectReferences)
            {
                if (reference is Texture2D t) { texture = t; return true; }
                if (reference is Sprite s && s.texture != null)
                {
                    MenuIcons.FromSprite(s, out texture, out file);
                    return texture != null || file != null;
                }
            }
            foreach (var path in DragAndDrop.paths ?? Array.Empty<string>())
            {
                if (!MenuIcons.IsImage(path)) continue;
                texture = MenuIcons.ProjectTexture(path);
                if (texture == null) file = Path.GetFullPath(path);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Fits VRChat's labels like its text auto-size: the largest size (18 down to 8) at which the words fit the box, on
        /// at most <paramref name="lines"/> lines when given.
        /// </summary>
        public static void Fit(Label label, string text, float width, float height, float max = 18f, float min = 8f, int lines = 0)
        {
            label.text = text;
            label.style.fontSize = max;
            if (string.IsNullOrEmpty(text)) return;
            void Measure()
            {
                if (label.panel == null) return;
                // Measured once at the largest size; smaller sizes scale the same words.
                var words = text.Replace("\n", " \n ").Split(' ');
                var space = label.MeasureTextSize("a a", 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined).x -
                            label.MeasureTextSize("aa", 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined).x;
                var line = label.MeasureTextSize("Ag", 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined).y;
                var sizes = words.Select(w => w == "\n" ? -1f : w.Length == 0 ? 0f : label.MeasureTextSize(w, 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined).x).ToArray();
                float fontSize = label.resolvedStyle.fontSize;
                if (fontSize <= 0 || line <= 0) return;
                for (float size = max; size >= min; size -= 0.5f)
                {
                    float scale = size / fontSize, lineWidth = 0, widest = 0;
                    int count = 1;
                    bool fits = true;
                    foreach (var w in sizes)
                    {
                        if (w < 0) { count++; lineWidth = 0; continue; }
                        float word = w * scale;
                        if (word > width) { fits = false; break; }
                        float next = lineWidth > 0 ? lineWidth + space * scale + word : word;
                        if (next > width) { count++; lineWidth = word; }
                        else lineWidth = next;
                        widest = Mathf.Max(widest, lineWidth);
                    }
                    if (fits && count * line * scale <= height + 0.5f && (lines <= 0 || count <= lines))
                    {
                        label.style.fontSize = size;
                        return;
                    }
                }
                label.style.fontSize = min;
            }
            // Measured once the label is laid out with the size just set, so its measures and its style agree: now for a label
            // shown before, a frame or two later for a new one. A newer text replaces a pending fit.
            int tries = 0;
            void Attempt()
            {
                if (label.text != text) return;
                if (label.panel == null || !(label.layout.width > 0) || Mathf.Abs(label.resolvedStyle.fontSize - max) > 0.01f)
                {
                    if (++tries <= 10) label.schedule.Execute(Attempt);
                    return;
                }
                Measure();
            }
            Attempt();
        }
    }

    /// <summary>VRChat's app logo shape in the Quick Menu's sidebar, drawn as a teal wing.</summary>
    internal sealed class QuickMenuLogo : VisualElement
    {
        private static readonly Color Teal = new Color32(42, 172, 196, 255);

        public QuickMenuLogo()
        {
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
        }

        private void Draw(MeshGenerationContext context)
        {
            var r = contentRect;
            if (r.width <= 0) return;
            Vector2 P(float x, float y) => new Vector2(r.x + x / 30f * r.width, r.y + y / 22f * r.height);
            var p = context.painter2D;
            p.fillColor = Teal;
            p.BeginPath();
            p.MoveTo(P(0, 0));
            p.LineTo(P(15, 0.6f));
            p.BezierCurveTo(P(22, 1.2f), P(26.5f, 6.5f), P(30, 22));
            p.BezierCurveTo(P(24, 19), P(16, 18.2f), P(9, 17.6f));
            p.LineTo(P(5.5f, 13.2f));
            p.LineTo(P(9.5f, 12.4f));
            p.LineTo(P(3, 10.2f));
            p.LineTo(P(1.2f, 6.8f));
            p.LineTo(P(6.5f, 6.4f));
            p.ClosePath();
            p.Fill();
        }
    }
}
