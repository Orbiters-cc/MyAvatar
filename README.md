# My Avatar

Unity 2022.3 avatar texture setup, with an optional Orbiters account.

## Get started

1. Install **My Avatar** from the Orbiters VPM repository. Toolkit and XRay Gizmos
   are required dependencies; Unit Git is optional. The VRChat avatar SDK removes this editor-only
   component when uploading an avatar.
2. Select the avatar root in an open scene. Add **Orbiters > My Avatar** using
   Add Component or the GameObject menu.
3. Drop PNG, JPG or TGA files (or their containing folder) into the drop field.
   Folder import includes immediate files, not nested folders. Up to 48 files,
   256 MB per file and 1 GB per batch are supported. The field turns into a
   progress bar, then shows the result with **Undo** and **Save**; drop another
   set on it at any time.
4. Clear matches apply automatically. A texture that still needs a slot appears as
   one row below the field: **Use on …** accepts the suggestion, **Choose slot…**
   picks another. Either applies immediately.
5. **Undo** restores the materials from before the set (then reads **Redo**),
   including after a scene reload. Save your scene to retain the component data.
6. **Save** saves the current avatar scene and generated assets. When Unit Git is
   installed it also creates a local commit named `texture change` once the
   project's Git repository is initialized in Unit Git. The checkpoint includes the current scene (including its other pending
   changes), generated textures/materials and their metadata. Unrelated project
   paths and staged changes are excluded. Ignored checkpoint files must be
   unignored first. Nothing is pushed.

## Thumbnail

The thumbnail is shown on a copy of VRChat's in-game avatar card, measured on the game
at 1:1 and set between faint neighbouring cards: the wide crop VRChat shows, the
platform badges, the warning badge of a Poor or Very Poor performance rank (the SDK's
own rating for the build target), the avatar name on up to two lines and your VRChat
name once the SDK is signed in. The editor font stands in for VRChat's. While the
studio follows the live preview, drag, scroll and Shift-drag right on the card image
to frame the avatar. **Create thumbnail** opens the Orbiters photoshoot, the same one MCB uses for
custom base media, set up for one 4:3 VRChat thumbnail (1200×900); the card then
shows the live preview. Choose a pose, light, background and expression, frame the
avatar, then press **Capture**: the thumbnail is saved at once and the card flashes,
while the studio keeps showing the live camera so you can capture again. **Browse**
uses an existing image instead and closes the studio. The image is saved as
`Assets/Orbiters/MyAvatar/Thumbnails/<avatar> <id>/<avatar> thumbnail.png`; a new
capture replaces it.

Whenever the VRChat SDK builder shows this avatar, My Avatar fills in this thumbnail
through the SDK's own thumbnail selection, exactly as if you had chosen the file with
**Select Image**. **Open in VRChat SDK** opens the panel on this avatar. The SDK then
treats it as a pending change: review it, and upload or discard as usual. A thumbnail
you choose in the SDK afterwards is left alone. Nothing is uploaded automatically.

## Posing

Three switches for posing the avatar in the Scene view (hover one for a short
explanation):

- **Bones** draws the avatar's bones with XRay Gizmos; click one to select it, then
  rotate it with the Rotate tool.
- **Symmetry** mirrors each rotation or move of a left or right bone onto its partner.
  It is a mode: it stays on while nothing can be mirrored and starts mirroring as soon
  as a bone or avatar is selected. Bones under an unevenly scaled parent are skipped.
- **Clothing** keeps clothing and accessories that are not merged yet (their own
  armature, merged at build by VRCFury Armature Link or similar) in the avatar's pose.
  Each clothing bone keeps its rest offset from the matching avatar bone, found by
  name, humanoid role or a contained avatar bone name. The list below shows each
  accessory and how many of its bones matched; click one to select it.

Pose edits stay regular Unity edits: Undo reverts the avatar and the mirrored or
following bones together.

## Hair, tail & toes

Hair, tail and toe PhysBones are recognised by name (`Hair_Front`, `Ponytail`,
`Tail1`, `Toe_L`, toe beans...), from the bone they start at or the object holding
them. Each part gets its own card, flowing into as many columns as the Inspector is
wide:

- **Grab** and **Pose**: Nobody, Only me or Everyone icon buttons, written to the PhysBones'
  grab and pose permissions. VRChat has no friends-only setting. Posing is limited to
  who can grab.
- **Stretch**: a slider for how much longer the chain gets when pulled (PhysBone Max Stretch), up
  to three times its length.

A choice applies to every PhysBone of the part and can be undone. Bones of the
avatar's own armature named like a part but driven by no PhysBone are offered under
**Add physics**, which adds a PhysBone per chain under `PhysBones/<Part>` on the
avatar, with settings suited to the part and the part's current permissions.

## Parameters

How many of VRChat's 256 bits of synced parameters the avatar will use once VRCFury
has built it, estimated without building: the avatar's own expression parameters,
what VRCFury toggles, sliders and full controllers add, and what is left. The count
updates as the hierarchy changes. **Compress parameters** adds or removes VRCFury's
Parameter Compressor on the avatar and shows how many bits it saves or would save.

## Toolbar

The bottom toolbar opens **Settings** (the Orbiters server: production by default, or a
local development server, each with its own login) and **Blendshape Links**, which
still ships with MCB and is available when MCB is installed.

## What changes

Existing project textures are reused without copying or reimporting. For external
files, a persistent cache keyed by source path, size and modification time checks
only the files you dropped; unchanged files reuse their cached assets. New files are
copied in parallel, without content hashing, to one folder per drop under
`Assets/Orbiters/MyAvatar/Textures/`. No project-wide texture search or comparison
runs. A normal map with an incompatible importer gets a separate correctly configured
copy; the original asset's settings stay unchanged.

