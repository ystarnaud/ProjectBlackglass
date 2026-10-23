# process-v1: the mission process's backend bodies (Stage 13)

Golden vectors for the `/server/v1/attempts/{aid}/…` routes (`Backend/src/Accounts.Api/Hosts/ProcessEndpoints.cs`). Each file is one JSON value in UTF-8, with no BOM and no trailing newline. They are changed only together with both runtimes' tests.

| Files | Who writes the bytes | Checked by |
|---|---|---|
| `*.request.json` | Unity's mission process (`ProcessApi`, `JsonUtility` over the Stage 11 DTOs) | Unity's `ProcessContractVectorTests` (byte for byte), and the backend's own strict parsers in `Accounts.Domain.Tests/Attempts/ProcessRequestVectorTests` |
| `*.response.json` | The API's own response records with its own options (`JsonSerializerDefaults.Web`) | `Accounts.Integration.Tests/Unit/ProcessResponseVectorTests` (byte for byte, no database), and Unity's `ProcessContractVectorTests` (parsed values for register, ready, redeem, start and recorded; Unity never reads the heartbeat and failed answers' bodies) |
| `register.response.shuffled.json` | The register answer with the loadout's keys in another order, as PostgreSQL `jsonb` may return them | Unity only: key order is not pinned |
| `timestamps.json` | Backend timestamps with 0, 3, 5, 6 and 7 fractional digits (System.Text.Json trims trailing zeros of PostgreSQL microseconds), and shapes that must be refused | The backend: every valid shape is one its serializer writes; Unity: each valid shape parses to its exact instant and each invalid one is refused |
| `problem.409.json` | Hand-written in ASP.NET Core's ProblemDetails shape with the `code` extension (`Results.Problem`); compared with the real API only in Part B | Unity (`code` parsing) |

The ready request is not repeated here: it is `attempt-v1/03-seeded-collect-and-use.ready.json`, already judged by the backend's ready path (`AttemptFixtureTests`). The values (session, account `JG`, operatives `C` and `D`, the catalogue, the register loadout) are fixture 03's, generated once from it; the join ticket is a well-formed `jt1_` value of zero bytes, never a real one.
