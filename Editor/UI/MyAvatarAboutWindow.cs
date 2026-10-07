using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Orbiters.MyAvatar.Editor
{
    // About My Avatar: its version, then the projects it works with and their licenses and credits, read from
    // THIRD_PARTY_NOTICES.md next to this file.
    internal sealed class MyAvatarAboutWindow : OrbitersAboutWindow
    {
        private const string NoticesPath = "Packages/orbiters.myavatar/Editor/THIRD_PARTY_NOTICES.md";

        internal static void Open() => Open<MyAvatarAboutWindow>("About My Avatar");

        protected override Product Describe() => new Product
        {
            Name = "My Avatar",
            Author = "Enzo DUTRA / blackorbit",
            Version = PackageInfo.FindForAssembly(typeof(MyAvatarAboutWindow).Assembly)?.version,
            NoticesPath = NoticesPath,
            Caption = "My Avatar works with these projects and downloads them through VPM when you ask; only the thumbnail fonts ship with it. Click one to read its license.",
            Logo = () =>
            {
                var svg = AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/orbiters.myavatar/Editor/UI/MyAvatarLogo.svg.txt");
                if (svg == null) return null;
                var logo = new OrbitersVectorLogo(svg.text, new Vector2(309, 258));
                logo.style.width = 96; logo.style.height = 80;
                return logo;
            },
        };
    }
}
