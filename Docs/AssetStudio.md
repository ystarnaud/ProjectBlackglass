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
- "Unity <version> was not found": install that exact version in Unity Hub or browse to `Unity.exe`.
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
7. Drag `Assets/Art/Characters/Darius/Weapons/Rifle.fbx` as Generic Prop and a mesh as Environment Module (WallStraight): see the dimension
   warnings; leave theme registration at None.
8. Drag any `.glb`: it is refused with the Blender message. Open the Unity Editor and import again: the "Editor has this project open" message.
9. Delete `Assets/_AssetStudioScratch` (and its `.meta`) in Unity or Explorer; `git status` must be clean of it.
