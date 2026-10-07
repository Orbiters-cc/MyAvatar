using System;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;
using VRC.Core;

namespace Orbiters.MyAvatar.Editor
{
    /// <summary>
    /// Says why Build &amp; Publish stopped (the blueprint ID belongs to someone else's avatar) and clears the ID in one
    /// click, so the avatar uploads as a new one. Opens once the VRChat SDK's own "Build Aborted" dialog is closed.
    /// </summary>
    internal sealed class BlueprintOwnerWindow : EditorWindow
    {
        private const string StyleSheetPath = "Packages/orbiters.myavatar/Editor/Blueprint/blueprint-owner-window.uss";
        private static readonly Vector2 Size = new Vector2(540f, 324f);

        private PipelineManager pipeline;
        private BlueprintOwnershipResult result;
        private string clearedId;

        internal static void ShowAfterSdkDialog(PipelineManager pipeline, BlueprintOwnershipResult result)
        {
            Debug.LogWarning("[My Avatar] Build & Publish stopped before the build: the blueprint ID " + result.AvatarId + " of " +
                             (pipeline != null ? pipeline.gameObject.name : "the avatar") + " belongs to " + Describe(result) +
                             ". VRChat would refuse the upload. Clear the ID to upload it as a new avatar.");
            // The SDK shows its modal dialog right after the callback; editor updates resume once it is closed.
            void Open()
            {
                EditorApplication.update -= Open;
                var window = CreateInstance<BlueprintOwnerWindow>();
                window.pipeline = pipeline;
                window.result = result;
                window.titleContent = new GUIContent("Blueprint ID");
                var main = EditorGUIUtility.GetMainWindowPosition();
                window.position = new Rect(main.center - Size / 2f, Size);
                window.minSize = window.maxSize = Size;
                window.ShowUtility();
            }

            EditorApplication.update += Open;
        }

        private static string Describe(BlueprintOwnershipResult result)
        {
            string avatar = string.IsNullOrEmpty(result.AvatarName) ? "an avatar" : "“" + result.AvatarName + "”";
            return avatar + " published by " + (string.IsNullOrEmpty(result.OwnerName) ? "another VRChat account" : result.OwnerName);
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;
            root.Clear();
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet != null)
            {
                root.styleSheets.Add(sheet);
            }

            root.AddToClassList("bp-owner");
            if (result == null)
            {
                Close();
                return;
            }

            if (string.IsNullOrEmpty(clearedId))
            {
                BuildQuestion(root);
            }
            else
            {
                BuildCleared(root);
            }

