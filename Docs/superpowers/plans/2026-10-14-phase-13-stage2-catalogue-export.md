# Phase 13 Stage 2: catalogue export, implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

Date: 2026-10-14. Status: **approved by the owner on 2026-10-14 with the rulings recorded at the end of this document.** Execution happens in the primary session, with independent reviews after Tasks 1–4, after Tasks 5–7 and after Task 8.

**Every hash, byte count and test count below is a planning prediction, not a measurement.** The evidence files under `.claude/scratchpad/phase13/stage-02/` record the measured values. Where a measurement differs from a prediction, the cause is found and recorded; the expected value is never edited to match.

**Goal:** Unity writes one deterministic, versioned catalogue (`Backend/catalogue/catalogue.json`), and the backend loads it, validates it strictly and refuses to serve with an invalid one. Both sides compute the same `catalogueVersion`.

**Architecture:**
- **Unity side.** A new Editor-only assembly `Blackglass.Backend.Editor` (`Assets/_Project/Editor/Backend/`) does four things:
  - reads the authored ScriptableObjects (item catalogue, loot table, progression track, operative definitions, starter loadout);
  - checks them;
  - builds a plain export model;
  - writes it in one canonical text form.

  The committed file is the export's exact bytes, and an EditMode test fails while it differs from a fresh export.
- **Backend side.** A new pure project `Accounts.Domain` embeds the committed file. It reads the file strictly and validates every rule. It then re-writes the file canonically and requires byte equality.
- **The shared version.** `catalogueVersion` is `sha256:` plus the lowercase hex SHA-256 of the file's bytes. A shared golden vector (`Backend/contracts/catalogue/`) proves that both writers produce the same bytes and both hash them the same way.
- **The startup gate.** `serve` checks the embedded catalogue before anything else.

**Tech stack:**
- Unity 6000.3.25f1, C# 9, NUnit EditMode tests (`Tools/run-tests.sh`), batch export via `Tools/run-method.sh`.
- .NET 10 and `System.Text.Json` (in the shared framework, so no new package); xUnit 2.9.3 as in Stage 1.
- No new package on either side.

**Spec:** `Docs/superpowers/specs/2026-10-14-phase-13-backend-design.md` revision 2.1:
- §4.6 (catalogue export, A-22), §4.7 (ids), §2.3 (layout: `Accounts.Domain`, `contracts/`), §3 (catalogue row), §19 (success XP, bag capacity);
- the stage-level plan `Docs/superpowers/plans/2026-10-14-phase-13-backend.md`, Stage 2;
- decision 048.

## Global Constraints

- **Scope.** Catalogue export and validation only. No accounts, mission deployment, settlement, gameplay networking, store, database table or migration.
- **No migration.** Stage 2 needs no database change. The migration catalog stays at `0000_foundation`, and `0000` is never edited. The invariant-gating tests (`UpgradeTests.Invariants_are_gated_by_their_version_prefix`, `drill-test.sh`) already derive versions from the catalog and the backup, so 0001 (Stage 3) needs no change to them. The rest of the Stage 1 suite has one `0000` literal: `ApiTests` asserts `Applied 0000`, which stays true when a `0001` exists.
- **Gameplay unchanged.**
  - No scene, prefab, HUD, input, project-settings or offline-client change.
  - Every existing serialized reference stays as it is. Asset changes only **add** an `id:` line (Task 1).
  - The game reads no new field at runtime.
- **Ids.**
  - Every id in the file matches `^[a-z0-9]+(?:[.-][a-z0-9]+)*$` and is at most 64 characters.
  - Ids are authored fields. They are never derived from a display name, an asset name, an asset GUID or the game's title.
  - The file contains no display text, description, presentation reference or stat value (spec §4.6 lists ids and rules only).
- **Canonical form, identical on both sides.**
  - UTF-8 without a BOM, LF line ends, two-space indentation, one member or element per line.
  - Members in the fixed order of the format below; `[]` for an empty array; invariant-culture integers; `true`/`false`.
  - Exactly one final `\n`.
  - Strings are ids or enum words, so nothing is escaped, and a writer refuses any other string.
- **Order.**
  - Sets are sorted ordinally by id: items, progression choice ids, operatives, starter operatives.
  - Lists whose order has meaning keep the authored order: loot entries (roll order), an operative's ability ids (slot order), starter grants and the stash.
- **Version.** `catalogueVersion = "sha256:" + lowercase hex(SHA-256(file bytes))`. It is not stored inside the file.
- **Format.** `formatVersion` 1. The backend refuses any other value (fail closed, like an unknown schema version).
- **Error kinds.** Every validation message on both sides starts with `missing:`, `duplicate:`, `malformed:` or `incompatible:`. Every problem is reported, not just the first.
- **Owner files.** `Assets/Settings/Mobile_RPAsset.asset` and `ProjectSettings/URPProjectSettings.asset` stay unstaged and byte-identical. `.claude/` is never committed.
- **Infrastructure.**
  - No VM, firewall, Docker daemon or SSH change, and no live accounts stack.
  - The only VM activity is re-running the existing `drill-test.sh` and `smoke-test.sh` (throwaway projects) because the image's Dockerfile changes.
  - The test cluster on `127.0.0.1:55432` keeps running.
- **Builds.**
  - Backend: on this PC run `bash ../.claude/scratchpad/phase13/tools/dt.sh test Backend.sln` from `Backend/` (the CET wrapper); `dotnet sln`/`dotnet new` need no wrapper. `UNIT` = `--filter "Requires!=Postgres"`.
  - Unity: the Editor must be closed for `Tools/run-tests.sh` and `Tools/run-method.sh`.
- **Commits.** Logically separated, on branch `phase13-stage2`, with trailer `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. No merge or push without the owner.

### The format (version 1)

```text
{
  "formatVersion": 1,
  "items":       [ { "id", "category", "slot", "stackable", "maxStack", "healAmount" } ... ]   sorted by id
  "lootTable":   { "version", "containerCount", "minItems", "maxItems",
                   "entries": [ { "itemId", "minQuantity", "maxQuantity", "weight" } ... ] }      authored order
  "progression": { "xpThresholds": [ int ... ], "missionCompletionXp", "choiceIds": [ id ... ] } choices sorted
  "operatives":  [ { "id", "roleId", "abilityIds": [ id ... ] } ... ]                            sorted by id
  "starter":     { "operatives": [ { "operativeId", "grants": [ { "itemId", "quantity", "equip" } ... ] } ... ],
                   "stash": [ { "itemId", "quantity" } ... ] }                                   operatives sorted
}
```

- `category`: `weapon`, `armor`, `utility` or `consumable`.
- `slot`: `none`, `weapon`, `armor` or `utility`.
- The exact text of a complete example is the golden vector in Task 2.

### Semantic rules (enforced by Unity's builder and by the backend's validator)

| Area | Rule (error kind) |
|---|---|
| Items | At least one (missing). Ids are well-formed (malformed), unique (duplicate) and ascending (malformed). `maxStack` ≥ 1 and `healAmount` ≥ 0 (malformed). `stackable` is true exactly when `maxStack` > 1 (incompatible). Weapon↔weapon slot, armor↔armor, utility↔utility, consumable↔none (incompatible). A consumable heals at least 1 (incompatible). Equipment doesn't stack (incompatible). |
| Loot table | `version` ≥ 1 and 0 ≤ `containerCount` ≤ 6 (malformed; `LootTable.MaxContainers`, enforced by the backend since the owner's correction of 2026-10-14). 1 ≤ `minItems` ≤ `maxItems` (malformed). At least one entry (missing). Every entry's item is a catalogue item (incompatible). `weight` ≥ 1 and 1 ≤ `minQuantity` ≤ `maxQuantity` (malformed). |
| Progression | At least one threshold (missing). Rank 1 needs 0 XP and thresholds strictly increase (malformed). `missionCompletionXp` ≥ 0 (malformed). Choice ids are well-formed, unique and ascending. |
| Operatives | At least one (missing). Ids are well-formed (lowercase GUIDs pass), unique and ascending. `roleId` and every ability id are well-formed. At most 4 abilities (incompatible; Unity's `OperativeDefinition.MaxAbilities`). No ability twice on one operative (duplicate). |
| Starter | Every operative id is an exported operative (incompatible), listed once (duplicate) and ascending. Every grant and stash item is a catalogue item (incompatible). Quantity ≥ 1 (malformed). `equip` only for an item with a slot, at most one per slot per operative (incompatible). Unity's builder also refuses an equipped stash grant (incompatible); the file has no `equip` member for the stash. |
| Unity only | A source asset that is missing, or an empty list slot (missing). An invalid asset by its own `IsValid` (malformed). Two different role or ability assets sharing one id (duplicate). A loot, grant or starter reference to an asset that isn't the exported one with that id (incompatible). |
| Backend only | No BOM and no CR (malformed). Strict JSON: no unknown, duplicate or missing member, and the right JSON kind (malformed/duplicate/missing). Integers fit 32 bits. `formatVersion` = 1 (incompatible). The file equals the canonical re-serialization of what was read (malformed). |

## Review Focus

These conditions are implied by the spec and most likely to bite. Each is pinned by a test in the task named.

1. **A Windows checkout or an editor changes line ends or adds a BOM.** The backend must refuse the file with a clear message, not silently compute a different version. Covered by Task 6 (the BOM and CRLF facts) and the Task 8 check that `.gitattributes` (`*.json eol=lf`) applies to the file.
2. **A designer renames an item, operative, role, ability or choice.** The export and its version must not change. Covered by Task 3, `Build_IgnoresDisplayNames`.
3. **Assets are reordered.** Reordering in the item catalogue, the progression choices or the operative folder must not change the version. Reordering loot entries or an operative's abilities must change it, because order means something there. Covered by Task 3, where the golden assets are deliberately scrambled while the vector is sorted, and the vector keeps loot entries and ability ids in authored, non-sorted order.
4. **The export runs on a non-English machine.** The bytes must be identical. Covered by Task 2, `Writer_IsTheSameUnderOtherCultures`.
5. **A copied operative asset keeps its GUID id.** The export must refuse it and name both assets. Covered by Task 3, `Build_RefusesTwoOperativesWithOneId`.

---

## Before Task 1: branch and baselines (no commit)

- [ ] **Create the branch from the merged Stage 1:** `git checkout -b phase13-stage2 main`. Expect `main` = `ad14373`. The owner's two settings files and `.claude/` carry over untouched.
- [ ] **Update `CLAUDE.md`'s "Current Development Stage" and "Current Priority"** (owner requirement 4). Record that Stage 0 and Stage 1 are done, merged and pushed (`ad14373`), and that Stage 2 (catalogue export) is the authorized work in progress. Stage 3 and later each still need the owner's go-ahead.
- [ ] **Commit the plan and `CLAUDE.md` as the branch's first commit:**
  ```bash
  git add Docs/superpowers/plans/2026-10-14-phase-13-stage2-catalogue-export.md CLAUDE.md
  git commit -m "Phase 13 Stage 2 plan (catalogue export); CLAUDE.md records Stage 1 done and Stage 2 authorized"
  ```
- **Review gates (owner):** an independent review of the Unity work after Task 4, one of the backend work after Task 7, and a final whole-branch review after Task 8. Reviews run as subagents; implementation stays in the primary session.
- [ ] **Measure the Unity baselines** (Editor closed): `bash Tools/run-tests.sh EditMode` and `bash Tools/run-tests.sh PlayMode`. Record the `total`, `passed` and `failed` counts in `.claude/scratchpad/phase13/stage-02/baseline.txt`. Every later Unity count in this plan is "baseline + N".
- [ ] **Measure the backend baseline.**
  - With the tunnel open (`bash Backend/tools/testdb.sh tunnel`, kept running), from `Backend/`, run `bash ../.claude/scratchpad/phase13/tools/dt.sh test Backend.sln` and the same with `--filter "Requires!=Postgres"`.
  - Expected: ALL 94/0 and UNIT 56/0, as measured on merged `main` on 2026-10-14.
  - Append both counts to `baseline.txt`.

## File map

| Path | Task | Responsibility |
|---|---|---|
| `Assets/_Project/Scripts/Abilities/AbilityDefinition.cs` | 1 | adds an authored `id`, `Id` and internal `SetId` |
| `Assets/_Project/Scripts/Operatives/OperativeRole.cs` | 1 | adds an authored `id`, `Id` and internal `SetId` |
| `Assets/_Project/Scripts/Items/StarterLoadout.cs` | 1 | adds a public read-only `Operatives` list |
| `Assets/_Project/Data/Abilities/{AimedShot,Blast,Mend,ReconScan}.asset` | 1 | one added `id:` line each |
| `Assets/_Project/Data/Operatives/Roles/{Assault,Recon,Support}.asset` | 1 | one added `id:` line each |
| `Assets/_Project/Editor/Backend/Blackglass.Backend.Editor.asmdef` | 2 | Editor-only assembly, references `Blackglass` |
| `Assets/_Project/Editor/Backend/CatalogueDocument.cs` | 2 | the plain export model |
| `Assets/_Project/Editor/Backend/CanonicalCatalogueWriter.cs` | 2 | canonical text, bytes and version |
| `Assets/_Project/Editor/Backend/CatalogueSources.cs` | 3 | the source assets and their fixed project paths |
| `Assets/_Project/Editor/Backend/CatalogueBuilder.cs` | 3 | assets → model, every check |
| `Assets/_Project/Editor/Backend/CatalogueExporter.cs` | 4 | menu and batch entry; writes the committed file |
| `Assets/_Project/Tests/EditMode/Blackglass.Tests.EditMode.asmdef` | 2 | adds the reference to `Blackglass.Backend.Editor` |
| `Assets/_Project/Tests/EditMode/CatalogueExportTests.cs` | 1, 2, 4 | project ids, writer and golden vector, committed-file checks |
| `Assets/_Project/Tests/EditMode/CatalogueBuilderTests.cs` | 3 | builder from in-memory assets: golden, determinism, refusals |
| `Backend/contracts/catalogue/golden-catalogue.json` and `.version` | 2 | the shared golden vector |
| `Backend/catalogue/catalogue.json` | 4 | the committed export (generated, never hand-edited) |
| `Backend/src/Accounts.Domain/` | 5, 6 | the model, canonical writer and version, then the reader, validator and `GameCatalogue` |
| `Backend/tests/Accounts.Domain.Tests/` | 5, 6 | xUnit, no I/O beyond fixture files |
| `Backend/Backend.sln` | 5 | adds the two projects |
| `Backend/src/Accounts.Api/{Accounts.Api.csproj,ServeCommand.cs,ExitCodes.cs}` | 7 | the startup gate (exit 4) |
| `Backend/tests/Accounts.Integration.Tests/Unit/ServeCatalogueTests.cs` | 7 | gate tests |
| `Backend/Dockerfile` | 7 | `COPY catalogue/ catalogue/` |
| `Backend/README.md` | 7 | catalogue section, exit code 4, layout |
| `Docs/superpowers/plans/2026-10-14-phase-13-backend.md`, `Docs/superpowers/specs/2026-10-14-phase-13-backend-design.md` | 8 | Stage 2 status and touches; §4.6, §4.7 and §19 alignment |
| `Assets/_Project/Editor/Backend/PermanentIds.cs`, `Assets/_Project/Tests/EditMode/PermanentIdsTests.cs` | owner correction | approved role and ability ids by asset path; fill a blank id, keep a match, refuse a different or duplicate id (the asmdef becomes `autoReferenced`) |
| `Assets/_Project/Editor/OperativeDataBuilder.cs`, `IntelligenceSceneBuilder.cs`, `Presentation/CombatPresentationSceneBuilder.cs` | owner correction | created roles and the Recon Scan get their approved ids; the PreviewBlast clone's copied id is cleared |
| `Backend/src/Accounts.Domain/Catalogue/CatalogueValidator.cs` | owner correction | the 0-6 container bound (`MaxContainers`) |

---

### Task 1: Authored ids for roles and abilities; the starter's operative list

**Files:**
- Modify: `Assets/_Project/Scripts/Abilities/AbilityDefinition.cs`, `Assets/_Project/Scripts/Operatives/OperativeRole.cs` and `Assets/_Project/Scripts/Items/StarterLoadout.cs`.
- Modify: the 7 assets listed in the file map (one added line each).
- Create: `Assets/_Project/Tests/EditMode/CatalogueExportTests.cs`.

**Interfaces:**
- Produces:
  - `public string AbilityDefinition.Id` and `internal void AbilityDefinition.SetId(string value)`;
  - `public string OperativeRole.Id` and `internal void OperativeRole.SetId(string value)`;
  - `public IReadOnlyList<StarterOperative> StarterLoadout.Operatives`.
- The ids, subject to Q1:
  - abilities: `ability.aimed-shot`, `ability.blast`, `ability.mend`, `ability.recon-scan`;
  - roles: `role.assault`, `role.recon`, `role.support`.

- [ ] **Step 1: Write the failing test.** Create `Assets/_Project/Tests/EditMode/CatalogueExportTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;

namespace Blackglass.Tests
{
    /// <summary>The backend catalogue export: the project's ids, the canonical writer and the committed file.</summary>
    public class CatalogueExportTests
    {
        static T Load<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            Assert.That(asset != null, Is.True, "missing asset " + path);
            return asset;
        }

        [Test]
        public void ProjectAbilitiesAndRoles_CarryTheirAuthoredIds()
        {
            var abilities = new Dictionary<string, string>
            {
                ["Assets/_Project/Data/Abilities/AimedShot.asset"] = "ability.aimed-shot",
                ["Assets/_Project/Data/Abilities/Blast.asset"] = "ability.blast",
                ["Assets/_Project/Data/Abilities/Mend.asset"] = "ability.mend",
                ["Assets/_Project/Data/Abilities/ReconScan.asset"] = "ability.recon-scan",
            };
            foreach (var pair in abilities)
            {
                var ability = Load<AbilityDefinition>(pair.Key);
                Assert.That(ability.Id, Is.EqualTo(pair.Value), pair.Key);
                Assert.That(ability.Id, Is.Not.EqualTo(ability.DisplayName));
            }
            var roles = new Dictionary<string, string>
            {
                ["Assets/_Project/Data/Operatives/Roles/Assault.asset"] = "role.assault",
                ["Assets/_Project/Data/Operatives/Roles/Recon.asset"] = "role.recon",
                ["Assets/_Project/Data/Operatives/Roles/Support.asset"] = "role.support",
            };
            foreach (var pair in roles)
            {
                var role = Load<OperativeRole>(pair.Key);
                Assert.That(role.Id, Is.EqualTo(pair.Value), pair.Key);
                Assert.That(role.Id, Is.Not.EqualTo(role.DisplayName));
            }
        }

