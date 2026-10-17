# Project Development Directives

## Project Goal

This project is a 3D single-player tactical/strategy game being developed in Unity.

The intended gameplay direction combines:

- Real-time character/unit movement and combat.
- The ability to pause or heavily slow gameplay for tactical decision-making.
- Tactical controls inspired by systems such as Fallout's pause-and-command concepts, without copying Fallout-specific mechanics or IP.
- The possibility of supporting a more traditional real-time strategy/control style for players who prefer continuous real-time gameplay.
- Exploration, characters/NPCs, buildings, equipment/items, combat, and world interaction.
- A system-oriented architecture that can expand considerably later without requiring the initial prototype to implement the entire game.

This is an original game. Do not copy copyrighted characters, dialogue, artwork, names, maps, lore, code, or other protected assets from existing games.

## Current Development Stage

Gameplay is complete through Phase 12.5 (combat presentation and VFX). The control loop, procedural missions, objectives, battlefield intelligence, persistent operatives, inventory and loot, the tactical HUD and combat presentation exist. `Docs/Decisions.md` records each decision.

The owner is building the online foundation with an authoritative dedicated server; see `Docs/OnlineReadinessAudit.md`. For Phase 13 the backend stack is decided (decision 048): ASP.NET Core on .NET 10, PostgreSQL, Npgsql and Dapper, Docker Compose on `bg-dev-services01`. FishNet and the Unity Linux dedicated server are in use. HAProxy, a private VLAN, Docker for mission servers and orchestration remain **provisional, not decided**.

The step currently authorized is **Phase 13: authenticated between-mission flow and trusted settlement**, following `Docs/superpowers/specs/2026-10-14-phase-13-backend-design.md` and the staged plan `Docs/superpowers/plans/2026-10-14-phase-13-backend.md`, one stage at a time, each stage started only when the owner says so. Decisions 048-050 record the design.

Progress:
- Stage 0 (decision records) and Stage 1 (the backend foundation: migrator, loopback-only Compose stack, backup and restore drill) are done, merged into `main` and pushed (`ad14373`).
- Stage 2 (catalogue export) is done, per `Docs/superpowers/plans/2026-10-14-phase-13-stage2-catalogue-export.md`. Unity exports `Backend/catalogue/catalogue.json` (`Blackglass > Backend > Export Catalogue`; an EditMode test fails on a stale export). The backend's `Accounts.Domain` validates it, and `serve` refuses to start on an invalid one (exit 4).
- Stage 3 (accounts and development authentication, plus the 2026-10-15 security corrections; schema version 2) is done, merged into `main` and pushed (`fc823e8`).
- Stage 4 (persistent operatives, starter inventory, provisioning at account creation, advancement picks; schema version 4) is done, merged into `main` and pushed (`f3cada4`), per `Docs/superpowers/plans/2026-10-15-phase-13-stage4-operatives-inventory.md` (revision 2; owner rulings D1–D6, decision 048 amendment 2). It touches Unity only for `InventoryRules`, the catalogue export (format 2) and EditMode tests, never a scene. The admin CLI loads the catalogue only for `account create` and `starter backfill`, so credential and session revocation, suspension, reactivation and rotation work even with a bad catalogue. The live accounts stack is not deployed.
- Stage 5 (persistent inventory operations: one atomic, version-checked batch endpoint; migration `0005_inventory_edits`) is done, merged into `main` and pushed (`62cbfcf`; authorized 2026-10-16, owner rulings S5-D1–S5-D8, spec revision 2.5; schema version 5; not deployed), per `Docs/superpowers/plans/2026-10-15-phase-13-stage5-inventory-operations.md` (revision 2). It adds one Unity EditMode test file and changes no Unity runtime code: offline partial transfers stay as they are. Since Stage 7, `InventoryLocked` is a mandatory, fail-closed, database-backed check against active participations (`ParticipationLock`) for inventory mutations, advancement picks and reads; the always-unlocked fallback is deleted (the standing obligation is fulfilled).
- Stage 6 (persistent energy accounting; migration `0006_energy`) is done, approved by the owner and merged into `main` and pushed (2026-10-17; authorized 2026-10-16, owner rulings E1–E7, spec revision 2.6, plus the grant-replay correction; schema version 6; the live stack is not deployed), per `Docs/superpowers/plans/2026-10-16-phase-13-stage6-energy.md` (revision 2). Backend only; no admission, reservations, charges, refunds or insurance. Stage 7 must keep one global unique key per transaction after the energy policy lock and never retry energy transactions automatically.
- Stage 7 (insurance, mission participants and admission; migration `0007_attempts_and_insurance`) is done, approved by the owner, merged into `main` and pushed (`71827c7`, 2026-10-17; owner rulings S7-D1–S7-D9, spec revision 2.7; schema version 7; the live stack is not deployed), per `Docs/superpowers/plans/2026-10-17-phase-13-stage7-admission.md` (revision 2). Backend only: energy is reserved at deployment and released before start; the start charge, claims, tickets and process sessions are Stage 8's (plan: `Docs/superpowers/plans/2026-10-17-phase-13-stage8-hosts-start.md`, awaiting review).
- Item ids, operative GUIDs, progression choice ids and the role and ability ids are permanent backend-facing keys: never rename one.
- Account ids are a PostgreSQL-generated sequential `BIGINT` (decision 048 amendment, superseding UUIDv7). Outside the database they are canonical Base58 JSON strings, converted at one boundary; C# uses `long`. An account id is public, never an authorization secret: client requests take the account from the session only. Mission attempt ids stay UUIDv7.
- Owned operatives and non-stackable items use the same scheme (048 amendment 2): `bigint` identities, Base58 outside, minted exactly once through a unique `(origin, origin_ref)`. Catalogue definition ids are separate and unchanged; stacks have no ids.

