"""My Avatar link for Adobe Substance 3D Painter (Orbiters).

Links a Painter project to an avatar in Unity (My Avatar): what is painted shows on the avatar in Unity shortly after each
stroke, and is applied to its materials when the project is saved. Projects My Avatar creates ("Open in Substance") are
set up here: mesh, texture sets, the avatar's textures in a "From Unity" fill layer, its normal maps as baked normals.

Installed by My Avatar in Painter's user startup folder (Documents/Adobe/Adobe Substance 3D Painter/python/startup):
Painter loads it at every start, and again when My Avatar installs a newer one. The protocol is described in My Avatar's
PainterLinkProtocol.cs. Painter only links to a Unity editor listening where its endpoint file says, and sends the token
only once that Unity proved it holds it (when the endpoint says it can: another program on that port would get the
token); it only runs project jobs made of files Unity wrote in the link's folder.
"""

import ctypes
import glob
import hashlib
import hmac
import json
import os
import struct
import sys
import tempfile
import time
import traceback
import zlib
from collections import OrderedDict

from PySide6 import QtCore, QtNetwork, QtWidgets

import substance_painter.application
import substance_painter.colormanagement
import substance_painter.event
import substance_painter.export
import substance_painter.js
import substance_painter.layerstack
import substance_painter.logging
import substance_painter.project
import substance_painter.resource
import substance_painter.textureset
import substance_painter.ui
import substance_painter_plugins

PLUGIN_VERSION = "1.4.0"
MAGIC = 0x4C50524F  # "ORPL"
# How long a connection attempt may wait for Unity before it is tried again.
CONNECT_SECONDS = 5.0
# How long Unity may take to prove it holds the endpoint's token.
PROOF_SECONDS = 5.0
# How long a project Unity asked for may take to open before another job can run.
PENDING_SECONDS = 600.0
# How long the first save of a new project may take before linking goes on without it.
SAVE_SECONDS = 120.0
VERSION = 1
HELLO, WELCOME, STRUCTURE, MAPPING, TEXTURE, COMMITTING, COMMIT, STATUS, REQUEST, BYE, MESH_RELOADED = range(1, 12)
HEADER = struct.Struct("<IBBHI")
# The largest token and payload a frame may carry, as Unity's (LiveLinkFormat.MaxTokenBytes, PainterLinkProtocol.MaxPayloadBytes):
# a frame announcing more is never buffered.
MAX_TOKEN_BYTES = 256
MAX_PAYLOAD_BYTES = 4 * 1024 * 1024
# Frame type 0 is the Toolkit's live link proof, the same on every link: before sending the token, Painter sends a nonce
# without a token, and Unity answers without a token with HMAC-SHA256 (keyed with the token) of this label, the magic
# (u32), the port connected to (u16) and the nonce (LiveLinkFormat.Proof).
PROOF = 0
PROOF_LABEL = b"Orbiters live link proof"
PROOF_NONCE_BYTES = 32
ENDPOINT_KIND = "orbiters.myavatar.painterLink"
PRESENCE_KIND = "orbiters.myavatar.painter"
JOB_KIND = "orbiters.myavatar.painterProject"
OPEN_KIND = "orbiters.myavatar.painterOpen"
METADATA = "orbiters.myavatar"

ChannelType = substance_painter.textureset.ChannelType
# Painter's channels as the link names them (the JavaScript API's map names).
CHANNELS = OrderedDict([
    (ChannelType.BaseColor, "basecolor"),
    (ChannelType.Opacity, "opacity"),
    (ChannelType.Normal, "normal"),
    (ChannelType.Height, "height"),
    (ChannelType.Roughness, "roughness"),
    (ChannelType.Metallic, "metallic"),
    (ChannelType.Emissive, "emissive"),
    (ChannelType.AO, "ambientocclusion"),
])
# The map a channel's strokes change when the project is saved.
MAP_OF_CHANNEL = {"basecolor": "basecolor", "opacity": "basecolor", "normal": "normal", "height": "normal",
                  "roughness": "roughness", "metallic": "metallic", "emissive": "emissive", "ambientocclusion": "ambientocclusion"}
# The channel a saved map is exported from (the normal always exports: the baked normal at least).
CHANNEL_OF_MAP = {"basecolor": ChannelType.BaseColor, "roughness": ChannelType.Roughness, "metallic": ChannelType.Metallic,
                  "emissive": ChannelType.Emissive, "ambientocclusion": ChannelType.AO}
# The values of a job's texture set that are images Unity wrote.
JOB_IMAGES = ("basecolor", "opacity", "normal", "roughness", "metallic", "emissive")


def log(message):
    substance_painter.logging.info("[My Avatar] " + message)


def link_folder():
    if sys.platform == "win32":
        base = os.environ.get("LOCALAPPDATA") or os.path.expanduser("~/AppData/Local")
    elif sys.platform == "darwin":
        base = os.path.expanduser("~/Library/Application Support")
    else:
        base = os.environ.get("XDG_DATA_HOME") or os.path.expanduser("~/.local/share")
    return os.path.join(base, "Orbiters", "PainterLink")


def pid_alive(pid):
    if not pid:
        return False
    if sys.platform == "win32":
        # os.kill would end the process on Windows: ask for its exit code instead.
        handle = ctypes.windll.kernel32.OpenProcess(0x1000, False, int(pid))  # PROCESS_QUERY_LIMITED_INFORMATION
        if not handle:
            return False
        try:
            code = ctypes.c_ulong()
            return bool(ctypes.windll.kernel32.GetExitCodeProcess(handle, ctypes.byref(code))) and code.value == 259  # STILL_ACTIVE
        finally:
            ctypes.windll.kernel32.CloseHandle(handle)
    try:
        os.kill(int(pid), 0)
        return True
    except OSError:
        return False


def read_endpoints():
    """The Unity editors running with the Painter link on, each with its avatars."""
    found = []
    for path in glob.glob(os.path.join(link_folder(), "unity-*.json")):
        try:
            with open(path, "r", encoding="utf-8") as handle:
                endpoint = json.load(handle)
        except (OSError, ValueError):
            continue
        if endpoint.get("kind") == ENDPOINT_KIND and endpoint.get("protocolVersion") == VERSION and pid_alive(endpoint.get("pid")):
            found.append(endpoint)
    return found


def presence_path():
    return os.path.join(link_folder(), "painter-%d.json" % os.getpid())


def write_presence():
    """Tells Unity this Painter runs the plugin, and which version (what it can do: open and create projects)."""
    try:
        os.makedirs(link_folder(), exist_ok=True)
        temp = presence_path() + ".tmp"
        with open(temp, "w", encoding="utf-8") as handle:
            json.dump({"kind": PRESENCE_KIND, "plugin": PLUGIN_VERSION, "pid": os.getpid(),
                       "painter": substance_painter.application.version()}, handle)
        os.replace(temp, presence_path())
    except OSError as exception:
        log("Could not tell Unity this Painter runs the plugin: %s" % exception)