        [Test]
        public void ProjectStarterLoadout_ListsItsThreeOperatives()
        {
            var starter = Load<StarterLoadout>("Assets/_Project/Data/Items/StarterLoadout.asset");
            Assert.That(starter.Operatives.Select(o => o.operative.Id), Is.EquivalentTo(new[]
            {
                "6f1d2a40-3b7c-4e95-8a1f-52c0d9e7b301",
                "a93e5c17-0d48-4f2b-b6a3-7e1c8d4f9a02",
                "2c84b7e9-51a6-4d03-9f7e-c3a0165d8b03",
            }));
        }
    }
}
```

- [ ] **Step 2: Run it and see it fail to compile.** Run `bash Tools/run-tests.sh EditMode "Blackglass.Tests.CatalogueExportTests"`. Expected: EXIT=1 with `error CS1061` (no `Id` or `Operatives`).

- [ ] **Step 3: Add the fields.**
  - In `AbilityDefinition.cs`, add this as the first serialized field, before `displayName`:
    ```csharp
        // The stable key the backend catalogue uses (never the display name). Authored once and never changed.
        [SerializeField] string id = "";
    ```
    Add next to `DisplayName`:
    ```csharp
        public string Id => id;
    ```
    Add next to `SetPresentation`:
    ```csharp
        internal void SetId(string value) => id = value ?? string.Empty;
    ```
  - In `OperativeRole.cs`, make the same three additions: the field before `displayName`, `Id` after the field declarations, and `SetId` before `Create`.
  - In `StarterLoadout.cs`, add after the `Stash` property:
    ```csharp
        /// <summary>The per-operative grants as authored (the backend catalogue export reads them).</summary>
        public IReadOnlyList<StarterOperative> Operatives => operatives ?? Array.Empty<StarterOperative>();
    ```

- [ ] **Step 4: Author the ids in the assets.** In each file, insert one line directly before the `displayName` line. The `displayName` line is unique in each file and sits right after `m_EditorClassIdentifier`. Use the Edit tool: Python heredocs have mangled backslashes on this machine before.

| File | Replace | With |
|---|---|---|
| `Data/Abilities/AimedShot.asset` | `  displayName: Aimed Shot` | `  id: ability.aimed-shot`↵`  displayName: Aimed Shot` |
| `Data/Abilities/Blast.asset` | `  displayName: Blast` | `  id: ability.blast`↵`  displayName: Blast` |
| `Data/Abilities/Mend.asset` | `  displayName: Mend` | `  id: ability.mend`↵`  displayName: Mend` |
| `Data/Abilities/ReconScan.asset` | `  displayName: Recon Scan` | `  id: ability.recon-scan`↵`  displayName: Recon Scan` |
| `Data/Operatives/Roles/Assault.asset` | `  displayName: Assault` | `  id: role.assault`↵`  displayName: Assault` |
| `Data/Operatives/Roles/Recon.asset` | `  displayName: Recon` | `  id: role.recon`↵`  displayName: Recon` |
| `Data/Operatives/Roles/Support.asset` | `  displayName: Support` | `  id: role.support`↵`  displayName: Support` |

  `Assets/_Project/Presentation/Preview/PreviewBlast.asset` is not referenced by any operative and is left without an id. The export only reads abilities that operatives use.

- [ ] **Step 5: Run the test and see it pass.** Run `bash Tools/run-tests.sh EditMode "Blackglass.Tests.CatalogueExportTests"`. Expected: EXIT=0, 2 passed.
- [ ] **Step 6: Run the whole EditMode suite.** Run `bash Tools/run-tests.sh EditMode`. Expected: baseline + 2, 0 failed.
- [ ] **Step 7: Check the asset diff.** `git diff --stat -- Assets/_Project/Data` shows 7 files, each `1 +`, and `git diff -- Assets/_Project/Data | grep '^-[^-]'` prints nothing.
- [ ] **Step 8: Commit.**
  ```bash
  git add Assets/_Project/Scripts/Abilities/AbilityDefinition.cs Assets/_Project/Scripts/Operatives/OperativeRole.cs \
    Assets/_Project/Scripts/Items/StarterLoadout.cs Assets/_Project/Data/Abilities/AimedShot.asset \
    Assets/_Project/Data/Abilities/Blast.asset Assets/_Project/Data/Abilities/Mend.asset \
    Assets/_Project/Data/Abilities/ReconScan.asset Assets/_Project/Data/Operatives/Roles/Assault.asset \
    Assets/_Project/Data/Operatives/Roles/Recon.asset Assets/_Project/Data/Operatives/Roles/Support.asset \
    Assets/_Project/Tests/EditMode/CatalogueExportTests.cs Assets/_Project/Tests/EditMode/CatalogueExportTests.cs.meta
  git commit -m "Phase 13 Stage 2: authored stable ids for roles and abilities; StarterLoadout lists its operatives"
  ```
  Unity creates the test file's `.meta` on its first import (Step 2's run); commit it.

---

### Task 2: The export model, the canonical writer and the shared golden vector

**Files:**
- Create: `Assets/_Project/Editor/Backend/Blackglass.Backend.Editor.asmdef`, `CatalogueDocument.cs` and `CanonicalCatalogueWriter.cs`.
- Create: `Backend/contracts/catalogue/golden-catalogue.json` and `Backend/contracts/catalogue/golden-catalogue.version`.
- Modify: `Assets/_Project/Tests/EditMode/Blackglass.Tests.EditMode.asmdef` (one reference) and `Assets/_Project/Tests/EditMode/CatalogueExportTests.cs`.

**Interfaces:**
- Produces (namespace `Blackglass.BackendTools`):
  - the model types `CatalogueDocument`, `CatalogueItem`, `CatalogueLootTable`, `CatalogueLootEntry`, `CatalogueProgression`, `CatalogueOperative`, `CatalogueStarter`, `CatalogueStarterOperative`, `CatalogueGrant` and `CatalogueStashGrant`, with the fields as below;
  - `CanonicalCatalogueWriter.IsWellFormedId(string) : bool`, `Write(CatalogueDocument) : string`, `ToBytes(CatalogueDocument) : byte[]` and `VersionOf(byte[]) : string`.
- The test helper `SharedVectors.GoldenBytes()`, `.GoldenVersion()` and `.ProjectRoot` (in `CatalogueExportTests.cs`, namespace `Blackglass.Tests`, `internal`).

- [ ] **Step 1: Create the golden vector.** Write `Backend/contracts/catalogue/golden-catalogue.json` with exactly this text: LF line ends, no BOM, two-space indentation and one final newline (2130 bytes):

```json
{
  "formatVersion": 1,
  "items": [
    {
      "id": "item.alpha-rifle",
      "category": "weapon",
      "slot": "weapon",
      "stackable": false,
      "maxStack": 1,
      "healAmount": 0
    },
    {
      "id": "item.beta-vest",
      "category": "armor",
      "slot": "armor",
      "stackable": false,
      "maxStack": 1,
      "healAmount": 0
    },
    {
      "id": "item.delta-boots",
      "category": "utility",
      "slot": "utility",
      "stackable": false,
      "maxStack": 1,
      "healAmount": 0
    },
    {
      "id": "item.gamma-kit",
      "category": "consumable",
      "slot": "none",
      "stackable": true,
      "maxStack": 3,
      "healAmount": 10
    }
  ],
  "lootTable": {
    "version": 7,
    "containerCount": 2,
    "minItems": 1,
    "maxItems": 3,
    "entries": [
      {
        "itemId": "item.gamma-kit",
        "minQuantity": 1,
        "maxQuantity": 2,
        "weight": 5
      },
      {
        "itemId": "item.alpha-rifle",
        "minQuantity": 1,
        "maxQuantity": 1,
        "weight": 1
      }
    ]
  },
  "progression": {
    "xpThresholds": [
      0,
      10,
      30
    ],
    "missionCompletionXp": 20,
    "choiceIds": [
      "choice.one",
      "choice.two"
    ]
  },
  "operatives": [
    {
      "id": "00000000-0000-4000-8000-000000000001",
      "roleId": "role.alpha",
      "abilityIds": [
        "ability.two",
        "ability.one"
      ]
    },
    {
      "id": "00000000-0000-4000-8000-000000000002",
      "roleId": "role.beta",
      "abilityIds": []
    }
  ],
  "starter": {
    "operatives": [
      {
        "operativeId": "00000000-0000-4000-8000-000000000001",
        "grants": [
          {
            "itemId": "item.alpha-rifle",
            "quantity": 1,
            "equip": true
          },
          {
            "itemId": "item.gamma-kit",
            "quantity": 2,
            "equip": false
          }
        ]
      }
    ],
    "stash": [
      {
        "itemId": "item.beta-vest",
        "quantity": 1
      },
      {
        "itemId": "item.gamma-kit",
        "quantity": 4
      }
    ]
  }
}
```

  Then compute its version with an independent tool and check it:
  ```bash
  printf 'sha256:%s\n' "$(sha256sum Backend/contracts/catalogue/golden-catalogue.json | cut -d' ' -f1)" > Backend/contracts/catalogue/golden-catalogue.version
  cat Backend/contracts/catalogue/golden-catalogue.version   # expected: sha256:355a9cc99cbb8eef349967f623868fc9fe8d306ce00e6f8de3f9a3f1a45f24eb
  ```
  A different hash means the text isn't byte-exact; fix the file, never the expected value. (The planner produced this hash from the text above.)

- [ ] **Step 2: Write the failing tests.**
  - In `Blackglass.Tests.EditMode.asmdef`, add `"Blackglass.Backend.Editor"` to `references` after `"Blackglass.AssetPipeline.Editor"`.
  - In `CatalogueExportTests.cs`, add the usings `System`, `System.Globalization`, `System.IO`, `System.Text`, `Blackglass.BackendTools` and `UnityEngine`, and this helper class after `CatalogueExportTests` in the same namespace:

```csharp
    /// <summary>The golden vector both sides check byte for byte (Backend/contracts/catalogue).</summary>
    internal static class SharedVectors
    {
        public static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;
        static string Folder => Path.Combine(ProjectRoot, "Backend", "contracts", "catalogue");
        public static byte[] GoldenBytes() => File.ReadAllBytes(Path.Combine(Folder, "golden-catalogue.json"));
        public static string GoldenVersion() => File.ReadAllText(Path.Combine(Folder, "golden-catalogue.version")).Trim();
    }
```

  Add these members to `CatalogueExportTests`:

```csharp
        const string One = "00000000-0000-4000-8000-000000000001";
        const string Two = "00000000-0000-4000-8000-000000000002";

        // The golden vector as a model, already in canonical order (the builder's sorting is Task 3's).
        static CatalogueDocument GoldenDocument()
        {
            var d = new CatalogueDocument();
            d.Items.Add(new CatalogueItem { Id = "item.alpha-rifle", Category = "weapon", Slot = "weapon", MaxStack = 1 });
            d.Items.Add(new CatalogueItem { Id = "item.beta-vest", Category = "armor", Slot = "armor", MaxStack = 1 });
            d.Items.Add(new CatalogueItem { Id = "item.delta-boots", Category = "utility", Slot = "utility", MaxStack = 1 });
            d.Items.Add(new CatalogueItem { Id = "item.gamma-kit", Category = "consumable", Slot = "none", Stackable = true, MaxStack = 3, HealAmount = 10 });
            d.LootTable.Version = 7;
            d.LootTable.ContainerCount = 2;
            d.LootTable.MinItems = 1;
            d.LootTable.MaxItems = 3;
            d.LootTable.Entries.Add(new CatalogueLootEntry { ItemId = "item.gamma-kit", MinQuantity = 1, MaxQuantity = 2, Weight = 5 });
            d.LootTable.Entries.Add(new CatalogueLootEntry { ItemId = "item.alpha-rifle", MinQuantity = 1, MaxQuantity = 1, Weight = 1 });
            d.Progression.XpThresholds.AddRange(new[] { 0, 10, 30 });
            d.Progression.MissionCompletionXp = 20;
            d.Progression.ChoiceIds.AddRange(new[] { "choice.one", "choice.two" });
            var one = new CatalogueOperative { Id = One, RoleId = "role.alpha" };
            one.AbilityIds.AddRange(new[] { "ability.two", "ability.one" });
            d.Operatives.Add(one);
            d.Operatives.Add(new CatalogueOperative { Id = Two, RoleId = "role.beta" });
            var starter = new CatalogueStarterOperative { OperativeId = One };
            starter.Grants.Add(new CatalogueGrant { ItemId = "item.alpha-rifle", Quantity = 1, Equip = true });
            starter.Grants.Add(new CatalogueGrant { ItemId = "item.gamma-kit", Quantity = 2 });
            d.Starter.Operatives.Add(starter);
            d.Starter.Stash.Add(new CatalogueStashGrant { ItemId = "item.beta-vest", Quantity = 1 });
            d.Starter.Stash.Add(new CatalogueStashGrant { ItemId = "item.gamma-kit", Quantity = 4 });
            return d;
        }

        [Test]
        public void Writer_WritesTheSharedGoldenVectorByteForByte()
        {
            var bytes = CanonicalCatalogueWriter.ToBytes(GoldenDocument());
            Assert.That(bytes.SequenceEqual(SharedVectors.GoldenBytes()), Is.True, Encoding.UTF8.GetString(bytes));
        }

        [Test]
        public void Writer_VersionIsTheSharedVectorsVersion()
        {
            Assert.That(SharedVectors.GoldenVersion(), Does.Match("^sha256:[0-9a-f]{64}$"));
            Assert.That(CanonicalCatalogueWriter.VersionOf(SharedVectors.GoldenBytes()), Is.EqualTo(SharedVectors.GoldenVersion()));
        }

        [Test]
        public void Writer_IsTheSameUnderOtherCultures()
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                foreach (var name in new[] { "tr-TR", "ar-SA", "fr-FR" })
                {
                    CultureInfo.CurrentCulture = new CultureInfo(name);
                    Assert.That(CanonicalCatalogueWriter.ToBytes(GoldenDocument()).SequenceEqual(SharedVectors.GoldenBytes()), Is.True, name);
                }
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        [Test]
        public void Writer_RefusesAStringThatIsNotAnId()
        {
            var document = GoldenDocument();
            document.Items[0].Id = "Item Alpha";
            Assert.That(() => CanonicalCatalogueWriter.Write(document), Throws.ArgumentException);
        }

        [Test]
        public void Writer_UsesUtf8WithoutABomAndOnlyLineFeeds()
        {
            var bytes = CanonicalCatalogueWriter.ToBytes(GoldenDocument());
            Assert.That(bytes[0], Is.EqualTo((byte)'{'));
            Assert.That(Array.IndexOf(bytes, (byte)'\r'), Is.EqualTo(-1));
            Assert.That(bytes[bytes.Length - 2], Is.EqualTo((byte)'}'));
            Assert.That(bytes[bytes.Length - 1], Is.EqualTo((byte)'\n'));
        }
```

- [ ] **Step 3: Run the tests and see them fail.** Run `bash Tools/run-tests.sh EditMode "Blackglass.Tests.CatalogueExportTests"`. Expected: EXIT=1, a compile error because `Blackglass.BackendTools` doesn't exist.

- [ ] **Step 4: Create the assembly.** `Assets/_Project/Editor/Backend/Blackglass.Backend.Editor.asmdef`:

```json
{
    "name": "Blackglass.Backend.Editor",
    "rootNamespace": "Blackglass.BackendTools",
    "references": [
        "Blackglass"
    ],
    "includePlatforms": [
        "Editor"
    ],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "overrideReferences": false,
    "precompiledReferences": [],
    "autoReferenced": false,
    "defineConstraints": [],
    "versionDefines": [],
    "noEngineReferences": false
}
```

- [ ] **Step 5: Write the model.** `Assets/_Project/Editor/Backend/CatalogueDocument.cs`:

```csharp
using System.Collections.Generic;

namespace Blackglass.BackendTools
{
    /// <summary>
    /// The backend catalogue (Backend/catalogue/catalogue.json, format 1) as plain data, in the shape and order it is
    /// written. Ids and rules only: no display text, presentation or stat values (spec §4.6). Strings are ids or enum
    /// words; CanonicalCatalogueWriter refuses anything else.
    /// </summary>
    public sealed class CatalogueDocument
    {
        public const int FormatVersion = 1;
        public readonly List<CatalogueItem> Items = new List<CatalogueItem>();
        public readonly CatalogueLootTable LootTable = new CatalogueLootTable();
        public readonly CatalogueProgression Progression = new CatalogueProgression();
        public readonly List<CatalogueOperative> Operatives = new List<CatalogueOperative>();
        public readonly CatalogueStarter Starter = new CatalogueStarter();
    }

    public sealed class CatalogueItem
    {
        public string Id;
        public string Category;   // weapon, armor, utility, consumable
        public string Slot;       // none, weapon, armor, utility
        public bool Stackable;
        public int MaxStack;
        public int HealAmount;
    }

    public sealed class CatalogueLootTable
    {
        public int Version;
        public int ContainerCount;
        public int MinItems;
        public int MaxItems;
        public readonly List<CatalogueLootEntry> Entries = new List<CatalogueLootEntry>();
    }

    public sealed class CatalogueLootEntry
    {
        public string ItemId;
        public int MinQuantity;
        public int MaxQuantity;
        public int Weight;
    }

    public sealed class CatalogueProgression
    {
        public readonly List<int> XpThresholds = new List<int>();
        public int MissionCompletionXp;
        public readonly List<string> ChoiceIds = new List<string>();
    }

    public sealed class CatalogueOperative
    {
        public string Id;
        public string RoleId;
        public readonly List<string> AbilityIds = new List<string>();
    }

    public sealed class CatalogueStarter
    {
        public readonly List<CatalogueStarterOperative> Operatives = new List<CatalogueStarterOperative>();
        public readonly List<CatalogueStashGrant> Stash = new List<CatalogueStashGrant>();
    }

    public sealed class CatalogueStarterOperative
    {
        public string OperativeId;
        public readonly List<CatalogueGrant> Grants = new List<CatalogueGrant>();
    }

    public sealed class CatalogueGrant
    {
        public string ItemId;
        public int Quantity;
        public bool Equip;
    }

