using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Orbiters.Toolkit.Editor.LiveLink;

namespace Orbiters.MyAvatar.Editor.Painter
{
    /// <summary>
    /// The live link between Substance 3D Painter (My Avatar's Painter plugin, <c>Painter~/orbiters_my_avatar</c>) and the
    /// avatar in Unity: textures show on the avatar as they are painted, and are applied for good when the Painter project is
    /// saved.
    ///
    /// Discovery: each Unity editor with the link on writes <c>%LOCALAPPDATA%/Orbiters/PainterLink/unity-&lt;pid&gt;.json</c>
    /// (<see cref="Endpoint"/>: port, token, project and its avatars), rewritten when the avatars or the port change, deleted
    /// when Unity stops. Painter lists the avatars of the editors still running; a project linked to one keeps it in its
    /// metadata and connects again on its own. Projects Unity asks Painter to create are jobs in <c>jobs/</c> (see
    /// <see cref="PainterProjectJob"/>).
    ///
    /// Transport: Toolkit's live link format (TCP on 127.0.0.1, magic "ORPL", version <see cref="Version"/>). Painter's
    /// frames carry the editor's token, Hello first; Unity reads nothing else from a connection until it proved the token,
    /// and Painter reads no token in Unity's frames. Painter only connects to the port the endpoint's Unity listens on and,
    /// when the endpoint offers it (<see cref="Endpoint.proof"/>), first asks Unity to prove it holds the token (Toolkit's
    /// type 0 frame, <c>LiveLinkFormat.Proof</c>): it sends the token only then.
    /// Payloads are UTF-8 JSON; images travel as files Painter writes in the link's folder, and Unity only reads them
    /// there (<c>live/</c> for live images, <c>commit/</c> for saved maps).
    /// <list type="bullet">
    /// <item>Hello (Painter): <see cref="Hello"/>, which avatar the project is linked to. Unity answers Welcome
    /// (<see cref="Welcome"/>: the avatar's materials and the mapping it knows), or Status with an error and closes.</item>
    /// <item>Structure (Painter): <see cref="Structure"/>, the project's texture sets and channels, at connection and when
    /// they change. Unity answers Mapping (<see cref="Mapping"/>): which texture set goes on which material, which channels it
    /// wants live, at which resolution.</item>
    /// <item>Texture (Painter): <see cref="Texture"/>, a channel of a texture set rendered (live: at the preview resolution,
    /// shortly after a stroke). Several waiting for one channel collapse to the newest.</item>
    /// <item>Committing (Painter): the project is being saved and its maps exported. Commit (Painter):
    /// <see cref="Commit"/>, the exported maps: Unity applies them to the materials, as a texture drop does.</item>
    /// <item>Status (Unity): <see cref="Status"/>, what Painter's panel shows; with <see cref="Status.closing"/>, Unity
    /// ends the link (Painter closes it and keeps the message). Request (Unity): <see cref="Request"/>, render again
    /// ("refresh"), export now ("commit"), or put a new mesh of the avatar in the project ("reloadMesh", with its OBJ:
    /// the avatar's meshes changed). MeshReloaded (Painter): <see cref="MeshReloaded"/>, how that went (sent once Painter is
    /// linked again when Unity was away meanwhile).</item>
    /// <item>Bye (either way): the connection closes.</item>
    /// </list>
    /// </summary>
    internal static class PainterLinkProtocol
    {
        public const uint Magic = 0x4C50524F; // "ORPL"
        public const byte Version = 1;
        // JSON only: texture data goes through files.
        public const int MaxPayloadBytes = 4 * 1024 * 1024;
        public const string EndpointKind = "orbiters.myavatar.painterLink";
        public const string JobKind = "orbiters.myavatar.painterProject";
        /// <summary>A job opening an avatar's existing project: <c>{"kind","project","avatarName","unityPid"}</c>.</summary>
        public const string OpenKind = "orbiters.myavatar.painterOpen";

        public enum FrameType : byte
        {
            Hello = 1,
            Welcome = 2,
            Structure = 3,
            Mapping = 4,
            Texture = 5,
            Committing = 6,
            Commit = 7,
            Status = 8,
            Request = 9,
            Bye = 10,
            MeshReloaded = 11,
        }

        public static readonly LiveLinkFormat Format = new LiveLinkFormat(Magic, Version, MaxPayloadBytes, type => Enum.IsDefined(typeof(FrameType), type));

        /// <summary>
        /// Whether the Toolkit's live link server answers proof requests (Toolkit releases before it close the connection
        /// on one, so the endpoint only offers it when it does). The same check as Mixer's link.
        /// </summary>
        public static readonly bool ServerProvesToken =
            typeof(LiveLinkFormat).GetMethod("Proof", new[] { typeof(string), typeof(int), typeof(byte[]) }) != null;

