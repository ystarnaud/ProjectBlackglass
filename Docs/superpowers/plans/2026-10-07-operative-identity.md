# Phase 10: Persistent operative identity and shallow advancement: implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Squad members become persistent operatives (stable id, immutable definition, in-memory persistent state with XP, ranks and one modifier choice per rank) whose effective configuration is applied to disposable mission units.

**Architecture:** `OperativeDefinition`/`OperativeRole`/`ProgressionTrack`/`AdvancementChoice` are immutable ScriptableObjects. `PersistentOperativeState` is a plain serializable class owned by a scene-level `SquadRoster`. A pure `EffectiveConfiguration.Evaluate` combines definition + role bonus + picked modifiers. `MissionSpawner` spawns from the roster (when `MissionSystems.roster` is set) and `UnitIdentity` applies the configuration to the existing `Health`/`UnitMover`/`UnitAttacker`/`UnitAbilities` through small setters. Mission Success awards XP via a new `MissionDirector.MissionFinished` event.

**Tech Stack:** Unity 6000.3.25f1, C#, Unity Input System, NUnit (EditMode + PlayMode), IMGUI for debug UI. No new packages.

**Spec:** `Docs/superpowers/specs/2026-10-07-operative-identity-design.md` (read it first; read `CLAUDE.md` and `Docs/Decisions.md` entries 006, 013, 025, 026, 033, 034, 035 for context).

## Global Constraints

- One namespace `Blackglass` for game code (assembly `Blackglass`, folder `Assets/_Project/Scripts/`), `Blackglass.Tests` for tests, `Blackglass.EditorTools` for editor tools. 4-space indent, Allman braces, `internal` members are reachable from both test assemblies (`AssemblyInfo.cs`).
- New game code goes in `Assets/_Project/Scripts/Operatives/`; authored data in `Assets/_Project/Data/Operatives/`. Every new file is committed together with the `.meta` file Unity generates for it (and the `.meta` of a new folder).
- Never use `??`, `?.` or `??=` on a `UnityEngine.Object` (a missing component is Unity's fake null); use `TryGetComponent` or `== null`. Plain C# objects (`RosterMember`, `PersistentOperativeState`) may use them.
- Shared ScriptableObject assets are immutable at runtime: nothing in this plan writes to a definition, role, choice, track, archetype or ability asset.
- No game rules in input callbacks; debug input uses the `Developer` action map (keyboard only, like F6-F8). No device-specific code in progression (the `NoDeviceTypesInGameplayTests` guard stays green).
- No inventory, loot, currency, campaign, save-to-disk, permadeath, injuries, kill XP, packages. Do not begin Phase 11.
- Progression is never read by the mission generator and never touches `SeededRandom` or `UnityEngine.Random`.
- Mission-completion XP goes to every roster member on Success only; runtime health is never persisted (each mission spawns every operative at effective max).
- Commit trailer: `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>` (a subagent's own truthful trailer is acceptable). Never `git add` `.claude/`; add explicit paths only. The working tree already contains unrelated modified `Assets/Settings/Mobile_RPAsset.asset` and `ProjectSettings/URPProjectSettings.asset`: leave them alone.
- Unity allows one Editor instance per project: run test commands one at a time, and close nothing else needed. `Tools/run-tests.sh EditMode|PlayMode ["filter"]` returns EXIT=0 all passed, 1 compile error, 2 test failures. The filter is an NUnit full-name filter, for example `Blackglass.Tests.ProgressionTrackTests`.
- Measured baselines on `main` (2026-10-07): EditMode **693/693**, PlayMode **701/701**. Test counts added by each task are listed; a task report must state the actual totals.

## Review Focus

1. **XP edge values.** Zero, negative and `int.MaxValue` XP; an award that would overflow must clamp, not wrap negative (Task 2).
2. **Stale choice ids.** A persisted pick whose choice no longer exists in the track must be skipped, not throw, and pending picks must never go negative if the track later has fewer ranks (Tasks 2, 3).
3. **Max health lowered below damage taken.** `Health.SetMax` must never leave a living unit at 0 hit points or revive the dead (Task 5).
4. **`UnitAttacker.Awake` re-applies the archetype** when a spawned object activates; that would silently erase the effective damage/range/interval unless the override is remembered (Tasks 5, 6).
5. **A unit that died or was destroyed still hears roster changes** (XP awarded after death, regeneration teardown) and must neither throw nor change a corpse; duplicate or blank operative ids in the starting squad must be refused loudly, not shared (Tasks 4, 6, 7).

---

## File map

| File | Status | Responsibility |
|---|---|---|
| `Scripts/Operatives/StatModifiers.cs` | create | Five-field modifier struct, `Combine` |
| `Scripts/Operatives/OperativeRole.cs` | create | Role SO: name, description, `StatModifiers` bonus |
| `Scripts/Operatives/AdvancementChoice.cs` | create | Choice SO: stable id, name, description, modifiers |
| `Scripts/Operatives/ProgressionTrack.cs` | create | Thresholds, choices, mission XP, debug XP step; rank maths |
| `Scripts/Operatives/PersistentOperativeState.cs` | create | Serializable id/XP/pick-ids; derived rank and pending picks |
| `Scripts/Operatives/OperativeDefinition.cs` | create | Definition SO: id, name, role, unit prefab, base numbers, archetype, abilities, validation |
| `Scripts/Operatives/EffectiveConfiguration.cs` | create | Pure evaluation: base + role + picks |
| `Scripts/Operatives/SquadRoster.cs` | create | Scene component owning `RosterMember`s; XP awards; events; mission-finished handling |
| `Scripts/Operatives/UnitIdentity.cs` | create | Runtime link unit <-> operative; applies/re-applies configuration |
| `Scripts/Operatives/OperativePanelView.cs` | create | IMGUI debug panel and pure text builders |
| `Scripts/Operatives/OperativeDeveloperInput.cs` | create | F9/F10/F11 developer actions |
| `Scripts/Combat/Health.cs` | modify | `SetMax` |
| `Scripts/Units/UnitMover.cs` | modify | `Speed`, `SetSpeed` |
| `Scripts/Units/UnitAttacker.cs` | modify | `ApplyEffective`; Awake respects it |
| `Scripts/Abilities/UnitAbilities.cs` | modify | power/cooldown multipliers at the three read sites |
| `Scripts/Mission/MissionSlots.cs` | modify | `MissionSystems.roster` |
| `Scripts/Mission/MissionSpawner.cs` | modify | spawn from the roster |
| `Scripts/Mission/MissionDirector.cs` | modify | friendly count from roster; `MissionFinished` event |
| `Scripts/DebugUI/PrototypeHud.cs` | modify | operative name/role in unit labels |
| `Input/BlackglassControls.inputactions` | modify | three Developer actions |
| `Editor/OperativeDataBuilder.cs` | create | Creates the prototype data assets and wires the scene |
| `Data/Operatives/**` | create (via builder) | 3 roles, 4 choices, track, 3 definitions |
| `Scenes/ProceduralMission.unity` | modify (via builder) | roster, panel, developer input, `systems.roster` |
| `Tests/**` | create/modify | listed per task |
| `Docs/Decisions.md` | modify | decision 036 |

Paths under `Scripts/`, `Data/`, `Input/`, `Editor/`, `Scenes/`, `Tests/` are relative to `Assets/_Project/`.

---

### Task 1: Modifiers, role, choice and progression track

**Files:**
- Create: `Scripts/Operatives/StatModifiers.cs`, `Scripts/Operatives/OperativeRole.cs`, `Scripts/Operatives/AdvancementChoice.cs`, `Scripts/Operatives/ProgressionTrack.cs`
- Test: `Tests/EditMode/StatModifiersTests.cs`, `Tests/EditMode/ProgressionTrackTests.cs`

**Interfaces:**
- Produces:
  - `struct StatModifiers { int maxHealth; float moveSpeed; float attackDamage; float abilityPower; float abilityCooldownReduction; static StatModifiers Combine(StatModifiers a, StatModifiers b); }`
  - `OperativeRole : ScriptableObject` with `string DisplayName`, `string Description`, `StatModifiers Bonus`, `internal static OperativeRole Create(string displayName, string description, StatModifiers bonus)`.
  - `AdvancementChoice : ScriptableObject` with `string Id`, `string DisplayName`, `string Description`, `StatModifiers Modifiers`, `internal static AdvancementChoice Create(string id, string displayName, string description, StatModifiers modifiers)` (throws `ArgumentException` for a blank id).
  - `ProgressionTrack : ScriptableObject` with `int MaxRank`, `int RankFor(int xp)`, `int XpForRank(int rank)`, `bool TryGetNextThreshold(int xp, out int threshold)`, `AdvancementChoice FindChoice(string id)`, `IReadOnlyList<AdvancementChoice> Choices`, `int MissionCompletionXp`, `int DebugXpStep`, `internal static ProgressionTrack Create(int[] thresholds, AdvancementChoice[] choices, int missionCompletionXp, int debugXpStep)`.
- Serialized field names (the data builder in Task 9 sets them by name): `OperativeRole`: `displayName`, `description`, `bonus`. `AdvancementChoice`: `id`, `displayName`, `description`, `modifiers`. `ProgressionTrack`: `xpThresholds`, `choices`, `missionCompletionXp`, `debugXpStep`.

- [ ] **Step 1: Write the failing tests**

`Tests/EditMode/StatModifiersTests.cs`:

```csharp
using NUnit.Framework;

namespace Blackglass.Tests
{
    public class StatModifiersTests
    {
        [Test]
        public void Default_ChangesNothing()
        {
            var none = default(StatModifiers);
            Assert.That(none.maxHealth, Is.EqualTo(0));
            Assert.That(none.moveSpeed, Is.EqualTo(0f));
            Assert.That(none.attackDamage, Is.EqualTo(0f));
            Assert.That(none.abilityPower, Is.EqualTo(0f));
            Assert.That(none.abilityCooldownReduction, Is.EqualTo(0f));
        }

        [Test]
        public void Combine_AddsEveryField()
        {
            var a = new StatModifiers { maxHealth = 25, moveSpeed = 0.5f, attackDamage = 0.1f, abilityPower = 0.2f, abilityCooldownReduction = 0.05f };
            var b = new StatModifiers { maxHealth = 10, moveSpeed = 0.25f, attackDamage = 0.15f, abilityPower = 0.1f, abilityCooldownReduction = 0.1f };

            var sum = StatModifiers.Combine(a, b);

            Assert.That(sum.maxHealth, Is.EqualTo(35));
            Assert.That(sum.moveSpeed, Is.EqualTo(0.75f).Within(0.0001f));
            Assert.That(sum.attackDamage, Is.EqualTo(0.25f).Within(0.0001f));
            Assert.That(sum.abilityPower, Is.EqualTo(0.3f).Within(0.0001f));
            Assert.That(sum.abilityCooldownReduction, Is.EqualTo(0.15f).Within(0.0001f));
        }

        [Test]
        public void Combine_WithDefault_ReturnsTheOther()
        {
            var a = new StatModifiers { maxHealth = 25, attackDamage = 0.15f };
            var sum = StatModifiers.Combine(a, default);
            Assert.That(sum.maxHealth, Is.EqualTo(25));
            Assert.That(sum.attackDamage, Is.EqualTo(0.15f));
        }
    }
}
```

`Tests/EditMode/ProgressionTrackTests.cs`:

```csharp
using System;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class ProgressionTrackTests
    {
        AdvancementChoice combat;
        AdvancementChoice hull;
        ProgressionTrack track;

        [SetUp]
        public void SetUp()
        {
            combat = AdvancementChoice.Create("combat", "Combat Training", "+15% attack damage", new StatModifiers { attackDamage = 0.15f });
            hull = AdvancementChoice.Create("survivability", "Reinforced", "+25 max health", new StatModifiers { maxHealth = 25 });
            track = ProgressionTrack.Create(new[] { 0, 100, 250, 450, 700 }, new[] { combat, hull }, 150, 50);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(track);
            Object.DestroyImmediate(combat);
            Object.DestroyImmediate(hull);
        }

        [TestCase(-5, 1)]
        [TestCase(0, 1)]
        [TestCase(99, 1)]
        [TestCase(100, 2)]
        [TestCase(249, 2)]
        [TestCase(250, 3)]
        [TestCase(449, 3)]
        [TestCase(450, 4)]
        [TestCase(700, 5)]
        [TestCase(100000, 5)]
        public void RankFor_UsesTheThresholds(int xp, int rank) => Assert.That(track.RankFor(xp), Is.EqualTo(rank));

        [Test]
        public void MaxRank_IsTheNumberOfThresholds() => Assert.That(track.MaxRank, Is.EqualTo(5));

        [Test]
        public void XpForRank_ReturnsTheThreshold_AndClampsOutOfRangeRanks()
        {
            Assert.That(track.XpForRank(1), Is.EqualTo(0));
            Assert.That(track.XpForRank(3), Is.EqualTo(250));
            Assert.That(track.XpForRank(5), Is.EqualTo(700));
            Assert.That(track.XpForRank(0), Is.EqualTo(0));
            Assert.That(track.XpForRank(99), Is.EqualTo(700));
        }

        [Test]
        public void TryGetNextThreshold_GivesTheNextRanksXp_AndFalseAtMax()
        {
            Assert.That(track.TryGetNextThreshold(0, out var next), Is.True);
            Assert.That(next, Is.EqualTo(100));
            Assert.That(track.TryGetNextThreshold(130, out next), Is.True);
            Assert.That(next, Is.EqualTo(250));
            Assert.That(track.TryGetNextThreshold(700, out _), Is.False);
            Assert.That(track.TryGetNextThreshold(9999, out _), Is.False);
        }

        [Test]
        public void FindChoice_ByStableId_OrNull()
        {
            Assert.That(track.FindChoice("combat"), Is.SameAs(combat));
            Assert.That(track.FindChoice("survivability"), Is.SameAs(hull));
            Assert.That(track.FindChoice("nope"), Is.Null);
            Assert.That(track.FindChoice(null), Is.Null);
            Assert.That(track.FindChoice(""), Is.Null);
        }

        [Test]
        public void ExposesTheConfiguredNumbers()
        {
            Assert.That(track.MissionCompletionXp, Is.EqualTo(150));
            Assert.That(track.DebugXpStep, Is.EqualTo(50));
            Assert.That(track.Choices, Has.Count.EqualTo(2));
        }

        [Test]
        public void Create_RejectsBadThresholds()
        {
            Assert.Throws<ArgumentException>(() => ProgressionTrack.Create(new int[0], new AdvancementChoice[0], 150, 50));
            Assert.Throws<ArgumentException>(() => ProgressionTrack.Create(new[] { 10, 100 }, new AdvancementChoice[0], 150, 50), "must start at 0");
            Assert.Throws<ArgumentException>(() => ProgressionTrack.Create(new[] { 0, 100, 100 }, new AdvancementChoice[0], 150, 50), "strictly increasing");
            Assert.Throws<ArgumentException>(() => ProgressionTrack.Create(new[] { 0, 100, 50 }, new AdvancementChoice[0], 150, 50));
        }

        [Test]
        public void Create_RejectsDuplicateOrMissingChoices()
        {
            var twin = AdvancementChoice.Create("combat", "Twin", "", default);
            try
            {
                Assert.Throws<ArgumentException>(() => ProgressionTrack.Create(new[] { 0, 100 }, new[] { combat, twin }, 150, 50));
                Assert.Throws<ArgumentException>(() => ProgressionTrack.Create(new[] { 0, 100 }, new AdvancementChoice[] { combat, null }, 150, 50));
            }
            finally
            {
                Object.DestroyImmediate(twin);
            }
        }

        [Test]
        public void AdvancementChoice_RejectsABlankId()
        {
            Assert.Throws<ArgumentException>(() => AdvancementChoice.Create("", "x", "", default));
            Assert.Throws<ArgumentException>(() => AdvancementChoice.Create("  ", "x", "", default));
            Assert.Throws<ArgumentException>(() => AdvancementChoice.Create(null, "x", "", default));
        }

        [Test]
        public void OperativeRole_ExposesItsBonus()
        {
            var role = OperativeRole.Create("Assault", "Front line", new StatModifiers { attackDamage = 0.2f });
            try
            {
                Assert.That(role.DisplayName, Is.EqualTo("Assault"));
                Assert.That(role.Description, Is.EqualTo("Front line"));
                Assert.That(role.Bonus.attackDamage, Is.EqualTo(0.2f));
            }
            finally
            {
                Object.DestroyImmediate(role);
            }
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails.** Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.StatModifiersTests;Blackglass.Tests.ProgressionTrackTests"` (Unity separates several filters with `;`; if that is rejected run each class separately). Expected: EXIT=1 with `error CS0246` for `StatModifiers` / `ProgressionTrack` (types do not exist yet).

- [ ] **Step 3: Implement.**

`Scripts/Operatives/StatModifiers.cs`:

```csharp
using System;

namespace Blackglass
{
    /// <summary>
    /// The few numbers a role or an advancement choice can change, as plain data. Max health is a flat amount and move
    /// speed is flat metres per second; the other three are fractions of the base (0.15 = +15%; the cooldown field is a
    /// reduction, 0.15 = 15% shorter). Modifiers only ever add up; they never touch a shared base asset.
    /// </summary>
    [Serializable]
    public struct StatModifiers
    {
        public int maxHealth;
        public float moveSpeed;
        public float attackDamage;
        public float abilityPower;
        public float abilityCooldownReduction;

        public static StatModifiers Combine(StatModifiers a, StatModifiers b) => new StatModifiers
        {
            maxHealth = a.maxHealth + b.maxHealth,
            moveSpeed = a.moveSpeed + b.moveSpeed,
            attackDamage = a.attackDamage + b.attackDamage,
            abilityPower = a.abilityPower + b.abilityPower,
            abilityCooldownReduction = a.abilityCooldownReduction + b.abilityCooldownReduction,
        };
    }
}
```

`Scripts/Operatives/OperativeRole.cs`:

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// A role/specialisation as shared, immutable data: a name and a small bonus every operative of the role gets. Roles are
    /// data, not code, so a new or hybrid role needs no new class. Holds no runtime state.
    /// </summary>
    [CreateAssetMenu(menuName = "Blackglass/Operative Role", fileName = "Role")]
    public sealed class OperativeRole : ScriptableObject
    {
        [SerializeField] string displayName = "Role";
        [SerializeField, TextArea] string description = "";
        [SerializeField] StatModifiers bonus;

        public string DisplayName => displayName;
        public string Description => description;
        public StatModifiers Bonus => bonus;

        internal static OperativeRole Create(string displayName, string description, StatModifiers bonus)
        {
            var role = CreateInstance<OperativeRole>();
            role.displayName = displayName;
            role.description = description;
            role.bonus = bonus;
            return role;
        }
    }
}
```

`Scripts/Operatives/AdvancementChoice.cs`:

```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// One thing an operative can pick when it reaches a new rank, as shared, immutable data. The id is the stable key a
    /// persistent state stores (never the display name or the asset reference), so a choice can be renamed freely.
    /// </summary>
    [CreateAssetMenu(menuName = "Blackglass/Advancement Choice", fileName = "Choice")]
    public sealed class AdvancementChoice : ScriptableObject
    {
        [SerializeField] string id = "choice";
        [SerializeField] string displayName = "Choice";
        [SerializeField, TextArea] string description = "";
        [SerializeField] StatModifiers modifiers;

        public string Id => id;
        public string DisplayName => displayName;
        public string Description => description;
        public StatModifiers Modifiers => modifiers;

        internal static AdvancementChoice Create(string id, string displayName, string description, StatModifiers modifiers)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("A choice needs a stable id.", nameof(id));
            var choice = CreateInstance<AdvancementChoice>();
            choice.id = id;
            choice.displayName = displayName;
            choice.description = description;
            choice.modifiers = modifiers;
            return choice;
        }
    }
}
```

`Scripts/Operatives/ProgressionTrack.cs`:

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// How operatives advance, as shared, immutable data: the XP needed for each rank (rank 1 needs 0), the choices on
    /// offer (one pick per rank gained after the first), and the two XP amounts the prototype awards. Holds no state: a
    /// persistent state's rank is derived from its XP and this track.
    /// </summary>
    [CreateAssetMenu(menuName = "Blackglass/Progression Track", fileName = "Progression")]
    public sealed class ProgressionTrack : ScriptableObject
    {
        [SerializeField] int[] xpThresholds = { 0, 100, 250, 450, 700 };
        [SerializeField] AdvancementChoice[] choices = new AdvancementChoice[0];
        [SerializeField, Min(0)] int missionCompletionXp = 150;
        [SerializeField, Min(1)] int debugXpStep = 50;

        public int MaxRank => Mathf.Max(1, xpThresholds.Length);
        public IReadOnlyList<AdvancementChoice> Choices => choices;
        /// <summary>XP every operative gets when a mission succeeds.</summary>
        public int MissionCompletionXp => missionCompletionXp;
        /// <summary>XP the developer key adds.</summary>
        public int DebugXpStep => debugXpStep;

        /// <summary>The rank an operative with this much XP holds: 1 up to MaxRank. Negative XP is rank 1.</summary>
        public int RankFor(int xp)
        {
            var rank = 1;
            for (var i = 1; i < xpThresholds.Length; i++)
            {
                if (xp >= xpThresholds[i])
                    rank = i + 1;
            }
            return rank;
        }

        /// <summary>XP at which a rank is reached; ranks outside 1..MaxRank are clamped.</summary>
        public int XpForRank(int rank)
        {
            if (xpThresholds.Length == 0)
                return 0;
            return xpThresholds[Mathf.Clamp(rank, 1, xpThresholds.Length) - 1];
        }

        /// <summary>The XP of the next rank, or false at the maximum rank.</summary>
        public bool TryGetNextThreshold(int xp, out int threshold)
        {
            var rank = RankFor(xp);
            if (rank >= xpThresholds.Length)
            {
                threshold = xpThresholds.Length > 0 ? xpThresholds[xpThresholds.Length - 1] : 0;
                return false;
            }
            threshold = xpThresholds[rank];
            return true;
        }

        /// <summary>The choice with this stable id, or null (unknown, removed from the asset, blank).</summary>
        public AdvancementChoice FindChoice(string id)
        {
            if (string.IsNullOrEmpty(id))
                return null;
            foreach (var choice in choices)
            {
                if (choice != null && choice.Id == id)
                    return choice;
            }
            return null;
        }

        internal static ProgressionTrack Create(int[] thresholds, AdvancementChoice[] choices, int missionCompletionXp, int debugXpStep)
        {
            ValidateThresholds(thresholds);
            var seen = new HashSet<string>();
            foreach (var choice in choices)
            {
                if (choice == null)
                    throw new ArgumentException("A track cannot list a missing choice.", nameof(choices));
                if (!seen.Add(choice.Id))
                    throw new ArgumentException($"Two choices share the id '{choice.Id}'.", nameof(choices));
            }

            var track = CreateInstance<ProgressionTrack>();
            track.xpThresholds = (int[])thresholds.Clone();
            track.choices = (AdvancementChoice[])choices.Clone();
            track.missionCompletionXp = Mathf.Max(0, missionCompletionXp);
            track.debugXpStep = Mathf.Max(1, debugXpStep);
            return track;
        }

        static void ValidateThresholds(int[] thresholds)
        {
            if (thresholds == null || thresholds.Length == 0)
                throw new ArgumentException("A track needs at least one rank.", nameof(thresholds));
            if (thresholds[0] != 0)
                throw new ArgumentException("Rank 1 must need 0 XP.", nameof(thresholds));
            for (var i = 1; i < thresholds.Length; i++)
            {
                if (thresholds[i] <= thresholds[i - 1])
                    throw new ArgumentException("Thresholds must strictly increase.", nameof(thresholds));
            }
        }

        // Keeps a hand-edited asset valid: at least rank 1 at 0 XP, strictly increasing thresholds.
        void OnValidate()
        {
            if (xpThresholds == null || xpThresholds.Length == 0)
                xpThresholds = new[] { 0 };
            xpThresholds[0] = 0;
            for (var i = 1; i < xpThresholds.Length; i++)
                xpThresholds[i] = Mathf.Max(xpThresholds[i], xpThresholds[i - 1] + 1);
        }
    }
}
```

