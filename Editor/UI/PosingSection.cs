using System.Linq;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.Posing;
using Orbiters.Toolkit.Editor.VRChat.Posing;
using Orbiters.XRayGizmos;
using Orbiters.XRayGizmos.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    // Posing in the Scene view: see and pick the avatar's bones (XRay Gizmos), mirror edits across left and right, and
    // keep clothing that is not merged yet in the avatar's pose.
    internal sealed class PosingSection : AvatarSection
    {
        private readonly MyAvatar avatar;
        private readonly IconButton bones, symmetry, accessories;
        private readonly Label status;
        private readonly VisualElement list;

        internal PosingSection(MyAvatar avatar) : base("Posing")
        {
            this.avatar = avatar;
            var row = new VisualElement(); row.AddToClassList("posing-toggles"); Body.Add(row);
            bones = Tile(row, IconGlyph.Bones, "Bones", "Show bones: draw the avatar's bones in the Scene view. Click a bone to select it, then rotate it.", ToggleBones);
            symmetry = Tile(row, IconGlyph.Mirror, "Symmetry", "Symmetry: rotating or moving a bone on one side does the same on the other side.", ToggleSymmetry);
            accessories = Tile(row, IconGlyph.Clothes, "Clothing", "Preview clothing and accessories as they will be attached once built: they follow the avatar's pose " +
                "(VRCFury Armature Links, My Avatar attachments, or matching bone names). Switch it off to put them back where they were.", ToggleAccessories);
            var beta = new StageBadge(FeatureStage.Beta); beta.AddToClassList("posing-toggle__badge"); accessories.Add(beta);
            status = new Label(); status.AddToClassList("avatar-section__note"); Body.Add(status);
            list = new VisualElement(); list.AddToClassList("posing-list"); Body.Add(list);

            XRayGizmoService.Changed += Refresh; MirrorPoseService.Changed += Refresh; AccessoryPoseSync.Changed += Refresh;
            RegisterCallback<DetachFromPanelEvent>(_ => { XRayGizmoService.Changed -= Refresh; MirrorPoseService.Changed -= Refresh; AccessoryPoseSync.Changed -= Refresh; });
            Refresh();
        }

        private static IconButton Tile(VisualElement row, IconGlyph glyph, string label, string tooltip, System.Action action)
        {
            var tile = new IconButton(glyph, label, tooltip, action);
            tile.AddToClassList("posing-toggle");
            row.Add(tile);
            return tile;
        }

        private SkinnedMeshRenderer BodyRenderer => avatar ? XRayArmatureMeshGenerator.FindBestRenderer(avatar.gameObject) : null;
        private bool BonesOn => XRayGizmoService.Enabled && XRayGizmoService.TargetMode == XRayGizmoTargetMode.PinnedObject && XRayGizmoService.PinnedObject == avatar.gameObject;
        private bool AccessoriesOn => AccessoryPoseSync.Enabled && AccessoryPoseSync.Root == avatar.transform;

        private void ToggleBones()
        {
            bool on = !BonesOn;
            bones.SetOn(on);
            if (!on)
            {
                XRayGizmoService.SetEnabled(false);
                XRayBonePickingService.SetEnabled(false);
            }
            else
            {
                XRayGizmoService.SetTargetMode(XRayGizmoTargetMode.PinnedObject);
                XRayGizmoService.SetPinnedObject(avatar.gameObject);
                XRayGizmoService.SetEnabled(true);
                XRayBonePickingService.SetEnabled(true);
            }
            SceneView.RepaintAll();
            Refresh();
        }

        private void ToggleSymmetry()
        {
            // Symmetry is a mode: it stays on while nothing can be mirrored and follows the avatar being posed.
            bool on = !MirrorPoseService.Enabled;
            symmetry.SetOn(on);
            if (on) MirrorPoseService.Enable(avatar.transform, BodyRenderer);
            else MirrorPoseService.Disable();
            Refresh();
        }

        private void ToggleAccessories()
        {
            bool on = !AccessoriesOn;
            accessories.SetOn(on);
            if (on) AccessoryPoseSync.Enable(avatar.transform);
            else AccessoryPoseSync.Disable();
            Refresh();
        }

        private void Refresh()
        {
            if (!avatar) return;
            bones.SetOn(BonesOn);
            symmetry.SetOn(MirrorPoseService.Enabled);
            accessories.SetOn(AccessoriesOn);

            status.text = AccessoriesOn ? AccessoryPoseSync.LastStatus
                : MirrorPoseService.Enabled ? MirrorPoseService.LastStatus
                : BonesOn ? "Click a bone in the Scene view to select it, then rotate it with the Rotate tool (E)."
                : AccessoryPoseSync.LastStatus != "Off" ? AccessoryPoseSync.LastStatus
                : "Turn on what you need while posing in the Scene view. Hover a button to see what it does.";
            list.Clear();
            var found = AccessoriesOn ? AccessoryPoseSync.Accessories.ToList() : AccessoryPoseSync.Find(avatar.transform);
            foreach (var accessory in found)
            {
                var item = new VisualElement(); item.AddToClassList("posing-item");
                var dot = new VisualElement(); dot.AddToClassList("posing-item__dot"); dot.EnableInClassList("partial", accessory.Reason != null || accessory.Following < accessory.Total / 2); item.Add(dot);
                var name = new Label(accessory.Name); name.AddToClassList("posing-item__name"); item.Add(name);
                var count = new Label(accessory.Total <= 1 ? accessory.Rule : $"{accessory.Rule} · {accessory.Following}/{accessory.Total} bones"); count.AddToClassList("posing-item__count"); item.Add(count);
                var target = accessory.Root;
                item.RegisterCallback<PointerDownEvent>(_ => { Selection.activeTransform = target; EditorGUIUtility.PingObject(target); });
                item.tooltip = (accessory.Reason != null ? accessory.Reason + "\n" : "") + "Click to select this accessory.";
                list.Add(item);
            }
        }
    }
}
