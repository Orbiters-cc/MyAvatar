using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.MyAvatar.Editor.Vrm;
using UnityEditor;
using UnityEngine;
using VRC.Core;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDKBase;

namespace Orbiters.MyAvatar.Editor.VrchatReady
{
    /// <summary>Writes the expressions as menu toggles; implemented with VRCFury's API by an assembly compiled only with VRCFury.</summary>
    internal interface IExpressionToggles
    {
        /// <summary>
        /// One menu toggle per expression under <paramref name="menuFolder"/>, one at a time, not saved, on a new object
        /// under <paramref name="root"/> registered with Undo; returns that object.
        /// </summary>
        GameObject Write(GameObject root, string menuFolder, IReadOnlyList<VrmModel.Expression> expressions);
    }

    /// <summary>
    /// Make it VRChat ready, in one Undo step: VRChat's avatar descriptor (and its pipeline manager) filled from the model,
    /// its VRM spring bones and colliders as PhysBones, its emotions as menu toggles, and the components only VRM apps read
    /// removed (VRChat doesn't allow them on an avatar).
    /// </summary>
    internal static class ReadyConversion
    {
        internal const string UndoName = "Make it VRChat ready";
        internal const string PhysBonesName = "PhysBones";
        internal const string ExpressionsMenu = "Expressions";

        /// <summary>Set by Orbiters.MyAvatar.Editor.VrchatReady.VRCFury when VRCFury is installed.</summary>
        internal static IExpressionToggles Toggles;

        internal sealed class Result
        {
            public VRCAvatarDescriptor Descriptor;
            public int PhysBones, Colliders, Toggles, Removed, Visemes;
            public bool EyeLook, Blink;
            public string Summary;
        }

        /// <summary>Makes the avatar ready as <paramref name="plan"/> says; everything or nothing, as one Undo step.</summary>
        internal static Result Apply(ReadyPlan plan)
        {
            if (plan == null || !plan.CanConvert) throw new InvalidOperationException(plan?.Problem ?? "Nothing to make ready.");
            if (plan.Root.GetComponentInParent<VRC_AvatarDescriptor>(true) != null) throw new InvalidOperationException("“" + plan.Root.name + "” already has an avatar descriptor.");
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(UndoName);
            var result = new Result();
            try
            {
                result.Colliders = PhysBones(plan, out result.PhysBones);
                result.Toggles = ExpressionToggles(plan);
                result.Removed = RemoveVrm(plan);
                result.Descriptor = Descriptor(plan, result);
                Undo.CollapseUndoOperations(group);
            }
            catch
            {
                // Nothing half done stays.
                Undo.CollapseUndoOperations(group);
                Undo.RevertAllDownToGroup(group);
                throw;
            }
            result.Summary = Summary(plan, result);
            Debug.Log("[My Avatar] “" + plan.Root.name + "” is VRChat ready: " + result.Summary);
            return result;
        }

        // ---- Descriptor ----

        private static VRCAvatarDescriptor Descriptor(ReadyPlan plan, Result result)
        {
            var root = plan.Root;
            var descriptor = Undo.AddComponent<VRCAvatarDescriptor>(root);
            if (!root.TryGetComponent<PipelineManager>(out _)) Undo.AddComponent<PipelineManager>(root);
            descriptor.ViewPosition = plan.ViewPosition;
            // VRChat's own animations, as a new descriptor gets them from its inspector (non-humanoid avatars have no idle
            // or gesture layer, but they never get here).
            descriptor.customizeAnimationLayers = false;
            descriptor.baseAnimationLayers = new[] { VRCAvatarDescriptor.AnimLayerType.Base, VRCAvatarDescriptor.AnimLayerType.Additive, VRCAvatarDescriptor.AnimLayerType.Gesture,
                    VRCAvatarDescriptor.AnimLayerType.Action, VRCAvatarDescriptor.AnimLayerType.FX }
                .Select(type => new VRCAvatarDescriptor.CustomAnimLayer { type = type, isDefault = true }).ToArray();
            descriptor.specialAnimationLayers = new[] { VRCAvatarDescriptor.AnimLayerType.Sitting, VRCAvatarDescriptor.AnimLayerType.TPose, VRCAvatarDescriptor.AnimLayerType.IKPose }
                .Select(type => new VRCAvatarDescriptor.CustomAnimLayer { type = type, isDefault = true }).ToArray();
            descriptor.customExpressions = false;
            descriptor.autoFootsteps = true;
            descriptor.autoLocomotion = true;

            if (plan.Face != null && plan.Visemes != null)
            {
                descriptor.lipSync = VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShape;
                descriptor.VisemeSkinnedMesh = plan.Face;
                descriptor.VisemeBlendShapes = plan.Visemes.Select(v => v ?? "").ToArray();
                result.Visemes = plan.VisemeCount;
            }
            else descriptor.lipSync = VRC_AvatarDescriptor.LipSyncStyle.Default;

            EyeLook(descriptor, plan, result);
            Colliders(descriptor, plan.Animator);
            EditorUtility.SetDirty(descriptor);
            return descriptor;
        }

