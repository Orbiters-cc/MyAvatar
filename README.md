# My Avatar

## 0.9.3 — 2026-10-07

- **Blueprint ID check**: Build & Publish stops before the build when the avatar's blueprint ID belongs to an avatar
  another VRChat account published (often a creator's ID left in a prefab), instead of failing after the whole build
  and upload. A window explains it and clears the ID in one click, so the avatar uploads as a new one. Asked to the
  Orbiters server for signed-in members with a linked VRChat account; the avatar shown in the SDK panel is checked
  ahead of time. Build & Test is never stopped, and nothing is blocked when the answer isn't known.
- Requires the matching Orbiters backend (`GET /vrchat/avatars/:id/owner`).

## 0.9.2 — 2026-10-06

- Thumbnail photoshoot: **Look at the camera**, with a **Look** dial from the head alone to the eyes alone, and a sphere that turns and tilts the avatar around what is framed (Orbiters Toolkit 0.3.14).
- Gallery: cards no longer cut off their creator name and buttons in a narrow Inspector; Publish is green; the license key and details sheets grow from their button every time they open (not only the first); opening the gallery from its card no longer jumps the page behind the banner.
- Requires Orbiters Toolkit 0.3.14.

## 0.9.1 — 2026-10-05

- The Parameters section is removed: XRay Gizmos 0.2.8 shows the avatar budget over the Scene view, with parameters as built (after VRCFury's compression), bones, PhysBones and contacts against VRChat's PC limits.
- Fix an error on every editor update once the avatar shown in the VRChat SDK panel was destroyed (a build copy, a test avatar); it also stopped other tools' update callbacks.
- Requires Orbiters Toolkit 0.3.13 and XRay Gizmos 0.2.8.

## 0.9.0 — 2026-10-05

- Asset gallery: clothes and accessories from Orbiters creators, filtered for the avatar's base and platform, added in one click. Paid assets list every store selling them with the creator's preferred one first, then turn into a license key field; purchases are recognised from your Gumroad or Jinxxy email. Access from a tier, a Discord role or testing shows "Included".
- Installs check the parameter budget, platform and dependencies first, show what an import would replace before changing anything, resume after an interruption and verify the avatar before reporting Installed. Gallery items in the accessories list show updates and can be removed.
- Creators publish from My Avatar: prefabs or a `.unitypackage`, packaging rules (no Poiyomi Pro, source files or VPM packages), parameter cost and size, a test install on the current avatar, versions with public, beta or alpha scope and testers, and a rights confirmation.
- Cleanup (Orbiters settings): frees the files of removed gallery assets that nothing uses, keeps uncertain ones with the reason, and moves files to a restorable quarantine first.
- Publishing opens its own window, so selecting something else keeps the form; the gallery page stays open when the Inspector is rebuilt.
- The `myavatar_gallery` MCP tool creates, builds, tests and publishes gallery assets with the same draft; publishing and withdrawing need the user's approval code.
- The drawing pen is now a free accessory in the gallery instead of a My Avatar section.
- Clothes and accessories leave alpha and are on by default.
- Requires Orbiters Toolkit 0.3.12 and the matching Orbiters backend.

## 0.8.5 — 2026-10-03

- Improve cross-base clothing fitting and restore completed-fit review, cancellation and creator commission actions.
- Repair broken clothing materials, default missing roughness maps to matte and preserve creator-authored packed-map channels and inversion.
- Skip already-uploaded thumbnails when preparing the VRChat SDK, so an unchanged image cannot block the new avatar bundle.
- Add Drawing pen after Parameters and hide the empty clothes-and-accessories section while keeping drop choices reachable.

Unity 2022.3 avatar setup: textures, clothes and accessories, thumbnail, posing, physics and tools, with an optional Orbiters account.

## Get started

1. Install **My Avatar** from the Orbiters VPM repository. Toolkit and XRay Gizmos
   are required dependencies; Unit Git 0.1.3 or newer is optional. The VRChat avatar SDK removes this editor-only
   component when uploading an avatar.
2. Select the avatar root in an open scene. Add **Orbiters > My Avatar** using
   Add Component or the GameObject menu.
3. Drop PNG, JPG or TGA files (or their containing folder) into the **Drop anything** field.
   Folder import includes immediate files, not nested folders. Up to 48 files,
   256 MB per file and 1 GB per batch are supported. The field turns into a
   progress bar, then shows the result with **Undo** and **Save**; drop another
   set on it at any time.
4. Clear matches apply automatically. Textures that still need a slot are listed
   below the field, grouped by the material they most likely belong to (**No clear
   target** last): **Use** accepts the suggestion, **Other…** / **Choose slot…** picks
   another, and either applies immediately. The **×** of a row, of a group or
   **Dismiss all** leaves images out (screenshots, icons of the dropped folder); Undo
   brings them back.
5. **Undo** restores the materials from before the set (then reads **Redo**),
   including after a scene reload. Save your scene to retain the component data.
6. **Save** saves the current avatar scene and generated assets. When Unit Git is
   installed it also creates a local commit named `texture change` once the
   project's Git repository is initialized in Unit Git. The checkpoint includes the current scene (including its other pending
   changes), generated textures/materials and their metadata. Unrelated project
   paths and staged changes are excluded. Ignored checkpoint files must be
   unignored first. Saving unchanged files succeeds without making an empty commit. Nothing is pushed.

## Clothes and accessories (alpha)

Turn it on in **Settings** (bottom toolbar) › Features › My Avatar. The **Drop anything** field then also
takes what clothing and accessory creators deliver: a `.unitypackage`, a `.zip` (also
one holding a package, like `…_UnzipMe.zip`), a prefab, an FBX or an OBJ (copied with the `.mtl` libraries and images it names), with their textures
and `.txt` readmes, or their folder; a drop holding none of those is a texture set. My Avatar imports what is not in the project yet,
picks what to put on, places it under the avatar root and attaches it. It never changes
the avatar or moves bones into its armature: what attaches is applied to the build copy.

- **Already set up** with VRCFury or Modular Avatar: placed on the avatar root, as its
  creator intended, with nothing added but a menu toggle when it has none.
- **Clothing with its own armature**: bone names that match the avatar's exactly get
  one VRCFury Armature Link. Other names (`upper_arm.L`, `J_Bip_L_UpperArm`, `Left arm`…)
  are matched by humanoid role and linked by My Avatar; at build each linked bone moves
  under its avatar bone, keeping its rest offset. Extra bones (hood strings, physics
  chains) follow their parent.
- **Props** (hats, hair, glasses, bracelets…) follow one bone, chosen from their name or
  position. Modelled far from it, they are placed on it; **Bone ▾** changes it.
- Empty constraints named after avatar bones (`Head`, `Left wrist`) are wired to them
  and become VRChat constraints.
- A saved VRCFury toggle under **Accessories/<name>** is added when the accessory has no
  toggle or controller of its own; the list shows the parameter bits used. Body
  blendshapes also drive the same-named shapes of the accessory at build, without a
  VRCFury component per shape.
- Images of the drop go to the accessory's materials, as in the texture field.

Variants are chosen for you: VRCFury over manual setups, PC over Quest (Quest on an
Android build target), with late-join sync over without. The only question is the
hand, when a package ships one per hand (**Left hand**, **Right hand**, **Both**); a
drop holding several different items lists them to pick from. Dropping something
already on the avatar offers **Replace** and **Add another**.

What My Avatar can't do for you is listed under the field with **Select**: a name that
asks for a manual step ("Put me in armature"), a script of another tool (VRCLens has its
own installer), a missing script, a bone matching nothing of the avatar. A package made
for Modular Avatar, and VRCFury itself, can be installed from there. `.blend`, `.max` and
`.stl` sources are refused with a note; setup scenes are not supported yet.

Each accessory is one Undo step; **Remove** in the list deletes it. Packages with scripts
make Unity recompile: the drop continues after it, even without the Inspector open.

The list shows each accessory with a picture of it as worn (made in the background and kept in
`Library/Orbiters/Thumbnails`), its fit, and how it is attached. With AI help on, each gets a short
clean name there and in Posing (`Glowsticks_Body ultipaw Variant` reads **Glowsticks for Ultipaw**);
its object keeps its own name, shown on hover.

### On a custom base

When the avatar uses a custom base, My Avatar finds out which one in the background when its
Inspector opens: from the avatar's MCB component (the exact version applied), or, without
MCB, from its body's model file, which Orbiters recognises (only a hash of the file is sent).
For each accessory then, only the meshes lying close to skin that the custom base's
blendshapes move count (its declared shapes and every flexing shape); shoes far from flexing
arms, rigid props and accessories that already have the shapes are left alone, without a
question. Each drop asks again; accessories already on the avatar are checked once per custom
base version.

- **Does it fit your body?** The question shows the custom base (picture, name, version) and the
  shapes involved. **Already fits** adds the blendshapes it lacks, so it flexes and moves
  with the body, including every animation of those shapes once built. Shapes its creator
  already made stay as they are. **ReFit** shows the original base see-through over
  the body and lines the accessory up with it (its hips on the original's, when a little
  off): drag its arrows in the Scene view if it is still off (the selection does not change),
  choose how tight it should fit, then **ReFit now**. This step needs MCB on the avatar, which
  provides the original body.
- An accessory that already has some of the custom base's shapes, or whose creator marked it
  with **Orbiters › Fit Info** as made for the custom base, is only offered the missing shapes;
  one marked as made for the original base goes straight to **ReFit**.
- ReFit is installed with one click the first time an answer needs it; the answer continues
  on its own once Unity has reloaded.
- The result says how many shapes now follow the body, with **Restore original**. When some
  spots could not be fitted exactly, **Ask a creator** opens ReFit's commission page on it.
- **Not now** stops asking about that accessory for this custom base version. MCB keeps the
  refits per custom base version: switching versions puts back those made for it, and
  accessories lacking the new version's shapes are asked about again.

With AI help on (the robot of this field, the same account setting), what the rules
could not decide is asked in the background: which of two items to use, the bone a prop
goes to when only its position suggests one, clothing bones nothing matched, and manual
steps its readme mentions. It receives object and bone names and paths inside the
accessory and the avatar, component types and up to 12,000 characters of the drop's
`.txt` and `.md` files (next to a dropped prefab too); no images, no computer paths.

## Quick optimization

Once a drop has placed an image that can be compressed, a **Quick optimization** card
appears below the drop field with the estimated texture memory before and after. It
opens by itself when a drop brings something to optimize; otherwise it stays folded
under its title.
**Optimize** caps the body material's textures (the largest part of the avatar's body mesh) at 2048 px and every other
texture the avatar's materials show at 512 px (never upscaling), and sets their PC (Standalone) import settings for the best quality per byte of VRAM:
BC1 for colour whose alpha is unused (measured on the pixels, not taken from the
importer), BC7 when alpha is used, BC5 for normal maps; mipmaps and mipmap streaming
on, crunch off (crunch only shrinks the download, not memory), best compression
quality. sRGB, alpha-is-transparency and every other setting stay as they are. Ramps,
lookup tables, gradients, SDF maps, very thin strips, HDR images, render textures and
textures that are not plain imported images are left alone. Mobile settings come later.

