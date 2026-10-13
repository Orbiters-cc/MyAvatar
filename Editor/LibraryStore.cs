using System;
using System.IO;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor
{
    // Small JSON files under Library/OrbitersMyAvatar. They are caches: failures never block applying textures.
    internal static class LibraryStore
    {
        internal static string Folder => Override ?? Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library", "OrbitersMyAvatar");

        /// <summary>
        /// Another folder for every cache while set (tests, for the length of one: they never write the user's). Every caller
        /// uses it meanwhile, background jobs the user started included.
        /// </summary>
        internal static string Override;

        internal static T Read<T>(string name) where T : new()
        {
            string path = Path.Combine(Folder, name);
            try { if (File.Exists(path)) return Newtonsoft.Json.JsonConvert.DeserializeObject<T>(File.ReadAllText(path)) ?? new T(); }
            catch (IOException) { }
            catch (Newtonsoft.Json.JsonException) { }
            return new T();
        }

        internal static void Write<T>(string name, T value)
        {
            string path = Path.Combine(Folder, name), temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(temp, Newtonsoft.Json.JsonConvert.SerializeObject(value));
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            catch (IOException) { }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
