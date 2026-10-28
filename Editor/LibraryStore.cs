using System;
using System.IO;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor
{
    // Small JSON files under Library/OrbitersMyAvatar. Caches (Write) never block anything when they fail; records that
    // later steps rely on (Commit) say so.
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

        /// <summary>Writes a cache: a failure is ignored (the cache is only read again).</summary>
        internal static void Write<T>(string name, T value)
        {
            try { Commit(name, value); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>
        /// Writes a record that later steps rely on (the gallery's installation journal and ledger): replaced whole, and an
        /// <see cref="IOException"/> when it could not be saved, so the caller stops before changing the project.
        /// </summary>
        internal static void Commit<T>(string name, T value)
        {
            string path = Path.Combine(Folder, name), temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(temp, Newtonsoft.Json.JsonConvert.SerializeObject(value));
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                throw new IOException("Could not save " + name + " in Library (" + e.Message + ")", e);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
