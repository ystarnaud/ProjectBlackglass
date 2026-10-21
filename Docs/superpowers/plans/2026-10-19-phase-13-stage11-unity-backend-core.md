# Phase 13 Stage 11: Unity backend core (library-free), implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task by task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Status (2026-10-20):** **accepted by the owner, merged into `main` and pushed** after the final validation ("Final validation" at the end) ("Final owner rulings" at the end): I1 is resolved by assignment to Stage 12 as a hard acceptance gate (B25), **not implemented in the current runtime**; the M1 parser hardening is done. Implemented on `phase13-stage11` ("As built": all suites green; final review 0 Critical, 1 Important (I1), 7 Minor). Plan revision 2.1 (revision 2 plus its review). The owner approved the architecture and ruled on D1–D8 ("Owner rulings" near the end). This revision applies the rulings' corrections: D2 (format versus settlement validation, the truthful `ledgerMismatch` fallback, fail-closed builds), D6 (every file operation on a dedicated I/O worker, the checked `$STATE_DIRECTORY`, a no-replace rename, the libc set the requirements need) and D7 (consumables must stack for new registrations and exports, with stored catalogues still readable). It also corrects the staged plan's Stage 13 settlement poll. Implementation is authorized on `phase13-stage11` once an independent review of this revision leaves no Critical or Important finding. No merge, push or deployment without the owner. Revision 1.1 (`2777623`) applied the first plan review (0 Critical, 5 Important, 14 Minor).

