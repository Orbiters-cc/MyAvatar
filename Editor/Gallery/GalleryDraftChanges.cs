using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    /// <summary>
    /// Changes to the creator's draft sent as JSON (the MCP tool's configure). Every key is read the way Json.NET fills the
    /// draft, whatever its case, so what only the user decides (through an approval or the window) and what Orbiters
    /// answered are always left out. Everything is checked before anything changes; pictures from the project are taken in
    /// last, and removed again when the change fails; pictures the draft no longer names go once it is saved.
    /// </summary>
    internal static class GalleryDraftChanges
    {
        // Fields the user decides through approval or the window only, and what Orbiters answered.
        private static readonly string[] Protected = { "rights", "assetId", "newAsset", "releaseId", "releaseAssetId", "idempotencyKey", "draftId", "picturesSent", "message", "messageError" };
        private static readonly string[] VariantProtected = { "report", "uploadedId", "uploadedRelease", "uploadedFingerprint", "testJob" };
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace };

        /// <summary>Applies <paramref name="json"/> (an object) to the saved draft, saves it and returns it.</summary>
        internal static GalleryCreatorDraft ApplyJson(string json)
        {
            JObject data;
            try { data = JObject.Parse(json ?? ""); }
            catch (JsonException ex) { throw new ArgumentException("The draft fields are not a JSON object: " + ex.Message); }
            return Apply(data);
        }

        internal static GalleryCreatorDraft Apply(JObject data)
        {
            data = (JObject)data.DeepClone();
            Canonical(data, typeof(GalleryCreatorDraft), "reset", "existingAssetId");
            bool reset = data.Value<bool?>("reset") == true, newAsset = data.Value<bool?>("newAsset") == true;
            int? existing = data["existingAssetId"] != null ? data.Value<int?>("existingAssetId") ?? 0 : (int?)null;
            data.Remove("reset"); data.Remove("existingAssetId");
            foreach (string key in Protected) data.Remove(key);
            if (data["variants"] is JArray variants)
                foreach (var variant in variants.OfType<JObject>()) Variant(variant);
            bool pictures = data["thumbnailPath"] != null || data["previewPaths"] != null || data["replacePreviews"] != null;
            // Every picture checked before anything is copied or changed.
            if (data.TryGetValue("thumbnailPath", out var thumbnail)) GalleryPictures.Check(PicturePath(thumbnail));
            var previews = data["previewPaths"];
            if (previews != null && previews.Type != JTokenType.Null)
            {
                if (!(previews is JArray list)) throw new ArgumentException("previewPaths is a list of picture paths.");
                foreach (var token in list) GalleryPictures.Check(PicturePath(token));
            }

            var draft = reset ? new GalleryCreatorDraft() : GalleryCreatorDraft.Load();
            var before = Pictures(GalleryCreatorDraft.Load());
            var taken = new List<string>();
            try
            {
                if (thumbnail != null) data["thumbnailPath"] = Take(thumbnail, taken);
                if (previews is JArray paths) data["previewPaths"] = new JArray(paths.Select(token => Take(token, taken)).Where(p => p != null).ToArray<object>());
                // Another asset (or a new one) drops the draft version and uploads that belonged to the previous one.
                if (existing.HasValue) draft.Target(existing.Value);
                else if (newAsset) draft.Target(0);
                JsonConvert.PopulateObject(data.ToString(), draft, Settings);
                foreach (var variant in draft.variants) variant.manifest.FixDefault();
                // New or replaced pictures go to Orbiters with the next publish.
                if (pictures) draft.picturesSent = false;
                draft.rights = false;
                draft.Save();
            }
            catch
            {
                foreach (string path in taken) GalleryPictures.Delete(path);
                throw;
            }
            // The draft's own pictures it doesn't name any more go (the window does the same when one is removed).
            var now = Pictures(draft);
            foreach (string path in before.Where(p => !now.Contains(p))) GalleryPictures.Delete(path);
            return draft;
        }

        private static void Variant(JObject variant)
        {
            Canonical(variant, typeof(GalleryVariantDraft), "prefabPaths");
            if (variant["prefabPaths"] is JArray paths)
            {
                variant["prefabGuids"] = new JArray(paths.Select(path =>
                {
                    string guid = AssetDatabase.AssetPathToGUID((string)path);
                    if (string.IsNullOrEmpty(guid) || AssetDatabase.LoadAssetAtPath<GameObject>((string)path) == null) throw new ArgumentException("Not a prefab in this project: " + path);
                    return guid;
                }));
                variant.Remove("prefabPaths");
                if (variant["source"] == null) variant["source"] = "prefab";
            }
            if (variant["packagePath"] != null && variant["source"] == null) variant["source"] = "package";
            if (variant["manifest"] is JObject manifest)
            {
                Canonical(manifest, typeof(GalleryManifest));
                // Setup prefabs are named by path; their GUID comes from the project.
                foreach (var setup in (manifest["setups"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    Canonical(setup, typeof(GallerySetup));
                    foreach (var prefab in (setup["prefabs"] as JArray ?? new JArray()).OfType<JObject>())
                    {
                        Canonical(prefab, typeof(GallerySetupPrefab));
                        string path = (string)prefab["path"];
                        if (string.IsNullOrEmpty(path)) continue;
                        string guid = AssetDatabase.AssetPathToGUID(path);
                        if (string.IsNullOrEmpty(guid)) throw new ArgumentException("Not an asset in this project: " + path);
                        prefab["guid"] = guid;
                        if (prefab["name"] == null) prefab["name"] = System.IO.Path.GetFileNameWithoutExtension(path);
                    }
                }
            }
            foreach (string key in VariantProtected) variant.Remove(key);
        }

        /// <summary>
        /// Spells each key of <paramref name="data"/> as the field of <paramref name="type"/> (or one of <paramref name="extra"/>)
        /// Json.NET would fill with it, ignoring case: checks by name then see every key. The same field twice is refused.
        /// </summary>
        internal static void Canonical(JObject data, Type type, params string[] extra)
        {
            var names = type.GetFields(BindingFlags.Public | BindingFlags.Instance).Select(f => f.Name)
                .Concat(type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name)).Concat(extra).Distinct().ToList();
            foreach (var property in data.Properties().ToList())
            {
                string name = names.FirstOrDefault(n => string.Equals(n, property.Name, StringComparison.OrdinalIgnoreCase));
                if (name == null || name == property.Name) continue;
                if (data.Property(name) != null) throw new ArgumentException("“" + property.Name + "” and “" + name + "” are the same field: give it once.");
                property.Replace(new JProperty(name, property.Value));
            }
        }

        private static string PicturePath(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type != JTokenType.String) throw new ArgumentException("Picture paths are strings.");
            return (string)token;
        }

        // A picture of the project comes in as a new one of the tool's (removed again if the change fails).
        private static string Take(JToken token, List<string> taken)
        {
            string given = PicturePath(token);
            if (given == null) return null;
            string path = GalleryPictures.Adopt(given);
            if (path != null && !GalleryPictures.Owned(given)) taken.Add(path);
            return path;
        }

        private static HashSet<string> Pictures(GalleryCreatorDraft draft) =>
            new HashSet<string>(draft.previewPaths.Append(draft.thumbnailPath).Where(p => !string.IsNullOrEmpty(p)), StringComparer.OrdinalIgnoreCase);
    }
}
