# Blackglass Asset Studio v0.1: design

Status: design approved in conversation 2026-10-08 (GLB/glTF decision: option A); this file is the written spec for review.
This is a developer utility, not a gameplay phase. No gameplay code changes.

## Goal
External FBX/OBJ files go into a Windows desktop tool, the user picks a profile, and Unity ends up with correctly configured
assets: Humanoid characters at the right visual height with a visual prefab, Humanoid animation clips with loop and root
settings, generic props, and environment modules that fit the Phase 9.5 kit. All Unity asset work is done by Unity's own
Editor APIs in batch mode; the desktop app never writes `.meta`, `.prefab` or any serialized Unity file.

```
source FBX/OBJ -> Asset Studio (classify, configure, validate, stage, manifest)
               -> Unity batch (-executeMethod) -> configured assets + structured result.json -> Asset Studio shows it
```

## Non-goals
3D viewport, model/rig/animation/material/texture editing, Blender automation, LOD or retopology, AI-service integrations,
animation-library manager, installer, telemetry or any network feature, GLB/glTF import (see Decisions), automatic Git
commits, a Git client, cancelling a running Unity import, a general plugin system for profiles.

## Decisions made in this design
1. **GLB/glTF: recognised and refused in v0.1.** Unity has no built-in importer; supporting it needs a package (glTFast) which
   bypasses `ModelImporter`, so it would not give the Humanoid and clip configuration this tool exists for. The app classifies
   `.glb/.gltf` as unsupported and shows "Convert to FBX in Blender; glTF support is a v0.2 candidate". No package is added.
   OBJ is accepted as a mesh (props and environment modules only; a character or animation profile refuses it).
2. **One shared manifest contract, compiled by both sides.** The manifest and result DTOs live as plain C# files in
   `Assets/_Project/Editor/AssetPipeline/Contract/` (no `UnityEngine` references; public fields, strings, arrays and nested
   classes only, so both `JsonUtility` and `System.Text.Json` with `IncludeFields` read them). The .NET `Core` project links
   those files with `<Compile Include=... Link=...>`. There is one definition, so the two sides cannot drift. Enums are
   strings with parse helpers (`JsonUtility` writes enums as integers, which would be unreadable and fragile).
