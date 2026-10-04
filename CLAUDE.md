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

We are at the very beginning of development.

The immediate objective is NOT to build the full game.

The objective is to establish a clean Unity project and create a very small vertical gameplay prototype proving the fundamental control loop.

Initial prototype:

1. Load a simple test scene.
2. Spawn one controllable player/unit.
3. Allow basic camera movement.
4. Allow movement of the controlled unit.
5. Create one simple target/enemy.
6. Allow a basic attack interaction.
7. Allow gameplay to be paused/unpaused through the tactical-control system.
8. Verify that commands can be issued cleanly without tying game logic directly to UI or input implementation.

Use placeholder geometry and placeholder materials only.

Do not spend time on final graphics, animations, audio, story, procedural generation, inventory depth, skill trees, multiplayer, networking, or production content yet.

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

The current priority is establishing the smallest playable prototype that proves:

Player Input
→ Command
→ Unit
→ Movement / Combat
→ Tactical Pause
→ Resume

Once that loop is working cleanly, further systems will be designed one at a time.

Do not begin later-stage game systems until specifically directed.