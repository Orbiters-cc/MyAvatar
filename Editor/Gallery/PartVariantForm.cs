using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    /// <summary>
    /// The + form of a part's gallery: a variant made from the one the item uses now, in the form of the part's kind
    /// (values: colours, numbers, choices, switches and pictures of their sizes; strings: a table of every text with its
    /// base and its longest length, and the language; model: the FBX, each slot's object, each material's shader from the
    /// part's list and its properties, the checks shown live), its name and description, seen live on the item, its
    /// thumbnail drawn from it, the rights statement, and Post. Laid out like the publishing window's steps.
    /// </summary>
    internal sealed class PartVariantForm : VisualElement
    {
        private static readonly string[] CommonLanguages = { "en", "fr", "es", "pt-BR", "ja", "de", "it", "ko", "zh-Hans", "zh-Hant", "ru", "pl", "nl", "tr", "sv", "uk" };
        private readonly ItemParts.Placed placed;
        private readonly Action back;
        private readonly PartVariant draft, original;
        private readonly VisualElement problemsBox, checksBox;
        private readonly Image thumbnail, turning;
        private readonly Label status;
        private readonly Button post;
        private readonly Toggle rights;
        private IVisualElementScheduledItem previewDelay, turn;
        private int previewGroup = -1, previewing;
        private bool posting, posted;
        private byte[] placeholder;
        private string draftFolder;

        internal PartVariantForm(ItemParts.Placed placed, Action back)
        {
            this.placed = placed; this.back = back;
            AddToClassList("gallery-page");
            AddToClassList("parts-form");
            original = ItemParts.VariantOf(placed.Current, placed.PartsPath, placed.Part) ?? ItemParts.Bases(placed.PartsPath, placed.Part).FirstOrDefault() ?? new PartVariant { part = placed.Part.key };
            draft = original.Copy();
            draft.part = placed.Part.key;
            draft.name = ""; draft.description = ""; draft.Id = 0; draft.BaseName = null; draft.Official = false; draft.Sha256 = null; draft.Folder = original.Folder;
            draft.Thumbnail = null;
            if (placed.Part.kind == ItemParts.Values && draft.values == null) draft.values = new JObject();
            // Every field has a value (Orbiters takes none left out): the field's default where the variant shown has none.
            foreach (var field in placed.Part.kind == ItemParts.Values ? placed.Part.fields ?? new List<ItemParts.Field>() : new List<ItemParts.Field>())
                if (field?.defaultValue != null && draft.values[field.key] == null) draft.values[field.key] = field.defaultValue.DeepClone();
            if (placed.Part.kind == ItemParts.Strings && draft.strings == null) draft.strings = new Dictionary<string, string>();
            if (placed.Part.kind == ItemParts.Model && draft.model == null) draft.model = new VariantModel();
            draftFolder = ItemParts.ItemFolder(placed.Item) + "/" + ItemParts.Clean(placed.Part.key, 24) + "/draft";

            var header = GalleryUI.Box("gallery-page__header", this);
            header.Add(GalleryUI.IconButton(IconGlyph.Chevron, "Back to the gallery", Leave, "gallery-back", "gallery-back__icon"));
            GalleryUI.Text("New " + placed.Part.Label.ToLowerInvariant() + " variant", "gallery-page__title", header);
            GalleryUI.Text("Starts from " + original.name + ". Only " + Allowed() + " can be in it, never a script. You see it on " + placed.Item.name + " as you go.", "creator-intro", this);

            var kind = Step(1, KindTitle());
            if (placed.Part.kind == ItemParts.Values) ValuesForm(kind);
            else if (placed.Part.kind == ItemParts.Strings) StringsForm(kind);
            else ModelForm(kind);
            checksBox = GalleryUI.Box("parts-form__checks", kind);

            var about = Step(2, "Name and description");
            Label("Name", about);
            about.Add(Field(draft.name, value => { draft.name = value; Changed(false); }, false, "What people see on its card (" + ItemParts.MaxName + " characters at most)"));
            Label("Description", about);
            about.Add(Field(draft.description, value => { draft.description = value; Changed(false); }, true, "Optional (" + ItemParts.MaxDescription + " characters at most)"));

            var look = Step(3, "On the item");
            var pictures = GalleryUI.Box("parts-form__pictures", look);
            thumbnail = new Image { scaleMode = ScaleMode.ScaleToFit }; thumbnail.AddToClassList("parts-form__thumbnail"); pictures.Add(thumbnail);
            if (placed.Part.kind == ItemParts.Model)
            {
                turning = new Image { scaleMode = ScaleMode.ScaleToFit }; turning.AddToClassList("parts-form__thumbnail"); pictures.Add(turning);
                turn = schedule.Execute(Turn).Every(66);
            }
            Hint("Its thumbnail (" + ItemParts.ThumbnailSize + " × " + ItemParts.ThumbnailSize + ") is drawn from the item as it shows the variant. Leaving the form puts back " + original.name + ".", look);

            var share = Step(4, "Post");
            rights = new Toggle { text = "I made this variant, or I have permission to share everything in it." };
            rights.AddToClassList("creator-rights");
            rights.RegisterValueChangedCallback(_ => UpdatePost());
            share.Add(rights);
            problemsBox = GalleryUI.Box("parts-form__problems", share);
            post = GalleryUI.Button("Post", () => _ = PostAsync(), "mcb-button--primary creator-publish");
            share.Add(post);
            status = GalleryUI.Text("", "creator-message", share);
            status.style.display = DisplayStyle.None;
            RegisterCallback<DetachFromPanelEvent>(_ => { turn?.Pause(); previewDelay?.Pause(); });
            // The item shows the variant the draft starts from: only its thumbnail is drawn until something changes.
            Check();
            schedule.Execute(() => _ = DrawThumbnailAsync(previewing));
        }

        private async Task DrawThumbnailAsync(int ticket)
        {
            if (placed.Item == null) return;
            var png = await ItemParts.ThumbnailAsync(placed.Item, placed.Definition, placed.PrefabPath, placed.PartsPath, placed.Part, Previewed());
            if (ticket != previewing || panel == null || png == null) return;
            draft.Thumbnail = png;
            thumbnail.image = PartVariants.Picture("form-thumbnail|" + PartVariant.Hash(png), () => png);
            UpdatePost();
        }

        private string Allowed() => placed.Part.kind == ItemParts.Values ? "its values and PNG or JPEG pictures"
            : placed.Part.kind == ItemParts.Strings ? "its texts" : "one FBX and the PNG or JPEG textures of its materials";

        private string KindTitle() => placed.Part.kind == ItemParts.Values ? "Values" : placed.Part.kind == ItemParts.Strings ? "Texts" : "Model";

        private VisualElement Step(int number, string title)
        {
            var step = GalleryUI.Box("creator-step", this);
            var header = GalleryUI.Box("creator-step__header", step);
            GalleryUI.Text(number.ToString(), "creator-step__number", header);
            GalleryUI.Text(title, "creator-step__title", header);
            return step;
        }

        private static void Label(string text, VisualElement parent) => GalleryUI.Text(text, "creator-label", parent);
        private static void Hint(string text, VisualElement parent) => GalleryUI.Text(text, "creator-hint", parent);

        private static TextField Field(string value, Action<string> changed, bool multiline = false, string placeholder = null)
        {
            var field = new TextField { value = value ?? "", multiline = multiline };
            field.AddToClassList("creator-field");
            if (multiline) field.AddToClassList("creator-field--multiline");
            if (!string.IsNullOrEmpty(placeholder))
            {
                var hint = GalleryUI.Text(placeholder, "gallery-search__hint", field);
                hint.style.top = 6; hint.pickingMode = PickingMode.Ignore;
                hint.style.display = string.IsNullOrEmpty(value) ? DisplayStyle.Flex : DisplayStyle.None;
                field.RegisterValueChangedCallback(evt => hint.style.display = string.IsNullOrEmpty(evt.newValue) ? DisplayStyle.Flex : DisplayStyle.None);
            }
            if (changed != null) field.RegisterValueChangedCallback(evt => changed(evt.newValue));
            return field;
        }

        // ---- Values ----

        private void ValuesForm(VisualElement step)
        {
            foreach (var field in placed.Part.fields ?? new List<ItemParts.Field>())
            {
                if (field == null) continue;
                var row = GalleryUI.Box("parts-form__field", step);
                GalleryUI.Text(field.Label, "parts-form__field-label", row);
                var value = draft.values[field.key] ?? field.defaultValue;
                switch (field.type)
                {
                    case ItemParts.Color:
                        ItemParts.TryColor(value?.Type == JTokenType.String ? (string)value : "#FFFFFF", out var colour);
                        var picker = new ColorField { value = colour, showAlpha = value?.Type == JTokenType.String && ((string)value).Length == 9, hdr = false };
                        picker.AddToClassList("parts-form__color");
                        picker.RegisterValueChangedCallback(evt => { draft.values[field.key] = ItemParts.ColorText(evt.newValue); Changed(true); });
                        row.Add(picker);
                        break;
                    case ItemParts.Number:
                        float min = field.min ?? 0, max = field.max ?? Mathf.Max(1, min + 1);
                        var slider = new Slider(min, max) { value = value != null && (value.Type == JTokenType.Float || value.Type == JTokenType.Integer) ? value.Value<float>() : min, showInputField = true };
                        slider.AddToClassList("parts-form__slider");
                        slider.RegisterValueChangedCallback(evt => { draft.values[field.key] = Math.Round(evt.newValue, 3); Changed(true); });
                        row.Add(slider);
                        break;
                    case ItemParts.Choice:
                        var options = field.options ?? new List<string>();
                        var segmented = new SegmentedControl(options.Select(o => ObjectNames.NicifyVariableName(o)), index => { draft.values[field.key] = options[index]; Changed(true); });
                        segmented.AddToClassList("parts-form__choice");
                        int at = value?.Type == JTokenType.String ? options.IndexOf((string)value) : -1;
                        if (at >= 0) segmented.SetIndex(at);
                        row.Add(segmented);
                        break;
                    case ItemParts.Toggle:
                        row.Add(new ToggleSwitch(value?.Type == JTokenType.Boolean && (bool)value, on => { draft.values[field.key] = on; Changed(true); }));
                        break;
                    case ItemParts.Picture:
                        PictureField(row, field);
                        break;
                }
            }
        }

        private void PictureField(VisualElement row, ItemParts.Field field)
        {
            var preview = new Image { scaleMode = ScaleMode.ScaleToFit }; preview.AddToClassList("parts-form__picture");
            var name = GalleryUI.Text("", "parts-form__file", row);
            string size = field.width > 0 && field.height > 0 ? field.width + " × " + field.height + " px" : "up to " + ItemParts.MaxPicture + " px";
            void Show()
            {
                string file = draft.values[field.key]?.Type == JTokenType.String ? (string)draft.values[field.key] : null;
                name.text = file != null ? file : "No picture · " + size;
                preview.image = file != null ? PartVariants.Picture("form|" + file + "|" + draft.FileBytes(file)?.Length, () => draft.FileBytes(file)) : null;
                preview.style.display = preview.image != null ? DisplayStyle.Flex : DisplayStyle.None;
            }
            row.Add(Small("Choose…", () =>
            {
                string path = EditorUtility.OpenFilePanel(field.Label + " (" + size + ")", "", "png,jpg,jpeg");
                if (string.IsNullOrEmpty(path)) return;
                string file = UniqueName(Path.GetFileName(path));
                draft.Files[file] = File.ReadAllBytes(path);
                draft.values[field.key] = file;
                Show(); Changed(true);
            }));
            if (field.optional) row.Add(Small("Clear", () => { draft.values.Remove(field.key); Show(); Changed(true); }));
            // Under its row.
            row.parent.Insert(row.parent.IndexOf(row) + 1, preview);
            Show();
        }

        // A file name allowed in a variant (letters, digits, spaces, dots, dashes, underscores), unique in it.
        private string UniqueName(string name)
        {
            string stem = new string(Path.GetFileNameWithoutExtension(name).Select(c => char.IsLetterOrDigit(c) && c < 128 || c == ' ' || c == '-' || c == '_' ? c : '-').ToArray()).Trim();
            if (stem.Length == 0) stem = "picture";
            if (stem.Length > 48) stem = stem.Substring(0, 48);
            string extension = Path.GetExtension(name).ToLowerInvariant();
            string candidate = stem + extension;
            for (int i = 2; draft.Files.ContainsKey(candidate) && i < 100; i++) candidate = stem + " " + i + extension;
            return candidate;
        }

        private static Button Small(string text, Action action)
        {
            var button = GalleryUI.Button(text, action, "accessory-button parts-form__small");
            return button;
        }

        // ---- Strings ----

        private void StringsForm(VisualElement step)
        {
            var baseStrings = ItemParts.BaseStrings.Read(ItemParts.AssetPath(placed.PartsPath, placed.Part.strings), null) ?? new ItemParts.BaseStrings();
            Label("Language", step);
            var languageRow = GalleryUI.Box("creator-row", step);
            var code = Field(draft.language, value => { draft.language = value?.Trim(); Changed(true); }, false, "A code such as fr, pt-BR or ja");
            languageRow.Add(code);
            var common = new DropdownField(new List<string> { "Common languages" }.Concat(CommonLanguages.Select(c => PartVariants.LanguageName(c) + " (" + c + ")")).ToList(), 0);
            common.AddToClassList("parts-form__languages");
            common.style.width = 200; common.style.flexGrow = 0; common.style.flexShrink = 0;
            var input = common.Q(className: "unity-base-field__input");
            if (input != null)
            {
                input.style.backgroundColor = new Color(42 / 255f, 42 / 255f, 42 / 255f);
                input.style.borderTopWidth = input.style.borderBottomWidth = input.style.borderLeftWidth = input.style.borderRightWidth = 0;
                input.style.borderTopLeftRadius = input.style.borderTopRightRadius = input.style.borderBottomLeftRadius = input.style.borderBottomRightRadius = 8;
                input.style.minHeight = 26; input.style.paddingLeft = 10;
            }
            common.RegisterValueChangedCallback(evt =>
            {
                int index = common.choices.IndexOf(evt.newValue);
                if (index > 0) code.value = CommonLanguages[index - 1];
            });
            languageRow.Add(common);
            Label("Texts", step);
            var filter = Field("", null, false, "Find a text by its key or words");
            filter.AddToClassList("parts-form__filter");
            step.Add(filter);
            var table = GalleryUI.Box("parts-form__table", step);
            var head = GalleryUI.Box("parts-form__text parts-form__text--head", table);
            GalleryUI.Text("Key and base text", "parts-form__text-base", head);
            GalleryUI.Text("Your text", "parts-form__text-yours", head);
            var rows = new List<(VisualElement row, string search)>();
            foreach (var pair in baseStrings.Texts)
            {
                string key = pair.Key;
                int max = baseStrings.MaxOf(key);
                var row = GalleryUI.Box("parts-form__text", table);
                var left = GalleryUI.Box("parts-form__text-base", row);
                GalleryUI.Text(key, "parts-form__text-key", left);
                GalleryUI.Text(pair.Value, "parts-form__text-original", left);
                var right = GalleryUI.Box("parts-form__text-yours", row);
                draft.strings.TryGetValue(key, out var mine);
                var count = GalleryUI.Text("", "parts-form__count", right);
                void Count(string text)
                {
                    int length = Length(text);
                    count.text = length + " / " + max;
                    count.EnableInClassList("parts-form__count--over", length > max || length == 0);
                }
                var field = Field(mine ?? "", value => { draft.strings[key] = value; Count(value); Changed(true, slow: true); });
                right.Insert(0, field);
                Count(mine ?? "");
                rows.Add((row, (key + " " + pair.Value + " " + mine).ToLowerInvariant()));
            }
            filter.RegisterValueChangedCallback(evt =>
            {
                string query = (evt.newValue ?? "").Trim().ToLowerInvariant();
                foreach (var (row, search) in rows) row.style.display = query.Length == 0 || search.Contains(query) ? DisplayStyle.Flex : DisplayStyle.None;
            });
        }

        private static int Length(string text)
        {
            int count = 0;
            for (int i = 0; i < (text ?? "").Length; i++) { count++; if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length) i++; }
            return count;
        }

        // ---- Model ----

        private VisualElement slotsBox;
        private GameObject fbxAsset;

        private void ModelForm(VisualElement step)
        {
            Label("FBX", step);
            var row = GalleryUI.Box("creator-row", step);
            var field = new ObjectField { objectType = typeof(GameObject), allowSceneObjects = false };
            field.AddToClassList("creator-field");
            field.RegisterValueChangedCallback(evt =>
            {
                var chosen = evt.newValue as GameObject;
                string path = chosen != null ? AssetDatabase.GetAssetPath(chosen) : null;
                if (chosen != null && !(AssetImporter.GetAtPath(path) is ModelImporter && path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)))
                { field.SetValueWithoutNotify(fbxAsset); Status("Choose an .fbx model.", true); return; }
                UseFbx(path);
            });
            row.Add(field);
            row.Add(Small("Choose file…", () =>
            {
                string path = EditorUtility.OpenFilePanel("A model for " + placed.Part.Label.ToLowerInvariant(), "", "fbx");
                if (string.IsNullOrEmpty(path)) return;
                UseFbx(path);
                field.SetValueWithoutNotify(fbxAsset);
            }));
            Hint("One FBX with an object for each slot; its blendshapes " + string.Join(", ", placed.Part.blendshapes ?? new List<string>()) + "; at most " + placed.Part.maxTriangles +
                 " triangles and a radius of " + placed.Part.maxRadius + " (the creator's model fits a radius of 1). Materials glow through their emission colour.", step);
            slotsBox = GalleryUI.Box(null, step);
            // The model the item uses now, when it came with a file.
            if (!string.IsNullOrEmpty(draft.model?.mesh) && !string.IsNullOrEmpty(original.Folder))
            {
                string path = original.Folder + "/" + draft.model.mesh;
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (asset != null) { field.SetValueWithoutNotify(asset); fbxAsset = asset; }
            }
            BuildSlots();
        }

        private void UseFbx(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { fbxAsset = null; draft.model.mesh = null; BuildSlots(); Changed(true); return; }
            string name = UniqueName(Path.GetFileName(path));
            var bytes = File.ReadAllBytes(path);
            // Read as applying will read it (its objects' names kept), in the item's folder.
            string copy = ItemParts.WriteAsset(draftFolder, name, bytes);
            ItemParts.ImportModel(copy);
            Orbiters.Toolkit.Editor.Storage.GeneratedAssets.Track(copy, draftFolder);
            fbxAsset = AssetDatabase.LoadAssetAtPath<GameObject>(copy);
            foreach (var old in draft.Files.Keys.Where(k => k.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)).ToList()) draft.Files.Remove(old);
            draft.Files[name] = bytes;
            draft.model.mesh = name;
            // Each slot takes an object of the same name, else the FBX's first mesh.
            var meshes = MeshObjects();
            foreach (var slot in placed.Part.slots ?? new List<ItemParts.Slot>())
            {
                if (!draft.model.slots.TryGetValue(slot.key, out var given) || given == null) draft.model.slots[slot.key] = given = new VariantSlot();
                if (!meshes.Contains(given.objectName)) given.objectName = meshes.FirstOrDefault(m => m == slot.Label || m == slot.key) ?? meshes.FirstOrDefault();
                FitMaterials(given);
            }
            BuildSlots();
            Changed(true, slow: true);
        }

        private List<string> MeshObjects() => fbxAsset == null ? new List<string>() : fbxAsset.GetComponentsInChildren<Transform>(true)
            .Where(t => t.TryGetComponent(out MeshFilter f) && f.sharedMesh != null || t.TryGetComponent(out SkinnedMeshRenderer s) && s.sharedMesh != null).Select(t => t.name).Distinct().ToList();

        private Mesh MeshOf(string objectName)
        {
            foreach (var t in fbxAsset != null ? fbxAsset.GetComponentsInChildren<Transform>(true) : new Transform[0])
            {
                if (t.name != objectName) continue;
                if (t.TryGetComponent(out SkinnedMeshRenderer skinned) && skinned.sharedMesh != null) return skinned.sharedMesh;
                if (t.TryGetComponent(out MeshFilter filter) && filter.sharedMesh != null) return filter.sharedMesh;
            }
            return null;
        }

        // The slot's mesh as applying will put it in (its object's turn and scale in the file baked in): measured, then
        // destroyed when it is a temporary copy.
        private ItemParts.MeshFacts Facts(string objectName)
        {
            var mesh = fbxAsset != null ? ItemParts.ModelMesh(fbxAsset, objectName) : null;
            if (mesh == null) return null;
            try { return ItemParts.Measure(placed.Part, mesh); }
            finally { if (!EditorUtility.IsPersistent(mesh)) UnityEngine.Object.DestroyImmediate(mesh); }
        }

        // A material per submesh: those given keep, new ones take the part's first shader.
        private void FitMaterials(VariantSlot slot)
        {
            int count = MeshOf(slot.objectName)?.subMeshCount ?? 1;
            slot.materials ??= new List<VariantMaterial>();
            while (slot.materials.Count < count) slot.materials.Add(new VariantMaterial { shader = (placed.Part.shaders ?? new List<string>()).FirstOrDefault() });
            while (slot.materials.Count > count) slot.materials.RemoveAt(slot.materials.Count - 1);
        }

        private void BuildSlots()
        {
            if (slotsBox == null) return;
            slotsBox.Clear();
            if (fbxAsset == null) { Hint("Choose the FBX first.", slotsBox); return; }
            var meshes = MeshObjects();
            foreach (var slot in placed.Part.slots ?? new List<ItemParts.Slot>())
            {
                if (!draft.model.slots.TryGetValue(slot.key, out var given) || given == null) continue;
                var box = GalleryUI.Box("parts-form__slot", slotsBox);
                var top = GalleryUI.Box("creator-row", box);
                GalleryUI.Text(slot.Label, "parts-form__slot-title", top);
                var objects = new DropdownField(meshes, Math.Max(0, meshes.IndexOf(given.objectName)));
                objects.AddToClassList("parts-form__object");
                objects.RegisterValueChangedCallback(evt => { given.objectName = evt.newValue; FitMaterials(given); BuildSlots(); Changed(true, slow: true); });
                top.Add(objects);
                for (int i = 0; i < given.materials.Count; i++) MaterialForm(box, given, i);
            }
        }

        private static readonly string[] MainProperties = { "_Color", "_MainTex", "_EmissionColor", "_EmissionMap", "_BumpMap", "_Glossiness", "_Metallic", "_MetallicStrength", "_GlossStrength", "_Matcap" };

        private void MaterialForm(VisualElement parent, VariantSlot slot, int index)
        {
            var material = slot.materials[index];
            var box = GalleryUI.Box("parts-form__material", parent);
            var top = GalleryUI.Box("creator-row", box);
            GalleryUI.Text("Material " + (index + 1), "parts-form__field-label", top);
            var shaders = placed.Part.shaders ?? new List<string>();
            var shaderField = new DropdownField(shaders, Math.Max(0, shaders.IndexOf(material.shader)));
            shaderField.AddToClassList("parts-form__shader");
            shaderField.RegisterValueChangedCallback(evt => { material.shader = evt.newValue; BuildSlots(); Changed(true); });
            top.Add(shaderField);
            var shader = Shader.Find(material.shader ?? "");
            if (shader == null) return;
            var more = new Foldout { text = "More properties", value = false }; more.AddToClassList("parts-form__more");
            for (int p = 0; p < shader.GetPropertyCount(); p++)
            {
                if ((shader.GetPropertyFlags(p) & ShaderPropertyFlags.HideInInspector) != 0) continue;
                string property = shader.GetPropertyName(p);
                var type = shader.GetPropertyType(p);
                if (type == ShaderPropertyType.Texture && shader.GetPropertyTextureDimension(p) != TextureDimension.Tex2D) continue;
                if (type == ShaderPropertyType.Vector) continue;
                var row = GalleryUI.Box("parts-form__field", Array.IndexOf(MainProperties, property) >= 0 ? box : more.contentContainer);
                GalleryUI.Text(shader.GetPropertyDescription(p), "parts-form__field-label", row);
                if (type == ShaderPropertyType.Color)
                {
                    var has = material.colors != null && material.colors.TryGetValue(property, out var text) && ItemParts.TryColor(text, out _);
                    ItemParts.TryColor(has ? material.colors[property] : null, out var current);
                    var colour = has ? (Color)current : (Color)shader.GetPropertyDefaultVectorValue(p);
                    var picker = new ColorField { value = colour, showAlpha = false, hdr = false }; picker.AddToClassList("parts-form__color");
                    picker.RegisterValueChangedCallback(evt => { (material.colors ??= new Dictionary<string, string>())[property] = ItemParts.ColorText(evt.newValue); Changed(true); });
                    row.Add(picker);
                }
                else if (type == ShaderPropertyType.Float || type == ShaderPropertyType.Range || type == ShaderPropertyType.Int)
                {
                    float value = material.floats != null && material.floats.TryGetValue(property, out float given) ? given : shader.GetPropertyDefaultFloatValue(p);
                    BaseField<float> number;
                    if (type == ShaderPropertyType.Range) { var limits = shader.GetPropertyRangeLimits(p); number = new Slider(limits.x, limits.y) { value = value, showInputField = true }; number.AddToClassList("parts-form__slider"); }
                    else { number = new FloatField { value = value }; number.AddToClassList("creator-field"); }
                    number.RegisterValueChangedCallback(evt => { (material.floats ??= new Dictionary<string, float>())[property] = evt.newValue; Changed(true); });
                    row.Add(number);
                }
                else if (type == ShaderPropertyType.Texture)
                {
                    var label = GalleryUI.Text(material.textures != null && material.textures.TryGetValue(property, out var file) ? file : "None", "parts-form__file", row);
                    row.Add(Small("Choose…", () =>
                    {
                        string path = EditorUtility.OpenFilePanel(shader.GetPropertyDescription(shader.FindPropertyIndex(property)) + " (up to " + placed.Part.maxTextureSize + " px)", "", "png,jpg,jpeg");
                        if (string.IsNullOrEmpty(path)) return;
                        string name = UniqueName(Path.GetFileName(path));
                        draft.Files[name] = File.ReadAllBytes(path);
                        (material.textures ??= new Dictionary<string, string>())[property] = name;
                        label.text = name;
                        Changed(true);
                    }));
                    row.Add(Small("Clear", () => { material.textures?.Remove(property); label.text = "None"; Changed(true); }));
                }
            }
            if (more.contentContainer.childCount > 0) box.Add(more);
        }

        // ---- Live checks, preview and thumbnail ----

        private void Changed(bool preview, bool slow = false)
        {
            Check();
            if (!preview) return;
            previewDelay?.Pause();
            previewDelay = schedule.Execute(() => _ = PreviewAsync()).StartingIn(slow ? 1200 : 450);
        }

        // The draft with what a preview needs that only Post asks for (a name, a thumbnail).
        private PartVariant Previewed()
        {
            var copy = draft.Copy();
            copy.Folder = draft.Folder;
            if (string.IsNullOrWhiteSpace(copy.name)) copy.name = "Draft";
            placeholder ??= Placeholder();
            if (copy.Thumbnail == null) copy.Thumbnail = placeholder;
            return copy;
        }

        private static byte[] Placeholder()
        {
            var texture = new Texture2D(ItemParts.ThumbnailSize, ItemParts.ThumbnailSize, TextureFormat.RGB24, false);
            texture.SetPixels(Enumerable.Repeat(ItemParts.ThumbnailBackground, ItemParts.ThumbnailSize * ItemParts.ThumbnailSize).ToArray());
            texture.Apply();
            try { return texture.EncodeToPNG(); }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
        }

        private List<string> Problems(PartVariant variant)
        {
            var problems = new List<string>();
            ItemParts.Check(placed.Part, variant, AssetDatabase.LoadAssetAtPath<GameObject>(placed.PrefabPath), problems, partsPath: placed.PartsPath);
            return problems;
        }

        private void Check()
        {
            var problems = Problems(Previewed());
            if (string.IsNullOrWhiteSpace(draft.name)) problems.Insert(0, "Give it a name.");
            problemsBox.Clear();
            foreach (string problem in problems.Take(8)) GalleryUI.Text(char.ToUpperInvariant(problem[0]) + problem.Substring(1), "parts-form__problem", problemsBox);
            if (problems.Count > 8) GalleryUI.Text("…and " + (problems.Count - 8) + " more.", "parts-form__problem", problemsBox);
            if (placed.Part.kind == ItemParts.Model) ModelChecks();
            UpdatePost(problems.Count == 0);
        }

        // Triangles, blendshapes, radius and material count of each slot's mesh, as the checks will count them.
        private void ModelChecks()
        {
            checksBox.Clear();
            if (fbxAsset == null) return;
            foreach (var slot in placed.Part.slots ?? new List<ItemParts.Slot>())
            {
                if (!draft.model.slots.TryGetValue(slot.key, out var given) || given == null) continue;
                var facts = Facts(given.objectName);
                if (facts == null) { Fact(slot.Label + ": no mesh named " + given.objectName + " in the FBX.", false); continue; }
                Fact(slot.Label + " · " + facts.Triangles + " of " + placed.Part.maxTriangles + " triangles", facts.Triangles <= placed.Part.maxTriangles);
                Fact("Radius " + facts.Radius.ToString("0.00") + " of " + placed.Part.maxRadius.ToString("0.##"), facts.Radius <= placed.Part.maxRadius * 1.0001f);
                Fact(facts.MissingShapes.Count == 0 ? "Every blendshape the item uses" : "Missing blendshapes: " + string.Join(", ", facts.MissingShapes), facts.MissingShapes.Count == 0);
                Fact(facts.Submeshes + " material slot" + (facts.Submeshes == 1 ? "" : "s"), facts.Submeshes == given.materials.Count);
            }
            foreach (var texture in draft.model.slots.Values.Where(s => s != null).SelectMany(s => s.materials ?? new List<VariantMaterial>()).SelectMany(m => m?.textures?.Values ?? Enumerable.Empty<string>()).Distinct())
                if (ItemParts.PictureSize(draft.FileBytes(texture), out int w, out int h))
                    Fact(texture + " · " + w + " × " + h, w <= placed.Part.maxTextureSize && h <= placed.Part.maxTextureSize);
        }

        private void Fact(string text, bool good)
        {
            var row = GalleryUI.Box("parts-form__fact", checksBox);
            var dot = GalleryUI.Box("parts-form__fact-dot", row);
            dot.EnableInClassList("parts-form__fact-dot--bad", !good);
            GalleryUI.Text(text, "parts-form__fact-text", row);
        }

        private void UpdatePost(bool? clean = null)
        {
            if (post == null) return;
            bool ok = clean ?? Problems(Previewed()).Count == 0 && !string.IsNullOrWhiteSpace(draft.name);
            post.SetEnabled(!posting && ok && rights.value && placed.AssetId > 0 && draft.Thumbnail != null);
            post.tooltip = placed.AssetId <= 0 ? "Only items from the asset gallery have a gallery to post to." : !rights.value ? "Confirm the rights statement first." : null;
        }

        private async Task PreviewAsync()
        {
            if (placed.Item == null || posting) return;
            var variant = Previewed();
            if (Problems(variant).Count > 0) return;
            int ticket = ++previewing;
            Status("Showing it on " + placed.Item.name + "…", false);
            var result = await ItemParts.ApplyAsync(placed, variant, text => Status(text, false));
            if (ticket != previewing || panel == null) return;
            // Every preview is one Undo step with the first.
            if (result.Applied)
            {
                if (previewGroup < 0) previewGroup = Undo.GetCurrentGroup();
                else Undo.CollapseUndoOperations(previewGroup);
                Undo.SetCurrentGroupName("Customize " + placed.Part.Label + ": preview");
            }
            Status(result.Applied ? result.Note : string.Join(" ", result.Problems.Take(3)), !result.Applied);
            if (result.Applied) await DrawThumbnailAsync(ticket);
        }

        private void Turn()
        {
            if (turning == null || placed.Item == null || panel == null) return;
            var renderers = (placed.Part.slots ?? new List<ItemParts.Slot>()).SelectMany(s => s.paths ?? new List<string>()).Take(1)
                .Select(p => placed.Item.Find(p.Trim('/'))).Where(t => t != null).Select(t => t.GetComponent<Renderer>()).Where(r => r != null).ToList();
            if (renderers.Count == 0) return;
            var old = turning.image;
            turning.image = ItemParts.RenderPicture(renderers, 256, (float)(EditorApplication.timeSinceStartup * 40 % 360), true);
            if (old != null) UnityEngine.Object.DestroyImmediate(old);
        }

        private void Status(string text, bool error)
        {
            if (status == null) return;
            status.text = text ?? "";
            status.EnableInClassList("error", error);
            status.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        // ---- Post, or leave ----

        private async Task PostAsync()
        {
            if (posting) return;
            posting = true;
            // Pressed: it says so at once.
            post.SetEnabled(false);
            post.text = "Posting…";
            try
            {
                var variant = draft.Copy();
                variant.Folder = draft.Folder;
                var info = await PartVariants.PostAsync(placed.AssetId, placed, variant, false, text => { post.text = text; }, CancellationToken.None);
                posted = true;
                // The item keeps it, as the gallery's variant it now is.
                variant.Id = info.id; variant.Author = info.author?.username; variant.Sha256 = info.sha256;
                var result = await ItemParts.ApplyAsync(placed, variant);
                if (result.Applied && previewGroup >= 0) Undo.CollapseUndoOperations(previewGroup);
                Status("Posted: everyone with " + placed.Item.name + " can use " + variant.name + " now.", false);
                post.text = "Posted";
                schedule.Execute(back).StartingIn(1200);
            }
            catch (Exception e)
            {
                posting = false;
                post.text = "Post";
                Status(e.Message, true);
                UpdatePost();
            }
        }

        // Back to the gallery: the variant the item had comes back when only previews changed it.
        private void Leave()
        {
            previewing++;
            if (!posted && previewGroup >= 0 && placed.Item != null)
            {
                int group = previewGroup;
                _ = RestoreAsync(group);
            }
            back();
        }

        private async Task RestoreAsync(int group)
        {
            var result = await ItemParts.ApplyAsync(placed, original);
            if (result.Applied) Undo.CollapseUndoOperations(group);
        }
    }
}