Phase 13 authorizes:
- the account and mission backend in `Backend/`, loopback-only, reached through SSH tunnels;
- the mission host agent and on-demand systemd mission units on `bg-dev-mission01`, under unprivileged users;
- server-side reconnect (solo pause, resume tokens, re-admission by ticket) and backend settlement;
- authoritative abilities, items and loot on the Linux mission server;
- one separate minimal online client entry scene (login, loadout through the API, deploy, ticket join, play with the existing network client view and controls, pending result, then the receipt).

Each of these stays out of scope until the owner authorizes it:
- changes to the offline client scenes (`ProceduralMission`, `Prototype`) or their input, HUD and gameplay;
- production authentication providers, purchases, stores and payment processors;
- public exposure, HAProxy, a private VLAN, Docker for mission servers, prewarming and orchestration frameworks;
- co-op attempts with more than one participant;
- layout streaming and geometry secrecy;
- final production content (graphics, animation, audio, story).

Placeholder art remains the default.

## Development Philosophy

Favor:

- simple systems;
- modular architecture;
- readable C#;
- composition over large inheritance hierarchies;
- explicit interfaces where they provide meaningful separation;
- data-driven configuration where appropriate;
- Unity-supported systems rather than unnecessary custom frameworks;
- maintainability by a small development team;
- systems that can be understood and modified by a developer working with AI assistance.

Avoid:

- premature optimization;
- speculative abstractions;
- giant manager classes;
- global mutable state;
- excessive singleton usage;
- unnecessary third-party packages;
- implementing systems merely because they might someday be useful;
- building large frameworks before they are required;
- silently changing architectural decisions.

## Architecture

Keep game-domain logic separate where practical from:

- input handling;
- presentation/visual effects;
- UI;
- Unity scene plumbing.

Systems should communicate through clear APIs rather than directly reaching into unrelated components.

Prefer small, focused components.

Do not create an elaborate dependency-injection framework or ECS architecture unless there is a demonstrated reason later.

Unity's normal GameObject/component model is acceptable for the prototype.

Use ScriptableObjects when they provide clear value for configurable game data, but do not convert everything into ScriptableObjects.

## Input

Use Unity's current Input System rather than the legacy Input Manager.

Input actions should be centralized enough that keyboard/mouse controls can later be rebound or supplemented with other control schemes.

Do not place game rules directly inside input callbacks.

Input should request actions from gameplay systems.

## Tactical Pause

Treat tactical pause as a first-class gameplay system rather than simply attaching random `Time.timeScale = 0` calls throughout the codebase.

The implementation should eventually allow us to distinguish between:

- simulation/gameplay time;
- UI/input that remains active while paused;
- camera controls that may remain available;
- effects or systems that should use unscaled time.

The prototype can initially use Unity time scaling, but wrap this behavior behind an explicit game-time/tactical-pause service so it can evolve later.