- [ ] **Step 4: Run to verify it passes.** Same command as Step 2. Expected: EXIT=0. New tests: 3 + 19 (the ten `TestCase`s count individually) = 22 (report the real number).

- [ ] **Step 5: Commit.**

```bash
git add Assets/_Project/Scripts/Operatives.meta Assets/_Project/Scripts/Operatives Assets/_Project/Tests/EditMode/StatModifiersTests.cs Assets/_Project/Tests/EditMode/StatModifiersTests.cs.meta Assets/_Project/Tests/EditMode/ProgressionTrackTests.cs Assets/_Project/Tests/EditMode/ProgressionTrackTests.cs.meta
git commit -m "Add operative stat modifiers, role, advancement choice and progression track

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Persistent operative state

**Files:**
- Create: `Scripts/Operatives/PersistentOperativeState.cs`
- Test: `Tests/EditMode/PersistentOperativeStateTests.cs`

**Interfaces:**
- Consumes: `ProgressionTrack.RankFor`, `ProgressionTrack.FindChoice` (Task 1).
- Produces: `[Serializable] sealed class PersistentOperativeState` with `PersistentOperativeState(string operativeId)` (throws `ArgumentException` for blank), `string OperativeId`, `int Experience`, `IReadOnlyList<string> ChoiceIds`, `int Rank(ProgressionTrack)`, `int PendingPicks(ProgressionTrack)`, `void AddExperience(int amount)` (throws `ArgumentOutOfRangeException` when negative; clamps at `int.MaxValue`), `bool TryPickChoice(ProgressionTrack track, string choiceId)`, `void ResetProgression()`.

- [ ] **Step 1: Write the failing tests.** `Tests/EditMode/PersistentOperativeStateTests.cs`:

```csharp
using System;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class PersistentOperativeStateTests
    {
        AdvancementChoice combat;
        AdvancementChoice hull;
        ProgressionTrack track;
        PersistentOperativeState state;

        [SetUp]
        public void SetUp()
        {
            combat = AdvancementChoice.Create("combat", "Combat Training", "", new StatModifiers { attackDamage = 0.15f });
            hull = AdvancementChoice.Create("survivability", "Reinforced", "", new StatModifiers { maxHealth = 25 });
            track = ProgressionTrack.Create(new[] { 0, 100, 250, 450, 700 }, new[] { combat, hull }, 150, 50);
            state = new PersistentOperativeState("op-1");
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(track);
            Object.DestroyImmediate(combat);
            Object.DestroyImmediate(hull);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void Constructor_RejectsABlankId(string id) =>
            Assert.Throws<ArgumentException>(() => new PersistentOperativeState(id));

        [Test]
        public void New_StartsAtRankOne_WithNoXpPicksOrPendingChoice()
        {
            Assert.That(state.OperativeId, Is.EqualTo("op-1"));
            Assert.That(state.Experience, Is.EqualTo(0));
            Assert.That(state.ChoiceIds, Is.Empty);
            Assert.That(state.Rank(track), Is.EqualTo(1));
            Assert.That(state.PendingPicks(track), Is.EqualTo(0));
        }

        [Test]
        public void AddExperience_RaisesTheRankAtTheThreshold_AndGrantsOnePendingPickPerRank()
        {
            state.AddExperience(99);
            Assert.That(state.Rank(track), Is.EqualTo(1));
            Assert.That(state.PendingPicks(track), Is.EqualTo(0));

            state.AddExperience(1);
            Assert.That(state.Rank(track), Is.EqualTo(2));
            Assert.That(state.PendingPicks(track), Is.EqualTo(1));

            state.AddExperience(150);   // 250: two ranks gained in total
            Assert.That(state.Rank(track), Is.EqualTo(3));
            Assert.That(state.PendingPicks(track), Is.EqualTo(2));
        }

        [Test]
        public void AddExperience_ZeroChangesNothing_NegativeThrows()
        {
            state.AddExperience(0);
            Assert.That(state.Experience, Is.EqualTo(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => state.AddExperience(-1));
            Assert.That(state.Experience, Is.EqualTo(0));
        }

        [Test]
        public void AddExperience_PastIntMax_ClampsInsteadOfWrapping()
        {
            state.AddExperience(int.MaxValue);
            state.AddExperience(500);
            Assert.That(state.Experience, Is.EqualTo(int.MaxValue));
            Assert.That(state.Rank(track), Is.EqualTo(5));
        }

        [Test]
        public void TryPickChoice_NeedsAPendingPick()
        {
            Assert.That(state.TryPickChoice(track, "combat"), Is.False);
            Assert.That(state.ChoiceIds, Is.Empty);

            state.AddExperience(100);
            Assert.That(state.TryPickChoice(track, "combat"), Is.True);
            Assert.That(state.ChoiceIds, Is.EqualTo(new[] { "combat" }));
            Assert.That(state.PendingPicks(track), Is.EqualTo(0));
            Assert.That(state.TryPickChoice(track, "survivability"), Is.False, "the only pick is spent");
        }

        [Test]
        public void TryPickChoice_UnknownOrBlankId_IsRefused_AndKeepsThePendingPick()
        {
            state.AddExperience(100);
            Assert.That(state.TryPickChoice(track, "not-a-choice"), Is.False);
            Assert.That(state.TryPickChoice(track, null), Is.False);
            Assert.That(state.TryPickChoice(track, ""), Is.False);
            Assert.That(state.PendingPicks(track), Is.EqualTo(1));
            Assert.That(state.ChoiceIds, Is.Empty);
        }

        [Test]
        public void TryPickChoice_TheSameChoiceCanBePickedAgain_PicksStack()
        {
            state.AddExperience(250);
            Assert.That(state.TryPickChoice(track, "combat"), Is.True);
            Assert.That(state.TryPickChoice(track, "combat"), Is.True);
            Assert.That(state.ChoiceIds, Is.EqualTo(new[] { "combat", "combat" }));
            Assert.That(state.TryPickChoice(track, "combat"), Is.False);
        }

        [Test]
        public void PendingPicks_NeverGoNegative_WhenAShorterTrackIsUsed()
        {
            state.AddExperience(700);
            state.TryPickChoice(track, "combat");
            state.TryPickChoice(track, "combat");
            state.TryPickChoice(track, "survivability");
            var shorter = ProgressionTrack.Create(new[] { 0, 100 }, new[] { combat, hull }, 150, 50);
            try
            {
                Assert.That(state.Rank(shorter), Is.EqualTo(2));
                Assert.That(state.PendingPicks(shorter), Is.EqualTo(0), "3 picks made, only 1 allowed by the short track");
            }
            finally
            {
                Object.DestroyImmediate(shorter);
            }
        }

        [Test]
        public void ResetProgression_ClearsXpAndPicks_ButKeepsTheId()
        {
            state.AddExperience(250);
            state.TryPickChoice(track, "combat");

            state.ResetProgression();

            Assert.That(state.OperativeId, Is.EqualTo("op-1"));
            Assert.That(state.Experience, Is.EqualTo(0));
            Assert.That(state.ChoiceIds, Is.Empty);
            Assert.That(state.Rank(track), Is.EqualTo(1));
        }

        [Test]
        public void SerializesToJsonAndBack_WithoutAnyUnityObjectReference()
        {
            state.AddExperience(260);
            state.TryPickChoice(track, "combat");

            var json = JsonUtility.ToJson(state);
            var copy = JsonUtility.FromJson<PersistentOperativeState>(json);

            Assert.That(copy.OperativeId, Is.EqualTo("op-1"));
            Assert.That(copy.Experience, Is.EqualTo(260));
            Assert.That(copy.ChoiceIds, Is.EqualTo(new[] { "combat" }));
            Assert.That(copy.Rank(track), Is.EqualTo(3));
            Assert.That(json, Does.Not.Contain("instanceID"), "state must stay plain data (Phase 14 will save it)");
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails.** `Tools/run-tests.sh EditMode "Blackglass.Tests.PersistentOperativeStateTests"`. Expected: EXIT=1, `CS0246 PersistentOperativeState`.

- [ ] **Step 3: Implement.** `Scripts/Operatives/PersistentOperativeState.cs`:

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The part of an operative that outlives a mission: its stable id, XP and the advancement choices it has made.
    /// Plain serializable data with no Unity object references (a future save file can store a list of these, keyed by
    /// id). Rank and the number of pending picks are derived from XP and a ProgressionTrack, never stored, so they cannot
    /// disagree with it. Transient mission state (health, cooldowns, orders) is deliberately not here.
    /// </summary>
    [Serializable]
    public sealed class PersistentOperativeState
    {
        [SerializeField] string operativeId;
        [SerializeField] int experience;
        [SerializeField] List<string> choiceIds = new List<string>();

        public PersistentOperativeState(string operativeId)
        {
            if (string.IsNullOrWhiteSpace(operativeId))
                throw new ArgumentException("An operative needs a stable id.", nameof(operativeId));
            this.operativeId = operativeId;
        }

        // For the serializer only (JsonUtility, Unity serialization); it leaves the id to the data it reads.
        PersistentOperativeState() { }

        public string OperativeId => operativeId;
        public int Experience => experience;
        public IReadOnlyList<string> ChoiceIds => choiceIds;

        public int Rank(ProgressionTrack track) => track.RankFor(experience);

        /// <summary>Choices the operative has earned but not yet made: one per rank after the first, minus the picks made. Never negative.</summary>
        public int PendingPicks(ProgressionTrack track) => Math.Max(0, Rank(track) - 1 - choiceIds.Count);

        /// <summary>Adds XP (zero is fine). Clamps at int.MaxValue instead of wrapping.</summary>
        public void AddExperience(int amount)
        {
            if (amount < 0)
                throw new ArgumentOutOfRangeException(nameof(amount), amount, "Experience cannot be negative.");
            experience = (int)Math.Min((long)int.MaxValue, (long)experience + amount);
        }

        /// <summary>Makes a pick: needs a pending pick and a choice the track knows. The same choice may be picked again.</summary>
        public bool TryPickChoice(ProgressionTrack track, string choiceId)
        {
            if (track.FindChoice(choiceId) == null || PendingPicks(track) <= 0)
                return false;
            choiceIds.Add(choiceId);
            return true;
        }

        /// <summary>Back to rank 1: no XP, no picks. The id stays.</summary>
        public void ResetProgression()
        {
            experience = 0;
            choiceIds.Clear();
        }
    }
}
```

- [ ] **Step 4: Run to verify it passes.** Same command. Expected EXIT=0, 13 new tests (3 `TestCase`s + 10 `Test`s). If the JSON round trip fails because the serializer cannot construct the class, report it; do not weaken the test.

- [ ] **Step 5: Commit.**

```bash
git add Assets/_Project/Scripts/Operatives Assets/_Project/Tests/EditMode/PersistentOperativeStateTests.cs Assets/_Project/Tests/EditMode/PersistentOperativeStateTests.cs.meta
git commit -m "Add the persistent operative state: id, XP and picks with derived rank

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Operative definition and effective configuration

**Files:**
- Create: `Scripts/Operatives/OperativeDefinition.cs`, `Scripts/Operatives/EffectiveConfiguration.cs`
- Test: `Tests/EditMode/OperativeDefinitionTests.cs`, `Tests/EditMode/EffectiveConfigurationTests.cs`

**Interfaces:**
- Consumes: `OperativeRole`, `ProgressionTrack`, `PersistentOperativeState`, `StatModifiers` (Tasks 1-2); existing `CombatArchetype` (`Role`, `Range`, `Damage`, `AttackInterval`), `AbilityDefinition`, `CombatRole`.
- Produces:
  - `OperativeDefinition : ScriptableObject` with `string Id`, `string DisplayName`, `OperativeRole Role`, `GameObject UnitPrefab`, `Sprite Portrait`, `int BaseMaxHealth`, `float BaseMoveSpeed`, `CombatArchetype Archetype`, `AbilityDefinition[] Abilities`, `bool IsValid(out string problem)`, `internal static OperativeDefinition Create(string id, string displayName, OperativeRole role, GameObject unitPrefab, int baseMaxHealth, float baseMoveSpeed, CombatArchetype archetype, AbilityDefinition[] abilities)`. Serialized names: `id`, `displayName`, `role`, `unitPrefab`, `portrait`, `baseMaxHealth`, `baseMoveSpeed`, `archetype`, `abilities`. Constant `OperativeDefinition.MaxAbilities = 4`.
  - `readonly struct EffectiveConfiguration` with `int Rank`, `int MaxHealth`, `float MoveSpeed`, `CombatRole AttackRole`, `float AttackRange`, `int AttackDamage`, `float AttackInterval`, `float AbilityPower` (multiplier, 1 = unchanged), `float AbilityCooldownMultiplier` (1 = unchanged), `static EffectiveConfiguration Base(OperativeDefinition)` (definition only: no role, no picks), `static EffectiveConfiguration Evaluate(OperativeDefinition, PersistentOperativeState, ProgressionTrack)` (null state or track = no picks, rank 1; unknown choice ids skipped; null role = no bonus).

- [ ] **Step 1: Write the failing tests.**

`Tests/EditMode/OperativeDefinitionTests.cs`:

```csharp
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class OperativeDefinitionTests
    {
        OperativeRole role;
        CombatArchetype archetype;
        AbilityDefinition ability;
        GameObject prefab;

        [SetUp]
        public void SetUp()
        {
            role = OperativeRole.Create("Assault", "", default);
            archetype = CombatArchetype.Create("Ranged", CombatRole.Ranged, 8f, 15, 1f);
            ability = AbilityDefinition.Create("Aimed Shot", AbilityTargetMode.Unit, 14f, true, AbilityCoverRule.Applies, 6f, AbilityEffect.Damage, 45);
            prefab = new GameObject("UnitPrefab");
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(role);
            Object.DestroyImmediate(archetype);
            Object.DestroyImmediate(ability);
            Object.DestroyImmediate(prefab);
        }

        OperativeDefinition Make(string id = "id-1", OperativeRole r = null, GameObject p = null, int health = 130,
            CombatArchetype a = null, params AbilityDefinition[] abilities)
        {
            if (abilities.Length == 0 && a == null)
                abilities = new[] { ability };
            return OperativeDefinition.Create(id, "Darius", r ?? role, p ?? prefab, health, 5f, a ?? archetype, abilities);
        }

        [Test]
        public void Create_ExposesTheAuthoredValues()
        {
            var definition = Make();
            try
            {
                Assert.That(definition.Id, Is.EqualTo("id-1"));
                Assert.That(definition.DisplayName, Is.EqualTo("Darius"));
                Assert.That(definition.Role, Is.SameAs(role));
                Assert.That(definition.UnitPrefab, Is.SameAs(prefab));
                Assert.That(definition.BaseMaxHealth, Is.EqualTo(130));
                Assert.That(definition.BaseMoveSpeed, Is.EqualTo(5f));
                Assert.That(definition.Archetype, Is.SameAs(archetype));
                Assert.That(definition.Abilities, Is.EqualTo(new[] { ability }));
                Assert.That(definition.IsValid(out var problem), Is.True, problem);
            }
            finally
            {
                Object.DestroyImmediate(definition);
            }
        }

        [Test]
        public void IsValid_NamesTheFirstProblem()
        {
            var created = new System.Collections.Generic.List<OperativeDefinition>();
            try
            {
                OperativeDefinition Add(OperativeDefinition d) { created.Add(d); return d; }

                Assert.That(Add(OperativeDefinition.Create("", "x", role, prefab, 100, 5f, archetype, new AbilityDefinition[0])).IsValid(out var p1), Is.False);
                StringAssert.Contains("id", p1);
                Assert.That(Add(OperativeDefinition.Create("a", "x", null, prefab, 100, 5f, archetype, new AbilityDefinition[0])).IsValid(out var p2), Is.False);
                StringAssert.Contains("role", p2);
                Assert.That(Add(OperativeDefinition.Create("a", "x", role, null, 100, 5f, archetype, new AbilityDefinition[0])).IsValid(out var p3), Is.False);
                StringAssert.Contains("prefab", p3);
                Assert.That(Add(OperativeDefinition.Create("a", "x", role, prefab, 100, 5f, null, new AbilityDefinition[0])).IsValid(out var p4), Is.False);
                StringAssert.Contains("archetype", p4);
                Assert.That(Add(OperativeDefinition.Create("a", "x", role, prefab, 0, 5f, archetype, new AbilityDefinition[0])).IsValid(out var p5), Is.False);
                StringAssert.Contains("health", p5);
                Assert.That(Add(OperativeDefinition.Create("a", "x", role, prefab, 100, 5f, archetype, new AbilityDefinition[] { ability, null })).IsValid(out var p6), Is.False);
                StringAssert.Contains("ability", p6);
                var five = new[] { ability, ability, ability, ability, ability };
                Assert.That(Add(OperativeDefinition.Create("a", "x", role, prefab, 100, 5f, archetype, five)).IsValid(out var p7), Is.False);
                StringAssert.Contains("4", p7);
                Assert.That(Add(OperativeDefinition.Create("a", " ", role, prefab, 100, 5f, archetype, new AbilityDefinition[0])).IsValid(out var p8), Is.False);
                StringAssert.Contains("name", p8);
            }
            finally
            {
                foreach (var d in created)
                    Object.DestroyImmediate(d);
            }
        }
    }
}
```

`Tests/EditMode/EffectiveConfigurationTests.cs`:

```csharp
using System;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class EffectiveConfigurationTests
    {
        AdvancementChoice combat, hull, speed, focus;
        ProgressionTrack track;
        OperativeRole assault;
        CombatArchetype archetype;
        AbilityDefinition aimed;
        GameObject prefab;
        OperativeDefinition darius;

        [SetUp]
        public void SetUp()
        {
            combat = AdvancementChoice.Create("combat", "Combat Training", "", new StatModifiers { attackDamage = 0.15f });
            hull = AdvancementChoice.Create("survivability", "Reinforced", "", new StatModifiers { maxHealth = 25 });
            speed = AdvancementChoice.Create("mobility", "Fleet-footed", "", new StatModifiers { moveSpeed = 0.75f });
            focus = AdvancementChoice.Create("ability", "Focus", "", new StatModifiers { abilityPower = 0.2f, abilityCooldownReduction = 0.15f });
            track = ProgressionTrack.Create(new[] { 0, 100, 250, 450, 700 }, new[] { combat, hull, speed, focus }, 150, 50);
            assault = OperativeRole.Create("Assault", "", new StatModifiers { attackDamage = 0.2f });
            archetype = CombatArchetype.Create("Ranged", CombatRole.Ranged, 8f, 15, 1f);
            aimed = AbilityDefinition.Create("Aimed Shot", AbilityTargetMode.Unit, 14f, true, AbilityCoverRule.Applies, 6f, AbilityEffect.Damage, 45);
            prefab = new GameObject("UnitPrefab");
            darius = OperativeDefinition.Create("darius-id", "Darius", assault, prefab, 130, 5f, archetype, new[] { aimed });
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in new Object[] { track, combat, hull, speed, focus, assault, archetype, aimed, prefab, darius })
                Object.DestroyImmediate(o);
        }

        PersistentOperativeState Advance(string id, int xp, params string[] picks)
        {
            var state = new PersistentOperativeState(id);
            state.AddExperience(xp);
            foreach (var pick in picks)
                Assert.That(state.TryPickChoice(track, pick), Is.True, pick);
            return state;
        }

        [Test]
        public void Base_IsTheDefinitionAndItsArchetypeAlone_NoRoleNoPicks()
        {
            var config = EffectiveConfiguration.Base(darius);

            Assert.That(config.Rank, Is.EqualTo(1));
            Assert.That(config.MaxHealth, Is.EqualTo(130));
            Assert.That(config.MoveSpeed, Is.EqualTo(5f));
            Assert.That(config.AttackRole, Is.EqualTo(CombatRole.Ranged));
            Assert.That(config.AttackRange, Is.EqualTo(8f));
            Assert.That(config.AttackDamage, Is.EqualTo(15));
            Assert.That(config.AttackInterval, Is.EqualTo(1f));
            Assert.That(config.AbilityPower, Is.EqualTo(1f));
            Assert.That(config.AbilityCooldownMultiplier, Is.EqualTo(1f));
        }

        [Test]
        public void Evaluate_AFreshOperative_GetsOnlyTheRoleBonus()
        {
            var config = EffectiveConfiguration.Evaluate(darius, new PersistentOperativeState("a"), track);

            Assert.That(config.Rank, Is.EqualTo(1));
            Assert.That(config.AttackDamage, Is.EqualTo(18), "15 +20% role bonus");
            Assert.That(config.MaxHealth, Is.EqualTo(130));
            Assert.That(config.MoveSpeed, Is.EqualTo(5f));
        }

        [Test]
        public void Evaluate_EachChoiceChangesItsOwnStat()
        {
            Assert.That(EffectiveConfiguration.Evaluate(darius, Advance("a", 100, "combat"), track).AttackDamage, Is.EqualTo(20), "15 * (1 + 0.2 + 0.15) = 20.25");
            Assert.That(EffectiveConfiguration.Evaluate(darius, Advance("a", 100, "survivability"), track).MaxHealth, Is.EqualTo(155));
            Assert.That(EffectiveConfiguration.Evaluate(darius, Advance("a", 100, "mobility"), track).MoveSpeed, Is.EqualTo(5.75f).Within(0.0001f));
            var focused = EffectiveConfiguration.Evaluate(darius, Advance("a", 100, "ability"), track);
            Assert.That(focused.AbilityPower, Is.EqualTo(1.2f).Within(0.0001f));
            Assert.That(focused.AbilityCooldownMultiplier, Is.EqualTo(0.85f).Within(0.0001f));
        }

        [Test]
        public void Evaluate_PicksStack()
        {
            var config = EffectiveConfiguration.Evaluate(darius, Advance("a", 450, "combat", "survivability", "mobility"), track);

            Assert.That(config.Rank, Is.EqualTo(4));
            Assert.That(config.AttackDamage, Is.EqualTo(20), "15 * (1 + 0.2 + 0.15) = 20.25");
            Assert.That(config.MaxHealth, Is.EqualTo(155));
            Assert.That(config.MoveSpeed, Is.EqualTo(5.75f).Within(0.0001f));
        }

        [Test]
        public void Evaluate_TheSamePickTwice_AddsItsModifierTwice()
        {
            var config = EffectiveConfiguration.Evaluate(darius, Advance("a", 250, "survivability", "survivability"), track);
            Assert.That(config.MaxHealth, Is.EqualTo(180));
        }

        [Test]
        public void Evaluate_CooldownReductionIsCappedAtSeventyFivePercent()
        {
            var huge = AdvancementChoice.Create("huge", "Huge", "", new StatModifiers { abilityCooldownReduction = 0.9f });
            var big = ProgressionTrack.Create(new[] { 0, 100 }, new[] { huge }, 150, 50);
            try
            {
                var state = new PersistentOperativeState("a");
                state.AddExperience(100);
                state.TryPickChoice(big, "huge");
                Assert.That(EffectiveConfiguration.Evaluate(darius, state, big).AbilityCooldownMultiplier, Is.EqualTo(0.25f).Within(0.0001f));
            }
            finally
            {
                Object.DestroyImmediate(big);
                Object.DestroyImmediate(huge);
            }
        }

        [Test]
        public void Evaluate_ASkippedUnknownChoiceId_IsIgnored_NotAnError()
        {
            var other = AdvancementChoice.Create("only-in-the-old-track", "Old", "", new StatModifiers { maxHealth = 999 });
            var oldTrack = ProgressionTrack.Create(new[] { 0, 100 }, new[] { other }, 150, 50);
            try
            {
                var state = new PersistentOperativeState("a");
                state.AddExperience(100);
                state.TryPickChoice(oldTrack, "only-in-the-old-track");

                var config = EffectiveConfiguration.Evaluate(darius, state, track);

                Assert.That(config.MaxHealth, Is.EqualTo(130), "the stale pick is skipped");
            }
            finally
            {
                Object.DestroyImmediate(oldTrack);
                Object.DestroyImmediate(other);
            }
        }

        [Test]
        public void Evaluate_NullStateOrTrackOrRole_IsTolerated()
        {
            Assert.That(EffectiveConfiguration.Evaluate(darius, null, track).AttackDamage, Is.EqualTo(18));
            Assert.That(EffectiveConfiguration.Evaluate(darius, new PersistentOperativeState("a"), null).Rank, Is.EqualTo(1));
            var roleless = OperativeDefinition.Create("x", "X", null, prefab, 100, 5f, archetype, new AbilityDefinition[0]);
            try
            {
                Assert.That(EffectiveConfiguration.Evaluate(roleless, null, null).AttackDamage, Is.EqualTo(15));
            }
            finally
            {
                Object.DestroyImmediate(roleless);
            }
        }

        [Test]
        public void Evaluate_ADefinitionWithoutAnArchetype_Throws()
        {
            var bare = OperativeDefinition.Create("x", "X", assault, prefab, 100, 5f, null, new AbilityDefinition[0]);
            try
            {
                Assert.Throws<ArgumentException>(() => EffectiveConfiguration.Evaluate(bare, null, track));
                Assert.Throws<ArgumentNullException>(() => EffectiveConfiguration.Evaluate(null, null, track));
            }
            finally
            {
                Object.DestroyImmediate(bare);
            }
        }

        [Test]
        public void Evaluate_ClampsToPlayableMinimums()
        {
            var crippling = AdvancementChoice.Create("c", "C", "", new StatModifiers { maxHealth = -500, moveSpeed = -50f, attackDamage = -5f });
            var weak = ProgressionTrack.Create(new[] { 0, 10 }, new[] { crippling }, 150, 50);
            try
            {
                var state = new PersistentOperativeState("a");
                state.AddExperience(10);
                state.TryPickChoice(weak, "c");
                var config = EffectiveConfiguration.Evaluate(darius, state, weak);
                Assert.That(config.MaxHealth, Is.EqualTo(1));
                Assert.That(config.MoveSpeed, Is.EqualTo(0.5f));
                Assert.That(config.AttackDamage, Is.EqualTo(0));
            }
            finally
            {
                Object.DestroyImmediate(weak);
                Object.DestroyImmediate(crippling);
            }
        }

        // The data-safety rule: two operatives sharing one definition never share progression, and the shared assets never change.
        [Test]
        public void TwoOperativesSharingOneDefinition_ProgressIndependently_AndTheAssetsStayUntouched()
        {
            var a = Advance("a", 250, "combat", "survivability");
            var b = new PersistentOperativeState("b");

            var ea = EffectiveConfiguration.Evaluate(darius, a, track);
            var eb = EffectiveConfiguration.Evaluate(darius, b, track);

            Assert.That(ea.Rank, Is.EqualTo(3));
            Assert.That(ea.MaxHealth, Is.EqualTo(155));
            Assert.That(ea.AttackDamage, Is.EqualTo(20));
            Assert.That(eb.Rank, Is.EqualTo(1));
            Assert.That(eb.MaxHealth, Is.EqualTo(130), "B did not inherit A's survivability pick");
            Assert.That(eb.AttackDamage, Is.EqualTo(18), "B did not inherit A's combat pick");
            Assert.That(b.Experience, Is.EqualTo(0));
            Assert.That(b.ChoiceIds, Is.Empty);

            Assert.That(darius.BaseMaxHealth, Is.EqualTo(130));
            Assert.That(darius.BaseMoveSpeed, Is.EqualTo(5f));
            Assert.That(archetype.Damage, Is.EqualTo(15));
            Assert.That(assault.Bonus.attackDamage, Is.EqualTo(0.2f));
            Assert.That(combat.Modifiers.attackDamage, Is.EqualTo(0.15f));
            Assert.That(hull.Modifiers.maxHealth, Is.EqualTo(25));
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails.** `Tools/run-tests.sh EditMode "Blackglass.Tests.OperativeDefinitionTests"` then the same for `EffectiveConfigurationTests`. Expected: EXIT=1, `CS0246 OperativeDefinition`/`EffectiveConfiguration`.

- [ ] **Step 3: Implement.**

`Scripts/Operatives/OperativeDefinition.cs`:

```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Who an operative is, as shared, immutable data: a stable id, a display name (changeable; never a key), a role, the
    /// complete unit prefab that represents them in a mission (replace it to change their model; their id, XP and picks
    /// live in PersistentOperativeState and are untouched), base health and speed, a weapon archetype and starting
    /// abilities. Holds no runtime state, ever: several operatives may share a role, archetype or ability asset.
    /// </summary>
    [CreateAssetMenu(menuName = "Blackglass/Operative", fileName = "Operative")]
    public sealed class OperativeDefinition : ScriptableObject
    {
        public const int MaxAbilities = 4;

        // Authored once and never changed afterwards: a GUID string. A duplicated asset keeps the old id, so use
        // "Generate new id" on a copy; the roster refuses duplicates loudly.
        [SerializeField] string id = "";
        [SerializeField] string displayName = "Operative";
        [SerializeField] OperativeRole role;
        [SerializeField] GameObject unitPrefab;
        [SerializeField] Sprite portrait;
        [SerializeField, Min(1)] int baseMaxHealth = 100;
        [SerializeField, Min(0.5f)] float baseMoveSpeed = 5f;
        [SerializeField] CombatArchetype archetype;
        [SerializeField] AbilityDefinition[] abilities = new AbilityDefinition[0];

        public string Id => id;
        public string DisplayName => displayName;
        public OperativeRole Role => role;
        public GameObject UnitPrefab => unitPrefab;
        public Sprite Portrait => portrait;
        public int BaseMaxHealth => baseMaxHealth;
        public float BaseMoveSpeed => baseMoveSpeed;
        public CombatArchetype Archetype => archetype;
        public AbilityDefinition[] Abilities => abilities;

        /// <summary>False with the first problem found (what to fix in the asset).</summary>
        public bool IsValid(out string problem)
        {
            if (string.IsNullOrWhiteSpace(id))
                problem = "the id is blank";
            else if (string.IsNullOrWhiteSpace(displayName))
                problem = "the display name is blank";
            else if (role == null)
                problem = "there is no role";
            else if (unitPrefab == null)
                problem = "there is no unit prefab";
            else if (archetype == null)
                problem = "there is no combat archetype";
            else if (baseMaxHealth < 1)
                problem = "base health is below 1";
            else if (abilities == null || abilities.Length > MaxAbilities)
                problem = $"there are more than {MaxAbilities} abilities";
            else if (Array.IndexOf(abilities, null) >= 0)
                problem = "an ability slot is empty";
            else
                problem = null;
            return problem == null;
        }

#if UNITY_EDITOR
        [ContextMenu("Generate new id")]
        void GenerateNewId()
        {
            id = Guid.NewGuid().ToString();
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif

        internal static OperativeDefinition Create(string id, string displayName, OperativeRole role, GameObject unitPrefab,
            int baseMaxHealth, float baseMoveSpeed, CombatArchetype archetype, AbilityDefinition[] abilities)
        {
            var definition = CreateInstance<OperativeDefinition>();
            definition.id = id;
            definition.displayName = displayName;
            definition.role = role;
            definition.unitPrefab = unitPrefab;
            definition.baseMaxHealth = baseMaxHealth;
            definition.baseMoveSpeed = baseMoveSpeed;
            definition.archetype = archetype;
            definition.abilities = abilities ?? new AbilityDefinition[0];
            return definition;
        }
    }
}
```

`Scripts/Operatives/EffectiveConfiguration.cs`:

```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// What an operative is worth right now: its definition, plus its role's bonus, plus the modifiers of every choice it
    /// has picked. A plain value computed on demand and never stored back into any asset. Combat stays in the combat
    /// components; they receive these numbers and do the rest.
    /// </summary>
    public readonly struct EffectiveConfiguration
    {
        // A cooldown can be cut by at most this much, so abilities never become free.
        const float MaxCooldownReduction = 0.75f;
        const float MinMoveSpeed = 0.5f;

        EffectiveConfiguration(int rank, int maxHealth, float moveSpeed, CombatRole attackRole, float attackRange,
            int attackDamage, float attackInterval, float abilityPower, float abilityCooldownMultiplier)
        {
            Rank = rank;
            MaxHealth = maxHealth;
            MoveSpeed = moveSpeed;
            AttackRole = attackRole;
            AttackRange = attackRange;
            AttackDamage = attackDamage;
            AttackInterval = attackInterval;
            AbilityPower = abilityPower;
            AbilityCooldownMultiplier = abilityCooldownMultiplier;
        }

        public int Rank { get; }
        public int MaxHealth { get; }
        public float MoveSpeed { get; }
        public CombatRole AttackRole { get; }
        public float AttackRange { get; }
        public int AttackDamage { get; }
        public float AttackInterval { get; }
        /// <summary>Multiplier on an ability's damage or healing; 1 leaves it as authored.</summary>
        public float AbilityPower { get; }
        /// <summary>Multiplier on an ability's cooldown; 1 leaves it as authored, never below 0.25.</summary>
        public float AbilityCooldownMultiplier { get; }

        /// <summary>The definition and its archetype alone: no role bonus, no picks, rank 1.</summary>
        public static EffectiveConfiguration Base(OperativeDefinition definition) => Build(definition, 1, default);

        /// <summary>
        /// Base + role bonus + the modifiers of the state's picks. A null state or track means no picks (rank 1); a pick
        /// whose id the track no longer knows is skipped; a null role gives no bonus.
        /// </summary>
        public static EffectiveConfiguration Evaluate(OperativeDefinition definition, PersistentOperativeState state, ProgressionTrack track)
        {
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));
            var total = definition.Role != null ? definition.Role.Bonus : default;
            var rank = 1;
            if (state != null && track != null)
            {
                rank = state.Rank(track);
                foreach (var choiceId in state.ChoiceIds)
                {
                    var choice = track.FindChoice(choiceId);
                    if (choice != null)
                        total = StatModifiers.Combine(total, choice.Modifiers);
                }
            }
            return Build(definition, rank, total);
        }

        static EffectiveConfiguration Build(OperativeDefinition definition, int rank, StatModifiers total)
        {
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));
            var archetype = definition.Archetype;
            if (archetype == null)
                throw new ArgumentException($"{definition.name} has no combat archetype.", nameof(definition));

            return new EffectiveConfiguration(
                rank,
                Math.Max(1, definition.BaseMaxHealth + total.maxHealth),
                Math.Max(MinMoveSpeed, definition.BaseMoveSpeed + total.moveSpeed),
                archetype.Role,
                archetype.Range,
                ScaleRounded(archetype.Damage, total.attackDamage),
                archetype.AttackInterval,
                Math.Max(0f, 1f + total.abilityPower),
                1f - Mathf.Clamp(total.abilityCooldownReduction, 0f, MaxCooldownReduction));
        }

        // Rounded away from zero in double precision so a .5 result never depends on float noise; never below 0.
        static int ScaleRounded(int value, float fraction) =>
            Math.Max(0, (int)Math.Round(value * (1.0 + fraction), MidpointRounding.AwayFromZero));
    }
}
```

- [ ] **Step 4: Run to verify it passes.** Both classes. Expected EXIT=0; 2 + 11 = 13 new tests.

- [ ] **Step 5: Commit.**

```bash
git add Assets/_Project/Scripts/Operatives Assets/_Project/Tests/EditMode/OperativeDefinitionTests.cs Assets/_Project/Tests/EditMode/OperativeDefinitionTests.cs.meta Assets/_Project/Tests/EditMode/EffectiveConfigurationTests.cs Assets/_Project/Tests/EditMode/EffectiveConfigurationTests.cs.meta
git commit -m "Add the operative definition and the pure effective-configuration evaluation

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Squad roster

