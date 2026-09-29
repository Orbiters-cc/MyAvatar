using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Armature;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor
{
    // A second package can reload scripts after another avatar's import finished. Keep the remaining local texture/AI
    // work in SessionState until the Inspector acknowledges it, including while texture import is awaiting I/O.
    internal static class AccessoryFollowUps
    {
        internal const string Key = "Orbiters.MyAvatar.AccessoryFollowUps";
        private static readonly HashSet<int> Claimed = new HashSet<int>();
        [Serializable] private sealed class Store { public List<Record> records = new List<Record>(); }
        [Serializable] private sealed class Record { public int avatar; public string token; public List<State> outcomes = new List<State>(); }
        [Serializable] private sealed class Link { public int source, target; }
        [Serializable] private sealed class Note { public int target; public string reason; }
        [Serializable] private sealed class State
        {
            public int attachment;
            public string candidate, staging;
            public List<AccessoryImport.Doc> docs;
            public List<string> images;
            public bool hasPlan, parentGuessed;
            public AttachmentKind kind;
            public List<Link> matches = new List<Link>();
            public List<int> unresolved = new List<int>();
            public List<Note> notes = new List<Note>();
        }

        private static List<Record> Read()
        {
            string json = SessionState.GetString(Key, null);
            return string.IsNullOrEmpty(json) ? new List<Record>() : JsonUtility.FromJson<Store>(json)?.records ?? new List<Record>();
        }

        private static void Write(List<Record> records)
        {
            if (records.Count == 0) SessionState.EraseString(Key);
            else SessionState.SetString(Key, JsonUtility.ToJson(new Store { records = records }));
        }

        private static int Id(UnityEngine.Object value) => value ? value.GetInstanceID() : 0;
        private static T Find<T>(int id) where T : UnityEngine.Object => id == 0 ? null : EditorUtility.InstanceIDToObject(id) as T;

        internal static void Save(MyAvatar avatar, List<AccessoryService.Outcome> outcomes)
        {
            var record = new Record { avatar = avatar.GetInstanceID(), token = Guid.NewGuid().ToString("N") };
            foreach (var outcome in outcomes)
            {
                var plan = outcome.plan;
                record.outcomes.Add(new State {
                    attachment = Id(outcome.attachment), candidate = outcome.candidate?.path, staging = outcome.staging,
                    docs = outcome.docs, images = outcome.images, hasPlan = plan != null,
                    kind = plan?.Kind ?? AttachmentKind.Empty, parentGuessed = plan?.ParentGuessed ?? false,
                    matches = plan?.Matches.Select(m => new Link { source = Id(m.Source), target = Id(m.Target) }).ToList() ?? new List<Link>(),
                    unresolved = plan?.Unmatched.Concat(plan.Ambiguous.Select(m => m.Source)).Where(b => b).Select(Id).Distinct().ToList() ?? new List<int>(),
                    notes = plan?.Notes.Select(n => new Note { target = Id(n.Target), reason = n.Reason }).ToList() ?? new List<Note>(),
                });
            }
            var records = Read(); records.Add(record); Write(records);
        }

        internal static bool Has(MyAvatar avatar) => avatar && !Claimed.Contains(avatar.GetInstanceID()) && Read().Any(r => r.avatar == avatar.GetInstanceID());
        internal static IEnumerable<string> Staging() => Read().SelectMany(r => r.outcomes).Select(o => o.staging);

        internal static List<AccessoryService.Outcome> Take(MyAvatar avatar)
        {
            if (!Has(avatar)) return null;
            var record = Read().First(r => r.avatar == avatar.GetInstanceID());
            Claimed.Add(record.avatar);
            var outcomes = new List<AccessoryService.Outcome>();
            foreach (var state in record.outcomes)
            {
                var attachment = Find<OrbitersAttachment>(state.attachment);
                // A deleted accessory's images must not fall back to recoloring the entire avatar.
                if (state.attachment != 0 && !attachment)
                {
                    outcomes.Add(new AccessoryService.Outcome { staging = state.staging, followUp = record.token });
                    continue;
                }
                var outcome = new AccessoryService.Outcome {
                    attachment = attachment, docs = state.docs, images = state.images, staging = state.staging, followUp = record.token,
                    candidate = string.IsNullOrEmpty(state.candidate) ? null : AccessoryCandidates.Choose(new[] { state.candidate }).best,
                };
                if (state.hasPlan && attachment)
                {
                    var plan = outcome.plan = new AttachmentPlan {
                        Root = attachment.gameObject, Avatar = avatar.transform, Kind = state.kind, ParentGuessed = state.parentGuessed,
                        Mode = attachment.mode, Parent = attachment.parent, Body = attachment.body,
                    };
                    foreach (var link in state.matches)
                    {
                        var source = Find<Transform>(link.source); var target = Find<Transform>(link.target);
                        if (source) plan.Matches.Add(new BoneMatch(source, target, target ? BoneMatchKind.ExactName : BoneMatchKind.None, target ? 1f : 0f));
                    }
                    plan.Unmatched.AddRange(state.unresolved.Select(Find<Transform>).Where(t => t));
                    foreach (var note in state.notes) plan.Notes.Add(new SetupNote(Find<UnityEngine.Object>(note.target), note.reason));
                }
                outcomes.Add(outcome);
            }
            return outcomes;
        }

        internal static void Complete(MyAvatar avatar, List<AccessoryService.Outcome> outcomes)
        {
            var tokens = new HashSet<string>(outcomes.Select(o => o.followUp).Where(t => !string.IsNullOrEmpty(t)));
            var records = Read(); records.RemoveAll(r => tokens.Contains(r.token)); Write(records);
            ReleaseClaim(avatar);
        }

        // A domain reload also clears these in-memory claims, without removing the durable records.
        internal static void ReleaseClaim(MyAvatar avatar) { if (avatar) Claimed.Remove(avatar.GetInstanceID()); }
    }
}
