using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor.FaceTracking
{
    /// <summary>The two iPhone apps VRCFaceTracking reads: their UDP ports and packets.</summary>
    internal static class FaceTrackingProtocols
    {
        internal const int IFacialMocapPort = 49983, LiveLinkPort = 11111;
        // ifacialmocap.com/for-developer: sent to the phone's port 49983, the phone then streams to this PC's port 49983.
        internal const string IFacialMocapHello = "iFacialMocap_sahuasouryya9218sauhuiayeta91555dy3719";
        // VRCFaceTracking's iFacialMocap module asks for the v2 format ("&" between a name and its value) the same way.
        internal const string IFacialMocapHelloV2 = IFacialMocapHello + "|sendDataVersion=v2";
        internal const int LiveLinkValues = 61;

        // "eyeBlink_L", "jawOpen": ARKit's names in lower camel case, sides as "_L" and "_R" except the jaw's and mouth's own
        // left and right.
        private static readonly Dictionary<string, ArKit> Names = Enum.GetValues(typeof(ArKit)).Cast<ArKit>().Where(s => s != ArKit.Count)
            .ToDictionary(IFacialMocapName, s => s, StringComparer.Ordinal);

        internal static string IFacialMocapName(ArKit shape)
        {
            string name = shape.ToString();
            bool sided = shape != ArKit.JawLeft && shape != ArKit.JawRight && shape != ArKit.MouthLeft && shape != ArKit.MouthRight;
            if (sided && name.EndsWith("Left", StringComparison.Ordinal)) name = name.Substring(0, name.Length - 4) + "_L";
            else if (sided && name.EndsWith("Right", StringComparison.Ordinal)) name = name.Substring(0, name.Length - 5) + "_R";
            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        /// <summary>
        /// An iFacialMocap frame: "mouthSmile_R-35|…|=head#rx,ry,rz,px,py,pz|rightEye#rx,ry,rz|leftEye#rx,ry,rz|", with
        /// "&amp;" instead of "-" in the v2 format. Weights are 0 to 100, angles degrees.
        /// </summary>
        internal static bool TryParseIFacialMocap(string text, FaceFrame frame)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('|') < 0) return false;
            frame.Clear();
            frame.App = FaceApp.IFacialMocap;
            int shapes = 0;
            foreach (string token in text.Split('|'))
            {
                if (token.Length == 0) continue;
                int hash = token.IndexOf('#');
                if (hash >= 0)
                {
                    var values = Floats(token.Substring(hash + 1));
                    if (values.Count < 3) continue;
                    var rotation = new Vector3(values[0], values[1], values[2]);
                    switch (token.Substring(0, hash))
                    {
                        case "=head": frame.Head = rotation; break;
                        case "rightEye": frame.RightEye = rotation; break;
                        case "leftEye": frame.LeftEye = rotation; break;
                    }
                    continue;
                }
                int separator = token.IndexOf('&');
                if (separator < 0) separator = token.LastIndexOf('-');
                if (separator <= 0 || !Names.TryGetValue(token.Substring(0, separator), out var shape)) continue;
                if (!float.TryParse(token.Substring(separator + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)) continue;
                frame.Shapes[(int)shape] = Mathf.Clamp01(value / 100f);
                shapes++;
            }
            return shapes > 0;
        }

        private static List<float> Floats(string text)
        {
            var values = new List<float>();
            foreach (string part in text.Split(','))
                if (float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)) values.Add(value);
            return values;
        }

        /// <summary>
        /// A Live Link Face frame (Unreal's AppleARKitLiveLinkSource, big-endian): version, device id and subject name
        /// (int32 length, UTF-8), frame number, sub-frame, rate numerator and denominator, then 61 floats: the 52 ARKit
        /// weights in <see cref="ArKit"/> order, the head's and each eye's yaw, pitch and roll (degrees / 180).
        /// VRCFaceTracking's module reads the last 244 bytes whatever comes before; so does this.
        /// </summary>
        internal static bool TryParseLiveLink(byte[] data, int length, FaceFrame frame, out string device)
        {
            device = null;
            if (data == null || length < LiveLinkValues * 4) return false;
            int start = length - LiveLinkValues * 4;
            var values = new float[LiveLinkValues];
            for (int i = 0; i < LiveLinkValues; i++)
            {
                float value = BigEndianFloat(data, start + i * 4);
                // Anything else on this port (a stray packet) is not a face.
                if (float.IsNaN(value) || float.IsInfinity(value) || Math.Abs(value) > 4f) return false;
                values[i] = value;
            }
            if (length >= 5)
            {
                int idLength = BigEndianInt(data, 1);
                if (idLength > 0 && idLength < 256 && 5 + idLength <= start) device = Encoding.UTF8.GetString(data, 5, idLength);
            }
            frame.Clear();
            frame.App = FaceApp.LiveLink;
            for (int i = 0; i < (int)ArKit.Count; i++) frame.Shapes[i] = Mathf.Clamp01(values[i]);
            frame.Head = new Vector3(values[53], values[52], values[54]);
            frame.LeftEye = new Vector3(values[56], values[55], values[57]);
            frame.RightEye = new Vector3(values[59], values[58], values[60]);
            return true;
        }

        /// <summary>A Live Link Face packet for <paramref name="frame"/>: tests, and the simulator's end-to-end check.</summary>
        internal static byte[] LiveLinkPacket(FaceFrame frame, string device = "00000000-0000-0000-0000-000000000000", string subject = "iPhone")
        {
            var bytes = new List<byte> { 6 };
            void Int(int value) { var b = BitConverter.GetBytes(value); if (BitConverter.IsLittleEndian) Array.Reverse(b); bytes.AddRange(b); }
            void Float(float value) { var b = BitConverter.GetBytes(value); if (BitConverter.IsLittleEndian) Array.Reverse(b); bytes.AddRange(b); }
            void Text(string value) { var b = Encoding.UTF8.GetBytes(value); Int(b.Length); bytes.AddRange(b); }
            Text(device); Text(subject);
            Int(0); Float(0f); Int(60); Int(1);
            bytes.Add(LiveLinkValues);
            for (int i = 0; i < (int)ArKit.Count; i++) Float(frame.Shapes[i]);
            Float(frame.Head.y); Float(frame.Head.x); Float(frame.Head.z);
            Float(frame.LeftEye.y); Float(frame.LeftEye.x); Float(frame.LeftEye.z);
            Float(frame.RightEye.y); Float(frame.RightEye.x); Float(frame.RightEye.z);
            return bytes.ToArray();
        }

        /// <summary>An iFacialMocap v2 frame for <paramref name="frame"/> (integers 0 to 100, degrees).</summary>
        internal static string IFacialMocapPacket(FaceFrame frame)
        {
            var text = new StringBuilder();
            for (int i = 0; i < (int)ArKit.Count; i++)
                text.Append(IFacialMocapName((ArKit)i)).Append('&').Append(Mathf.RoundToInt(frame.Shapes[i] * 100f).ToString(CultureInfo.InvariantCulture)).Append('|');
            string V(Vector3 v) => string.Join(",", new[] { v.x, v.y, v.z }.Select(x => x.ToString("0.###", CultureInfo.InvariantCulture)));
            text.Append("=head#").Append(V(frame.Head)).Append(",0,0,0|rightEye#").Append(V(frame.RightEye)).Append("|leftEye#").Append(V(frame.LeftEye)).Append('|');
            return text.ToString();
        }

        private static float BigEndianFloat(byte[] data, int at)
        {
            var b = new[] { data[at], data[at + 1], data[at + 2], data[at + 3] };
            if (BitConverter.IsLittleEndian) Array.Reverse(b);
            return BitConverter.ToSingle(b, 0);
        }

        private static int BigEndianInt(byte[] data, int at) => (data[at] << 24) | (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3];

        /// <summary>An adapter of a virtual machine or VPN, which a phone on the Wi-Fi cannot reach.</summary>
        internal static bool IsVirtual(string adapter) =>
            new[] { "virtual", "vethernet", "vmware", "virtualbox", "hyper-v", "wsl", "docker", "tailscale", "zerotier", "hamachi", "radmin", "vpn", "loopback" }
                .Any(v => (adapter ?? "").IndexOf(v, StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>This PC's addresses on the local network, the one the phone most likely reaches first.</summary>
        internal static List<(IPAddress address, string adapter)> LocalAddresses()
        {
            var found = new List<(IPAddress address, string adapter, int score)>();
            try
            {
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    var properties = adapter.GetIPProperties();
                    bool gateway = properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                    bool virtualAdapter = IsVirtual(adapter.Name + " " + adapter.Description);
                    foreach (var unicast in properties.UnicastAddresses)
                    {
                        var address = unicast.Address;
                        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) continue;
                        byte[] b = address.GetAddressBytes();
                        if (b[0] == 169 && b[1] == 254) continue;
                        bool privateRange = b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] < 32) || (b[0] == 192 && b[1] == 168);
                        int score = (gateway ? 4 : 0) + (privateRange ? 2 : 0) + (virtualAdapter ? -8 : 0) +
                                    (adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 || adapter.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 1 : 0);
                        found.Add((address, adapter.Name, score));
                    }
                }
            }
            catch (NetworkInformationException) { }
            return found.OrderByDescending(f => f.score).Select(f => (f.address, f.adapter)).ToList();
        }
    }

    /// <summary>
    /// Listens for both iPhone apps at once, like VRCFaceTracking's modules (UDP 49983 for iFacialMocap, 11111 for Live
    /// Link Face), on background threads, and keeps the latest frame. The app is recognised by the port its packets reach.
    /// iFacialMocap streams once asked: the hello goes to the phone's address until frames arrive.
    /// </summary>
    internal sealed class FaceTrackingReceiver : IDisposable
    {
        private sealed class Listener
        {
            public UdpClient Client;
            public Thread Thread;
            public string Error;
        }

        private const int SioUdpConnReset = -1744830452;
        private readonly object gate = new object();
        private readonly FaceFrame latest = new FaceFrame();
        // The last frames and when they arrived, newest at head.
        private readonly FaceFrame[] history = new FaceFrame[FaceTrackingPlayback.History];
        private readonly double[] times = new double[FaceTrackingPlayback.History];
        private int head = -1, count;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Queue<double> arrivals = new Queue<double>();
        private readonly HashSet<string> greeted = new HashSet<string>();
        private Listener iFacialMocap, liveLink;
        private volatile bool running;
        private double lastPacket = double.NegativeInfinity, lastIFacialMocap = double.NegativeInfinity, lastHello = double.NegativeInfinity;
        private IPAddress phone;
        private long frames;

        public FaceApp App { get; private set; }
        /// <summary>The device's name (Live Link Face) or address.</summary>
        public string Device { get; private set; }
        public long Frames { get { lock (gate) return frames; } }
        public double Now => clock.Elapsed.TotalSeconds;
        public double Started { get; private set; }
        /// <summary>Seconds since the last frame, or infinity.</summary>
        public double Age { get { lock (gate) return Now - lastPacket; } }
        public float Fps { get { lock (gate) { Trim(Now); return arrivals.Count; } } }
        public bool Running => running;

        public string Error(FaceApp app) => app == FaceApp.IFacialMocap ? iFacialMocap?.Error : app == FaceApp.LiveLink ? liveLink?.Error : null;

        public void Start()
        {
            if (running) return;
            running = true;
            Started = Now;
            iFacialMocap = Listen(FaceTrackingProtocols.IFacialMocapPort, FaceApp.IFacialMocap);
            liveLink = Listen(FaceTrackingProtocols.LiveLinkPort, FaceApp.LiveLink);
        }

        /// <summary>The phone iFacialMocap runs on, or null: the hello is sent there until its frames arrive.</summary>
        public void SetPhone(IPAddress address)
        {
            lock (gate) { phone = address; lastHello = double.NegativeInfinity; }
        }

        /// <summary>Main thread, every editor frame: asks the phone for frames while none arrive.</summary>
        public void Tick()
        {
            IPAddress target;
            lock (gate)
            {
                double now = Now;
                if (phone == null || now - lastIFacialMocap < 2.0 || now - lastHello < 1.5) return;
                lastHello = now;
                target = phone;
            }
            Hello(new IPEndPoint(target, FaceTrackingProtocols.IFacialMocapPort));
        }

        public bool TryRead(FaceFrame into)
        {
            lock (gate)
            {
                if (frames == 0) return false;
                latest.CopyTo(into);
                return true;
            }
        }

        /// <summary>
        /// The face as it was a moment ago, between the two frames around that moment: every frame the phone sends shows,
        /// and Wi-Fi bursts and gaps play out evenly, at whatever rate the editor draws. The moment trails the newest frame by
        /// about one and a half frames of the phone's rate.
        /// </summary>
        public bool TryReadSmoothed(FaceFrame into)
        {
            lock (gate)
            {
                if (count == 0) return false;
                double now = Now;
                Trim(now);
                double delay = arrivals.Count > 1 ? Math.Min(.1, Math.Max(.015, 1.5 / arrivals.Count)) : 0;
                FaceTrackingPlayback.Sample(history, times, head, count, now - delay, into);
                return true;
            }
        }

        private Listener Listen(int port, FaceApp app)
        {
            var listener = new Listener();
            try
            {
                listener.Client = new UdpClient(new IPEndPoint(IPAddress.Any, port)) { Client = { ReceiveTimeout = 500 } };
                // Windows reports an unreachable phone as a reset on the next receive: ignore it.
                try { listener.Client.Client.IOControl(SioUdpConnReset, new byte[] { 0 }, null); } catch (Exception) { }
            }
            catch (SocketException ex)
            {
                listener.Error = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? "Port " + port + " is already in use: close VRCFaceTracking (or the other app using it) while testing here."
                    : "Could not listen on port " + port + ": " + ex.Message;
                return listener;
            }
            listener.Thread = new Thread(() => Receive(listener, app)) { IsBackground = true, Name = "My Avatar face tracking " + port };
            listener.Thread.Start();
            return listener;
        }

        private void Receive(Listener listener, FaceApp app)
        {
            var frame = new FaceFrame();
            while (running)
            {
                IPEndPoint from = null;
                byte[] data;
                try { data = listener.Client.Receive(ref from); }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut || ex.SocketErrorCode == SocketError.ConnectionReset) { continue; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    if (running) listener.Error = ex.Message;
                    break;
                }
                string device = null;
                bool parsed = app == FaceApp.IFacialMocap
                    ? FaceTrackingProtocols.TryParseIFacialMocap(Encoding.ASCII.GetString(data), frame)
                    : FaceTrackingProtocols.TryParseLiveLink(data, data.Length, frame, out device);
                if (!parsed) continue;
                bool greet;
                lock (gate)
                {
                    frame.CopyTo(latest);
                    double now = Now;
                    head = (head + 1) % history.Length;
                    if (history[head] == null) history[head] = new FaceFrame();
                    frame.CopyTo(history[head]);
                    times[head] = now;
                    if (count < history.Length) count++;
                    lastPacket = now;
                    if (app == FaceApp.IFacialMocap) lastIFacialMocap = now;
                    App = app;
                    Device = string.IsNullOrEmpty(device) ? from.Address.ToString() : device + " (" + from.Address + ")";
                    frames++;
                    arrivals.Enqueue(now);
                    Trim(now);
                    greet = app == FaceApp.IFacialMocap && greeted.Add(from.Address.ToString());
                }
                // As the module does: once a phone streams, ask it for the v2 format.
                if (greet) Hello(new IPEndPoint(from.Address, FaceTrackingProtocols.IFacialMocapPort));
            }
        }

        private void Hello(IPEndPoint phoneEndPoint)
        {
            var client = iFacialMocap?.Client;
            if (client == null) return;
            var bytes = Encoding.UTF8.GetBytes(FaceTrackingProtocols.IFacialMocapHelloV2);
            try { client.Send(bytes, bytes.Length, phoneEndPoint); }
            catch (Exception ex) when (ex is SocketException || ex is ObjectDisposedException) { }
        }

        private void Trim(double now)
        {
            while (arrivals.Count > 0 && arrivals.Peek() < now - 1.0) arrivals.Dequeue();
        }

        public void Dispose()
        {
            running = false;
            foreach (var listener in new[] { iFacialMocap, liveLink })
            {
                if (listener == null) continue;
                try { listener.Client?.Close(); } catch (Exception) { }
                listener.Thread?.Join(1000);
            }
            iFacialMocap = liveLink = null;
        }
    }
    /// <summary>Playing frames back from a short history: the frame at a moment, between the two around it.</summary>
    internal static class FaceTrackingPlayback
    {
        internal const int History = 48;

        /// <summary>
        /// Writes into <paramref name="into"/> the face at <paramref name="time"/> from <paramref name="count"/> frames (a ring,
        /// newest at <paramref name="head"/>): the newest after it, the oldest before it, else the two around it blended.
        /// </summary>
        internal static void Sample(FaceFrame[] frames, double[] times, int head, int count, double time, FaceFrame into)
        {
            int size = frames.Length;
            if (count <= 0) return;
            if (time >= times[head] || count == 1) { frames[head].CopyTo(into); return; }
            for (int age = 0; age < count - 1; age++)
            {
                int newer = ((head - age) % size + size) % size, older = ((newer - 1) % size + size) % size;
                if (times[older] > time) continue;
                double span = times[newer] - times[older];
                float t = span <= 0 ? 1f : (float)((time - times[older]) / span);
                FaceFrame.Lerp(frames[older], frames[newer], t, into);
                return;
            }
            frames[((head - count + 1) % size + size) % size].CopyTo(into);
        }
    }
}