**Files:**
- Create: `Scripts/Operatives/SquadRoster.cs`
- Test: `Tests/EditMode/SquadRosterTests.cs`

**Interfaces:**
- Consumes: `OperativeDefinition`, `PersistentOperativeState`, `ProgressionTrack`, `EffectiveConfiguration` (Tasks 1-3); `MissionPhase` (existing enum in `MissionRuntime.cs`).
- Produces:
  - `sealed class RosterMember { OperativeDefinition Definition; PersistentOperativeState State; string Id; }`
  - `SquadRoster : MonoBehaviour` with `IReadOnlyList<RosterMember> Members`, `int Count`, `ProgressionTrack Track`, `RosterMember Find(string id)`, `EffectiveConfiguration Evaluate(RosterMember)`, `int Rank(RosterMember)`, `int PendingPicks(RosterMember)`, `bool AwardExperience(string id, int amount)` (false for unknown id or amount <= 0), `void AwardMissionCompletion()`, `bool TryPickChoice(string id, string choiceId)`, `bool ResetProgression(string id)`, `IReadOnlyList<PersistentOperativeState> States()`, `event Action<string> Changed` (operative id), `internal void Initialize(OperativeDefinition[] squad, ProgressionTrack progression)`, `internal void HandleMissionFinished(MissionPhase phase)`.
  - Serialized names (Task 9 sets them): `startingSquad`, `track`. (A `director` field is added in Task 7.)

- [ ] **Step 1: Write the failing tests.** `Tests/EditMode/SquadRosterTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class SquadRosterTests
    {
        readonly List<Object> created = new List<Object>();
        ProgressionTrack track;
        OperativeDefinition alpha, bravo, charlie;
        GameObject host;
        SquadRoster roster;

        T Own<T>(T asset) where T : Object
        {
            created.Add(asset);
            return asset;
        }

        [SetUp]
        public void SetUp()
        {
            var combat = Own(AdvancementChoice.Create("combat", "Combat Training", "", new StatModifiers { attackDamage = 0.15f }));
            var hull = Own(AdvancementChoice.Create("survivability", "Reinforced", "", new StatModifiers { maxHealth = 25 }));
            track = Own(ProgressionTrack.Create(new[] { 0, 100, 250, 450, 700 }, new[] { combat, hull }, 150, 50));
            var assault = Own(OperativeRole.Create("Assault", "", new StatModifiers { attackDamage = 0.2f }));
            var archetype = Own(CombatArchetype.Create("Ranged", CombatRole.Ranged, 8f, 15, 1f));
            var prefab = Own(new GameObject("UnitPrefab"));
            alpha = Own(OperativeDefinition.Create("id-alpha", "Alpha", assault, prefab, 130, 5f, archetype, new AbilityDefinition[0]));
            bravo = Own(OperativeDefinition.Create("id-bravo", "Bravo", assault, prefab, 80, 6.5f, archetype, new AbilityDefinition[0]));
            charlie = Own(OperativeDefinition.Create("id-charlie", "Charlie", assault, prefab, 100, 5.5f, archetype, new AbilityDefinition[0]));
            host = Own(new GameObject("Roster"));
            roster = host.AddComponent<SquadRoster>();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in created)
            {
                if (o != null)
                    Object.DestroyImmediate(o);
            }
            created.Clear();
        }

        [Test]
        public void Initialize_MakesOneMemberPerDefinition_InOrder_WithTheDefinitionsIds()
        {
            roster.Initialize(new[] { alpha, bravo, charlie }, track);

            Assert.That(roster.Count, Is.EqualTo(3));
            Assert.That(roster.Members.Select(m => m.Id), Is.EqualTo(new[] { "id-alpha", "id-bravo", "id-charlie" }));
            Assert.That(roster.Members.Select(m => m.Definition), Is.EqualTo(new[] { alpha, bravo, charlie }));
            Assert.That(roster.Members.Select(m => m.State.OperativeId), Is.EqualTo(new[] { "id-alpha", "id-bravo", "id-charlie" }));
            Assert.That(roster.Track, Is.SameAs(track));
            Assert.That(roster.States().Select(s => s.OperativeId), Is.EqualTo(new[] { "id-alpha", "id-bravo", "id-charlie" }));
        }

        [Test]
        public void Initialize_AgainRebuildsFreshStates()
        {
            roster.Initialize(new[] { alpha }, track);
            roster.AwardExperience("id-alpha", 100);
            roster.Initialize(new[] { alpha, bravo }, track);

            Assert.That(roster.Count, Is.EqualTo(2));
            Assert.That(roster.Find("id-alpha").State.Experience, Is.EqualTo(0));
        }

        [Test]
        public void Initialize_RefusesADuplicateId_LoudlyAndKeepsTheFirst()
        {
            var twin = Own(OperativeDefinition.Create("id-alpha", "Twin", alpha.Role, alpha.UnitPrefab, 90, 5f, alpha.Archetype, new AbilityDefinition[0]));
            LogAssert.Expect(LogType.Error, new Regex("id-alpha"));

            roster.Initialize(new[] { alpha, twin, bravo }, track);

            Assert.That(roster.Members.Select(m => m.Definition.DisplayName), Is.EqualTo(new[] { "Alpha", "Bravo" }));
        }

        [Test]
        public void Initialize_RefusesABlankIdOrAMissingDefinition()
        {
            var blank = Own(OperativeDefinition.Create("", "Blank", alpha.Role, alpha.UnitPrefab, 90, 5f, alpha.Archetype, new AbilityDefinition[0]));
            LogAssert.Expect(LogType.Error, new Regex("Blank"));
            LogAssert.Expect(LogType.Error, new Regex("missing"));

            roster.Initialize(new[] { blank, null, bravo }, track);

            Assert.That(roster.Members.Select(m => m.Definition.DisplayName), Is.EqualTo(new[] { "Bravo" }));
        }

        [Test]
        public void Find_ByIdOrNull()
        {
            roster.Initialize(new[] { alpha, bravo }, track);
            Assert.That(roster.Find("id-bravo").Definition, Is.SameAs(bravo));
            Assert.That(roster.Find("nope"), Is.Null);
            Assert.That(roster.Find(null), Is.Null);
        }

        [Test]
        public void AwardExperience_GoesToOneOperativeOnly()
        {
            roster.Initialize(new[] { alpha, bravo, charlie }, track);

            Assert.That(roster.AwardExperience("id-bravo", 120), Is.True);

            Assert.That(roster.Find("id-alpha").State.Experience, Is.EqualTo(0));
            Assert.That(roster.Find("id-bravo").State.Experience, Is.EqualTo(120));
            Assert.That(roster.Find("id-charlie").State.Experience, Is.EqualTo(0));
            Assert.That(roster.Rank(roster.Find("id-bravo")), Is.EqualTo(2));
            Assert.That(roster.Rank(roster.Find("id-alpha")), Is.EqualTo(1));
            Assert.That(roster.PendingPicks(roster.Find("id-bravo")), Is.EqualTo(1));
        }

        [Test]
        public void AwardExperience_UnknownIdOrNonPositiveAmount_IsRefused_AndRaisesNothing()
        {
            roster.Initialize(new[] { alpha }, track);
            var changed = new List<string>();
            roster.Changed += changed.Add;

            Assert.That(roster.AwardExperience("nope", 50), Is.False);
            Assert.That(roster.AwardExperience("id-alpha", 0), Is.False);
            Assert.That(roster.AwardExperience("id-alpha", -5), Is.False);

            Assert.That(changed, Is.Empty);
            Assert.That(roster.Find("id-alpha").State.Experience, Is.EqualTo(0));
        }

        [Test]
        public void Changed_NamesTheOperative_ForXpPicksAndReset()
        {
            roster.Initialize(new[] { alpha, bravo }, track);
            var changed = new List<string>();
            roster.Changed += changed.Add;

            roster.AwardExperience("id-alpha", 100);
            roster.TryPickChoice("id-alpha", "combat");
            roster.ResetProgression("id-alpha");

            Assert.That(changed, Is.EqualTo(new[] { "id-alpha", "id-alpha", "id-alpha" }));
        }

        [Test]
        public void TryPickChoice_NeedsAPendingPick_AndAKnownChoice()
        {
            roster.Initialize(new[] { alpha }, track);
            Assert.That(roster.TryPickChoice("id-alpha", "combat"), Is.False, "no XP yet");
            roster.AwardExperience("id-alpha", 100);
            Assert.That(roster.TryPickChoice("id-alpha", "nope"), Is.False);
            Assert.That(roster.TryPickChoice("nope", "combat"), Is.False);
            Assert.That(roster.TryPickChoice("id-alpha", "combat"), Is.True);
            Assert.That(roster.Find("id-alpha").State.ChoiceIds, Is.EqualTo(new[] { "combat" }));
        }

        [Test]
        public void Evaluate_UsesTheMembersOwnState()
        {
            roster.Initialize(new[] { alpha, bravo }, track);
            roster.AwardExperience("id-alpha", 100);
            roster.TryPickChoice("id-alpha", "survivability");

            Assert.That(roster.Evaluate(roster.Find("id-alpha")).MaxHealth, Is.EqualTo(155));
            Assert.That(roster.Evaluate(roster.Find("id-bravo")).MaxHealth, Is.EqualTo(80));
        }

        [Test]
        public void ResetProgression_AffectsOnlyThatOperative()
        {
            roster.Initialize(new[] { alpha, bravo }, track);
            roster.AwardExperience("id-alpha", 250);
            roster.AwardExperience("id-bravo", 250);

            Assert.That(roster.ResetProgression("id-alpha"), Is.True);
            Assert.That(roster.ResetProgression("nope"), Is.False);

            Assert.That(roster.Find("id-alpha").State.Experience, Is.EqualTo(0));
            Assert.That(roster.Find("id-bravo").State.Experience, Is.EqualTo(250));
        }

        [Test]
        public void AwardMissionCompletion_GivesEveryoneTheTracksAmount_AndRaisesChangedForEach()
        {
            roster.Initialize(new[] { alpha, bravo, charlie }, track);
            var changed = new List<string>();
            roster.Changed += changed.Add;

            roster.AwardMissionCompletion();

            Assert.That(roster.Members.Select(m => m.State.Experience), Is.EqualTo(new[] { 150, 150, 150 }));
            Assert.That(changed, Is.EqualTo(new[] { "id-alpha", "id-bravo", "id-charlie" }));
        }

        [Test]
        public void HandleMissionFinished_AwardsOnSuccessOnly()
        {
            roster.Initialize(new[] { alpha, bravo }, track);

            roster.HandleMissionFinished(MissionPhase.Failure);
            roster.HandleMissionFinished(MissionPhase.Active);
            Assert.That(roster.Members.Select(m => m.State.Experience), Is.EqualTo(new[] { 0, 0 }));

            roster.HandleMissionFinished(MissionPhase.Success);
            Assert.That(roster.Members.Select(m => m.State.Experience), Is.EqualTo(new[] { 150, 150 }));
        }

        [Test]
        public void ANullTrack_WithASquad_LogsAnErrorAndBuildsNothing()
        {
            LogAssert.Expect(LogType.Error, new Regex("progression track"));
            roster.Initialize(new[] { alpha }, null);
            Assert.That(roster.Count, Is.EqualTo(0));
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails.** `Tools/run-tests.sh EditMode "Blackglass.Tests.SquadRosterTests"`. Expected EXIT=1 (`SquadRoster` missing).

- [ ] **Step 3: Implement.** `Scripts/Operatives/SquadRoster.cs`:

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>One operative in the roster: the immutable definition and its own mutable persistent state.</summary>
    public sealed class RosterMember
    {
        internal RosterMember(OperativeDefinition definition, PersistentOperativeState state)
        {
            Definition = definition;
            State = state;
        }

        public OperativeDefinition Definition { get; }
        public PersistentOperativeState State { get; }
        public string Id => State.OperativeId;
    }

    /// <summary>
    /// The current squad of persistent operatives, living in the scene so it outlasts every generated mission (the
    /// director only destroys what it generated). It builds one PersistentOperativeState per starting definition (the id
    /// comes from the definition) and is the only place states are changed: XP awards, advancement picks and resets all
    /// raise Changed(operativeId) so a unit in a running mission can re-apply its configuration. Persistence is in memory
    /// only (decision 036); States() is the plain data a future save will store.
    /// </summary>
    public sealed class SquadRoster : MonoBehaviour
    {
        [SerializeField] OperativeDefinition[] startingSquad = new OperativeDefinition[0];
        [SerializeField] ProgressionTrack track;

        readonly List<RosterMember> members = new List<RosterMember>();

        public IReadOnlyList<RosterMember> Members => members;
        public int Count => members.Count;
        public ProgressionTrack Track => track;

        /// <summary>Raised with the operative's id after its XP, rank, picks or progression changed.</summary>
        public event Action<string> Changed;

        void Awake() => Build();

        internal void Initialize(OperativeDefinition[] squad, ProgressionTrack progression)
        {
            startingSquad = squad;
            track = progression;
            Build();
        }

        void Build()
        {
            members.Clear();
            if (startingSquad == null || startingSquad.Length == 0)
                return;
            if (track == null)
            {
                Debug.LogError($"{name}: the squad roster has no progression track.", this);
                return;
            }
            var ids = new HashSet<string>();
            foreach (var definition in startingSquad)
            {
                if (definition == null)
                {
                    Debug.LogError($"{name}: a starting-squad entry is missing.", this);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(definition.Id))
                {
                    Debug.LogError($"{name}: {definition.DisplayName} has a blank operative id and was skipped.", this);
                    continue;
                }
                if (!ids.Add(definition.Id))
                {
                    Debug.LogError($"{name}: {definition.DisplayName} repeats the operative id {definition.Id} and was skipped.", this);
                    continue;
                }
                members.Add(new RosterMember(definition, new PersistentOperativeState(definition.Id)));
            }
        }

        /// <summary>The member with this id, or null.</summary>
        public RosterMember Find(string id)
        {
            if (string.IsNullOrEmpty(id))
                return null;
            foreach (var member in members)
            {
                if (member.Id == id)
                    return member;
            }
            return null;
        }

        public EffectiveConfiguration Evaluate(RosterMember member) =>
            EffectiveConfiguration.Evaluate(member.Definition, member.State, track);

        public int Rank(RosterMember member) => member.State.Rank(track);

        public int PendingPicks(RosterMember member) => member.State.PendingPicks(track);

        /// <summary>The persistent states in squad order: the data a save would store.</summary>
        public IReadOnlyList<PersistentOperativeState> States()
        {
            var states = new List<PersistentOperativeState>(members.Count);
            foreach (var member in members)
                states.Add(member.State);
            return states;
        }

        /// <summary>Adds XP to one operative. False (and nothing raised) for an unknown id or an amount of 0 or less.</summary>
        public bool AwardExperience(string id, int amount)
        {
            var member = Find(id);
            if (member == null || amount <= 0)
                return false;
            member.State.AddExperience(amount);
            Changed?.Invoke(member.Id);
            return true;
        }

        /// <summary>The track's mission-completion XP to every member, dead or alive (assumption recorded in decision 036).</summary>
        public void AwardMissionCompletion()
        {
            foreach (var member in members)
            {
                member.State.AddExperience(track.MissionCompletionXp);
                Changed?.Invoke(member.Id);
            }
        }

        public bool TryPickChoice(string id, string choiceId)
        {
            var member = Find(id);
            if (member == null || !member.State.TryPickChoice(track, choiceId))
                return false;
            Changed?.Invoke(member.Id);
            return true;
        }

        public bool ResetProgression(string id)
        {
            var member = Find(id);
            if (member == null)
                return false;
            member.State.ResetProgression();
            Changed?.Invoke(member.Id);
            return true;
        }

        internal void HandleMissionFinished(MissionPhase phase)
        {
            if (phase == MissionPhase.Success)
                AwardMissionCompletion();
        }
    }
}
```