        // The eye bones and how far they turn (each eye its own way when looking aside: one turns toward the nose, the other
        // away), and the blink. Rotations are the bones' local rotations, as the descriptor's inspector records them; turns
        // go around the avatar's own right and up axes from the eyes' rest pose.
        private static void EyeLook(VRCAvatarDescriptor descriptor, ReadyPlan plan, Result result)
        {
            var eyes = descriptor.customEyeLookSettings;
            if (eyes.eyeMovement == null) eyes.eyeMovement = new VRCAvatarDescriptor.CustomEyeLookSettings.EyeMovements { confidence = .5f, excitement = .5f };
            var left = plan.LeftEye;
            var right = plan.RightEye;
            bool bones = left != null && right != null;
            if (bones)
            {
                var root = plan.Root.transform;
                eyes.leftEye = left;
                eyes.rightEye = right;
                eyes.eyesLookingStraight = Rotations(left, right, Quaternion.identity, Quaternion.identity);
                var up = Quaternion.AngleAxis(-plan.LookUp, root.right);
                var down = Quaternion.AngleAxis(plan.LookDown, root.right);
                eyes.eyesLookingUp = Rotations(left, right, up, up);
                eyes.eyesLookingDown = Rotations(left, right, down, down);
                // Looking to the avatar's left: its left eye turns away from the nose, its right eye toward it.
                eyes.eyesLookingLeft = Rotations(left, right, Quaternion.AngleAxis(-plan.LookOuter, root.up), Quaternion.AngleAxis(-plan.LookInner, root.up));
                eyes.eyesLookingRight = Rotations(left, right, Quaternion.AngleAxis(plan.LookInner, root.up), Quaternion.AngleAxis(plan.LookOuter, root.up));
            }
            bool blink = plan.BlinkMesh != null && plan.BlinkMesh.sharedMesh != null && plan.BlinkMesh.sharedMesh.GetBlendShapeIndex(plan.Blink) >= 0;
            if (blink)
            {
                eyes.eyelidType = VRCAvatarDescriptor.EyelidType.Blendshapes;
                eyes.eyelidsSkinnedMesh = plan.BlinkMesh;
                // Blink, looking up, looking down: the last two stay off.
                eyes.eyelidsBlendshapes = new[] { plan.BlinkMesh.sharedMesh.GetBlendShapeIndex(plan.Blink), -1, -1 };
            }
            else eyes.eyelidType = VRCAvatarDescriptor.EyelidType.None;
            descriptor.customEyeLookSettings = eyes;
            descriptor.enableEyeLook = bones || blink;
            result.EyeLook = bones;
            result.Blink = blink;
        }

        private static VRCAvatarDescriptor.CustomEyeLookSettings.EyeRotations Rotations(Transform left, Transform right, Quaternion leftTurn, Quaternion rightTurn)
        {
            var l = Local(left, leftTurn);
            var r = Local(right, rightTurn);
            return new VRCAvatarDescriptor.CustomEyeLookSettings.EyeRotations { linked = Quaternion.Angle(l, r) < .01f, left = l, right = r };
        }

        // The eye's local rotation once turned by a world rotation from where it rests now.
        private static Quaternion Local(Transform eye, Quaternion turn)
        {
            var parent = eye.parent != null ? eye.parent.rotation : Quaternion.identity;
            return Quaternion.Inverse(parent) * (turn * eye.rotation);
        }

