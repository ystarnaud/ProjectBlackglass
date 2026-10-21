# Attempt fixtures v1

Attempts Unity actually generates and plays, judged by the real backend (Stage 12 Task 7; gate B25, the owner's test 9).
Written by Unity, never by hand: **Blackglass > Backend > Export Attempt Fixtures**, or
`Tools/run-method.sh Blackglass.BackendTools.AttemptFixtureExport.ExportFromCommandLine` with the Editor closed.
`AttemptFixtureExportTests` (Unity EditMode) fails while these files differ from a fresh export, and
`AttemptFixturePlayTests` (PlayMode) checks that each seeded fixture's ready report is, byte for byte, what the real
`MissionDirector.GenerateForAttempt` produces for that seed. `.gitattributes` keeps the bytes exact.

**The backend is the judge.** `Backend/tests/Accounts.Domain.Tests/Settlement/AttemptFixtureTests.cs` runs the ready
endpoint's checks in its order over each ready report (`ReadyReport.Parse`, `ObjectiveList.Validate`,
`LootPoolValidator.Validate` against `Backend/catalogue/catalogue.json`'s table, the canonical loot hash), then
`ResultManifest.Parse` and the real `ManifestValidator` over the manifest, and must get exactly the fixture's expected errors.
A disagreement is investigated in Unity's generation or the backend's rules; a validator is never adjusted to a fixture and a
file is never edited to pass.

## Files

Per fixture `NN-name`:
- `NN-name.json`: the description (canonical JSON): `name`, `covers`, `attemptId`, `processSessionId`, `buildRevision`,
  `catalogueVersion`, `seed` (null for a forced fixture), `snapshot` (the admission snapshot in `LoadoutSnapshot.ToJson`'s
  shape), `events` (what the play did: `collect`, `use`, `equip`, `rogueAdd`, with the quantities Unity really moved),
  `readyFile`, `manifestFile` (null when the ready report is refused: no attempt starts), `expectedReadyErrors`,
  `expectedManifestErrors`.
- `NN-name.ready.json`: the ready report exactly as `JsonUtility.ToJson(ReadyRequest)` writes it (the bytes Stage 13 sends).
- `NN-name.manifest.json`: the format-v1 manifest Unity built at the freeze (canonical bytes).

## How a fixture is made

Account `JG`, two operatives (C: Kestrel, marksman rifle `2k` equipped, vest `2n`, medkit ×2; D: Darius, service rifle
`2m` equipped, medkit ×1). The real pieces run: `AttemptStart` over a register answer, the server fixture's generation steps
(`ServerFixture.Settings()`, the squad size, `MissionGenerator`, `ObjectivePlacer`, `SecurityPlacer`, `LootPlacer` with the
shipped loot table), the one-entry-per-roll fill (`LootContainer.InitializeForAttempt`), the snapshot inventory, Unity's item
moves reported to an `AttemptSession`, the freeze and `ManifestBuilder`. Forced fixtures replace only the containers.

| Fixture | What it covers |
|---|---|
| 01 forced duplicate medkits | B25 test 1: medkit ×1 and ×2 in one container are two lines; both accepted |
| 02 forced mixed | stackable and non-stackable rolls in three containers; collected equipment equipped |
| 03 seeded (seed 1) | the server fixture, whose container 1 naturally holds two medkit lines; collect, use, equip |
| 04 seeded (seed 2) | defeat, a dead operative holding loot |
| 05 seeded (seed 3) | owned items only |
| 06 seeded (seed 4) | a bag change no event reported: `ledgerMismatch`, quarantined (`V14.integrity`) |
| 07 forced partial and complete | B25 test 2: a partial and a complete collect keep separate provenance |
| 91 invalid | fixture 01's pool merged into one line of 3 (the I1 failure): refused |
| 92 invalid | a medkit line of 4 (over the stack and the table): refused; Unity fails such a generation instead |
| 93 invalid | fixture 01's pool with another pool's hash: refused |
| 94 invalid | fixture 07's manifest claiming two units of a one-unit line: `V9.pool_exceeded` |
