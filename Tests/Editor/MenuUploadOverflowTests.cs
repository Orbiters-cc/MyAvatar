using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.MyAvatar.Editor.Expressions;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;
using ControlType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType;

namespace Orbiters.MyAvatar.Editor.Tests
{
    // Found by the compatibility project (menus profile, 2026-11-02): lilycalInventory added an item to MasculineCanine's
    // root at upload, Modular Avatar split the full page and "Remove default menu" missed the item it moved.
    public sealed class MenuUploadOverflowTests
    {
        private readonly List<Object> owned = new List<Object>();

        [TearDown] public void TearDown()
        {
            foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
            owned.Clear();
        }

        private VRCExpressionsMenu Menu(params VRCExpressionsMenu.Control[] controls)
        {
            var menu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            owned.Add(menu);
            menu.controls = controls.ToList();
            return menu;
        }

        private static VRCExpressionsMenu.Control Item(string name) => new VRCExpressionsMenu.Control { name = name, type = ControlType.Toggle };
        private static VRCExpressionsMenu.Control Folder(string name, VRCExpressionsMenu menu) => new VRCExpressionsMenu.Control { name = name, type = ControlType.SubMenu, subMenu = menu };

        [Test]
        public void AnItemAnotherToolPushedIntoANewPageIsStillFound()
        {
            var page = Menu(Item("Mood"), Item("Glasses"));
            var clothes = Menu(Item("Hat"));
            var root = Menu(Item("A"), Folder("Clothes", clothes), Folder("Next", page));
            var mood = new List<MenuPaths.Step> { new MenuPaths.Step("Mood", 0, false) };
            var known = new HashSet<string> { "/Clothes" };

            Assert.IsNull(MenuPaths.Find(root, mood), "not where the plan put it");
            var found = MenuPaths.FindMoved(root, mood, known);
            Assert.IsNotNull(found);
            Assert.AreSame(page, found.Value.Menu);
            Assert.AreEqual("Mood", found.Value.Control.name);

            Assert.IsNull(MenuPaths.FindMoved(root, mood, new HashSet<string> { "/Clothes", "/Next" }), "a folder the plan knows isn't searched");
            Assert.IsNull(MenuPaths.FindMoved(root, new List<MenuPaths.Step> { new MenuPaths.Step("Scarf", 0, false) }, known), "an item that is nowhere stays missing");

            root.controls.Add(Folder("More", Menu(Item("Mood"))));
            Assert.IsNull(MenuPaths.FindMoved(root, mood, known), "two candidates: none is taken");
        }

        [Test]
        public void TheFoldersOfAPlannedMenuArePathsOfNames()
        {
            var hats = new MenuPage { Name = "Hats" };
            var clothes = new MenuPage { Name = "Clothes" };
            clothes.Items.Add(new MenuNode { Name = "Hats", Type = ControlType.SubMenu, Page = hats });
            var root = new MenuPage();
            root.Items.Add(new MenuNode { Name = "Clothes", Type = ControlType.SubMenu, Page = clothes });
            root.Items.Add(new MenuNode { Name = "Dance", Type = ControlType.Button });
            CollectionAssert.AreEquivalent(new[] { "/Clothes", "/Clothes/Hats" }, MenuPaths.Folders(root));
        }
    }
}