3. **Run files live outside the project.** Per run the app writes `manifest.json`, `result.json` and `unity.log` under
   `%LocalAppData%\BlackglassAssetStudio\runs\<runId>\`, together with staged copies of the sources. The project only
   receives imported assets. The last 10 run folders are kept; older ones are deleted at the start of a run.
4. **Source files are staged, then copied by Unity.** The app copies each source (and a sibling `<name>.fbm` texture folder if
   present) into the run folder; Unity copies from there into the destination under `Assets/`. The original is never opened
   for writing. Copying only inside Unity avoids the project importing a file with default settings before it is configured.
5. **Validate is local; measurements come from the import.** "Validate" runs instant .NET checks (no Unity). Height, bounds,
   Avatar and clip data are measured in Unity during import and returned in `result.json`. Because assets Studio created can be
   updated in place, correcting a scale and re-importing is a cheap loop. (A read-only "inspect" run in Unity was rejected for
   v0.1: it needs a scratch import into `Assets/` that must be cleaned up afterwards.)
6. **Ownership label for safe overwrite.** Assets the pipeline creates get the `AssetDatabase` label `BlackglassStudio`.
   Re-importing the same destination updates labelled assets silently. An existing unlabelled asset at a destination is a
   conflict: the item fails with the list of conflicting paths unless the item's `allowOverwrite` is ticked in the app.
7. **Visual character prefab convention.** The generated `<Name>_Visual.prefab` is: root `<Name>_Visual` (identity scale),
   child `ImportedCharacter` = the model instance carrying the correction scale, with the `Animator` (Avatar assigned,
   no root motion) on the model instance. It contains no gameplay components and no colliders. It is placed under a gameplay
   shell's `Visual` node by hand or later tooling; v0.1 does not edit gameplay prefabs, `Darius_Player` is untouched. The
   gameplay root's scale is never read or written.
8. **Height is measured in Unity, not in the app.** FBX cannot be measured reliably outside Unity. The importer instantiates
   the model, bakes each `SkinnedMeshRenderer` (as the Darius tests do) and takes the posed bounds height from the lowest to
   the highest point; plain `MeshRenderer` bounds are used for rigid meshes. The measured height, the target and the final
   scale are reported. Stray geometry (hair, coat tails) can skew this, so a manual scale override always wins.
9. **Unity must be closed.** Unity refuses a second instance on the same project. The app checks `Temp\UnityLockfile` (can it
   be opened exclusively?) before launch and refuses with "Close the Unity Editor for this project, then retry". The same
   pipeline is also exposed as an Editor menu command that runs a chosen manifest inside an open Editor
   (`Blackglass > Asset Pipeline > Run Manifest...`), which is useful for debugging and costs one method.
10. **Environment semantics are the existing `EnvironmentElement` enum**, not the looser list in the original brief, so the
    importer cannot create a type the generator does not know. Mapping used in the UI: Floor, WallStraight (tall wall),
    WallCorner, WallEnd, WallJunction, DoorFrame (door opening), LowCover, LowCoverLong, Pillar, Crate (cover/prop),
    Terminal, LightFixture. "Detail/Prop" is just the Generic Prop profile and is never registered in a theme.

## Layout
```
Tools/BlackglassAssetStudio/
  BlackglassAssetStudio.sln
  src/Core/        net10.0-windows (no UI) : profiles, classification, scale math, settings, path rules,
                                             manifest builder, staging, Unity locator/launcher, result reader
  src/App/         net10.0-windows WPF     : views and view-models, thin
  tests/Core.Tests/ xunit                  : pure-logic tests (the only added NuGet packages, test-only)
Assets/_Project/Editor/AssetPipeline/
  Blackglass.AssetPipeline.Editor.asmdef   Editor-only; references Blackglass (runtime asmdef)
  Contract/    ImportManifest.cs ImportResult.cs                (shared with Core)
  Entry/       AssetPipelineRunner.cs  (batch entry + menu command), ResultWriter.cs
  Importers/   CharacterImporter.cs AnimationImporter.cs PropImporter.cs EnvironmentModuleImporter.cs
               ModelImportSettings.cs (shared ModelImporter plumbing), PrefabWriter.cs, OwnershipLabel.cs,
               HeightMeasure.cs, EnvironmentSpecs.cs
docs/AssetStudio.md                         concise manual (10 required topics)
```
`.gitignore` is changed for `Tools/BlackglassAssetStudio/`: negate `*.csproj`/`*.sln` there and ignore its `bin/` and `obj/`.
The existing `Blackglass.Tests.EditMode` asmdef gets a reference to the new Editor asmdef so contract and scale tests can run
in the existing suite (`Tools/run-tests.sh EditMode`). Nothing else existing is modified.

## Manifest (schemaVersion 1)
```json
{
  "schemaVersion": 1,
  "runId": "20261008-153012-ab12",
  "resultPath": "C:\\...\\runs\\<runId>\\result.json",
  "items": [
    {
      "id": "e1c2...",                      // stable per list entry, echoed in the result
      "profile": "HumanoidCharacter",       // see Profiles
      "name": "Kestrel",                    // asset name (also file and prefab base name)
      "sourcePath": "C:\\...\\runs\\<runId>\\staged\\Kestrel.fbx",
      "destinationFolder": "Assets/Art/Characters/Kestrel",   // project-relative, validated
      "allowOverwrite": false,
      "character": { "targetHeight": 1.85, "normalizeHeight": true, "scaleOverride": 0,
                     "rigHumanoid": true, "createAvatar": true,
                     "materialMode": "Embedded", "generatePrefab": true },
      "animation": { "category": "Locomotion", "clipName": "Walk", "sharedAvatarPath": "Assets/Art/.../X.fbx",
                     "loop": "Yes", "bakeRotation": true, "bakeHeight": true, "bakePositionXZ": true },
      "prop": { "generatePrefab": true, "scale": 1.0 },
      "environment": { "element": "WallStraight", "themePath": "Assets/_Project/Environment/Themes/CorporatePrototype/CorporatePrototype.asset",
                       "themeMode": "None|Append|Replace", "replaceIndex": 0, "scale": 1.0 }
    }
  ]
}
```
Only the block matching `profile` is read; the others are ignored, so one flat class suffices (no polymorphism, which
`JsonUtility` cannot do). `scaleOverride` 0 means "none". `loop` is `Auto|Yes|No` (Auto = by category default). Unknown
`schemaVersion` makes the Unity runner fail with an explicit message.

(`//` comments in the example are illustrative; the real file is plain JSON.)