        public static byte[] Json(object value) => Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value));

        public static T Read<T>(byte[] payload) => JsonConvert.DeserializeObject<T>(Encoding.UTF8.GetString(payload ?? Array.Empty<byte>()));

        /// <summary>
        /// A payload read from Painter, as it is used: lists that are missing become empty, entries that are null or unnamed
        /// go; null when what is left can't be used (a texture without its set, channel or file).
        /// </summary>
        public static object Checked(object payload)
        {
            switch (payload)
            {
                case Structure structure:
                    structure.textureSets = (structure.textureSets ?? new List<TextureSetInfo>()).Where(s => s != null && !string.IsNullOrEmpty(s.name)).ToList();
                    foreach (var set in structure.textureSets) set.channels = (set.channels ?? new List<string>()).Where(c => c != null).ToList();
                    return structure;
                case Texture texture:
                    return string.IsNullOrEmpty(texture.textureSet) || string.IsNullOrEmpty(texture.channel) || string.IsNullOrEmpty(texture.path) ? null : texture;
                case Commit commit:
                    commit.maps = (commit.maps ?? new List<CommitMap>()).Where(m => m != null && !string.IsNullOrEmpty(m.textureSet) && !string.IsNullOrEmpty(m.map)).ToList();
                    return commit;
                default:
                    return payload;
            }
        }

        // ---- Payloads (field names are the JSON's) ----

        public sealed class Endpoint
        {
            public string kind = EndpointKind;
            public int protocolVersion = Version;
            public int pid;
            public string host = "127.0.0.1";
            public int port;
            public string token;
            /// <summary>Unity answers proof requests: Painter checks Unity holds the token before it sends it.</summary>
            public bool proof = ServerProvesToken;
            public string projectName, projectPath;
            public List<EndpointAvatar> avatars = new List<EndpointAvatar>();
        }

        public sealed class EndpointAvatar
        {
            /// <summary>The avatar's My Avatar component, as a GlobalObjectId string (stable across sessions).</summary>
            public string id;
            public string name, scene;
        }

        public sealed class Hello
        {
            public int protocolVersion;
            public string painter, plugin;
            public string avatarId;
            public string projectName, projectPath;
        }

        public sealed class Welcome
        {
            public string avatarId, avatarName;
            public List<MaterialInfo> materials = new List<MaterialInfo>();
        }

        public sealed class MaterialInfo
        {
            /// <summary>The material's identity on this avatar (its asset GUID and local id, or its name for a scene material).</summary>
            public string id;
            public string name, shader;
        }

        public sealed class Structure
        {
            public List<TextureSetInfo> textureSets = new List<TextureSetInfo>();
        }

        public sealed class TextureSetInfo
        {
            public string name;
            public int width, height;
            /// <summary>Channel identifiers as Painter's JS API names them: basecolor, roughness, metallic, normal, height,
            /// emissive, opacity, ambientOcclusion...</summary>
            public List<string> channels = new List<string>();
        }

        public sealed class Mapping
        {
            public List<SetMapping> sets = new List<SetMapping>();
            /// <summary>The long side of live images, in pixels (they are rendered square or at the set's ratio).</summary>
            public int previewSize = 1024;
        }

        public sealed class SetMapping
        {
            public string textureSet;
            /// <summary>The material it paints (<see cref="MaterialInfo.id"/>), or null when it isn't on the avatar.</summary>
            public string material;
            public string materialName;
            /// <summary>The channels shown live.</summary>
            public List<string> channels = new List<string>();
            /// <summary>The maps the material takes when the project is saved (see <see cref="CommitMap.map"/>).</summary>
            public List<string> maps = new List<string>();
        }

        public sealed class Texture
        {
            public string textureSet, channel, path;
            public int width, height;
            /// <summary>Grows with every image of a channel: an older one arriving late is dropped.</summary>
            public long revision;
        }

        public sealed class Commit
        {
            public List<CommitMap> maps = new List<CommitMap>();
            /// <summary>Why Painter could not export (no maps then), or null.</summary>
            public string error;
        }

        public sealed class CommitMap
        {
            /// <summary>basecolor (opacity in its alpha), normal (OpenGL, with the baked normal and the height), roughness,
            /// metallic, emissive, ambientOcclusion.</summary>
            public string textureSet, map, path;
        }

        public sealed class Status
        {
            public string message;
            public bool error;
            /// <summary>The texture sets shown live on the avatar.</summary>
            public List<string> live = new List<string>();
            /// <summary>Unity ends the link after this (the avatar's scene closed, another project took the avatar): Painter
            /// closes the connection and keeps the message instead of saying Unity went away.</summary>
            public bool closing;
            /// <summary>Another Painter project linked to the avatar: this one stops linking on its own until asked to.</summary>
            public bool takenOver;
        }

        public sealed class Request
        {
            public string kind;
            /// <summary>The OBJ of a "reloadMesh".</summary>
            public string path;
        }

        public sealed class MeshReloaded
        {
            /// <summary>Why Painter could not load the mesh, or null when the project has it.</summary>
            public string error;
            /// <summary>The OBJ the answer is about (the <see cref="Request.path"/> it answers).</summary>
            public string path;
        }
    }
}