    public sealed class CatalogueStashGrant
    {
        public string ItemId;
        public int Quantity;
    }
}
```

- [ ] **Step 6: Write the writer.** `Assets/_Project/Editor/Backend/CanonicalCatalogueWriter.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Blackglass.BackendTools
{
    /// <summary>
    /// Writes a CatalogueDocument in the one canonical text form the backend also writes (CanonicalCatalogueJson in
    /// Backend/src/Accounts.Domain): UTF-8 without a BOM, LF line ends, two-space indentation, one member or element per
    /// line, members in a fixed order, "[]" for an empty array, invariant integers and one final newline. Every string is
    /// an id or an enum word, so nothing is escaped; anything else is refused. The document is written in the order it
    /// holds (CatalogueBuilder sorts). catalogueVersion is "sha256:" and the lowercase hex SHA-256 of these bytes.
    /// </summary>
    public static class CanonicalCatalogueWriter
    {
        public const int MaxIdLength = 64;
        static readonly Regex IdPattern = new Regex("^[a-z0-9]+(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant);
        static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public static bool IsWellFormedId(string value) =>
            value != null && value.Length > 0 && value.Length <= MaxIdLength && IdPattern.IsMatch(value);

        public static byte[] ToBytes(CatalogueDocument document) => Utf8NoBom.GetBytes(Write(document));

        public static string VersionOf(byte[] bytes)
        {
            byte[] hash;
            using (var sha = SHA256.Create())
                hash = sha.ComputeHash(bytes);
            var text = new StringBuilder("sha256:");
            foreach (var b in hash)
                text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return text.ToString();
        }

        public static string Write(CatalogueDocument d)
        {
            var root = new Obj()
                .Add("formatVersion", Int(CatalogueDocument.FormatVersion))
                .Add("items", Elements(d.Items, item => new Obj()
                    .Add("id", Str(item.Id))
                    .Add("category", Str(item.Category))
                    .Add("slot", Str(item.Slot))
                    .Add("stackable", Bool(item.Stackable))
                    .Add("maxStack", Int(item.MaxStack))
                    .Add("healAmount", Int(item.HealAmount))))
                .Add("lootTable", new Obj()
                    .Add("version", Int(d.LootTable.Version))
                    .Add("containerCount", Int(d.LootTable.ContainerCount))
                    .Add("minItems", Int(d.LootTable.MinItems))
                    .Add("maxItems", Int(d.LootTable.MaxItems))
                    .Add("entries", Elements(d.LootTable.Entries, entry => new Obj()
                        .Add("itemId", Str(entry.ItemId))
                        .Add("minQuantity", Int(entry.MinQuantity))
                        .Add("maxQuantity", Int(entry.MaxQuantity))
                        .Add("weight", Int(entry.Weight)))))
                .Add("progression", new Obj()
                    .Add("xpThresholds", Elements(d.Progression.XpThresholds, Int))
                    .Add("missionCompletionXp", Int(d.Progression.MissionCompletionXp))
                    .Add("choiceIds", Elements(d.Progression.ChoiceIds, Str)))
                .Add("operatives", Elements(d.Operatives, operative => new Obj()
                    .Add("id", Str(operative.Id))
                    .Add("roleId", Str(operative.RoleId))
                    .Add("abilityIds", Elements(operative.AbilityIds, Str))))
                .Add("starter", new Obj()
                    .Add("operatives", Elements(d.Starter.Operatives, starter => new Obj()
                        .Add("operativeId", Str(starter.OperativeId))
                        .Add("grants", Elements(starter.Grants, grant => new Obj()
                            .Add("itemId", Str(grant.ItemId))
                            .Add("quantity", Int(grant.Quantity))
                            .Add("equip", Bool(grant.Equip))))))
                    .Add("stash", Elements(d.Starter.Stash, grant => new Obj()
                        .Add("itemId", Str(grant.ItemId))
                        .Add("quantity", Int(grant.Quantity)))));
            var text = new StringBuilder();
            Render(text, root, 0);
            return text.Append('\n').ToString();
        }

        abstract class Node { }

        sealed class Obj : Node
        {
            public readonly List<KeyValuePair<string, Node>> Members = new List<KeyValuePair<string, Node>>();

            public Obj Add(string key, Node value)
            {
                Members.Add(new KeyValuePair<string, Node>(key, value));
                return this;
            }
        }

        sealed class Arr : Node
        {
            public readonly List<Node> Items = new List<Node>();
        }

        sealed class Literal : Node
        {
            public readonly string Text;
            public Literal(string text) { Text = text; }
        }

        static Node Int(int value) => new Literal(value.ToString(CultureInfo.InvariantCulture));

        static Node Bool(bool value) => new Literal(value ? "true" : "false");

        static Node Str(string value)
        {
            if (!IsWellFormedId(value))
                throw new ArgumentException($"'{value}' is not a well-formed catalogue id or word.", nameof(value));
            return new Literal("\"" + value + "\"");
        }

        static Node Elements<T>(IEnumerable<T> values, Func<T, Node> element)
        {
            var array = new Arr();
            foreach (var value in values)
                array.Items.Add(element(value));
            return array;
        }

        static void Render(StringBuilder text, Node node, int depth)
        {
            switch (node)
            {
                case Obj obj:
                    text.Append("{\n");
                    for (var i = 0; i < obj.Members.Count; i++)
                    {
                        Indent(text, depth + 1);
                        text.Append('"').Append(obj.Members[i].Key).Append("\": ");
                        Render(text, obj.Members[i].Value, depth + 1);
                        text.Append(i + 1 < obj.Members.Count ? ",\n" : "\n");
                    }
                    Indent(text, depth);
                    text.Append('}');
                    break;
                case Arr arr when arr.Items.Count == 0:
                    text.Append("[]");
                    break;
                case Arr arr:
                    text.Append("[\n");
                    for (var i = 0; i < arr.Items.Count; i++)
                    {
                        Indent(text, depth + 1);
                        Render(text, arr.Items[i], depth + 1);
                        text.Append(i + 1 < arr.Items.Count ? ",\n" : "\n");
                    }
                    Indent(text, depth);
                    text.Append(']');
                    break;
                case Literal literal:
                    text.Append(literal.Text);
                    break;
            }
        }

        static void Indent(StringBuilder text, int depth) => text.Append(' ', depth * 2);
    }
}
```

- [ ] **Step 7: Run the tests and see them pass.** Run `bash Tools/run-tests.sh EditMode "Blackglass.Tests.CatalogueExportTests"`. Expected: EXIT=0, 7 passed (2 from Task 1 and 5 new).
- [ ] **Step 8: Run the whole suite.** Run `bash Tools/run-tests.sh EditMode`. Expected: baseline + 7, 0 failed.
- [ ] **Step 9: Commit.**
  ```bash
  git add Assets/_Project/Editor/Backend Assets/_Project/Editor/Backend.meta \
    Assets/_Project/Tests/EditMode/Blackglass.Tests.EditMode.asmdef Assets/_Project/Tests/EditMode/CatalogueExportTests.cs \
    Backend/contracts/catalogue/golden-catalogue.json Backend/contracts/catalogue/golden-catalogue.version
  git commit -m "Phase 13 Stage 2: catalogue export model, canonical writer and the shared golden vector"
  ```

---

### Task 3: The builder: assets to model, with every check

**Files:**
- Create: `Assets/_Project/Editor/Backend/CatalogueSources.cs` and `Assets/_Project/Editor/Backend/CatalogueBuilder.cs`.
- Create: `Assets/_Project/Tests/EditMode/CatalogueBuilderTests.cs`.

**Interfaces:**
- Consumes:
  - from Task 1: `AbilityDefinition.Id`, `OperativeRole.Id`, `SetId` and `StarterLoadout.Operatives`;
  - from Task 2: the model, `CanonicalCatalogueWriter` and `SharedVectors`.
- Produces:
  - `CatalogueSources`, with the fields `Items`, `LootTable`, `Progression` and `Starter`, the list `Operatives`, the path constants and `static CatalogueSources LoadProject()`;
  - `CatalogueBuilder.Build(CatalogueSources sources, List<string> errors) : CatalogueDocument`, which returns null when refused;
  - the test fixture `GoldenAssets : IDisposable` (internal, in `CatalogueBuilderTests.cs`).

- [ ] **Step 1: Write the failing tests.** Create `Assets/_Project/Tests/EditMode/CatalogueBuilderTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Blackglass.BackendTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    /// <summary>
    /// The golden vector's catalogue as in-memory assets. Lists are deliberately scrambled (the export sorts sets by id),
    /// and every display name carries an optional prefix (the export must not see display names).
    /// </summary>
    internal sealed class GoldenAssets : IDisposable
    {
        public const string One = "00000000-0000-4000-8000-000000000001";
        public const string Two = "00000000-0000-4000-8000-000000000002";
        public const string Three = "00000000-0000-4000-8000-000000000003";

        readonly List<Object> created = new List<Object>();
        readonly string prefix;
        public readonly CombatArchetype Archetype;
        public readonly GameObject Prefab;
        public readonly ItemDefinition Rifle, Vest, Boots, Kit;
        public readonly AbilityDefinition AbilityOne, AbilityTwo;
        public readonly OperativeRole Alpha, Beta;
        public readonly OperativeDefinition OperativeOne, OperativeTwo;
        public readonly List<OperativeDefinition> Operatives = new List<OperativeDefinition>();
        public ItemCatalogue Items;
        public LootTable Loot;
        public ProgressionTrack Progression;
        public StarterLoadout Starter;

        public GoldenAssets(string displayPrefix = "")
        {
            prefix = displayPrefix;
            Archetype = Keep(CombatArchetype.Create(prefix + "Archetype", CombatRole.Ranged, 10f, 10, 1f));
            Prefab = Keep(new GameObject(prefix + "Prefab"));
            Rifle = Item("item.alpha-rifle", ItemCategory.Weapon, ItemSlot.Weapon, 1, 0, Archetype);
            Vest = Item("item.beta-vest", ItemCategory.Armor, ItemSlot.Armor);
            Boots = Item("item.delta-boots", ItemCategory.Utility, ItemSlot.Utility);
            Kit = Item("item.gamma-kit", ItemCategory.Consumable, ItemSlot.None, 3, 10);
            Items = Keep(ItemCatalogue.Create(Kit, Boots, Rifle, Vest));
            Loot = Keep(LootTable.Create(7, 2, 1, 3, new[] { Entry(Kit, 1, 2, 5), Entry(Rifle, 1, 1, 1) }));
            Progression = Keep(ProgressionTrack.Create(new[] { 0, 10, 30 }, new[] { Choice("choice.two"), Choice("choice.one") }, 20, 5));
            AbilityOne = Ability("ability.one");
            AbilityTwo = Ability("ability.two");
            Alpha = Role("role.alpha");
            Beta = Role("role.beta");
            OperativeOne = Operative(One, Alpha, AbilityTwo, AbilityOne);
            OperativeTwo = Operative(Two, Beta);
            Operatives.Add(OperativeTwo);
            Operatives.Add(OperativeOne);
            Starter = StarterFor(new[] { Grant(Rifle, 1, true), Grant(Kit, 2) }, new[] { Grant(Vest, 1), Grant(Kit, 4) }, OperativeOne);
        }

        public CatalogueSources Sources()
        {
            var sources = new CatalogueSources { Items = Items, LootTable = Loot, Progression = Progression, Starter = Starter };
            sources.Operatives.AddRange(Operatives);
            return sources;
        }

        public T Keep<T>(T asset) where T : Object
        {
            created.Add(asset);
            return asset;
        }

        public ItemDefinition Item(string id, ItemCategory category, ItemSlot slot, int maxStack = 1, int heal = 0, CombatArchetype weapon = null) =>
            Keep(ItemDefinition.Create(id, prefix + id, category, slot, maxStack, default, weapon, heal));

        public AbilityDefinition Ability(string id)
        {
            var ability = Keep(AbilityDefinition.Create(prefix + id, AbilityTargetMode.Unit, 10f, true, AbilityCoverRule.Applies, 5f, AbilityEffect.Damage, 10));
            ability.SetId(id);
            ability.name = id;
            return ability;
        }

        public OperativeRole Role(string id)
        {
            var role = Keep(OperativeRole.Create(prefix + id, "", default));
            role.SetId(id);
            role.name = id;
            return role;
        }

        public AdvancementChoice Choice(string id)
        {
            var choice = Keep(AdvancementChoice.Create(id, prefix + id, "", default));
            choice.name = id;
            return choice;
        }

        public OperativeDefinition Operative(string id, OperativeRole role, params AbilityDefinition[] abilities)
        {
            var operative = Keep(OperativeDefinition.Create(id, prefix + id, role, Prefab, 100, 5f, Archetype, abilities));
            operative.name = "operative-" + id;
            return operative;
        }

        public StarterLoadout StarterFor(StarterGrant[] grants, StarterGrant[] stash, params OperativeDefinition[] operatives)
        {
            var entries = operatives.Select(o => new StarterOperative { operative = o, items = grants }).ToArray();
            var starter = Keep(StarterLoadout.Create(entries, stash));
            starter.name = "starter";
            return starter;
        }

        public static LootEntry Entry(ItemDefinition item, int min, int max, int weight) =>
            new LootEntry { item = item, minQuantity = min, maxQuantity = max, weight = weight };

        public static StarterGrant Grant(ItemDefinition item, int quantity, bool equip = false) =>
            new StarterGrant { item = item, quantity = quantity, equip = equip };

        public void Dispose()
        {
            foreach (var asset in created)
            {
                if (asset != null)
                    Object.DestroyImmediate(asset);
            }
        }
    }

    public class CatalogueBuilderTests
    {
        GoldenAssets a;

        [SetUp]
        public void SetUp() => a = new GoldenAssets();

        [TearDown]
        public void TearDown() => a.Dispose();

        static void AssertRefused(CatalogueSources sources, string kind, string fragment)
        {
            var errors = new List<string>();
            Assert.That(CatalogueBuilder.Build(sources, errors), Is.Null, "the build should be refused");
            Assert.That(errors.Exists(e => e.StartsWith(kind, StringComparison.Ordinal) && e.Contains(fragment)), Is.True,
                $"expected a '{kind}' error containing '{fragment}', got:\n" + string.Join("\n", errors));
        }

        static void SetChoices(ProgressionTrack track, params AdvancementChoice[] choices)
        {
            var serialized = new SerializedObject(track);
            var list = serialized.FindProperty("choices");
            list.arraySize = choices.Length;
            for (var i = 0; i < choices.Length; i++)
                list.GetArrayElementAtIndex(i).objectReferenceValue = choices[i];
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        [Test]
        public void Build_FromTheGoldenAssets_WritesTheGoldenVectorByteForByte()
        {
            var errors = new List<string>();
            var document = CatalogueBuilder.Build(a.Sources(), errors);
            Assert.That(document, Is.Not.Null, string.Join("\n", errors));
            Assert.That(CanonicalCatalogueWriter.ToBytes(document).SequenceEqual(SharedVectors.GoldenBytes()), Is.True,
                CanonicalCatalogueWriter.Write(document));
        }

        [Test]
        public void Build_IgnoresDisplayNames()
        {
            using (var renamed = new GoldenAssets("Renamed "))
            {
                var errors = new List<string>();
                var original = CanonicalCatalogueWriter.ToBytes(CatalogueBuilder.Build(a.Sources(), errors));
                var other = CanonicalCatalogueWriter.ToBytes(CatalogueBuilder.Build(renamed.Sources(), errors));
                Assert.That(errors, Is.Empty);
                Assert.That(other.SequenceEqual(original), Is.True);
            }
        }

        [Test]
        public void Build_ListsEveryMissingSource()
        {
            var errors = new List<string>();
            Assert.That(CatalogueBuilder.Build(new CatalogueSources(), errors), Is.Null);
            Assert.That(errors.Count, Is.EqualTo(5), string.Join("\n", errors));
            Assert.That(errors.TrueForAll(e => e.StartsWith("missing: ", StringComparison.Ordinal)), Is.True);
        }

        [Test]
        public void Build_RefusesAnEmptyCatalogueEntry()
        {
            a.Items = a.Keep(ItemCatalogue.Create(a.Kit, null, a.Boots, a.Rifle, a.Vest));
            AssertRefused(a.Sources(), "missing:", "entry 1 is empty");
        }

        [Test]
        public void Build_RefusesAnInvalidItem()
        {
            a.Items = a.Keep(ItemCatalogue.Create(a.Kit, a.Boots, a.Rifle, a.Vest, a.Item("item.empty-kit", ItemCategory.Consumable, ItemSlot.None, 3, 0)));
            AssertRefused(a.Sources(), "malformed:", "a consumable needs a heal amount of at least 1");
        }

        [Test]
        public void Build_RefusesAMalformedItemId()
        {
            a.Items = a.Keep(ItemCatalogue.Create(a.Kit, a.Boots, a.Rifle, a.Vest, a.Item("Item.Upper", ItemCategory.Armor, ItemSlot.Armor)));
            AssertRefused(a.Sources(), "malformed:", "id 'Item.Upper'");
        }

        [Test]
        public void Build_RefusesTwoItemsWithOneId()
        {
            a.Items = a.Keep(ItemCatalogue.Create(a.Kit, a.Boots, a.Rifle, a.Vest, a.Item("item.beta-vest", ItemCategory.Armor, ItemSlot.Armor)));
            AssertRefused(a.Sources(), "duplicate:", "item id 'item.beta-vest' is on both");
        }

        [Test]
        public void Build_RefusesAnItemListedTwice()
        {
            a.Items = a.Keep(ItemCatalogue.Create(a.Kit, a.Boots, a.Rifle, a.Vest, a.Vest));
            AssertRefused(a.Sources(), "duplicate:", "is listed more than once");
        }

        [Test]
        public void Build_RefusesAnInvalidLootTable()
        {
            a.Loot = a.Keep(LootTable.Create(7, 2, 1, 3, new LootEntry[0]));
            AssertRefused(a.Sources(), "malformed:", "there are no entries");
        }

        [Test]
        public void Build_RefusesLootOutsideTheCatalogue()
        {
            var stray = a.Item("item.stray", ItemCategory.Armor, ItemSlot.Armor);
            a.Loot = a.Keep(LootTable.Create(7, 2, 1, 3, new[] { GoldenAssets.Entry(stray, 1, 1, 1) }));
            AssertRefused(a.Sources(), "incompatible:", "which is not in the item catalogue");
        }

        [Test]
        public void Build_RefusesAMalformedChoiceId()
        {
            a.Progression = a.Keep(ProgressionTrack.Create(new[] { 0, 10, 30 }, new[] { a.Choice("Choice One") }, 20, 5));
            AssertRefused(a.Sources(), "malformed:", "id 'Choice One'");
        }

        [Test]
        public void Build_RefusesAnEmptyChoiceSlot()
        {
            SetChoices(a.Progression, a.Choice("choice.one"), null);
            AssertRefused(a.Sources(), "missing:", "choice 1 is empty");
        }

        [Test]
        public void Build_RefusesTwoChoicesWithOneId()
        {
            SetChoices(a.Progression, a.Choice("choice.one"), a.Choice("choice.one"));
            AssertRefused(a.Sources(), "duplicate:", "choice id 'choice.one'");
        }

        [Test]
        public void Build_RefusesAnInvalidOperative()
        {
            a.Operatives.Add(a.Keep(OperativeDefinition.Create(GoldenAssets.Three, "Three", a.Alpha, null, 100, 5f, a.Archetype, new AbilityDefinition[0])));
            AssertRefused(a.Sources(), "malformed:", "there is no unit prefab");
        }

        [Test]
        public void Build_RefusesAnUppercaseOperativeId()
        {
            a.Operatives.Add(a.Operative("00000000-0000-4000-8000-00000000000A", a.Alpha));
            AssertRefused(a.Sources(), "malformed:", "id '00000000-0000-4000-8000-00000000000A'");
        }

        [Test]
        public void Build_RefusesTwoOperativesWithOneId()
        {
            var copy = a.Operative(GoldenAssets.One, a.Beta);
            copy.name = "copied-operative";
            a.Operatives.Add(copy);
            AssertRefused(a.Sources(), "duplicate:", $"operative id '{GoldenAssets.One}' is on both operative-{GoldenAssets.One} and copied-operative");
        }

        [Test]
        public void Build_RefusesARoleWithoutAnId()
        {
            a.Alpha.SetId("");
            AssertRefused(a.Sources(), "missing:", "role asset role.alpha (used by operative");
        }

        [Test]
        public void Build_RefusesAnAbilityWithoutAnId()
        {
            a.AbilityOne.SetId("");
            AssertRefused(a.Sources(), "missing:", "ability asset ability.one");
        }

        [Test]
        public void Build_RefusesTwoAbilityAssetsWithOneId()
        {
            a.Operatives.Add(a.Operative(GoldenAssets.Three, a.Beta, a.Ability("ability.one")));
            AssertRefused(a.Sources(), "duplicate:", "ability id 'ability.one' is on both");
        }

        [Test]
        public void Build_RefusesTheSameAbilityTwiceOnOneOperative()
        {
            a.Operatives.Add(a.Operative(GoldenAssets.Three, a.Beta, a.AbilityOne, a.AbilityOne));
            AssertRefused(a.Sources(), "duplicate:", "lists ability ability.one more than once");
        }

        [Test]
        public void Build_RefusesAStarterOperativeThatIsNotExported()
        {
            var outsider = a.Operative(GoldenAssets.Three, a.Beta);
            a.Starter = a.StarterFor(new StarterGrant[0], new StarterGrant[0], outsider);
            AssertRefused(a.Sources(), "incompatible:", "which is not an exported operative definition");
        }

        [Test]
        public void Build_RefusesAStarterOperativeListedTwice()
        {
            a.Starter = a.StarterFor(new StarterGrant[0], new StarterGrant[0], a.OperativeOne, a.OperativeOne);
            AssertRefused(a.Sources(), "duplicate:", "lists operative");
        }

        [Test]
        public void Build_RefusesAGrantWithoutAnItem()
        {
            a.Starter = a.StarterFor(new[] { GoldenAssets.Grant(null, 1) }, new StarterGrant[0], a.OperativeOne);
            AssertRefused(a.Sources(), "missing:", "grant 0 has no item");
        }

        [Test]
        public void Build_RefusesAGrantOutsideTheCatalogue()
        {
            var stray = a.Item("item.stray", ItemCategory.Armor, ItemSlot.Armor);
            a.Starter = a.StarterFor(new[] { GoldenAssets.Grant(stray, 1) }, new StarterGrant[0], a.OperativeOne);
            AssertRefused(a.Sources(), "incompatible:", "is not in the item catalogue");
        }

        [Test]
        public void Build_RefusesAQuantityBelowOne()
        {
            a.Starter = a.StarterFor(new[] { GoldenAssets.Grant(a.Kit, 0) }, new StarterGrant[0], a.OperativeOne);
            AssertRefused(a.Sources(), "malformed:", "quantity 0 is below 1");
        }

        [Test]
        public void Build_RefusesEquippingAConsumable()
        {
            a.Starter = a.StarterFor(new[] { GoldenAssets.Grant(a.Kit, 1, true) }, new StarterGrant[0], a.OperativeOne);
            AssertRefused(a.Sources(), "incompatible:", "cannot be equipped");
        }

        [Test]
        public void Build_RefusesTwoEquipsInOneSlot()
        {
            a.Starter = a.StarterFor(new[] { GoldenAssets.Grant(a.Rifle, 1, true), GoldenAssets.Grant(a.Rifle, 1, true) }, new StarterGrant[0], a.OperativeOne);
            AssertRefused(a.Sources(), "incompatible:", "a second item to equip in the Weapon slot");
        }

        [Test]
        public void Build_RefusesAnEquippedStashGrant()
        {
            a.Starter = a.StarterFor(new StarterGrant[0], new[] { GoldenAssets.Grant(a.Vest, 1, true) }, a.OperativeOne);
            AssertRefused(a.Sources(), "incompatible:", "a stash item cannot be equipped");
        }

        [Test]
        public void Build_ReportsEveryProblemAtOnce()
        {
            a.Alpha.SetId("");
            var stray = a.Item("item.stray", ItemCategory.Armor, ItemSlot.Armor);
            a.Loot = a.Keep(LootTable.Create(7, 2, 1, 3, new[] { GoldenAssets.Entry(stray, 1, 1, 1) }));
            AssertRefused(a.Sources(), "missing:", "role asset role.alpha");
            AssertRefused(a.Sources(), "incompatible:", "which is not in the item catalogue");
        }
    }
}
```

- [ ] **Step 2: Run the tests and see them fail.** Run `bash Tools/run-tests.sh EditMode "Blackglass.Tests.CatalogueBuilderTests"`. Expected: EXIT=1, a compile error because `CatalogueSources` and `CatalogueBuilder` are missing.

- [ ] **Step 3: Write the sources.** `Assets/_Project/Editor/Backend/CatalogueSources.cs`:

```csharp
using System.Collections.Generic;
using UnityEditor;

namespace Blackglass.BackendTools
{
    /// <summary>
    /// The authored assets the backend catalogue is exported from. LoadProject reads the project's fixed paths and every
    /// OperativeDefinition under the definitions folder; a missing asset stays null and CatalogueBuilder reports it.
    /// </summary>
    public sealed class CatalogueSources
    {
        public const string ItemCataloguePath = "Assets/_Project/Data/Items/ItemCatalogue.asset";
        public const string LootTablePath = "Assets/_Project/Data/Items/LootTable.asset";
        public const string StarterPath = "Assets/_Project/Data/Items/StarterLoadout.asset";
        public const string ProgressionPath = "Assets/_Project/Data/Operatives/Progression.asset";
        public const string OperativeFolder = "Assets/_Project/Data/Operatives/Definitions";

        public ItemCatalogue Items;
        public LootTable LootTable;
        public ProgressionTrack Progression;
        public StarterLoadout Starter;
        public readonly List<OperativeDefinition> Operatives = new List<OperativeDefinition>();

        public static CatalogueSources LoadProject()
        {
            var sources = new CatalogueSources
            {
                Items = AssetDatabase.LoadAssetAtPath<ItemCatalogue>(ItemCataloguePath),
                LootTable = AssetDatabase.LoadAssetAtPath<LootTable>(LootTablePath),
                Progression = AssetDatabase.LoadAssetAtPath<ProgressionTrack>(ProgressionPath),
                Starter = AssetDatabase.LoadAssetAtPath<StarterLoadout>(StarterPath),
            };
            foreach (var guid in AssetDatabase.FindAssets("t:OperativeDefinition", new[] { OperativeFolder }))
                sources.Operatives.Add(AssetDatabase.LoadAssetAtPath<OperativeDefinition>(AssetDatabase.GUIDToAssetPath(guid)));
            return sources;
        }
    }
}
```

- [ ] **Step 4: Write the builder.** `Assets/_Project/Editor/Backend/CatalogueBuilder.cs`:

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass.BackendTools
{
    /// <summary>
    /// Turns the authored assets into a CatalogueDocument. It checks everything the backend's validator checks, plus what
    /// only the assets show: which assets share an id, and references to an asset other than the exported one. Every
    /// problem is collected; the result is null when there is any. Each message starts with its kind: "missing:",
    /// "duplicate:", "malformed:" or "incompatible:". Sets (items, choices, operatives, starter operatives) are sorted by
    /// id, so asset order never changes the export; lists whose order means something (loot entries, an operative's
    /// abilities, grants, the stash) keep the authored order. Display names are never read.
    /// </summary>
    public static class CatalogueBuilder
    {
        const string IdRule = "is not a well-formed id (lowercase letters and digits separated by single '.' or '-', at most 64 characters)";

        public static CatalogueDocument Build(CatalogueSources sources, List<string> errors)
        {
            var start = errors.Count;
            if (sources.Items == null) errors.Add($"missing: the item catalogue ({CatalogueSources.ItemCataloguePath})");
            if (sources.LootTable == null) errors.Add($"missing: the loot table ({CatalogueSources.LootTablePath})");
            if (sources.Progression == null) errors.Add($"missing: the progression track ({CatalogueSources.ProgressionPath})");
            if (sources.Starter == null) errors.Add($"missing: the starter loadout ({CatalogueSources.StarterPath})");
            if (sources.Operatives.Count == 0) errors.Add($"missing: no operative definitions in {CatalogueSources.OperativeFolder}");
            if (errors.Count > start)
                return null;

            var document = new CatalogueDocument();
            var items = AddItems(sources.Items, document, errors);
            AddLootTable(sources.LootTable, items, document, errors);
            AddProgression(sources.Progression, document, errors);
            var operatives = AddOperatives(sources.Operatives, document, errors);
            AddStarter(sources.Starter, items, operatives, document, errors);
            return errors.Count > start ? null : document;
        }

        static Dictionary<string, ItemDefinition> AddItems(ItemCatalogue catalogue, CatalogueDocument document, List<string> errors)
        {
            var byId = new Dictionary<string, ItemDefinition>(StringComparer.Ordinal);
            var items = catalogue.Items;
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null) { errors.Add($"missing: item catalogue {catalogue.name} entry {i} is empty"); continue; }
                if (!item.IsValid(out var problem)) { errors.Add($"malformed: item asset {item.name}: {problem}"); continue; }
                if (!CanonicalCatalogueWriter.IsWellFormedId(item.Id)) { errors.Add($"malformed: item asset {item.name}: id '{item.Id}' {IdRule}"); continue; }
                if (byId.TryGetValue(item.Id, out var other))
                {
                    errors.Add(ReferenceEquals(other, item)
                        ? $"duplicate: item asset {item.name} is listed more than once in {catalogue.name}"
                        : $"duplicate: item id '{item.Id}' is on both {other.name} and {item.name}");
                    continue;
                }
                var category = CategoryWord(item.Category);
                var slot = SlotWord(item.Slot);
                if (category == null || slot == null)
                {
                    errors.Add($"incompatible: item asset {item.name}: category {(int)item.Category} or slot {(int)item.Slot} has no catalogue word");
                    continue;
                }
                byId.Add(item.Id, item);
                document.Items.Add(new CatalogueItem
                {
                    Id = item.Id, Category = category, Slot = slot, Stackable = item.IsStackable, MaxStack = item.MaxStack, HealAmount = item.HealAmount,
                });
            }
            document.Items.Sort((x, y) => string.CompareOrdinal(x.Id, y.Id));
            return byId;
        }

        static void AddLootTable(LootTable table, Dictionary<string, ItemDefinition> items, CatalogueDocument document, List<string> errors)
        {
            if (!table.IsValid(out var problem)) { errors.Add($"malformed: loot table {table.name}: {problem}"); return; }
            if (table.Version < 1) { errors.Add($"malformed: loot table {table.name}: the version must be at least 1"); return; }
            var row = document.LootTable;
            row.Version = table.Version;
            row.ContainerCount = table.ContainerCount;
            row.MinItems = table.MinItems;
            row.MaxItems = table.MaxItems;
            for (var i = 0; i < table.Entries.Length; i++)
            {
                var entry = table.Entries[i];
                if (!InCatalogue(entry.item, items))
                {
                    errors.Add($"incompatible: loot table {table.name} entry {i} uses item asset {entry.item.name}, which is not in the item catalogue");
                    continue;
                }
                row.Entries.Add(new CatalogueLootEntry { ItemId = entry.item.Id, MinQuantity = entry.minQuantity, MaxQuantity = entry.maxQuantity, Weight = entry.weight });
            }
        }

        static void AddProgression(ProgressionTrack track, CatalogueDocument document, List<string> errors)
        {
            var row = document.Progression;
            for (var rank = 1; rank <= track.MaxRank; rank++)
                row.XpThresholds.Add(track.XpForRank(rank));
            // The asset's OnValidate and Create already enforce these; the export checks again because the backend will.
            if (row.XpThresholds[0] != 0)
                errors.Add($"malformed: progression track {track.name}: rank 1 must need 0 XP");
            for (var i = 1; i < row.XpThresholds.Count; i++)
            {
                if (row.XpThresholds[i] > row.XpThresholds[i - 1])
                    continue;
                errors.Add($"malformed: progression track {track.name}: XP thresholds must strictly increase (rank {i + 1})");
                break;
            }
            if (track.MissionCompletionXp < 0)
                errors.Add($"malformed: progression track {track.name}: mission completion XP is negative");
            row.MissionCompletionXp = track.MissionCompletionXp;

            var byId = new Dictionary<string, AdvancementChoice>(StringComparer.Ordinal);
            for (var i = 0; i < track.Choices.Count; i++)
            {
                var choice = track.Choices[i];
                if (choice == null) { errors.Add($"missing: progression track {track.name} choice {i} is empty"); continue; }
                if (!CanonicalCatalogueWriter.IsWellFormedId(choice.Id)) { errors.Add($"malformed: choice asset {choice.name}: id '{choice.Id}' {IdRule}"); continue; }
                if (byId.TryGetValue(choice.Id, out var other)) { errors.Add($"duplicate: choice id '{choice.Id}' is on both {other.name} and {choice.name}"); continue; }
                byId.Add(choice.Id, choice);
                row.ChoiceIds.Add(choice.Id);
            }
            row.ChoiceIds.Sort(string.CompareOrdinal);
        }

        static Dictionary<string, OperativeDefinition> AddOperatives(List<OperativeDefinition> definitions, CatalogueDocument document, List<string> errors)
        {
            var byId = new Dictionary<string, OperativeDefinition>(StringComparer.Ordinal);
            var roles = new Dictionary<string, OperativeRole>(StringComparer.Ordinal);
            var abilities = new Dictionary<string, AbilityDefinition>(StringComparer.Ordinal);
            foreach (var definition in definitions)
            {
                if (definition == null) { errors.Add($"missing: an operative definition in {CatalogueSources.OperativeFolder} could not be loaded"); continue; }
                if (!definition.IsValid(out var problem)) { errors.Add($"malformed: operative asset {definition.name}: {problem}"); continue; }
                if (!CanonicalCatalogueWriter.IsWellFormedId(definition.Id)) { errors.Add($"malformed: operative asset {definition.name}: id '{definition.Id}' {IdRule}"); continue; }
                if (byId.TryGetValue(definition.Id, out var twin))
                {
                    errors.Add($"duplicate: operative id '{definition.Id}' is on both {twin.name} and {definition.name} (use \"Generate new id\" on a copied asset)");
                    continue;
                }
                var row = new CatalogueOperative { Id = definition.Id, RoleId = definition.Role.Id };
                var ok = Keyed(definition.Role, definition.Role.Id, "role", definition, roles, errors);
                var seen = new HashSet<AbilityDefinition>();
                foreach (var ability in definition.Abilities)
                {
                    if (!Keyed(ability, ability.Id, "ability", definition, abilities, errors)) { ok = false; continue; }
                    if (!seen.Add(ability)) { errors.Add($"duplicate: operative asset {definition.name} lists ability {ability.name} more than once"); ok = false; continue; }
                    row.AbilityIds.Add(ability.Id);
                }
                if (!ok)
                    continue;
                byId.Add(definition.Id, definition);
                document.Operatives.Add(row);
            }
            document.Operatives.Sort((x, y) => string.CompareOrdinal(x.Id, y.Id));
            return byId;
        }

        // A role or ability, which the catalogue names by its own id: the id must be authored, well-formed and not shared
        // with a different asset.
        static bool Keyed<T>(T asset, string id, string kind, OperativeDefinition user, Dictionary<string, T> seen, List<string> errors) where T : UnityEngine.Object
        {
            if (string.IsNullOrWhiteSpace(id)) { errors.Add($"missing: {kind} asset {asset.name} (used by operative {user.name}) has no id"); return false; }
            if (!CanonicalCatalogueWriter.IsWellFormedId(id)) { errors.Add($"malformed: {kind} asset {asset.name}: id '{id}' {IdRule}"); return false; }
            if (seen.TryGetValue(id, out var other) && !ReferenceEquals(other, asset)) { errors.Add($"duplicate: {kind} id '{id}' is on both {other.name} and {asset.name}"); return false; }
            seen[id] = asset;
            return true;
        }

        static void AddStarter(StarterLoadout starter, Dictionary<string, ItemDefinition> items, Dictionary<string, OperativeDefinition> operatives,
            CatalogueDocument document, List<string> errors)
        {
            var listed = new HashSet<string>(StringComparer.Ordinal);
            var entries = starter.Operatives;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry == null || entry.operative == null) { errors.Add($"missing: starter loadout {starter.name} entry {i} has no operative"); continue; }
                var operative = entry.operative;
                if (operative.Id == null || !operatives.TryGetValue(operative.Id, out var known) || !ReferenceEquals(known, operative))
                {
                    errors.Add($"incompatible: starter loadout {starter.name} entry {i} names operative asset {operative.name}, which is not an exported operative definition");
                    continue;
                }
                if (!listed.Add(operative.Id)) { errors.Add($"duplicate: starter loadout {starter.name} lists operative {operative.name} more than once"); continue; }
                var row = new CatalogueStarterOperative { OperativeId = operative.Id };
                var equipped = new HashSet<ItemSlot>();
                var grants = entry.items ?? new StarterGrant[0];
                for (var j = 0; j < grants.Length; j++)
                {
                    var grant = grants[j];
                    var where = $"starter loadout {starter.name}, operative {operative.name}, grant {j}";
                    if (!GrantIsUsable(grant.item, grant.quantity, where, items, errors))
                        continue;
                    if (grant.equip && grant.item.Slot == ItemSlot.None) { errors.Add($"incompatible: {where}: item {grant.item.Id} has no equipment slot, so it cannot be equipped"); continue; }
                    if (grant.equip && !equipped.Add(grant.item.Slot)) { errors.Add($"incompatible: {where}: a second item to equip in the {grant.item.Slot} slot"); continue; }
                    row.Grants.Add(new CatalogueGrant { ItemId = grant.item.Id, Quantity = grant.quantity, Equip = grant.equip });
                }
                document.Starter.Operatives.Add(row);
            }
            document.Starter.Operatives.Sort((x, y) => string.CompareOrdinal(x.OperativeId, y.OperativeId));

            var stash = starter.Stash;
            for (var k = 0; k < stash.Count; k++)
            {
                var grant = stash[k];
                var where = $"starter loadout {starter.name}, stash grant {k}";
                if (!GrantIsUsable(grant.item, grant.quantity, where, items, errors))
                    continue;
                if (grant.equip) { errors.Add($"incompatible: {where}: a stash item cannot be equipped"); continue; }
                document.Starter.Stash.Add(new CatalogueStashGrant { ItemId = grant.item.Id, Quantity = grant.quantity });
            }
        }

        static bool GrantIsUsable(ItemDefinition item, int quantity, string where, Dictionary<string, ItemDefinition> items, List<string> errors)
        {
            if (item == null) { errors.Add($"missing: {where} has no item"); return false; }
            if (!InCatalogue(item, items)) { errors.Add($"incompatible: {where}: item asset {item.name} is not in the item catalogue"); return false; }
            if (quantity < 1) { errors.Add($"malformed: {where}: quantity {quantity} is below 1"); return false; }
            return true;
        }

        // The exported definition with this id, and the very same asset (Unity's == for a missing reference).
        static bool InCatalogue(ItemDefinition item, Dictionary<string, ItemDefinition> items) =>
            item != null && item.Id != null && items.TryGetValue(item.Id, out var known) && ReferenceEquals(known, item);

        static string CategoryWord(ItemCategory category)
        {
            switch (category)
            {
                case ItemCategory.Weapon: return "weapon";
                case ItemCategory.Armor: return "armor";
                case ItemCategory.Utility: return "utility";
                case ItemCategory.Consumable: return "consumable";
                default: return null;
            }
        }

        static string SlotWord(ItemSlot slot)
        {
            switch (slot)
            {
                case ItemSlot.None: return "none";
                case ItemSlot.Weapon: return "weapon";
                case ItemSlot.Armor: return "armor";
                case ItemSlot.Utility: return "utility";
                default: return null;
            }
        }
    }
}
```

  Unity's overloaded `==` is used on purpose for every asset null check (`item == null`, `entry.operative == null`): a missing reference in a serialized array is a fake null (see the project's Unity test-run notes). Never use `??` or `?.` on a `UnityEngine.Object`.

