# Phase 13 Stage 11: Unity backend core (library-free), implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task by task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Status (2026-10-19):** revision 1.1, **plan only, for the owner's review. Implementation is not authorized.** Branch `phase13-stage11-plan` from `main` at `6bdc6ca` (Stage 10 accepted and closed). The decisions in "Decisions for the owner" (D1–D8) need rulings first. Revision 1.1 applies the independent plan review (0 Critical, 5 Important, 14 Minor; see "Plan review" at the end).

**Goal:** a plain C# core inside the Unity project that later stages (12, 13, 14, 16) build on:
- the backend DTOs;
- the exact manifest format v1 (canonical bytes and hashes equal to the .NET backend's, every golden vector reproduced);
- the mission's item-provenance ledger;
- the manifest builder;
- the crash-safe outbox writer;
- the library-free ticket-admission boundary.

Nothing here runs in a scene yet. No gameplay, networking, scene, prefab, HUD, input or project-settings file changes.

**Architecture:**
- New assembly `Blackglass.Backend.Core` in `Assets/_Project/Scripts/Backend/Core/` (D1). It has no references and `noEngineReferences: true`, so the compiler itself keeps out UnityEngine, FishNet and every other library. Only the base class library is used: `System.IO`, `System.Security.Cryptography.SHA256`, `System.Text`, and `System.Runtime.InteropServices` for five libc calls (D6).
- Data flows one way:

  ```
  snapshot (LoadoutSnapshotDto) ─┐
  container fill (pool lines) ───┼─► MissionItemLedger ─► ParticipantFacts ─► ManifestBuilder ─► BuiltManifest (canonical bytes + hash)
  collect / use / equip events ──┘        ▲ verify against the runtime bags                             │
                                                                                                         ▼
                                                     ResultPublication (step-wise, main thread): WriteDurable → await the recorded notice or its deadline → Publish
  ```
- The ledger never reads Unity objects. Stage 12 feeds it events and plain snapshots of `ItemInventory` contents.
- The manifest model and its strict reader are a faithful port of `Backend/src/Accounts.Domain/Settlement/ResultManifest.cs` and `CanonicalJson.cs`. The vectors in `Backend/contracts/manifest-v1/` are the authority. Nothing in that folder changes.

**Tech stack:**
- Unity 6000.3.25f1, API compatibility .NET Standard 2.1, C# as the project compiles it (no records or `init`).
- NUnit EditMode tests in the existing `Blackglass.Tests.EditMode` assembly, run by `Tools/run-tests.sh`.
- The Linux server player is Mono (`ServerBuild` enforces `Mono2x`).
- One new xUnit test file in `Backend/tests/Accounts.Domain.Tests` (D3), run with `dotnet test`; it needs no database.

**Spec:**
- `Docs/superpowers/specs/2026-10-14-phase-13-backend-design.md`: revision 2.13, §2.3, §4.7, §5.1, §8, §9, §14.6, §16.3.
- Its controlling amendments are in `Docs/superpowers/plans/2026-10-17-phase-13-stage9-settlement.md`:
  - S9-B4 and §G: format v1;
  - §H: the publish/visibility contract;
  - S9-B23: publication order;
  - "Obligations this stage hands on": Stage 11.
- Stage 10's reader of the outbox: `Backend/src/MissionHost.Agent/Outbox.cs` and `Unix.cs`. Where they and the spec's older text differ, §G, §H and the agent's as-built reader win.

---

## Baselines (measured 2026-10-19 on `main` at `6bdc6ca`, Editor closed, `Tools/run-tests.sh`)

| Suite | Result |
|---|---|
| EditMode (`Blackglass.Tests.EditMode`) | **1579 total, 1579 passed, 0 failed, 0 skipped** (EXIT=0) |
| PlayMode (`Blackglass.Tests.PlayMode`) | **1434 total, 1434 passed, 0 failed, 0 skipped** (EXIT=0) |

The evidence is in `.claude/scratchpad/phase13/stage-11/baseline/` (local, not committed). Every task below adds EditMode tests only, so the PlayMode total must stay at its baseline, and the EditMode total must grow only by this stage's new tests.

---

## Global Constraints

- **Touch only** these paths:
  - `Assets/_Project/Scripts/Backend/Core/**` (new, with `.meta` files);
  - `Assets/_Project/Tests/EditMode/Backend/**` (new, with `.meta` files);
  - `Assets/_Project/Tests/EditMode/Blackglass.Tests.EditMode.asmdef` (one added reference, D1);
  - `Backend/contracts/ledger-v1/**` (new, D3);
  - `Backend/tests/Accounts.Domain.Tests/Settlement/LedgerScenarioTests.cs` (new, D3);
  - `Backend/tests/Accounts.Domain.Tests/Accounts.Domain.Tests.csproj` (one copy line, D3);
  - `Backend/tests/UnityCore.Linux.Tests/**` (new, D8), its entry in `Backend/Backend.sln`, and `Backend/tools/vm-test.sh` (a `--unity-core` mode that also snapshots `Assets/_Project/Scripts/Backend/Core`, D8);
  - this plan, the staged plan's Stage 11 status, and `CLAUDE.md`'s status lines.
- **Never** change a file in `Backend/contracts/manifest-v1/`, `catalogue/`, `inventory/`, `progression/` or `starter/`. A vector change is a format change and needs the owner. No second manifest format: there is one model, `ResultManifestV1`, written only by `ManifestCodec`.
- **No change** to:
  - `Assets/_Project/Scripts/Blackglass.asmdef`, `Scripts/Net/**`, `Scripts/Items/**`, `Scripts/Server/**`, `Scripts/Mission/**`;
  - any scene, prefab, HUD, input action or project setting;
  - combat, fog of war, procedural generation, controller support or gameplay behaviour.

  Stage 12 wires the core in.
- **No FishNet adapter.** `NetAdmission` and the networking gate stay as they are; `INetAdmission` is only declared (D5).
- **Core references nothing:** no UnityEngine, no `UnityEngine.Networking`/`UnityWebRequest`, no `System.Net*`, no FishNet, GameKit, Unity Transport or Unity Collections. The asmdef enforces it, and a source audit test pins it.
- **Format v1 exactly:**
  - UTF-8 without a BOM, no whitespace, members in ordinal key order, minimal escapes (`"`, `\`, U+0000–U+001F only; `\b \t \n \f \r`, otherwise `\u00xx` in lowercase hex);
  - lists sorted by their stated keys;
  - integers as `0` or `[1-9][0-9]*`;
  - bounds and refusal codes as `README.md` and `ResultManifest.cs` state them;
  - `manifestHash` = `"sha256:"` + lowercase hex SHA-256 of the canonical bytes.
- **Ids:**
  - account, operative and item ids are canonical Base58 strings outside Core's model and `long` inside it;
  - attempt ids are canonical lowercase UUIDv7 (`xxxxxxxx-xxxx-7xxx-[89ab]xxx-xxxxxxxxxxxx`);
  - process-session ids are canonical lowercase UUIDs of any version;
  - in-mission entry ids are local and never written to a manifest.
- **Facts only:** no XP, rewards, energy, insurance, refunds, retention choice, seed or salt in a manifest, a builder input or an outbox file. `infra_void` cannot be expressed: the outcome enum has five members.
- **The outbox contract (§H, agent as built):**
  - files `<accountBase58>.manifest.tmp|written|ready`, mode 0644, regular and singly linked;
  - every move is a real `rename(2)`, never link plus unlink (the agent refuses a file with two links);
  - the process never deletes a `.written` or `.ready` file.
- **DTOs:** `[Serializable]` classes with public camelCase fields named exactly as the backend's JSON; arrays, no dictionaries, no nullables (`JsonUtility`). Shapes `JsonUtility` cannot represent (nulls with meaning, polymorphic members) use the strict reader (D4).
- **Secrets:** no DTO overrides `ToString`. No test or log prints an access token, ticket, process credential or seed.
- **Commits:** stage by explicit paths, never `git commit -a`. Keep the owner's `Assets/Settings/Mobile_RPAsset.asset`, `ProjectSettings/URPProjectSettings.asset` and the untracked `.claude/` unstaged and unchanged. Commit messages end with the attribution line given in the session.
- **No infrastructure:** no VM, systemd template, SSH, firewall, Docker or live-stack change, and no deployment. No real Unity mission workload. B24 is untouched.

## Review Focus

1. **A stackable collected partly (the bag fills up), then the rest of the same line collected later**, by the same operative or another. Expected:
   - one loot lot per (operative, line), merged, keeping its first acquisition sequence;
   - the container line's remainder tracked exactly;
   - collected never exceeds the pool line.

   Task 4 (`Collect_partial_then_rest_merges_one_lot_per_line`) and Task 5 (the bag-full simulation).
2. **A container filled with two plan items of the same stackable, which `ItemInventory.Add` merges into one entry.** The pool line is the entry index after the fill, so the ledger's pool lines, the `ready` report's pool and `LootPoolHash` must agree on one line. Task 4 (`RegisterContainer_uses_entries_after_fill`).
3. **A crash between the rename and the directory fsync, a leftover `.tmp` beside a `.written`, and a second write for the same participant.** Expected: `.written` is complete or absent, never partial. A `.tmp` is never promoted or relayed. A durable result is never replaced: identical bytes are a no-op, different bytes are refused as `Conflict`. Task 8.
4. **A runtime string the format refuses**, such as an objective id `Extract` (uppercase), a definition id with a space, or a build revision over 64 characters. The builder fails closed with the format's code (`ManifestFormatException`) and never produces bytes the backend would answer with 422. Task 6.
5. **Play after the freeze** (decision 033): a collect, use or equip after the participant's outcome changes neither the facts nor the bytes. The container's remainder still follows real takes. Task 4 (`Events_after_freeze_change_no_facts`) and Task 6.

---

## File structure

```
Assets/_Project/Scripts/Backend/Core/
  Blackglass.Backend.Core.asmdef      no references, noEngineReferences, autoReferenced false
  AssemblyInfo.cs                      InternalsVisibleTo("Blackglass.Tests.EditMode")
  Ids/Base58Id.cs                      port of Backend Base58Id (Format, TryParse)
  Ids/CanonicalIds.cs                  UUID, UUIDv7, sha256:, layout hash, catalogue-id, word and build-revision forms
  Json/JsonNode.cs                     the strict reader's tree (object members kept in order, raw number text kept)
  Json/StrictJsonReader.cs             UTF-8 JSON → JsonNode; syntax errors only (NotJson)
  Json/CanonicalJsonWriter.cs          port of CanonicalJson.ToBytes
  Manifest/ManifestCodes.cs            the 15 refusal codes
  Manifest/ResultManifestV1.cs         the typed model (facts only), enums ManifestOutcome, ManifestIntegrity, ObjectiveStateKind, EquipSlot
  Manifest/ManifestCodec.cs            Parse (strict, first refusal, same order as the backend), ToCanonicalBytes, HashOf
  Manifest/ManifestBuilder.cs          ParticipantFacts + header + result → BuiltManifest, with the fail-closed self-check
  Manifest/LootPoolHash.cs             port of Backend LootHash.Compute
  Ledger/LedgerCatalogue.cs            definition id → stackable, consumable, slot
  Ledger/LedgerInputs.cs               OperativeSnapshot, ContainerEntry, RuntimeEntry, RuntimeEquipment, PoolLine
  Ledger/MissionItemLedger.cs          lots, events, verification, freeze
  Ledger/ParticipantFacts.cs           the per-operative facts a manifest reports
  Outbox/IDurableFileSystem.cs         the seam: write-durable, rename, sync-directory, read, exists
  Outbox/PortableDurableFileSystem.cs  FileStream + Flush(true) + File.Move; directory sync is a no-op (Editor, Windows)
  Outbox/PosixDurableFileSystem.cs     libc open/write/fsync/close/rename (Linux x86_64 server)
  Outbox/OutboxWriter.cs               .tmp → .written → .ready for one attempt directory
  Outbox/ResultPublication.cs          §H order as a main-thread state machine: durable write, notice or deadline, publish
  Dto/ProcessDtos.cs                   /server/v1 requests and responses, LoadoutSnapshotDto
  Dto/ClientDtos.cs                    /v1 login, me, roster, inventory read, deploy, attempt, tickets
  Dto/ReceiptV1.cs                     receipt read model + reader on StrictJsonReader (D4)
  Net/INetAdmission.cs                 the admission boundary and its value types (D5)
Assets/_Project/Tests/EditMode/Backend/
  CoreSourceAuditTests.cs, Base58IdTests.cs, CanonicalIdsTests.cs, ManifestGoldenVectorTests.cs, ManifestCodecTests.cs,
  LootPoolHashTests.cs, DtoTests.cs, ReceiptVectorTests.cs, MissionItemLedgerTests.cs, LedgerSimulation.cs (test helper),
  LedgerSimulationTests.cs, ManifestBuilderTests.cs, LedgerScenarioVectorTests.cs, OutboxWriterTests.cs,
  FaultingFileSystem.cs (test helper), ResultPublicationTests.cs, NetAdmissionContractTests.cs
Backend/contracts/ledger-v1/
  .gitattributes ("* -text"), README.md, NN-name.json (scenarios with hand-written expected manifests)
Backend/tests/Accounts.Domain.Tests/Settlement/LedgerScenarioTests.cs
Backend/tests/UnityCore.Linux.Tests/      (D8) xUnit, compiles Core's Outbox and Ids sources by link, references MissionHost.Agent
```

Unity creates a `.meta` file for every new file and folder at the first batch run; commit them with their files. Test classes use the namespace `Blackglass.Tests` like the existing ones. Core's namespace is `Blackglass.Backend.Core`.

Focused run (each task): `Tools/run-tests.sh EditMode "<fully qualified class or namespace regex>"`. Unity's `-testFilter` accepts a semicolon-separated list and regular expressions. Expected EXIT: 0 when all pass, 2 on test failures, 1 on a compile error.

---

## Task 0: record the plan's authorization (docs only, after the owner's rulings)

**Files:** this plan (rulings recorded under "Decisions for the owner"), `Docs/superpowers/plans/2026-10-14-phase-13-backend.md` (Stage 11 status), `CLAUDE.md` (status lines).

- [ ] **Step 1:** Record the owner's rulings on D1–D8, and any plan change they require, in this plan (revision 2).
- [ ] **Step 2:** Re-measure both baselines if `main` moved since `6bdc6ca`; update the Baselines table.
- [ ] **Step 3:** Commit the docs by explicit paths.

## Task 1: The core assembly, ids and the library audit

**Files:**
- Create: `Scripts/Backend/Core/Blackglass.Backend.Core.asmdef`, `AssemblyInfo.cs`, `Ids/Base58Id.cs`, `Ids/CanonicalIds.cs`
- Modify: `Tests/EditMode/Blackglass.Tests.EditMode.asmdef` (add `"Blackglass.Backend.Core"` to `references`)
- Test: `Tests/EditMode/Backend/CoreSourceAuditTests.cs`, `Base58IdTests.cs`, `CanonicalIdsTests.cs`

**Interfaces (produced):**
```csharp
namespace Blackglass.Backend.Core
{
    public static class Base58Id
    {
        public const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        public const int MaxLength = 11;                       // long.MaxValue = "NQm6nKp8qFC"
        public static string Format(long id);                  // id >= 1, else ArgumentOutOfRangeException
        public static bool TryParse(string text, out long id); // exact canonical spelling only (Backend Base58Id.TryParse)
    }
    public static class CanonicalIds
    {
        public static bool IsUuid(string text);         // 36 chars, lowercase hex, hyphens at 8,13,18,23 (Backend CanonicalUuid)
        public static bool IsAttemptId(string text);    // IsUuid && text[14]=='7' && text[19] in "89ab"
        public static bool IsSha256(string text);       // ^sha256:[0-9a-f]{64}$
        public static bool IsLayoutHash(string text);   // ^[0-9a-f]{16}$
        public static bool IsDefinitionId(string text); // Backend CanonicalCatalogueJson.IsWellFormedId: [a-z0-9]+([.-][a-z0-9]+)*, at most 64
        public static bool IsWord(string text, int maxLength);    // Backend StrictJson.IsWord: [a-z0-9]+([._-][a-z0-9]+)*
        public static bool IsBuildRevision(string text); // Backend BuildIdentity.IsWellFormedRevision: ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$
    }
}
```
Each pattern is copied from the named backend member. Read those files first; where this table and the backend differ, the backend wins and the difference goes in the ledger as a ruling.

Asmdef:
```json
{ "name": "Blackglass.Backend.Core", "rootNamespace": "Blackglass.Backend.Core", "references": [], "includePlatforms": [],
  "excludePlatforms": [], "allowUnsafeCode": false, "overrideReferences": true, "precompiledReferences": [],
  "autoReferenced": false, "defineConstraints": [], "versionDefines": [], "noEngineReferences": true }
```
`overrideReferences: true` with an empty `precompiledReferences` keeps every precompiled DLL out, including FishNet's.

- [ ] **Step 1: Write the failing tests.**
  - `CoreSourceAuditTests`:
    - `Every_core_file_names_no_library`: every `*.cs` under `Assets/_Project/Scripts/Backend/Core` (recursive; the folder is non-empty, so the audit can't pass vacuously) has no `using` directive or qualified name (regex `\b(using\s+)?(UnityEngine|UnityEditor|FishNet|GameKit|Unity\.Netcode|Unity\.Networking|Unity\.Transport|Unity\.Collections|System\.Net|Mirror|LiteNetLib)\b\s*[.;]`) and no `UnityWebRequest` identifier, after stripping comments. Matching whole names, not substrings, keeps a comment such as "mirrors the backend" from failing the audit; the asmdef is the real boundary.
    - `The_core_assembly_definition_has_no_references`: parse the asmdef text, then assert `"references": []`, `"noEngineReferences": true`, `"overrideReferences": true` and `"precompiledReferences": []`.
    - `The_gameplay_assembly_does_not_reference_core_yet`: `Scripts/Blackglass.asmdef` doesn't contain `Blackglass.Backend.Core` (Stage 12 adds it).
  - `Base58IdTests`:
    - round trips 1, 57, 58, 3364, `long.MaxValue` (`"NQm6nKp8qFC"`);
    - refuses `""`, `"1"`, `"12"` (leading zero digit), `"0"`, `"O"`, `"I"`, `"l"`, `"NQm6nKp8qFD"` (overflow), 12 characters and non-ASCII;
    - is case-sensitive (`"2k"` ≠ `"2K"`);
    - matches the backend's examples used in the vectors (`"JG"`, `"C"`, `"D"`, `"2k"`, `"2m"`, `"2n"`);
    - orders numerically, not lexically (`"z"` = 57 < `"21"` = 58).
  - `CanonicalIdsTests`:
    - one accepted and several refused examples per predicate, including the attempt id from the vectors;
    - an uppercase UUID and a version-4 UUID as attempt ids (refused);
    - `sha256:` with 63 or 65 digits or uppercase hex (refused);
    - the definition id `item.medkit` (accepted) and `Item.Medkit`, `item..x`, a 65-character id (refused);
    - the build revision `r1` (accepted), `-r1` and 65 characters (refused).
- [ ] **Step 2: Run** `Tools/run-tests.sh EditMode "Blackglass.Tests.CoreSourceAuditTests;Blackglass.Tests.Base58IdTests;Blackglass.Tests.CanonicalIdsTests"`. Expected: EXIT=1 (compile error: `Blackglass.Backend.Core` doesn't exist).
- [ ] **Step 3: Implement** the asmdef, `AssemblyInfo.cs` and the two id classes as ports of the backend members named above. `Base58Id.Format` and `TryParse` follow `Backend/src/Accounts.Domain/Ids/Base58Id.cs` line for line (overflow guard `value > (long.MaxValue - digit) / 58`).
- [ ] **Step 4: Run** the same command. Expected: EXIT=0, all new tests pass.
- [ ] **Step 5: Run** the full EditMode suite: `Tools/run-tests.sh EditMode`. Expected: 1579 plus this task's tests, 0 failures (the asmdef change breaks nothing).
- [ ] **Step 6: Commit** the new files, their `.meta` files and the test asmdef.

## Task 2: Strict JSON, canonical bytes, the manifest codec and the golden vectors

**Files:**
- Create: `Json/JsonNode.cs`, `Json/StrictJsonReader.cs`, `Json/CanonicalJsonWriter.cs`, `Manifest/ManifestCodes.cs`, `Manifest/ResultManifestV1.cs`, `Manifest/ManifestCodec.cs`, `Manifest/LootPoolHash.cs`
- Test: `Tests/EditMode/Backend/ManifestGoldenVectorTests.cs`, `ManifestCodecTests.cs`, `LootPoolHashTests.cs`

**Interfaces (produced):**
```csharp
public abstract class JsonNode { }   // JsonObject (List<KeyValuePair<string, JsonNode>> Members, duplicates kept, in order),
                                     // JsonArray (List<JsonNode>), JsonString (string), JsonNumber (string RawText), JsonBool, JsonNull
public static class StrictJsonReader
{
    // RFC 8259 syntax over strictly decoded UTF-8 (invalid UTF-8, a BOM, a trailing comma, a leading zero, a comment, nesting
    // deeper than 64 or trailing text → false). Duplicate keys are kept (the codec refuses them later, in its own order).
    // Numbers keep their raw text. A string (value or member name) holding a lone surrogate escape is kept with
    // JsonString.InvalidUtf16 = true: the codec raises NotJson only where the backend's GetString()/Name would throw (M1).
    public static bool TryRead(byte[] utf8, out JsonNode root);
}
public static class CanonicalJsonWriter
{
    // Port of CanonicalJson.ToBytes: members sorted with StringComparer.Ordinal, a duplicate member is an ArgumentException,
    // arrays in the given order, a JsonNumber written as its raw text (the codec only builds nodes from validated integer literals,
    // via JsonNumber.FromInt(long) = long.ToString(CultureInfo.InvariantCulture)), minimal escapes, UTF-8 without a BOM.
    public static byte[] ToBytes(JsonNode node);
}
public static class ManifestCodes { public const string TooLarge = "TooLarge", NotJson = "NotJson", TooDeep = "TooDeep",
    WrongType = "WrongType", DuplicateKey = "DuplicateKey", UnknownMember = "UnknownMember", MissingMember = "MissingMember",
    UnsupportedFormat = "UnsupportedFormat", NotInteger = "NotInteger", OutOfBounds = "OutOfBounds", NonCanonicalId = "NonCanonicalId",
    BadValue = "BadValue", BadEnum = "BadEnum", BackendOnlyOutcome = "BackendOnlyOutcome", DuplicateEntry = "DuplicateEntry";
    public static readonly IReadOnlyList<string> All; }   // the 15, in this order

public enum ManifestOutcome { Success, Defeat, Abandoned, Disconnected, Expired }   // words: success, defeat, abandoned, disconnected, expired
public enum ManifestIntegrity { Ok, LedgerMismatch }                                // words: ok, ledgerMismatch
public enum ObjectiveStateKind { Active, Completed, Failed }
public enum EquipSlot { Armor, Utility, Weapon }                                   // words: armor, utility, weapon; lists sort by the word (ordinal), never the enum value

public sealed class ResultManifestV1   // immutable: a constructor taking every member, get-only properties, lists copied
{
    public const int FormatVersion = 1, MaxBytes = 65536, MaxElapsedMs = 86400000, MaxDepthBelowRoot = 4, MaxOperatives = 8,
        MaxObjectives = 64, MaxEntries = 256, MaxStringBytes = 128, MaxContainerOrLine = 65535;
    public string AttemptId { get; } public long AccountId { get; } public string ProcessSessionId { get; } public string BuildRevision { get; }
    public string CatalogueVersion { get; } public ManifestIntegrity Integrity { get; } public ManifestOutcome Outcome { get; }
    public int OutcomeSequence { get; } public int ServerElapsedMs { get; } public string LayoutHash { get; } public int LootTableVersion { get; }
    public string LootHash { get; } public IReadOnlyList<ObjectiveFact> Objectives { get; } public IReadOnlyList<OperativeFact> Operatives { get; }
}
public sealed class ObjectiveFact { public string ObjectiveId { get; } public ObjectiveStateKind State { get; } }
public sealed class OperativeFact
{
    public long OperativeId { get; } public int Slot { get; } public bool Alive { get; }
    public IReadOnlyList<OwnedUse> ConsumedOwned { get; }   // (DefinitionId, Quantity)
    public IReadOnlyList<LootUse> ConsumedLoot { get; }     // (ContainerIndex, Line, DefinitionId, Quantity)
    public IReadOnlyList<long> HeldItems { get; }
    public IReadOnlyList<OwnedStackFact> HeldStacks { get; } // (DefinitionId, Quantity)
    public IReadOnlyList<LootLotFact> HeldLoot { get; }      // (ContainerIndex, Line, Quantity)
    public IReadOnlyList<LineCount> Collected { get; }       // (ContainerIndex, Line, Quantity)
    public IReadOnlyList<EquipmentFact> Equipment { get; }   // (Slot, long? ItemId, int? ContainerIndex, int? Line): exactly one ref
}
public sealed class ManifestParseResult
{
    public bool Ok { get; }                 // true: Manifest, CanonicalBytes and ManifestHash are set; false: Code is set
    public ResultManifestV1 Manifest { get; } public byte[] CanonicalBytes { get; } public string ManifestHash { get; }
    public string Code { get; }
}
public static class ManifestCodec
{
    public static ManifestParseResult Parse(byte[] raw);          // never throws on bad input
    public static byte[] ToCanonicalBytes(ResultManifestV1 m);    // sorts every list by its stated key (port of ToCanonicalBytes)
    public static string HashOf(byte[] bytes);                    // "sha256:" + lowercase hex
}
public static class LootPoolHash { public static string Compute(int lootTableVersion, IReadOnlyList<PoolLine> pool); }
```
`PoolLine` (`ContainerIndex`, `Line`, `DefinitionId`, `Quantity`) is declared in `Ledger/LedgerInputs.cs` in this task, because Task 4 uses it.

**The refusal order is the backend's, exactly.** Port `ResultManifest.Parse` and its private readers (`ReadRoot`, `ReadObjective`, `ReadOperative`, `RawMembers`, `Exact`, `Members`, `Required`, `Str`, `Int`, `Index`, `Quantity`, `Bool`, `OneOf`, `Hash`, `Base58`, `Definition`, `ListOf`, `Distinct`) statement by statement. In order:
1. over 65,536 bytes → `TooLarge`;
2. `StrictJsonReader.TryRead` false → `NotJson`;
3. the depth check over the whole tree (a container more than 4 levels below the root) → `TooDeep`;
4. the root's duplicate keys, unknown members, missing members;
5. `formatVersion` (`Int(0, int.MaxValue)` and then `!= 1` → `UnsupportedFormat`);
6. then `accountId`, `attemptId`, `buildRevision`, `catalogueVersion`, `integrity`, `layoutHash`, `lootHash`, `lootTableVersion`, `objectives` (then `Distinct` by id), `operatives` (then `Distinct` by slot, then by id), `outcome` (`infra_void` → `BackendOnlyOutcome` before `BadEnum`), `outcomeSequence`, `processSessionId`, `serverElapsedMs`.

**Inside an entry, members are read in the backend's constructor order, not ordinal (M1):** a loot consumption reads `containerIndex`, `line`, `definitionId`, `quantity`; an owned consumption `definitionId`, `quantity`; an equipment item ref `slot` then `itemId`; a loot equipment ref `slot`, `containerIndex`, `line`; a collected entry `containerIndex`, `line`, `quantity`; a held loot lot `containerIndex`, `line`, `quantity`; a held stack `definitionId`, `quantity`. Inside an operative, members are read in this order: `alive`, `collected` (then `Distinct`), `consumed` (each entry: `Required("source")`, `OneOf`, then `Exact`; `Distinct` loot by line, then owned by definition), `equipment` (an entry with `itemId` is an item ref, otherwise a loot ref; `Distinct` by slot), `held` (`Required("origin")`; owned with `itemId` is an item, owned without it a stack; `Distinct` loot, items, stacks), `operativeId`, `slot`.

`Int` checks:
- the JSON kind (`WrongType`);
- the raw literal against `^(?:0|[1-9][0-9]*)$` (`NotInteger`);
- the length > 10, the parse and the range (`OutOfBounds`).

`Str` checks the kind (`WrongType`), then a lone surrogate (`NotJson`, where the backend's `GetString()` throws), then the UTF-8 byte count > 128 (`OutOfBounds`). `RawMembers` raises `NotJson` for a member name with a lone surrogate, where the backend's `p.Name` would throw. The reader itself never refuses a lone surrogate. The executor records any refusal-order difference found while porting as a ruling, and adds a Unity-only test for it (never a vector).

- [ ] **Step 1: Write the failing tests.**
  - `ManifestGoldenVectorTests` (reads `Backend/contracts/manifest-v1` through `ContractVectors.ProjectRoot`, files as raw bytes with `File.ReadAllBytes`):
    - `Every_valid_vector_canonicalizes_to_its_bytes_and_hash`:
      - at least 13 inputs (the folder holds 13);
      - for each: `Parse` is `Ok`, the canonical bytes equal `.canonical.json` byte for byte, and the hash equals `.hash.txt` without its final `\n`;
      - the canonical form is a fixed point (`Parse(canonical).CanonicalBytes` equals it).
    - `Every_invalid_vector_is_refused_with_its_code`: for each input, the code equals `.code.txt` without its final `\n`. The set of codes seen equals `ManifestCodes.All` minus `TooLarge`, so a lost vector fails the test.
    - `Vectors_10_11_12_share_02s_canonical_bytes`: the README's statement, checked.
    - `A_body_over_65536_bytes_is_TooLarge`: built in the test like the backend's: the 02-full canonical bytes padded inside `buildRevision`'s place with spaces before the closing brace to 65,537 bytes. `TooLarge` wins over everything else.
    - `The_vector_folder_is_unchanged`: the folder's file set equals the dictionary's keys (no file added or removed, M13), and the SHA-256 of each file's bytes, listed in the dictionary, equals the file's. This pins "never silently modify the vectors". The executor fills the dictionary by hashing the files at `6bdc6ca` (`git show 6bdc6ca:<path> | sha256sum`), never from the working tree after a change.
  - `ManifestCodecTests` (Unity-only cases the vectors don't cover; each is one input and its expected code or bytes):
    - a BOM → `NotJson`; a comment → `NotJson`; a lone surrogate escape `"\ud800"` in `buildRevision` → `NotJson`;
    - a lone surrogate in `outcome` together with `"formatVersion":2` → `UnsupportedFormat` (the backend decodes `outcome` only later);
    - nesting 64 deep → `TooDeep`; 65 deep → `NotJson` (the backend's `MaxDepth = 64` fails first);
    - entry member order: an equipment `{"itemId":"0","slot":"x"}` → `BadEnum` (slot read first); a loot consumption with a bad `line` and a bad `definitionId` → `OutOfBounds` (line before definition);
    - numeric order of held items and equipment (M3): ids `"z"` (57) and `"21"` (58) sort `"z"` first, the reverse of an ordinal string sort;
    - a root that is an array → `WrongType`;
    - `"formatVersion":2` → `UnsupportedFormat`; `"formatVersion":"1"` → `WrongType`;
    - an 11-digit `outcomeSequence` → `OutOfBounds`; `serverElapsedMs` 86,400,001 → `OutOfBounds`;
    - `slot` 8 → `OutOfBounds`; an operatives list of 0 → `OutOfBounds`; 257 `held` entries → `OutOfBounds`;
    - a 129-byte string (two-byte UTF-8 characters) → `OutOfBounds`;
    - a held stack with `itemId` and `definitionId` → `UnknownMember`; a consumption without `source` → `MissingMember`;
    - two equipment entries for `weapon` → `DuplicateEntry`; two operatives with one id in different slots → `DuplicateEntry`;
    - `ToCanonicalBytes` of a model built in code (not parsed) equals the 02-full canonical bytes;
    - `HashOf(empty)` = `sha256:e3b0c442…b855` (the well-known empty digest, written out in full in the test).
  - The Unity-only syntax expectations (BOM, comment, lone surrogate, 64 and 65 deep) are inferred from System.Text.Json, not pinned by the backend (M2). Task 7's backend test file asserts the same inputs against `ResultManifest.Parse` with the same codes, so both runtimes pin them.
  - `LootPoolHashTests`:
    - `Compute(1, [(1,0,"medkit",2),(0,1,"ammo-box",1),(0,0,"stim",3)])` equals the backend's pinned `sha256:bce2a2eda82db4789a87efbde6bcae0fe68342ced7877883a5024dded26591c0` (`ReadyReportTests.Hash`);
    - the reversed list gives the same hash; table version 2 gives a different one.
- [ ] **Step 2: Run** `Tools/run-tests.sh EditMode "Blackglass.Tests.ManifestGoldenVectorTests;Blackglass.Tests.ManifestCodecTests;Blackglass.Tests.LootPoolHashTests"`. Expected: EXIT=1 (types missing).
- [ ] **Step 3: Implement**:
  - the reader: a byte-level recursive-descent parser over `new UTF8Encoding(false, true)` decoded text, refusing a leading `U+FEFF`;
  - the writer and codec as ports;
  - `LootPoolHash` as a port of `LootHash.Compute` (`"blackglass-loot-pool/1\n"`, `"table=<v>\n"`, then `"<ci>,<line>,<def>,<qty>\n"` per line sorted by `(ci, line)`, invariant culture).
- [ ] **Step 4: Run** the same command. Expected: EXIT=0. Then run the full EditMode suite. Expected: 0 failures.
- [ ] **Step 5: Commit.**

## Task 3: Backend DTOs and the receipt reader

**Files:**
- Create: `Dto/ProcessDtos.cs`, `Dto/ClientDtos.cs`, `Dto/ReceiptV1.cs`
- Test: `Tests/EditMode/Backend/DtoTests.cs`, `ReceiptVectorTests.cs`

DTOs carry backend-owned persistent data as the backend sends it: Base58 strings, catalogue ids, versions. They are never Unity's runtime state. `InventoryState`, `PersistentOperativeState`, `ItemInventory` and `SquadRoster` are untouched, and mapping DTOs to runtime state is Stage 12's (server) and Stage 16's (client), each at one boundary. Field names are the backend's JSON names. The backend source of each shape is cited so the executor copies member lists from code, not from this table.

| DTO (`[Serializable]`, public fields) | Shape | Backend source |
|---|---|---|
| `SessionRegisterRequest` | `{buildRevision, catalogueVersion}` | `MissionRequests.cs` `RegisterRequest.Parse` |
| `SessionRegisterResponse` | `{processSessionId, seed, missionTemplate:{id, version}, catalogueVersion, participants:[{accountId, loadout}], deadlines:{launch}}` | `ProcessEndpoints.cs` `RegisterResponse`, `TemplateDto`, `ParticipantDto`, `LaunchDeadlineDto` |
| `LoadoutSnapshotDto` | `{accountId, inventoryVersion, catalogueVersion, operatives:[{slot, operativeId, definitionId, experience, rank, choices[], bag:{instances:[{itemId, definitionId}], stacks:[{definitionId, quantity}]}, equipment:{weapon, armor, utility}}]}` | `LoadoutSnapshot.ToJson` |
| `ReadyRequest` / `ReadyResponse` | `{processSessionId, layoutHash, lootTableVersion, lootHash, objectives:[{objectiveId, required}], lootPool:[{containerIndex, line, definitionId, quantity}]}` / `{joinDeadline}` | `ReadyReport.Parse`, `ReadyResponse` |
| `StartRequest` / `StartResponse` | `{processSessionId}` / `{started, startedAt}` | `StartRequest.Parse`, `ProcessEndpoints` start |
| `RedeemRequest` / `RedeemResponse` | `{processSessionId, ticket}` / `{accountId, operativeIds[]}` | `ProcessEndpoints` redeem, `RedeemResponse` |
| `ProcessHeartbeatRequest` | `{processSessionId, phase, participants:[{accountId, connected}]}` | `MissionRequests.cs` line 143 |
| `RecordedRequest` / `StateResponse` | `{manifestHash}` / `{state}` | `RecordedRequest.Parse`, `StateDto` |
| `FailedRequest` | `{processSessionId, reason}` | `FailedRequest.Parse` |
| `DevLoginRequest` / `DevLoginResponse` | `{accountKey}` / `{accessToken, expiresAt, accountId}` | spec §16.1, `Accounts.Api` auth endpoint |
| `MeResponse` | `{accountId, inventoryVersion, energy:{balance, cap, nextRegenAt}, insurance:{active, policyId, expiresAt}, activeAttemptId}` | §16.1 as built (Stages 4, 6, 7) |
| `RosterResponse` | `{version, operatives:[{operativeId, definitionId, definitionKnown, experience, rank, pendingPicks, choices[]}]}` | §16.1, roster endpoint |
| `InventoryResponse` | `{version, locked, stash:{instances:[{itemId, definitionId, definitionKnown}], stacks:[{definitionId, definitionKnown, quantity}]}, operatives:[{operativeId, bag:{instances, stacks}, equipment:{weapon, armor, utility}}]}` | §16.1, inventory endpoint |
| `DeployRequest` / `DeployResponse` | `{missionTemplate, operativeIds[]}` / `{attemptId, state, energy:{cost, balanceAfter}, insurance:{covered, policyId, policyVersion}}` | §16.1, `DeployRequest.cs` |
| `TicketResponse` | `{ticket, ticketExpiresAt}` | §16.1 |

**Not in Stage 11:** the inventory-operations request (polymorphic members that `JsonUtility` can't write without extra fields the backend refuses). Stage 16 writes it with `CanonicalJsonWriter` nodes.

`ReceiptV1` is a read model of receipt v1, read by `ReceiptV1.TryRead(byte[] utf8, out ReceiptV1 receipt)` on `StrictJsonReader`. `JsonUtility` cannot represent `alive: null` (an infrastructure void) or the two insurance shapes (`{covered:false}` and the full object). Its fields:
- `receiptVersion`, `attemptId`, `accountId`, `outcome` (a string; `infra_void` is valid here), `settledAt` (the string as sent), `catalogueVersion`, `inventoryVersion`;
- `energy {cost, refunded, state}`;
- `consumed[]` (`{operativeId, definitionId, quantity}`); `granted[]` (`{operativeId, definitionId, quantity, itemIds[]}`);
- `insurance`: either `{covered:false}`, or `{covered, applied, policyId, policyVersion, refunds:[{definitionId, quantity}], retained:[{definitionId, itemIds[], quantity}], xp:[{amount, operativeId}]}` (`receipt/02-insured-defeat.receipt.json`);
- `operatives[] {operativeId, alive: bool?}`;
- `xp[] {operativeId, amount}`.

The reader refuses a `receiptVersion` other than 1 and a missing required member. It tolerates unknown members: receipts are backend-written and may gain fields. Stage 16 only displays them.

- [ ] **Step 1: Write the failing tests.**
  - `DtoTests`:
    - for each response DTO, `JsonUtility.FromJson` of a backend-shaped sample (members in the backend's order, written in the test from the cited source) gives the expected values;
    - `LoadoutSnapshotDto` reads `"weapon":null` as an empty string, and an `EquipmentDto.Weapon`-style accessor returns null for it. The test pins what `JsonUtility` actually does with a JSON `null` in a string field; if it differs, the accessor adapts and the test records the behaviour;
    - `MeResponse` with `"inventoryVersion":null` and `"energy":null`: the test pins the values `JsonUtility` produces and documents that presence can't be read from these DTOs (Stage 16 reads `409 NotProvisioned` instead);
    - for each request DTO, the member names in `JsonUtility.ToJson`'s output equal exactly the backend parser's member list (no extra or missing member, since the backend refuses unknown members);
    - `SessionRegisterResponse.seed` exists only on that DTO: a reflection test asserts that no other type in `Blackglass.Backend.Core` has a member whose name contains `seed` or `salt` (case-insensitive).
  - `ReceiptVectorTests`: for each `Backend/contracts/manifest-v1/receipt/NN.receipt.json`, `TryRead` succeeds and its fields equal the matching `.input.json` values (outcome, energy, consumed, granted, insurance, operatives, xp, settledAt). `03-void`'s operatives read `alive` as null, and `02-insured-defeat`'s insurance reads `applied: true` with its refunds and retained lines. Retention vectors are not read (D4).
- [ ] **Step 2: Run** `Tools/run-tests.sh EditMode "Blackglass.Tests.DtoTests;Blackglass.Tests.ReceiptVectorTests"`. Expected: EXIT=1.
- [ ] **Step 3: Implement** the DTO classes (public fields, `[Serializable]`, nested classes for nested objects, arrays, no properties) and `ReceiptV1`.
- [ ] **Step 4: Run** the same command. Expected: EXIT=0.
- [ ] **Step 5: Commit.**

## Task 4: `MissionItemLedger`

**Files:**
- Create: `Ledger/LedgerCatalogue.cs`, `Ledger/LedgerInputs.cs` (adding to Task 2's `PoolLine`), `Ledger/MissionItemLedger.cs`, `Ledger/ParticipantFacts.cs`
- Test: `Tests/EditMode/Backend/MissionItemLedgerTests.cs`

**Model (spec §8.2, made exact):**

| Holder | Lots |
|---|---|
| An operative's bag | owned items: one per snapshot item id (the runtime entry id **is** the Base58 item id, as `OwnedIdCompatibilityTests` already pins). An owned stack: one per definition, quantity = the snapshot stack. Loot lots: one per `(containerIndex, line)`, with quantity and `acquiredSequence` (the global event counter at its first collect). A collected non-stackable's runtime entry id → its `(containerIndex, line)` |
| A container | one line per runtime entry after the fill, `line` = the entry's index: `(entryId, definitionId, remaining)` |

| Event (Stage 12's source) | Effect | Refused (integrity failure, code) |
|---|---|---|
| `Collect(operativeId, containerIndex, entryId, quantity)` (`UnitItems.TryCollect` per moved entry, with the quantity `ItemTransfer.Move` reported) | line `remaining -= q`; the operative's lot for that line `+= q` (created with the next sequence if absent, otherwise merged and keeping its sequence); `collected[line] += q`; for a non-stackable, remember `entryId → line` for this operative | `unknown_operative`, `unknown_container_entry`, `quantity_invalid` (q < 1), `line_exceeded` (q > remaining; nothing is applied) |
| `Use(operativeId, definitionId, quantity = 1)` (`UnitItems.Used`) | take q units: **loot lots of that definition first, in ascending `(acquiredSequence, containerIndex, line)`, then the owned stack**; record `consumedLoot[line] += n` and `consumedOwned[def] += n` | `use_not_stackable` or `use_not_consumable` (V8 would refuse it; nothing applied); `use_exceeds_held` (what is held is consumed, and the shortfall is a failure) |
| `Equip(operativeId, entryId)` (`UnitItems.TryEquip` success) | the slot is the definition's (`LedgerCatalogue`), never the caller's word. `slot → item(itemId)` for an owned item of this operative, or `slot → loot(ci, line)` for a collected non-stackable it holds. The previous occupant stays held (a swap) | `equip_not_held`, `equip_no_slot` |
| `Freeze(accountId, operativeBags, containers)` (the participant's outcome freeze) | first runs `VerifyOperative` for each of its operatives and `VerifyContainer` for every container, from the runtime state passed in (fail closed: a failure marks `LedgerMismatch`, M10); then snapshots that participant's facts and integrity. Later events for its operatives change no facts and return `Frozen`. A later collect still lowers the container line's remainder (shared world state), so container verification stays exact. Idempotent | none |

**Integrity** is per participant and sticky. Any refused event, any failed verification and any `ReportIntegrityFailure` marks every unfrozen participant whose operative the event names as `LedgerMismatch`; a container failure or an `unknown_operative` refusal (which names nobody) marks every unfrozen participant. Failure codes are bounded words, `[a-z_]{1,40}`, at most 32 distinct per participant. Facts stay well-formed after a failure: no zero or negative quantity and no duplicate entry, so the manifest still parses and the backend quarantines it with V14 instead of answering 422.

**Interfaces (produced):**
```csharp
public sealed class LedgerItemDefinition
{
    public LedgerItemDefinition(string id, bool stackable, bool consumable, EquipSlot? slot);
    public string Id { get; } public bool Stackable { get; } public bool Consumable { get; } public EquipSlot? Slot { get; }
}
public sealed class LedgerCatalogue { public LedgerCatalogue(IEnumerable<LedgerItemDefinition> definitions); public bool TryGet(string id, out LedgerItemDefinition d); }

public sealed class OperativeSnapshot
{
    public OperativeSnapshot(string operativeId, int slot, IReadOnlyList<SnapshotItem> items, IReadOnlyList<SnapshotStack> stacks,
        string weaponItemId, string armorItemId, string utilityItemId);   // null or "" = empty slot
    public static IReadOnlyList<OperativeSnapshot> FromDto(LoadoutSnapshotDto dto);   // the one DTO → ledger boundary
}
public sealed class SnapshotItem { public SnapshotItem(string itemId, string definitionId); ... }
public sealed class SnapshotStack { public SnapshotStack(string definitionId, int quantity); ... }
public sealed class ContainerEntry { public ContainerEntry(string entryId, string definitionId, int quantity); ... }   // index = line
public sealed class RuntimeEntry { public RuntimeEntry(string entryId, string definitionId, int quantity); ... }        // an ItemEntry, copied
public sealed class RuntimeEquipment { public RuntimeEquipment(string weaponEntryId, string armorEntryId, string utilityEntryId); ... }
public sealed class PoolLine { public PoolLine(int containerIndex, int line, string definitionId, int quantity); ... }

public enum LedgerEventResult { Applied, Frozen, Refused }
public sealed class LedgerCheck { public bool Ok { get; } public IReadOnlyList<string> Codes { get; } }

public sealed class MissionItemLedger
{
    public MissionItemLedger(LedgerCatalogue catalogue);
    public void AddParticipant(string accountId, IReadOnlyList<OperativeSnapshot> operatives);   // malformed snapshot → ArgumentException
    public void RegisterContainer(int containerIndex, IReadOnlyList<ContainerEntry> entriesAfterFill);  // once per index; ArgumentException on reuse
    public IReadOnlyList<PoolLine> PoolLines();                    // sorted (containerIndex, line): the ready report's lootPool
    public LedgerEventResult Collect(string operativeId, int containerIndex, string entryId, int quantity);
    public LedgerEventResult Use(string operativeId, string definitionId, int quantity = 1);
    public LedgerEventResult Equip(string operativeId, string entryId);
    public LedgerCheck VerifyOperative(string operativeId, IReadOnlyList<RuntimeEntry> bag, RuntimeEquipment equipment);
    public LedgerCheck VerifyContainer(int containerIndex, IReadOnlyList<RuntimeEntry> contents);
    public void ReportIntegrityFailure(string accountId, string code);
    public ParticipantFacts Freeze(string accountId, IReadOnlyList<OperativeRuntimeState> operatives, IReadOnlyList<ContainerRuntimeState> containers);
    // OperativeRuntimeState(operativeId, bag entries, RuntimeEquipment); ContainerRuntimeState(containerIndex, entries)
    public bool IsFrozen(string accountId);
}
public sealed class ParticipantFacts
{
    public string AccountId { get; } public ManifestIntegrity Integrity { get; } public IReadOnlyList<string> FailureCodes { get; }
    public IReadOnlyList<OperativeFacts> Operatives { get; }   // slot order
}
public sealed class OperativeFacts
{
    public string OperativeId { get; } public int Slot { get; }
    public IReadOnlyList<OwnedUse> ConsumedOwned { get; } public IReadOnlyList<LootUse> ConsumedLoot { get; }
    public IReadOnlyList<string> HeldItems { get; } public IReadOnlyList<OwnedStackFact> HeldStacks { get; }
    public IReadOnlyList<LootLotFact> HeldLoot { get; } public IReadOnlyList<LineCount> Collected { get; }
    public IReadOnlyList<LedgerEquipment> Equipment { get; }   // (EquipSlot, string ItemId | (ContainerIndex, Line))
}
```
`OwnedUse`, `LootUse`, `OwnedStackFact`, `LootLotFact` and `LineCount` are Task 2's model types, reused. Facts list only positive quantities. An owned stack with 0 left is omitted, which is exactly V10's `before − used = held` with `held` absent meaning 0.

**`AddParticipant` refuses:**
- an operative id or item id that isn't canonical Base58;
- a duplicate operative id, slot or item id across the ledger;
- a slot outside 0..7;
- an unknown definition;
- an item whose definition is stackable, or a stack whose definition isn't;
- a stack quantity below 1;
- an equipped item id not among the operative's items, or in the wrong slot for its definition.

A malformed snapshot is a backend invariant failure. Stage 12 then reports `failed` before the start, so nothing is charged.

**Verification (`VerifyOperative`):**
- per definition, the runtime quantity (sum of entries) equals the ledger's: owned items of that definition + owned stack + loot lots of that definition (a collected non-stackable is a loot lot of quantity 1, counted once there);
- every runtime non-stackable entry id maps to exactly one ledger item (an owned item id or a collected loot entry), and every ledger item has its entry;
- each runtime equipped entry id resolves to the ledger's ref for that slot, and an empty runtime slot means an empty ledger slot.

`VerifyContainer`: each line's entry id is present with `remaining` units and its definition, or absent when `remaining` is 0. There are no other entries.

- [ ] **Step 1: Write the failing tests** (`MissionItemLedgerTests`; a small golden-like catalogue: `rifle` (non-stackable, weapon), `vest` (armor), `boots` (utility), `kit` (stackable max 3, consumable), and, for the refusal test only, `scrap` (stackable, not consumable: invalid in both real catalogues, M8, so it never appears in Task 5's runs)):
  - **Snapshot:** `AddParticipant_refuses_each_malformed_snapshot` (one case per refusal above); `Facts_of_an_untouched_snapshot_hold_exactly_the_snapshot` (items, one stack per definition, equipment).
  - **Container:**
    - `RegisterContainer_uses_entries_after_fill`: two plan items of `kit` merged by the caller into one entry are one line, and `PoolLines` lists it once;
    - `PoolLines_are_sorted_and_match_LootPoolHash_input`;
    - `RegisterContainer_twice_for_one_index_throws`.
  - **Collect:**
    - `Collect_moves_units_and_records_collected`;
    - `Collect_partial_then_rest_merges_one_lot_per_line`: 2 of 3, then 1, gives one lot of 3 with the first sequence and collected 3;
    - `Collect_beyond_the_line_is_refused_and_applies_nothing`;
    - `Collect_from_an_unknown_entry_is_refused`;
    - `Two_operatives_collect_one_line_without_exceeding_it`.
  - **Use:**
    - `Use_takes_loot_before_owned`: owned 2 + loot 2, use 3 gives loot 2, then owned 1;
    - `Use_takes_the_oldest_loot_first`: line (1,0) collected before (0,0) means (1,0) is used first despite its larger container index;
    - `Use_of_a_non_stackable_or_non_consumable_is_refused`;
    - `Use_beyond_holdings_consumes_what_is_held_and_fails_integrity`.
  - **Equip:**
    - `Equip_an_owned_item_swaps_and_keeps_the_old_one_held`;
    - `Equip_a_collected_rifle_refers_to_its_line`;
    - `Equip_an_item_of_another_operative_is_refused`;
    - `Equip_a_stack_or_slotless_item_is_refused`.
  - **Verify:**
    - `VerifyOperative_ok_after_each_event`, against hand-built runtime entries mirroring what `ItemInventory` would hold;
    - `VerifyOperative_detects_a_count_mismatch_an_unmapped_item_a_missing_item_and_an_equipment_mismatch` (four cases, each a distinct code);
    - `VerifyContainer_detects_extra_and_short_entries`.
  - **Integrity:**
    - `A_failure_is_sticky_and_keeps_facts_well_formed`: after a refused use, all quantities are ≥ 1 and there are no duplicates;
    - `ReportIntegrityFailure_marks_only_that_participant`;
    - failure codes outside `[a-z_]{1,40}` → `ArgumentException`.
  - **Freeze:**
    - `Freeze_verifies_and_a_mismatch_at_the_freeze_is_reported` (a runtime bag that disagrees at the freeze gives `LedgerMismatch`);
    - `Freeze_is_idempotent_and_returns_equal_facts`;
    - `Events_after_freeze_change_no_facts` (collect, use, equip after the freeze give `Frozen`, and the facts are equal);
    - `A_collect_after_freeze_still_lowers_the_container_line`.
  - **Conservation:** `Facts_conserve_after_every_event` uses a test helper `Conservation.Check(snapshot, facts, pool)` that asserts V10's three equations, V8's loot-collected rule and V12's held-ref rule, mirrored from `ManifestValidator.ValidateItems`. It is a fast inner check only: Task 7's ledger-v1 scenarios run by the real validator are the authority, and any disagreement is resolved there (M9). It checks after a fixed 20-event script mixing every event.
- [ ] **Step 2: Run** `Tools/run-tests.sh EditMode "Blackglass.Tests.MissionItemLedgerTests"`. Expected: EXIT=1.
- [ ] **Step 3: Implement.** The consumption order, exactly:
  ```csharp
  // Loot first, oldest first; then owned. Only lots of this operative and this definition.
  var lots = LootLotsOf(operative).Where(l => l.DefinitionId == definitionId && l.Quantity > 0)
      .OrderBy(l => l.AcquiredSequence).ThenBy(l => l.ContainerIndex).ThenBy(l => l.Line);
  var left = quantity;
  foreach (var lot in lots) { var n = Math.Min(left, lot.Quantity); lot.Quantity -= n; AddConsumedLoot(operative, lot, n); left -= n; if (left == 0) break; }
  if (left > 0) { var n = Math.Min(left, OwnedStack(operative, definitionId)); TakeOwned(operative, definitionId, n); left -= n; }
  if (left > 0) Fail(operative, "use_exceeds_held");
  ```
- [ ] **Step 4: Run** the same command. Expected: EXIT=0.
- [ ] **Step 5: Commit.**

## Task 5: The ledger against Unity's real inventory (simulation)

**Files:**
- Test: `Tests/EditMode/Backend/LedgerSimulation.cs` (helper), `LedgerSimulationTests.cs`

The helper drives **Unity's real item code**, `ItemInventory`, `ItemTransfer.Move`, `ItemInventory.Remove` and `OperativeEquipment.TryEquip`, with `ItemDefinition.Create` and `ItemCatalogue.Create` (internal, visible to the tests). For every mutation it calls the ledger event the Stage 12 wiring will call, with the quantity Unity reported, then calls `VerifyOperative` and `VerifyContainer` for every holder. Nothing in `Scripts/Items` changes. This proves the ledger's model of stacking, merging, splitting at `MaxStack`, partial moves at bag capacity and entry-id behaviour matches the real code.

```csharp
internal sealed class LedgerSimulation
{
    public LedgerSimulation(ItemCatalogue catalogue, LedgerCatalogue ledgerCatalogue, string accountId, IReadOnlyList<OperativeSnapshot> snapshot, int bagCapacity);
    public void FillContainer(int containerIndex, IEnumerable<(string definitionId, int quantity)> plan);  // ItemInventory(0).Add per plan item, then RegisterContainer(entries)
    // The ledger is told only what Unity actually applied (M6): no event for a refused or empty action.
    public TransferResult Collect(string operativeId, int containerIndex, int line, int quantity);  // ItemTransfer.Move(...); ledger.Collect(…, result.Moved) only when Moved >= 1
    public bool Use(string operativeId, string definitionId);   // UnitItems.Check's rules (Consumable, HealAmount >= 1) first; bag.Remove(entry, 1) == 1, then ledger.Use
    public bool Equip(string operativeId, string entryId);      // OperativeEquipment.TryEquip(...).Ok, then ledger.Equip
    public void RogueAdd(string operativeId, string definitionId, int quantity);   // bag.Add with no ledger event (tests and scenario 07 only)
    public ParticipantFacts Freeze();                            // the ledger's Freeze with the current runtime state
    public void AssertConsistent();                              // every VerifyOperative / VerifyContainer Ok
}
```

- [ ] **Step 1: Write the failing tests:**
  - `A_picked_medkit_merges_into_an_owned_stack`: owned 2 kits (max 3) + a loot kit gives one runtime entry of 3, yet the ledger keeps owned 2 and loot 1; using 1 consumes the loot first;
  - `Splitting_at_max_stack_is_tracked`: owned 2 + loot 3 gives runtime entries 3 + 2, and the ledger totals 5;
  - `A_full_bag_takes_part_of_a_stack_line` (capacity 2, a 3-kit line: a partial move; the remainder stays in the container under the same entry id, and a later collect after a use takes the rest);
  - `A_non_stackable_keeps_its_entry_id_through_collect_and_equip`;
  - `Two_lines_of_one_definition_are_used_oldest_first`;
  - `A_rogue_bag_change_is_caught`: `bag.Add` without a ledger event, then `VerifyOperative` fails with `bag_count_mismatch`, and the frozen facts report `LedgerMismatch`;
  - `Seeded_random_missions_stay_consistent_and_conserve`: 200 seeded runs (fixed seeds 1..200, `System.Random`), each 1–3 operatives, 1–4 containers filled from random plans of the five test definitions, 0–40 random actions (collect with a random quantity, use, equip, including ones Unity refuses). `AssertConsistent` after every action. At the end, `Freeze` and `Conservation.Check`. The integrity must be `Ok` in every run. Runtime under 10 s.
- [ ] **Step 2: Run** `Tools/run-tests.sh EditMode "Blackglass.Tests.LedgerSimulationTests"`. Expected: EXIT=2 or EXIT=1 until the helper compiles. Write the helper first, then watch the tests fail on any ledger gap Task 4 missed. If all pass at once, check that `A_rogue_bag_change_is_caught` fails when its assertion is inverted. That proves the harness detects mismatches.
- [ ] **Step 3: Fix** the ledger for any mismatch found (each fix as a ruling in the ledger if it changes Task 4's model).
- [ ] **Step 4: Run** the same command. Expected: EXIT=0.
- [ ] **Step 5: Commit.**

## Task 6: `ManifestBuilder`

**Files:**
- Create: `Manifest/ManifestBuilder.cs`
- Test: `Tests/EditMode/Backend/ManifestBuilderTests.cs`

**Interfaces (produced):**
```csharp
public sealed class ManifestHeader     // per attempt and participant; no seed, no salt, no secret
{
    public ManifestHeader(string attemptId, string accountId, string processSessionId, string buildRevision, string catalogueVersion,
        string layoutHash, int lootTableVersion, string lootHash);
}
public sealed class ObjectiveResult { public ObjectiveResult(string objectiveId, ObjectiveStateKind state); }
public sealed class ParticipantResult  // the authoritative outcome at the freeze
{
    public ParticipantResult(ManifestOutcome outcome, int outcomeSequence, int serverElapsedMs, IReadOnlyList<ObjectiveResult> objectives,
        IReadOnlyCollection<string> deadOperativeIds);   // alive = not in this set
}
public sealed class BuiltManifest { public byte[] CanonicalBytes { get; } public string ManifestHash { get; } public ResultManifestV1 Manifest { get; } }
public sealed class ManifestFormatException : Exception { public string Code { get; } }   // the format code; never carries a secret
public static class ManifestBuilder
{
    public static BuiltManifest Build(ManifestHeader header, ParticipantResult result, ParticipantFacts facts);
}
```

**Build steps:**
1. **Check the header** with `CanonicalIds` (each failure throws `ManifestFormatException` with the code the codec would give for the written value: `NonCanonicalId` for ids and hashes, `BadValue` for the build revision, `NotInteger` for a negative number (written as `-n`), `OutOfBounds` for a number above its bound). `header.accountId == facts.AccountId`, and `facts` comes from `Freeze` (an unfrozen ledger has no `ParticipantFacts`, by construction).
2. **Map facts to `ResultManifestV1`:**
   - Base58 → `long` through `Base58Id.TryParse`;
   - integrity from `facts.Integrity`;
   - objectives as given, with their ids checked by `CanonicalIds.IsWord(id, 64)` → `BadValue`;
   - `alive` from `deadOperativeIds`;
   - every operative of the participant, in slot order.
3. **`bytes = ManifestCodec.ToCanonicalBytes(model)`.**
4. **Self-check (fail closed, D2):** `ManifestCodec.Parse(bytes)` must be `Ok`. Its canonical bytes must equal `bytes`, its attempt and account must equal the header's, and `bytes.Length` must be ≤ 65,536. Otherwise throw `ManifestFormatException(code)`. The builder never returns bytes the backend's parser would refuse.
5. **Return** the bytes, `ManifestCodec.HashOf(bytes)` and the model.

- [ ] **Step 1: Write the failing tests:**
  - `Builds_02_full_from_facts`: a ledger fed the events that produce 02-full's operatives, a header and a result equal to 02-full's, give exactly `valid/02-full.canonical.json`'s bytes and `.hash.txt`'s hash. The events are written out in the test, in this order: operative C (slot 0, snapshot: items `2k` weapon and `2m` armor equipped, 3 medkits) uses one medkit (owned, since no loot is held yet), then collects (0,0)×2 and (0,1)×1 (medkit lines), then uses one medkit (loot first, oldest first: from (0,0)); operative D (slot 1, snapshot: item `2n` weapon equipped) collects (2,0)×1 (a utility item), equips it and is dead at the freeze. The facts then equal the vector: an owned use before the collects is the only order loot-first consumption allows;
  - `Builds_13_ledger_mismatch`: a forced integrity failure gives exactly `valid/13-ledger-mismatch`'s bytes;
  - `Every_outcome_builds` (five outcomes; the enum has exactly five names, and none of them is `infra_void`);
  - `The_same_inputs_give_the_same_bytes`: two independent ledgers fed the same events give byte-identical output;
  - `An_unfrozen_participant_cannot_be_built` (compile-time: `Build` takes `ParticipantFacts`, which only `Freeze` returns; the test asserts there is no public constructor, by reflection);
  - `Events_after_freeze_do_not_change_the_bytes` (Review Focus 5);
  - `A_refused_string_fails_closed` (Review Focus 4): objective id `Extract`, a definition id `item medkit` reached through a crafted catalogue, a 65-character build revision and an uppercase attempt id each throw `ManifestFormatException` with the expected code, and nothing is returned;
  - `Facts_only`: by reflection, no public member of `ManifestHeader`, `ParticipantResult`, `ResultManifestV1`, `OperativeFact` or `BuiltManifest` is named like `seed`, `salt`, `xp`, `experience`, `reward`, `energy`, `insurance`, `refund` or `retention` (case-insensitive substring). And a builder run whose test harness also holds a seed value `0x5EED1234` never contains `"5EED"`, `"1592660532"` or `"seed"` in its bytes;
  - `Conservation_holds_for_every_built_manifest`: `Conservation.Check` on the parsed output of the Task 5 seeded runs, built through the builder.
- [ ] **Step 2: Run** `Tools/run-tests.sh EditMode "Blackglass.Tests.ManifestBuilderTests"`. Expected: EXIT=1.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the same command. Expected: EXIT=0.
- [ ] **Step 5: Commit.**

## Task 7: Shared ledger scenarios, validated by the backend (D3)

**Files:**
- Create: `Backend/contracts/ledger-v1/.gitattributes` (`* -text`), `README.md`, `01-owned-swap-and-use.json` … `07-ledger-mismatch.json`
- Create: `Backend/tests/Accounts.Domain.Tests/Settlement/LedgerScenarioTests.cs` (the scenarios, plus the Unity-only parser expectations of Task 2 asserted against `ResultManifest.Parse`, M2)
- Modify: `Backend/tests/Accounts.Domain.Tests/Accounts.Domain.Tests.csproj`: add `<None Include="..\..\contracts\ledger-v1\**\*" Link="contracts\ledger-v1\%(RecursiveDir)%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />`
- Test: `Tests/EditMode/Backend/LedgerScenarioVectorTests.cs`

**Scenario file (hand-written; the admission catalogue is `Backend/contracts/catalogue/golden-catalogue.json`, version `golden-catalogue.version`):**
```json
{
  "name": "loot-first-consumption",
  "attempt": { "attemptId": "0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41", "processSessionId": "0192f1a5-0000-7000-8000-000000000001",
               "buildRevision": "r1", "layoutHash": "cf2e14dece9cc2a6", "lootTableVersion": 7 },
  "snapshot": { "accountId": "JG", "inventoryVersion": 3, "catalogueVersion": "<golden version>", "operatives": [
      { "slot": 0, "operativeId": "C", "definitionId": "00000000-0000-4000-8000-000000000001", "experience": 0, "rank": 1, "choices": [],
        "bag": { "instances": [ { "itemId": "2k", "definitionId": "item.alpha-rifle" } ],
                 "stacks": [ { "definitionId": "item.gamma-kit", "quantity": 2 } ] },
        "equipment": { "weapon": "2k", "armor": null, "utility": null } } ] },
  "pool": [ { "containerIndex": 0, "line": 0, "definitionId": "item.gamma-kit", "quantity": 2 } ],
  "objectives": [ { "objectiveId": "extract", "required": true, "state": "completed" } ],
  "result": { "outcome": "success", "outcomeSequence": 10, "serverElapsedMs": 60000, "dead": [] },
  "events": [ { "op": "collect", "operativeId": "C", "containerIndex": 0, "line": 0, "quantity": 2 },
              { "op": "use", "operativeId": "C", "definitionId": "item.gamma-kit" },
              { "op": "use", "operativeId": "C", "definitionId": "item.gamma-kit" },
              { "op": "use", "operativeId": "C", "definitionId": "item.gamma-kit" } ],
  "expectedManifest": { "formatVersion": 1, "...": "the full manifest-v1 object, written by hand; lootHash = LootHash.Compute(7, pool)" },
  "expectedErrors": []
}
```
Each scenario is checked twice, independently:
- **Unity (`LedgerScenarioVectorTests`):** `LedgerSimulation` loads the snapshot (definitions from the golden catalogue), fills the containers from `pool` (one entry per line, as the fill leaves them), replays `events` with `AssertConsistent` after each, freezes, builds. The result must equal `ManifestCodec.ToCanonicalBytes(Parse(expectedManifest))` byte for byte. The test also asserts `LootPoolHash.Compute(7, pool)` equals `expectedManifest.lootHash`.
- **Backend (`LedgerScenarioTests`, xUnit):**
  - `ResultManifest.Parse(expectedManifest)` is `Parsed`, and its canonical bytes are a fixed point;
  - `LootHash.Compute(7, pool)` equals its `lootHash`;
  - `ManifestValidator.Validate(manifest, hash, context)` returns exactly `expectedErrors`. The context is built from the scenario: `LoadoutSnapshot.Parse(snapshot)`, the pool, the objectives with `required`, no settled collections, `GameCatalogue` from the golden catalogue, no recorded hash, `SettlementLimits.Default`, and attempt facts with `AttemptStartedAt = ParticipantStartedAt = Now − 2 min`.

**Scenarios** (golden catalogue items: `item.alpha-rifle` weapon, `item.beta-vest` armor, `item.delta-boots` utility, `item.gamma-kit` stackable consumable with max 3; bag capacity 6):

| # | Covers | expectedErrors |
|---|---|---|
| 01 owned-swap-and-use | equipment changes among owned items; an owned kit used | `[]` |
| 02 loot-first-consumption | a merged stack (owned 2 + loot 2), 3 used: loot first | `[]` |
| 03 partial-collect | a capacity-limited partial collect, then the rest after a use | `[]` |
| 04 loot-equipment | a collected rifle equipped; the owned rifle stays held | `[]` |
| 05 two-operatives-defeat | two operatives on one line, one dead, defeat; consumption from both | `[]` |
| 06 oldest-first | two kit lines collected at different times; the older line is used first | `[]` |
| 07 ledger-mismatch | a rogue bag change without an event; the manifest is still built | `["V14.integrity"]` |

**Event vocabulary** (defined in the README): `collect {operativeId, containerIndex, line, quantity}`; `use {operativeId, definitionId}`; `equip {operativeId, item: {itemId} | {containerIndex, line}}`; `rogueAdd {operativeId, definitionId, quantity}` (a bag change with no ledger event, scenario 07). Optional top-level `bagCapacity` overrides the golden catalogue's 6 (scenario 03). Containers are filled one entry per pool line, in line order, with `ItemInventory(0).Add`; since `Add` merges same-definition stackables, **two lines of one stackable are placed in different containers** (scenario 06), or the first line is at `maxStack`. The Unity side maps each golden item to `ItemDefinition.Create(id, id, category, slot, maxStack, heal)` with the golden catalogue's category, slot, `maxStack` and heal amount. `AssertConsistent` runs after every event except a `rogueAdd`, whose mismatch is expected at the freeze. **Ids of mixed length** (`"z"` and `"21"`) appear in scenario 04's held items, so the backend confirms numeric ordering (M3).

The README states:
- the format;
- that each expected manifest is hand-written from the events (the authority, like the manifest-v1 vectors);
- that a change to a scenario is a contract change needing the owner.

- [ ] **Step 1: Write** the seven scenarios by hand, working each expected manifest out from its events on paper; note the arithmetic in the README per scenario. Then write both test classes.
- [ ] **Step 2: Run** `dotnet test Backend/tests/Accounts.Domain.Tests --filter "FullyQualifiedName~LedgerScenario"`. Expected: PASS (it needs only the backend and the hand-written files). A failure here is a scenario-authoring error, fixed in the scenario before any Unity work.
- [ ] **Step 3: Run** `Tools/run-tests.sh EditMode "Blackglass.Tests.LedgerScenarioVectorTests"`. Expected: EXIT=0 if Tasks 4–6 are right. A mismatch is investigated against the hand-written manifest, never by copying Unity's output into the scenario.
- [ ] **Step 4: Run** the backend domain suite (`dotnet test Backend/tests/Accounts.Domain.Tests`). Expected: all pass, with the count before plus the new tests.
- [ ] **Step 5: Commit** (the contracts folder, the backend test, the csproj line, the Unity test).

## Task 8: `OutboxWriter`, durable file systems and `ResultPublication`

**Files:**
- Create: `Outbox/IDurableFileSystem.cs`, `Outbox/PortableDurableFileSystem.cs`, `Outbox/PosixDurableFileSystem.cs`, `Outbox/OutboxWriter.cs`, `Outbox/ResultPublication.cs`
- Create (D8): `Backend/tests/UnityCore.Linux.Tests/UnityCore.Linux.Tests.csproj` and `PosixOutboxTests.cs`; the project's line in `Backend/Backend.sln`; the `--unity-core` mode in `Backend/tools/vm-test.sh`
- Test: `Tests/EditMode/Backend/FaultingFileSystem.cs` (helper), `OutboxWriterTests.cs`, `ResultPublicationTests.cs`

**Interfaces (produced):**
```csharp
public interface IDurableFileSystem
{
    bool DirectoryExists(string path);
    bool FileExists(string path);
    byte[] ReadAllBytes(string path);
    void WriteDurable(string path, byte[] bytes);   // create or truncate, mode 0644, write everything, fsync the file, close
    void Rename(string from, string to);            // one atomic rename(2); the caller has checked that `to` is absent
    void SyncDirectory(string path);                // fsync the directory, so a rename inside it is durable
}
public static class DurableFileSystems
{
    public static IDurableFileSystem ForServer();   // Posix on Linux x86_64; anything else → PlatformNotSupportedException (fail closed, M5)
    public static IDurableFileSystem ForEditor();   // Portable: tests and Editor tools only, never a real outbox
}

public enum OutboxState { None, TemporaryOnly, Written, Ready }
public enum WriteResult { Written, AlreadyWritten, Conflict }
public enum PublishResult { Published, AlreadyPublished, NothingWritten }
public sealed class OutboxWriter
{
    public const string TmpSuffix = ".manifest.tmp", WrittenSuffix = ".manifest.written", ReadySuffix = ".manifest.ready";
    public OutboxWriter(string attemptDirectory, IDurableFileSystem fileSystem);   // the directory must already exist (systemd creates it)
    public OutboxState StateOf(string accountId);
    public WriteResult WriteDurable(string accountId, byte[] canonicalBytes);      // §H steps 1–2
    public PublishResult Publish(string accountId);                                // §H step 4
}

public enum RecordedNotice { Accepted, Rejected, Unauthorized, Unavailable, TimedOut }   // any value continues to publication
public enum PublicationStage { AwaitingNotice, Published, Conflict }
/// One participant's §H publication as a state machine driven by the main thread (I1): nothing in it blocks or waits.
/// Stage 13's UnityWebRequest adapter sends `recorded` after Begin and calls OnNotice with the answer; the server's
/// tick calls Tick, which publishes once the deadline (provisional 30 s, configuration) has passed with no answer.
public sealed class ResultPublication
{
    public static ResultPublication Begin(OutboxWriter outbox, string accountId, BuiltManifest manifest, DateTime nowUtc, TimeSpan noticeDeadline);
        // §H 1–2: WriteDurable. Written or AlreadyWritten → AwaitingNotice; Conflict → Conflict (no notice, no publish).
        // An IOException propagates: nothing was published and no notice may be sent.
    public PublicationStage Stage { get; }
    public string ManifestHash { get; }        // what the adapter sends to `recorded`
    public RecordedNotice? Notice { get; }
    public PublicationStage OnNotice(RecordedNotice notice);   // §H 3 ended by an answer → §H 4 Publish; idempotent once Published
    public PublicationStage Tick(DateTime nowUtc);             // past the deadline with no notice → TimedOut → Publish
}
```

**`WriteDurable(account, bytes)`, exactly:**
1. `account` must be canonical Base58, `bytes` 1..65,536 long, and the directory must exist. Otherwise `ArgumentException` or `DirectoryNotFoundException`, and nothing is written.
2. If `.ready` exists: identical bytes → `fs.SyncDirectory(dir)` then `AlreadyWritten`; different → `Conflict`. Else if `.written` exists: the same comparison and the same sync. **A durable result is never replaced.** The sync on the `AlreadyWritten` path covers a retry after a failed directory fsync (M4).
3. `fs.WriteDurable(dir/<account>.manifest.tmp, bytes)`. This creates or truncates: a `.tmp` left by an earlier failed write in this process is rewritten, never promoted.
4. `fs.Rename(tmp, written)`, then `fs.SyncDirectory(dir)`. From here the result is complete and durable. `→ Written`.

**`Publish(account)`:**
- `.ready` exists → `fs.SyncDirectory(dir)`, then `AlreadyPublished` (M4);
- `.written` absent → `NothingWritten`;
- otherwise `fs.Rename(written, ready)`, then `fs.SyncDirectory(dir)` → `Published`.

Nothing ever deletes `.written`, `.ready` or `.tmp`. A completed `.written` stays after the process exits, for the agent's read-only recovery (A10-D1).

**`PosixDurableFileSystem`** (Linux x86_64 only; `DllImport("libc.so.6", SetLastError = true)`: the explicit soname resolves under both Mono and CoreCLR without relying on Mono's dllmap, I2):
- **Calls:** `open`, `write`, `fsync`, `close`, `rename`.
- **Flags:** `O_WRONLY=0x1 | O_CREAT=0x40 | O_TRUNC=0x200 | O_NOFOLLOW=0x20000 | O_CLOEXEC=0x80000`, mode `0644` (420). The agent, another uid without capabilities, reads through the unit's `UMask=0022` and the `0755` state directory.
- **Directory sync:** `O_RDONLY=0 | O_DIRECTORY=0x10000 | O_CLOEXEC`, then `fsync`.
- **Writes:** `write(int fd, byte[] buffer, IntPtr count)`; a partial write copies the unwritten tail into a new array (`Buffer.BlockCopy`) and writes again, so no unsafe code or pointer arithmetic is needed (Core keeps `allowUnsafeCode: false`). `EINTR` (4) is retried. Every failure is an `IOException` with the errno from `Marshal.GetLastWin32Error()`.
- The flag values match `Backend/src/MissionHost.Agent/Unix.cs`. **Why libc:** Mono's `FileStream.Flush(true)` and `File.Move` don't document an fsync or a single `rename(2)`. A link-and-unlink move would leave a doubly linked file, which the agent refuses (`Unix.ReadUntrusted`). There is no managed directory fsync.

**`PortableDurableFileSystem`** (the Editor on Windows, and any non-Linux system):
- `FileStream(FileMode.Create)`, `Write`, `Flush(true)`;
- `File.Move` for the rename;
- `SyncDirectory` is a no-op. Durability across power loss is a Linux concern, verified in Stage 12 on the VM.

**`FaultingFileSystem`** (test helper) wraps the portable one. It records every call in order (`WriteDurable(tmp)`, `Rename(tmp→written)`, `SyncDirectory`, …) and can "crash" by throwing a `SimulatedCrash` exception:
- before or after any numbered call;
- in the middle of `WriteDurable`, after half the bytes, leaving a partial `.tmp`.

A helper `AgentView.Classify(dir, account)` mirrors the agent's rules: `.tmp` is never a result; `.ready` is relayable now; `.written` is relayable only after a verified exit. It also checks that every manifest file parses with `ManifestCodec.Parse`, identifies the attempt and account, and is a regular file.

- [ ] **Step 1: Write the failing tests:**
  - **The call order:** `WriteDurable_calls_in_order` gives exactly `[WriteDurable(tmp), Rename(tmp, written), SyncDirectory(dir)]`; `Publish_calls_in_order` gives `[Rename(written, ready), SyncDirectory(dir)]`.
  - **Crash boundaries** (one test per crash point; each asserts `AgentView` and the bytes):
    - in the middle of the tmp write: a partial `.tmp`, no `.written`, absent to the agent;
    - after the tmp write, before the rename: a complete `.tmp`, still absent (a `.tmp` is never a result);
    - after the rename, before the directory sync: `.written` complete (on the portable system the rename is already visible; the test documents that on Linux the directory fsync makes it durable);
    - after the written sync, before the notice: `.written` complete, recoverable after exit, `present` once the exit is verified;
    - after the notice, before publication: the same;
    - after the publish rename: `.ready`, relayable now.
  - **Idempotence and refusals:**
    - `A_second_write_of_identical_bytes_is_AlreadyWritten`;
    - `A_second_write_of_different_bytes_is_Conflict_and_changes_nothing` (Review Focus 3);
    - `A_leftover_tmp_is_rewritten_never_promoted`;
    - `Publish_without_written_is_NothingWritten`; `Publish_twice_is_AlreadyPublished`;
    - `A_missing_directory_is_refused_and_nothing_is_created`;
    - `A_bad_account_name_or_empty_or_oversized_bytes_is_refused`;
    - `The_written_file_survives_writer_disposal` (no `Publish`, then a new `OutboxWriter` reads `Written`);
    - `File_names_match_the_agents_pattern`: `^[1-9A-HJ-NP-Za-km-z]{1,11}\.manifest\.(tmp|written|ready)$`, copied from `Outbox.cs`.
  - **Retry after a failed directory sync (M4):** the first `SyncDirectory` fails, the retry returns `AlreadyWritten`, and the recorded calls show a second `SyncDirectory`; the same for `AlreadyPublished`.
  - **Publication (`ResultPublicationTests`, a fake clock):**
    - `Begin_writes_durably_and_awaits_the_notice` (`.written` exists, no `.ready`, `ManifestHash` set);
    - `Every_notice_value_publishes`;
    - `No_answer_publishes_at_the_deadline`: `Tick` before 30 s changes nothing; after it, `TimedOut` and `.ready`;
    - `A_late_notice_after_publication_is_ignored`;
    - `A_Conflict_publishes_nothing`;
    - `A_failed_durable_write_propagates_and_creates_no_publication`;
    - `No_backend_is_needed_to_preserve_a_result`: with no notice at all, `.written` holds the exact bytes from `Begin` on, and `.ready` after the deadline;
    - `Nothing_blocks`: every call returns within 50 ms with the clock frozen.
  - **Platform selection:** on Windows, `ForServer()` throws `PlatformNotSupportedException` and `ForEditor()` returns the portable type (M5).
  - **Linux (D8, `Backend/tests/UnityCore.Linux.Tests`, xUnit on .NET 10):** the project compiles `Assets/_Project/Scripts/Backend/Core/Outbox/*.cs` and `Ids/*.cs` by link and references `MissionHost.Agent`. `PosixOutboxTests` write through `PosixDurableFileSystem` + `OutboxWriter` into a temporary directory, then read back with the agent's own `Unix.ReadUntrusted` and `OutboxScanner.Scan`: a regular file, one link, mode 0644 under umask 0022, the exact bytes, `.tmp` ignored, `.written` relayable only with `includeWritten`, `.ready` relayable at once. Also: a rename leaves the inode count at 1 (`fstat`), and the directory fsync succeeds. Run: `Backend/tools/vm-test.sh --unity-core` (the disposable .NET SDK container on the services VM; it touches no VM configuration). Mono coverage is a Stage 12 obligation: a server-player `-outbox-selftest` run on the mission VM with the owner's go-ahead.
- [ ] **Step 2: Run** `Tools/run-tests.sh EditMode "Blackglass.Tests.OutboxWriterTests;Blackglass.Tests.ResultPublicationTests"`. Expected: EXIT=1. Run `Backend/tools/vm-test.sh --unity-core`. Expected: a build failure (types missing).
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the same commands. Expected: EXIT=0, and the Linux tests pass.
- [ ] **Step 5: Commit.**

## Task 9: `INetAdmission`, the final audit, docs and the stop

**Files:**
- Create: `Net/INetAdmission.cs`
- Test: `Tests/EditMode/Backend/NetAdmissionContractTests.cs`
- Modify: this plan ("As built"), `Docs/superpowers/plans/2026-10-14-phase-13-backend.md` (Stage 11 status), `CLAUDE.md`

**Interfaces (produced, D5):**
```csharp
public enum AdmissionStatus { Pending, Admitted, Refused }
/// Mapped onto the existing wire byte NetRefusal by the networking layer: UnknownToken, WrongAttempt and AlreadyConnected
/// one to one; Timeout and Unavailable (ticket redemption only) also send UnknownToken, so the wire stays unchanged in Stage 11.
public enum AdmissionRefusal { None, UnknownToken, WrongAttempt, AlreadyConnected, Timeout, Unavailable }
public sealed class AdmissionDecision
{
    public static AdmissionDecision Pending();
    // Shape only (I4): the account is an opaque non-empty string (NetTicketAdmission enforces Base58; the harness keeps its
    // free-form token-file accounts); ownership is a distinct list of opaque owned-unit keys, 0..8 (backend operative ids
    // for tickets, "1".."n" unit ids for the harness: spec §14.6's explicit set); capabilities are opaque words the adapter
    // interprets (the harness's "fixture").
    public static AdmissionDecision Admit(string account, IReadOnlyList<string> ownedUnitKeys, IReadOnlyList<string> capabilities);
    public static AdmissionDecision Refuse(AdmissionRefusal reason);                               // reason != None
    public AdmissionStatus Status { get; } public AdmissionRefusal Refusal { get; }
    public string Account { get; } public IReadOnlyList<string> OwnedUnitKeys { get; } public IReadOnlyList<string> Capabilities { get; }
}
/// The server's admission boundary (spec §14.6). Implementations, both in Stage 14: the token-file NetAdmission (harness,
/// adapted) and NetTicketAdmission (redeems a ticket with the backend without blocking the main thread). The networking
/// layer calls Poll each tick until it is no longer Pending, then maps the owned-unit keys to unit ids itself.
/// Library-free: no connection objects, no transport types, no tasks.
public interface INetAdmission
{
    string AttemptId { get; }
    int Admitted { get; }
    AdmissionDecision Begin(int clientId, string token);   // may return Pending
    AdmissionDecision Poll(int clientId);                  // the same decision once final; Refused(UnknownToken) for an unknown client
    void Release(int clientId);                            // a disconnect; unknown ids are ignored
}
```

- [ ] **Step 1: Write the failing tests:**
  - `Admit_requires_a_non_empty_account_and_distinct_keys` (at most 8 keys; empty strings refused);
  - `The_harness_shape_fits`: `Admit("owner", ["1","2","3"], ["fixture"])` (a token-file entry with `+fixture`) and `Admit("spectator", [], [])` are valid, so Stage 14 can adapt `NetAdmission` without changing it;
  - `Every_refusal_maps_to_a_wire_byte`: a table test of the mapping documented on `AdmissionRefusal` against `NetRefusal`'s values;
  - `Refuse_requires_a_reason`;
  - `Decisions_are_immutable` (lists copied);
  - `The_interface_mentions_no_connection_or_transport_type`: by reflection, every parameter and return type of `INetAdmission` is in the `Blackglass.Backend.Core` namespace or is `string`, `int` or `void`.
- [ ] **Step 2: Run** `Tools/run-tests.sh EditMode "Blackglass.Tests.NetAdmissionContractTests"`. Expected: EXIT=1. Then implement it, and run again. Expected: EXIT=0. Commit.
- [ ] **Step 3: Run the full suites:** `Tools/run-tests.sh EditMode`, `Tools/run-tests.sh PlayMode`, `dotnet test Backend/tests/Accounts.Domain.Tests` and `Backend/tools/vm-test.sh --unity-core` (D8). Expected:
  - EditMode: 1579 + this stage's tests, 0 failed;
  - PlayMode: exactly the baseline total, 0 failed;
  - backend domain: the previous total + `LedgerScenarioTests`, 0 failed.

  Save the outputs to `.claude/scratchpad/phase13/stage-11/`.
- [ ] **Step 4: The scope check:**
  - `git status --short` shows only this stage's paths plus the owner's two Unity files and `.claude/`;
  - `git diff --stat main...HEAD` lists only the paths in Global Constraints;
  - `git diff main...HEAD -- Backend/contracts/manifest-v1 Assets/_Project/Scripts/Blackglass.asmdef Assets/_Project/Scripts/Net Assets/_Project/Scripts/Items` is empty.
- [ ] **Step 5: The final review:** one independent whole-branch review on the most capable model, against this plan and the spec, with Review Focus verbatim. Fix Critical and Important findings test-first. Record the minors.
- [ ] **Step 6: Docs as built:** counts, rulings and deferred minors in this plan; the Stage 11 status in the staged plan and `CLAUDE.md`. Commit by explicit paths. **Stop for the owner's review:** no merge or push without approval.

---

## Acceptance criteria

- Every `valid/` and `invalid/` manifest-v1 vector passes in EditMode byte for byte (canonical bytes, hash, refusal code). The vector folder is unchanged and pinned by hashes.
- `LootPoolHash` equals the backend's pinned value.
- The receipt vectors read correctly; retention vectors are untouched and unused by Unity (D4).
- The ledger:
  - stays consistent with Unity's real `ItemInventory` after every mutation in 200 seeded runs and every named case;
  - conserves (V10), uses loot first and oldest first;
  - keeps non-stackable identities through collect and equip, and records equipment changes;
  - detects a rogue mutation as `ledgerMismatch`.
- The seven ledger-v1 scenarios produce byte-identical manifests in Unity and validate in the backend's `ManifestValidator` with exactly the expected errors: none, or `V14.integrity` for the mismatch scenario.
- The builder is deterministic, facts-only and fail-closed; it never sees a seed or salt.
- The outbox writer follows `.tmp` → `.written` → `.ready` with a file fsync, an atomic rename and a directory fsync, verified on Linux against the agent's own reader (D8). Publication never blocks the main thread. Every crash boundary leaves a state the agent classifies correctly. A durable result is never replaced or deleted, and preserving one needs no backend.
- `INetAdmission` exists, library-free. `NetAdmission` and all networking code are unchanged.
- The Core assembly references nothing, and the source audit passes.
- EditMode = baseline + new tests, PlayMode = baseline, backend domain tests green. Only the listed paths changed.

## Out of scope (later stages)

- Wiring the ledger to `UnitItems` events, snapshot mode and the server initializer (Stage 12).
- The `UnityWebRequest` client, the bounded `recorded` notifier, start, heartbeat and register (Stage 13).
- `NetTicketAdmission`, ownership lists and new requests (Stage 14).
- The inventory-operations request writer and the client receipt screen (Stage 16).
- Verifying `PosixDurableFileSystem` under Mono on the mission VM (Stage 12's `-outbox-selftest`, with the owner's go-ahead); CoreCLR on Linux is covered in Stage 11 (D8).
- Forbidding non-stackable consumables in the catalogue validators, if the owner chooses D7 (a), unless authorized into Stage 11.
- Any B24 work.

## Compatibility with Stages 12 and 13

- **Stage 12:**
  - snapshot mode: `OperativeSnapshot.FromDto` feeds the ledger and the runtime builds bag entries whose ids are the Base58 item ids;
  - the container fill calls `RegisterContainer` with the entries after `LootContainer.Initialize`, and the `ready` report's `lootPool` and `lootHash` come from `PoolLines()` and `LootPoolHash`;
  - `UnitItems`' additive detail events call `Collect`, `Use` and `Equip`;
  - the outcome freeze calls `VerifyOperative` and `VerifyContainer` for that participant, then `Freeze`, then `ManifestBuilder.Build` and `ResultPublication.Begin`. **The outbox directory is `$STATE_DIRECTORY`** (systemd `StateDirectory=blackglass-outbox/%i`; the unit makes `/var/lib/blackglass` inaccessible, I5): the process never builds the path itself and refuses to run an attempt if the variable is missing or the directory doesn't exist. The server constructs the durable file system with `DurableFileSystems.ForServer()` and refuses to start without it (M5). A Mono self-test (`-outbox-selftest`) verifies the libc path on the mission VM (D8);
  - a `ManifestFormatException` is Stage 12's to handle (D2).
- **Stage 13:** the `UnityWebRequest` adapter posts `RecordedRequest {manifestHash}` after `ResultPublication.Begin`, feeds the answer to `OnNotice`, and the server tick calls `Tick`; nothing blocks the main thread. The DTOs are the request and response bodies. The process does not poll settlement (S9-B6): the staged plan's Stage 13 line "status poll → `NetResultConfirmedMessage`" is superseded and is not inherited here.
- **Stage 14:** `NetAdmission` and `NetTicketAdmission` implement `INetAdmission`.
- **Stage 16:** client DTOs and `ReceiptV1`.

---

## Decisions for the owner

Each is a genuine architectural choice the spec leaves open. The plan is written for the recommendation; a different ruling changes only the task named.

**D1. Where Core lives: its own assembly (recommended) or a folder of the gameplay assembly.**
- *Recommended:* `Blackglass.Backend.Core` asmdef with no references and no engine references. The compiler, not only a text audit, stops UnityEngine, FishNet or `UnityWebRequest` from entering Core. It compiles in seconds and is reusable by an Editor tool or a plain .NET test project later.
- *Cost:*
  - one reference line in the EditMode test asmdef now;
  - one in `Blackglass.asmdef` in Stage 12;
  - Core can't use `UnityEngine.Debug` or `JsonUtility` itself (the DTOs are still `JsonUtility`-compatible, because `JsonUtility` runs in the caller).
- *Alternative:* the spec's literal folder `Scripts/Backend/Core` inside `Blackglass`, with a source audit as `Net/Core` has today. Simpler, but the audit is the only guard, and Core would compile against FishNet.

**D2. A strict manifest reader in Core, used as a fail-closed self-check (recommended), or a writer only.**
- *Recommended:* port the backend's parser to Unity.
  - It is the only way to run every `invalid/` vector with its exact code, as S9-B4 requires.
  - It gives the builder a self-check: its own bytes must parse, re-canonicalize identically and name the right attempt and account before anything reaches the outbox.
  - About 400 lines of ported code.
- *Consequence for Stage 12:* on a self-check failure (a server bug, never expected), the builder throws, and Stage 12 must choose.
  - **(a)** Write nothing and log an alert. The agent's positive exit evidence then voids the participant and refunds energy, and owned items are untouched. This is my recommendation for Stage 12.
  - **(b)** Write the raw bytes anyway, so the backend stores them as malformed for operator review.
  - **(c)** Write a minimal valid manifest instead (the header, the participant's operatives with empty lists, `integrity: ledgerMismatch`). It is quarantined under V14 for an operator, with no automatic refund.
  - *Trade-off (M11):* (a) is an infrastructure void, so a reproducible builder failure in a lost mission would turn a defeat into a full refund; (c) closes that incentive at the cost of a little more code. **Revised recommendation: (c)**, with (a) only if (c)'s own self-check fails.
- *Alternative:* a writer only, testing the invalid vectors indirectly at the typed-model level. Less code, but the syntax-level codes (`NotJson`, `DuplicateKey`, `NotInteger`, …) would be untestable in Unity, which falls short of "reproduce every vector".

**D3. Proving backend acceptance: shared ledger scenarios run by both runtimes (recommended), or a Unity-side mirror of the validator.**
- *Recommended:* `Backend/contracts/ledger-v1/` with hand-written expected manifests. Unity must produce them byte for byte from events, and the backend's own `ManifestValidator` must accept them.
  - This meets the staged plan's evidence item ("one manifest accepted byte for byte by the stage 9 validator fixture") with the real validator, independently.
  - *Cost:* a new contract folder plus one backend test file and one csproj line outside `Assets/`.
- *Alternative:* a test-side Unity copy of V8–V12 only. No backend change, but it is a second implementation of the validator that could drift.

**D4. Receipt and retention vectors.**
- *Recommended:*
  - read the `receipt/` vectors now with a small `ReceiptV1` reader on the strict JSON reader (`JsonUtility` can't hold `alive: null` or the two insurance shapes), so Stage 16 inherits a tested reader;
  - do **not** consume the `retention/` vectors in Unity at all. Retention is computed by the backend with a secret salt the mission process must never see, and a test asserts no Core type has a salt member.
- *Alternative:* defer the receipt reader to Stage 16 (Stage 11 shrinks by one class and one test file).

**D5. The `INetAdmission` shape: poll-based, shape-only, in Core (recommended; revised after review I4).**
- *Recommended:*
  - `Begin` / `Poll` / `Release` with immutable decisions. Ticket redemption is asynchronous (an HTTP call), and FishNet's callbacks run on the main thread, so polling keeps tasks and threads out of Core.
  - The decision carries an opaque account string, a list of opaque owned-unit keys and opaque capability words, so both spec §14.6 implementations fit: the harness `NetAdmission` (free-form accounts, unit ids 1..n, `+fixture`) and `NetTicketAdmission` (Base58 account, backend operative ids). Each implementation enforces its own id forms.
  - New refusal reasons map onto the existing `NetRefusal` byte, so the wire doesn't change.
  - The existing `NetAdmission` keeps its synchronous API until Stage 14 adapts it.
- *Alternative:* a callback or `Task`-based interface, or putting the interface in `Net/Core`. The spec's §2.3 places it in Backend Core.

**D6. Durability through five libc calls in Core (recommended).**
- *Recommended:* `PosixDurableFileSystem` with `DllImport("libc")` for `open`/`write`/`fsync`/`close`/`rename` on the Linux server (Mono), plus a portable fallback for the Editor.
  - Mono documents neither an fsync behind `FileStream.Flush(true)` nor a single `rename(2)` behind `File.Move`, and offers no directory fsync.
  - A link-and-unlink move would leave a doubly linked file that Stage 10's agent refuses.
  - The library is named `libc.so.6` explicitly, and writes need no unsafe code.
  - Verification is D8.
- *Alternative:* managed calls only (`FileStream.Flush(true)`, `File.Move`), accepting unproven durability and the hard-link risk.

**D7. Consumables that don't stack (review I3).** Both catalogue validators accept a consumable with `maxStack` 1 (`ItemDefinition.cs:364-369`, `CatalogueValidator.cs:54-67`) and `UnitItems` lets it be used, but format v1 can't report its use (V8 requires a stackable consumable; V10 requires every snapshot instance to be held). Every legitimate use would be a `ledgerMismatch` and a quarantine. It is latent: today's only consumable, the medkit, stacks to 3.
- *Recommended (a):* forbid it in both validators (Unity `ItemDefinition.IsValid` and the backend `CatalogueValidator`, a test each). It is a small change outside Stage 11's paths, so either authorize it into Stage 11 (two source files and two test files) or schedule it before Stage 12.
- *Alternative (b):* accept the limitation; the ledger refuses such a use with `use_not_stackable`, and a test pins it.

**D8. Verifying the Linux durable write path in Stage 11 (review I2).** No Linux Unity Editor exists in this workflow, so an EditMode test can't run the libc path, and the mission VM runs only the server player.
- *Recommended:* because Core has no engine references (D1), compile its `Outbox` and `Ids` sources by link into a new .NET 10 xUnit project, `Backend/tests/UnityCore.Linux.Tests`, that references `MissionHost.Agent`. Run it through `Backend/tools/vm-test.sh` with a new `--unity-core` mode that also snapshots Core's sources (the existing disposable SDK container on the services VM; no VM configuration change). Its tests read the files back with the agent's own `Unix.ReadUntrusted` and `OutboxScanner`, so the writer is proven against the real reader on Linux.
  - *Cost:* a new test project, its solution entry and one mode in `vm-test.sh`, all outside `Assets/`.
  - This covers CoreCLR. Mono-specific behaviour (the `DllImport` resolution) remains a Stage 12 obligation: a server-player `-outbox-selftest` on the mission VM, with your go-ahead.
- *Alternative:* no Linux check in Stage 11; Stage 12's mission-VM self-test is the first and only proof.

---

## Plan review (2026-10-19, independent reviewer, revision 1)

0 Critical, 5 Important, 14 Minor. Full report: `.claude/scratchpad/phase13/stage-11/plan-review.md` (local). Every claim was checked against the code at `6bdc6ca`. The reviewer confirmed:
- the member names, bounds, codes and refusal order against `ResultManifest.cs`;
- that every vector is reproducible;
- that the ledger's facts satisfy V8–V12 for every sequence real Unity code can produce (except I3);
- the Base58-entry-id approach;
- the file mode and names against the agent.

How revision 1.1 answers each finding:

| Finding | Change |
|---|---|
| I1 a blocking publisher | `ResultPublisher` replaced by the step-wise `ResultPublication` (Begin / OnNotice / Tick), driven by the main thread |
| I2 no runnable Linux test | D8: a .NET 10 Linux test project on the VM runner, reading back through the agent's code; `libc.so.6`; a write loop without unsafe code; a Mono self-test obligation for Stage 12 |
| I3 non-stackable consumables | D7 added |
| I4 `INetAdmission` didn't fit the harness | shape-only decision (opaque account, owned-unit keys, capabilities); refusal-to-wire mapping; D5 revised |
| I5 the wrong outbox path | Stage 12 uses `$STATE_DIRECTORY`; fails closed without it |
| M1 refusal order | lone surrogates flagged and refused where the backend decodes; entry member order listed and tested |
| M2 unpinned syntax codes | asserted in the backend test too; the 64-deep boundary added |
| M3 numeric id order | codec test with `"z"`/`"21"`; mixed-length ids in a ledger-v1 scenario |
| M4 sync on retry | `AlreadyWritten` and `AlreadyPublished` sync the directory; tested |
| M5 platform selection | `ForServer()` fails closed off Linux x86_64; `ForEditor()` for tests |
| M6 simulation calls | the ledger is told only what Unity applied; `UnitItems.Check` mirrored |
| M7 scenario gaps | event vocabulary, capacity override, catalogue mapping, container placement and rogue handling specified |
| M8 `scrap` | used only in the refusal test |
| M9 the conservation mirror | declared an inner check; ledger-v1 is the authority |
| M10 the freeze | `Freeze` takes the runtime state and verifies, fail-closed; `unknown_operative` marks every participant |
| M11 D2 fallback | option (c) added and recommended |
| M12 inconsistencies | fixed (no `Compare`; five libc calls; get-only model; raw integer nodes; no double count; codes; receipt shapes; slot sorting by word) |
| M13 vector file set | the folder's file set is pinned too |
| M14 audit false positives | the audit matches names, not substrings |
