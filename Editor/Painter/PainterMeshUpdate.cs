using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.Meshes;
using Orbiters.Toolkit.Editor.VRChat;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor.Painter
{
    /// <summary>
    /// Painter's mesh follows the avatar's. When another tool changes the avatar's meshes for good (MCB applying an export
    /// from Blender: <see cref="AvatarMeshEdits"/>), the project Open in Substance made gets the avatar posed again as the
    /// project was made (<see cref="PainterLink.pose"/>), and Painter's plugin puts that mesh in the project, the strokes
    /// kept where they are on the surface. Only that project: one linked from Painter keeps its own mesh
    /// (<see cref="PainterLinkService.Session.MadeProject"/>). A project that isn't open in Painter gets it once it links
    /// again. With <see cref="PainterLink.followLive"/>, Painter also follows the edits MCB shows live from Blender
    /// (temporary meshes), a moment after they pause, without waiting for Blender's export. A mesh Painter was loading
    /// across a script reload is still settled by its answer.
    /// </summary>
    internal static class PainterMeshUpdate
    {
        // The plugin version that loads new meshes.
        private static readonly Version Reloads = new Version(1, 3, 0);
        // Painter loads the mesh, then computes the project again: a big one takes a while.
        private const double AnswerSeconds = 600;
        // How long Blender's live edits stay still before Painter gets them (Blender sends one about 200 ms after an edit).
        private const double QuietSeconds = 1.0;
        // The mesh updates Painter hadn't answered before a script reload.
        private const string PendingKey = "Orbiters.MyAvatar.PainterMeshUpdate.Pending";

        private sealed class Update
        {
            public PainterLinkService.Session Session;
            public double SentAt = -1;
            // The OBJ sent (Painter's answer names it).
            public string Path;
            // The meshes changed again meanwhile: once this one is in Painter, the next one is made.
            public bool Again;
            // A live edit (temporary meshes): it doesn't settle the avatar's change, the export does.
            public bool Live, AgainLive = true;
        }

        [Serializable]
        private sealed class Pending
        {
            public string avatar, path;
            public double sentAt;
            public bool again, live, againLive;
        }

        [Serializable]
        private sealed class PendingList
        {
            public List<Pending> items = new List<Pending>();
        }

        private static readonly Dictionary<MyAvatar, Update> Running = new Dictionary<MyAvatar, Update>();

        [InitializeOnLoadMethod]
        private static void Register()
        {
            AvatarMeshEdits.Applied += Applied;
            AvatarMeshEdits.Previewed += Previewed;
            AssemblyReloadEvents.beforeAssemblyReload += Keep;
        }

        // Before a script reload: what Painter is loading, for its answer once it links again.
        private static void Keep()
        {
            var list = new PendingList();
            foreach (var pair in Running)
                if (pair.Key && pair.Value.SentAt >= 0)
                    list.items.Add(new Pending
                    {
                        avatar = PainterLinkService.Id(pair.Key), path = pair.Value.Path, sentAt = pair.Value.SentAt,
                        again = pair.Value.Again, live = pair.Value.Live, againLive = pair.Value.AgainLive,
                    });
            SessionState.SetString(PendingKey, list.items.Count == 0 ? "" : JsonUtility.ToJson(list));
        }

        private static Pending TakePending(MyAvatar avatar)
        {
            string json = SessionState.GetString(PendingKey, "");
            if (string.IsNullOrEmpty(json)) return null;
            PendingList list;
            try { list = JsonUtility.FromJson<PendingList>(json); }
            catch (Exception) { list = null; }
            string id = PainterLinkService.Id(avatar);
            var found = list?.items.FirstOrDefault(p => p.avatar == id);
            if (found == null) return null;
            list.items.Remove(found);
            SessionState.SetString(PendingKey, list.items.Count == 0 ? "" : JsonUtility.ToJson(list));
            return found;
        }

        // Live edits waiting to be still for QuietSeconds, by session.
        private static readonly Dictionary<PainterLinkService.Session, double> Quiet = new Dictionary<PainterLinkService.Session, double>();

        private static void Previewed(Transform avatarRoot)
        {
            var root = BrokenMaterials.AvatarRoot(avatarRoot.gameObject);
            foreach (var avatar in root.GetComponentsInChildren<MyAvatar>(true))
            {
                if (EditorUtility.IsPersistent(avatar) || !avatar.painter.posed || !avatar.painter.followLive) continue;
                var session = PainterLinkService.For(avatar);
                if (session == null || !session.MadeProject) continue;
                Quiet[session] = EditorApplication.timeSinceStartup + QuietSeconds;
                EditorApplication.update -= SendQuiet;
                EditorApplication.update += SendQuiet;
            }
        }

        private static void SendQuiet()
        {
            double now = EditorApplication.timeSinceStartup;
            foreach (var pair in Quiet.ToList())
            {
                if (pair.Value > now) continue;
                Quiet.Remove(pair.Key);
                var session = pair.Key;
                if (session.Avatar && session.Avatar.painter.followLive && PainterLinkService.For(session.Avatar) == session) Start(session, live: true);
            }
            if (Quiet.Count == 0) EditorApplication.update -= SendQuiet;
        }

        /// <summary>Whether the avatar's new mesh is being made or loaded in Painter now.</summary>
        internal static bool Busy(MyAvatar avatar) => avatar && Running.ContainsKey(avatar);

        /// <summary>The avatar's meshes changed for good (see <see cref="AvatarMeshEdits.Applied"/>).</summary>
        internal static void Applied(Transform avatarRoot)
        {
            var root = BrokenMaterials.AvatarRoot(avatarRoot.gameObject);
            foreach (var avatar in root.GetComponentsInChildren<MyAvatar>(true))
            {
                if (EditorUtility.IsPersistent(avatar)) continue;
                var session = PainterLinkService.For(avatar);
                // A project not made by Open in Substance (linked from Painter, or made before mesh updates) has no pose to
                // make its mesh again: the card says so.
                bool linked = session != null || !string.IsNullOrEmpty(avatar.painter.projectPath);
                if (!avatar.painter.posed && !linked) continue;
                MarkOutdated(avatar, true);
                // After the tool that changed the meshes is done with this frame; the made project only.
                if (session != null && session.MadeProject) Later(session);
            }
            PainterLinkService.NotifyChanged();
        }

        /// <summary>A project linked again: it gets the avatar's new mesh if the meshes changed since it was made.</summary>
        internal static void Linked(PainterLinkService.Session session)
        {
            var avatar = session.Avatar;
            if (!avatar || !session.MadeProject) return;
            // Sent before a script reload and not answered yet: the answer comes on this connection.
            var pending = TakePending(avatar);
            if (pending != null && !Running.ContainsKey(avatar) && EditorApplication.timeSinceStartup - pending.sentAt < AnswerSeconds)
            {
                Running[avatar] = new Update
                {
                    Session = session, SentAt = pending.sentAt, Path = pending.path, Again = pending.again, Live = pending.live, AgainLive = pending.againLive,
                };
                EditorApplication.update -= Tick;
                EditorApplication.update += Tick;
                return;
            }
            if (avatar.painter.meshOutdated) Later(session);
        }

        // Started on the next editor update. Not delayCall: it waits for a repaint, which an editor in the background (the
        // user in Blender or Painter) doesn't do, so the update would wait for them to come back to Unity.
        private static readonly List<PainterLinkService.Session> Due = new List<PainterLinkService.Session>();

        private static void Later(PainterLinkService.Session session)
        {
            if (!Due.Contains(session)) Due.Add(session);
            EditorApplication.update -= StartDue;
            EditorApplication.update += StartDue;
        }

        private static void StartDue()
        {
            EditorApplication.update -= StartDue;
            var due = Due.ToList();
            Due.Clear();
            // Still linked: a project gone meanwhile gets the mesh when it links again.
            foreach (var session in due)
                if (session.Avatar && PainterLinkService.For(session.Avatar) == session) Start(session);
        }

        /// <summary>
        /// Poses the avatar as its project was made (a hidden copy, as the scene shows it: Blender's live edit included),
        /// then sends the mesh to Painter. One at a time: what changes meanwhile is sent once Painter has this one. Only for
        /// the project Open in Substance made: any other keeps its own mesh.
        /// </summary>
        internal static void Start(PainterLinkService.Session session, bool live = false)
        {
            var avatar = session.Avatar;
            if (!avatar || !avatar.painter.posed || !session.MadeProject) return;
            if (Running.TryGetValue(avatar, out var running))
            {
                running.Again = true;
                running.AgainLive &= live;
                return;
            }
            // Painter runs a plugin that doesn't load meshes: the new one reloads itself once installed and links again,
            // which comes back here.
            if (!Version.TryParse(session.Hello.plugin, out var plugin) || plugin < Reloads)
            {
                bool installed = false;
                try { installed = PainterApp.InstallPlugin(); }
                catch (Exception ex) { Debug.LogException(ex); }
                session.Error = !installed;
                session.State = installed ? "Updating Painter's My Avatar plugin to send it the avatar's new mesh (restart Painter if this stays)…"
                    : "The avatar's meshes changed: restart Painter (it loads My Avatar's new plugin) to get the new mesh in the project.";
                PainterLinkService.NotifyChanged();
                return;
            }
            var update = new Update { Session = session, Live = live };
            Running[avatar] = update;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            session.Error = false;
            try
            {
                // The avatar as the scene shows it, posed as the project was made: a hidden copy, no build. The renderers the
                // project's texture sets were made from are in it even when hidden now (Painter would drop their sets).
                var include = avatar.painter.sets.Where(s => !string.IsNullOrEmpty(s.renderer)).Select(s => s.renderer).Distinct().ToList();
                string path = PainterPose.WithPosed(avatar, avatar.painter.pose, parts => PainterProjectExport.WriteMeshUpdate(avatar, parts), include);
                update.Path = path.Replace('\\', '/');
                update.SentAt = EditorApplication.timeSinceStartup;
                session.RequestMeshReload(path);
                session.State = live ? "Painter is loading Blender's edit…"
                    : "Painter is loading the avatar's new mesh (the strokes stay where they are on the surface)…";
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                Fail(avatar, ex.Message);
            }
            PainterLinkService.NotifyChanged();
        }

        /// <summary>Painter's answer: the project has the new mesh, or why not.</summary>
        internal static void Reloaded(PainterLinkService.Session session, PainterLinkProtocol.MeshReloaded answer)
        {
            var avatar = session.Avatar;
            if (!avatar || !Running.TryGetValue(avatar, out var update)) return;
            // About another mesh than the one waiting (an older answer): the one waited for answers later.
            if (!string.IsNullOrEmpty(answer?.path) && !string.IsNullOrEmpty(update.Path) && !PainterLinkService.SamePath(answer.path, update.Path)) return;
            Forget(avatar);
            if (!string.IsNullOrEmpty(answer?.error))
            {
                session.Error = true;
                session.State = "Painter could not load the avatar's new mesh: " + answer.error;
            }
            else if (update.Again) Start(session, update.AgainLive);
            else if (update.Live)
            {
                session.Error = false;
                session.State = "Painter shows Blender's edit. Saving in Blender applies it to the avatar.";
            }
            else
            {
                MarkOutdated(avatar, false);
                session.Error = false;
                session.State = "Painter has the avatar's new mesh. Bake the mesh maps again in Painter where they look off.";
            }
            PainterLinkService.NotifyChanged();
        }

        // Painter never answered, or the project went away after the mesh was sent.
        private static void Tick()
        {
            double now = EditorApplication.timeSinceStartup;
            foreach (var pair in Running.ToList())
            {
                var update = pair.Value;
                if (update.SentAt < 0) continue;
                var session = pair.Key ? PainterLinkService.For(pair.Key) : null;
                if (session != update.Session) Forget(pair.Key);
                else if (now - update.SentAt > AnswerSeconds) Fail(pair.Key, "Painter didn't answer");
                else continue;
                PainterLinkService.NotifyChanged();
            }
        }

        private static void Fail(MyAvatar avatar, string reason)
        {
            Forget(avatar);
            var session = avatar ? PainterLinkService.For(avatar) : null;
            if (session == null) return;
            session.Error = true;
            session.State = "Painter's mesh could not be updated: " + reason + ".";
        }

        private static void Forget(MyAvatar avatar)
        {
            foreach (var key in Running.Keys.Where(k => k == avatar || !k).ToList()) Running.Remove(key);
            if (Running.Count == 0) EditorApplication.update -= Tick;
        }

        // Bookkeeping of the link, outside Undo: undoing the avatar's change doesn't undo what Painter loaded.
        private static void MarkOutdated(MyAvatar avatar, bool outdated)
        {
            if (avatar.painter.meshOutdated == outdated) return;
            avatar.painter.meshOutdated = outdated;
            EditorUtility.SetDirty(avatar);
            PrefabUtility.RecordPrefabInstancePropertyModifications(avatar);
        }
    }
}
