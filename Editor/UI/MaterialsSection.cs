using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    // Materials worth changing on this avatar (AvatarMaterials), folded under the texture optimization like it. Open
    // surfaces whose back is hidden (fur cards, feathers) are fixed by themselves unless the user asked to be asked first
    // (Orbiters settings); Unity's Standard shader is offered VRChat's Toon Standard. Hidden when there is nothing to say.
    internal sealed class MaterialsSection : VisualElement
    {
        internal const string AutoFix = "MyAvatar.AutoFixOpenSurfaces";
        // Per avatar: whether the user opened the card. Open surfaces already tried this session are not fixed again.
        private static readonly Dictionary<int, bool> Folds = new Dictionary<int, bool>();
        private static readonly HashSet<int> Tried = new HashSet<int>();

        private readonly MyAvatar avatar;
        private readonly VisualElement body, list;
        private readonly Label title, chip, note;
        private readonly Button askFirst;
        private IVisualElementScheduledItem pending;
        private string shown, fixedText;

        [InitializeOnLoadMethod]
        private static void Register() => OrbitersFeatures.Register(new OrbitersFeature
        {
            Key = AutoFix, Product = "My Avatar", Label = "Show both sides of open surfaces by itself",
            Description = "Fur cards, feathers and other open surfaces are seen from behind too. My Avatar draws their back as soon as it finds them " +
                          "(on a copy of the material, with Undo). Switch it off to be asked first.",
            Stage = FeatureStage.Stable, Default = true,
        });

        internal MaterialsSection(MyAvatar avatar)
        {
            this.avatar = avatar;
            AddToClassList("optimize-card"); AddToClassList("materials-card");
            var header = new VisualElement(); header.AddToClassList("optimize-card__header"); Add(header);
            var chevron = new VectorIcon(IconGlyph.Chevron); chevron.AddToClassList("optimize-card__chevron"); header.Add(chevron);
            title = new Label("Materials"); title.AddToClassList("optimize-card__title"); header.Add(title);
            chip = new Label(); chip.AddToClassList("optimize-card__chip"); header.Add(chip);
            body = new VisualElement(); body.AddToClassList("optimize-card__body"); Add(body);
            var noteRow = new VisualElement(); noteRow.AddToClassList("materials__note-row"); body.Add(noteRow);
            note = new Label(); note.AddToClassList("materials__note"); note.style.display = DisplayStyle.None; noteRow.Add(note);
            askFirst = MyAvatarEditor.Button("Ask me first", () => OrbitersFeatures.SetEnabled(AutoFix, false));
            askFirst.tooltip = "From now on, list open surfaces here and fix them only when you click. Orbiters settings has the same switch.";
            askFirst.AddToClassList("avatar-link"); askFirst.style.display = DisplayStyle.None; noteRow.Add(askFirst);
            list = new VisualElement(); body.Add(list);
            // Opens on press, like the optimization card.
            header.RegisterCallback<PointerDownEvent>(evt => { if (evt.button == 0) Show(!Open, remember: true); });
            Show(Open, remember: false);
            EditorApplication.hierarchyChanged += Schedule;
            Undo.undoRedoPerformed += Schedule;
            OrbitersFeatures.Changed += FeatureChanged;
            RegisterCallback<DetachFromPanelEvent>(_ => { EditorApplication.hierarchyChanged -= Schedule; Undo.undoRedoPerformed -= Schedule; OrbitersFeatures.Changed -= FeatureChanged; });
            // Once on screen: fixing materials creates assets, which waits until the Inspector is built.
            style.display = DisplayStyle.None;
            schedule.Execute(Refresh);
        }

        private bool Open => avatar && Folds.TryGetValue(avatar.GetInstanceID(), out bool open) && open;

        private void Show(bool open, bool remember)
        {
            if (remember && avatar) Folds[avatar.GetInstanceID()] = open;
            EnableInClassList("optimize-card--open", open);
            body.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void FeatureChanged(string key) { if (key == AutoFix) { shown = null; Schedule(); } }

        private void Schedule()
        {
            pending?.Pause();
            pending = schedule.Execute(Refresh).StartingIn(400);
        }

        private void Refresh()
        {
            var root = avatar ? TextureOptimization.AvatarRoot(avatar) : null;
            var advice = AvatarMaterials.Inspect(root);
            if (AutoFixOpenSurfaces(advice)) advice = AvatarMaterials.Inspect(root);
            style.display = advice.Count == 0 && fixedText == null ? DisplayStyle.None : DisplayStyle.Flex;
            askFirst.style.display = fixedText != null && note.text == fixedText && OrbitersFeatures.IsEnabled(AutoFix) ? DisplayStyle.Flex : DisplayStyle.None;
            int suggestions = advice.Count(a => a.Standard || !a.Locked);
            chip.text = suggestions > 0 ? suggestions + (suggestions == 1 ? " suggestion" : " suggestions") : fixedText != null ? "Fixed" : "";
            chip.style.display = chip.text.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            chip.EnableInClassList("materials-card__chip--todo", suggestions > 0);
            string state = string.Join("|", advice.Select(a => a.Material.GetInstanceID() + ":" + a.Standard + a.HiddenBack + a.Renderers.Count)) + "|" + OrbitersFeatures.IsEnabled(AutoFix);
            if (state == shown) return;
            shown = state;
            list.Clear();
            if (advice.Count == 0) return;
            var caption = new Label(Caption(advice));
            caption.AddToClassList("optimize-card__text"); list.Add(caption);
            foreach (var entry in advice) list.Add(Row(entry));
            if (suggestions > 1)
            {
                var actions = new VisualElement(); actions.AddToClassList("materials__actions"); list.Add(actions);
                var fixAll = MyAvatarEditor.Button("Fix all", null);
                fixAll.AddToClassList("mcb-button--primary");
                fixAll.tooltip = "Move the Standard materials to Toon Standard and show both sides of open surfaces. My Avatar works on copies; Undo restores the materials.";
                fixAll.clicked += () => Fix(fixAll, advice);
                actions.Add(fixAll);
            }
        }

        // Open surfaces show their back as soon as they are found, once per material and session (Undo can bring it back).
        private bool AutoFixOpenSurfaces(List<MaterialAdvice> advice)
        {
            if (!avatar || !OrbitersFeatures.IsEnabled(AutoFix) || EditorApplication.isPlayingOrWillChangePlaymode || EditorUtility.IsPersistent(avatar)) return false;
            var open = advice.Where(a => a.HiddenBack && (a.Standard || !a.Locked) && Tried.Add(a.Material.GetInstanceID())).ToList();
            if (open.Count == 0) return false;
            try
            {
                int count = AvatarMaterials.Apply(avatar, open);
                if (count == 0) return false;
                var names = string.Join(", ", open.Select(a => a.Material.name));
                bool toon = open.Any(a => a.Standard);
                fixedText = (count == 1 ? "1 open surface shows" : count + " open surfaces show") + " both sides now (" + names + (toon ? ", on Toon Standard" : "") + "). Undo restores it.";
                SetNote(fixedText, false);
                Show(true, remember: false);
                return true;
            }
            catch (System.Exception ex) { SetNote(ex.Message, true); return false; }
        }

        private static string Caption(List<MaterialAdvice> advice)
        {
            var parts = new List<string>();
            int standard = advice.Count(a => a.Standard), hidden = advice.Count(a => a.HiddenBack);
            if (standard > 0) parts.Add("VRChat suggests its Toon Standard shader for avatars instead of Unity's Standard");
            if (hidden > 0) parts.Add("open surfaces such as fur cards and feathers are seen from behind too, so their back should be drawn");
            string text = string.Join("; ", parts) + ".";
            return char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        private VisualElement Row(MaterialAdvice entry)
        {
            var row = new VisualElement(); row.AddToClassList("materials__row");
            var texts = new VisualElement(); texts.AddToClassList("materials__texts"); row.Add(texts);
            var name = new Label(entry.Material.name) { tooltip = AssetDatabase.GetAssetPath(entry.Material) }; name.AddToClassList("materials__name"); texts.Add(name);
            var issues = new List<string>();
            if (entry.Standard) issues.Add("Standard shader");
            if (entry.HiddenBack) issues.Add(entry.Locked ? "back hidden (unlock it in Poiyomi to change)" : "back hidden on an open surface");
            var detail = new Label(string.Join(" · ", issues) + " · " + string.Join(", ", entry.Renderers.Select(r => r.name).Distinct()));
            detail.AddToClassList("materials__detail"); texts.Add(detail);
            if (entry.Standard || !entry.Locked)
            {
                var fix = MyAvatarEditor.Button(entry.Standard ? "Use Toon Standard" : "Show both sides", null);
                fix.AddToClassList("avatar-link");
                fix.tooltip = entry.Standard
                    ? "Rebuild it with VRChat's Toon Standard, keeping its texture maps" + (entry.HiddenBack ? ", and show both sides." : ".")
                    : "Draw the back of its faces too.";
                fix.clicked += () => Fix(fix, new[] { entry });
                row.Add(fix);
            }
            else
            {
                var select = MyAvatarEditor.Button("Select", () => { Selection.activeObject = entry.Material; EditorGUIUtility.PingObject(entry.Material); });
                select.AddToClassList("avatar-link");
                row.Add(select);
            }
            return row;
        }

        private void Fix(Button button, IEnumerable<MaterialAdvice> advice)
        {
            var targets = advice.ToList();
            button.SetEnabled(false);
            SetNote("Updating materials…", false);
            // Paint the immediate response before the materials are rebuilt and saved.
            schedule.Execute(() =>
            {
                try
                {
                    int count = AvatarMaterials.Apply(avatar, targets);
                    SetNote(count == 0 ? null : count + (count == 1 ? " material updated." : " materials updated.") + " Undo restores them.", false);
                }
                catch (System.Exception ex) { SetNote(ex.Message, true); }
                button.SetEnabled(true);
                shown = null;
                Refresh();
            });
        }

        private void SetNote(string text, bool warning)
        {
            note.text = text;
            note.EnableInClassList("warning", warning);
            note.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }
    }
}