### Result
```json
{ "schemaVersion": 1, "runId": "...", "unityVersion": "6000.3.25f1", "success": true,
  "errors": [], "warnings": [],
  "items": [ { "id": "...", "success": true, "errors": [], "warnings": [],
      "importedAssets": ["Assets/..."], "createdPrefabs": ["Assets/..."], "changedAssets": ["Assets/..."],
      "dimensions": {"x":0.6,"y":1.77,"z":0.4},
      "measuredHeight": 2.4, "targetHeight": 1.85, "appliedScale": 0.7708, "scaleSource": "Auto|Manual|None",
      "avatar": { "valid": true, "isHuman": true, "message": "" },
      "clips": [ {"name":"Walk","duration":1.03,"loop":true,"bakeRotation":true,"bakeHeight":true,"bakePositionXZ":true} ],
      "registeredInTheme": "" } ] }
```
The runner writes the result file even on an exception (a catch-all produces `success:false` with the message and stack
head), and the Unity exit code is 0 for success, 1 for any failed item or fatal error.

## Profiles (Core, defined in code as data tables with per-item overrides)
| Profile | Defaults |
|---|---|
| Humanoid Character | rig Humanoid, create Avatar, embedded materials, target 1.85 m (settings default), normalize on, generate prefab |
| Humanoid Animation — Locomotion | Humanoid, copy shared Avatar, loop Yes, bake rotation/height/XZ (in place) |
| Humanoid Animation — Combat | as above, loop No, bake height only |
| Humanoid Animation — Reaction | loop No, bake height only |
| Humanoid Animation — Death | loop No, bake height and XZ (corpse lies on the floor; matches decision 034) |
| Generic Prop | no rig, scale 1, generate prefab; weapons use this |
| Environment Module | no rig, scale 1 (never auto-normalised), element required, prefab collider-free, theme registration optional |

A profile owns defaults and which settings the UI shows. A UI control never contains an import rule.

## Classification (Core, pure)
Inputs: drop-folder name chain, file stem, extension. Rules in a priority list; the first match wins: unsupported extension
(glb/gltf, anything not fbx/obj) -> `Unsupported`; folder hints (`Animations`, `Characters`, `Environment`, `Props`, `Weapons`)
then stem keywords (idle/walk/run/sprint/crouch -> Locomotion; fire/shoot/attack/reload/aim/melee -> Combat; hit/react/flinch
-> Reaction; death/die/dead -> Death; wall/floor/pillar/door/cover/corner -> Environment with a suggested element).
Anything unmatched defaults to Generic Prop. The result is a suggestion with a reason string shown in the UI; the user can
always override the profile before import. A clip name is suggested from the stem with prefix noise removed.

## Settings (Core, per user)
`%AppData%\BlackglassAssetStudio\settings.json`: `projectPath`, `unityExePath`, `defaultTargetHeight` (1.85), `defaultSharedAvatarPath`
(project-relative asset path or empty), `lastDropFolder`. Nothing machine-specific is committed.
- **Project path:** when the app runs from `...\Tools\BlackglassAssetStudio\...` it proposes the repository root; otherwise the
  user picks it. Valid = folder with `Assets` and `ProjectSettings\ProjectVersion.txt`. Spaces and any drive are fine
  (arguments are passed as an argument list, never one concatenated string).