- [ ] **Step 4: Run to verify it passes.** Expected EXIT=0, 14 new tests.

- [ ] **Step 5: Commit.**

```bash
git add Assets/_Project/Scripts/Operatives Assets/_Project/Tests/EditMode/SquadRosterTests.cs Assets/_Project/Tests/EditMode/SquadRosterTests.cs.meta
git commit -m "Add the squad roster: persistent states, XP awards and change events

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Hooks in the existing components

**Files:**
- Modify: `Scripts/Combat/Health.cs` (add `SetMax` after `Initialize`)
- Modify: `Scripts/Units/UnitMover.cs` (add `Speed`, `SetSpeed`)
- Modify: `Scripts/Units/UnitAttacker.cs` (add `ApplyEffective`; make `Awake`/`ApplyArchetype` aware of it)
- Modify: `Scripts/Abilities/UnitAbilities.cs` (multipliers at lines 163, 253, 279)
- Test: `Tests/EditMode/HealthSetMaxTests.cs`, `Tests/EditMode/UnitEffectiveSettersTests.cs`, `Tests/PlayMode/UnitAbilitiesModifiersPlayModeTests.cs`

**Interfaces:**
- Produces: `internal void Health.SetMax(int maximum)`; `public float UnitMover.Speed`, `internal void UnitMover.SetSpeed(float)`; `internal void UnitAttacker.ApplyEffective(CombatRole role, float range, int damage, float interval)`; `public float UnitAbilities.PowerMultiplier`, `public float UnitAbilities.CooldownMultiplier`, `internal void UnitAbilities.SetModifiers(float power, float cooldown)`, `public float UnitAbilities.EffectiveCooldown(AbilityDefinition)`, `public int UnitAbilities.EffectiveAmount(AbilityDefinition)`.

- [ ] **Step 1: Write the failing tests.**

`Tests/EditMode/HealthSetMaxTests.cs`:

```csharp
using System;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class HealthSetMaxTests
    {
        GameObject host;
        Health health;

        [SetUp]
        public void SetUp()
        {
            host = new GameObject("HealthSetMax");
            health = host.AddComponent<Health>();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(host);

        [Test]
        public void RaisingTheMax_KeepsTheDamageTaken_SoCurrentRisesByTheDifference()
        {
            health.TakeDamage(30);

            health.SetMax(125);

            Assert.That(health.Max, Is.EqualTo(125));
            Assert.That(health.Current, Is.EqualTo(95));
        }

        [Test]
        public void LoweringTheMax_KeepsTheDamageTaken_WhileTheUnitStaysAlive()
        {
            health.TakeDamage(30);

            health.SetMax(80);

            Assert.That(health.Max, Is.EqualTo(80));
            Assert.That(health.Current, Is.EqualTo(50));
        }

        [Test]
        public void LoweringTheMaxBelowTheDamageTaken_LeavesALivingUnitAtOneHitPoint_NeverDeadNeverZero()
        {
            health.TakeDamage(90);
            var deaths = 0;
            health.Died += () => deaths++;

            health.SetMax(50);

            Assert.That(health.IsAlive, Is.True);
            Assert.That(health.Current, Is.EqualTo(1));
            Assert.That(deaths, Is.EqualTo(0));
        }

        [Test]
        public void ADeadUnit_StaysDead_AtZeroHitPoints()
        {
            health.TakeDamage(100);

            health.SetMax(500);

            Assert.That(health.IsAlive, Is.False);
            Assert.That(health.Current, Is.EqualTo(0));
            Assert.That(health.Heal(10), Is.EqualTo(0));
        }

        [Test]
        public void AFreshUnit_SetMax_IsFullAtTheNewMax()
        {
            health.SetMax(155);
            Assert.That(health.Max, Is.EqualTo(155));
            Assert.That(health.Current, Is.EqualTo(155));
        }

        [Test]
        public void SetMax_BelowOne_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => health.SetMax(0));
            Assert.That(health.Max, Is.EqualTo(100));
        }
    }
}
```

`Tests/EditMode/UnitEffectiveSettersTests.cs`:

```csharp
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class UnitEffectiveSettersTests
    {
        GameObject host;

        [TearDown]
        public void TearDown()
        {
            if (host != null)
                Object.DestroyImmediate(host);
        }

        [Test]
        public void UnitMover_SetSpeed_ChangesTheFieldAndTheAgent()
        {
            host = new GameObject("Mover");
            var mover = host.AddComponent<UnitMover>();

            mover.SetSpeed(6.5f);

            Assert.That(mover.Speed, Is.EqualTo(6.5f));
            Assert.That(host.GetComponent<NavMeshAgent>().speed, Is.EqualTo(6.5f));
        }

        [Test]
        public void UnitMover_SetSpeed_NegativeBecomesZero()
        {
            host = new GameObject("Mover");
            var mover = host.AddComponent<UnitMover>();
            mover.SetSpeed(-3f);
            Assert.That(mover.Speed, Is.EqualTo(0f));
        }

        [Test]
        public void UnitAttacker_ApplyEffective_OverridesTheArchetypeNumbers_ButKeepsTheArchetype()
        {
            host = new GameObject("Attacker");
            var attacker = host.AddComponent<UnitAttacker>();
            var marksman = CombatArchetype.Create("Marksman", CombatRole.Ranged, 16f, 40, 2.5f);
            try
            {
                attacker.ApplyArchetype(marksman);

                attacker.ApplyEffective(CombatRole.Ranged, 16f, 46, 2.5f);

                Assert.That(attacker.Archetype, Is.SameAs(marksman));
                Assert.That(attacker.Damage, Is.EqualTo(46));
                Assert.That(attacker.Range, Is.EqualTo(16f));
                Assert.That(attacker.Cooldown, Is.EqualTo(2.5f));
                Assert.That(attacker.Role, Is.EqualTo(CombatRole.Ranged));
            }
            finally
            {
                Object.DestroyImmediate(marksman);
            }
        }

        [Test]
        public void UnitAttacker_ApplyArchetype_AfterEffective_PutsTheArchetypeBack()
        {
            host = new GameObject("Attacker");
            var attacker = host.AddComponent<UnitAttacker>();
            var melee = CombatArchetype.Create("Melee", CombatRole.Melee, 2f, 25, 1f);
            try
            {
                attacker.ApplyEffective(CombatRole.Ranged, 9f, 99, 3f);
                attacker.ApplyArchetype(melee);

                Assert.That(attacker.Damage, Is.EqualTo(25));
                Assert.That(attacker.Range, Is.EqualTo(2f));
                Assert.That(attacker.Role, Is.EqualTo(CombatRole.Melee));
            }
            finally
            {
                Object.DestroyImmediate(melee);
            }
        }
    }
}
```

`Tests/PlayMode/UnitAbilitiesModifiersPlayModeTests.cs` (copies the arrangement of `UnitAbilitiesPlayModeTests`):

```csharp
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class UnitAbilitiesModifiersPlayModeTests
    {
        TestWorld world;
        Encounter encounter;
        AbilityDefinition aimed;
        AbilityDefinition mend;
        UnitAbilities abilities;
        Health casterHealth;
        Health ally;
        Health hostile;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            world.CreateEnvironment((new Vector3(-3f, 1.5f, -2f), new Vector3(4f, 3f, 0.5f)));
            encounter = world.CreateEncounter();
            aimed = world.CreateAimedShot();
            mend = world.CreateMend();
            var caster = world.CreateFighter(new Vector3(0f, 0f, -6f));
            casterHealth = caster.GetComponent<Health>();
            abilities = world.AddAbilities(caster, encounter, aimed, mend);
            var allyUnit = world.CreateFighter(new Vector3(4f, 0f, -6f));
            ally = allyUnit.GetComponent<Health>();
            hostile = world.CreateDummy(new Vector3(3f, 0f, 2f));
            encounter.Initialize(new[] { casterHealth, ally }, new[] { hostile });
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        [Test]
        public void Defaults_AreOne_AndChangeNothing()
        {
            Assert.That(abilities.PowerMultiplier, Is.EqualTo(1f));
            Assert.That(abilities.CooldownMultiplier, Is.EqualTo(1f));
            Assert.That(abilities.EffectiveAmount(aimed), Is.EqualTo(aimed.Amount));
            Assert.That(abilities.EffectiveCooldown(aimed), Is.EqualTo(aimed.Cooldown));
        }

        [UnityTest]
        public IEnumerator Power_ScalesDamage_AndCooldownMultiplier_ShortensTheCooldown()
        {
            yield return null;
            abilities.SetModifiers(1.2f, 0.5f);

            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(aimed, hostile)), Is.True);

            Assert.That(hostile.Max - hostile.Current, Is.EqualTo(54), "45 * 1.2");
            Assert.That(abilities.CooldownRemaining(0), Is.EqualTo(3f).Within(0.1f), "6 s * 0.5");
        }

        [UnityTest]
        public IEnumerator Power_ScalesHealing()
        {
            yield return null;
            ally.TakeDamage(80);
            abilities.SetModifiers(1.5f, 1f);

            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(mend, ally)), Is.True);

            Assert.That(ally.Current, Is.EqualTo(ally.Max - 80 + 60), "40 * 1.5 = 60 restored");
        }

        [Test]
        public void SetModifiers_NeverGoesNegative()
        {
            abilities.SetModifiers(-1f, -1f);
            Assert.That(abilities.PowerMultiplier, Is.EqualTo(0f));
            Assert.That(abilities.CooldownMultiplier, Is.EqualTo(0f));
        }
    }
}
#endif
```

- [ ] **Step 2: Run to verify it fails.** `Tools/run-tests.sh EditMode "Blackglass.Tests.HealthSetMaxTests"` (EXIT=1: `SetMax` missing). The other two files fail to compile for the same reason, which is expected until Step 3.

- [ ] **Step 3: Implement.**

`Health.cs`, add after `Initialize`:

```csharp
        /// <summary>
        /// Changes the maximum without touching the damage already taken, so a stronger unit gains the difference as health
        /// and a weaker one loses it. A living unit never drops below 1 hit point this way, and a dead one stays dead with
        /// 0 hit points.
        /// </summary>
        internal void SetMax(int maximum)
        {
            if (maximum < 1)
                throw new ArgumentOutOfRangeException(nameof(maximum), maximum, "Maximum health must be at least 1.");
            max = maximum;
            if (hasDied)
                damageTaken = max;
            else if (damageTaken >= max)
                damageTaken = max - 1;
        }
```

`UnitMover.cs`, add after the `PivotHeight` property:

```csharp
        /// <summary>Walking speed in metres per second (what the agent uses).</summary>
        public float Speed => speed;

        /// <summary>Changes the walking speed, also on the agent. Safe before Awake (the unit's hierarchy may still be inactive).</summary>
        internal void SetSpeed(float value)
        {
            speed = Mathf.Max(0f, value);
            Agent.speed = speed;
        }
```

`UnitAttacker.cs`: add a field `bool hasEffective;` next to `ownHealth`; change `Awake` to `if (archetype != null && !hasEffective) ApplyArchetype(archetype);`; in `ApplyArchetype` add `hasEffective = false;` after `archetype = preset;`; add:

```csharp
        /// <summary>
        /// Sets the numbers this unit fights with after role/advancement bonuses (the archetype stays assigned for its name).
        /// Once applied, Awake no longer copies the archetype over them; ApplyArchetype takes the unit back to the preset.
        /// </summary>
        internal void ApplyEffective(CombatRole combatRole, float attackRange, int attackDamage, float attackInterval)
        {
            role = combatRole;
            range = Mathf.Max(0.1f, attackRange);
            damage = Mathf.Max(0, attackDamage);
            cooldown = Mathf.Max(0f, attackInterval);
            hasEffective = true;
        }
```

`UnitAbilities.cs`: add fields and members near the top (after `UnitAttacker attacker;`):

```csharp
        float powerMultiplier = 1f;
        float cooldownMultiplier = 1f;

        /// <summary>Multiplier on this unit's ability damage and healing (1 = as authored).</summary>
        public float PowerMultiplier => powerMultiplier;
        /// <summary>Multiplier on this unit's ability cooldowns (1 = as authored).</summary>
        public float CooldownMultiplier => cooldownMultiplier;

        /// <summary>The per-unit scaling progression applies; the shared AbilityDefinition is never changed.</summary>
        internal void SetModifiers(float power, float cooldown)
        {
            powerMultiplier = Mathf.Max(0f, power);
            cooldownMultiplier = Mathf.Max(0f, cooldown);
        }

        /// <summary>The cooldown this unit actually waits after using the ability, in scaled seconds.</summary>
        public float EffectiveCooldown(AbilityDefinition ability) => ability.Cooldown * cooldownMultiplier;

        /// <summary>The damage or healing this unit's use of the ability deals, rounded away from zero.</summary>
        public int EffectiveAmount(AbilityDefinition ability) =>
            Mathf.Max(0, (int)System.Math.Round(ability.Amount * (double)powerMultiplier, System.MidpointRounding.AwayFromZero));
```

and replace `Time.time + ability.Cooldown` with `Time.time + EffectiveCooldown(ability)`, `target.Heal(ability.Amount)` with `target.Heal(EffectiveAmount(ability))`, and `victim.TakeDamage(ability.Amount, source)` with `victim.TakeDamage(EffectiveAmount(ability), source)`. `UnitAbilities.Initialize` must NOT reset the multipliers.

- [ ] **Step 4: Run to verify it passes.** `Tools/run-tests.sh EditMode "Blackglass.Tests.HealthSetMaxTests"`; `... "Blackglass.Tests.UnitEffectiveSettersTests"`; `Tools/run-tests.sh PlayMode "Blackglass.Tests.UnitAbilitiesModifiersPlayModeTests"`. Then the regression runs `Tools/run-tests.sh EditMode` (full, expected 693 + new) and `Tools/run-tests.sh PlayMode "Blackglass.Tests.UnitAbilitiesPlayModeTests"` plus `...UnitAttackerTests` is in EditMode (covered). New tests: 6 + 4 + 4 = 14.

- [ ] **Step 5: Commit.**

```bash
git add Assets/_Project/Scripts/Combat/Health.cs Assets/_Project/Scripts/Units/UnitMover.cs Assets/_Project/Scripts/Units/UnitAttacker.cs Assets/_Project/Scripts/Abilities/UnitAbilities.cs Assets/_Project/Tests/EditMode/HealthSetMaxTests.cs Assets/_Project/Tests/EditMode/HealthSetMaxTests.cs.meta Assets/_Project/Tests/EditMode/UnitEffectiveSettersTests.cs Assets/_Project/Tests/EditMode/UnitEffectiveSettersTests.cs.meta Assets/_Project/Tests/PlayMode/UnitAbilitiesModifiersPlayModeTests.cs Assets/_Project/Tests/PlayMode/UnitAbilitiesModifiersPlayModeTests.cs.meta
git commit -m "Add the setters progression needs: health max, speed, effective attack, ability scaling

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Unit identity and spawning from the roster

**Files:**
- Create: `Scripts/Operatives/UnitIdentity.cs`
- Modify: `Scripts/Mission/MissionSlots.cs` (add `roster` to `MissionSystems`)
- Modify: `Scripts/Mission/MissionSpawner.cs` (friendly loop)
- Modify: `Scripts/Mission/MissionDirector.cs` (`Run`: friendly count)
- Modify: `Tests/PlayMode/TestSupport/MissionRig.cs` (add `AddRoster`)
- Create: `Tests/PlayMode/TestSupport/OperativeKit.cs`
- Test: `Tests/PlayMode/OperativeSpawnPlayModeTests.cs`

