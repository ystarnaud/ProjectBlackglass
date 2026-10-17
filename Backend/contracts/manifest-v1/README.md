# Result manifest format v1: golden vectors

One participant's result manifest, written by the mission server (Stage 12) and relayed by the host agent (Stage 10) to
`POST /agent/v1/results` (plan `Docs/superpowers/plans/2026-10-17-phase-13-stage9-settlement.md`, S9-B4, §G and §H).
**The vector files are the authority:** where this prose and a vector disagree, the vector wins. A change to any vector is a
format change and needs the owner's approval. `Backend/tools/manifest_vectors.py` writes every file here; it is an
independent implementation of the rules below and must leave the files unchanged when re-run.

Who must pass them:
- the backend's domain tests (`Backend/tests/Accounts.Domain.Tests/Settlement/GoldenVectorTests.cs`);
- Stage 11's Unity EditMode tests, byte for byte, read by path from this folder.

## Members

All members are required, and no others are allowed. Facts only: no XP, rewards, energy, insurance, retention choice or seed.

| Member | Value |
|---|---|
| `formatVersion` | `1` |
| `attemptId` | canonical lowercase UUIDv7 (`xxxxxxxx-xxxx-7xxx-[89ab]xxx-xxxxxxxxxxxx`) |
| `accountId` | canonical Base58 (no leading `1`, at most 11 characters) |
| `processSessionId` | canonical lowercase UUID |
| `buildRevision` | `^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$` |
| `catalogueVersion`, `lootHash` | `sha256:` and 64 lowercase hex digits |
| `layoutHash` | 16 lowercase hex digits |
| `integrity` | `ok` or `ledgerMismatch` |
| `outcome` | `success`, `defeat`, `abandoned`, `disconnected` or `expired` (`infra_void` is backend-only) |
| `outcomeSequence` | 0..2,147,483,647 |
| `serverElapsedMs` | 0..86,400,000 |
| `lootTableVersion` | 1..2,147,483,647 |
| `objectives` | 0..64 of `{objectiveId, state}`; `objectiveId` is a lowercase word (`[a-z0-9]+([._-][a-z0-9]+)*`, at most 64); `state` is `active`, `completed` or `failed` |
| `operatives` | 1..8 of the operative object below |

**Operative:** `{operativeId (Base58), slot (0..7), alive (boolean), consumed, held, collected, equipment}`, each list 0..256 entries.
- `consumed`: owned `{definitionId, quantity, source:"owned"}` or loot `{containerIndex, definitionId, line, quantity, source:"loot"}`.
- `held`: an owned item `{itemId, origin:"owned"}`, an owned stack `{definitionId, origin:"owned", quantity}` or loot `{containerIndex, line, origin:"loot", quantity}`.
- `collected`: `{containerIndex, line, quantity}`.
- `equipment`: an owned item `{itemId, slot}` or a held loot line `{containerIndex, line, slot}`; `slot` is `armor`, `utility` or `weapon`.

Bounds: `containerIndex` and `line` 0..65,535; quantities 1..2,147,483,647; strings at most 128 UTF-8 bytes; containers nested at
most 4 levels below the top-level object; the body at most 65,536 bytes. A definition id has the catalogue id form
(`[a-z0-9]+([.-][a-z0-9]+)*`, at most 64). Consumption is per definition, so a consumable must be stackable in format v1.

Duplicates are refused: one objective per id; one operative per slot and per id; per operative, one owned consumption per
definition, one loot consumption, held loot lot and collected entry per `(containerIndex, line)`, one held item per id, one
held stack per definition and one equipment entry per slot.

## Numbers

Integer literals only: `0` or a non-zero digit followed by digits. No sign (not even `-0`), fraction or exponent.

## Canonical form

A parseable body in any valid encoding is accepted and canonicalized. The canonical bytes are:
- UTF-8 without a BOM, no whitespace;
- object members in ordinal (code-unit) key order;
- strings with minimal escapes: only `"`, `\` and U+0000–U+001F are escaped (`\b \t \n \f \r`, else `\u00xx` in lowercase hex); everything else is raw UTF-8;
- lists sorted: `objectives` by `objectiveId`; `operatives` by `slot`; `collected` by `(containerIndex, line)`; `equipment` by `slot`;
  `consumed` loot entries first by `(containerIndex, line)`, then owned entries by `definitionId`; `held` loot first by
  `(containerIndex, line)`, then owned items by numeric id, then owned stacks by `definitionId`.

`manifestHash` is `sha256:` and the lowercase hex SHA-256 of the canonical bytes. The process sends it to
`…/participants/{acc}/recorded`.

## Refusal codes (422)

The first refusal met, in this order: size (`TooLarge`), UTF-8 and JSON syntax including leading zeros and trailing commas
(`NotJson`), nesting (`TooDeep`), then the structure: per object, duplicate keys (`DuplicateKey`), unknown members
(`UnknownMember`), missing members (`MissingMember`); `formatVersion` first (`UnsupportedFormat`), then each member in ordinal key
order. Per value: the JSON kind (`WrongType`), the integer literal form (`NotInteger`), ranges, lengths and list sizes
(`OutOfBounds`), id and hash forms (`NonCanonicalId`), other patterns (`BadValue`), enumerations (`BadEnum`), `infra_void`
(`BackendOnlyOutcome`), and duplicates within a list (`DuplicateEntry`).

## Files

- `valid/NN-name.input.json`, `.canonical.json` (the exact canonical bytes) and `.hash.txt` (the `sha256:` line).
  `10-reverse-lists`, `11-whitespace` and `12-key-order` encode `02-full` differently and share its canonical bytes and hash.
- `invalid/NN-name.input.json` and `.code.txt`: one or more vectors per refusal code except `TooLarge` (a 64 KiB file is not
  kept here; the domain tests build one).
- `retention/NN.json`: a salt, an account id, policy terms and candidate loot lines, with the expected ranked selection
  (S9-B12.3: category priority, then `SHA-256(salt ‖ accountId as 8 bytes big-endian ‖ containerIndex as 4 bytes ‖ line as 4 bytes)`
  compared bytewise, then `(containerIndex, line)`; the first `maxEntries`, each capped at `maxUnitsPerEntry` units).
- `receipt/NN.input.json` and `.receipt.json`: receipt v1's fields and its exact bytes (canonical JSON; lists sorted by numeric
  operative id, then definition id; item ids ascending; `settledAt` in UTC with six fraction digits).
