# Combat Presentation & VFX Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the existing combat visible and audible (muzzle flash, tracer, surface impacts, character hit reaction, a layered Blast explosion, a heal pulse, placeholder sounds, recoil, a small camera impulse) through a small presentation layer that never decides gameplay, follows tactical pause and obeys the Phase 11 knowledge model.

**Architecture:** Gameplay classes raise small immutable records after they resolve an action (`UnitAttacker.Fired`, `UnitAbilities.ShotResolved`/`AreaResolved`; damage/heal/death reuse `Health` events). A per-unit `UnitPresentation` turns them into requests for one scene-level `CombatPresenter`, which gates them through a pure `PresentationVisibility` + the knowledge service, spawns pooled prefab effects under explicit budgets, plays budgeted audio and sends a camera impulse. Profiles (`CombatPresentationProfile` ScriptableObjects) hang off `WeaponVisualAsset` and `AbilityDefinition`. All effect assets are generated once by an idempotent Editor builder.

**Tech Stack:** Unity 6000.3.25f1, URP 17.3, built-in Particle System / LineRenderer / AudioSource, NUnit (EditMode + PlayMode), `Tools/run-tests.sh`. No new packages.

**Spec:** `docs/superpowers/specs/2026-10-10-combat-presentation-vfx-design.md` (read it first; this plan implements it). Brief: the Phase 12.5 prompt.

## Global Constraints

- **Gameplay stays authoritative.** No presentation code may change damage, hit chance, cooldowns, item consumption, AI, timing or mission outcome. Records are raised *after* resolution and are read-only. Presentation off (`PresentationSettings.enabled = false`) or a missing asset must leave every gameplay result identical.
- **Decision 037 is kept (owner ruling).** `UnitAttacker.Fired` is raised **after** `Attacked`/`Missed`, because `IntelligenceService` exposes the shooter inside those events. Presentation adds no reveal logic and no gunfire-discovery/hearing channel.
- **Time:** only `TacticalPause` writes `Time.timeScale`. Presentation code uses scaled `Time.time`/`Time.deltaTime` under `SimulationTime.IsRunning`. The only allowed unscaled read in `Presentation/` is the audio clock in `CombatAudio` (allow-listed in the audit test).
- **Fog:** the gate uses `IntelligenceService.CanTarget` (never `IsUnitShown`, which the F12 truth view widens) and region-level point visibility. A missing gate input means "do not show". Hidden one-shot events are dropped, never buffered.
- **No colliders / NavMesh obstacles / rigidbodies on effect prefabs.** Effects never enter nav, cover, targeting, interaction or LOS queries.
- **Scale:** effects are spawned unparented in world space at the muzzle's world position; parent scales (FBX x100, socket 1/1.85, visual 1.0639) never reach them. Gameplay root, `NavMeshAgent` and colliders are never touched.
- **Cosmetic randomness** uses `CosmeticRandom` (System.Random), never `UnityEngine.Random` and never the mission/loot/combat streams.
- **Unity facts:** never use `??`/`?.` on `UnityEngine.Object`; EditMode tests get no `Awake/OnEnable/Update` (lifecycle tests are PlayMode); colliders created at runtime need `Physics.SyncTransforms()`; one Unity instance per project (run suites serially, Editor closed); `Tools/run-tests.sh` EXIT 0 = green, 1 = compile error, 2 = test failures; assemblies: runtime code in `Blackglass` (`Assets/_Project/Scripts`), Editor builders in `Assets/_Project/Editor` (`#if UNITY_EDITOR`, `namespace Blackglass.EditorTools`, can see internals via `InternalsVisibleTo`).
- **Branch and commits:** work on branch `phase125-combat-presentation`. **Do not commit, push or start Phase 13**; the owner decides (brief §17). Verification steps replace the usual commit steps. The two uncommitted files `Assets/Settings/Mobile_RPAsset.asset` and `ProjectSettings/URPProjectSettings.asset` are not part of this phase: never stage or modify them, and keep `.claude/` untracked.
- **Scratch output** (renders, reports, briefs) goes in `F:\Programs\ProjectBlackglass\.claude\scratchpad\phase125\`, never in system temp.
- **Test totals:** measure the baseline in Task 0 with real runs before trusting any number (last recorded: EditMode 1159, PlayMode 1206).
- Use only original, locally generated textures and sounds; sounds are named `PLACEHOLDER_*`.

## Review Focus

The inputs and conditions the spec implies but no obvious test covers; each is pinned by a test in the named task.

1. A unit with **no `UnitWeaponVisual`, no socket, no muzzle or no profile** (the capsule rigs, hostile prefabs without a weapon, an unequipped operative) fires and gets hurt without exceptions or log spam. (Task 7, Task 15)
2. A victim **killed by the shot or the blast** (Health deactivates it, `UnitAnimationDriver` detaches its visual) still shows its impact, and the hit flash never leaves a unit stuck bright. (Task 7, Task 10)
3. **Point-blank / zero-length / NaN geometry** (shooter on top of the target, identical positions) omits the tracer and impact instead of throwing or drawing garbage. (Task 7)
4. **Regeneration while paused, and pause/resume spam,** neither duplicates nor leaks effects, voices or lights, and a paused effect resumes instead of restarting. (Task 5, Task 14, Task 15)
5. **Many shooters in one frame** (a squad plus hostiles) stay inside the effect, light and voice budgets, and do not stack audio gain. (Task 6, Task 15)
6. A **hidden hostile damaged by something other than a shot** (retaliation, blast) shows no hit flash, burst or sound to the player. (Task 10, Task 15)

## File structure

Create (runtime, `Assets/_Project/Scripts/Presentation/`, namespace `Blackglass`):

| File | Responsibility |
|---|---|
| `ImpactSurfaceKind.cs` | enum Default/Concrete/Metal/Character |
| `PresentationVisibility.cs` | `PointVisibility`, `ShotPermissions`, pure gate rules |
| `EffectBudget.cs` | `EffectCategory`, `PresentationQuality`, active-count caps and refusal counter |
| `CosmeticRandom.cs` | independent System.Random wrapper |
| `SequenceFilter.cs` | duplicate-record rejection per emitter |
| `RecoilEnvelope.cs`, `ImpulseEnvelope.cs` | pure scaled-time envelopes for recoil and camera impulse |
| `ShotRecord.cs`, `AreaRecord.cs` | the records gameplay publishes |
| `PresentationSettings.cs` | enabled, quality, master volume, camera shake |
| `CombatPresentationProfile.cs` | profile asset + `AudioCue`, `TracerStyle`, `SurfaceImpact` |
| `ImpactSurface.cs` | `ImpactSurface` component + `ImpactSurfaces` classification |
| `PooledEffect.cs`, `TracerView.cs`, `EffectPool.cs` | pooled instance runtime |
| `ShotGeometry.cs` | cosmetic endpoint / impact resolution (reads physics, writes nothing) |
| `CombatPresenter.cs` | scene-level gate, spawn, budgets, audio, impulse, clear |
| `UnitPresentation.cs` | per-unit subscriber, profile/muzzle resolution, recoil, flash, pulse |
| `CueThrottle.cs`, `VoicePicker.cs`, `CombatAudio.cs` | audio limiting and playback |
| `CameraImpulse.cs` | additive camera offset source |
| `PresentationPreviewPanel.cs` | IMGUI panel for the preview scene |

Create (Editor, `Assets/_Project/Editor/Presentation/`): `EffectTextures.cs`, `EffectMaterials.cs`, `ParticleAuthoring.cs`, `EffectPrefabs.cs`, `PlaceholderSounds.cs`, `CombatPresentationAssetBuilder.cs`, `CombatPresentationSceneBuilder.cs`.

Create (assets, generated, `Assets/_Project/Presentation/`): `Textures/`, `Materials/`, `Prefabs/`, `Profiles/`, `Audio/`, and `Assets/_Project/Scenes/Presentation Test Scene.unity`.

Modify: `UnitAttacker.cs`, `UnitAbilities.cs`, `WeaponVisualAsset.cs`, `AbilityDefinition.cs`, `UnitWeaponVisual.cs`, `MissionBuilder.cs`, `MissionContent.cs`, `MissionSlots.cs` (`MissionSystems`), `MissionDirector.cs` (`ResetSystems`), `MissionSpawner.cs`, `IntelligenceService.cs`, `Knowledge.cs`, `UnitAnimationDriver.cs`, `TacticalCameraController.cs`, `Docs/Decisions.md`; new docs `Docs/CombatPresentation.md`, `Docs/Phase12.5-ManualTests.md`.

Create (tests): `Assets/_Project/Tests/EditMode/*Presentation*.cs` and `Assets/_Project/Tests/PlayMode/*Presentation*.cs` plus `Tests/PlayMode/TestSupport/PresentationRig.cs`.

---

### Task 0: Branch and baseline

**Files:** none (creates `.claude/scratchpad/phase125/baseline.txt`).

- [ ] **Step 1: Create the branch**

Run (from `F:\Programs\ProjectBlackglass`, Git Bash):
```bash
git checkout -b phase125-combat-presentation
git status --short
```
Expected: `Switched to a new branch`, status shows only the two pre-existing modified settings files and `?? .claude/`.

- [ ] **Step 2: Confirm the Editor is closed, measure the EditMode baseline**

```bash
mkdir -p .claude/scratchpad/phase125
Tools/run-tests.sh EditMode > .claude/scratchpad/phase125/baseline-edit.txt 2>&1; tail -5 .claude/scratchpad/phase125/baseline-edit.txt
```
Expected: `EXIT=0` and a `total="…" passed="…"` line. Record both numbers.

- [ ] **Step 3: Measure the PlayMode baseline**

```bash
Tools/run-tests.sh PlayMode > .claude/scratchpad/phase125/baseline-play.txt 2>&1; tail -5 .claude/scratchpad/phase125/baseline-play.txt
```
Expected: `EXIT=0`. If a test is already failing, stop and report it; do not proceed on a red baseline.

- [ ] **Step 4: Note the two totals** at the top of `.claude/scratchpad/phase125/baseline.txt` (`EditMode N, PlayMode M`). Later tasks compare against them.

---

### Task 1: Pure presentation core (visibility gate, budget, random, filter, envelopes)

**Files:**
- Create: `Assets/_Project/Scripts/Presentation/ImpactSurfaceKind.cs`, `PresentationVisibility.cs`, `EffectBudget.cs`, `CosmeticRandom.cs`, `SequenceFilter.cs`, `RecoilEnvelope.cs`, `ImpulseEnvelope.cs`, `PresentationSettings.cs`
- Test: `Assets/_Project/Tests/EditMode/PresentationVisibilityTests.cs`, `PresentationCoreTests.cs`

**Interfaces:**
- Produces (used by every later task):
  - `enum ImpactSurfaceKind { Default = 0, Concrete = 1, Metal = 2, Character = 3 }`
  - `enum PointVisibility { Outside, Hidden, Visible }`
  - `readonly struct ShotPermissions { bool Muzzle; bool Tracer; bool Impact; ctor(bool muzzle, bool tracer, bool impact) }`
  - `static class PresentationVisibility { const float PathSampleSpacing = 3f; static bool AllowsPoint(Func<Vector3, PointVisibility> query, Vector3 point); static bool AllowsPath(Func<Vector3, PointVisibility> query, Vector3 from, Vector3 to); static ShotPermissions EvaluateShot(bool shooterObserved, bool victimObserved, Func<Vector3, PointVisibility> query, Vector3 muzzle, bool hasTracer, Vector3 tracerEnd, bool hasImpact, Vector3 impactPoint, bool impactOnCharacter) }`
  - `enum EffectCategory { Muzzle, Tracer, Impact, Area, Pulse }`, `enum PresentationQuality { Low, Medium, High }`
  - `sealed class EffectBudget { EffectBudget(PresentationQuality q); int Cap(EffectCategory); int Active(EffectCategory); bool TryAcquire(EffectCategory); void Release(EffectCategory); int LightCap; int LightsActive; bool TryAcquireLight(); void ReleaseLight(); int Refused; void Reset(); }`
  - `sealed class CosmeticRandom { CosmeticRandom(int seed); CosmeticRandom(); float Value(); float Range(float min, float max); bool Coin(); float Sign(); }`
  - `sealed class SequenceFilter { bool TryAccept(int emitterId, int sequence); void Clear(); }`
  - `sealed class RecoilEnvelope { void Kick(float meters, float recoverSeconds); void Advance(float dt); float Offset; bool IsActive; void Reset(); }`
  - `sealed class ImpulseEnvelope { const float MaxOffset = 0.08f; const float Duration = 0.5f; void Add(float strength, float phaseX, float phaseY); void Advance(float dt); Vector2 Offset; float Amplitude; void Reset(); }`
  - `[Serializable] sealed class PresentationSettings { bool enabled = true; PresentationQuality quality = High; float masterVolume = 0.8f; float cameraShake = 0.5f; }`

- [ ] **Step 1: Write the failing visibility tests**

`Assets/_Project/Tests/EditMode/PresentationVisibilityTests.cs`:
```csharp
using System;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class PresentationVisibilityTests
    {
        static readonly Func<Vector3, PointVisibility> AllVisible = _ => PointVisibility.Visible;
        static readonly Vector3 Muzzle = new Vector3(0f, 1.5f, 0f);

        // Everything with x > 5 is hidden, everything with x < -50 is outside the regions.
        static PointVisibility FogRight(Vector3 p) =>
            p.x > 5f ? PointVisibility.Hidden : p.x < -50f ? PointVisibility.Outside : PointVisibility.Visible;

        [Test]
        public void ObservedShooter_AllVisible_AllowsEverything()
        {
            var p = PresentationVisibility.EvaluateShot(true, true, AllVisible, Muzzle, true, new Vector3(0f, 1f, 10f), true, new Vector3(0f, 1f, 10f), false);
            Assert.That(p.Muzzle && p.Tracer && p.Impact, Is.True);
        }

        [Test]
        public void HiddenShooter_ShowsNoMuzzleAndNoTracer()
        {
            var p = PresentationVisibility.EvaluateShot(false, true, AllVisible, Muzzle, true, new Vector3(0f, 1f, 10f), true, new Vector3(0f, 1f, 10f), false);
            Assert.That(p.Muzzle, Is.False);
            Assert.That(p.Tracer, Is.False);
        }

        [Test]
        public void ImpactOnAVisibleFriend_FromAHiddenShooter_IsStillShown()
        {
            var p = PresentationVisibility.EvaluateShot(false, true, AllVisible, Muzzle, false, default, true, Vector3.zero, true);
            Assert.That(p.Impact, Is.True, "local damage feedback does not depend on seeing the shooter");
        }

        [Test]
        public void TracerIntoHiddenGround_IsSuppressed_ButMuzzleStays()
        {
            var p = PresentationVisibility.EvaluateShot(true, true, FogRight, Muzzle, true, new Vector3(20f, 1f, 0f), true, new Vector3(20f, 1f, 0f), false);
            Assert.That(p.Muzzle, Is.True);
            Assert.That(p.Tracer, Is.False);
            Assert.That(p.Impact, Is.False, "the impact point is hidden ground");
        }

        [Test]
        public void TracerCrossingAHiddenStretchInTheMiddle_IsSuppressed()
        {
            static PointVisibility Gap(Vector3 p) => p.x > 4f && p.x < 8f ? PointVisibility.Hidden : PointVisibility.Visible;
            Assert.That(PresentationVisibility.AllowsPath(Gap, new Vector3(0f, 1f, 0f), new Vector3(12f, 1f, 0f)), Is.False);
        }

        [Test]
        public void OutsideSamples_AreNeutral()
        {
            Assert.That(PresentationVisibility.AllowsPath(FogRight, new Vector3(-60f, 1f, 0f), new Vector3(0f, 1f, 0f)), Is.True);
        }

        [Test]
        public void CharacterImpact_UsesTheVictim_NotThePoint()
        {
            var hiddenVictim = PresentationVisibility.EvaluateShot(true, false, AllVisible, Muzzle, true, Vector3.forward, true, Vector3.forward, true);
            Assert.That(hiddenVictim.Impact, Is.False);
            var shownVictim = PresentationVisibility.EvaluateShot(true, true, FogRight, Muzzle, true, Vector3.forward, true, new Vector3(30f, 0f, 0f), true);
            Assert.That(shownVictim.Impact, Is.True);
        }

        [Test]
        public void WorldImpact_RequiresAVisiblePoint_NotJustANonHiddenOne()
        {
            var p = PresentationVisibility.EvaluateShot(true, true, FogRight, Muzzle, false, default, true, new Vector3(-60f, 0f, 0f), false);
            Assert.That(p.Impact, Is.False, "a point outside every region is not shown");
        }

        [Test]
        public void ZeroLengthPath_DoesNotThrow_AndTestsTheSinglePoint()
        {
            Assert.That(PresentationVisibility.AllowsPath(FogRight, new Vector3(9f, 0f, 0f), new Vector3(9f, 0f, 0f)), Is.False);
            Assert.That(PresentationVisibility.AllowsPath(AllVisible, Vector3.zero, Vector3.zero), Is.True);
        }
    }
}
```

`Assets/_Project/Tests/EditMode/PresentationCoreTests.cs`:
```csharp
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class PresentationCoreTests
    {
        // ---- budget ----
        [Test]
        public void Budget_RefusesAtTheCap_CountsTheRefusal_AndReleaseFreesASlot()
        {
            var budget = new EffectBudget(PresentationQuality.High);
            var cap = budget.Cap(EffectCategory.Muzzle);
            for (var i = 0; i < cap; i++)
                Assert.That(budget.TryAcquire(EffectCategory.Muzzle), Is.True);
            Assert.That(budget.TryAcquire(EffectCategory.Muzzle), Is.False);
            Assert.That(budget.Refused, Is.EqualTo(1));
            budget.Release(EffectCategory.Muzzle);
            Assert.That(budget.TryAcquire(EffectCategory.Muzzle), Is.True);
            Assert.That(budget.Active(EffectCategory.Muzzle), Is.EqualTo(cap));
        }

        [Test]
        public void Budget_LowQualityHasSmallerCaps_ButAtLeastOne()
        {
            var high = new EffectBudget(PresentationQuality.High);
            var low = new EffectBudget(PresentationQuality.Low);
            foreach (EffectCategory category in System.Enum.GetValues(typeof(EffectCategory)))
            {
                Assert.That(low.Cap(category), Is.LessThanOrEqualTo(high.Cap(category)));
                Assert.That(low.Cap(category), Is.GreaterThanOrEqualTo(1));
            }
            Assert.That(low.LightCap, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void Budget_LightsAreCappedSeparately_AndReleaseNeverGoesNegative()
        {
            var budget = new EffectBudget(PresentationQuality.High);
            for (var i = 0; i < budget.LightCap; i++)
                Assert.That(budget.TryAcquireLight(), Is.True);
            Assert.That(budget.TryAcquireLight(), Is.False);
            for (var i = 0; i < budget.LightCap + 3; i++)
                budget.ReleaseLight();
            Assert.That(budget.LightsActive, Is.EqualTo(0));
            budget.Release(EffectCategory.Tracer);
            Assert.That(budget.Active(EffectCategory.Tracer), Is.EqualTo(0));
        }

        [Test]
        public void Budget_Reset_ClearsEverything()
        {
            var budget = new EffectBudget(PresentationQuality.Medium);
            budget.TryAcquire(EffectCategory.Impact);
            budget.TryAcquireLight();
            budget.Reset();
            Assert.That(budget.Active(EffectCategory.Impact), Is.EqualTo(0));
            Assert.That(budget.LightsActive, Is.EqualTo(0));
            Assert.That(budget.Refused, Is.EqualTo(0));
        }

        // ---- cosmetic random ----
        [Test]
        public void CosmeticRandom_SameSeedSameSequence_AndRangeStaysInside()
        {
            var a = new CosmeticRandom(42);
            var b = new CosmeticRandom(42);
            for (var i = 0; i < 50; i++)
            {
                var v = a.Range(0.6f, 1.2f);
                Assert.That(v, Is.EqualTo(b.Range(0.6f, 1.2f)));
                Assert.That(v, Is.InRange(0.6f, 1.2f));
            }
            Assert.That(Mathf.Abs(new CosmeticRandom(1).Sign()), Is.EqualTo(1f));
        }

        [Test]
        public void CosmeticRandom_NeverTouchesUnityRandom()
        {
            Random.InitState(1234);
            var expected = new float[5];
            for (var i = 0; i < expected.Length; i++)
                expected[i] = Random.value;

            Random.InitState(1234);
            var cosmetic = new CosmeticRandom();
            for (var i = 0; i < 200; i++)
                cosmetic.Value();
            for (var i = 0; i < expected.Length; i++)
                Assert.That(Random.value, Is.EqualTo(expected[i]), "the gameplay random stream moved");
        }

        // ---- sequence filter ----
        [Test]
        public void SequenceFilter_AcceptsEachSequenceOnce_PerEmitter()
        {
            var filter = new SequenceFilter();
            Assert.That(filter.TryAccept(1, 1), Is.True);
            Assert.That(filter.TryAccept(1, 1), Is.False, "the same record twice");
            Assert.That(filter.TryAccept(1, 2), Is.True);
            Assert.That(filter.TryAccept(2, 1), Is.True, "another emitter has its own counter");
            Assert.That(filter.TryAccept(1, 1), Is.False);
            filter.Clear();
            Assert.That(filter.TryAccept(1, 1), Is.True);
        }

        // ---- recoil ----
        [Test]
        public void Recoil_KicksThenRecovers_OnlyAsTimeAdvances()
        {
            var recoil = new RecoilEnvelope();
            Assert.That(recoil.Offset, Is.EqualTo(0f));
            recoil.Kick(0.04f, 0.1f);
            Assert.That(recoil.Offset, Is.EqualTo(0.04f).Within(1e-6f));
            recoil.Advance(0f);
            Assert.That(recoil.Offset, Is.EqualTo(0.04f).Within(1e-6f), "no time, no movement (pause)");
            recoil.Advance(0.05f);
            Assert.That(recoil.Offset, Is.InRange(0.005f, 0.035f));
            recoil.Advance(0.2f);
            Assert.That(recoil.Offset, Is.EqualTo(0f));
            Assert.That(recoil.IsActive, Is.False);
        }

        [Test]
        public void Recoil_NegativeOrZeroKick_DoesNothing_AndResetClears()
        {
            var recoil = new RecoilEnvelope();
            recoil.Kick(-1f, 0.1f);
            Assert.That(recoil.IsActive, Is.False);
            recoil.Kick(0.03f, 0f);
            Assert.That(float.IsNaN(recoil.Offset), Is.False);
            recoil.Reset();
            Assert.That(recoil.Offset, Is.EqualTo(0f));
        }

        // ---- impulse ----
        [Test]
        public void Impulse_DecaysToZero_AndNeverExceedsTheMaximum()
        {
            var impulse = new ImpulseEnvelope();
            impulse.Add(5f, 1f, 2f);   // clamped to 1
            for (var t = 0f; t < ImpulseEnvelope.Duration; t += 0.016f)
            {
                Assert.That(impulse.Offset.magnitude, Is.LessThanOrEqualTo(ImpulseEnvelope.MaxOffset * 1.5f));
                impulse.Advance(0.016f);
            }
            impulse.Advance(1f);
            Assert.That(impulse.Offset, Is.EqualTo(Vector2.zero));
            Assert.That(impulse.Amplitude, Is.EqualTo(0f));
        }

        [Test]
        public void Impulse_FrozenWithoutAdvance_AndAWeakerImpulseDoesNotReplaceAStrongerOne()
        {
            var impulse = new ImpulseEnvelope();
            impulse.Add(1f, 0f, 0f);
            impulse.Advance(0.1f);
            var frozen = impulse.Offset;
            impulse.Advance(0f);
            Assert.That(impulse.Offset, Is.EqualTo(frozen));
            var amplitude = impulse.Amplitude;
            impulse.Add(0.1f, 3f, 3f);
            Assert.That(impulse.Amplitude, Is.EqualTo(amplitude), "a weaker impulse is ignored while a stronger one is running");
        }

        [Test]
        public void Impulse_ZeroStrength_IsIgnored()
        {
            var impulse = new ImpulseEnvelope();
            impulse.Add(0f, 1f, 1f);
            impulse.Advance(0.05f);
            Assert.That(impulse.Offset, Is.EqualTo(Vector2.zero));
        }
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
Tools/run-tests.sh EditMode "Blackglass.Tests.PresentationVisibilityTests"
```
Expected: `EXIT=1` and `error CS0246` / `CS0103` for the missing types.

- [ ] **Step 3: Implement**

`ImpactSurfaceKind.cs`:
```csharp
namespace Blackglass
{
    /// <summary>The kind of surface an impact lands on; picks the impact effect and sound. Values are serialized in profiles: never renumber.</summary>
    public enum ImpactSurfaceKind
    {
        Default = 0,
        Concrete = 1,
        Metal = 2,
        Character = 3,
    }
}
```

`PresentationVisibility.cs`:
```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>What the player's knowledge says about a world point: not in any region, in a region that is not observed, or observed.</summary>
    public enum PointVisibility
    {
        /// <summary>Not inside any region (a wall, the void): neutral for path tests, never enough to show an impact.</summary>
        Outside,
        Hidden,
        Visible,
    }

    /// <summary>Which parts of a shot's presentation the player may see (sounds follow the same flags as their visuals).</summary>
    public readonly struct ShotPermissions
    {
        public ShotPermissions(bool muzzle, bool tracer, bool impact)
        {
            Muzzle = muzzle;
            Tracer = tracer;
            Impact = impact;
        }

        /// <summary>Muzzle flash, smoke, light, firing sound and recoil.</summary>
        public bool Muzzle { get; }
        public bool Tracer { get; }
        /// <summary>The impact effect and its sound.</summary>
        public bool Impact { get; }
    }

    /// <summary>
    /// Pure rules for what combat presentation may show under the knowledge model (decision 037). The caller supplies
    /// the point query (the intelligence service, or "everything visible" without one). A missing answer means do not show.
    /// </summary>
    public static class PresentationVisibility
    {
        /// <summary>A tracer is sampled this often along its path.</summary>
        public const float PathSampleSpacing = 3f;

        public static bool AllowsPoint(Func<Vector3, PointVisibility> query, Vector3 point) =>
            query != null && query(point) == PointVisibility.Visible;

        /// <summary>True when no sample from `from` to `to` (inclusive, every PathSampleSpacing metres) is hidden ground.</summary>
        public static bool AllowsPath(Func<Vector3, PointVisibility> query, Vector3 from, Vector3 to)
        {
            if (query == null)
                return false;
            var steps = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(from, to) / PathSampleSpacing));
            for (var i = 0; i <= steps; i++)
            {
                if (query(Vector3.Lerp(from, to, i / (float)steps)) == PointVisibility.Hidden)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Muzzle effects need an observed shooter. A tracer needs that plus a path with no hidden ground. An impact on a
        /// character needs the victim observed; an impact on the world needs a visible point (the caller offsets it off the surface).
        /// </summary>
        public static ShotPermissions EvaluateShot(bool shooterObserved, bool victimObserved, Func<Vector3, PointVisibility> query,
            Vector3 muzzle, bool hasTracer, Vector3 tracerEnd, bool hasImpact, Vector3 impactPoint, bool impactOnCharacter)
        {
            var tracer = shooterObserved && hasTracer && AllowsPath(query, muzzle, tracerEnd);
            var impact = hasImpact && (impactOnCharacter ? victimObserved : AllowsPoint(query, impactPoint));
            return new ShotPermissions(shooterObserved, tracer, impact);
        }
    }
}
```

`EffectBudget.cs`:
```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    public enum EffectCategory
    {
        Muzzle,
        Tracer,
        Impact,
        Area,
        Pulse,
    }

    public enum PresentationQuality
    {
        Low,
        Medium,
        High,
    }

    /// <summary>
    /// Explicit caps on concurrently *active* presentation effects and lights. Exhaustion degrades cosmetically: the caller
    /// skips the request. Pure counters, no scene dependency.
    /// </summary>
    public sealed class EffectBudget
    {
        static readonly int[] BaseCaps = { 12, 24, 24, 4, 8 };   // Muzzle, Tracer, Impact, Area, Pulse
        const int BaseLightCap = 6;

        readonly int[] caps = new int[BaseCaps.Length];
        readonly int[] active = new int[BaseCaps.Length];

        public EffectBudget(PresentationQuality quality)
        {
            var scale = quality == PresentationQuality.High ? 1f : quality == PresentationQuality.Medium ? 0.6f : 0.3f;
            for (var i = 0; i < caps.Length; i++)
                caps[i] = Mathf.Max(1, Mathf.RoundToInt(BaseCaps[i] * scale));
            LightCap = Mathf.Max(1, Mathf.RoundToInt(BaseLightCap * scale));
        }

        public int LightCap { get; }
        public int LightsActive { get; private set; }
        /// <summary>Requests refused since the last Reset (one aggregated counter, never logged per frame).</summary>
        public int Refused { get; private set; }

        public int Cap(EffectCategory category) => caps[(int)category];
        public int Active(EffectCategory category) => active[(int)category];

        public bool TryAcquire(EffectCategory category)
        {
            var i = (int)category;
            if (active[i] >= caps[i])
            {
                Refused++;
                return false;
            }
            active[i]++;
            return true;
        }

        public void Release(EffectCategory category)
        {
            var i = (int)category;
            if (active[i] > 0)
                active[i]--;
        }

        public bool TryAcquireLight()
        {
            if (LightsActive >= LightCap)
                return false;
            LightsActive++;
            return true;
        }

        public void ReleaseLight()
        {
            if (LightsActive > 0)
                LightsActive--;
        }

        public void Reset()
        {
            Array.Clear(active, 0, active.Length);
            LightsActive = 0;
            Refused = 0;
        }
    }
}
```

`CosmeticRandom.cs`:
```csharp
using System;

namespace Blackglass
{
    /// <summary>
    /// The random source for purely cosmetic choices (spark spread, miss deflection, shake phase). It is its own
    /// System.Random: it never reads or advances UnityEngine.Random, so gameplay, generation and loot are unaffected
    /// by whether presentation runs.
    /// </summary>
    public sealed class CosmeticRandom
    {
        readonly Random random;

        public CosmeticRandom(int seed) => random = new Random(seed);

        public CosmeticRandom() : this(Environment.TickCount ^ 0x5bd1e995) { }

        /// <summary>0 (inclusive) to 1 (exclusive).</summary>
        public float Value() => (float)random.NextDouble();

        public float Range(float min, float max) => min + (max - min) * Value();

        public bool Coin() => random.Next(2) == 0;

        /// <summary>-1 or +1.</summary>
        public float Sign() => Coin() ? 1f : -1f;
    }
}
```

`SequenceFilter.cs`:
```csharp
using System.Collections.Generic;

namespace Blackglass
{
    /// <summary>Rejects a presentation record that was already delivered (same emitter, same or older sequence).</summary>
    public sealed class SequenceFilter
    {
        readonly Dictionary<int, int> last = new Dictionary<int, int>();

        public bool TryAccept(int emitterId, int sequence)
        {
            if (last.TryGetValue(emitterId, out var seen) && sequence <= seen)
                return false;
            last[emitterId] = sequence;
            return true;
        }

        public void Clear() => last.Clear();
    }
}
```

`RecoilEnvelope.cs`:
```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>A weapon kick that eases back over a time the caller advances (scaled time, so it freezes with the pause).</summary>
    public sealed class RecoilEnvelope
    {
        float distance;
        float recover = 1f;
        float elapsed;

        public bool IsActive { get; private set; }

        /// <summary>Metres of backward displacement right now.</summary>
        public float Offset
        {
            get
            {
                if (!IsActive)
                    return 0f;
                var left = 1f - elapsed / recover;
                return distance * left * left;
            }
        }

        public void Kick(float meters, float recoverSeconds)
        {
            distance = Mathf.Max(0f, meters);
            recover = Mathf.Max(0.0001f, recoverSeconds);
            elapsed = 0f;
            IsActive = distance > 0f;
        }

        public void Advance(float dt)
        {
            if (!IsActive || dt <= 0f)
                return;
            elapsed += dt;
            if (elapsed >= recover)
                IsActive = false;
        }

        public void Reset()
        {
            IsActive = false;
            elapsed = 0f;
        }
    }
}
```

`ImpulseEnvelope.cs`:
```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// A short decaying wobble for the camera. Strength is 0..1; the offset never exceeds MaxOffset metres. Advanced with
    /// scaled time by its owner, so it holds still while the game is paused. A new impulse replaces the running one only
    /// when it is stronger than what is left of it.
    /// </summary>
    public sealed class ImpulseEnvelope
    {
        public const float MaxOffset = 0.08f;
        public const float Duration = 0.5f;

        float strength;
        float elapsed = Duration;
        float phaseX;
        float phaseY;

        /// <summary>Current strength after decay (0..1).</summary>
        public float Amplitude
        {
            get
            {
                if (elapsed >= Duration)
                    return 0f;
                var left = 1f - elapsed / Duration;
                return strength * left * left;
            }
        }

        public Vector2 Offset
        {
            get
            {
                var amplitude = Amplitude;
                if (amplitude <= 0f)
                    return Vector2.zero;
                return amplitude * MaxOffset * new Vector2(Mathf.Sin(elapsed * 55f + phaseX), Mathf.Sin(elapsed * 71f + phaseY));
            }
        }

        public void Add(float newStrength, float newPhaseX, float newPhaseY)
        {
            newStrength = Mathf.Clamp01(newStrength);
            if (newStrength <= 0f || newStrength <= Amplitude)
                return;
            strength = newStrength;
            elapsed = 0f;
            phaseX = newPhaseX;
            phaseY = newPhaseY;
        }

        public void Advance(float dt)
        {
            if (dt > 0f && elapsed < Duration)
                elapsed += dt;
        }

        public void Reset() => elapsed = Duration;
    }
}
```

`PresentationSettings.cs`:
```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>Presentation switches. Turning any of them changes only what is shown, never a gameplay result.</summary>
    [Serializable]
    public sealed class PresentationSettings
    {
        public bool enabled = true;
        public PresentationQuality quality = PresentationQuality.High;
        [Range(0f, 1f)] public float masterVolume = 0.8f;
        /// <summary>0 turns the camera impulse off.</summary>
        [Range(0f, 1f)] public float cameraShake = 0.5f;
    }
}
```

- [ ] **Step 4: Run both test classes**

```bash
Tools/run-tests.sh EditMode "Blackglass.Tests.PresentationVisibilityTests"
Tools/run-tests.sh EditMode "Blackglass.Tests.PresentationCoreTests"
```
Expected: `EXIT=0`, all passed (9 + 12 tests; if `total` differs, count the `[Test]` methods).

- [ ] **Step 5: Verification note:** no commit. Append "Task 1 done, EditMode +N" to `.claude/scratchpad/phase125/progress.md`.

---

### Task 2: Gameplay records and events (`Fired`, `ShotResolved`, `AreaResolved`)

**Files:**
- Create: `Assets/_Project/Scripts/Presentation/ShotRecord.cs`, `AreaRecord.cs`
- Modify: `Assets/_Project/Scripts/Units/UnitAttacker.cs` (`TryAttack`), `Assets/_Project/Scripts/Abilities/UnitAbilities.cs` (`Apply`, `Hit`)
- Test: `Assets/_Project/Tests/PlayMode/CombatRecordsPlayModeTests.cs`

**Interfaces:**
- Consumes: `CoverRules.ResolveHit(bool inCover, float hitChance, float roll)`, `LineOfSight.EyeHeight`.
- Produces:
  - `enum ShotOutcome { Hit, CoverMiss }`
  - `readonly struct ShotRecord { const float BodyHeight = 0.3f; ctor(int emitterId, int sequence, Health source, Health target, Vector3 origin, Vector3 aimPoint, ShotOutcome outcome, bool isMelee, AbilityDefinition ability); int EmitterId; int Sequence; Health Source; Health Target; Vector3 Origin; Vector3 AimPoint; ShotOutcome Outcome; bool IsMelee; AbilityDefinition Ability; }`
  - `readonly struct AreaRecord { ctor(int emitterId, int sequence, Health caster, AbilityDefinition ability, Vector3 center, float radius); int EmitterId; int Sequence; Health Caster; AbilityDefinition Ability; Vector3 Center; float Radius; }`
  - `UnitAttacker.Fired : event Action<ShotRecord>` (raised after `Attacked`/`Missed`, once per discharged shot)
  - `UnitAbilities.ShotResolved : event Action<ShotRecord>`, `UnitAbilities.AreaResolved : event Action<AreaRecord>`

- [ ] **Step 1: Write the failing PlayMode tests**

`CombatRecordsPlayModeTests.cs`:
```csharp
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Utils;

namespace Blackglass.Tests
{
    public class CombatRecordsPlayModeTests
    {
        TestWorld world;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            world.CreateEnvironment();
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        static UnitAttacker AttackerOf(Component c) => c.GetComponent<UnitAttacker>();
        static Health HealthOf(Component c) => c.GetComponent<Health>();

        [UnityTest]
        public IEnumerator Fired_IsRaisedOncePerDischargedShot_AfterAttacked_WithTheShotsFacts()
        {
            var shooter = world.CreateFighter(new Vector3(0f, 0f, -5f), role: CombatRole.Ranged, range: 12f, cooldown: 0.2f);
            var target = world.CreateDummy(new Vector3(0f, 0f, 3f));
            yield return null;
            var attacker = AttackerOf(shooter);
            var order = new List<string>();
            var records = new List<ShotRecord>();
            attacker.Attacked += _ => order.Add("Attacked");
            attacker.Missed += _ => order.Add("Missed");
            attacker.Fired += r => { order.Add("Fired"); records.Add(r); };

            Assert.That(attacker.TryAttack(target), Is.True);

            Assert.That(order, Is.EqualTo(new[] { "Attacked", "Fired" }), "Fired comes after Attacked (decision 037 exposure is applied inside Attacked)");
            var record = records[0];
            Assert.That(record.Outcome, Is.EqualTo(ShotOutcome.Hit));
            Assert.That(record.Source, Is.EqualTo(HealthOf(shooter)));
            Assert.That(record.Target, Is.EqualTo(target));
            Assert.That(record.IsMelee, Is.False);
            Assert.That(record.Ability, Is.Null);
            Assert.That(record.Sequence, Is.EqualTo(1));
            Assert.That(record.AimPoint, Is.EqualTo(target.transform.position + Vector3.up * ShotRecord.BodyHeight).Using(Vector3EqualityComparer.Instance));
            Assert.That(record.Origin, Is.EqualTo(shooter.transform.position + Vector3.up * LineOfSight.EyeHeight).Using(Vector3EqualityComparer.Instance));

            yield return new WaitForSeconds(0.3f);
            attacker.TryAttack(target);
            Assert.That(records[1].Sequence, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator Fired_ForAKillingBlow_StillCarriesTheTargetsPositionSnapshot()
        {
            var shooter = world.CreateFighter(new Vector3(0f, 0f, -5f), damage: 500, role: CombatRole.Ranged, range: 12f);
            var target = world.CreateDummy(new Vector3(1f, 0f, 3f));
            yield return null;
            var records = new List<ShotRecord>();
            AttackerOf(shooter).Fired += r => records.Add(r);

            AttackerOf(shooter).TryAttack(target);

            Assert.That(target.IsAlive, Is.False);
            Assert.That(records.Count, Is.EqualTo(1));
            Assert.That(records[0].AimPoint.x, Is.EqualTo(1f).Within(1e-4f));
        }

        [UnityTest]
        public IEnumerator Fired_IsNotRaisedForARejectedAttack()
        {
            var shooter = world.CreateFighter(new Vector3(0f, 0f, -5f), role: CombatRole.Ranged, range: 5f);
            var far = world.CreateDummy(new Vector3(0f, 0f, 20f));
            yield return null;
            var fired = 0;
            AttackerOf(shooter).Fired += _ => fired++;

            Assert.That(AttackerOf(shooter).TryAttack(far), Is.False, "out of range");
            Assert.That(AttackerOf(shooter).TryAttack(null), Is.False);
            Assert.That(fired, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator Fired_IsNotRaisedDuringCooldown()
        {
            var shooter = world.CreateFighter(new Vector3(0f, 0f, -5f), role: CombatRole.Ranged, range: 12f, cooldown: 5f);
            var target = world.CreateDummy(new Vector3(0f, 0f, 3f));
            yield return null;
            var fired = 0;
            AttackerOf(shooter).Fired += _ => fired++;
            AttackerOf(shooter).TryAttack(target);
            Assert.That(AttackerOf(shooter).TryAttack(target), Is.False);
            Assert.That(fired, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator Fired_ForAMelee_IsMarkedMelee()
        {
            var shooter = world.CreateFighter(new Vector3(0f, 0f, 0f));
            var target = world.CreateDummy(new Vector3(0f, 0f, 1.2f));
            yield return null;
            var records = new List<ShotRecord>();
            AttackerOf(shooter).Fired += r => records.Add(r);
            Assert.That(AttackerOf(shooter).TryAttack(target), Is.True);
            Assert.That(records[0].IsMelee, Is.True);
        }

        [UnityTest]
        public IEnumerator Fired_OnACoverMiss_IsRaisedAfterMissed_AndNoDamageIsDealt()
        {
            world.Dispose();
            world = new TestWorld();
            var environment = world.CreateEnvironment((new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f)));
            var point = world.CreateCoverPoint(new Vector3(0f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment));
            var registry = world.CreateRegistry(point);
            var defender = world.CreateFighter(new Vector3(0f, 0f, -3f), role: CombatRole.Ranged, range: 8f, registry: registry);
            Assert.That(defender.Issue(new MoveToCoverCommand(point)), Is.True);
            yield return TestWorld.WaitUntil(() => defender.GetComponent<UnitCover>().Status == CoverStatus.Occupied, 10f);
            yield return new WaitForFixedUpdate();
            var shooter = world.CreateFighter(new Vector3(0f, 0f, 5f), damage: 10, role: CombatRole.Ranged, range: 12f);
            shooter.GetComponent<AutoRetaliate>().enabled = false;
            AttackerOf(shooter).HitRoll = () => 0.99f;
            yield return new WaitForFixedUpdate();
            var order = new List<string>();
            var records = new List<ShotRecord>();
            AttackerOf(shooter).Attacked += _ => order.Add("Attacked");
            AttackerOf(shooter).Missed += _ => order.Add("Missed");
            AttackerOf(shooter).Fired += r => { order.Add("Fired"); records.Add(r); };

            Assert.That(AttackerOf(shooter).TryAttack(HealthOf(defender)), Is.True);

            Assert.That(order, Is.EqualTo(new[] { "Missed", "Fired" }));
            Assert.That(records[0].Outcome, Is.EqualTo(ShotOutcome.CoverMiss));
            Assert.That(HealthOf(defender).Current, Is.EqualTo(HealthOf(defender).Max));
        }

        // ---- abilities ----

        [UnityTest]
        public IEnumerator AimedShot_RaisesShotResolvedOnce_WithTheAbility_AndARefusedUseRaisesNothing()
        {
            var caster = world.CreateFighter(new Vector3(0f, 0f, -5f), role: CombatRole.Ranged, range: 12f);
            var encounter = world.CreateEncounter();
            var target = world.CreateDummy(new Vector3(0f, 0f, 3f));
            encounter.Initialize(new[] { HealthOf(caster) }, new[] { target });
            var aimed = world.CreateAimedShot();
            var abilities = world.AddAbilities(caster, encounter, aimed);
            yield return null;
            var records = new List<ShotRecord>();
            abilities.ShotResolved += records.Add;

            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(aimed, target)), Is.True);
            Assert.That(records.Count, Is.EqualTo(1));
            Assert.That(records[0].Ability, Is.EqualTo(aimed));
            Assert.That(records[0].Outcome, Is.EqualTo(ShotOutcome.Hit));
            Assert.That(records[0].Source, Is.EqualTo(HealthOf(caster)));

            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(aimed, target)), Is.False, "on cooldown");
            Assert.That(records.Count, Is.EqualTo(1), "a refused use presents nothing");
        }

        [UnityTest]
        public IEnumerator Blast_RaisesAreaResolvedOnce_WithTheGameplayCentreAndRadius_EvenWithNoVictims()
        {
            var caster = world.CreateFighter(new Vector3(0f, 0f, -5f), role: CombatRole.Ranged, range: 12f);
            var encounter = world.CreateEncounter();
            var victim = world.CreateDummy(new Vector3(0.5f, 0f, 3f));
            encounter.Initialize(new[] { HealthOf(caster) }, new[] { victim });
            var blast = world.CreateBlast();
            var abilities = world.AddAbilities(caster, encounter, blast);
            yield return null;
            var areas = new List<AreaRecord>();
            abilities.AreaResolved += areas.Add;
            var shots = 0;
            abilities.ShotResolved += _ => shots++;

            Assert.That(abilities.TryUse(AbilityCommand.AtGround(blast, new Vector3(0f, 0f, 3f))), Is.True);

            Assert.That(areas.Count, Is.EqualTo(1));
            Assert.That(areas[0].Center, Is.EqualTo(new Vector3(0f, 0f, 3f)).Using(Vector3EqualityComparer.Instance));
            Assert.That(areas[0].Radius, Is.EqualTo(3f));
            Assert.That(areas[0].Ability, Is.EqualTo(blast));
            Assert.That(shots, Is.EqualTo(0), "an area is not a shot");
            Assert.That(victim.Current, Is.LessThan(victim.Max), "the blast still hit (gameplay unchanged)");
        }

        [UnityTest]
        public IEnumerator Mend_RaisesNoShotOrAreaRecord()
        {
            var caster = world.CreateFighter(new Vector3(0f, 0f, 0f));
            var encounter = world.CreateEncounter();
            encounter.Initialize(new[] { HealthOf(caster) }, new Health[0]);
            var mend = world.CreateMend();
            var abilities = world.AddAbilities(caster, encounter, mend);
            HealthOf(caster).TakeDamage(30);
            yield return null;
            var raised = 0;
            abilities.ShotResolved += _ => raised++;
            abilities.AreaResolved += _ => raised++;
            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(mend, HealthOf(caster))), Is.True);
            Assert.That(raised, Is.EqualTo(0));
        }
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
Tools/run-tests.sh PlayMode "Blackglass.Tests.CombatRecordsPlayModeTests"
```
Expected: `EXIT=1` (`CS0234`/`CS1061`: `ShotRecord`, `Fired` missing).

- [ ] **Step 3: Implement the records**

`ShotRecord.cs`:
```csharp
using UnityEngine;

namespace Blackglass
{
    public enum ShotOutcome
    {
        Hit,
        /// <summary>The target's cover turned the shot away.</summary>
        CoverMiss,
    }

    /// <summary>
    /// What a discharged unit-targeted shot (a basic attack or Aimed Shot) tells the presentation layer, captured at the
    /// moment gameplay resolved it. Positions are snapshots: presentation never reads the target again. Read-only; it
    /// decides nothing.
    /// </summary>
    public readonly struct ShotRecord
    {
        /// <summary>Height above the target's pivot of the point a shot is drawn at (chest).</summary>
        public const float BodyHeight = 0.3f;

        public ShotRecord(int emitterId, int sequence, Health source, Health target, Vector3 origin, Vector3 aimPoint,
            ShotOutcome outcome, bool isMelee, AbilityDefinition ability)
        {
            EmitterId = emitterId;
            Sequence = sequence;
            Source = source;
            Target = target;
            Origin = origin;
            AimPoint = aimPoint;
            Outcome = outcome;
            IsMelee = isMelee;
            Ability = ability;
        }

        /// <summary>Identifies the emitting component; with Sequence it makes a record unique.</summary>
        public int EmitterId { get; }
        public int Sequence { get; }
        public Health Source { get; }
        public Health Target { get; }
        /// <summary>The shooter's eye at the time of the shot (a fallback when no muzzle is known).</summary>
        public Vector3 Origin { get; }
        public Vector3 AimPoint { get; }
        public ShotOutcome Outcome { get; }
        public bool IsMelee { get; }
        /// <summary>The ability that fired it, or null for a basic attack.</summary>
        public AbilityDefinition Ability { get; }
    }
}
```

`AreaRecord.cs`:
```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// A ground-targeted damage ability has been applied: where, how large (the gameplay radius it actually used) and
    /// by whom. Raised even when nobody was hit. Read-only; it decides nothing.
    /// </summary>
    public readonly struct AreaRecord
    {
        public AreaRecord(int emitterId, int sequence, Health caster, AbilityDefinition ability, Vector3 center, float radius)
        {
            EmitterId = emitterId;
            Sequence = sequence;
            Caster = caster;
            Ability = ability;
            Center = center;
            Radius = radius;
        }

        public int EmitterId { get; }
        public int Sequence { get; }
        public Health Caster { get; }
        public AbilityDefinition Ability { get; }
        public Vector3 Center { get; }
        public float Radius { get; }
    }
}
```

- [ ] **Step 4: Modify `UnitAttacker`** — add the event, the sequence counter and restructure `TryAttack` without changing behaviour (the roll is still taken in the same place).

In `UnitAttacker.cs`, after the `Missed` event declaration add:
```csharp
        /// <summary>
        /// Raised once per discharged shot (hit or miss), after Attacked or Missed, so anything those events set in motion
        /// (the intelligence service exposing a hostile shooter) is already in force. Never raised for a rejected attempt.
        /// Presentation only: the record is a read-only snapshot.
        /// </summary>
        public event Action<ShotRecord> Fired;
```
and a field next to `ShotsFired`: `int shotSequence;`.

Replace the body of `TryAttack` after the early return with:
```csharp
            nextAttackTime = Time.time + cooldown;
            ShotsFired++;
            var inCover = IsTargetInCover(target, out var hitChance);
            var landed = CoverRules.ResolveHit(inCover, hitChance, HitRoll());
            // The record is a snapshot taken before damage: a victim that dies (and deactivates) changes nothing in it.
            var record = new ShotRecord(GetInstanceID(), ++shotSequence, OwnHealth, target,
                transform.position + Vector3.up * LineOfSight.EyeHeight, target.transform.position + Vector3.up * ShotRecord.BodyHeight,
                landed ? ShotOutcome.Hit : ShotOutcome.CoverMiss, role == CombatRole.Melee, null);
            if (landed)
            {
                Hits++;
                target.TakeDamage(damage, OwnHealth);
                Attacked?.Invoke(target);
            }
            else
            {
                Missed?.Invoke(target);
            }
            Fired?.Invoke(record);
            return true;
```

- [ ] **Step 5: Modify `UnitAbilities`**

Add events and a counter near the other events:
```csharp
        /// <summary>Raised after a unit-targeted damage ability (Aimed Shot) was applied. Presentation only.</summary>
        public event Action<ShotRecord> ShotResolved;

        /// <summary>Raised after a ground-targeted damage ability (Blast) was applied, even when nobody was hit. Presentation only.</summary>
        public event Action<AreaRecord> AreaResolved;

        int recordSequence;
```
Change `Apply`'s two damage cases to:
```csharp
                case AbilityEffect.Damage when ability.TargetMode == AbilityTargetMode.Unit:
                {
                    var origin = transform.position + Vector3.up * LineOfSight.EyeHeight;
                    var aimPoint = target.transform.position + Vector3.up * ShotRecord.BodyHeight;
                    var landed = Hit(ability, target, transform.position, source);
                    ShotResolved?.Invoke(new ShotRecord(GetInstanceID(), ++recordSequence, source, target, origin, aimPoint,
                        landed ? ShotOutcome.Hit : ShotOutcome.CoverMiss, false, ability));
                    break;
                }
                case AbilityEffect.Damage:
                    // A snapshot first: a victim dying (and deactivating) mid-blast must not change who else is hit.
                    CollectArea(ability, aim, areaBuffer);
                    for (var i = 0; i < areaBuffer.Count; i++)
                        Hit(ability, areaBuffer[i], aim, source);
                    AreaResolved?.Invoke(new AreaRecord(GetInstanceID(), ++recordSequence, source, ability, aim, ability.Radius));
                    break;
```
and change `Hit` to return whether it landed:
```csharp
        // The effect on one unit: cover (when the ability says it applies) may turn it away; otherwise it takes the damage.
        bool Hit(AbilityDefinition ability, Health victim, Vector3 from, Health source)
        {
            var lands = true;
            if (ability.CoverRule == AbilityCoverRule.Applies && victim.TryGetComponent<UnitCover>(out var cover)
                && cover.IsProtectedFrom(from))
                lands = CoverRules.ResolveHit(true, cover.HitChance, Attacker.RollHit());
            if (!lands)
            {
                Missed?.Invoke(ability, victim);
                return false;
            }
            victim.TakeDamage(EffectiveAmount(ability), source);
            return true;
        }
```

- [ ] **Step 6: Run the new tests, then the combat/ability suites that touch these methods**

```bash
Tools/run-tests.sh PlayMode "Blackglass.Tests.CombatRecordsPlayModeTests"
Tools/run-tests.sh PlayMode "Blackglass.Tests.CoverCombatPlayModeTests"
Tools/run-tests.sh PlayMode "Blackglass.Tests.CommandableUnitAbilityPlayModeTests"
Tools/run-tests.sh EditMode "Blackglass.Tests.AbilityCommandTests"
```
Expected: all `EXIT=0`.

- [ ] **Step 7: Record progress** in `.claude/scratchpad/phase125/progress.md`.

---

### Task 3: Presentation profile asset and its integration points

**Files:**
- Create: `Assets/_Project/Scripts/Presentation/CombatPresentationProfile.cs`
- Modify: `Assets/_Project/Scripts/Items/WeaponVisualAsset.cs`, `Assets/_Project/Scripts/Abilities/AbilityDefinition.cs`
- Test: `Assets/_Project/Tests/EditMode/CombatPresentationProfileTests.cs`

**Interfaces:**
- Consumes: `ImpactSurfaceKind`, `CosmeticRandom` (Task 1).
- Produces:
  - `[Serializable] sealed class AudioCue { bool IsEmpty; float Volume; float PitchMin; float PitchMax; int Priority; float MinDistance; float MaxDistance; float MinInterval; AudioClip Pick(CosmeticRandom random); internal static AudioCue Create(float volume = 1f, int priority = 128, float minInterval = 0.06f, params AudioClip[] clips) }`
  - `[Serializable] sealed class TracerStyle { GameObject Prefab; float Width; float Length; float Speed; Color StartColor; Color EndColor; bool IsUsable; internal static TracerStyle Create(GameObject prefab, float width = 0.035f, float length = 6f, float speed = 300f) }`
  - `[Serializable] sealed class SurfaceImpact { ImpactSurfaceKind Surface; GameObject Prefab; float Scale; AudioCue Cue; internal static SurfaceImpact Create(ImpactSurfaceKind surface, GameObject prefab, AudioCue cue = null, float scale = 1f) }`
  - `sealed class CombatPresentationProfile : ScriptableObject` with: `GameObject MuzzleEffect; float MuzzleScale; TracerStyle Tracer; AudioCue FireCue; IReadOnlyList<SurfaceImpact> Impacts; SurfaceImpact ImpactFor(ImpactSurfaceKind kind)` (exact, else Default, else null)`; float RecoilKick; float RecoilSeconds; GameObject AreaEffect; float ReferenceRadius; AudioCue AreaCue; float CameraImpulse; GameObject PulseEffect; AudioCue PulseCue; AudioCue DeathCue; List<string> Problems(); static CombatPresentationProfile CreateEmpty()`, plus internal setters `SetMuzzle(GameObject, float)`, `SetTracer(TracerStyle)`, `SetFireCue(AudioCue)`, `SetImpacts(params SurfaceImpact[])`, `SetRecoil(float kick, float seconds)`, `SetArea(GameObject, float referenceRadius, AudioCue, float cameraImpulse)`, `SetPulse(GameObject, AudioCue)`, `SetDeathCue(AudioCue)`.
  - `WeaponVisualAsset.Presentation : CombatPresentationProfile` and `internal void SetPresentation(CombatPresentationProfile)`; `AbilityDefinition.Presentation : CombatPresentationProfile` and `internal void SetPresentation(CombatPresentationProfile)`.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/EditMode/CombatPresentationProfileTests.cs`:
```csharp
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class CombatPresentationProfileTests
    {
        static GameObject Prefab(string name) => new GameObject(name);

        [Test]
        public void ImpactFor_PrefersTheExactSurface_ThenDefault_ThenNothing()
        {
            var profile = CombatPresentationProfile.CreateEmpty();
            var concrete = Prefab("Concrete");
            var fallback = Prefab("Default");
            profile.SetImpacts(SurfaceImpact.Create(ImpactSurfaceKind.Concrete, concrete), SurfaceImpact.Create(ImpactSurfaceKind.Default, fallback));
            Assert.That(profile.ImpactFor(ImpactSurfaceKind.Concrete).Prefab, Is.EqualTo(concrete));
            Assert.That(profile.ImpactFor(ImpactSurfaceKind.Metal).Prefab, Is.EqualTo(fallback));
            profile.SetImpacts(SurfaceImpact.Create(ImpactSurfaceKind.Concrete, concrete));
            Assert.That(profile.ImpactFor(ImpactSurfaceKind.Metal), Is.Null);
            Object.DestroyImmediate(concrete);
            Object.DestroyImmediate(fallback);
        }

        [Test]
        public void EmptyProfile_HasNoProblems_AndNothingToShow()
        {
            var profile = CombatPresentationProfile.CreateEmpty();
            Assert.That(profile.Problems(), Is.Empty);
            Assert.That(profile.MuzzleEffect, Is.Null);
            Assert.That(profile.Tracer.IsUsable, Is.False);
            Assert.That(profile.FireCue.IsEmpty, Is.True);
            Assert.That(profile.ImpactFor(ImpactSurfaceKind.Default), Is.Null);
        }

        [Test]
        public void Problems_ReportsAColliderOnAnEffectPrefab_DuplicateSurfaces_AndAnAreaWithoutAReferenceRadius()
        {
            var profile = CombatPresentationProfile.CreateEmpty();
            var bad = Prefab("Bad");
            bad.AddComponent<BoxCollider>();
            var ok = Prefab("Ok");
            profile.SetMuzzle(bad, 1f);
            profile.SetImpacts(SurfaceImpact.Create(ImpactSurfaceKind.Metal, ok), SurfaceImpact.Create(ImpactSurfaceKind.Metal, ok));
            profile.SetArea(ok, 0f, null, 0.5f);
            var problems = profile.Problems();
            Assert.That(problems, Has.Some.Contains("collider"));
            Assert.That(problems, Has.Some.Contains("Metal").And.Some.Contains("twice"));
            Assert.That(problems, Has.Some.Contains("reference radius"));
            Object.DestroyImmediate(bad);
            Object.DestroyImmediate(ok);
        }

        [Test]
        public void Problems_ReportsAnImpactEntryWithNeitherPrefabNorSound()
        {
            var profile = CombatPresentationProfile.CreateEmpty();
            profile.SetImpacts(SurfaceImpact.Create(ImpactSurfaceKind.Concrete, null));
            Assert.That(profile.Problems(), Has.Some.Contains("Concrete"));
        }

        [Test]
        public void AudioCue_Pick_SkipsMissingClips_AndIsNullWhenEmpty()
        {
            var empty = AudioCue.Create(1f, 128, 0.06f, null, null);
            Assert.That(empty.IsEmpty, Is.True);
            Assert.That(empty.Pick(new CosmeticRandom(1)), Is.Null);

            var clip = AudioClip.Create("c", 100, 1, 44100, false);
            var cue = AudioCue.Create(1f, 128, 0.06f, null, clip, null);
            Assert.That(cue.IsEmpty, Is.False);
            for (var i = 0; i < 10; i++)
                Assert.That(cue.Pick(new CosmeticRandom(i)), Is.EqualTo(clip));
            Object.DestroyImmediate(clip);
        }

        [Test]
        public void TracerStyle_IsUsableOnlyWithAPrefab()
        {
            Assert.That(new TracerStyle().IsUsable, Is.False);
            var prefab = Prefab("Tracer");
            Assert.That(TracerStyle.Create(prefab).IsUsable, Is.True);
            Object.DestroyImmediate(prefab);
        }

        [Test]
        public void WeaponVisualAsset_AndAbilityDefinition_CarryAnOptionalProfile()
        {
            var visual = WeaponVisualAsset.Create(null, Vector3.zero, Vector3.zero, Vector3.one);
            Assert.That(visual.Presentation, Is.Null);
            var profile = CombatPresentationProfile.CreateEmpty();
            visual.SetPresentation(profile);
            Assert.That(visual.Presentation, Is.EqualTo(profile));

            var ability = AbilityDefinition.Create("Blast", AbilityTargetMode.Ground, 12f, true, AbilityCoverRule.Ignored, 10f, AbilityEffect.Damage, 35, 3f);
            Assert.That(ability.Presentation, Is.Null);
            ability.SetPresentation(profile);
            Assert.That(ability.Presentation, Is.EqualTo(profile));
        }
    }
}
```

- [ ] **Step 2: Run to verify failure** — `Tools/run-tests.sh EditMode "Blackglass.Tests.CombatPresentationProfileTests"`. Expected: `EXIT=1`, missing types.

- [ ] **Step 3: Implement `CombatPresentationProfile.cs`**
```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>One sound: clips to pick from, loudness, pitch spread, voice priority (0 most important), falloff distances and a minimum interval between plays.</summary>
    [Serializable]
    public sealed class AudioCue
    {
        [SerializeField] AudioClip[] clips = new AudioClip[0];
        [SerializeField, Range(0f, 1f)] float volume = 1f;
        [SerializeField, Range(0.5f, 2f)] float pitchMin = 0.95f;
        [SerializeField, Range(0.5f, 2f)] float pitchMax = 1.05f;
        [SerializeField, Range(0, 256)] int priority = 128;
        [SerializeField, Min(0.1f)] float minDistance = 4f;
        [SerializeField, Min(1f)] float maxDistance = 45f;
        [SerializeField, Min(0f)] float minInterval = 0.06f;

        public float Volume => volume;
        public float PitchMin => pitchMin;
        public float PitchMax => Mathf.Max(pitchMin, pitchMax);
        public int Priority => priority;
        public float MinDistance => minDistance;
        public float MaxDistance => Mathf.Max(minDistance + 0.1f, maxDistance);
        public float MinInterval => minInterval;

        public bool IsEmpty
        {
            get
            {
                if (clips == null)
                    return true;
                for (var i = 0; i < clips.Length; i++)
                    if (clips[i] != null)
                        return false;
                return true;
            }
        }

        /// <summary>A random non-missing clip, or null when there is none.</summary>
        public AudioClip Pick(CosmeticRandom random)
        {
            if (IsEmpty)
                return null;
            var count = 0;
            for (var i = 0; i < clips.Length; i++)
                if (clips[i] != null)
                    count++;
            var chosen = Mathf.Min(count - 1, (int)(random.Value() * count));
            for (var i = 0; i < clips.Length; i++)
            {
                if (clips[i] == null)
                    continue;
                if (chosen-- == 0)
                    return clips[i];
            }
            return null;
        }

        internal static AudioCue Create(float volume = 1f, int priority = 128, float minInterval = 0.06f, params AudioClip[] clips) =>
            new AudioCue { volume = volume, priority = priority, minInterval = minInterval, clips = clips ?? new AudioClip[0] };
    }

    /// <summary>The tracer: a pooled prefab (a LineRenderer with a TracerView) and how the streak looks and moves. Speed 0 or less draws it at once.</summary>
    [Serializable]
    public sealed class TracerStyle
    {
        [SerializeField] GameObject prefab;
        [SerializeField, Min(0.005f)] float width = 0.035f;
        [SerializeField, Min(0.1f)] float length = 6f;
        [SerializeField] float speed = 300f;
        [SerializeField] Color startColor = new Color(1f, 0.88f, 0.6f, 0.95f);
        [SerializeField] Color endColor = new Color(1f, 0.6f, 0.25f, 0f);

        public GameObject Prefab => prefab;
        public float Width => width;
        public float Length => length;
        public float Speed => speed;
        public Color StartColor => startColor;
        public Color EndColor => endColor;
        public bool IsUsable => prefab != null;

        internal static TracerStyle Create(GameObject prefab, float width = 0.035f, float length = 6f, float speed = 300f) =>
            new TracerStyle { prefab = prefab, width = width, length = length, speed = speed };
    }

    /// <summary>The effect and sound for an impact on one kind of surface.</summary>
    [Serializable]
    public sealed class SurfaceImpact
    {
        [SerializeField] ImpactSurfaceKind surface;
        [SerializeField] GameObject prefab;
        [SerializeField, Min(0.1f)] float scale = 1f;
        [SerializeField] AudioCue cue = new AudioCue();

        public ImpactSurfaceKind Surface => surface;
        public GameObject Prefab => prefab;
        public float Scale => scale;
        public AudioCue Cue => cue;

        internal static SurfaceImpact Create(ImpactSurfaceKind surface, GameObject prefab, AudioCue cue = null, float scale = 1f) =>
            new SurfaceImpact { surface = surface, prefab = prefab, scale = scale, cue = cue ?? new AudioCue() };
    }

    /// <summary>
    /// How a weapon or an ability looks and sounds, as shared immutable data: the muzzle effect, the tracer, the impact per
    /// surface, the firing sound, the recoil, and (for an area ability) the explosion at a reference radius. It holds no
    /// playback state and decides nothing. Every part is optional; a missing part is simply not shown.
    /// </summary>
    [CreateAssetMenu(menuName = "Blackglass/Combat Presentation Profile", fileName = "CombatPresentation")]
    public sealed class CombatPresentationProfile : ScriptableObject
    {
        [Header("Weapon")]
        [SerializeField] GameObject muzzleEffect;
        [SerializeField, Min(0.1f)] float muzzleScale = 1f;
        [SerializeField] TracerStyle tracer = new TracerStyle();
        [SerializeField] AudioCue fireCue = new AudioCue();
        [SerializeField] SurfaceImpact[] impacts = new SurfaceImpact[0];
        [SerializeField, Min(0f)] float recoilKick = 0.035f;
        [SerializeField, Min(0.01f)] float recoilSeconds = 0.12f;

        [Header("Area ability")]
        [SerializeField] GameObject areaEffect;
        // The gameplay radius the area prefab was authored for; the instance is scaled by radius / referenceRadius.
        [SerializeField, Min(0f)] float referenceRadius = 3f;
        [SerializeField] AudioCue areaCue = new AudioCue();
        [SerializeField, Range(0f, 1f)] float cameraImpulse = 0.6f;

        [Header("Support and characters")]
        [SerializeField] GameObject pulseEffect;
        [SerializeField] AudioCue pulseCue = new AudioCue();
        [SerializeField] AudioCue deathCue = new AudioCue();

        public GameObject MuzzleEffect => muzzleEffect;
        public float MuzzleScale => muzzleScale;
        public TracerStyle Tracer => tracer ?? (tracer = new TracerStyle());
        public AudioCue FireCue => fireCue ?? (fireCue = new AudioCue());
        public IReadOnlyList<SurfaceImpact> Impacts => impacts ?? Array.Empty<SurfaceImpact>();
        public float RecoilKick => recoilKick;
        public float RecoilSeconds => recoilSeconds;
        public GameObject AreaEffect => areaEffect;
        public float ReferenceRadius => referenceRadius;
        public AudioCue AreaCue => areaCue ?? (areaCue = new AudioCue());
        public float CameraImpulse => cameraImpulse;
        public GameObject PulseEffect => pulseEffect;
        public AudioCue PulseCue => pulseCue ?? (pulseCue = new AudioCue());
        public AudioCue DeathCue => deathCue ?? (deathCue = new AudioCue());

        /// <summary>The entry for this surface, else the Default entry, else null.</summary>
        public SurfaceImpact ImpactFor(ImpactSurfaceKind kind)
        {
            SurfaceImpact fallback = null;
            foreach (var entry in Impacts)
            {
                if (entry == null)
                    continue;
                if (entry.Surface == kind)
                    return entry;
                if (entry.Surface == ImpactSurfaceKind.Default)
                    fallback = entry;
            }
            return fallback;
        }

        /// <summary>What is wrong with this profile, one line each (empty when it is fine). The asset builder and the tests assert it is empty.</summary>
        public List<string> Problems()
        {
            var problems = new List<string>();
            CheckPrefab(problems, "muzzle effect", muzzleEffect);
            CheckPrefab(problems, "tracer", Tracer.Prefab);
            CheckPrefab(problems, "area effect", areaEffect);
            CheckPrefab(problems, "pulse effect", pulseEffect);
            if (areaEffect != null && referenceRadius <= 0f)
                problems.Add("the area effect needs a reference radius above 0");
            var seen = new HashSet<ImpactSurfaceKind>();
            foreach (var entry in Impacts)
            {
                if (entry == null)
                    continue;
                if (!seen.Add(entry.Surface))
                    problems.Add($"the {entry.Surface} impact is listed twice");
                if (entry.Prefab == null && entry.Cue.IsEmpty)
                    problems.Add($"the {entry.Surface} impact has neither an effect nor a sound");
                CheckPrefab(problems, entry.Surface + " impact", entry.Prefab);
            }
            return problems;
        }

        static void CheckPrefab(List<string> problems, string what, GameObject prefab)
        {
            if (prefab == null)
                return;
            if (prefab.GetComponentInChildren<Collider>(true) != null)
                problems.Add($"the {what} prefab has a collider (effects must never enter gameplay queries)");
            if (prefab.GetComponentInChildren<Rigidbody>(true) != null)
                problems.Add($"the {what} prefab has a rigidbody");
        }

        /// <summary>A profile that shows nothing. Used when a unit has no profile and the presenter has no fallback asset.</summary>
        public static CombatPresentationProfile CreateEmpty()
        {
            var profile = CreateInstance<CombatPresentationProfile>();
            profile.hideFlags = HideFlags.DontSave;
            return profile;
        }

        internal void SetMuzzle(GameObject prefab, float scale) { muzzleEffect = prefab; muzzleScale = scale; }
        internal void SetTracer(TracerStyle style) => tracer = style;
        internal void SetFireCue(AudioCue cue) => fireCue = cue;
        internal void SetImpacts(params SurfaceImpact[] entries) => impacts = entries;
        internal void SetRecoil(float kick, float seconds) { recoilKick = kick; recoilSeconds = seconds; }
        internal void SetArea(GameObject prefab, float radius, AudioCue cue, float impulse)
        {
            areaEffect = prefab;
            referenceRadius = radius;
            areaCue = cue ?? new AudioCue();
            cameraImpulse = impulse;
        }
        internal void SetPulse(GameObject prefab, AudioCue cue) { pulseEffect = prefab; pulseCue = cue ?? new AudioCue(); }
        internal void SetDeathCue(AudioCue cue) => deathCue = cue ?? new AudioCue();
    }
}
```
(The `??` above are on plain C# serializable classes, never on `UnityEngine.Object` references.)

- [ ] **Step 4: Modify `WeaponVisualAsset`** — add the field `[SerializeField] CombatPresentationProfile presentation;` after `localScale`, and below the `LocalScale` property:
```csharp
        /// <summary>How this weapon looks and sounds when fired, or null (the presenter's fallback is used).</summary>
        public CombatPresentationProfile Presentation => presentation;

        internal void SetPresentation(CombatPresentationProfile profile) => presentation = profile;
```
- [ ] **Step 5: Modify `AbilityDefinition`** — add `[SerializeField] CombatPresentationProfile presentation;` after `revealSeconds`, and below the `RevealSeconds` property:
```csharp
        /// <summary>How the ability looks and sounds when it resolves (an explosion for Blast), or null. Presentation only.</summary>
        public CombatPresentationProfile Presentation => presentation;

        internal void SetPresentation(CombatPresentationProfile profile) => presentation = profile;
```
- [ ] **Step 6: Run**
```bash
Tools/run-tests.sh EditMode "Blackglass.Tests.CombatPresentationProfileTests"
Tools/run-tests.sh EditMode "Blackglass.Tests.AbilityDefinitionTests"
Tools/run-tests.sh EditMode "Blackglass.Tests.AbilityDefinitionRevealTests"
```
Expected: `EXIT=0`.

---

### Task 4: Muzzle binding, surface classification and the point-visibility query

**Files:**
- Create: `Assets/_Project/Scripts/Presentation/ImpactSurface.cs`
- Modify: `Scripts/Items/UnitWeaponVisual.cs`, `Scripts/Mission/MissionBuilder.cs`, `Scripts/Mission/MissionContent.cs`, `Scripts/Intelligence/IntelligenceService.cs`, `Scripts/Intelligence/Knowledge.cs`
- Test: `Tests/EditMode/ImpactSurfaceTests.cs`, `Tests/PlayMode/MuzzleBindingPlayModeTests.cs`, `Tests/PlayMode/ImpactSurfaceMissionPlayModeTests.cs`, `Tests/PlayMode/PointVisibilityPlayModeTests.cs`

**Interfaces:**
- Consumes: `ImpactSurfaceKind`, `PointVisibility` (Task 1), `MissionBoxKind` (existing: Wall, Baffle, Pillar, LowWall, Crate).
- Produces:
  - `sealed class ImpactSurface : MonoBehaviour { ImpactSurfaceKind Kind; void Set(ImpactSurfaceKind kind) }`
  - `static class ImpactSurfaces { static ImpactSurfaceKind Classify(Collider collider); static ImpactSurfaceKind ForBox(MissionBoxKind kind) }`
  - `UnitWeaponVisual.Muzzle : Transform` (the `Muzzle` child of the placed model, or null) and `UnitWeaponVisual.Shown : WeaponVisualAsset`
  - `IntelligenceService.VisibilityOfPoint(Vector3 world) : PointVisibility`; `Knowledge.PointVisibilityOf(IntelligenceService, Vector3) : PointVisibility` (Visible without a service); `Knowledge.IsObserved(IntelligenceService, Health) : bool` (true without a service; uses `CanTarget`, never the truth view)

- [ ] **Step 1: Write the failing tests**

`Tests/EditMode/ImpactSurfaceTests.cs`:
```csharp
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class ImpactSurfaceTests
    {
        GameObject root;

        [TearDown]
        public void TearDown()
        {
            if (root != null)
                Object.DestroyImmediate(root);
        }

        [Test]
        public void ForBox_MapsStructureToConcrete_AndLowCoverAndCratesToMetal()
        {
            Assert.That(ImpactSurfaces.ForBox(MissionBoxKind.Wall), Is.EqualTo(ImpactSurfaceKind.Concrete));
            Assert.That(ImpactSurfaces.ForBox(MissionBoxKind.Baffle), Is.EqualTo(ImpactSurfaceKind.Concrete));
            Assert.That(ImpactSurfaces.ForBox(MissionBoxKind.Pillar), Is.EqualTo(ImpactSurfaceKind.Concrete));
            Assert.That(ImpactSurfaces.ForBox(MissionBoxKind.LowWall), Is.EqualTo(ImpactSurfaceKind.Metal));
            Assert.That(ImpactSurfaces.ForBox(MissionBoxKind.Crate), Is.EqualTo(ImpactSurfaceKind.Metal));
        }

        [Test]
        public void Classify_UsesTheNearestImpactSurface_ThenHealthThenDefault()
        {
            root = new GameObject("Root");
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.transform.SetParent(root.transform);
            wall.AddComponent<ImpactSurface>().Set(ImpactSurfaceKind.Metal);
            Assert.That(ImpactSurfaces.Classify(wall.GetComponent<Collider>()), Is.EqualTo(ImpactSurfaceKind.Metal));

            var child = GameObject.CreatePrimitive(PrimitiveType.Cube);
            child.transform.SetParent(wall.transform);
            Assert.That(ImpactSurfaces.Classify(child.GetComponent<Collider>()), Is.EqualTo(ImpactSurfaceKind.Metal), "a child collider takes its parent's surface");

            var unit = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            unit.transform.SetParent(root.transform);
            unit.AddComponent<Health>();
            Assert.That(ImpactSurfaces.Classify(unit.GetComponent<Collider>()), Is.EqualTo(ImpactSurfaceKind.Character));

            var plain = GameObject.CreatePrimitive(PrimitiveType.Cube);
            plain.transform.SetParent(root.transform);
            Assert.That(ImpactSurfaces.Classify(plain.GetComponent<Collider>()), Is.EqualTo(ImpactSurfaceKind.Default));
            Assert.That(ImpactSurfaces.Classify(null), Is.EqualTo(ImpactSurfaceKind.Default));
        }
    }
}
```

`Tests/PlayMode/MuzzleBindingPlayModeTests.cs`:
```csharp
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class MuzzleBindingPlayModeTests
    {
        const string FriendlyPath = "Assets/Art/Characters/EnemyUnit/Prefabs/EnemyUnit_Friendly.prefab";
        TestWorld world;

        [SetUp]
        public void SetUp() => world = new TestWorld();

        [TearDown]
        public void TearDown() => world.Dispose();

        GameObject WeaponPrefab(bool withMuzzle)
        {
            var prefab = world.Track(new GameObject("TestWeapon"));
            prefab.SetActive(false);
            var body = new GameObject("Body");
            body.transform.SetParent(prefab.transform, false);
            if (withMuzzle)
            {
                var muzzle = new GameObject("Muzzle");
                muzzle.transform.SetParent(prefab.transform, false);
                muzzle.transform.localPosition = new Vector3(0f, 0f, 0.5f);
            }
            return prefab;
        }

        UnitWeaponVisual UnitWithSocket()
        {
            var unit = world.Track(new GameObject("Unit"));
            var socket = new GameObject(UnitWeaponVisual.SocketName);
            socket.transform.SetParent(unit.transform, false);
            socket.transform.localScale = Vector3.one * 0.01f;   // an imported hierarchy with a scale correction
            return unit.AddComponent<UnitWeaponVisual>();
        }

        [UnityTest]
        public IEnumerator Muzzle_IsFoundWhenTheWeaponIsPlaced_ClearedWhenReplacedOrRemoved_AndFollowsTheWorldPosition()
        {
            var visual = UnitWithSocket();
            yield return null;
            Assert.That(visual.Muzzle, Is.Null, "nothing shown yet");

            var withMuzzle = WeaponVisualAsset.Create(WeaponPrefab(true), Vector3.zero, Vector3.zero, Vector3.one);
            visual.Show(withMuzzle);
            Assert.That(visual.Muzzle, Is.Not.Null);
            Assert.That(visual.Shown, Is.EqualTo(withMuzzle));
            Assert.That(visual.Muzzle.position.z, Is.EqualTo(0.005f).Within(1e-5f), "world position follows the 0.01 scale of the socket");

            var withoutMuzzle = WeaponVisualAsset.Create(WeaponPrefab(false), Vector3.zero, Vector3.zero, Vector3.one);
            visual.Show(withoutMuzzle);
            Assert.That(visual.Muzzle, Is.Null, "a weapon without a Muzzle child has no muzzle");
            LogAssert.NoUnexpectedReceived();

            visual.Show(withMuzzle);
            Assert.That(visual.Muzzle, Is.Not.Null);
            visual.Show(null);
            Assert.That(visual.Muzzle, Is.Null);
            Assert.That(visual.Shown, Is.Null);
        }

        [UnityTest]
        public IEnumerator TheRealRifleOnEnemyUnit_HasAMuzzle()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(FriendlyPath);
            var unit = world.Track(Object.Instantiate(prefab));
            yield return null;
            var visual = unit.GetComponent<UnitWeaponVisual>();
            Assert.That(visual.Muzzle, Is.Not.Null);
            Assert.That(visual.Muzzle.name, Is.EqualTo("Muzzle"));
            Assert.That(visual.Shown, Is.Not.Null);
        }
    }
}
#endif
```

`Tests/PlayMode/ImpactSurfaceMissionPlayModeTests.cs`:
```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class ImpactSurfaceMissionPlayModeTests
    {
        [UnityTest]
        public IEnumerator GeneratedMission_TagsEveryBoxAndFloorWithItsSurface()
        {
            using (var rig = new MissionRig())
            {
                yield return rig.Generate(7);
                var mission = rig.Director.Current;
                Assert.That(mission, Is.Not.Null, "Precondition: the mission generated");
                var children = mission.Geometry.Cast<Transform>().ToDictionary(t => t.name, t => t, System.StringComparer.Ordinal);
                foreach (var box in mission.Layout.Boxes)
                {
                    Assert.That(children.TryGetValue(box.Name, out var cube), Is.True, box.Name);
                    Assert.That(cube.GetComponent<ImpactSurface>().Kind, Is.EqualTo(ImpactSurfaces.ForBox(box.Kind)), box.Name);
                }
                var floors = children.Where(p => p.Key.StartsWith("Floor_")).ToList();
                Assert.That(floors, Is.Not.Empty);
                foreach (var floor in floors)
                    Assert.That(floor.Value.GetComponent<ImpactSurface>().Kind, Is.EqualTo(ImpactSurfaceKind.Concrete));
            }
        }
    }
}
#endif
```

`Tests/PlayMode/PointVisibilityPlayModeTests.cs`:
```csharp
#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class PointVisibilityPlayModeTests
    {
        [Test]
        public void WithoutAService_EverythingIsVisibleAndObserved()
        {
            var unit = new GameObject("U").AddComponent<Health>();
            try
            {
                Assert.That(Knowledge.PointVisibilityOf(null, Vector3.zero), Is.EqualTo(PointVisibility.Visible));
                Assert.That(Knowledge.IsObserved(null, unit), Is.True);
            }
            finally { Object.DestroyImmediate(unit.gameObject); }
        }

        [Test]
        public void ServiceWithNoMission_AnswersVisible()
        {
            var service = new GameObject("S").AddComponent<IntelligenceService>();
            try { Assert.That(service.VisibilityOfPoint(new Vector3(100f, 0f, 100f)), Is.EqualTo(PointVisibility.Visible)); }
            finally { Object.DestroyImmediate(service.gameObject); }
        }
    }
}
#endif
```
Region-level answers (Hidden/Visible/Outside) and `IsObserved` ignoring the truth view are covered with the real fog rig in Task 15.

- [ ] **Step 2: Run to verify failure** — `Tools/run-tests.sh EditMode "Blackglass.Tests.ImpactSurfaceTests"` → `EXIT=1`.

- [ ] **Step 3: Implement `ImpactSurface.cs`**
```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>Marks a collider (or everything below it) as a kind of surface for impact effects. Presentation data only; nothing in gameplay reads it.</summary>
    [DisallowMultipleComponent]
    public sealed class ImpactSurface : MonoBehaviour
    {
        [SerializeField] ImpactSurfaceKind kind = ImpactSurfaceKind.Default;

        public ImpactSurfaceKind Kind => kind;

        public void Set(ImpactSurfaceKind surface) => kind = surface;
    }

    public static class ImpactSurfaces
    {
        /// <summary>The surface of a hit collider: its nearest ImpactSurface, else Character when it belongs to a unit (Health), else Default.</summary>
        public static ImpactSurfaceKind Classify(Collider collider)
        {
            if (collider == null)
                return ImpactSurfaceKind.Default;
            var surface = collider.GetComponentInParent<ImpactSurface>();
            if (surface != null)
                return surface.Kind;
            return collider.GetComponentInParent<Health>() != null ? ImpactSurfaceKind.Character : ImpactSurfaceKind.Default;
        }

        /// <summary>Generated structure is concrete; low walls and crates are metal.</summary>
        public static ImpactSurfaceKind ForBox(MissionBoxKind kind)
        {
            switch (kind)
            {
                case MissionBoxKind.Wall:
                case MissionBoxKind.Baffle:
                case MissionBoxKind.Pillar:
                    return ImpactSurfaceKind.Concrete;
                case MissionBoxKind.LowWall:
                case MissionBoxKind.Crate:
                    return ImpactSurfaceKind.Metal;
                default:
                    return ImpactSurfaceKind.Default;
            }
        }
    }
}
```

- [ ] **Step 4: Modify `UnitWeaponVisual`** — add `public const string MuzzleName = "Muzzle";`, then next to `Current`:
```csharp
        /// <summary>The weapon asset currently shown (null when none).</summary>
        public WeaponVisualAsset Shown => shown;

        /// <summary>The Muzzle child of the shown model (looked up once when it is placed), or null when there is none.</summary>
        public Transform Muzzle { get; private set; }
```
In `Place`, set `Muzzle = null;` right after `current = null;` inside the removal block **and** immediately before `shown = asset;` (so every path clears it); after the `EnvironmentVisualBuilder.StripColliders(current);` line add `Muzzle = FindMuzzle(current.transform);`. Add the helper at the end of the class:
```csharp
        static Transform FindMuzzle(Transform root)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == MuzzleName)
                    return t;
            }
            return null;
        }
```

- [ ] **Step 5: Modify `MissionBuilder.Cube`** — add a trailing parameter `ImpactSurfaceKind surface` and, immediately after the material/visibility lines and **before** `if (!obstacle) return;`, add `cube.AddComponent<ImpactSurface>().Set(surface);`. Update the two call sites: floors pass `ImpactSurfaceKind.Concrete`; boxes pass `ImpactSurfaces.ForBox(box.Kind)`.

- [ ] **Step 6: Modify `MissionContent`** — after `var box = crate.AddComponent<BoxCollider>();` (loot crate, line ~64) add `crate.AddComponent<ImpactSurface>().Set(ImpactSurfaceKind.Metal);`; after `var box = terminal.AddComponent<BoxCollider>();` (terminal, line ~94) add `terminal.AddComponent<ImpactSurface>().Set(ImpactSurfaceKind.Metal);`.

- [ ] **Step 7: Modify `IntelligenceService` and `Knowledge`**

`IntelligenceService` (next to `IsCoverShown`):
```csharp
        /// <summary>
        /// What the player's knowledge says about a world point, region-level: Visible when its region is observed (or the fog is
        /// off / no mission), Hidden when it is in a region that is not, Outside when it is in no region. Ignores the truth view.
        /// </summary>
        public PointVisibility VisibilityOfPoint(Vector3 world)
        {
            if (model == null || model.IsOpen)
                return PointVisibility.Visible;
            if (map.RegionAt(world) < 0)
                return PointVisibility.Outside;
            return model.StateOfPoint(world) == KnowledgeState.Observed ? PointVisibility.Visible : PointVisibility.Hidden;
        }
```
`Knowledge`:
```csharp
        /// <summary>The point's visibility for combat presentation; Visible without a service.</summary>
        public static PointVisibility PointVisibilityOf(IntelligenceService intelligence, Vector3 world) =>
            intelligence == null ? PointVisibility.Visible : intelligence.VisibilityOfPoint(world);

        /// <summary>Whether the unit is observed right now (friendlies always). Unlike IsShown it ignores the developer truth view.</summary>
        public static bool IsObserved(IntelligenceService intelligence, Health unit) =>
            intelligence == null || intelligence.CanTarget(unit);
```
(`Knowledge.cs` needs `using UnityEngine;` at the top for `Vector3`.)

- [ ] **Step 8: Run**
```bash
Tools/run-tests.sh EditMode "Blackglass.Tests.ImpactSurfaceTests"
Tools/run-tests.sh PlayMode "Blackglass.Tests.MuzzleBindingPlayModeTests"
Tools/run-tests.sh PlayMode "Blackglass.Tests.ImpactSurfaceMissionPlayModeTests"
Tools/run-tests.sh PlayMode "Blackglass.Tests.PointVisibilityPlayModeTests"
Tools/run-tests.sh PlayMode "Blackglass.Tests.WeaponVisualPlayModeTests"
Tools/run-tests.sh PlayMode "Blackglass.Tests.HostileWeaponFogPlayModeTests"
```
Expected: all `EXIT=0` (the last two prove `UnitWeaponVisual` still behaves).

---

### Task 5: Pooled effect runtime (`PooledEffect`, `TracerView`, `EffectPool`)

**Files:**
- Create: `Scripts/Presentation/PooledEffect.cs`, `TracerView.cs`, `EffectPool.cs`
- Test: `Tests/PlayMode/EffectPoolPlayModeTests.cs`

**Interfaces:**
- Consumes: `EffectCategory` (Task 1), `TracerStyle` (Task 3), `SimulationTime`, `TacticalPause`.
- Produces:
  - `PooledEffect : MonoBehaviour` — `GameObject Prefab; EffectCategory Category; float EndTime; bool Expired; bool IsActive; bool HoldsLight; bool HasGatePoint; Vector3 GatePoint; bool IsVisible; TracerView Tracer; ParticleSystem[] Particles; Light[] Lights; internal static PooledEffect Create(GameObject prefab, Transform container); internal void Begin(EffectCategory, Vector3 position, Quaternion rotation, float scale, float lifetime, bool allowLights); internal void SetGate(Vector3 point); void SetVisible(bool show); internal void ResetForPool(Transform container)`
  - `TracerView : MonoBehaviour` (requires `LineRenderer`) — `float Launch(Vector3 from, Vector3 to, TracerStyle style)` (returns the lifetime to use); `bool IsRunning`; `Vector3 HeadPoint`, `Vector3 TailPoint`; `void ResetState()`
  - `internal sealed class EffectPool` — `EffectPool(Transform container)`; `PooledEffect Rent(GameObject prefab)`; `void Track(PooledEffect)`; `void Return(PooledEffect)`; `IReadOnlyList<PooledEffect> Active`; `int ActiveCount`; `int IdleCount`

- [ ] **Step 1: Write the failing tests** — `Tests/PlayMode/EffectPoolPlayModeTests.cs`:
```csharp
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class EffectPoolPlayModeTests
    {
        TestWorld world;
        TacticalPause pause;
        Transform container;
        EffectPool pool;
        GameObject prefab;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            container = world.Track(new GameObject("Container")).transform;
            pool = new EffectPool(container);
            prefab = world.Track(new GameObject("FxPrefab"));
            prefab.SetActive(false);
            var ps = prefab.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.playOnAwake = false;
            main.loop = true;
            main.startLifetime = 5f;
            var emission = ps.emission;
            emission.rateOverTime = 50f;
            var lightObject = new GameObject("Light");
            lightObject.transform.SetParent(prefab.transform, false);
            var light = lightObject.AddComponent<Light>();
            light.intensity = 2f;
            light.enabled = false;
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        PooledEffect Spawn(float lifetime = 5f, bool lights = true)
        {
            var effect = pool.Rent(prefab);
            effect.Begin(EffectCategory.Muzzle, new Vector3(1f, 2f, 3f), Quaternion.identity, 2f, lifetime, lights);
            effect.HoldsLight = lights;
            pool.Track(effect);
            return effect;
        }

        [UnityTest]
        public IEnumerator Begin_PlacesScalesAndStartsTheEffect_AndReturnResetsEverything()
        {
            var effect = Spawn();
            yield return null;
            Assert.That(effect.IsActive, Is.True);
            Assert.That(effect.transform.position, Is.EqualTo(new Vector3(1f, 2f, 3f)));
            Assert.That(effect.transform.localScale, Is.EqualTo(Vector3.one * 2f));
            Assert.That(effect.Particles[0].isPlaying, Is.True);
            Assert.That(effect.Lights[0].enabled, Is.True);

            effect.SetGate(Vector3.one);
            effect.SetVisible(false);
            pool.Return(effect);

            Assert.That(effect.IsActive, Is.False);
            Assert.That(effect.gameObject.activeSelf, Is.False);
            Assert.That(effect.Particles[0].particleCount, Is.EqualTo(0));
            Assert.That(effect.Lights[0].enabled, Is.False);
            Assert.That(effect.Lights[0].intensity, Is.EqualTo(2f), "the light's authored intensity is restored");
            Assert.That(effect.transform.localScale, Is.EqualTo(Vector3.one));
            Assert.That(effect.transform.parent, Is.EqualTo(container));
            Assert.That(effect.IsVisible, Is.True, "the gate state is reset too");
            Assert.That(effect.HasGatePoint, Is.False);
            Assert.That(effect.HoldsLight, Is.False);
            Assert.That(pool.ActiveCount, Is.EqualTo(0));
            Assert.That(pool.IdleCount, Is.EqualTo(1));
            Assert.That(pool.Rent(prefab), Is.SameAs(effect), "the instance is reused");
        }

        [UnityTest]
        public IEnumerator WithoutLights_TheLightStaysOff()
        {
            var effect = Spawn(lights: false);
            yield return null;
            Assert.That(effect.Lights[0].enabled, Is.False);
        }

        [UnityTest]
        public IEnumerator Lifetime_IsScaledTime_ItDoesNotRunWhilePaused_AndResumesAfterwards()
        {
            var effect = Spawn(lifetime: 0.4f);
            yield return new WaitForSeconds(0.15f);
            Assert.That(effect.Expired, Is.False);

            pause.Pause();
            yield return new WaitForSecondsRealtime(0.8f);
            Assert.That(effect.Expired, Is.False, "wall-clock time must not age an effect during a pause");

            pause.Resume();
            yield return new WaitForSeconds(0.4f);
            Assert.That(effect.Expired, Is.True);
        }

        [UnityTest]
        public IEnumerator Particles_FreezeDuringPause_AndContinueFromWhereTheyWere_NotRestart()
        {
            var effect = Spawn();
            yield return new WaitForSeconds(0.3f);
            var ps = effect.Particles[0];
            var time = ps.time;
            var count = ps.particleCount;
            Assert.That(count, Is.GreaterThan(0), "Precondition: particles were emitted");

            pause.Pause();
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.That(ps.time, Is.EqualTo(time).Within(0.02f));
            Assert.That(ps.particleCount, Is.EqualTo(count));
            Assert.That(effect.IsActive, Is.True, "a paused effect is not released");

            pause.Resume();
            yield return new WaitForSeconds(0.2f);
            Assert.That(ps.time, Is.GreaterThan(time), "it continued rather than restarted");
            Assert.That(ps.isPlaying, Is.True);
        }

        [UnityTest]
        public IEnumerator LightEnvelope_FadesOnScaledTime_AndFreezesWhilePaused()
        {
            var effect = Spawn(lifetime: 3f);   // the default light envelope is 0.1 s
            yield return null;
            pause.Pause();
            yield return null;
            var intensity = effect.Lights[0].intensity;
            yield return new WaitForSecondsRealtime(0.4f);
            Assert.That(effect.Lights[0].intensity, Is.EqualTo(intensity));
            pause.Resume();
            yield return new WaitForSeconds(0.3f);
            Assert.That(effect.Lights[0].enabled, Is.False, "the light's short envelope ended");
        }

        [UnityTest]
        public IEnumerator Tracer_MovesOnlyWhileTheGameRuns_AndEnds()
        {
            var tracerPrefab = world.Track(new GameObject("TracerPrefab"));
            tracerPrefab.SetActive(false);
            var line = tracerPrefab.AddComponent<LineRenderer>();
            line.positionCount = 2;
            tracerPrefab.AddComponent<TracerView>();
            var style = TracerStyle.Create(tracerPrefab, 0.04f, 2f, 20f);

            var effect = pool.Rent(tracerPrefab);
            effect.Begin(EffectCategory.Tracer, Vector3.zero, Quaternion.identity, 1f, 10f, false);
            var lifetime = effect.Tracer.Launch(Vector3.zero, new Vector3(0f, 0f, 20f), style);
            pool.Track(effect);
            Assert.That(lifetime, Is.EqualTo((20f + 2f) / 20f + 0.05f).Within(0.01f));

            yield return new WaitForSeconds(0.2f);
            var head = effect.Tracer.HeadPoint.z;
            Assert.That(head, Is.GreaterThan(0.5f));

            pause.Pause();
            yield return new WaitForSecondsRealtime(0.4f);
            Assert.That(effect.Tracer.HeadPoint.z, Is.EqualTo(head).Within(0.001f));

            pause.Resume();
            yield return new WaitForSeconds(lifetime + 0.2f);
            Assert.That(effect.Tracer.IsRunning, Is.False);
        }

        [UnityTest]
        public IEnumerator Tracer_ZeroLengthOrNaN_IsOmittedWithoutThrowing()
        {
            var tracerPrefab = world.Track(new GameObject("TracerPrefab"));
            tracerPrefab.SetActive(false);
            tracerPrefab.AddComponent<LineRenderer>();
            tracerPrefab.AddComponent<TracerView>();
            var style = TracerStyle.Create(tracerPrefab);
            var effect = pool.Rent(tracerPrefab);
            effect.Tracer.Launch(Vector3.one, Vector3.one, style);
            Assert.That(effect.Tracer.IsRunning, Is.False);
            effect.Tracer.Launch(Vector3.zero, new Vector3(float.NaN, 0f, 1f), style);
            Assert.That(effect.Tracer.IsRunning, Is.False);
            yield return null;
        }

        [UnityTest]
        public IEnumerator ADestroyedIdleEffect_IsNeverHandedOut()
        {
            var first = Spawn();
            pool.Return(first);
            Object.DestroyImmediate(first.gameObject);
            yield return null;
            var second = pool.Rent(prefab);
            Assert.That(second, Is.Not.Null);
            Assert.That(second.gameObject, Is.Not.Null);
        }
    }
}
```

- [ ] **Step 2: Run to verify failure** — `Tools/run-tests.sh PlayMode "Blackglass.Tests.EffectPoolPlayModeTests"` → `EXIT=1`.

- [ ] **Step 3: Implement `TracerView.cs`**
```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// A hitscan streak: a short bright segment that travels from the muzzle to a fixed end point at a configured speed
    /// (or is drawn at once). Purely cosmetic: it carries no collider and nothing waits for it. Advances only while the
    /// simulation runs, so a tactical pause freezes it where it is.
    /// </summary>
    [RequireComponent(typeof(LineRenderer))]
    public sealed class TracerView : MonoBehaviour
    {
        const float ImmediateSeconds = 0.05f;

        LineRenderer line;
        Vector3 start;
        Vector3 direction;
        float total;
        float length;
        float speed;
        float head;

        LineRenderer Line => line != null ? line : line = GetComponent<LineRenderer>();

        public bool IsRunning { get; private set; }
        public Vector3 HeadPoint => start + direction * Mathf.Min(head, total);
        public Vector3 TailPoint => start + direction * Mathf.Clamp(head - length, 0f, total);

        /// <summary>
        /// Starts a streak between two captured points. Returns the lifetime the owner should give the effect. A zero-length or
        /// non-finite segment draws nothing (the detail is omitted rather than invented).
        /// </summary>
        public float Launch(Vector3 from, Vector3 to, TracerStyle style)
        {
            var delta = to - from;
            total = delta.magnitude;
            if (!(total > 0.01f) || float.IsInfinity(total))
            {
                ResetState();
                return 0.01f;
            }
            start = from;
            direction = delta / total;
            length = Mathf.Max(0.1f, style.Length);
            speed = style.Speed > 0f ? style.Speed : total / ImmediateSeconds;
            head = Mathf.Min(0.5f, total);
            var l = Line;
            l.useWorldSpace = true;
            l.positionCount = 2;
            l.widthMultiplier = style.Width;
            l.startColor = style.StartColor;
            l.endColor = style.EndColor;
            IsRunning = true;
            Apply();
            return (total + length) / speed + 0.05f;
        }

        void Update()
        {
            if (!IsRunning || !SimulationTime.IsRunning)
                return;
            head += speed * Time.deltaTime;
            Apply();
        }

        void Apply()
        {
            var tail = head - length;
            if (tail >= total)
            {
                Line.enabled = false;
                IsRunning = false;
                return;
            }
            var a = Mathf.Max(0f, tail);
            var b = Mathf.Min(head, total);
            Line.enabled = b > a;
            Line.SetPosition(0, start + direction * a);
            Line.SetPosition(1, start + direction * b);
        }

        public void ResetState()
        {
            IsRunning = false;
            head = 0f;
            total = 0f;
            Line.enabled = false;
        }
    }
}
```
(`NaN` in `total` fails `total > 0.01f`, so the guard covers it.)

- [ ] **Step 4: Implement `PooledEffect.cs`**
```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// One pooled presentation instance. Caches its particle systems, renderers and lights once, plays them from the start on
    /// Begin, ends on a scaled-time deadline (so a pause neither ages nor releases it) and resets completely on return to the
    /// pool: particles and trails cleared, lights and renderers restored, scale and parent restored, gate state forgotten.
    /// A short light envelope (lightSeconds, scaled time) fades any lights it holds.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PooledEffect : MonoBehaviour
    {
        [SerializeField, Min(0f)] float lightSeconds = 0.1f;

        ParticleSystem[] particles = new ParticleSystem[0];
        Renderer[] renderers = new Renderer[0];
        Light[] lights = new Light[0];
        float[] lightIntensity = new float[0];
        TracerView tracer;
        float lightAge;
        bool visible = true;

        public GameObject Prefab { get; internal set; }
        public EffectCategory Category { get; private set; }
        public float EndTime { get; private set; }
        public bool IsActive { get; private set; }
        public bool Expired => IsActive && Time.time >= EndTime;
        /// <summary>True while this instance counts against the light budget.</summary>
        public bool HoldsLight { get; internal set; }
        public bool HasGatePoint { get; private set; }
        public Vector3 GatePoint { get; private set; }
        public bool IsVisible => visible;
        public TracerView Tracer => tracer;
        public ParticleSystem[] Particles => particles;
        public Light[] Lights => lights;

        internal static PooledEffect Create(GameObject prefab, Transform container)
        {
            var instance = Instantiate(prefab, container);
            instance.name = prefab.name;
            if (!instance.TryGetComponent(out PooledEffect effect))
                effect = instance.AddComponent<PooledEffect>();
            effect.Prefab = prefab;
            effect.Build();
            instance.SetActive(false);
            return effect;
        }

        void Build()
        {
            particles = GetComponentsInChildren<ParticleSystem>(true);
            renderers = GetComponentsInChildren<Renderer>(true);
            lights = GetComponentsInChildren<Light>(true);
            lightIntensity = new float[lights.Length];
            for (var i = 0; i < lights.Length; i++)
            {
                lightIntensity[i] = lights[i].intensity;
                lights[i].enabled = false;
            }
            TryGetComponent(out tracer);
        }

        internal void Begin(EffectCategory category, Vector3 position, Quaternion rotation, float scale, float lifetime, bool allowLights)
        {
            Category = category;
            transform.SetPositionAndRotation(position, rotation);
            transform.localScale = Vector3.one * scale;
            gameObject.SetActive(true);
            IsActive = true;
            EndTime = Time.time + lifetime;
            lightAge = 0f;
            visible = true;
            HasGatePoint = false;
            foreach (var r in renderers)
                r.forceRenderingOff = false;
            for (var i = 0; i < lights.Length; i++)
            {
                lights[i].intensity = lightIntensity[i];
                lights[i].enabled = allowLights;
            }
            foreach (var ps in particles)
            {
                ps.Clear(false);
                ps.Play(false);
            }
        }

        /// <summary>Marks this effect as long-lived at a world point, so it can be hidden and shown again as the player's knowledge changes.</summary>
        internal void SetGate(Vector3 point)
        {
            HasGatePoint = true;
            GatePoint = point;
        }

        /// <summary>Hides or shows the effect without stopping or restarting it.</summary>
        public void SetVisible(bool show)
        {
            if (show == visible)
                return;
            visible = show;
            foreach (var r in renderers)
                r.forceRenderingOff = !show;
            for (var i = 0; i < lights.Length; i++)
                lights[i].enabled = show && HoldsLight && lightAge < lightSeconds;
        }

        void Update()
        {
            if (!IsActive || lights.Length == 0 || !SimulationTime.IsRunning)
                return;
            lightAge += Time.deltaTime;
            var left = lightSeconds <= 0f ? 0f : Mathf.Clamp01(1f - lightAge / lightSeconds);
            for (var i = 0; i < lights.Length; i++)
            {
                if (!lights[i].enabled)
                    continue;
                if (left <= 0f)
                    lights[i].enabled = false;
                else
                    lights[i].intensity = lightIntensity[i] * left * left;
            }
        }

        internal void ResetForPool(Transform container)
        {
            foreach (var ps in particles)
            {
                ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                ps.Clear(false);
            }
            if (tracer != null)
                tracer.ResetState();
            for (var i = 0; i < lights.Length; i++)
            {
                lights[i].enabled = false;
                lights[i].intensity = lightIntensity[i];
            }
            foreach (var r in renderers)
                r.forceRenderingOff = false;
            transform.SetParent(container, false);
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;
            transform.localScale = Vector3.one;
            gameObject.SetActive(false);
            IsActive = false;
            HasGatePoint = false;
            HoldsLight = false;
            visible = true;
            EndTime = 0f;
            lightAge = 0f;
        }
    }
}
```

- [ ] **Step 5: Implement `EffectPool.cs`**
```csharp
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Reuses presentation instances: one idle stack per source prefab and an explicit list of the active ones, so active
    /// budgets can be enforced by the caller (an idle-pool size says nothing about what is playing). Instances live under a
    /// container owned by the presenter, never under a mission.
    /// </summary>
    internal sealed class EffectPool
    {
        readonly Transform container;
        readonly Dictionary<GameObject, Stack<PooledEffect>> idle = new Dictionary<GameObject, Stack<PooledEffect>>();
        readonly List<PooledEffect> active = new List<PooledEffect>();

        public EffectPool(Transform container) => this.container = container;

        public IReadOnlyList<PooledEffect> Active => active;
        public int ActiveCount => active.Count;

        public int IdleCount
        {
            get
            {
                var n = 0;
                foreach (var stack in idle.Values)
                    n += stack.Count;
                return n;
            }
        }

        /// <summary>An idle instance of the prefab (never a destroyed one), or a new one. The caller calls Begin and Track.</summary>
        public PooledEffect Rent(GameObject prefab)
        {
            if (idle.TryGetValue(prefab, out var stack))
            {
                while (stack.Count > 0)
                {
                    var candidate = stack.Pop();
                    if (candidate != null)
                        return candidate;
                }
            }
            return PooledEffect.Create(prefab, container);
        }

        public void Track(PooledEffect effect) => active.Add(effect);

        public void Return(PooledEffect effect)
        {
            if (effect == null)
                return;
            var index = active.IndexOf(effect);
            if (index >= 0)
            {
                active[index] = active[active.Count - 1];
                active.RemoveAt(active.Count - 1);
            }
            effect.ResetForPool(container);
            if (!idle.TryGetValue(effect.Prefab, out var stack))
                idle[effect.Prefab] = stack = new Stack<PooledEffect>();
            stack.Push(effect);
        }
    }
}
```

- [ ] **Step 6: Run** — `Tools/run-tests.sh PlayMode "Blackglass.Tests.EffectPoolPlayModeTests"`. Expected: `EXIT=0`. If `Particles_FreezeDuringPause…` shows `ps.time` moving while paused, confirm the system's `main.useUnscaledTime` is false (the default) before touching any code.

---

### Task 6: Audio runtime (`CueThrottle`, `VoicePicker`, `CombatAudio`)

**Files:**
- Create: `Scripts/Presentation/CueThrottle.cs`, `VoicePicker.cs`, `CombatAudio.cs`
- Test: `Tests/EditMode/CombatAudioLogicTests.cs`, `Tests/PlayMode/CombatAudioPlayModeTests.cs`

**Interfaces:**
- Consumes: `AudioCue`, `CosmeticRandom` (Tasks 1, 3).
- Produces:
  - `sealed class CueThrottle { const float CrowdWindow = 0.15f; bool TryPlay(int key, float now, float minInterval, out float gain); void Clear() }`
  - `static class VoicePicker { static int Pick(IReadOnlyList<bool> busy, IReadOnlyList<int> priority, IReadOnlyList<long> order, int newPriority) }` (a free voice first; else the least important busy voice whose priority number is >= the new one, oldest first; else -1)
  - `CombatAudio : MonoBehaviour` — `void Initialize(int voices = 10)`; `bool Play(AudioCue cue, Vector3 position, float masterVolume, CosmeticRandom random)`; `void SetPaused(bool paused)`; `bool IsPaused`; `int ActiveVoices`; `int VoiceCount`; `void StopAll()`; `IReadOnlyList<AudioSource> Sources`

- [ ] **Step 1: Write the failing tests**

`Tests/EditMode/CombatAudioLogicTests.cs`:
```csharp
using NUnit.Framework;

namespace Blackglass.Tests
{
    public class CombatAudioLogicTests
    {
        [Test]
        public void Throttle_RefusesPlaysCloserThanTheInterval()
        {
            var throttle = new CueThrottle();
            Assert.That(throttle.TryPlay(1, 0f, 0.06f, out _), Is.True);
            Assert.That(throttle.TryPlay(1, 0.03f, 0.06f, out _), Is.False);
            Assert.That(throttle.TryPlay(1, 0.07f, 0.06f, out _), Is.True);
            Assert.That(throttle.TryPlay(2, 0.03f, 0.06f, out _), Is.True, "another cue has its own interval");
        }

        [Test]
        public void Throttle_CrowdedPlaysOfTheSameCue_GetQuieter()
        {
            var throttle = new CueThrottle();
            throttle.TryPlay(1, 0.00f, 0f, out var first);
            throttle.TryPlay(1, 0.02f, 0f, out var second);
            throttle.TryPlay(1, 0.04f, 0f, out var third);
            Assert.That(first, Is.EqualTo(1f));
            Assert.That(second, Is.LessThan(first));
            Assert.That(third, Is.LessThan(second));
            throttle.TryPlay(1, 1.0f, 0f, out var later);
            Assert.That(later, Is.EqualTo(1f), "the crowd window passed");
        }

        [Test]
        public void Throttle_Clear_ForgetsEverything()
        {
            var throttle = new CueThrottle();
            throttle.TryPlay(1, 0f, 5f, out _);
            throttle.Clear();
            Assert.That(throttle.TryPlay(1, 0.01f, 5f, out _), Is.True);
        }

        [Test]
        public void Picker_PrefersAFreeVoice()
        {
            Assert.That(VoicePicker.Pick(new[] { true, false, true }, new[] { 10, 10, 10 }, new long[] { 1, 2, 3 }, 128), Is.EqualTo(1));
        }

        [Test]
        public void Picker_StealsTheLeastImportantThenOldest_ButNeverAMoreImportantVoice()
        {
            var busy = new[] { true, true, true };
            Assert.That(VoicePicker.Pick(busy, new[] { 50, 200, 200 }, new long[] { 5, 9, 3 }, 128), Is.EqualTo(2), "priority 200 is less important; the older of the two is voice 2");
            Assert.That(VoicePicker.Pick(busy, new[] { 130, 160, 190 }, new long[] { 1, 2, 3 }, 128), Is.EqualTo(2), "the least important one");
            Assert.That(VoicePicker.Pick(busy, new[] { 10, 20, 30 }, new long[] { 1, 2, 3 }, 5), Is.EqualTo(2), "a more important new sound steals the least important voice");
        }

        [Test]
        public void Picker_DropsTheNewSound_WhenEveryVoiceIsMoreImportant()
        {
            Assert.That(VoicePicker.Pick(new[] { true, true }, new[] { 10, 20 }, new long[] { 1, 2 }, 200), Is.EqualTo(-1));
        }
    }
}
```
(Priority numbers: smaller = more important. A new sound with priority 200 may steal a voice whose number is >= 200 only; with every voice at 10/20 it is dropped.)

`Tests/PlayMode/CombatAudioPlayModeTests.cs`:
```csharp
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CombatAudioPlayModeTests
    {
        TestWorld world;
        CombatAudio audio;
        CosmeticRandom random;
        readonly List<AudioClip> clips = new List<AudioClip>();

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            audio = world.Track(new GameObject("Audio")).AddComponent<CombatAudio>();
            audio.Initialize(10);
            random = new CosmeticRandom(3);
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            foreach (var clip in clips)
                Object.DestroyImmediate(clip);
            clips.Clear();
        }

        AudioCue Cue(float seconds = 0.5f, int priority = 128, float minInterval = 0f)
        {
            var clip = AudioClip.Create("clip", Mathf.Max(1, (int)(44100 * seconds)), 1, 44100, false);
            clips.Add(clip);
            return AudioCue.Create(1f, priority, minInterval, clip);
        }

        [UnityTest]
        public IEnumerator Play_UsesASpatialVoice_AtTheGivenPosition_WithTheCuesFalloff()
        {
            var cue = Cue();
            Assert.That(audio.Play(cue, new Vector3(3f, 0f, 4f), 0.5f, random), Is.True);
            yield return null;
            Assert.That(audio.ActiveVoices, Is.EqualTo(1));
            var source = audio.Sources[0];
            Assert.That(source.transform.position, Is.EqualTo(new Vector3(3f, 0f, 4f)));
            Assert.That(source.spatialBlend, Is.EqualTo(1f));
            Assert.That(source.volume, Is.EqualTo(0.5f).Within(0.01f));
            Assert.That(source.maxDistance, Is.EqualTo(cue.MaxDistance));
        }

        [UnityTest]
        public IEnumerator EmptyOrNullCue_IsANoOp()
        {
            Assert.That(audio.Play(null, Vector3.zero, 1f, random), Is.False);
            Assert.That(audio.Play(new AudioCue(), Vector3.zero, 1f, random), Is.False);
            Assert.That(audio.ActiveVoices, Is.EqualTo(0));
            yield return null;
        }

        [UnityTest]
        public IEnumerator VoiceCap_IsEnforced()
        {
            for (var i = 0; i < 25; i++)
                audio.Play(Cue(2f), Vector3.zero, 1f, random);
            yield return null;
            Assert.That(audio.ActiveVoices, Is.LessThanOrEqualTo(10));
            Assert.That(audio.VoiceCount, Is.EqualTo(10));
        }

        [UnityTest]
        public IEnumerator SameCuePlayedTogether_DoesNotStackGain()
        {
            var cue = Cue(1f);
            for (var i = 0; i < 4; i++)
                audio.Play(cue, Vector3.zero, 1f, random);
            yield return null;
            var total = 0f;
            foreach (var source in audio.Sources)
                if (source.clip != null)
                    total += source.volume;
            Assert.That(total, Is.LessThan(3f), "four simultaneous plays of one cue are quieter than four full-volume sounds");
        }

        [UnityTest]
        public IEnumerator Pause_HoldsTheVoices_NeverReleasesThem_AndResumeFinishesThem()
        {
            audio.Play(Cue(0.5f), Vector3.zero, 1f, random);
            yield return null;
            audio.SetPaused(true);
            yield return new WaitForSecondsRealtime(0.8f);
            Assert.That(audio.IsPaused, Is.True);
            Assert.That(audio.ActiveVoices, Is.EqualTo(1), "a paused voice is not finished");
            Assert.That(audio.Play(Cue(), Vector3.zero, 1f, random), Is.False, "nothing new starts during a pause");

            audio.SetPaused(false);
            yield return new WaitForSecondsRealtime(0.7f);
            Assert.That(audio.ActiveVoices, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator RepeatedPauseResume_DoesNotDuplicateOrLeak()
        {
            audio.Play(Cue(0.4f), Vector3.zero, 1f, random);
            for (var i = 0; i < 10; i++)
            {
                audio.SetPaused(true);
                audio.SetPaused(true);
                audio.SetPaused(false);
            }
            Assert.That(audio.ActiveVoices, Is.EqualTo(1));
            yield return new WaitForSecondsRealtime(0.6f);
            Assert.That(audio.ActiveVoices, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator StopAll_ReleasesEverything_EvenWhilePaused()
        {
            audio.Play(Cue(2f), Vector3.zero, 1f, random);
            audio.SetPaused(true);
            audio.StopAll();
            Assert.That(audio.ActiveVoices, Is.EqualTo(0));
            yield return null;
        }

        [UnityTest]
        public IEnumerator MinInterval_DropsARepeatedPlay()
        {
            var cue = Cue(0.5f, minInterval: 0.5f);
            Assert.That(audio.Play(cue, Vector3.zero, 1f, random), Is.True);
            Assert.That(audio.Play(cue, Vector3.zero, 1f, random), Is.False);
            yield return null;
        }
    }
}
```

- [ ] **Step 2: Run to verify failure** — `Tools/run-tests.sh EditMode "Blackglass.Tests.CombatAudioLogicTests"` → `EXIT=1`.

- [ ] **Step 3: Implement `CueThrottle.cs` and `VoicePicker.cs`** (one file each)
```csharp
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Limits how often one cue may start and lowers the gain of crowded plays, so several soldiers firing together do not
    /// stack volume. Pure; the caller supplies the clock.
    /// </summary>
    public sealed class CueThrottle
    {
        public const float CrowdWindow = 0.15f;

        readonly Dictionary<int, List<float>> plays = new Dictionary<int, List<float>>();

        public bool TryPlay(int key, float now, float minInterval, out float gain)
        {
            gain = 1f;
            if (!plays.TryGetValue(key, out var list))
                plays[key] = list = new List<float>();
            for (var i = list.Count - 1; i >= 0; i--)
                if (list[i] < now - CrowdWindow)
                    list.RemoveAt(i);
            if (list.Count > 0 && now - list[list.Count - 1] < minInterval)
                return false;
            gain = 1f / Mathf.Sqrt(list.Count + 1);
            list.Add(now);
            return true;
        }

        public void Clear() => plays.Clear();
    }
}
```
```csharp
using System.Collections.Generic;

namespace Blackglass
{
    /// <summary>Chooses which voice a new sound uses.</summary>
    public static class VoicePicker
    {
        /// <summary>
        /// A free voice; else the least important busy voice (largest priority number, oldest first) that is not more
        /// important than the new sound; else -1 (the new sound is dropped).
        /// </summary>
        public static int Pick(IReadOnlyList<bool> busy, IReadOnlyList<int> priority, IReadOnlyList<long> order, int newPriority)
        {
            for (var i = 0; i < busy.Count; i++)
                if (!busy[i])
                    return i;
            var best = -1;
            for (var i = 0; i < busy.Count; i++)
            {
                if (priority[i] < newPriority)
                    continue;
                if (best < 0 || priority[i] > priority[best] || (priority[i] == priority[best] && order[i] < order[best]))
                    best = i;
            }
            return best;
        }
    }
}
```
A voice whose priority number is lower than the new sound's (more important) is skipped, so a new priority-200 sound cannot steal voices at 10 or 20, while a new priority-5 sound can steal any.

- [ ] **Step 4: Implement `CombatAudio.cs`**
```csharp
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// A small pool of spatial AudioSources for combat sounds, played through the scene's existing AudioListener. It limits
    /// simultaneous voices, repeated plays and stacked gain, and pauses and resumes its voices with the tactical pause.
    /// Whether a voice is still in use comes from its own audio clock (which does not advance while paused), never from
    /// AudioSource.isPlaying, so a paused voice is never mistaken for a finished one. Sounds are only started by callers that
    /// already passed the knowledge gate.
    /// </summary>
    public sealed class CombatAudio : MonoBehaviour
    {
        public const int DefaultVoices = 10;

        sealed class Voice
        {
            public AudioSource Source;
            public bool InUse;
            public float EndClock;
            public int Priority;
            public long Order;
        }

        readonly CueThrottle throttle = new CueThrottle();
        readonly List<AudioSource> sources = new List<AudioSource>();
        Voice[] voices = new Voice[0];
        bool[] busyBuffer = new bool[0];
        int[] priorityBuffer = new int[0];
        long[] orderBuffer = new long[0];
        float clock;
        long order;

        public bool IsPaused { get; private set; }
        public int VoiceCount => voices.Length;
        public IReadOnlyList<AudioSource> Sources => sources;

        public int ActiveVoices
        {
            get
            {
                var n = 0;
                foreach (var voice in voices)
                    if (IsBusy(voice))
                        n++;
                return n;
            }
        }

        public void Initialize(int voiceCount = DefaultVoices)
        {
            if (voices.Length > 0)
                return;
            voiceCount = Mathf.Max(1, voiceCount);
            voices = new Voice[voiceCount];
            busyBuffer = new bool[voiceCount];
            priorityBuffer = new int[voiceCount];
            orderBuffer = new long[voiceCount];
            for (var i = 0; i < voiceCount; i++)
            {
                var child = new GameObject("Voice_" + i);
                child.transform.SetParent(transform, false);
                var source = child.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.loop = false;
                source.spatialBlend = 1f;
                source.rolloffMode = AudioRolloffMode.Logarithmic;
                source.dopplerLevel = 0f;
                voices[i] = new Voice { Source = source };
                sources.Add(source);
            }
        }

        // The audio clock is real time that stops while the combat sounds are paused. It is audio bookkeeping, not
        // simulation, and the only unscaled-time read in the presentation layer (allow-listed in the source audit).
        void Update()
        {
            if (!IsPaused)
                clock += Time.unscaledDeltaTime;
        }

        bool IsBusy(Voice voice) => voice.InUse && clock < voice.EndClock;

        public bool Play(AudioCue cue, Vector3 position, float masterVolume, CosmeticRandom random)
        {
            if (IsPaused || cue == null || voices.Length == 0)
                return false;
            var clip = cue.Pick(random);
            if (clip == null)
                return false;
            if (!throttle.TryPlay(RuntimeHelpers.GetHashCode(cue), clock, cue.MinInterval, out var gain))
                return false;
            for (var i = 0; i < voices.Length; i++)
            {
                busyBuffer[i] = IsBusy(voices[i]);
                priorityBuffer[i] = voices[i].Priority;
                orderBuffer[i] = voices[i].Order;
            }
            var index = VoicePicker.Pick(busyBuffer, priorityBuffer, orderBuffer, cue.Priority);
            if (index < 0)
                return false;

            var voice = voices[index];
            var source = voice.Source;
            source.Stop();
            source.transform.position = position;
            source.clip = clip;
            source.volume = Mathf.Clamp01(cue.Volume * masterVolume * gain);
            source.pitch = random.Range(cue.PitchMin, cue.PitchMax);
            source.minDistance = cue.MinDistance;
            source.maxDistance = cue.MaxDistance;
            source.priority = cue.Priority;
            source.Play();
            voice.InUse = true;
            voice.EndClock = clock + clip.length / Mathf.Max(0.1f, source.pitch);
            voice.Priority = cue.Priority;
            voice.Order = ++order;
            return true;
        }

        /// <summary>Pauses or resumes every voice that is still in use. Idempotent.</summary>
        public void SetPaused(bool paused)
        {
            if (paused == IsPaused)
                return;
            IsPaused = paused;
            foreach (var voice in voices)
            {
                if (!IsBusy(voice))
                    continue;
                if (paused)
                    voice.Source.Pause();
                else
                    voice.Source.UnPause();
            }
        }

        /// <summary>Stops everything and forgets all throttling (mission change); safe while paused.</summary>
        public void StopAll()
        {
            foreach (var voice in voices)
            {
                voice.Source.Stop();
                voice.InUse = false;
                voice.EndClock = 0f;
            }
            throttle.Clear();
        }
    }
}
```

- [ ] **Step 5: Run**
```bash
Tools/run-tests.sh EditMode "Blackglass.Tests.CombatAudioLogicTests"
Tools/run-tests.sh PlayMode "Blackglass.Tests.CombatAudioPlayModeTests"
```
Expected: `EXIT=0`. The `source.volume` assertions are bookkeeping on our component, not audio hardware, so they hold with no sound device.

---

### Task 7: Cosmetic shot geometry, `CombatPresenter` and `UnitPresentation` (the first complete rifle shot, minus art)

**Files:**
- Create: `Scripts/Presentation/ShotGeometry.cs`, `CombatPresenter.cs`, `UnitPresentation.cs`, `CameraImpulse.cs`
- Create (test support): `Tests/PlayMode/TestSupport/PresentationRig.cs`
- Test: `Tests/PlayMode/ShotGeometryPlayModeTests.cs`, `Tests/PlayMode/CombatPresenterPlayModeTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–6.
- Produces:
  - `internal readonly struct ShotEndpoint { bool HasTracer; Vector3 End; bool HasImpact; Vector3 ImpactPoint; Vector3 ImpactNormal; ImpactSurfaceKind Surface; ctor(bool hasTracer, Vector3 end, bool hasImpact, Vector3 impactPoint, Vector3 impactNormal, ImpactSurfaceKind surface) }`
  - `internal static class ShotGeometry { const float MissCap = 30f; static ShotEndpoint Resolve(in ShotRecord record, Vector3 muzzle, CosmeticRandom random) }`
  - `CameraImpulse : MonoBehaviour { void Add(float strength); Vector3 LocalOffset; float DistanceFactor(Vector3 world); void Clear() }`
  - `CombatPresenter : MonoBehaviour` — `PresentationSettings Settings; bool Enabled { get; set; }` (turning it off calls `Clear`); `bool IsActive; CombatPresentationProfile Fallback; CombatPresentationProfile SupportProfile; EffectBudget Budget; CombatAudio Audio; int ActiveEffects; int ActiveOf(EffectCategory); internal void Bind(IntelligenceService intelligence, TacticalPause pause, CameraImpulse impulse, CombatPresentationProfile fallback, CombatPresentationProfile support); internal void UseKnowledge(Func<Vector3, PointVisibility> points, Func<Health, bool> units); internal void ClearKnowledgeOverride(); bool Observes(Health unit); PointVisibility QueryPoint(Vector3 point); internal void Regate(); internal void ShowShot(in ShotRecord record, CombatPresentationProfile profile, Transform muzzle); internal void ShowArea(in AreaRecord record, CombatPresentationProfile profile); internal void ShowPulse(Health unit, Vector3 point); internal void ShowDeath(Health unit, Vector3 point); internal void ShowCharacterBurst(Health victim, Vector3 point, Vector3 normal); internal bool ConsumeCharacterImpact(Health victim); internal void WarnOnce(UnityEngine.Object key, string message); public void Clear(); public void SetQuality(PresentationQuality quality)`
  - `UnitPresentation : MonoBehaviour` — `void Bind(CombatPresenter presenter)`; `CombatPresenter Presenter`; `internal void Deliver(in ShotRecord record)`; `internal void Deliver(in AreaRecord record)` (both dedupe through a `SequenceFilter`)

**Amendments to Task 5 files that this task needs** (make them first; the Task 5 tests must still pass):
- `PooledEffect`: add `internal void SetLifetime(float seconds) => EndTime = Time.time + seconds;`
- `EffectPool`: add
```csharp
        /// <summary>Releases (through the callback) every active effect whose scaled-time deadline has passed, and forgets destroyed ones.</summary>
        public void Sweep(System.Action<PooledEffect> release)
        {
            for (var i = active.Count - 1; i >= 0; i--)
            {
                var effect = active[i];
                if (effect == null)
                    active.RemoveAt(i);
                else if (effect.Expired)
                    release(effect);
            }
        }

        public void ReleaseAll(System.Action<PooledEffect> release)
        {
            for (var i = active.Count - 1; i >= 0; i--)
            {
                var effect = active[i];
                if (effect == null)
                    active.RemoveAt(i);
                else
                    release(effect);
            }
        }

        public int CountOf(EffectCategory category)
        {
            var n = 0;
            foreach (var effect in active)
                if (effect != null && effect.Category == category)
                    n++;
            return n;
        }
```

- [ ] **Step 1: Write the test support** — `Tests/PlayMode/TestSupport/PresentationRig.cs`:
```csharp
#if UNITY_EDITOR
using System;
using System.Collections;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>
    /// A small arena with a CombatPresenter whose profiles use primitive stand-in effect prefabs (a particle system, a light,
    /// a tracer line), so presentation behaviour can be tested without the generated art. Optional 0.9 m wall for cover.
    /// </summary>
    internal sealed class PresentationRig : IDisposable
    {
        public readonly TestWorld World = new TestWorld();
        public readonly TacticalPause Pause;
        public readonly CombatPresenter Presenter;
        public readonly CombatPresentationProfile RifleProfile;
        public readonly CombatPresentationProfile FallbackProfile;
        public readonly GameObject EnvironmentRoot;
        public readonly GameObject MuzzlePrefab;
        public readonly GameObject TracerPrefab;
        public readonly GameObject AreaPrefab;
        public readonly GameObject PulsePrefab;
        public CommandableUnit Defender { get; private set; }
        public CoverRegistry Registry { get; private set; }
        public CoverLocation CoverPoint { get; private set; }

        public PresentationRig(bool withWall = false, IntelligenceService intelligence = null)
        {
            EnvironmentRoot = withWall
                ? World.CreateEnvironment((new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f)))
                : World.CreateEnvironment();
            foreach (var box in EnvironmentRoot.GetComponentsInChildren<BoxCollider>())
                box.gameObject.AddComponent<ImpactSurface>().Set(ImpactSurfaceKind.Concrete);
            Pause = World.Track(new GameObject("Pause")).AddComponent<TacticalPause>();

            MuzzlePrefab = Fx("MuzzleFx", light: true);
            TracerPrefab = Fx("TracerFx", tracer: true);
            AreaPrefab = Fx("AreaFx", light: true);
            PulsePrefab = Fx("PulseFx");
            RifleProfile = CombatPresentationProfile.CreateEmpty();
            RifleProfile.SetMuzzle(MuzzlePrefab, 1.5f);
            RifleProfile.SetTracer(TracerStyle.Create(TracerPrefab, 0.04f, 3f, 200f));
            RifleProfile.SetImpacts(
                SurfaceImpact.Create(ImpactSurfaceKind.Concrete, Fx("ConcreteFx")),
                SurfaceImpact.Create(ImpactSurfaceKind.Metal, Fx("MetalFx")),
                SurfaceImpact.Create(ImpactSurfaceKind.Character, Fx("CharacterFx")),
                SurfaceImpact.Create(ImpactSurfaceKind.Default, Fx("DefaultFx")));
            RifleProfile.SetRecoil(0.05f, 0.2f);
            RifleProfile.SetArea(AreaPrefab, 3f, null, 0.6f);
            FallbackProfile = CombatPresentationProfile.CreateEmpty();
            FallbackProfile.SetImpacts(SurfaceImpact.Create(ImpactSurfaceKind.Character, Fx("FallbackCharacterFx")),
                SurfaceImpact.Create(ImpactSurfaceKind.Default, Fx("FallbackDefaultFx")));
            FallbackProfile.SetPulse(PulsePrefab, null);

            var systems = World.Track(new GameObject("Systems"));
            systems.SetActive(false);
            Presenter = systems.AddComponent<CombatPresenter>();
            Presenter.Bind(intelligence, Pause, null, FallbackProfile, FallbackProfile);
            systems.SetActive(true);
        }

        /// <summary>A stand-in effect prefab: a particle system, optionally a light or a tracer line. Never a collider.</summary>
        public GameObject Fx(string name, bool light = false, bool tracer = false)
        {
            var prefab = World.Track(new GameObject(name));
            prefab.SetActive(false);
            var ps = prefab.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.startLifetime = 1f;
            if (light)
            {
                var child = new GameObject("Light");
                child.transform.SetParent(prefab.transform, false);
                var l = child.AddComponent<Light>();
                l.enabled = false;
                l.intensity = 2f;
            }
            if (tracer)
            {
                prefab.AddComponent<LineRenderer>().positionCount = 2;
                prefab.AddComponent<TracerView>();
            }
            return prefab;
        }

        public WeaponVisualAsset WeaponAsset(bool withMuzzle = true, CombatPresentationProfile profile = null)
        {
            var weapon = World.Track(new GameObject("RigWeapon"));
            weapon.SetActive(false);
            new GameObject("Model").transform.SetParent(weapon.transform, false);
            if (withMuzzle)
            {
                var muzzle = new GameObject(UnitWeaponVisual.MuzzleName);
                muzzle.transform.SetParent(weapon.transform, false);
                muzzle.transform.localPosition = new Vector3(0f, 0f, 0.5f);
            }
            var asset = WeaponVisualAsset.Create(weapon, Vector3.zero, Vector3.zero, Vector3.one);
            asset.SetPresentation(profile != null ? profile : RifleProfile);
            return asset;
        }

        /// <summary>A ranged shooter (retaliation off) holding a weapon with a muzzle, with a UnitPresentation bound to the presenter.</summary>
        public CommandableUnit CreateShooter(Vector3 ground, bool weapon = true, bool withMuzzle = true,
            CombatRole role = CombatRole.Ranged, int damage = 10)
        {
            var unit = World.CreateFighter(ground, 1000, damage, 0.1f, role, role == CombatRole.Melee ? 2f : 30f);
            unit.GetComponent<AutoRetaliate>().enabled = false;
            if (weapon)
            {
                var socket = new GameObject(UnitWeaponVisual.SocketName);
                socket.transform.SetParent(unit.transform, false);
                socket.transform.localPosition = new Vector3(0.3f, 0.5f, 0.4f);
                unit.gameObject.AddComponent<UnitWeaponVisual>().Show(WeaponAsset(withMuzzle));
            }
            unit.gameObject.AddComponent<UnitPresentation>().Bind(Presenter);
            Physics.SyncTransforms();
            return unit;
        }

        /// <summary>A target with Health and a UnitPresentation.</summary>
        public Health CreateTarget(Vector3 ground)
        {
            var target = World.CreateDummy(ground);
            target.gameObject.AddComponent<UnitPresentation>().Bind(Presenter);
            Physics.SyncTransforms();
            return target;
        }

        /// <summary>Puts a ranged defender behind the 0.9 m wall (rig built with withWall). Yield-return it.</summary>
        public IEnumerator PutDefenderInCover()
        {
            var environment = EnvironmentRoot;
            CoverPoint = World.CreateCoverPoint(new Vector3(0f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment));
            Registry = World.CreateRegistry(CoverPoint);
            Defender = World.CreateFighter(new Vector3(0f, 0f, -3f), 1000, 10, 0.3f, CombatRole.Ranged, 8f, Registry);
            Defender.GetComponent<AutoRetaliate>().enabled = false;
            Defender.gameObject.AddComponent<UnitPresentation>().Bind(Presenter);
            Defender.Issue(new MoveToCoverCommand(CoverPoint));
            yield return TestWorld.WaitUntil(() => Defender.GetComponent<UnitCover>().Status == CoverStatus.Occupied, 10f);
            yield return new WaitForFixedUpdate();
        }

        public void Dispose()
        {
            World.Dispose();
            Time.timeScale = 1f;
        }
    }
}
#endif
```

- [ ] **Step 2: Write the failing geometry tests** — `Tests/PlayMode/ShotGeometryPlayModeTests.cs`:
```csharp
#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class ShotGeometryPlayModeTests
    {
        TestWorld world;
        Health shooter;
        Health target;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            shooter = world.CreateDummy(new Vector3(0f, 0f, -8f));
            target = world.CreateDummy(new Vector3(0f, 0f, 4f));
            Physics.SyncTransforms();
        }

        [TearDown]
        public void TearDown() => world.Dispose();

        ShotRecord Record(Vector3 aim, ShotOutcome outcome, bool melee = false) =>
            new ShotRecord(1, 1, shooter, target, aim - Vector3.forward * 5f, aim, outcome, melee, null);

        GameObject Wall(Vector3 position, Vector3 scale, ImpactSurfaceKind kind)
        {
            var wall = world.CreateObstacle(position, scale);
            wall.AddComponent<ImpactSurface>().Set(kind);
            Physics.SyncTransforms();
            return wall;
        }

        static readonly Vector3 Muzzle = new Vector3(0f, 1.4f, -7f);

        [Test]
        public void OpenHit_DrawsTheTracerToTheAimPoint_AndAnImpactOnTheCharacter()
        {
            var aim = target.transform.position + Vector3.up * ShotRecord.BodyHeight;
            var end = ShotGeometry.Resolve(Record(aim, ShotOutcome.Hit), Muzzle, new CosmeticRandom(1));
            Assert.That(end.HasTracer && end.HasImpact, Is.True);
            Assert.That(end.End, Is.EqualTo(aim));
            Assert.That(end.Surface, Is.EqualTo(ImpactSurfaceKind.Character));
            Assert.That(Vector3.Dot(end.ImpactNormal, (aim - Muzzle).normalized), Is.LessThan(-0.99f), "the normal faces back at the shooter");
        }

        [Test]
        public void HitBehindAWall_TruncatesTheTracerAtTheWall_AndOmitsTheImpact()
        {
            Wall(new Vector3(0f, 1.5f, 0f), new Vector3(4f, 3f, 0.5f), ImpactSurfaceKind.Concrete);
            var aim = target.transform.position + Vector3.up * ShotRecord.BodyHeight;
            var end = ShotGeometry.Resolve(Record(aim, ShotOutcome.Hit), Muzzle, new CosmeticRandom(1));
            Assert.That(end.HasTracer, Is.True);
            Assert.That(end.End.z, Is.LessThan(0f), "the streak stops at the wall's near face");
            Assert.That(end.HasImpact, Is.False, "a body hit behind a wall is not invented");
        }

        [Test]
        public void CoverMiss_WithAWall_LandsOnTheWall_WithItsSurface()
        {
            Wall(new Vector3(0f, 0.45f, 2f), new Vector3(4f, 0.9f, 0.5f), ImpactSurfaceKind.Metal);
            var aim = target.transform.position + Vector3.up * ShotRecord.BodyHeight;
            var end = ShotGeometry.Resolve(Record(aim, ShotOutcome.CoverMiss), Muzzle, new CosmeticRandom(1));
            Assert.That(end.HasTracer && end.HasImpact, Is.True);
            Assert.That(end.Surface, Is.EqualTo(ImpactSurfaceKind.Metal));
            Assert.That(end.ImpactPoint.z, Is.InRange(1.7f, 2.3f));
        }

        [Test]
        public void CoverMiss_InTheOpen_PassesBesideTheTarget_AndNeverShowsABodyHit()
        {
            var aim = target.transform.position + Vector3.up * ShotRecord.BodyHeight;
            for (var seed = 0; seed < 20; seed++)
            {
                var end = ShotGeometry.Resolve(Record(aim, ShotOutcome.CoverMiss), Muzzle, new CosmeticRandom(seed));
                Assert.That(end.HasTracer, Is.True);
                Assert.That(end.Surface, Is.Not.EqualTo(ImpactSurfaceKind.Character));
                var along = Vector3.Dot(end.End - Muzzle, (aim - Muzzle).normalized);
                Assert.That(along, Is.GreaterThan((aim - Muzzle).magnitude - 0.5f), "the streak carries on past the target");
                Assert.That(Vector3.Distance(end.End, aim), Is.GreaterThan(0.4f), "it does not end on the body");
            }
        }

        [Test]
        public void Melee_HasNoTracer_AndAHitShowsOnlyTheCharacterImpact_AMissShowsNothing()
        {
            var aim = target.transform.position + Vector3.up * ShotRecord.BodyHeight;
            var hit = ShotGeometry.Resolve(Record(aim, ShotOutcome.Hit, melee: true), Muzzle, new CosmeticRandom(1));
            Assert.That(hit.HasTracer, Is.False);
            Assert.That(hit.HasImpact && hit.Surface == ImpactSurfaceKind.Character, Is.True);
            var miss = ShotGeometry.Resolve(Record(aim, ShotOutcome.CoverMiss, melee: true), Muzzle, new CosmeticRandom(1));
            Assert.That(miss.HasTracer || miss.HasImpact, Is.False);
        }

        [Test]
        public void PointBlankAndNonFiniteGeometry_ShowNothing_AndDoNotThrow()
        {
            var aim = new Vector3(1f, 1f, 1f);
            var same = ShotGeometry.Resolve(Record(aim, ShotOutcome.Hit), aim, new CosmeticRandom(1));
            Assert.That(same.HasTracer || same.HasImpact, Is.False);
            var nan = ShotGeometry.Resolve(Record(new Vector3(float.NaN, 0f, 0f), ShotOutcome.CoverMiss), Muzzle, new CosmeticRandom(1));
            Assert.That(nan.HasTracer || nan.HasImpact, Is.False);
            var inf = ShotGeometry.Resolve(Record(new Vector3(float.PositiveInfinity, 0f, 0f), ShotOutcome.Hit), Muzzle, new CosmeticRandom(1));
            Assert.That(inf.HasTracer || inf.HasImpact, Is.False);
        }

        [Test]
        public void UnitsInTheWay_NeverBlockTheStreak()
        {
            var blocker = world.CreateDummy(new Vector3(0f, 0f, 0f));
            Physics.SyncTransforms();
            var aim = target.transform.position + Vector3.up * ShotRecord.BodyHeight;
            var end = ShotGeometry.Resolve(Record(aim, ShotOutcome.Hit), Muzzle, new CosmeticRandom(1));
            Assert.That(blocker, Is.Not.Null);
            Assert.That(end.End, Is.EqualTo(aim), "units never block (the LineOfSight convention)");
            Assert.That(end.HasImpact, Is.True);
        }
    }
}
#endif
```

- [ ] **Step 3: Run to verify failure** — `Tools/run-tests.sh PlayMode "Blackglass.Tests.ShotGeometryPlayModeTests"` → `EXIT=1`.

- [ ] **Step 4: Implement `ShotGeometry.cs`**
```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>Where a shot is drawn to and what it hits visually. Cosmetic only.</summary>
    internal readonly struct ShotEndpoint
    {
        public ShotEndpoint(bool hasTracer, Vector3 end, bool hasImpact, Vector3 impactPoint, Vector3 impactNormal, ImpactSurfaceKind surface)
        {
            HasTracer = hasTracer;
            End = end;
            HasImpact = hasImpact;
            ImpactPoint = impactPoint;
            ImpactNormal = impactNormal;
            Surface = surface;
        }

        public bool HasTracer { get; }
        public Vector3 End { get; }
        public bool HasImpact { get; }
        public Vector3 ImpactPoint { get; }
        public Vector3 ImpactNormal { get; }
        public ImpactSurfaceKind Surface { get; }
    }

    /// <summary>
    /// Works out, from a shot gameplay already resolved, where its streak ends and what the impact looks like. It only reads
    /// colliders (units never count, the LineOfSight convention) and writes nothing; it never decides a hit. A hit is drawn to
    /// the recorded aim point unless a world blocker is in the way (then the streak stops there and the body impact is
    /// omitted). A cover miss lands on the cover that turned it away, else passes beside the target and carries on to the
    /// first world surface or a cap. Anything that cannot be resolved safely (zero length, NaN) shows nothing.
    /// </summary>
    internal static class ShotGeometry
    {
        public const float MissCap = 30f;
        const float Epsilon = 0.05f;
        // A unit's feet, measured down from the recorded aim point (chest = pivot + BodyHeight, the pivot is 1 m up): 0.1 m above the floor.
        const float AimToFeet = ShotRecord.BodyHeight + 0.9f;

        public static ShotEndpoint Resolve(in ShotRecord record, Vector3 muzzle, CosmeticRandom random)
        {
            var toAim = record.AimPoint - muzzle;
            var distance = toAim.magnitude;
            if (!(distance > 0.1f) || float.IsInfinity(distance))
                return default;
            var direction = toAim / distance;

            if (record.Outcome == ShotOutcome.Hit)
            {
                if (record.IsMelee)
                    return new ShotEndpoint(false, default, true, record.AimPoint, -direction, ImpactSurfaceKind.Character);
                if (TryFirstWorldHit(muzzle, direction, distance, out var blocker) && blocker.distance < distance - Epsilon)
                    return new ShotEndpoint(true, blocker.point, false, default, default, ImpactSurfaceKind.Default);
                return new ShotEndpoint(true, record.AimPoint, true, record.AimPoint, -direction, ImpactSurfaceKind.Character);
            }

            if (record.IsMelee)
                return default;

            // Cover miss: the cover rule is judged on the line to the target's feet, so look for what that line crosses.
            var toFeet = record.AimPoint - Vector3.up * AimToFeet - muzzle;
            var feetDistance = toFeet.magnitude;
            if (feetDistance > 0.1f && TryFirstWorldHit(muzzle, toFeet / feetDistance, feetDistance, out var cover))
                return new ShotEndpoint(true, cover.point, true, cover.point, cover.normal, ImpactSurfaces.Classify(cover.collider));

            // Open ground: pass beside the target and carry on.
            var side = Vector3.Cross(Vector3.up, direction);
            if (side.sqrMagnitude < 1e-4f)
                side = Vector3.right;
            side.Normalize();
            var deflected = (record.AimPoint + side * (random.Sign() * random.Range(0.6f, 1.2f)) - muzzle).normalized;
            if (TryFirstWorldHit(muzzle, deflected, MissCap, out var beyond))
                return new ShotEndpoint(true, beyond.point, true, beyond.point, beyond.normal, ImpactSurfaces.Classify(beyond.collider));
            return new ShotEndpoint(true, muzzle + deflected * MissCap, false, default, default, ImpactSurfaceKind.Default);
        }

        // The nearest collider that is not part of a unit, skipping units in the way (a few at most).
        static bool TryFirstWorldHit(Vector3 origin, Vector3 direction, float maxDistance, out RaycastHit result)
        {
            var travelled = 0f;
            var from = origin;
            for (var i = 0; i < 4 && travelled < maxDistance; i++)
            {
                if (!Physics.Raycast(from, direction, out var hit, maxDistance - travelled, ~0, QueryTriggerInteraction.Ignore))
                    break;
                if (hit.collider.GetComponentInParent<Health>() == null)
                {
                    hit.distance += travelled;
                    result = hit;
                    return true;
                }
                var step = hit.distance + 0.02f;
                travelled += step;
                from += direction * step;
            }
            result = default;
            return false;
        }
    }
}
```

- [ ] **Step 5: Run the geometry tests** — `Tools/run-tests.sh PlayMode "Blackglass.Tests.ShotGeometryPlayModeTests"`. Expected `EXIT=0`. (If `UnitsInTheWay…` fails because the blocker dummy at the origin hides nothing, check that `CreateDummy`'s collider is a `CapsuleCollider` under a `Health`.)

- [ ] **Step 6: Implement `CameraImpulse.cs`** (the controller integration is Task 11; the component is needed now by the presenter)
```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// A decaying camera wobble source. Lives on the camera rig; TacticalCameraController adds LocalOffset to the viewing
    /// camera's local position. It advances on scaled time only, so a tactical pause holds it still while the camera's own
    /// pan, zoom and rotation (unscaled) keep working. Strength 0 does nothing.
    /// </summary>
    public sealed class CameraImpulse : MonoBehaviour
    {
        // Impulses fade out beyond this ground distance from the rig.
        public const float FalloffDistance = 40f;

        readonly ImpulseEnvelope envelope = new ImpulseEnvelope();
        readonly CosmeticRandom random = new CosmeticRandom();

        /// <summary>The offset in the camera's local space (x right, y up).</summary>
        public Vector3 LocalOffset
        {
            get
            {
                var o = envelope.Offset;
                return new Vector3(o.x, o.y, 0f);
            }
        }

        public void Add(float strength) => envelope.Add(strength, random.Range(0f, 6.28f), random.Range(0f, 6.28f));

        /// <summary>1 at the rig, falling to 0 at FalloffDistance (flat distance).</summary>
        public float DistanceFactor(Vector3 world)
        {
            var d = world - transform.position;
            d.y = 0f;
            return Mathf.Clamp01(1f - d.magnitude / FalloffDistance);
        }

        public void Clear() => envelope.Reset();

        void Update()
        {
            if (SimulationTime.IsRunning)
                envelope.Advance(Time.deltaTime);
        }

        void OnDisable() => envelope.Reset();
    }
}
```

- [ ] **Step 7: Implement `CombatPresenter.cs`**
```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The one scene-level presentation service. UnitPresentation components hand it what gameplay resolved; it decides what
    /// the player may see and hear (the knowledge gate), spawns pooled effects under explicit budgets, plays budgeted audio,
    /// sends a camera impulse and clears everything on a mission change. It never touches gameplay state, and every part is
    /// optional: a missing prefab, clip or profile just means nothing is shown. Effects follow the simulation clock, so a
    /// tactical pause freezes them; audio is paused through TacticalPause.Changed.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CombatPresenter : MonoBehaviour
    {
        const float MuzzleLifetime = 1.6f;
        const float ImpactLifetime = 1.4f;
        const float AreaLifetime = 4f;
        const float PulseLifetime = 1.4f;
        const float FallbackMuzzleForward = 0.6f;
        const float SurfaceOffset = 0.3f;

        [SerializeField] PresentationSettings settings = new PresentationSettings();
        [SerializeField] IntelligenceService intelligence;
        [SerializeField] TacticalPause pause;
        [SerializeField] CameraImpulse cameraImpulse;
        [SerializeField] CombatPresentationProfile fallbackProfile;
        [SerializeField] CombatPresentationProfile supportProfile;

        readonly CosmeticRandom random = new CosmeticRandom();
        readonly HashSet<UnityEngine.Object> warned = new HashSet<UnityEngine.Object>();
        readonly Dictionary<Health, int> impactFrames = new Dictionary<Health, int>();
        readonly List<Health> staleBuffer = new List<Health>();
        EffectPool pool;
        EffectBudget budget;
        CombatAudio audio;
        Transform container;
        CombatPresentationProfile emptyFallback;
        Func<Vector3, PointVisibility> queryDelegate;
        Func<Vector3, PointVisibility> pointOverride;
        Func<Health, bool> unitOverride;
        bool subscribed;

        public PresentationSettings Settings => settings;
        public bool IsActive => isActiveAndEnabled && settings.enabled;
        public EffectBudget Budget { get { EnsureBuilt(); return budget; } }
        public CombatAudio Audio { get { EnsureBuilt(); return audio; } }
        public int ActiveEffects => pool == null ? 0 : pool.ActiveCount;
        public int ActiveOf(EffectCategory category) => pool == null ? 0 : pool.CountOf(category);

        /// <summary>Turning it off releases everything that is playing.</summary>
        public bool Enabled
        {
            get => settings.enabled;
            set
            {
                if (settings.enabled == value)
                    return;
                settings.enabled = value;
                if (!value)
                    Clear();
            }
        }

        /// <summary>The fallback profile (an empty one, warned about once, when none is assigned).</summary>
        public CombatPresentationProfile Fallback
        {
            get
            {
                if (fallbackProfile != null)
                    return fallbackProfile;
                if (emptyFallback == null)
                {
                    emptyFallback = CombatPresentationProfile.CreateEmpty();
                    WarnOnce(this, "CombatPresenter has no fallback profile; units without a presentation profile show nothing.");
                }
                return emptyFallback;
            }
        }

        public CombatPresentationProfile SupportProfile => supportProfile != null ? supportProfile : Fallback;

        internal void Bind(IntelligenceService service, TacticalPause pauseService, CameraImpulse impulse,
            CombatPresentationProfile fallback, CombatPresentationProfile support)
        {
            Unsubscribe();
            intelligence = service;
            pause = pauseService;
            cameraImpulse = impulse;
            fallbackProfile = fallback;
            supportProfile = support;
            if (isActiveAndEnabled)
                Subscribe();
        }

        /// <summary>Test and preview hook: replaces the knowledge queries (never used by the mission scenes).</summary>
        internal void UseKnowledge(Func<Vector3, PointVisibility> points, Func<Health, bool> units)
        {
            pointOverride = points;
            unitOverride = units;
        }

        internal void ClearKnowledgeOverride()
        {
            pointOverride = null;
            unitOverride = null;
        }

        void Awake() => EnsureBuilt();

        void OnEnable()
        {
            EnsureBuilt();
            Subscribe();
        }

        void OnDisable()
        {
            Unsubscribe();
            Clear();
        }

        void OnDestroy()
        {
            if (container != null)
                Destroy(container.gameObject);
        }

        void EnsureBuilt()
        {
            if (pool != null)
                return;
            container = new GameObject("PresentationPool").transform;
            pool = new EffectPool(container);
            budget = new EffectBudget(settings.quality);
            queryDelegate = QueryPoint;
            var audioObject = new GameObject("CombatAudio");
            audioObject.transform.SetParent(transform, false);
            audio = audioObject.AddComponent<CombatAudio>();
            audio.Initialize();
        }

        void Subscribe()
        {
            if (subscribed)
                return;
            subscribed = true;
            if (pause != null)
            {
                pause.Changed += OnPauseChanged;
                audio.SetPaused(pause.IsPaused);
            }
            if (intelligence != null)
                intelligence.Changed += Regate;
        }

        void Unsubscribe()
        {
            if (!subscribed)
                return;
            subscribed = false;
            if (pause != null)
                pause.Changed -= OnPauseChanged;
            if (intelligence != null)
                intelligence.Changed -= Regate;
        }

        void OnPauseChanged(bool paused) => audio.SetPaused(paused);

        void Update()
        {
            if (pool == null || !SimulationTime.IsRunning)
                return;
            pool.Sweep(Release);
        }

        // ---- knowledge ----

        public bool Observes(Health unit) =>
            unit == null || (unitOverride != null ? unitOverride(unit) : Knowledge.IsObserved(intelligence, unit));

        public PointVisibility QueryPoint(Vector3 point) =>
            pointOverride != null ? pointOverride(point) : Knowledge.PointVisibilityOf(intelligence, point);

        /// <summary>Hides or shows every long-lived effect according to the current knowledge, without restarting any.</summary>
        internal void Regate()
        {
            if (pool == null)
                return;
            var active = pool.Active;
            for (var i = 0; i < active.Count; i++)
            {
                var effect = active[i];
                if (effect != null && effect.HasGatePoint)
                    effect.SetVisible(PresentationVisibility.AllowsPoint(queryDelegate, effect.GatePoint));
            }
        }

        // ---- requests from UnitPresentation ----

        internal void ShowShot(in ShotRecord record, CombatPresentationProfile profile, Transform muzzle)
        {
            if (!IsActive)
                return;
            EnsureBuilt();
            profile = profile != null ? profile : Fallback;
            var shooterObserved = Observes(record.Source);
            var victimObserved = record.Target != null && Observes(record.Target);

            Vector3 from;
            if (muzzle != null)
            {
                from = muzzle.position;
            }
            else
            {
                var toAim = record.AimPoint - record.Origin;
                from = record.Origin + (toAim.sqrMagnitude > 1e-4f ? toAim.normalized : Vector3.forward) * FallbackMuzzleForward;
                if (record.Source != null && profile.MuzzleEffect != null)
                    WarnOnce(record.Source, $"{record.Source.name} fired without a Muzzle on its weapon; effects start in front of the unit.");
            }

            var endpoint = ShotGeometry.Resolve(record, from, random);
            var onCharacter = endpoint.Surface == ImpactSurfaceKind.Character;
            var gatePoint = onCharacter ? endpoint.ImpactPoint : endpoint.ImpactPoint + endpoint.ImpactNormal * SurfaceOffset;
            var allowed = PresentationVisibility.EvaluateShot(shooterObserved, victimObserved, queryDelegate, from,
                endpoint.HasTracer, endpoint.End, endpoint.HasImpact, gatePoint, onCharacter);

            var aim = endpoint.HasTracer ? endpoint.End - from : record.AimPoint - from;
            var rotation = aim.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(aim) : Quaternion.identity;
            if (allowed.Muzzle && !record.IsMelee)
            {
                Spawn(profile.MuzzleEffect, EffectCategory.Muzzle, from, rotation, profile.MuzzleScale, MuzzleLifetime, false, default);
                PlayCue(profile.FireCue, from);
            }
            if (allowed.Tracer)
                SpawnTracer(profile.Tracer, from, endpoint.End);
            if (allowed.Impact)
            {
                PlayImpact(profile, endpoint.Surface, endpoint.ImpactPoint, endpoint.ImpactNormal);
                if (onCharacter && record.Target != null)
                    MarkImpact(record.Target);
            }
        }

        internal void ShowArea(in AreaRecord record, CombatPresentationProfile profile)
        {
            if (!IsActive)
                return;
            EnsureBuilt();
            profile = profile != null ? profile : Fallback;
            var center = record.Center;
            if (QueryPoint(center) != PointVisibility.Visible)
                return;
            if (profile.AreaEffect != null)
            {
                var scale = record.Radius / Mathf.Max(0.01f, profile.ReferenceRadius);
                Spawn(profile.AreaEffect, EffectCategory.Area, center + Vector3.up * 0.15f, Quaternion.identity, scale, AreaLifetime, true, center);
            }
            PlayCue(profile.AreaCue, center);
            if (cameraImpulse != null && settings.cameraShake > 0f)
                cameraImpulse.Add(profile.CameraImpulse * settings.cameraShake * cameraImpulse.DistanceFactor(center));
        }

        internal void ShowPulse(Health unit, Vector3 point)
        {
            if (!IsActive || unit == null || !Observes(unit))
                return;
            EnsureBuilt();
            var profile = SupportProfile;
            Spawn(profile.PulseEffect, EffectCategory.Pulse, point, Quaternion.identity, 1f, PulseLifetime, true, point);
            PlayCue(profile.PulseCue, point);
        }

        internal void ShowDeath(Health unit, Vector3 point)
        {
            if (!IsActive || unit == null || !Observes(unit))
                return;
            EnsureBuilt();
            PlayCue(Fallback.DeathCue, point);
        }

        internal void ShowCharacterBurst(Health victim, Vector3 point, Vector3 normal)
        {
            if (!IsActive || victim == null || !Observes(victim))
                return;
            EnsureBuilt();
            PlayImpact(Fallback, ImpactSurfaceKind.Character, point, normal);
        }

        /// <summary>True once when a shot already placed a character impact on this victim in the current frame.</summary>
        internal bool ConsumeCharacterImpact(Health victim)
        {
            if (victim == null || !impactFrames.TryGetValue(victim, out var frame))
                return false;
            impactFrames.Remove(victim);
            return frame >= Time.frameCount;
        }

        void MarkImpact(Health victim)
        {
            if (impactFrames.Count > 64)
            {
                staleBuffer.Clear();
                foreach (var pair in impactFrames)
                    if (pair.Key == null || pair.Value < Time.frameCount - 2)
                        staleBuffer.Add(pair.Key);
                foreach (var stale in staleBuffer)
                    impactFrames.Remove(stale);
            }
            impactFrames[victim] = Time.frameCount;
        }

        // ---- spawning ----

        PooledEffect Spawn(GameObject prefab, EffectCategory category, Vector3 position, Quaternion rotation, float scale,
            float lifetime, bool lingering, Vector3 gatePoint)
        {
            if (prefab == null)
                return null;
            if (!budget.TryAcquire(category))
            {
                if (category != EffectCategory.Area || !RecycleOldest(category) || !budget.TryAcquire(category))
                    return null;
            }
            var effect = pool.Rent(prefab);
            var light = effect.Lights.Length > 0 && budget.TryAcquireLight();
            effect.HoldsLight = light;
            effect.Begin(category, position, rotation, scale, lifetime, light);
            pool.Track(effect);
            if (lingering)
                effect.SetGate(gatePoint);
            return effect;
        }

        void SpawnTracer(TracerStyle style, Vector3 from, Vector3 to)
        {
            if (style == null || !style.IsUsable || !budget.TryAcquire(EffectCategory.Tracer))
                return;
            var effect = pool.Rent(style.Prefab);
            if (effect.Tracer == null)
            {
                budget.Release(EffectCategory.Tracer);
                pool.Return(effect);
                WarnOnce(style.Prefab, "The tracer prefab has no TracerView component.");
                return;
            }
            effect.HoldsLight = false;
            effect.Begin(EffectCategory.Tracer, from, Quaternion.identity, 1f, 10f, false);
            effect.SetLifetime(effect.Tracer.Launch(from, to, style));
            pool.Track(effect);
        }

        void PlayImpact(CombatPresentationProfile profile, ImpactSurfaceKind surface, Vector3 point, Vector3 normal)
        {
            var entry = profile.ImpactFor(surface);
            if (entry == null)
                entry = Fallback.ImpactFor(surface);
            if (entry == null)
                return;
            var rotation = normal.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(normal) : Quaternion.identity;
            Spawn(entry.Prefab, EffectCategory.Impact, point, rotation, entry.Scale, ImpactLifetime, true, point + normal * SurfaceOffset);
            PlayCue(entry.Cue, point);
        }

        void PlayCue(AudioCue cue, Vector3 position)
        {
            if (cue == null || cue.IsEmpty)
                return;
            audio.Play(cue, position, settings.masterVolume, random);
        }

        bool RecycleOldest(EffectCategory category)
        {
            PooledEffect oldest = null;
            foreach (var effect in pool.Active)
                if (effect != null && effect.Category == category && (oldest == null || effect.EndTime < oldest.EndTime))
                    oldest = effect;
            if (oldest == null)
                return false;
            Release(oldest);
            return true;
        }

        void Release(PooledEffect effect)
        {
            budget.Release(effect.Category);
            if (effect.HoldsLight)
                budget.ReleaseLight();
            pool.Return(effect);
        }

        /// <summary>Releases every effect, stops every sound and forgets pending impacts (mission change, disable).</summary>
        public void Clear()
        {
            if (pool == null)
                return;
            pool.ReleaseAll(Release);
            budget.Reset();
            audio.StopAll();
            impactFrames.Clear();
            if (cameraImpulse != null)
                cameraImpulse.Clear();
        }

        public void SetQuality(PresentationQuality quality)
        {
            EnsureBuilt();
            Clear();
            settings.quality = quality;
            budget = new EffectBudget(quality);
        }

        /// <summary>Logs a message once per key, so a misconfiguration is reported without flooding the Console.</summary>
        internal void WarnOnce(UnityEngine.Object key, string message)
        {
            if (key == null || !warned.Add(key))
                return;
            Debug.LogWarning(message, key);
        }
    }
}
```

- [ ] **Step 8: Implement `UnitPresentation.cs`** (shots, areas and recoil; hit flash, pulse and death are added in Tasks 10 and 13)
```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Per-unit presentation adapter: listens to what its unit's gameplay components resolved (a discharged shot, an ability),
    /// resolves the weapon's profile and muzzle, and hands the request to the scene's CombatPresenter. It keeps the weapon's
    /// visual recoil. It never changes gameplay and nothing in gameplay reads it. Added by the mission spawner (and by hand in
    /// test scenes), never baked into a prefab, so rigs without a presenter are untouched. Records are delivered only to live
    /// subscribers: enabling it later replays nothing.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class UnitPresentation : MonoBehaviour
    {
        [SerializeField] CombatPresenter presenter;
        Health health;
        UnitAttacker attacker;
        UnitAbilities abilities;
        UnitWeaponVisual weapon;
        bool subscribed;
        readonly SequenceFilter seen = new SequenceFilter();
        readonly RecoilEnvelope recoil = new RecoilEnvelope();
        Transform recoilTarget;
        Vector3 recoilBase;

        public CombatPresenter Presenter => presenter;

        public void Bind(CombatPresenter target)
        {
            Unsubscribe();
            presenter = target;
            if (isActiveAndEnabled)
                Subscribe();
        }

        void OnEnable()
        {
            if (presenter != null)
                Subscribe();
        }

        void OnDisable()
        {
            Unsubscribe();
            EndRecoil();
        }

        void Subscribe()
        {
            if (subscribed)
                return;
            subscribed = true;
            TryGetComponent(out health);
            TryGetComponent(out attacker);
            TryGetComponent(out abilities);
            TryGetComponent(out weapon);
            if (attacker != null)
                attacker.Fired += OnFired;
            if (abilities != null)
            {
                abilities.ShotResolved += OnAbilityShot;
                abilities.AreaResolved += OnAreaResolved;
            }
            if (weapon != null)
                weapon.Changed += OnWeaponChanged;
        }

        void Unsubscribe()
        {
            if (!subscribed)
                return;
            subscribed = false;
            if (attacker != null)
                attacker.Fired -= OnFired;
            if (abilities != null)
            {
                abilities.ShotResolved -= OnAbilityShot;
                abilities.AreaResolved -= OnAreaResolved;
            }
            if (weapon != null)
                weapon.Changed -= OnWeaponChanged;
        }

        void OnFired(ShotRecord record) => Deliver(record);
        void OnAbilityShot(ShotRecord record) => Deliver(record);
        void OnAreaResolved(AreaRecord record) => Deliver(record);

        internal void Deliver(in ShotRecord record)
        {
            if (presenter == null || !seen.TryAccept(record.EmitterId, record.Sequence))
                return;
            var profile = ProfileFor(record);
            presenter.ShowShot(record, profile, weapon != null ? weapon.Muzzle : null);
            if (!record.IsMelee && presenter.IsActive)
                Kick(profile);
        }

        internal void Deliver(in AreaRecord record)
        {
            if (presenter == null || !seen.TryAccept(record.EmitterId, record.Sequence))
                return;
            presenter.ShowArea(record, record.Ability != null ? record.Ability.Presentation : null);
        }

        // An ability's own profile wins; else the weapon in hand; else the presenter's fallback.
        CombatPresentationProfile ProfileFor(in ShotRecord record)
        {
            if (record.Ability != null && record.Ability.Presentation != null)
                return record.Ability.Presentation;
            var shown = weapon != null ? weapon.Shown : null;
            if (shown != null && shown.Presentation != null)
                return shown.Presentation;
            return presenter.Fallback;
        }

        // ---- recoil: a visual-only kick of the managed weapon model along its own barrel axis ----

        void Kick(CombatPresentationProfile profile)
        {
            if (weapon == null || weapon.Current == null || weapon.Muzzle == null || profile.RecoilKick <= 0f)
                return;
            var model = weapon.Current.transform;
            if (recoilTarget != model)
            {
                EndRecoil();
                recoilTarget = model;
                recoilBase = model.localPosition;
            }
            recoil.Kick(profile.RecoilKick, profile.RecoilSeconds);
            ApplyRecoil();
        }

        void Update()
        {
            if (!recoil.IsActive || !SimulationTime.IsRunning)
                return;
            recoil.Advance(Time.deltaTime);
            ApplyRecoil();
        }

        void ApplyRecoil()
        {
            if (recoilTarget == null || weapon == null || weapon.Muzzle == null)
            {
                recoil.Reset();
                return;
            }
            var offset = recoil.Offset;
            if (offset <= 0f)
            {
                recoilTarget.localPosition = recoilBase;
                return;
            }
            var back = -weapon.Muzzle.forward * offset;
            recoilTarget.localPosition = recoilBase + (recoilTarget.parent != null ? recoilTarget.parent.InverseTransformVector(back) : back);
        }

        void OnWeaponChanged() => EndRecoil();

        void EndRecoil()
        {
            recoil.Reset();
            if (recoilTarget != null)
                recoilTarget.localPosition = recoilBase;
            recoilTarget = null;
        }
    }
}
```

- [ ] **Step 9: Write the presenter/unit tests** — `Tests/PlayMode/CombatPresenterPlayModeTests.cs`:
```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CombatPresenterPlayModeTests
    {
        static readonly UnityEngine.TestTools.Utils.Vector3EqualityComparer V3 = new UnityEngine.TestTools.Utils.Vector3EqualityComparer(1e-4f);
        PresentationRig rig;

        [TearDown]
        public void TearDown() => rig?.Dispose();

        static UnitAttacker AttackerOf(CommandableUnit unit) => unit.GetComponent<UnitAttacker>();

        [UnityTest]
        public IEnumerator ValidHit_ShowsMuzzleTracerAndCharacterImpact_AndDoesNotChangeTheDamage()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f));
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            var visual = shooter.GetComponent<UnitWeaponVisual>();

            Assert.That(AttackerOf(shooter).TryAttack(target), Is.True);

            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Muzzle), Is.EqualTo(1));
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Tracer), Is.EqualTo(1));
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Impact), Is.EqualTo(1));
            Assert.That(target.Current, Is.EqualTo(target.Max - 10), "gameplay damage is exactly the attacker's");
            var muzzleEffect = FindActive(EffectCategory.Muzzle);
            Assert.That(muzzleEffect.transform.position, Is.EqualTo(visual.Muzzle.position).Using(V3));
        }

        static PooledEffect FindActive(EffectCategory category)
        {
            foreach (var effect in Object.FindObjectsByType<PooledEffect>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                if (effect.IsActive && effect.Category == category)
                    return effect;
            return null;
        }

        [UnityTest]
        public IEnumerator RejectedAttack_ShowsNothing()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f));
            var far = rig.CreateTarget(new Vector3(0f, 0f, 60f));
            yield return null;
            Assert.That(AttackerOf(shooter).TryAttack(far), Is.False);
            Assert.That(rig.Presenter.ActiveEffects, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator CoverMiss_ShowsMuzzleAndTracer_AndALandingOnTheCover_NeverACharacterImpact()
        {
            rig = new PresentationRig(withWall: true);
            yield return rig.PutDefenderInCover();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, 5f));
            AttackerOf(shooter).HitRoll = () => 0.99f;
            yield return new WaitForFixedUpdate();
            var defenderHealth = rig.Defender.GetComponent<Health>();

            Assert.That(AttackerOf(shooter).TryAttack(defenderHealth), Is.True);

            Assert.That(defenderHealth.Current, Is.EqualTo(defenderHealth.Max), "Precondition: it was a cover miss");
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Muzzle), Is.EqualTo(1));
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Tracer), Is.EqualTo(1));
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Impact), Is.EqualTo(1), "the shot lands on the wall");
            Assert.That(rig.Presenter.ConsumeCharacterImpact(defenderHealth), Is.False, "no body hit is depicted");
        }

        [UnityTest]
        public IEnumerator Melee_ShowsNoMuzzleOrTracer_ButAHitShowsTheCharacterImpact()
        {
            rig = new PresentationRig();
            var attacker = rig.CreateShooter(new Vector3(0f, 0f, 0f), weapon: false, role: CombatRole.Melee);
            var target = rig.CreateTarget(new Vector3(0f, 0f, 1.2f));
            yield return null;
            Assert.That(AttackerOf(attacker).TryAttack(target), Is.True);
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Muzzle), Is.EqualTo(0));
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Tracer), Is.EqualTo(0));
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Impact), Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator AKillingShot_StillShowsItsMuzzleAndImpact_FromTheSnapshot()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f), damage: 5000);
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            Assert.That(AttackerOf(shooter).TryAttack(target), Is.True);
            Assert.That(target.IsAlive, Is.False, "Precondition: the shot killed and deactivated its target");
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Muzzle), Is.EqualTo(1));
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Impact), Is.EqualTo(1), "the impact comes from the captured aim point, not the dead unit");
        }

        [UnityTest]
        public IEnumerator Muzzle_FollowsTheWeaponsCurrentPose_AndKeepsWorldScale()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f));
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            var visual = shooter.GetComponent<UnitWeaponVisual>();
            var socket = visual.Current.transform.parent;
            socket.localScale = Vector3.one * 0.01f;      // an imported hierarchy with a scale correction
            yield return null;

            socket.localPosition = new Vector3(-0.4f, 0.9f, 0.2f);
            AttackerOf(shooter).TryAttack(target);

            var effect = FindActive(EffectCategory.Muzzle);
            Assert.That(effect.transform.position, Is.EqualTo(visual.Muzzle.position).Using(V3));
            Assert.That(effect.transform.lossyScale, Is.EqualTo(Vector3.one * 1.5f).Using(V3),
                "the effect keeps its profile size in world units, whatever the socket scale");
        }

        [UnityTest]
        public IEnumerator MissingMuzzle_StillShowsTheEffects_WithOneWarning()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f), withMuzzle: false);
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            LogAssert.Expect(LogType.Warning, new Regex("without a Muzzle"));
            AttackerOf(shooter).TryAttack(target);
            yield return new WaitForSeconds(0.15f);
            AttackerOf(shooter).TryAttack(target);
            yield return new WaitForSeconds(0.15f);
            AttackerOf(shooter).TryAttack(target);
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Muzzle), Is.GreaterThanOrEqualTo(1));
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator UnitWithNoWeaponVisualOrProfile_FiresWithoutErrors_AndUsesTheFallbackImpact()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f), weapon: false);
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            Assert.That(AttackerOf(shooter).TryAttack(target), Is.True);
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Muzzle), Is.EqualTo(0), "the fallback profile has no muzzle effect");
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Impact), Is.EqualTo(1));
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator DisabledPresentation_ShowsNothing_AndGameplayIsIdentical()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f));
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            rig.Presenter.Enabled = false;
            Assert.That(AttackerOf(shooter).TryAttack(target), Is.True);
            Assert.That(rig.Presenter.ActiveEffects, Is.EqualTo(0));
            Assert.That(target.Current, Is.EqualTo(target.Max - 10));
        }

        [UnityTest]
        public IEnumerator DeliveringTheSameRecordTwice_PresentsOnce()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f));
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            var record = new ShotRecord(7, 1, shooter.GetComponent<Health>(), target, shooter.transform.position + Vector3.up * 0.5f,
                target.transform.position + Vector3.up * ShotRecord.BodyHeight, ShotOutcome.Hit, false, null);
            var presentation = shooter.GetComponent<UnitPresentation>();
            presentation.Deliver(record);
            presentation.Deliver(record);
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Muzzle), Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator Clear_ReleasesEveryEffect_AndResetsTheBudgets()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f));
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            AttackerOf(shooter).TryAttack(target);
            Assert.That(rig.Presenter.ActiveEffects, Is.GreaterThan(0));
            rig.Presenter.Clear();
            Assert.That(rig.Presenter.ActiveEffects, Is.EqualTo(0));
            Assert.That(rig.Presenter.Budget.Active(EffectCategory.Muzzle), Is.EqualTo(0));
            Assert.That(rig.Presenter.Budget.LightsActive, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator Recoil_KicksTheWeaponBack_RecoversExactly_AndFreezesWhilePaused()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f));
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            var visual = shooter.GetComponent<UnitWeaponVisual>();
            yield return null;
            var model = visual.Current.transform;
            var basePosition = model.localPosition;

            AttackerOf(shooter).TryAttack(target);
            var kicked = Vector3.Distance(model.position, model.parent.TransformPoint(basePosition));
            Assert.That(kicked, Is.EqualTo(0.05f).Within(0.005f), "the profile's 5 cm kick, in world units");
            Assert.That(Vector3.Dot(model.position - model.parent.TransformPoint(basePosition), visual.Muzzle.forward), Is.LessThan(0f), "backwards along the barrel");

            rig.Pause.Pause();
            var frozen = model.localPosition;
            yield return new WaitForSecondsRealtime(0.4f);
            Assert.That(model.localPosition, Is.EqualTo(frozen), "recoil does not move while paused");
            rig.Pause.Resume();
            yield return new WaitForSeconds(0.5f);
            Assert.That(model.localPosition, Is.EqualTo(basePosition).Using(V3));
        }

        [UnityTest]
        public IEnumerator FogOverride_HiddenShooter_ShowsNoMuzzle_AndAFriendFiringIntoHiddenGroundShowsNoTracerOrImpact()
        {
            rig = new PresentationRig();
            var friend = rig.CreateShooter(new Vector3(-6f, 0f, 0f));
            var hidden = rig.CreateShooter(new Vector3(12f, 0f, -6f));
            var visibleTarget = rig.CreateTarget(new Vector3(-6f, 0f, 8f));
            var hiddenTarget = rig.CreateTarget(new Vector3(12f, 0f, 8f));
            yield return null;
            // x > 5 is hidden ground; units there are not observed.
            rig.Presenter.UseKnowledge(p => p.x > 5f ? PointVisibility.Hidden : PointVisibility.Visible, u => u.transform.position.x <= 5f);

            AttackerOf(hidden).TryAttack(hiddenTarget);
            Assert.That(rig.Presenter.ActiveEffects, Is.EqualTo(0), "nothing from a hidden shooter at a hidden target");

            AttackerOf(friend).TryAttack(visibleTarget);
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Muzzle), Is.EqualTo(1));
            rig.Presenter.Clear();

            // The same friend fires at a target that is itself in hidden ground: flash yes, tracer and impact no.
            var far = rig.CreateTarget(new Vector3(-6f, 0f, 20f));
            far.transform.position = new Vector3(8f, 1f, 0f);
            Physics.SyncTransforms();
            AttackerOf(friend).TryAttack(far);
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Muzzle), Is.EqualTo(1));
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Tracer), Is.EqualTo(0));
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Impact), Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator PresentationNeverMakesAnEffectAGameplayObstacle()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f));
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            AttackerOf(shooter).TryAttack(target);
            foreach (var effect in Object.FindObjectsByType<PooledEffect>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                Assert.That(effect.GetComponentsInChildren<Collider>(true), Is.Empty, effect.name);
                Assert.That(effect.GetComponentsInChildren<UnityEngine.AI.NavMeshObstacle>(true), Is.Empty, effect.name);
            }
        }
    }
}
#endif
```
- [ ] **Step 10: Run, fix, re-run**
```bash
Tools/run-tests.sh PlayMode "Blackglass.Tests.CombatPresenterPlayModeTests"
```
Expected: `EXIT=0`. Diagnose any failure with `superpowers:systematic-debugging`, not by loosening an assertion.

- [ ] **Step 11: Regression check of the suites that touch the changed classes**
```bash
Tools/run-tests.sh PlayMode "Blackglass.Tests.EffectPoolPlayModeTests"
Tools/run-tests.sh PlayMode "Blackglass.Tests.CombatRecordsPlayModeTests"
Tools/run-tests.sh PlayMode "Blackglass.Tests.CombatPlayModeTests"
```

---

### Task 8: Generated effect assets for the rifle shot (textures, materials, muzzle flash, tracer, impacts, rifle profile)

**Files:**
- Create: `Tools/run-method.sh`
- Create (Editor): `Assets/_Project/Editor/Presentation/EffectTextures.cs`, `EffectMaterials.cs`, `ParticleAuthoring.cs`, `EffectPrefabs.cs`, `CombatPresentationAssetBuilder.cs`
- Modify: `Scripts/Presentation/CombatPresentationProfile.cs` (add `EnsureImpact`, `SurfaceImpact.FillFrom`)
- Generated (committed by the owner later, not by this plan): `Assets/_Project/Presentation/{Textures,Materials,Prefabs,Profiles}/…`; modified `Assets/_Project/Data/Items/Visuals/RifleVisual.asset`, `MarksmanRifleVisual.asset` (profile reference)
- Test: `Tests/EditMode/CombatPresentationAssetTests.cs`

**Interfaces:**
- Consumes: `CombatPresentationProfile` and its internal setters, `TracerStyle.Create`, `SurfaceImpact.Create`, `PooledEffect`, `TracerView` (Tasks 3, 5).
- Produces:
  - Menu `Blackglass/Presentation/Build Effects` and batch entry `Blackglass.EditorTools.CombatPresentationAssetBuilder.BuildFromCommandLine` (idempotent: creates only what is missing; `Rebuild Effects (overwrite)` regenerates prefabs/materials in place, keeping GUIDs).
  - Assets (paths relative to `Assets/_Project/Presentation/`): `Textures/SoftCircle.png`, `Smoke.png`, `Ring.png`; `Materials/FxGlow.mat`, `FxSmoke.mat`, `FxDust.mat`, `FxRing.mat`, `FxTracer.mat`, `FxSpark.mat`, `FxChip.mat`; `Prefabs/MuzzleFlashRifle.prefab`, `TracerRifle.prefab`, `ImpactConcrete.prefab`, `ImpactMetal.prefab`, `ImpactCharacter.prefab`, `ImpactDefault.prefab`; `Profiles/RifleCombatPresentation.asset`, `DefaultCombatPresentation.asset`.
  - `internal sealed class EffectKit { Texture2D SoftCircle, SmokeMap, RingMap; Material Glow, Smoke, Dust, Ring, Tracer, Spark, Chip; static EffectKit Load(bool force) }`
  - `internal static class EffectPaths { const string Root, Textures, Materials, Prefabs, Profiles, Audio, Preview; static void EnsureFolder(string assetPath) }`
  - `CombatPresentationProfile.EnsureImpact(ImpactSurfaceKind kind, GameObject prefab, AudioCue cue)` (adds the entry, or fills a missing prefab/cue on the existing one; never overwrites a set value)

- [ ] **Step 1: Add the batch runner** — `Tools/run-method.sh`:
```bash
#!/usr/bin/env bash
# Runs one static Editor method in batch mode. The Unity Editor must be closed.
# Usage: Tools/run-method.sh Namespace.Class.Method
set -u
method="${1:?usage: Tools/run-method.sh Namespace.Class.Method}"
root="$(cd "$(dirname "$0")/.." && (pwd -W 2>/dev/null || pwd))"
unity="${UNITY_EXE:-/c/Program Files/Unity/Hub/Editor/6000.3.25f1/Editor/Unity.exe}"
mkdir -p "$root/Logs"
log="$root/Logs/Method-${method//./_}.log"
"$unity" -batchmode -quit -projectPath "$root" -executeMethod "$method" -logFile "$log"
code=$?
if grep -q "another Unity instance is running" "$log" 2>/dev/null; then
  echo "The Unity Editor has this project open. Close it and run again."
fi
grep -h "error CS" "$log" 2>/dev/null | sort -u
grep -hE "Exception" -A4 "$log" 2>/dev/null | head -40
echo "EXIT=$code"
exit $code
```

- [ ] **Step 2: Write the failing asset tests** — `Tests/EditMode/CombatPresentationAssetTests.cs`:
```csharp
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

namespace Blackglass.Tests
{
    public class CombatPresentationAssetTests
    {
        const string Root = "Assets/_Project/Presentation/";
        const string Visuals = "Assets/_Project/Data/Items/Visuals/";

        public static readonly string[] RiflePrefabs =
        {
            "MuzzleFlashRifle", "TracerRifle", "ImpactConcrete", "ImpactMetal", "ImpactCharacter", "ImpactDefault",
        };

        static T Load<T>(string path) where T : Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            Assert.That(asset, Is.Not.Null, "Missing " + path + ": run Blackglass/Presentation/Build Effects");
            return asset;
        }

        [TestCaseSource(nameof(RiflePrefabs))]
        public void EffectPrefab_IsPoolReady_NeverAGameplayObject_AndFollowsTheSimulationClock(string name)
        {
            var prefab = Load<GameObject>(Root + "Prefabs/" + name + ".prefab");
            Assert.That(prefab.GetComponent<PooledEffect>(), Is.Not.Null, "PooledEffect on the root");
            Assert.That(prefab.GetComponentsInChildren<Collider>(true), Is.Empty);
            Assert.That(prefab.GetComponentsInChildren<Rigidbody>(true), Is.Empty);
            Assert.That(prefab.GetComponentsInChildren<NavMeshObstacle>(true), Is.Empty);
            foreach (var ps in prefab.GetComponentsInChildren<ParticleSystem>(true))
            {
                Assert.That(ps.main.useUnscaledTime, Is.False, name + "/" + ps.name + " must use scaled time");
                Assert.That(ps.main.playOnAwake, Is.False, name + "/" + ps.name);
                Assert.That(ps.main.loop, Is.False, name + "/" + ps.name);
            }
            foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
            {
                Assert.That(renderer.sharedMaterial, Is.Not.Null, name + "/" + renderer.name);
                Assert.That(renderer.sharedMaterial.shader.name, Does.StartWith("Universal Render Pipeline"), name + "/" + renderer.name);
                Assert.That(renderer.shadowCastingMode, Is.EqualTo(UnityEngine.Rendering.ShadowCastingMode.Off));
            }
        }

        [Test]
        public void MuzzleFlash_HasASmokePuffAndACappedLight()
        {
            var prefab = Load<GameObject>(Root + "Prefabs/MuzzleFlashRifle.prefab");
            Assert.That(prefab.GetComponentsInChildren<ParticleSystem>(true).Select(p => p.name), Does.Contain("Smoke").And.Contain("Flash"));
            var lights = prefab.GetComponentsInChildren<Light>(true);
            Assert.That(lights.Length, Is.EqualTo(1), "one cheap muzzle light");
            Assert.That(lights[0].shadows, Is.EqualTo(LightShadows.None));
            Assert.That(lights[0].range, Is.LessThanOrEqualTo(6f));
        }

        [Test]
        public void TracerPrefab_HasALineAndATracerView()
        {
            var prefab = Load<GameObject>(Root + "Prefabs/TracerRifle.prefab");
            Assert.That(prefab.GetComponent<LineRenderer>(), Is.Not.Null);
            Assert.That(prefab.GetComponent<TracerView>(), Is.Not.Null);
        }

        [Test]
        public void EffectMaterials_AreTransparentParticleMaterials_ThatDoNotWriteDepth()
        {
            foreach (var name in new[] { "FxGlow", "FxSmoke", "FxDust", "FxRing", "FxTracer", "FxSpark", "FxChip" })
            {
                var material = Load<Material>(Root + "Materials/" + name + ".mat");
                Assert.That(material.shader.name, Is.EqualTo("Universal Render Pipeline/Particles/Unlit"), name);
                Assert.That(material.GetFloat("_Surface"), Is.EqualTo(1f), name);
                Assert.That(material.GetFloat("_ZWrite"), Is.EqualTo(0f), name);
                Assert.That(material.renderQueue, Is.GreaterThanOrEqualTo(3000), name);
                Assert.That(material.GetTexture("_BaseMap"), Is.Not.Null, name);
            }
        }

        [TestCase("RifleCombatPresentation")]
        [TestCase("DefaultCombatPresentation")]
        public void Profile_HasNoProblems(string name)
        {
            Assert.That(Load<CombatPresentationProfile>(Root + "Profiles/" + name + ".asset").Problems(), Is.Empty);
        }

        [Test]
        public void RifleProfile_ReferencesTheEffects_ForEveryRequiredSurface()
        {
            var profile = Load<CombatPresentationProfile>(Root + "Profiles/RifleCombatPresentation.asset");
            Assert.That(profile.MuzzleEffect, Is.Not.Null);
            Assert.That(profile.Tracer.IsUsable, Is.True);
            foreach (var kind in new[] { ImpactSurfaceKind.Concrete, ImpactSurfaceKind.Metal, ImpactSurfaceKind.Character, ImpactSurfaceKind.Default })
            {
                var entry = profile.ImpactFor(kind);
                Assert.That(entry, Is.Not.Null, kind.ToString());
                Assert.That(entry.Surface, Is.EqualTo(kind), kind + " has its own entry, not the default");
                Assert.That(entry.Prefab, Is.Not.Null, kind.ToString());
            }
            Assert.That(profile.RecoilKick, Is.GreaterThan(0f));
        }

        [Test]
        public void DefaultProfile_ShowsAtLeastCharacterAndDefaultImpacts()
        {
            var profile = Load<CombatPresentationProfile>(Root + "Profiles/DefaultCombatPresentation.asset");
            Assert.That(profile.ImpactFor(ImpactSurfaceKind.Character).Surface, Is.EqualTo(ImpactSurfaceKind.Character));
            Assert.That(profile.ImpactFor(ImpactSurfaceKind.Default).Surface, Is.EqualTo(ImpactSurfaceKind.Default));
        }

        [TestCase("RifleVisual")]
        [TestCase("MarksmanRifleVisual")]
        public void RifleWeaponVisuals_UseTheSharedRifleProfile(string name)
        {
            var visual = Load<WeaponVisualAsset>(Visuals + name + ".asset");
            var profile = Load<CombatPresentationProfile>(Root + "Profiles/RifleCombatPresentation.asset");
            Assert.That(visual.Presentation, Is.EqualTo(profile));
        }

        [Test]
        public void Builder_IsIdempotent_ItKeepsHandTunedValues()
        {
            var profile = Load<CombatPresentationProfile>(Root + "Profiles/RifleCombatPresentation.asset");
            var prefab = profile.MuzzleEffect;
            // The builder never replaces a reference that is already set; Task 8 step 6 re-runs it and re-checks the GUIDs.
            Assert.That(AssetDatabase.GetAssetPath(prefab), Is.EqualTo(Root + "Prefabs/MuzzleFlashRifle.prefab"));
        }
    }
}
```

- [ ] **Step 3: Run to verify failure** — `Tools/run-tests.sh EditMode "Blackglass.Tests.CombatPresentationAssetTests"` → tests fail with "Missing Assets/_Project/Presentation/…" (the project compiles; nothing generated yet).

- [ ] **Step 4: Add `EnsureImpact` to the profile** — in `CombatPresentationProfile.cs` add to `SurfaceImpact`:
```csharp
        internal void FillFrom(GameObject newPrefab, AudioCue newCue)
        {
            if (prefab == null && newPrefab != null)
                prefab = newPrefab;
            if (cue.IsEmpty && newCue != null && !newCue.IsEmpty)
                cue = newCue;
        }
```
and to `CombatPresentationProfile`:
```csharp
        /// <summary>Adds the surface's entry, or fills the missing prefab or sound of the existing one. Never overwrites a value that is set.</summary>
        internal void EnsureImpact(ImpactSurfaceKind kind, GameObject prefab, AudioCue cue)
        {
            foreach (var entry in Impacts)
            {
                if (entry != null && entry.Surface == kind)
                {
                    entry.FillFrom(prefab, cue);
                    return;
                }
            }
            var list = new List<SurfaceImpact>(Impacts) { SurfaceImpact.Create(kind, prefab, cue) };
            impacts = list.ToArray();
        }
```

- [ ] **Step 5: Implement the Editor builder files** (all `#if UNITY_EDITOR`, `namespace Blackglass.EditorTools`, in `Assets/_Project/Editor/Presentation/`).

`EffectTextures.cs`:
```csharp
#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Blackglass.EditorTools
{
    internal static class EffectPaths
    {
        public const string Root = "Assets/_Project/Presentation";
        public const string Textures = Root + "/Textures";
        public const string Materials = Root + "/Materials";
        public const string Prefabs = Root + "/Prefabs";
        public const string Profiles = Root + "/Profiles";
        public const string Audio = Root + "/Audio";
        public const string Preview = Root + "/Preview";

        public static void EnsureFolder(string assetPath)
        {
            if (AssetDatabase.IsValidFolder(assetPath))
                return;
            var parent = Path.GetDirectoryName(assetPath).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(assetPath));
        }
    }

    /// <summary>Small original particle masks generated with code: a soft disc, a smoke puff and a thin ring. Replace the PNGs to change the look.</summary>
    internal static class EffectTextures
    {
        public static Texture2D SoftCircle(bool force) => Make(EffectPaths.Textures + "/SoftCircle.png", 64, force,
            (u, v) => Mathf.Pow(1f - Mathf.SmoothStep(0f, 1f, Mathf.Sqrt(u * u + v * v)), 1.5f));

        public static Texture2D Smoke(bool force) => Make(EffectPaths.Textures + "/Smoke.png", 128, force, (u, v) =>
        {
            var disc = Mathf.Pow(1f - Mathf.SmoothStep(0f, 1f, Mathf.Sqrt(u * u + v * v)), 1.2f);
            var noise = 0.5f + 0.5f * Mathf.PerlinNoise(u * 2.3f + 5f, v * 2.3f + 9f);
            return disc * noise;
        });

        public static Texture2D Ring(bool force) => Make(EffectPaths.Textures + "/Ring.png", 128, force, (u, v) =>
        {
            var r = Mathf.Sqrt(u * u + v * v);
            if (r > 1f)
                return 0f;
            var d = (r - 0.82f) / 0.08f;
            return Mathf.Exp(-d * d);
        });

        static Texture2D Make(string path, int size, bool force, Func<float, float, float> alpha)
        {
            if (!force)
            {
                var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (existing != null)
                    return existing;
            }
            EffectPaths.EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var u = (x + 0.5f) / size * 2f - 1f;
                    var v = (y + 0.5f) / size * 2f - 1f;
                    pixels[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(alpha(u, v)));
                }
            }
            texture.SetPixels32(pixels);
            File.WriteAllBytes(path, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Default;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
    }
}
#endif
```

`EffectMaterials.cs`:
```csharp
#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Blackglass.EditorTools
{
    internal enum EffectBlend { Alpha, Additive }

    /// <summary>The shared materials every generated effect uses: URP Particles/Unlit, transparent, no depth writes, no shadows.</summary>
    internal sealed class EffectKit
    {
        public Texture2D SoftCircle;
        public Texture2D SmokeMap;
        public Texture2D RingMap;
        public Material Glow, Smoke, Dust, Ring, Tracer, Spark, Chip;

        public static EffectKit Load(bool force)
        {
            var kit = new EffectKit
            {
                SoftCircle = EffectTextures.SoftCircle(force),
                SmokeMap = EffectTextures.Smoke(force),
                RingMap = EffectTextures.Ring(force),
            };
            kit.Glow = EffectMaterials.Get("FxGlow", kit.SoftCircle, EffectBlend.Additive, Color.white, force);
            kit.Smoke = EffectMaterials.Get("FxSmoke", kit.SmokeMap, EffectBlend.Alpha, Color.white, force);
            kit.Dust = EffectMaterials.Get("FxDust", kit.SmokeMap, EffectBlend.Alpha, Color.white, force);
            kit.Ring = EffectMaterials.Get("FxRing", kit.RingMap, EffectBlend.Additive, Color.white, force);
            kit.Tracer = EffectMaterials.Get("FxTracer", kit.SoftCircle, EffectBlend.Additive, Color.white, force);
            kit.Spark = EffectMaterials.Get("FxSpark", kit.SoftCircle, EffectBlend.Additive, Color.white, force);
            kit.Chip = EffectMaterials.Get("FxChip", kit.SoftCircle, EffectBlend.Alpha, Color.white, force);
            return kit;
        }
    }

    internal static class EffectMaterials
    {
        public static Material Get(string name, Texture2D map, EffectBlend blend, Color tint, bool force)
        {
            var path = EffectPaths.Materials + "/" + name + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null && !force)
                return material;
            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null)
                throw new InvalidOperationException("The URP Particles/Unlit shader is missing.");
            EffectPaths.EnsureFolder(EffectPaths.Materials);
            if (material == null)
            {
                material = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(material, path);
            }
            material.shader = shader;
            material.SetTexture("_BaseMap", map);
            material.SetColor("_BaseColor", tint);
            Configure(material, blend);
            EditorUtility.SetDirty(material);
            return material;
        }

        static void Configure(Material m, EffectBlend blend)
        {
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", blend == EffectBlend.Additive ? 2f : 0f);
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", blend == EffectBlend.Additive ? (float)BlendMode.One : (float)BlendMode.OneMinusSrcAlpha);
            if (m.HasProperty("_SrcBlendAlpha"))
                m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            if (m.HasProperty("_DstBlendAlpha"))
                m.SetFloat("_DstBlendAlpha", blend == EffectBlend.Additive ? (float)BlendMode.One : (float)BlendMode.OneMinusSrcAlpha);
            m.SetOverrideTag("RenderType", "Transparent");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            m.renderQueue = (int)RenderQueue.Transparent;
            m.SetShaderPassEnabled("ShadowCaster", false);
        }
    }
}
#endif
```

`ParticleAuthoring.cs`:
```csharp
#if UNITY_EDITOR
using UnityEngine;

namespace Blackglass.EditorTools
{
    /// <summary>One particle system, described in plain numbers (world-unit sizes and speeds; scaled by the root's scale).</summary>
    internal sealed class PsSpec
    {
        public string Name = "Particles";
        public Material Material;
        public ParticleSystemRenderMode Render = ParticleSystemRenderMode.Billboard;
        public Vector2 Lifetime = new Vector2(0.5f, 0.5f);
        public Vector2 Size = new Vector2(0.2f, 0.2f);
        public Vector2 Speed = Vector2.zero;
        public int Burst = 1;
        public float Duration = 0.5f;
        public bool ShapeEnabled = true;
        public ParticleSystemShapeType Shape = ParticleSystemShapeType.Cone;
        public float ShapeAngle = 0f;
        public float ShapeRadius = 0.01f;
        public Color StartColor = Color.white;
        public Gradient ColorOverLifetime;
        public AnimationCurve SizeOverLifetime;
        public float Gravity;
        public float LengthScale = 2f;
        public Vector3 LocalPosition;
        public Vector3 LocalEuler;
        public bool RandomRotation = true;
    }

    internal static class ParticleAuthoring
    {
        /// <summary>Alpha 1 until `hold` (0..1 of the lifetime), then fading to 0 at the end.</summary>
        public static Gradient FadeOut(float hold = 0.2f)
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, Mathf.Clamp01(hold)), new GradientAlphaKey(0f, 1f) });
            return gradient;
        }

        /// <summary>Grows from `from` to `to` times the start size over the lifetime.</summary>
        public static AnimationCurve Grow(float from, float to) => AnimationCurve.EaseInOut(0f, from, 1f, to);

        public static ParticleSystem Build(Transform parent, PsSpec s)
        {
            var go = new GameObject(s.Name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = s.LocalPosition;
            go.transform.localEulerAngles = s.LocalEuler;
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.duration = s.Duration;
            main.startLifetime = new ParticleSystem.MinMaxCurve(s.Lifetime.x, s.Lifetime.y);
            main.startSpeed = new ParticleSystem.MinMaxCurve(s.Speed.x, s.Speed.y);
            main.startSize = new ParticleSystem.MinMaxCurve(s.Size.x, s.Size.y);
            main.startColor = s.StartColor;
            main.gravityModifier = s.Gravity;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.maxParticles = Mathf.Max(8, s.Burst * 2);
            main.useUnscaledTime = false;
            if (s.RandomRotation)
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);

            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)s.Burst) });

            var shape = ps.shape;
            shape.enabled = s.ShapeEnabled;
            shape.shapeType = s.Shape;
            shape.angle = s.ShapeAngle;
            shape.radius = s.ShapeRadius;

            if (s.ColorOverLifetime != null)
            {
                var color = ps.colorOverLifetime;
                color.enabled = true;
                color.color = new ParticleSystem.MinMaxGradient(s.ColorOverLifetime);
            }
            if (s.SizeOverLifetime != null)
            {
                var size = ps.sizeOverLifetime;
                size.enabled = true;
                size.size = new ParticleSystem.MinMaxCurve(1f, s.SizeOverLifetime);
            }

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = s.Material;
            renderer.renderMode = s.Render;
            renderer.lengthScale = s.LengthScale;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return ps;
        }
    }
}
#endif
```

`EffectPrefabs.cs` (rifle stage; later tasks add `ExplosionGrenade` and `HealPulse` to this class):
```csharp
#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace Blackglass.EditorTools
{
    /// <summary>Builds the effect prefabs: small particle systems, an optional capped light, a PooledEffect on the root. No colliders, ever.</summary>
    internal static class EffectPrefabs
    {
        public static GameObject MuzzleFlashRifle(EffectKit kit, bool force) => Build("MuzzleFlashRifle", force, root =>
        {
            SetLightSeconds(root, 0.06f);
            ParticleAuthoring.Build(root.transform, new PsSpec
            {
                Name = "Flash", Material = kit.Glow, Burst = 2, Duration = 0.1f, ShapeEnabled = false,
                Lifetime = new Vector2(0.04f, 0.06f), Size = new Vector2(0.28f, 0.4f),
                StartColor = new Color(1f, 0.85f, 0.5f, 1f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.3f),
            });
            ParticleAuthoring.Build(root.transform, new PsSpec
            {
                Name = "Cone", Material = kit.Spark, Render = ParticleSystemRenderMode.Stretch, Burst = 1, Duration = 0.1f,
                Lifetime = new Vector2(0.05f, 0.05f), Size = new Vector2(0.18f, 0.22f), Speed = new Vector2(12f, 16f),
                Shape = ParticleSystemShapeType.Cone, ShapeAngle = 6f, LengthScale = 3f,
                StartColor = new Color(1f, 0.75f, 0.35f, 0.9f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.2f),
            });
            ParticleAuthoring.Build(root.transform, new PsSpec
            {
                Name = "Smoke", Material = kit.Smoke, Burst = 3, Duration = 0.2f,
                Lifetime = new Vector2(0.7f, 1.2f), Size = new Vector2(0.08f, 0.12f), Speed = new Vector2(0.4f, 0.9f),
                Shape = ParticleSystemShapeType.Cone, ShapeAngle = 12f, Gravity = -0.05f,
                StartColor = new Color(0.72f, 0.72f, 0.72f, 0.28f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.1f),
                SizeOverLifetime = ParticleAuthoring.Grow(1f, 4f),
            });
            AddLight(root.transform, new Color(1f, 0.78f, 0.45f), range: 4f, intensity: 3f, localPosition: new Vector3(0f, 0f, 0.15f));
        });

        public static GameObject TracerRifle(EffectKit kit, bool force) => Build("TracerRifle", force, root =>
        {
            var line = root.AddComponent<LineRenderer>();
            line.sharedMaterial = kit.Tracer;
            line.positionCount = 2;
            line.useWorldSpace = true;
            line.widthMultiplier = 0.035f;
            line.numCapVertices = 2;
            line.alignment = LineAlignment.View;
            line.textureMode = LineTextureMode.Stretch;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.enabled = false;
            root.AddComponent<TracerView>();
        });

        public static GameObject ImpactConcrete(EffectKit kit, bool force) => Build("ImpactConcrete", force, root =>
        {
            ParticleAuthoring.Build(root.transform, new PsSpec
            {
                Name = "Dust", Material = kit.Dust, Burst = 6, Duration = 0.2f,
                Lifetime = new Vector2(0.5f, 0.9f), Size = new Vector2(0.15f, 0.25f), Speed = new Vector2(0.4f, 1.2f),
                Shape = ParticleSystemShapeType.Cone, ShapeAngle = 35f, ShapeRadius = 0.02f, Gravity = -0.02f,
                StartColor = new Color(0.62f, 0.6f, 0.55f, 0.35f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.1f),
                SizeOverLifetime = ParticleAuthoring.Grow(1f, 3f),
            });
            ParticleAuthoring.Build(root.transform, new PsSpec
            {
                Name = "Chips", Material = kit.Chip, Burst = 6, Duration = 0.2f,
                Lifetime = new Vector2(0.4f, 0.7f), Size = new Vector2(0.025f, 0.05f), Speed = new Vector2(2f, 4.5f),
                Shape = ParticleSystemShapeType.Cone, ShapeAngle = 40f, Gravity = 1.5f,
                StartColor = new Color(0.45f, 0.43f, 0.4f, 1f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.5f),
            });
        });

        public static GameObject ImpactMetal(EffectKit kit, bool force) => Build("ImpactMetal", force, root =>
        {
            ParticleAuthoring.Build(root.transform, new PsSpec
            {
                Name = "Sparks", Material = kit.Spark, Render = ParticleSystemRenderMode.Stretch, Burst = 9, Duration = 0.2f,
                Lifetime = new Vector2(0.2f, 0.4f), Size = new Vector2(0.03f, 0.05f), Speed = new Vector2(3f, 7f),
                Shape = ParticleSystemShapeType.Cone, ShapeAngle = 50f, LengthScale = 4f, Gravity = 1f,
                StartColor = new Color(1f, 0.8f, 0.4f, 1f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.3f),
            });
            ParticleAuthoring.Build(root.transform, new PsSpec
            {
                Name = "Flash", Material = kit.Glow, Burst = 1, Duration = 0.1f, ShapeEnabled = false,
                Lifetime = new Vector2(0.05f, 0.05f), Size = new Vector2(0.18f, 0.18f),
                StartColor = new Color(1f, 0.9f, 0.6f, 1f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.2f),
            });
        });

        public static GameObject ImpactCharacter(EffectKit kit, bool force) => Build("ImpactCharacter", force, root =>
        {
            ParticleAuthoring.Build(root.transform, new PsSpec
            {
                Name = "Puff", Material = kit.Dust, Burst = 4, Duration = 0.2f,
                Lifetime = new Vector2(0.3f, 0.5f), Size = new Vector2(0.1f, 0.16f), Speed = new Vector2(0.5f, 1.4f),
                Shape = ParticleSystemShapeType.Cone, ShapeAngle = 40f,
                StartColor = new Color(0.75f, 0.72f, 0.7f, 0.4f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.1f),
                SizeOverLifetime = ParticleAuthoring.Grow(1f, 2.5f),
            });
            ParticleAuthoring.Build(root.transform, new PsSpec
            {
                Name = "Specks", Material = kit.Chip, Burst = 4, Duration = 0.2f,
                Lifetime = new Vector2(0.25f, 0.45f), Size = new Vector2(0.02f, 0.035f), Speed = new Vector2(1.5f, 3.5f),
                Shape = ParticleSystemShapeType.Cone, ShapeAngle = 45f, Gravity = 1.2f,
                StartColor = new Color(0.8f, 0.78f, 0.75f, 1f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.4f),
            });
        });

        public static GameObject ImpactDefault(EffectKit kit, bool force) => Build("ImpactDefault", force, root =>
        {
            ParticleAuthoring.Build(root.transform, new PsSpec
            {
                Name = "Puff", Material = kit.Dust, Burst = 3, Duration = 0.2f,
                Lifetime = new Vector2(0.4f, 0.7f), Size = new Vector2(0.1f, 0.16f), Speed = new Vector2(0.3f, 0.9f),
                Shape = ParticleSystemShapeType.Cone, ShapeAngle = 35f,
                StartColor = new Color(0.7f, 0.7f, 0.68f, 0.3f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.1f),
                SizeOverLifetime = ParticleAuthoring.Grow(1f, 3f),
            });
        });

        // ---- shared helpers (also used by the explosion and heal-pulse stages) ----

        public static GameObject Build(string name, bool force, Action<GameObject> populate)
        {
            var path = EffectPaths.Prefabs + "/" + name + ".prefab";
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null && !force)
                return existing;
            EffectPaths.EnsureFolder(EffectPaths.Prefabs);
            var root = new GameObject(name);
            try
            {
                root.AddComponent<PooledEffect>();
                populate(root);
                return PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        public static void SetLightSeconds(GameObject root, float seconds)
        {
            var serialized = new SerializedObject(root.GetComponent<PooledEffect>());
            serialized.FindProperty("lightSeconds").floatValue = seconds;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        public static Light AddLight(Transform parent, Color color, float range, float intensity, Vector3 localPosition)
        {
            var child = new GameObject("Light");
            child.transform.SetParent(parent, false);
            child.transform.localPosition = localPosition;
            var light = child.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.range = range;
            light.intensity = intensity;
            light.shadows = LightShadows.None;
            light.enabled = false;
            return light;
        }
    }
}
#endif
```

`CombatPresentationAssetBuilder.cs`:
```csharp
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Blackglass.EditorTools
{
    /// <summary>
    /// Generates the combat presentation assets once: textures, materials, effect prefabs, profiles, placeholder sounds, and
    /// the references on the rifle weapon visuals and the ability assets. Idempotent: it creates only what is missing and
    /// fills only empty references, so hand tuning survives; "Rebuild" regenerates prefabs and materials in place (GUIDs kept).
    /// Nothing here runs when the game starts.
    /// </summary>
    public static class CombatPresentationAssetBuilder
    {
        const string VisualsRoot = "Assets/_Project/Data/Items/Visuals/";

        [MenuItem("Blackglass/Presentation/Build Effects")]
        public static void BuildMenu() => Build(false);

        [MenuItem("Blackglass/Presentation/Rebuild Effects (overwrite prefabs and materials)")]
        public static void RebuildMenu() => Build(true);

        /// <summary>Unity -batchmode -executeMethod entry.</summary>
        public static void BuildFromCommandLine() => Build(false);

        public static void Build(bool force)
        {
            var kit = EffectKit.Load(force);
            BuildRifleStage(kit, force);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("Combat presentation assets built (force=" + force + ").");
        }

        static CombatPresentationProfile BuildRifleStage(EffectKit kit, bool force)
        {
            var muzzle = EffectPrefabs.MuzzleFlashRifle(kit, force);
            var tracer = EffectPrefabs.TracerRifle(kit, force);
            var concrete = EffectPrefabs.ImpactConcrete(kit, force);
            var metal = EffectPrefabs.ImpactMetal(kit, force);
            var character = EffectPrefabs.ImpactCharacter(kit, force);
            var fallback = EffectPrefabs.ImpactDefault(kit, force);

            var rifle = LoadOrCreateProfile("RifleCombatPresentation");
            if (rifle.MuzzleEffect == null)
                rifle.SetMuzzle(muzzle, 1f);
            if (!rifle.Tracer.IsUsable)
                rifle.SetTracer(TracerStyle.Create(tracer, 0.035f, 6f, 300f));
            rifle.EnsureImpact(ImpactSurfaceKind.Concrete, concrete, null);
            rifle.EnsureImpact(ImpactSurfaceKind.Metal, metal, null);
            rifle.EnsureImpact(ImpactSurfaceKind.Character, character, null);
            rifle.EnsureImpact(ImpactSurfaceKind.Default, fallback, null);
            EditorUtility.SetDirty(rifle);

            var defaults = LoadOrCreateProfile("DefaultCombatPresentation");
            defaults.EnsureImpact(ImpactSurfaceKind.Character, character, null);
            defaults.EnsureImpact(ImpactSurfaceKind.Default, fallback, null);
            EditorUtility.SetDirty(defaults);

            GiveRifleProfile("RifleVisual", rifle);
            GiveRifleProfile("MarksmanRifleVisual", rifle);
            return rifle;
        }

        static void GiveRifleProfile(string visualName, CombatPresentationProfile profile)
        {
            var visual = AssetDatabase.LoadAssetAtPath<WeaponVisualAsset>(VisualsRoot + visualName + ".asset");
            if (visual == null)
                throw new System.InvalidOperationException("Missing weapon visual " + visualName + ".");
            if (visual.Presentation != null)
                return;
            visual.SetPresentation(profile);
            EditorUtility.SetDirty(visual);
        }

        internal static CombatPresentationProfile LoadOrCreateProfile(string name)
        {
            var path = EffectPaths.Profiles + "/" + name + ".asset";
            var profile = AssetDatabase.LoadAssetAtPath<CombatPresentationProfile>(path);
            if (profile != null)
                return profile;
            EffectPaths.EnsureFolder(EffectPaths.Profiles);
            profile = ScriptableObject.CreateInstance<CombatPresentationProfile>();
            AssetDatabase.CreateAsset(profile, path);
            return profile;
        }
    }
}
#endif
```
(`BuildRifleStage` returns the rifle profile so that the later stages can extend it.)

- [ ] **Step 6: Run the builder twice and check idempotence**
```bash
Tools/run-method.sh Blackglass.EditorTools.CombatPresentationAssetBuilder.BuildFromCommandLine
git status --short Assets/_Project/Presentation | head -30
Tools/run-method.sh Blackglass.EditorTools.CombatPresentationAssetBuilder.BuildFromCommandLine
```
Expected: both `EXIT=0`; the first creates `Assets/_Project/Presentation/…` (and modifies the two weapon-visual assets); after the second run `git diff --stat` shows no further change (no GUID churn). If a material shows magenta, the URP shader keyword/property names differ in 17.3: inspect `Shader.Find("Universal Render Pipeline/Particles/Unlit")` properties with `ShaderUtil.GetPropertyName` and correct `Configure`; do not change the test.

- [ ] **Step 7: Run the asset tests** — `Tools/run-tests.sh EditMode "Blackglass.Tests.CombatPresentationAssetTests"`. Expected `EXIT=0`.

- [ ] **Step 8: Regression** — the weapon visual assets changed: `Tools/run-tests.sh PlayMode "Blackglass.Tests.WeaponVisualPlayModeTests"` and `Tools/run-tests.sh EditMode "Blackglass.Tests.ItemCatalogueTests"` (or the existing item/inventory data tests; find them with `ls Assets/_Project/Tests/EditMode | grep -i item`). Expected `EXIT=0`.

---

### Task 9: Presentation Test Scene, preview panel and the first rendered frames (Milestone A check)

**Files:**
- Create: `Scripts/Presentation/PresentationPreviewPanel.cs`
- Create (Editor): `Assets/_Project/Editor/Presentation/CombatPresentationSceneBuilder.cs`
- Generated: `Assets/_Project/Scenes/Presentation Test Scene.unity`
- Test: `Tests/PlayMode/PresentationPreviewScenePlayModeTests.cs`, `Tests/PlayMode/PresentationRenderCaptureTests.cs`

**Interfaces:**
- Consumes: Tasks 1–8.
- Produces:
  - `PresentationPreviewPanel : MonoBehaviour` with public methods (also what the IMGUI buttons call): `FireAtCharacter()`, `FireAtConcrete()`, `FireAtMetal()`, `FireShotPastTarget()`, `FireRightShooter()`, `Stress()`, `TogglePause()`, `ToggleFogSimulation()`, `TogglePresentation()`, `CycleQuality()`, `bool FogSimulated`. (Blast, heal and shake are added in Tasks 11–13 by extending the same class.)
  - Menu `Blackglass/Presentation/Build Preview Scene` and batch entry `Blackglass.EditorTools.CombatPresentationSceneBuilder.BuildPreviewFromCommandLine`.
  - Scene objects (names the tests rely on): `Systems` (TacticalPause, CombatPresenter, PresentationPreviewPanel), `CameraRig` (TacticalCameraController, CameraImpulse) with child `Camera` (Camera, AudioListener, tag MainCamera), `Shooter`, `ShooterRight`, `Target`, `TargetRight`, `ConcreteBlock`, `MetalBlock`, `Floor`.

- [ ] **Step 1: Confirm the scene wiring seam** — `UnitPresentation.presenter` is declared `[SerializeField]` (Task 7) so a scene can reference the presenter without code; the preview builder below sets it through `SerializedObject`. Nothing to change if Task 7 was followed.

- [ ] **Step 2: Write the failing scene tests** — `Tests/PlayMode/PresentationPreviewScenePlayModeTests.cs`:
```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class PresentationPreviewScenePlayModeTests
    {
        public const string ScenePath = "Assets/_Project/Scenes/Presentation Test Scene.unity";
        Scene scene;

        [UnitySetUp]
        public IEnumerator Load()
        {
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            scene = SceneManager.GetSceneByPath(ScenePath);
            Assert.That(scene.isLoaded, Is.True);
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator Unload()
        {
            Time.timeScale = 1f;
            if (scene.isLoaded)
                yield return SceneManager.UnloadSceneAsync(scene);
        }

        T Find<T>() where T : Component =>
            scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<T>(true)).First();

        [UnityTest]
        public IEnumerator Scene_HasOneListener_NoMissionAndNoCampaignSystems()
        {
            var listeners = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<AudioListener>(true)).ToArray();
            Assert.That(listeners.Length, Is.EqualTo(1));
            Assert.That(scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<MissionDirector>(true)), Is.Empty);
            Assert.That(scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<SquadInventory>(true)), Is.Empty);
            yield return null;
        }

        [UnityTest]
        public IEnumerator FireAtCharacter_UsesTheRealAttackPath_AndShowsMuzzleTracerAndImpact()
        {
            var panel = Find<PresentationPreviewPanel>();
            var presenter = Find<CombatPresenter>();
            var target = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Health>(true)).First(h => h.name == "Target");
            var before = target.Current;
            panel.FireAtCharacter();
            Assert.That(presenter.ActiveOf(EffectCategory.Muzzle), Is.EqualTo(1));
            Assert.That(presenter.ActiveOf(EffectCategory.Tracer), Is.EqualTo(1));
            Assert.That(presenter.ActiveOf(EffectCategory.Impact), Is.EqualTo(1));
            Assert.That(target.Current, Is.LessThan(before), "the shot is a real attack on a scene dummy");
            yield return null;
        }

        [UnityTest]
        public IEnumerator FireAtConcrete_AndMetal_ShowDifferentImpacts()
        {
            var panel = Find<PresentationPreviewPanel>();
            var presenter = Find<CombatPresenter>();
            panel.FireAtConcrete();
            var concrete = Object.FindObjectsByType<PooledEffect>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .First(e => e.Category == EffectCategory.Impact).name;
            presenter.Clear();
            panel.FireAtMetal();
            var metal = Object.FindObjectsByType<PooledEffect>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .First(e => e.Category == EffectCategory.Impact).name;
            Assert.That(concrete, Is.EqualTo("ImpactConcrete"));
            Assert.That(metal, Is.EqualTo("ImpactMetal"));
            yield return null;
        }

        [UnityTest]
        public IEnumerator ShotPastTarget_ShowsATracerButNoCharacterImpact()
        {
            var panel = Find<PresentationPreviewPanel>();
            var presenter = Find<CombatPresenter>();
            panel.FireShotPastTarget();
            Assert.That(presenter.ActiveOf(EffectCategory.Tracer), Is.EqualTo(1));
            Assert.That(Object.FindObjectsByType<PooledEffect>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .Any(e => e.Category == EffectCategory.Impact && e.name == "ImpactCharacter"), Is.False);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Pause_FreezesTheEffects_AndRepeatedPauseResumeLeavesNoDuplicates()
        {
            var panel = Find<PresentationPreviewPanel>();
            var presenter = Find<CombatPresenter>();
            panel.FireAtCharacter();
            var count = presenter.ActiveEffects;
            var muzzle = Object.FindObjectsByType<PooledEffect>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .First(e => e.Category == EffectCategory.Muzzle);
            var ps = muzzle.Particles.First(p => p.name == "Smoke");
            yield return new WaitForSeconds(0.1f);
            panel.TogglePause();
            var time = ps.time;
            yield return new WaitForSecondsRealtime(0.4f);
            Assert.That(ps.time, Is.EqualTo(time).Within(0.02f));
            for (var i = 0; i < 6; i++)
                panel.TogglePause();
            Assert.That(presenter.ActiveEffects, Is.EqualTo(count), "pause and resume create nothing");
            Time.timeScale = 1f;
        }

        [UnityTest]
        public IEnumerator FogSimulation_HidesTheRightHalf_AndPresentationOffShowsNothing()
        {
            var panel = Find<PresentationPreviewPanel>();
            var presenter = Find<CombatPresenter>();
            panel.ToggleFogSimulation();
            Assert.That(panel.FogSimulated, Is.True);
            panel.FireRightShooter();
            Assert.That(presenter.ActiveEffects, Is.EqualTo(0), "a shooter in hidden ground shows nothing");
            panel.FireAtCharacter();
            Assert.That(presenter.ActiveOf(EffectCategory.Muzzle), Is.EqualTo(1), "the visible half still works");
            presenter.Clear();
            panel.ToggleFogSimulation();
            panel.TogglePresentation();
            panel.FireAtCharacter();
            Assert.That(presenter.ActiveEffects, Is.EqualTo(0));
            yield return null;
        }

        [UnityTest]
        public IEnumerator Stress_StaysInsideTheBudgets_AndDoesNotThrow()
        {
            var panel = Find<PresentationPreviewPanel>();
            var presenter = Find<CombatPresenter>();
            panel.Stress();
            foreach (EffectCategory category in System.Enum.GetValues(typeof(EffectCategory)))
                Assert.That(presenter.ActiveOf(category), Is.LessThanOrEqualTo(presenter.Budget.Cap(category)), category.ToString());
            Assert.That(presenter.Budget.LightsActive, Is.LessThanOrEqualTo(presenter.Budget.LightCap));
            yield return null;
        }
    }
}
#endif
```

- [ ] **Step 3: Run to verify failure** — `Tools/run-tests.sh PlayMode "Blackglass.Tests.PresentationPreviewScenePlayModeTests"` → `EXIT=1` (`PresentationPreviewPanel` missing).

- [ ] **Step 4: Implement `PresentationPreviewPanel.cs`**
```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Developer preview for the Presentation Test Scene only. Every button drives the real gameplay path on scene dummies
    /// (a real UnitAttacker, a real Health, the real TacticalPause) or, for surface impacts, hands the same presentation path
    /// a synthetic shot record. It never touches missions, XP, inventory or operatives. The "fog" switch only ever adds a
    /// restriction (the right half of the arena becomes hidden ground); nothing here can widen what normal play shows.
    /// </summary>
    public sealed class PresentationPreviewPanel : MonoBehaviour
    {
        [SerializeField] CombatPresenter presenter;
        [SerializeField] TacticalPause pause;
        [SerializeField] UnitAttacker shooter;
        [SerializeField] UnitAttacker shooterRight;
        [SerializeField] Health target;
        [SerializeField] Health targetRight;
        [SerializeField] Transform concreteBlock;
        [SerializeField] Transform metalBlock;

        int sequence;
        bool fogSimulated;

        public bool FogSimulated => fogSimulated;

        public void FireAtCharacter() => shooter.TryAttack(target);

        public void FireRightShooter() => shooterRight.TryAttack(targetRight);

        public void FireAtConcrete() => FireAtSurface(concreteBlock);

        public void FireAtMetal() => FireAtSurface(metalBlock);

        // A synthetic cover-miss record aimed just behind the block: the shot geometry finds the block's own surface.
        void FireAtSurface(Transform block)
        {
            var presentation = shooter.GetComponent<UnitPresentation>();
            var origin = shooter.transform.position + Vector3.up * LineOfSight.EyeHeight;
            var behind = block.position + Vector3.forward * (block.localScale.z * 0.5f + 3f);
            var record = new ShotRecord(GetInstanceID(), ++sequence, shooter.GetComponent<Health>(), null, origin,
                new Vector3(block.position.x, 1.3f, behind.z), ShotOutcome.CoverMiss, false, null);
            presentation.Deliver(record);
        }

        public void FireShotPastTarget()
        {
            var presentation = shooter.GetComponent<UnitPresentation>();
            var origin = shooter.transform.position + Vector3.up * LineOfSight.EyeHeight;
            var record = new ShotRecord(GetInstanceID(), ++sequence, shooter.GetComponent<Health>(), target, origin,
                target.transform.position + Vector3.up * ShotRecord.BodyHeight, ShotOutcome.CoverMiss, false, null);
            presentation.Deliver(record);
        }

        public void Stress()
        {
            for (var i = 0; i < 40; i++)
            {
                shooter.TryAttack(target);
                shooterRight.TryAttack(targetRight);
            }
        }

        public void TogglePause() => pause.Toggle();

        public void ToggleFogSimulation()
        {
            fogSimulated = !fogSimulated;
            if (fogSimulated)
                presenter.UseKnowledge(p => p.x > 0f ? PointVisibility.Hidden : PointVisibility.Visible, u => u.transform.position.x <= 0f);
            else
                presenter.ClearKnowledgeOverride();
            presenter.Regate();
        }

        public void TogglePresentation() => presenter.Enabled = !presenter.Enabled;

        public void CycleQuality()
        {
            var next = (PresentationQuality)(((int)presenter.Settings.quality + 1) % 3);
            presenter.SetQuality(next);
        }

        void OnGUI()
        {
            GUILayout.BeginArea(new Rect(10f, 10f, 280f, Screen.height - 20f), GUI.skin.box);
            GUILayout.Label("Combat presentation preview (scene dummies only)");
            if (GUILayout.Button("Fire at character (real attack)")) FireAtCharacter();
            if (GUILayout.Button("Fire at concrete block")) FireAtConcrete();
            if (GUILayout.Button("Fire at metal block")) FireAtMetal();
            if (GUILayout.Button("Shot past the target (cover miss)")) FireShotPastTarget();
            if (GUILayout.Button("Fire from the right shooter")) FireRightShooter();
            if (GUILayout.Button("Stress: 40 shots x2")) Stress();
            GUILayout.Space(6f);
            if (GUILayout.Button(pause.IsPaused ? "Resume" : "Tactical pause")) TogglePause();
            if (GUILayout.Button(fogSimulated ? "Fog simulation: ON (right half hidden)" : "Fog simulation: off")) ToggleFogSimulation();
            if (GUILayout.Button(presenter.Enabled ? "Presentation: ON" : "Presentation: OFF")) TogglePresentation();
            if (GUILayout.Button("Quality: " + presenter.Settings.quality)) CycleQuality();
            GUILayout.Label("Camera shake " + presenter.Settings.cameraShake.ToString("0.00"));
            presenter.Settings.cameraShake = GUILayout.HorizontalSlider(presenter.Settings.cameraShake, 0f, 1f);
            GUILayout.Label($"Active effects {presenter.ActiveEffects}  lights {presenter.Budget.LightsActive}  refused {presenter.Budget.Refused}");
            GUILayout.EndArea();
        }
    }
}
```

- [ ] **Step 5: Implement `CombatPresentationSceneBuilder.cs`** (preview scene now; Task 14 adds the ProceduralMission wiring to the same class)
```csharp
#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Blackglass.EditorTools
{
    /// <summary>Builds the isolated Presentation Test Scene, and wires the presenter into the ProceduralMission scene.</summary>
    public static class CombatPresentationSceneBuilder
    {
        public const string PreviewScenePath = "Assets/_Project/Scenes/Presentation Test Scene.unity";
        const string ProfilesRoot = "Assets/_Project/Presentation/Profiles/";
        const string VisualsRoot = "Assets/_Project/Data/Items/Visuals/";
        const string MaterialsRoot = "Assets/_Project/Materials/";

        [MenuItem("Blackglass/Presentation/Build Preview Scene")]
        public static void BuildPreviewMenu() => BuildPreview();

        /// <summary>Unity -batchmode -executeMethod entry.</summary>
        public static void BuildPreviewFromCommandLine() => BuildPreview();

        public static void BuildPreview()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            Block("Floor", new Vector3(0f, -0.1f, 0f), new Vector3(60f, 0.2f, 60f), ImpactSurfaceKind.Concrete, MaterialsRoot + "Ground.mat");
            var concrete = Block("ConcreteBlock", new Vector3(-5f, 1f, 8f), new Vector3(4f, 2f, 0.8f), ImpactSurfaceKind.Concrete, MaterialsRoot + "Obstacle.mat");
            var metal = Block("MetalBlock", new Vector3(5f, 1f, 8f), new Vector3(3f, 2f, 0.8f), ImpactSurfaceKind.Metal, MaterialsRoot + "Unit.mat");

            var systems = new GameObject("Systems");
            var pause = systems.AddComponent<TacticalPause>();
            var presenter = systems.AddComponent<CombatPresenter>();

            var rig = new GameObject("CameraRig");
            rig.transform.position = new Vector3(0f, 0f, 2f);
            var cameraObject = new GameObject("Camera") { tag = "MainCamera" };
            cameraObject.transform.SetParent(rig.transform, false);
            var camera = cameraObject.AddComponent<Camera>();
            cameraObject.AddComponent<AudioListener>();
            var controller = rig.AddComponent<TacticalCameraController>();
            var impulse = rig.AddComponent<CameraImpulse>();
            var controllerObject = new SerializedObject(controller);
            controllerObject.FindProperty("viewCamera").objectReferenceValue = camera;
            controllerObject.ApplyModifiedPropertiesWithoutUndo();

            var rifle = Load<WeaponVisualAsset>(VisualsRoot + "RifleVisual.asset");
            var shooter = Unit("Shooter", new Vector3(-3f, 1f, -6f), rifle, presenter, ranged: true);
            var shooterRight = Unit("ShooterRight", new Vector3(6f, 1f, -6f), rifle, presenter, ranged: true);
            var target = Unit("Target", new Vector3(-3f, 1f, 4f), null, presenter, ranged: false);
            var targetRight = Unit("TargetRight", new Vector3(6f, 1f, 4f), null, presenter, ranged: false);

            var presenterObject = new SerializedObject(presenter);
            presenterObject.FindProperty("pause").objectReferenceValue = pause;
            presenterObject.FindProperty("cameraImpulse").objectReferenceValue = impulse;
            presenterObject.FindProperty("fallbackProfile").objectReferenceValue = Load<CombatPresentationProfile>(ProfilesRoot + "DefaultCombatPresentation.asset");
            presenterObject.FindProperty("supportProfile").objectReferenceValue = Load<CombatPresentationProfile>(ProfilesRoot + "DefaultCombatPresentation.asset");
            presenterObject.ApplyModifiedPropertiesWithoutUndo();

            var panel = systems.AddComponent<PresentationPreviewPanel>();
            var panelObject = new SerializedObject(panel);
            Set(panelObject, "presenter", presenter);
            Set(panelObject, "pause", pause);
            Set(panelObject, "shooter", shooter.GetComponent<UnitAttacker>());
            Set(panelObject, "shooterRight", shooterRight.GetComponent<UnitAttacker>());
            Set(panelObject, "target", target.GetComponent<Health>());
            Set(panelObject, "targetRight", targetRight.GetComponent<Health>());
            Set(panelObject, "concreteBlock", concrete.transform);
            Set(panelObject, "metalBlock", metal.transform);
            panelObject.ApplyModifiedPropertiesWithoutUndo();

            if (!EditorSceneManager.SaveScene(scene, PreviewScenePath))
                throw new InvalidOperationException("Could not save " + PreviewScenePath + ".");
        }

        static GameObject Block(string name, Vector3 position, Vector3 scale, ImpactSurfaceKind surface, string materialPath)
        {
            var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
            block.name = name;
            block.transform.position = position;
            block.transform.localScale = scale;
            block.AddComponent<ImpactSurface>().Set(surface);
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material != null)
                block.GetComponent<Renderer>().sharedMaterial = material;
            return block;
        }

        // A scene dummy: a capsule with Health, a (ranged) attacker, and, for shooters, a WeaponSocket + the rifle visual.
        static GameObject Unit(string name, Vector3 position, WeaponVisualAsset weapon, CombatPresenter presenter, bool ranged)
        {
            var unit = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            unit.name = name;
            unit.transform.position = position;
            unit.AddComponent<Health>().Initialize(100000);
            unit.AddComponent<UnitAttacker>().Initialize(ranged ? 40f : 2f, 5, 0f, ranged ? CombatRole.Ranged : CombatRole.Melee);
            if (weapon != null)
            {
                var socket = new GameObject(UnitWeaponVisual.SocketName);
                socket.transform.SetParent(unit.transform, false);
                socket.transform.localPosition = new Vector3(0.3f, 0.5f, 0.4f);
                var visual = unit.AddComponent<UnitWeaponVisual>();
                var visualObject = new SerializedObject(visual);
                visualObject.FindProperty("defaultVisual").objectReferenceValue = weapon;
                visualObject.ApplyModifiedPropertiesWithoutUndo();
            }
            var presentation = unit.AddComponent<UnitPresentation>();
            var presentationObject = new SerializedObject(presentation);
            presentationObject.FindProperty("presenter").objectReferenceValue = presenter;
            presentationObject.ApplyModifiedPropertiesWithoutUndo();
            return unit;
        }

        static T Load<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
                throw new InvalidOperationException("Missing asset " + path + ". Run Blackglass/Presentation/Build Effects first.");
            return asset;
        }

        static void Set(SerializedObject serialized, string field, UnityEngine.Object value)
        {
            var property = serialized.FindProperty(field);
            if (property == null)
                throw new InvalidOperationException($"No serialized field '{field}' on {serialized.targetObject.name}.");
            property.objectReferenceValue = value;
        }
    }
}
#endif
```
(Capsule dummies keep their primitive `CapsuleCollider`; their pivot is the capsule centre, 1 m up, matching the real units, so `ShotRecord.BodyHeight` aims at the chest. The shooter's `UnitWeaponVisual.defaultVisual` is placed in `Awake`, so the muzzle exists when the scene runs.)

- [ ] **Step 6: Build the scene and run the scene tests**
```bash
Tools/run-method.sh Blackglass.EditorTools.CombatPresentationSceneBuilder.BuildPreviewFromCommandLine
Tools/run-tests.sh PlayMode "Blackglass.Tests.PresentationPreviewScenePlayModeTests"
```
Expected: `EXIT=0`. (If the `TacticalCameraController` throws without input actions, check that `MissionRig`'s `withCamera` path also passes nulls; it does, so the controller tolerates null actions.)

- [ ] **Step 7: Write the render capture tests** — `Tests/PlayMode/PresentationRenderCaptureTests.cs`. They render frames from the scene's gameplay camera to PNGs for the report, and fail only if the frame is blank or the file cannot be written. If the editor has no GPU the tests are ignored and the report says "not rendered".
```csharp
#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    [Category("Render")]
    public class PresentationRenderCaptureTests
    {
        Scene scene;
        string outputDirectory;

        [UnitySetUp]
        public IEnumerator Load()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("No graphics device: frames cannot be rendered in this environment.");
            outputDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".claude", "scratchpad", "phase125", "renders"));
            Directory.CreateDirectory(outputDirectory);
            Time.captureFramerate = 60;   // deterministic 1/60 s frames
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(PresentationPreviewScenePlayModeTests.ScenePath,
                new LoadSceneParameters(LoadSceneMode.Additive));
            scene = SceneManager.GetSceneByPath(PresentationPreviewScenePlayModeTests.ScenePath);
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator Unload()
        {
            Time.captureFramerate = 0;
            Time.timeScale = 1f;
            if (scene.isLoaded)
                yield return SceneManager.UnloadSceneAsync(scene);
        }

        T Find<T>() where T : Component =>
            scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<T>(true)).First();

        // Renders the scene camera into a PNG and returns the file's non-uniformity (0 = a flat colour).
        float Capture(string name, int width = 1280, int height = 720)
        {
            var camera = Find<Camera>();
            var texture = new RenderTexture(width, height, 24);
            var previous = camera.targetTexture;
            camera.targetTexture = texture;
            camera.Render();
            RenderTexture.active = texture;
            var image = new Texture2D(width, height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            image.Apply();
            RenderTexture.active = null;
            camera.targetTexture = previous;
            File.WriteAllBytes(Path.Combine(outputDirectory, name + ".png"), image.EncodeToPNG());
            var pixels = image.GetPixels32();
            var min = 255;
            var max = 0;
            foreach (var p in pixels)
            {
                var v = (p.r + p.g + p.b) / 3;
                min = Mathf.Min(min, v);
                max = Mathf.Max(max, v);
            }
            Object.Destroy(image);
            texture.Release();
            return max - min;
        }

        // Pose the camera for a close look, bypassing the rig's own placement.
        void CloseUp(Vector3 position, Vector3 lookAt)
        {
            Find<TacticalCameraController>().enabled = false;
            var camera = Find<Camera>();
            camera.transform.position = position;
            camera.transform.LookAt(lookAt);
        }

        [UnityTest]
        public IEnumerator GameplayCamera_RifleShot_AtTheNormalZoom()
        {
            Find<PresentationPreviewPanel>().FireAtCharacter();
            yield return null; yield return null;
            Assert.That(Capture("gameplay_rifle_hit"), Is.GreaterThan(20f), "the frame is not blank");
        }

        [UnityTest]
        public IEnumerator CloseUp_MuzzleFlashAndTracer()
        {
            CloseUp(new Vector3(-6f, 3f, -9f), new Vector3(-3f, 1.2f, -4f));
            Find<PresentationPreviewPanel>().FireAtCharacter();
            yield return null; yield return null;
            Assert.That(Capture("closeup_muzzle_tracer"), Is.GreaterThan(20f));
        }

        [UnityTest]
        public IEnumerator CloseUp_ConcreteAndMetalImpacts()
        {
            CloseUp(new Vector3(0f, 3f, 3f), new Vector3(0f, 1.2f, 8f));
            var panel = Find<PresentationPreviewPanel>();
            panel.FireAtConcrete();
            panel.FireAtMetal();
            yield return null; yield return null; yield return null;
            Assert.That(Capture("closeup_impacts"), Is.GreaterThan(20f));
        }

        [UnityTest]
        public IEnumerator CloseUp_CharacterHit()
        {
            CloseUp(new Vector3(-6f, 2f, 0f), new Vector3(-3f, 1.3f, 4f));
            Find<PresentationPreviewPanel>().FireAtCharacter();
            yield return null; yield return null; yield return null;
            Assert.That(Capture("closeup_character_hit"), Is.GreaterThan(20f));
        }
    }
}
#endif
```

- [ ] **Step 8: Run the capture tests and LOOK at the frames**
```bash
Tools/run-tests.sh PlayMode "Blackglass.Tests.PresentationRenderCaptureTests"
```
Expected: `EXIT=0` (or all ignored, in which case record "not rendered" and continue). Then open each PNG in `.claude/scratchpad/phase125/renders/` with the Read tool and judge it honestly:
- the muzzle flash is small, warm and short; the smoke is faint grey, not a cloud;
- the tracer is a thin streak, not a laser beam;
- concrete shows grey dust, metal shows orange sparks, the character gets a small neutral puff;
- nothing covers the playfield at the gameplay zoom.
Tune the generated numbers in `EffectPrefabs.cs` (sizes, alpha, counts) and `Rebuild Effects (overwrite)` until they read well, re-capturing after each change. Record what you saw and changed in `.claude/scratchpad/phase125/progress.md`. Never claim the effects look right without having looked at the frames.

- [ ] **Step 9: Milestone A gate** — run the complete EditMode and PlayMode suites once; both must be green and the totals must equal the Task 0 baselines plus the tests added so far.
```bash
Tools/run-tests.sh EditMode
Tools/run-tests.sh PlayMode
```

---

### Task 10: Character feedback — hit flash that restores the team tint, a default burst, a death sound hook, Fire on Aimed Shot

**Files:**
- Modify: `Scripts/Presentation/UnitPresentation.cs`, `Scripts/Units/UnitAnimationDriver.cs`
- Test: `Tests/PlayMode/UnitFeedbackPlayModeTests.cs`, `Tests/PlayMode/AimedShotAnimationPlayModeTests.cs`

**Interfaces:**
- Consumes: `Health.Damaged/Died/Healed`, `UnitTeamTint` (`TeamColor`, `Apply()`), `CombatPresenter.Observes/ConsumeCharacterImpact/ShowCharacterBurst/ShowDeath/ShowPulse` (Task 7).
- Produces: `UnitPresentation` now also flashes its skinned bodies on damage, shows one default burst for damage that no shot already depicted, plays the death cue and (Task 13) the heal pulse; `UnitAnimationDriver` also raises the `Fire` trigger when the unit's Aimed Shot resolves.

- [ ] **Step 1: Write the failing tests** — `Tests/PlayMode/UnitFeedbackPlayModeTests.cs`:
```csharp
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class UnitFeedbackPlayModeTests
    {
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        PresentationRig rig;
        Material shared;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            if (shared != null)
                Object.DestroyImmediate(shared);
        }

        // A target with a skinned body (no mesh needed for property blocks) and an optional team tint.
        Health BodyTarget(Vector3 ground, Color? team, out SkinnedMeshRenderer body)
        {
            var target = rig.CreateTarget(ground);
            var bodyObject = new GameObject("Body");
            bodyObject.transform.SetParent(target.transform, false);
            body = bodyObject.AddComponent<SkinnedMeshRenderer>();
            shared = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            shared.SetColor(BaseColorId, Color.white);
            body.sharedMaterial = shared;
            if (team.HasValue)
                target.gameObject.AddComponent<UnitTeamTint>().SetTeamColor(team.Value);
            // The presentation caches bodies when it subscribes; re-enable so it sees the new child.
            var presentation = target.GetComponent<UnitPresentation>();
            presentation.enabled = false;
            presentation.enabled = true;
            return target;
        }

        static Color BlockColor(Renderer renderer)
        {
            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block);
            return block.GetColor(BaseColorId);
        }

        [UnityTest]
        public IEnumerator Damage_FlashesTheBody_ThenRestoresTheExactTeamTint_WithoutTouchingTheSharedMaterial()
        {
            rig = new PresentationRig();
            var team = new Color(0.5f, 0.6f, 1f, 1f);
            var target = BodyTarget(new Vector3(0f, 0f, 4f), team, out var body);
            yield return null;

            target.TakeDamage(10);
            Assert.That(BlockColor(body).r, Is.GreaterThan(team.r), "brighter than the team colour while flashing");
            yield return new WaitForSeconds(0.3f);
            var restored = BlockColor(body);
            Assert.That(restored.r, Is.EqualTo(team.r).Within(1e-4f));
            Assert.That(restored.g, Is.EqualTo(team.g).Within(1e-4f));
            Assert.That(restored.b, Is.EqualTo(team.b).Within(1e-4f));
            Assert.That(shared.GetColor(BaseColorId), Is.EqualTo(Color.white), "the shared material is never modified");
        }

        [UnityTest]
        public IEnumerator WithoutATeamTint_TheFlashClearsItsPropertyBlock()
        {
            rig = new PresentationRig();
            var target = BodyTarget(new Vector3(0f, 0f, 4f), null, out var body);
            yield return null;
            target.TakeDamage(10);
            Assert.That(body.HasPropertyBlock(), Is.True);
            yield return new WaitForSeconds(0.3f);
            Assert.That(body.HasPropertyBlock(), Is.False, "back to the authored look");
        }

        [UnityTest]
        public IEnumerator Flash_FreezesWhilePaused()
        {
            rig = new PresentationRig();
            var target = BodyTarget(new Vector3(0f, 0f, 4f), Color.white, out var body);
            yield return null;
            target.TakeDamage(10);
            rig.Pause.Pause();
            var frozen = BlockColor(body);
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.That(BlockColor(body), Is.EqualTo(frozen));
            rig.Pause.Resume();
            yield return new WaitForSeconds(0.3f);
            Assert.That(BlockColor(body).r, Is.EqualTo(1f).Within(1e-4f));
        }

        [UnityTest]
        public IEnumerator KillingBlow_NeverLeavesTheBodyStuckBright()
        {
            rig = new PresentationRig();
            var target = BodyTarget(new Vector3(0f, 0f, 4f), new Color(0.5f, 0.6f, 1f, 1f), out var body);
            yield return null;
            target.TakeDamage(target.Max);
            Assert.That(target.IsAlive, Is.False);
            yield return null;
            Assert.That(BlockColor(body).r, Is.EqualTo(0.5f).Within(1e-4f), "restored when the unit deactivated");
        }

        [UnityTest]
        public IEnumerator HiddenVictim_ShowsNoFlashAndNoBurst()
        {
            rig = new PresentationRig();
            var target = BodyTarget(new Vector3(0f, 0f, 4f), null, out var body);
            yield return null;
            rig.Presenter.UseKnowledge(_ => PointVisibility.Visible, _ => false);
            target.TakeDamage(10);
            yield return null;
            Assert.That(body.HasPropertyBlock(), Is.False);
            Assert.That(rig.Presenter.ActiveEffects, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator DamageWithNoShot_ShowsExactlyOneDefaultBurst()
        {
            rig = new PresentationRig();
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            target.TakeDamage(10);
            yield return null;
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Impact), Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ARifleHit_ShowsOneCharacterImpact_NotTwo()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f));
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            shooter.GetComponent<UnitAttacker>().TryAttack(target);
            yield return null;
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Impact), Is.EqualTo(1), "the shot's impact covers the damage");
        }

        [UnityTest]
        public IEnumerator PresentationOff_NoFlashNoBurst_AndHealthIsUnchanged()
        {
            rig = new PresentationRig();
            var target = BodyTarget(new Vector3(0f, 0f, 4f), null, out var body);
            yield return null;
            rig.Presenter.Enabled = false;
            target.TakeDamage(10);
            yield return null;
            Assert.That(body.HasPropertyBlock(), Is.False);
            Assert.That(rig.Presenter.ActiveEffects, Is.EqualTo(0));
            Assert.That(target.Current, Is.EqualTo(target.Max - 10));
        }
    }
}
#endif
```

`Tests/PlayMode/AimedShotAnimationPlayModeTests.cs`:
```csharp
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class AimedShotAnimationPlayModeTests
    {
        TestWorld world;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            world.CreateEnvironment();
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        [UnityTest]
        public IEnumerator AimedShot_PlaysTheFireClip_AndTheGameplayResultIsTheSame()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(MissionRig.DariusPlayerPath);
            var unit = world.Track(Object.Instantiate(prefab, new Vector3(0f, 1f, -6f), Quaternion.identity));
            var encounter = world.CreateEncounter();
            var target = world.CreateDummy(new Vector3(0f, 0f, 2f));
            var health = unit.GetComponent<Health>();
            encounter.Initialize(new[] { health }, new[] { target });
            var aimed = world.CreateAimedShot();
            var abilities = unit.AddComponent<UnitAbilities>();
            abilities.Initialize(encounter, aimed);
            var animator = unit.GetComponent<UnitAnimationDriver>().Animator;
            yield return null;

            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(aimed, target)), Is.True);
            yield return TestWorld.WaitUntil(() => DariusMeasure.InState(animator, "Fire"), 3f);
            Assert.That(DariusMeasure.InState(animator, "Fire"), Is.True);
            Assert.That(target.Current, Is.EqualTo(target.Max - 45), "the ability's damage is unchanged");
        }
    }
}
#endif
```

- [ ] **Step 2: Run to verify failure** — `Tools/run-tests.sh PlayMode "Blackglass.Tests.UnitFeedbackPlayModeTests"` (compiles; flash tests fail) and `…AimedShotAnimationPlayModeTests` (fails: no Fire state).

- [ ] **Step 3: Extend `UnitPresentation`** — add these members and replace `Subscribe`, `Unsubscribe`, `OnDisable`, `Update` as shown (the shot/area/recoil code from Task 7 is unchanged):
```csharp
        const float FlashSeconds = 0.12f;
        // Multiplies the base colour while flashing (HDR-style overshoot, so a white body still visibly brightens).
        const float FlashBoost = 2.5f;
        // The unit pivot is the capsule centre, about this far above the floor.
        const float PivotToFloor = 0.95f;
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        SkinnedMeshRenderer[] bodies = new SkinnedMeshRenderer[0];
        UnitTeamTint tint;
        MaterialPropertyBlock block;
        float flashRemaining;
        int damagedFrame = -1;
```
```csharp
        void Subscribe()
        {
            if (subscribed)
                return;
            subscribed = true;
            TryGetComponent(out health);
            TryGetComponent(out attacker);
            TryGetComponent(out abilities);
            TryGetComponent(out weapon);
            TryGetComponent(out tint);
            bodies = GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (attacker != null)
                attacker.Fired += OnFired;
            if (abilities != null)
            {
                abilities.ShotResolved += OnAbilityShot;
                abilities.AreaResolved += OnAreaResolved;
            }
            if (weapon != null)
                weapon.Changed += OnWeaponChanged;
            if (health != null)
            {
                health.Damaged += OnDamaged;
                health.Died += OnDied;
                health.Healed += OnHealed;
            }
        }

        void Unsubscribe()
        {
            if (!subscribed)
                return;
            subscribed = false;
            if (attacker != null)
                attacker.Fired -= OnFired;
            if (abilities != null)
            {
                abilities.ShotResolved -= OnAbilityShot;
                abilities.AreaResolved -= OnAreaResolved;
            }
            if (weapon != null)
                weapon.Changed -= OnWeaponChanged;
            if (health != null)
            {
                health.Damaged -= OnDamaged;
                health.Died -= OnDied;
                health.Healed -= OnHealed;
            }
        }

        void OnDisable()
        {
            Unsubscribe();
            EndRecoil();
            EndFlash();
        }

        void Update()
        {
            if (!SimulationTime.IsRunning)
                return;
            if (recoil.IsActive)
            {
                recoil.Advance(Time.deltaTime);
                ApplyRecoil();
            }
            if (flashRemaining > 0f)
            {
                flashRemaining -= Time.deltaTime;
                if (flashRemaining <= 0f)
                    EndFlash();
                else
                    ApplyFlash();
            }
        }

        // Damage no shot already depicted (a blast, retaliation, anything else) shows one small neutral burst, once the
        // frame's shots have been presented. The killing blow shows none: the unit is already gone.
        void LateUpdate()
        {
            if (damagedFrame != Time.frameCount)
                return;
            damagedFrame = -1;
            if (presenter == null || health == null || !health.IsAlive)
                return;
            if (!presenter.ConsumeCharacterImpact(health))
                presenter.ShowCharacterBurst(health, transform.position + Vector3.up * ShotRecord.BodyHeight, Vector3.up);
        }

        void OnDamaged(int amount)
        {
            // A unit the player may not see shows no reaction.
            if (presenter == null || !presenter.IsActive || !presenter.Observes(health))
                return;
            flashRemaining = FlashSeconds;
            damagedFrame = Time.frameCount;
            ApplyFlash();
        }

        void OnDied()
        {
            EndFlash();
            damagedFrame = -1;
            if (presenter != null)
                presenter.ShowDeath(health, transform.position);
        }

        void OnHealed(int restored)
        {
            if (presenter != null && restored > 0)
                presenter.ShowPulse(health, transform.position - Vector3.up * PivotToFloor);
        }

        // The flash is a property block on this unit's own skinned bodies, on top of the team tint: no material is shared or modified.
        void ApplyFlash()
        {
            if (bodies.Length == 0)
                return;
            var k = Mathf.Clamp01(flashRemaining / FlashSeconds);
            var baseColor = tint != null ? tint.TeamColor : Color.white;
            var boost = 1f + FlashBoost * k;
            var color = new Color(baseColor.r * boost, baseColor.g * boost, baseColor.b * boost, baseColor.a);
            block = block ?? new MaterialPropertyBlock();
            foreach (var body in bodies)
            {
                if (body == null)
                    continue;
                body.GetPropertyBlock(block);
                block.SetColor(BaseColorId, color);
                body.SetPropertyBlock(block);
            }
        }

        void EndFlash()
        {
            var wasFlashing = flashRemaining > 0f;
            flashRemaining = 0f;
            if (!wasFlashing)
                return;
            if (tint != null)
            {
                tint.Apply();
                return;
            }
            foreach (var body in bodies)
                if (body != null)
                    body.SetPropertyBlock(null);
        }
```
(`block = block ?? new MaterialPropertyBlock()` is on a plain C# class, not a `UnityEngine.Object`.) Remove the old `Update` and `OnDisable` from Task 7. Note the heal pulse handler is included now; its effect asset arrives in Task 13 (a missing `PulseEffect` simply spawns nothing).

- [ ] **Step 4: Extend `UnitAnimationDriver`** — add `UnitAbilities abilities;` next to the other cached components; in `Awake` add `TryGetComponent(out abilities);`; in `OnEnable` add `if (abilities != null) abilities.ShotResolved += OnAbilityShot;`; in `OnDisable` add `if (abilities != null) abilities.ShotResolved -= OnAbilityShot;`; add the handler:
```csharp
        // Aimed Shot fires the weapon, so it plays the Fire clip like a basic attack. Blast and Mend have no clip.
        void OnAbilityShot(ShotRecord record)
        {
            if (!dead && CanDrive && !record.IsMelee)
                animator.SetTrigger(FireId);
        }
```

- [ ] **Step 5: Run**
```bash
Tools/run-tests.sh PlayMode "Blackglass.Tests.UnitFeedbackPlayModeTests"
Tools/run-tests.sh PlayMode "Blackglass.Tests.AimedShotAnimationPlayModeTests"
Tools/run-tests.sh PlayMode "Blackglass.Tests.UnitAnimationDriverPlayModeTests"
Tools/run-tests.sh PlayMode "Blackglass.Tests.CombatPresenterPlayModeTests"
```
Expected: `EXIT=0`. The animation driver test already existing proves the Fire/Hit/Dead behaviour is unchanged.

- [ ] **Step 6: Look at the flash** — add a capture test next to the others in `PresentationRenderCaptureTests` (`CloseUp_CharacterHit` already fires at `Target`, a plain capsule with no skinned body, so it only shows the burst). Instantiate `EnemyUnit_Hostile` and `Darius_Player` prefabs at known points in a throwaway PlayMode capture test, damage each, capture one frame 2 frames later, and read the PNG: the flash must be visible on both the tinted EnemyUnit and the white Darius. If Darius shows no flash, raise `FlashBoost` (it is only a multiplier on `_BaseColor`) and re-check; record the result honestly.

---

### Task 11: The layered Blast explosion, scaled to its gameplay radius, with a camera impulse

**Files:**
- Modify: `Editor/Presentation/EffectPrefabs.cs` (`ExplosionGrenade`), `Editor/Presentation/CombatPresentationAssetBuilder.cs` (explosion stage), `Scripts/CameraControl/TacticalCameraController.cs`, `Scripts/Presentation/PresentationPreviewPanel.cs`, `Editor/Presentation/CombatPresentationSceneBuilder.cs`, `Tests/PlayMode/TestSupport/PresentationRig.cs` (impulse)
- Generated: `Prefabs/ExplosionGrenade.prefab`, `Profiles/BlastCombatPresentation.asset`, `Preview/PreviewBlast.asset`; modified `Data/Abilities/Blast.asset` (profile reference), the preview scene
- Test: `Tests/EditMode/ExplosionAssetTests.cs`, `Tests/PlayMode/AreaPresentationPlayModeTests.cs`, `Tests/PlayMode/CameraImpulsePlayModeTests.cs`

**Interfaces:**
- Consumes: `AreaRecord`, `CombatPresenter.ShowArea`, `CameraImpulse`, `ImpulseEnvelope` (Tasks 1, 2, 7).
- Produces: `TacticalCameraController` adds `CameraImpulse.LocalOffset` (camera-local, rotated into place) to the viewing camera's local position and exposes `internal void SetImpulse(CameraImpulse)`; the explosion prefab, its profile and the preview "Cast Blast" button.

- [ ] **Step 1: Write the failing tests**

`Tests/EditMode/ExplosionAssetTests.cs`:
```csharp
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Blackglass.Tests
{
    public class ExplosionAssetTests
    {
        const string Root = "Assets/_Project/Presentation/";

        static GameObject Prefab() => AssetDatabase.LoadAssetAtPath<GameObject>(Root + "Prefabs/ExplosionGrenade.prefab");

        [Test]
        public void Explosion_IsLayered_AndHasNoColliderOrPhysics()
        {
            var prefab = Prefab();
            Assert.That(prefab, Is.Not.Null, "Run Blackglass/Presentation/Build Effects");
            var names = prefab.GetComponentsInChildren<ParticleSystem>(true).Select(p => p.name).ToList();
            Assert.That(names, Is.SupersetOf(new[] { "Flash", "Fireball", "Dust", "Sparks", "Fragments", "Smoke", "Ring" }));
            Assert.That(prefab.GetComponentsInChildren<Collider>(true), Is.Empty);
            Assert.That(prefab.GetComponentsInChildren<Rigidbody>(true), Is.Empty);
            foreach (var ps in prefab.GetComponentsInChildren<ParticleSystem>(true))
            {
                Assert.That(ps.collision.enabled, Is.False, ps.name + ": cosmetic fragments never collide");
                Assert.That(ps.main.useUnscaledTime, Is.False, ps.name);
                Assert.That(ps.main.scalingMode, Is.EqualTo(ParticleSystemScalingMode.Hierarchy), ps.name + " scales with the root");
            }
            Assert.That(prefab.GetComponentsInChildren<Light>(true).Length, Is.EqualTo(1));
        }

        [Test]
        public void TheRing_EndsExactlyAtTheReferenceRadius_SoItReadsAsTheRealBoundary()
        {
            var profile = AssetDatabase.LoadAssetAtPath<CombatPresentationProfile>(Root + "Profiles/BlastCombatPresentation.asset");
            Assert.That(profile, Is.Not.Null);
            var ring = Prefab().GetComponentsInChildren<ParticleSystem>(true).First(p => p.name == "Ring");
            var endFactor = ring.sizeOverLifetime.size.Evaluate(1f);
            var finalRadius = ring.main.startSize.constantMax * endFactor * 0.5f;
            Assert.That(finalRadius, Is.EqualTo(profile.ReferenceRadius).Within(0.01f));
        }

        [Test]
        public void BlastProfile_HasNoProblems_AndTheBlastAbilityUsesIt()
        {
            var profile = AssetDatabase.LoadAssetAtPath<CombatPresentationProfile>(Root + "Profiles/BlastCombatPresentation.asset");
            Assert.That(profile.Problems(), Is.Empty);
            Assert.That(profile.AreaEffect, Is.EqualTo(Prefab()));
            var blast = AssetDatabase.LoadAssetAtPath<AbilityDefinition>("Assets/_Project/Data/Abilities/Blast.asset");
            Assert.That(blast.Presentation, Is.EqualTo(profile));
            Assert.That(blast.Radius, Is.EqualTo(profile.ReferenceRadius), "the prefab was authored for the shipped Blast radius");
        }
    }
}
```

`Tests/PlayMode/AreaPresentationPlayModeTests.cs`:
```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class AreaPresentationPlayModeTests
    {
        PresentationRig rig;

        [TearDown]
        public void TearDown() => rig?.Dispose();

        // A caster, an encounter with two hostile dummies (one inside, one outside the blast) and an ability with this radius.
        (UnitAbilities abilities, AbilityDefinition ability, Health inside, Health outside) BlastWorld(float radius, float cooldown = 10f)
        {
            var caster = rig.World.CreateFighter(new Vector3(0f, 0f, -6f), role: CombatRole.Ranged, range: 30f);
            caster.GetComponent<AutoRetaliate>().enabled = false;
            caster.gameObject.AddComponent<UnitPresentation>().Bind(rig.Presenter);
            var encounter = rig.World.CreateEncounter();
            var inside = rig.World.CreateDummy(new Vector3(0.5f, 0f, 4f));
            var outside = rig.World.CreateDummy(new Vector3(radius + 3f, 0f, 4f));
            inside.gameObject.AddComponent<UnitPresentation>().Bind(rig.Presenter);
            encounter.Initialize(new[] { caster.GetComponent<Health>() }, new[] { inside, outside });
            var ability = AbilityDefinition.Create("Blast", AbilityTargetMode.Ground, 15f, true, AbilityCoverRule.Ignored, cooldown,
                AbilityEffect.Damage, 35, radius);
            ability.SetPresentation(rig.RifleProfile);
            var abilities = rig.World.AddAbilities(caster, encounter, ability);
            Physics.SyncTransforms();
            return (abilities, ability, inside, outside);
        }

        PooledEffect AreaEffect() =>
            Object.FindObjectsByType<PooledEffect>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).First(e => e.Category == EffectCategory.Area);

        [UnityTest]
        public IEnumerator Blast_PlaysAtTheGameplayCentre_ScaledByTheGameplayRadius_AndDamageIsUnchanged()
        {
            rig = new PresentationRig();
            var (abilities, ability, inside, outside) = BlastWorld(6f);
            yield return null;

            Assert.That(abilities.TryUse(AbilityCommand.AtGround(ability, new Vector3(0f, 0f, 4f))), Is.True);

            var effect = AreaEffect();
            Assert.That(effect.transform.position.x, Is.EqualTo(0f).Within(1e-3f));
            Assert.That(effect.transform.position.z, Is.EqualTo(4f).Within(1e-3f));
            Assert.That(effect.transform.localScale, Is.EqualTo(Vector3.one * 2f), "radius 6 over a reference radius of 3");
            Assert.That(inside.Current, Is.EqualTo(inside.Max - 35));
            Assert.That(outside.Current, Is.EqualTo(outside.Max), "the blast radius is the gameplay radius only");
        }

        [UnityTest]
        public IEnumerator PresentationOff_DealsTheSameDamage_AndShowsNothing()
        {
            rig = new PresentationRig();
            var (abilities, ability, inside, _) = BlastWorld(3f);
            rig.Presenter.Enabled = false;
            yield return null;
            Assert.That(abilities.TryUse(AbilityCommand.AtGround(ability, new Vector3(0f, 0f, 4f))), Is.True);
            Assert.That(rig.Presenter.ActiveEffects, Is.EqualTo(0));
            Assert.That(inside.Current, Is.EqualTo(inside.Max - 35));
        }

        [UnityTest]
        public IEnumerator HiddenCentre_ShowsNothing_AndSendsNoImpulse()
        {
            rig = new PresentationRig();
            var (abilities, ability, _, _) = BlastWorld(3f);
            yield return null;
            rig.Presenter.UseKnowledge(_ => PointVisibility.Hidden, _ => true);
            abilities.TryUse(AbilityCommand.AtGround(ability, new Vector3(0f, 0f, 4f)));
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Area), Is.EqualTo(0));
            Assert.That(rig.Impulse.LocalOffset, Is.EqualTo(Vector3.zero));
        }

        [UnityTest]
        public IEnumerator Impulse_ScalesWithTheSetting_AndZeroTurnsItOff()
        {
            rig = new PresentationRig();
            var (abilities, ability, _, _) = BlastWorld(3f, cooldown: 0f);
            yield return null;

            rig.Presenter.Settings.cameraShake = 0f;
            abilities.TryUse(AbilityCommand.AtGround(ability, new Vector3(0f, 0f, 4f)));
            yield return null;
            Assert.That(rig.Impulse.LocalOffset, Is.EqualTo(Vector3.zero), "shake 0 disables the impulse");

            rig.Presenter.Clear();
            rig.Presenter.Settings.cameraShake = 1f;
            abilities.TryUse(AbilityCommand.AtGround(ability, new Vector3(0f, 0f, 4f)));
            yield return null;
            Assert.That(rig.Impulse.LocalOffset.magnitude, Is.GreaterThan(0f));
            Assert.That(rig.Impulse.LocalOffset.magnitude, Is.LessThanOrEqualTo(ImpulseEnvelope.MaxOffset * 1.5f));
        }

        [UnityTest]
        public IEnumerator FiveBlasts_KeepAtMostFourExplosionsActive_AndGameplayStillApplies()
        {
            rig = new PresentationRig();
            var (abilities, ability, inside, _) = BlastWorld(3f, cooldown: 0f);
            yield return null;
            for (var i = 0; i < 6; i++)
                Assert.That(abilities.TryUse(AbilityCommand.AtGround(ability, new Vector3(0f, 0f, 4f))), Is.True);
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Area), Is.LessThanOrEqualTo(rig.Presenter.Budget.Cap(EffectCategory.Area)));
            Assert.That(rig.Presenter.Budget.LightsActive, Is.LessThanOrEqualTo(rig.Presenter.Budget.LightCap));
            Assert.That(inside.Current, Is.EqualTo(inside.Max - 6 * 35), "every blast still hit");
        }

        [UnityTest]
        public IEnumerator TheGeneratedExplosion_ScalesItsExtentWithTheRadius()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Presentation/Prefabs/ExplosionGrenade.prefab");
            Assert.That(prefab, Is.Not.Null);
            // Measure at the same moment of two instances (scale 1 and scale 2).
            var small = Object.Instantiate(prefab);
            var large = Object.Instantiate(prefab);
            large.transform.localScale = Vector3.one * 2f;
            var smallDust = small.GetComponentsInChildren<ParticleSystem>(true).First(p => p.name == "Dust");
            var largeDust = large.GetComponentsInChildren<ParticleSystem>(true).First(p => p.name == "Dust");
            smallDust.Play();
            largeDust.Play();
            yield return new WaitForSeconds(0.4f);
            var smallParticles = new ParticleSystem.Particle[smallDust.main.maxParticles];
            var largeParticles = new ParticleSystem.Particle[largeDust.main.maxParticles];
            var smallCount = smallDust.GetParticles(smallParticles);
            var largeCount = largeDust.GetParticles(largeParticles);
            float Max(ParticleSystem.Particle[] p, int n)
            {
                var m = 0f;
                for (var i = 0; i < n; i++)
                    m = Mathf.Max(m, new Vector2(p[i].position.x, p[i].position.z).magnitude);
                return m;
            }
            var ratio = Max(largeParticles, largeCount) / Mathf.Max(0.01f, Max(smallParticles, smallCount));
            Object.Destroy(small);
            Object.Destroy(large);
            Assert.That(ratio, Is.InRange(1.6f, 2.4f), "twice the radius gives roughly twice the spread");
        }
    }
}
#endif
```

`Tests/PlayMode/CameraImpulsePlayModeTests.cs`:
```csharp
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CameraImpulsePlayModeTests
    {
        TestWorld world;
        TacticalPause pause;
        TacticalCameraController controller;
        CameraImpulse impulse;
        Camera view;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            var rig = world.Track(new GameObject("CameraRig"));
            rig.SetActive(false);
            var cameraObject = new GameObject("Camera");
            cameraObject.transform.SetParent(rig.transform, false);
            view = cameraObject.AddComponent<Camera>();
            controller = rig.AddComponent<TacticalCameraController>();
            controller.Initialize(view, null, null, null, null, null, null);
            impulse = rig.AddComponent<CameraImpulse>();
            controller.SetImpulse(impulse);
            rig.SetActive(true);
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        [UnityTest]
        public IEnumerator WithoutAnImpulse_TheCameraSitsAtItsRestPose()
        {
            yield return null;
            var rest = view.transform.localPosition;
            yield return new WaitForSeconds(0.1f);
            Assert.That(view.transform.localPosition, Is.EqualTo(rest));
        }

        [UnityTest]
        public IEnumerator Impulse_MovesTheViewingCameraOnly_StaysSmall_AndDecaysBackToRest()
        {
            yield return null;
            var rest = view.transform.localPosition;
            var pivot = controller.transform.position;
            impulse.Add(1f);
            yield return null; yield return null;
            Assert.That(Vector3.Distance(view.transform.localPosition, rest), Is.GreaterThan(0f));
            Assert.That(Vector3.Distance(view.transform.localPosition, rest), Is.LessThanOrEqualTo(ImpulseEnvelope.MaxOffset * 1.5f));
            Assert.That(controller.transform.position, Is.EqualTo(pivot), "the pivot the gameplay uses never moves");
            yield return new WaitForSeconds(ImpulseEnvelope.Duration + 0.2f);
            Assert.That(Vector3.Distance(view.transform.localPosition, rest), Is.LessThan(1e-4f));
        }

        [UnityTest]
        public IEnumerator Impulse_FreezesDuringPause_WhileTheCameraControlsKeepWorking()
        {
            yield return null;
            impulse.Add(1f);
            yield return new WaitForSeconds(0.05f);
            pause.Pause();
            yield return null;
            var frozen = view.transform.localPosition;
            var pivotBefore = controller.transform.position;
            yield return new WaitForSecondsRealtime(0.4f);
            Assert.That(view.transform.localPosition, Is.EqualTo(frozen).Using(new UnityEngine.TestTools.Utils.Vector3EqualityComparer(1e-5f)));

            controller.FocusOn(pivotBefore + new Vector3(3f, 0f, 0f));
            yield return null;
            Assert.That(controller.transform.position.x, Is.EqualTo(pivotBefore.x + 3f).Within(0.01f), "normal camera control still works while paused");
        }

        [UnityTest]
        public IEnumerator PointerRays_AreOffByNoMoreThanTheShakeAmplitude()
        {
            yield return null;
            var centre = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            Vector3 GroundHit()
            {
                var ray = view.ScreenPointToRay(centre);
                var plane = new Plane(Vector3.up, Vector3.zero);
                Assert.That(plane.Raycast(ray, out var d), Is.True);
                return ray.GetPoint(d);
            }
            var still = GroundHit();
            impulse.Add(1f);
            yield return null; yield return null;
            var shaken = GroundHit();
            Assert.That(Vector3.Distance(still, shaken), Is.LessThanOrEqualTo(ImpulseEnvelope.MaxOffset * 2f),
                "a click during the shake lands within a few centimetres of where it would have");
        }
    }
}
#endif
```

- [ ] **Step 2: Run to verify failure** — `Tools/run-tests.sh PlayMode "Blackglass.Tests.CameraImpulsePlayModeTests"` → `EXIT=1` (`SetImpulse` missing).

- [ ] **Step 3: Integrate the impulse into `TacticalCameraController`** — add the field and setter near the other serialized fields:
```csharp
        // Optional: an additive wobble source (explosion impulse). Applied to the viewing camera only, never to the pivot.
        [SerializeField] CameraImpulse impulse;

        internal void SetImpulse(CameraImpulse source) => impulse = source;
```
and replace the body of `ApplyCameraPose`:
```csharp
        void ApplyCameraPose()
        {
            if (viewCamera == null)
                return;
            var localRotation = Quaternion.Euler(pitch, 0f, 0f);
            var offset = impulse != null ? localRotation * impulse.LocalOffset : Vector3.zero;
            viewCamera.transform.SetLocalPositionAndRotation(localRotation * new Vector3(0f, 0f, -distance) + offset, localRotation);
        }
```
Decision for the record (put it in decision 044 in Task 16): the impulse is at most 8 cm and never rotates the camera, so a pointer ray is off by at most that much; the test above pins the bound.

- [ ] **Step 4: Update `PresentationRig`** — create the impulse and bind it: in the constructor replace the `Bind` line with
```csharp
            Impulse = World.Track(new GameObject("CameraImpulse")).AddComponent<CameraImpulse>();
            Presenter.Bind(intelligence, Pause, Impulse, FallbackProfile, FallbackProfile);
```
and add `public readonly CameraImpulse Impulse;` to the fields (assign before `Presenter.Bind`; the `Systems` object is still inactive at that point, so `Bind` runs before `OnEnable`).

- [ ] **Step 5: Run the camera and area tests** (the area tests need only the rig; the asset test needs Step 6)
```bash
Tools/run-tests.sh PlayMode "Blackglass.Tests.CameraImpulsePlayModeTests"
Tools/run-tests.sh PlayMode "Blackglass.Tests.AreaPresentationPlayModeTests"
```
`TheGeneratedExplosion_ScalesItsExtentWithTheRadius` fails until Step 6 builds the prefab; every other test must pass now.

- [ ] **Step 6: Add the explosion prefab** — in `EffectPrefabs.cs` add:
```csharp
        // Authored for the shipped Blast radius of 3 m; the presenter scales the instance by gameplay radius / 3.
        public const float ExplosionReferenceRadius = 3f;

        public static GameObject ExplosionGrenade(EffectKit kit, bool force) => Build("ExplosionGrenade", force, root =>
        {
            SetLightSeconds(root, 0.3f);
            var t = root.transform;
            ParticleAuthoring.Build(t, new PsSpec
            {
                Name = "Flash", Material = kit.Glow, Burst = 1, Duration = 0.2f, ShapeEnabled = false,
                Lifetime = new Vector2(0.1f, 0.12f), Size = new Vector2(2.6f, 2.6f),
                StartColor = new Color(1f, 0.9f, 0.6f, 1f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.2f),
            });
            ParticleAuthoring.Build(t, new PsSpec
            {
                Name = "Fireball", Material = kit.Glow, Burst = 10, Duration = 0.2f,
                Lifetime = new Vector2(0.35f, 0.6f), Size = new Vector2(0.9f, 1.4f), Speed = new Vector2(1.5f, 3.5f),
                Shape = ParticleSystemShapeType.Sphere, ShapeRadius = 0.4f,
                StartColor = new Color(1f, 0.55f, 0.2f, 0.8f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.2f),
                SizeOverLifetime = ParticleAuthoring.Grow(0.6f, 1.8f),
            });
            ParticleAuthoring.Build(t, new PsSpec
            {
                Name = "Dust", Material = kit.Dust, Burst = 14, Duration = 0.3f,
                Lifetime = new Vector2(0.8f, 1.4f), Size = new Vector2(0.8f, 1.4f), Speed = new Vector2(2.5f, 5f),
                Shape = ParticleSystemShapeType.Sphere, ShapeRadius = 0.5f, Gravity = -0.03f,
                StartColor = new Color(0.55f, 0.5f, 0.45f, 0.45f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.15f),
                SizeOverLifetime = ParticleAuthoring.Grow(0.6f, 2f),
            });
            ParticleAuthoring.Build(t, new PsSpec
            {
                Name = "Sparks", Material = kit.Spark, Render = ParticleSystemRenderMode.Stretch, Burst = 20, Duration = 0.2f,
                Lifetime = new Vector2(0.4f, 0.8f), Size = new Vector2(0.05f, 0.09f), Speed = new Vector2(6f, 12f),
                Shape = ParticleSystemShapeType.Sphere, ShapeRadius = 0.2f, LengthScale = 4f, Gravity = 1f,
                StartColor = new Color(1f, 0.75f, 0.35f, 1f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.3f),
            });
            ParticleAuthoring.Build(t, new PsSpec
            {
                Name = "Fragments", Material = kit.Chip, Burst = 12, Duration = 0.2f,
                Lifetime = new Vector2(0.7f, 1f), Size = new Vector2(0.06f, 0.12f), Speed = new Vector2(5f, 10f),
                Shape = ParticleSystemShapeType.Hemisphere, ShapeRadius = 0.3f, Gravity = 2f,
                StartColor = new Color(0.12f, 0.11f, 0.1f, 1f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.6f),
            });
            ParticleAuthoring.Build(t, new PsSpec
            {
                Name = "Smoke", Material = kit.Smoke, Burst = 16, Duration = 0.4f,
                Lifetime = new Vector2(2.5f, 3.5f), Size = new Vector2(1.2f, 1.8f), Speed = new Vector2(0.5f, 1.4f),
                Shape = ParticleSystemShapeType.Sphere, ShapeRadius = 0.6f, Gravity = -0.04f,
                StartColor = new Color(0.35f, 0.35f, 0.36f, 0.32f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.25f),
                SizeOverLifetime = ParticleAuthoring.Grow(0.8f, 2.2f),
            });
            // A thin ring that ends exactly at the gameplay radius (start size is the diameter at the end of the grow curve).
            ParticleAuthoring.Build(t, new PsSpec
            {
                Name = "Ring", Material = kit.Ring, Render = ParticleSystemRenderMode.HorizontalBillboard, Burst = 1, Duration = 0.2f,
                ShapeEnabled = false, RandomRotation = false,
                Lifetime = new Vector2(0.4f, 0.4f), Size = new Vector2(ExplosionReferenceRadius * 2f, ExplosionReferenceRadius * 2f),
                StartColor = new Color(1f, 0.85f, 0.6f, 0.7f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.2f),
                SizeOverLifetime = ParticleAuthoring.Grow(0.1f, 1f),
            });
            AddLight(t, new Color(1f, 0.7f, 0.4f), range: 9f, intensity: 6f, localPosition: new Vector3(0f, 0.8f, 0f));
        });
```
(`Ring` uses `Size` as a constant, so `main.startSize.constantMax` is the diameter at full size and the grow curve ends at 1.)

In `CombatPresentationAssetBuilder.Build` add, after the rifle stage:
```csharp
            BuildExplosionStage(kit, force);
```
and the method:
```csharp
        const string AbilitiesRoot = "Assets/_Project/Data/Abilities/";

        static void BuildExplosionStage(EffectKit kit, bool force)
        {
            var explosion = EffectPrefabs.ExplosionGrenade(kit, force);
            var profile = LoadOrCreateProfile("BlastCombatPresentation");
            if (profile.AreaEffect == null)
                profile.SetArea(explosion, EffectPrefabs.ExplosionReferenceRadius, profile.AreaCue, 0.7f);
            EditorUtility.SetDirty(profile);

            var blast = AssetDatabase.LoadAssetAtPath<AbilityDefinition>(AbilitiesRoot + "Blast.asset");
            if (blast == null)
                throw new System.InvalidOperationException("Missing the Blast ability asset.");
            if (blast.Presentation == null)
            {
                blast.SetPresentation(profile);
                EditorUtility.SetDirty(blast);
            }
        }
```
Run `Tools/run-method.sh Blackglass.EditorTools.CombatPresentationAssetBuilder.BuildFromCommandLine` (twice: the second must change nothing), then:
```bash
Tools/run-tests.sh EditMode "Blackglass.Tests.ExplosionAssetTests"
Tools/run-tests.sh PlayMode "Blackglass.Tests.AreaPresentationPlayModeTests"
Tools/run-tests.sh EditMode "Blackglass.Tests.CombatPresentationAssetTests"
Tools/run-tests.sh EditMode "Blackglass.Tests.AbilityDefinitionTests"
```
Expected: all `EXIT=0`.

- [ ] **Step 7: Extend the preview scene and panel for the Blast.** In `PresentationPreviewPanel` add the fields `[SerializeField] UnitAbilities caster; [SerializeField] AbilityDefinition blast; [SerializeField] Transform blastPoint;` and
```csharp
        public void CastBlast() => caster.TryUse(AbilityCommand.AtGround(blast, blastPoint.position));
```
with a button `if (GUILayout.Button("Cast Blast (real ability, radius 3)")) CastBlast();`. In `CombatPresentationSceneBuilder.BuildPreview` add, before the panel is created: an `Encounter` on `Systems` initialised with friendlies `[Shooter, Caster]` and hostiles `[Target, TargetRight, BlastVictim_1, BlastVictim_2]`; the `Caster` unit at `(0, 1, -2)` with `UnitAttacker` (ranged), `Health`, `UnitPresentation` and a `UnitAbilities` initialised (`abilities.Initialize(encounter, previewBlast)`) with `PreviewBlast`; two `BlastVictim` capsules (Health 100000, `UnitPresentation`) at `(-1, 1, 8)` and `(1.2, 1, 7.4)` between the blocks; a `BlastPoint` empty at `(0, 0, 8)`; and wire `caster`, `blast`, `blastPoint` into the panel. `PreviewBlast` is a clone of `Blast.asset` with its cooldown set to 0 through `SerializedObject` (field `cooldown`), saved as `Assets/_Project/Presentation/Preview/PreviewBlast.asset`, keeping `presentation` pointing at `BlastCombatPresentation`. Also set the controller's `impulse` field in the builder: `controllerObject.FindProperty("impulse").objectReferenceValue = impulse;`. Add to `PresentationPreviewScenePlayModeTests`:
```csharp
        [UnityTest]
        public IEnumerator CastBlast_IsARealAbility_ShowsTheExplosion_AndDamagesOnlyTheVictims()
        {
            var panel = Find<PresentationPreviewPanel>();
            var presenter = Find<CombatPresenter>();
            var healths = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Health>(true)).ToList();
            var victim = healths.First(h => h.name == "BlastVictim_1");
            var bystander = healths.First(h => h.name == "Target");
            var victimBefore = victim.Current;
            var bystanderBefore = bystander.Current;
            panel.CastBlast();
            Assert.That(presenter.ActiveOf(EffectCategory.Area), Is.EqualTo(1));
            Assert.That(victim.Current, Is.LessThan(victimBefore));
            Assert.That(bystander.Current, Is.EqualTo(bystanderBefore));
            yield return null;
        }
```
Rebuild the scene (`Tools/run-method.sh Blackglass.EditorTools.CombatPresentationSceneBuilder.BuildPreviewFromCommandLine`) and run `Tools/run-tests.sh PlayMode "Blackglass.Tests.PresentationPreviewScenePlayModeTests"` (expected `EXIT=0`).

- [ ] **Step 8: Look at it.** Add to `PresentationRenderCaptureTests`:
```csharp
        [UnityTest]
        public IEnumerator Gameplay_Blast_AtTheNormalZoomAndCloseUp()
        {
            Find<PresentationPreviewPanel>().CastBlast();
            yield return null; yield return null; yield return null;
            Assert.That(Capture("gameplay_blast_a"), Is.GreaterThan(20f));
            for (var i = 0; i < 12; i++) yield return null;
            Assert.That(Capture("gameplay_blast_b"), Is.GreaterThan(20f));
            Find<TacticalCameraController>().enabled = false;
            var camera = Find<Camera>();
            camera.transform.position = new Vector3(0f, 6f, 0f);
            camera.transform.LookAt(new Vector3(0f, 1f, 8f));
            Find<PresentationPreviewPanel>().CastBlast();
            yield return null; yield return null;
            Assert.That(Capture("closeup_blast"), Is.GreaterThan(20f));
        }
```
Run `Tools/run-tests.sh PlayMode "Blackglass.Tests.PresentationRenderCaptureTests"`, open the PNGs, and judge: the fireball sits inside the ring, the ring reaches the 3 m boundary and fades, smoke is mid-grey and readable (HUD/battlefield not hidden at the normal zoom), no fullscreen flash. Tune `ExplosionGrenade` numbers and `Rebuild Effects (overwrite)`; record observations in `.claude/scratchpad/phase125/progress.md`.

- [ ] **Step 9: Milestone B gate:** `Tools/run-tests.sh EditMode` and `Tools/run-tests.sh PlayMode` both green; totals = baseline + tests added so far.

---

### Task 12: Placeholder sounds and the audio cues

**Files:**
- Create (Editor): `Editor/Presentation/PlaceholderSounds.cs`
- Modify: `Scripts/Presentation/CombatPresentationProfile.cs` (`AudioCue.WithFalloff`), `Editor/Presentation/CombatPresentationAssetBuilder.cs` (audio stage), `Tests/PlayMode/TestSupport/PresentationRig.cs` (cues)
- Generated: `Assets/_Project/Presentation/Audio/PLACEHOLDER_*.wav`, `README.md`; modified profiles
- Test: `Tests/EditMode/PlaceholderSoundTests.cs`, `Tests/PlayMode/CombatAudioPresentationPlayModeTests.cs`

**Interfaces:**
- Consumes: `AudioCue`, `CombatAudio`, `CombatPresenter.PlayCue` paths (Tasks 3, 6, 7).
- Produces: `AudioCue.WithFalloff(float minDistance, float maxDistance) : AudioCue` (internal, returns this); generated sounds `PLACEHOLDER_RifleShot`, `PLACEHOLDER_Explosion`, `PLACEHOLDER_ImpactConcrete`, `PLACEHOLDER_ImpactMetal`, `PLACEHOLDER_ImpactBody`, `PLACEHOLDER_HealChime`, `PLACEHOLDER_BodyFall`; cues wired into `RifleCombatPresentation` (fire + four impacts), `DefaultCombatPresentation` (character/default impacts, death cue, pulse cue) and `BlastCombatPresentation` (area cue).

- [ ] **Step 1: Write the failing tests**

`Tests/EditMode/PlaceholderSoundTests.cs`:
```csharp
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Blackglass.Tests
{
    public class PlaceholderSoundTests
    {
        const string Root = "Assets/_Project/Presentation/";

        [TestCase("PLACEHOLDER_RifleShot", 0.2f, 0.6f)]
        [TestCase("PLACEHOLDER_Explosion", 1.0f, 2.5f)]
        [TestCase("PLACEHOLDER_ImpactConcrete", 0.1f, 0.4f)]
        [TestCase("PLACEHOLDER_ImpactMetal", 0.15f, 0.6f)]
        [TestCase("PLACEHOLDER_ImpactBody", 0.1f, 0.3f)]
        [TestCase("PLACEHOLDER_HealChime", 0.3f, 1.0f)]
        [TestCase("PLACEHOLDER_BodyFall", 0.2f, 0.6f)]
        public void Sound_ExistsIsAudible_AndNeitherSilentNorClipping(string name, float minSeconds, float maxSeconds)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(Root + "Audio/" + name + ".wav");
            Assert.That(clip, Is.Not.Null, "Run Blackglass/Presentation/Build Effects");
            Assert.That(clip.length, Is.InRange(minSeconds, maxSeconds));
            Assert.That(clip.channels, Is.EqualTo(1));
            var data = new float[clip.samples * clip.channels];
            Assert.That(clip.GetData(data, 0), Is.True);
            var peak = 0f;
            var sum = 0.0;
            foreach (var s in data)
            {
                peak = Mathf.Max(peak, Mathf.Abs(s));
                sum += s * s;
            }
            Assert.That(peak, Is.InRange(0.3f, 0.99f), "audible and not clipping");
            Assert.That(sum / data.Length, Is.GreaterThan(1e-4), "not silent");
        }

        [Test]
        public void TheFolder_StatesThatTheSoundsAreGeneratedPlaceholders()
        {
            var readme = Path.Combine("Assets/_Project/Presentation/Audio", "README.md");
            Assert.That(File.Exists(readme), Is.True);
            var text = File.ReadAllText(readme);
            Assert.That(text, Does.Contain("generated").IgnoreCase.And.Contain("placeholder").IgnoreCase);
            Assert.That(text, Does.Contain("no external"));
        }

        [Test]
        public void Profiles_CarryTheirCues()
        {
            var rifle = AssetDatabase.LoadAssetAtPath<CombatPresentationProfile>(Root + "Profiles/RifleCombatPresentation.asset");
            Assert.That(rifle.FireCue.IsEmpty, Is.False);
            foreach (var kind in new[] { ImpactSurfaceKind.Concrete, ImpactSurfaceKind.Metal, ImpactSurfaceKind.Character, ImpactSurfaceKind.Default })
                Assert.That(rifle.ImpactFor(kind).Cue.IsEmpty, Is.False, kind.ToString());
            var defaults = AssetDatabase.LoadAssetAtPath<CombatPresentationProfile>(Root + "Profiles/DefaultCombatPresentation.asset");
            Assert.That(defaults.DeathCue.IsEmpty, Is.False);
            Assert.That(defaults.PulseCue.IsEmpty, Is.False);
            Assert.That(defaults.ImpactFor(ImpactSurfaceKind.Character).Cue.IsEmpty, Is.False);
            var blast = AssetDatabase.LoadAssetAtPath<CombatPresentationProfile>(Root + "Profiles/BlastCombatPresentation.asset");
            Assert.That(blast.AreaCue.IsEmpty, Is.False);
            Assert.That(blast.AreaCue.Priority, Is.LessThan(rifle.FireCue.Priority), "explosions outrank rifle shots for a voice");
        }
    }
}
```

`Tests/PlayMode/CombatAudioPresentationPlayModeTests.cs`:
```csharp
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CombatAudioPresentationPlayModeTests
    {
        PresentationRig rig;

        [TearDown]
        public void TearDown() => rig?.Dispose();

        static UnitAttacker AttackerOf(CommandableUnit unit) => unit.GetComponent<UnitAttacker>();

        [UnityTest]
        public IEnumerator AShot_PlaysTheFiringSound_AndTheImpactSound_ThroughTheListenerPool()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f));
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            Assert.That(AttackerOf(shooter).TryAttack(target), Is.True);
            Assert.That(rig.Presenter.Audio.ActiveVoices, Is.EqualTo(2), "one firing sound, one impact sound");
        }

        [UnityTest]
        public IEnumerator ARejectedAttack_PlaysNothing()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f));
            var far = rig.CreateTarget(new Vector3(0f, 0f, 80f));
            yield return null;
            AttackerOf(shooter).TryAttack(far);
            Assert.That(rig.Presenter.Audio.ActiveVoices, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator HiddenShooter_AndHiddenImpacts_PlayNoSound_NoHearingChannel()
        {
            rig = new PresentationRig();
            var hidden = rig.CreateShooter(new Vector3(12f, 0f, -6f));
            var hiddenTarget = rig.CreateTarget(new Vector3(12f, 0f, 8f));
            yield return null;
            rig.Presenter.UseKnowledge(p => p.x > 5f ? PointVisibility.Hidden : PointVisibility.Visible, u => u.transform.position.x <= 5f);
            AttackerOf(hidden).TryAttack(hiddenTarget);
            Assert.That(rig.Presenter.Audio.ActiveVoices, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator SixShootersInOneFrame_StayInsideTheVoiceBudget_AndTheCueGainDrops()
        {
            rig = new PresentationRig();
            var target = rig.CreateTarget(new Vector3(0f, 0f, 12f));
            for (var i = 0; i < 6; i++)
            {
                var shooter = rig.CreateShooter(new Vector3(-5f + 2f * i, 0f, -6f));
                AttackerOf(shooter).TryAttack(target);
            }
            yield return null;
            Assert.That(rig.Presenter.Audio.ActiveVoices, Is.LessThanOrEqualTo(rig.Presenter.Audio.VoiceCount));
            var volumes = new System.Collections.Generic.List<float>();
            foreach (var source in rig.Presenter.Audio.Sources)
                if (source.clip != null)
                    volumes.Add(source.volume);
            Assert.That(volumes, Is.Not.Empty);
            Assert.That(Mathf.Max(volumes.ToArray()), Is.LessThanOrEqualTo(1f));
        }

        [UnityTest]
        public IEnumerator Pause_HoldsTheSounds_ResumeReleasesThem_AndClearStopsThemEvenWhilePaused()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f));
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            AttackerOf(shooter).TryAttack(target);
            var voices = rig.Presenter.Audio.ActiveVoices;
            rig.Pause.Pause();
            Assert.That(rig.Presenter.Audio.IsPaused, Is.True);
            yield return new WaitForSecondsRealtime(0.6f);
            Assert.That(rig.Presenter.Audio.ActiveVoices, Is.EqualTo(voices), "paused sounds are neither finished nor duplicated");
            rig.Presenter.Clear();
            Assert.That(rig.Presenter.Audio.ActiveVoices, Is.EqualTo(0));
            rig.Pause.Resume();
            Assert.That(rig.Presenter.Audio.IsPaused, Is.False);
        }

        [UnityTest]
        public IEnumerator Death_PlaysTheDeathSound_OnlyForAnObservedUnit()
        {
            rig = new PresentationRig();
            var visible = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            var hidden = rig.CreateTarget(new Vector3(3f, 0f, 4f));
            yield return null;
            rig.Presenter.UseKnowledge(_ => PointVisibility.Visible, u => u != hidden);
            hidden.TakeDamage(hidden.Max);
            Assert.That(rig.Presenter.Audio.ActiveVoices, Is.EqualTo(0));
            visible.TakeDamage(visible.Max);
            Assert.That(rig.Presenter.Audio.ActiveVoices, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator MissingClips_FailSafely()
        {
            rig = new PresentationRig();
            rig.RifleProfile.SetFireCue(new AudioCue());
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f));
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            Assert.That(AttackerOf(shooter).TryAttack(target), Is.True);
            LogAssert.NoUnexpectedReceived();
        }
    }
}
#endif
```

- [ ] **Step 2: Add the falloff helper** to `AudioCue` (Task 3 file):
```csharp
        internal AudioCue WithFalloff(float min, float max)
        {
            minDistance = min;
            maxDistance = max;
            return this;
        }
```

- [ ] **Step 3: Update `PresentationRig`** so its profiles carry real (generated at runtime) clips: add a private list `readonly List<AudioClip> clips` and a helper
```csharp
        AudioCue Cue(float seconds = 0.3f, int priority = 128, float minInterval = 0.02f)
        {
            var clip = AudioClip.Create("rig", Mathf.Max(1, (int)(44100 * seconds)), 1, 44100, false);
            clips.Add(clip);
            return AudioCue.Create(1f, priority, minInterval, clip);
        }
```
In the constructor after building `RifleProfile`: `RifleProfile.SetFireCue(Cue(0.3f, 100));` and give each rig impact a cue (`SurfaceImpact.Create(kind, prefab, Cue(0.2f, 160))`); `FallbackProfile.SetDeathCue(Cue(0.3f, 150));` and `FallbackProfile.SetPulse(PulsePrefab, Cue(0.4f, 140));` (the existing `SetPulse(PulsePrefab, null)` call is replaced); `RifleProfile.SetArea(AreaPrefab, 3f, Cue(1f, 40), 0.6f);`. In `Dispose` destroy the clips (`Object.DestroyImmediate`). Re-run `CombatPresenterPlayModeTests` afterwards: still green (they assert effect counts, not voices).

- [ ] **Step 4: Implement `PlaceholderSounds.cs`** (Editor):
```csharp
#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Blackglass.EditorTools
{
    /// <summary>
    /// Synthesises the placeholder combat sounds locally (filtered noise bursts, decaying sines) and writes them as 16-bit
    /// mono WAV files. They are original, generated by this project and meant to be replaced; no external audio is used.
    /// Deterministic: every sound has a fixed seed.
    /// </summary>
    internal static class PlaceholderSounds
    {
        const int Rate = 44100;

        public static AudioClip RifleShot(bool force) => Make("PLACEHOLDER_RifleShot", 0.35f, 1, force, (t, n, s) =>
        {
            s.Low += (n - s.Low) * 0.35f;
            var envelope = Mathf.Exp(-t * 28f);
            var thump = Mathf.Sin(2f * Mathf.PI * Mathf.Lerp(120f, 60f, t / 0.35f) * t) * Mathf.Exp(-t * 14f);
            return (n * 0.5f + s.Low * 0.6f) * envelope + thump * 0.6f;
        });

        public static AudioClip Explosion(bool force) => Make("PLACEHOLDER_Explosion", 1.6f, 2, force, (t, n, s) =>
        {
            s.Low += (n - s.Low) * 0.04f;
            var attack = Mathf.Clamp01(t / 0.01f);
            var body = s.Low * 3.2f * Mathf.Exp(-t * 2.2f) * attack;
            var sub = Mathf.Sin(2f * Mathf.PI * 55f * t) * Mathf.Exp(-t * 3f) * 0.8f;
            var crackle = n * Mathf.Exp(-t * 9f) * 0.4f;
            return body + sub + crackle;
        });

        public static AudioClip ImpactConcrete(bool force) => Make("PLACEHOLDER_ImpactConcrete", 0.2f, 3, force, (t, n, s) =>
        {
            s.Low += (n - s.Low) * 0.3f;
            return (n - s.Low) * Mathf.Exp(-t * 30f) * 0.9f + Mathf.Sin(2f * Mathf.PI * 160f * t) * Mathf.Exp(-t * 40f) * 0.4f;
        });

        public static AudioClip ImpactMetal(bool force) => Make("PLACEHOLDER_ImpactMetal", 0.35f, 4, force, (t, n, s) =>
        {
            var ring = Mathf.Sin(2f * Mathf.PI * 2300f * t) * 0.25f + Mathf.Sin(2f * Mathf.PI * 3150f * t) * 0.2f
                + Mathf.Sin(2f * Mathf.PI * 4100f * t) * 0.12f;
            return n * Mathf.Exp(-t * 60f) * 0.5f + ring * Mathf.Exp(-t * 18f);
        });

        public static AudioClip ImpactBody(bool force) => Make("PLACEHOLDER_ImpactBody", 0.16f, 5, force, (t, n, s) =>
        {
            s.Low += (n - s.Low) * 0.15f;
            return s.Low * Mathf.Exp(-t * 32f) * 1.4f + Mathf.Sin(2f * Mathf.PI * 110f * t) * Mathf.Exp(-t * 25f) * 0.6f;
        });

        public static AudioClip HealChime(bool force) => Make("PLACEHOLDER_HealChime", 0.6f, 6, force, (t, n, s) =>
        {
            var first = Mathf.Sin(2f * Mathf.PI * 523.25f * t) * Mathf.Exp(-t * 5f) * Mathf.Clamp01(t / 0.02f);
            var u = t - 0.12f;
            var second = u > 0f ? Mathf.Sin(2f * Mathf.PI * 659.25f * u) * Mathf.Exp(-u * 5f) * Mathf.Clamp01(u / 0.02f) * 0.8f : 0f;
            return (first + second) * 0.4f;
        });

        public static AudioClip BodyFall(bool force) => Make("PLACEHOLDER_BodyFall", 0.35f, 7, force, (t, n, s) =>
        {
            s.Low += (n - s.Low) * 0.1f;
            return Mathf.Sin(2f * Mathf.PI * 70f * t) * Mathf.Exp(-t * 14f) + s.Low * Mathf.Exp(-t * 20f) * 0.8f;
        });

        public static void WriteReadme()
        {
            EffectPaths.EnsureFolder(EffectPaths.Audio);
            var path = EffectPaths.Audio + "/README.md";
            if (File.Exists(path))
                return;
            File.WriteAllText(path,
                "# Combat audio placeholders\n\n" +
                "Every `PLACEHOLDER_*.wav` in this folder is generated locally by `Blackglass/Presentation/Build Effects`\n" +
                "(`PlaceholderSounds.cs`): filtered noise bursts and decaying sine tones with fixed seeds. They are original\n" +
                "to this project, there is no external audio and no third-party licence, and they are meant to be replaced.\n\n" +
                "To replace one, import a clip and assign it in the `AudioCue` of the relevant `CombatPresentationProfile`\n" +
                "(`Assets/_Project/Presentation/Profiles`). Nothing in code names these files.\n");
            AssetDatabase.ImportAsset(path);
        }

        sealed class State { public float Low; }

        static AudioClip Make(string name, float seconds, int seed, bool force, Func<float, float, State, float> sample)
        {
            var path = EffectPaths.Audio + "/" + name + ".wav";
            if (!force)
            {
                var existing = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                if (existing != null)
                    return existing;
            }
            EffectPaths.EnsureFolder(EffectPaths.Audio);
            var count = Mathf.CeilToInt(seconds * Rate);
            var data = new float[count];
            var random = new System.Random(seed);
            var state = new State();
            var peak = 0f;
            for (var i = 0; i < count; i++)
            {
                var noise = (float)(random.NextDouble() * 2.0 - 1.0);
                data[i] = sample(i / (float)Rate, noise, state);
                peak = Mathf.Max(peak, Mathf.Abs(data[i]));
            }
            var gain = peak > 0f ? 0.85f / peak : 1f;
            WriteWav(path, data, gain);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (AudioImporter)AssetImporter.GetAtPath(path);
            importer.forceToMono = true;
            importer.loadInBackground = false;
            importer.defaultSampleSettings = new AudioImporterSampleSettings
            {
                loadType = AudioClipLoadType.DecompressOnLoad,
                compressionFormat = AudioCompressionFormat.PCM,
                sampleRateSetting = AudioSampleRateSetting.PreserveSampleRate,
            };
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<AudioClip>(path);
        }

        static void WriteWav(string path, float[] data, float gain)
        {
            using (var stream = new FileStream(path, FileMode.Create))
            using (var writer = new BinaryWriter(stream))
            {
                var bytes = data.Length * 2;
                writer.Write(new[] { 'R', 'I', 'F', 'F' });
                writer.Write(36 + bytes);
                writer.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
                writer.Write(16);
                writer.Write((short)1);       // PCM
                writer.Write((short)1);       // mono
                writer.Write(Rate);
                writer.Write(Rate * 2);       // byte rate
                writer.Write((short)2);       // block align
                writer.Write((short)16);
                writer.Write(new[] { 'd', 'a', 't', 'a' });
                writer.Write(bytes);
                foreach (var sample in data)
                    writer.Write((short)Mathf.Clamp(Mathf.RoundToInt(sample * gain * short.MaxValue), short.MinValue, short.MaxValue));
            }
        }
    }
}
#endif
```

- [ ] **Step 5: Add the audio stage to the builder** — in `CombatPresentationAssetBuilder.Build` after the explosion stage: `BuildAudioStage(force);` and
```csharp
        static void BuildAudioStage(bool force)
        {
            PlaceholderSounds.WriteReadme();
            var shot = AudioCue.Create(0.8f, 100, 0.05f, PlaceholderSounds.RifleShot(force)).WithFalloff(5f, 55f);
            var concrete = AudioCue.Create(0.6f, 160, 0.04f, PlaceholderSounds.ImpactConcrete(force)).WithFalloff(3f, 35f);
            var metal = AudioCue.Create(0.6f, 160, 0.04f, PlaceholderSounds.ImpactMetal(force)).WithFalloff(3f, 40f);
            var body = AudioCue.Create(0.5f, 160, 0.04f, PlaceholderSounds.ImpactBody(force)).WithFalloff(3f, 30f);
            var fall = AudioCue.Create(0.6f, 150, 0.1f, PlaceholderSounds.BodyFall(force)).WithFalloff(3f, 35f);
            var chime = AudioCue.Create(0.6f, 140, 0.1f, PlaceholderSounds.HealChime(force)).WithFalloff(3f, 30f);
            var boom = AudioCue.Create(1f, 40, 0.1f, PlaceholderSounds.Explosion(force)).WithFalloff(8f, 80f);

            var rifle = LoadOrCreateProfile("RifleCombatPresentation");
            if (rifle.FireCue.IsEmpty)
                rifle.SetFireCue(shot);
            rifle.EnsureImpact(ImpactSurfaceKind.Concrete, null, concrete);
            rifle.EnsureImpact(ImpactSurfaceKind.Metal, null, metal);
            rifle.EnsureImpact(ImpactSurfaceKind.Character, null, body);
            rifle.EnsureImpact(ImpactSurfaceKind.Default, null, concrete);
            EditorUtility.SetDirty(rifle);

            var defaults = LoadOrCreateProfile("DefaultCombatPresentation");
            defaults.EnsureImpact(ImpactSurfaceKind.Character, null, body);
            defaults.EnsureImpact(ImpactSurfaceKind.Default, null, concrete);
            if (defaults.DeathCue.IsEmpty)
                defaults.SetDeathCue(fall);
            if (defaults.PulseCue.IsEmpty)
                defaults.SetPulse(defaults.PulseEffect, chime);
            EditorUtility.SetDirty(defaults);

            var blast = LoadOrCreateProfile("BlastCombatPresentation");
            if (blast.AreaCue.IsEmpty)
                blast.SetArea(blast.AreaEffect, blast.ReferenceRadius, boom, blast.CameraImpulse);
            EditorUtility.SetDirty(blast);
        }
```

- [ ] **Step 6: Build, then run** (the builder twice for idempotence):
```bash
Tools/run-method.sh Blackglass.EditorTools.CombatPresentationAssetBuilder.BuildFromCommandLine
Tools/run-method.sh Blackglass.EditorTools.CombatPresentationAssetBuilder.BuildFromCommandLine
Tools/run-tests.sh EditMode "Blackglass.Tests.PlaceholderSoundTests"
Tools/run-tests.sh PlayMode "Blackglass.Tests.CombatAudioPresentationPlayModeTests"
Tools/run-tests.sh PlayMode "Blackglass.Tests.CombatPresenterPlayModeTests"
Tools/run-tests.sh EditMode "Blackglass.Tests.CombatPresentationAssetTests"
```
Expected: all `EXIT=0`. `git status` after the second build run shows no new churn.

- [ ] **Step 7: Listen is not possible in batch mode** — say so in the report; audio is verified by the tests above (voice bookkeeping, gating, pause) and the owner must listen to the placeholders (manual check).

---

### Task 13: Heal pulse (the supportive-action cue)

**Files:**
- Modify: `Editor/Presentation/EffectPrefabs.cs` (`HealPulse`), `Editor/Presentation/CombatPresentationAssetBuilder.cs` (pulse stage), `Scripts/Presentation/PresentationPreviewPanel.cs`, `Editor/Presentation/CombatPresentationSceneBuilder.cs`
- Generated: `Prefabs/HealPulse.prefab`; modified `DefaultCombatPresentation` (pulse prefab)
- Test: `Tests/PlayMode/HealPulsePlayModeTests.cs` (+ one asset case added to `CombatPresentationAssetTests`)

**Interfaces:**
- Consumes: `Health.Healed`, `UnitPresentation.OnHealed` (Task 10), `CombatPresenter.ShowPulse` (Task 7), `PresentationRig`.
- Produces: `PresentationPreviewPanel.HealDummy()`; the pulse prefab and its profile entry.

- [ ] **Step 1: Write the failing tests** — `Tests/PlayMode/HealPulsePlayModeTests.cs`:
```csharp
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class HealPulsePlayModeTests
    {
        PresentationRig rig;

        [TearDown]
        public void TearDown() => rig?.Dispose();

        [UnityTest]
        public IEnumerator HealingARealWound_ShowsOnePulse_AndAtFullHealthShowsNothing()
        {
            rig = new PresentationRig();
            var unit = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;

            Assert.That(unit.Heal(10), Is.EqualTo(0), "Precondition: full health, nothing restored");
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Pulse), Is.EqualTo(0));

            unit.TakeDamage(30);
            Assert.That(unit.Heal(30), Is.EqualTo(30));
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Pulse), Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator Mend_PlaysThePulseWhenItSucceeds_NotWhenQueuedOrRefused_AndHealingIsUnchanged()
        {
            rig = new PresentationRig();
            var caster = rig.CreateShooter(new Vector3(0f, 0f, -4f));
            var ally = rig.CreateTarget(new Vector3(2f, 0f, -4f));
            var encounter = rig.World.CreateEncounter();
            encounter.Initialize(new[] { caster.GetComponent<Health>(), ally }, new Health[0]);
            var mend = rig.World.CreateMend();
            var abilities = rig.World.AddAbilities(caster, encounter, mend);
            ally.TakeDamage(50);
            yield return null;
            rig.Presenter.Clear();

            Assert.That(abilities.CanQueue(AbilityCommand.OnUnit(mend, ally)), Is.True, "queuing is only a check");
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Pulse), Is.EqualTo(0));

            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(mend, ally)), Is.True);
            Assert.That(ally.Current, Is.EqualTo(ally.Max - 10), "Mend restores 40 hit points, exactly as before");
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Pulse), Is.EqualTo(1));

            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(mend, ally)), Is.False, "on cooldown: refused");
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Pulse), Is.EqualTo(1), "a refused use shows nothing");
        }

        [UnityTest]
        public IEnumerator PulseOnAHiddenUnit_IsNotShown()
        {
            rig = new PresentationRig();
            var unit = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            unit.TakeDamage(30);
            rig.Presenter.UseKnowledge(_ => PointVisibility.Visible, _ => false);
            unit.Heal(30);
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Pulse), Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator Pulse_FreezesWithThePause()
        {
            rig = new PresentationRig();
            var unit = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            unit.TakeDamage(30);
            unit.Heal(30);
            var effect = Object.FindFirstObjectByType<PooledEffect>(FindObjectsInactive.Exclude);
            rig.Pause.Pause();
            yield return new WaitForSecondsRealtime(0.4f);
            Assert.That(effect.IsActive, Is.True);
            Assert.That(effect.Expired, Is.False);
            rig.Pause.Resume();
        }
    }
}
#endif
```
Add to `CombatPresentationAssetTests`: `"HealPulse"` and `"ExplosionGrenade"` are not in `RiflePrefabs`; add a second `TestCaseSource` array `OtherPrefabs = { "HealPulse", "ExplosionGrenade" }` driving the same pool-ready assertions (rename the test accordingly, keeping the body), and a test that `DefaultCombatPresentation.PulseEffect` is the `HealPulse` prefab.

- [ ] **Step 2: Run to verify failure** — `Tools/run-tests.sh PlayMode "Blackglass.Tests.HealPulsePlayModeTests"`. The rig uses stand-in prefabs, so these tests should already pass after Task 10 (the handler exists); if one fails, the cause is in `OnHealed` / `ShowPulse`, so fix that before moving on. The new asset-test case fails until Step 3.

- [ ] **Step 3: Add the prefab** — in `EffectPrefabs.cs`:
```csharp
        public static GameObject HealPulse(EffectKit kit, bool force) => Build("HealPulse", force, root =>
        {
            ParticleAuthoring.Build(root.transform, new PsSpec
            {
                Name = "Ring", Material = kit.Ring, Render = ParticleSystemRenderMode.HorizontalBillboard, Burst = 1, Duration = 0.2f,
                ShapeEnabled = false, RandomRotation = false,
                Lifetime = new Vector2(0.6f, 0.6f), Size = new Vector2(1.8f, 1.8f),
                StartColor = new Color(0.55f, 1f, 0.7f, 0.8f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.3f),
                SizeOverLifetime = ParticleAuthoring.Grow(0.35f, 1f),
            });
            ParticleAuthoring.Build(root.transform, new PsSpec
            {
                Name = "Motes", Material = kit.Glow, Burst = 10, Duration = 0.3f,
                Lifetime = new Vector2(0.6f, 0.9f), Size = new Vector2(0.05f, 0.09f), Speed = new Vector2(0.6f, 1.4f),
                Shape = ParticleSystemShapeType.Cone, ShapeAngle = 20f, ShapeRadius = 0.35f, Gravity = -0.1f,
                LocalEuler = new Vector3(-90f, 0f, 0f),
                StartColor = new Color(0.6f, 1f, 0.75f, 1f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.4f),
            });
        });
```
and in the builder add after the audio stage:
```csharp
        static void BuildPulseStage(EffectKit kit, bool force)
        {
            var pulse = EffectPrefabs.HealPulse(kit, force);
            var defaults = LoadOrCreateProfile("DefaultCombatPresentation");
            if (defaults.PulseEffect == null)
                defaults.SetPulse(pulse, defaults.PulseCue);
            EditorUtility.SetDirty(defaults);
        }
```
called from `Build` **before** `BuildAudioStage` is irrelevant: both stages fill only empty fields (the pulse cue and prefab are independent). Run the builder twice, then `Tools/run-tests.sh EditMode "Blackglass.Tests.CombatPresentationAssetTests"` and `Tools/run-tests.sh PlayMode "Blackglass.Tests.HealPulsePlayModeTests"`.

- [ ] **Step 4: Preview scene** — add `[SerializeField] Health healee;` to the panel, `public void HealDummy() { healee.TakeDamage(40); healee.Heal(40); }`, a button `"Heal pulse (real Health.Heal)"`, and in the scene builder a `Healee` capsule at `(6, 1, -3)` (Health 100000, `UnitPresentation`) wired to the panel. Add a scene test:
```csharp
        [UnityTest]
        public IEnumerator HealDummy_ShowsAPulse_ThroughTheRealHealingPath()
        {
            var panel = Find<PresentationPreviewPanel>();
            panel.HealDummy();
            Assert.That(Find<CombatPresenter>().ActiveOf(EffectCategory.Pulse), Is.EqualTo(1));
            yield return null;
        }
```
Rebuild the scene and run `Tools/run-tests.sh PlayMode "Blackglass.Tests.PresentationPreviewScenePlayModeTests"`.

- [ ] **Step 5: Look at it** — add a capture case for the pulse and the character hit on `Healee`/`Target`, read the PNGs, tune, rebuild.

- [ ] **Step 6: Milestone C gate:** `Tools/run-tests.sh EditMode` and `Tools/run-tests.sh PlayMode` green; totals = baseline + added tests.

---

### Task 14: Production wiring — `MissionSystems`, mission cleanup, spawner, the ProceduralMission scene

**Files:**
- Modify: `Scripts/Mission/MissionSlots.cs` (`MissionSystems`), `Scripts/Mission/MissionDirector.cs` (`ResetSystems`), `Scripts/Mission/MissionSpawner.cs`, `Editor/Presentation/CombatPresentationSceneBuilder.cs` (`WireProceduralMission`), `Tests/PlayMode/TestSupport/MissionRig.cs` (`AddPresentation`)
- Generated: modified `Assets/_Project/Scenes/ProceduralMission.unity`
- Test: `Tests/PlayMode/MissionPresentationPlayModeTests.cs`, `Tests/EditMode/ProceduralMissionPresentationWiringTests.cs`

**Interfaces:**
- Consumes: `CombatPresenter`, `UnitPresentation`, `CameraImpulse`, the generated profiles (Tasks 7–13).
- Produces: `MissionSystems.presentation : CombatPresenter` (optional); every mission-spawned unit carries a bound `UnitPresentation` when it is set; `MissionDirector.ResetSystems` calls `presentation.Clear()`; `MissionRig.AddPresentation(IntelligenceService intelligence = null) : CombatPresenter`; menu `Blackglass/Presentation/Wire ProceduralMission Scene` and batch entry `Blackglass.EditorTools.CombatPresentationSceneBuilder.WireProceduralMissionFromCommandLine`.

- [ ] **Step 1: Write the failing tests**

`Tests/PlayMode/MissionPresentationPlayModeTests.cs`:
```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class MissionPresentationPlayModeTests
    {
        MissionRig rig;

        [TearDown]
        public void TearDown() => rig?.Dispose();

        static ShotRecord SyntheticShot(CommandableUnit shooter, CommandableUnit target, int sequence) =>
            new ShotRecord(shooter.GetInstanceID(), sequence, shooter.GetComponent<Health>(), target.GetComponent<Health>(),
                shooter.transform.position + Vector3.up * 0.5f, target.transform.position + Vector3.up * ShotRecord.BodyHeight, ShotOutcome.Hit, false, null);

        [UnityTest]
        public IEnumerator EveryMissionUnit_GetsAUnitPresentation_BoundToThePresenter()
        {
            rig = new MissionRig();
            var presenter = rig.AddPresentation();
            yield return rig.Generate(7);
            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready));
            foreach (var unit in rig.Director.Friendlies.Concat(rig.Director.Hostiles))
            {
                Assert.That(unit.TryGetComponent(out UnitPresentation presentation), Is.True, unit.name);
                Assert.That(presentation.Presenter, Is.EqualTo(presenter), unit.name);
            }
        }

        [UnityTest]
        public IEnumerator WithoutAPresenter_NoUnitGetsAUnitPresentation()
        {
            rig = new MissionRig();
            yield return rig.Generate(7);
            foreach (var unit in rig.Director.Friendlies.Concat(rig.Director.Hostiles))
                Assert.That(unit.GetComponent<UnitPresentation>(), Is.Null, unit.name);
        }

        [UnityTest]
        public IEnumerator Regeneration_ReleasesEveryEffect_StopsAudio_AndLeavesNoStaleSubscriptions()
        {
            rig = new MissionRig();
            var presenter = rig.AddPresentation();
            yield return rig.Generate(7);
            var shooter = rig.Director.Friendlies[0];
            var target = rig.Director.Hostiles[0];
            shooter.GetComponent<UnitPresentation>().Deliver(SyntheticShot(shooter, target, 1));
            Assert.That(presenter.ActiveEffects, Is.GreaterThan(0), "Precondition: something is playing");

            yield return rig.Generate(11);

            Assert.That(presenter.ActiveEffects, Is.EqualTo(0));
            Assert.That(presenter.Budget.LightsActive, Is.EqualTo(0));
            Assert.That(presenter.Audio.ActiveVoices, Is.EqualTo(0));
            Assert.That(Object.FindObjectsByType<UnitPresentation>(FindObjectsSortMode.None).Length,
                Is.EqualTo(rig.Director.Friendlies.Count + rig.Director.Hostiles.Count), "only the new mission's units remain");
        }

        [UnityTest]
        public IEnumerator RegenerationWhilePaused_ClearsAndResumes_WithoutRestartingOrLeaking()
        {
            rig = new MissionRig();
            var presenter = rig.AddPresentation();
            yield return rig.Generate(7);
            var shooter = rig.Director.Friendlies[0];
            var target = rig.Director.Hostiles[0];
            shooter.GetComponent<UnitPresentation>().Deliver(SyntheticShot(shooter, target, 1));
            rig.Pause.Pause();
            Assert.That(presenter.Audio.IsPaused, Is.True);

            yield return rig.Generate(11);

            Assert.That(rig.Pause.IsPaused, Is.False, "the director resumes the pause service");
            Assert.That(presenter.Audio.IsPaused, Is.False);
            Assert.That(presenter.ActiveEffects, Is.EqualTo(0));
            Assert.That(Time.timeScale, Is.EqualTo(1f));
        }

        [UnityTest]
        public IEnumerator PresenterOff_LeavesTheGeneratedMissionIdentical()
        {
            rig = new MissionRig();
            var presenter = rig.AddPresentation();
            presenter.Enabled = false;
            yield return rig.Generate(5);
            var withLayout = rig.Director.Current.Layout.Rooms.Count;
            var friendlies = rig.Director.Friendlies.Count;
            var hostiles = rig.Director.Hostiles.Count;
            rig.Dispose();

            rig = new MissionRig();
            yield return rig.Generate(5);
            Assert.That(rig.Director.Current.Layout.Rooms.Count, Is.EqualTo(withLayout));
            Assert.That(rig.Director.Friendlies.Count, Is.EqualTo(friendlies));
            Assert.That(rig.Director.Hostiles.Count, Is.EqualTo(hostiles));
        }
    }
}
#endif
```

`Tests/EditMode/ProceduralMissionPresentationWiringTests.cs`:
```csharp
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Blackglass.Tests
{
    public class ProceduralMissionPresentationWiringTests
    {
        const string ScenePath = "Assets/_Project/Scenes/ProceduralMission.unity";
        Scene scene;

        [SetUp]
        public void Open() => scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);

        [TearDown]
        public void Close() => EditorSceneManager.CloseScene(scene, true);

        T One<T>() where T : Component => scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<T>(true)).Single();

        [Test]
        public void ThePresenter_IsWiredToTheScenesSystems()
        {
            var presenter = new SerializedObject(One<CombatPresenter>());
            Assert.That(presenter.FindProperty("pause").objectReferenceValue, Is.EqualTo(One<TacticalPause>()));
            Assert.That(presenter.FindProperty("intelligence").objectReferenceValue, Is.EqualTo(One<IntelligenceService>()));
            Assert.That(presenter.FindProperty("cameraImpulse").objectReferenceValue, Is.EqualTo(One<CameraImpulse>()));
            Assert.That(presenter.FindProperty("fallbackProfile").objectReferenceValue, Is.Not.Null);
            Assert.That(presenter.FindProperty("supportProfile").objectReferenceValue, Is.Not.Null);
        }

        [Test]
        public void TheDirector_AndTheCameraController_KnowThePresentation()
        {
            var director = new SerializedObject(One<MissionDirector>());
            Assert.That(director.FindProperty("systems.presentation").objectReferenceValue, Is.EqualTo(One<CombatPresenter>()));
            var controller = new SerializedObject(One<TacticalCameraController>());
            Assert.That(controller.FindProperty("impulse").objectReferenceValue, Is.EqualTo(One<CameraImpulse>()));
        }

        [Test]
        public void TheSceneStillHasExactlyOneAudioListener()
        {
            Assert.That(scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<AudioListener>(true)).Count(), Is.EqualTo(1));
        }
    }
}
```

- [ ] **Step 2: Run to verify failure** — `Tools/run-tests.sh PlayMode "Blackglass.Tests.MissionPresentationPlayModeTests"` → `EXIT=1` (`MissionRig.AddPresentation`, `MissionSystems.presentation` missing).

- [ ] **Step 3: Production code**

`MissionSlots.cs` — in `MissionSystems` add (and mention it in the summary comment as optional):
```csharp
        public CombatPresenter presentation;
```
`MissionDirector.ResetSystems` — first statement:
```csharp
            if (systems.presentation != null)
                systems.presentation.Clear();
```
`MissionSpawner.TrySpawn` — in the friendly loop, immediately before `result.Friendlies.Add(unit);`:
```csharp
                if (systems.presentation != null)
                    unit.gameObject.AddComponent<UnitPresentation>().Bind(systems.presentation);
```
and in the hostile loop, immediately before `result.Hostiles.Add(unit);`:
```csharp
                if (systems.presentation != null)
                    unit.gameObject.AddComponent<UnitPresentation>().Bind(systems.presentation);
```
(The unit is inactive under the inactive Actors object, so `Bind` only stores the reference; `OnEnable` subscribes when the mission activates, after every other component is wired.)

`MissionRig.cs` — add:
```csharp
        /// <summary>Adds a combat presenter (with the real fallback profile) to the persistent systems. Call before the first Generate.</summary>
        public CombatPresenter AddPresentation(IntelligenceService intelligence = null)
        {
            var host = World.Track(new GameObject("Presentation"));
            host.SetActive(false);
            var presenter = host.AddComponent<CombatPresenter>();
            var fallback = Load<CombatPresentationProfile>("Presentation/Profiles/DefaultCombatPresentation.asset");
            presenter.Bind(intelligence != null ? intelligence : systems.intelligence, Pause, null, fallback, fallback);
            host.SetActive(true);
            systems.presentation = presenter;
            return presenter;
        }
```

- [ ] **Step 4: Add the scene wiring** to `CombatPresentationSceneBuilder`:
```csharp
        public const string MissionScenePath = "Assets/_Project/Scenes/ProceduralMission.unity";

        [MenuItem("Blackglass/Presentation/Wire ProceduralMission Scene")]
        public static void WireProceduralMissionMenu() => WireProceduralMission();

        /// <summary>Unity -batchmode -executeMethod entry.</summary>
        public static void WireProceduralMissionFromCommandLine() => WireProceduralMission();

        public static void WireProceduralMission()
        {
            // Open the scene first: opening it in Single mode unloads unreferenced assets.
            var scene = EditorSceneManager.OpenScene(MissionScenePath, OpenSceneMode.Single);
            var director = FindOne<MissionDirector>();
            var pause = FindOne<TacticalPause>();
            var intelligence = FindOne<IntelligenceService>();
            var controller = FindOne<TacticalCameraController>();

            var presenter = UnityEngine.Object.FindFirstObjectByType<CombatPresenter>();
            if (presenter == null)
                presenter = new GameObject("Presentation").AddComponent<CombatPresenter>();
            var impulse = controller.GetComponent<CameraImpulse>();
            if (impulse == null)
                impulse = controller.gameObject.AddComponent<CameraImpulse>();

            var fallback = Load<CombatPresentationProfile>(ProfilesRoot + "DefaultCombatPresentation.asset");
            var presenterObject = new SerializedObject(presenter);
            Set(presenterObject, "pause", pause);
            Set(presenterObject, "intelligence", intelligence);
            Set(presenterObject, "cameraImpulse", impulse);
            Set(presenterObject, "fallbackProfile", fallback);
            Set(presenterObject, "supportProfile", fallback);
            presenterObject.ApplyModifiedPropertiesWithoutUndo();

            var controllerObject = new SerializedObject(controller);
            Set(controllerObject, "impulse", impulse);
            controllerObject.ApplyModifiedPropertiesWithoutUndo();

            var directorObject = new SerializedObject(director);
            Set(directorObject, "systems.presentation", presenter);
            directorObject.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
                throw new InvalidOperationException("Could not save " + MissionScenePath + ".");
        }

        static T FindOne<T>() where T : Component
        {
            var found = UnityEngine.Object.FindFirstObjectByType<T>();
            if (found == null)
                throw new InvalidOperationException($"ProceduralMission needs a {typeof(T).Name}.");
            return found;
        }
```
(`Set` takes a nested property path: `Set(serialized, "systems.presentation", …)` works because `FindProperty` accepts dotted paths; the existing `Set` already throws on a missing field.) Run it, then re-run it and confirm the scene diff does not grow:
```bash
Tools/run-method.sh Blackglass.EditorTools.CombatPresentationSceneBuilder.WireProceduralMissionFromCommandLine
Tools/run-method.sh Blackglass.EditorTools.CombatPresentationSceneBuilder.WireProceduralMissionFromCommandLine
git diff --stat Assets/_Project/Scenes/ProceduralMission.unity
```
Expected: `EXIT=0` twice; the second run changes nothing.

- [ ] **Step 5: Run**
```bash
Tools/run-tests.sh PlayMode "Blackglass.Tests.MissionPresentationPlayModeTests"
Tools/run-tests.sh EditMode "Blackglass.Tests.ProceduralMissionPresentationWiringTests"
```
Expected: `EXIT=0`.

- [ ] **Step 6: Regression on the systems this touched** (mission lifecycle, spawner, HUD, intelligence):
```bash
Tools/run-tests.sh PlayMode "Blackglass.Tests.MissionDirector"
Tools/run-tests.sh PlayMode "Blackglass.Tests.HostileWeaponFogPlayModeTests"
Tools/run-tests.sh PlayMode "Blackglass.Tests.DariusMissionPlayModeTests"
```
(If the first filter matches nothing, list the mission tests with `ls Assets/_Project/Tests/PlayMode | grep -i mission` and run each.) Expected `EXIT=0`.

---

### Task 15: Hardening — pause with the real assets, fog with the real knowledge model, budgets, presentation-off regression, source and prefab audits

**Files:**
- Modify: `Tests/PlayMode/TestSupport/PresentationRig.cs` (shared world, shooter cooldown)
- Test: `Tests/PlayMode/PresentationPauseRealAssetsPlayModeTests.cs`, `PresentationFogRealKnowledgePlayModeTests.cs`, `PresentationBudgetPlayModeTests.cs`, `PresentationRegressionPlayModeTests.cs`, `Tests/EditMode/PresentationSourceAuditTests.cs`
- Fix whatever these find in the earlier tasks' code (use `superpowers:systematic-debugging`).

**Interfaces:**
- Consumes: everything. Produces: only tests (and fixes).

- [ ] **Step 1: Let the rig share a world and choose a cooldown** — `PresentationRig` constructor becomes `PresentationRig(bool withWall = false, IntelligenceService intelligence = null, TestWorld world = null, bool createEnvironment = true)`: `World = world != null ? world : new TestWorld(); ownsWorld = world == null;` (make `World` a readonly field assigned in the constructor); skip `CreateEnvironment` and the `ImpactSurface` tagging when `createEnvironment` is false (then `EnvironmentRoot` is null); `Dispose()` disposes the world only when `ownsWorld`. `CreateShooter` gets a trailing `float cooldown = 0.1f` passed to `World.CreateFighter`. Re-run `CombatPresenterPlayModeTests` and `CombatAudioPresentationPlayModeTests` (green).

- [ ] **Step 2: Pause with the real generated assets** — `Tests/PlayMode/PresentationPauseRealAssetsPlayModeTests.cs` (loads the Presentation Test Scene; same `Load`/`Unload`/`Find<T>` helpers as `PresentationPreviewScenePlayModeTests`):
```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class PresentationPauseRealAssetsPlayModeTests
    {
        Scene scene;

        [UnitySetUp]
        public IEnumerator Load()
        {
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(PresentationPreviewScenePlayModeTests.ScenePath,
                new LoadSceneParameters(LoadSceneMode.Additive));
            scene = SceneManager.GetSceneByPath(PresentationPreviewScenePlayModeTests.ScenePath);
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator Unload()
        {
            Time.timeScale = 1f;
            if (scene.isLoaded)
                yield return SceneManager.UnloadSceneAsync(scene);
        }

        T Find<T>() where T : Component => scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<T>(true)).First();

        [UnityTest]
        public IEnumerator EverythingFreezesAtPause_AndContinuesAfterwards_NothingRestartsOrIsReleasedEarly()
        {
            var panel = Find<PresentationPreviewPanel>();
            var presenter = Find<CombatPresenter>();
            panel.FireAtCharacter();
            panel.CastBlast();
            panel.HealDummy();
            yield return new WaitForSeconds(0.08f);
            var effects = Object.FindObjectsByType<PooledEffect>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            var count = effects.Length;
            Assert.That(count, Is.GreaterThan(3), "Precondition: muzzle, tracer, impact, explosion and pulse are playing");
            var smoke = effects.Where(e => e.Category == EffectCategory.Area).SelectMany(e => e.Particles).First(p => p.name == "Smoke");
            var smokeTime = smoke.time;
            var smokeCount = smoke.particleCount;
            var lightIntensity = effects.SelectMany(e => e.Lights).Where(l => l.enabled).Select(l => l.intensity).ToArray();
            var voices = presenter.Audio.ActiveVoices;

            panel.TogglePause();
            yield return null;
            var impulse = Find<CameraImpulse>().LocalOffset;
            yield return new WaitForSecondsRealtime(0.6f);

            Assert.That(Object.FindObjectsByType<PooledEffect>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length, Is.EqualTo(count),
                "no effect was released or created during the pause");
            Assert.That(smoke.time, Is.EqualTo(smokeTime).Within(0.03f));
            Assert.That(smoke.particleCount, Is.EqualTo(smokeCount));
            Assert.That(effects.SelectMany(e => e.Lights).Where(l => l.enabled).Select(l => l.intensity).ToArray(), Is.EqualTo(lightIntensity).Within(0.01f));
            Assert.That(presenter.Audio.ActiveVoices, Is.EqualTo(voices));
            Assert.That(Find<CameraImpulse>().LocalOffset, Is.EqualTo(impulse));

            for (var i = 0; i < 20; i++)
                panel.TogglePause();   // an even number: still paused, repeatedly toggled
            Assert.That(Time.timeScale, Is.EqualTo(0f));
            Assert.That(presenter.ActiveEffects, Is.EqualTo(count), "pause spam creates nothing");

            panel.TogglePause();
            yield return new WaitForSeconds(0.25f);
            Assert.That(smoke.time, Is.GreaterThan(smokeTime), "the explosion's smoke continued, it did not restart");
            Assert.That(smoke.particleCount, Is.GreaterThanOrEqualTo(1));
        }

        [UnityTest]
        public IEnumerator AfterTheLifetimes_EverythingIsReleased_AndBudgetsAreBackToZero()
        {
            var panel = Find<PresentationPreviewPanel>();
            var presenter = Find<CombatPresenter>();
            panel.FireAtCharacter();
            panel.CastBlast();
            panel.HealDummy();
            yield return new WaitForSeconds(5f);
            Assert.That(presenter.ActiveEffects, Is.EqualTo(0));
            Assert.That(presenter.Budget.LightsActive, Is.EqualTo(0));
            foreach (EffectCategory category in System.Enum.GetValues(typeof(EffectCategory)))
                Assert.That(presenter.Budget.Active(category), Is.EqualTo(0), category.ToString());
        }
    }
}
#endif
```

- [ ] **Step 3: Fog with the real knowledge model** — `PresentationFogRealKnowledgePlayModeTests.cs`:
```csharp
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class PresentationFogRealKnowledgePlayModeTests
    {
        IntelRig intel;
        PresentationRig rig;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            intel?.Dispose();
        }

        // A friendly in room A, a ranged hostile 13 m away in room B (out of the 8 m observation range), the real service.
        (Health shooter, UnitAttacker attacker) Setup()
        {
            intel = new IntelRig(corridor: true);
            var (shooter, attacker) = intel.AddShooter(IntelRig.InLineGround);
            intel.Begin(IntelRig.Fog(s => { s.observationRange = 8f; s.exposureSeconds = 1f; }));
            rig = new PresentationRig(intelligence: intel.Service, world: intel.World, createEnvironment: false);
            shooter.gameObject.AddComponent<UnitPresentation>().Bind(rig.Presenter);
            intel.Friendly.gameObject.AddComponent<UnitPresentation>().Bind(rig.Presenter);
            intel.Friendly.GetComponent<UnitAttacker>().Initialize(40f, 10, 0f, CombatRole.Ranged);   // the friend can shoot back across the corridor
            return (shooter, attacker);
        }

        [UnityTest]
        public IEnumerator Decision037_AHostilesOwnShot_ShowsItsFlashAndTheFriendsHit_PresentationAddsNoReveal()
        {
            var (shooter, attacker) = Setup();
            yield return null;
            Assert.That(rig.Presenter.Observes(shooter), Is.False, "Precondition: the shooter is not observed before it fires");

            Assert.That(attacker.TryAttack(intel.FriendlyHealth), Is.True);

            Assert.That(rig.Presenter.Observes(shooter), Is.True, "037 exposed it inside Attacked, before Fired");
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Muzzle), Is.EqualTo(1));
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Impact), Is.EqualTo(1), "the visible friend shows its hit");

            yield return new WaitForSeconds(1.4f);
            Assert.That(rig.Presenter.Observes(shooter), Is.False, "exposure ran out: nothing presentation did kept it alive");
        }

        [UnityTest]
        public IEnumerator AVisibleFriendFiringIntoUnknownGround_ShowsItsFlash_ButNoTracerImpactOrHitSound()
        {
            var (shooter, _) = Setup();
            yield return null;
            Assert.That(rig.Presenter.Observes(shooter), Is.False);
            var friendAttacker = intel.Friendly.GetComponent<UnitAttacker>();
            // The friend shoots a unit standing in room B, which nobody has seen.
            var hostileHealth = shooter;
            friendAttacker.TryAttack(hostileHealth);
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Muzzle), Is.EqualTo(1));
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Tracer), Is.EqualTo(0), "the path ends in unobserved ground");
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Impact), Is.EqualTo(0), "the victim is not observed");
            Assert.That(rig.Presenter.Observes(shooter), Is.False, "firing at a hidden unit did not reveal it");
        }

        [UnityTest]
        public IEnumerator ADiscoveredButUnobservedRoom_ShowsNoLiveExplosion()
        {
            Setup();
            yield return null;
            intel.Service.Model.RevealRegion(1);   // room B is on the map but nobody is looking at it
            Assert.That(intel.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Discovered));
            var inRoomB = IntelRig.InLineGround;
            var record = new AreaRecord(1, 1, intel.FriendlyHealth, rig.World.CreateBlast(), inRoomB, 3f);
            intel.Friendly.GetComponent<UnitPresentation>().Deliver(record);
            Assert.That(rig.Presenter.ActiveEffects, Is.EqualTo(0));
            Assert.That(rig.Presenter.Audio.ActiveVoices, Is.EqualTo(0));
            Assert.That(rig.Impulse.LocalOffset, Is.EqualTo(Vector3.zero));
        }

        [UnityTest]
        public IEnumerator TheTruthView_NeverUnlocksCombatEffects()
        {
            var (shooter, _) = Setup();
            yield return null;
            intel.Service.TruthView = true;
            Assert.That(intel.Service.IsUnitShown(shooter), Is.True, "Precondition: the truth view draws everything");
            Assert.That(rig.Presenter.Observes(shooter), Is.False);
            Assert.That(intel.Service.VisibilityOfPoint(IntelRig.InLineGround), Is.EqualTo(PointVisibility.Hidden));
            Assert.That(intel.Service.VisibilityOfPoint(IntelRig.FriendlyGround), Is.EqualTo(PointVisibility.Visible));
            Assert.That(intel.Service.VisibilityOfPoint(new Vector3(500f, 0f, 500f)), Is.EqualTo(PointVisibility.Outside));
        }

        [UnityTest]
        public IEnumerator HiddenOneShotEvents_AreDropped_NotReplayedWhenTheAreaIsLaterSeen()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(12f, 0f, -6f));
            var target = rig.CreateTarget(new Vector3(12f, 0f, 8f));
            yield return null;
            rig.Presenter.UseKnowledge(p => p.x > 5f ? PointVisibility.Hidden : PointVisibility.Visible, u => u.transform.position.x <= 5f);
            shooter.GetComponent<UnitAttacker>().TryAttack(target);
            Assert.That(rig.Presenter.ActiveEffects, Is.EqualTo(0));

            rig.Presenter.ClearKnowledgeOverride();   // the area becomes visible
            rig.Presenter.Regate();
            yield return null;
            Assert.That(rig.Presenter.ActiveEffects, Is.EqualTo(0), "nothing buffered, nothing replayed");
        }

        [UnityTest]
        public IEnumerator LingeringEffects_AreHiddenAndShownAgain_WithoutRestarting()
        {
            rig = new PresentationRig();
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            target.TakeDamage(10);   // spawns a lingering default burst
            yield return null;
            var effect = Object.FindFirstObjectByType<PooledEffect>(FindObjectsInactive.Exclude);
            Assert.That(effect, Is.Not.Null);
            var time = effect.Particles[0].time;

            rig.Presenter.UseKnowledge(_ => PointVisibility.Hidden, _ => true);
            rig.Presenter.Regate();
            Assert.That(effect.IsVisible, Is.False);
            Assert.That(effect.IsActive, Is.True, "hidden, not released");

            rig.Presenter.ClearKnowledgeOverride();
            rig.Presenter.Regate();
            yield return null;
            Assert.That(effect.IsVisible, Is.True);
            Assert.That(effect.Particles[0].time, Is.GreaterThanOrEqualTo(time), "it kept simulating; it was not restarted");
        }
    }
}
#endif
```

- [ ] **Step 4: Budgets under stress** — `PresentationBudgetPlayModeTests.cs`:
```csharp
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class PresentationBudgetPlayModeTests
    {
        PresentationRig rig;

        [TearDown]
        public void TearDown() => rig?.Dispose();

        [UnityTest]
        public IEnumerator HundredsOfShots_StayInsideEveryCap_AndNeverChangeTheDamageDealt()
        {
            rig = new PresentationRig();
            var target = rig.CreateTarget(new Vector3(0f, 0f, 12f));
            var shooters = new CommandableUnit[10];
            for (var i = 0; i < shooters.Length; i++)
                shooters[i] = rig.CreateShooter(new Vector3(-9f + 2f * i, 0f, -6f), damage: 1, cooldown: 0f);
            yield return null;

            var shots = 0;
            for (var round = 0; round < 25; round++)
                foreach (var shooter in shooters)
                    if (shooter.GetComponent<UnitAttacker>().TryAttack(target))
                        shots++;

            Assert.That(shots, Is.EqualTo(250));
            Assert.That(target.Current, Is.EqualTo(target.Max - 250), "every shot dealt exactly its damage whatever the budgets did");
            foreach (EffectCategory category in System.Enum.GetValues(typeof(EffectCategory)))
                Assert.That(rig.Presenter.ActiveOf(category), Is.LessThanOrEqualTo(rig.Presenter.Budget.Cap(category)), category.ToString());
            Assert.That(rig.Presenter.Budget.LightsActive, Is.LessThanOrEqualTo(rig.Presenter.Budget.LightCap));
            Assert.That(rig.Presenter.Audio.ActiveVoices, Is.LessThanOrEqualTo(rig.Presenter.Audio.VoiceCount));
            Assert.That(rig.Presenter.Budget.Refused, Is.GreaterThan(0), "the caps were actually hit");
        }

        [UnityTest]
        public IEnumerator QualityLow_HasSmallerCaps_AndSwitchingClearsCleanly()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f), cooldown: 0f);
            var target = rig.CreateTarget(new Vector3(0f, 0f, 12f));
            yield return null;
            rig.Presenter.SetQuality(PresentationQuality.Low);
            for (var i = 0; i < 30; i++)
                shooter.GetComponent<UnitAttacker>().TryAttack(target);
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Muzzle), Is.LessThanOrEqualTo(rig.Presenter.Budget.Cap(EffectCategory.Muzzle)));
            Assert.That(rig.Presenter.Budget.Cap(EffectCategory.Muzzle), Is.LessThan(new EffectBudget(PresentationQuality.High).Cap(EffectCategory.Muzzle)));
            rig.Presenter.SetQuality(PresentationQuality.High);
            Assert.That(rig.Presenter.ActiveEffects, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator ATracerPrefabWithoutATracerView_WarnsOnce_AndTheShotStillResolves()
        {
            rig = new PresentationRig();
            var brokenTracer = rig.Fx("BrokenTracer");   // a particle system, no TracerView
            rig.RifleProfile.SetTracer(TracerStyle.Create(brokenTracer));
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f), cooldown: 0f);
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("no TracerView"));
            for (var i = 0; i < 5; i++)
                Assert.That(shooter.GetComponent<UnitAttacker>().TryAttack(target), Is.True);
            Assert.That(target.Current, Is.EqualTo(target.Max - 50));
            LogAssert.NoUnexpectedReceived();
        }
    }
}
#endif
```

- [ ] **Step 5: The presentation-off regression and the no-replay check** — `PresentationRegressionPlayModeTests.cs`:
```csharp
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class PresentationRegressionPlayModeTests
    {
        struct Outcome
        {
            public int Shots, Hits, TargetHealth, SecondHealth, AbilityUses;
            public float Cooldown, RandomA, RandomB, RandomC;
        }

        // The same scripted fight, with presentation on or off. Gameplay uses Random.value for every shot's roll, so the
        // stream position at the end proves presentation consumed none.
        IEnumerator Fight(bool presentation, System.Action<Outcome> done)
        {
            using (var rig = new PresentationRig(withWall: true))
            {
                rig.Presenter.Enabled = presentation;
                var shooter = rig.CreateShooter(new Vector3(0f, 0f, 6f), damage: 7, cooldown: 0f);
                var target = rig.CreateTarget(new Vector3(0f, 0f, 9f));
                var second = rig.CreateTarget(new Vector3(3f, 0f, 9f));
                var encounter = rig.World.CreateEncounter();
                encounter.Initialize(new[] { shooter.GetComponent<Health>() }, new[] { target, second });
                var blast = rig.World.CreateBlast();
                var abilities = rig.World.AddAbilities(shooter, encounter, blast);
                yield return null;

                Random.InitState(4321);
                var attacker = shooter.GetComponent<UnitAttacker>();
                for (var i = 0; i < 12; i++)
                    attacker.TryAttack(i % 2 == 0 ? target : second);
                abilities.TryUse(AbilityCommand.AtGround(blast, new Vector3(1f, 0f, 9f)));
                target.Heal(5);
                yield return new WaitForSeconds(0.2f);

                done(new Outcome
                {
                    Shots = attacker.ShotsFired, Hits = attacker.Hits, TargetHealth = target.Current, SecondHealth = second.Current,
                    AbilityUses = abilities.UsedCount, Cooldown = abilities.CooldownRemaining(0),
                    RandomA = Random.value, RandomB = Random.value, RandomC = Random.value,
                });
            }
        }

        [UnityTest]
        public IEnumerator PresentationOn_AndOff_GiveIdenticalGameplay_AndTheSameRandomStream()
        {
            Outcome on = default, off = default;
            yield return Fight(true, o => on = o);
            yield return Fight(false, o => off = o);

            Assert.That(on.Shots, Is.EqualTo(off.Shots));
            Assert.That(on.Hits, Is.EqualTo(off.Hits));
            Assert.That(on.TargetHealth, Is.EqualTo(off.TargetHealth));
            Assert.That(on.SecondHealth, Is.EqualTo(off.SecondHealth));
            Assert.That(on.AbilityUses, Is.EqualTo(off.AbilityUses));
            Assert.That(on.Cooldown, Is.EqualTo(off.Cooldown).Within(0.25f), "cooldowns run on the same scaled clock");
            Assert.That(on.RandomA, Is.EqualTo(off.RandomA));
            Assert.That(on.RandomB, Is.EqualTo(off.RandomB));
            Assert.That(on.RandomC, Is.EqualTo(off.RandomC));
            Assert.That(on.Shots, Is.EqualTo(12));
        }

        [UnityTest]
        public IEnumerator DisablingAndReEnablingTheUnitPresentation_ReplaysNothing()
        {
            var rig = new PresentationRig();
            try
            {
                var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f));
                var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
                yield return null;
                shooter.GetComponent<UnitAttacker>().TryAttack(target);
                rig.Presenter.Clear();
                var presentation = shooter.GetComponent<UnitPresentation>();
                presentation.enabled = false;
                presentation.enabled = true;
                yield return null;
                Assert.That(rig.Presenter.ActiveEffects, Is.EqualTo(0));
            }
            finally { rig.Dispose(); }
        }
    }
}
#endif
```

- [ ] **Step 6: Source audit** — `Tests/EditMode/PresentationSourceAuditTests.cs`:
```csharp
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Blackglass.Tests
{
    /// <summary>Guards the presentation layer's rules by reading its source: time, randomness, and who may touch gameplay state.</summary>
    public class PresentationSourceAuditTests
    {
        const string Folder = "Assets/_Project/Scripts/Presentation";

        static string[] Sources => Directory.GetFiles(Folder, "*.cs");

        static void AssertNone(string token, params string[] allowedFiles)
        {
            foreach (var file in Sources)
            {
                var name = Path.GetFileName(file);
                if (allowedFiles.Contains(name))
                    continue;
                Assert.That(File.ReadAllText(file), Does.Not.Contain(token), name + " must not contain '" + token + "'");
            }
        }

        [Test]
        public void NoPresentationScript_WritesTheTimeScale_OrUsesWallClockTime()
        {
            AssertNone("timeScale =");
            AssertNone("timeScale=");
            AssertNone("useUnscaledTime = true");
            AssertNone("realtimeSinceStartup");
            AssertNone("unscaledTime");
            AssertNone("WaitForSecondsRealtime");
        }

        [Test]
        public void OnlyTheAudioClock_ReadsUnscaledDeltaTime()
        {
            AssertNone("unscaledDeltaTime", "CombatAudio.cs");
        }

        [Test]
        public void PresentationNeverDrawsFromTheGameplayRandomStream()
        {
            AssertNone("UnityEngine.Random");
            AssertNone("Random.value");
            AssertNone("Random.Range(");
            AssertNone("Random.InitState");
            AssertNone("insideUnitSphere");
        }

        [Test]
        public void OnlyThePreviewPanel_MayChangeHealth_OrIssueOrders()
        {
            AssertNone(".TakeDamage(", "PresentationPreviewPanel.cs");
            AssertNone(".Heal(", "PresentationPreviewPanel.cs");
            AssertNone(".Issue(", "PresentationPreviewPanel.cs");
            AssertNone(".TryUse(", "PresentationPreviewPanel.cs");
            AssertNone(".TryAttack(", "PresentationPreviewPanel.cs");
            AssertNone("NavMeshAgent");
            AssertNone("Physics.SyncTransforms");
        }

        [Test]
        public void TheAuditActuallyFindsTheSources()
        {
            Assert.That(Sources.Length, Is.GreaterThan(10));
            Assert.That(Sources.Select(Path.GetFileName), Does.Contain("CombatAudio.cs"));
        }
    }
}
```

- [ ] **Step 7: Run each new class, fix what they find, then run the combat/ability/mission/fog suites again**
```bash
Tools/run-tests.sh EditMode "Blackglass.Tests.PresentationSourceAuditTests"
Tools/run-tests.sh PlayMode "Blackglass.Tests.PresentationPauseRealAssetsPlayModeTests"
Tools/run-tests.sh PlayMode "Blackglass.Tests.PresentationFogRealKnowledgePlayModeTests"
Tools/run-tests.sh PlayMode "Blackglass.Tests.PresentationBudgetPlayModeTests"
Tools/run-tests.sh PlayMode "Blackglass.Tests.PresentationRegressionPlayModeTests"
```
Expected: `EXIT=0` for each. Likely first-run findings: a `TacticalPause.IsPaused` assertion ordering, the cover geometry in the regression fight, the friendly's range in the fog setup (see the note under Step 3), the audit's `Random.` pattern matching a comment. Fix the test setup or the code, never the rule.

- [ ] **Step 8: Milestone D gate** — full `Tools/run-tests.sh EditMode` and `Tools/run-tests.sh PlayMode`; both green; record the totals in `progress.md`.

---

### Task 16: Documentation, decision record, manual checks, performance and render report

**Files:**
- Create: `Docs/CombatPresentation.md`, `Docs/Phase12.5-ManualTests.md`, `Tests/PlayMode/PresentationPerformanceReportTests.cs`
- Modify: `Docs/Decisions.md` (decision 044 + amendment notes to 014, 025), `docs/superpowers/specs/2026-10-10-combat-presentation-vfx-design.md` (record the three plan clarifications, below)
- Create (memory): `C:\Users\Yann\.claude\projects\F--Programs-ProjectBlackglass\memory\phase125-combat-presentation-state.md` and its `MEMORY.md` line

- [ ] **Step 1: Performance report test** (a measurement, not a gate) — `PresentationPerformanceReportTests.cs`:
```csharp
#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// Plays a representative burst in the preview scene for a fixed time and writes the frame times with their conditions to
    /// .claude/scratchpad/phase125/performance.txt. It asserts only that frames were measured: no frame-rate target is claimed.
    /// </summary>
    [Category("Perf")]
    public class PresentationPerformanceReportTests
    {
        [UnityTest]
        public IEnumerator Measure_ABurstOfShotsAndBlasts_InThePreviewScene()
        {
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(PresentationPreviewScenePlayModeTests.ScenePath,
                new LoadSceneParameters(LoadSceneMode.Additive));
            var scene = SceneManager.GetSceneByPath(PresentationPreviewScenePlayModeTests.ScenePath);
            var panel = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<PresentationPreviewPanel>(true)).First();
            var presenter = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<CombatPresenter>(true)).First();
            yield return new WaitForSeconds(1f);   // warm up

            var times = new System.Collections.Generic.List<float>();
            var peakEffects = 0;
            var start = Time.realtimeSinceStartup;
            var next = 0f;
            while (Time.realtimeSinceStartup - start < 8f)
            {
                if (Time.realtimeSinceStartup - start > next)
                {
                    panel.Stress();
                    panel.CastBlast();
                    next += 0.5f;
                }
                peakEffects = Mathf.Max(peakEffects, presenter.ActiveEffects);
                times.Add(Time.unscaledDeltaTime);
                yield return null;
            }

            times.Sort();
            float Percentile(float p) => times[Mathf.Min(times.Count - 1, (int)(times.Count * p))];
            var report = $"Frames {times.Count} in 8 s; mean {times.Average() * 1000f:F2} ms; median {Percentile(0.5f) * 1000f:F2} ms; " +
                         $"p95 {Percentile(0.95f) * 1000f:F2} ms; p99 {Percentile(0.99f) * 1000f:F2} ms; worst {times.Last() * 1000f:F2} ms; " +
                         $"peak active effects {peakEffects}\n" +
                         $"Conditions: Unity {Application.unityVersion}, {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType}), " +
                         $"{Screen.width}x{Screen.height}, quality '{QualitySettings.names[QualitySettings.GetQualityLevel()]}', " +
                         $"{SystemInfo.processorType}, Editor Play Mode (batch: {Application.isBatchMode}); Stress button (80 shots) + Blast every 0.5 s.\n";
            var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".claude", "scratchpad", "phase125", "performance.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.AppendAllText(path, report);
            Debug.Log(report);
            Assert.That(times.Count, Is.GreaterThan(10));
            yield return SceneManager.UnloadSceneAsync(scene);
        }
    }
}
#endif
```
Run `Tools/run-tests.sh PlayMode "Blackglass.Tests.PresentationPerformanceReportTests"` and keep the file; report the conditions exactly (batch mode has no vsync and a tiny window, so the numbers are indicative only; say so).

- [ ] **Step 2: `Docs/CombatPresentation.md`** — the authoring guide. Sections (write real content, short): *Architecture in one picture* (records → UnitPresentation → CombatPresenter → pools/audio/impulse); *Change a muzzle flash* (edit `Prefabs/MuzzleFlashRifle.prefab` or swap `RifleCombatPresentation.muzzleEffect`; size via `muzzleScale`); *Change the tracer* (`TracerRifle.prefab` material/width/length/speed on the profile's tracer entry); *Change or add an impact* (per-surface entries on the profile; `ImpactSurface` on colliders; new surfaces need a new `ImpactSurfaceKind` value at the end of the enum); *Change the explosion* (`ExplosionGrenade.prefab`, `referenceRadius`, the ring ends at the radius, Blast's `presentation` field); *Change a sound* (replace the clip in the cue; cues carry volume, pitch spread, priority, falloff, min interval); *Add a weapon's look* (new profile asset + `WeaponVisualAsset.presentation`; no code); *Budgets and quality* (the table of caps; `PresentationSettings`); *The knowledge gate* (what is shown when; 037); *Preview scene* (open `Presentation Test Scene`, press play; every button's meaning); *Regenerating the assets* (`Blackglass/Presentation/Build Effects`, idempotent; `Rebuild` overwrites in place); *Known limits*.

- [ ] **Step 3: `Docs/Phase12.5-ManualTests.md`** — the manual checks the owner still has to do (none are claimed as run): the 15 brief §18 items, each with the exact steps (e.g. "ProceduralMission → Deploy → select Darius → take control → click a hostile: you see a short muzzle flash with a faint smoke puff, a thin streak, a small puff where it lands; listen for the placeholder shot"), plus: pause mid-explosion and look; resume; hostile fires from fog (flash appears with its 3 s exposure, tracer only if the path is lit); Recon Scan reveals but shows no effects; regenerate (F5) during a fight and while paused; controller (DualSense) play-through for pause/audio; listen to the placeholder sounds for obnoxious levels; eye-check the Darius hit flash; shake setting 0 vs 0.5; low-quality tier.

- [ ] **Step 4: Decision 044 in `Docs/Decisions.md`** — numbered, in the file's existing format (**Decided / Why / Rejected / Implications / Known limitations**), recording: records raised after resolution and after `Attacked`/`Missed` (decision 037 kept; presentation adds no reveal); one presenter + per-unit adapter; profiles on `WeaponVisualAsset` and `AbilityDefinition` (Mend/Medkit heal pulse comes from the presenter's support profile because both end in `Health.Healed`); the visibility gate rules and conservative tracer suppression; scaled-time lifetimes and explicit audio pausing with its own audio clock; budgets; cosmetic random; camera impulse 8 cm, position-only, pointer-ray bound; `HitFlash`/`AttackLineView` stay debug-only (amends 014); generated assets and placeholder sounds; **Rejected:** a global event bus, per-weapon firing scripts, VFX Graph, decals, `AudioListener.pause`, buffering hidden events, a second damage radius in the VFX prefab; **Known limitations** (from the render/manual notes). Add a one-line "Amended by 044" note to 014 and 025.

- [ ] **Step 5: Spec clarifications** — in the design spec record the three places the plan refined it: (1) the placeholder sounds live under `Assets/_Project/Presentation/Audio/`; (2) the preview's surface-impact buttons use a synthetic cover-miss record through `UnitPresentation.Deliver` (the character hit, Blast and heal buttons use the real gameplay path); (3) the heal pulse uses the presenter's `supportProfile` (Mend's `AbilityDefinition.presentation` is not read); Aimed Shot uses the weapon's profile unless its own `presentation` is set.

- [ ] **Step 6: Memory** — create `phase125-combat-presentation-state.md` (type project: branch `phase125-combat-presentation`, nothing committed, test totals, assets generated, owner checks open, the 037 ruling, link `[[keep-exposure-by-firing]]`, `[[phase12-inventory-state]]`) and add its one-line pointer to `MEMORY.md`.

---

### Task 17: Final verification and whole-branch review

- [ ] **Step 1: Full suites, serially, Editor closed**
```bash
Tools/run-tests.sh EditMode
Tools/run-tests.sh PlayMode
```
Both `EXIT=0`. Record the totals against the Task 0 baselines (EditMode and PlayMode each grew by the tests added in Tasks 1–16).

- [ ] **Step 2: Idempotence and cleanliness**
```bash
Tools/run-method.sh Blackglass.EditorTools.CombatPresentationAssetBuilder.BuildFromCommandLine
Tools/run-method.sh Blackglass.EditorTools.CombatPresentationSceneBuilder.WireProceduralMissionFromCommandLine
git status --short
```
Expected: no new churn from the second builder runs; `git status` shows the Phase 12.5 files, the two weapon-visual assets, `Blast.asset`, the two scenes and the two unrelated pre-existing settings files (which must be unchanged from the start of the phase: `git diff --stat Assets/Settings ProjectSettings` shows only what was already there). Nothing is committed.

- [ ] **Step 3: Look at the final renders** — re-run `PresentationRenderCaptureTests`, open each PNG, and write the honest visual verdict (readability from the gameplay camera, no wall of smoke, flash on Darius, ring at the gameplay radius) into the report.

- [ ] **Step 4: Whole-branch review** — invoke `superpowers:requesting-code-review` over `git diff main` (the working tree on `phase125-combat-presentation`), with the spec and this plan as the requirements, and the Review Focus list above as extra things to attack. Fix real findings with `superpowers:receiving-code-review`; re-run the affected suites.

- [ ] **Step 5: Completion report to the owner** (brief §20), as text in the final message and in `.claude/scratchpad/phase125/report.md`: files and assets created or changed; the presentation architecture and event timing; profile integration; the effects created; muzzle and scale handling; hitscan/tracer/miss behaviour; surface implementation; explosion composition; animation and support feedback; audio assets, provenance (generated, no external source) and playback rules; the pause implementation; fog safeguards and the conservative fallbacks (tracer suppression, impact point offsets); pooling and budgets; mission cleanup; the preview scene and authoring workflow; tests actually performed (with totals); manual checks still required; performance observations with their conditions; known visual limitations and unresolved bugs. State plainly anything not run: Play Mode in the player, builds, controller hardware, listening to the audio. Do not commit, push or start Phase 13.