**Interfaces:**
- Consumes: Tasks 1-5.
- Produces:
  - `UnitIdentity : MonoBehaviour` with `string OperativeId`, `OperativeDefinition Definition`, `PersistentOperativeState State`, `string DisplayName`, `string RoleName`, `EffectiveConfiguration Effective`, `bool HasConfiguration`, `internal void Bind(SquadRoster squad, RosterMember operative)`.
  - `MissionSystems.roster` (`SquadRoster`, optional).
  - Test support: `OperativeKit.Build()` returning an `IDisposable` kit with `ProgressionTrack Track`, `OperativeDefinition[] Definitions` (Alpha Assault 130 HP speed 5.0 Ranged `[AimedShot, Blast]`; Bravo Recon 80 HP speed 6.5 Marksman `[AimedShot]`; Charlie Support 100 HP speed 5.5 Ranged `[Mend, AimedShot]`; ids `test-alpha`, `test-bravo`, `test-charlie`; roles Assault +20% attack damage, Recon +0.5 move speed, Support +15% ability power; track `[0,100,250,450,700]`, choices `combat`/`survivability`/`mobility`/`ability` as in the spec, mission XP 150, debug step 50); `MissionRig.AddRoster(OperativeDefinition[] squad, ProgressionTrack track)` returning the `SquadRoster`.

- [ ] **Step 1: Write the test-support code and the failing tests.**

`Tests/PlayMode/TestSupport/OperativeKit.cs`:

```csharp
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>
    /// A small in-memory squad of three differently configured operatives, built from the project's real unit prefab,
    /// archetypes and abilities, with a progression track shaped like the shipped one. Nothing is written to disk.
    /// </summary>
    internal sealed class OperativeKit : IDisposable
    {
        readonly List<UnityEngine.Object> created = new List<UnityEngine.Object>();

        public ProgressionTrack Track { get; private set; }
        public OperativeDefinition[] Definitions { get; private set; }

        public static OperativeKit Build()
        {
            var kit = new OperativeKit();
            var combat = kit.Own(AdvancementChoice.Create("combat", "Combat Training", "+15% attack damage", new StatModifiers { attackDamage = 0.15f }));
            var hull = kit.Own(AdvancementChoice.Create("survivability", "Reinforced", "+25 max health", new StatModifiers { maxHealth = 25 }));
            var speed = kit.Own(AdvancementChoice.Create("mobility", "Fleet-footed", "+0.75 m/s", new StatModifiers { moveSpeed = 0.75f }));
            var focus = kit.Own(AdvancementChoice.Create("ability", "Focus", "+20% ability power, -15% cooldown",
                new StatModifiers { abilityPower = 0.2f, abilityCooldownReduction = 0.15f }));
            kit.Track = kit.Own(ProgressionTrack.Create(new[] { 0, 100, 250, 450, 700 }, new[] { combat, hull, speed, focus }, 150, 50));

            var assault = kit.Own(OperativeRole.Create("Assault", "", new StatModifiers { attackDamage = 0.2f }));
            var recon = kit.Own(OperativeRole.Create("Recon", "", new StatModifiers { moveSpeed = 0.5f }));
            var support = kit.Own(OperativeRole.Create("Support", "", new StatModifiers { abilityPower = 0.15f }));

            var prefab = Load<GameObject>("Prefabs/FriendlyUnit.prefab");
            var ranged = Load<CombatArchetype>("Data/Archetypes/Ranged.asset");
            var marksman = Load<CombatArchetype>("Data/Archetypes/Marksman.asset");
            var aimed = Load<AbilityDefinition>("Data/Abilities/AimedShot.asset");
            var blast = Load<AbilityDefinition>("Data/Abilities/Blast.asset");
            var mend = Load<AbilityDefinition>("Data/Abilities/Mend.asset");

            kit.Definitions = new[]
            {
                kit.Own(OperativeDefinition.Create("test-alpha", "Alpha", assault, prefab, 130, 5f, ranged, new[] { aimed, blast })),
                kit.Own(OperativeDefinition.Create("test-bravo", "Bravo", recon, prefab, 80, 6.5f, marksman, new[] { aimed })),
                kit.Own(OperativeDefinition.Create("test-charlie", "Charlie", support, prefab, 100, 5.5f, ranged, new[] { mend, aimed })),
            };
            return kit;
        }

        T Own<T>(T asset) where T : UnityEngine.Object
        {
            created.Add(asset);
            return asset;
        }

        static T Load<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>("Assets/_Project/" + path);
            if (asset == null)
                throw new InvalidOperationException("Missing asset " + path);
            return asset;
        }

        public void Dispose()
        {
            foreach (var asset in created)
            {
                if (asset != null)
                    UnityEngine.Object.DestroyImmediate(asset);
            }
            created.Clear();
        }
    }
}
#endif
```

`MissionRig.cs`: add

```csharp
        /// <summary>
        /// Adds a squad roster to the persistent systems: the director then spawns the squad from it. The roster is built
        /// while its object is inactive, so nothing runs before it is wired.
        /// </summary>
        public SquadRoster AddRoster(OperativeDefinition[] squad, ProgressionTrack track)
        {
            var host = World.Track(new GameObject("Roster"));
            host.SetActive(false);
            var roster = host.AddComponent<SquadRoster>();
            roster.Initialize(squad, track);
            host.SetActive(true);
            systems.roster = roster;
            return roster;
        }
```

`Tests/PlayMode/OperativeSpawnPlayModeTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class OperativeSpawnPlayModeTests
    {
        MissionRig rig;
        OperativeKit kit;
        SquadRoster roster;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
            kit?.Dispose();
            kit = null;
        }

        IEnumerator Start(int seed = 12345)
        {
            rig = new MissionRig();
            kit = OperativeKit.Build();
            roster = rig.AddRoster(kit.Definitions, kit.Track);
            yield return rig.Generate(seed);
            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", rig.Director.Report.Failures));
        }

        [UnityTest]
        public IEnumerator Spawn_MakesOneUnitPerOperative_BoundToItsPersistentState()
        {
            yield return Start();
            var friendlies = rig.Director.Friendlies;

            Assert.That(friendlies, Has.Count.EqualTo(3));
            for (var i = 0; i < 3; i++)
            {
                var identity = friendlies[i].GetComponent<UnitIdentity>();
                Assert.That(identity, Is.Not.Null, friendlies[i].name);
                Assert.That(identity.OperativeId, Is.EqualTo(kit.Definitions[i].Id));
                Assert.That(identity.State, Is.SameAs(roster.Members[i].State));
                Assert.That(identity.Definition, Is.SameAs(kit.Definitions[i]));
                Assert.That(identity.DisplayName, Is.EqualTo(kit.Definitions[i].DisplayName));
                Assert.That(friendlies[i].name, Is.EqualTo($"FriendlyUnit_{i + 1}"), "scene object names are unchanged");
            }
            Assert.That(rig.Active.Unit, Is.SameAs(friendlies[0]));
        }

        [UnityTest]
        public IEnumerator Spawn_AppliesEachOperativesEffectiveConfiguration_ToTheRealComponents()
        {
            yield return Start();

            for (var i = 0; i < 3; i++)
            {
                var unit = rig.Director.Friendlies[i];
                var effective = roster.Evaluate(roster.Members[i]);
                var health = unit.GetComponent<Health>();
                var attacker = unit.GetComponent<UnitAttacker>();

                Assert.That(health.Max, Is.EqualTo(effective.MaxHealth), unit.name);
                Assert.That(health.Current, Is.EqualTo(effective.MaxHealth), unit.name);
                Assert.That(unit.GetComponent<UnitMover>().Speed, Is.EqualTo(effective.MoveSpeed).Within(0.001f), unit.name);
                Assert.That(unit.GetComponent<UnityEngine.AI.NavMeshAgent>().speed, Is.EqualTo(effective.MoveSpeed).Within(0.001f), unit.name);
                Assert.That(attacker.Damage, Is.EqualTo(effective.AttackDamage), unit.name + " (Awake must not reset the archetype damage)");
                Assert.That(attacker.Range, Is.EqualTo(effective.AttackRange), unit.name);
                Assert.That(attacker.Cooldown, Is.EqualTo(effective.AttackInterval), unit.name);
                Assert.That(attacker.Role, Is.EqualTo(effective.AttackRole), unit.name);
                Assert.That(unit.GetComponent<UnitIdentity>().Effective.MaxHealth, Is.EqualTo(effective.MaxHealth));
            }
        }

        [UnityTest]
        public IEnumerator TheThreeOperatives_PlayDifferently()
        {
            yield return Start();
            var units = rig.Director.Friendlies;

            Assert.That(units.Select(u => u.GetComponent<Health>().Max), Is.EqualTo(new[] { 130, 80, 100 }));
            Assert.That(units.Select(u => u.GetComponent<UnitMover>().Speed).ToArray(),
                Is.EqualTo(new[] { 5f, 7f, 5.5f }).Within(0.001f), "Recon's role adds 0.5 m/s to 6.5");
            Assert.That(units.Select(u => u.GetComponent<UnitAttacker>().Range).ToArray(), Is.EqualTo(new[] { 8f, 16f, 8f }));
            Assert.That(units.Select(u => u.GetComponent<UnitAbilities>().Count), Is.EqualTo(new[] { 2, 1, 2 }));
            Assert.That(units[2].GetComponent<UnitAbilities>().Definition(0).DisplayName, Is.EqualTo("Mend"));
            Assert.That(units[2].GetComponent<UnitAbilities>().PowerMultiplier, Is.EqualTo(1.15f).Within(0.001f), "Support's role bonus");
            Assert.That(units[0].GetComponent<UnitAbilities>().PowerMultiplier, Is.EqualTo(1f));
        }

        [UnityTest]
        public IEnumerator HostilesAreNotOperatives()
        {
            yield return Start();
            foreach (var hostile in rig.Director.Hostiles)
                Assert.That(hostile.TryGetComponent<UnitIdentity>(out _), Is.False, hostile.name);
        }

        [UnityTest]
        public IEnumerator ALivePick_ChangesTheRunningUnit_KeepingDamageTaken()
        {
            yield return Start();
            var unit = rig.Director.Friendlies[0];
            var health = unit.GetComponent<Health>();
            var attacker = unit.GetComponent<UnitAttacker>();
            health.TakeDamage(30);
            var damageBefore = attacker.Damage;

            roster.AwardExperience("test-alpha", 100);
            Assert.That(roster.TryPickChoice("test-alpha", "survivability"), Is.True);

            Assert.That(health.Max, Is.EqualTo(155));
            Assert.That(health.Current, Is.EqualTo(125), "the 30 damage taken stays taken");

            roster.AwardExperience("test-alpha", 150);
            Assert.That(roster.TryPickChoice("test-alpha", "combat"), Is.True);
            Assert.That(attacker.Damage, Is.GreaterThan(damageBefore));
            Assert.That(attacker.Damage, Is.EqualTo(roster.Evaluate(roster.Members[0]).AttackDamage));
        }

        [UnityTest]
        public IEnumerator ADeadUnit_IgnoresLaterRosterChanges_WithoutErrors()
        {
            yield return Start();
            var unit = rig.Director.Friendlies[1];
            var identity = unit.GetComponent<UnitIdentity>();
            var maxBefore = identity.Effective.MaxHealth;
            var health = unit.GetComponent<Health>();
            health.TakeDamage(health.Max);
            Assert.That(unit.gameObject.activeSelf, Is.False, "a dead unit is deactivated");

            Assert.DoesNotThrow(() =>
            {
                roster.AwardExperience("test-bravo", 100);
                roster.TryPickChoice("test-bravo", "survivability");
            });

            Assert.That(roster.Find("test-bravo").State.Experience, Is.EqualTo(100), "the persistent record is updated");
            Assert.That(identity.Effective.MaxHealth, Is.EqualTo(maxBefore), "the corpse is not re-configured");
            Assert.That(identity.OperativeId, Is.EqualTo("test-bravo"), "identity survives death");
        }

        [UnityTest]
        public IEnumerator WithoutARoster_TheLegacySlotsStillSpawnTheSquad()
        {
            rig = new MissionRig();
            yield return rig.Generate(12345);
            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready));
            Assert.That(rig.Director.Friendlies, Has.Count.EqualTo(3));
            foreach (var unit in rig.Director.Friendlies)
                Assert.That(unit.TryGetComponent<UnitIdentity>(out _), Is.False);
            Assert.That(rig.Director.Friendlies[1].GetComponent<UnitAttacker>().Archetype.DisplayName, Is.EqualTo("Marksman"));
        }

        [UnityTest]
        public IEnumerator ARosterOfTwo_ProducesATwoMemberSquad()
        {
            rig = new MissionRig();
            kit = OperativeKit.Build();
            roster = rig.AddRoster(kit.Definitions.Take(2).ToArray(), kit.Track);
            yield return rig.Generate(12345);

            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", rig.Director.Report.Failures));
            Assert.That(rig.Director.Friendlies, Has.Count.EqualTo(2));
            Assert.That(rig.Director.Friendlies.Select(u => u.GetComponent<UnitIdentity>().OperativeId), Is.EqualTo(new[] { "test-alpha", "test-bravo" }));
        }
    }
}
#endif
```

- [ ] **Step 2: Run to verify it fails.** `Tools/run-tests.sh PlayMode "Blackglass.Tests.OperativeSpawnPlayModeTests"`. Expected EXIT=1: `UnitIdentity` / `MissionSystems.roster` do not exist.

- [ ] **Step 3: Implement.**

`Scripts/Operatives/UnitIdentity.cs`:

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Ties a mission unit to a persistent operative. The unit is disposable and keeps no progression: the operative's
    /// definition and state live in the SquadRoster, and this component only remembers which member it is and applies the
    /// member's effective configuration to the unit's own components (Health, UnitMover, UnitAttacker, UnitAbilities).
    /// While the unit is active it re-applies whenever the roster reports a change to its operative, so a level-up or
    /// a pick shows in a running mission. A deactivated (dead) unit hears nothing and is not re-configured.
    /// </summary>
    public sealed class UnitIdentity : MonoBehaviour
    {
        SquadRoster roster;
        RosterMember member;
        bool subscribed;

        public string OperativeId => member != null ? member.Id : string.Empty;
        public OperativeDefinition Definition => member != null ? member.Definition : null;
        public PersistentOperativeState State => member != null ? member.State : null;
        public string DisplayName => member != null ? member.Definition.DisplayName : name;
        public string RoleName => member != null && member.Definition.Role != null ? member.Definition.Role.DisplayName : "-";
        /// <summary>The configuration last applied to this unit.</summary>
        public EffectiveConfiguration Effective { get; private set; }
        public bool HasConfiguration { get; private set; }

        /// <summary>Binds the unit to a roster member and applies its configuration now. Safe on an inactive object.</summary>
        internal void Bind(SquadRoster squad, RosterMember operative)
        {
            Unsubscribe();
            roster = squad;
            member = operative;
            Reapply();
            if (isActiveAndEnabled)
                Subscribe();
        }

        void OnEnable() => Subscribe();

        void OnDisable() => Unsubscribe();

        void Subscribe()
        {
            if (subscribed || roster == null)
                return;
            roster.Changed += OnRosterChanged;
            subscribed = true;
        }

        void Unsubscribe()
        {
            if (!subscribed)
                return;
            if (roster != null)
                roster.Changed -= OnRosterChanged;
            subscribed = false;
        }

        void OnRosterChanged(string operativeId)
        {
            if (member != null && operativeId == member.Id)
                Reapply();
        }

        void Reapply()
        {
            if (roster == null || member == null)
                return;
            Effective = roster.Evaluate(member);
            HasConfiguration = true;
            Apply(Effective);
        }

        void Apply(EffectiveConfiguration config)
        {
            if (TryGetComponent<Health>(out var health))
                health.SetMax(config.MaxHealth);
            if (TryGetComponent<UnitMover>(out var mover))
                mover.SetSpeed(config.MoveSpeed);
            if (TryGetComponent<UnitAttacker>(out var attacker))
                attacker.ApplyEffective(config.AttackRole, config.AttackRange, config.AttackDamage, config.AttackInterval);
            if (TryGetComponent<UnitAbilities>(out var abilities))
                abilities.SetModifiers(config.AbilityPower, config.AbilityCooldownMultiplier);
        }
    }
}
```

`MissionSlots.cs`: update the `MissionSystems` summary to say `roster` is optional too and add the field `public SquadRoster roster;` at the end (after `interactables`).

`MissionSpawner.cs`: in `TrySpawn` replace

```csharp
            if (friendlySlots.Count == 0 || hostileSlots.Count == 0)
            {
                failure = "the director has no friendly or no hostile slots";
                return false;
            }
```
with
```csharp
            var roster = systems.roster != null && systems.roster.Count > 0 ? systems.roster : null;
            if ((roster == null && friendlySlots.Count == 0) || hostileSlots.Count == 0)
            {
                failure = "the director has no friendly (or roster) or no hostile slots";
                return false;
            }
```
and replace the first lines of the friendly loop body

```csharp
                var slot = friendlySlots[i % friendlySlots.Count];
```
with
```csharp
                RosterMember member = null;
                FriendlySlot slot;
                if (roster != null)
                {
                    if (i >= roster.Count)
                    {
                        failure = $"the layout has {layout.FriendlySpawns.Count} friendly spawns but the roster has only {roster.Count} operatives";
                        return false;
                    }
                    member = roster.Members[i];
                    slot = new FriendlySlot { prefab = member.Definition.UnitPrefab, archetype = member.Definition.Archetype, abilities = member.Definition.Abilities };
                }
                else
                {
                    slot = friendlySlots[i % friendlySlots.Count];
                }
```
and, right after the `UnitAbilities` initialisation `if (...) unit.gameObject.AddComponent<UnitAbilities>()...;` add:
```csharp
                if (member != null)
                    unit.gameObject.AddComponent<UnitIdentity>().Bind(roster, member);   // after archetype and abilities: it scales them
```
Update the class summary sentence to mention the roster ("Friendlies come from the roster when one is set, else from the slots").

`MissionDirector.cs` in `Run`, replace
```csharp
            if (friendlySlots.Length > 0)
                request.friendlyCount = Mathf.Clamp(friendlySlots.Length, 1, 6);
```
with
```csharp
            if (systems.roster != null && systems.roster.Count > 0)
                request.friendlyCount = Mathf.Clamp(systems.roster.Count, 1, 6);
            else if (friendlySlots.Length > 0)
                request.friendlyCount = Mathf.Clamp(friendlySlots.Length, 1, 6);
```

- [ ] **Step 4: Run to verify it passes.** `Tools/run-tests.sh PlayMode "Blackglass.Tests.OperativeSpawnPlayModeTests"` (8 new tests). Then the regression: `Tools/run-tests.sh EditMode` (full) and `Tools/run-tests.sh PlayMode "Blackglass.Tests.MissionDirectorPlayModeTests"` and `"Blackglass.Tests.DariusMissionPlayModeTests"`. All green. If the `Awake`-clobber assertion on `attacker.Damage` fails, `UnitAttacker.hasEffective` is not being honoured; fix there, not in the test.

- [ ] **Step 5: Commit.**

```bash
git add Assets/_Project/Scripts/Operatives Assets/_Project/Scripts/Mission/MissionSlots.cs Assets/_Project/Scripts/Mission/MissionSpawner.cs Assets/_Project/Scripts/Mission/MissionDirector.cs Assets/_Project/Tests/PlayMode/TestSupport Assets/_Project/Tests/PlayMode/OperativeSpawnPlayModeTests.cs Assets/_Project/Tests/PlayMode/OperativeSpawnPlayModeTests.cs.meta
git commit -m "Spawn the squad from the roster and apply each operative's effective configuration

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Mission-completion XP, persistence across missions, health and death boundary, determinism

**Files:**
- Modify: `Scripts/Mission/MissionDirector.cs` (`MissionFinished` event)
- Modify: `Scripts/Operatives/SquadRoster.cs` (director subscription)
- Modify: `Tests/PlayMode/TestSupport/MissionRig.cs` (`AddRoster` passes the director)
- Test: `Tests/PlayMode/OperativePersistencePlayModeTests.cs`

