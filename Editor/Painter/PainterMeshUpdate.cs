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
    /// from Blender: <see cref="AvatarMeshEdits"/>), a project made by Open in Substance gets the avatar built and posed again
    /// as the project was made (<see cref="PainterLink.pose"/>), and Painter's plugin puts that mesh in the project, the
    /// strokes kept where they are on the surface. A project that isn't open in Painter gets it once it links again.
    /// </summary>
    internal static class PainterMeshUpdate
    {
        // The plugin version that loads new meshes.
        private static readonly Version Reloads = new Version(1, 3, 0);
        // Painter loads the mesh, then computes the project again: a big one takes a while.
        private const double AnswerSeconds = 600;

        private sealed class Update
        {
            public HiddenAvatarBuild Build;
            public PainterLinkService.Session Session;
            public double SentAt = -1;
            // The meshes changed again meanwhile: once this one is in Painter, the next one is made.
            public bool Again;
        }

        private static readonly Dictionary<MyAvatar, Update> Running = new Dictionary<MyAvatar, Update>();

        [InitializeOnLoadMethod]
        private static void Register() => AvatarMeshEdits.Applied += Applied;

        /// <summary>Whether the avatar's new mesh is being made or loaded in Painter now.</summary>
        internal static bool Busy(MyAvatar avatar) => avatar && Running.ContainsKey(avatar);

        private static void Applied(Transform avatarRoot)
        {
            var root = BrokenMaterials.AvatarRoot(avatarRoot.gameObject);
            foreach (var avatar in root.GetComponentsInChildren<MyAvatar>(true))
            {
                if (!avatar.painter.posed || EditorUtility.IsPersistent(avatar)) continue;
                MarkOutdated(avatar, true);
                // After the tool that changed the meshes is done with this frame.
                var session = PainterLinkService.For(avatar);
                if (session != null) Later(session);
            }
            PainterLinkService.NotifyChanged();
        }

        /// <summary>A project linked again: it gets the avatar's new mesh if the meshes changed since it was made.</summary>
        internal static void Linked(PainterLinkService.Session session)
        {
            var avatar = session.Avatar;
            if (!avatar || !avatar.painter.posed || !avatar.painter.meshOutdated) return;
            Later(session);
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

        /// <summary>Builds and poses the avatar as its project was made, then sends the mesh to Painter.</summary>
        internal static void Start(PainterLinkService.Session session)
        {
            var avatar = session.Avatar;
            if (!avatar || !avatar.painter.posed) return;
            if (Running.TryGetValue(avatar, out var running))
            {
                running.Again = true;
                return;
            }
            // Painter runs a plugin that doesn't load meshes: the new one reloads itself once installed and links again,
            // which comes back here.
            if (!Version.TryParse(session.Hello.plugin, out var plugin) || plugin < Reloads)
            {
                bool installed = false;
                try
                {
                    if (!PainterApp.PluginCurrent) { PainterApp.InstallPlugin(); installed = true; }
                }
                catch (Exception ex) { Debug.LogException(ex); }
                session.Error = !installed;
                session.State = installed ? "Updating Painter's My Avatar plugin to send it the avatar's new mesh…"
                    : "The avatar's meshes changed: restart Painter (it loads My Avatar's new plugin) to get the new mesh in the project.";
                PainterLinkService.NotifyChanged();
                return;
            }
            var update = new Update { Session = session };
            Running[avatar] = update;
            if (Running.Count == 1) EditorApplication.update += Tick;
            session.Error = false;
            session.State = "The avatar's meshes changed: building it to update Painter's mesh…";
            PainterLinkService.NotifyChanged();
            update.Build = PainterPose.Build(avatar);
            update.Build.Completed += () => Built(avatar, update);
            if (update.Build.Finished) Built(avatar, update);
        }

        private static void Built(MyAvatar avatar, Update update)
        {
            var build = update.Build;
            if (build == null) return;
            update.Build = null;
            List<PainterPose.Part> parts = null;
            try
            {
                if (!avatar) { Forget(avatar); return; }
                if (build.Error != null || !build.Copy)
                {
                    Fail(avatar, build.Cancelled ? "the build was cancelled" : "the avatar could not be built: " + build.Error);
                    return;
                }
                // Unlinked meanwhile: the project gets the mesh when it links again.
                var session = PainterLinkService.For(avatar);
                if (session == null) { Forget(avatar); return; }
                (parts, _) = PainterPose.Posed(build.Copy, new PainterPose.Rest(build.Copy), Array.Empty<FollowLink>(), avatar.painter.pose);
                string path = PainterProjectExport.WriteMeshUpdate(avatar, parts);
                update.Session = session;
                update.SentAt = EditorApplication.timeSinceStartup;
                session.RequestMeshReload(path);
                session.State = "Painter is loading the avatar's new mesh (the strokes stay where they are on the surface)…";
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                Fail(avatar, ex.Message);
            }
            finally
            {
                if (parts != null) PainterPose.Release(parts);
                build.Release();
                PainterLinkService.NotifyChanged();
            }
        }

        /// <summary>Painter's answer: the project has the new mesh, or why not.</summary>
        internal static void Reloaded(PainterLinkService.Session session, PainterLinkProtocol.MeshReloaded answer)
        {
            var avatar = session.Avatar;
            if (!avatar || !Running.TryGetValue(avatar, out var update)) return;
            Forget(avatar);
            if (!string.IsNullOrEmpty(answer?.error))
            {
                session.Error = true;
                session.State = "Painter could not load the avatar's new mesh: " + answer.error;
            }
            else if (update.Again) Start(session);
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
