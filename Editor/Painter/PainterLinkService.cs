using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor.LiveLink;
using Orbiters.Toolkit.Editor.Textures;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Orbiters.MyAvatar.Editor.Painter
{
    /// <summary>How the Painter link reaches Painter: a frame to one connection, or the connection closed.</summary>
    internal interface IPainterLinkTransport
    {
        bool Send(int connection, byte type, byte[] payload);
        void Disconnect(int connection);
    }

    /// <summary>
    /// Unity's end of the Substance 3D Painter link (see <see cref="PainterLinkProtocol"/>): one listener per editor, started
    /// when an avatar's My Avatar Inspector shows with Painter and its My Avatar plugin installed (and again after script
    /// reloads), the endpoint file Painter finds it by, and a <see cref="Session"/> for each Painter project linked to an
    /// avatar (<see cref="Links"/>).
    /// </summary>
    [InitializeOnLoad]
    internal static class PainterLinkService
    {
        private const string TokenKey = "Orbiters.MyAvatar.PainterLink.Token", PortKey = "Orbiters.MyAvatar.PainterLink.Port", ActiveKey = "Orbiters.MyAvatar.PainterLink.Active";
        private const string CleanedKey = "Orbiters.MyAvatar.PainterLink.Cleaned";
        private const double EndpointSettleSeconds = 1.5, EndpointRetrySeconds = 5;
        private static LiveLinkServer server;
        private static string token;
        private static double endpointDirtyAt = -1;
        private static string writtenEndpoint;
        private static bool endpointFailing;

        /// <summary>The projects linked through this editor's listener.</summary>
        internal static readonly Links Current = new Links(new ServerTransport(), Find);

        private sealed class ServerTransport : IPainterLinkTransport
        {
            public bool Send(int connection, byte type, byte[] payload) => server != null && server.Send(connection, type, payload);
            public void Disconnect(int connection) => server?.Disconnect(connection);
        }

        /// <summary>Something the Painter card shows changed (a project linked or gone, a state, a commit).</summary>
        internal static event Action Changed;

        /// <summary>Tells the Painter cards something they show changed.</summary>
        internal static void NotifyChanged() => Changed?.Invoke();

        static PainterLinkService()
        {
            // Listening again after a script reload: Painter reconnects on its own.
            if (SessionState.GetBool(ActiveKey, false)) EditorApplication.update += StartAfterReload;
        }

        private static void StartAfterReload()
        {
            EditorApplication.update -= StartAfterReload;
            EnsureRunning();
        }

        // ---- Folders and paths ----

        /// <summary>The link's folder while a test works in its own (null: the user's).</summary>
        internal static string LinkFolderOverride;

        /// <summary>
        /// The folder Painter and Unity share: endpoints, project jobs, live images and exports. The plugin's
        /// <c>link_folder</c> is the same: %LOCALAPPDATA% on Windows, Application Support on macOS.
        /// </summary>
        internal static string LinkFolder => LinkFolderOverride ?? Path.Combine(AppData, "Orbiters", "PainterLink");

        private static string AppData => Application.platform == RuntimePlatform.OSXEditor
            ? Path.Combine(Home, "Library", "Application Support")
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        /// <summary>The user's Documents folder: Painter's user folder and the projects Open in Substance makes are in it.</summary>
        internal static string Documents => Application.platform == RuntimePlatform.WindowsEditor
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            // Mono answers the home folder itself for MyDocuments elsewhere.
            : Path.Combine(Home, "Documents");

        private static StringComparison PathComparison => Application.platform == RuntimePlatform.LinuxEditor ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        private static string Normalized(string path) => Path.GetFullPath(path).Replace('\\', '/');

        /// <summary>
        /// A path as the avatar keeps it: under the user's home folder as "~/…", so scenes and prefabs never hold the user's
        /// name (see <see cref="Local"/>).
        /// </summary>
        internal static string Portable(string path)
        {
            if (string.IsNullOrEmpty(path) || path.StartsWith("~/", StringComparison.Ordinal)) return path;
            try
            {
                string full = Normalized(path);
                string home = string.IsNullOrEmpty(Home) ? null : Normalized(Home).TrimEnd('/') + "/";
                return home != null && full.StartsWith(home, PathComparison) ? "~/" + full.Substring(home.Length) : full;
            }
            catch (Exception) { return path; }
        }

        /// <summary>A path kept by <see cref="Portable"/>, on this machine.</summary>
        internal static string Local(string path) =>
            string.IsNullOrEmpty(path) || !path.StartsWith("~/", StringComparison.Ordinal) ? path : Path.Combine(Home, path.Substring(2)).Replace('\\', '/');

        /// <summary>Whether two paths (kept or not) name the same file.</summary>
        internal static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { return string.Equals(Normalized(Local(a)), Normalized(Local(b)), PathComparison); }
            catch (Exception) { return false; }
        }

        /// <summary>Whether a path lies inside a folder: what Painter names is only read from the link's folders.</summary>
        internal static bool Inside(string path, string folder)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(folder)) return false;
            try { return Normalized(path).StartsWith(Normalized(folder).TrimEnd('/') + "/", PathComparison); }
            catch (Exception) { return false; }
        }

        /// <summary>The avatar's Painter project (the one Open in Substance made, else the one linked last), on this machine.</summary>
        internal static string ProjectPath(MyAvatar avatar) => Local(avatar.painter.projectPath);

        /// <summary>Whether the avatar's Painter project is on this machine.</summary>
        internal static bool HasProject(MyAvatar avatar)
        {
            string path = ProjectPath(avatar);
            return !string.IsNullOrEmpty(path) && File.Exists(path);
        }

        // ---- The listener ----

        internal static bool Running => server != null;

        /// <summary>
        /// Starts listening (once) when Painter is installed and runs My Avatar's plugin (installing it, or Open in
        /// Substance, starts it too): an editor that never links to Painter opens no port.
        /// </summary>
        internal static void EnsureRunning()
        {
            if (server != null || !PainterApp.Installed || !PainterApp.PluginInstalled || EditorApplication.isPlayingOrWillChangePlaymode) return;
            token = SessionState.GetString(TokenKey, "");
            if (string.IsNullOrEmpty(token))
            {
                var bytes = new byte[24];
                using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
                token = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
                SessionState.SetString(TokenKey, token);
            }
            try
            {
                server = new LiveLinkServer(PainterLinkProtocol.Format, token, SessionState.GetInt(PortKey, 0), accept: Decode,
                    collapseKey: frame => frame.Type == (byte)PainterLinkProtocol.FrameType.Texture ? Collapse(frame) : null,
                    closingType: (byte)PainterLinkProtocol.FrameType.Bye, threadName: "My Avatar Painter link");
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException || ex is IOException)
            {
                Debug.LogWarning("[My Avatar] Substance Painter link: could not listen (" + ex.Message + ").");
                return;
            }
            SessionState.SetInt(PortKey, server.Port);
            SessionState.SetBool(ActiveKey, true);
            server.Received += Current.OnFrame;
            server.ConnectionChanged += Current.OnConnection;
            EditorApplication.hierarchyChanged += MarkEndpoint;
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorSceneManager.sceneClosed += OnSceneClosed;
            EditorApplication.update += Update;
            EditorApplication.quitting += Quit;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            WriteEndpoint();
            if (!SessionState.GetBool(CleanedKey, false))
            {
                SessionState.SetBool(CleanedKey, true);
                string folder = LinkFolder;
                int own = Process.GetCurrentProcess().Id;
                Task.Run(() => PainterLinkCleanup.Run(folder, own, DateTime.UtcNow));
            }
        }

        private static void OnSceneOpened(UnityEngine.SceneManagement.Scene scene, OpenSceneMode mode) => MarkEndpoint();
        private static void OnSceneClosed(UnityEngine.SceneManagement.Scene scene) => MarkEndpoint();

        // Payloads are JSON: read on the connection's thread, so one that doesn't hold together (or is null) closes it there.
        internal static bool Decode(LiveLinkFrame frame)
        {
            try
            {
                switch ((PainterLinkProtocol.FrameType)frame.Type)
                {
                    case PainterLinkProtocol.FrameType.Hello: frame.Decoded = PainterLinkProtocol.Read<PainterLinkProtocol.Hello>(frame.Payload); break;
                    case PainterLinkProtocol.FrameType.Structure: frame.Decoded = PainterLinkProtocol.Read<PainterLinkProtocol.Structure>(frame.Payload); break;
                    case PainterLinkProtocol.FrameType.Texture: frame.Decoded = PainterLinkProtocol.Read<PainterLinkProtocol.Texture>(frame.Payload); break;
                    case PainterLinkProtocol.FrameType.Commit: frame.Decoded = PainterLinkProtocol.Read<PainterLinkProtocol.Commit>(frame.Payload); break;
                    case PainterLinkProtocol.FrameType.MeshReloaded: frame.Decoded = PainterLinkProtocol.Read<PainterLinkProtocol.MeshReloaded>(frame.Payload); break;
                    default: return true;
                }
                frame.Decoded = PainterLinkProtocol.Checked(frame.Decoded);
                return frame.Decoded != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string Collapse(LiveLinkFrame frame) =>
            frame.Decoded is PainterLinkProtocol.Texture texture ? texture.textureSet + "\n" + texture.channel : null;

        // Unity quitting: Painter no longer lists it. A script reload keeps the file: the link listens again at once.
        private static void Quit()
        {
            Stop();
            SessionState.SetBool(ActiveKey, false);
            try { File.Delete(EndpointPath); } catch (Exception) { }
        }

        private static void Stop()
        {
            Current.Clear();
            if (server != null)
            {
                server.Dispose();
                server = null;
            }
            EditorApplication.hierarchyChanged -= MarkEndpoint;
            EditorSceneManager.sceneOpened -= OnSceneOpened;
            EditorSceneManager.sceneClosed -= OnSceneClosed;
            EditorApplication.update -= Update;
            EditorApplication.quitting -= Quit;
            AssemblyReloadEvents.beforeAssemblyReload -= Stop;
            writtenEndpoint = null;
        }

        private static string EndpointPath => Path.Combine(LinkFolder, "unity-" + Process.GetCurrentProcess().Id + ".json");

        private static void MarkEndpoint() => endpointDirtyAt = EditorApplication.timeSinceStartup;

        private static void Update()
        {
            if (endpointDirtyAt >= 0 && EditorApplication.timeSinceStartup - endpointDirtyAt > EndpointSettleSeconds)
            {
                endpointDirtyAt = -1;
                WriteEndpoint();
            }
            Current.Update(EditorApplication.timeSinceStartup);
        }

        /// <summary>The avatars of the loaded scenes, by their My Avatar component.</summary>
        internal static IEnumerable<MyAvatar> SceneAvatars() =>
            UnityEngine.Object.FindObjectsOfType<MyAvatar>(true).Where(a => a && !EditorUtility.IsPersistent(a) && a.gameObject.scene.IsValid());

        internal static string Id(MyAvatar avatar) => GlobalObjectId.GetGlobalObjectIdSlow(avatar).ToString();

        private static MyAvatar Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (GlobalObjectId.TryParse(id, out var global) && GlobalObjectId.GlobalObjectIdentifierToObjectSlow(global) is MyAvatar found && found) return found;
            // A scene never saved has no identity Unity looks objects up by: its avatar by the id it is listed with, when that
            // names one avatar only.
            var listed = SceneAvatars().Where(a => Id(a) == id).Take(2).ToList();
            return listed.Count == 1 ? listed[0] : null;
        }

        private static void WriteEndpoint()
        {
            if (server == null) return;
            var endpoint = new PainterLinkProtocol.Endpoint
            {
                pid = Process.GetCurrentProcess().Id, port = server.Port, token = token,
                projectName = PlayerSettings.productName, projectPath = Path.GetDirectoryName(Application.dataPath),
                avatars = SceneAvatars().Select(a => new PainterLinkProtocol.EndpointAvatar { id = Id(a), name = a.name, scene = a.gameObject.scene.name }).ToList(),
            };
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(endpoint);
            if (json == writtenEndpoint) return;
            try
            {
                LiveLinkServer.WriteAtomically(EndpointPath, json);
                writtenEndpoint = json;
                endpointFailing = false;
            }
            catch (Exception ex)
            {
                // Painter finds Unity by this file: written again shortly (said once while it keeps failing).
                endpointDirtyAt = EditorApplication.timeSinceStartup + EndpointRetrySeconds;
                if (!endpointFailing) Debug.LogWarning("[My Avatar] Substance Painter link: could not write " + EndpointPath + " (" + ex.Message + "). Trying again.");
                endpointFailing = true;
            }
        }

        // ---- Sessions ----

        /// <summary>The Painter project linked to this avatar now, or null.</summary>
        internal static Session For(MyAvatar avatar) => Current.For(avatar);

        /// <summary>The Painter projects linked through one listener (the editor's, <see cref="Current"/>; tests make their own).</summary>
        internal sealed class Links
        {
            // How long Painter gets to close a link Unity ends before Unity closes it (the message it was sent goes first).
            internal const double ClosingSeconds = 2;
            private readonly IPainterLinkTransport transport;
            private readonly Func<string, MyAvatar> find;
            private readonly Dictionary<int, Session> sessions = new Dictionary<int, Session>();
            private readonly Dictionary<int, double> closing = new Dictionary<int, double>();
            private double now;

            internal Links(IPainterLinkTransport transport, Func<string, MyAvatar> find)
            {
                this.transport = transport;
                this.find = find;
            }

            internal Session For(MyAvatar avatar) => avatar ? sessions.Values.FirstOrDefault(s => s.Avatar == avatar) : null;

            internal IEnumerable<Session> All => sessions.Values;

            internal bool Holds(Session session) => session != null && sessions.TryGetValue(session.Connection, out var held) && held == session;

            internal bool Send(int connection, PainterLinkProtocol.FrameType type, object payload) =>
                transport.Send(connection, (byte)type, PainterLinkProtocol.Json(payload));

            // Unity ends a link: Painter shows why and closes it (it links again on its own unless another project took over);
            // closed from here a moment later if it doesn't.
            private void Close(int connection, string message, bool error = false, bool takenOver = false)
            {
                Send(connection, PainterLinkProtocol.FrameType.Status, new PainterLinkProtocol.Status { message = message, error = error, closing = true, takenOver = takenOver });
                closing[connection] = now;
            }

            internal void OnConnection(int connection, bool up)
            {
                if (up) return;
                closing.Remove(connection);
                if (!sessions.TryGetValue(connection, out var session)) return;
                session.Dispose();
                sessions.Remove(connection);
                Changed?.Invoke();
            }

            internal void OnFrame(LiveLinkFrame frame)
            {
                // A link being ended says nothing more.
                if (closing.ContainsKey(frame.Connection)) return;
                sessions.TryGetValue(frame.Connection, out var session);
                switch ((PainterLinkProtocol.FrameType)frame.Type)
                {
                    case PainterLinkProtocol.FrameType.Hello:
                    {
                        var hello = (PainterLinkProtocol.Hello)frame.Decoded;
                        var avatar = find(hello.avatarId);
                        if (session != null)
                        {
                            session.Dispose();
                            sessions.Remove(frame.Connection);
                        }
                        if (!avatar)
                        {
                            Close(frame.Connection, "This project's avatar isn't in the scenes open in Unity.", error: true);
                            if (session != null) Changed?.Invoke();
                            return;
                        }
                        // One Painter project per avatar: a newer one takes over, and the older one stops linking on its own
                        // (it would take the avatar back a moment later otherwise, and so on).
                        string newer = string.IsNullOrEmpty(hello.projectName) ? "Another project" : Path.GetFileNameWithoutExtension(hello.projectName);
                        foreach (var other in sessions.Where(p => p.Value.Avatar == avatar).ToList())
                        {
                            other.Value.Dispose();
                            sessions.Remove(other.Key);
                            Close(other.Key, newer + " linked to " + avatar.name + " in Unity: this project stopped linking. Link it again to take the avatar back.", takenOver: true);
                        }
                        var linked = sessions[frame.Connection] = new Session(this, frame.Connection, avatar, hello);
                        linked.Welcome();
                        PainterMeshUpdate.Linked(linked);
                        Changed?.Invoke();
                        break;
                    }
                    case PainterLinkProtocol.FrameType.Structure:
                        session?.OnStructure((PainterLinkProtocol.Structure)frame.Decoded);
                        break;
                    case PainterLinkProtocol.FrameType.Texture:
                        session?.OnTexture((PainterLinkProtocol.Texture)frame.Decoded);
                        break;
                    case PainterLinkProtocol.FrameType.Committing:
                        session?.OnCommitting();
                        break;
                    case PainterLinkProtocol.FrameType.Commit:
                        session?.OnCommit((PainterLinkProtocol.Commit)frame.Decoded);
                        break;
                    case PainterLinkProtocol.FrameType.MeshReloaded:
                        if (session != null) PainterMeshUpdate.Reloaded(session, (PainterLinkProtocol.MeshReloaded)frame.Decoded);
                        break;
                }
            }

            /// <summary>
            /// Each editor frame: a project whose avatar is gone (its scene closed, or opened again) is unlinked, so Painter
            /// links again to the avatar once it is back instead of sending saves nobody applies; links ended a while ago close.
            /// </summary>
            internal void Update(double time)
            {
                now = time;
                foreach (var pair in sessions.ToList())
                {
                    if (pair.Value.Avatar)
                    {
                        pair.Value.Update();
                        continue;
                    }
                    pair.Value.Dispose();
                    sessions.Remove(pair.Key);
                    Close(pair.Key, "The avatar's scene was closed in Unity: this project links again once it is open.");
                    Changed?.Invoke();
                }
                foreach (var pair in closing.Where(p => time - p.Value > ClosingSeconds).ToList())
                {
                    closing.Remove(pair.Key);
                    transport.Disconnect(pair.Key);
                }
            }

            internal void Clear()
            {
                foreach (var session in sessions.Values) session.Dispose();
                sessions.Clear();
                closing.Clear();
            }
        }

        /// <summary>A Painter project linked to an avatar: its texture sets, which material each paints, what shows live.</summary>
        internal sealed class Session : IDisposable
        {
            private readonly Links links;
            public readonly int Connection;
            public readonly MyAvatar Avatar;
            public readonly PainterLinkProtocol.Hello Hello;
            public PainterLinkProtocol.Structure Structure = new PainterLinkProtocol.Structure();
            public string State = "Connected to Substance Painter.";
            public bool Error, Committing;
            private double committingSince;
            public int LiveImages;
            private readonly PainterLivePreview preview = new PainterLivePreview();
            private readonly Dictionary<string, long> decoding = new Dictionary<string, long>(StringComparer.Ordinal);
            private string structureSent;

            public Session(Links links, int connection, MyAvatar avatar, PainterLinkProtocol.Hello hello)
            {
                this.links = links;
                Connection = connection;
                Avatar = avatar;
                Hello = hello;
            }

            public string ProjectName => string.IsNullOrEmpty(Hello.projectName) ? "Untitled project" : Path.GetFileNameWithoutExtension(Hello.projectName);

            /// <summary>
            /// The linked project is the one Open in Substance made for the avatar: its mesh is the avatar posed, made again
            /// when the avatar's meshes change. Any other project keeps its own mesh.
            /// </summary>
            public bool MadeProject { get; private set; }

            public void Welcome()
            {
                Send(PainterLinkProtocol.FrameType.Welcome, new PainterLinkProtocol.Welcome { avatarId = Id(Avatar), avatarName = Avatar.name });
                var painter = Avatar.painter;
                MadeProject = painter.posed && SamePath(Hello.projectPath, painter.projectPath);
                // The avatar keeps the project Open in Substance made while it exists (mesh updates go to it alone); without
                // one, the project linked last, to open it again from the card. Bookkeeping, outside Undo.
                if (MadeProject || string.IsNullOrEmpty(Hello.projectPath) || SamePath(Hello.projectPath, painter.projectPath)) return;
                if (painter.posed && HasProject(Avatar)) return;
                painter.projectPath = Portable(Hello.projectPath);
                painter.posed = false;
                painter.meshOutdated = false;
                EditorUtility.SetDirty(Avatar);
                PrefabUtility.RecordPrefabInstancePropertyModifications(Avatar);
            }

            private void Send(PainterLinkProtocol.FrameType type, object payload) => links.Send(Connection, type, payload);

            public void Update()
            {
                // An export that never answered (Painter closed or stuck meanwhile): stop waiting for it.
                if (Committing && EditorApplication.timeSinceStartup - committingSince > 300)
                {
                    Committing = false;
                    Error = true;
                    State = "Painter didn't send the saved textures. Save again in Painter, or click Apply all now.";
                    Changed?.Invoke();
                }
            }

            public void OnStructure(PainterLinkProtocol.Structure structure)
            {
                Structure = structure ?? new PainterLinkProtocol.Structure();
                // Painter sends its texture sets again after layer changes: the same sets change nothing here.
                string sent = Newtonsoft.Json.JsonConvert.SerializeObject(Structure);
                if (sent == structureSent) return;
                structureSent = sent;
                if (!Avatar) return;
                PainterMapping.AutoMap(Avatar, Structure.textureSets.Select(s => s.name));
                SendMapping();
                Changed?.Invoke();
            }

            /// <summary>Tells Painter which texture set paints which material and what to show live (after any mapping change).</summary>
            public void SendMapping()
            {
                if (!Avatar) return;
                var slots = TextureMatching.Slots(Avatar);
                var mapping = new PainterLinkProtocol.Mapping { previewSize = Mathf.Clamp(Avatar.painter.previewSize, 256, 4096) };
                foreach (var set in Structure.textureSets)
                {
                    var material = PainterMapping.Material(Avatar, set.name);
                    var targets = material ? PainterTargets.For(material, slots) : null;
                    var entry = new PainterLinkProtocol.SetMapping { textureSet = set.name };
                    if (targets != null)
                    {
                        entry.material = PainterMapping.Key(Avatar, set.name);
                        entry.materialName = material.name;
                        entry.channels = targets.LiveChannels(material).Where(c => set.channels.Contains(c) || c == "normal").ToList();
                        entry.maps = targets.Maps;
                    }
                    mapping.sets.Add(entry);
                }
                // A texture set no longer painting a material, or painting another one now, leaves the material it showed on
                // as it is (Painter sends the images again for the new one).
                foreach (string shown in preview.Live.ToList())
                {
                    var entry = mapping.sets.FirstOrDefault(s => s.textureSet == shown);
                    if (entry?.material == null || preview.ShownOn(shown) != PainterMapping.Material(Avatar, shown)) preview.Clear(shown);
                }
                Send(PainterLinkProtocol.FrameType.Mapping, mapping);
            }

            public async void OnTexture(PainterLinkProtocol.Texture texture)
            {
                string key = texture.textureSet + "\n" + texture.channel;
                if (preview.Stale(texture.textureSet, texture.channel, texture.revision)) return;
                if (decoding.TryGetValue(key, out long newest) && newest >= texture.revision) return;
                // Live images are only read where Painter writes them.
                if (!Inside(texture.path, Path.Combine(LinkFolder, "live"))) return;
                decoding[key] = texture.revision;
                TgaImage image;
                try
                {
                    // Read and decoded off the editor's thread: only the upload to the GPU waits for a frame.
                    image = await Task.Run(() => TgaImage.Read(texture.path));
                }
                catch (Exception ex)
                {
                    // Painter keeps the newest few images: an old one may be gone before it is read.
                    if (!(ex is FileNotFoundException || ex is DirectoryNotFoundException || ex is IOException))
                        Debug.LogWarning("[My Avatar] Substance Painter link: " + Path.GetFileName(texture.path) + " could not be read (" + ex.Message + ").");
                    return;
                }
                if (!Avatar || decoding.TryGetValue(key, out newest) && newest > texture.revision || !links.Holds(this)) return;
                var material = PainterMapping.Material(Avatar, texture.textureSet);
                var targets = material ? PainterTargets.For(material, TextureMatching.Slots(Avatar)) : null;
                if (targets == null) return;
                preview.Show(Avatar.transform, texture.textureSet, texture.channel, texture.revision, image, material, targets);
                foreach (var (renderer, slot) in PainterLivePreview.Parts(Avatar.transform, material))
                    AvatarTextureEdits.Notify(new AvatarTextureEdits.Painted { Renderer = renderer, Slot = slot, Channel = texture.channel, Path = texture.path });
                LiveImages++;
                if (!Committing && !Error) State = "Live: what you paint shows on the avatar. Save in Painter to apply it.";
                Changed?.Invoke();
            }

            public void OnCommitting()
            {
                Committing = true;
                committingSince = EditorApplication.timeSinceStartup;
                State = "Painter is exporting the saved textures…";
                Changed?.Invoke();
            }

            public void OnCommit(PainterLinkProtocol.Commit commit)
            {
                Committing = false;
                if (!Avatar) return;
                if (!string.IsNullOrEmpty(commit.error))
                {
                    Error = true;
                    State = "Painter could not export the saved textures: " + commit.error;
                    Changed?.Invoke();
                    return;
                }
                try
                {
                    if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Leave Play Mode to apply textures from Painter.");
                    var slots = TextureMatching.Slots(Avatar);
                    int applied = PainterCommit.Apply(Avatar, commit.maps, set =>
                    {
                        var material = PainterMapping.Material(Avatar, set);
                        return (material, material ? PainterTargets.For(material, slots) : null);
                    });
                    // The saved textures are on the materials now: the live images of their sets go.
                    foreach (string set in commit.maps.Select(m => m.textureSet).Distinct()) preview.Clear(set);
                    Error = false;
                    State = applied == 0 ? "Saved in Painter: nothing to apply." : "Applied " + applied + " texture" + (applied == 1 ? "" : "s") + " from Painter. Undo reverts them; Save keeps them.";
                    Send(PainterLinkProtocol.FrameType.Status, new PainterLinkProtocol.Status { message = "Applied " + applied + " texture" + (applied == 1 ? "" : "s") + " in Unity.", live = preview.Live.ToList() });
                    // Slots filled for the first time now show live too.
                    SendMapping();
                }
                catch (Exception ex)
                {
                    Error = true;
                    State = "The textures from Painter could not be applied: " + ex.Message;
                    Send(PainterLinkProtocol.FrameType.Status, new PainterLinkProtocol.Status { message = State, error = true });
                    Debug.LogException(ex);
                }
                finally
                {
                    // The export is in the project now (or failed): Painter makes a new one at the next save.
                    PainterCommit.Discard(commit.maps);
                }
                Changed?.Invoke();
            }

            /// <summary>Asks Painter to export and apply every mapped texture now.</summary>
            public void RequestCommit() => Send(PainterLinkProtocol.FrameType.Request, new PainterLinkProtocol.Request { kind = "commit" });

            /// <summary>Asks Painter to put this mesh (an OBJ of the avatar posed as the project's) in the project.</summary>
            public void RequestMeshReload(string path) =>
                Send(PainterLinkProtocol.FrameType.Request, new PainterLinkProtocol.Request { kind = "reloadMesh", path = path.Replace('\\', '/') });

            /// <summary>Asks Painter to render every live channel again.</summary>
            public void RequestRefresh() => Send(PainterLinkProtocol.FrameType.Request, new PainterLinkProtocol.Request { kind = "refresh" });

            /// <summary>The avatar's own textures again (until the next stroke).</summary>
            public void ClearPreview()
            {
                preview.Clear();
                Changed?.Invoke();
            }

            public IEnumerable<string> LiveSets => preview.Live;

            public void Dispose() => preview.Dispose();
        }
    }

    /// <summary>
    /// What the link leaves in its folder and no one needs any more, cleared once per editor session (off the editor's
    /// thread): exports Unity applied, live images of Painters that closed, project sources and meshes older than a week,
    /// jobs and endpoint files left by editors and Painters that are gone.
    /// </summary>
    internal static class PainterLinkCleanup
    {
        internal static readonly TimeSpan Exports = TimeSpan.FromHours(1), Sources = TimeSpan.FromDays(7);

        internal static void Run(string folder, int ownPid, DateTime now, Func<int, bool> alive = null)
        {
            alive ??= Alive;
            try
            {
                if (!Directory.Exists(folder)) return;
                // commit/<pid>/<time>/: a save's export, applied within moments.
                foreach (string pid in Folders(Path.Combine(folder, "commit")))
                {
                    foreach (string export in Folders(pid))
                        if (now - Directory.GetLastWriteTimeUtc(export) > Exports) Delete(export);
                    if (Folders(pid).Length == 0 && Directory.GetFiles(pid).Length == 0) Delete(pid);
                }
                // live/<pid>/: the newest images of a Painter that runs.
                foreach (string pid in Folders(Path.Combine(folder, "live")))
                    if (!int.TryParse(Path.GetFileName(pid), out int id) || !alive(id)) Delete(pid);
                // projects/<avatar>-<time>/: what a project was made from (it is in the project once made).
                foreach (string project in Folders(Path.Combine(folder, "projects")))
                    if (now - Directory.GetLastWriteTimeUtc(project) > Sources) Delete(project);
                foreach (string mesh in Files(Path.Combine(folder, "meshes")))
                    if (now - File.GetLastWriteTimeUtc(mesh) > Sources) DeleteFile(mesh);
                foreach (string job in Files(Path.Combine(folder, "jobs")))
                    if (now - File.GetLastWriteTimeUtc(job) > Exports) DeleteFile(job);
                foreach (string file in Files(folder))
                {
                    string name = Path.GetFileName(file);
                    if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) && now - File.GetLastWriteTimeUtc(file) > Exports) DeleteFile(file);
                    else if (Pid(name, "unity-") is int unity && unity != ownPid && !alive(unity)) DeleteFile(file);
                    else if (Pid(name, "painter-") is int painter && !alive(painter)) DeleteFile(file);
                }
            }
            catch (Exception) { }
        }

        // "unity-1234.json" → 1234.
        private static int? Pid(string name, string prefix) =>
            name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal) &&
            int.TryParse(name.Substring(prefix.Length, name.Length - prefix.Length - 5), out int pid) ? pid : (int?)null;

        private static bool Alive(int pid)
        {
            try
            {
                using (var process = Process.GetProcessById(pid)) return !process.HasExited;
            }
            catch (Exception) { return false; }
        }

        private static string[] Folders(string path)
        {
            try { return Directory.Exists(path) ? Directory.GetDirectories(path) : new string[0]; }
            catch (Exception) { return new string[0]; }
        }

        private static string[] Files(string path)
        {
            try { return Directory.Exists(path) ? Directory.GetFiles(path) : new string[0]; }
            catch (Exception) { return new string[0]; }
        }

        private static void Delete(string folder)
        {
            try { Directory.Delete(folder, true); }
            catch (Exception) { }
        }

        private static void DeleteFile(string file)
        {
            try { File.Delete(file); }
            catch (Exception) { }
        }
    }
}