- [ ] **Step 5: Run the tests and see them pass.** Run `bash Tools/run-tests.sh EditMode "Blackglass.Tests.CatalogueBuilderTests"`. Expected: EXIT=0, 29 passed.
- [ ] **Step 6: Run the whole suite.** Run `bash Tools/run-tests.sh EditMode`. Expected: baseline + 36, 0 failed.
- [ ] **Step 7: Commit.**
  ```bash
  git add Assets/_Project/Editor/Backend/CatalogueSources.cs Assets/_Project/Editor/Backend/CatalogueSources.cs.meta \
    Assets/_Project/Editor/Backend/CatalogueBuilder.cs Assets/_Project/Editor/Backend/CatalogueBuilder.cs.meta \
    Assets/_Project/Tests/EditMode/CatalogueBuilderTests.cs Assets/_Project/Tests/EditMode/CatalogueBuilderTests.cs.meta
  git commit -m "Phase 13 Stage 2: catalogue builder checks missing, duplicate, malformed and incompatible definitions"
  ```

---

### Task 4: The exporter and the committed catalogue

**Files:**
- Create: `Assets/_Project/Editor/Backend/CatalogueExporter.cs` and `Backend/catalogue/catalogue.json` (generated).
- Modify: `Assets/_Project/Tests/EditMode/CatalogueExportTests.cs`.

**Interfaces:**
- Consumes: `CatalogueSources.LoadProject`, `CatalogueBuilder.Build` and `CanonicalCatalogueWriter`.
- Produces:
  - `CatalogueExporter.Export(CatalogueSources, List<string>) : byte[]`, which returns null when refused;
  - `CatalogueExporter.CommittedPath`;
  - the menu item `Blackglass/Backend/Export Catalogue`;
  - the batch entry `Blackglass.BackendTools.CatalogueExporter.ExportFromCommandLine`.

- [ ] **Step 1: Write the failing tests.** Add to `CatalogueExportTests`:

```csharp
        [Test]
        public void CommittedCatalogue_EqualsAFreshExportOfTheProject()
        {
            var errors = new List<string>();
            var fresh = CatalogueExporter.Export(CatalogueSources.LoadProject(), errors);
            Assert.That(fresh, Is.Not.Null, "the project's catalogue export is refused:\n" + string.Join("\n", errors));
            TestContext.WriteLine("catalogueVersion (fresh export): " + CanonicalCatalogueWriter.VersionOf(fresh));
            Assert.That(File.Exists(CatalogueExporter.CommittedPath), Is.True,
                "Backend/catalogue/catalogue.json is missing: run Blackglass > Backend > Export Catalogue and commit it.");
            Assert.That(File.ReadAllBytes(CatalogueExporter.CommittedPath).SequenceEqual(fresh), Is.True,
                "Backend/catalogue/catalogue.json is stale or was edited by hand: run Blackglass > Backend > Export Catalogue " +
                "(or Tools/run-method.sh Blackglass.BackendTools.CatalogueExporter.ExportFromCommandLine) and commit the result.");
        }

        [Test]
        public void ProjectExport_IsTheSameEveryTime()
        {
            var errors = new List<string>();
            var first = CatalogueExporter.Export(CatalogueSources.LoadProject(), errors);
            var second = CatalogueExporter.Export(CatalogueSources.LoadProject(), errors);
            Assert.That(errors, Is.Empty, string.Join("\n", errors));
            Assert.That(second.SequenceEqual(first), Is.True);
        }

        [Test]
        public void CommittedCatalogue_NamesNoGameTitle()
        {
            var text = File.ReadAllText(CatalogueExporter.CommittedPath);
            Assert.That(text.IndexOf("blackglass", StringComparison.OrdinalIgnoreCase), Is.EqualTo(-1));
        }

        [Test]
        public void Export_ReturnsNoBytesWhenRefused()
        {
            using (var assets = new GoldenAssets())
            {
                assets.Alpha.SetId("");
                var errors = new List<string>();
                Assert.That(CatalogueExporter.Export(assets.Sources(), errors), Is.Null);
                Assert.That(errors, Is.Not.Empty);
            }
        }
```