        // The automatic colliders (head, torso, hands, feet, fingers), measured as the descriptor's inspector measures them
        // (VRCAvatarDescriptorEditor3.UpdateAutoColliders).
        private static void Colliders(VRCAvatarDescriptor d, Animator animator)
        {
            if (animator == null || !animator.isHuman) return;
            Set(ref d.collider_head, VRCAvatarDescriptor.CalcHeadCollider(animator, d.ViewPosition, 1f));
            Set(ref d.collider_torso, VRCAvatarDescriptor.CalcTorsoCollider(animator));
            Set(ref d.collider_handL, VRCAvatarDescriptor.CalcPalmCollider(animator, true));
            Set(ref d.collider_handR, VRCAvatarDescriptor.CalcPalmCollider(animator, false));
            Set(ref d.collider_footL, VRCAvatarDescriptor.CalcFootCollider(animator, true));
            Set(ref d.collider_footR, VRCAvatarDescriptor.CalcFootCollider(animator, false));
            Set(ref d.collider_fingerIndexL, VRCAvatarDescriptor.CalcFingerCollider(animator, 0, true));
            Set(ref d.collider_fingerMiddleL, VRCAvatarDescriptor.CalcFingerCollider(animator, 1, true));
            Set(ref d.collider_fingerRingL, VRCAvatarDescriptor.CalcFingerCollider(animator, 2, true));
            Set(ref d.collider_fingerLittleL, VRCAvatarDescriptor.CalcFingerCollider(animator, 3, true));
            Set(ref d.collider_fingerIndexR, VRCAvatarDescriptor.CalcFingerCollider(animator, 0, false));
            Set(ref d.collider_fingerMiddleR, VRCAvatarDescriptor.CalcFingerCollider(animator, 1, false));
            Set(ref d.collider_fingerRingR, VRCAvatarDescriptor.CalcFingerCollider(animator, 2, false));
            Set(ref d.collider_fingerLittleR, VRCAvatarDescriptor.CalcFingerCollider(animator, 3, false));

            void Set(ref VRCAvatarDescriptor.ColliderConfig dest, VRCAvatarDescriptor.ColliderConfig config)
            {
                config.isMirrored = dest.isMirrored;
                if (dest.state == VRCAvatarDescriptor.ColliderConfig.State.Automatic) dest = config;
                dest.transform = config.transform;
            }
        }

        // ---- PhysBones ----

        // One collider per VRM collider (on its bone, shared by the springs that use it), then one PhysBone per group on an
        // object of its own under "PhysBones" (its root transform set), created with Undo once complete.
        private static int PhysBones(ReadyPlan plan, out int count)
        {
            count = 0;
            var made = new Dictionary<VrmModel.Collider, VRCPhysBoneCollider>();
            foreach (var source in plan.Colliders)
            {
                if (source?.Transform == null) continue;
                var collider = Undo.AddComponent<VRCPhysBoneCollider>(source.Transform.gameObject);
                collider.radius = source.Radius;
                collider.insideBounds = source.Inside;
                if (source.Plane)
                {
                    collider.shapeType = VRCPhysBoneColliderBase.ShapeType.Plane;
                    collider.position = source.Offset;
                    collider.rotation = Quaternion.FromToRotation(Vector3.up, source.Tail.sqrMagnitude > 1e-8f ? source.Tail : Vector3.up);
                }
                else if (source.Capsule)
                {
                    // VRM gives the capsule's segment; a PhysBone collider's height counts its round ends too.
                    var axis = source.Tail - source.Offset;
                    collider.shapeType = VRCPhysBoneColliderBase.ShapeType.Capsule;
                    collider.position = (source.Offset + source.Tail) / 2;
                    collider.rotation = axis.sqrMagnitude > 1e-10f ? Quaternion.FromToRotation(Vector3.up, axis) : Quaternion.identity;
                    collider.height = axis.magnitude + 2 * source.Radius;
                }
                else
                {
                    collider.shapeType = VRCPhysBoneColliderBase.ShapeType.Sphere;
                    collider.position = source.Offset;
                }
                made[source] = collider;
            }
            if (plan.PhysBones.Count == 0) return made.Count;
            var holder = new GameObject(PhysBonesName);
            holder.transform.SetParent(plan.Root.transform, false);
            foreach (var group in plan.PhysBones)
            {
                if (group.Root == null) continue;
                var host = new GameObject(group.Name);
                host.transform.SetParent(holder.transform, false);
                var bone = host.AddComponent<VRCPhysBone>();
                bone.version = VRCPhysBoneBase.Version.Version_1_1;
                bone.integrationType = VRCPhysBoneBase.IntegrationType.Simplified;
                bone.rootTransform = group.Root;
                bone.ignoreTransforms = group.Ignored.Where(t => t != null).Distinct().ToList();
                bone.endpointPosition = group.Endpoint;
                // The bone strands hang from stays still; a lone strand's root turns toward its first child, as in VRM.
                bone.multiChildType = group.StillRoot ? VRCPhysBoneBase.MultiChildType.Ignore : VRCPhysBoneBase.MultiChildType.First;
                bone.pull = group.Pull;
                bone.spring = group.Spring;
                bone.stiffness = group.Stiffness;
                bone.gravity = group.Gravity;
                bone.gravityFalloff = 0f;
                bone.radius = group.Radius;
                bone.colliders = group.Colliders.Where(made.ContainsKey).Select(c => (VRCPhysBoneColliderBase)made[c]).ToList();
                count++;
            }
            Undo.RegisterCreatedObjectUndo(holder, UndoName);
            return made.Count;
        }

