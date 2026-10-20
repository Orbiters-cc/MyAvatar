using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Orbiters.Toolkit.Editor.Processes;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor.Painter
{
    /// <summary>
    /// Substance 3D Painter on this machine: where it is installed (Adobe's installer or Steam), whether it runs, and My Avatar's
    /// Painter plugin, installed in Painter's user Python folder so it starts with Painter.
    /// </summary>
    internal static class PainterApp
    {
        private const string ExecutableName = "Adobe Substance 3D Painter";
        private const string PluginName = "orbiters_my_avatar";
        private const string PluginSource = "Packages/orbiters.myavatar/Painter~/" + PluginName + ".py";
        private const double CacheSeconds = 60d;
        private static string executable;
        private static double checkedAt = -1;

        /// <summary>Painter's executable, or null when it isn't installed (checked once a minute at most).</summary>
        internal static string Executable
        {
            get
            {
                double now = EditorApplication.timeSinceStartup;
                if (checkedAt >= 0 && now - checkedAt < CacheSeconds) return executable;
                checkedAt = now;
                executable = Candidates().FirstOrDefault(File.Exists);
                return executable;
            }
        }

        internal static bool Installed => Executable != null;

        private static IEnumerable<string> Candidates()
        {
            if (Application.platform == RuntimePlatform.OSXEditor)
            {
                yield return "/Applications/Adobe Substance 3D Painter/Adobe Substance 3D Painter.app/Contents/MacOS/Adobe Substance 3D Painter";
                yield break;
            }
            foreach (string root in InstalledApps.ProgramRoots())
            {
                yield return Path.Combine(root, "Adobe", ExecutableName, ExecutableName + ".exe");
                yield return Path.Combine(root, "Allegorithmic", "Substance Painter", "Substance Painter.exe");
            }
            foreach (string folder in InstalledApps.SteamApps("Substance 3D Painter").OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase))
                yield return Path.Combine(folder, ExecutableName + ".exe");
        }

        private const double RunningSeconds = 2d;
        private static bool running;
        private static double runningAt = -1;

        /// <summary>Whether Painter is running (it then picks up project jobs on its own); looked up every 2 seconds at most.</summary>
        internal static bool Running
        {
            get
            {
                double now = EditorApplication.timeSinceStartup;
                if (runningAt >= 0 && now - runningAt < RunningSeconds) return running;
                runningAt = now;
                try
                {
                    var processes = Process.GetProcessesByName(ExecutableName);
                    running = processes.Length > 0;
                    foreach (var process in processes) process.Dispose();
                }
                catch (Exception) { running = false; }
                return running;
            }
        }

        /// <summary>
        /// Starts Painter, on <paramref name="project"/> when given (its plugin finds a waiting project job otherwise); false
        /// when it isn't installed.
        /// </summary>
        internal static bool Launch(string project = null)
        {
            string path = Executable;
            if (path == null) return false;
            var start = new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) };
            if (!string.IsNullOrEmpty(project)) start.Arguments = "\"" + project + "\"";
            Process.Start(start)?.Dispose();
            return true;
        }

        /// <summary>Brings Painter's window in front (restored when minimised); false when it isn't running.</summary>
        internal static bool Show() => AppWindows.BringToFront(ExecutableName);

        // The plugin version that opens existing projects for Unity.
        private static readonly Version Opens = new Version(1, 1, 0);

        /// <summary>
        /// The plugin version the running Painter loaded (its <c>painter-&lt;pid&gt;.json</c> in the link folder), or null: no
        /// Painter runs, it runs without the plugin, or with one older than these files.
        /// </summary>
        internal static Version RunningPlugin
        {
            get
            {
                foreach (string file in Directory.Exists(PainterLinkService.LinkFolder) ? Directory.GetFiles(PainterLinkService.LinkFolder, "painter-*.json") : new string[0])
                {
                    try
                    {
                        var presence = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(file));
                        int pid = (int)presence["pid"];
                        using (var process = Process.GetProcessById(pid))
                            if (process.HasExited || !process.ProcessName.StartsWith(ExecutableName, StringComparison.OrdinalIgnoreCase)) continue;
                        if (Version.TryParse((string)presence["plugin"], out var version)) return version;
                    }
                    // A Painter gone since (no such process), or a file being written.
                    catch (Exception) { }
                }
                return null;
            }
        }

        /// <summary>
        /// Opens the avatar's Painter project: Painter starts on it, or the running Painter's plugin opens it (asking before
        /// it closes the project it has open). Returns what happens, and whether it does.
        /// </summary>
        internal static (bool Opening, string Message) OpenProject(MyAvatar avatar)
        {
            string project = PainterLinkService.ProjectPath(avatar);
            string name = Path.GetFileName(project);
            if (string.IsNullOrEmpty(project) || !File.Exists(project)) return (false, "The project is gone: " + project + ". Open in Substance makes a new one.");
            if (!Running)
            {
                runningAt = -1;
                return Launch(project) ? (true, "Painter starts on " + name + "…") : (false, "Substance 3D Painter wasn't found.");
            }
            var plugin = RunningPlugin;
            if (plugin == null || plugin < Opens)
                return (false, "The Painter that is open runs an older My Avatar plugin: open " + name + " from its File menu, or restart Painter " +
                               "(it loads the new plugin) and click Open the project again.");
            var job = new System.Collections.Generic.Dictionary<string, object>
            {
                ["kind"] = PainterLinkProtocol.OpenKind, ["project"] = project.Replace('\\', '/'), ["avatarName"] = avatar.name,
                ["unityPid"] = Process.GetCurrentProcess().Id,
            };
            string path = Path.Combine(PainterLinkService.LinkFolder, "jobs", "open-" + Guid.NewGuid().ToString("N") + ".json");
            Orbiters.Toolkit.Editor.LiveLink.LiveLinkServer.WriteAtomically(path, Newtonsoft.Json.JsonConvert.SerializeObject(job));
            return (true, "Painter opens " + name + " (it asks before closing the project it has open)…");
        }

        /// <summary>Painter's user folder (Documents/Adobe/Adobe Substance 3D Painter), where it loads Python plugins from.</summary>
        internal static string UserFolder => Path.Combine(PainterLinkService.Documents, "Adobe", ExecutableName);

        /// <summary>The plugin's file in Painter's startup folder: Painter runs it whenever it starts.</summary>
        internal static string PluginPath => Path.Combine(UserFolder, "python", "startup", PluginName + ".py");

        /// <summary>Whether My Avatar's plugin is in Painter's startup folder (any version).</summary>
        internal static bool PluginInstalled => File.Exists(PluginPath);

        private static readonly Regex VersionLine = new Regex(@"^PLUGIN_VERSION\s*=\s*""([0-9]+(?:\.[0-9]+){1,3})""", RegexOptions.Multiline);
        private static (string Stamp, Version Version) installed, bundled;

        /// <summary>The version a plugin file declares (its <c>PLUGIN_VERSION</c>), or null; read again only when the file changes.</summary>
        internal static Version VersionOf(string path, ref (string Stamp, Version Version) cache)
        {
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists) return null;
                string stamp = file.Length + ":" + file.LastWriteTimeUtc.Ticks;
                if (cache.Stamp != stamp)
                {
                    var match = VersionLine.Match(File.ReadAllText(path));
                    cache = (stamp, match.Success && Version.TryParse(match.Groups[1].Value, out var version) ? version : null);
                }
                return cache.Version;
            }
            catch (Exception) { return null; }
        }

        /// <summary>The plugin version this package brings.</summary>
        internal static Version BundledPlugin => VersionOf(Path.GetFullPath(PluginSource), ref bundled);

        /// <summary>The plugin version in Painter's startup folder, or null.</summary>
        internal static Version InstalledPlugin => VersionOf(PluginPath, ref installed);

        /// <summary>
        /// Whether the plugin Painter runs is this package's or newer (another project's newer My Avatar may have installed a
        /// newer one: it stays).
        /// </summary>
        internal static bool PluginCurrent
        {
            get
            {
                var have = InstalledPlugin;
                var bring = BundledPlugin;
                return have != null && bring != null && have >= bring;
            }
        }

        /// <summary>
        /// Installs (or updates) the plugin, never over a newer one; false when there was nothing to do. Written whole (a
        /// running plugin never reloads a half-copied file) and dated now: a running Painter reloads it at once.
        /// </summary>
        internal static bool InstallPlugin()
        {
            string source = Path.GetFullPath(PluginSource);
            if (!File.Exists(source)) throw new FileNotFoundException("My Avatar's Painter plugin is missing from the package.", source);
            if (PluginCurrent) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(PluginPath));
            string temp = PluginPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(source, temp, true);
                File.SetLastWriteTimeUtc(temp, DateTime.UtcNow);
                if (!File.Exists(PluginPath)) File.Move(temp, PluginPath);
                else
                {
                    // A Documents folder some sync tool holds may refuse the swap: copied over then (the file is whole already).
                    try { File.Replace(temp, PluginPath, null); }
                    catch (IOException) { File.Copy(temp, PluginPath, true); }
                }
                File.SetLastWriteTimeUtc(PluginPath, DateTime.UtcNow);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (Exception) { }
            }
            return true;
        }
    }
}