File discovery and copying run in the background. Unity's asset import and material
APIs still run on its main thread. Importer settings are written before the first
import, so each new texture is imported exactly once, in one batch. There is no
per-image forced synchronous import, per-image SaveAndReimport, or whole-project Refresh.

The tool creates material copies under `Assets/Orbiters/MyAvatar/<batch-id>/`
and assigns them only to renderers under this component.
Original materials and external images are preserved. New normal maps get Unity's
normal-map importer; new named mask maps use linear color. Standard shader normal,
metallic, specular and emission keywords are enabled where appropriate.

Only actual visible 2D texture properties on the current shader are considered,
including locked/optimized shaders such as Poiyomi's `Hidden/Locked/...` shaders.
A locked shader only keeps the features that were enabled when it was locked: if a
texture set member needs a slot the material lacks (for example emission), the
Inspector says so; enable the feature (unlocking if needed) and drop again.
Custom shaders can require their own feature toggles. The tool does not convert
roughness to smoothness, repack texture channels, change UVs or switch shaders.

Undo becomes **Redo** after restoring the previous materials. Redo
restores the saved assignments immediately without importing or requesting AI.
Undo preserves generated files so other references remain valid. It refuses to
replace renderer assignments edited since the apply. Each drop is one logical
operation: later AI matches and slots you choose reuse that batch's
materials and keep its snapshot, so Undo returns to the state before the drop.
A new drop replaces the component's previous persistent Undo snapshot; Unity's
regular Undo is also recorded, with AI matches as their own step. Undo after a Git
checkpoint creates a new local change, not a history rewrite. Existing imported files also remain after cancellation.

## Optional account and AI

Connecting an account is optional; local matching always works. **Login with
Discord** or **Login with Telegram** opens your browser on that login; Orbiters then
asks you to confirm the connection once, showing the same four-letter code as Unity,
and My Avatar is connected. The link works once and expires after ten minutes. The
account is shared with the other Orbiters tools; logging out affects all of them.

The robot in the corner of the drop field shows whether AI help is available: crossed
out while you are not connected or AI is off. Once connected, click it to turn AI help
on or off; this is the same setting as on your Orbiters account page.

Local matches are applied immediately. When textures remain unmatched, an account
is connected and AI help is on, My Avatar
then asks the Orbiters backend in the background. It sends texture filenames,
dimensions, pixel statistics measured locally in Unity (grayscale, normal-color,
black and bright coverage, mean brightness), current material/shader slot information
and the provisional local matches. No image data and no computer paths are uploaded.
The backend uses the configured AI provider and honors the account's AI preference;
reasoning can be switched per feature in the website's AI administration. Source
context and model output are excluded from Orbiters AI history; usage and status are
retained. Provider processing follows that provider's configured data policy.

Responses use validated texture/slot IDs; low-confidence and incompatible
assignments are ignored. Confident answers fill unmatched textures or correct a
local match. An answer is discarded if you drop again, edit a slot, or use Undo/Redo
while it is pending. Choices remembered from an earlier apply are never overridden.
On a network or provider failure the Inspector shows the reason and keeps the local matches.

Local matching works with any naming convention and avatar layout:

- Slots you chose by hand for a file on this avatar are reused (stored in
  `Library/OrbitersMyAvatar/slot-choices.json`). Automatic matches are not stored,
  so a wrong guess never becomes permanent.
- Filenames are split into words (camelCase, separators, digits) and compared with
  the name of the texture currently in each slot, the material name, mesh names
  (for meshes with one or two materials) and the material's other textures. Role
  words (`BaseColor`, `Normal`, `_N`, `Emissive`...) set the slot type and are not
  used as names. A word shared by many materials, like a base or author name, counts
  little; words shared by most dropped files, like an export prefix, are ignored.
  Plurals, prefixes (`BodyMatt` ~ `Body`) and a few avatar part synonyms (hair and
  feathers, eye and iris, body and skin) are recognised.
- Primary slots are preferred over detail, matcap, rim and other effect layers;
  particle, trail and line materials are ranked last.
- A match needs clear evidence and a margin over the next candidate. Files of one
  set follow the material their siblings matched. A new texture also replaces the
  old one in the same kind of slot on other materials that used it.
- When two colour images collide on one slot and exactly one is mostly black with
  bright details, it goes to the material's emission slot.

Unresolved cases retain a suggested target and offer **Use on …**. Only those
go to AI, with the free primary slots of avatar meshes.

## Development and releases

Repository: https://github.com/Orbiters-cc/MyAvatar

The package version starts at `0.0.1`. `Build Release` validates and publishes a
package ZIP plus `package.json`. Set the repository variable `PACKAGE_NAME` to
`MyAvatar`. Push a changed, unused version to `master` to release automatically;
dispatch the workflow for the first release. The Orbiters release webhook keeps
the canonical VPM listing current.

Assemblies: `Orbiters.MyAvatar` contains the persistent component;
`Orbiters.MyAvatar.Editor` contains import, matching, changes and Inspector UI.
Toolkit owns account storage, browser login, API roots/transport, shared account
controls, animated glow rendering, SVG logo drawing, the photoshoot panel, the
shared controls (segmented control, scrub dial, icons, switch, support footer),
symmetry and accessory posing, the parameter estimate and hair/tail/toe PhysBone
editing. XRay Gizmos draws and picks bones. Unit Git, when installed, owns Git
execution and scoped index handling; the `MYAVATAR_UNITGIT` version define enables
the commit step. My Avatar does not depend on MCB.