def texture_set_names():
    return [ts.name() for ts in substance_painter.textureset.all_texture_sets()]


def proof_request(nonce):
    """The frame asking Unity to prove it holds the token: the nonce, no token."""
    return HEADER.pack(MAGIC, VERSION, PROOF, 0, len(nonce)) + nonce


def proof(token, port, nonce):
    """What a Unity holding the token answers to a proof request with this nonce, on this port."""
    message = PROOF_LABEL + struct.pack("<IH", MAGIC, int(port) & 0xFFFF) + bytes(nonce)
    return hmac.new(token.encode("utf-8"), message, hashlib.sha256).digest()


def same_path(a, b):
    """Whether two paths name the same file."""
    if not a or not b:
        return False
    return os.path.normcase(os.path.abspath(a)) == os.path.normcase(os.path.abspath(b))


def is_inside(path, folder):
    """Whether a path lies inside a folder."""
    if not path or not folder:
        return False
    root = os.path.normcase(os.path.abspath(folder)).rstrip("\\/") + os.sep
    return os.path.normcase(os.path.abspath(path)).startswith(root)


def choose_endpoint(endpoints, avatar_id, unity_project=None):
    """The Unity editor to link the avatar through: one listing it, the Unity project it was linked in first (a copy of
    that project open beside it lists the same avatar)."""
    listing = [e for e in endpoints if any(a.get("id") == avatar_id for a in e.get("avatars", []))]
    for endpoint in listing:
        if unity_project and same_path(endpoint.get("projectPath"), unity_project):
            return endpoint
    return listing[0] if listing else None


def owner_of(table, port):
    """The process listening on 127.0.0.1 (or every address) at a port, from a MIB_TCPTABLE_OWNER_PID; None if none."""
    if len(table) < 4:
        return None
    count = struct.unpack_from("<I", table, 0)[0]
    for i in range(count):
        offset = 4 + i * 24
        if offset + 24 > len(table):
            break
        _state, address, local_port, _remote, _remote_port, pid = struct.unpack_from("<6I", table, offset)
        # The port in network order in the low 16 bits; 127.0.0.1 in network order.
        if ((local_port & 0xFF) << 8 | (local_port >> 8) & 0xFF) == port and address in (0x0100007F, 0):
            return pid
    return None


def listening_pid(port):
    """The process that listens on 127.0.0.1 at a port (Windows), or None when it can't be told."""
    if sys.platform != "win32" or not port:
        return None
    try:
        get_table = ctypes.windll.iphlpapi.GetExtendedTcpTable
        size = ctypes.c_ulong(0)
        # AF_INET, TCP_TABLE_OWNER_PID_LISTENER.
        get_table(None, ctypes.byref(size), False, 2, 3, 0)
        for _ in range(3):
            buffer = ctypes.create_string_buffer(max(4, size.value))
            result = get_table(buffer, ctypes.byref(size), False, 2, 3, 0)
            if result == 0:
                return owner_of(buffer.raw, port)
            if result != 122:  # ERROR_INSUFFICIENT_BUFFER: the table grew meanwhile.
                return None
    except Exception:
        pass
    return None


def job_problem(job, folder, exists=os.path.exists):
    """
    Why a job from Unity can't run (None when it can): a project is only made from a mesh and images Unity wrote in the
    link's folder, and saved where no file is yet; only Painter projects are opened.
    """
    project = job.get("project") or ""
    if not str(project).lower().endswith(".spp"):
        return "it doesn't name a Painter project (%s)" % project
    if job.get("kind") == OPEN_KIND:
        return None
    if exists(project):
        return "a file is there already (%s)" % project
    mesh = job.get("mesh") or ""
    if not str(mesh).lower().endswith(".obj") or not is_inside(mesh, folder):
        return "its mesh isn't one Unity wrote (%s)" % mesh
    for entry in job.get("textureSets") or []:
        if not isinstance(entry, dict) or not entry.get("name"):
            return "a texture set has no name"
        for key in JOB_IMAGES:
            value = entry.get(key)
            if isinstance(value, str) and not is_inside(value, folder):
                return "an image isn't one Unity wrote (%s)" % value
    return None


def mapping_changes(old, new, old_size, new_size):
    """
    What to render after Unity sends a mapping: every live channel when the image size changed, else only those new,
    or of a texture set that paints another material now (the others show already).
    """
    wanted = []
    for name, entry in new.items():
        before = old.get(name)
        for channel in entry.get("channels", []):
            if new_size != old_size or before is None or before.get("material") != entry.get("material") \
                    or channel not in before.get("channels", []):
                wanted.append((name, channel))
    return wanted


def exportable(maps, has_channel):
    """The saved maps a texture set can export: those whose channel it has, and the normal."""
    return [m for m in maps if m == "normal" or (m in CHANNEL_OF_MAP and has_channel(CHANNEL_OF_MAP[m]))]


def new_export_folder(folder):
    """A folder of its own for a save's export, in commit/<pid>/ of the link's folder: dated, with a unique end (two saves
    within a second each get theirs: Unity deletes each one once it applied it)."""
    parent = os.path.join(folder, "commit", str(os.getpid()))
    os.makedirs(parent, exist_ok=True)
    return tempfile.mkdtemp(prefix=time.strftime("%Y%m%d-%H%M%S-"), dir=parent)