**Interfaces:**
- Consumes: Tasks 1-6; `MissionRuntime.PhaseChanged` (existing).
- Produces: `public event Action<MissionPhase> MissionFinished` on `MissionDirector` (raised once, with `Success` or `Failure`, when the running mission's phase becomes terminal); `SquadRoster` serialized field `director` (`MissionDirector`, optional) and `internal void Initialize(OperativeDefinition[] squad, ProgressionTrack progression, MissionDirector missionDirector = null)`.

- [ ] **Step 1: Write the failing tests.** `Tests/PlayMode/OperativePersistencePlayModeTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>What survives a mission and what does not: persistent XP and picks do; runtime units and health do not.</summary>
    public class OperativePersistencePlayModeTests
    {
        MissionRig rig;
        OperativeKit kit;
        SquadRoster roster;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
            kit?.Dispose();
            kit = null;
        }

        MissionDirector Director => rig.Director;

        IEnumerator Start(MissionSettings settings = null, int seed = 12345)
        {
            rig = new MissionRig(settings);
            kit = OperativeKit.Build();
            roster = rig.AddRoster(kit.Definitions, kit.Track);
            yield return rig.Generate(seed);
            Assert.That(Director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", Director.Report.Failures));
        }

        static void Kill(Component unit)
        {
            var health = unit.GetComponent<Health>();
            health.TakeDamage(health.Max);
        }

        void DisarmHostiles()
        {
            foreach (var hostile in Director.Hostiles)
                hostile.GetComponent<EnemyAI>().enabled = false;
        }

        IEnumerator CompleteTheMission()
        {
            DisarmHostiles();
            foreach (var hostile in Director.Hostiles)
                Kill(hostile);
            yield return null;
            yield return null;
            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.ExtractionOpen));
            Assert.That(Director.Friendlies[0].GetComponent<NavMeshAgent>().Warp(Director.Current.ExtractionZone.position), Is.True);
            yield return null;
            yield return null;
            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.Success));
        }

        [UnityTest]
        public IEnumerator XpAndPicks_SurviveRegeneration_AndTheNewUnitsAreConfiguredFromThem()
        {
            yield return Start();
            roster.AwardExperience("test-alpha", 250);
            Assert.That(roster.TryPickChoice("test-alpha", "survivability"), Is.True);
            Assert.That(roster.TryPickChoice("test-alpha", "combat"), Is.True);
            var stateBefore = roster.Find("test-alpha").State;
            var oldUnit = Director.Friendlies[0];

            yield return rig.Generate(777);

            Assert.That(oldUnit == null, Is.True, "the mission unit was destroyed with its mission");
            var newUnit = Director.Friendlies[0];
            var identity = newUnit.GetComponent<UnitIdentity>();
            Assert.That(identity.OperativeId, Is.EqualTo("test-alpha"));
            Assert.That(identity.State, Is.SameAs(stateBefore), "the same persistent record, not a copy");
            Assert.That(stateBefore.Experience, Is.EqualTo(250));
            Assert.That(stateBefore.ChoiceIds, Is.EqualTo(new[] { "survivability", "combat" }));
            Assert.That(newUnit.GetComponent<Health>().Max, Is.EqualTo(155));
            Assert.That(newUnit.GetComponent<UnitAttacker>().Damage, Is.EqualTo(roster.Evaluate(roster.Members[0]).AttackDamage));
        }

        [UnityTest]
        public IEnumerator XpIsTrackedPerOperative_AndOthersAreUnaffected()
        {
            yield return Start();

            roster.AwardExperience("test-bravo", 120);
            roster.TryPickChoice("test-bravo", "mobility");

            Assert.That(roster.Members.Select(m => m.State.Experience), Is.EqualTo(new[] { 0, 120, 0 }));
            var units = Director.Friendlies;
            Assert.That(units[0].GetComponent<UnitMover>().Speed, Is.EqualTo(5f).Within(0.001f));
            Assert.That(units[1].GetComponent<UnitMover>().Speed, Is.EqualTo(6.5f + 0.5f + 0.75f).Within(0.001f));
            Assert.That(units[2].GetComponent<UnitMover>().Speed, Is.EqualTo(5.5f).Within(0.001f));
            Assert.That(units[0].GetComponent<UnitIdentity>().Effective.Rank, Is.EqualTo(1));
            Assert.That(units[1].GetComponent<UnitIdentity>().Effective.Rank, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator ADeadOperative_KeepsItsPersistentIdentity_AndStartsTheNextMissionAtFullEffectiveHealth()
        {
            yield return Start();
            roster.AwardExperience("test-charlie", 100);
            roster.TryPickChoice("test-charlie", "survivability");
            var charlie = Director.Friendlies[2];
            var damaged = Director.Friendlies[0];
            damaged.GetComponent<Health>().TakeDamage(60);
            Kill(charlie);
            Assert.That(charlie.GetComponent<Health>().IsAlive, Is.False);

            var record = roster.Find("test-charlie");
            Assert.That(record, Is.Not.Null, "death does not remove the operative from the roster");
            Assert.That(record.State.Experience, Is.EqualTo(100));
            Assert.That(record.State.ChoiceIds, Is.EqualTo(new[] { "survivability" }));
            Assert.That(charlie.GetComponent<UnitIdentity>().OperativeId, Is.EqualTo("test-charlie"), "the corpse still knows who it was");

            Assert.That(Director.RegenerateSame(), Is.True);
            yield return TestWorld.WaitUntil(() => Director.State == MissionState.Ready, 20f);

            foreach (var unit in Director.Friendlies)
            {
                var health = unit.GetComponent<Health>();
                var effective = roster.Evaluate(roster.Find(unit.GetComponent<UnitIdentity>().OperativeId));
                Assert.That(health.IsAlive, Is.True, unit.name);
                Assert.That(health.Current, Is.EqualTo(effective.MaxHealth), unit.name + ": runtime damage does not carry over");
                Assert.That(health.Max, Is.EqualTo(effective.MaxHealth), unit.name);
            }
            Assert.That(Director.Friendlies[2].GetComponent<Health>().Max, Is.EqualTo(125), "Charlie's persistent pick is still in force");
        }

        [UnityTest]
        public IEnumerator Success_AwardsTheTracksXpToEveryOperative_Once_DeadOnesIncluded()
        {
            yield return Start(new MissionSettings { hackTerminal = false });
            Kill(Director.Friendlies[2]);   // a casualty; the mission still succeeds with the others

            yield return CompleteTheMission();
            yield return new WaitForSeconds(0.3f);

            Assert.That(roster.Members.Select(m => m.State.Experience), Is.EqualTo(new[] { 150, 150, 150 }));
        }

        [UnityTest]
        public IEnumerator Failure_AwardsNothing()
        {
            yield return Start();
            foreach (var friendly in Director.Friendlies)
                Kill(friendly);
            yield return null;
            yield return null;
            yield return null;

            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.Failure));
            Assert.That(roster.Members.Select(m => m.State.Experience), Is.EqualTo(new[] { 0, 0, 0 }));
        }

        [UnityTest]
        public IEnumerator MissionFinished_IsRaisedOncePerMission()
        {
            yield return Start(new MissionSettings { hackTerminal = false });
            var finished = new System.Collections.Generic.List<MissionPhase>();
            Director.MissionFinished += finished.Add;

            yield return CompleteTheMission();
            foreach (var friendly in Director.Friendlies)
                Kill(friendly);
            yield return null;
            yield return null;

            Assert.That(finished, Is.EqualTo(new[] { MissionPhase.Success }), "success is terminal: later deaths raise nothing");
        }

        [UnityTest]
        public IEnumerator ARegeneratedMission_CanAwardAgain()
        {
            yield return Start(new MissionSettings { hackTerminal = false });
            yield return CompleteTheMission();
            Assert.That(roster.Members[0].State.Experience, Is.EqualTo(150));

            yield return rig.Generate(777);
            yield return CompleteTheMission();

            Assert.That(roster.Members[0].State.Experience, Is.EqualTo(300));
        }

        // Progression must never leak into generation: same seed, wildly different XP and picks, identical mission.
        [UnityTest]
        public IEnumerator TheSameSeed_GeneratesTheSameMission_WhateverTheOperativesHaveEarned()
        {
            yield return Start(seed: 777);
            var layoutHash = Director.Report.LayoutHash;
            var objectiveHash = Director.Report.ObjectiveHash;
            var friendlySpawns = Director.Current.Layout.FriendlySpawns.ToArray();
            var hostileSpawns = Director.Current.Layout.HostileSpawns.ToArray();

            foreach (var member in roster.Members)
            {
                roster.AwardExperience(member.Id, 700);
                roster.TryPickChoice(member.Id, "survivability");
                roster.TryPickChoice(member.Id, "mobility");
                roster.TryPickChoice(member.Id, "combat");
                roster.TryPickChoice(member.Id, "ability");
            }
            Assert.That(roster.Members.All(m => roster.PendingPicks(m) == 0), Is.True);

            yield return rig.Generate(777);

            Assert.That(Director.State, Is.EqualTo(MissionState.Ready));
            Assert.That(Director.Report.LayoutHash, Is.EqualTo(layoutHash));
            Assert.That(Director.Report.ObjectiveHash, Is.EqualTo(objectiveHash));
            Assert.That(Director.Current.Layout.FriendlySpawns.ToArray(), Is.EqualTo(friendlySpawns));
            Assert.That(Director.Current.Layout.HostileSpawns.ToArray(), Is.EqualTo(hostileSpawns));
        }
    }
}
#endif
```

- [ ] **Step 2: Run to verify it fails.** `Tools/run-tests.sh PlayMode "Blackglass.Tests.OperativePersistencePlayModeTests"`. Expected EXIT=1: `MissionFinished` missing.

- [ ] **Step 3: Implement.**

`MissionDirector.cs`:
- Add next to `StateChanged`: `/// <summary>Raised once when the running mission ends, with Success or Failure.</summary>` `public event Action<MissionPhase> MissionFinished;`
- In `TryAttempt`, after `Runtime.Start();` add `Runtime.PhaseChanged += OnRuntimePhaseChanged;`.
- In `DetachRuntime`, before `Runtime.Detach();` add `Runtime.PhaseChanged -= OnRuntimePhaseChanged;`.
- Add the handler:

```csharp
        void OnRuntimePhaseChanged(MissionPhase phase)
        {
            if (phase == MissionPhase.Success || phase == MissionPhase.Failure)
                MissionFinished?.Invoke(phase);
        }
```

`SquadRoster.cs`:
- Add `[SerializeField] MissionDirector director;` and `bool subscribed;`.
- Replace `Initialize` with:

```csharp
        internal void Initialize(OperativeDefinition[] squad, ProgressionTrack progression, MissionDirector missionDirector = null)
        {
            Unsubscribe();
            startingSquad = squad;
            track = progression;
            director = missionDirector;
            Build();
            if (isActiveAndEnabled)
                Subscribe();
        }

        void OnEnable() => Subscribe();

        void OnDisable() => Unsubscribe();

        void Subscribe()
        {
            if (subscribed || director == null)
                return;
            director.MissionFinished += HandleMissionFinished;
            subscribed = true;
        }

        void Unsubscribe()
        {
            if (!subscribed)
                return;
            if (director != null)
                director.MissionFinished -= HandleMissionFinished;
            subscribed = false;
        }
```
- Update the class summary: "A serialized MissionDirector (optional) makes it award mission-completion XP when a mission succeeds."

`MissionRig.AddRoster`: call `roster.Initialize(squad, track, Director);`.

- [ ] **Step 4: Run to verify it passes.** `Tools/run-tests.sh PlayMode "Blackglass.Tests.OperativePersistencePlayModeTests"` (8 new tests). Regression: `Tools/run-tests.sh PlayMode "Blackglass.Tests.MissionRuntimePlayModeTests"`, `"Blackglass.Tests.MissionDirectorPlayModeTests"`, `"Blackglass.Tests.OperativeSpawnPlayModeTests"`; `Tools/run-tests.sh EditMode` (full).

- [ ] **Step 5: Commit.**

```bash
git add Assets/_Project/Scripts/Operatives Assets/_Project/Scripts/Mission/MissionDirector.cs Assets/_Project/Tests/PlayMode/TestSupport/MissionRig.cs Assets/_Project/Tests/PlayMode/OperativePersistencePlayModeTests.cs Assets/_Project/Tests/PlayMode/OperativePersistencePlayModeTests.cs.meta
git commit -m "Award mission XP on success; prove persistence, the health boundary and determinism

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Debug panel, developer keys and unit-label names

**Files:**
- Modify: `Input/BlackglassControls.inputactions` (Developer map)
- Create: `Scripts/Operatives/OperativePanelView.cs`, `Scripts/Operatives/OperativeDeveloperInput.cs`
- Modify: `Scripts/DebugUI/PrototypeHud.cs` (`DescribeOperative`, use in `DrawUnitLabel`)
- Test: `Tests/EditMode/OperativePanelTextTests.cs`, `Tests/EditMode/OperativeInputAssetTests.cs`, `Tests/PlayMode/OperativeDeveloperInputTests.cs`

**Interfaces:**
- Consumes: Tasks 1-7; `ActiveCharacter.Unit`; `MissionDirector.Friendlies`; `InputActionUtility.SetEnabled`.
- Produces:
  - Developer actions `ToggleOperativePanel` (`<Keyboard>/f9`), `AddExperience` (`<Keyboard>/f10`), `ResetProgression` (`<Keyboard>/f11`), all `Button`, group `KeyboardMouse`.
  - `OperativePanelView : MonoBehaviour`: `internal void Initialize(SquadRoster roster, ActiveCharacter active, MissionDirector director)`, `bool IsVisible`, `void SetVisible(bool)`, `void Toggle()`, and `internal static` text builders: `ShortId(string)`, `DescribeHeader(string displayName, string roleName, string id)`, `DescribeProgress(int rank, int maxRank, int experience, int? nextThreshold)`, `DescribeStat(string label, string baseValue, string effectiveValue)`, `DescribePicks(IReadOnlyList<string> pickNames, int pending)`, `DescribeStatus(bool deployed, bool alive)`, `DescribeRosterRow(string displayName, string roleName, int rank, int experience, string status)`.
  - `OperativeDeveloperInput : MonoBehaviour`: `internal void Initialize(SquadRoster, ActiveCharacter, OperativePanelView, InputActionReference toggle, InputActionReference addExperience, InputActionReference reset)`. Serialized names: `roster`, `activeCharacter`, `panel`, `toggleAction`, `addExperienceAction`, `resetAction`.
  - `PrototypeHud.DescribeOperative(string displayName, string roleName)` returning `"Darius (Assault)"`.
  - `OperativePanelView` serialized names: `roster`, `activeCharacter`, `director`.

- [ ] **Step 1: Write the failing tests.**

`Tests/EditMode/OperativePanelTextTests.cs`:

```csharp
using NUnit.Framework;

namespace Blackglass.Tests
{
    public class OperativePanelTextTests
    {
        [Test]
        public void ShortId_IsTheFirstEightCharacters_OrADashWhenEmpty()
        {
            Assert.That(OperativePanelView.ShortId("6f1d2a40-3b7c-4e95-8a1f-52c0d9e7b301"), Is.EqualTo("6f1d2a40"));
            Assert.That(OperativePanelView.ShortId("abc"), Is.EqualTo("abc"));
            Assert.That(OperativePanelView.ShortId(""), Is.EqualTo("-"));
            Assert.That(OperativePanelView.ShortId(null), Is.EqualTo("-"));
        }

        [Test]
        public void DescribeHeader_NameRoleAndShortId() =>
            Assert.That(OperativePanelView.DescribeHeader("Darius", "Assault", "6f1d2a40-3b7c"), Is.EqualTo("Darius - Assault [6f1d2a40]"));

        [Test]
        public void DescribeProgress_ShowsTheNextThreshold_OrMax()
        {
            Assert.That(OperativePanelView.DescribeProgress(2, 5, 130, 250), Is.EqualTo("Rank 2/5 | XP 130/250"));
            Assert.That(OperativePanelView.DescribeProgress(5, 5, 700, null), Is.EqualTo("Rank 5/5 (max) | XP 700"));
        }

        [Test]
        public void DescribeStat_ShowsBaseToEffective_OnlyWhenTheyDiffer()
        {
            Assert.That(OperativePanelView.DescribeStat("Max health", "130", "155"), Is.EqualTo("Max health: 130 -> 155"));
            Assert.That(OperativePanelView.DescribeStat("Max health", "130", "130"), Is.EqualTo("Max health: 130"));
        }

        [Test]
        public void DescribePicks_ListsThemAndFlagsAvailablePicks()
        {
            Assert.That(OperativePanelView.DescribePicks(new string[0], 0), Is.EqualTo("Picks: none"));
            Assert.That(OperativePanelView.DescribePicks(new[] { "Combat Training", "Reinforced" }, 0), Is.EqualTo("Picks: Combat Training, Reinforced"));
            Assert.That(OperativePanelView.DescribePicks(new[] { "Combat Training" }, 2), Is.EqualTo("Picks: Combat Training | PICK AVAILABLE x2"));
        }

        [Test]
        public void DescribeStatus_NotDeployedAliveOrDead()
        {
            Assert.That(OperativePanelView.DescribeStatus(false, false), Is.EqualTo("not deployed"));
            Assert.That(OperativePanelView.DescribeStatus(true, true), Is.EqualTo("alive"));
            Assert.That(OperativePanelView.DescribeStatus(true, false), Is.EqualTo("dead"));
        }

        [Test]
        public void DescribeRosterRow_OneLinePerOperative() =>
            Assert.That(OperativePanelView.DescribeRosterRow("Kestrel", "Recon", 2, 130, "dead"), Is.EqualTo("Kestrel (Recon) rank 2 XP 130 dead"));

        [Test]
        public void HudOperativeLabel_NameAndRole() =>
            Assert.That(PrototypeHud.DescribeOperative("Darius", "Assault"), Is.EqualTo("Darius (Assault)"));
    }
}
```

`Tests/EditMode/OperativeInputAssetTests.cs`:

```csharp
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.InputSystem;

namespace Blackglass.Tests
{
    public class OperativeInputAssetTests
    {
        const string AssetPath = "Assets/_Project/Input/BlackglassControls.inputactions";

        static InputActionAsset Load()
        {
            var asset = AssetDatabase.LoadAssetAtPath<InputActionAsset>(AssetPath);
            Assert.That(asset, Is.Not.Null, $"Input actions not found at {AssetPath}");
            return asset;
        }

        [TestCase("ToggleOperativePanel", "<Keyboard>/f9")]
        [TestCase("AddExperience", "<Keyboard>/f10")]
        [TestCase("ResetProgression", "<Keyboard>/f11")]
        public void TheDeveloperMap_HasTheOperativeActions_OnKeyboardKeys(string action, string path)
        {
            var found = Load().FindAction("Developer/" + action);
            Assert.That(found, Is.Not.Null, action);
            Assert.That(found.type, Is.EqualTo(InputActionType.Button));
            Assert.That(found.bindings.Select(b => b.path), Is.EqualTo(new[] { path }));
            Assert.That(found.bindings.Single().groups, Is.EqualTo("KeyboardMouse"));
        }

        [Test]
        public void TheOperativeKeys_DoNotClashWithAnyOtherBinding()
        {
            var keys = new[] { "<Keyboard>/f9", "<Keyboard>/f10", "<Keyboard>/f11" };
            foreach (var key in keys)
            {
                var users = Load().actionMaps.SelectMany(m => m.actions).Where(a => a.bindings.Any(b => b.path == key)).Select(a => a.name).ToArray();
                Assert.That(users, Has.Length.EqualTo(1), key + " is bound by: " + string.Join(", ", users));
            }
        }
    }
}
```

`Tests/PlayMode/OperativeDeveloperInputTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class OperativeDeveloperInputTests : InputTestFixture
    {
        Keyboard keyboard;
        InputActionAsset actions;
        MissionRig rig;
        OperativeKit kit;
        SquadRoster roster;
        OperativePanelView panel;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            actions = TestControls.Load();
            rig = new MissionRig();
            kit = OperativeKit.Build();
            roster = rig.AddRoster(kit.Definitions, kit.Track);
            // Inactive while wiring, so OnEnable subscribes to the real actions, not to the empty references.
            var host = rig.World.Track(new GameObject("OperativeDeveloper"));
            host.SetActive(false);
            panel = host.AddComponent<OperativePanelView>();
            panel.Initialize(roster, rig.Active, rig.Director);
            var input = host.AddComponent<OperativeDeveloperInput>();
            input.Initialize(roster, rig.Active, panel, TestControls.Ref(actions, "Developer/ToggleOperativePanel"),
                TestControls.Ref(actions, "Developer/AddExperience"), TestControls.Ref(actions, "Developer/ResetProgression"));
            host.SetActive(true);
        }

        public override void TearDown()
        {
            rig.Dispose();
            kit.Dispose();
            TestControls.Reset(actions);
            base.TearDown();
        }

        IEnumerator Tap(UnityEngine.InputSystem.Controls.KeyControl key)
        {
            Press(key);
            yield return null;
            Release(key);
            yield return null;
        }

        [UnityTest]
        public IEnumerator F10_AddsTheTracksDebugXp_ToTheControlledOperativeOnly()
        {
            yield return rig.Generate(31);
            Assert.That(rig.Active.Unit, Is.SameAs(rig.Director.Friendlies[0]));

            yield return Tap(keyboard.f10Key);

            Assert.That(roster.Members.Select(m => m.State.Experience), Is.EqualTo(new[] { 50, 0, 0 }));

            rig.Active.SetUnit(rig.Director.Friendlies[2]);
            yield return Tap(keyboard.f10Key);
            Assert.That(roster.Members.Select(m => m.State.Experience), Is.EqualTo(new[] { 50, 0, 50 }));
        }

        [UnityTest]
        public IEnumerator F11_ResetsTheControlledOperativesProgression_AndNoOneElses()
        {
            yield return rig.Generate(31);
            roster.AwardExperience("test-alpha", 250);
            roster.AwardExperience("test-bravo", 250);
            roster.TryPickChoice("test-alpha", "combat");

            yield return Tap(keyboard.f11Key);

            Assert.That(roster.Find("test-alpha").State.Experience, Is.EqualTo(0));
            Assert.That(roster.Find("test-alpha").State.ChoiceIds, Is.Empty);
            Assert.That(roster.Find("test-bravo").State.Experience, Is.EqualTo(250));
            Assert.That(rig.Director.Friendlies[0].GetComponent<Health>().Max, Is.EqualTo(130), "the running unit follows the reset");
        }

        [UnityTest]
        public IEnumerator F9_TogglesThePanel_HiddenByDefault()
        {
            yield return rig.Generate(31);
            Assert.That(panel.IsVisible, Is.False);

            yield return Tap(keyboard.f9Key);
            Assert.That(panel.IsVisible, Is.True);
            yield return null;   // OnGUI may run; it must not throw
            yield return Tap(keyboard.f9Key);
            Assert.That(panel.IsVisible, Is.False);
        }

        [UnityTest]
        public IEnumerator WithNoControlledUnit_TheKeysDoNothing_AndDoNotThrow()
        {
            yield return rig.Generate(31);
            rig.Active.SetUnit(null);

            yield return Tap(keyboard.f10Key);
            yield return Tap(keyboard.f11Key);

            Assert.That(roster.Members.Select(m => m.State.Experience), Is.EqualTo(new[] { 0, 0, 0 }));
        }
    }
}
#endif
```

- [ ] **Step 2: Run to verify it fails.** `Tools/run-tests.sh EditMode "Blackglass.Tests.OperativePanelTextTests"` → EXIT=1 (types missing).

- [ ] **Step 3: Implement.**

`BlackglassControls.inputactions`, `Developer` map: after the `ToggleVisuals` action line add a comma and these three action objects (keep the same field layout as the line above; ids below are fixed):

```json
                { "name": "ToggleOperativePanel", "type": "Button", "id": "b7e41c2a-90d3-4a6f-8e15-3c2f7a9d0b41", "expectedControlType": "Button", "processors": "", "interactions": "", "initialStateCheck": false },
                { "name": "AddExperience", "type": "Button", "id": "c18f5d3b-a1e4-4b70-9f26-4d3a8b0e1c52", "expectedControlType": "Button", "processors": "", "interactions": "", "initialStateCheck": false },
                { "name": "ResetProgression", "type": "Button", "id": "d29a6e4c-b2f5-4c81-a037-5e4b9c1f2d63", "expectedControlType": "Button", "processors": "", "interactions": "", "initialStateCheck": false }
```

and after the `f8` binding line (add a comma to it) these three bindings:

```json
                { "name": "", "id": "e3ab7f5d-c306-4d92-b148-6f5cad2a3e74", "path": "<Keyboard>/f9", "interactions": "", "processors": "", "groups": "KeyboardMouse", "action": "ToggleOperativePanel", "isComposite": false, "isPartOfComposite": false },
                { "name": "", "id": "f4bc8a6e-d417-4ea3-8259-7a6dbe3b4f85", "path": "<Keyboard>/f10", "interactions": "", "processors": "", "groups": "KeyboardMouse", "action": "AddExperience", "isComposite": false, "isPartOfComposite": false },
                { "name": "", "id": "05cd9b7f-e528-4fb4-936a-8b7ecf4c5096", "path": "<Keyboard>/f11", "interactions": "", "processors": "", "groups": "KeyboardMouse", "action": "ResetProgression", "isComposite": false, "isPartOfComposite": false }