## Unit Commands

Design movement and combat so that commands are conceptually separate from the physical unit implementation.

For example, a unit may receive commands such as:

- move;
- attack;
- stop;
- interact.

Do not hard-code mouse clicks directly into movement/combat components.

This separation is important because later we may support:

- direct real-time controls;
- tactical paused commands;
- queued commands;
- RTS-style controls;
- AI issuing the same kinds of commands.

Do not implement all of those features yet. Only make the initial architecture compatible with them.

## Assets

For now:

- use primitive Unity geometry;
- use freely available Unity-provided assets only if necessary;
- do not spend time sourcing polished art;
- do not commit large generated assets without discussing them first.

Later, the art workflow may include AI-assisted concept art, AI-assisted 3D mesh generation, Blender cleanup, topology work, rigging, animation, and final Unity import.

That pipeline is outside the current prototype scope.

## Source Control

The project is maintained in Git and GitHub.

Before making broad changes:

1. inspect the existing repository;
2. understand the current structure;
3. state what you intend to change;
4. keep changes logically grouped.

Do not delete or substantially rewrite working systems merely to impose a preferred architecture without first explaining why.

Do not commit:

- Unity Library/
- Temp/
- Logs/
- Obj/
- generated build output;
- editor-specific temporary files;
- secrets or API keys.

Ensure an appropriate Unity `.gitignore` exists.

Use Git LFS later for large binary assets if needed, but do not introduce it unnecessarily during the code-only prototype.

## Working With the Owner

The owner is using AI-assisted development but retains architectural control.

When a requirement is ambiguous:

- identify the ambiguity;
- choose the simplest reasonable interpretation when it is safe to do so;
- clearly state any consequential assumption.

Do not bury important design decisions inside implementation work.

Before introducing a major dependency, package, architecture, persistent-data format, or project-wide pattern, explain the tradeoff first.

Minor implementation decisions do not require permission.

## Task Discipline

Work incrementally.

For each development task:

1. inspect the existing implementation;
2. identify the smallest useful change;
3. implement it;
4. compile/check for obvious errors;
5. report exactly what changed;
6. note any remaining issue or architectural concern.

Do not modify unrelated code.

Do not perform large speculative refactors while completing a narrow task.

When practical, keep the project runnable after each meaningful change.

## Documentation

Maintain concise documentation for architectural decisions that are likely to matter later.

If we make a significant architectural decision, record:

- what was decided;
- why;
- important alternatives rejected;
- implications for future development.

Do not generate extensive documentation for trivial implementation details.

## Current Priority

Phase 13, stage by stage (plan revision 2.1).
- Stage 0 (decision records and this authorization) and Stage 1 (backend foundation) are done, merged and pushed.
- Stage 2 (catalogue export) is done.
- Stage 3 (accounts and development authentication) is done, merged and pushed.
- Stage 4 is done, merged and pushed (`f3cada4`; live stack not deployed).
- Stage 5 is done, merged and pushed (`62cbfcf`; live stack not deployed).
- Stage 6 is done, merged and pushed (live stack not deployed).
- Stage 7 (insurance, mission participants and admission; migration `0007_attempts_and_insurance`; schema version 7) is done, approved by the owner, merged into `main` and pushed (`71827c7`, 2026-10-17; live stack not deployed), per `Docs/superpowers/plans/2026-10-17-phase-13-stage7-admission.md` (revision 2). Energy is reserved at deployment; the start charge is Stage 8's.
- Stage 8 (hosts, claim, process sessions, tickets and the start commit; migration `0008_hosts_sessions_tickets`) has a detailed plan awaiting the owner's review: `Docs/superpowers/plans/2026-10-17-phase-13-stage8-hosts-start.md` (revision 1, with decisions B1–B13 and the test-runner proposal). Implementation starts only when the owner approves it.
- No merge, push or live accounts-stack deployment happens without the owner's approval.

- Each stage records concrete evidence (test counts, transcripts, diff stat) and changes only the paths it lists.
- Keep visibility policy and the backend core free of networking-library types.
- Keep the offline client scenes and their gameplay unchanged.
- Every energy, insurance, XP, retention and timer value is provisional configuration.

Next step, authorized separately: Phase 14 (server persistence hardening and recovery).

Do not begin later-stage systems until specifically directed.