class Link(QtCore.QObject):
    """The connection to Unity, the live renders and the commits."""

    changed = QtCore.Signal()

    def __init__(self):
        super().__init__()
        self.socket = None
        self.buffer = bytearray()
        self.endpoint = None
        # Whether the connection's Unity proved it holds the token (or its endpoint doesn't ask): nothing is sent before.
        self.proved = False
        # The nonce of a proof request not answered yet, and when it was sent.
        self.awaiting_proof = None
        self.proof_started = 0.0
        self.avatar_id = None
        self.avatar_name = None
        self.connected = False
        self.message = "Not linked to an avatar."
        self.error = False
        self.mapping = {}  # texture set -> {"material", "materialName", "channels", "maps"}
        self.preview_size = 1024
        self.live_enabled = True
        self.dirty = OrderedDict()  # (texture set, channel) -> True, renders waiting
        self.touched = set()  # (texture set, map) changed since the last commit
        # Whether the user clicked or pressed a pen since the project opened: before that, texture changes are Painter
        # computing the project it loads, not edits to apply.
        self.interacted = False
        self.revision = 0
        self.last_render = None
        self.pressed = False
        self.saving_ourselves = False
        self.pending_job = None
        self.pending_since = 0.0
        self.structure_sent = False
        self.last_structure = None
        self.connect_started = 0.0
        # The Unity project the link was made in (a copy of it may list the same avatar).
        self.unity_project = None
        # Another project took the avatar over in Unity: no linking again on its own until asked.
        self.paused = False
        # A new project's first save is under way: it links once it has its file.
        self.awaiting_save = 0.0
        # The mesh Unity asked for last, and the answer Unity didn't get (it was away): sent once linked again.
        self.mesh_path = None
        self.unsent_mesh_answer = None
        # The converted map that is the exported normal (looked up once), and when this code was loaded.
        self.normal_map = None
        try:
            self.loaded_at = os.path.getmtime(__file__)
        except OSError:
            self.loaded_at = time.time()
        self.render_timer = QtCore.QTimer(self)
        self.render_timer.setSingleShot(True)
        self.render_timer.timeout.connect(self.render_next)
        self.tick_timer = QtCore.QTimer(self)
        self.tick_timer.timeout.connect(self.tick)
        self.tick_timer.start(1500)

    # ---- Project and link state ----

    def project_ready(self):
        # A project being saved is "locked": Painter raises instead of answering.
        try:
            return substance_painter.project.is_open() and substance_painter.project.is_in_edition_state() and not substance_painter.project.is_busy()
        except Exception:
            return False

    def load_link(self):
        """
        The avatar the open project is linked to (its metadata). A project Painter keeps locked a moment (a save, an
        export) keeps the avatar it had: forgetting it would stop linking again for good.
        """
        if not substance_painter.project.is_open():
            self.avatar_id = None
            self.avatar_name = None
            self.unity_project = None
            self.paused = False
            return
        try:
            link = substance_painter.project.Metadata(METADATA).get("link")
        except Exception as exception:
            log("The project's link could not be read now (%s): keeping %s." % (exception, self.avatar_name or "none"))
            return
        self.paused = False
        if isinstance(link, dict):
            self.avatar_id = link.get("avatarId")
            self.avatar_name = link.get("avatarName")
            self.unity_project = link.get("unityProject")
        else:
            self.avatar_id = None
            self.avatar_name = None
            self.unity_project = None

    def link_to(self, endpoint, avatar):
        """Links the open project to an avatar of a Unity editor (kept in the project's metadata)."""
        if not substance_painter.project.is_open():
            return
        substance_painter.project.Metadata(METADATA).set("link", {
            "avatarId": avatar["id"], "avatarName": avatar.get("name"),
            "unityProject": endpoint.get("projectPath"), "plugin": PLUGIN_VERSION})
        self.avatar_id = avatar["id"]
        self.avatar_name = avatar.get("name")
        self.unity_project = endpoint.get("projectPath")
        self.paused = False
        self.disconnect()
        if self.trusted(endpoint):
            self.connect_to(endpoint)

    def unlink(self):
        if substance_painter.project.is_open():
            substance_painter.project.Metadata(METADATA).set("link", {})
        self.avatar_id = None
        self.avatar_name = None
        self.unity_project = None
        self.paused = False
        self.disconnect()
        self.set_message("Not linked to an avatar.")

    def resume(self):
        """Links again after another project took the avatar (it takes the avatar back)."""
        self.paused = False
        self.set_message("Linking to %s again…" % (self.avatar_name or "the avatar"))
        self.tick()

    def trusted(self, endpoint):
        """Whether the endpoint's Unity listens on its port: another program there would get the token."""
        owner = listening_pid(int(endpoint.get("port") or 0))
        if owner is None or owner == endpoint.get("pid"):
            return True
        self.set_message("Another program listens where Unity should: not linking. Reopen the avatar's Inspector in Unity.", True)
        return False

    def set_message(self, text, error=False):
        self.message = text
        self.error = error
        self.changed.emit()

    def tick(self):
        """Every second and a half: project jobs from Unity, and the linked avatar's editor when not connected."""
        try:
            self.check_update()
            self.check_jobs()
            self.check_proof()
            # The texture sets once Painter is free again (it was busy when Unity answered).
            if self.connected and self.proved and not self.structure_sent:
                self.send_structure()
            # An attempt Unity never answered (it was restarting its scripts): tried again from its endpoint file.
            if self.socket is not None and not self.connected and time.time() - self.connect_started > CONNECT_SECONDS:
                log("Unity didn't answer on port %s: trying again." % (self.endpoint or {}).get("port"))
                self.disconnect()
            # A new project links once its first save gave it its file (Unity tells the project it made by its file).
            saving = self.awaiting_save and time.time() - self.awaiting_save < SAVE_SECONDS
            if self.socket is None and self.avatar_id and not self.paused and not saving and self.project_ready():
                endpoint = choose_endpoint(read_endpoints(), self.avatar_id, self.unity_project)
                if endpoint is None:
                    self.set_message("Waiting for Unity: open the project with " + (self.avatar_name or "the avatar") + " in Unity.")
                elif self.trusted(endpoint):
                    self.connect_to(endpoint)
        except Exception:
            log("Tick failed: " + traceback.format_exc())

    # ---- Connection ----

    def connect_to(self, endpoint):
        self.endpoint = endpoint
        self.buffer = bytearray()
        self.connect_started = time.time()
        self.proved = False
        self.awaiting_proof = None
        socket = QtNetwork.QTcpSocket(self)
        self.socket = socket
        # Each socket's signals act only while it is the link's: a closed one's late "disconnected" must not end the
        # connection made after it.
        socket.connected.connect(lambda: self.on_connected() if self.socket is socket else None)
        socket.readyRead.connect(lambda: self.on_ready_read() if self.socket is socket else None)
        socket.disconnected.connect(lambda: self.on_disconnected() if self.socket is socket else None)
        socket.errorOccurred.connect(lambda _: self.on_disconnected() if self.socket is socket else None)
        socket.connectToHost(endpoint.get("host", "127.0.0.1"), int(endpoint["port"]))

    def disconnect(self):
        socket = self.socket
        self.socket = None
        self.connected = False
        self.mapping = {}
        self.awaiting_proof = None
        # Nothing read from that connection is kept.
        self.buffer = bytearray()
        if socket is not None:
            try:
                # A goodbye carries the token: only to a Unity that proved it holds it.
                if self.proved and socket.state() == QtNetwork.QAbstractSocket.SocketState.ConnectedState:
                    self.send(BYE, socket=socket)
                socket.abort()
            except RuntimeError:
                pass
            socket.deleteLater()
        self.proved = False
        self.changed.emit()

    def refuse(self, message):
        """Drops a connection whose peer didn't prove it is the endpoint's Unity: the token never went to it."""
        log(message)
        self.proved = False
        self.disconnect()
        self.set_message(message, True)

    def check_proof(self):
        """A proof request Unity never answered: the connection goes (it is tried again from the endpoint file)."""
        if self.awaiting_proof is not None and time.time() - self.proof_started > PROOF_SECONDS:
            self.refuse("Unity didn't prove it holds the link's token on port %s: not linked." % (self.endpoint or {}).get("port"))

    def send(self, frame_type, payload=None, socket=None):
        socket = socket or self.socket
        # Nothing (the token least of all) leaves before Unity proved it holds the token.
        if socket is None or not self.proved:
            return False
        token = (self.endpoint or {}).get("token", "").encode("utf-8")
        data = b"" if payload is None else json.dumps(payload).encode("utf-8")
        socket.write(HEADER.pack(MAGIC, VERSION, frame_type, len(token), len(data)) + token + data)
        socket.flush()
        return True

    def on_connected(self):
        self.connected = True
        if (self.endpoint or {}).get("proof"):
            # Unity proves it holds the endpoint's token before Painter sends it (something else may listen on the port
            # while Unity reloads its scripts).
            nonce = os.urandom(PROOF_NONCE_BYTES)
            self.awaiting_proof = nonce
            self.proof_started = time.time()
            self.socket.write(proof_request(nonce))
            self.socket.flush()
            self.set_message("Checking Unity…")
            return
        self.proved = True
        self.hello()

    def on_proof(self, token_length, payload):
        """Unity's answer to the proof request: linked when it holds the token, dropped otherwise."""
        nonce = self.awaiting_proof
        endpoint = self.endpoint or {}
        expected = proof(endpoint.get("token", ""), endpoint.get("port") or 0, nonce) if nonce is not None else b""
        if nonce is None or token_length != 0 or not hmac.compare_digest(payload, expected):
            self.refuse("What listens on Unity's port doesn't hold the link's token (an editor that is gone?): not linked.")
            return
        self.awaiting_proof = None
        self.proved = True
        self.hello()

    def hello(self):
        """Says which avatar the project is linked to, then sends its texture sets."""
        log("Linked to Unity on port %s." % (self.endpoint or {}).get("port"))
        self.send(HELLO, {
            "protocolVersion": VERSION, "painter": substance_painter.application.version(), "plugin": PLUGIN_VERSION,
            "avatarId": self.avatar_id, "projectName": substance_painter.project.name(),
            "projectPath": substance_painter.project.file_path()})
        self.structure_sent = False
        self.send_structure()
        # A mesh loaded while Unity was away (its scripts reloading): it still waits for the answer.
        if self.unsent_mesh_answer is not None:
            answer, self.unsent_mesh_answer = self.unsent_mesh_answer, None
            self.send(MESH_RELOADED, answer)
        self.set_message("Connected to Unity.")

    def on_disconnected(self):
        if self.socket is None:
            return
        socket = self.socket
        self.socket = None
        self.connected = False
        self.proved = False
        self.awaiting_proof = None
        self.mapping = {}
        socket.deleteLater()
        log("Unity went away: linking again once it is back.")
        self.set_message("Unity went away: linking again once it is back.")

    def on_ready_read(self):
        socket = self.socket
        if socket is None:
            return
        self.buffer.extend(bytes(socket.readAll()))
        while len(self.buffer) >= HEADER.size:
            magic, version, frame_type, token_length, payload_length = HEADER.unpack_from(self.buffer)
            if magic != MAGIC or version != VERSION:
                self.set_message("Unity speaks another version of the link: update My Avatar and its Painter plugin.", True)
                self.disconnect()
                return
            if token_length > MAX_TOKEN_BYTES or payload_length > MAX_PAYLOAD_BYTES:
                self.refuse("Unity sent a frame larger than the link allows: not linked.")
                return
            size = HEADER.size + token_length + payload_length
            if len(self.buffer) < size:
                return
            payload = bytes(self.buffer[HEADER.size + token_length:size])
            del self.buffer[:size]
            # The proof (no token) comes first when it was asked for; nothing else is read before it.
            if frame_type == PROOF:
                self.on_proof(token_length, payload)
                if self.socket is not socket:
                    return
                continue
            if not self.proved:
                self.refuse("Unity sent frames before proving it holds the link's token: not linked.")
                return
            try:
                data = json.loads(payload.decode("utf-8")) if payload else {}
                self.on_frame(frame_type, data if isinstance(data, dict) else {})
            except Exception:
                log("Frame failed: " + traceback.format_exc())
            # The frame ended the link: what follows it is not read.
            if self.socket is not socket:
                return

    def on_frame(self, frame_type, data):
        if frame_type == WELCOME:
            self.avatar_name = data.get("avatarName") or self.avatar_name
            self.set_message("Linked to " + (self.avatar_name or "the avatar") + ".")
        elif frame_type == MAPPING:
            mapping = {s["textureSet"]: s for s in data.get("sets", []) if isinstance(s, dict) and s.get("textureSet") and s.get("material")}
            size = int(data.get("previewSize") or 1024)
            # Shown live once, as it is now: what is new to Unity (a mapping sent again changes nothing shown).
            for key in mapping_changes(self.mapping, mapping, self.preview_size, size):
                self.dirty[key] = True
            self.mapping = mapping
            self.preview_size = size
            self.schedule_render(50)
            self.changed.emit()
        elif frame_type == STATUS:
            self.set_message(data.get("message") or self.message, bool(data.get("error")))
            if data.get("takenOver"):
                self.paused = True
            # Unity ends the link (the avatar's scene closed, another project took it): its message stays shown.
            if data.get("closing"):
                log("Unity ended the link: %s" % self.message)
                self.disconnect()
        elif frame_type == REQUEST:
            if data.get("kind") == "refresh":
                for name, entry in self.mapping.items():
                    for channel in entry.get("channels", []):
                        self.dirty[(name, channel)] = True
                self.schedule_render(0)
            elif data.get("kind") == "commit":
                self.commit_later(everything=True)
            elif data.get("kind") == "reloadMesh":
                self.reload_mesh(data.get("path"))
        elif frame_type == BYE:
            self.on_disconnected()

    def send_structure(self):
        if not self.connected or not self.project_ready():
            self.structure_sent = False
            return
        sets = []
        for ts in substance_painter.textureset.all_texture_sets():
            resolution = ts.get_resolution()
            channels = []
            try:
                channels = [CHANNELS[c] for c in ts.get_stack().all_channels().keys() if c in CHANNELS]
            except Exception:
                pass
            sets.append({"name": ts.name(), "width": resolution.width, "height": resolution.height, "channels": channels})
        payload = {"textureSets": sets}
        # Layer edits ask for it again and again: the same texture sets go once.
        if self.structure_sent and payload == self.last_structure:
            return
        self.structure_sent = self.send(STRUCTURE, payload)
        if self.structure_sent:
            self.last_structure = payload

    # ---- Live renders ----

    def on_texture_state(self, event):
        if event.action == substance_painter.event.TextureStateEventAction.REMOVE:
            self.send_structure()
            return
        channel = CHANNELS.get(event.channel_type)
        if channel is None:
            return
        try:
            name = substance_painter.textureset.Stack(stack_id=event.stack_id).material().name()
        except Exception:
            return
        if event.action == substance_painter.event.TextureStateEventAction.ADD:
            self.send_structure()
        if self.interacted and event.action == substance_painter.event.TextureStateEventAction.UPDATE:
            self.touched.add((name, MAP_OF_CHANNEL[channel]))
        entry = self.mapping.get(name)
        if not self.live_enabled or entry is None:
            return
        # The normal shown live is the one saving exports: painted normal and height over the baked normal.
        live = "normal" if channel == "height" else channel
        if live in entry.get("channels", []):
            self.dirty[(name, live)] = True
        self.schedule_render(250)

    def schedule_render(self, milliseconds):
        if self.dirty:
            self.render_timer.start(milliseconds)

    def preview_resolution(self, texture_set):
        resolution = substance_painter.textureset.TextureSet.from_name(texture_set).get_resolution()
        side = max(resolution.width, resolution.height)
        scale = min(1.0, float(self.preview_size) / side)
        return max(32, int(resolution.width * scale)), max(32, int(resolution.height * scale))

    def render_next(self):
        """Renders one waiting channel, then the next on a later turn of the event loop: painting never waits long."""
        if not self.dirty or not self.connected:
            return
        # Never during a stroke, nor while Painter computes: a render then would make the stroke stutter.
        if self.pressed or not self.project_ready():
            self.render_timer.start(150)
            return
        (name, channel), _ = self.dirty.popitem(last=False)
        try:
            width, height = self.preview_resolution(name)
            self.revision += 1
            folder = os.path.join(link_folder(), "live", str(os.getpid()))
            os.makedirs(folder, exist_ok=True)
            stem = "%08x_%s" % (zlib.crc32(name.encode("utf-8")), channel)
            path = os.path.join(folder, "%s_%d.tga" % (stem, self.revision)).replace("\\", "/")
            started = time.perf_counter()
            # Dithering stays at its default: Painter 10 only accepts its own misspelling of "disabled".
            info = json.dumps({"padding": "Infinite", "resolution": [width, height], "bitDepth": 8})
            if channel == "normal":
                self.render_normal(name, path, width, height, info)
            else:
                substance_painter.js.evaluate("alg.mapexport.save(%s, %s, %s)" % (json.dumps([name, channel]), json.dumps(path), info))
            self.last_render = (channel, width, (time.perf_counter() - started) * 1000.0)
            self.send(TEXTURE, {"textureSet": name, "channel": channel, "path": path, "width": width, "height": height,
                                "revision": self.revision})
            # Only the newest few images of a channel stay: Unity reads each one as it arrives.
            older = sorted(glob.glob(os.path.join(folder, stem + "_*.tga")), key=os.path.getmtime)[:-3]
            for stale in older:
                try:
                    os.remove(stale)
                except OSError:
                    pass
            self.changed.emit()
        except Exception as exception:
            log("Render of %s %s failed: %s" % (name, channel, exception))
        if self.dirty:
            self.render_timer.start(0)

    def render_normal(self, name, path, width, height, info):
        """
        The normal saving exports (the painted normal and height over the baked normal), OpenGL: Painter's converted map
        when it names one (the JavaScript API's identifiers differ between versions), else exported alone at this size.
        """
        if self.normal_map is None:
            try:
                names = substance_painter.js.evaluate("alg.mapexport.convertedMapIdentifiers()") or []
            except Exception:
                names = []
            log("Converted maps: %s" % ", ".join(str(n) for n in names))
            normals = [n for n in names if "normal" in str(n).lower() and "directx" not in str(n).lower()]
            self.normal_map = normals[0] if normals else ""
        if self.normal_map:
            try:
                substance_painter.js.evaluate("alg.mapexport.saveConvertedMap(%s, %s, %s, %s)" % (
                    json.dumps([name]), json.dumps(self.normal_map), json.dumps(path), info))
                return
            except Exception as exception:
                log("Converted map %s refused (%s): exporting the normal instead." % (self.normal_map, exception))
                self.normal_map = ""
        folder = os.path.dirname(path)
        stem = os.path.splitext(os.path.basename(path))[0]
        config = {
            "exportPath": folder, "exportShaderParams": False, "defaultExportPreset": "orbiters_live_normal",
            "exportPresets": [{"name": "orbiters_live_normal", "maps": [{"fileName": stem, "channels": [
                {"destChannel": c, "srcChannel": c, "srcMapType": "virtualMap", "srcMapName": "Normal_OpenGL"} for c in "RGB"]}]}],
            "exportList": [{"rootPath": name}],
            "exportParameters": [{"parameters": {"fileFormat": "tga", "bitDepth": "8", "dithering": False, "paddingAlgorithm": "infinite",
                                                 "sizeLog2": [max(1, width.bit_length() - 1), max(1, height.bit_length() - 1)]}}]}
        result = substance_painter.export.export_project_textures(config)
        written = [f for files in result.textures.values() for f in files]
        if not written:
            raise RuntimeError("the normal export wrote nothing (%s)" % result.message)
        if os.path.normcase(os.path.abspath(written[0])) != os.path.normcase(os.path.abspath(path)):
            os.replace(written[0], path)

    # ---- Updates ----

    def check_update(self):
        """
        A newer plugin installed by My Avatar (its file changed): reloads itself, the project and link kept. A file that
        doesn't compile (still being written) waits for the next change.
        """
        try:
            changed = os.path.getmtime(__file__)
        except OSError:
            return
        if changed <= self.loaded_at:
            return
        self.loaded_at = changed
        try:
            with open(__file__, "r", encoding="utf-8") as handle:
                compile(handle.read(), __file__, "exec")
        except (OSError, SyntaxError, ValueError) as exception:
            log("The plugin's new file can't be loaded (%s): waiting for the next one." % exception)
            return
        log("A new version of the plugin was installed: reloading it.")
        module = sys.modules.get(__name__)
        if module is not None:
            QtCore.QTimer.singleShot(0, lambda: substance_painter_plugins.reload_plugin(module))

    # ---- The avatar's mesh ----

    def reload_mesh(self, path, attempts=240):
        """
        Puts the avatar's new mesh (an OBJ Unity wrote, posed as the project's) in the project, the strokes kept where they
        are on the surface. Waits for Painter to be free and for a stroke under way to end, half a second at a time.
        """
        self.mesh_path = path
        if not path or not os.path.isfile(path):
            self.mesh_reloaded("the mesh file is missing: %s" % path)
            return

        def attempt():
            if self.pressed or not self.project_ready():
                if attempts > 0:
                    QtCore.QTimer.singleShot(500, lambda: self.reload_mesh(path, attempts - 1))
                else:
                    self.mesh_reloaded("Painter stayed busy")
                return
            self.set_message("Loading the avatar's new mesh from Unity…")
            settings = substance_painter.project.MeshReloadingSettings(import_cameras=False, preserve_strokes=True)
            try:
                substance_painter.project.reload_mesh(path, settings, self.on_mesh_reloaded)
            except Exception as exception:
                log("Mesh reload failed: " + traceback.format_exc())
                self.mesh_reloaded(str(exception))

        substance_painter.project.execute_when_not_busy(attempt)

    def on_mesh_reloaded(self, status):
        self.mesh_reloaded(None if status == substance_painter.project.ReloadMeshStatus.SUCCESS else "Painter could not load it")

    def mesh_reloaded(self, error):
        if error:
            log("The avatar's new mesh was not loaded: " + error)
            self.set_message("The avatar's new mesh could not be loaded: %s" % error, True)
        else:
            log("The avatar's new mesh is in the project.")
            self.set_message("The avatar's new mesh is in the project.")
            # The texture sets as the new mesh has them, on the next tick.
            self.structure_sent = False
        answer = {"error": error, "path": self.mesh_path}
        # Unity away meanwhile (its scripts reloading): it gets the answer once linked again.
        if not (self.connected and self.send(MESH_RELOADED, answer)):
            self.unsent_mesh_answer = answer

    # ---- Commits ----

    def on_saved(self, _event):
        if self.saving_ourselves or not self.connected:
            return
        self.commit_later(everything=False)

    def commit_later(self, everything, attempts=60):
        """
        Commits once Painter can export: after a save the project stays "locked" a moment after Painter stops being busy
        (its texture sets can't be read then), so it is tried again every half second for a while.
        """
        def attempt():
            try:
                substance_painter.textureset.all_texture_sets()
            except Exception as exception:
                if attempts > 0:
                    QtCore.QTimer.singleShot(500, lambda: self.commit_later(everything, attempts - 1))
                else:
                    self.commit_failed("Painter kept the project locked: %s" % exception)
                return
            try:
                self.commit(everything)
            except Exception as exception:
                log("Commit failed: " + traceback.format_exc())
                self.commit_failed(str(exception))

        substance_painter.project.execute_when_not_busy(attempt)

    def commit_failed(self, reason):
        log("The export for Unity failed: " + reason)
        self.set_message("The export for Unity failed: %s" % reason, True)
        # Unity stops waiting and says why.
        self.send(COMMIT, {"maps": [], "error": reason})

    def commit(self, everything):
        """Exports the maps the strokes changed (all mapped ones when asked) and hands them to Unity."""
        if not self.connected or not self.mapping:
            return
        wanted = {}
        stacks = {}
        for name, entry in self.mapping.items():
            try:
                stack = substance_painter.textureset.TextureSet.from_name(name).get_stack()
            except Exception:
                continue
            # Only maps the texture set has a channel for (an export of a missing channel fails the whole save).
            available = exportable(entry.get("maps", []), stack.has_channel)
            maps = [m for m in available if everything or (name, m) in self.touched]
            # Metallic and roughness go together: some shaders read them from one packed texture, others need both set.
            if "roughness" in maps or "metallic" in maps:
                maps += [m for m in ("roughness", "metallic") if m in available and m not in maps]
            if maps:
                wanted[name] = maps
                stacks[name] = stack
        if not wanted:
            return
        self.send(COMMITTING, {})
        self.set_message("Sending the textures to Unity…")
        folder = new_export_folder(link_folder())
        presets, export_list = [], []
        for name, maps in wanted.items():
            stack = stacks[name]
            preset = "orbiters_" + str(len(presets))
            presets.append({"name": preset, "maps": [self.map_definition(m, stack) for m in maps]})
            export_list.append({"rootPath": name, "exportPreset": preset})
        config = {
            "exportPath": folder.replace("\\", "/"), "exportShaderParams": False, "defaultExportPreset": presets[0]["name"],
            "exportPresets": presets, "exportList": export_list,
            "exportParameters": [{"parameters": {"fileFormat": "png", "bitDepth": "8", "dithering": True,
                                                 "paddingAlgorithm": "infinite"}}]}
        result = substance_painter.export.export_project_textures(config)
        sent = []
        for (name, _stack), files in result.textures.items():
            for path in files:
                stem = os.path.splitext(os.path.basename(path))[0]
                kind = stem.rsplit("__", 1)[-1] if "__" in stem else None
                if kind:
                    sent.append({"textureSet": name, "map": kind, "path": path.replace("\\", "/")})
        self.send(COMMIT, {"maps": sent})
        for name, maps in wanted.items():
            for m in maps:
                self.touched.discard((name, m))
        self.set_message("Sent %d textures to Unity." % len(sent))

    @staticmethod
    def map_definition(kind, stack):
        file_name = "$textureSet__" + kind
        if kind == "basecolor":
            maps = [{"destChannel": c, "srcChannel": c, "srcMapType": "documentMap", "srcMapName": "basecolor"} for c in "RGB"]
            if stack.has_channel(ChannelType.Opacity):
                maps.append({"destChannel": "A", "srcChannel": "L", "srcMapType": "documentMap", "srcMapName": "opacity"})
            return {"fileName": file_name, "channels": maps}
        if kind == "normal":
            return {"fileName": file_name, "channels": [{"destChannel": c, "srcChannel": c, "srcMapType": "virtualMap",
                                                         "srcMapName": "Normal_OpenGL"} for c in "RGB"]}
        if kind == "emissive":
            return {"fileName": file_name, "channels": [{"destChannel": c, "srcChannel": c, "srcMapType": "documentMap",
                                                         "srcMapName": "emissive"} for c in "RGB"]}
        source = {"ambientocclusion": "ambientOcclusion"}.get(kind, kind)
        return {"fileName": file_name, "channels": [{"destChannel": "L", "srcChannel": "L", "srcMapType": "documentMap",
                                                     "srcMapName": source}]}

    # ---- Projects created for Unity ----

    def check_jobs(self):
        if self.pending_job is not None:
            if time.time() - self.pending_since < PENDING_SECONDS:
                return
            # The project never opened: another job may run.
            log("The project for %s never opened." % self.pending_job.get("avatarName", "the avatar"))
            self.set_message("The project for %s didn't open in Painter: ask again from Unity." % self.pending_job.get("avatarName", "the avatar"), True)
            self.pending_job = None
        for path in sorted(glob.glob(os.path.join(link_folder(), "jobs", "*.json"))):
            try:
                with open(path, "r", encoding="utf-8") as handle:
                    job = json.load(handle)
            except (OSError, ValueError):
                continue
            if job.get("kind") not in (JOB_KIND, OPEN_KIND):
                continue
            if not pid_alive(job.get("unityPid")) or time.time() - os.path.getmtime(path) > 3600:
                self.remove(path)
                continue
            problem = job_problem(job, link_folder())
            if problem:
                log("Job %s refused: %s." % (os.path.basename(path), problem))
                self.set_message("Unity's request was refused: %s." % problem, True)
                self.remove(path)
                continue
            # Taken: another Painter (or a second look) won't run it again.
            try:
                os.replace(path, path + ".taken")
            except OSError:
                continue
            if job.get("kind") == OPEN_KIND:
                self.open_job(job, path + ".taken")
            else:
                self.start_job(job, path + ".taken")
            return

    @staticmethod
    def remove(path):
        try:
            os.remove(path)
        except OSError:
            pass

    def close_current(self, question):
        """Closes the open project once the user agrees (saving it first if they want); False when it stays open."""
        if not substance_painter.project.is_open():
            return True
        main = substance_painter.ui.get_main_window()
        answer = QtWidgets.QMessageBox.question(main, "My Avatar", question + "\n\nClose the current project?",
                                                QtWidgets.QMessageBox.Yes | QtWidgets.QMessageBox.No, QtWidgets.QMessageBox.Yes)
        if answer != QtWidgets.QMessageBox.Yes:
            return False
        if substance_painter.project.needs_saving():
            save = QtWidgets.QMessageBox.question(
                main, "My Avatar", "Save the current project first?",
                QtWidgets.QMessageBox.Save | QtWidgets.QMessageBox.Discard | QtWidgets.QMessageBox.Cancel, QtWidgets.QMessageBox.Save)
            if save == QtWidgets.QMessageBox.Cancel:
                return False
            if save == QtWidgets.QMessageBox.Save:
                if not substance_painter.project.file_path():
                    self.set_message("Save the current project, then ask again from Unity.", True)
                    return False
                substance_painter.project.save()
        self.disconnect()
        substance_painter.project.close()
        return True

    def open_job(self, job, path):
        """Opens the project an avatar was painted in (Unity's "Open the project in Painter")."""
        self.remove(path)
        substance_painter.ui.show_main_window()
        target = os.path.normcase(os.path.abspath(job.get("project") or ""))
        current = substance_painter.project.file_path() if substance_painter.project.is_open() else None
        if current and os.path.normcase(os.path.abspath(current)) == target:
            return
        if not os.path.isfile(target):
            self.set_message("The project for %s is gone: %s" % (job.get("avatarName", "the avatar"), job.get("project")), True)
            return
        if not self.close_current("Unity asks to open the project of %s." % job.get("avatarName", "the avatar")):
            return
        try:
            substance_painter.project.open(job["project"])
        except Exception as exception:
            self.set_message("The project could not be opened: %s" % exception, True)

    def start_job(self, job, path):
        substance_painter.ui.show_main_window()
        if not self.close_current("Unity asks for a new project to paint %s." % job.get("avatarName", "the avatar")):
            self.remove(path)
            return
        self.pending_job = job
        self.pending_since = time.time()
        self.remove(path)
        settings = substance_painter.project.Settings(
            normal_map_format=substance_painter.project.NormalMapFormat.OpenGL,
            tangent_space_mode=substance_painter.project.TangentSpace.PerVertex,
            default_texture_resolution=int(job.get("resolution") or 2048),
            import_cameras=False)
        self.set_message("Creating the project for %s…" % job.get("avatarName", "the avatar"))
        try:
            substance_painter.project.create(mesh_file_path=job["mesh"], settings=settings)
        except Exception as exception:
            self.pending_job = None
            self.set_message("The project could not be created: %s" % exception, True)

    def on_edition_entered(self, _event):
        self.interacted = False
        self.touched.clear()
        job = self.pending_job
        if job is None:
            self.load_link()
            self.changed.emit()
            return
        self.pending_job = None
        try:
            self.set_up(job)
        except Exception:
            log("Setting up the project failed: " + traceback.format_exc())
            self.set_message("The avatar's textures could not all be set up (see the log).", True)
        substance_painter.project.Metadata(METADATA).set("link", {
            "avatarId": job["avatarId"], "avatarName": job.get("avatarName"), "unityProject": job.get("unityProject"),
            "plugin": PLUGIN_VERSION})
        self.avatar_id = job["avatarId"]
        self.avatar_name = job.get("avatarName")
        self.unity_project = job.get("unityProject")
        self.paused = False
        path = job.get("project")
        if path:
            os.makedirs(os.path.dirname(path), exist_ok=True)
            # Linked once saved: Unity knows the project it made by its file.
            self.awaiting_save = time.time()

            def save():
                self.saving_ourselves = True
                try:
                    substance_painter.project.save_as(path, substance_painter.project.ProjectSaveMode.Full)
                except Exception as exception:
                    self.set_message("The project could not be saved to %s: %s" % (path, exception), True)
                finally:
                    self.saving_ourselves = False
                    self.awaiting_save = 0.0
                self.tick()

            substance_painter.project.execute_when_not_busy(save)

    def set_up(self, job):
        """The avatar's textures and values from Unity in a "From Unity" fill layer of each texture set."""
        Color = substance_painter.colormanagement.Color
        by_name = {ts.name().lower(): ts for ts in substance_painter.textureset.all_texture_sets()}
        with substance_painter.layerstack.ScopedModification("My Avatar textures"):
            for entry in job.get("textureSets", []):
                ts = by_name.get(entry["name"].lower())
                if ts is None:
                    continue
                size = int(entry.get("resolution") or 0)
                if size:
                    ts.set_resolution(substance_painter.textureset.Resolution(size, size))
                stack = ts.get_stack()
                for channel, fmt in ((ChannelType.Emissive, substance_painter.textureset.ChannelFormat.sRGB8),
                                     (ChannelType.Opacity, substance_painter.textureset.ChannelFormat.L8)):
                    key = "emissive" if channel == ChannelType.Emissive else "opacity"
                    if key in entry and not stack.has_channel(channel):
                        stack.add_channel(channel, fmt)
                if entry.get("normal"):
                    normal = substance_painter.resource.import_project_resource(entry["normal"], substance_painter.resource.Usage.TEXTURE)
                    try:
                        ts.set_mesh_map_resource(substance_painter.textureset.MeshMapUsage.Normal, normal.identifier())
                    except Exception as exception:
                        log("Normal map of %s not set: %s" % (ts.name(), exception))
                fill = substance_painter.layerstack.insert_fill(substance_painter.layerstack.InsertPosition.from_textureset_stack(stack))
                fill.set_name("From Unity")
                for key, channel in (("basecolor", ChannelType.BaseColor), ("roughness", ChannelType.Roughness),
                                     ("metallic", ChannelType.Metallic), ("emissive", ChannelType.Emissive),
                                     ("opacity", ChannelType.Opacity)):
                    value = entry.get(key)
                    if value is None or not stack.has_channel(channel):
                        continue
                    if isinstance(value, str):
                        resource = substance_painter.resource.import_project_resource(value, substance_painter.resource.Usage.TEXTURE)
                        fill.set_source(channel, resource.identifier())
                    else:
                        values = list(value) + [value[-1]] * (3 - len(value)) if isinstance(value, list) else [value] * 3
                        fill.set_source(channel, Color(float(values[0]), float(values[1]), float(values[2])))


