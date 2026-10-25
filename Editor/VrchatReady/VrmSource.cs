using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Orbiters.MyAvatar.Editor.Vrm;
using UnityEngine;

// The optional assemblies of Make it VRChat ready: VRCFury's menu toggles for the expressions, and the tests that build
// VRM components with UniVRM.
[assembly: InternalsVisibleTo("Orbiters.MyAvatar.Editor.VrchatReady.VRCFury")]
[assembly: InternalsVisibleTo("Orbiters.MyAvatar.Editor.Tests.Vrm")]

namespace Orbiters.MyAvatar.Editor.VrchatReady
{
    /// <summary>
    /// Reads one VRM version's components (UniVRM's), registered by an assembly compiled only when UniVRM is there, as
    /// the VRM writers are (<see cref="VrmExport.Writers"/>).
    /// </summary>
    internal interface IVrmReader
    {
        /// <summary>What the model's VRM components say, or null when <paramref name="root"/> holds none of this version's.</summary>
        VrmSource Read(GameObject root);
    }

    /// <summary>
    /// What a VRM model's own components say, in the terms VRChat's components need: where the player looks from, the
    /// expressions (vowels, blink, emotions...), how its eyes turn, its spring bones and their colliders, and the
    /// components only VRM apps read. The VRM → VRChat direction of <see cref="VrmModel"/>, which reads a VRChat avatar
    /// for a VRM file; its expression and collider types are shared.
    /// </summary>
    internal sealed class VrmSource
    {
        /// <summary>"VRM 0.x" or "VRM 1.0".</summary>
        public string Format;
        /// <summary>What the file says about the model: its name, authors and license (shown, never changed).</summary>
        public string Title, Authors, License;
        /// <summary>The license limits who may use the model as an avatar (only its author, or people the author allows).</summary>
        public bool Restricted;
        /// <summary>The first person bone (the head) and the view point the VRM gives in its space (0.x first person, 1.0 look-at).</summary>
        public Transform Head;
        public Vector3? ViewOffset;
        /// <summary>The eyes the VRM's look-at turns, and whether it turns bones (else it moves blend shapes).</summary>
        public Transform LeftEye, RightEye;
        public bool EyeBones;
        /// <summary>How far the eyes turn, in degrees: up, down, toward the nose and away from it.</summary>
        public float LookUp = 10f, LookDown = 10f, LookInner = 10f, LookOuter = 10f;
        /// <summary>Every expression with blend shapes, its name always set (VRM's own name for a preset).</summary>
        public readonly List<VrmModel.Expression> Expressions = new List<VrmModel.Expression>();
        public readonly List<Spring> Springs = new List<Spring>();
        /// <summary>The components only VRM apps read: VRChat doesn't allow them on an avatar.</summary>
        public readonly List<Component> Components = new List<Component>();
        /// <summary>VRM 1.0 node constraints: removed with the rest, not converted.</summary>
        public int Constraints;

        /// <summary>
        /// A spring as VRM simulates it. VRM 0.x: every bone under each root moves (a leaf points 7 cm on along its bone);
        /// VRM 1.0: one root, its <see cref="Chain"/> of joints to the tip. Stiffness, gravity and drag are VRM's.
        /// </summary>
        internal sealed class Spring
        {
            public string Name;
            public readonly List<Transform> Roots = new List<Transform>();
            /// <summary>VRM 1.0: the joints from the root to the tip (which doesn't turn); null in VRM 0.x.</summary>
            public List<Transform> Chain;
            public float Stiffness = 1f, GravityPower, Drag = .4f;
            /// <summary>The joints' collision radius, in metres.</summary>
            public float Radius = .02f;
            public Vector3 GravityDirection = Vector3.down;
            /// <summary>The colliders it collides with; the same collider is the same object in every spring.</summary>
            public readonly List<VrmModel.Collider> Colliders = new List<VrmModel.Collider>();
        }
    }
}
