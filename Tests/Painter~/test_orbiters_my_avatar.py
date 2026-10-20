"""
Pure-Python tests of My Avatar's Painter plugin (Painter~/orbiters_my_avatar.py): Painter's and Qt's modules are
stand-ins, so nothing starts Painter or a Qt application. Run with: python -I Tests/Painter~/test_orbiters_my_avatar.py
"""
import hashlib
import hmac
import importlib.util
import json
import os
import shutil
import struct
import sys
import tempfile
import time
import types
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
PLUGIN = os.path.normpath(os.path.join(HERE, "..", "..", "Painter~", "orbiters_my_avatar.py"))


class Signal:
    def __init__(self, *_):
        self.handlers = []

    def connect(self, handler):
        self.handlers.append(handler)

    def emit(self, *args):
        for handler in self.handlers:
            handler(*args)


class Timer:
    single_shots = []

    def __init__(self, *_):
        self.timeout = Signal()
        self.started = []

    def setSingleShot(self, _):
        pass

    def start(self, milliseconds=0):
        self.started.append(milliseconds)

    def stop(self):
        pass

    @staticmethod
    def singleShot(milliseconds, handler):
        Timer.single_shots.append((milliseconds, handler))


class Anything:
    """A module member the tests never use: any attribute, any call."""

    def __init__(self, *_, **__):
        pass

    def __getattr__(self, _):
        return Anything()

    def __call__(self, *_, **__):
        return Anything()


def module(module_name, **members):
    made = types.ModuleType(module_name)
    made.__dict__.update(members)
    return made


class FakeSocket:
    def __init__(self):
        self.written = bytearray()
        self.aborted = False
        self.open = False

    def write(self, data):
        self.written.extend(data)

    def flush(self):
        pass

    def state(self):
        return "connected" if self.open and not self.aborted else None

    def abort(self):
        self.aborted = True

    def deleteLater(self):
        pass


def install_stubs():
    channel = types.SimpleNamespace(BaseColor="BaseColor", Opacity="Opacity", Normal="Normal", Height="Height",
                                    Roughness="Roughness", Metallic="Metallic", Emissive="Emissive", AO="AO")
    core = module("PySide6.QtCore", QObject=type("QObject", (), {"__init__": lambda self, *a: None}), Signal=Signal, QTimer=Timer,
                  QEvent=types.SimpleNamespace(MouseButtonPress=1, TabletPress=2, MouseButtonRelease=3, TabletRelease=4))
    network = module("PySide6.QtNetwork", QTcpSocket=Anything,
                     QAbstractSocket=types.SimpleNamespace(SocketState=types.SimpleNamespace(ConnectedState="connected")))
    widgets = module("PySide6.QtWidgets", QWidget=type("QWidget", (), {}), QApplication=Anything(), QMessageBox=Anything(),
                     QVBoxLayout=Anything, QHBoxLayout=Anything, QLabel=Anything, QComboBox=Anything, QPushButton=Anything,
                     QCheckBox=Anything)
    sys.modules["PySide6"] = module("PySide6", QtCore=core, QtNetwork=network, QtWidgets=widgets)
    sys.modules.update({"PySide6.QtCore": core, "PySide6.QtNetwork": network, "PySide6.QtWidgets": widgets})
    logged = []
    parts = {
        "application": module("substance_painter.application", version=lambda: "10.1.2"),
        "colormanagement": module("substance_painter.colormanagement"),
        "event": module("substance_painter.event", DISPATCHER=Anything()),
        "export": module("substance_painter.export"),
        "js": module("substance_painter.js"),
        "layerstack": module("substance_painter.layerstack"),
        "logging": module("substance_painter.logging", info=logged.append),
        "project": module("substance_painter.project", is_open=lambda: True, is_in_edition_state=lambda: True, is_busy=lambda: False,
                          name=lambda: "Test", file_path=lambda: "C:/Projects/Test.spp"),
        "resource": module("substance_painter.resource"),
        "textureset": module("substance_painter.textureset", ChannelType=channel, all_texture_sets=lambda: []),
        "ui": module("substance_painter.ui"),
    }
    painter = module("substance_painter", **parts)
    sys.modules["substance_painter"] = painter
    for name, part in parts.items():
        sys.modules["substance_painter." + name] = part
    sys.modules["substance_painter_plugins"] = module("substance_painter_plugins", reload_plugin=lambda m: None)
    return logged


def load(path=PLUGIN, name="orbiters_my_avatar_under_test"):
    spec = importlib.util.spec_from_file_location(name, path)
    plugin = importlib.util.module_from_spec(spec)
    sys.modules[name] = plugin
    spec.loader.exec_module(plugin)
    return plugin