- [ ] **Step 2: Run the tests and see them fail.** Run `bash Tools/run-tests.sh EditMode "Blackglass.Tests.CatalogueExportTests"`. Expected: EXIT=1, a compile error because `CatalogueExporter` is missing.

- [ ] **Step 3: Write the exporter.** `Assets/_Project/Editor/Backend/CatalogueExporter.cs`:

```csharp
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Blackglass.BackendTools
{
    /// <summary>
    /// Writes Backend/catalogue/catalogue.json from the project's authored data (Blackglass > Backend > Export Catalogue,
    /// or Tools/run-method.sh Blackglass.BackendTools.CatalogueExporter.ExportFromCommandLine with the Editor closed).
    /// Nothing is written when CatalogueBuilder refuses; every problem is logged. CatalogueExportTests fails while the
    /// committed file differs from a fresh export, and the backend refuses a file that isn't in canonical form.
    /// </summary>
    public static class CatalogueExporter
    {
        public static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;
        public static string CommittedPath => Path.Combine(ProjectRoot, "Backend", "catalogue", "catalogue.json");

        /// <summary>The canonical bytes of a fresh export, or null with every problem added to errors.</summary>
        public static byte[] Export(CatalogueSources sources, List<string> errors)
        {
            var document = CatalogueBuilder.Build(sources, errors);
            return document == null ? null : CanonicalCatalogueWriter.ToBytes(document);
        }

        [MenuItem("Blackglass/Backend/Export Catalogue")]
        static void ExportMenu()
        {
            if (TryExportProject(out var message))
                Debug.Log(message);
            else
                Debug.LogError(message);
        }

        /// <summary>Unity -batchmode -executeMethod entry: exit 0 after writing the file, 1 when the export is refused.</summary>
        public static void ExportFromCommandLine()
        {
            var ok = TryExportProject(out var message);
            if (ok)
                Debug.Log(message);
            else
                Debug.LogError(message);
            EditorApplication.Exit(ok ? 0 : 1);
        }

        static bool TryExportProject(out string message)
        {
            var errors = new List<string>();
            var bytes = Export(CatalogueSources.LoadProject(), errors);
            if (bytes == null)
            {
                message = "Catalogue export refused; nothing was written:\n" + string.Join("\n", errors);
                return false;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(CommittedPath));
            var temporary = CommittedPath + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            if (File.Exists(CommittedPath))
                File.Replace(temporary, CommittedPath, null);
            else
                File.Move(temporary, CommittedPath);
            message = $"Catalogue exported to {CommittedPath}: {CanonicalCatalogueWriter.VersionOf(bytes)}";
            return true;
        }
    }
}
```

- [ ] **Step 4: Run the export.** With the Editor closed, run `bash Tools/run-method.sh Blackglass.BackendTools.CatalogueExporter.ExportFromCommandLine`. Expected:
  - EXIT=0;
  - `Logs/Method-Blackglass_BackendTools_CatalogueExporter_ExportFromCommandLine.log` contains `Catalogue exported to …: sha256:…`;
  - `Backend/catalogue/catalogue.json` exists and `Backend/catalogue/catalogue.json.tmp` does not.

- [ ] **Step 5: Compare with the expected export.** With the ids proposed in Q1, the export must be exactly this text (4219 bytes, `sha256:916f1af1e1200f94abf52319c780e23dbed1278e9420272faa2c6bbb6ff41fab`):
  - Run `sha256sum Backend/catalogue/catalogue.json`.
  - A different hash means either the project data differs from what the planner read on 2026-10-14 or the writer differs from the golden rules. Stop and diff against the text below. Never hand-edit the file.

```json
{
  "formatVersion": 1,
  "items": [
    {
      "id": "item.combat-blade",
      "category": "weapon",
      "slot": "weapon",
      "stackable": false,
      "maxStack": 1,
      "healAmount": 0
    },
    {
      "id": "item.light-vest",
      "category": "armor",
      "slot": "armor",
      "stackable": false,
      "maxStack": 1,
      "healAmount": 0
    },
    {
      "id": "item.marksman-rifle",
      "category": "weapon",
      "slot": "weapon",
      "stackable": false,
      "maxStack": 1,
      "healAmount": 0
    },
    {
      "id": "item.medkit",
      "category": "consumable",
      "slot": "none",
      "stackable": true,
      "maxStack": 3,
      "healAmount": 40
    },
    {
      "id": "item.service-rifle",
      "category": "weapon",
      "slot": "weapon",
      "stackable": false,
      "maxStack": 1,
      "healAmount": 0
    },
    {
      "id": "item.servo-boots",
      "category": "utility",
      "slot": "utility",
      "stackable": false,
      "maxStack": 1,
      "healAmount": 0
    }
  ],
  "lootTable": {
    "version": 1,
    "containerCount": 3,
    "minItems": 1,
    "maxItems": 2,
    "entries": [
      {
        "itemId": "item.medkit",
        "minQuantity": 1,
        "maxQuantity": 2,
        "weight": 4
      },
      {
        "itemId": "item.service-rifle",
        "minQuantity": 1,
        "maxQuantity": 1,
        "weight": 1
      },
      {
        "itemId": "item.marksman-rifle",
        "minQuantity": 1,
        "maxQuantity": 1,
        "weight": 1
      },
      {
        "itemId": "item.combat-blade",
        "minQuantity": 1,
        "maxQuantity": 1,
        "weight": 1
      },
      {
        "itemId": "item.light-vest",
        "minQuantity": 1,
        "maxQuantity": 1,
        "weight": 2
      },
      {
        "itemId": "item.servo-boots",
        "minQuantity": 1,
        "maxQuantity": 1,
        "weight": 2
      }
    ]
  },
  "progression": {
    "xpThresholds": [
      0,
      100,
      250,
      450,
      700
    ],
    "missionCompletionXp": 150,
    "choiceIds": [
      "ability",
      "combat",
      "mobility",
      "survivability"
    ]
  },
  "operatives": [
    {
      "id": "2c84b7e9-51a6-4d03-9f7e-c3a0165d8b03",
      "roleId": "role.support",
      "abilityIds": [
        "ability.mend",
        "ability.aimed-shot"
      ]
    },
    {
      "id": "6f1d2a40-3b7c-4e95-8a1f-52c0d9e7b301",
      "roleId": "role.assault",
      "abilityIds": [
        "ability.aimed-shot",
        "ability.blast"
      ]
    },
    {
      "id": "a93e5c17-0d48-4f2b-b6a3-7e1c8d4f9a02",
      "roleId": "role.recon",
      "abilityIds": [
        "ability.aimed-shot",
        "ability.recon-scan"
      ]
    }
  ],
  "starter": {
    "operatives": [
      {
        "operativeId": "2c84b7e9-51a6-4d03-9f7e-c3a0165d8b03",
        "grants": [
          {
            "itemId": "item.service-rifle",
            "quantity": 1,
            "equip": true
          },
          {
            "itemId": "item.medkit",
            "quantity": 2,
            "equip": false
          }
        ]
      },
      {
        "operativeId": "6f1d2a40-3b7c-4e95-8a1f-52c0d9e7b301",
        "grants": [
          {
            "itemId": "item.service-rifle",
            "quantity": 1,
            "equip": true
          },
          {
            "itemId": "item.medkit",
            "quantity": 2,
            "equip": false
          }
        ]
      },
      {
        "operativeId": "a93e5c17-0d48-4f2b-b6a3-7e1c8d4f9a02",
        "grants": [
          {
            "itemId": "item.marksman-rifle",
            "quantity": 1,
            "equip": true
          },
          {
            "itemId": "item.medkit",
            "quantity": 2,
            "equip": false
          }
        ]
      }
    ],
    "stash": [
      {
        "itemId": "item.combat-blade",
        "quantity": 1
      },
      {
        "itemId": "item.service-rifle",
        "quantity": 1
      },
      {
        "itemId": "item.light-vest",
        "quantity": 1
      },
      {
        "itemId": "item.servo-boots",
        "quantity": 1
      },
      {
        "itemId": "item.medkit",
        "quantity": 4
      }
    ]
  }
}
```

- [ ] **Step 6: Run the tests and see them pass.** Run `bash Tools/run-tests.sh EditMode "Blackglass.Tests.CatalogueExportTests"`. Expected: EXIT=0, 11 passed.
- [ ] **Step 7: Run the whole suite.** Run `bash Tools/run-tests.sh EditMode`. Expected: baseline + 40, 0 failed. Then check the stored line ends with `git add -N Backend/catalogue/catalogue.json && git ls-files --eol Backend/catalogue/catalogue.json`. Expected: `i/lf w/lf attr/text=auto eol=lf`, which the existing `*.json eol=lf` rule supplies.
- [ ] **Step 8: Commit.**
  ```bash
  git add Assets/_Project/Editor/Backend/CatalogueExporter.cs Assets/_Project/Editor/Backend/CatalogueExporter.cs.meta \
    Assets/_Project/Tests/EditMode/CatalogueExportTests.cs Backend/catalogue/catalogue.json
  git commit -m "Phase 13 Stage 2: catalogue exporter and the committed catalogue.json; EditMode fails on a stale export"
  ```

---

### Task 5: Backend model, canonical writer and version (`Accounts.Domain`)

**Files:**
- Create:
  - `Backend/src/Accounts.Domain/Accounts.Domain.csproj`;
  - `Backend/src/Accounts.Domain/Catalogue/CatalogueDocument.cs`;
  - `Backend/src/Accounts.Domain/Catalogue/CanonicalCatalogueJson.cs`;
  - `Backend/src/Accounts.Domain/Catalogue/CatalogueVersion.cs`.
- Create (tests):
  - `Backend/tests/Accounts.Domain.Tests/Accounts.Domain.Tests.csproj`;
  - `Backend/tests/Accounts.Domain.Tests/Catalogue/Golden.cs`;
  - `Backend/tests/Accounts.Domain.Tests/Catalogue/CanonicalCatalogueJsonTests.cs`.
- Modify: `Backend/Backend.sln`.

**Interfaces:**
- Produces (namespace `Blackglass.Accounts.Domain.Catalogue`):
  - the enums `ItemCategory { Weapon, Armor, Utility, Consumable }` and `ItemSlot { None, Weapon, Armor, Utility }`;
  - the records `CatalogueItem(string Id, ItemCategory Category, ItemSlot Slot, bool Stackable, int MaxStack, int HealAmount)`, `LootEntry(string ItemId, int MinQuantity, int MaxQuantity, int Weight)`, `LootTable(int Version, int ContainerCount, int MinItems, int MaxItems, IReadOnlyList<LootEntry> Entries)`, `Progression(IReadOnlyList<int> XpThresholds, int MissionCompletionXp, IReadOnlyList<string> ChoiceIds)`, `OperativeDefinition(string Id, string RoleId, IReadOnlyList<string> AbilityIds)`, `StarterGrant(string ItemId, int Quantity, bool Equip)`, `StashGrant(string ItemId, int Quantity)`, `StarterOperative(string OperativeId, IReadOnlyList<StarterGrant> Grants)`, `Starter(IReadOnlyList<StarterOperative> Operatives, IReadOnlyList<StashGrant> Stash)` and `CatalogueDocument(int FormatVersion, IReadOnlyList<CatalogueItem> Items, LootTable LootTable, Progression Progression, IReadOnlyList<OperativeDefinition> Operatives, Starter Starter)` with `const int SupportedFormatVersion = 1`;
  - `CanonicalCatalogueJson.IsWellFormedId(string?)`, `Word(ItemCategory)`, `Word(ItemSlot)`, `Write(CatalogueDocument) : string` and `ToBytes(CatalogueDocument) : byte[]`;
  - `CatalogueVersion.Of(ReadOnlySpan<byte>) : string`.

- [ ] **Step 1: Create the projects.**
  - `Backend/src/Accounts.Domain/Accounts.Domain.csproj`:
    ```xml
    <Project Sdk="Microsoft.NET.Sdk">
      <PropertyGroup>
        <RootNamespace>Blackglass.Accounts.Domain</RootNamespace>
      </PropertyGroup>
    </Project>
    ```
    (Task 6 adds the embedded catalogue.)
  - `Backend/tests/Accounts.Domain.Tests/Accounts.Domain.Tests.csproj`:
    ```xml
    <Project Sdk="Microsoft.NET.Sdk">
      <PropertyGroup>
        <RootNamespace>Blackglass.Accounts.Domain.Tests</RootNamespace>
        <IsPackable>false</IsPackable>
      </PropertyGroup>
      <ItemGroup>
        <ProjectReference Include="..\..\src\Accounts.Domain\Accounts.Domain.csproj" />
      </ItemGroup>
      <ItemGroup>
        <Using Include="Xunit" />
      </ItemGroup>
      <ItemGroup>
        <PackageReference Include="Microsoft.NET.Test.Sdk" />
        <PackageReference Include="xunit" />
        <PackageReference Include="xunit.runner.visualstudio">
          <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
          <PrivateAssets>all</PrivateAssets>
        </PackageReference>
      </ItemGroup>
      <ItemGroup>
        <None Include="..\..\catalogue\catalogue.json" Link="catalogue\catalogue.json" CopyToOutputDirectory="PreserveNewest" />
        <None Include="..\..\contracts\catalogue\*" Link="contracts\catalogue\%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
      </ItemGroup>
    </Project>
    ```
  - From `Backend/`, run `dotnet sln Backend.sln add src/Accounts.Domain/Accounts.Domain.csproj tests/Accounts.Domain.Tests/Accounts.Domain.Tests.csproj`.

- [ ] **Step 2: Write the failing tests.**
  - `Backend/tests/Accounts.Domain.Tests/Catalogue/Golden.cs`:

```csharp
using Blackglass.Accounts.Domain.Catalogue;

namespace Blackglass.Accounts.Domain.Tests.Catalogue;

/// <summary>The shared golden vector (Backend/contracts/catalogue), which Unity's CatalogueExportTests checks too.</summary>
internal static class Golden
{
    public const string One = "00000000-0000-4000-8000-000000000001";
    public const string Two = "00000000-0000-4000-8000-000000000002";

    static string Folder => Path.Combine(AppContext.BaseDirectory, "contracts", "catalogue");
    public static byte[] Bytes() => File.ReadAllBytes(Path.Combine(Folder, "golden-catalogue.json"));
    public static string Version() => File.ReadAllText(Path.Combine(Folder, "golden-catalogue.version")).Trim();

    public static CatalogueDocument Document() => new(
        FormatVersion: 1,
        Items:
        [
            new("item.alpha-rifle", ItemCategory.Weapon, ItemSlot.Weapon, false, 1, 0),
            new("item.beta-vest", ItemCategory.Armor, ItemSlot.Armor, false, 1, 0),
            new("item.delta-boots", ItemCategory.Utility, ItemSlot.Utility, false, 1, 0),
            new("item.gamma-kit", ItemCategory.Consumable, ItemSlot.None, true, 3, 10),
        ],
        LootTable: new(7, 2, 1, 3, [new("item.gamma-kit", 1, 2, 5), new("item.alpha-rifle", 1, 1, 1)]),
        Progression: new([0, 10, 30], 20, ["choice.one", "choice.two"]),
        Operatives: [new(One, "role.alpha", ["ability.two", "ability.one"]), new(Two, "role.beta", [])],
        Starter: new(
            [new(One, [new("item.alpha-rifle", 1, true), new("item.gamma-kit", 2, false)])],
            [new("item.beta-vest", 1), new("item.gamma-kit", 4)]));
}
```

  - `Backend/tests/Accounts.Domain.Tests/Catalogue/CanonicalCatalogueJsonTests.cs`:

```csharp
using Blackglass.Accounts.Domain.Catalogue;

namespace Blackglass.Accounts.Domain.Tests.Catalogue;

public class CanonicalCatalogueJsonTests
{
    [Fact]
    public void The_writer_produces_the_shared_golden_vector()
        => Assert.Equal(Golden.Bytes(), CanonicalCatalogueJson.ToBytes(Golden.Document()));

    [Fact]
    public void The_version_is_the_shared_vectors_version()
    {
        Assert.Matches("^sha256:[0-9a-f]{64}$", Golden.Version());
        Assert.Equal(Golden.Version(), CatalogueVersion.Of(Golden.Bytes()));
    }

    [Fact]
    public void The_writer_refuses_a_string_that_is_not_an_id()
    {
        var d = Golden.Document();
        d = d with { Items = [d.Items[0] with { Id = "Item Alpha" }, .. d.Items.Skip(1)] };
        Assert.Throws<ArgumentException>(() => CanonicalCatalogueJson.Write(d));
    }
}
```

- [ ] **Step 3: Run the tests and see them fail.** From `Backend/`, run `bash ../.claude/scratchpad/phase13/tools/dt.sh test tests/Accounts.Domain.Tests/Accounts.Domain.Tests.csproj`. Expected: a build failure, because the types don't exist.

- [ ] **Step 4: Write the model.** `Backend/src/Accounts.Domain/Catalogue/CatalogueDocument.cs`:

```csharp
namespace Blackglass.Accounts.Domain.Catalogue;

public enum ItemCategory { Weapon, Armor, Utility, Consumable }

public enum ItemSlot { None, Weapon, Armor, Utility }

public sealed record CatalogueItem(string Id, ItemCategory Category, ItemSlot Slot, bool Stackable, int MaxStack, int HealAmount);

public sealed record LootEntry(string ItemId, int MinQuantity, int MaxQuantity, int Weight);

public sealed record LootTable(int Version, int ContainerCount, int MinItems, int MaxItems, IReadOnlyList<LootEntry> Entries);

public sealed record Progression(IReadOnlyList<int> XpThresholds, int MissionCompletionXp, IReadOnlyList<string> ChoiceIds);

public sealed record OperativeDefinition(string Id, string RoleId, IReadOnlyList<string> AbilityIds);

public sealed record StarterGrant(string ItemId, int Quantity, bool Equip);

public sealed record StashGrant(string ItemId, int Quantity);

public sealed record StarterOperative(string OperativeId, IReadOnlyList<StarterGrant> Grants);

public sealed record Starter(IReadOnlyList<StarterOperative> Operatives, IReadOnlyList<StashGrant> Stash);

/// <summary>
/// The catalogue exported from Unity (Backend/catalogue/catalogue.json), in the shape and order of the file. Ids and
/// rules only; display text stays in Unity. Lists keep the file's order.
/// </summary>
public sealed record CatalogueDocument(int FormatVersion, IReadOnlyList<CatalogueItem> Items, LootTable LootTable,
    Progression Progression, IReadOnlyList<OperativeDefinition> Operatives, Starter Starter)
{
    public const int SupportedFormatVersion = 1;
}
```

- [ ] **Step 5: Write the writer and the version.**
  - `Backend/src/Accounts.Domain/Catalogue/CanonicalCatalogueJson.cs`:

```csharp
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Blackglass.Accounts.Domain.Catalogue;

/// <summary>
/// The one canonical text form of a catalogue, identical to Unity's CanonicalCatalogueWriter (Assets/_Project/Editor/
/// Backend): UTF-8 without a BOM, LF line ends, two-space indentation, one member or element per line, members in a fixed
/// order, "[]" for an empty array, invariant integers and one final newline. Strings are ids or enum words, so nothing is
/// escaped; anything else is refused. The document is written in the order it holds.
/// </summary>
public static partial class CanonicalCatalogueJson
{
    public const int MaxIdLength = 64;

    [GeneratedRegex("^[a-z0-9]+(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    public static bool IsWellFormedId(string? value) => value is { Length: > 0 and <= MaxIdLength } && IdPattern().IsMatch(value);

    public static string Word(ItemCategory category) => category switch
    {
        ItemCategory.Weapon => "weapon",
        ItemCategory.Armor => "armor",
        ItemCategory.Utility => "utility",
        ItemCategory.Consumable => "consumable",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "No catalogue word for this category."),
    };

    public static string Word(ItemSlot slot) => slot switch
    {
        ItemSlot.None => "none",
        ItemSlot.Weapon => "weapon",
        ItemSlot.Armor => "armor",
        ItemSlot.Utility => "utility",
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "No catalogue word for this slot."),
    };

    public static byte[] ToBytes(CatalogueDocument document) => new UTF8Encoding(false).GetBytes(Write(document));

    public static string Write(CatalogueDocument d)
    {
        var root = new Obj()
            .Add("formatVersion", Int(d.FormatVersion))
            .Add("items", Elements(d.Items, item => new Obj()
                .Add("id", Str(item.Id))
                .Add("category", Str(Word(item.Category)))
                .Add("slot", Str(Word(item.Slot)))
                .Add("stackable", Bool(item.Stackable))
                .Add("maxStack", Int(item.MaxStack))
                .Add("healAmount", Int(item.HealAmount))))
            .Add("lootTable", new Obj()
                .Add("version", Int(d.LootTable.Version))
                .Add("containerCount", Int(d.LootTable.ContainerCount))
                .Add("minItems", Int(d.LootTable.MinItems))
                .Add("maxItems", Int(d.LootTable.MaxItems))
                .Add("entries", Elements(d.LootTable.Entries, entry => new Obj()
                    .Add("itemId", Str(entry.ItemId))
                    .Add("minQuantity", Int(entry.MinQuantity))
                    .Add("maxQuantity", Int(entry.MaxQuantity))
                    .Add("weight", Int(entry.Weight)))))
            .Add("progression", new Obj()
                .Add("xpThresholds", Elements(d.Progression.XpThresholds, Int))
                .Add("missionCompletionXp", Int(d.Progression.MissionCompletionXp))
                .Add("choiceIds", Elements(d.Progression.ChoiceIds, Str)))
            .Add("operatives", Elements(d.Operatives, operative => new Obj()
                .Add("id", Str(operative.Id))
                .Add("roleId", Str(operative.RoleId))
                .Add("abilityIds", Elements(operative.AbilityIds, Str))))
            .Add("starter", new Obj()
                .Add("operatives", Elements(d.Starter.Operatives, starter => new Obj()
                    .Add("operativeId", Str(starter.OperativeId))
                    .Add("grants", Elements(starter.Grants, grant => new Obj()
                        .Add("itemId", Str(grant.ItemId))
                        .Add("quantity", Int(grant.Quantity))
                        .Add("equip", Bool(grant.Equip))))))
                .Add("stash", Elements(d.Starter.Stash, grant => new Obj()
                    .Add("itemId", Str(grant.ItemId))
                    .Add("quantity", Int(grant.Quantity)))));
        var text = new StringBuilder();
        Render(text, root, 0);
        return text.Append('\n').ToString();
    }

    abstract class Node { }

    sealed class Obj : Node
    {
        public List<KeyValuePair<string, Node>> Members { get; } = [];

        public Obj Add(string key, Node value)
        {
            Members.Add(new(key, value));
            return this;
        }
    }

    sealed class Arr(List<Node> items) : Node
    {
        public List<Node> Items { get; } = items;
    }

    sealed class Literal(string text) : Node
    {
        public string Text { get; } = text;
    }

    static Node Int(int value) => new Literal(value.ToString(CultureInfo.InvariantCulture));

    static Node Bool(bool value) => new Literal(value ? "true" : "false");

    static Node Str(string value) => IsWellFormedId(value)
        ? new Literal("\"" + value + "\"")
        : throw new ArgumentException($"'{value}' is not a well-formed catalogue id or word.", nameof(value));

    static Node Elements<T>(IEnumerable<T> values, Func<T, Node> element) => new Arr([.. values.Select(element)]);

    static void Render(StringBuilder text, Node node, int depth)
    {
        switch (node)
        {
            case Obj obj:
                text.Append("{\n");
                for (var i = 0; i < obj.Members.Count; i++)
                {
                    text.Append(' ', (depth + 1) * 2).Append('"').Append(obj.Members[i].Key).Append("\": ");
                    Render(text, obj.Members[i].Value, depth + 1);
                    text.Append(i + 1 < obj.Members.Count ? ",\n" : "\n");
                }
                text.Append(' ', depth * 2).Append('}');
                break;
            case Arr { Items.Count: 0 }:
                text.Append("[]");
                break;
            case Arr arr:
                text.Append("[\n");
                for (var i = 0; i < arr.Items.Count; i++)
                {
                    text.Append(' ', (depth + 1) * 2);
                    Render(text, arr.Items[i], depth + 1);
                    text.Append(i + 1 < arr.Items.Count ? ",\n" : "\n");
                }
                text.Append(' ', depth * 2).Append(']');
                break;
            case Literal literal:
                text.Append(literal.Text);
                break;
        }
    }
}
```

  - `Backend/src/Accounts.Domain/Catalogue/CatalogueVersion.cs`:

```csharp
using System.Security.Cryptography;

namespace Blackglass.Accounts.Domain.Catalogue;

/// <summary>catalogueVersion: "sha256:" and the lowercase hex SHA-256 of the catalogue file's bytes (spec §4.6).</summary>
public static class CatalogueVersion
{
    public static string Of(ReadOnlySpan<byte> bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
}
```

- [ ] **Step 6: Run the tests and see them pass.** From `Backend/`, run `bash ../.claude/scratchpad/phase13/tools/dt.sh test tests/Accounts.Domain.Tests/Accounts.Domain.Tests.csproj`. Expected: 3 passed, 0 failed.
- [ ] **Step 7: Commit.**
  ```bash
  git add Backend/Backend.sln Backend/src/Accounts.Domain Backend/tests/Accounts.Domain.Tests
  git commit -m "Phase 13 Stage 2: Accounts.Domain catalogue model, canonical writer and version, checked against the golden vector"
  ```

---

### Task 6: Strict reader, validator and `GameCatalogue`

**Files:**
- Create:
  - `Backend/src/Accounts.Domain/Catalogue/CatalogueException.cs`;
  - `Backend/src/Accounts.Domain/Catalogue/CatalogueReader.cs`;
  - `Backend/src/Accounts.Domain/Catalogue/CatalogueValidator.cs`;
  - `Backend/src/Accounts.Domain/Catalogue/GameCatalogue.cs`.
- Create (tests): `Backend/tests/Accounts.Domain.Tests/Catalogue/CatalogueLoadTests.cs` and `CatalogueRejectionTests.cs`.
- Modify: `Backend/src/Accounts.Domain/Accounts.Domain.csproj` (embeds the catalogue).

**Interfaces:**
- Consumes: Task 5's model, `CanonicalCatalogueJson` and `CatalogueVersion`.
- Produces:
  - `CatalogueException(IReadOnlyList<string> errors)` with `Errors`;
  - `CatalogueReader.Read(ReadOnlyMemory<byte>, List<string>) : CatalogueDocument?`;
  - `CatalogueValidator.Validate(CatalogueDocument, List<string>)`;
  - `GameCatalogue`, with `Version`, `Document`, `FindItem(string)`, `FindOperative(string)`, `static Load(ReadOnlyMemory<byte>)`, `static Embedded` and `static EmbeddedBytes()`.

- [ ] **Step 1: Embed the catalogue.** Add to `Accounts.Domain.csproj`:
  ```xml
  <ItemGroup>
    <!-- The committed catalogue, embedded so the image carries exactly the file the tests checked. -->
    <EmbeddedResource Include="..\..\catalogue\catalogue.json" LogicalName="catalogue.json" />
  </ItemGroup>
  ```

- [ ] **Step 2: Write the failing tests.**
  - `Backend/tests/Accounts.Domain.Tests/Catalogue/CatalogueLoadTests.cs`:

```csharp
using System.Security.Cryptography;
using Blackglass.Accounts.Domain.Catalogue;
using Xunit.Abstractions;

namespace Blackglass.Accounts.Domain.Tests.Catalogue;

public class CatalogueLoadTests(ITestOutputHelper output)
{
    static string Committed => Path.Combine(AppContext.BaseDirectory, "catalogue", "catalogue.json");

    [Fact]
    public void The_golden_vector_loads_with_the_shared_version()
    {
        var catalogue = GameCatalogue.Load(Golden.Bytes());
        Assert.Equal(Golden.Version(), catalogue.Version);
        Assert.Equal(Golden.Bytes(), CanonicalCatalogueJson.ToBytes(catalogue.Document));
    }

    [Fact]
    public void The_committed_catalogue_loads_and_its_version_is_the_sha256_of_the_file()
    {
        var bytes = File.ReadAllBytes(Committed);
        var catalogue = GameCatalogue.Load(bytes);
        Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)), catalogue.Version);
        output.WriteLine($"catalogueVersion (committed file): {catalogue.Version}");
    }

    [Fact]
    public void The_embedded_catalogue_is_the_committed_file()
        => Assert.Equal(File.ReadAllBytes(Committed), GameCatalogue.EmbeddedBytes());

    [Fact]
    public void The_embedded_catalogue_loads()
        => Assert.Equal(GameCatalogue.Load(File.ReadAllBytes(Committed)).Version, GameCatalogue.Embedded.Version);

    [Fact]
    public void Lookups_find_items_and_operatives_by_id()
    {
        var catalogue = GameCatalogue.Load(Golden.Bytes());
        Assert.Equal(3, catalogue.FindItem("item.gamma-kit")!.MaxStack);
        Assert.Null(catalogue.FindItem("item.unknown"));
        Assert.Equal("role.beta", catalogue.FindOperative(Golden.Two)!.RoleId);
        Assert.Null(catalogue.FindOperative("00000000-0000-4000-8000-000000000009"));
    }
}
```

  - `Backend/tests/Accounts.Domain.Tests/Catalogue/CatalogueRejectionTests.cs`:

```csharp
using System.Text;
using Blackglass.Accounts.Domain.Catalogue;

namespace Blackglass.Accounts.Domain.Tests.Catalogue;

/// <summary>Every way a catalogue file can be wrong is refused with a message of the right kind.</summary>
public class CatalogueRejectionTests
{
    // Assert.Contains with a predicate (not Assert.True(…Any…)): xUnit's analyzers flag the latter, and warnings are errors.
    static void AssertRejected(byte[] bytes, string expected)
    {
        var e = Assert.Throws<CatalogueException>(() => GameCatalogue.Load(bytes));
        Assert.Contains(e.Errors, x => x.Contains(expected, StringComparison.Ordinal));
    }

    // The golden vector with one exact edit; the edit must match exactly one place.
    static byte[] Edited(string find, string replace)
    {
        var text = Encoding.UTF8.GetString(Golden.Bytes());
        var first = text.IndexOf(find, StringComparison.Ordinal);
        Assert.True(first >= 0 && text.IndexOf(find, first + 1, StringComparison.Ordinal) < 0, $"'{find}' must occur exactly once");
        return Encoding.UTF8.GetBytes(text.Replace(find, replace, StringComparison.Ordinal));
    }

    [Fact]
    public void A_byte_order_mark_is_rejected()
        => AssertRejected([0xEF, 0xBB, 0xBF, .. Golden.Bytes()], "malformed: the file starts with a byte-order mark");

    [Fact]
    public void Crlf_line_ends_are_rejected()
        => AssertRejected(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Golden.Bytes()).Replace("\n", "\r\n")), "malformed: the file contains a carriage return");

    [Fact]
    public void A_truncated_file_is_rejected()
    {
        var bytes = Golden.Bytes();
        AssertRejected(bytes[..(bytes.Length / 2)], "malformed: not valid JSON");
    }

    [Fact]
    public void A_root_that_is_not_an_object_is_rejected()
        => AssertRejected("[]\n"u8.ToArray(), "malformed: $ must be an object");

    [Theory]
    [InlineData("\"healAmount\": 10\n", "\"healAmount\": 10,\n      \"price\": 5\n", "malformed: $.items[3].price is not a known member")]
    [InlineData("\"maxStack\": 3,", "\"maxStack\": 3,\n      \"maxStack\": 3,", "duplicate: $.items[3].maxStack appears more than once")]
    [InlineData("\"maxQuantity\": 2,\n        \"weight\": 5", "\"maxQuantity\": 2", "missing: $.lootTable.entries[0].weight")]
    [InlineData("\"quantity\": 4", "\"quantity\": \"4\"", "malformed: $.starter.stash[1].quantity must be a 32-bit integer")]
    [InlineData("\"version\": 7,", "\"version\": 7.0,", "malformed: $.lootTable.version must be a 32-bit integer")]
    [InlineData("\"category\": \"armor\"", "\"category\": \"shield\"", "malformed: $.items[1].category must be one of")]
    [InlineData("\"equip\": true", "\"equip\": 1", "malformed: $.starter.operatives[0].grants[0].equip must be true or false")]
    [InlineData("\"weight\": 1\n", "\"weight\": 1,\n", "malformed: not valid JSON")]
    [InlineData("\"formatVersion\": 1,", "\"formatVersion\":  1,", "malformed: the file is not in canonical form")]
    [InlineData("\"stackable\": true,\n      \"maxStack\": 3,", "\"maxStack\": 3,\n      \"stackable\": true,", "malformed: the file is not in canonical form")]
    [InlineData("\"id\": \"item.beta-vest\"", "\"id\": \"item.Beta-vest\"", "malformed: items[1].id 'item.Beta-vest' is not a well-formed id")]
    [InlineData("\"formatVersion\": 1,", "\"formatVersion\": 2,", "incompatible: format version 2 is not supported (this backend reads 1)")]
    public void An_edited_golden_vector_is_rejected(string find, string replace, string expected)
        => AssertRejected(Edited(find, replace), expected);

    static CatalogueDocument Item(CatalogueDocument d, int index, Func<CatalogueItem, CatalogueItem> change) =>
        d with { Items = [.. d.Items.Select((item, i) => i == index ? change(item) : item)] };

    static CatalogueDocument Loot(CatalogueDocument d, Func<LootTable, LootTable> change) => d with { LootTable = change(d.LootTable) };

    static CatalogueDocument Track(CatalogueDocument d, Func<Progression, Progression> change) => d with { Progression = change(d.Progression) };

    static CatalogueDocument Op(CatalogueDocument d, int index, Func<OperativeDefinition, OperativeDefinition> change) =>
        d with { Operatives = [.. d.Operatives.Select((o, i) => i == index ? change(o) : o)] };

    static CatalogueDocument Start(CatalogueDocument d, Func<Starter, Starter> change) => d with { Starter = change(d.Starter) };

    static CatalogueDocument Grants(CatalogueDocument d, Func<IReadOnlyList<StarterGrant>, IReadOnlyList<StarterGrant>> change) =>
        Start(d, s => s with { Operatives = [s.Operatives[0] with { Grants = change(s.Operatives[0].Grants) }] });

    const string Unknown = "00000000-0000-4000-8000-000000000009";

    // Faults the writer can express (canonical text), so only the validator can catch them.
    static readonly Dictionary<string, (Func<CatalogueDocument, CatalogueDocument> Mutate, string Expected)> Mutations = new()
    {
        ["duplicate item"] = (d => d with { Items = [.. d.Items, d.Items[0]] }, "duplicate: items lists 'item.alpha-rifle' more than once"),
        ["unsorted items"] = (d => d with { Items = [d.Items[1], d.Items[0], d.Items[2], d.Items[3]] }, "malformed: items must be in ascending id order"),
        ["no items"] = (d => d with { Items = [] }, "missing: items is empty"),
        ["weapon in the armor slot"] = (d => Item(d, 0, i => i with { Slot = ItemSlot.Armor }), "incompatible: items[0] 'item.alpha-rifle': a weapon must use the weapon slot"),
        ["stackable flag disagrees"] = (d => Item(d, 3, i => i with { Stackable = false }), "incompatible: items[3] 'item.gamma-kit': stackable must be true exactly when the maximum stack is above 1"),
        ["consumable without healing"] = (d => Item(d, 3, i => i with { HealAmount = 0 }), "incompatible: items[3] 'item.gamma-kit': a consumable needs a heal amount of at least 1"),
        ["stacking equipment"] = (d => Item(d, 1, i => i with { Stackable = true, MaxStack = 2 }), "incompatible: items[1] 'item.beta-vest': equipment cannot stack"),
        ["maximum stack below 1"] = (d => Item(d, 1, i => i with { MaxStack = 0 }), "malformed: items[1] 'item.beta-vest': the maximum stack is below 1"),
        ["loot from an unknown item"] = (d => Loot(d, t => t with { Entries = [t.Entries[0] with { ItemId = "item.unknown" }, t.Entries[1]] }), "incompatible: lootTable.entries[0]: unknown item 'item.unknown'"),
        ["loot weight 0"] = (d => Loot(d, t => t with { Entries = [t.Entries[0] with { Weight = 0 }, t.Entries[1]] }), "malformed: lootTable.entries[0]: the weight is below 1"),
        ["loot quantity range empty"] = (d => Loot(d, t => t with { Entries = [t.Entries[0] with { MinQuantity = 3 }, t.Entries[1]] }), "malformed: lootTable.entries[0]: the quantity range is empty"),
        ["loot items range empty"] = (d => Loot(d, t => t with { MinItems = 4 }), "malformed: lootTable: the items per container range is empty"),
        ["loot without entries"] = (d => Loot(d, t => t with { Entries = [] }), "missing: lootTable.entries is empty"),
        ["negative container count"] = (d => Loot(d, t => t with { ContainerCount = -1 }), "malformed: lootTable: the container count is negative"),
        ["loot version 0"] = (d => Loot(d, t => t with { Version = 0 }), "malformed: lootTable: the version must be at least 1"),
        ["thresholds not from 0"] = (d => Track(d, p => p with { XpThresholds = [5, 10, 30] }), "malformed: progression.xpThresholds: rank 1 must need 0 XP"),
        ["thresholds not increasing"] = (d => Track(d, p => p with { XpThresholds = [0, 30, 30] }), "malformed: progression.xpThresholds: thresholds must strictly increase (rank 3)"),
        ["no thresholds"] = (d => Track(d, p => p with { XpThresholds = [] }), "missing: progression.xpThresholds is empty"),
        ["negative completion XP"] = (d => Track(d, p => p with { MissionCompletionXp = -1 }), "malformed: progression.missionCompletionXp is negative"),
        ["duplicate choice"] = (d => Track(d, p => p with { ChoiceIds = ["choice.one", "choice.one"] }), "duplicate: progression.choiceIds lists 'choice.one' more than once"),
        ["unsorted choices"] = (d => Track(d, p => p with { ChoiceIds = ["choice.two", "choice.one"] }), "malformed: progression.choiceIds must be in ascending id order"),
        ["no operatives"] = (d => d with { Operatives = [] }, "missing: operatives is empty"),
        ["duplicate operative"] = (d => d with { Operatives = [d.Operatives[0], d.Operatives[0], d.Operatives[1]] }, $"duplicate: operatives lists '{Golden.One}' more than once"),
        ["unsorted operatives"] = (d => d with { Operatives = [d.Operatives[1], d.Operatives[0]] }, "malformed: operatives must be in ascending id order"),
        ["five abilities"] = (d => Op(d, 1, o => o with { AbilityIds = ["ability.a", "ability.b", "ability.c", "ability.d", "ability.e"] }), $"incompatible: operatives[1] '{Golden.Two}': more than 4 abilities"),
        ["same ability twice"] = (d => Op(d, 1, o => o with { AbilityIds = ["ability.a", "ability.a"] }), "duplicate: operatives[1].abilityIds lists 'ability.a' more than once"),
        ["starter for an unknown operative"] = (d => Start(d, s => s with { Operatives = [s.Operatives[0] with { OperativeId = Unknown }] }), $"incompatible: starter.operatives[0]: unknown operative '{Unknown}'"),
        ["starter operative twice"] = (d => Start(d, s => s with { Operatives = [s.Operatives[0], s.Operatives[0]] }), $"duplicate: starter.operatives lists '{Golden.One}' more than once"),
        ["grant of an unknown item"] = (d => Grants(d, g => [g[0] with { ItemId = "item.unknown" }, g[1]]), "incompatible: starter.operatives[0].grants[0]: unknown item 'item.unknown'"),
        ["grant quantity 0"] = (d => Grants(d, g => [g[0], g[1] with { Quantity = 0 }]), "malformed: starter.operatives[0].grants[1]: the quantity is below 1"),
        ["equipping a consumable"] = (d => Grants(d, g => [g[0], g[1] with { Equip = true }]), "incompatible: starter.operatives[0].grants[1]: item 'item.gamma-kit' has no equipment slot, so it cannot be equipped"),
        ["two items for one slot"] = (d => Grants(d, g => [g[0], g[0], g[1]]), "incompatible: starter.operatives[0].grants[1]: a second item to equip in the Weapon slot"),
        ["stash of an unknown item"] = (d => Start(d, s => s with { Stash = [s.Stash[0] with { ItemId = "item.unknown" }, s.Stash[1]] }), "incompatible: starter.stash[0]: unknown item 'item.unknown'"),
        ["stash quantity 0"] = (d => Start(d, s => s with { Stash = [s.Stash[0], s.Stash[1] with { Quantity = 0 }] }), "malformed: starter.stash[1]: the quantity is below 1"),
    };

    public static TheoryData<string> MutationNames()
    {
        var names = new TheoryData<string>();
        foreach (var name in Mutations.Keys)
            names.Add(name);
        return names;
    }

    [Theory]
    [MemberData(nameof(MutationNames))]
    public void A_semantic_fault_is_rejected(string name)
    {
        var (mutate, expected) = Mutations[name];
        AssertRejected(CanonicalCatalogueJson.ToBytes(mutate(Golden.Document())), expected);
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        var d = Track(Loot(Golden.Document(), t => t with { Version = 0 }), p => p with { MissionCompletionXp = -1 });
        var e = Assert.Throws<CatalogueException>(() => GameCatalogue.Load(CanonicalCatalogueJson.ToBytes(d)));
        Assert.Contains(e.Errors, x => x.Contains("the version must be at least 1", StringComparison.Ordinal));
        Assert.Contains(e.Errors, x => x.Contains("missionCompletionXp is negative", StringComparison.Ordinal));
    }
}
```