Import settings change in place, all in one import pass. A texture also used by
something else (another object or avatar in the open scenes, or a prefab or scene in
`Assets` that depends on it, other than this avatar's own prefab and model), or inside
a package, is copied under `Assets/Orbiters/MyAvatar/<batch-id>/` instead, and this
avatar gets copies of the materials that show it, assigned only to renderers under this
component. It applies entirely or not at all: if a step fails, the import settings
already written are put back and the copies removed. The row then
shows the estimated texture memory (VRAM, as VRChat ranks it, not download size) and
how many textures changed; **Optimize** comes back after a new texture drop or an Undo.
The estimate reads the PC import settings, so it is right even when the editor keeps
textures uncompressed ("Compress Textures on Import" off). **Undo** restores the import settings and materials,
including after a restart: the component stores what it changed, so save your scene.
Unity's Undo and Redo work too. Import settings or material slots you changed since
are kept; copies stay on disk. Undo the optimization before undoing its texture set.

Once optimized, the row points to [d4rkAvatarOptimizer](https://github.com/d4rkc0d3r/d4rkAvatarOptimizer)
for merging meshes and materials and removing unused bones and blendshapes, offers
**Add to avatar** when it is installed but not on the avatar, or notes that it is
disabled. It optimizes at upload; with VRCFury or Modular Avatar its author
recommends that over **Create Optimized Copy**. My Avatar is not associated with
d4rkAvatarOptimizer.

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
capture gets a unique filename so Unity Undo and Redo restore the previous pixels.

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
- **Clothing** (beta) previews clothing and accessories as they will be attached once
  built: they follow the avatar's pose. It reads VRCFury Armature Links (merged bones,
  and props linked to one bone), My Avatar attachments, and for clothing that nothing
  links yet, matching bone names. Each follower keeps its rest offset from its avatar
  bone, taken from both meshes' bind poses. Bones driven by constraints are left to
  them. The list below shows each accessory, how it follows and how many of its bones
  do; click one to select it. It is only a preview: switching it off (or saving the
  scene, entering Play Mode, a script reload) puts the accessories back where they were.

Pose edits of the avatar stay regular Unity edits: Undo reverts the avatar and its
mirrored bones together.

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

## Avatar budget

XRay Gizmos shows where the avatar stands against VRChat's limits in a panel over the Scene view: synced
parameters as built (after VRCFury's compression), bones, PhysBones and contacts, with a custom base's share.

## Tools

Buttons for tools that go with My Avatar, each marked **Install** until it is in the project:

- **Gesture Manager** (VRChat curated repository): installs the latest version when missing,
  then puts its Gesture Manager object at the scene root, or selects the one already there.
- **Unit Git**: opens Unit Git, after installing it when missing.

An install goes through VPM like any optional dependency; the tool opens once Unity has reloaded.

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

## Blueprint ID check

VRChat only lets an account update the avatars it published. When an avatar carries someone else's blueprint ID (its
Pipeline Manager's ID, often left in a prefab by the avatar's creator), the VRChat SDK only finds out when it uploads,
after the whole build. With an Orbiters account linked to your VRChat account, My Avatar checks the ID before the build
starts: when you click **Build & Publish**, it asks Orbiters who published the avatar that ID points to and compares it
with the VRChat account the SDK uploads with (or your linked one). When it belongs to someone else, the build stops
right away: the SDK reports it was aborted, then My Avatar's window says whose avatar it is and offers **Clear the
blueprint ID** (one Undo step): the avatar is then uploaded as a new one, after you give it a name and a thumbnail in the
SDK panel. **Put the ID back** restores it.

- The avatar shown in the SDK panel is checked as soon as it is selected, so Build & Publish doesn't wait; answers are
  kept ten minutes.
- Nothing is blocked when the answer isn't known: not signed in, no linked VRChat account, an avatar Orbiters can't
  see (private, hidden or deleted), or no answer within a few seconds. Build & Test is never stopped.
- Orbiters reads the avatar's author with its own VRChat account and only answers signed-in members with a linked
  VRChat account.

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
shared controls (segmented control, scrub dial, icons, switch, stage badge, support footer),
feature flags, VPM dependency prompts, bone matching, accessory attachment and its build step,
symmetry and accessory posing, the parameter estimate and hair/tail/toe PhysBone
editing. XRay Gizmos draws and picks bones. Unit Git, when installed, owns Git
execution and scoped index handling; the `MYAVATAR_UNITGIT` version define enables
the commit step. My Avatar does not depend on MCB.