- **Unity exe:** read `m_EditorVersion` from `ProjectVersion.txt`; look for `<Hub>\Editor\<version>\Editor\Unity.exe` in the usual
  Hub locations (Program Files, then the Hub `secondaryInstallPath.json` if present); the user can browse. If the project
  version is not found, the app reports it instead of silently using another version.
- **Shared Avatar:** if empty, the app offers `Assets/Art/Characters/Darius/Models/Darius Stand Idle.fbx` when that file
  exists (it is the Humanoid model whose Avatar the current clips copy). An animation item with Humanoid + no avatar path and
  no model in the same file fails validation with an actionable message.
- **Destination rules:** must start with `Assets/`, no `..`, no invalid characters, not inside `Packages` or `Library`.

## Unity pipeline
`AssetPipelineRunner.RunFromCommandLine()` reads `-blackglassManifest <path>`, validates `schemaVersion`, then for each item:
resolves the destination, applies the ownership/conflict rule, copies the staged source in, `AssetDatabase.ImportAsset`, applies
the profile's importer, saves, runs the post-import checks, and collects the result. Items are independent: one failure does
not stop the rest. A single `AssetDatabase.StartAssetEditing`/`StopAssetEditing` pair brackets the batch; the result is written
last; `EditorApplication.Exit(code)` is called explicitly.

- **CharacterImporter:** `ModelImporter` animationType Human, avatarSetup CreateFromThisModel, `importAnimation` off unless the
  file carries clips the user wants (v0.1: off), `globalScale`/`useFileScale` untouched, materials per `materialMode`. After
  `SaveAndReimport` it instantiates, measures height (decision 8), computes the scale, and writes the prefab (decision 7).
  Post checks: renderer count > 0, Avatar `isValid` and `isHuman` (failure is reported with the Unity-provided
  `humanDescription` hint "open the Avatar configuration"; the item still fails rather than producing a wrong prefab),
  measured height > 0.
- **AnimationImporter:** `ModelImporter` animationType Human, avatarSetup CopyFromOther using the shared Avatar (loaded from
  `sharedAvatarPath`; missing or non-humanoid is an item error), `clipAnimations` set explicitly to one clip named `clipName`
  covering the take, with `loopTime`, `lockRootRotation`, `lockRootHeightY`, `lockRootPositionXZ` (the bake-into-pose flags) from
  the manifest, and `loopPose` follows loop. The model's meshes are not needed; materials are not imported
  (`materialImportMode None`). Post checks: the clip exists, duration > 0, Avatar compatible, loop flag as requested. No `.anim`
  copies are created (decision 034).
- **PropImporter:** meshes imported with scale per manifest, no colliders, embedded materials; generates `<Name>.prefab` with
  the model instance at the root. Reports dimensions and warns when the largest side is outside 0.05 m - 10 m.
- **EnvironmentModuleImporter:** as the prop, plus: `EnvironmentSpecs` holds the footprint and pivot per element from
  `EnvironmentAssetGuide.md` section 2; the importer measures the bounds, compares with the spec footprint at a 5 cm tolerance
  and reports each mismatched axis as a warning with the numbers (not an error, and never silently scaled). A non-1 manifest
  `scale` is applied to the model instance inside the prefab, and the user chose it explicitly. Pivot is validated (lowest point
  at y ≈ 0 for bottom-centre elements, top at 0 for Floor, centred in XZ) and reported. The prefab is collider-free by
  construction. Theme registration (`Append`/`Replace`) edits the `EnvironmentTheme` through `SerializedObject` on its
  `entries` array, only if the theme asset exists and the element is valid; it marks the theme dirty and saves it, and the
  change appears in the result so Git shows it. No registration by default.
- **Ownership:** `OwnershipLabel` sets/reads the `BlackglassStudio` label on every created asset (model, prefab).
  Registration into a theme is never an overwrite of an unlabelled asset: it is an edit of a user-chosen asset and is
  reported as `changedAssets`.

