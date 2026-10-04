# Architecture Decisions

Short record of decisions that are likely to matter later. Newest last.

## 001 — Engine version: Unity 6.3 LTS (6000.3.25f1)

- **Decided:** Create the project with Unity 6.3 LTS, 6000.3.25f1 (installed via Unity Hub, 2026-10-03).
- **Why:** Current LTS stream, supported until December 2027. Input System and URP development is focused on Unity 6.
- **Rejected:** 2022.3.46f1 (already installed, but its free LTS support has ended; we would have to upgrade soon). Unity 6.0 LTS (support ends October 2026). 6000.6 (newer, but not an LTS release).
- **Implications:** Everyone working on the project needs exactly this Editor version (see `ProjectSettings/ProjectVersion.txt`). Upgrade only deliberately, as its own change.
- **Known issue:** Unity 6 requires Windows 10 21H1 (build 19043) or newer. The current development machine runs Windows 10 2004 (build 19041). The Editor works but prints an "unsupported OS" warning at every launch. Built Windows players ran here without any warning. Updating to Windows 10 22H2 meets the minimum. Windows 10 itself has been out of mainstream support since October 2025, so Windows 11 is the longer-term option.

## 002 — Render pipeline: URP

- **Decided:** Universal Render Pipeline, set up from Unity's "Universal 3D" project template.
- **Why:** Unity's default for new 3D projects. The Built-in pipeline is being deprecated, so starting there would mean migrating materials and lighting later.
- **Rejected:** Built-in (deprecated); HDRP (heavier and aimed at high-end visuals we don't need).
- **Implications:** Use URP shaders (`Universal Render Pipeline/Lit`) for placeholder materials. Pipeline assets live in `Assets/Settings/`, where the template put them.

## 003 — Repository and folder layout

- **Decided:** The Unity project sits at the repository root. All of our own assets go under `Assets/_Project/`. Folders are created only when something goes in them.
- **Why:** The root layout is the Unity convention and matches the standard Unity `.gitignore`. One project folder keeps our content separate from Unity-generated and third-party folders (`Settings/`, `Plugins/`, imported packages). Empty folders cause churn: Git doesn't track them, and Unity deletes their orphaned `.meta` files.
- **Rejected:** A `UnityProject/` subfolder (only useful if the repo will hold non-Unity code). A large pre-made folder tree (speculative).
- **Implications:** New top-level categories (for example `Scripts/`, `Prefabs/`, `Materials/`) are added under `Assets/_Project/` when the first real asset needs them.

## 004 — Line endings

- **Decided:** `* text=auto` for everything. On top of that, the formats Unity writes as text (`.unity`, `.prefab`, `.asset`, `.meta`, `.inputactions`, `.asmdef`, `.json`, …) get `eol=lf`, so they stay LF in the working tree too. Those rules set only `eol` and never force `text`.
- **Why:** This machine's Git has `core.autocrlf=true`, and Unity writes LF. Without the rules, these files check out as CRLF, and once Unity re-saves them they show as modified even though nothing changed. `text` is not forced because some `.asset` files are always binary (LightingData, NavMesh, TerrainData). Forcing `text` would make Git rewrite CRLF bytes inside them and corrupt them on commit. That was reproduced in a test repo.
- **Implications:** C# scripts and other hand-edited files follow each machine's `autocrlf` setting (CRLF on Windows). When a new Unity text format appears, add an `eol=lf` line for it. Git LFS is not used yet. Add it deliberately when large binary art arrives.

## 005 — Package baseline

- **Decided:** Keep the URP template's packages that serve the prototype: URP, Input System, Test Framework, uGUI, and the Rider/Visual Studio integrations. Remove the template extras: Timeline, Visual Scripting, Unity Version Control (`collab-proxy`), Multiplayer Center, AI Navigation. Built-in engine modules stay at Unity's defaults. Versions match what Unity 6.3 recommends.
- **Why:** `CLAUDE.md` asks for no unnecessary packages. Each removed package can be added back with one line in `Packages/manifest.json`. Pruning built-in modules would save little and cause confusing missing-type errors later.
- **Implications:** The unit-movement task should decide explicitly whether to use NavMesh. If it does, re-add `com.unity.ai.navigation`.
- **Input:** The project uses the Input System package only. The legacy Input Manager is disabled (Player Settings → Active Input Handling). `Assets/InputSystem_Actions.inputactions` is Unity's generic template action map, registered as the project-wide actions asset. It should be replaced by our own tactical action map when input work starts.
