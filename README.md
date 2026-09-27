# My Avatar

Unity 2022.3 avatar texture setup, with an optional Orbiters account.

## Get started

1. Install **My Avatar** from the Orbiters VPM repository. Toolkit is a required
   dependency; Unit Git is optional. The VRChat avatar SDK removes this editor-only
   component when uploading an avatar.
2. Select the avatar root in an open scene. Add **Orbiters > My Avatar** using
   Add Component or the GameObject menu.
3. Drop PNG, JPG or TGA files (or their containing folder) into the Inspector.
   Folder import includes immediate files, not nested folders. Up to 48 files,
   256 MB per file and 1 GB per batch are supported.
4. Clear material-name and shader-slot matches apply automatically. Review the
   inline cards for alternatives or missing matches. Choose a slot and click
   **Apply selected matches**. Competing variants are left for you to choose.
5. **Undo last apply** restores the preceding renderer material assignments,
   including after a scene reload. Save your scene to retain the component data.
6. **Save** saves the current avatar scene and generated assets. When Unit Git is
   installed the button reads **Save · Unit Git** and also creates a local commit
   named `texture change` once the project's Git repository is initialized in Unit
   Git. The checkpoint includes the current scene (including its other pending
   changes), generated textures/materials and their metadata. Unrelated project
   paths and staged changes are excluded. Ignored checkpoint files must be
   unignored first. Nothing is pushed.

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

Undo becomes **Redo last apply** after restoring the previous materials. Redo
restores the saved assignments immediately without importing or requesting AI.
Undo preserves generated files so other references remain valid. It refuses to
replace renderer assignments edited since the apply. Each drop is one logical
operation: later AI matches and **Apply selected matches** reuse that batch's
materials and keep its snapshot, so Undo returns to the state before the drop.
A new drop replaces the component's previous persistent Undo snapshot; Unity's
regular Undo is also recorded, with AI matches as their own step. Undo after a Git
checkpoint creates a new local change, not a history rewrite. Existing imported files also remain after cancellation.

## Optional account and AI

The account row uses the same Magic Sync account as MCB. Connecting
is optional; local matching always works. Logging out affects the shared account.

Local matches are applied immediately. When textures remain unmatched, an account
is connected and AI is enabled in that account on the Orbiters website, My Avatar
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

Unresolved cases retain a suggested target and offer **Use this on …**. Only those
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
Toolkit owns account storage, API roots/transport, shared account controls,
animated glow rendering and SVG logo drawing. Unit Git, when installed, owns Git
execution and scoped index handling; the `MYAVATAR_UNITGIT` version define enables
the commit step. My Avatar does not depend on MCB.