## Desktop app architecture
WPF, MVVM written by hand (a view-model base with `INotifyPropertyChanged` and a small `RelayCommand`; no MVVM package).
- `MainViewModel`: asset list, selection, settings, log/status text, busy flag, commands (Add Files, Remove, Clear, Validate,
  Import Selected, Import All, Settings).
- `AssetItemViewModel`: wraps a Core `AssetItem` (source path, classification + reason, profile, per-profile settings, validation
  messages, last result).
- UI: left list with drag-drop and a profile column; right inspector with the profile-specific settings group, calculated scale
  fields for characters (source height is "—" until an import measures it, then shown from the last result; scale factor =
  target / measured, or the manual override); bottom tabs for Status, Result (warnings and errors with
  asset paths, clips and Avatar summary, created/changed list), and Log (path to `unity.log` with Open buttons).
- Long work is `async`: stage -> launch Unity with `System.Diagnostics.Process` (arguments via `ArgumentList`) -> await exit ->
  read result. The UI thread is never blocked; a busy flag disables the buttons. There is no Cancel while Unity runs
  (killing it mid-write can corrupt `Library` and half-write assets). Closing the window during a run asks to wait.
- After a run the app diffs `git status --porcelain` before and after (if `git` is available and the project is a repository)
  and lists the files that changed. It never stages, commits or pushes.
- Errors are specific: missing Unity, wrong version, Editor open, destination conflict, missing Avatar, Unity exit code with the
  last `error`/`Exception` lines from `unity.log`, unreadable or invalid `result.json` (then the log path is shown).

## Testing and verification
Core.Tests (xunit): profile defaults and selection; classification table; scale calculation (normal, zero/negative target,
manual override wins, near-zero measured height rejected); manifest serialisation round-trip and the golden fixture
(`tests/Core.Tests/Fixtures/manifest-v1.json`); destination and path validation; Unity-version/executable resolution with fake
folders; settings load/save and corrupt-file fallback; the `git status` diff parser. Unity EditMode tests (in the existing
suite): the same golden fixture parses with `JsonUtility`, scale calculation parity, environment spec table matches the guide
values, destination validation. Importer behaviour is verified by a manual and a scripted batch procedure using the owner's
Darius files as inputs into a scratch destination (deleted afterwards), recorded in `docs/AssetStudio.md`.

### Validation checklist before completion
The 24 items in the brief, evidence recorded per item. In addition: `git status` shows no changed original source file,
no stray manifests in `Assets/`, `Tools/run-tests.sh EditMode` and PlayMode stay green, and the Unity log has no new errors.

## Milestones (compile and test after each; one commit per logical step)
A. Solution scaffold, `.gitignore`, settings, drag-drop list, classification, profiles (WPF runs).
B. Contract DTOs, manifest builder, staging, Unity locator/launcher, runner skeleton, `result.json` round-trip.
C. CharacterImporter, height normalisation, prefab.
D. AnimationImporter and Avatar handling.
E. Prop and Environment Module importers, theme registration, docs, publish command, final verification.

## Publishing
`dotnet publish src/App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true` (documented; the project
file carries the `RuntimeIdentifier`-compatible settings). No installer.

## Known limitations (stated up front)
- GLB/glTF are refused; OBJ is mesh-only.
- Source height is only known from an import result: the importer measures and applies the automatic scale in the same run,
  and the app then shows the measurement so a manual override can be set for a second pass.
- Unity must be closed for a batch run; each run costs the editor start-up (roughly half a minute or more).
- External textures other than a sibling `<name>.fbm` folder are not copied.
- Stray geometry can skew measured height (manual override is the remedy).
- Theme registration is append/replace by index only; no removal.

## Open assumptions (change if wrong)
- Target framework `net10.0-windows` (current installed SDK); test packages (xunit, Microsoft.NET.Test.Sdk) are the only NuGet
  dependencies and only for tests.
- The work happens on branch `asset-studio`; merge and push only on your instruction.
