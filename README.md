# My Avatar

Unity 2022.3 avatar texture setup, with an optional Orbiters account.

## Get started

1. Install **My Avatar** from the Orbiters VPM repository. Toolkit and Unit Git
   are required dependencies; the VRChat avatar SDK removes this editor-only
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
6. **Save · Unit Git** saves the current avatar scene and creates a local commit
   named `texture change`. Initialize the project's Git repository in Unit Git
   first. The checkpoint includes the current scene (including its other pending
   changes), generated textures/materials and their metadata. Unrelated project
   paths and staged changes are excluded. Ignored checkpoint files must be
   unignored first. Nothing is pushed.

## What changes

Each drop imports copies under `Assets/Orbiters/MyAvatar/<batch-id>/`. The tool
creates material copies and assigns them only to renderers under this component.
Original materials and external images are preserved. Normal maps get Unity's
normal-map importer; named mask maps use linear color. Standard shader normal,
metallic, specular and emission keywords are enabled where appropriate.

Only actual visible 2D texture properties on the current shader are considered.
Locked or baked shaders may need unlocking before their slots are editable.
Custom shaders can require their own feature toggles. The tool does not convert
roughness to smoothness, repack texture channels, change UVs or switch shaders.

Undo preserves generated files so other references remain valid. It refuses to
replace renderer assignments edited since the apply. A new apply replaces the
component's previous persistent Undo snapshot; Unity's regular Undo is also
recorded. Undo after a Git checkpoint creates a new local change, not a history
rewrite. Existing imported files also remain after cancellation.

## Optional account and AI

The account row uses the same Magic Sync account as MCB. Connecting
is optional; local matching always works. Logging out affects the shared account.

When an account is connected and AI is enabled in that account on the Orbiters
website, My Avatar automatically sends texture filenames, relative imported paths, dimensions, approximate
grayscale/normal-color measurements, current material/shader slot information and
a small JPEG preview sheet to the Orbiters backend. Full-resolution images and
absolute computer paths are not uploaded. The backend uses the configured AI
provider and honors the account's AI preference. Source context and model output
are excluded from Orbiters AI history; usage and status are retained. Provider
processing follows that provider's configured data policy.

Responses use validated texture/slot IDs. Local matches remain authoritative;
low-confidence, conflicting and incompatible assignments stay unassigned. On a
network or provider failure the Inspector shows the reason and uses local matches.

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
animated glow rendering and SVG logo drawing. Unit Git owns Git execution and
scoped index handling. My Avatar does not depend on MCB.
