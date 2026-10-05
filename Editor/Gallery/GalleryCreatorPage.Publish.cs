using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.Net;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    internal sealed partial class GalleryCreatorPage
    {
        // ---- 4. Publish ----

        private void PublishStep(VisualElement step)
        {
            var rights = new Toggle { text = "I own the rights to everything this version distributes, or I have permission to share it.", value = draft.rights };
            rights.AddToClassList("creator-rights");
            rights.RegisterValueChangedCallback(evt => { draft.rights = evt.newValue; draft.Save(); publishButton?.SetEnabled(CanPublish()); });
            step.Add(rights);
            if (draft.newAsset || !draft.listed)
            {
                var listing = new Toggle { text = "Also publish its listing on Orbiters (it can stay private until you do).", value = draft.publishListing };
                listing.AddToClassList("creator-rights");
                listing.RegisterValueChangedCallback(evt => { draft.publishListing = evt.newValue; draft.Save(); });
                step.Add(listing);
            }
            publishButton = GalleryUI.Button(publishing ? "Publishing…" : "Publish " + draft.version, () => _ = PublishAsync(), "mcb-button--primary creator-publish");
            publishButton.SetEnabled(CanPublish());
            step.Add(publishButton);
            var message = GalleryUI.Text(draft.message, "creator-message", step);
            message.EnableInClassList("error", draft.messageError);
            message.style.display = string.IsNullOrEmpty(draft.message) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private Button publishButton;

        private bool CanPublish() => !publishing && draft.rights && GalleryPublisher.Missing(draft) == null;

        private async Task PublishAsync()
        {
            if (!CanPublish()) return;
            publishing = true;
            publishButton.SetEnabled(false);
            void Progress(string text) { publishButton.text = text; Message(text, false); }
            try
            {
                await GalleryPublisher.PublishAsync(draft, Progress, CancellationToken.None);
                publishing = false;
                back();
            }
            catch (Exception ex)
            {
                publishing = false;
                Message(ex.Message + " Publish again to continue: what was already sent is kept.", true);
                publishButton.text = "Publish " + draft.version;
                publishButton.SetEnabled(CanPublish());
            }
        }
    }
}