class Panel(QtWidgets.QWidget):
    """The dock: which avatar the project is linked to, the link's state, and what to send."""

    def __init__(self, link):
        super().__init__()
        self.link = link
        self.setObjectName("orbiters_my_avatar_panel")
        self.setWindowTitle("My Avatar")
        layout = QtWidgets.QVBoxLayout(self)
        layout.setContentsMargins(10, 10, 10, 10)
        self.status = QtWidgets.QLabel()
        self.status.setWordWrap(True)
        layout.addWidget(self.status)
        row = QtWidgets.QHBoxLayout()
        self.avatars = QtWidgets.QComboBox()
        self.link_button = QtWidgets.QPushButton("Link")
        self.link_button.clicked.connect(self.on_link)
        row.addWidget(self.avatars, 1)
        row.addWidget(self.link_button)
        layout.addLayout(row)
        self.live = QtWidgets.QCheckBox("Show strokes live in Unity")
        self.live.setChecked(True)
        self.live.toggled.connect(self.on_live)
        layout.addWidget(self.live)
        self.send = QtWidgets.QPushButton("Apply every texture in Unity")
        self.send.setToolTip("Saving the project applies the textures you painted. This applies all of them now.")
        self.send.clicked.connect(lambda: self.link.commit_later(everything=True))
        layout.addWidget(self.send)
        self.detail = QtWidgets.QLabel()
        self.detail.setStyleSheet("color: gray;")
        self.detail.setWordWrap(True)
        layout.addWidget(self.detail)
        layout.addStretch(1)
        self.choices = []
        link.changed.connect(self.refresh)
        self.timer = QtCore.QTimer(self)
        self.timer.timeout.connect(self.refresh_avatars)
        self.timer.start(3000)
        self.refresh_avatars()
        self.refresh()

    def refresh_avatars(self):
        if self.link.avatar_id:
            return
        current = self.avatars.currentData()
        self.choices = []
        self.avatars.clear()
        for endpoint in read_endpoints():
            for avatar in endpoint.get("avatars", []):
                self.choices.append((endpoint, avatar))
                label = "%s · %s" % (avatar.get("name"), endpoint.get("projectName") or "Unity")
                self.avatars.addItem(label, avatar.get("id"))
        if current is not None:
            index = self.avatars.findData(current)
            if index >= 0:
                self.avatars.setCurrentIndex(index)
        self.refresh()

    def refresh(self):
        link = self.link
        linked = bool(link.avatar_id)
        self.avatars.setVisible(not linked)
        self.link_button.setText("Link again" if link.paused else "Unlink" if linked else "Link")
        self.link_button.setEnabled(linked or (self.avatars.count() > 0 and substance_painter.project.is_open()))
        if not substance_painter.project.is_open():
            text = "Open a project, or click Open in Substance in My Avatar (Unity)."
        elif not linked:
            text = "Link this project to an avatar open in Unity." if self.avatars.count() else \
                "No avatar in Unity: open a scene with My Avatar in Unity."
        else:
            text = link.message
        self.status.setText(text)
        self.status.setStyleSheet("color: #ff7070;" if link.error else "")
        self.send.setEnabled(link.connected and bool(link.mapping))
        mapped = len(link.mapping)
        detail = []
        if link.connected:
            detail.append("%d texture set%s on the avatar" % (mapped, "" if mapped == 1 else "s"))
        if link.last_render:
            channel, width, ms = link.last_render
            detail.append("last %s at %d px in %d ms" % (channel, width, ms))
        self.detail.setText(" · ".join(detail))

    def on_link(self):
        if self.link.paused:
            self.link.resume()
        elif self.link.avatar_id:
            self.link.unlink()
        else:
            index = self.avatars.currentIndex()
            if 0 <= index < len(self.choices):
                endpoint, avatar = self.choices[index]
                self.link.link_to(endpoint, avatar)
        self.refresh_avatars()
        self.refresh()

    def on_live(self, on):
        self.link.live_enabled = on
        if on:
            for name, entry in self.link.mapping.items():
                for channel in entry.get("channels", []):
                    self.link.dirty[(name, channel)] = True
            self.link.schedule_render(0)


