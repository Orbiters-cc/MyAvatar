using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Orbiters.MyAvatar.Editor.Optimization;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    // Under the download: the real size of the last build, kept apart from the estimate, and "What weighs most", the parts
    // of the estimate and its biggest pieces (each a click away). All from what the estimate measured: nothing more runs.
    internal sealed partial class OptimizationSection
    {
        private const string WeighsOpenKey = "Orbiters.MyAvatar.Optimization.WeighsOpen";
        private const int ShownPieces = 8;

        private static readonly (DownloadSize.PieceKind Kind, string Name, IconGlyph Glyph, Color Color)[] Kinds =
        {
            (DownloadSize.PieceKind.Texture, "Textures", IconGlyph.Grid, new Color(0f, .855f, .427f)),
            (DownloadSize.PieceKind.Mesh, "Meshes", IconGlyph.Sculpt, new Color(.31f, .64f, 1f)),
            (DownloadSize.PieceKind.BlendShapes, "Blendshapes", IconGlyph.Sliders, new Color(.71f, .55f, 1f)),
            (DownloadSize.PieceKind.Animation, "Animations", IconGlyph.Clock, new Color(1f, .69f, .125f)),
            (DownloadSize.PieceKind.Shader, "Shaders", IconGlyph.Render, new Color(1f, .48f, .42f)),
            (DownloadSize.PieceKind.Audio, "Sounds", IconGlyph.Sound, new Color(.24f, .84f, .82f)),
            (DownloadSize.PieceKind.Other, "Other", IconGlyph.Layers, new Color(.45f, .47f, .52f)),
        };

        private static bool WeighsOpen
        {
            get => SessionState.GetBool(WeighsOpenKey, false);
            set => SessionState.SetBool(WeighsOpenKey, value);
        }

        // The fold's header and body, made with the card (its contents follow each estimate).
        private void WeighsFold(EstimateCard view)
        {
            view.WeighsHead = new VisualElement { focusable = true }; view.WeighsHead.AddToClassList("opt-weighs__head"); view.Card.Add(view.WeighsHead);
            var chevron = new VectorIcon(IconGlyph.Chevron); chevron.AddToClassList("opt-weighs__chevron"); view.WeighsHead.Add(chevron);
            var label = new Label("What weighs most"); label.AddToClassList("opt-weighs__title"); view.WeighsHead.Add(label);
            view.WeighsAside = new Label(); view.WeighsAside.AddToClassList("opt-weighs__aside"); view.WeighsHead.Add(view.WeighsAside);
            view.Weighs = new VisualElement(); view.Weighs.AddToClassList("opt-weighs"); view.Card.Add(view.Weighs);
            void Toggle()
            {
                WeighsOpen = !WeighsOpen;
                view.WeighsHead.EnableInClassList("opt-weighs__head--open", WeighsOpen);
                Display(view.Weighs, WeighsOpen && view.Weighs.childCount > 0);
            }
            // Opens on press.
            view.WeighsHead.RegisterCallback<PointerDownEvent>(evt => { if (evt.button == 0) Toggle(); });
            view.WeighsHead.RegisterCallback<KeyDownEvent>(evt => { if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.Space) Toggle(); });
            view.WeighsHead.EnableInClassList("opt-weighs__head--open", WeighsOpen);
            view.WeighsHead.tooltip = "What the estimated download is made of: each part as compressed in the bundle, and the pieces that weigh most. Click one to show it.";
        }

        private void ShowWeighs(EstimateCard view, DownloadSize.Parts parts)
        {
            view.Weighs.Clear();
            bool any = parts != null && parts.Bundle > 0;
            Display(view.WeighsHead, any);
            if (!any) { Display(view.Weighs, false); return; }
            long bundle = parts.Bundle;
            var top = parts.Pieces.OrderByDescending(p => p.Bytes).FirstOrDefault();
            view.WeighsAside.text = top != null ? top.Name + " · " + Percent(top.Bytes, bundle) : "";

            var totals = new (DownloadSize.PieceKind Kind, long Bytes)[]
            {
                (DownloadSize.PieceKind.Texture, parts.Textures), (DownloadSize.PieceKind.Mesh, parts.Meshes), (DownloadSize.PieceKind.BlendShapes, parts.BlendShapes),
                (DownloadSize.PieceKind.Animation, parts.Animations), (DownloadSize.PieceKind.Shader, parts.Shaders), (DownloadSize.PieceKind.Audio, parts.Audio),
                (DownloadSize.PieceKind.Other, parts.Other),
            };
            var bar = new BudgetBar(); bar.AddToClassList("orbiters-budget--inline"); bar.AddToClassList("opt-weighs__bar");
            bar.SetSegments(totals.Where(t => t.Bytes > 0).Select(t =>
            {
                var kind = Kinds.First(k => k.Kind == t.Kind);
                return new BudgetBar.Segment(kind.Name + " " + Size(t.Bytes) + " · " + Percent(t.Bytes, bundle), t.Bytes, kind.Color);
            }));
            view.Weighs.Add(bar);

            foreach (var piece in parts.Pieces.OrderByDescending(p => p.Bytes).Take(ShownPieces))
            {
                var kind = Kinds.First(k => k.Kind == piece.Kind);
                var row = new VisualElement(); row.AddToClassList("opt-cost"); row.AddToClassList("opt-piece");
                var icon = new VectorIcon(kind.Glyph); icon.AddToClassList("opt-piece__icon"); icon.AddToClassList("opt-piece__icon--" + piece.Kind.ToString().ToLowerInvariant());
                row.Add(icon);
                var texts = new VisualElement(); texts.AddToClassList("opt-cost__texts"); row.Add(texts);
                var name = new Label(piece.Name); name.AddToClassList("opt-cost__name"); texts.Add(name);
                string owner = piece.Item ?? (string.IsNullOrEmpty(piece.Owner) ? null : piece.Owner);
                if (owner != null) { var where = new Label(owner); where.AddToClassList("opt-cost__owner"); where.EnableInClassList("opt-cost__owner--item", piece.Item != null); texts.Add(where); }
                var amount = new Label(Size(piece.Bytes)); amount.AddToClassList("opt-cost__amount"); row.Add(amount);
                var share = new Label(Percent(piece.Bytes, bundle)); share.AddToClassList("opt-cost__share"); row.Add(share);
                bool shows = piece.Asset != 0 || !string.IsNullOrEmpty(piece.Owner);
                row.tooltip = kind.Name.TrimEnd('s') + (piece.Item != null ? " of “" + piece.Item + "”" : "") + (string.IsNullOrEmpty(piece.Owner) ? "" : ", on " + piece.Owner) +
                              ". Its share of the compressed download, estimated." + (shows ? " Click to show it." : "");
                if (shows) { var p = piece; Pressable(row, () => Show(p.Asset, p.Owner)); }
                else row.AddToClassList("opt-cost--still");
                view.Weighs.Add(row);
            }
            Display(view.Weighs, WeighsOpen);
        }

        // VRChat's figure for a part of the bundle (it shows sizes about 5% under the file).
        private static string Size(long bytes) => DownloadSize.Amount(DownloadSize.AsShown(bytes));

        private static string Percent(long part, long whole) =>
            whole <= 0 ? "" : part * 100 < whole ? "<1%" : Mathf.RoundToInt(100f * part / whole).ToString(CultureInfo.InvariantCulture) + "%";

        // ---- The last build, measured ----

        private void ShowMeasured(EstimateCard view, bool quest)
        {
            var root = TextureOptimization.AvatarRoot(avatar);
            string platform = !quest ? AvatarPlatform.Pc : AvatarPlatform.Current != AvatarPlatform.Pc ? AvatarPlatform.Current : AvatarPlatform.Android;
            var record = MeasuredBuilds.For(root, platform);
            Display(view.Measured, record != null);
            if (record == null) return;
            string text = "Last build " + DownloadSize.Megabytes(DownloadSize.AsShown(record.Bytes)) + " · " + AvatarPlatform.ShortLabel(platform) + ", " + Ago(record.When);
            if (record.Estimate > 0) text += " · estimated then " + DownloadSize.Megabytes(record.Estimate);
            var stale = MeasuredBuilds.Stale(record, OptimizationEstimate.Fingerprint(avatar));
            if (stale == true) text += " · changed since";
            view.Measured.text = text;
            view.Measured.EnableInClassList("opt-measured--stale", stale == true);
            view.Measured.tooltip = "The bundle the VRChat SDK built for " + AvatarPlatform.Label(platform) + ": " + DownloadSize.Megabytes(record.Bytes) +
                                    " on disk, shown as VRChat shows it (about 5% less). Kept apart from the estimate, which it never changes" +
                                    (stale == true ? "; the avatar or its settings changed since it was built." : stale == null ? "; from an earlier Unity session." : ".");
        }

        private static string Ago(DateTime when)
        {
            var span = DateTime.UtcNow - when;
            if (span.TotalMinutes < 1) return "just now";
            if (span.TotalHours < 1) return Mathf.FloorToInt((float)span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min ago";
            if (span.TotalDays < 1) return Mathf.FloorToInt((float)span.TotalHours).ToString(CultureInfo.InvariantCulture) + " h ago";
            int days = Mathf.FloorToInt((float)span.TotalDays);
            return days == 1 ? "yesterday" : days.ToString(CultureInfo.InvariantCulture) + " days ago";
        }
    }
}