- [ ] **Step 3: Run the tests and see them fail.** From `Backend/`, run `bash ../.claude/scratchpad/phase13/tools/dt.sh test tests/Accounts.Domain.Tests/Accounts.Domain.Tests.csproj`. Expected: a build failure, because `GameCatalogue` and `CatalogueException` are missing.

- [ ] **Step 4: Write the exception and the reader.**
  - `Backend/src/Accounts.Domain/Catalogue/CatalogueException.cs`:

```csharp
namespace Blackglass.Accounts.Domain.Catalogue;

/// <summary>An invalid catalogue: every problem found, each starting with missing:, duplicate:, malformed: or incompatible:.</summary>
public sealed class CatalogueException(IReadOnlyList<string> errors)
    : Exception("Invalid catalogue:\n" + string.Join("\n", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
```

  - `Backend/src/Accounts.Domain/Catalogue/CatalogueReader.cs`:

```csharp
using System.Text.Json;

namespace Blackglass.Accounts.Domain.Catalogue;

/// <summary>
/// Reads catalogue JSON strictly into a CatalogueDocument. Every object must hold exactly its known members, once each,
/// with the right JSON kind; integers must fit 32 bits; enum words must be known. Each problem is added to errors with its
/// JSON path, and the result is null when there was any. Semantic rules are CatalogueValidator's.
/// </summary>
public static class CatalogueReader
{
    static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 16,
    };

    public static CatalogueDocument? Read(ReadOnlyMemory<byte> bytes, List<string> errors)
    {
        JsonDocument json;
        try { json = JsonDocument.Parse(bytes, Options); }
        catch (JsonException e) { errors.Add($"malformed: not valid JSON ({e.Message})"); return null; }
        using (json)
        {
            var start = errors.Count;
            var ok = new Reader(errors).TryDocument(json.RootElement, out var document);
            return ok && errors.Count == start ? document : null;
        }
    }

    delegate bool ElementReader<T>(JsonElement element, string path, out T value);

    sealed class Reader(List<string> errors)
    {
        public bool TryDocument(JsonElement e, out CatalogueDocument document)
        {
            document = null!;
            if (!TryMembers(e, "$", out var m, "formatVersion", "items", "lootTable", "progression", "operatives", "starter"))
                return false;
            var ok = TryInt(m["formatVersion"], "$.formatVersion", out var format);
            ok &= TryList<CatalogueItem>(m["items"], "$.items", TryItem, out var items);
            ok &= TryLootTable(m["lootTable"], "$.lootTable", out var loot);
            ok &= TryProgression(m["progression"], "$.progression", out var progression);
            ok &= TryList<OperativeDefinition>(m["operatives"], "$.operatives", TryOperative, out var operatives);
            ok &= TryStarter(m["starter"], "$.starter", out var starter);
            if (ok)
                document = new CatalogueDocument(format, items, loot, progression, operatives, starter);
            return ok;
        }

        bool TryItem(JsonElement e, string path, out CatalogueItem item)
        {
            item = null!;
            if (!TryMembers(e, path, out var m, "id", "category", "slot", "stackable", "maxStack", "healAmount"))
                return false;
            var ok = TryString(m["id"], path + ".id", out var id);
            ok &= TryWord(m["category"], path + ".category", out ItemCategory category);
            ok &= TryWord(m["slot"], path + ".slot", out ItemSlot slot);
            ok &= TryBool(m["stackable"], path + ".stackable", out var stackable);
            ok &= TryInt(m["maxStack"], path + ".maxStack", out var maxStack);
            ok &= TryInt(m["healAmount"], path + ".healAmount", out var heal);
            if (ok)
                item = new CatalogueItem(id, category, slot, stackable, maxStack, heal);
            return ok;
        }

        bool TryLootTable(JsonElement e, string path, out LootTable table)
        {
            table = null!;
            if (!TryMembers(e, path, out var m, "version", "containerCount", "minItems", "maxItems", "entries"))
                return false;
            var ok = TryInt(m["version"], path + ".version", out var version);
            ok &= TryInt(m["containerCount"], path + ".containerCount", out var containers);
            ok &= TryInt(m["minItems"], path + ".minItems", out var min);
            ok &= TryInt(m["maxItems"], path + ".maxItems", out var max);
            ok &= TryList<LootEntry>(m["entries"], path + ".entries", TryLootEntry, out var entries);
            if (ok)
                table = new LootTable(version, containers, min, max, entries);
            return ok;
        }

        bool TryLootEntry(JsonElement e, string path, out LootEntry entry)
        {
            entry = null!;
            if (!TryMembers(e, path, out var m, "itemId", "minQuantity", "maxQuantity", "weight"))
                return false;
            var ok = TryString(m["itemId"], path + ".itemId", out var itemId);
            ok &= TryInt(m["minQuantity"], path + ".minQuantity", out var min);
            ok &= TryInt(m["maxQuantity"], path + ".maxQuantity", out var max);
            ok &= TryInt(m["weight"], path + ".weight", out var weight);
            if (ok)
                entry = new LootEntry(itemId, min, max, weight);
            return ok;
        }

        bool TryProgression(JsonElement e, string path, out Progression progression)
        {
            progression = null!;
            if (!TryMembers(e, path, out var m, "xpThresholds", "missionCompletionXp", "choiceIds"))
                return false;
            var ok = TryList<int>(m["xpThresholds"], path + ".xpThresholds", TryInt, out var thresholds);
            ok &= TryInt(m["missionCompletionXp"], path + ".missionCompletionXp", out var xp);
            ok &= TryList<string>(m["choiceIds"], path + ".choiceIds", TryString, out var choices);
            if (ok)
                progression = new Progression(thresholds, xp, choices);
            return ok;
        }

        bool TryOperative(JsonElement e, string path, out OperativeDefinition operative)
        {
            operative = null!;
            if (!TryMembers(e, path, out var m, "id", "roleId", "abilityIds"))
                return false;
            var ok = TryString(m["id"], path + ".id", out var id);
            ok &= TryString(m["roleId"], path + ".roleId", out var roleId);
            ok &= TryList<string>(m["abilityIds"], path + ".abilityIds", TryString, out var abilities);
            if (ok)
                operative = new OperativeDefinition(id, roleId, abilities);
            return ok;
        }

        bool TryStarter(JsonElement e, string path, out Starter starter)
        {
            starter = null!;
            if (!TryMembers(e, path, out var m, "operatives", "stash"))
                return false;
            var ok = TryList<StarterOperative>(m["operatives"], path + ".operatives", TryStarterOperative, out var operatives);
            ok &= TryList<StashGrant>(m["stash"], path + ".stash", TryStashGrant, out var stash);
            if (ok)
                starter = new Starter(operatives, stash);
            return ok;
        }

        bool TryStarterOperative(JsonElement e, string path, out StarterOperative operative)
        {
            operative = null!;
            if (!TryMembers(e, path, out var m, "operativeId", "grants"))
                return false;
            var ok = TryString(m["operativeId"], path + ".operativeId", out var id);
            ok &= TryList<StarterGrant>(m["grants"], path + ".grants", TryGrant, out var grants);
            if (ok)
                operative = new StarterOperative(id, grants);
            return ok;
        }

        bool TryGrant(JsonElement e, string path, out StarterGrant grant)
        {
            grant = null!;
            if (!TryMembers(e, path, out var m, "itemId", "quantity", "equip"))
                return false;
            var ok = TryString(m["itemId"], path + ".itemId", out var itemId);
            ok &= TryInt(m["quantity"], path + ".quantity", out var quantity);
            ok &= TryBool(m["equip"], path + ".equip", out var equip);
            if (ok)
                grant = new StarterGrant(itemId, quantity, equip);
            return ok;
        }

        bool TryStashGrant(JsonElement e, string path, out StashGrant grant)
        {
            grant = null!;
            if (!TryMembers(e, path, out var m, "itemId", "quantity"))
                return false;
            var ok = TryString(m["itemId"], path + ".itemId", out var itemId);
            ok &= TryInt(m["quantity"], path + ".quantity", out var quantity);
            if (ok)
                grant = new StashGrant(itemId, quantity);
            return ok;
        }

        // The members of an object, by name, when it has exactly the expected names, once each.
        bool TryMembers(JsonElement e, string path, out Dictionary<string, JsonElement> members, params string[] names)
        {
            members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (e.ValueKind != JsonValueKind.Object) { errors.Add($"malformed: {path} must be an object"); return false; }
            var ok = true;
            foreach (var p in e.EnumerateObject())
            {
                if (Array.IndexOf(names, p.Name) < 0) { errors.Add($"malformed: {path}.{p.Name} is not a known member"); ok = false; }
                else if (!members.TryAdd(p.Name, p.Value)) { errors.Add($"duplicate: {path}.{p.Name} appears more than once"); ok = false; }
            }
            foreach (var name in names)
            {
                if (!members.ContainsKey(name)) { errors.Add($"missing: {path}.{name}"); ok = false; }
            }
            return ok;
        }

        bool TryList<T>(JsonElement e, string path, ElementReader<T> read, out List<T> list)
        {
            list = [];
            if (e.ValueKind != JsonValueKind.Array) { errors.Add($"malformed: {path} must be an array"); return false; }
            var ok = true;
            var i = 0;
            foreach (var element in e.EnumerateArray())
            {
                if (read(element, $"{path}[{i}]", out var value)) list.Add(value);
                else ok = false;
                i++;
            }
            return ok;
        }

        bool TryInt(JsonElement e, string path, out int value)
        {
            value = 0;
            if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out value))
                return true;
            errors.Add($"malformed: {path} must be a 32-bit integer");
            return false;
        }

        bool TryString(JsonElement e, string path, out string value)
        {
            value = "";
            if (e.ValueKind == JsonValueKind.String) { value = e.GetString()!; return true; }
            errors.Add($"malformed: {path} must be a string");
            return false;
        }

        bool TryBool(JsonElement e, string path, out bool value)
        {
            value = e.ValueKind == JsonValueKind.True;
            if (e.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return true;
            errors.Add($"malformed: {path} must be true or false");
            return false;
        }

        bool TryWord(JsonElement e, string path, out ItemCategory value) =>
            TryEnumWord<ItemCategory>(e, path, CanonicalCatalogueJson.Word, out value);

        bool TryWord(JsonElement e, string path, out ItemSlot value) =>
            TryEnumWord<ItemSlot>(e, path, CanonicalCatalogueJson.Word, out value);

        bool TryEnumWord<TEnum>(JsonElement e, string path, Func<TEnum, string> word, out TEnum value) where TEnum : struct, Enum
        {
            value = default;
            if (!TryString(e, path, out var text))
                return false;
            foreach (var candidate in Enum.GetValues<TEnum>())
            {
                if (word(candidate) == text) { value = candidate; return true; }
            }
            errors.Add($"malformed: {path} must be one of {string.Join(", ", Enum.GetValues<TEnum>().Select(word))}");
            return false;
        }
    }
}
```

- [ ] **Step 5: Write the validator.** `Backend/src/Accounts.Domain/Catalogue/CatalogueValidator.cs`:

```csharp
namespace Blackglass.Accounts.Domain.Catalogue;

/// <summary>
/// The semantic rules of a catalogue (the plan's rule table), mirrored by Unity's CatalogueBuilder so an export the
/// backend would refuse is never written. Every problem is added to errors; nothing stops at the first.
/// </summary>
public static class CatalogueValidator
{
    /// <summary>Unity's OperativeDefinition.MaxAbilities.</summary>
    public const int MaxAbilities = 4;

    public static void Validate(CatalogueDocument d, List<string> errors)
    {
        if (d.FormatVersion != CatalogueDocument.SupportedFormatVersion)
        {
            errors.Add($"incompatible: format version {d.FormatVersion} is not supported (this backend reads {CatalogueDocument.SupportedFormatVersion})");
            return; // nothing else can be judged against an unknown format
        }
        var items = CheckItems(d.Items, errors);
        CheckLoot(d.LootTable, items, errors);
        CheckProgression(d.Progression, errors);
        var operatives = CheckOperatives(d.Operatives, errors);
        CheckStarter(d.Starter, items, operatives, errors);
    }

    static Dictionary<string, CatalogueItem> CheckItems(IReadOnlyList<CatalogueItem> items, List<string> errors)
    {
        if (items.Count == 0) errors.Add("missing: items is empty");
        var ids = items.Select(i => i.Id).ToList();
        Unique(ids, "items", errors);
        Ascending(ids, "items", errors);
        var byId = new Dictionary<string, CatalogueItem>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var path = $"items[{i}] '{item.Id}'";
            Id(item.Id, $"items[{i}].id", errors);
            if (item.MaxStack < 1) errors.Add($"malformed: {path}: the maximum stack is below 1");
            if (item.HealAmount < 0) errors.Add($"malformed: {path}: the heal amount is negative");
            if (item.Stackable != item.MaxStack > 1) errors.Add($"incompatible: {path}: stackable must be true exactly when the maximum stack is above 1");
            var slotProblem = item.Category switch
            {
                ItemCategory.Weapon when item.Slot != ItemSlot.Weapon => "a weapon must use the weapon slot",
                ItemCategory.Armor when item.Slot != ItemSlot.Armor => "armor must use the armor slot",
                ItemCategory.Utility when item.Slot != ItemSlot.Utility => "a utility item must use the utility slot",
                ItemCategory.Consumable when item.Slot != ItemSlot.None => "a consumable has no equipment slot",
                _ => null,
            };
            if (slotProblem is not null) errors.Add($"incompatible: {path}: {slotProblem}");
            if (item.Category == ItemCategory.Consumable && item.HealAmount < 1) errors.Add($"incompatible: {path}: a consumable needs a heal amount of at least 1");
            if (item.Category != ItemCategory.Consumable && item.MaxStack != 1) errors.Add($"incompatible: {path}: equipment cannot stack");
            byId.TryAdd(item.Id, item);
        }
        return byId;
    }

    static void CheckLoot(LootTable table, Dictionary<string, CatalogueItem> items, List<string> errors)
    {
        if (table.Version < 1) errors.Add("malformed: lootTable: the version must be at least 1");
        if (table.ContainerCount < 0) errors.Add("malformed: lootTable: the container count is negative");
        if (table.MinItems < 1 || table.MaxItems < table.MinItems) errors.Add("malformed: lootTable: the items per container range is empty");
        if (table.Entries.Count == 0) errors.Add("missing: lootTable.entries is empty");
        for (var i = 0; i < table.Entries.Count; i++)
        {
            var entry = table.Entries[i];
            var path = $"lootTable.entries[{i}]";
            Id(entry.ItemId, path + ".itemId", errors);
            if (!items.ContainsKey(entry.ItemId)) errors.Add($"incompatible: {path}: unknown item '{entry.ItemId}'");
            if (entry.Weight < 1) errors.Add($"malformed: {path}: the weight is below 1");
            if (entry.MinQuantity < 1 || entry.MaxQuantity < entry.MinQuantity) errors.Add($"malformed: {path}: the quantity range is empty");
        }
    }

    static void CheckProgression(Progression progression, List<string> errors)
    {
        var thresholds = progression.XpThresholds;
        if (thresholds.Count == 0) errors.Add("missing: progression.xpThresholds is empty");
        else if (thresholds[0] != 0) errors.Add("malformed: progression.xpThresholds: rank 1 must need 0 XP");
        for (var i = 1; i < thresholds.Count; i++)
        {
            if (thresholds[i] > thresholds[i - 1]) continue;
            errors.Add($"malformed: progression.xpThresholds: thresholds must strictly increase (rank {i + 1})");
            break;
        }
        if (progression.MissionCompletionXp < 0) errors.Add("malformed: progression.missionCompletionXp is negative");
        for (var i = 0; i < progression.ChoiceIds.Count; i++)
            Id(progression.ChoiceIds[i], $"progression.choiceIds[{i}]", errors);
        Unique(progression.ChoiceIds, "progression.choiceIds", errors);
        Ascending(progression.ChoiceIds, "progression.choiceIds", errors);
    }

    static HashSet<string> CheckOperatives(IReadOnlyList<OperativeDefinition> operatives, List<string> errors)
    {
        if (operatives.Count == 0) errors.Add("missing: operatives is empty");
        var ids = operatives.Select(o => o.Id).ToList();
        Unique(ids, "operatives", errors);
        Ascending(ids, "operatives", errors);
        for (var i = 0; i < operatives.Count; i++)
        {
            var operative = operatives[i];
            Id(operative.Id, $"operatives[{i}].id", errors);
            Id(operative.RoleId, $"operatives[{i}].roleId", errors);
            if (operative.AbilityIds.Count > MaxAbilities) errors.Add($"incompatible: operatives[{i}] '{operative.Id}': more than {MaxAbilities} abilities");
            for (var j = 0; j < operative.AbilityIds.Count; j++)
                Id(operative.AbilityIds[j], $"operatives[{i}].abilityIds[{j}]", errors);
            Unique(operative.AbilityIds, $"operatives[{i}].abilityIds", errors);
        }
        return new HashSet<string>(ids, StringComparer.Ordinal);
    }

    static void CheckStarter(Starter starter, Dictionary<string, CatalogueItem> items, HashSet<string> operatives, List<string> errors)
    {
        var ids = starter.Operatives.Select(o => o.OperativeId).ToList();
        Unique(ids, "starter.operatives", errors);
        Ascending(ids, "starter.operatives", errors);
        for (var i = 0; i < starter.Operatives.Count; i++)
        {
            var entry = starter.Operatives[i];
            var path = $"starter.operatives[{i}]";
            if (!operatives.Contains(entry.OperativeId)) errors.Add($"incompatible: {path}: unknown operative '{entry.OperativeId}'");
            var equipped = new HashSet<ItemSlot>();
            for (var j = 0; j < entry.Grants.Count; j++)
            {
                var grant = entry.Grants[j];
                var where = $"{path}.grants[{j}]";
                var item = CheckGrant(grant.ItemId, grant.Quantity, where, items, errors);
                if (!grant.Equip || item is null) continue;
                if (item.Slot == ItemSlot.None) errors.Add($"incompatible: {where}: item '{item.Id}' has no equipment slot, so it cannot be equipped");
                else if (!equipped.Add(item.Slot)) errors.Add($"incompatible: {where}: a second item to equip in the {item.Slot} slot");
            }
        }
        for (var k = 0; k < starter.Stash.Count; k++)
            CheckGrant(starter.Stash[k].ItemId, starter.Stash[k].Quantity, $"starter.stash[{k}]", items, errors);
    }

    static CatalogueItem? CheckGrant(string itemId, int quantity, string path, Dictionary<string, CatalogueItem> items, List<string> errors)
    {
        Id(itemId, path + ".itemId", errors);
        if (quantity < 1) errors.Add($"malformed: {path}: the quantity is below 1");
        if (items.TryGetValue(itemId, out var item)) return item;
        errors.Add($"incompatible: {path}: unknown item '{itemId}'");
        return null;
    }

    static void Id(string value, string path, List<string> errors)
    {
        if (!CanonicalCatalogueJson.IsWellFormedId(value))
            errors.Add($"malformed: {path} '{value}' is not a well-formed id (lowercase letters and digits separated by single '.' or '-', at most {CanonicalCatalogueJson.MaxIdLength} characters)");
    }

    static void Unique(IReadOnlyList<string> ids, string path, List<string> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!seen.Add(id) && reported.Add(id)) errors.Add($"duplicate: {path} lists '{id}' more than once");
        }
    }

    static void Ascending(IReadOnlyList<string> ids, string path, List<string> errors)
    {
        for (var i = 1; i < ids.Count; i++)
        {
            if (string.CompareOrdinal(ids[i - 1], ids[i]) <= 0) continue;
            errors.Add($"malformed: {path} must be in ascending id order ('{ids[i]}' follows '{ids[i - 1]}')");
            return;
        }
    }
}
```

  A duplicate is adjacent once sorted, so `Ascending` uses `<= 0`: equal neighbours are `Unique`'s to report, not an ordering error.

