using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor.VRChat;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor
{
    // Asks Orbiters AI what local rules could not decide for an accessory: which variant, which bone a prop goes to, where
    // unmatched clothing bones belong and what needs a manual step. Names, paths inside the accessory and the avatar,
    // component types and the drop's text documentation are sent; no images and no computer paths.
    internal static class AccessoryAi
    {
        [Serializable] private sealed class Answer { public string candidate, target; public LinkAnswer[] links; public SetupAnswer[] setup; public string[] warnings; }
        [Serializable] private sealed class LinkAnswer { public string bone, avatarBone; public float confidence; }
        [Serializable] private sealed class SetupAnswer { public string @object, reason; }

        internal sealed class Request
        {
            public object payload;
            public readonly Dictionary<string, Transform> avatarBones = new Dictionary<string, Transform>(), bones = new Dictionary<string, Transform>();
            public readonly Dictionary<string, GameObject> objects = new Dictionary<string, GameObject>();
            public readonly Dictionary<string, AccessoryCandidates.Candidate> candidates = new Dictionary<string, AccessoryCandidates.Candidate>();
            public bool worthAsking;
        }

        internal sealed class Result
        {
            public AccessoryCandidates.Candidate candidate;
            public Transform target;
            public readonly List<(Transform from, Transform to)> links = new List<(Transform, Transform)>();
            public readonly List<(GameObject target, string reason)> setup = new List<(GameObject, string)>();
            public string[] warnings = Array.Empty<string>();
        }

        private static readonly Type[] Plain = { typeof(Transform), typeof(MeshFilter), typeof(MeshRenderer), typeof(SkinnedMeshRenderer) };

        /// <summary>Before anything is installed: only which of several equally good variants to use.</summary>
        internal static Request ForCandidates(Transform avatarRoot, AccessoryCandidates.Choice choice, List<AccessoryImport.Doc> docs)
        {
            var request = new Request();
            var list = new[] { choice.best }.Concat(choice.rivals).ToList();
            for (int i = 0; i < list.Count && i < 24; i++) request.candidates["c" + i] = list[i];
            request.worthAsking = request.candidates.Count > 1;
            request.payload = new
            {
                avatar = new { bones = AvatarBones(avatarRoot, null, request) },
                accessory = new
                {
                    name = choice.best.name,
                    candidates = request.candidates.Select(p => Candidate(p.Key, p.Value)).ToArray(),
                    selected = (string)null, rigid = false,
                },
                docs = Docs(docs),
            };
            return request;
        }

        /// <summary>After the local install: the bone a guessed prop goes to, unmatched clothing bones and manual steps.</summary>
        internal static Request ForPlan(AttachmentPlan plan, AccessoryCandidates.Candidate installed, List<AccessoryImport.Doc> docs)
        {
            var request = new Request();
            var root = plan.Root.transform;
            var avatarIds = new Dictionary<Transform, string>();
            var avatarBones = AvatarBones(plan.Avatar, avatarIds, request);
            var bones = new List<object>();
            var ids = new Dictionary<Transform, string>();
            var matches = plan.Matches.Where(m => m.Matched).ToDictionary(m => m.Source, m => m.Target);
            foreach (var bone in plan.Matches.Select(m => m.Source).Concat(plan.Unmatched).Distinct().Take(400)) ids[bone] = "s" + ids.Count;
            foreach (var pair in ids)
            {
                request.bones[pair.Value] = pair.Key;
                bones.Add(new
                {
                    id = pair.Value, name = pair.Key.name, path = AnimationUtility.CalculateTransformPath(pair.Key, root),
                    parent = pair.Key.parent != null && ids.TryGetValue(pair.Key.parent, out var parent) ? parent : null,
                    match = matches.TryGetValue(pair.Key, out var m) && avatarIds.TryGetValue(m, out var a) ? a : null,
                });
            }
            var unresolved = plan.Unmatched.Where(ids.ContainsKey).Select(b => ids[b]).ToArray();
            var objects = new List<object>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                var types = t.GetComponents<Component>().Where(c => c != null && !Plain.Contains(c.GetType())).Select(c => c.GetType().Name).Distinct().ToArray();
                bool noted = plan.Notes.Any(n => n.Target == t.gameObject || n.Target is Component c && c != null && c.gameObject == t.gameObject);
                if (types.Length == 0 && !noted) continue;
                if (objects.Count >= 80) break;
                string id = "o" + objects.Count;
                request.objects[id] = t.gameObject;
                objects.Add(new { id, path = t == root ? t.name : AnimationUtility.CalculateTransformPath(t, root), components = types });
            }
            var docList = Docs(docs);
            bool rigid = plan.Kind == AttachmentKind.Rigid;
            request.worthAsking = rigid && plan.ParentGuessed || unresolved.Length > 0 || docList.Length > 0 && objects.Count > 0;
            request.payload = new
            {
                avatar = new { bones = avatarBones },
                accessory = new
                {
                    name = plan.Root.name,
                    candidates = installed != null ? new[] { Candidate("c0", installed) } : Array.Empty<object>(),
                    selected = installed != null ? "c0" : null,
                    bones = bones.ToArray(), unresolved, rigid, objects = objects.ToArray(),
                },
                docs = docList,
            };
            return request;
        }

        internal static async Task<Result> RequestAsync(string token, Request request, CancellationToken cancellation)
        {
            var answer = await OrbitersApi.SendAsync<Answer>(OrbitersEnvironment.ApiUrl("myavatar/accessory-plan"), token, request.payload, cancellation);
            var result = new Result { warnings = answer?.warnings ?? Array.Empty<string>() };
            if (answer == null) return result;
            // Only ids this request offered are trusted; the server checks the same.
            if (answer.candidate != null && request.candidates.TryGetValue(answer.candidate, out var candidate)) result.candidate = candidate;
            if (answer.target != null && request.avatarBones.TryGetValue(answer.target, out var target)) result.target = target;
            foreach (var link in answer.links ?? Array.Empty<LinkAnswer>())
                if (link.confidence >= .8f && request.bones.TryGetValue(link.bone ?? "", out var from) && request.avatarBones.TryGetValue(link.avatarBone ?? "", out var to))
                    result.links.Add((from, to));
            foreach (var note in answer.setup ?? Array.Empty<SetupAnswer>())
                if (note.@object != null && request.objects.TryGetValue(note.@object, out var go) && !string.IsNullOrWhiteSpace(note.reason))
                    result.setup.Add((go, note.reason.Trim()));
            return result;
        }

        private static object[] AvatarBones(Transform avatarRoot, Dictionary<Transform, string> ids, Request request)
        {
            var body = AttachmentPlanner.Body(avatarRoot);
            var index = Orbiters.Toolkit.Armature.AvatarBoneIndex.Build(avatarRoot, Orbiters.Toolkit.Editor.Posing.AvatarSkeleton.Bones(avatarRoot, body));
            var result = new List<object>();
            foreach (var bone in index.Bones.OrderBy(b => AnimationUtility.CalculateTransformPath(b, avatarRoot)).Take(400))
            {
                string id = "a" + result.Count;
                ids?.Add(bone, id);
                request.avatarBones[id] = bone;
                result.Add(new { id, name = bone.name, path = AnimationUtility.CalculateTransformPath(bone, avatarRoot), human = index.TryGetHumanoid(bone, out var human) ? human.ToString() : null });
            }
            return result.ToArray();
        }

        private static object Candidate(string id, AccessoryCandidates.Candidate c) => new
        {
            id, name = c.name, path = c.path.StartsWith("Assets/", StringComparison.Ordinal) ? c.path.Substring(7) : c.path,
            kind = c.path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ? "prefab" : "model",
            setup = VrcFury.Features(c.asset).Select(f => "VRCFury " + f.kind).Distinct().ToArray(),
            renderers = c.renderers,
            bones = c.asset.GetComponentsInChildren<SkinnedMeshRenderer>(true).SelectMany(r => r.bones).Where(b => b != null).Distinct().Count(),
        };

        private static object[] Docs(List<AccessoryImport.Doc> docs)
        {
            int total = 0;
            var result = new List<object>();
            foreach (var doc in docs ?? new List<AccessoryImport.Doc>())
            {
                if (result.Count >= AccessoryImport.MaxDocs || total >= 12000) break;
                var text = doc.text.Length > Math.Min(AccessoryImport.MaxDocChars, 12000 - total) ? doc.text.Substring(0, Math.Min(AccessoryImport.MaxDocChars, 12000 - total)) : doc.text;
                total += text.Length;
                result.Add(new { name = doc.name, text });
            }
            return result.ToArray();
        }
    }
}