            (root.panel?.visualTree ?? root).RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Escape)
                {
                    Close();
                }
            }, TrickleDown.TrickleDown);
        }

        private void BuildQuestion(VisualElement root)
        {
            string avatarName = pipeline != null ? pipeline.gameObject.name : "This avatar";
            root.Add(Header("console.warnicon", "This blueprint ID isn't yours", avatarName, "bp-owner__badge--warning"));

            var card = new VisualElement();
            card.AddToClassList("bp-owner__card");
            // The account the SDK uploads with, else the one linked to Orbiters.
            string you = CurrentUserName();
            if (string.IsNullOrEmpty(you))
            {
                you = string.IsNullOrEmpty(result.LinkedName) ? "your VRChat account" : result.LinkedName;
            }

            card.Add(Text("The VRChat SDK stopped before building. This avatar carries the ID of " + Bold(AvatarLabel()) + ", published by " +
                          Bold(string.IsNullOrEmpty(result.OwnerName) ? "another VRChat account" : result.OwnerName) + ". VRChat only lets " + Bold(you) +
                          " update avatars it published, so the upload would have failed at the very end.", "bp-owner__message"));
            root.Add(card);

            var idRow = new VisualElement();
            idRow.AddToClassList("bp-owner__id");
            idRow.Add(Text("Blueprint ID", "bp-owner__id-label"));
            var id = Text(result.AvatarId, "bp-owner__id-value");
            id.selection.isSelectable = true;
            idRow.Add(id);
            var copy = new Button { text = "Copy" };
            copy.AddToClassList("bp-owner__chip-button");
            ButtonInteraction.RegisterImmediateClick(copy, () => EditorGUIUtility.systemCopyBuffer = result.AvatarId);
            idRow.Add(copy);
            root.Add(idRow);

            root.Add(Text("Clearing it uploads the avatar as a new one: the SDK asks for its name and thumbnail first.", "bp-owner__hint"));
            root.Add(Spacer());

            var buttons = new VisualElement();
            buttons.AddToClassList("bp-owner__buttons");
            var keep = new Button { text = "Keep it" };
            keep.AddToClassList("bp-owner__button");
            ButtonInteraction.RegisterImmediateClick(keep, Close);
            var clear = new Button { text = "Clear the blueprint ID" };
            clear.AddToClassList("bp-owner__button");
            clear.AddToClassList("bp-owner__button--primary");
            ButtonInteraction.RegisterImmediateClick(clear, Clear);
            buttons.Add(keep);
            buttons.Add(clear);
            root.Add(buttons);
            clear.Focus();
        }

        private void BuildCleared(VisualElement root)
        {
            root.Add(Header("TestPassed", "Blueprint ID cleared", pipeline != null ? pipeline.gameObject.name : string.Empty, "bp-owner__badge--done"));
            var card = new VisualElement();
            card.AddToClassList("bp-owner__card");
            card.AddToClassList("bp-owner__card--done");
            card.Add(Text("In the VRChat SDK panel, give the avatar a name and a thumbnail, then click " + Bold("Build & Publish") +
                          " again: it is uploaded as a new avatar, under your account.", "bp-owner__message"));
            root.Add(card);
            root.Add(Text("Saved with the scene. To put the old ID back, use Undo or the button below.", "bp-owner__hint"));
            root.Add(Spacer());

            var buttons = new VisualElement();
            buttons.AddToClassList("bp-owner__buttons");
            var restore = new Button { text = "Put the ID back" };
            restore.AddToClassList("bp-owner__button");
            ButtonInteraction.RegisterImmediateClick(restore, Restore);
            var panel = new Button { text = "Show the SDK panel" };
            panel.AddToClassList("bp-owner__button");
            panel.AddToClassList("bp-owner__button--primary");
            ButtonInteraction.RegisterImmediateClick(panel, () =>
            {
                EditorApplication.ExecuteMenuItem("VRChat SDK/Show Control Panel");
                Close();
            });
            buttons.Add(restore);
            buttons.Add(panel);
            root.Add(buttons);
        }

        private void Clear()
        {
            if (pipeline == null)
            {
                Close();
                return;
            }

            clearedId = pipeline.blueprintId;
            Set(string.Empty, "Clear the blueprint ID");
            CreateGUI();
        }

        private void Restore()
        {
            if (pipeline != null && !string.IsNullOrEmpty(clearedId))
            {
                Set(clearedId, "Put the blueprint ID back");
            }

            Close();
        }

        private void Set(string value, string undoName)
        {
            Undo.RecordObject(pipeline, undoName);
            pipeline.blueprintId = value;
            EditorUtility.SetDirty(pipeline);
            PrefabUtility.RecordPrefabInstancePropertyModifications(pipeline);
            if (pipeline.gameObject.scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(pipeline.gameObject.scene);
            }
        }

        private string AvatarLabel() => string.IsNullOrEmpty(result.AvatarName) ? "another avatar" : "“" + result.AvatarName + "”";

        private static string CurrentUserName()
        {
            try
            {
                return APIUser.CurrentUser?.displayName;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static VisualElement Header(string icon, string title, string subject, string badgeClass)
        {
            var header = new VisualElement();
            header.AddToClassList("bp-owner__header");
            var badge = new VisualElement();
            badge.AddToClassList("bp-owner__badge");
            badge.AddToClassList(badgeClass);
            badge.Add(new Image { image = EditorGUIUtility.IconContent(icon).image, scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore });
            header.Add(badge);
            var titles = new VisualElement();
            titles.AddToClassList("bp-owner__titles");
            titles.Add(Text(title, "bp-owner__title"));
            if (!string.IsNullOrEmpty(subject))
            {
                titles.Add(Text(subject, "bp-owner__subject"));
            }

            header.Add(titles);
            return header;
        }

        private static string Bold(string text) => "<b>" + (text ?? string.Empty).Replace("<", "‹").Replace(">", "›") + "</b>";

        private static Label Text(string text, string className)
        {
            var label = new Label(text) { enableRichText = true };
            label.AddToClassList(className);
            return label;
        }

        private static VisualElement Spacer()
        {
            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            return spacer;
        }
    }
}