LOGGED = install_stubs()
plugin = load()


def frames(data):
    """The frames written to a socket: (type, payload)."""
    found = []
    while len(data) >= plugin.HEADER.size:
        magic, version, frame_type, token_length, payload_length = plugin.HEADER.unpack_from(data)
        size = plugin.HEADER.size + token_length + payload_length
        payload = bytes(data[plugin.HEADER.size + token_length:size])
        found.append((frame_type, json.loads(payload.decode("utf-8")) if payload else None))
        data = data[size:]
    return found


class Sets:
    """Painter's texture sets as the plugin reads them."""

    def __init__(self, names):
        self.names = names

    def __call__(self):
        sets = []
        for name in self.names:
            stack = types.SimpleNamespace(all_channels=lambda: {"BaseColor": 1, "Roughness": 1})
            sets.append(types.SimpleNamespace(name=lambda n=name: n, get_resolution=lambda: types.SimpleNamespace(width=1024, height=1024),
                                              get_stack=lambda s=stack: s))
        return sets


class EndpointTests(unittest.TestCase):
    def test_the_editor_of_the_linked_project_is_chosen_first(self):
        copy = {"projectPath": "D:/Copy of Avatar", "avatars": [{"id": "A"}]}
        own = {"projectPath": "D:/Avatar", "avatars": [{"id": "A"}]}
        other = {"projectPath": "D:/Other", "avatars": [{"id": "B"}]}
        self.assertIs(plugin.choose_endpoint([copy, own, other], "A", "d:/avatar"), own)
        self.assertIs(plugin.choose_endpoint([copy, own], "A", None), copy, "Without a project, the first one listing it.")
        self.assertIs(plugin.choose_endpoint([copy, own], "A", "D:/Gone"), copy)
        self.assertIsNone(plugin.choose_endpoint([other], "A", "D:/Avatar"))

    def test_the_listener_of_a_port_is_read_from_the_tcp_table(self):
        def row(address, port, pid):
            # The port in network order in the low 16 bits.
            return struct.pack("<6I", 2, address, ((port & 0xFF) << 8) | (port >> 8), 0, 0, pid)

        loopback = 0x0100007F
        table = struct.pack("<I", 3) + row(loopback, 5000, 11) + row(0x0101A8C0, 4100, 22) + row(loopback, 4100, 33)
        self.assertEqual(plugin.owner_of(table, 4100), 33, "127.0.0.1 only, not another address.")
        self.assertEqual(plugin.owner_of(table, 5000), 11)
        self.assertIsNone(plugin.owner_of(table, 9))
        self.assertIsNone(plugin.owner_of(table[:30], 4100), "A table cut short reads what it holds.")
        self.assertIsNone(plugin.owner_of(b"", 4100))


class JobTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.mkdtemp(prefix="OrbitersPainterJobs-")

    def tearDown(self):
        shutil.rmtree(self.folder, ignore_errors=True)

    def job(self, **changes):
        inside = os.path.join(self.folder, "projects", "Avatar-1").replace("\\", "/")
        job = {"kind": plugin.JOB_KIND, "mesh": inside + "/Avatar.obj", "project": os.path.join(self.folder, "New.spp"),
               "textureSets": [{"name": "Skin", "basecolor": inside + "/Skin_basecolor.png", "metallic": 0.0, "roughness": inside + "/r.png"}]}
        job.update(changes)
        return job

    def test_projects_are_only_made_from_files_unity_wrote(self):
        self.assertIsNone(plugin.job_problem(self.job(), self.folder))
        self.assertIsNotNone(plugin.job_problem(self.job(mesh="C:/Windows/System32/x.obj"), self.folder))
        self.assertIsNotNone(plugin.job_problem(self.job(mesh=os.path.join(self.folder, "x.exe")), self.folder))
        outside = self.job()
        outside["textureSets"][0]["normal"] = "C:/Users/Someone/secret.png"
        self.assertIsNotNone(plugin.job_problem(outside, self.folder))
        self.assertIsNotNone(plugin.job_problem(self.job(project="C:/Users/Someone/notes.txt"), self.folder))
        # Never saved over a file.
        taken = os.path.join(self.folder, "Taken.spp")
        open(taken, "w").close()
        self.assertIsNotNone(plugin.job_problem(self.job(project=taken), self.folder))
        self.assertIsNotNone(plugin.job_problem(self.job(textureSets=[{"basecolor": [1, 1, 1]}]), self.folder))

    def test_only_painter_projects_are_opened(self):
        self.assertIsNone(plugin.job_problem({"kind": plugin.OPEN_KIND, "project": "D:/Painter/Avatar.spp"}, self.folder))
        self.assertIsNotNone(plugin.job_problem({"kind": plugin.OPEN_KIND, "project": "D:/Painter/run.bat"}, self.folder))
        self.assertIsNotNone(plugin.job_problem({"kind": plugin.OPEN_KIND}, self.folder))