- [ ] **Step 6: Write `GameCatalogue`.** `Backend/src/Accounts.Domain/Catalogue/GameCatalogue.cs`:

```csharp
namespace Blackglass.Accounts.Domain.Catalogue;

/// <summary>
/// A validated catalogue and its catalogueVersion. Load accepts only the bytes Unity's exporter writes: no BOM, no CR,
/// strictly the known structure, every semantic rule, and byte-for-byte equal to the canonical form of what was read (so
/// spacing, member order, number forms and duplicate members can't vary). Version is "sha256:" and the lowercase hex
/// SHA-256 of those bytes, the value a mission process must report (spec §4.6).
/// </summary>
public sealed class GameCatalogue
{
    static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    static readonly Lazy<GameCatalogue> embedded = new(() => Load(EmbeddedBytes()));

    readonly Dictionary<string, CatalogueItem> items;
    readonly Dictionary<string, OperativeDefinition> operatives;

    GameCatalogue(string version, CatalogueDocument document)
    {
        Version = version;
        Document = document;
        items = document.Items.ToDictionary(i => i.Id, StringComparer.Ordinal);
        operatives = document.Operatives.ToDictionary(o => o.Id, StringComparer.Ordinal);
    }

    public string Version { get; }
    public CatalogueDocument Document { get; }

    public CatalogueItem? FindItem(string id) => items.GetValueOrDefault(id);
    public OperativeDefinition? FindOperative(string id) => operatives.GetValueOrDefault(id);

    /// <summary>The catalogue this build embeds (Backend/catalogue/catalogue.json); throws CatalogueException when invalid.</summary>
    public static GameCatalogue Embedded => embedded.Value;

    public static byte[] EmbeddedBytes()
    {
        using var stream = typeof(GameCatalogue).Assembly.GetManifestResourceStream("catalogue.json")
            ?? throw new InvalidOperationException("The embedded catalogue.json resource is missing.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    public static GameCatalogue Load(ReadOnlyMemory<byte> bytes)
    {
        var errors = new List<string>();
        var span = bytes.Span;
        if (span.StartsWith(Utf8Bom)) errors.Add("malformed: the file starts with a byte-order mark");
        if (span.IndexOf((byte)'\r') >= 0) errors.Add("malformed: the file contains a carriage return; it must use LF line ends");
        if (errors.Count > 0) throw new CatalogueException(errors);

        var document = CatalogueReader.Read(bytes, errors) ?? throw new CatalogueException(errors);
        CatalogueValidator.Validate(document, errors);
        if (errors.Count > 0) throw new CatalogueException(errors);
        if (!span.SequenceEqual(CanonicalCatalogueJson.ToBytes(document)))
            throw new CatalogueException(["malformed: the file is not in canonical form (spacing, member order or number form differ); re-export it from Unity"]);
        return new GameCatalogue(CatalogueVersion.Of(span), document);
    }
}
```

  The validator rejects duplicate item and operative ids before the constructor builds its dictionaries.

- [ ] **Step 7: Run the tests and see them pass.** From `Backend/`, run `bash ../.claude/scratchpad/phase13/tools/dt.sh test tests/Accounts.Domain.Tests/Accounts.Domain.Tests.csproj`. Expected: 59 passed, 0 failed. That is 3 from Task 5, 5 load tests, and 51 rejection tests (4 facts, 12 inline cases, 34 mutations and 1 all-at-once).
- [ ] **Step 8: Commit.**
  ```bash
  git add Backend/src/Accounts.Domain Backend/tests/Accounts.Domain.Tests
  git commit -m "Phase 13 Stage 2: strict catalogue reader and validator; GameCatalogue embeds the committed file and refuses anything non-canonical"
  ```

---

### Task 7: `serve` refuses an invalid catalogue; the image carries it

**Files:**
- Modify:
  - `Backend/src/Accounts.Api/Accounts.Api.csproj`;
  - `Backend/src/Accounts.Api/ServeCommand.cs`;
  - `Backend/src/Accounts.Api/ExitCodes.cs`;
  - `Backend/Dockerfile`;
  - `Backend/README.md`.
- Create: `Backend/tests/Accounts.Integration.Tests/Unit/ServeCatalogueTests.cs`.

**Interfaces:**
- Consumes: `GameCatalogue.Embedded` and `CatalogueException`.
- Produces:
  - `ExitCodes.InvalidCatalogue = 4`;
  - `ServeCommand.StartAsync(env, error, ct, Func<GameCatalogue>? loadCatalogue = null)`, a backward-compatible optional parameter;
  - the `GameCatalogue` singleton registered in the API's services.

- [ ] **Step 1: Write the failing tests.** `Backend/tests/Accounts.Integration.Tests/Unit/ServeCatalogueTests.cs`:

```csharp
using Blackglass.Accounts.Api;
using Blackglass.Accounts.Domain.Catalogue;

namespace Blackglass.Accounts.IntegrationTests.Unit;

public class ServeCatalogueTests
{
    [Fact]
    public async Task Serve_refuses_an_invalid_catalogue_before_reading_its_settings()
    {
        var err = new StringWriter();
        var start = await ServeCommand.StartAsync(new Dictionary<string, string?>(), err, CancellationToken.None,
            () => GameCatalogue.Load("{}\n"u8.ToArray()));
        Assert.Equal(ExitCodes.InvalidCatalogue, start.ExitCode);
        Assert.Null(start.App);
        Assert.Contains("Refusing to start: Invalid catalogue", err.ToString());
        Assert.DoesNotContain("Configuration error", err.ToString());
    }

    [Fact]
    public async Task The_embedded_catalogue_passes_so_serve_goes_on_to_read_its_settings()
    {
        var err = new StringWriter();
        var start = await ServeCommand.StartAsync(new Dictionary<string, string?>(), err, CancellationToken.None);
        Assert.Equal(ExitCodes.Failure, start.ExitCode);
        Assert.Contains("Configuration error", err.ToString());
        Assert.DoesNotContain("catalogue", err.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Run the tests and see them fail.** From `Backend/`, run `bash ../.claude/scratchpad/phase13/tools/dt.sh test Backend.sln --filter "FullyQualifiedName~ServeCatalogueTests"`. Expected: a build failure, because `Accounts.Domain` isn't referenced and `InvalidCatalogue` is missing.

- [ ] **Step 3: Implement.**
  - `Accounts.Api.csproj`: add `<ProjectReference Include="..\Accounts.Domain\Accounts.Domain.csproj" />` beside the Persistence reference.
  - `ExitCodes.cs`: add `public const int InvalidCatalogue = 4;` after `SchemaNotCurrent`.
  - `ServeCommand.cs`: add `using Blackglass.Accounts.Domain.Catalogue;`, change the signature, and put the gate first:
    ```csharp
        public static async Task<ServeStart> StartAsync(IReadOnlyDictionary<string, string?> env, TextWriter error, CancellationToken ct,
            Func<GameCatalogue>? loadCatalogue = null)
        {
            // The catalogue comes first: an image with an invalid catalogue never reads its settings or the database.
            GameCatalogue catalogue;
            try { catalogue = (loadCatalogue ?? (() => GameCatalogue.Embedded))(); }
            catch (CatalogueException e) { error.WriteLine($"Refusing to start: {e.Message}"); return new(ExitCodes.InvalidCatalogue, null); }

            DbSettings settings;
    ```
    The rest stays as it is. After `builder.Services.AddSingleton(dataSource);`, add:
    ```csharp
            builder.Services.AddSingleton(catalogue);
    ```
  - `Dockerfile`: after `COPY db/ db/`, add `COPY catalogue/ catalogue/`.
  - `README.md`:
    - in "Layout", add `src/Accounts.Domain` (the catalogue model, reader and validator; pure, no I/O) and `catalogue/`/`contracts/`, and add `4 serve refused an invalid catalogue` to the exit-code list;
    - add this section after "Migrations":
    ```markdown
    ## Catalogue
    - `catalogue/catalogue.json` is exported from Unity (Blackglass > Backend > Export Catalogue, or `Tools/run-method.sh Blackglass.BackendTools.CatalogueExporter.ExportFromCommandLine` with the Editor closed). Never edit it by hand: Unity's `CatalogueExportTests` fails while it differs from a fresh export, and the backend refuses a file that isn't in the exporter's canonical form.
    - `catalogueVersion` is `sha256:` plus the lowercase hex SHA-256 of the file's bytes; nothing inside the file states it. `src/Accounts.Domain` embeds the file, and `serve` refuses to start (exit 4) when it is invalid.
    - Format 1 holds ids and rules only (items, loot table, progression, operatives with role and ability ids, starter loadout); display text stays in Unity. Any other `formatVersion` is refused.
    - `contracts/catalogue/golden-catalogue.json` and `.version` are the shared vector: Unity's and the backend's writers must both produce it byte for byte, and both hash it to the `.version` value.
    ```

- [ ] **Step 4: Run the tests and see them pass.** Run the same filter. Expected: 2 passed.
- [ ] **Step 5: Run the full backend suites.**
  - With the tunnel open, `bash ../.claude/scratchpad/phase13/tools/dt.sh test Backend.sln`. Expected: **ALL 155 passed, 0 failed** (94 + 59 + 2).
  - `--filter "Requires!=Postgres"`. Expected: **UNIT 117 passed, 0 failed** (56 + 59 + 2).
- [ ] **Step 6: Commit.**
  ```bash
  git add Backend/src/Accounts.Api Backend/Dockerfile Backend/README.md Backend/tests/Accounts.Integration.Tests/Unit/ServeCatalogueTests.cs
  git commit -m "Phase 13 Stage 2: serve refuses an invalid catalogue (exit 4) before reading settings; the image carries the catalogue"
  ```

---

### Task 8: Documentation alignment and Stage 2 acceptance

**Files:**
- Modify: `Docs/superpowers/plans/2026-10-14-phase-13-backend.md` (Stage 1 status line; Stage 2 status and touches) and `Docs/superpowers/specs/2026-10-14-phase-13-backend-design.md` (§4.6, §4.7 and the §19 bag-capacity row).

- [ ] **Step 1: Update the stage-level plan.**
  - Stage 1's status: "**Done; merged into `main` and pushed (`ad14373`).**"
  - Stage 2's status: "**Done on branch `phase13-stage2`, not merged.** Detailed plan: `Docs/superpowers/plans/2026-10-14-phase-13-stage2-catalogue-export.md`."
  - Replace Stage 2's **Touches** with the file map of this plan, as approved in Q3.

- [ ] **Step 2: Align the spec.**
  - In §4.6, replace the bullet list and the version sentence with:
    - the five sections as implemented, with operatives carrying `roleId` and ordered `abilityIds`, and the starter's per-operative grants and stash;
    - "ids and rules only; no display text";
    - `formatVersion` 1;
    - the canonical form in one sentence;
    - "`catalogueVersion` is `sha256:` and the lowercase hex SHA-256 of the file's bytes; the backend accepts only canonical bytes";
    - the shared golden vector in `Backend/contracts/catalogue/`.
  - In §4.7, change the "Catalogue ids" row to "Existing authored string ids (items, choices, operative GUIDs) plus authored role and ability ids (Stage 2)".
  - In §19, set the bag-capacity row's source per Q2 (recommended: "`SquadInventory.bagCapacity` (scene); its single authority is settled in Stage 4/5, when the backend first enforces it").

- [ ] **Step 3: Run the Unity acceptance.** With the Editor closed, run `bash Tools/run-tests.sh EditMode` and `bash Tools/run-tests.sh PlayMode`. Expected: EditMode at **baseline + 40** and PlayMode at **baseline**, both with 0 failed. Save both outputs to `.claude/scratchpad/phase13/stage-02/unity-*.txt`.

- [ ] **Step 4: Check that both sides agree on the version.**
  ```bash
  grep -o 'catalogueVersion (fresh export): sha256:[0-9a-f]*' Logs/TestResults-EditMode.xml
  (cd Backend && bash ../.claude/scratchpad/phase13/tools/dt.sh test tests/Accounts.Domain.Tests/Accounts.Domain.Tests.csproj \
     --filter "FullyQualifiedName~The_committed_catalogue_loads" --logger "console;verbosity=detailed" | grep -o 'catalogueVersion (committed file): sha256:[0-9a-f]*')
  printf 'sha256:%s\n' "$(sha256sum Backend/catalogue/catalogue.json | cut -d' ' -f1)"
  ```
  Expected: three identical values. With the Q1 ids, that value is `sha256:916f1af1e1200f94abf52319c780e23dbed1278e9420272faa2c6bbb6ff41fab`. Save the output to `stage-02/catalogue-version.txt`.

- [ ] **Step 5: Run the backend acceptance.** With the tunnel open, run ALL (155/0) and UNIT (117/0). Save the output to `stage-02/backend.txt`.

- [ ] **Step 6: Run the VM checks.** The image's Dockerfile changed, so this step re-runs the Stage 1 drill and smoke tests; no other VM change is made.
  ```bash
  bash Backend/tools/vm-sync.sh
  ssh bg-dev-services01-admin 'sudo -n bash /opt/blackglass/backend/src/Backend/deploy/services/tests/drill-test.sh'
  ssh bg-dev-services01-admin 'sudo -n bash /opt/blackglass/backend/src/Backend/deploy/services/tests/smoke-test.sh'
  ssh bg-dev-services01-admin 'sudo -n docker compose ls; ss -H -ltn'
  ```
  Expected:
  - drill: exit 0, 16 ok;
  - smoke: exit 0, 24 ok;
  - afterwards only `blackglass-testdb` runs, and the listeners are still `:22`, `127.0.0.1:55432` and the resolver stubs.

  Save to `stage-02/vm.txt`, with addresses redacted.

- [ ] **Step 7: Audit the paths and the owner files.**
  - `git diff --stat main...HEAD` lists only this plan's file map, plus the `.meta` files Unity created for new files and folders.
  - `git diff main...HEAD -- Backend/db` is empty (no migration).
  - `git status --short` shows only the owner's two settings files and `.claude/`.
  - The two owner files' `sha256sum` equals the values recorded in `.claude/scratchpad/phase13/stage-00/`.
  - `git ls-files --eol Backend/catalogue/catalogue.json Backend/contracts/catalogue/golden-catalogue.json` shows `i/lf w/lf` for both.

  Save to `stage-02/audit.txt`.

- [ ] **Step 8: Commit the docs.**
  ```bash
  git add Docs/superpowers/plans/2026-10-14-phase-13-backend.md Docs/superpowers/specs/2026-10-14-phase-13-backend-design.md
  git commit -m "Phase 13 Stage 2: spec and stage plan aligned with the catalogue export as built"
  ```

- [ ] **Step 9: Report and stop.** Write `stage-02/summary.md` with:
  - the commits;
  - the Unity counts against the baseline;
  - the backend counts;
  - the three matching `catalogueVersion` values;
  - the drill and smoke results, the listeners and the path audit;
  - the open concerns.

  Do not merge or push.

---

## Owner rulings (2026-10-14)

- **Q1 approved:** the seven ids below are permanent, backend-facing identifiers. Asset GUIDs and serialized references are not altered. Missing and duplicate ids are validated.
- **Q2 approved:** bag capacity stays out of format 1, and the current gameplay behaviour (8 entries) is unchanged. Stage 4/5 must establish an authoritative, versioned capacity configuration.
- **Q3 approved:** the expanded touch list. The startup validation is completed in Stage 2, not deferred.
- **Further requirements:**
  - `catalogueVersion` comes from the canonical exported bytes and is not self-referential: it is never stored in the file it hashes.
  - Shared golden vectors verify that Unity and .NET produce the same bytes.
  - Hashes and test counts in this plan are predictions.
  - `CLAUDE.md` is updated at the start of the stage.
  - Gameplay, asset GUIDs, serialized references, loot rules, ability effects and progression behaviour are preserved.
  - There is no migration unless a real requirement shows one is needed.

The questions as they were asked:

**Q1. Authored ids for roles and abilities.** Spec §4.6 exports "operative definitions: id, role and ability ids", but neither `OperativeRole` nor `AbilityDefinition` has an id today.
- **Recommended:** add an authored `id` field to both, set in the 3 role and 4 ability assets: `role.assault`, `role.recon`, `role.support`, `ability.aimed-shot`, `ability.blast`, `ability.mend` and `ability.recon-scan`.
  - The change is additive: each asset gains one line, and every serialized reference is kept.
  - No gameplay code reads the new field.
- Rejected alternatives:
  - Unity asset GUIDs: they belong to the `.meta` files, aren't authored, and change if a `.meta` is regenerated.
  - Display or asset names: they would break when renamed.
  - Leaving roles and abilities out: that contradicts §4.6.
- The ids become permanent keys once the backend stores them (Stage 4 on). Rename any you'd prefer now.

**Q2. Bag capacity (§19 says "catalogue export").** Today it is `SquadInventory.bagCapacity`, a scene component field (8 in `ProceduralMission.unity`), not a data asset, so there is no single authority to export.
- **Recommended:** leave it out of format 1, and settle its authority in Stage 4/5, when the backend first enforces bag capacity. That would likely move it to a data asset and add it in format 2, or as a field the validator gains then.
- Alternative: export a constant 8 now. That would duplicate the scene value with nothing tying the two together.

**Q3. Paths beyond the stage plan's Stage 2 "Touches".** The stage plan lists only `Assets/_Project/Editor/Backend/`, `Backend/catalogue/` and one EditMode test file. This plan also needs:
- three runtime scripts, all additive: the `Id` fields and `StarterLoadout.Operatives`;
- the 7 assets, one line each;
- the EditMode test assembly's reference list;
- two EditMode test files instead of one: the builder's refusal cases are their own file;
- `Backend/contracts/catalogue/`, the shared golden vector named in spec §2.3;
- the new `Accounts.Domain` and `Accounts.Domain.Tests` projects named in spec §2.3, and `Backend.sln`;
- the `serve` startup gate in `Accounts.Api` (spec §4.6: "the backend refuses to start on an invalid catalogue"), with exit code 4 and one test file;
- `Dockerfile` (`COPY catalogue/`) and `README.md`.

Approve the expanded list, or name what to cut. The startup gate and the Dockerfile line can move to Stage 4 if you'd rather keep Stage 2 library-only.

**Decisions made in this plan (say if you want any changed):**
- The version is the hash of the file's bytes, with no version field inside the file.
- Both writers are hand-written to one set of rules and proven equal by the golden vector, rather than relying on library formatting.
- Display text is not exported; clients show names from their own Unity assets.
- The exporter lives in an Editor-only assembly. The dedicated server must report its `catalogueVersion` from Stage 13 on; that stage decides between moving the builder and writer into `Scripts/Backend/Core` (hashing what the server actually loaded) and embedding the committed file's version at build time.
- There is no migration and no `/health` change.
- The upgrade and drill gating tests are untouched, because they are already version-agnostic for 0001.
- `CLAUDE.md` is updated at the start of Stage 2 (owner requirement 4).