**Goal:** a plain C# core inside the Unity project that later stages (12, 13, 14, 16) build on:
- the backend DTOs;
- the exact manifest format v1 (canonical bytes and hashes equal to the .NET backend's, every golden vector reproduced);
- the mission's item-provenance ledger;
- the manifest builder;
- the crash-safe outbox writer;
- the library-free ticket-admission boundary.

Nothing here runs in a scene yet. No gameplay, networking, scene, prefab, HUD, input or project-settings file changes.

**Architecture:**
- New assembly `Blackglass.Backend.Core` in `Assets/_Project/Scripts/Backend/Core/` (D1). It has no references and `noEngineReferences: true`, so the compiler itself keeps out UnityEngine, FishNet and every other library. Only the base class library is used: `System.IO`, `System.Security.Cryptography.SHA256`, `System.Text`, `System.Threading` for the outbox's I/O worker, and `System.Runtime.InteropServices` for the durable writer's libc calls (D6).
- Data flows one way:

  ```
  snapshot (LoadoutSnapshotDto) ─┐
  container fill (pool lines) ───┼─► MissionItemLedger ─► ParticipantFacts ─► ManifestBuilder ─► BuiltManifest (canonical bytes + hash)
  collect / use / equip events ──┘        ▲ verify against the runtime bags                             │
                                                                                                         ▼
                          main thread: OutboxWorker.Submit(bytes, hash) → PublicationHandle (status polled, never waited on)
                          I/O worker:  .tmp + fsync → rename → .written + directory fsync ... AllowPublication (Stage 13) ... → .ready + directory fsync
  ```
- The ledger never reads Unity objects. Stage 12 feeds it events and plain snapshots of `ItemInventory` contents.
- The manifest model and its strict reader are a faithful port of `Backend/src/Accounts.Domain/Settlement/ResultManifest.cs` and `CanonicalJson.cs`. The vectors in `Backend/contracts/manifest-v1/` are the authority. Nothing in that folder changes.

**Tech stack:**
- Unity 6000.3.25f1, API compatibility .NET Standard 2.1, C# as the project compiles it (no records or `init`).
- NUnit EditMode tests in the existing `Blackglass.Tests.EditMode` assembly, run by `Tools/run-tests.sh`.
- The Linux server player is Mono (`ServerBuild` enforces `Mono2x`).
- Backend tests (xUnit, .NET 10): `LedgerScenarioTests` and `CatalogueRegistrationRulesTests` in `Accounts.Domain.Tests` (no database, `dotnet test`); two `CatalogueHistoryTests` and one `LedgerFallbackQuarantineTests` file in `Accounts.Integration.Tests` (the VM runner, `vm-test.sh --filter`); the new `UnityCore.Linux.Tests` project (`vm-test.sh --unity-core`).

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

The evidence is in `.claude/scratchpad/phase13/stage-11/baseline/` (local, not committed). Every Unity test this stage adds is an EditMode test, so the PlayMode total must stay at its baseline, and the EditMode total must grow only by this stage's new tests. If `main` moves before implementation starts, both baselines are measured again first.

---

## Global Constraints

- **Touch only** these paths:
  - `Assets/_Project/Scripts/Backend/Core/**` (new, with `.meta` files);
  - `Assets/_Project/Tests/EditMode/Backend/**` (new, with `.meta` files);
  - `Assets/_Project/Tests/EditMode/Blackglass.Tests.EditMode.asmdef` (one added reference, D1);
  - `Backend/contracts/ledger-v1/**` (new, D3);
  - `Backend/tests/Accounts.Domain.Tests/Settlement/LedgerScenarioTests.cs` (new, D3);
  - `Backend/tests/Accounts.Domain.Tests/Accounts.Domain.Tests.csproj` (one copy line, D3);
  - `Backend/contracts/ledger-v1/**` also holds the fallback scenario (D2); `Backend/tests/Accounts.Integration.Tests/Database/LedgerFallbackQuarantineTests.cs` (new, D2);
  - `Backend/tests/UnityCore.Linux.Tests/**` (new, D8; not in `Backend.sln`, because it links files outside `Backend/`), and the `unity-core` mode in `Backend/tools/vm-test.sh` and `Backend/tools/run-tests-in-container.sh` (D8);
  - D7: `Backend/src/Accounts.Domain/Catalogue/CatalogueRegistrationRules.cs` (new), `Backend/src/Accounts.Persistence/Catalogue/CatalogueHistoryStore.cs` (`RegisterAsync` only), `Backend/tests/Accounts.Domain.Tests/Catalogue/CatalogueRegistrationRulesTests.cs` (new), `Backend/tests/Accounts.Integration.Tests/Database/CatalogueHistoryTests.cs` (two tests), `Assets/_Project/Editor/Backend/CatalogueBuilder.cs` (one rule), `Assets/_Project/Tests/EditMode/CatalogueBuilderTests.cs` (one test);
  - this plan, the staged plan's Stage 11 status and Stage 13 lifecycle line, and `CLAUDE.md`'s status lines.
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
  - the process never deletes a `.written` or `.ready` file;
  - production publication uses only the systemd-provided `$STATE_DIRECTORY`, checked, and every file operation is relative to that one directory;
  - no file write or `fsync` runs in the simulation frame loop: one dedicated I/O worker does them (D6).
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
3. **A crash between the rename and the directory fsync, a leftover `.tmp` beside a `.written`, a second write for the same participant, and a slow or failing `fsync` while the simulation runs.** Expected: `.written` is complete or absent, never partial. A `.tmp` is never promoted or relayed. A durable result is never replaced: identical bytes are a no-op, different bytes are refused as `Conflict`, and the rename itself refuses an existing target. The simulation's calls return at once while the worker waits on the disk. Task 8.
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
  Manifest/ManifestBuilder.cs          ParticipantFacts + header + result → BuiltManifest (format self-check); the snapshot fallback; BuildForPublication
  Manifest/LootPoolHash.cs             port of Backend LootHash.Compute
  Ledger/LedgerCatalogue.cs            definition id → stackable, consumable, slot
  Ledger/LedgerInputs.cs               OperativeSnapshot, ContainerEntry, RuntimeEntry, RuntimeEquipment, PoolLine, OperativeRuntimeState, ContainerRuntimeState
  Ledger/MissionItemLedger.cs          lots, events, verification, freeze
  Ledger/ParticipantFacts.cs           the per-operative facts a manifest reports
  Outbox/OutboxFileNames.cs            the agent's naming contract
  Outbox/OutboxIOException.cs          a bounded failure code plus errno
  Outbox/IOutboxDirectory.cs           one approved directory, addressed by outbox file names only
  Outbox/OutboxLocation.cs             the $STATE_DIRECTORY rule (exactly <parent>/<attemptId>)
  Outbox/ILibc.cs, NativeLibc.cs       the libc seam and its DllImports (libc.so.6)
  Outbox/PosixOutboxDirectory.cs       dirfd-relative openat/write/fsync/renameat2(NOREPLACE)/fsync(dir) (Linux x86_64 server)
  Outbox/PortableOutboxDirectory.cs    FileStream + Flush(true) + File.Move; directory sync a no-op (Editor and tests only)
  Outbox/OutboxDirectories.cs          OpenForServer (Posix, checked) and OpenForEditor
  Outbox/OutboxWriter.cs               the §H protocol, synchronous, used only by the worker
  Outbox/OutboxWorker.cs               the dedicated I/O thread and its job queue
  Outbox/PublicationHandle.cs          the thread-safe status the simulation polls
  Dto/ProcessDtos.cs                   /server/v1 requests and responses, LoadoutSnapshotDto
  Dto/ClientDtos.cs                    /v1 login, me, roster, inventory read, deploy, attempt, tickets
  Dto/ReceiptV1.cs                     receipt read model + reader on StrictJsonReader (D4)
  Net/INetAdmission.cs                 the admission boundary and its value types (D5)
Assets/_Project/Tests/EditMode/Backend/
  CoreSourceAuditTests.cs, Base58IdTests.cs, CanonicalIdsTests.cs, ManifestGoldenVectorTests.cs, ManifestCodecTests.cs,
  LootPoolHashTests.cs, DtoTests.cs, ReceiptVectorTests.cs, MissionItemLedgerTests.cs, LedgerSimulation.cs (test helper),
  LedgerSimulationTests.cs, ManifestBuilderTests.cs, LedgerScenarioVectorTests.cs, OutboxLocationTests.cs,
  PosixOutboxDirectoryTests.cs, OutboxWriterTests.cs, OutboxWorkerTests.cs, FaultingOutboxDirectory.cs, FakeLibc.cs, AgentView.cs
  (test helpers), NetAdmissionContractTests.cs
Backend/contracts/ledger-v1/
  .gitattributes ("* -text"), README.md, NN-name.json (scenarios with hand-written expected manifests)
Backend/tests/Accounts.Domain.Tests/Settlement/LedgerScenarioTests.cs
Backend/tests/Accounts.Integration.Tests/Database/LedgerFallbackQuarantineTests.cs   (D2)
Backend/tests/UnityCore.Linux.Tests/      (D8) xUnit, compiles Core's Outbox and Base58Id sources by link, references MissionHost.Agent
Backend/src/Accounts.Domain/Catalogue/CatalogueRegistrationRules.cs                 (D7) and its call in CatalogueHistoryStore.RegisterAsync
Assets/_Project/Editor/Backend/CatalogueBuilder.cs                                   (D7) one exporter rule
```

Unity creates a `.meta` file for every new file and folder at the first batch run; commit them with their files. Test classes use the namespace `Blackglass.Tests` like the existing ones. Core's namespace is `Blackglass.Backend.Core`.

Focused run (each task): `Tools/run-tests.sh EditMode "<fully qualified class or namespace regex>"`. Unity's `-testFilter` accepts a semicolon-separated list and regular expressions. Expected EXIT: 0 when all pass, 2 on test failures, 1 on a compile error.

---

## Task 0: record the rulings (docs only; done by this revision)

**Files:** this plan, `Docs/superpowers/plans/2026-10-14-phase-13-backend.md` (Stage 11 status, Stage 13's lifecycle line), `CLAUDE.md` (status lines).

- [x] **Step 1:** Record the owner's rulings on D1–D8 and the plan changes they require (revision 2, "Owner rulings").
- [x] **Step 2:** Correct the staged plan's Stage 13 line: no process-side settlement poll (S9-B6).
- [ ] **Step 3:** Before Task 1, re-measure both baselines if `main` moved since `6bdc6ca`; update the Baselines table.

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
        public static bool IsSha256(string text);       // ^sha256:[0-9a-f]{64}\z
        public static bool IsLayoutHash(string text);   // ^[0-9a-f]{16}\z
        public static bool IsDefinitionId(string text); // Backend CanonicalCatalogueJson.IsWellFormedId: [a-z0-9]+([.-][a-z0-9]+)*, at most 64
        public static bool IsWord(string text, int maxLength);    // Backend StrictJson.IsWord: [a-z0-9]+([._-][a-z0-9]+)*
        public static bool IsBuildRevision(string text); // Backend BuildIdentity.IsWellFormedRevision: ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z
    }
}
// Every predicate is anchored at the true end of the text (the backend's \z, never $, which in .NET also matches before a
// final "\n"): the predicates are written as explicit character loops, with no Regex (rev 2 review I1).
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
    - the build revision `r1` (accepted), `-r1` and 65 characters (refused);
    - for every predicate, the accepted example plus a trailing `"\n"` is refused (review I1).
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
- the raw literal against `^(?:0|[1-9][0-9]*)\z` (`NotInteger`);
- the length > 10, the parse and the range (`OutOfBounds`).

`Str` checks the kind (`WrongType`), then a lone surrogate (`NotJson`, where the backend's `GetString()` throws), then the UTF-8 byte count > 128 (`OutOfBounds`). `RawMembers` raises `NotJson` for a member name with a lone surrogate, where the backend's `p.Name` would throw. The reader itself never refuses a lone surrogate. The executor records any refusal-order difference found while porting as a ruling, and adds a Unity-only test for it (never a vector).

**Format validation only (D2).** `ManifestCodec.Parse` answers one question: are these bytes a format-v1 manifest, and what is its canonical form? It does not check the admission snapshot, the loot pool, conservation, the objectives or any other V-rule. Its documentation says so, and no Unity code treats a successful parse as acceptance. Only the backend's `ManifestValidator` judges settlement (Task 7 runs it over Unity's bytes).

- [ ] **Step 1: Write the failing tests.**
  - `ManifestGoldenVectorTests` (reads `Backend/contracts/manifest-v1` through `ContractVectors.ProjectRoot`, files as raw bytes with `File.ReadAllBytes`):
    - `Every_valid_vector_canonicalizes_to_its_bytes_and_hash`:
      - at least 13 inputs (the folder holds 13);
      - for each: `Parse` is `Ok`, the canonical bytes equal `.canonical.json` byte for byte, and the hash equals `.hash.txt` without its final `\n`;
      - the canonical form is a fixed point (`Parse(canonical).CanonicalBytes` equals it).
    - `Every_invalid_vector_is_refused_with_its_code`: for each input, the code equals `.code.txt` without its final `\n`. The set of codes seen equals `ManifestCodes.All` minus `TooLarge`, so a lost vector fails the test.
    - `Vectors_10_11_12_share_02s_canonical_bytes`: the README's statement, checked.
    - `A_body_over_65536_bytes_is_TooLarge`: the 02-full canonical bytes with spaces inserted before the final closing brace up to 65,537 bytes (otherwise a valid manifest). `TooLarge` wins over everything else.
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
    public void ReportIntegrityFailure(string accountId, string code);   // InvalidOperationException once that participant is frozen
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
- a duplicate operative id, slot or item id across the ledger, or two stacks of one definition in one operative;
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

## Task 6: `ManifestBuilder`, with the truthful fallback (D2)

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
public enum ManifestBuildKind { Normal, LedgerMismatchFallback, Failed }
public sealed class ManifestBuildOutcome
{
    public ManifestBuildKind Kind { get; }
    public BuiltManifest Manifest { get; }              // null when Failed
    public IReadOnlyList<string> Diagnostics { get; }   // bounded words, in order: "normal:<code>", "fallback:<code>", the ledger's failure codes
}
public static class ManifestBuilder
{
    public static BuiltManifest Build(ManifestHeader header, ParticipantResult result, ParticipantFacts facts);
    public static BuiltManifest BuildLedgerMismatchFallback(ManifestHeader header, ParticipantResult result,
        string accountId, IReadOnlyList<OperativeSnapshot> admissionSnapshot);
    public static ManifestBuildOutcome BuildForPublication(ManifestHeader header, ParticipantResult result, ParticipantFacts facts,
        IReadOnlyList<OperativeSnapshot> admissionSnapshot);   // never throws ManifestFormatException
}
```

**`Build` steps:**
1. **Check the header** with `CanonicalIds` (each failure throws `ManifestFormatException` with the code the codec would give for the written value: `NonCanonicalId` for ids and hashes, `BadValue` for the build revision, `NotInteger` for a negative number (written as `-n`), `OutOfBounds` for a number outside its bounds, such as `lootTableVersion` 0). `header.accountId == facts.AccountId`, and `facts` comes from `Freeze` (an unfrozen ledger has no `ParticipantFacts`, by construction).
2. **Map facts to `ResultManifestV1`:**
   - Base58 → `long` through `Base58Id.TryParse`;
   - integrity from `facts.Integrity`;
   - objectives as given, with their ids checked by `CanonicalIds.IsWord(id, 64)` → `BadValue`;
   - `alive` from `deadOperativeIds`;
   - every operative of the participant, in slot order.
3. **`bytes = ManifestCodec.ToCanonicalBytes(model)`.**
4. **Format self-check (fail closed, D2):** `ManifestCodec.Parse(bytes)` must be `Ok`. Its canonical bytes must equal `bytes`; its attempt, account and **integrity** must equal the header's and the facts' (so a `LedgerMismatch` can never come out as `ok`); `bytes.Length` ≤ 65,536. Otherwise throw `ManifestFormatException(code)`. The builder never returns bytes the backend's parser would refuse. **This check is format-only:** it says nothing about V4–V15, which only the backend judges.
5. **Return** the bytes, `ManifestCodec.HashOf(bytes)` and the model.

**`BuildLedgerMismatchFallback`** restates only what is verified and preserved: the header, the authoritative result (outcome, sequence, elapsed time, objectives, the dead set) and the admission snapshot's operatives (ids, slots, owned items as held items, owned stacks as held stacks, the snapshot's equipment as item refs), with no consumption, no collection and no loot, and `integrity: "ledgerMismatch"`. Its meaning: "the mission's item facts are unknown; here is the admission state". It claims no item change, and `ledgerMismatch` makes the backend quarantine it for an operator (V14) instead of settling it. It checks `accountId == header.accountId`, the same header checks as `Build` and the same self-check. It never reads the ledger, so it survives a ledger defect.