class MappingTests(unittest.TestCase):
    def test_a_mapping_sent_again_renders_nothing(self):
        old = {"Skin": {"material": "Body#0", "channels": ["basecolor", "roughness"]}}
        self.assertEqual(plugin.mapping_changes(old, json.loads(json.dumps(old)), 1024, 1024), [])
        self.assertEqual(plugin.mapping_changes({}, old, 1024, 1024), [("Skin", "basecolor"), ("Skin", "roughness")])
        self.assertEqual(plugin.mapping_changes(old, old, 1024, 2048), [("Skin", "basecolor"), ("Skin", "roughness")], "Another size.")
        moved = {"Skin": {"material": "Body#1", "channels": ["basecolor", "roughness"]}}
        self.assertEqual(plugin.mapping_changes(old, moved, 1024, 1024), [("Skin", "basecolor"), ("Skin", "roughness")], "Another material.")
        more = {"Skin": {"material": "Body#0", "channels": ["basecolor", "roughness", "normal"]}}
        self.assertEqual(plugin.mapping_changes(old, more, 1024, 1024), [("Skin", "normal")], "A channel live for the first time.")

    def test_saves_within_a_second_export_to_folders_of_their_own(self):
        folder = tempfile.mkdtemp(prefix="OrbitersPainterExports-")
        try:
            first, second = plugin.new_export_folder(folder), plugin.new_export_folder(folder)
            self.assertNotEqual(first, second)
            for made in (first, second):
                self.assertTrue(os.path.isdir(made))
                # commit/<pid>/<folder>: where Unity looks for the save and what it deletes once applied.
                self.assertEqual(os.path.dirname(os.path.dirname(made)), os.path.join(folder, "commit"))
                self.assertEqual(os.path.basename(os.path.dirname(made)), str(os.getpid()))
        finally:
            shutil.rmtree(folder, ignore_errors=True)

    def test_saved_maps_need_their_channel(self):
        has = {"BaseColor", "Roughness"}.__contains__
        self.assertEqual(plugin.exportable(["basecolor", "normal", "roughness", "metallic", "emissive", "ambientocclusion"], has),
                         ["basecolor", "normal", "roughness"])
        self.assertEqual(plugin.exportable(["unknown"], has), [])


