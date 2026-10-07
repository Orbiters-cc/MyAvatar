using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor
{
    // Stores the avatar's VRChat thumbnail as a PNG in a folder My Avatar manages for that avatar.
    internal static class AvatarThumbnail
    {
        internal static readonly Vector2Int Size = new Vector2Int(1200, 900); // VRChat's 4:3 thumbnail format.

        internal static string Folder(MyAvatar avatar)
        {
            string id = GlobalObjectId.GetGlobalObjectIdSlow(avatar).ToString();
            string hash = BitConverter.ToString(System.Security.Cryptography.MD5.Create().ComputeHash(System.Text.Encoding.UTF8.GetBytes(id))).Replace("-", "").Substring(0, 6).ToLowerInvariant();
            return "Assets/Orbiters/MyAvatar/Thumbnails/" + SafeName(avatar.gameObject.name) + " " + hash;
        }

        // Each capture is immutable so Undo/Redo can restore both the reference and its pixels. Ref sheets go here too.
        internal static Texture2D Save(MyAvatar avatar, Texture2D image, string kind = "thumbnail")
        {
            string folder = Folder(avatar);
            TextureImport.EnsureFolder(folder);
            string path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + SafeName(avatar.gameObject.name) + " " + kind + ".png");
            File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(Application.dataPath), path), image.EncodeToPNG());
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            if (AssetImporter.GetAtPath(path) is TextureImporter importer && (importer.mipmapEnabled || importer.npotScale != TextureImporterNPOTScale.None))
            {
                importer.mipmapEnabled = false; importer.npotScale = TextureImporterNPOTScale.None;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        internal static string FullPath(Texture2D texture)
        {
            string path = texture ? AssetDatabase.GetAssetPath(texture) : null;
            return string.IsNullOrEmpty(path) ? null : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Application.dataPath), path));
        }

        private static string SafeName(string name) => string.Concat((name ?? "Avatar").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
    }
}