class PressWatcher(QtCore.QObject):
    """Whether a mouse button or pen is down anywhere in Painter: renders wait for the stroke to end."""

    def __init__(self, link):
        super().__init__()
        self.link = link

    def eventFilter(self, _watched, event):
        kind = event.type()
        if kind in (QtCore.QEvent.MouseButtonPress, QtCore.QEvent.TabletPress):
            self.link.pressed = True
            self.link.interacted = True
        elif kind in (QtCore.QEvent.MouseButtonRelease, QtCore.QEvent.TabletRelease):
            self.link.pressed = False
            self.link.schedule_render(200)
        return False


_state = {}


def start_plugin():
    link = Link()
    panel = Panel(link)
    watcher = PressWatcher(link)
    QtWidgets.QApplication.instance().installEventFilter(watcher)
    dock = substance_painter.ui.add_dock_widget(panel)
    handlers = [
        (substance_painter.event.TextureStateEvent, link.on_texture_state),
        (substance_painter.event.ProjectSaved, link.on_saved),
        (substance_painter.event.ProjectEditionEntered, link.on_edition_entered),
        (substance_painter.event.ProjectAboutToClose, lambda _e: (link.disconnect(), link.load_link())),
        (substance_painter.event.LayerStacksModelDataChanged, lambda _e: link.send_structure()),
    ]
    for event, handler in handlers:
        substance_painter.event.DISPATCHER.connect_strong(event, handler)
    _state.update(link=link, panel=panel, dock=dock, watcher=watcher, handlers=handlers)
    if substance_painter.project.is_open():
        link.load_link()
    write_presence()
    log("Painter link %s started." % PLUGIN_VERSION)


def close_plugin():
    try:
        os.remove(presence_path())
    except OSError:
        pass
    link = _state.get("link")
    for event, handler in _state.get("handlers", []):
        try:
            substance_painter.event.DISPATCHER.disconnect(event, handler)
        except Exception:
            pass
    if _state.get("watcher") is not None:
        QtWidgets.QApplication.instance().removeEventFilter(_state["watcher"])
    if link is not None:
        link.tick_timer.stop()
        link.disconnect()
    if _state.get("panel") is not None:
        substance_painter.ui.delete_ui_element(_state["panel"])
    _state.clear()


if __name__ == "__main__":
    start_plugin()