        // ---- Expressions ----

        private static int ExpressionToggles(ReadyPlan plan)
        {
            if (plan.Toggles.Count == 0 || Toggles == null) return 0;
            var made = Toggles.Write(plan.Root, ExpressionsMenu, plan.Toggles);
            return made != null ? plan.Toggles.Count : 0;
        }

        // ---- VRM's components ----

        // A component another one needs goes after it (UniVRM 1.0's instance needs its humanoid). An object right under the
        // root that held only VRM components and holds nothing else now (UniVRM's "secondary") goes too; bones stay, even
        // empty ones (a spring's tip is a PhysBone's now).
        private static int RemoveVrm(ReadyPlan plan)
        {
            var left = plan.Remove.Where(c => c != null && !(c is Transform)).Distinct().ToList();
            var holders = left.Select(c => c.gameObject).Where(g => g != plan.Root && g.transform.parent == plan.Root.transform).Distinct().ToList();
            int removed = 0;
            for (int pass = 0; left.Count > 0; pass++)
            {
                if (pass > plan.Remove.Count) throw new InvalidOperationException("These components can't be removed: " + string.Join(", ", left.Select(c => c.GetType().Name).Distinct()) + ".");
                var free = left.Where(c => !left.Any(other => other != c && other.gameObject == c.gameObject && Requires(other.GetType(), c.GetType()))).ToList();
                if (free.Count == 0) free = left.ToList();
                foreach (var component in free)
                {
                    Undo.DestroyObjectImmediate(component);
                    removed++;
                }
                left = left.Where(c => c != null && !free.Contains(c)).ToList();
            }
            var bones = new HashSet<Transform>(plan.Root.GetComponentsInChildren<SkinnedMeshRenderer>(true).SelectMany(r => r.bones).Where(b => b != null));
            foreach (var holder in holders)
                if (holder != null && holder.transform.childCount == 0 && holder.GetComponents<Component>().Length == 1 && !bones.Contains(holder.transform))
                    Undo.DestroyObjectImmediate(holder);
            return removed;
        }

        private static bool Requires(Type type, Type required)
        {
            for (var t = type; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                foreach (RequireComponent attribute in t.GetCustomAttributes(typeof(RequireComponent), false))
                    if (new[] { attribute.m_Type0, attribute.m_Type1, attribute.m_Type2 }.Any(r => r != null && r.IsAssignableFrom(required))) return true;
            return false;
        }

        private static string Summary(ReadyPlan plan, Result result)
        {
            var parts = new List<string>
            {
                result.Visemes > 0 ? result.Visemes + " visemes" : "no lip sync",
                result.EyeLook ? "eye look" + (result.Blink ? " and blink" : "") : result.Blink ? "blink" : "no eye look",
            };
            if (result.PhysBones > 0) parts.Add(Count(result.PhysBones, "PhysBone") + (result.Colliders > 0 ? " with " + Count(result.Colliders, "collider") : ""));
            if (result.Toggles > 0) parts.Add(Count(result.Toggles, "expression") + " in the menu");
            if (result.Removed > 0) parts.Add(Count(result.Removed, "VRM component") + " removed");
            return string.Join(", ", parts) + ".";
        }

        internal static string Count(int n, string noun) => n + " " + noun + (n == 1 ? "" : "s");
    }
}