```

Keep the file's existing line endings and indentation (the file is LF/CRLF per `.gitattributes`; edit with the Edit tool, do not rewrite the file).

`Scripts/Operatives/OperativePanelView.cs`:

```csharp
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Debug-only operative inspector (IMGUI, works while paused; hidden until F9). Shows the controlled operative's name,
    /// role, short id, rank, XP, effective stats ("base -> effective"), abilities and picks, one button per advancement
    /// choice while a pick is pending, and a compact line for every operative in the roster. Not the final character
    /// sheet. All wording is built by pure internal static methods, which the tests cover.
    /// </summary>
    public sealed class OperativePanelView : MonoBehaviour
    {
        [SerializeField] SquadRoster roster;
        [SerializeField] ActiveCharacter activeCharacter;
        // Optional: tells the roster list whether an operative is deployed and alive.
        [SerializeField] MissionDirector director;

        bool visible;

        public bool IsVisible => visible;

        internal void Initialize(SquadRoster squad, ActiveCharacter active, MissionDirector missionDirector)
        {
            roster = squad;
            activeCharacter = active;
            director = missionDirector;
        }

        public void SetVisible(bool show) => visible = show;

        public void Toggle() => visible = !visible;

        internal static string ShortId(string id) =>
            string.IsNullOrEmpty(id) ? "-" : id.Length <= 8 ? id : id.Substring(0, 8);

        internal static string DescribeHeader(string displayName, string roleName, string id) =>
            $"{displayName} - {roleName} [{ShortId(id)}]";

        internal static string DescribeProgress(int rank, int maxRank, int experience, int? nextThreshold) =>
            nextThreshold.HasValue
                ? $"Rank {rank}/{maxRank} | XP {experience}/{nextThreshold.Value}"
                : $"Rank {rank}/{maxRank} (max) | XP {experience}";

        internal static string DescribeStat(string label, string baseValue, string effectiveValue) =>
            baseValue == effectiveValue ? $"{label}: {effectiveValue}" : $"{label}: {baseValue} -> {effectiveValue}";

        internal static string DescribePicks(IReadOnlyList<string> pickNames, int pending)
        {
            var picked = pickNames.Count == 0 ? "none" : string.Join(", ", pickNames);
            return pending > 0 ? $"Picks: {picked} | PICK AVAILABLE x{pending}" : $"Picks: {picked}";
        }

        internal static string DescribeStatus(bool deployed, bool alive) => !deployed ? "not deployed" : alive ? "alive" : "dead";

        internal static string DescribeRosterRow(string displayName, string roleName, int rank, int experience, string status) =>
            $"{displayName} ({roleName}) rank {rank} XP {experience} {status}";

        static string Number(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        void OnGUI()
        {
            if (!visible || roster == null || roster.Track == null)
                return;
            string pickedChoiceId = null;
            GUILayout.BeginArea(new Rect(10f, 200f, 400f, Screen.height - 220f), GUI.skin.box);
            GUILayout.Label("OPERATIVES  (F9 hide | F10 +XP | F11 reset progression)");
            var member = ControlledMember();
            if (member == null)
                GUILayout.Label("Controlled: none");
            else
                pickedChoiceId = DrawDetail(member);
            GUILayout.Space(8f);
            foreach (var other in roster.Members)
            {
                var deployed = TryFindUnit(other.Id, out var unit);
                var alive = deployed && unit.TryGetComponent<Health>(out var health) && health.IsAlive;
                GUILayout.Label(DescribeRosterRow(other.Definition.DisplayName, RoleName(other.Definition), roster.Rank(other),
                    other.State.Experience, DescribeStatus(deployed, alive)));
            }
            GUILayout.EndArea();

            // Applied after the layout pass, so the number of controls cannot change in the middle of it.
            if (pickedChoiceId != null && member != null)
                roster.TryPickChoice(member.Id, pickedChoiceId);
        }

        // Returns the id of the choice whose button was clicked this frame, or null.
        string DrawDetail(RosterMember member)
        {
            var definition = member.Definition;
            var state = member.State;
            var track = roster.Track;
            var rank = roster.Rank(member);
            var pending = roster.PendingPicks(member);
            var baseConfig = EffectiveConfiguration.Base(definition);
            var now = roster.Evaluate(member);

            GUILayout.Label(DescribeHeader(definition.DisplayName, RoleName(definition), state.OperativeId));
            GUILayout.Label(DescribeProgress(rank, track.MaxRank, state.Experience,
                track.TryGetNextThreshold(state.Experience, out var next) ? next : (int?)null));
            GUILayout.Label(DescribeStat("Max health", baseConfig.MaxHealth.ToString(CultureInfo.InvariantCulture), now.MaxHealth.ToString(CultureInfo.InvariantCulture)));
            GUILayout.Label(DescribeStat("Move speed", Number(baseConfig.MoveSpeed), Number(now.MoveSpeed)));
            GUILayout.Label($"Weapon: {definition.Archetype.DisplayName} ({now.AttackRole})");
            GUILayout.Label(DescribeStat("Attack damage", baseConfig.AttackDamage.ToString(CultureInfo.InvariantCulture), now.AttackDamage.ToString(CultureInfo.InvariantCulture)));
            GUILayout.Label(DescribeStat("Attack range", Number(baseConfig.AttackRange), Number(now.AttackRange)));
            GUILayout.Label(DescribeStat("Attack interval", Number(baseConfig.AttackInterval), Number(now.AttackInterval)));
            GUILayout.Label(DescribeStat("Ability power", "x" + Number(baseConfig.AbilityPower), "x" + Number(now.AbilityPower)));
            GUILayout.Label(DescribeStat("Ability cooldown", "x" + Number(baseConfig.AbilityCooldownMultiplier), "x" + Number(now.AbilityCooldownMultiplier)));

            var abilityNames = new List<string>();
            foreach (var ability in definition.Abilities)
                abilityNames.Add(ability.DisplayName);
            GUILayout.Label("Abilities: " + (abilityNames.Count == 0 ? "none" : string.Join(", ", abilityNames)));

            var pickNames = new List<string>();
            foreach (var id in state.ChoiceIds)
            {
                var choice = track.FindChoice(id);
                pickNames.Add(choice != null ? choice.DisplayName : id);
            }
            GUILayout.Label(DescribePicks(pickNames, pending));

            string clicked = null;
            if (pending > 0)
            {
                foreach (var choice in track.Choices)
                {
                    if (GUILayout.Button($"{choice.DisplayName}: {choice.Description}") && clicked == null)
                        clicked = choice.Id;
                }
            }
            return clicked;
        }

        RosterMember ControlledMember()
        {
            if (activeCharacter == null || activeCharacter.Unit == null)
                return null;
            return activeCharacter.Unit.TryGetComponent<UnitIdentity>(out var identity) ? roster.Find(identity.OperativeId) : null;
        }

        bool TryFindUnit(string operativeId, out CommandableUnit unit)
        {
            unit = null;
            if (director == null)
                return false;
            foreach (var candidate in director.Friendlies)
            {
                if (candidate != null && candidate.TryGetComponent<UnitIdentity>(out var identity) && identity.OperativeId == operativeId)
                {
                    unit = candidate;
                    return true;
                }
            }
            return false;
        }

        static string RoleName(OperativeDefinition definition) => definition.Role != null ? definition.Role.DisplayName : "-";
    }
}
```

`Scripts/Operatives/OperativeDeveloperInput.cs`:

```csharp
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>
    /// Developer keys for progression testing (F9 panel, F10 +XP, F11 reset) as input actions. They ask the roster to
    /// change the controlled operative; the rules live in the roster and the track, not here.
    /// </summary>
    public sealed class OperativeDeveloperInput : MonoBehaviour
    {
        [SerializeField] SquadRoster roster;
        [SerializeField] ActiveCharacter activeCharacter;
        [SerializeField] OperativePanelView panel;
        [SerializeField] InputActionReference toggleAction;
        [SerializeField] InputActionReference addExperienceAction;
        [SerializeField] InputActionReference resetAction;

        internal void Initialize(SquadRoster squad, ActiveCharacter active, OperativePanelView view,
            InputActionReference toggle, InputActionReference addExperience, InputActionReference reset)
        {
            roster = squad;
            activeCharacter = active;
            panel = view;
            toggleAction = toggle;
            addExperienceAction = addExperience;
            resetAction = reset;
        }

        void OnEnable()
        {
            InputActionUtility.SetEnabled(true, toggleAction, addExperienceAction, resetAction);
            if (toggleAction != null)
                toggleAction.action.performed += OnToggle;
            if (addExperienceAction != null)
                addExperienceAction.action.performed += OnAddExperience;
            if (resetAction != null)
                resetAction.action.performed += OnReset;
        }

        void OnDisable()
        {
            if (toggleAction != null)
                toggleAction.action.performed -= OnToggle;
            if (addExperienceAction != null)
                addExperienceAction.action.performed -= OnAddExperience;
            if (resetAction != null)
                resetAction.action.performed -= OnReset;
            InputActionUtility.SetEnabled(false, toggleAction, addExperienceAction, resetAction);
        }

        void OnToggle(InputAction.CallbackContext context)
        {
            if (panel != null)
                panel.Toggle();
        }

        void OnAddExperience(InputAction.CallbackContext context)
        {
            if (roster != null && roster.Track != null && TryControlledId(out var id))
                roster.AwardExperience(id, roster.Track.DebugXpStep);
        }

        void OnReset(InputAction.CallbackContext context)
        {
            if (roster != null && TryControlledId(out var id))
                roster.ResetProgression(id);
        }

        bool TryControlledId(out string id)
        {
            id = null;
            if (activeCharacter == null || activeCharacter.Unit == null || !activeCharacter.Unit.TryGetComponent<UnitIdentity>(out var identity))
                return false;
            id = identity.OperativeId;
            return true;
        }
    }
}
```

`PrototypeHud.cs`: add next to `DescribeUnit`

```csharp
        /// <summary>An operative's label name, such as "Darius (Assault)".</summary>
        internal static string DescribeOperative(string displayName, string roleName) => $"{displayName} ({roleName})";
```
and in `DrawUnitLabel` replace `var text = DescribeUnit(health.name, ...` with

```csharp
            var label = health.TryGetComponent<UnitIdentity>(out var identity) ? DescribeOperative(identity.DisplayName, identity.RoleName) : health.name;
            var text = DescribeUnit(label, health.Current, health.Max, hasAttacker ? attacker.Role : CombatRole.Melee, archetypeName);
```

- [ ] **Step 4: Run to verify it passes.** `Tools/run-tests.sh EditMode "Blackglass.Tests.OperativePanelTextTests"` (8), `... "Blackglass.Tests.OperativeInputAssetTests"` (4, the first creates 3 `TestCase`s + 1), then `Tools/run-tests.sh PlayMode "Blackglass.Tests.OperativeDeveloperInputTests"` (4). Regression: full `Tools/run-tests.sh EditMode`; `Tools/run-tests.sh PlayMode "Blackglass.Tests.MissionDeveloperInputTests"`, `"Blackglass.Tests.PrototypeSceneTests"`, `"Blackglass.Tests.RebindingTests"`; the `NoDeviceTypesInGameplayTests` (EditMode) must stay green (no `Gamepad`/`Keyboard` types in the new gameplay scripts: the new scripts only use `InputActionReference`).

- [ ] **Step 5: Commit.**

```bash
git add Assets/_Project/Input/BlackglassControls.inputactions Assets/_Project/Scripts/Operatives Assets/_Project/Scripts/DebugUI/PrototypeHud.cs Assets/_Project/Tests/EditMode/OperativePanelTextTests.cs Assets/_Project/Tests/EditMode/OperativePanelTextTests.cs.meta Assets/_Project/Tests/EditMode/OperativeInputAssetTests.cs Assets/_Project/Tests/EditMode/OperativeInputAssetTests.cs.meta Assets/_Project/Tests/PlayMode/OperativeDeveloperInputTests.cs Assets/_Project/Tests/PlayMode/OperativeDeveloperInputTests.cs.meta
git commit -m "Add the operative debug panel, F9/F10/F11 developer keys and operative unit labels

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Prototype data assets and the builder

**Files:**
- Create: `Editor/OperativeDataBuilder.cs`
- Create (by running the builder): `Data/Operatives/Progression.asset`, `Data/Operatives/Roles/{Assault,Recon,Support}.asset`, `Data/Operatives/Choices/{Combat,Survivability,Mobility,Ability}.asset`, `Data/Operatives/Definitions/{Darius,Kestrel,Sable}.asset` (+ `.meta`, + folder `.meta`)
- Test: `Tests/EditMode/OperativeAssetTests.cs`

**Interfaces:**
- Consumes: Tasks 1-4 (serialized field names listed there), Task 8 (`OperativePanelView`/`OperativeDeveloperInput` field names for Task 10).
- Produces: public constants `OperativeDataBuilder.TrackPath = "Assets/_Project/Data/Operatives/Progression.asset"`, definition paths `.../Definitions/Darius.asset`, `Kestrel.asset`, `Sable.asset`; static methods `CreateAssets()` (menu `Blackglass/Operatives/Create Prototype Operatives (keeps existing assets)`) and `BuildAndWireFromCommandLine()` (Task 10 adds `WireScene`; here it only creates assets).

Prototype data (spec table; Assault bonus is +20% attack damage, see the spec's amended table):

| Asset | Values |
|---|---|
| Role Assault | bonus `attackDamage` 0.20 |
| Role Recon | bonus `moveSpeed` 0.5 |
| Role Support | bonus `abilityPower` 0.15 |
| Choice `combat` "Combat Training" | `attackDamage` 0.15 |
| Choice `survivability` "Reinforced" | `maxHealth` 25 |
| Choice `mobility` "Fleet-footed" | `moveSpeed` 0.75 |
| Choice `ability` "Focus" | `abilityPower` 0.20, `abilityCooldownReduction` 0.15 |
| Track | thresholds `0,100,250,450,700`, the four choices in that order, `missionCompletionXp` 150, `debugXpStep` 50 |
| Darius | id `6f1d2a40-3b7c-4e95-8a1f-52c0d9e7b301`, role Assault, prefab `Assets/Art/Characters/Darius/Prefabs/Darius_Player.prefab`, health 130, speed 5.0, archetype `Data/Archetypes/Ranged.asset`, abilities `[AimedShot, Blast]` |
| Kestrel | id `a93e5c17-0d48-4f2b-b6a3-7e1c8d4f9a02`, role Recon, prefab `Assets/_Project/Prefabs/FriendlyUnit.prefab`, health 80, speed 6.5, archetype Marksman, abilities `[AimedShot]` |
| Sable | id `2c84b7e9-51a6-4d03-9f7e-c3a0165d8b03`, role Support, prefab FriendlyUnit, health 100, speed 5.5, archetype Ranged, abilities `[Mend, AimedShot]` |

- [ ] **Step 1: Write the failing tests.** `Tests/EditMode/OperativeAssetTests.cs`:

```csharp
using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;

namespace Blackglass.Tests
{
    /// <summary>The authored prototype data: valid, distinct, and shaped as the design says.</summary>
    public class OperativeAssetTests
    {
        const string Root = "Assets/_Project/Data/Operatives/";

        static T Load<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(Root + path);
            Assert.That(asset, Is.Not.Null, "missing " + path + " (run Blackglass/Operatives/Create Prototype Operatives)");
            return asset;
        }

        static OperativeDefinition[] Squad() => new[]
        {
            Load<OperativeDefinition>("Definitions/Darius.asset"),
            Load<OperativeDefinition>("Definitions/Kestrel.asset"),
            Load<OperativeDefinition>("Definitions/Sable.asset"),
        };

        [Test]
        public void EveryDefinition_IsValid()
        {
            foreach (var definition in Squad())
            {
                Assert.That(definition.IsValid(out var problem), Is.True, definition.name + ": " + problem);
            }
        }

        [Test]
        public void TheIds_AreFixedUniqueGuids_AndNotTheDisplayNames()
        {
            var squad = Squad();
            Assert.That(squad.Select(d => d.Id), Is.EqualTo(new[]
            {
                "6f1d2a40-3b7c-4e95-8a1f-52c0d9e7b301",
                "a93e5c17-0d48-4f2b-b6a3-7e1c8d4f9a02",
                "2c84b7e9-51a6-4d03-9f7e-c3a0165d8b03",
            }));
            Assert.That(squad.Select(d => d.Id).Distinct().Count(), Is.EqualTo(3));
            foreach (var definition in squad)
            {
                Assert.That(Guid.TryParse(definition.Id, out _), Is.True, definition.name);
                Assert.That(definition.Id, Is.Not.EqualTo(definition.DisplayName));
            }
            Assert.That(squad.Select(d => d.DisplayName), Is.EqualTo(new[] { "Darius", "Kestrel", "Sable" }));
        }

        [Test]
        public void TheThreeRoles_AreAssaultReconSupport()
        {
            Assert.That(Squad().Select(d => d.Role.DisplayName), Is.EqualTo(new[] { "Assault", "Recon", "Support" }));
        }

        [Test]
        public void TheOperatives_AreConfiguredDifferently()
        {
            var squad = Squad();
            Assert.That(squad.Select(d => d.BaseMaxHealth), Is.EqualTo(new[] { 130, 80, 100 }));
            Assert.That(squad.Select(d => d.BaseMoveSpeed).ToArray(), Is.EqualTo(new[] { 5f, 6.5f, 5.5f }));
            Assert.That(squad.Select(d => d.Archetype.DisplayName), Is.EqualTo(new[] { "Ranged", "Marksman", "Ranged" }));
            Assert.That(squad.Select(d => d.Abilities.Select(a => a.DisplayName).ToArray()), Is.EqualTo(new[]
            {
                new[] { "Aimed Shot", "Blast" }, new[] { "Aimed Shot" }, new[] { "Mend", "Aimed Shot" },
            }));
            var configs = squad.Select(d => EffectiveConfiguration.Evaluate(d, new PersistentOperativeState(d.Id), Load<ProgressionTrack>("Progression.asset"))).ToArray();
            Assert.That(configs.Select(c => (c.MaxHealth, c.MoveSpeed, c.AttackRange, c.AttackDamage)).Distinct().Count(), Is.EqualTo(3));
        }

        [Test]
        public void TheUnitPrefabs_AreDariusForDarius_AndTheCapsuleForTheOthers()
        {
            var squad = Squad();
            Assert.That(AssetDatabase.GetAssetPath(squad[0].UnitPrefab), Does.EndWith("Darius_Player.prefab"));
            Assert.That(AssetDatabase.GetAssetPath(squad[1].UnitPrefab), Does.EndWith("FriendlyUnit.prefab"));
            Assert.That(AssetDatabase.GetAssetPath(squad[2].UnitPrefab), Does.EndWith("FriendlyUnit.prefab"));
        }

        [Test]
        public void TheTrack_HasFiveRanks_AndTheFourChoices()
        {
            var track = Load<ProgressionTrack>("Progression.asset");
            Assert.That(track.MaxRank, Is.EqualTo(5));
            Assert.That(track.XpForRank(5), Is.EqualTo(700));
            Assert.That(track.Choices.Select(c => c.Id), Is.EqualTo(new[] { "combat", "survivability", "mobility", "ability" }));
            Assert.That(track.MissionCompletionXp, Is.EqualTo(150));
            Assert.That(track.DebugXpStep, Is.EqualTo(50));
        }

        [Test]
        public void EveryChoice_ChangesSomething_AndNoChoiceIsAnAcrossTheBoardBoost()
        {
            var track = Load<ProgressionTrack>("Progression.asset");
            foreach (var choice in track.Choices)
            {
                var m = choice.Modifiers;
                var changed = new[] { m.maxHealth != 0, m.moveSpeed != 0f, m.attackDamage != 0f, m.abilityPower != 0f, m.abilityCooldownReduction != 0f };
                Assert.That(changed.Count(c => c), Is.GreaterThanOrEqualTo(1), choice.Id);
                Assert.That(changed.Count(c => c), Is.LessThanOrEqualTo(2), choice.Id + " should be a focused choice");
            }
            Assert.That(track.Choices.Select(c => c.Modifiers.maxHealth != 0 ? "health" : c.Modifiers.moveSpeed != 0f ? "speed"
                : c.Modifiers.attackDamage != 0f ? "damage" : "ability").Distinct().Count(), Is.EqualTo(4), "one choice per theme");
        }

        [Test]
        public void ThePrototypeSquad_DoesNotShareProgression_EvenThoughDefinitionsShareAssets()
        {
            var track = Load<ProgressionTrack>("Progression.asset");
            var squad = Squad();
            Assert.That(squad[0].Archetype, Is.SameAs(squad[2].Archetype), "Darius and Sable share the Ranged archetype asset");
            var a = new PersistentOperativeState(squad[0].Id);
            var b = new PersistentOperativeState(squad[2].Id);
            a.AddExperience(700);
            a.TryPickChoice(track, "combat");
            Assert.That(EffectiveConfiguration.Evaluate(squad[2], b, track).AttackDamage,
                Is.EqualTo(EffectiveConfiguration.Evaluate(squad[2], new PersistentOperativeState("fresh"), track).AttackDamage));
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails.** `Tools/run-tests.sh EditMode "Blackglass.Tests.OperativeAssetTests"`. Expected EXIT=2: every test fails with "missing ... (run Blackglass/Operatives/...)".

- [ ] **Step 3: Implement the builder, then run it.** `Editor/OperativeDataBuilder.cs`:

```csharp
#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Blackglass.EditorTools
{
    /// <summary>
    /// Creates the prototype operative data: three roles, four advancement choices, the progression track and the three
    /// definitions. Existing assets are kept (so hand tuning is never overwritten). Fixed ids are authored here once.
    /// </summary>
    public static class OperativeDataBuilder
    {
        const string Root = "Assets/_Project/Data/Operatives";
        public const string TrackPath = Root + "/Progression.asset";
        const string RoleDir = Root + "/Roles";
        const string ChoiceDir = Root + "/Choices";
        const string DefinitionDir = Root + "/Definitions";
        const string CapsulePrefab = "Assets/_Project/Prefabs/FriendlyUnit.prefab";
        const string DariusPrefab = "Assets/Art/Characters/Darius/Prefabs/Darius_Player.prefab";
        const string DataRoot = "Assets/_Project/Data";

        [MenuItem("Blackglass/Operatives/Create Prototype Operatives (keeps existing assets)")]
        public static void CreateAssetsMenu() => CreateAssets();

        /// <summary>Unity -batchmode -executeMethod entry.</summary>
        public static void BuildAndWireFromCommandLine()
        {
            CreateAssets();
        }

        public static void CreateAssets()
        {
            Directory.CreateDirectory(RoleDir);
            Directory.CreateDirectory(ChoiceDir);
            Directory.CreateDirectory(DefinitionDir);
            AssetDatabase.Refresh();

            var assault = Role("Assault", "Front-line fire and area control.", s => s.FindProperty("bonus.attackDamage").floatValue = 0.20f);
            var recon = Role("Recon", "Fast scouting and long-range precision.", s => s.FindProperty("bonus.moveSpeed").floatValue = 0.5f);
            var support = Role("Support", "Keeps the squad standing.", s => s.FindProperty("bonus.abilityPower").floatValue = 0.15f);

            var combat = Choice("Combat", "combat", "Combat Training", "+15% attack damage",
                s => s.FindProperty("modifiers.attackDamage").floatValue = 0.15f);
            var survivability = Choice("Survivability", "survivability", "Reinforced", "+25 max health",
                s => s.FindProperty("modifiers.maxHealth").intValue = 25);
            var mobility = Choice("Mobility", "mobility", "Fleet-footed", "+0.75 m/s move speed",
                s => s.FindProperty("modifiers.moveSpeed").floatValue = 0.75f);
            var ability = Choice("Ability", "ability", "Focus", "+20% ability power, -15% ability cooldown", s =>
            {
                s.FindProperty("modifiers.abilityPower").floatValue = 0.20f;
                s.FindProperty("modifiers.abilityCooldownReduction").floatValue = 0.15f;
            });

            Make<ProgressionTrack>(TrackPath, s =>
            {
                var thresholds = s.FindProperty("xpThresholds");
                var values = new[] { 0, 100, 250, 450, 700 };
                thresholds.arraySize = values.Length;
                for (var i = 0; i < values.Length; i++)
                    thresholds.GetArrayElementAtIndex(i).intValue = values[i];
                SetRefs(s.FindProperty("choices"), combat, survivability, mobility, ability);
                s.FindProperty("missionCompletionXp").intValue = 150;
                s.FindProperty("debugXpStep").intValue = 50;
            });

            var ranged = Load<CombatArchetype>(DataRoot + "/Archetypes/Ranged.asset");
            var marksman = Load<CombatArchetype>(DataRoot + "/Archetypes/Marksman.asset");
            var aimed = Load<AbilityDefinition>(DataRoot + "/Abilities/AimedShot.asset");
            var blast = Load<AbilityDefinition>(DataRoot + "/Abilities/Blast.asset");
            var mend = Load<AbilityDefinition>(DataRoot + "/Abilities/Mend.asset");

            Definition("Darius", "6f1d2a40-3b7c-4e95-8a1f-52c0d9e7b301", assault, Load<GameObject>(DariusPrefab), 130, 5f, ranged, aimed, blast);
            Definition("Kestrel", "a93e5c17-0d48-4f2b-b6a3-7e1c8d4f9a02", recon, Load<GameObject>(CapsulePrefab), 80, 6.5f, marksman, aimed);
            Definition("Sable", "2c84b7e9-51a6-4d03-9f7e-c3a0165d8b03", support, Load<GameObject>(CapsulePrefab), 100, 5.5f, ranged, mend, aimed);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        static OperativeRole Role(string name, string description, Action<SerializedObject> fill) =>
            Make<OperativeRole>($"{RoleDir}/{name}.asset", s =>
            {
                s.FindProperty("displayName").stringValue = name;
                s.FindProperty("description").stringValue = description;
                fill(s);
            });

        static AdvancementChoice Choice(string file, string id, string name, string description, Action<SerializedObject> fill) =>
            Make<AdvancementChoice>($"{ChoiceDir}/{file}.asset", s =>
            {
                s.FindProperty("id").stringValue = id;
                s.FindProperty("displayName").stringValue = name;
                s.FindProperty("description").stringValue = description;
                fill(s);
            });

        static OperativeDefinition Definition(string name, string id, OperativeRole role, GameObject prefab, int health, float speed,
            CombatArchetype archetype, params AbilityDefinition[] abilities) =>
            Make<OperativeDefinition>($"{DefinitionDir}/{name}.asset", s =>
            {
                s.FindProperty("id").stringValue = id;
                s.FindProperty("displayName").stringValue = name;
                SetRef(s.FindProperty("role"), role);
                SetRef(s.FindProperty("unitPrefab"), prefab);
                s.FindProperty("baseMaxHealth").intValue = health;
                s.FindProperty("baseMoveSpeed").floatValue = speed;
                SetRef(s.FindProperty("archetype"), archetype);
                SetRefs(s.FindProperty("abilities"), abilities);
            });

        // Creates the asset when absent and fills it; an existing asset is returned untouched.
        static T Make<T>(string path, Action<SerializedObject> fill) where T : ScriptableObject
        {
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing != null)
                return existing;
            var asset = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, path);
            var serialized = new SerializedObject(asset);
            fill(serialized);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
            return asset;
        }

        // objectReferenceValue is rejected for a freshly created asset in batch mode; the instance id is accepted.
        static void SetRef(SerializedProperty property, UnityEngine.Object value)
        {
            property.objectReferenceValue = value;
            if (property.objectReferenceValue == null && value != null)
                property.objectReferenceInstanceIDValue = value.GetInstanceID();
        }

        static void SetRefs(SerializedProperty array, params UnityEngine.Object[] values)
        {
            array.arraySize = values.Length;
            for (var i = 0; i < values.Length; i++)
                SetRef(array.GetArrayElementAtIndex(i), values[i]);
        }

        static T Load<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
                throw new InvalidOperationException("Missing asset " + path);
            return asset;
        }
    }
}
#endif
```

Run the builder in batch mode (Editor closed):

```bash
"/c/Program Files/Unity/Hub/Editor/6000.3.25f1/Editor/Unity.exe" -batchmode -quit -projectPath "$(pwd -W)" -executeMethod Blackglass.EditorTools.OperativeDataBuilder.BuildAndWireFromCommandLine -logFile "$(pwd -W)/Logs/BuildOperativeData.log"; echo "EXIT=$?"
```
Expected EXIT=0 and 11 new `.asset` files under `Assets/_Project/Data/Operatives/` (1 track, 3 roles, 4 choices, 3 definitions), plus their `.meta` files, the three subfolder metas and `Operatives.meta`. Open the log if EXIT is not 0.

- [ ] **Step 4: Run to verify it passes.** `Tools/run-tests.sh EditMode "Blackglass.Tests.OperativeAssetTests"` (8 new tests). If a definition shows a missing prefab/role/archetype reference, the instance-id fallback did not take: open the asset YAML (`Data/Operatives/Definitions/Darius.asset`) to see which `{fileID: 0}` it holds, fix `SetRef`, delete the generated assets and re-run the builder.

- [ ] **Step 5: Commit.**

```bash
git add Assets/_Project/Editor/OperativeDataBuilder.cs Assets/_Project/Editor/OperativeDataBuilder.cs.meta Assets/_Project/Data/Operatives.meta Assets/_Project/Data/Operatives Assets/_Project/Tests/EditMode/OperativeAssetTests.cs Assets/_Project/Tests/EditMode/OperativeAssetTests.cs.meta
git commit -m "Add the prototype operative data (three roles, four choices, track, Darius/Kestrel/Sable) and its builder

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 10: Scene wiring, existing-test fixes and decision 036