class LinkTests(unittest.TestCase):
    def setUp(self):
        self.link = plugin.Link()
        self.socket = FakeSocket()
        self.link.socket = self.socket
        self.link.endpoint = {"token": "secret", "port": 1}
        self.link.connected = True
        self.link.proved = True

    def test_the_same_texture_sets_go_once(self):
        sys.modules["substance_painter.textureset"].all_texture_sets = Sets(["Skin", "Clothes"])
        try:
            self.link.send_structure()
            self.link.send_structure()
            self.assertEqual([t for t, _ in frames(self.socket.written)], [plugin.STRUCTURE])
            sys.modules["substance_painter.textureset"].all_texture_sets = Sets(["Skin"])
            self.link.send_structure()
            sent = frames(self.socket.written)
            self.assertEqual(len(sent), 2, "A texture set gone is sent.")
            self.assertEqual([s["name"] for s in sent[1][1]["textureSets"]], ["Skin"])
        finally:
            sys.modules["substance_painter.textureset"].all_texture_sets = lambda: []

    def test_a_mapping_sent_again_queues_no_render(self):
        mapping = {"sets": [{"textureSet": "Skin", "material": "Body#0", "channels": ["basecolor"]}], "previewSize": 1024}
        self.link.on_frame(plugin.MAPPING, mapping)
        self.assertEqual(list(self.link.dirty), [("Skin", "basecolor")])
        self.link.dirty.clear()
        self.link.on_frame(plugin.MAPPING, json.loads(json.dumps(mapping)))
        self.assertEqual(list(self.link.dirty), [])

    def test_a_project_taken_over_stops_linking_until_asked(self):
        self.link.avatar_id = "A"
        self.link.on_frame(plugin.STATUS, {"message": "Other linked to Avatar in Unity.", "closing": True, "takenOver": True})
        self.assertTrue(self.link.paused)
        self.assertIsNone(self.link.socket, "Unity ended the link: it closes.")
        self.assertEqual(self.link.message, "Other linked to Avatar in Unity.", "The reason stays shown.")
        self.assertEqual([t for t, _ in frames(self.socket.written)], [], "No goodbye on a socket not connected.")

    def test_a_closing_status_from_unity_keeps_its_message(self):
        self.link.on_frame(plugin.STATUS, {"message": "The avatar's scene was closed in Unity.", "closing": True})
        self.assertFalse(self.link.paused, "It links again on its own.")
        self.assertIsNone(self.link.socket)
        self.assertEqual(self.link.message, "The avatar's scene was closed in Unity.")

    def test_a_mesh_answer_waits_for_unity_to_be_back(self):
        self.link.mesh_path = "C:/Link/meshes/Avatar-1.obj"
        self.link.connected = False
        self.link.proved = False
        self.link.socket = None
        self.link.mesh_reloaded(None)
        self.assertEqual(self.link.unsent_mesh_answer, {"error": None, "path": "C:/Link/meshes/Avatar-1.obj"})
        self.link.socket = self.socket
        self.link.on_connected()
        sent = frames(self.socket.written)
        self.assertEqual([t for t, _ in sent], [plugin.HELLO, plugin.STRUCTURE, plugin.MESH_RELOADED])
        self.assertEqual(sent[2][1]["path"], "C:/Link/meshes/Avatar-1.obj")
        self.assertIsNone(self.link.unsent_mesh_answer)

    def test_frames_after_one_that_ends_the_link_are_not_read(self):
        status = json.dumps({"message": "Bye", "closing": True}).encode("utf-8")
        mapping = json.dumps({"sets": [{"textureSet": "Skin", "material": "Body#0", "channels": ["basecolor"]}]}).encode("utf-8")
        data = plugin.HEADER.pack(plugin.MAGIC, plugin.VERSION, plugin.STATUS, 0, len(status)) + status + \
            plugin.HEADER.pack(plugin.MAGIC, plugin.VERSION, plugin.MAPPING, 0, len(mapping)) + mapping
        self.socket.readAll = lambda: data
        self.link.on_ready_read()
        self.assertEqual(self.link.mapping, {})
        self.assertEqual(list(self.link.dirty), [])


