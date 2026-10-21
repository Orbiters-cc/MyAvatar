# My Avatar

## 0.9.13 — 2026-10-21

- A prop's own settings show on its card under On this avatar, as a small switch that moves on press: Orb's text slots
  (4 or 8: 50 or 82 synced bits), so an orb fits an avatar with few free bits. The parameter count below the list
  follows; a toggle adds Orb's Walk boost radial to its menu, another (Replace Orbiters overlay) has Orb project its menu
  on a screen before you instead of Orbiters desktop's SteamVR overlay. A card's settings line up in one column. Needs
  Orbiters Toolkit 0.4.2.
- A gallery item with a newer version shows Update on its line in Mixer's Hierarchy too (the same update as on its line
  under On this avatar, in the background, the pill saying "Updating…" meanwhile).
- The gallery's green buttons (Add, Update, Post review) have white text, like the progress bar that takes their place.

## 0.9.12 — 2026-10-21

- The optimization estimate waits while a tool edits the avatar's meshes live (Mixer sculpting or painting): it took
  ~30 ms of every frame and sometimes 2.6 s at once, which made strokes stutter, and measured a temporary copy anyway.
  Only that avatar's estimate waits: the other avatars' go on, and its hidden copy is let go when the editing lasts.
- My Avatar works from other views of the avatar (Orbiters Toolkit's `AvatarObjectTools`): Mixer's Hierarchy shows a
  "Drop anything" field on the avatar's row, and what is dropped there goes to My Avatar's drop field (clothing,
  accessories or textures, exactly as there); an accessory whose fit is in question shows "ReFit ?" on its row, which
  answers like the fit card's ReFit tile. Both are done by the avatar's Inspector: the one showing it, else the avatar
  is selected (or opens in an Inspector of its own when every Inspector is locked). Each drop waits for that Inspector on
  its own, the row saying "Adding…" meanwhile, for 10 seconds at most: one no Inspector took by then is given up with a
  warning instead of running later (a second drop no longer replaces the first).
- **Share My Avatar diagnostics** (Orbiters settings › My Avatar · Diagnostics, off by default) sends My Avatar's own
  warnings and errors, with their stack traces, to Orbiters through Orbiters Toolkit's shared reporter. The card says it
  covers every Unity project on the computer and that reports are linked to your Orbiters account while you are signed
  in. Tokens, keys, cookies, e-mails, links, VRChat ids, other accounts' and avatars' names, your user and computer names
  and folder paths are removed first; ReFit's, VRCFury's and Unity's logs and tests' deliberate failures are never sent
  as My Avatar's.
- **Export FBX** on a refitted accessory writes its refitted meshes as they look fitted (the fit applied, the body's
  blendshapes kept at their weights) with their armature to one FBX file that Unity and Blender import as it is.
  Requires Orbiters Toolkit 0.4.1.
- **Substance 3D Painter link** (see [Substance 3D Painter link](#substance-3d-painter-link-beta), now described):
  - Live metallic and roughness show as painted: the preview packed them with every pass of its shader and showed
    (0, 0, 0, 1) whatever was painted (Standard looked like a mirror). Live images are uploaded without a mip chain made
    on the editor's thread; their mips are made on the GPU.
  - Closing or reopening the avatar's scene ends the link and tells Painter why; Painter links again once the avatar is
    back. Before, Painter stayed "linked": its saves were dropped and Scene views repainted every frame.
  - Only the project Open in Substance made follows the avatar's meshes. A project linked from Painter keeps its own mesh
    (a Blender export replaced it with the posed avatar, strokes reprojected) and no longer replaces the made project on
    the avatar. Mesh updates keep the names of the project's texture sets (two materials of one name no longer swap),
    include the renderers the project was made with that are hidden now, and map a new material's texture set at once. A
    mesh Painter was loading across a script reload is settled by its answer instead of being loaded twice.
  - Each save in Painter is imported into new files (`Skin_BaseColor 2.png`, …; the newest four of each map stay): Undo
    of a save shows the one before. Two texture sets on one material give it the first one's maps instead of failing the
    whole save, and texture sets whose names make the same file name no longer overwrite each other. Painter's exports
    are deleted once applied.
  - Unity reads Painter's images only from the link's folder, and Painter only makes projects from files Unity wrote
    there, saved where no file is yet. Painter only links to the Unity editor listening on the port its endpoint file
    names (Windows), and sends the link's token only once that Unity proved it holds it (with an Orbiters Toolkit that
    answers the live link's proof request; the endpoint file says so). A payload that doesn't hold together closes the
    connection.
  - A second Painter project linking the same avatar takes it over once: the first stops linking until you click
    **Link again** in its panel (they took turns every 1.5 seconds). With two copies of a Unity project open, Painter
    links through the one the project was linked in.
  - Painter plugin 1.4.0: installed by its version, never over a newer one (another project's), written whole and dated so
    a running Painter reloads it; a file that doesn't compile yet is never loaded. Painter renders again only what a
    mapping changes (a layer edit rendered every channel of every texture set again), exports only the maps a texture set
    has a channel for, and a project that never opened no longer holds up the next one.
  - The listener only starts once the plugin is installed. The Painter card no longer reads the plugin files or lists
    processes at every redraw. Open in Substance keeps its choices across script reloads and no longer adds a timer at
    each rebuild; posed meshes no longer leak when posing fails; the bones' X-ray and weight paint shown on the avatar are
    never baked into Painter's mesh.
  - The project's path is kept from your home folder (`~/Documents/…`): scenes and prefabs no longer hold your user
    name. On macOS, Unity uses the folders the plugin uses.
  - The link's folder is cleared once per Unity session of what nobody needs (see the section).
  - The plugin is in the release package again: the package's file list left `Painter~` out, so **Install the Painter
    plugin** failed with "missing from the package".
  - Two avatars of one name keep their own mesh updates for Painter. Two saves within a second export to folders of
    their own (Unity's clean-up of the first deleted the second). Older saves of a map go to the Recycle Bin, and only
    once the new one is applied. Taking live images off keeps what other tools put in the renderer's slot meanwhile. A
    live image that can't be shown says why on the card. The plugin closes a connection announcing a frame larger than
    the link allows instead of reading it.
- Clothing lined up with the original base has its hips put on the original's again when they are a few centimetres off
  and the armature fit leaves it as it is (0.9.10 left them 1 to 6 cm off). Cancelling or dismissing a line-up puts the
  clothing back as it was before it, with Undo. Clothing made for the avatar's original base says so instead of "made for
  another body".
- The optimization estimate is measured again when a component's settings change (a PhysBone's, a toggle's) and after a
  package or SDK update; the bones' X-ray shown on the avatar no longer makes it measure again. A setting counts for the
  avatar holding the component only: editing a light or another avatar measures no avatar again.
- An upload step that fails and is skipped (menu clean-up, an optimization pass) logs one warning with the exception's
  details instead of an exception and a warning. A Gallery installation that stops no longer names the asset in the
  console.
- The Console warning of the blueprint ID check no longer names the other VRChat account or its avatar (the window still
  does).

## 0.9.11 — 2026-10-19

- Requires Orbiters Toolkit 0.4.0, which now draws the bones the Posing section uses; the separate gizmos package is no longer needed.

## 0.9.10 — 2026-10-18

- Clothing refitted from the original base is first fitted onto the original base's armature, bone by bone, as onto an
  avatar's, while the original is shown over the body. A custom base can move its bones on the same body (the Ultipaw's
  hips, spine and legs stand 8 cm above the Masculine Canine's, its shoulders 1 cm): clothing left on them was refitted
  8 cm too high (the Rexouium FishingOutfit on the Ultipaw: collar at the chin, shorts over the belly). Before, only the
  hips were lined up, and not at all with an original base imported without a humanoid avatar.
- Clothing dropped on an avatar on a custom base is placed on the original base's armature over the body, as it would be
  worn on the original, before any refit: the Rexouium FishingOutfit sat 8 cm high on the Ultipaw, aligned with the
  custom base's bones. Clothing made for the original stays where it is and keeps that place once uploaded; clothing
  made for the custom base itself is left as it is. The first drop on a custom base version reads its original (MCB may
  import it, which can take half a minute); later drops reuse it.
- Clothing resized from another body ("made for another body: shrunk to 91 %") is refitted as such: what of it lay in
  the body as put on is covered after the fit, however little. The FashionSuit (made for the Vulper) on the Ultipaw no
  longer shows the arms through its jacket nor the calves through its trousers, and keeps its belt over the waistband.
- Needs Orbiters Toolkit 0.3.24.

## 0.9.9 — 2026-10-13

- **Optimization**: a new section, framed like face tracking, gathers everything that makes the avatar lighter. It shows
  the rank VRChat gives the avatar now (from the SDK, on the platform the project builds for) and offers **Small**, **Big** or **Custom**
  optimization of the copy VRChat gets when you upload; the avatar in your scene never changes. **Small** removes what
  nothing shows or uses: objects that start hidden and that no animation shows, disabled components no animation turns
  on, PhysBones that move nothing, unused PhysBone colliders, blendshapes nothing animates or that animations only ever
  set to the weight they have (baked in at their weight),
  bones a mesh lists without using, empty material slots and slots sharing an opaque material, and textures materials
  still point at that their shader doesn't use (after Poiyomi locks). **Big** also merges the meshes that are always
  shown into the one animations use, and turns bones that only end a PhysBone chain into its endpoint. **Custom** has a
  switch per change. Under the choice, an estimate of what VRChat would rank the uploaded copy (a hidden copy put
  through the same changes and measured with the SDK) lists every stat that changes and says what holds each rank. The
  frame's ring shows how much lighter the upload downloads than the avatar as it is (every change counted).
  See [Optimization](#optimization).
- **Download size**: the section estimates the file players download, drawn as VRChat's own loading bar draws it over
  an avatar that starts downloading ("0% / 16.8 MB", its stripe drifting left), now and at upload, with how long the
  avatar takes to show once downloaded, in orange over VRChat's limits on the bundle the SDK uploads (200 MB, 10 MB on
  Quest; unpacked 500 MB, 40 MB on Quest). Textures, meshes,
  blendshapes, animations and shaders are counted at the size the build stores them, then compressed as VRChat
  compresses them: textures are measured on the data the build stores (the upload's copies included), the rest with
  ratios measured on real bundles. On MasculineCanine (Big) it says 14.5 MB for a 15.3 MB bundle, which VRChat shows
  about 5% smaller (a 17.7 MB bundle showed as 16.8 MB in game).
- The section shows one platform, the one the project builds for: PC, or Quest when the build target is Android (rank,
  download, load time, texture memory and what the upload does to the textures).
- **Big** also hides the body under clothing worn all the time (triangles opaque clothing covers from every side go),
  folds bones that never move into the bone they hang from, and gives a Quest upload ASTC textures (6x6, 8x8 for masks)
  at half the PC size, at most 1024 px.
- **Textures sharp from a distance, at upload**: textures are no longer optimized with Apply and Undo. The upload gets
  copies of them, each sized to stay sharp from a distance (**Small** 1 m, **Big** 3 m, a slider under **Custom**, 25 cm
  to 4 m) from how much of the avatar it covers, measured on the meshes, never larger than it is, and compressed for the
  platform; your textures, materials and import settings stay as they are. Copies are kept in `Library` and reused by
  the next upload. The **At upload** estimate (texture memory, download) counts them with every other change.
  Quick optimization (0.6.0–0.9.8) and what it remembered are gone: textures it optimized keep the PC override it wrote
  in their import settings (max size, format, crunch), and avatars whose shared textures it copied keep those copies
  (`Assets/Orbiters/MyAvatar/<id>/Textures` and `Materials`). To go back to the originals, untick **Override for PC**
  in those textures' import settings and put the original materials back on the renderers; the upload's copies are then
  made from the originals.
- **Small** also uploads the same image once when it is imported twice under two names (the same file bytes and import
  settings: a package bringing its own copy of a texture the avatar already has).
- **Fewer polygons** (Small within 4 mm, Big within 8 mm, a slider under Custom from half a millimetre): each mesh loses
  the polygons whose removal keeps its surface within the budget. What expressions, visemes and small toggles move stays
  exactly as it is; broad shapes (body types, muscle flexes, pose correctives) are carried through and the surface is
  checked with each of them, as it stands now, and for shading. The estimate measures it in the background. It runs on
  **AlterMesh**, Orbiters' mesh simplifier in Orbiters Toolkit, derived from Meshia Mesh Simplification (Ram.Type-0,
  MIT) with its bugs fixed; no package to install.
- **Big** goes further:
  - static props (plain meshes always shown) join the skinned mesh, skinned to their own transform;
  - meshes every animation shows and hides together (an outfit's pieces under one toggle) become one, their
    blendshape animations moved onto the merged mesh;
  - its textures upload crunched (colour and masks, quality 75: a much smaller download for the same memory), with
    metallic, roughness and smoothness maps at half the size the others get;
  - the menu's icons upload at 64 px at most (copies of the menus, your images stay as they are);
  - objects and the parameters only animators use upload with one or two letter names (bones of the humanoid model,
    Body, VRChat's parameters and those of the menu, OSC and saved values keep theirs).
- **Avatar-wide bounds** (Small): every skinned mesh that moves with the hips gets the same culling box at upload,
  around the whole avatar with room for its limbs, so no part vanishes at the edge of the view because its own box was
  small or measured from a far bone. Props held by a constraint (they can leave the avatar) keep theirs.
- **Checks**: a new section after Optimization builds a hidden copy the way an upload does (VRCFury, Modular Avatar,
  MCB) and lists what won't work in VRChat: menu pages over 8 controls, controls on missing or unread parameters, synced
  parameters no animator reads, transitions waiting on parameters their controller doesn't have, states nothing leads to,
  body shrink shapes left on while clothing can come off ("black holes"), and what Quest loses (desktop shaders,
  components it removes, its rank). When the same avatar's other-platform version is open (the same blueprint), both are
  built and their synced parameters compared. See [Checks](#checks).
- **Export to VRM**: a new section after Checks writes a .vrm file (VRM 0.x for VSeeFace and most apps, or VRM 1.0) of
  the avatar as it uploads, VRCFury, Modular Avatar and MCB built and the Optimization level applied, for VSeeFace,
  Warudo, VNyan and other VRM apps: MToon materials, springs and colliders from its PhysBones, visemes, blink, the
  emotions its face has and perfect sync (an expression per ARKit shape the face has, under its own name or another
  face tracking standard's). The form says what the file says about the model (name, authors, who may use it and how),
  starting at the most restrictive, and Export waits for you to confirm the model's license allows VRM. Needs UniVRM
  (0.131 or later). See [Export to VRM](#export-to-vrm).
- **Face tracking**: where tracking moves both a shape that shrinks the iris and the wide eyes, the iris keeps its size
  and the eyes open a quarter as wide: the two stacked show far too much eye white.
- The texture optimization moved from under the drop zone into the Optimization section, with the pointer to
  d4rkAvatarOptimizer, which now only covers what My Avatar doesn't do (merging meshes that use different materials).
- Orbiters Toolkit gains the shared pieces (`Orbiters.Toolkit.Editor.VRChat.Optimization`): what a built avatar
  animates and points at, the component and PhysBone clean-ups, bone-list pruning, material slot and mesh merging
  (props and meshes toggled together too), unused and duplicate texture references, menu icons, short names, and
  taking meshes apart and putting them back together (`MeshParts`). VRChat's own parameter names are shared
  (`VrcParameters`), and the build's copies of the animations can be read (`AttachmentAnimationBuild.Owned`, `Clips`).
  Also new there: `AvatarBounds`, the avatar built as an upload builds it in a hidden copy (`HiddenAvatarBuild`), the
  checks (`Orbiters.Toolkit.Editor.VRChat.Checks`), and `VrcFury.IsComponent` / `ModularAvatar.IsComponent`. Its blendshape pruning now also works on meshes imported without Read/Write (outside Play
  mode).
- **Edit menu**: settings changed together are written in the order made; a menu or parameters asset that other
  avatars, Full Controllers or prefabs use is copied before it changes (Full Controller items are read-only); the
  upload's plan is taken before Modular Avatar builds its menu, so its items stay with **Remove default menu**; left-out
  items are matched by type and parameter too; the window stays open when a save fails; double-click renames the folder
  itself; number fields keep Backspace and Ctrl+Z; each change is one Undo step; parameters OSC apps drive (heart rate,
  chatbox…) are never frozen. Items of VRCFury, Modular Avatar or a Full Controller left out before this version have
  to be left out once more.
- GoGoLoco, face tracking and the Tools section show what they will install (packages and Creator Companion
  repositories) before installing anything.
- Broken materials: a new backup for each original and every fix one Undo step; a texture drop's Undo no longer takes
  your other changes with it.
- Gallery: deleting a review needs a deliberate second press, a failed edit keeps what you typed, likes stay in sync,
  pictures are size-capped and freed, versions compare as versions; the AI's gallery tool only takes pictures from the
  window or the project.
- Novabeast and Rexouium looks copy a shared parameters asset before changing it; face tracking converts eye-tracking
  conditions in every layer and its live test rebuilds when the avatar changed; buttons a section turned off stay off
  after an action.

## 0.9.8 — 2026-10-10

- **Likes and reviews in the gallery**: an asset's details show its likes beside its name and its reviews at the end, the
  same as on its Orbiters page. Signed in, you like it with the thumb (green once liked), like other people's reviews,
  and write your own review, then edit it in place or delete it with a second press. Reviewers who own the asset are
  marked **Owns it**, the creator **Creator**.
- Everything shows at once: a like counts before Orbiters answers (quick double presses send only the last wish), a new
  review appears dimmed until it is saved, and a failure puts back what Orbiters holds with a short note.
- **Like counts on the cards**, at the end of the creator line; liking an asset in its details updates its card.
- **Materials that don't render**: the Materials card opens on an avatar drawn pink (its shader is missing, fails to
  compile or is unsupported, Poiyomi's generated shader of a locked material included), says per material what it was
  made for and the shader it moves to, and **Fix materials** fixes them all in one Undo step, keeping each original
  beside it as "name (old)". See [Materials that don't render](#materials-that-dont-render). The `myavatar_materials`
  MCP tool does the same.
- **Face tracking for part of a face**: the Face tracking card now shows up when the face has most of the shapes of its
  mouth, or of its eyes, not only of both (e.g. Ultinova's lip tracking shapes without eye shapes). It says which parts
  move and which are missing; an upload leaves out the parameters of a part the face has no shapes for, and takes them
  back once it has some.
- **GoGoLoco versions**: once GoGoLoco is on, the Locomotion card shows a big tile per version the installed GoGoLoco
  ships (1.8.6: **All**, locomotion, poses and emotes with its Base, Action and Sitting layers; **Beyond**, All plus
  flying from seats it carries, with an FX layer), each with the synced bits it adds to this avatar. A press switches
  the prefab in one Undo step, where the old one was, keeping its name and active state; nothing of the old one stays.
- **Edit menu**: the new Menu card opens the avatar's VRChat menu exactly as it will be in the game, VRCFury (SPS
  included) and Modular Avatar included: the Quick Menu's Expressions page and the Action Menu as Gesture Manager draws
  it, side by side. Rename items in place in either, give them an icon (drop a picture on them or choose one), drag them
  to another place, into a folder or back up, hold one on another to make a folder of the two (or tick items and
  **Create folder**), then **Save** (one Undo step, then the scene and assets are saved like the texture Save) or
  **Cancel**; Ctrl+Z and Ctrl+Y step through every change. The card is marked beta. See [Menu](#menu).
- **Textures find added parts**: a dropped set named after its numbered material ("hair_Material.002_Normal" for
  Blender's "Material.002") goes to that material, and so does one named after the object holding a part or its model
  file ("Spiky hair" above the hair's "NurbsPath.001"). The avatar's own name and the skeleton say nothing about one
  part, so a "Rexouium_…" file is not pulled onto a Rexouium hair. Where an earlier drop guessed is no hint either:
  a set that once landed on the wrong material (the body's MiscMatt instead of the hair) finds its part when dropped
  again, while images in slots you chose still count, also when the drop used the project file as it is.
- **Export prefixes don't name parts**: in a folder of several sets exported with one prefix ("<model> hoodies hairs for
  substance_EyeMatt.001_BaseColor", "…_MiscMatt…"), the words they start with are ignored and what follows names the
  part, even when other files in the folder don't have the prefix ("hairs" no longer pulls the eyes to the feathers),
  and when one set is dropped alone beside sets already on the avatar: words all those sets share ("Matt", ".001")
  don't count either, so EyeMatt goes to the eyes and not to MiscMatt.
  A set made for a Blender "Material.002" the avatar doesn't have (a part since removed) is left for you to choose
  instead of guessed onto another part.
- **Face tracking that glides**: the smoothing presets work like OSCmooth. Locally, **Balanced** (the default) starts at
  0.6 and **Smooth** at 0.85; **Responsive** keeps the template's 0.25. Others now see the face glide between the values
  VRChat sends them at the preset's pace (each frame Balanced moves 9 × the frame time of the way, Smooth 7, Responsive
  the template's 15, which got there long before the next value and then held, so the face moved in steps), and
  smoothing stays on down to 10 fps instead of switching off below 25.
- **"Disable Facial Expressions" works on every avatar**: hand-gesture layers that show the face's own expression shapes
  ("smile", "furrow" on a Novabeast), not only those face tracking also drives, rest while it is on (and while the face
  is tracked); layers moving only a body part (ears, tail, an arm flex) or blinking keep going. An avatar's own Bool
  `EyeTrackingActive` conditions read the template's Float correctly once merged.
- **Glowing eyes keep their colors after Fix materials**: lilToon's emission tinted by the main texture
  (`_EmissionMainStrength`) becomes Poiyomi's "base color as map" (and back), so a white emission mask no longer lays a
  white veil over the eyes, stronger in dark maps.
- **The tongue stays in for others**: while lip tracking is on, the avatar's other hand-gesture layers (the Rexouium's
  ear gestures put the tongue out) play without their jaw, tongue and mouth curves: the ears still move and the tracked
  mouth is left alone.
- **Leave menu items out of the upload**: in Edit menu, the red cross at an item's top right (or **Leave out** in the
  side panel) takes it out of both menus at once. It waits under the Action Menu, in **Left out of the upload**, and a
  click puts it back where it was. When that place has filled up since, the item goes on a "Next" page: VRCFury's, or,
  without VRCFury, a "Next" folder made at the eighth place with the place's last item and this one.
- **Remove default menu**, on the Menu card and in Edit menu: the avatar's own menu (the descriptor's, e.g. the
  Rexouium's Credits, Avatar Appearance, Body, Head and Gesture Speeds) is left out of the upload; what VRCFury, Modular
  Avatar, MCB and Orbiters add stays. One chip puts it back.
- **What a left-out item takes with it**, on the upload's copy only, after VRCFury: its parameter keeps its default (its
  synced bits are freed) unless another item still sets it, conditions on it are decided, states and blend tree branches
  that can no longer play go, a layer left playing one constant pose is written into the avatar and removed, objects left
  hidden for good are deleted, and materials and animations nothing uses any more stay out of the upload. Logic the menu
  didn't switch stays: the Rexouium's petting still answers to its contacts. A Rexouium with its default menu removed
  keeps 27 parameters (55 synced bits) at their default, loses 21 layers and 21 objects, and uploads 14 materials
  instead of 26 (its alternate fur colours are gone).
- **Parameter budgets count the avatar as uploaded**: XRay Gizmos' chart and My Avatar's bit counts leave out the
  synced parameters left-out items free (the same ones the upload frees: those only they set, the avatar's own, a Full
  Controller's or a VRCFury Toggle's), as soon as an item is left out, the default menu removed or a look hidden.
- **Hidden Rexouium feathers don't upload**: a feather group hidden under **Looks** is left out with its menu toggle, and
  its meshes, materials and textures with it.
- **Novabeast looks**: under **Looks**, a Novabeast shows its chest fluff, collar, goggles and mane as chips and its
  breasts and tail size as sliders, shown in the scene. A hidden part stays hidden in VRChat (its toggle leaves the menu
  and its synced bit is freed; it is a shape of the body mesh, so the mesh stays); a slider sets where it starts.
- **Item settings in Edit menu**: the side panel shows the selected item's type (toggle, button, slider...), parameter,
  value, what it starts at and whether it is saved and synced, and changes them. **Save** writes them to your own
  VRCFury Toggle (toggle, hold button or slider, its default, saved) or to the avatar's own menu (type, parameter, value)
  and parameters (default, saved, synced); an item from a package says its settings stay as its creator made them.
- **Default icons**: Orbiters' toggles, sliders and folders without an icon get a white dot, a gauge or a folder (64 ×
  64 PNGs in `Assets/Orbiters/Menu Icons/`), new ones when they are made and older ones at upload; VRCFury's **Next**
  pages get a white arrow unless Override Menu Settings gives them an icon. Edit menu shows them as uploaded.

## 0.9.7 — 2026-10-09

- **GoGoLoco in one click**: a Locomotion section, framed like face tracking, offers Franada’s GoGoLoco on every humanoid
  avatar. **Add** downloads it from its VPM listing when it is missing (with VRCFury when the project has neither VRCFury
  nor Modular Avatar) and puts its standard prefab ("GogoLoco All", for VRCFury, or for Modular Avatar in a project that
  only has it) on the avatar in one Undo step, as GoGoLoco's own Add Prefabs window does. Once it is on, the section shows
  its version with **Select** and **Remove**; a GoGoLoco set up another way (unpacked, or in the descriptor's playable
  layers) shows as already set up. Franada is credited under the frame, with a link, and in the About window.
- Face tracking and GoGoLoco share their one-click pieces: the downloads that finish after Unity has imported the
  packages (`PackageSetup`), finding a setup already on the avatar (`PackageUse`) and the frame with its credit line
  (`ToolHero`).
- **Update in the list**: a gallery asset on the avatar shows when the gallery has a newer version for it (its version
  pill turns green, "1.0.1 → 1.0.3") and an **Update** button installs it in place of the old one, with its progress and
  a Retry if it fails, like the gallery page's cards (both use `GalleryInstaller.Update`).
- Gallery packages of props that Toolkit fits to the avatar (the drawing pen, the hand screen) declare the Toolkit
  version they need, also when the prefab refers to none of Toolkit's files.

## 0.9.6 — 2026-10-08

- **Face tracking quick settings, simpler**: they fold into the face tracking frame (open right after the setup) and start
  with four big buttons: **Meta Quest**, **Vive (SRanipal)**, **Apple (ARKit)** and **Custom**. A tracker keeps only what
  it sends, from VRCFaceTracking's compatibility table (an iPhone has no pupil dilation, lip tighteners or tongue
  directions; a Vive has pupils and the tongue in every direction but no brows, nose, lip press or lip tighteners; a Quest
  Pro has no pupil dilation and its tongue only sticks out), with the recommended smoothing and the expressive mouth.
  **Custom** shows every setting: the smoothing, each feature others see (Nose, Lip press, Lip tighteners and Tongue
  direction are now their own switches) with the parameter budget, and the expressive mouth.
- **Expressive mouth on every avatar**: a face without its own grin shape grins with its cheek and eye squints when a full
  smile raises the upper lip; Rexouium and Ultirex faces keep their sculpted grin.
- **Test live in about a second** instead of a whole upload build: only the template's controllers are copied in memory and
  finished as at upload (the face's shape names, the mouth tuning), then MCB's corrective blendshape links of the custom
  base are applied to them, so fixes such as an Orbit Face eyelid fix following the blinks and the eyes looking aside show
  in the test. The avatar's own layers are left out. Changing the expressive mouth puts the face together again at once.
- At upload, the face's shape names are matched before MCB's corrective links run, so those links also follow the face
  tracking clips.
- **Avatar customization**: one compact frame replaces the Rexouium card and the Hair, Ears, Tail and Toes cards. A row per
  part with who can grab and pose it (three icons) and its stretch, an **All** row that sets every part at once, chains
  without physics with **Add physics** inline, and the avatar's looks below (Rexouium's feathers as chips, the ear size
  slider, double-click for the sculpted size).
- **Face tracking**: the quick settings are open by default; the frame shows VRCFaceTracking's logo in the shape ring, the
  template as "Adjerry91's face tracking template" with the installed templates' version, and **Get VRCFaceTracking** opens
  its install guide. Setting it up is now **Add** under "Face tracking available".
- **Smoother live test**: every frame the phone sends is shown (the face plays a moment behind, between the frames around
  that moment, so Wi-Fi bursts play out evenly) and the values VRChat receives follow every drawn frame instead of ten times
  a second. The corner shows the frames received and drawn; Unity draws less often while another app is in front.
- Face tracking counts as its own share in the Avatar budget (XRay Gizmos 0.2.9).
- The VRChat avatar card of the thumbnail now comes from Orbiters Toolkit, shared with Unit Git's upload releases.
- **Face tracking fixes**: the upload only edits VRCFury's copy of the FX controller, never the avatar's own asset; the
  template animates the face where it is now, also after the face mesh was renamed or moved (an MCB version), and the
  section shows **Check the face** with **Point at “…”** when the template's face is missing; blinks the avatar animates
  wait for eye tracking only on the way to closed eyes, no longer on the way back to rest; while expressions are disabled
  the expression layer stays at rest (menu toggles and Entry transitions included, no more flicker).
- The live test: no longer listed in the VRChat SDK panel, adds nothing to the Undo history, frees what each rebuild
  made, closes its ports and threads before scripts reload, and survives changing the expressive mouth while it builds.
  A pending one-click setup gives up after five minutes instead of waiting forever. The budget bar uses the same face
  tracking count as the estimate.
- **Restore** of a fitted accessory also takes back the fit MCB saved for the version, so it no longer returns after a
  version switch. Fit notes made for another custom base, or whose fit is gone, are asked again instead of failing. A
  refit finishing after the custom base changed is taken back; the line-up ends when the custom base changes.
- **Undo textures** and the optimization's undo skip renderers that no longer exist (MCB replaced them) and say how many,
  instead of failing. The MCB tile finds MCB on a child of the avatar.
- Requires Orbiters Toolkit 0.3.18 (and MCB 1.12.4 for the corrective links in the test and fits taken back).

## 0.9.5 — 2026-10-07

- **No more "This file was already uploaded"**: the VRChat SDK keeps a pending thumbnail through every reload of the
  avatar, even when its blueprint ID was cleared meanwhile (VRChat not finding the avatar for a moment). My Avatar now
  checks its thumbnail again whenever the blueprint ID changes and takes it back when VRChat already has that image.
- **Thumbnail check before the build**: when the thumbnail waiting in the SDK (from My Avatar or picked in the SDK) is
  the exact image VRChat already holds, Build & Publish stops at once instead of failing after the whole build and
  upload. The thumbnail is taken out of the upload; clicking Build & Publish again uploads the avatar.
- Gallery assets on the avatar no longer list texture slots or material repair: their creator set them up. Their
  picture shows props hidden until toggled on, such as the drawing pen.
- **Face tracking**: when the avatar's face has most of the blendshapes face tracking uses (Unified Expressions, ARKit or
  SRanipal, also under other names such as Rexouium's "LipTrack_JawOpen"), a Face tracking section offers to set it up in
  one click: Adjerry91's Face Tracking Templates (and VRCFury) are downloaded through VPM when missing and the template for
  that standard goes on the avatar. At upload, its blendshapes animate the face's own shapes and hand-gesture expressions
  pause while the face is tracked. The templates' files are never changed.
- Face tracking moves the mouth like the Ultirex's hand-made setup (OSCmooth) at upload: smiles and frowns are full at
  0.8 instead of 1 (VRCFaceTracking seldom reports more), smiles, frowns, the lower lip, jaw and tongue are smoothed
  like the original (70 % of the last frame kept, 30 % before), and a face with its own grin (Rexouium, Ultirex) turns a
  smile with the upper lip raised into that grin. Rexouium's lip tracking shapes take the original's weights (lower lip
  20 %, cheek puff 71 %, a pout also narrows the mouth), wide eyes raise the brows and the lids follow the eyes.
- **Face tracking, redesigned**: a hero with the face's shape coverage and the template, then quick settings stored on
  the template (one Undo step each, applied to the upload copy only): **Smoothing** (Responsive, Balanced, Smooth: the
  template's Local Smoothing to start with, and how much slower the mouth follows), **Others see** (eyes, pupils,
  brows, mouth, tongue, cheeks, with the parameter bits each costs against the avatar's 256: features left out lose
  their parameters and menu toggles in VRCFury's copy), and the **Expressive mouth** (the Ultirex tuning above, now
  optional). Every parameter budget (My Avatar, the gallery, the Toolkit) counts the features you keep.
- **Test live**, without Play Mode: the avatar is built like an upload on a hidden copy (VRCFury, MCB, My Avatar, the
  Toolkit, with a progress bar the first time; kept for ten minutes so testing again starts at once), then driven every
  editor frame by an iPhone or a simulator through VRCFaceTracking's own mapping (its iFacialMocap and Live Link modules,
  correctors, v2 parameters and binary bits) and the built FX and Additive layers, parameter drivers included. A face
  close-up turns with a drag. Pairing shows this PC's address with copy buttons and each app's steps, remembers the
  iPhone's address for iFacialMocap, detects which app is sending, and shows the frame rate, the last frame's age and a
  firewall hint when nothing arrives. "What VRChat receives" shows the values live. Stopping leaves nothing in your scene.
- **About** in the bottom toolbar: My Avatar's version and the projects it works with, each with its license and credits.
- **Materials**, folded under the texture optimization: the avatar's materials on Unity's Standard shader (VRChat suggests
  its Toon Standard) and open surfaces whose back is hidden (a tail of fur cards, feathers: many of their edges belong to a
  single triangle). Open surfaces show both sides by themselves as soon as My Avatar finds them (on copies, with Undo;
  a Standard material moves to Toon Standard for it); **Ask me first**, or Orbiters settings, lists them instead.
  **Use Toon Standard** rebuilds a material with its texture maps, **Fix all** does every suggestion. Transparent and
  cutout Standard materials stay as they are.
- **Rexouium** options in their own section below Posing: show or hide each feather group (what the avatar starts with in
  VRChat, shown in the scene too) and an Ears slider from small to big.
- Posing no longer lists a custom base's logic (MCB) or the face tracking template among the accessories to pose.
- Fix emission missing after a drop on materials locked with emission off (Poiyomi keeps only the features that were on
  when locking): texture slots now come from the material's original shader, so the hair's emission map lands on every
  hair material, and assigning it also turns on Poiyomi's emission toggle and strength.
- In a wide Inspector, the thumbnail photoshoot shows the card, Done, the shot and framing on the left and the pose,
  light, background, expression, effects and text options beside them.
- **Text on the thumbnail**: one line, right on the card. Hover the card in the photoshoot and press **Add text**, or use
  the Text tab: drag the text to move it (it snaps to the middle), drag its corner or scroll over it to resize it,
  double-click it to write. Pick among Unity's default font and eight fonts that ship with My Avatar (each tile shows
  your own text), a look (clean, outline, shadow or neon glow), colours that read well or are taken from the shot, and a
  size. Captured thumbnails have it drawn in exactly as on the card; each avatar keeps its text for next time.
- **Create ref sheet** is a small link under the thumbnail's buttons (and under Take pictures in the gallery's
  Pictures), hidden while the photoshoot is open: Done closes the thumbnail photoshoot and the ref sheet alike.
- The photoshoot gets the Toolkit's poses of your own: add humanoid animations from the project to the Pose tab.
- Requires Orbiters Toolkit 0.3.17.

## 0.9.4 — 2026-10-07

- **Pictures** in Publish to the gallery: take pictures of the avatar wearing your clothing or accessory with the
  photoshoot, shown live on the gallery card buyers will see. The first picture goes on the card, the others and ref
  sheets open on the asset's page (the website and My Avatar's details); add picture files too, remove them, put another
  on the card, or save a copy. They are sent with the asset when you publish. Needs the matching Orbiters server.
- **Create ref sheet**, under the thumbnail's buttons (and in the gallery's Pictures): the avatar from the front, the back
  and the side on one 1920×1080 sheet. In the Thumbnail section it is saved beside the thumbnails and shown in the
  Project window.
- The thumbnail photoshoot gets the Toolkit's new effects, backgrounds of your own and environment light.
- Requires Orbiters Toolkit 0.3.16.

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

1. Install **My Avatar** from the Orbiters VPM repository. Orbiters Toolkit
   is a required dependency; Unit Git 0.1.3 or newer is optional. The VRChat avatar SDK removes this editor-only
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

## Optimization

The **Optimization** section shows the rank the VRChat SDK gives the avatar now, a ring with how much lighter the upload
downloads than the avatar as it is (0% with optimization off), and three tiles. Every figure is for the platform the
project builds for: PC, or Quest when the build target is Android (switch the platform to see the other).


- **Small**: the uploaded copy loses what nothing shows or uses, polygons that move the surface 4 mm at most, and its
  textures are sized to stay sharp from 1 m.
  - Objects that start hidden and that no animation shows, with everything in them, unless something outside them points
    in (a visible mesh's bones, the descriptor's eyes) or they hold a humanoid bone. VRChat ranks hidden objects as if
    they were on.
  - Components that start disabled and that no animation turns on: PhysBones, PhysBone colliders, contacts, constraints
    (VRChat's and Unity's), lights and renderers.
  - PhysBones that move nothing: no vertex weighted to their chain, no renderer or other component on it, no animator
    reading their parameters.
  - PhysBone colliders no PhysBone lists; PhysBones set to follow animations whose bones nothing animates stop doing so.
  - Blendshapes nothing animates (animations, visemes, eyelids, the standard MMD morphs on Body), and those animations
    only ever set to the weight they already have, baked in at their weight.
  - Bones a skinned mesh lists without any vertex weighted to them (VRChat counts every listed bone).
  - Empty submeshes, and submeshes of one mesh sharing an opaque or cutout material (drawn as one slot). Renderers whose
    materials an animation swaps keep their slots.
  - Textures a material points at that its shader doesn't declare (left by an earlier shader, or a feature a locked
    Poiyomi shader compiled out): they upload and count for nothing. This runs after Poiyomi locks its materials.
  - The same image imported twice under two names (the same file bytes and import settings for the platform): the
    materials get copies pointing at one of them, the one most of them show already.
  - Every skinned mesh that moves with the hips gets the same culling box: around all of them as they stand, from the
    hips, with 30% of the avatar's size of room on every side (raised arms, kicks, sitting). Meshes below a constraint
    (a prop that can be dropped in the world) keep their own.
- **Big**: Small, and
  - skinned meshes that are always shown (never hidden or switched off by an animation, nothing animating or pointing at
    them) join the one animations use, or the largest; its path, blendshapes and material slots stay first, so its
    animations keep working. Meshes drawn differently (shadows, probes, quality, layer) stay apart, and so does a mesh
    whose materials have a property the merged renderer's animations set (it would reach them too). Static props
    (mesh renderers always shown, nothing animating or pointing at them) join too, skinned to their own transform,
    which then folds into the bone it hangs from like any bone that never moves.
  - meshes every animation shows and hides together become one: their toggles (their objects' or their own) have the
    same keys in every clip and the same state at rest, so the merged mesh, under its own toggles, shows and hides
    exactly as they did. Their animated blendshapes join it (renamed when the name is taken) and the build's copies of
    the animations move their curves onto it. Meshes whose materials an animation swaps, or that something points at,
    stay apart.
  - polygons within 8 mm instead of 4, and textures sized to stay sharp from 3 m instead of 1 (see below).
  - textures upload crunched (colour and masks, quality 75; normal maps keep their format): crunch shrinks the download,
    not memory. Metallic, roughness and smoothness maps get half the size the others get.
  - the menu's icons upload at 64 px at most: the menus holding a larger one are copied (the menu assets stay as they
    are), and point at copies of the icons compressed for the platform (BC7, ASTC 6x6 on Quest).
  - objects and the parameters only animators use get the shortest names (a, b, ... aa): the build's copies of the
    animations follow (paths, masks, conditions, blend trees, parameter drivers, audio), and so do PhysBones and
    contacts. The bones of the humanoid's model (the Animator binds them by name), Body (MMD dance worlds animate it),
    VRChat's own parameters and those of the expression menu (OSC and saved values use them) keep theirs. It saves a
    few kilobytes.
  - the body under clothing worn all the time goes: each body vertex, posed as now, casts rays outward (its normal and
    eight directions around it); one every ray stops on clothing within reach of (8% of the avatar's height) is hidden,
    and a triangle whose three vertices are hidden goes. Skin near a collar, a cuff or a hem keeps a way out and stays.
    Clothing counts when it is always shown, its opaque materials only (cutout and transparent ones show the body), and
    when its animations change nothing but blendshapes the body has too. A body shape pushed through clothing that
    doesn't follow it can show a hole there.
  - bones that never move against their parent (nothing animates, simulates or constrains them, not humanoid, nothing
    but meshes, colliders and contacts points at them: clothing armatures under the body's bones, attachment bones)
    are skinned through the bone they hang from instead, with a bind pose that keeps every vertex in place: fewer bones
    counted. The transforms stay.
  - bones that only end a PhysBone chain become its endpoint position (only when every end sits at the same offset).
  - a Quest (Android) upload gets its textures in ASTC (6x6 for colour and normal maps, 8x8 for masks and other linear
    data) at half the PC size, at most 1024 px.
- **Custom**: a switch per change, and the **Textures** and **Polygons** sliders.

Click the chosen tile again to turn optimization off. The setting is saved on the component and runs when you build or
upload (structure at build step -1100: after VRCFury, Modular Avatar and MCB, before d4rkAvatarOptimizer and
AvatarOptimizer; materials at 9000, after Poiyomi locks). Play mode (Gesture Manager, Av3Emulator) keeps the avatar as
it is. The console says what each upload removed. The changes to objects and meshes build on one another, and some
rewire several objects and their animations at once: one that fails part-way stops the upload rather than upload a
half-changed avatar (the console and the **At upload** estimate say which; turn it off under **Custom** to upload). A
material or texture change that fails leaves the materials as they render now, and the upload goes on.

Under the tiles, **At upload** estimates the ranks of the uploaded copy: a hidden copy of the avatar (never in your
scene) goes through the same changes and is measured with the SDK, then each stat that changes is listed with what sets
the rank (for example "Very Poor from polygons"). Texture memory is counted as the upload's copies (in their sizes and
formats) and, for textures the upload keeps, in the formats the build will have (their Android settings on Quest).
VRCFury and Modular Avatar haven't built on the copy: what their components point at counts as used. With
optimization off the panel shows the avatar as VRChat sees it now.

**Download** comes first, drawn as VRChat's loading bar over an avatar that starts downloading ("0% / 16.8 MB", its
darker stripe drifting left as it does while it waits): the size of the file players download, estimated without
building. Each texture is measured as the bundle stores it: its data in its build format (its Android settings on
Quest), or the upload's copy when the upload resizes or compresses it (made then, kept in `Library`, and reused by the
upload), compressed with Deflate and scaled to what LZMA makes of it (ratios by format and size, measured on
MasculineCanine's textures one bundle each: within 5% each, 0.2% in all). Each mesh counts with its vertices, triangles
and the vertices each blendshape moves, plus animations and shaders, with ratios from real bundles. VRChat shows an
avatar's size about 5% under the bundle the SDK uploads (MasculineCanine: 17.7 MB built, 16.8 MB in game), and so does
the bar. A build of MasculineCanine at Big was 15.3 MB (14.5 MB as VRChat shows it); the estimate said 14.5 MB. Until
its textures are measured (a moment after the section opens), they are estimated from their source images. Blendshapes
count as the build keeps them: MCB's custom base meshes lose the shapes nothing animates at every upload, and what
VRCFury and Modular Avatar will animate counts as used. VRChat refuses an upload whose bundle is over 200 MB (10 MB on
Quest), or over 500 MB unpacked (40 MB on Quest): the caption turns orange and says which. These limits apply to the
bundle itself, not the 5% smaller size VRChat shows. The ranks count texture memory in the formats the build will have
(the upload's copies included), not as the editor holds it (often uncompressed).

When the estimate fails, the card says why and keeps it until something changes or you click **Try again**: nothing
measures it again behind your back.

Under the bar, how long the avatar takes to show once downloaded: unpacking (LZMA), loading its assets and setting it
up (animator layers, PhysBones), measured on MasculineCanine on a desktop PC (bundle opened in 830 ms, assets loaded in
110 ms). A Quest takes about four times longer.

### Polygons

Each mesh of at least 2,000 triangles is simplified as far as its surface stays within the budget: 4 mm for **Small**,
8 mm for **Big**; under **Custom**, the **Polygons** slider goes from half a millimetre (**Identical**, half a pixel seen
from a meter away) through 1, 2 and 4 mm to 8 mm (**Lighter**). The triangles any blendshape moving less than a quarter
of the mesh moves (expressions, visemes, small toggles) are kept exactly as they are; broader shapes (body types, muscle
flexes, pose correctives) are carried through. Each try is measured: 99% of the original points within the budget and
none beyond twice it, at rest, as the avatar stands now and with each broad shape at full weight; and nothing folds (one
point in 500 at most shades over 60 degrees differently: a fold or a flipped triangle shows even where it stays close).
Borders, seams, weights and material slots are kept. The budget is measured on the avatar through each mesh's bones and
bind poses (a mesh imported at scale 100, switched off at rest or given a larger culling box loses what it would at
scale 1, shown, with its own box). The estimate simplifies one mesh after another in the background; a mesh is known
by everything it holds (vertices with their UVs, normals and weights, triangles, bind poses, blendshape offsets), so one
changed in Blender is simplified again. On MasculineCanine, the blendshapes its expressions and correctives
use lock most of the body, so the gain is small: at 8 mm its upload loses 506 polygons, invisibly (renders of the
original and the result differ on 0.06% of their pixels). Meshes without expressions (clothing, props, hair) gain more.
The estimate counts every blendshape as used (it can't see what build tools will animate) and says about 1,000 for
MasculineCanine.

The simplifier is **AlterMesh** (`Orbiters.AlterMesh`, in Orbiters Toolkit), derived from
[Meshia Mesh Simplification](https://github.com/RamType0/Meshia.MeshSimplification) 3.2.0 by Ram.Type-0 (MIT, credited
in the About window). Changed from Meshia: blendshape normal and tangent offsets are no longer normalized at each
collapse (it smeared their shading), submeshes left empty no longer crash it, zero-length bit counts no longer throw,
and a simplification cancelled (before a script reload) or failing frees its native memory at once.

### Textures

The upload's textures are sized to stay sharp from a distance: 1 m for **Small**, 3 m for **Big**; under **Custom**, a
slider under **Textures sized to the distance** goes from 25 cm to 4 m. A texture keeps a texel for every screen pixel from there, about 1,000
pixels across a meter of surface seen from one meter in today's headsets. Each texture's size comes from how much of the
avatar it covers: its UV area against the surface of the mesh as it stands (tiling counted), on the use that needs it
most. Textures read by direction (matcaps, reflections, rim) get 512 px at 1 m; textures that can't be measured (no
mesh, no UVs) get 2048 px (the body and face) or 512 px at 1 m, scaled with the distance. Nothing goes below 128 px, and
a texture is **only ever made smaller**: one already smaller than the distance needs keeps its size.

Each changed texture uploads as a copy in the format with the best quality per byte of texture memory: BC1 for colour
whose alpha is unused (measured on the pixels, not taken from the importer), BC7 when alpha is used, BC5 for normal maps
(DXT5nm and crunched ones keep their layout; one already in BC5 at its size isn't encoded again), with mipmaps and
mipmap streaming, not readable (a readable texture keeps a second copy in memory in game). A crunched texture stays
crunched with its quality. Halving metallic and roughness maps halves the size they would have, with or without
**Textures sized to the distance**. On Quest a texture keeps its Android format unless **Quest textures** is on (crunch
on Android or iOS is ETC); an iOS project reads the iPhone import settings. Ramps, lookup tables, gradients, SDF maps,
very thin strips, HDR images, render textures, sprites and textures that are not plain imported images are left alone,
and so is anything that would take more memory. Only the texture changes (sizes, Quest textures, crunch, smaller
metallic and roughness) touch textures: under **Custom** without them, every texture uploads as it is. The same image
imported twice gets the size the most demanding of its uses needs, since one of them takes the others' places.

The copies are made from the image file itself when Unity's importer only passes its pixels through (PNG and JPG colour
and mask images: nothing is compressed twice), else from the texture as imported, never scaled up (a texture the editor
holds smaller, through mipmap streaming or Build Settings' size override, uploads as it is). They are halved on the GPU
step by step; their mips keep a cut-out's coverage when the import does (**Mip Maps Preserve Coverage**), then they
are compressed and put on copies of the materials that show them, the materials and textures animations swap in
included; the build's copies of the animations point at them. Every copy is made before anything changes: one that
fails leaves the upload's textures as they are. Your textures, materials and import settings never change, so there is
nothing to undo. Copies are kept in `Library/OrbitersMyAvatar/UploadTextures` by the image's contents and the copy's size
and format (up to 1 GB, those used longest ago go first), so the next upload reuses them; the estimate keeps only the
upload's own there. **At upload** shows the texture memory (VRAM, as VRChat ranks it, not download size) before and
after, and the download counts the copies with every other change.

### d4rkAvatarOptimizer

Merging meshes that use different materials needs rewriting their shaders: the section points to
[d4rkAvatarOptimizer](https://github.com/d4rkc0d3r/d4rkAvatarOptimizer) for that (it runs after My Avatar at upload),
offers to install it, **Add to avatar** when it is installed but not on the avatar, or notes that it is disabled. My
Avatar is not associated with d4rkAvatarOptimizer.

## Checks

**Check** builds a hidden copy of the avatar the way an upload does: the VRChat SDK's build steps in its order (VRCFury,
Modular Avatar, MCB, face tracking, the menu editor...), all at once as at upload, for the current build target, in its
own preview scene. The editor pauses while it builds, as it does at upload; **Cancel** on its progress bar stops the
check and brings the last results back. Left out: Poiyomi's material lock and the build cache (they write to the
project), My Avatar's optimization (it renames objects and parameters) and VRCFury's parameter compressor (it reshuffles
the synced parameters and may ask whether to), so issues name things as you made them. VRCFury doesn't ask about Write
Defaults on the copy: they stay as they are. What VRCFury would stop the build on with a question (more than 256
contacts or 8192 expression parameters, desktop shaders with Android or iOS as the build target) is reported as an
**Upload** problem instead, without building; when the other platform's version has one, the Quest group says it
couldn't be compared. The copy is closed once checked and what its build wrote (VRCFury's build folder, the build cache)
is deleted; your avatar never changes. Each group shows "Nothing to fix" or its problems (red), warnings (orange) and
notes (grey), each with what happens in game, how to fix it and where it is. When the avatar changes since (its
objects, any component, its menus, parameters, controllers, clips, materials, meshes or textures), the result says to
check again.

- **Menu and parameters**: menus with more than 8 controls (the Action Menu shows 8: the others can't be reached),
  controls on a parameter that isn't an expression parameter (VRChat ignores them) or that no animator has (they move
  nothing), puppets moving a parameter that isn't a Float or one parameter with two axes, values a parameter can't hold
  (Ints hold 0 to 255, Floats -1 to 1), submenu controls without a menu, parameters listed twice (unless VRCFury, which
  removes them at upload, is installed) and synced parameters no animator reads (bits of the 256 sent for nothing).
  Sub-parameters a control keeps from an earlier type are ignored, as VRChat ignores them.
- **Animators**: in every playable layer, transitions, blend trees (a synced layer's own too) and states' speed, time,
  mirror and cycle offset reading a parameter their controller doesn't have (they never change), parameter drivers
  setting or copying a parameter no layer and no expression parameter has (they do nothing), and states nothing leads
  to, from the entry, Any State and the states reached, through sub-state machines.
- **Holes in the body**: a body shape that hides skin under clothing (Shrink_*, Hide_*) left on at rest while the
  clothing over that skin can be toggled off (its object or its renderer), nothing always worn covers it and no
  animation sets the shape back: with the clothing off, the skin shows a hole. Clothing elsewhere (a hat over a shrunk
  chest) doesn't count.
- **Quest**: for the version Quest players see (the avatar's Android version when it has one; else this one, when the
  build target is Android or iOS or most of its materials use VRChat's mobile shaders), materials whose shader isn't one
  of VRChat's mobile shaders, components Quest removes (Unity constraints, lights, sounds, cloth, physics, cameras, Final
  IK: the SDK's PC-only list), and a Poor or Very Poor rank on Quest. A PC-only avatar gets a note instead. The other
  platform's version (its per-platform override in the SDK's build panel, or the first avatar in the open scenes with
  the same blueprint whose materials are made for the other platform; copies for the same platform don't count) is
  built too and the synced parameters compared: VRChat matches them between versions by their place in the list, so
  both need the same ones, of the same types, in the same order. VRCFury puts the Quest version's in the PC version's
  order at upload, once this computer uploaded the PC version: then only what the order can't fix is a problem.

## Export to VRM

**Export…** builds a hidden copy of the avatar the way an upload does (as **Check** does), then writes it as a .vrm file
with UniVRM; the avatar, its assets and their import settings never change. The **v0.x** and **v1.0** tiles pick the VRM
version: 0.x is what most apps read, 1.0 the current standard (springs per chain, capsule and plane colliders).

- **The build**: every VRChat build step runs (VRCFury, Modular Avatar, MCB, face tracking), with your Optimization
  level, without what only VRChat uses (short names, menu icons, crunch and Quest textures: VRM stores PNG and JPEG).
  Objects and renderers that start off are left out: a VRM shows everything it holds.
- **Expressions**: VRM's vowels from the descriptor's visemes (aa, ih, ou, E, oh; jaw flap opens the mouth for all),
  blink from its eyelids or the face's blink shape, one-eye blinks and the emotions (happy, angry, sad, relaxed,
  surprised) from the face's shape names, and **perfect sync**: an expression named after each of ARKit's 52 shapes the
  face has, found under ARKit's name or another face tracking standard's (Unified Expressions, SRanipal), which
  VSeeFace, Warudo and VNyan drive from an iPhone. The build keeps those shapes even when nothing in VRChat animates
  them; every other shape leaves the meshes (baked in at its weight), and morph targets store only the vertices they
  move.
- **Springs**: each PhysBone becomes springs, one per chain (a branch starts a chain of its own, VRM 1.0 adds an end
  node where the last bone points). There is no exact match between the two: pull and stiffness make VRM's stiffness
  (VRM's usual 1 for PhysBone's usual 0.2), spring (bounce) lowers the drag, gravity carries over, radius scales with the
  bone. Colliders follow (spheres; capsules as a row of spheres in 0.x; planes and inside colliders in 1.0 only).
- **Materials**: each becomes MToon with its main texture and colour, a shade colour a little darker, its normal map,
  emission when it is on, cutout or transparency from its render queue, and both sides drawn when it culls nothing.
  Toon shading settings beyond that (rim, matcap, outlines) aren't carried over.
- **The rest**: the T-pose UniVRM uses, the head's view point from the descriptor, look-at from the eye bones, the
  thumbnail from the Thumbnail section, and UniVRM's checks and normalization. UniVRM's own exporter switches every
  texture it copies to uncompressed and leaves it so; My Avatar's reads copies instead.

The form says what the file says about the model: its name (the avatar's by default), version, authors (required),
contact and reference, who may use it as an avatar (only you, people you allow, everyone) and whether commercial,
violent or sexual use, redistribution and modification are allowed, all off by default. Many bases forbid converting
them to other formats: **The model's license allows VRM** must be on before Export. The file's license in VRM 0.x
follows the switches (redistribution prohibited, else CC BY with NC and ND as they say).

## Substance 3D Painter link (beta)

The Painter card shows under Export to VRM when Substance 3D Painter is installed (Adobe's installer or Steam). It is on
by default; turn it off in **Settings** (bottom toolbar) › Features › My Avatar › Substance 3D Painter link.

- **Open in Substance** poses a hidden copy of the avatar for painting (arms level, legs apart, fingers spread, the mouth
  open, centered for Painter's symmetry, with copies in natural poses if you want them) and Painter makes a project of
  it: a texture set per material, the avatar's textures in a "From Unity" layer, its normal maps as the baked normals.
  The avatar in your scene is never posed.
- **Live**: what you paint shows on the avatar in Unity a moment after each stroke, through the renderers' property
  blocks (never saved, never in Undo). **Saving in Painter** applies the maps you painted to the avatar's materials as
  a texture drop does, with Undo. The card maps each texture set to the material it paints.
- **Mesh updates**: when MCB applies a Blender export (or shows Blender's edits live), the project Open in Substance
  made gets the avatar's new mesh, posed as it was made, the strokes kept on the surface. A project you linked from
  Painter's My Avatar panel keeps its own mesh.

What it installs, runs and writes:

- **A Python plugin in Painter**: installing it (the card's button, Open in Substance or Open the project) copies
  `orbiters_my_avatar.py` into Painter's startup folder (`Documents/Adobe/Adobe Substance 3D Painter/python/startup`).
  Painter runs it at every start and reloads it when My Avatar installs a newer one; it adds a **My Avatar** panel.
  Delete the file to remove it.
- **A listener on this computer**: once the plugin is installed, each Unity editor listens on 127.0.0.1 (a port the
  system picks; nothing from outside the computer reaches it) and writes its port, a random token and the names of the
  avatars in its open scenes to `%LOCALAPPDATA%/Orbiters/PainterLink/unity-<pid>.json`, deleted when Unity quits. Painter
  links only to the Unity editor listening on that port, has it prove it holds the token before sending it, and proves
  the token itself before Unity reads anything it sends.
- **Files**: live images (`live/`), saved maps (`commit/`, deleted once applied), what projects are made from
  (`projects/`) and mesh updates (`meshes/`), all under `%LOCALAPPDATA%/Orbiters/PainterLink` (on macOS
  `~/Library/Application Support/Orbiters/PainterLink`), cleared once per Unity session of what nobody needs (applied
  exports, live images of Painters that closed, project sources and meshes older than a week, old jobs and endpoint
  files). Projects are saved in `Documents/Orbiters/Painter/<Unity project>/`; saved maps are imported into
  `Assets/Orbiters/MyAvatar/<batch>/Painter`. The avatar keeps its project's path from your home folder (`~/Documents/…`).

## Materials that don't render

An avatar bought for another shader, or for another version of it, often renders pink: its materials name a shader
that is not in the project. My Avatar lists them first in the **Materials** card (which opens by itself once) and
reads what each one was made for from what it saved: its shader GUID (lilToon's variants keep theirs across versions),
Poiyomi's markers and the original shader of a locked material, lilToon's version, and VRChat's fallback tag.

- **Its own family is installed**: it moves back onto it with every saved setting. A locked Poiyomi material whose
  generated shader is missing is unlocked onto the shader it was locked from (its version under Poiyomi's Old Versions
  when that is the one), the way Poiyomi does it; Poiyomi locks it again when you upload.
- **Another family**: Poiyomi Toon if installed, else lilToon, else VRChat's Toon Standard (opaque materials), else
  Standard. It keeps its texture, color, normal map, emission, culling and blending (opaque, cutout or transparent); from
  lilToon to Poiyomi (and back) also its shadow colors, lilToon-style rim light and backlight, which both shaders share.
  Each row says what is not carried over (outline, matcap, 2nd texture layer...). Installing the original shader
  instead keeps everything: the card offers lilToon or Poiyomi Toon from VPM when one of them is what is missing.
- Particles, trails and lines are only moved within their own family; otherwise the row asks for a particle shader.

**Fix materials** fixes them all in one Undo step. Materials in `Assets` are changed where they are, so every avatar
using one renders again, and each original is kept beside it as "name (old).mat" (its own GUID, labelled
`MyAvatarBackup`, never listed as broken itself; it renders again as made once its shader is installed); materials inside a package or a model are copied to `Assets/Orbiters/MyAvatar/MaterialEdits/`
and assigned to this avatar's renderers only. Fixing again after an Undo uses the same "(old)" backup when it holds the
same original (an original imported again since gets "name (old) 1.mat"), and the copy the Undo left unused; a fix that
fails midway puts everything back and removes the files it made. The Console lists what changed. The `myavatar_materials` MCP tool
(`inspect`, `fix`) does the same on any scene avatar, with or without My Avatar on it.

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

VRChat refuses an image identical to the avatar's current thumbnail, and only says so at the
end of the upload. My Avatar compares its thumbnail with VRChat's before offering it, again
whenever the blueprint ID changes, and takes it back if VRChat already has it. If the thumbnail
waiting in the SDK is still a duplicate when you click **Build & Publish**, the build stops at
once, the thumbnail is taken out of the upload and the next click uploads the avatar.

## Posing

Three switches for posing the avatar in the Scene view (hover one for a short
explanation):

- **Bones** draws the avatar's bones over the Scene view; click one to select it, then
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

## Avatar customization

Hair, ear, tail and toe PhysBones are recognised by name (`Hair_Front`, `Ponytail`,
`Tail1`, `Toe_L`, toe beans...), from the bone they start at or the object holding
them. Each part is a row of the **Avatar customization** frame (its PhysBone count selects them), with an **All**
row on top that sets every part at once:

- **Grab** and **Pose**: Nobody, Only me or Everyone icon buttons, written to the PhysBones'
  grab and pose permissions. VRChat has no friends-only setting. Posing is limited to
  who can grab. A picker outlined in orange means its PhysBones disagree.
- **Stretch**: a slider for how much longer the chain gets when pulled (PhysBone Max Stretch), up
  to three times its length.

A choice applies to every PhysBone of the part and can be undone. Bones of the
avatar's own armature named like a part but driven by no PhysBone are offered under
**Add physics**, which adds a PhysBone per chain under `PhysBones/<Part>` on the
avatar, with settings suited to the part and the part's current permissions.

Under **Looks**, a Rexouium shows its feather groups as chips (what the avatar starts with in VRChat, shown in the scene
too) and its ear size. A hidden feather group is left out of the upload with its menu toggle, its meshes, materials and
textures with it. A Novabeast shows its chest fluff, collar, goggles and mane as chips and its breasts and tail size as
sliders. A hidden part stays hidden in VRChat: its toggle leaves the menu and its synced bit is freed (it is a shape of
the body mesh, which stays). A slider sets where it starts in VRChat; the upload starts from the avatar's own pose.
Looks change this avatar alone: when its expression parameters are shared (with another avatar in the open scenes,
another prefab of its package such as its other variants, or they sit in a package), the avatar gets its own copy under
`Assets/Orbiters/MyAvatar/Parameters/` first. Undo and Redo show every chip and slider as the avatar is.

## Avatar budget

Orbiters Toolkit shows where the avatar stands against VRChat's limits in a panel over the Scene view: synced
parameters as built (after VRCFury's compression), bones, PhysBones and contacts, with a custom base's share.

## Tools

Buttons for tools that go with My Avatar, each marked **Install** until it is in the project:

- **Gesture Manager** (VRChat curated repository): installs the latest version when missing,
  then puts its Gesture Manager object at the scene root, or selects the one already there.
- **Unit Git**: opens Unit Git, after installing it when missing.

An install shows what it downloads first (each package, and the VPM repositories it adds to your Creator Companion,
which every project uses), then goes through VPM once you accept; the tool opens once Unity has reloaded.

## Menu

The **Menu** card opens **Edit menu**: the avatar's Expressions menu as VRChat will show it once uploaded, instantly and
without building anything. It starts from the menu on the avatar descriptor. With Modular Avatar in the project, its
build comes first: Menu Installers add their menu or the Menu Items, Menu Groups and Install Targets on their object
(to the main menu or the menu they name), and a menu of more than 8 items keeps 7 and continues in **More**. Then
comes what VRCFury's build does: Full Controller menus merge in (folders of the same name merge), SPS adds its folder
(the socket toggles, Stealth, Auto and Dual Mode, the DPS Tip Light), Toggles, Puppets and Gesture locks add their items
at their menu paths (creating folders), Move Menu Item and Set Icon run, items are sorted by the component that added
them, Reorder Menu Item runs, and a menu of more than 8 items continues on a **Next** page (with Override Menu Settings'
text and icon). MCB's sliders, GoGoLoco and My Avatar's face tracking are VRCFury components, so they show too. The menu
follows the avatar live: change a toggle or a menu asset and it updates.

- The **Quick Menu** and the **Action Menu** show side by side, on the same page: an edit in one shows in the other at
  once. The Quick Menu is the Expressions page of VRChat's Quick Menu, measured from the game (Reset Avatar first on the
  main menu, two columns of tiles, each item's type as a badge, long names shrunk to fit). The Action Menu is Gesture
  Manager's radial menu (BlackStartX, MIT License).
- Click a folder to open it; the breadcrumb or back goes up. Click an item to see what it is and where it comes from.
- **Select**: tick the small box at an item's top left (in either menu) to select it without opening it, folders
  included; several items can be ticked. One ticked item shows its name and icon beside the menu; with any selection,
  **Create folder (submenu)** puts the items in a new folder at the first one's place.
- **Rename**: double-click an item (or F2), or type in the side panel.
- **Icon**: drop a picture on an item, from the Project window or from your computer, or choose one in the side panel.
  A picture from your computer is imported on Save into `Assets/Orbiters/MyAvatar/Menu Icons/` with the settings VRChat
  wants for menu icons (at most 256 pixels, compressed, no mipmaps).
- **Settings**: the side panel shows the selected item's type, parameter and value, what it starts at when the avatar
  loads, and whether its parameter is saved and synced, and changes them. They are written on Save to your own VRCFury
  Toggle (toggle, hold button or slider, its default, saved) or to the avatar's own menu (type, parameter, value) and
  parameters asset (default, saved, synced), copied first like the menus when something else uses them, in the order
  they were made: a default set after picking another parameter goes to that parameter, and a toggle made a slider gets
  its slider's starting point. Values stay within what VRChat keeps (whole numbers 0 to 255, decimals -1 to 1); syncing
  one more parameter is refused past 256 synced bits unless VRCFury compresses them at upload. A VRCFury Full
  Controller's items (VRCFury gives its parameters their own names at upload), Modular Avatar's and a package prefab's
  say their settings stay as their creator made them.
- **Leave out**: the red cross at an item's top right (or **Leave out** in the side panel) takes it out of both menus on
  pointer down. Left-out items wait under the Action Menu, in **Left out of the upload**; a click puts one back where
  it was. A place filled up since goes on over a "Next" page: VRCFury splits it, and without VRCFury a "Next" folder
  (with the arrow) takes the eighth place, holding the place's last item and the one put back. When even that can't be
  done, the editor offers up to six folders with room.
- **Remove default menu** (in the toolbar, or on the Menu card): leaves out the avatar's own menu, the descriptor's and
  its submenus, keeping what VRCFury, Modular Avatar, MCB and Orbiters add. It shows as one chip under the Action Menu.
- **Move**: drag an item between two others to move it there, onto a folder's middle to put it inside (hover it to
  open it and drop deeper), onto **Next** for the next page, or onto back or a breadcrumb to move it up. Released in
  place, outside the menu or anywhere it can't go, it goes back and nothing changes.
- **New folder**: hold a dragged item on another item's middle (its icon in the Action Menu) until the target lights up,
  then release: a new folder takes the target's place with both inside, named in place ("New folder"). Saving makes it
  a new menu next to the avatar's menus (or in `Assets/Orbiters/MyAvatar/Menus/` when those are in a package) when it is
  in the avatar's own menu, else VRCFury's Move Menu Item moves its items to its path.
- Every change is a Unity Undo step (Ctrl+Z, Ctrl+Y or Ctrl+Shift+Z). Nothing is written before **Save**; **Cancel**
  forgets the changes. Closing the window with changes asks first.

**Save** writes each change where it belongs, in one Undo step:

- the avatar's own menu assets are edited (names, icons, order, items moved between them); a menu or parameters asset
  from a package, or used by anything else (another avatar, a prefab, another scene, a menu outside the avatar's, a Full
  Controller), is copied into `Assets/Orbiters/MyAvatar/Menus/` first and the copy put in its place. A new folder in the
  avatar's menu is a menu of its own even when it only holds what VRCFury moves into it. Copies, new menus and imported
  pictures a save taken back with Ctrl+Z leaves behind are deleted at a later Save, once nothing uses them;
- your own VRCFury Toggles and Puppets (in your scene, not from a package's prefab) get their new menu path and icon,
  and your own Modular Avatar Menu Items their new name (label) and icon;
- everything else (Full Controller menus, prefabs from packages, the folders VRCFury makes for menu paths) is left as it
  is and changed with VRCFury's own Move Menu Item, Set Icon, Reorder Menu Item and Override Menu Settings, on one
  object at the end of the avatar, **Menu edits (My Avatar)**, rewritten at every Save.

Then, like the texture Save, it saves the assets and the avatar's scene (with Unit Git, also a local "menu change"
checkpoint; nothing is pushed). Ctrl+Z still puts the menu back as it was.

Left-out items and **Remove default menu** are kept on the My Avatar component; the menu assets don't change. Each
left-out item is kept with what it is (its type, parameter and the component behind it): a new item of the same name in
its place stays in, and one missing for now (an accessory left out of the build) stays saved and is left out again once
it is back. What goes is worked out before Modular Avatar builds, so **Remove default menu** keeps Modular Avatar's items
and items on its **More** pages are found; only VRCFury's own pages are followed as pages (a folder of yours called
"Next" is a folder). They act at upload, on the build copy after VRCFury: the items go (a folder left empty goes too, and pages close up), and so does
what only they switched. Each parameter only they set keeps its default, freeing its synced bits, unless an item still
in the menu sets it; conditions on it are decided, states and blend tree branches that can no longer play are removed,
a layer left playing one constant pose is written into the avatar and removed, objects left hidden for good are deleted,
and materials, textures and animations nothing uses any more stay out of the upload; the parameter budgets (Avatar budget,
My Avatar) count those bits as free already (not those a submenu another folder still opens keeps, nor those of a
toggle another of its exclusive tag switches off). Parameters apps set over OSC (heart rate, chatbox and text displays,
OSCmooth, VRCOSC, by the names those apps use) are never fixed. What the menu didn't switch keeps
working (the Rexouium's petting still answers to its contacts: only its menu switch goes). The Console sums it up after
each upload ("[My Avatar] Menu: ..."). An object only moved out of sight rather than switched off stays, like the
Rexouium's credits plane, which rests shrunk inside its chest once its Credits item is gone.

Orbiters' toggles, sliders and folders without an icon get a white dot, a gauge or a folder (64 × 64 PNGs in
`Assets/Orbiters/Menu Icons/`), and VRCFury's **Next** pages a white arrow unless Override Menu Settings gives them an
icon.

Before keeping anything, Save works out the menu again from what it wrote: when VRCFury would not build exactly the menu
you edited, or the settings written are not the ones edited, nothing is kept and the editor says why (closing the window
with **Save** then keeps it open). VRCFury finds items by name, so two items with the same name in one
menu can't trade places; rename one first. A folder that opens the same menu as another folder (a submenu used twice,
or one looping back to a menu above it) moves like any other item when it is in the avatar's own menu assets, and keeps
opening that menu. Such a folder that VRCFury or Modular Avatar adds (a Full Controller's or an installer's) stays where
it is: VRCFury's Move Menu Item would copy its menu instead of moving it.

## Face tracking

When the avatar's face (the mesh lip sync uses) has most of the blendshapes face tracking uses for its mouth, its eyes
(with the brows) or both, the **Face tracking** section offers **Add** (Face tracking, Lip tracking or Eye tracking
available). My Avatar works out which standard the face fits best: Unified Expressions, ARKit (Perfect Sync) or SRanipal,
also when the shapes are named another way (e.g. Rexouium's "LipTrack_JawOpen", "Eye_LookUp_L", "EyesWide_L"). Chips
under the text show each part: green with most of its shapes, amber with some, grey when missing (the tooltip lists the
missing shapes). A part without any of its shapes is left out of the upload's parameters (no bits spent on it) and comes
back by itself once the face has its shapes. One click then:

- when Adjerry91's Face Tracking Templates (or VRCFury) are missing, shows what it would download first: each package,
  and the VPM repositories it adds to your Creator Companion (every project uses them); once you accept, it downloads
  them and the setup finishes once Unity has imported them (the section says so, or why not);
- puts the template for that standard on the avatar, pointed at the face mesh (one Undo step).

When you upload, after VRCFury has merged the template: blendshapes it animates under the standard's names animate the
face's own shapes, the avatar's hand-gesture expressions pause while VRCFaceTracking drives the face (and return to rest),
and blinks the avatar animates itself wait while the eyes are tracked. The templates' files and the avatar's own
controllers are never changed. The template adds about 140 to 170 parameter bits; VRCFury compresses parameters when the
avatar goes over 256. **Remove** takes it off again.

The **Smoothing** setting chooses how the face follows the tracker, the way OSCmooth does: **Responsive** is the
template's, **Balanced** (the default) and **Smooth** are calmer. Each sets the local smoothing the face starts with
(0.25, 0.6 or 0.85; the template's Local Smoothing slider still changes it in game), how fast others see the face move
toward each value VRChat sends them, and how much slower than the rest the mouth follows. Smoothing stays on down to
10 fps. While lip tracking is on, the avatar's other hand-gesture layers play without their jaw, tongue and mouth curves,
so a gesture never pulls the tracked mouth (or sticks the tongue out) while the ears still move.

Face tracking blendshapes are animated by [Adjerry91's Face Tracking Templates](https://github.com/Adjerry91/VRCFaceTracking-Templates).
If you sell an avatar that uses them, their author asks for that credit, with the link, on your store or product page.

## Toolbar

The bottom toolbar opens **Settings** (the Orbiters server: production by default, or a
local development server, each with its own login), **Blendshape Links**, which
still ships with MCB and is available when MCB is installed, and **About**: My Avatar's
version and the projects it works with, each with its license and credits
(`Editor/THIRD_PARTY_NOTICES.md`).

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

The slot an image lands in decides how it is imported, whoever chose it (name matching, Orbiters AI or the slot menu): a
normal slot gets a normal map, colour slots sRGB colour, data slots (metallic, smoothness, occlusion, masks, height)
linear data. An image this drop copied in is converted in place; one used elsewhere gets a converted copy beside it, so
originals never change. A project texture already imported as a normal map counts as one even without "normal" in its
name. Images can also be dragged from installed packages, read-only ones included.

Only actual visible 2D texture properties on the current shader are considered,
including locked/optimized shaders such as Poiyomi's `Hidden/Locked/...` shaders.
A locked shader only keeps the features that were enabled when it was locked: if a
texture set member needs a slot the material lacks (for example emission), the
Inspector says so; enable the feature (unlocking if needed) and drop again.
Custom shaders can require their own feature toggles. A roughness image put in a
smoothness slot (or the reverse) is inverted into a generated map, and maps put in a packed slot (metallic beside
smoothness or roughness) are packed into one generated image that keeps the slot's other channels; dropped files never
change. A drop never changes UVs or switches shaders.

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
editing. Orbiters Toolkit draws and picks bones. Unit Git, when installed, owns Git
execution and scoped index handling; the `MYAVATAR_UNITGIT` version define enables
the commit step. My Avatar does not depend on MCB.
