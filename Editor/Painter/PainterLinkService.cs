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
    /// <summary>
    /// Unity's end of the Substance 3D Painter link (see <see cref="PainterLinkProtocol"/>): one listener per editor, started
    /// when an avatar's My Avatar Inspector shows with Painter installed (and again after script reloads), the endpoint file
    /// Painter finds it by, and a <see cref="Session"/> for each Painter project linked to an avatar.
    /// </summary>
    [InitializeOnLoad]
    internal static class PainterLinkService
    {
        private const string TokenKey = "Orbiters.MyAvatar.PainterLink.Token", PortKey = "Orbiters.MyAvatar.PainterLink.Port", ActiveKey = "Orbiters.MyAvatar.PainterLink.Active";
        private const double EndpointSettleSeconds = 1.5;
        private static LiveLinkServer server;
        private static string token;
        private static readonly Dictionary<int, Session> sessions = new Dictionary<int, Session>();
        private static double endpointDirtyAt = -1;
        private static string writtenEndpoint;

        /// <summary>Something the Painter card shows changed (a project linked or gone, a state, a commit).</summary>
        internal static event Action Changed;

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

        /// <summary>The folder Painter and Unity share: endpoints, project jobs, live images and exports.</summary>
        internal static string LinkFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Orbiters", "PainterLink");

        internal static bool Running => server != null;

        /// <summary>Starts listening (once) when Painter is installed.</summary>
        internal static void EnsureRunning()
        {
            if (server != null || !PainterApp.Installed || EditorApplication.isPlayingOrWillChangePlaymode) return;
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
            server.Received += OnFrame;
            server.ConnectionChanged += OnConnection;
            EditorApplication.hierarchyChanged += MarkEndpoint;
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorSceneManager.sceneClosed += OnSceneClosed;
            EditorApplication.update += Update;
            EditorApplication.quitting += Quit;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            WriteEndpoint();
        }

        private static void OnSceneOpened(UnityEngine.SceneManagement.Scene scene, OpenSceneMode mode) => MarkEndpoint();
        private static void OnSceneClosed(UnityEngine.SceneManagement.Scene scene) => MarkEndpoint();

        // Payloads are JSON: read on the connection's thread, so one that doesn't hold together closes it there.
        private static bool Decode(LiveLinkFrame frame)
        {
            try
            {
                switch ((PainterLinkProtocol.FrameType)frame.Type)
                {
                    case PainterLinkProtocol.FrameType.Hello: frame.Decoded = PainterLinkProtocol.Read<PainterLinkProtocol.Hello>(frame.Payload); break;
                    case PainterLinkProtocol.FrameType.Structure: frame.Decoded = PainterLinkProtocol.Read<PainterLinkProtocol.Structure>(frame.Payload); break;
                    case PainterLinkProtocol.FrameType.Texture: frame.Decoded = PainterLinkProtocol.Read<PainterLinkProtocol.Texture>(frame.Payload); break;
                    case PainterLinkProtocol.FrameType.Commit: frame.Decoded = PainterLinkProtocol.Read<PainterLinkProtocol.Commit>(frame.Payload); break;
                }
                return true;
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
            foreach (var session in sessions.Values) session.Dispose();
            sessions.Clear();
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
            foreach (var session in sessions.Values.ToList()) session.Update();
        }

        /// <summary>The avatars of the loaded scenes, by their My Avatar component.</summary>
        internal static IEnumerable<MyAvatar> SceneAvatars() =>
            UnityEngine.Object.FindObjectsOfType<MyAvatar>(true).Where(a => a && !EditorUtility.IsPersistent(a) && a.gameObject.scene.IsValid());

        internal static string Id(MyAvatar avatar) => GlobalObjectId.GetGlobalObjectIdSlow(avatar).ToString();

        private static MyAvatar Find(string id)
        {
            if (string.IsNullOrEmpty(id) || !GlobalObjectId.TryParse(id, out var global)) return null;
            return GlobalObjectId.GlobalObjectIdentifierToObjectSlow(global) as MyAvatar;
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
            }
            catch (Exception ex) { Debug.LogWarning("[My Avatar] Substance Painter link: could not write " + EndpointPath + " (" + ex.Message + ")."); }
        }

        // ---- Sessions ----

        /// <summary>The Painter project linked to this avatar now, or null.</summary>
        internal static Session For(MyAvatar avatar) => sessions.Values.FirstOrDefault(s => s.Avatar == avatar);

        private static void OnConnection(int connection, bool up)
        {
            if (up) return;
            if (sessions.TryGetValue(connection, out var session))
            {
                session.Dispose();
                sessions.Remove(connection);
                Changed?.Invoke();
            }
        }

        private static void OnFrame(LiveLinkFrame frame)
        {
            sessions.TryGetValue(frame.Connection, out var session);
            switch ((PainterLinkProtocol.FrameType)frame.Type)
            {
                case PainterLinkProtocol.FrameType.Hello:
                {
                    var hello = (PainterLinkProtocol.Hello)frame.Decoded;
                    var avatar = Find(hello.avatarId);
                    if (!avatar)
                    {
                        server.Send(frame.Connection, (byte)PainterLinkProtocol.FrameType.Status, PainterLinkProtocol.Json(new PainterLinkProtocol.Status
                        {
                            message = "This project's avatar isn't in the scenes open in Unity.", error = true,
                        }));
                        server.Disconnect(frame.Connection);
                        return;
                    }
                    session?.Dispose();
                    // One Painter project per avatar: a newer one takes over.
                    foreach (var other in sessions.Where(p => p.Value.Avatar == avatar).ToList())
                    {
                        other.Value.Dispose();
                        sessions.Remove(other.Key);
                        server.Disconnect(other.Key);
                    }
                    sessions[frame.Connection] = new Session(frame.Connection, avatar, hello);
                    sessions[frame.Connection].Welcome();
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
            }
        }

        /// <summary>A Painter project linked to an avatar: its texture sets, which material each paints, what shows live.</summary>
        internal sealed class Session : IDisposable
        {
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

            public Session(int connection, MyAvatar avatar, PainterLinkProtocol.Hello hello)
            {
                Connection = connection;
                Avatar = avatar;
                Hello = hello;
            }

            public string ProjectName => string.IsNullOrEmpty(Hello.projectName) ? "Untitled project" : Path.GetFileNameWithoutExtension(Hello.projectName);

            public void Welcome()
            {
                Send(PainterLinkProtocol.FrameType.Welcome, new PainterLinkProtocol.Welcome { avatarId = Id(Avatar), avatarName = Avatar.name });
                if (!string.IsNullOrEmpty(Hello.projectPath) && Avatar.painter.projectPath != Hello.projectPath)
                {
                    Avatar.painter.projectPath = Hello.projectPath;
                    EditorUtility.SetDirty(Avatar);
                }
            }

            private void Send(PainterLinkProtocol.FrameType type, object payload) =>
                server?.Send(Connection, (byte)type, PainterLinkProtocol.Json(payload));

            public void Update()
            {
                if (!Avatar) Dispose();
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
                // A texture set no longer painting a material leaves the avatar as it is.
                foreach (string gone in preview.Live.Where(name => mapping.sets.All(s => s.textureSet != name || s.material == null)).ToList()) preview.Clear(gone);
                Send(PainterLinkProtocol.FrameType.Mapping, mapping);
            }

            public async void OnTexture(PainterLinkProtocol.Texture texture)
            {
                string key = texture.textureSet + "\n" + texture.channel;
                if (preview.Stale(texture.textureSet, texture.channel, texture.revision)) return;
                if (decoding.TryGetValue(key, out long newest) && newest >= texture.revision) return;
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
                if (!Avatar || decoding.TryGetValue(key, out newest) && newest > texture.revision || sessions.Values.All(s => s != this)) return;
                var material = PainterMapping.Material(Avatar, texture.textureSet);
                var targets = material ? PainterTargets.For(material, TextureMatching.Slots(Avatar)) : null;
                if (targets == null) return;
                preview.Show(Avatar.transform, texture.textureSet, texture.channel, texture.revision, image, material, targets);
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
                Changed?.Invoke();
            }

            /// <summary>Asks Painter to export and apply every mapped texture now.</summary>
            public void RequestCommit() => Send(PainterLinkProtocol.FrameType.Request, new PainterLinkProtocol.Request { kind = "commit" });

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
}