**Files:**
- Modify: `Editor/OperativeDataBuilder.cs` (add `WireScene`, call it from `BuildAndWireFromCommandLine`, add a menu item)
- Modify (by running the builder): `Scenes/ProceduralMission.unity`
- Modify: `Tests/PlayMode/ProceduralMissionSceneTests.cs` (new tests) and any existing scene test that depended on the old squad configuration
- Modify: `Docs/Decisions.md` (decision 036)
- Modify: `Docs/superpowers/specs/2026-10-07-operative-identity-design.md` (Assault bonus +20%)

**Interfaces:**
- Consumes: everything above. Scene object names created: `Squad` (`SquadRoster`), `OperativePanel` (`OperativePanelView`), `OperativeDeveloper` (`OperativeDeveloperInput`).
- Produces: a `ProceduralMission` scene whose director spawns Darius, Kestrel and Sable from the roster.

- [ ] **Step 1: Write the failing scene tests.** Add to `ProceduralMissionSceneTests` (in the same class, next to the other scene tests; `LoadMission()`, `squad`, `director` are the existing helpers):

```csharp
        [UnityTest]
        public IEnumerator TheSceneSquad_IsThreeDistinctPersistentOperatives()
        {
            yield return LoadMission();
            var identities = squad.Select(u => u.GetComponent<UnitIdentity>()).ToArray();
            foreach (var identity in identities)
                Assert.That(identity != null, Is.True, "every squad member is an operative");

            Assert.That(identities.Select(i => i.DisplayName), Is.EqualTo(new[] { "Darius", "Kestrel", "Sable" }));
            Assert.That(identities.Select(i => i.RoleName), Is.EqualTo(new[] { "Assault", "Recon", "Support" }));
            Assert.That(identities.Select(i => i.OperativeId).Distinct().Count(), Is.EqualTo(3));
            Assert.That(squad.Select(u => HealthOf(u).Max), Is.EqualTo(new[] { 130, 80, 100 }));
            Assert.That(squad[0].GetComponent<UnitAnimationDriver>(), Is.Not.Null, "Darius keeps his model");
            Assert.That(squad[1].GetComponent<UnitAttacker>().Archetype.DisplayName, Is.EqualTo("Marksman"));
            Assert.That(active.Unit, Is.SameAs(squad[0]));
        }

        [UnityTest]
        public IEnumerator TheSceneRoster_KeepsXp_WhenTheMissionIsRegenerated()
        {
            yield return LoadMission();
            var roster = Object.FindFirstObjectByType<SquadRoster>();
            Assert.That(roster, Is.Not.Null);
            var kestrelId = squad[1].GetComponent<UnitIdentity>().OperativeId;
            Assert.That(roster.AwardExperience(kestrelId, 120), Is.True);

            Assert.That(director.Generate(777), Is.True);
            yield return TestWorld.WaitUntil(() => director.State == MissionState.Ready || director.State == MissionState.Failed, 20f);
            Assert.That(director.State, Is.EqualTo(MissionState.Ready), director.Report.Failure);

            var kestrel = director.Friendlies[1].GetComponent<UnitIdentity>();
            Assert.That(kestrel.OperativeId, Is.EqualTo(kestrelId));
            Assert.That(kestrel.State.Experience, Is.EqualTo(120));
            Assert.That(director.Friendlies[0].GetComponent<UnitIdentity>().State.Experience, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator TheSceneHasTheOperativePanelAndItsDeveloperKeys()
        {
            yield return LoadMission();
            Assert.That(Object.FindFirstObjectByType<OperativePanelView>(), Is.Not.Null);
            Assert.That(Object.FindFirstObjectByType<OperativeDeveloperInput>(), Is.Not.Null);

            var panel = Object.FindFirstObjectByType<OperativePanelView>();
            Press(keyboard.f9Key);
            yield return null;
            Release(keyboard.f9Key);
            yield return null;
            Assert.That(panel.IsVisible, Is.True);
        }
```

- [ ] **Step 2: Run to verify it fails.** `Tools/run-tests.sh PlayMode "Blackglass.Tests.ProceduralMissionSceneTests"`. Expected EXIT=2: the new tests fail (the scene has no roster); the existing ones still pass.

- [ ] **Step 3: Implement the wiring.** Add to `OperativeDataBuilder.cs` the usings `System.Linq`, `UnityEditor.SceneManagement`, `UnityEngine.InputSystem`; the constants

```csharp
        const string ScenePath = "Assets/_Project/Scenes/ProceduralMission.unity";
        const string ControlsPath = "Assets/_Project/Input/BlackglassControls.inputactions";
```

change `BuildAndWireFromCommandLine` to call `CreateAssets(); WireScene();`, and add:

```csharp
        [MenuItem("Blackglass/Operatives/Wire ProceduralMission Scene")]
        public static void WireSceneMenu() => WireScene();

        /// <summary>
        /// Adds the squad roster, the operative panel and the developer input to ProceduralMission and points the director's
        /// systems at the roster. Idempotent: objects already there are reused and re-pointed.
        /// </summary>
        public static void WireScene()
        {
            var track = Load<ProgressionTrack>(TrackPath);
            var squad = new[]
            {
                Load<OperativeDefinition>($"{DefinitionDir}/Darius.asset"),
                Load<OperativeDefinition>($"{DefinitionDir}/Kestrel.asset"),
                Load<OperativeDefinition>($"{DefinitionDir}/Sable.asset"),
            };
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var director = UnityEngine.Object.FindFirstObjectByType<MissionDirector>();
            var active = UnityEngine.Object.FindFirstObjectByType<ActiveCharacter>();
            if (director == null || active == null)
                throw new InvalidOperationException("ProceduralMission needs a MissionDirector and an ActiveCharacter.");

            var roster = FindOrAdd<SquadRoster>("Squad");
            var rosterObject = new SerializedObject(roster);
            SetRefs(rosterObject.FindProperty("startingSquad"), squad);
            SetRef(rosterObject.FindProperty("track"), track);
            SetRef(rosterObject.FindProperty("director"), director);
            rosterObject.ApplyModifiedPropertiesWithoutUndo();

            var panel = FindOrAdd<OperativePanelView>("OperativePanel");
            var panelObject = new SerializedObject(panel);
            SetRef(panelObject.FindProperty("roster"), roster);
            SetRef(panelObject.FindProperty("activeCharacter"), active);
            SetRef(panelObject.FindProperty("director"), director);
            panelObject.ApplyModifiedPropertiesWithoutUndo();

            var input = FindOrAdd<OperativeDeveloperInput>("OperativeDeveloper");
            var inputObject = new SerializedObject(input);
            SetRef(inputObject.FindProperty("roster"), roster);
            SetRef(inputObject.FindProperty("activeCharacter"), active);
            SetRef(inputObject.FindProperty("panel"), panel);
            SetRef(inputObject.FindProperty("toggleAction"), ActionReference("ToggleOperativePanel"));
            SetRef(inputObject.FindProperty("addExperienceAction"), ActionReference("AddExperience"));
            SetRef(inputObject.FindProperty("resetAction"), ActionReference("ResetProgression"));
            inputObject.ApplyModifiedPropertiesWithoutUndo();

            var directorObject = new SerializedObject(director);
            SetRef(directorObject.FindProperty("systems.roster"), roster);
            directorObject.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        static T FindOrAdd<T>(string objectName) where T : Component
        {
            var existing = UnityEngine.Object.FindFirstObjectByType<T>();
            if (existing != null)
                return existing;
            return new GameObject(objectName).AddComponent<T>();
        }

        static InputActionReference ActionReference(string actionName)
        {
            var reference = AssetDatabase.LoadAllAssetsAtPath(ControlsPath).OfType<InputActionReference>()
                .FirstOrDefault(r => r.action != null && r.action.name == actionName);
            if (reference == null)
                throw new InvalidOperationException($"No InputActionReference for '{actionName}' in {ControlsPath}.");
            return reference;
        }
```

Run the builder (Editor closed) as in Task 9 Step 3. Expected EXIT=0 and `git diff --stat Assets/_Project/Scenes/ProceduralMission.unity` shows the new objects. Do not hand-edit the scene YAML.

- [ ] **Step 4: Run, then fix what the new squad broke.** `Tools/run-tests.sh PlayMode "Blackglass.Tests.ProceduralMissionSceneTests"`. The three new tests must pass. Existing scene tests (this file, `DariusMissionPlayModeTests`, `MissionObjectivesSceneTests`, `PrototypeSceneTests` family only if they load the ProceduralMission scene) may have relied on the old squad: all three friendlies had the same three abilities in the order Aimed Shot, Blast, Mend, 100 health and speed 5; slot 2 was Ranged, slot 0 Ranged. Run the whole PlayMode suite (`Tools/run-tests.sh PlayMode`), and for each failure decide: a test that asserts the intent of an old behaviour (for example "ability slot 3 is Mend") is updated to the operative that now owns it (Sable: Mend is slot 1, Aimed Shot slot 2); never weaken an assertion that guards behaviour (cover, LOS, Tab, follow/park, commands). Report every changed existing test and why.

Then update the spec table: in `Docs/superpowers/specs/2026-10-07-operative-identity-design.md` change "Assault (+10% attack damage)" to "Assault (+20% attack damage)".

- [ ] **Step 5: Write decision 036.** Append to `Docs/Decisions.md` (match the style of 035: bullet fields **Decided / Why / Rejected / Implications**):

```markdown
## 036 — Persistent operatives: definition, state and mission unit

- **Decided:** An operative is three separate things. `OperativeDefinition` (ScriptableObject, immutable: authored GUID id, display name, role, unit prefab, base health and speed, archetype, starting abilities) says who they are. `PersistentOperativeState` (plain `[Serializable]` class: id, XP, picked choice ids) is what survives between missions; it lives in a scene-level `SquadRoster`. The mission unit (a spawned GameObject with `UnitIdentity`) is disposable and holds only runtime state (`Health` damage, cooldowns, orders). `EffectiveConfiguration.Evaluate(definition, state, track)` = definition + role bonus + the modifiers of the picked choices; `UnitIdentity` applies it to `Health`, `UnitMover`, `UnitAttacker` and `UnitAbilities`, at spawn and again whenever the roster reports a change. Rank and pending picks are derived from XP and the `ProgressionTrack` (never stored). XP: `ProgressionTrack.MissionCompletionXp` to every roster member when a mission ends in Success (dead operatives included), plus a debug key; no kill XP (Health.Died carries no killer). One pick per rank after the first, from four choices (combat, survivability, mobility, ability); picks stack.
- **Stable id:** a GUID string authored into the definition; the starting roster uses it, future recruits would get `Guid.NewGuid()`; the roster refuses blank and duplicate ids loudly. Never the display name (renameable), instance id, hierarchy or spawn order.
- **Health and death boundary:** runtime health exists only in `Health` and ends with the unit. Every mission spawns every operative at effective maximum health. A death changes nothing in the persistent state; the dead unit stays dead for its mission (existing behaviour). Injuries, recovery and permadeath are not built; the seam is the spawn step where effective maximum health is computed. `Health.SetMax` keeps damage already taken, so a live level-up raises current health by the difference.
- **Why:** a scene object must not be the identity of a character (models will change; missions are regenerated). Keeping the three layers apart means shared assets are never mutated (two operatives can share a role, archetype or ability asset), progression can be tested without a scene, and Phase 14 only has to serialize a list of `PersistentOperativeState`.
- **Rejected:** a generic modifier/stat framework (five named fields on `StatModifiers` do the job); storing rank or pending picks (could desynchronize); a static or DontDestroyOnLoad registry (global mutable state); progression on the unit component (the unit is destroyed with its mission); splitting the unit prefab into gameplay and visual prefabs now (it would rework Darius's animator wiring; `OperativeDefinition.unitPrefab` already makes the model replaceable without touching identity).
- **Implications:** persistence is in memory for the application session; reloading the scene resets the roster. `MissionSystems.roster` is optional: without it the legacy `FriendlySlot` path spawns the squad unchanged (to be removed once every scene uses a roster). Progression is never read by the mission generator and uses no random stream, so a seed produces the same mission whatever the squad has earned. Debug keys F9 (panel), F10 (+XP to the controlled operative) and F11 (reset its progression) are keyboard-only Developer actions. Phase 14 (save/load) should store `SquadRoster.States()` keyed by id and resolve definitions by id; a missing definition or choice id must degrade gracefully (choice ids already do).
```

- [ ] **Step 6: Run and commit.** `Tools/run-tests.sh EditMode` and `Tools/run-tests.sh PlayMode` (both full runs, all green), then:

```bash
git add Assets/_Project/Editor/OperativeDataBuilder.cs Assets/_Project/Scenes/ProceduralMission.unity Assets/_Project/Tests Docs/Decisions.md Docs/superpowers/specs/2026-10-07-operative-identity-design.md
git commit -m "Wire the operative squad into the ProceduralMission scene; record decision 036

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

`git add Assets/_Project/Tests` here covers only changed existing scene tests and the new scene tests; check `git status` first so unrelated files are not swept in.

---

### Task 11: Whole-branch verification

**Files:** none (verification and report only; a fix found here becomes its own commit).

- [ ] **Step 1: Full suites.** `Tools/run-tests.sh EditMode` and `Tools/run-tests.sh PlayMode`. Record the totals. Expected: EditMode 693 + 92 = **785** (22 + 13 + 13 + 14 + 10 + 12 + 8), PlayMode 701 + 27 = **728** (4 + 8 + 8 + 4 + 3), minus nothing: no existing test is removed. The controller reconciles any difference against the per-task counts. Both must report `failed="0"`.
- [ ] **Step 2: Console check.** Search the last test logs for unexpected output: `grep -n "Error\|Exception" Logs/TestRun-PlayMode.log | grep -v "expected"` and read what remains; any `Debug.LogError` from `SquadRoster`, `UnitIdentity` or the spawner outside a deliberate test is a bug.
- [ ] **Step 3: Scene smoke.** Load `ProceduralMission` in batch mode through the existing scene tests (Task 10 covers it). Confirm in `git diff main --stat` that no file outside the file map changed, that `Assets/Settings/Mobile_RPAsset.asset` and `ProjectSettings/URPProjectSettings.asset` are not in the diff, and that no `.claude/` path is committed.
- [ ] **Step 4: Manual test sequence.** Write it into the completion report (the owner performs it). Cover: F9 panel shows Darius, Kestrel and Sable with different roles/stats; Tab through each and check the "Controlled" line and the unit labels ("Name (Role)"); use each one's abilities (Darius Blast and Aimed Shot, Kestrel Aimed Shot, Sable Mend and Aimed Shot); F10 on one operative only, the others unchanged in the roster list; reach rank 2 (100 XP) and click a pick, watch the stat line change from "base -> effective" and the unit's real behaviour (health bar, speed, damage, cooldown) change without regenerating; F7/F6 regenerate and see XP and picks persist while health is back to full; get an operative killed, confirm the roster row says "dead" and its XP/rank remain, regenerate and see it alive at full health; complete a mission (extraction) and see +150 XP on all three; F11 resets one operative; keyboard/mouse and controller play unchanged (Tab, Shift+Tab, F follow, pause, tactical commands, ability chord); regenerate the same seed with different XP and confirm the same layout (the mission debug view's layout hash).
- [ ] **Step 5: Hand back.** Report per the task statement's completion-report list (files created/changed, architecture, ids, roles, operatives, XP, advancement, effective stats, integration, boundaries, tooling, manual tests, known limitations, concerns before Phase 11). Do not merge, push or start Phase 11.

---

## Self-review (done by the plan author)

**Spec coverage:** definition/state/runtime separation (Tasks 1-3, 6); stable GUID id (Tasks 3, 4, 9); roles as data (1, 9); three differing operatives (9, 6); XP per operative (2, 4, 7); thresholds/ranks (1, 2); a meaningful pick (1, 9, 7); modifier evaluation without mutating assets (3 isolation test, 9 asset test); ability and combat integration through setters, no copied combat logic (5, 6); visual identity via `unitPrefab` (3, 6, 9); roster and spawn integration (4, 6); mission-end persistence in memory (7); health/death boundary (5, 7, decision 036); Tab/follow/park/commands/controllers untouched, guarded by the existing suites run in Tasks 6-10; debug UI, XP and reset keys (8); determinism (7); documentation (10); manual tests and report (11).

**Placeholder scan:** none; the baselines in Global Constraints are measured (EditMode 693, PlayMode 701).

**Type consistency:** `Rank(track)`/`PendingPicks(track)` on the state versus `Rank(member)`/`PendingPicks(member)` on the roster are distinct overload sets used consistently; `EffectiveConfiguration` property names (`MaxHealth`, `MoveSpeed`, `AttackRole/Range/Damage/Interval`, `AbilityPower`, `AbilityCooldownMultiplier`, `Rank`) are the same in Tasks 3, 5, 6, 7, 8; `SquadRoster.Initialize` gains its third parameter in Task 7 with a default so Task 4-6 callers compile unchanged; `MissionRig.AddRoster` passes the director from Task 7 on.