class ProofTests(unittest.TestCase):
    """Unity proves it holds the endpoint's token before Painter sends it (Toolkit's LiveLinkFormat.Proof)."""

    TOKEN = "f9K2-endpoint_token"

    def setUp(self):
        self.link = plugin.Link()
        self.socket = FakeSocket()
        self.socket.open = True
        self.link.socket = self.socket
        self.link.avatar_id = "A"

    def connect(self, proves=True, port=4123):
        self.link.endpoint = {"token": self.TOKEN, "port": port, "pid": 1, "proof": proves}
        self.link.on_connected()

    def answer(self, payload, token=b""):
        self.socket.readAll = lambda: plugin.HEADER.pack(plugin.MAGIC, plugin.VERSION, plugin.PROOF, len(token), len(payload)) + token + payload
        self.link.on_ready_read()

    def test_the_proof_is_the_toolkits_recipe(self):
        nonce = bytes(range(32))
        message = b"Orbiters live link proof" + struct.pack("<I", 0x4C50524F) + struct.pack("<H", 4123) + nonce
        self.assertEqual(plugin.proof(self.TOKEN, 4123, nonce), hmac.new(self.TOKEN.encode("utf-8"), message, hashlib.sha256).digest())
        self.assertNotEqual(plugin.proof(self.TOKEN, 4124, nonce), plugin.proof(self.TOKEN, 4123, nonce), "Bound to the port.")
        self.assertEqual(plugin.proof_request(nonce), struct.pack("<IBBHI", 0x4C50524F, 1, 0, 0, 32) + nonce)

    def test_nothing_leaves_before_unity_proves_it_holds_the_token(self):
        self.connect()
        sent = bytes(self.socket.written)
        self.assertEqual(len(sent), plugin.HEADER.size + plugin.PROOF_NONCE_BYTES, "Only the proof request.")
        self.assertEqual(plugin.HEADER.unpack_from(sent)[2:4], (plugin.PROOF, 0), "Type 0, no token.")
        self.assertNotIn(self.TOKEN.encode("utf-8"), sent)
        self.assertFalse(self.link.send(plugin.STRUCTURE, {}), "Nothing else goes meanwhile.")
        nonce = sent[plugin.HEADER.size:]
        self.answer(plugin.proof(self.TOKEN, 4123, nonce))
        self.assertTrue(self.link.proved)
        self.assertEqual([t for t, _ in frames(self.socket.written[len(sent):])][:1], [plugin.HELLO], "Linked once proved.")
        self.assertIn(self.TOKEN.encode("utf-8"), bytes(self.socket.written[len(sent):]))

    def test_a_wrong_proof_drops_the_connection_without_the_token(self):
        self.connect()
        nonce = bytes(self.socket.written[plugin.HEADER.size:])
        self.answer(plugin.proof("another token", 4123, nonce))
        self.assertIsNone(self.link.socket)
        self.assertTrue(self.socket.aborted)
        self.assertNotIn(self.TOKEN.encode("utf-8"), bytes(self.socket.written), "Not even in a goodbye.")
        self.assertTrue(self.link.error)

    def test_frames_before_the_proof_drop_the_connection(self):
        self.connect()
        mapping = json.dumps({"sets": []}).encode("utf-8")
        self.socket.readAll = lambda: plugin.HEADER.pack(plugin.MAGIC, plugin.VERSION, plugin.MAPPING, 0, len(mapping)) + mapping
        self.link.on_ready_read()
        self.assertIsNone(self.link.socket)
        self.assertNotIn(self.TOKEN.encode("utf-8"), bytes(self.socket.written))

    def test_a_proof_with_a_token_or_unasked_is_refused(self):
        self.connect()
        nonce = bytes(self.socket.written[plugin.HEADER.size:])
        self.answer(plugin.proof(self.TOKEN, 4123, nonce), token=b"x")
        self.assertIsNone(self.link.socket)
        self.setUp()
        self.connect(proves=False)
        self.socket.written.clear()
        self.answer(plugin.proof(self.TOKEN, 4123, bytes(32)))
        self.assertIsNone(self.link.socket, "No proof was asked for.")

    def test_a_frame_larger_than_the_link_allows_is_never_buffered(self):
        self.connect()
        sent = len(self.socket.written)
        # Whatever listens on the port announces a 4 GB payload and streams junk.
        self.socket.readAll = lambda: plugin.HEADER.pack(plugin.MAGIC, plugin.VERSION, plugin.PROOF, 0, 0xFFFFFFFF) + b"junk" * 64
        self.link.on_ready_read()
        self.assertIsNone(self.link.socket)
        self.assertTrue(self.socket.aborted)
        self.assertEqual(len(self.link.buffer), 0, "Nothing of it is kept.")
        self.assertEqual(len(self.socket.written), sent, "Nothing more was sent, the token least of all.")
        self.assertTrue(self.link.error)

    def test_a_proof_never_answered_drops_the_connection(self):
        self.connect()
        self.link.proof_started -= plugin.PROOF_SECONDS + 1
        self.link.check_proof()
        self.assertIsNone(self.link.socket)
        self.assertNotIn(self.TOKEN.encode("utf-8"), bytes(self.socket.written))

    def test_an_endpoint_without_proof_links_at_once(self):
        # A Unity whose Toolkit doesn't answer proof requests (its endpoint doesn't offer one).
        self.connect(proves=False)
        self.assertEqual([t for t, _ in frames(self.socket.written)][:1], [plugin.HELLO])

    def test_unlinking_while_unity_proves_says_no_goodbye(self):
        self.connect()
        self.link.disconnect()
        self.assertEqual(len(self.socket.written), plugin.HEADER.size + plugin.PROOF_NONCE_BYTES)


class UpdateTests(unittest.TestCase):
    def test_a_new_file_is_only_loaded_once_it_compiles(self):
        folder = tempfile.mkdtemp(prefix="OrbitersPainterPlugin-")
        try:
            copy = os.path.join(folder, "orbiters_my_avatar.py")
            shutil.copyfile(PLUGIN, copy)
            loaded = load(copy, "orbiters_my_avatar_copy")
            link = loaded.Link()
            Timer.single_shots.clear()
            with open(copy, "a", encoding="utf-8") as handle:
                handle.write("\ndef broken(:\n")
            os.utime(copy, (time.time() + 10, time.time() + 10))
            link.check_update()
            self.assertEqual(Timer.single_shots, [], "Half written: not loaded.")
            link.check_update()
            self.assertEqual(Timer.single_shots, [], "Not tried again until the file changes again.")
            shutil.copyfile(PLUGIN, copy)
            os.utime(copy, (time.time() + 20, time.time() + 20))
            link.check_update()
            self.assertEqual(len(Timer.single_shots), 1, "Whole again: reloaded.")
        finally:
            sys.modules.pop("orbiters_my_avatar_copy", None)
            shutil.rmtree(folder, ignore_errors=True)


if __name__ == "__main__":
    unittest.main(verbosity=2)
