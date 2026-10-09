# Blackglass Asset Studio v0.1

A Windows tool that imports FBX/OBJ files into the Blackglass Unity project with the right settings. It prepares a manifest and runs Unity in batch
mode; Unity's own APIs create every asset. It does not replace Blender. Design: `docs/superpowers/specs/2026-10-08-asset-studio-design.md`.

## 1. Build

Requires the .NET SDK 10 (`dotnet --list-sdks`).

```bash
dotnet build Tools/BlackglassAssetStudio/BlackglassAssetStudio.sln
dotnet test  Tools/BlackglassAssetStudio/BlackglassAssetStudio.sln
```

## 2. Run

```bash
dotnet run --project Tools/BlackglassAssetStudio/src/App
```

or start `Tools/BlackglassAssetStudio/src/App/bin/Debug/net10.0-windows/BlackglassAssetStudio.exe`. **Close the Unity Editor before importing**:
Unity allows one instance per project.

## 3. Configure the project path

Settings... > Blackglass project folder (the folder with `Assets` and `ProjectSettings`). When the app runs from inside the repository it
proposes that folder itself. The Unity executable is found from `ProjectSettings/ProjectVersion.txt` (Unity Hub folders); browse for it if
needed. Settings live in `%AppData%\BlackglassAssetStudio\settings.json`; run files in `%LocalAppData%\BlackglassAssetStudio\runs\<runId>\`
(last 10 kept). Nothing machine-specific is written into the repository.

## 4. Import a character

1. Prepare the FBX in Blender: Humanoid-compatible skeleton, T-pose, feet on the origin, applied transforms. Export in **metres** (apply the unit
   scale); see Troubleshooting if the file declares centimetres.
2. Drop it on the window. Profile: Humanoid Character. Check the name and destination (default `Assets/Art/Characters/<Name>`).
3. Set the target height (default 1.85 m), Validate, Import Selected.
4. Result: `<Name>.fbx` (Humanoid, Avatar), `<Name>_Visual.prefab`. Put the visual prefab under a gameplay unit's `Visual` child (the shell is not
   edited by the tool). An invalid Humanoid mapping fails the item with instructions and creates no prefab.

A sibling `<source name>.fbm` texture folder is copied next to the model as `<target name>.fbm` and follows the same ownership and overwrite rule
as the model. Files you later remove from the source `.fbm` stay in the project on re-import.

## 5. Height normalisation

Unity measures the model after import (posed mesh bounds) and the visual scale is `target / measured`. The scale goes on the `ImportedCharacter`
child inside the visual prefab; the prefab root and any gameplay root stay at scale 1, so colliders, NavMeshAgent, ranges and cover are not
affected. Stray geometry (hair, coat tails, floating parts) can skew the measurement: set **Manual visual scale** (it always wins) and import
again; the same assets are updated in place. Large corrections raise a warning.

The tool leaves the FBX unit settings untouched. A file that declares centimetres therefore measures 100 times too small (a fresh import of the
Darius model measures about 0.0188 m, because the project's own Darius `.meta` compensates with `globalScale` 100) and the automatic correction is
about x100 with a warning; the posed prefab instance still ends up at the target height. A model that measures under 0.01 m fails with an
"unusable measurement" error and needs a Manual visual scale.

## 6. Import a Mixamo animation

1. Download from Mixamo as FBX, preferably **Without Skin**, and use the same character skeleton as the shared Avatar. Keep "In Place" on for
   locomotion.
2. Drop it; the name usually picks Locomotion / Combat / Reaction / Death. Override if wrong.
3. Shared Avatar: a Humanoid model already in the project (default `Assets/Art/Characters/Darius/Models/Darius Stand Idle.fbx`, offered when present).
   For a whole set of clips use the **Character for animations** box instead (next paragraph).

**Many animations for one character.** Above the list, **Character for animations**: Browse to the character's model FBX (for example
`Assets/Art/Characters/EnemyUnit/Models/EnemyUnit.fbx`). Every animation in the list, and every one you add later, is then filled in for you:
asset name `<Character> <Action>` (`EnemyUnit-Crouch-Idle-Rifle.fbx` becomes `EnemyUnit Crouch Idle Rifle`), clip name `<Action>` without spaces
(`CrouchIdleRifle`), destination `<character folder>/Animations`, and the character model as the skeleton to copy. The action words come from the
file name: the character's own name at the start, Mixamo's `mixamo.com`, `Without Skin` and a `(1)` copy suffix are dropped. A field you edited by
hand is never overwritten. The profile (Locomotion, Combat, Reaction, Death) is still a suggestion from the name: check the **Loop** setting for
one-shot moves the name does not reveal (for example Fall, Stand-Ground, Stand-Crouch or a bare Crouch, which default to Locomotion and loop).
**Editing several at once.** Select two or more items (Ctrl-click, Shift-click or Ctrl+A) and the right-hand panel becomes **Edit N selected items**:
asset type, save into folder, allow overwrite and, when every selected item is an animation, the skeleton to copy, Loop and the three root-bake
checkboxes. A change applies to every selected item immediately and counts as a hand edit, so the Character box will not overwrite it. A field the
items disagree on shows empty (text boxes) or a filled square (check boxes); leave it alone to keep each item's own value. Asset name and clip name
stay per item. Changing the asset type resets that item's loop and root settings to the type's defaults; items already of that type are left as they
are. Import Selected and Remove act on the whole selection.
**Add Folder...** adds every model file under a folder. **Browse...** next to Save into folder, Skeleton to copy and Theme asset opens a picker
inside the project and fills in the `Assets/...` path; a pick outside the project is refused.
4. Defaults: Locomotion loops, all root axes baked (in place); Combat and Reaction do not loop, bake height; Death does not loop, bakes
   height and horizontal position. The FBX import settings carry these (no `.anim` copies).

Notes: Unity cannot skip meshes on import, so a skinned animation FBX keeps its mesh inside the animation asset. Unity 6000.3 gives the importer no
access to the model importer's own animation warnings, so retargeting warnings from Unity are not shown in the Result tab: after importing
animations, check the Unity console or the run's `unity.log` (Log tab) for retargeting warnings.

## 7. Import an environment module

Follow `docs/EnvironmentAssetGuide.md` (1 m grid, pivots, front +Z, 3 m walls). Drop the FBX, choose the **Element**. The importer measures it,
compares with the guide and lists mismatches as warnings; it never rescales unless you type an explicit scale. It writes a collider-free prefab. To
use it, choose "Register in theme" Append (or Replace + index) with the theme asset, or add it to the theme by hand.

## 8. Unity batch mode

The app runs: `Unity.exe -batchmode -projectPath <project> -executeMethod Blackglass.AssetPipeline.AssetPipelineRunner.RunFromCommandLine
-blackglassManifest <manifest.json> -blackglassResult <result.json> -quit -logFile <unity.log>`. Unity reads the manifest (schemaVersion 1),
imports each item independently, writes `result.json` (assets, prefabs, warnings, errors, dimensions, Avatar, clips) and exits 0 on success.
Existing assets are updated only if Asset Studio created them (asset label `BlackglassStudio`); anything else needs **Allow overwrite**. From
inside an open Editor use `Blackglass > Asset Pipeline > Run Manifest...` with a `manifest.json` from a run folder. Sources are copied; the
originals are never modified. The result tab lists project files that changed (via `git status`); nothing is committed.

## 9. Troubleshooting

- "The Unity Editor has this project open": close it. A leftover `Temp/UnityLockfile` after a crash is ignored.
- "Unity X is selected in Settings, but the project uses Unity Y": the Unity executable in Settings must be the exact version listed in `ProjectSettings/ProjectVersion.txt`. The version is read from the Unity Hub folder name (`<version>/Editor/Unity.exe`) or the executable's product version. Browse to the right `Unity.exe` or clear the setting. If the version cannot be determined the executable is used and Settings says it could not be verified.
- "finished without a usable result": open the log (Log tab); the last error lines are quoted in the message. Usual causes: a compile error in
  the project, or an import crash.
- Humanoid mapping failed: open the model in Unity > Rig > Configure, or fix the skeleton in Blender.
- Clip not humanoid / wrong pose: the animation's skeleton differs from the shared Avatar's. Also check the Unity console or `unity.log` for
  retargeting warnings; the Result tab does not carry them.
- Height looks wrong: stray geometry; use the manual scale.
- Huge or tiny scale correction (about x100 or x0.01): the FBX declares centimetres. Export in metres from Blender (apply the unit scale) or set a
  Manual visual scale. A model measuring under 0.01 m fails with "unusable measurement" until a manual scale is given.
- GLB/glTF are refused in v0.1: convert to FBX in Blender.
- Windows without CET support: `BlackglassAssetStudio.exe` (the built one and the published one) can abort at start with a ".NET Runtime" crash
  (exit code -2146233082) on an old, unpatched Windows 10 build. Install the pending Windows updates, or run the same app with
  `dotnet BlackglassAssetStudio.dll` from the build output folder.

## 10. Publish a standalone EXE

```bash
dotnet publish Tools/BlackglassAssetStudio/src/App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o Tools/BlackglassAssetStudio/publish
```

The result is `Tools/BlackglassAssetStudio/publish/BlackglassAssetStudio.exe` (not committed, about 140 MB). On first run choose the project path in Settings.

## 11. Worked example: a character FBX with an idle animation and textures (EnemyUnit)

First real use of the tool, 2026-10-09. The source is one FBX that holds the skinned model **and** an idle clip, with two PNG textures
(colour map and normal map) exported from Blender.

**Blender, textures.** A model imported from a GLB carries its images packed inside the `.blend`. Save the `.blend` where the textures should go,
then File > External Data > Unpack Resources: this writes the PNGs (here `modddif_image_0.png` and `modddif_image_1.png`, the second is the normal
map) into a `textures` folder. Export the FBX **after** unpacking. An FBX exported while the images are still packed stores the file names as
`textures\packed\<name>_png` (no extension, absolute path), which Unity cannot match to `<name>.png`, so the model stays grey.

**Asset Studio.** The Humanoid Character profile turns animation import off, so a model-plus-idle FBX is dropped twice:
1. Humanoid Character: name `EnemyUnit`, destination `Assets/Art/Characters/EnemyUnit`, target height as wanted. Gives `EnemyUnit.fbx` (Humanoid, Avatar)
   and `EnemyUnit_Visual.prefab`.
2. Humanoid Animation, profile Locomotion (idle loops, root bake on), name `EnemyUnit Idle`, clip name `Idle`, shared Avatar = the character FBX from step 1.
   The tool does not detect a duplicate drop. The name of the second import must differ from the first.

**Unity, by hand (the tool does not do this yet).**
1. Copy the PNGs into the project (a `Textures` folder, as for Darius).
2. Select the FBX > Materials tab > **Extract Materials...** into a `Materials` folder.
3. On each `.mat`: Base Map = the colour PNG, Normal Map = the normal PNG (accept "Fix now" so the texture is marked as a normal map).

**Folder layout.** The tool writes the model and prefab flat into the destination folder, and animations into `Assets/Art/Animations` unless the
destination is changed. The Darius layout (`Models/`, `Prefabs/`, `Animations/`, `Textures/`, `Materials/`) was made by hand; to get it, type the
folder into the Destination box of each import, or move the files in Unity (the `.meta` files keep every GUID). Re-importing after a move writes a
second prefab at the destination, so re-import into the folder where the assets now live.

**What to drag into a scene.** The `_Visual.prefab`, never the FBX. The prefab holds the height correction (EnemyUnit: the model measures 1.0 m, so
the child `ImportedCharacter` has scale 1.85 for the default 1.85 m target). The FBX is the raw source at native size. To change the height of a
finished character, set the scale of `ImportedCharacter` inside the prefab to (desired height / measured height) or re-import with a new target.

**Character Test Scene.** It holds a `Darius_Visual` prefab instance, a camera and a light. To look at another model: delete that instance, drag in
the new `_Visual.prefab` (rotation Y 180, feet on the floor), drag the clip onto it so Unity makes an Animator Controller, press Play.

## 12. v0.2 candidates from first real use (not scheduled)

Done in the batch-animation round (2026-10-09): browse pickers for project paths, Add Folder, plainer labels with help lines, the Character
box for animations, and multi-select with a batch edit panel (section 6). Nothing else below is built. Start only when the owner asks.

1. **Loose textures.** Copy the image files an FBX references (resolve the name even when Blender wrote `<name>_png`, search `Textures` and the
   source folder) next to or under the model, under the same ownership rule as the `.fbm` folder, and **warn** when a referenced texture cannot be
   found. Today the tool copies only a sibling `<name>.fbm` folder and says nothing about missing textures.
2. **Materials.** Extract materials into a `Materials` folder and assign Base Map and Normal Map (mark the normal map texture type), so a character
   arrives textured. Needs a rule to tell the colour map from the normal map (file name, or the FBX's own texture slots).
3. **Character with an embedded clip.** One drop that produces the character and its idle (or a chosen clip), instead of two drops and two copies of the
   same FBX.
4. **Folder layout.** An optional "Darius layout" that creates `Models/`, `Prefabs/`, `Animations/`, `Textures/`, `Materials/` under the character and
   routes each result there.
5. **Duplicate-drop and name checks.** Warn when the same source file is added twice or two items would write the same target. (Two files with
   identical content under different names, such as `EnemyUnit-Crouch-Idle-Rifle.fbx` and `EnemyUnit-Rifle-Idle.fbx`, would need a content check.)
6. **Test bench.** A menu command that puts a chosen `_Visual.prefab` into the Character Test Scene with an Animator Controller for its clip.
7. **Unit fix** for centimetre-declared FBXs (decision 039), and **forwarding Unity's retargeting warnings** (not available through the 6000.3 API).

## Tests

`dotnet test` runs the pure-logic suite. Real Unity runs are opt-in: close the Editor and set `BLACKGLASS_E2E=1`
(`BLACKGLASS_E2E=1 dotnet test Tools/BlackglassAssetStudio/BlackglassAssetStudio.sln`); the E2E classes share one xunit collection because
Unity allows one instance per project. Unity-side tests: `Tools/run-tests.sh EditMode "Blackglass.Tests.AssetPipeline"`.

## Manual test procedure

The owner runs this once on a machine where the app starts. It uses the Darius files as inputs and a scratch destination that is deleted at the end.

1. Close the Unity Editor. Start the app. Settings: the project path is the repository, the Unity version is detected, the shared Avatar defaults to
   the Darius model.
2. Drag `Assets/Art/Characters/Darius/Models/Darius Stand Idle.fbx` onto the list. Profile: Humanoid Character. Set name `TestDarius`, destination
   `Assets/_AssetStudioScratch`, target height `1.88`. Validate (no errors). Import Selected.
3. Result tab: SUCCESS, measured about 0.0188 m (the Darius FBX declares centimetres), scale about x100 (Auto) with a "large correction" warning,
   Avatar humanoid, prefab `TestDarius_Visual.prefab`, changed-files list.
4. Open Unity: select `TestDarius_Visual.prefab`: root scale (1,1,1), child `ImportedCharacter` scale about 100, an Animator with the Avatar. Drag it
   into the Character Test Scene; the instance is about 1.88 m tall (the target).
5. Back in the app set Manual visual scale `110`, Import Selected again: the item says updated; the child scale is 110 (the instance is about 2.07 m).
6. Drag the 14 files from `Assets/Art/Characters/Darius/Animations/` onto the list. Check suggested profiles (Idle/Walk/Run to Locomotion;
   Fire/Reload to Combat; Hit Reaction to Reaction; Death to Death); set the shared Avatar to `Assets/_AssetStudioScratch/TestDarius.fbx`,
   destination `Assets/_AssetStudioScratch/Anims`; Import All. In Unity: Walk loops, Fire does not, Death bakes horizontal position, all show
   Humanoid copy-avatar. Check the Unity console for retargeting warnings.
7. Drag `Assets/Art/Weapons/Rifle/Models/Rifle.fbx` as Generic Prop and a mesh as Environment Module (WallStraight): see the dimension
   warnings; leave theme registration at None.
8. Drag any `.glb`: it is refused with the Blender message. Open the Unity Editor and import again: the "Editor has this project open" message.
9. Delete `Assets/_AssetStudioScratch` (and its `.meta`) in Unity or Explorer; `git status` must be clean of it.
