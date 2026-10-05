using System;
using System.Globalization;
using System.Linq;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    /// <summary>Small builders and the card state logic shared by the gallery's views.</summary>
    internal static class GalleryUI
    {
        internal const string StyleSheetPath = "Packages/orbiters.myavatar/Editor/Gallery/gallery.uss";
        private static StyleSheet sheet;
        internal static StyleSheet Sheet => sheet ? sheet : sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);

        internal static Label Text(string text, string className, VisualElement parent = null)
        {
            var label = new Label(text ?? "");
            if (!string.IsNullOrEmpty(className)) foreach (var name in className.Split(' ')) label.AddToClassList(name);
            parent?.Add(label);
            return label;
        }

        internal static VisualElement Box(string className, VisualElement parent = null)
        {
            var box = new VisualElement();
            if (!string.IsNullOrEmpty(className)) foreach (var name in className.Split(' ')) box.AddToClassList(name);
            parent?.Add(box);
            return box;
        }

        /// <summary>A button acting on press, with Unity's click as fallback (ButtonInteraction).</summary>
        internal static Button Button(string text, Action action, string className = null)
        {
            var button = MyAvatarEditor.Button(text, () => { });
            if (!string.IsNullOrEmpty(className)) foreach (var name in className.Split(' ')) button.AddToClassList(name);
            ButtonInteraction.RegisterImmediateClick(button, action);
            return button;
        }

        internal static Button IconButton(IconGlyph glyph, string tooltip, Action action, string className, string iconClass)
        {
            var button = new Button { tooltip = tooltip };
            button.AddToClassList(className);
            var icon = new VectorIcon(glyph); icon.AddToClassList(iconClass); button.Add(icon);
            ButtonInteraction.RegisterImmediateClick(button, action);
            return button;
        }

        /// <summary>A whole element acting as a button: immediate feedback on press, action on release inside it.</summary>
        internal static void Clickable(VisualElement element, Action action, string pressedClass)
        {
            element.focusable = true;
            element.RegisterCallback<PointerDownEvent>(evt => { if (evt.button == 0) element.AddToClassList(pressedClass); });
            element.RegisterCallback<PointerLeaveEvent>(_ => element.RemoveFromClassList(pressedClass));
            element.RegisterCallback<PointerUpEvent>(evt =>
            {
                bool pressed = element.ClassListContains(pressedClass);
                element.RemoveFromClassList(pressedClass);
                if (evt.button == 0 && pressed) action();
            });
            element.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter && evt.keyCode != KeyCode.Space) return;
                action(); evt.StopPropagation();
            });
        }

        internal static string Size(long bytes) => Toolkit.Editor.Storage.ArchiveBudget.Format(bytes);

        /// <summary>Compares "1.10.0" and "1.9.2" by their numbers; text after them breaks ties.</summary>
        internal static int CompareVersions(string a, string b)
        {
            int[] Parts(string v) => (v ?? "").Split('-', '+')[0].Split('.').Select(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0).ToArray();
            var left = Parts(a); var right = Parts(b);
            for (int i = 0; i < Math.Max(left.Length, right.Length); i++)
            {
                int x = i < left.Length ? left[i] : 0, y = i < right.Length ? right[i] : 0;
                if (x != y) return x.CompareTo(y);
            }
            return string.CompareOrdinal(a ?? "", b ?? "");
        }

        internal enum State { Add, Buy, Installed, Update, Working, Question, Failed, Unavailable }

        /// <summary>What a card offers for this avatar: its job first, then what the avatar wears, then access.</summary>
        internal static State StateOf(GalleryAsset asset, OrbitersAttachment.GalleryReceipt installed, GalleryJob job, out string note)
        {
            note = null;
            if (job != null && !job.Terminal) { note = job.status; return job.Waiting ? State.Question : State.Working; }
            if (job != null && job.stage == GalleryJob.Failed) { note = job.error; return State.Failed; }
            if (installed != null)
            {
                var release = asset.fit?.release;
                if (asset.fit != null && asset.fit.Compatible && release != null && CompareVersions(release.version, installed.version) > 0)
                { note = $"{installed.version} → {release.version}"; return State.Update; }
                note = "Version " + installed.version;
                return State.Installed;
            }
            if (asset.fit == null || !asset.fit.Compatible) { note = FitNote(asset); return State.Unavailable; }
            return asset.access != null && asset.access.CanAdd ? State.Add : State.Buy;
        }

        internal static string FitNote(GalleryAsset asset)
        {
            var fit = asset.fit;
            switch (fit?.state)
            {
                case "platform": return (fit.platforms ?? new System.Collections.Generic.List<string>()).Count > 0 ? string.Join(" & ", fit.platforms.Select(AvatarPlatform.ShortLabel)) + " only" : "Other platform";
                case "base": return "Other bases";
                case "unknown-base": return "Specific bases";
                case "none": return "Coming soon";
                default: return "Not available";
            }
        }

        /// <summary>Short label of a price or access for the picture's corner chip, with its style.</summary>
        internal static (string text, string style) AccessChip(GalleryAsset asset)
        {
            switch (asset.access?.state)
            {
                case "creator": return ("Yours", "gallery-chip--owned");
                case "owned": return ("Owned", "gallery-chip--owned");
                case "included": return (asset.access.label?.Replace("Included with your ", "With your ") ?? "Included", "gallery-chip--included");
                case "free": return ("Free", "gallery-chip--free");
                default: return (asset.price != null ? asset.price.Label : "Paid", null);
            }
        }

        /// <summary>A spinner ring rotated in code (UI Toolkit has no keyframe animation).</summary>
        internal static VisualElement Spinner(string className)
        {
            var ring = Box(className);
            ring.schedule.Execute(() => ring.style.rotate = new Rotate(new Angle((float)(EditorApplication.timeSinceStartup * 360d % 360d)))).Every(16);
            return ring;
        }

        /// <summary>A soft band sweeping over a placeholder while it loads.</summary>
        internal static VisualElement Shimmer(VisualElement host)
        {
            var band = Box("gallery-card__shimmer", host);
            band.schedule.Execute(() =>
            {
                float width = host.resolvedStyle.width;
                if (float.IsNaN(width) || width <= 0f) return;
                float t = (float)(EditorApplication.timeSinceStartup * 0.7 % 1.0);
                band.style.left = Mathf.Lerp(-0.4f * width, width, t);
            }).Every(16);
            return band;
        }
    }
}
