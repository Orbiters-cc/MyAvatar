using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    internal sealed class TextureDropElement : VisualElement
    {
        internal TextureDropElement(Action<string[]> dropped)
        {
            AddToClassList("texture-drop"); focusable = true;
            var icon = new Image { image = EditorGUIUtility.IconContent("TextAsset Icon").image, scaleMode = ScaleMode.ScaleToFit };
            icon.AddToClassList("drop-icon"); Add(icon);
            Add(new Label("Drop your texture set") { name = "drop-title" });
            Add(new Label("PNG, JPG or TGA · multiple files or a folder") { name = "drop-hint" });
            var browse = new Button(() => {
                string path = EditorUtility.OpenFolderPanel("Choose a texture folder", "", "");
                if (!string.IsNullOrEmpty(path)) dropped(new[] { path });
            }) { text = "Choose folder…" };
            browse.AddToClassList("mcb-button");
            Add(browse);
            generateVisualContent += DrawBorder;
            RegisterCallback<DragUpdatedEvent>(e => { DragAndDrop.visualMode = DragAndDropVisualMode.Copy; AddToClassList("drag-over"); MarkDirtyRepaint(); e.StopPropagation(); });
            RegisterCallback<DragLeaveEvent>(_ => Leave());
            RegisterCallback<DragExitedEvent>(_ => Leave());
            RegisterCallback<DragPerformEvent>(e => {
                var paths = DragAndDrop.paths.Concat(DragAndDrop.objectReferences.Select(AssetDatabase.GetAssetPath)).Where(p => !string.IsNullOrEmpty(p)).Distinct().ToArray();
                DragAndDrop.AcceptDrag(); Leave(); dropped(paths); e.StopPropagation();
            });
        }
        private void Leave() { RemoveFromClassList("drag-over"); MarkDirtyRepaint(); }
        private void DrawBorder(MeshGenerationContext context)
        {
            var p = context.painter2D;
            p.strokeColor = ClassListContains("drag-over") ? new Color(0,.855f,.427f) : new Color(.48f,.48f,.48f); p.lineWidth = 1.5f;
            var r = new Rect(3, 3, Mathf.Max(0, layout.width - 6), Mathf.Max(0, layout.height - 6));
            float radius = 15, left = r.xMin, top = r.yMin, right = r.xMax, bottom = r.yMax;
            void Line(Vector2 a, Vector2 b) { p.BeginPath(); p.MoveTo(a); p.LineTo(b); p.Stroke(); }
            for (float x = left + radius; x < right - radius; x += 12) { Line(new Vector2(x, top), new Vector2(Mathf.Min(x + 6, right-radius), top)); Line(new Vector2(x, bottom), new Vector2(Mathf.Min(x + 6,right-radius), bottom)); }
            for (float y = top + radius; y < bottom - radius; y += 12) { Line(new Vector2(left,y),new Vector2(left,Mathf.Min(y+6,bottom-radius))); Line(new Vector2(right,y),new Vector2(right,Mathf.Min(y+6,bottom-radius))); }
            foreach (var corner in new[] { (new Vector2(left+radius,top+radius),180f), (new Vector2(right-radius,top+radius),270f), (new Vector2(right-radius,bottom-radius),0f), (new Vector2(left+radius,bottom-radius),90f) })
            { p.BeginPath(); p.Arc(corner.Item1,radius,new Angle(corner.Item2),new Angle(corner.Item2+90)); p.Stroke(); }
        }
    }
}