**`BuildForPublication`:**
1. Try `Build`. Success → `Normal` (the facts' own integrity, `ok` or `ledgerMismatch`), with the ledger's failure codes as diagnostics.
2. On `ManifestFormatException(code)`, add `normal:<code>` and try the fallback. Success → `LedgerMismatchFallback`.
3. On a second `ManifestFormatException(code2)`, add `fallback:<code2>` and return `Failed` with no bytes. Stage 12 then writes nothing, keeps the diagnostics in its log and reports the failure. It never assumes a quarantine.

The fallback can rescue only a facts-level failure (for example a ledger output the format can't hold). A malformed header or objective id fails both builds, because the fallback needs the same header and result.

- [ ] **Step 1: Write the failing tests:**
  - `Builds_02_full_from_facts`: a ledger fed the events that produce 02-full's operatives, a header and a result equal to 02-full's, give exactly `valid/02-full.canonical.json`'s bytes and `.hash.txt`'s hash. The events are written out in the test, in this order: operative C (slot 0, snapshot: items `2k` weapon and `2m` armor equipped, 3 medkits) uses one medkit (owned, since no loot is held yet), then collects (0,0)×2 and (0,1)×1 (medkit lines), then uses one medkit (loot first, oldest first: from (0,0)); operative D (slot 1, snapshot: item `2n` weapon equipped) collects (2,0)×1 (a utility item), equips it and is dead at the freeze. The facts then equal the vector: an owned use before the collects is the only order loot-first consumption allows;
  - `Builds_13_ledger_mismatch`: a forced integrity failure gives exactly `valid/13-ledger-mismatch`'s bytes;
  - `Every_outcome_builds` (five outcomes; the enum has exactly five names, and none of them is `infra_void`);
  - `The_same_inputs_give_the_same_bytes`: two independent ledgers fed the same events give byte-identical output;
  - `An_unfrozen_participant_cannot_be_built` (compile-time: `Build` takes `ParticipantFacts`, which only `Freeze` returns; the test asserts there is no public constructor, by reflection);
  - `Events_after_freeze_do_not_change_the_bytes` (Review Focus 5);
  - `A_refused_string_fails_closed` (Review Focus 4): objective id `Extract`, objective id `"extract\n"`, build revision `"r1\n"`, a definition id `item medkit` reached through a crafted catalogue, a 65-character build revision and an uppercase attempt id each throw `ManifestFormatException` with the expected code, and nothing is returned;
  - `A_known_mismatch_never_builds_as_ok`: facts with `LedgerMismatch` give `"integrity":"ledgerMismatch"` from `Build` and from every `BuildForPublication` path;
  - `The_fallback_restates_the_snapshot`: exact expected bytes, written in the test, for a two-operative snapshot with an equipped item and a stack (same bytes as ledger-v1 scenario 08, Task 7);
  - `BuildForPublication_uses_the_fallback_only_after_a_format_failure`: valid facts → `Normal`; facts with an unrepresentable entry (a crafted catalogue whose definition id breaks the format, reached only through the ledger) → `LedgerMismatchFallback`, diagnostics `["normal:BadValue", ...]`; a malformed objective id → `Failed`, diagnostics `["normal:BadValue", "fallback:BadValue"]`, no bytes;
  - `A_parse_proves_the_format_only`: a manifest that parses cleanly but breaks conservation (more held than the snapshot) still parses, documenting that local parsing is not settlement validation;
  - `Facts_only`: by reflection, no public member of `ManifestHeader`, `ParticipantResult`, `ResultManifestV1`, `OperativeFact`, `BuiltManifest` or `ManifestBuildOutcome` is named like `seed`, `salt`, `xp`, `experience`, `reward`, `energy`, `insurance`, `refund` or `retention` (case-insensitive substring). And a builder run whose test harness also holds a seed value `0x5EED1234` never contains `"5EED"`, `"1592660532"` or `"seed"` in its bytes;
  - `Conservation_holds_for_every_built_manifest`: `Conservation.Check` on the parsed output of the Task 5 seeded runs, built through the builder.
- [ ] **Step 2: Run** `Tools/run-tests.sh EditMode "Blackglass.Tests.ManifestBuilderTests"`. Expected: EXIT=1.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the same command. Expected: EXIT=0.
- [ ] **Step 5: Commit.**

## Task 7: Shared ledger scenarios, validated by the backend (D3)

**Files:**
- Create: `Backend/contracts/ledger-v1/.gitattributes` (`* -text`), `README.md`, `01-owned-swap-and-use.json` … `08-ledger-mismatch-fallback.json`
- Create: `Backend/tests/Accounts.Integration.Tests/Database/LedgerFallbackQuarantineTests.cs` (D2: the store quarantines a snapshot-restating `ledgerMismatch` manifest with no effect)
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
| 08 ledger-mismatch-fallback | `"build": "fallback"`: the fallback manifest of a two-operative snapshot (an equipped item, a stack); no events | `["V14.integrity"]` |

Scenario 08 checks D2's fallback with the real validator: restating the snapshot breaks no rule except V14, so the backend quarantines it and never settles it. `LedgerFallbackQuarantineTests` then submits a manifest of the same shape (the attempt's own snapshot, nothing collected or consumed, `ledgerMismatch`) through `SettlementStore` and asserts: `Quarantined` with exactly `["V14.integrity"]`, the participant `quarantined`, no settlement row, the inventory and energy unchanged.

**Event vocabulary** (defined in the README): `collect {operativeId, containerIndex, line, quantity}`; `use {operativeId, definitionId}`; `equip {operativeId, item: {itemId} | {containerIndex, line}}`; `rogueAdd {operativeId, definitionId, quantity}` (a bag change with no ledger event, scenario 07). Optional top-level `bagCapacity` overrides the golden catalogue's 6 (scenario 03). Optional top-level `build`: `"normal"` (the default) or `"fallback"` (scenario 08: Unity calls `BuildLedgerMismatchFallback` with the snapshot and replays no events). Containers are filled one entry per pool line, in line order, with `ItemInventory(0).Add`; since `Add` merges same-definition stackables, **two lines of one stackable are placed in different containers** (scenario 06), or the first line is at `maxStack`. The Unity side maps each golden item to `ItemDefinition.Create(id, id, category, slot, maxStack, heal)` with the golden catalogue's category, slot, `maxStack` and heal amount. `AssertConsistent` runs after every event except a `rogueAdd`, whose mismatch is expected at the freeze. **Ids of mixed length** (`"z"` and `"21"`) appear in scenario 04's held items, so the backend confirms numeric ordering (M3).

The README states:
- the format;
- that each expected manifest is hand-written from the events (the authority, like the manifest-v1 vectors);
- that a change to a scenario is a contract change needing the owner.

- [ ] **Step 1: Write** the eight scenarios by hand, working each expected manifest out from its events on paper; note the arithmetic in the README per scenario. Then write both test classes.
- [ ] **Step 2: Run** `dotnet test Backend/tests/Accounts.Domain.Tests --filter "FullyQualifiedName~LedgerScenario"`. Expected: PASS (it needs only the backend and the hand-written files). A failure here is a scenario-authoring error, fixed in the scenario before any Unity work.
- [ ] **Step 3: Run** `Tools/run-tests.sh EditMode "Blackglass.Tests.LedgerScenarioVectorTests"`. Expected: EXIT=0 if Tasks 4–6 are right. A mismatch is investigated against the hand-written manifest, never by copying Unity's output into the scenario.
- [ ] **Step 4: Run** the backend domain suite (`dotnet test Backend/tests/Accounts.Domain.Tests`) and `Backend/tools/vm-test.sh --filter "FullyQualifiedName~LedgerFallbackQuarantineTests"`. Expected: all pass, with the count before plus the new tests.
- [ ] **Step 5: Commit** (the contracts folder, the backend test, the csproj line, the Unity test).

## Task 8: The outbox: an approved directory, the durable protocol and the I/O worker (D6, D8)

**Files:**
- Create: `Outbox/OutboxFileNames.cs`, `Outbox/OutboxIOException.cs`, `Outbox/IOutboxDirectory.cs`, `Outbox/OutboxLocation.cs`, `Outbox/ILibc.cs`, `Outbox/NativeLibc.cs`, `Outbox/PosixOutboxDirectory.cs`, `Outbox/PortableOutboxDirectory.cs`, `Outbox/OutboxDirectories.cs`, `Outbox/OutboxWriter.cs`, `Outbox/OutboxWorker.cs`, `Outbox/PublicationHandle.cs`
- Create (D8): `Backend/tests/UnityCore.Linux.Tests/UnityCore.Linux.Tests.csproj`, `PosixOutboxTests.cs`; the `unity-core` mode in `Backend/tools/vm-test.sh` and `Backend/tools/run-tests-in-container.sh`
- Test: `Tests/EditMode/Backend/FaultingOutboxDirectory.cs`, `FakeLibc.cs`, `AgentView.cs` (helpers), `OutboxLocationTests.cs`, `PosixOutboxDirectoryTests.cs`, `OutboxWriterTests.cs`, `OutboxWorkerTests.cs`

**The execution model (owner ruling D6).** Nothing in the simulation frame loop writes a file or calls `fsync`:
1. On the simulation (main) thread, Stage 12 captures the immutable result: `BuiltManifest.CanonicalBytes` and `ManifestHash` (Task 6).
2. `OutboxWorker.Submit(accountId, bytes, hash)` copies the bytes, queues the job and returns a `PublicationHandle` at once. It takes one short lock that the worker never holds across I/O.
3. One dedicated background thread (`blackglass-outbox`) owns the `IOutboxDirectory` and performs every file operation, one job at a time.
4. The simulation reads `handle.Status`, an immutable snapshot taken under the same short lock; it never waits.
5. Core has no engine references (D1), so the worker cannot touch a Unity object or API. Stage 12 reads handles only from the main thread.

No backend communication is in the outbox. The §H step 3 (the bounded `recorded` notice) is Stage 13's: it sends the notice once a handle is `Written`, and calls `handle.AllowPublication()` when that attempt ends (any answer, or its time bound). The worker then performs §H step 4. Until then a durable `.written` is the result, and the agent's read-only recovery relays it after a verified exit (A10-D1), so preserving a result never needs the backend.

**Interfaces (produced):**
```csharp
public static class OutboxFileNames      // the agent's naming contract (OutboxScanner.FileName, A10-D1)
{
    public const string TmpSuffix = ".manifest.tmp", WrittenSuffix = ".manifest.written", ReadySuffix = ".manifest.ready";
    public static string Tmp(string accountId); public static string Written(string accountId); public static string Ready(string accountId);
        // ArgumentException unless accountId is canonical Base58 (Base58Id.TryParse and Format round trip)
    public static bool IsOutboxName(string name);   // ^[1-9A-HJ-NP-Za-km-z]{1,11}\.manifest\.(tmp|written|ready)\z and canonical Base58 (explicit checks, no Regex)
}
public sealed class OutboxIOException : IOException
{
    public OutboxIOException(string code, int errno);   // code: [a-z_]{1,40}; errno 0 when not from libc
    public string Code { get; } public int Errno { get; }
}
public enum EntryKind { Absent, RegularFile, Unsafe }   // Unsafe: a symbolic link, a non-regular file, or more than one hard link
/// One approved directory, addressed by outbox file names only (never a path): every method refuses a name that fails
/// OutboxFileNames.IsOutboxName, so no operation can leave the directory. Not thread-safe: the worker is its only user.
public interface IOutboxDirectory : IDisposable
{
    EntryKind Probe(string name);
    byte[] ReadRegular(string name, int maxBytes);          // a regular, singly linked file of at most maxBytes; else OutboxIOException
    void WriteAndSync(string name, byte[] bytes);           // create or truncate, mode 0644, no symlink, write all, fsync, close
    void RenameNoReplace(string fromName, string toName);   // one atomic rename that never replaces an existing name
    void SyncDirectory();                                   // fsync the directory itself
}
public static class OutboxLocation
{
    public const string StateDirectoryVariable = "STATE_DIRECTORY";
    /// The only production path rule: the value systemd set must be exactly "<requiredParent>/<attemptId>": absolute, one path
    /// (systemd joins several with ':'), normalized (no "//", ".", "..", trailing '/'), at most 4096 bytes, under the parent,
    /// with the attempt's canonical UUIDv7 as its last segment. Refusal codes: missing, not_absolute, multiple_paths,
    /// not_normalized, too_long, outside_parent, wrong_attempt.
    public static bool TryResolve(string stateDirectory, string requiredParent, string attemptId, out string path, out string refusal);
}
public static class OutboxDirectories
{
    /// Production: Linux x86_64 only (else PlatformNotSupportedException). Resolves the value with OutboxLocation (refusal →
    /// OutboxIOException(refusal, 0)), then opens it as a PosixOutboxDirectory. Called once at process start, before the loop.
    public static IOutboxDirectory OpenForServer(string stateDirectory, string requiredParent, string attemptId);
    /// Tests and Editor tools only, never a real outbox: PortableOutboxDirectory (FileStream + Flush(true), File.Move,
    /// SyncDirectory a no-op). A missing directory → DirectoryNotFoundException; nothing is created.
    public static IOutboxDirectory OpenForEditor(string path);
}

public enum WriteResult { Written, AlreadyWritten, AlreadyPublished, Conflict }
public enum PublishResult { Published, AlreadyPublished, NothingWritten, Conflict }
/// The §H protocol over one directory, synchronous: only the worker (and tests) call it.
public sealed class OutboxWriter
{
    public const int MaxBytes = 65536;
    public OutboxWriter(IOutboxDirectory directory);
    public WriteResult WriteDurable(string accountId, byte[] canonicalBytes);   // §H 1–2
    public PublishResult Publish(string accountId, byte[] canonicalBytes);      // §H 4; the bytes identify the result it may publish
}

public enum PublicationStage { Queued, Writing, Written, Publishing, Published, Conflict, Failed, Cancelled }
public sealed class PublicationStatus   // immutable
{
    public PublicationStage Stage { get; } public bool Durable { get; } public bool PublicationAllowed { get; }
    public string FailureCode { get; } public int Errno { get; }   // set only when Failed
}
public sealed class PublicationHandle
{
    public string AccountId { get; } public string ManifestHash { get; }
    public PublicationStatus Status { get; }   // thread-safe, never waits for I/O
    public void AllowPublication();             // Stage 13: the recorded-notice attempt ended; idempotent
    public void Cancel();                       // see below; idempotent; never deletes anything
}
public sealed class OutboxWorker : IDisposable
{
    public OutboxWorker(IOutboxDirectory directory);    // starts the background thread "blackglass-outbox"; owns the directory
    public PublicationHandle Submit(string accountId, byte[] canonicalBytes, string manifestHash);
    public bool Stop(TimeSpan drain);                   // see below; true when every job reached a resting stage in time
    public void Dispose();                              // Stop(TimeSpan.Zero); never closes the directory itself (review I3)
}
```

**`OutboxWriter.WriteDurable(account, bytes)`, exactly:**
1. `account` canonical Base58 and `bytes` 1..65,536 long, else `ArgumentException` and no file operation.
2. `Probe(ready)`: `Unsafe` → `OutboxIOException("unsafe_entry")`. `RegularFile` → `ReadRegular`; equal bytes → `SyncDirectory()`, `AlreadyPublished`; different → `Conflict`.
3. `Probe(written)`: the same, giving `AlreadyWritten` (after `SyncDirectory()`, M4) or `Conflict`. **A durable result is never replaced.**
4. `Probe(tmp)`: `Unsafe` → `unsafe_entry`. A regular leftover `.tmp` is truncated and rewritten by the next step, never promoted.
5. `WriteAndSync(tmp, bytes)`, `RenameNoReplace(tmp, written)`, `SyncDirectory()` → `Written`. Only after the directory fsync is the result durable.

**`Publish(account, bytes)`:** `Probe(ready)` regular → equal: `SyncDirectory()`, `AlreadyPublished`; different: `Conflict`. `Probe(written)` absent → `NothingWritten`; regular with different bytes → `Conflict`; equal → `RenameNoReplace(written, ready)`, `SyncDirectory()` → `Published`. `Unsafe` anywhere → `unsafe_entry`.

Nothing ever deletes, truncates or rewrites a `.written` or `.ready` file. A `.tmp` is truncated only by the next write of the same participant.

**The worker, exactly:**
- `Submit` validates its arguments on the caller's thread (`ArgumentException`: account, length 1..65,536, a `sha256:` hash), copies the bytes, and returns:
  - the existing handle, when one for that account is not `Failed` or `Cancelled` and has equal bytes and hash (a repeated request);
  - a new handle already in `Conflict`, with no file operation, when that existing handle has different bytes;
  - otherwise a new `Queued` handle. After `Stop`, `Submit` throws `InvalidOperationException`.
- The thread takes jobs in submission order: a `Queued` job is written first (verifying `SHA-256(bytes)` against the hash, else `Failed("hash_mismatch")`), then any `Written` job whose publication is allowed is published.
- Results: `Written`/`AlreadyWritten` → `Written` (`Durable`); `AlreadyPublished`/`Published` → `Published`; `Conflict` → `Conflict`; `OutboxIOException` → `Failed` with its code and errno; any other exception → `Failed("unexpected")`. The thread never dies on a job's exception. `Durable` stays false when a failure came before the directory fsync completed.
- **No automatic retry.** A failed `fsync` may have dropped the dirty pages, so a later `fsync` proves nothing about the old write. For a failed **directory** fsync the same caution applies: on ext4 it normally means the journal aborted and the filesystem went read-only, so the resubmit's directory fsync fails too; if it does succeed, `Durable` means "a directory fsync succeeded after the rename", which is the best evidence Linux offers. Stage 12 resubmits at most once and logs the first failure (review M2). A new `Submit` of the same bytes runs the protocol again: a fresh `.tmp` rewritten in full, or, when `.written` already exists, another directory fsync (`AlreadyWritten`). Stage 12 decides whether to resubmit.
- `Cancel()`: a `Queued` job becomes `Cancelled` with nothing written. A job being written finishes its durable write (never half), then becomes `Cancelled` with `Durable` true. A `Written` job becomes `Cancelled` and is never published (its `.written` stays for the agent's exit recovery). A job being published finishes as `Published`. Terminal stages ignore it.
- **Only the worker thread closes the directory**, as its very last action after its loop ends (review I3). `Stop` and `Dispose` only signal; neither ever closes the descriptor from the caller's thread, so a descriptor number can't be reused under a worker still in `fsync`. The thread is a background thread (`IsBackground = true`), so it never keeps the process alive.
- `Stop(drain)`: no new submissions. The thread still completes every queued durable write and every allowed publication, then ends. `Stop` waits at most `drain` and returns whether the thread ended. A job still waiting for `AllowPublication` stays `Written`, and its `.written` is the result.

**`PosixOutboxDirectory`** (Linux x86_64; every call through `ILibc`, so its loops are tested with a fake in EditMode and with the real libc on Linux):
- **Open:** `open(path, O_RDONLY | O_DIRECTORY | O_CLOEXEC)` (it follows the root-owned symlinks systemd's `DynamicUser=` layout puts under `/var/lib`), then `fstat` the descriptor: a directory (`not_a_directory`), owned by `geteuid()` (`wrong_owner`), with no group or other write bit (`writable_by_others`). The directory descriptor is kept, and every later call is relative to it, so a renamed or replaced path can't redirect a write.
- **Probe and read:** `openat(dirfd, name, O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC)` (`ENOENT` → `Absent`; `ELOOP` → `Unsafe`; any other errno → `open_failed`), then `fstat`: regular and `st_nlink == 1`, else `Unsafe`; `read` in a loop to the `fstat` size (bounded by `maxBytes`), `EINTR` retried.
- **Write:** `openat(dirfd, name, O_WRONLY | O_CREAT | O_TRUNC | O_NOFOLLOW | O_CLOEXEC, 0644)`, then `fstat` (regular, one link, owned by `geteuid()`, else `unsafe_entry`) and `fchmod(fd, 0644)`, since `O_CREAT`'s mode applies only to a new file (review M3); `write` in a loop over a pinned buffer (`GCHandle`, no unsafe code), continuing after a partial write and retrying `EINTR`; `fsync`, retrying `EINTR`; `close`. A failed `write` or `fsync` closes the descriptor and throws (`write_failed`, `fsync_failed`). `close` is never retried: `EINTR` from `close` counts as closed (Linux has released the descriptor); any other `close` error is `close_failed`. The mode reaches the agent through the unit's `UMask=0022` and the `0755` state directory.
- **Rename:** `renameat2(dirfd, from, dirfd, to, RENAME_NOREPLACE)`, retrying `EINTR`. An existing target is `EEXIST`, so even a race can't replace a result (`rename_failed` with errno 17). A filesystem without `RENAME_NOREPLACE` (`EINVAL`) fails closed (`rename_failed`); ext4, the mission VM's filesystem, supports it.
- **Directory sync:** `fsync(dirfd)`, retrying `EINTR` (`directory_sync_failed`).
- **Calls** (all `libc.so.6`, `SetLastError = true`, errno from `Marshal.GetLastWin32Error()`): `open`, `openat`, `read`, `write`, `fsync`, `close`, `fstat`, `fchmod`, `renameat2`, `geteuid`, `umask` (tests only). `size_t`/`ssize_t` are `IntPtr`/`UIntPtr`; names are passed as NUL-terminated ASCII `byte[]` (no string marshalling question on Mono). Any `openat` errno other than `ENOENT` (`Absent`) and `ELOOP` (`Unsafe`) is a failure (`open_failed`), never `Absent`; `read` or `write` returning 0 before the end is a failure, not another loop (review M4). `fstat` and `renameat2` need glibc 2.33 and 2.28; Ubuntu 24.04 has 2.39. The `struct stat` layout is x86_64's, as in the agent's `Unix.cs`. `openat`'s mode argument is passed as a fourth integer, which the x86_64 variadic calling convention accepts.

**`FakeLibc`** (EditMode helper): in-memory files and descriptors behind `ILibc`. Scripted faults: a short `write`, `EINTR` on the next N calls of a function, `EIO` from `fsync` or `close`, `EEXIST`/`EINVAL` from `renameat2`, a symlink or FIFO entry, a directory with the wrong owner or mode.

**`FaultingOutboxDirectory`** (EditMode helper) wraps `PortableOutboxDirectory`. It records every call in order, and can "crash" (throw `SimulatedCrash`) before or after any numbered call or halfway through `WriteAndSync`, leaving a partial `.tmp`. It can also fail a call with an `OutboxIOException`, or block a call on a gate (a delayed `fsync`). `AgentView.Classify(dir, account)` mirrors the agent's rules (`.tmp` never a result; `.ready` relayable now; `.written` only after a verified exit) and checks that each result file parses with `ManifestCodec.Parse` and names the attempt and account.

- [ ] **Step 1: Write the failing tests:**
  - **`OutboxLocationTests`:** the good value `/var/lib/blackglass-outbox/<attempt>` resolves; one case per refusal code (empty, relative, `a:b`, `//`, `/./`, `/../`, a trailing `/`, a sibling of the parent, a prefix trick such as `/var/lib/blackglass-outbox-evil/<attempt>`, another attempt id, an uppercase attempt id, 4,097 bytes). `OpenForServer` on Windows throws `PlatformNotSupportedException`; `OpenForEditor` on a missing directory throws and creates nothing.
  - **`PosixOutboxDirectoryTests` (over `FakeLibc`):**
    - the open refuses a non-directory, another owner and a group- or world-writable directory, and closes its descriptor;
    - a write split into three partial writes produces the exact bytes;
    - `EINTR` on `openat`, `write`, `fsync` and `renameat2` is retried, and the result is correct;
    - `EINTR` on `close` is not retried and counts as success;
    - `fsync` `EIO` → `fsync_failed` (errno 5), the descriptor closed, no rename attempted by the writer;
    - `close` `EIO` → `close_failed`;
    - `renameat2` `EEXIST` and `EINVAL` → `rename_failed` (17, 22), nothing replaced;
    - a symlink and a FIFO entry probe as `Unsafe`; a doubly linked file probes as `Unsafe`;
    - every method refuses `../x.manifest.tmp`, `a/b`, an empty name and a non-canonical Base58 name before any libc call.
  - **`OutboxWriterTests` (over `FaultingOutboxDirectory`):**
    - **Call order:** `WriteDurable_calls_in_order` gives exactly `[Probe(ready), Probe(written), Probe(tmp), WriteAndSync(tmp), RenameNoReplace(tmp, written), SyncDirectory]`; `Publish_calls_in_order` gives `[Probe(ready), Probe(written), ReadRegular(written), RenameNoReplace(written, ready), SyncDirectory]`.
    - **Crash boundaries** (one test per point; each asserts `AgentView` and the bytes after a fresh writer re-reads the directory): halfway through the tmp write (a partial `.tmp`, absent); after the tmp write (absent: a `.tmp` is never a result); after the rename, before the directory sync (`.written` complete; on Linux the directory fsync makes it durable); after the written sync (`.written`, present once the exit is verified); after the publish rename (`.ready`, relayable now).
    - **Idempotence and refusals:** identical bytes again → `AlreadyWritten`; different bytes → `Conflict` with nothing changed (Review Focus 3); a leftover `.tmp` is rewritten, never promoted; `Publish` with nothing written → `NothingWritten`; twice → `AlreadyPublished`; `Publish` with different bytes → `Conflict`; an `Unsafe` `.written` → `unsafe_entry`; a bad account, empty or oversized bytes → `ArgumentException` with no call recorded; the file names match the agent's pattern.
    - **Retry after a failed directory sync (M4):** the first `SyncDirectory` fails; a second `WriteDurable` returns `AlreadyWritten` and the calls show a second `SyncDirectory`. The same for `AlreadyPublished`.
  - **`OutboxWorkerTests`** (real thread; every wait in a test is bounded, 5 s):
    - `Submit_returns_before_any_file_operation`: with a `FaultingOutboxDirectory` whose `WriteAndSync` blocks on a gate, `Submit` returns a `Queued` or `Writing` handle and no `.written` exists;
    - `The_simulation_is_not_blocked_by_a_slow_fsync` (the responsiveness check): with `WriteAndSync` held on a gate, 1,000 simulated ticks each call `Status` and `Submit` (another account) and all complete while the gate is still closed (the structural proof: the calls returned while the worker was blocked), with a generous 250 ms bound on the slowest call against Editor jitter. Then the gate opens and the handle reaches `Written`;
    - `Dispose_while_the_worker_is_blocked_closes_nothing_early` (review I3): `Dispose` while `WriteAndSync` is held; release the gate; the file lands in the original directory, and the recorded calls show `Dispose` of the directory after the last file call;
    - every test releases its gates and stops its worker in `TearDown`, so a failing test can't leave a blocked thread across a domain reload (review M5);
    - `Submit_copies_the_bytes` (changing the caller's array afterwards changes nothing on disk);
    - `Written_waits_for_AllowPublication`, then `Published` with `.ready`;
    - `AllowPublication_before_the_write_completes_publishes_after_it`;
    - `A_repeated_identical_Submit_returns_the_same_handle`; `A_different_Submit_is_Conflict_and_touches_nothing`;
    - `A_failure_at_each_step_is_reported` (one case per injected failure: tmp write, `fsync`, rename, directory sync, publish rename, publish sync): the handle is `Failed` with the code and errno, `Durable` is right, and `AgentView` classifies the files correctly;
    - `A_resubmit_after_a_failed_directory_sync_becomes_Written` (`AlreadyWritten` path, a second `SyncDirectory`);
    - `A_hash_that_does_not_match_the_bytes_is_Failed`;
    - `Cancel_before_the_write_writes_nothing`; `Cancel_during_the_write_finishes_it_and_never_publishes`; `Cancel_after_Written_never_publishes`;
    - `Stop_completes_queued_writes_and_allowed_publications`; `Stop_leaves_an_unreleased_result_written`; `Submit_after_Stop_throws`;
    - `An_unexpected_exception_does_not_kill_the_worker` (the next job still completes);
    - `No_backend_is_needed_to_preserve_a_result`: no `AllowPublication` at all; the `.written` holds the exact bytes from the moment the handle is `Written`.
  - **Linux (D8, `Backend/tests/UnityCore.Linux.Tests`, xUnit on .NET 10):** the project compiles Core's `Outbox/*.cs`, `Ids/Base58Id.cs` and `Ids/CanonicalIds.cs` by link (`Nullable` and implicit usings off, as Unity compiles them; `TreatWarningsAsErrors` stays on, with any .NET 10 analyzer rule that a .NET Standard 2.1 source can't satisfy suppressed by name in the project file and listed in the ledger) and references `MissionHost.Agent`. Core uses only .NET Standard 2.1 APIs (`RuntimeInformation.IsOSPlatform`/`ProcessArchitecture`, `Marshal.GetLastWin32Error`, `SHA256.Create`, hand-written lowercase hex), never the agent's .NET-only ones (`OperatingSystem.IsLinux`, `GetLastPInvokeError`, `Convert.ToHexString`, `SHA256.HashData`). With the real libc, in a fresh directory on the `/work` volume (the VM's filesystem, not the container's overlay), after the fixture sets `umask(0022)` through libc:
    - a full write and publish through `OutboxWorker`, then read back with the agent's own `Unix.ReadUntrusted` and `OutboxScanner.Scan`: a regular file, one link, mode 0644, the exact bytes; `.tmp` ignored; `.written` relayable only with `includeWritten`; `.ready` relayable at once;
    - `RENAME_NOREPLACE` refuses an existing target, and the target's bytes are unchanged;
    - a planted symlink `.written` and a planted FIFO probe as `Unsafe` (the FIFO without blocking);
    - the open refuses a directory chmodded 0777 (`writable_by_others`) and a regular file (`not_a_directory`);
    - a `FaultingLibc` wrapper around the real libc forces partial writes and `EINTR` on every call once, and the file is still exact;
    - the directory fsync succeeds.

    Run by `Backend/tools/vm-test.sh --unity-core` (review I2). The mode snapshots `Backend/` and `Assets/_Project/Scripts/Backend/Core/` into one tree id and syncs both to their repo-relative paths, so the links resolve. It then uses two containers, each with the runner's user, CPU, memory, capability and lock limits and **no database secrets or settings**: (1) restore and build this project only, in the existing networked container design (`--network container:<test cluster>`, the only route to the package feed); (2) run its tests with `--network none --no-restore --no-build`. Nothing changes for the other modes. Because the project links files outside `Backend/`, it is **not** added to `Backend.sln`.

    Passing it proves the code on CoreCLR, not Mono: Stage 12 must run the same operations in Unity's Linux Mono server player on the mission VM (`-outbox-selftest`, with the owner's go-ahead) before relying on them.
- [ ] **Step 2: Run** `Tools/run-tests.sh EditMode "Blackglass.Tests.OutboxLocationTests;Blackglass.Tests.PosixOutboxDirectoryTests;Blackglass.Tests.OutboxWriterTests;Blackglass.Tests.OutboxWorkerTests"`. Expected: EXIT=1 (types missing).
- [ ] **Step 3: Implement**, the protocol first, then the directories, then the worker.
- [ ] **Step 4: Run** the same command. Expected: EXIT=0. Run `Backend/tools/vm-test.sh --unity-core`. Expected: all Linux tests pass.
- [ ] **Step 5: Commit.**

**If the worker cannot meet these conditions** (for example a Mono threading limitation discovered later), stop and report. There is no synchronous main-thread fallback.

## Task 9: Consumables must stack, for new exports and registrations only (D7)

**Why:** format v1 can report a consumable's use only as a stack (V8 needs a stackable consumable; V10 needs every snapshot instance held), so a non-stackable consumable could never be used without a `ledgerMismatch`. Both validators accept one today; no catalogue in the repository has one (the medkit and `item.gamma-kit` stack to 3).

**Historical compatibility (checked):** `GameCatalogue.Load` runs `CatalogueValidator.Validate`, and `CatalogueHistoryStore.LookupLockedAsync` loads every stored version through `GameCatalogue.Load`, returning `Unavailable("invalid")` when it throws. A rule added to `CatalogueValidator` would make a stored catalogue unreadable and hold its settlements (`catalogue_unavailable`). So the rule is **not** added there. It is a separate registration rule:
- `CatalogueValidator.Validate` and `GameCatalogue.Load` are unchanged: they remain format 2's reading rules, for every stored version.
- New `CatalogueRegistrationRules.Check(CatalogueDocument)` returns the registration-only problems (today one: `incompatible: items[i] '<id>': a consumable must be stackable (registration rule, Stage 11)`).
- `CatalogueHistoryStore.RegisterAsync` applies it **only when it inserts a new version** (inside the `!exists` branch, before the identity-kind check), throwing `CatalogueRegistrationException`. Re-registering an already stored version (serve's startup, an admission) is unchanged. Every registration path goes through `RegisterAsync`: `serve` (exit `CatalogueIdentityChanged` with the message), `recover catalogue import`, and the first admission.
- Unity's exporter (`CatalogueBuilder`) refuses a non-stackable consumable, so a new export can't produce one. `ItemDefinition.IsValid` (offline gameplay validation) is unchanged: offline play may keep any stack size, and only the export to the backend is constrained.

No stored bytes, hash identities, item definitions, catalogue data or settled outcomes change.

**Files:**
- Create: `Backend/src/Accounts.Domain/Catalogue/CatalogueRegistrationRules.cs`; `Backend/tests/Accounts.Domain.Tests/Catalogue/CatalogueRegistrationRulesTests.cs`
- Modify: `Backend/src/Accounts.Persistence/Catalogue/CatalogueHistoryStore.cs` (the check in `RegisterAsync`); `Backend/tests/Accounts.Integration.Tests/Database/CatalogueHistoryTests.cs` (two tests)
- Modify: `Assets/_Project/Editor/Backend/CatalogueBuilder.cs` (one rule); `Assets/_Project/Tests/EditMode/CatalogueBuilderTests.cs` (one test)

- [ ] **Step 1: Write the failing tests:**
  - `CatalogueRegistrationRulesTests`:
    - `The_golden_and_committed_catalogues_pass`;
    - `A_non_stackable_consumable_is_refused_for_registration`: the golden document with `item.gamma-kit` at `maxStack` 1 and `stackable` false gives exactly the one problem;
    - `A_historical_catalogue_with_one_still_loads`: the same document's canonical bytes load through `GameCatalogue.Load` (its version is the hash of those bytes, unchanged).
  - `CatalogueHistoryTests` (integration):
    - `A_stored_catalogue_that_breaks_the_registration_rule_stays_readable`: the bytes inserted directly as history (superuser, as an older registration would have left them) → `LookupAsync` is `Found` with the same version; `RegisterAsync` of the same catalogue returns false and doesn't throw;
    - `A_new_catalogue_that_breaks_the_rule_is_not_registered`: a fresh consumable id (so the identity-kind check can't be what throws) at `maxStack` 1; `RegisterAsync` throws `CatalogueRegistrationException` whose message names the registration rule, and no row exists for its version (review M7).

  Accepted limitation (review M7): `serve` keeps admitting under an already-registered version that breaks the rule. None exists today, and refusing would break the historical-compatibility ruling.
  - `CatalogueBuilderTests.Build_RefusesANonStackableConsumable`: a consumable asset with `maxStack` 1 → `incompatible:` and `a consumable must be stackable`.
- [ ] **Step 2: Run** `dotnet test Backend/tests/Accounts.Domain.Tests --filter "FullyQualifiedName~CatalogueRegistrationRules"` (expected: build failure), `Tools/run-tests.sh EditMode "Blackglass.Tests.CatalogueBuilderTests"` (expected: EXIT=2, one failure) and `Backend/tools/vm-test.sh --filter "FullyQualifiedName~CatalogueHistoryTests"` (expected: the new test fails).
- [ ] **Step 3: Implement** the rule, the `RegisterAsync` check and the exporter rule.
- [ ] **Step 4: Run** the same three commands. Expected: all pass. The committed `Backend/catalogue/catalogue.json` is unchanged, and Unity's stale-export test still passes.
- [ ] **Step 5: Commit.**

## Task 10: `INetAdmission`, the final audit, docs and the stop

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
- [ ] **Step 3: Run the full suites:** `Tools/run-tests.sh EditMode`, `Tools/run-tests.sh PlayMode`, `dotnet test Backend/tests/Accounts.Domain.Tests`, `Backend/tools/vm-test.sh --unity-core` (D8) and `Backend/tools/vm-test.sh --filter "FullyQualifiedName~CatalogueHistoryTests|FullyQualifiedName~LedgerFallbackQuarantineTests|FullyQualifiedName~SettlementTests"` (the integration tests the shared contracts touch). Expected:
  - EditMode: 1579 + this stage's tests, 0 failed;
  - PlayMode: exactly the baseline total, 0 failed;
  - backend domain: the previous total + the new tests, 0 failed;
  - the Linux and integration runs: 0 failed.

  Save the outputs to `.claude/scratchpad/phase13/stage-11/`.
- [ ] **Step 4: The scope check:**
  - `git status --short` shows only this stage's paths plus the owner's two Unity files and `.claude/`;
  - `git diff --stat main...HEAD` lists only the paths in Global Constraints;
  - `git diff main...HEAD -- Backend/contracts/manifest-v1 Backend/catalogue Assets/_Project/Scripts/Blackglass.asmdef Assets/_Project/Scripts/Net Assets/_Project/Scripts/Items Backend/deploy` is empty.
- [ ] **Step 5: The final review:** one independent whole-branch review on the most capable model, against this plan and the spec, with Review Focus verbatim: contract fidelity, the worker's thread safety, filesystem crash safety, path safety, historical catalogue compatibility. Fix Critical and Important findings test-first. Record the minors.
- [ ] **Step 6: Docs as built:** counts, rulings, known runtime limitations, Stage 12 obligations and deferred minors in this plan; the Stage 11 status in the staged plan and `CLAUDE.md`. Commit by explicit paths. **Stop for the owner's review:** no merge, push or deployment.

---

## Acceptance criteria

- Every `valid/` and `invalid/` manifest-v1 vector passes in EditMode byte for byte (canonical bytes, hash, refusal code). The vector folder is unchanged and pinned by hashes.
- `LootPoolHash` equals the backend's pinned value.
- The receipt vectors read correctly; retention vectors are untouched and unused by Unity (D4).
- The ledger:
  - stays consistent with Unity's real `ItemInventory` after every mutation in 200 seeded runs and every named case;
  - conserves (V10), uses loot first and oldest first;
  - keeps non-stackable identities through collect and equip, and records equipment changes;
  - detects a rogue mutation as `ledgerMismatch`, which never becomes `ok`.
- The eight ledger-v1 scenarios produce byte-identical manifests in Unity and validate in the backend's `ManifestValidator` with exactly the expected errors: none, or `V14.integrity` for the mismatch and fallback scenarios. The backend store quarantines a snapshot-restating `ledgerMismatch` manifest with no effect.
- The builder is deterministic, facts-only and fail-closed; it never sees a seed or salt. `BuildForPublication` returns the normal manifest, the truthful fallback, or a failure with diagnostics, never an invented result.
- The outbox follows `.tmp` → `.written` → `.ready` with a file fsync, a no-replace rename and a directory fsync, all relative to the one approved directory, verified on Linux against the agent's own reader (D8). Every file operation runs on the worker thread; the simulation's calls never wait for I/O. Every crash and failure point leaves a state the agent classifies correctly. A durable result is never replaced or deleted, and preserving one needs no backend.
- New catalogue registrations and Unity exports refuse a non-stackable consumable; every stored catalogue stays readable under its original rules (D7).
- `INetAdmission` exists, library-free. `NetAdmission` and all networking code are unchanged.
- The Core assembly references nothing, and the source audit passes.
- EditMode = baseline + new tests, PlayMode = baseline, backend domain and the touched integration tests green. Only the listed paths changed.

## Out of scope (later stages)

- Wiring the ledger to `UnitItems` events, snapshot mode and the server initializer (Stage 12).
- The `UnityWebRequest` client, the bounded `recorded` notifier (which calls `AllowPublication`), start, heartbeat and register (Stage 13).
- `NetTicketAdmission`, ownership lists and new requests (Stage 14).
- The inventory-operations request writer and the client receipt screen (Stage 16).
- Verifying the outbox's libc calls and worker thread under Unity's Linux Mono server player on the mission VM (Stage 12's `-outbox-selftest`, with the owner's go-ahead); CoreCLR on Linux is covered here (D8).
- Retention and insurance selection (the backend's, D4).
- Any B24 work.

## Compatibility with Stages 12 and 13

- **Stage 12:**
  - snapshot mode: `OperativeSnapshot.FromDto` feeds the ledger and the runtime builds bag entries whose ids are the Base58 item ids; the same snapshot is kept for the fallback;
  - the container fill calls `RegisterContainer` with the entries after `LootContainer.Initialize`, and the `ready` report's `lootPool` and `lootHash` come from `PoolLines()` and `LootPoolHash`;
  - `UnitItems`' additive detail events call `Collect`, `Use` and `Equip`;
  - at process start, before the mission loop: `OutboxDirectories.OpenForServer(Environment.GetEnvironmentVariable("STATE_DIRECTORY"), "/var/lib/blackglass-outbox", attemptId)` and one `OutboxWorker`. A refusal stops the process before it reports ready (nothing is charged). The unit's `StateDirectory=blackglass-outbox/%i` and `StateDirectoryMode=0755` already satisfy the checks;
  - the outcome freeze calls `Freeze` (which verifies), then `ManifestBuilder.BuildForPublication`, then `OutboxWorker.Submit` with the bytes and hash. A `Failed` build writes nothing, logs its diagnostics and reports the failure; quarantine is never assumed (D2). A `Failed` handle is resubmitted or reported per Stage 12's policy;
  - the Mono self-test (`-outbox-selftest`) runs the worker and the libc path in the server player on the mission VM before any real result depends on them (D8).
- **Stage 13:** the `UnityWebRequest` adapter posts `RecordedRequest {manifestHash}` once a handle is `Written` (never before: the hash must name a durable result), retries within the provisional 30 s bound, then calls `handle.AllowPublication()` whatever the answer. The process then tells the client the result is saved and pending, and exits. **It does not poll settlement** (S9-B6): the authenticated player client polls its own receipt. The staged plan's Stage 13 line is corrected in this revision.
- **Stage 14:** `NetAdmission` and `NetTicketAdmission` implement `INetAdmission`.
- **Stage 16:** client DTOs and `ReceiptV1`.

---

## Owner rulings (2026-10-19)

The owner approved the architecture and authorized implementation after this revision and its review, with these rulings. Each is reflected in the task named.

- **D1, its own assembly: approved.** `Blackglass.Backend.Core`, `noEngineReferences`, no networking or third-party reference; plain C# types; DTOs `JsonUtility`-compatible without referencing UnityEngine. The EditMode tests reference it now; Stage 12 adds the gameplay reference (Task 1).
- **D2, the strict parser: approved with safeguards.**
  - Parse and canonicalize format v1, reproducing all 13 valid and 20 invalid vectors (bytes, hashes, codes), using only APIs of Unity's runtime (Task 2).
  - **Format validation is not settlement validation:** a local parse proves the bytes are a format-v1 manifest. It does not prove the admission snapshot, the loot pool, conservation or any other V-rule; only the backend's `ManifestValidator` judges those (Tasks 2, 6, 7).
  - A known ledger mismatch never produces `integrity: "ok"` (Tasks 4, 6).
  - The minimal `ledgerMismatch` fallback restates only verified attempt metadata, the authoritative result (outcome, sequence, elapsed time, objectives and who died, all known to the server independently of the item ledger) and the preserved admission snapshot, and is tested to be quarantined by the backend (Tasks 6, 7). Including the result is this plan's reading of "verified attempt metadata" (review M8). When that isn't possible, the build fails closed with diagnostics; nothing is fabricated and quarantine is never assumed.
- **D3, shared ledger vectors: approved.** `Backend/contracts/ledger-v1/` with successful and integrity-failure cases; the backend reconstructs the snapshot, pool and expected state and runs its own parser and validator over the same bytes Unity produces. The validator is never weakened (Task 7).
- **D4, receipts yes, retention no: approved.** The three receipt vectors are read; no retention or insurance selection in Unity; the retention salt never reaches the mission process (Task 3).
- **D5, a pollable `INetAdmission`: approved.** Abstraction only; refusal codes and network messages unchanged; no FishNet change (Task 10).
- **D6, durable Linux writes: approved, with the execution-model change.** The immutable snapshot is captured on the simulation thread; a dedicated I/O worker performs every file operation; progress is a non-blocking thread-safe status; Unity stays on the main thread. The exact `.tmp` → `.written` → `.ready` contract with file and directory fsyncs; partial writes, `EINTR`, failed `fsync`, rename failures, repeated requests, cancellation and crashes handled; only `$STATE_DIRECTORY`, checked, with no escape. The libc set is what the requirements need (nine functions), not five. Injected-filesystem tests, Linux native tests and a Stage 12 Mono obligation (Task 8). No synchronous fallback.
- **D7, consumables must stack: approved with historical compatibility.** A registration rule and an exporter rule; stored catalogues stay readable under their original rules, with a regression test; no data change (Task 9).
- **D8, Linux verification: approved.** `UnityCore.Linux.Tests` through an isolated `vm-test.sh --unity-core` mode (no network, no database, the existing lock and cleanup); not proof of Mono compatibility, which stays a Stage 12 check (Task 8).

The original decision texts (revision 1.1) are in this file's history at `2777623`.

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
| I1 a blocking publisher | `ResultPublisher` replaced by the step-wise `ResultPublication` (Begin / OnNotice / Tick), driven by the main thread (itself replaced in revision 2 by the I/O worker, D6) |
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

## Plan review (2026-10-19, independent reviewer, revision 2)

0 Critical, 3 Important, 9 Minor, all applied in revision 2.1 before implementation (the owner's condition: no unresolved Critical or Important finding). The reviewer confirmed the vector counts and refusal order, that any V-code quarantines (`SettlementStore`) and that revalidation re-runs V14, that a snapshot-restating fallback trips only V14, that every registration path goes through `RegisterAsync` while stored versions keep loading through `GameCatalogue.Load`, that the unit file (`StateDirectory`, `StateDirectoryMode=0755`, `UMask=0022`, `DynamicUser`) agrees with the location and open checks, the glibc minimums, and the Stage 13 correction.

| Finding | Change |
|---|---|
| I1 `$` anchors accept a trailing newline the backend's `\z` refuses | every pattern anchored with `\z`; predicates as explicit loops, no Regex; a trailing-`\n` refusal per predicate and in the builder test |
| I2 `--unity-core` can't restore offline | restore and build in the existing networked container without secrets, test with `--network none --no-restore --no-build`; one tree id for both subtrees, synced repo-relative |
| I3 `Dispose` could close the descriptor under the worker | only the worker closes the directory, as its last action; a test for it |
| M1 links and .NET Standard 2.1 APIs | `CanonicalIds.cs` linked; the replacements named; analyzer suppressions by name |
| M2 resubmit after a failed directory fsync | the meaning of `Durable` stated; at most one resubmit |
| M3 `.tmp` mode | `fstat` and `fchmod(0644)` after the write open |
| M4 libc details | other errnos are failures, a 0-byte read or write is a failure, `IntPtr` sizes, NUL-terminated names |
| M5 test threads | background thread; gates released in `TearDown`; a structural responsiveness check with a generous bound |
| M6 Linux test directory | on `/work`; `umask(0022)` set through libc |
| M7 D7 tests | a fresh item id and the message asserted; the already-registered limitation recorded |
| M8 fallback and ledger | the result's inclusion stated as this plan's reading; duplicate stacks refused; `ReportIntegrityFailure` after the freeze throws |
| M9 consistency | eight scenarios; `CLAUDE.md` status; the review table note; the runtime-state types listed; bounds wording; the TooLarge test; the gated level named |

## As built (2026-10-19, branch `phase13-stage11`; status since: see "Final owner rulings")

Implemented test-first, natively in one session, from `23a9c04` (plan revision 2.1) to the commit that records this section. One commit per task: Task 1 `36fe67c`, 2 `d50047b`, 3 `0430a23`, 4 `ba83a38`, 5 `b23feda`, 6 `6aec104`, 7 `46aaf5e`, 8 `cc174f7`, 9 `db90546`, 10 `e41da26`.

**Tests (all green):**

| Suite | Result |
|---|---|
| Unity EditMode (full, at `e41da26`) | **1835 / 1835** (baseline 1579 + 256 new) |
| Unity PlayMode (full, at `e41da26`) | **1434 / 1434** (exactly the baseline) |
| Backend domain (`Accounts.Domain.Tests`) | **787 / 787** (759 before + 28 new) |
| VM runner `--full` at `e41da26` | domain 786, agent 254, integration **1773**, 0 failed |
| VM runner `--unity-core` (`UnityCore.Linux.Tests`, real libc, `--network none`) | **8 / 8** |
| manifest-v1 golden vectors in Unity | all 13 valid and 20 invalid, byte for byte; the folder pinned by file set and hashes |
| ledger-v1 scenarios | 8 / 8 byte-identical in Unity; exactly the expected errors from the real `ManifestValidator` |

Evidence (local, not committed): `.claude/scratchpad/phase13/stage-11/runs/`.

**Rulings made during execution** (each in the ledger with its cost if wrong):
- Full Unity suites ran at a mid-point (1727/1727 after Task 5) and at the end, not after every task.
- Tasks 3 and 10 wrote their code before the red run (the compile failure was certain); Task 9's two integration tests were not run red first.
- The fallback test in Task 6 uses the builder tests' own header, not scenario 08's bytes; scenario 08 pins its own bytes in both runtimes.
- The write path opens without `O_TRUNC`, checks `fstat` (regular, one link, ours) and then calls `ftruncate`, so a hard-linked or foreign file is never truncated. This adds `ftruncate` to the libc set, which is now `open`, `openat`, `read`, `write`, `fsync`, `close`, `fstat`, `fchmod`, `ftruncate`, `renameat2` and `geteuid`.
- `UnityCore.Linux.Tests` also links `Json/StrictJsonReader.cs` and `JsonNode.cs`, because `OutboxLocation` counts UTF-8 bytes.

**Final independent review** (most capable model, whole branch): **0 Critical, 1 Important, 7 Minor.**

It confirmed:
- contract fidelity: the refusal order, entry member order, lone surrogates, the canonical writer, the ids and `LootPoolHash`;
- that the D2 fallback is truthful and a known mismatch can never come out as `ok`;
- Review Focus 1, 3, 4 and 5;
- the worker's thread safety;
- the outbox's filesystem and path safety;
- D7's historical compatibility, the scope, and the runner change.

- **I1, an accepted cross-stage dependency (owner ruling of 2026-10-20, option (a)); a hard Stage 12 acceptance gate (B25), not implemented or resolved in the current runtime:**
  - The problem: `LootContainer.Initialize` fills with `ItemInventory.Add`, which merges same-definition plan items, and the ledger takes pool lines from the entries after the fill. Two medkit plan items in one container (1 + 2) become one line of 3. The backend's ready-report rule (`LootPoolValidator`: each line's quantity inside a loot-table entry's range, 1..2 for the medkit) then refuses the pool, and the attempt is cancelled (`InvalidPool`). With the live catalogue this is plausible in a noticeable share of missions.
  - Why the tests missed it: the ledger-v1 scenarios avoided merged pools on purpose, and nothing ran the ready-report rule on a merged pool.
  - Now pinned by `LedgerScenarioTests.A_merged_container_line_is_refused_by_the_ready_report_rule`.
  - The ways to resolve it:
    - **(a) recommended:** Stage 12's attempt-mode fill adds one entry per plan item (the existing internal `ItemInventory.TryAddEntry(new ItemEntry(id, quantity))`, no merging), so a pool line is a plan item, as the backend expects, and the ledger is unchanged. The table's `maxQuantity` must not exceed the item's `maxStack`; the fill should refuse that case.
    - **(b)** The backend judges a container's per-definition totals instead of per-line quantities. This changes the ready-report contract.
    - **(c)** The ledger keeps plan lines and attributes merged entries to them by a fixed rule. This adds complexity to every collect.
- **Minor, deferred** (not fixed; for the owner):
  - **M1 (pre-existing, backend): fixed after the owner's authorization (`c32d462`).** `ResultManifest.Parse` called `Claims()` outside its `try`, and a root member name of at least 9 raw bytes holding a lone surrogate escape made `NameEquals` throw. Reproduced first (2 of 3 test names threw `InvalidOperationException`; the 6-byte name didn't, because `NameEquals` skips unescaping it). Now a failed comparison counts as "not this claim name" (such a name can't equal an ASCII one), so the claims are unchanged and `ReadRoot` refuses the body `NotJson`, as for the short name. `ResultManifestTests.A_lone_surrogate_in_a_member_name_is_NotJson_with_the_bodys_claims`.
  - **M2:** `BuildForPublication` lets a caller error escape as an `ArgumentException` instead of returning `Failed`: a header/facts account mismatch, or a dead id that isn't the participant's. It still writes no bytes; Stage 12 must catch it.
  - **M3:** the `.tmp` write open lacks `O_NONBLOCK`. A FIFO planted between the probe and the open, possible only by the same uid, would block the worker.
  - **M4:** the `--unity-core` build container shares the test cluster's network namespace, as every other mode does (no secrets are mounted). Only its test container has no network.
  - **M5:** `serve` reports a failed D7 registration rule with the exit code named `CatalogueIdentityChanged`; the message is correct.
  - **M6:** these docs. Done by this commit.
  - **M7:** `Cancel` is not sticky per participant. A later `Submit` of the same bytes starts a new job; Stage 12 decides.

**Known runtime limitations and Stage 12 obligations:**
- **Mono:** the outbox's libc calls and its worker thread are proven on CoreCLR on Linux only. Stage 12 must run them in Unity's Linux Mono server player on the mission VM (`-outbox-selftest`, with the owner's go-ahead) before any real result depends on them. **Done (2026-10-21 UTC):** the self-test ran once at `5551a00` under manifest revision 5. The owner accepted it with one documented module-baseline exception (see the Stage 12 self-test manifest's §9).
- **The fill (I1, B25):** per the owner's ruling below; Stage 12 can't be accepted without it.
- **The outbox directory:** open it with `OutboxDirectories.OpenForServer($STATE_DIRECTORY, "/var/lib/blackglass-outbox", attemptId)` at start, before the loop, and refuse to run without it. Read handles only from the main thread.
- **`BuildForPublication`:** a `Failed` outcome writes nothing and is reported. Catch an `ArgumentException` from it (M2).
- **Stage 13:** send `recorded` only once a handle is `Written`, then call `AllowPublication()` whatever the answer. The process never polls settlement.
- **B24:** remains blocking. No real Unity mission workload ran, and none may until the per-slot TCP bind restriction is demonstrated.

## Final owner rulings (2026-10-20) and the Stage 12 handoff

The owner conditionally accepted Stage 11 and ruled:

- **I1: option (a), assigned to Stage 12 as a mandatory, blocking integration requirement (spec §18.2 B25, recorded beside B24).** The Stage 11 ledger architecture is unchanged; the backend's `LootPoolValidator` contract is unchanged; the ledger never splits merged entries into artificial lines; offline stacking and the offline `LootContainer.Initialize` are unchanged. **I1 is not implemented in the current runtime:** today's only fill merges.
  - Stage 12 adds a dedicated online mission-container initialization path, `ItemInventory.TryAddEntry` or an equivalent non-merging operation.
  - **Invariants:**
    - one successful `LootItemPlan` roll is exactly one `ItemEntry`, and one entry exactly one registered `PoolLine`;
    - line indices are consecutive from 0 within each container;
    - the mapping from source entry identity to line is established before gameplay and never changes when another entry is collected or removed;
    - each line's definition and quantity are its roll's;
    - the ready-report pool and its hash are computed from those entries, and the real backend `LootPoolValidator` accepts the pool.
  - **Every roll must fit one entry:** a stackable roll's quantity is 1..`maxStack`; a non-stackable roll is exactly 1 item. Otherwise generation fails closed with an explicit error. Never merge, split, truncate or drop items to manufacture a valid ready report; never alter a registered catalogue version or weaken backend validation.
  - **The nine required Stage 12 tests**, with forced duplicate-roll fixtures, not only randomized ones:
    1. two medkit rolls (1 and 2) give two entries and two valid backend pool lines;
    2. a partial collect from one line and a complete collect from another keep their separate provenance;
    3. destination inventory merging doesn't change source-line accounting;
    4. loot-first, oldest-first consumption stays correct across those lines;
    5. invalid quantities are refused before ready;
    6. pool lines are stable after source inventory mutations;
    7. the registered SHA-256 loot hash equals the backend's;
    8. offline generation, loot presentation and deterministic hashes are unchanged;
    9. the real backend ready and settlement validators accept valid generated cases.
  - `LedgerScenarioTests.A_merged_container_line_is_refused_by_the_ready_report_rule` keeps asserting that the old merged representation is refused; its expectation never changes to make a suite green.
- **M1: a narrowly scoped, test-first backend fix, authorized and done** (see the final review list above).
- **Merge:** authorized once the full validation passes on the final tree, the diff is clean and no new Critical or Important finding appears. Preserved, never committed: the owner's `Assets/Settings/Mobile_RPAsset.asset`, `ProjectSettings/URPProjectSettings.asset` and `.claude/` scratch content.
- **Stage 12 gates carried forward:** B24 (the per-slot TCP bind restriction, demonstrated on `bg-dev-mission01`), B25 (I1), and the Linux Unity self-test of the outbox worker (owner approval of its host manifest before it runs). The self-test is **complete**: run and accepted on 2026-10-21. B24 and B25 remain open. No real Unity mission workload before B24.

## Final validation (2026-10-20)

Run on the final implementation tree (`4ba9633`; the later commits change docs only, and the VM runs name the Backend tree `a576e48`):

| Suite | Result |
|---|---|
| Unity EditMode | **1835 / 1835** |
| Unity PlayMode | **1434 / 1434** |
| Backend domain (local and VM) | **790 / 790** (787 + M1's 3), including I1's `A_merged_container_line_is_refused_by_the_ready_report_rule`, unchanged |
| Mission host agent (VM) | **254 / 254** |
| Integration (VM) | **1773 / 1773** |
| Linux native outbox (`--unity-core`) | **8 / 8** |
| manifest-v1 vectors | byte-identical (file set and hashes pinned in Unity; backend golden tests green) |
| ledger-v1 scenarios | 8 / 8 on both sides |

Independent focused review of the post-review changes (`ce8694a..4ba9633`): 0 Critical, 0 Important, 5 Minor (doc wording; fixed in `ef5c9ed` and this commit). Zero unexplained failures.

