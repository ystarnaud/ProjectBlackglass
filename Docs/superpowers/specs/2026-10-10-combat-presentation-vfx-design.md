# Phase 12.5 — Combat Presentation & VFX Foundation: design

Status: design approved in conversation 2026-10-10 (owner: "yes approved", after the correction that decision 037's temporary exposure of a firing hostile is kept); written spec awaiting owner review.
Brief: the Phase 12.5 prompt (sections 1–20). This document records the design choices made against the repository as it is at `9375ed6` (Unity 6000.3.25f1, URP 17.3).

## 1. Goal and success criteria

Make the existing combat visible and audible through a small presentation layer **and** a working set of prototype effects, without changing a single gameplay rule:

Gameplay validates and resolves → publishes a presentation record → presentation shows, animates and sounds it.

At completion a normal mission shows: a rifle muzzle flash with a restrained smoke puff, a tracer, concrete and metal impacts, a character hit reaction, a layered Blast explosion with camera impulse, firing/impact/explosion/heal sounds, a small recoil, and a heal pulse; all frozen by tactical pause and all filtered by the Phase 11 knowledge model.

Out of scope (brief §19): new weapon simulation, reload/ammo, ballistics, destruction, tactical smoke, ragdolls, a new animation framework, peeking/IK, music, haptics, save/load, Asset Studio changes, decals/scorch marks (a correct particle impact without a decal is accepted), Recon Scan visuals, Phase 13.

## 2. What exists (inspection results)

- No ParticleSystem, AudioSource or audio clip anywhere. Each scene has exactly one `AudioListener` (on the camera). Packages: URP 17.3, Input System, no VFX Graph (none will be added).
- `UnitAttacker.TryAttack` is the only basic-attack path. It raises `Attacked` (hit, after damage) or `Missed` (cover miss) and nothing for a rejected attempt. `IntelligenceService.WatchHostile` handles those two events by exposing the shooter for `exposureSeconds` and running an intelligence pass **inside that call** (decision 037).
- `UnitAbilities.TryUse` → `Apply`: Aimed Shot (unit damage), Blast (ground area, centre + `Radius`, 3 m, ignores cover), Mend (heal), Recon Scan (reveal). `Health` has `Damaged`, `AttackedBy`, `Died`, `Healed` (raised only when hit points were actually restored; Medkit and Mend both go through it).
- `UnitAnimationDriver` already triggers `Fire` (on Attacked/Missed), `Hit` (on Damaged, not the killing blow) and `Dead`; the Darius and EnemyUnit controllers already have Fire, Hit, Death states. Nothing triggers a melee-specific clip (the melee archetype reuses Fire).
- `Rifle.prefab` already has a `Muzzle` child (+Z out of the barrel). `UnitWeaponVisual` places the managed `EquippedWeapon` model in the character's `WeaponSocket` and raises `Changed`. Scales on that chain: FBX x100 inside the prefab, socket 1/1.85 on EnemyUnit, visual 1.0639 on Darius. World-space effect placement makes this irrelevant.
- `HitFlash` targets the hidden placeholder capsule, so these units show no flash; `UnitTeamTint` already writes `_BaseColor` through a property block on the skinned renderers (Darius's base colour is white).
- Pause: `TacticalPause` writes `Time.timeScale`; `SimulationTime.IsRunning` is the "may simulate this frame" test. Particle systems and `Time.time` freeze at timeScale 0; `AudioSource` does not.
- Knowledge: `IntelligenceService.CanTarget(unit)` (observed, friendlies always), `MissionIntelligence.StateOfPoint(world)` (region level), `Changed` event. `FogPresenter` darkens by scene depth; transparent particles write no depth, so fog alone cannot hide an effect.
- Mission lifecycle: `MissionDirector.Teardown` → `ResetSystems()` (clears each persistent system) and destroys `GeneratedMissionRoot`; `MissionSpawner` adds per-unit presenters (e.g. `HostilePresenter`) at spawn.
- Obstacle geometry is plain cubes created in `MissionBuilder.Cube` from `MissionBoxKind` (Wall, Baffle, Pillar, LowWall, Crate), each a `CoverSurface`; themed visuals are separate non-colliding children.
- Editor builders (`HudSceneBuilder`, `InventorySceneBuilder`, …) wire scene objects idempotently; Unity batch test runs are single-instance (`Tools/run-tests.sh`).

## 3. Conflicts with recorded decisions and how they are resolved

| Decision | Tension | Resolution |
|---|---|---|
| 037 exposure by firing | The brief says a hit from an unknown shooter must not reveal it | **037 is kept (owner ruling).** Presentation adds no reveal logic. `UnitAttacker.Fired` is raised *after* `Attacked`/`Missed`, so the exposure is already in force when the gate runs: a hostile's own shot is shown because the shooter is Observed for 3 s; presentation never causes that. |
| 014 | `AttackLineView` and `HitFlash` are "the only hit feedback, debug-only" | They stay as debug views. The presentation layer is the real feedback; a skinned-renderer flash replaces the invisible capsule flash for the model units. Amended in decision 044. |
| 034 | Darius's white `_BaseColor` makes tint-based flashes invisible | The hit reaction is a particle burst plus a brightness flash that overshoots white (HDR multiplier on `_BaseColor` through the property block); verified by render, with the burst as the guaranteed cue. |
| 038 | Fog shader cannot darken transparent particles | An explicit gate decides every effect; lingering effects are re-gated, never relied on to be fogged. |
| 025 | Abilities are instant | Blast is presented at its resolution time with no flight. |
| 007 | Only `TacticalPause` writes `timeScale` | Unchanged. Presentation reads `TacticalPause.Changed` for audio and `SimulationTime`/scaled `Time.time` everywhere else. No presentation script writes `timeScale`. |
| No friendly fire | — | Explosion presentation never implies it: it plays the gameplay centre and radius only; damaged units are exactly those `Health.Damaged` reports. |

## 4. Architecture

New folder `Assets/_Project/Scripts/Presentation/` in the existing `Blackglass` assembly (no new asmdef). Three layers, gameplay never references the top two:

1. **Gameplay records** (small additions to existing classes, no behavioural change):
   - `ShotRecord` (readonly struct): `Sequence` (per-emitter counter, plus emitter id, for duplicate prevention), `Source` (`Health` of the shooter), `Target` (`Health`), `Origin` (shooter pivot + eye, snapshot), `AimPoint` (target body point, snapshot), `Outcome` (`Hit` | `CoverMiss`), `IsMelee`, `Ability` (the `AbilityDefinition` for Aimed Shot, else null).
   - `AreaRecord` (readonly struct): `Sequence`, `Caster`, `Ability`, `Center`, `Radius` (the values `Apply` used).
   - `UnitAttacker.Fired(ShotRecord)`: raised once per discharged basic attack, **after** `Attacked`/`Missed`; the record is built before `TakeDamage`, so a victim that dies and deactivates changes nothing. Never raised for a rejected attempt (`TryAttack` returns false before any of this).
   - `UnitAbilities.ShotResolved(ShotRecord)` for a unit-targeted damage ability and `UnitAbilities.AreaResolved(AreaRecord)` for a ground damage ability, both raised from `TryUse` after `Apply` (only on a validated, applied use). Mend and Medkit need no new event: `Health.Healed`.
   - Existing `Health.Damaged`/`Died`/`Healed` carry the damage, death and heal feedback; no copy of them is made.
2. **`UnitPresentation`** (per-unit MonoBehaviour added by `MissionSpawner`, like `HostilePresenter`, and by hand in test scenes; not in prefabs, so the capsule rigs and the Prototype scene are untouched). It subscribes to its unit's records and `Health` events, resolves the **profile** (§5), caches the **muzzle** (§7), owns the **recoil** and the **hit flash/heal pulse anchors** (§10), and forwards a request to the presenter. Subscriptions are made in `OnEnable`/`OnDisable`. It captures positions at event time; it never reads a destroyed Transform afterwards.
3. **`CombatPresenter`** (one per scene, on its own root `Presentation` object): applies the visibility gate (§12), spawns pooled effects (§16), plays audio (§13), sends camera impulses (§14), and clears on mission change. `PresentationSettings` (enabled, quality tier, master volume, camera-shake intensity 0..1) is a serialized plain class on it. Disabled → every request is dropped before any work.

No static event bus. `MissionDirector.ResetSystems()` calls `CombatPresenter.Clear()` like every other persistent system; `MissionSpawner` registers units with `UnitPresentation`, which holds the presenter reference through a serialized/`Bind` call (as `HostilePresenter.Bind` does).

### Duplicate prevention

One attack produces one record from exactly one emitter (`TryAttack` for basic attacks, `TryUse` for abilities), so command → combat → health passes cannot double-present. `UnitPresentation` ignores a record whose `(emitter, Sequence)` it has already seen. `Health.Damaged` feedback is per damage instance, independent of the shot (a Blast has no shot record, only damage instances).

### No replay

Records are delivered to live subscribers only. Neither the HUD reconnecting nor a character switch re-emits anything; nothing is stored or buffered for later.

## 5. Presentation profiles

`CombatPresentationProfile` (ScriptableObject, immutable, no playback state). Optional sections, all null-safe:

- **Muzzle:** one `muzzleEffect` prefab (flash + smoke puff + optional capped light as children), `muzzleScale`.
- **Tracer:** `TracerStyle` (material, width, length, colour gradient, speed ≤ 0 means immediate; default 300 m/s, a short streak), enabled flag.
- **Fire cue:** `AudioCue` (clip array, volume, pitch range, priority, min/max distance).
- **Impacts:** table per `ImpactSurfaceKind` (Default, Concrete, Metal, Character): prefab, scale, sound cue. Missing entry → Default; missing Default → none.
- **Recoil:** `kickMeters` (default 0.035), `recoverSeconds` (0.12).
- **Area (abilities):** `areaEffect` prefab, `referenceRadius` (the radius the prefab was authored for; the instance is scaled by gameplay radius / reference), `areaCue`, `cameraImpulse` strength.
- **Support:** `pulseEffect` prefab and cue (used for the heal pulse).
- **Character hit cue:** sound played when the weapon hits a character.

Integration points: `WeaponVisualAsset` gets an optional `presentation` field (the weapon in hand decides how it looks and sounds; the rifle's `WeaponVisualAsset` references the rifle profile), `AbilityDefinition` gets an optional `presentation` field. A unit's firing profile is `UnitWeaponVisual`'s currently shown asset's profile; a unit without a weapon visual (capsules) or a profile gets the shared **fallback profile** (`DefaultCombatPresentation`: character/default impact only, no tracer, no muzzle) so nothing throws. Validation: `CombatPresentationProfile.OnValidate` and a one-time-per-asset `Debug.LogWarning` at first use for missing prefabs/clips (a `HashSet` of already-warned assets on the presenter; no per-frame logging). Different weapons reuse the same prefabs with different scale/cues; there is no per-weapon script.

## 6. Timing rules

- **Firing:** `Fired`/`ShotResolved` is raised only for a shot that actually discharged. A miss still produces muzzle flash, firing sound, recoil, tracer and a miss impact. A rejected request produces nothing.
- **Hit:** character impact at the snapshot aim point plus `Health.Damaged` feedback on the victim.
- **Delayed visuals:** a tracer is spawned with a captured origin and end point and moves between them; nothing re-reads the target's position. The tracer's travel is only cosmetic: damage was applied before the record was even delivered, and nothing waits for it.
- **Area:** the explosion plays at `AreaRecord.Center` immediately.
- **Order within a hit frame:** `Damaged` (victim reaction), `Attacked`, intelligence exposure, `Fired` (shooter effects). Visual order within one frame is irrelevant.
- **Queued commands:** nothing is presented while orders are only queued, because records are raised only from execution.

## 7. First weapon: rifle, muzzle, scale, recoil

- `UnitWeaponVisual` finds the `Muzzle` transform **once** in `Place` (a child named `Muzzle` of the placed model; no per-shot hierarchy search; null when absent) and exposes `Muzzle`. `Changed` already fires on every replace/remove; `UnitPresentation` re-reads `Muzzle` and the profile on `Changed` and clears bindings when the weapon is removed. When the muzzle is missing the effect falls back to the unit pivot + 1.4 m up + 0.6 m forward (one warning per unit), never a character-name offset.
- Effects are spawned **unparented** at `Muzzle.position` / `Quaternion.LookRotation(aimDirection)` with scale from the profile, so the 100x/1.85x/1.0639x scale corrections never reach them. The muzzle is read at event time, so it follows the current pose.
- The rifle gets a `WeaponVisualAsset` with the shared rifle profile (muzzle flash, smoke, tracer, concrete/metal/default/character impacts, a placeholder shot sound, recoil). Darius and the EnemyUnit prefabs share it through the existing item/visual path.
- **Recoil:** `WeaponRecoil` (per unit, owned by `UnitPresentation`) offsets the managed `EquippedWeapon` transform's local position by `kickMeters` *in world units* back along the muzzle's backward axis (converted through the parent's inverse transform), then eases it back over `recoverSeconds` on scaled time, only while `SimulationTime.IsRunning`. It touches only the managed child (re-bound on `Changed`), never the unit root, agent, collider or aim. The existing `Fire` animation clip stays as the character animation (no additional animation work).

## 8. Tracers, misses and endpoints

- Hitscan stays hitscan. The tracer is a pooled `LineRenderer`-based `TracerView` (one material, no per-shot material) travelling at the profile speed (or drawn at once) along `Origin(muzzle) → End`.
- **Hit:** `End` = the record's aim point. A cosmetic non-allocating raycast (`Physics.RaycastNonAlloc`, `QueryTriggerInteraction.Ignore`, the same "colliders under a `Health` do not block" convention as `LineOfSight`) from the muzzle toward the aim point truncates the tracer at the first world blocker (and then drops the character impact) so it never visibly runs through geometry.
- **Cover miss:** the cover rule said the target was protected from here, so the cosmetic endpoint is the first world collider on the muzzle→aim line (the cover that turned it away) with that surface's impact; if there is none, the line is deflected by 0.6–1.2 m sideways (cosmetic random) and extended to the first blocker or a 30 m cap; no blocker and no cap hit means a tracer to the cap and no impact. A miss never shows a character impact.
- All of this is computed in the presenter after the gameplay record. It reads colliders but writes nothing, and uses no gameplay random stream. If a cosmetic query cannot be resolved (no origin, NaN, zero length), the tracer/impact is omitted.
- No penetration, ricochet, drop or projectile object. Effect objects have no colliders and no `NavMeshObstacle`, so they can never enter nav, cover, targeting, interaction or LOS queries (asserted by a test over the effect prefabs).

## 9. Surface-aware impacts

- `ImpactSurface` (small MonoBehaviour: `ImpactSurfaceKind kind`) is added by `MissionBuilder.Cube` to obstacle cubes from their `MissionBoxKind`: Wall, Baffle, Pillar → Concrete; LowWall, Crate → Metal. Floors get Concrete (the floor cubes). Objective content (terminal) gets Metal where its collider is created. Classification is a pure `ImpactSurfaces.Classify(collider)`: `GetComponentInParent<ImpactSurface>()` → its kind; else a parent `Health` → Character; else Default. No material-name inference, no mesh analysis.
- Impact position and normal come from the cosmetic raycast hit. For a character hit the point is where the shot line first enters the **target's own collider** (`Collider.Raycast`, on its surface, facing the shooter; see §21), falling back to an analytic capsule surface for a deactivated killing-blow target and to the snapshot aim point otherwise.
- Effects: concrete = grey dust puff + a few chips; metal = a brief spark burst + tiny flash; default = a small neutral puff; character = restrained neutral puff (no gore). Each plays an impact sound from the profile.
- No decals.

## 10. Explosion, character feedback, support cue

**Explosion** (`Blast`'s profile `areaEffect`, scaled to gameplay radius):
- Layers (one prefab, separate child particle systems): a 0.08 s additive flash; a fire/dust burst; a spark burst; ~12 small cosmetic fragments (pooled particle system with `Collision` module off and no colliders, no physics debris); a 3–4 s dissipating smoke cloud; a thin expanding ring that ends exactly at the gameplay radius and fades (so it reads as the real boundary); a short capped point light. Plus the area sound and a camera impulse.
- Spawned at `AreaRecord.Center` lifted 0.15 m, scaled by `Radius / referenceRadius`. The VFX prefab owns no damage radius, collider or script that touches gameplay. Fragments cannot damage, block, cover or be looted.
- The smoke is cosmetic: it does not touch LOS, `Knowledge` or cover. Its density is kept low and its colour mid-grey so the HUD and battlefield stay readable from the gameplay camera.

**Character feedback:** `UnitPresentation` listens to its unit's `Health.Damaged` (once per damage instance, killing blow included). It plays the unit-local reaction: a skinned-renderer brightness flash restoring the correct appearance afterwards (property block only; for a unit with `UnitTeamTint` it re-applies the tint colour, otherwise it clears the block; no shared material is modified; the flash timer runs on scaled time), and a small neutral burst at the body when the damage did not come from a shot that already placed a character impact (so a rifle hit shows one burst, a Blast victim shows one). `Died`: plays the profile's death/body-fall sound at the captured position; the existing Death animation and `DeathMarker` are unchanged. The animation `Hit`/`Dead`/`Fire` triggers stay with `UnitAnimationDriver`. One small addition: `UnitAnimationDriver` also triggers `Fire` when the unit's Aimed Shot resolves (the ability fires the weapon); Blast and Mend get no clip (none exists).

**Support cue:** `UnitPresentation` listens to `Health.Healed(restored)`: when it is raised (the effect succeeded: Mend and Medkit alike) it plays the pulse effect (a spreading green ring and a few rising motes) at the unit's feet and the pulse cue, taken from the presenter's `supportProfile` (see §21); selecting or queueing does nothing, and a full-health target (no `Healed`) shows nothing.

## 11. Animation

Reuses the existing clips and triggers; root motion stays off; locomotion state machine is not restarted by shots (the existing Fire state is entered by trigger as it is today). No IK, no new states. Recoil is separate and cosmetic (§7).

## 12. Fog of war and information safety

A pure `PresentationVisibility` class (no scene dependency, unit-testable with lambdas) decides, per record, what may be shown. Inputs: whether the shooter is *observed* (`CanTarget`, i.e. friendly or live-seen hostile; **not** the F12 truth view, which never unlocks combat effects), whether each queried point is Observed (`MissionIntelligence.StateOfPoint`), and the segment geometry. A null `IntelligenceService` or open fog means everything is allowed (the default for scenes without the feature). Rules:

| Element | Allowed when |
|---|---|
| Muzzle flash, smoke puff, muzzle light, fire sound, recoil | shooter observed |
| Tracer | shooter observed, `Origin` and `End` Observed, and samples every 3 m along the path Observed (otherwise the tracer is suppressed; no partial masking) |
| Impact (surface or character) and its sound | the impact point Observed (a character impact: the victim observed) |
| Victim hit flash, burst, hit sound, heal pulse, death sound | the unit observed |
| Explosion, its sound, camera impulse | `Center` Observed (impulse also scaled by distance to the camera) |
| Lingering smoke/light | re-evaluated when `IntelligenceService.Changed` fires; hidden by `Renderer.forceRenderingOff`/`Light.enabled=false` without restarting or releasing |

Consequences, with 037 kept: an unknown hostile that fires becomes Observed for 3 s *by 037* and its flash/sound/impact on a visible friendly then show; presentation adds no reveal. A visible friendly firing into unknown space shows its flash and sound but no tracer or impact. A discovered-but-unobserved room shows no live effects. Hidden one-shot records are dropped, never buffered. No lights, sounds, smoke or marks are emitted for hidden points, and no gunfire-based discovery or hearing channel is added. If a gate input is missing, the answer is "do not show".

## 13. Audio

- `CombatAudio` (owned by `CombatPresenter`): a pool of 10 `AudioSource` children (3D, `spatialBlend` 1, logarithmic rolloff, per-cue min/max distance) using the scene's existing `AudioListener`; no new listener is created and no `AudioMixer` asset is added (volume = cue volume × master × a crowd factor).
- Limits: at most 10 simultaneous voices (oldest, lowest-priority is stolen); a per-cue-key minimum interval of 60 ms; a crowd factor of `1/sqrt(n)` for `n` recent plays of the same key within 0.15 s, so several rifles together do not stack gain.
- The same visibility gate applies before any play. A missing clip or null cue is a no-op.
- **Pause:** `CombatPresenter` subscribes to `TacticalPause.Changed`; on pause every playing combat voice is `Pause()`d, on resume `UnPause()`d (tracked by our own flag, so a paused source is never mistaken for finished). Interface sounds (none exist yet) would use other sources. Voices are released only when not playing *and* not paused by us.
- **Assets:** placeholder sounds are synthesised locally by the Editor builder (noise bursts with filtered envelopes: rifle shot, explosion boom, concrete/metal/body impacts, heal chime), written as 16-bit mono WAV files named `PLACEHOLDER_*.wav` under `Assets/_Project/Presentation/Audio/` (not `Assets/_Project/Audio/Placeholder/`; see §21), with a `README.md` stating they are generated, original and replaceable. No external audio is added, so there is no licence to record beyond "generated by this project".

## 14. Camera feedback

`CameraImpulse` (a tiny component on the camera rig; `TacticalCameraController.ApplyCameraPose` adds its offset to the viewing camera's local position, not to the rig pivot). Explosion-only: `Add(strength)` scaled by the settings intensity (0 disables) and by distance; a decaying noise offset in camera-local space (≤ 0.08 m **per axis**, magnitude up to about 0.11 m, no rotation), advanced on scaled time, so it freezes during tactical pause while pan/zoom/rotate stay on unscaled time. Ordinary rifle fire never shakes. It never touches pivots, focus points, bounds, command destinations or facing. The pointer ray uses the viewing camera (`PointerTargetResolver`), so the shake moves the pointer's ground point by a few centimetres (up to about 19 cm at the minimum camera pitch of 25 degrees, at full strength), not "at most the amplitude"; the offset was kept in the ray rather than suppressed for queries (decision 044).

## 15. Pause and lifetimes

- All combat particle systems use scaled time (`useUnscaledTime = false`), so `timeScale = 0` freezes particles, trails, fragments and smoke where they are. Pooled-effect lifetimes are scaled-time end timestamps (`Time.time`-based), advanced in `Update` only while `SimulationTime.IsRunning`; an effect resumes from where it was, is never restarted, and is never released because `ParticleSystem.isPlaying` or `AudioSource.isPlaying` is false while paused.
- Custom motion (tracer travel, recoil, flash envelope, light envelope, camera impulse) uses `Time.deltaTime` under the `SimulationTime.IsRunning` guard. The shaders used are Unity's particle/unlit URP shaders (scaled `_Time`); no custom time-reading shader is added. The audit is a test: a source-scan EditMode test over `Presentation/` that fails on `timeScale =`, `unscaledTime`, `unscaledDeltaTime`, `realtimeSinceStartup` and `useUnscaledTime = true` (a small allow-list for anything justified in review).
- Repeated pause/resume cannot duplicate effects or sounds: effects are created only from records, never from pause events; the pause subscription only pauses/unpauses existing voices. A paused-mission regeneration calls `Clear()` (which releases everything and stops audio regardless of pause state; `ResetSystems` resumes the pause service as today).

## 16. Pooling, budgets, cleanup, cosmetic random

- `EffectPool`: one inactive stack per source prefab, an explicit **active** list per category, and a pure `EffectBudget` (EditMode-testable) with per-category caps scaled by the quality tier: muzzle 12, tracer 24, impact 24, explosion 4 (a fifth recycles the oldest), pulse 8, lights 6 total (extra lights are simply left disabled on the effect), particles-fragment cap by the prefab's `maxParticles`, audio 10, persistent marks 0. Exhaustion degrades cosmetically (skip the request); it never blocks or alters gameplay and never logs per frame (one aggregated counter on the presenter).
- `PooledEffect` (on every instantiated effect root) resets on release: `ParticleSystem.Stop(true, StopEmittingAndClear)` and `Clear`, trail data cleared with `Clear()`, transform parent/position/rotation/scale, `Light` intensity/range/enabled, renderer `forceRenderingOff`, `MaterialPropertyBlock`s, lifetime and gate state, and the references (source unit, mission token) set to null.
- **Cosmetic random:** a `CosmeticRandom` (System.Random-based, own seed, never `UnityEngine.Random`, never the mission/loot/combat seeds) supplies sparks, deflection and shake jitter. A test asserts `UnityEngine.Random.state` is identical before and after a full presentation burst and that generation, loot and hit outcomes are identical with presentation on, off and at every quality tier.
- **Mission cleanup:** `CombatPresenter.Clear()` (called from `ResetSystems`, and when a new mission begins): increments a mission token, releases every active effect, stops/clears audio, clears the dedupe tables and subscriptions' pending state. Delayed callbacks (e.g. a tracer in flight) carry the token and are dropped when it no longer matches. `UnitPresentation` unsubscribes on disable/destroy; pooled roots live under a presenter-owned persistent container (not under the mission root), so teardown destroys nothing the pool needs and nothing it should clear is left behind.

## 17. Assets, authoring and the preview scene

An idempotent Editor builder (`CombatPresentationAssetBuilder`, menu `Blackglass/Presentation/Build Effects`, also callable via `-executeMethod`) generates and saves under `Assets/_Project/Presentation/`: soft-circle, smoke-noise and spark textures (PNG written locally), URP `Particles/Unlit` materials (additive for flash/sparks/ring, alpha-blended for smoke/dust), effect prefabs (MuzzleFlashRifle, TracerRifle, ImpactConcrete, ImpactMetal, ImpactCharacter, ImpactDefault, ExplosionGrenade, HealPulse), profiles (`RifleCombatPresentation`, `BlastCombatPresentation`, `DefaultCombatPresentation`; there is no `MendCombatPresentation`, see §21), the placeholder WAVs, and wires the profile references on the rifle `WeaponVisualAsset` and the ability assets. It creates only what is missing (assets carry no per-start regeneration); a `Force` option rebuilds. All textures/materials are plain replaceable assets referenced by the prefabs.

`Presentation Test Scene` (new, isolated, no mission): a floor, a concrete block, a metal block, a shooter dummy (real `UnitAttacker`, `UnitWeaponVisual` with the rifle, `UnitPresentation`), target dummies with `Health`, the real `TacticalPause`, `CombatPresenter` and the gameplay camera rig, plus a small IMGUI panel: fire at concrete / metal / character / a cover-miss, cast Blast, damage-then-heal, pause toggle, a 40-shot stress button, shake slider, presentation on/off, and a fog simulation toggle that swaps in a fake knowledge source so the gate can be seen to hide things. The character, Blast, heal and stress buttons drive the real gameplay path on scene dummies; the concrete, metal and past-target buttons hand `UnitPresentation.Deliver` a synthetic cover-miss record (§21). Nothing there touches missions, XP, inventory or operatives. The ProceduralMission scene has no preview controls.

`Docs/CombatPresentation.md`: how to change a muzzle flash, tracer, impact, explosion and sound by editing assets/profiles without touching combat code, how to add a weapon profile, and the budget table. `Docs/Decisions.md` gets decision 044. `Docs/Phase12.5-ManualTests.md` lists the manual checks.

## 18. Testing and verification

**EditMode (pure):** `PresentationVisibility` truth table and path sampling; `EffectBudget` caps/recycling; pooled lifetime stepping (scaled time, pause stops advance, resume continues); `CosmeticRandom` independence; dedupe table; profile fallback selection and one-time warning; `ImpactSurfaces.Classify`; a source scan that no presentation script writes `timeScale` or reads unscaled time (allow-list in the test); effect prefabs contain no colliders.

**PlayMode:** `Fired` on hit, on cover miss, not on a rejected attack, and after exposure for a hostile (037 order); no duplicate presentation for one attack through command → attacker → health; abilities: one `ShotResolved`/`AreaResolved` per valid use, none for a refused one; heal pulse only when `Healed` fires; weapon replace/removal rebinds the muzzle; missing muzzle/profile/clip fails safely; pause freezes particles, tracer, recoil, flash and a light; resume continues the same effect; regeneration (running and paused) releases everything and drops stale callbacks; fog cases (hidden shooter's first shot, friendly into unknown space, discovered-unobserved room); budgets under a 40-shot burst and under 10 explosions; **presentation-off versus on regression**: the same scripted encounter yields identical hits, health, cooldowns, item counts and mission outcome.

**Rendered verification:** frames are rendered from the real gameplay camera in the test scene and in a generated mission (rifle fire, concrete and metal impacts, character hit, Blast at gameplay radius) and saved under `.claude/scratchpad/phase125-renders/`; if the environment cannot render, that is reported as unverified. **Performance:** a representative encounter is run for a fixed duration in the Editor and the measured frame times are reported with their conditions (resolution, machine, Editor vs build); no universal target is claimed. Play Mode, builds, profiling and controller hardware are reported only if actually run.

## 19. Milestones

- **A** — Records and events (`Fired`, `ShotResolved`, `AreaResolved`), profiles, `UnitPresentation`, `CombatPresenter` skeleton, pooling, visibility gate; the rifle shot complete (muzzle flash, smoke, tracer, default impact, sound, recoil) in the test scene and a mission.
- **B** — `ImpactSurface` classification, concrete/metal/character impacts, the layered Blast explosion at gameplay scale, camera impulse.
- **C** — Audio set, hit flash/death sound, heal pulse, ability integration, `UnitAnimationDriver` Fire on Aimed Shot.
- **D** — Pause/fog/pooling/cleanup hardening, regeneration while paused, budgets under stress, presentation-off regression, docs, performance and render report.

Each milestone ends with the project compiling, EditMode and PlayMode green, and a report; no commits or pushes without the owner's instruction (work happens on branch `phase125-combat-presentation`). Phase 13 is not started.

## 20. Risks and items to confirm during planning

1. How `PointerTargetResolver` builds its ray (shake influence, §14).
2. Whether batch-mode rendering is available for the verification frames (GPU in batch mode); fall back to Editor Play Mode screenshots, else report unverified.
3. The exact hook for `Systems` wiring (`MissionDirector.systems`) and the scene builder pattern for adding `CombatPresenter` to `ProceduralMission` and `Character Test Scene`.
4. Whether the animation `Fire` trigger from `Attacked`/`Missed` and the new `Fired` can be left independent (they are today equivalent); the plan keeps the working animation path untouched.
5. HDR flash on Darius's white `_BaseColor` is verified by render; the particle burst is the guaranteed cue if the flash is not visible.
6. The two uncommitted settings files (`Mobile_RPAsset.asset`, `URPProjectSettings.asset`) are not part of this phase and stay untouched.

## 21. Clarifications made while implementing

Where the implemented layer differs from, or refines, the text above (decision 044 records the rulings):

1. **Audio folder.** The placeholder sounds live under `Assets/_Project/Presentation/Audio/` (with the rest of the generated assets), not `Assets/_Project/Audio/Placeholder/`. The README there states they are generated and replaceable.
2. **Preview scene records.** The preview's concrete, metal and past-target buttons use a synthetic cover-miss record delivered through `UnitPresentation.Deliver`; the character, Blast, heal and stress buttons use the real gameplay path. The fog switch replaces the knowledge queries (the scene has no `IntelligenceService`) and only ever adds a restriction.
3. **Heal pulse and Aimed Shot.** The heal pulse uses the presenter's `supportProfile` (`DefaultCombatPresentation`); Mend's `AbilityDefinition.presentation` is not read, and there is no Mend profile asset, because Mend and the Medkit both end in `Health.Healed`. Aimed Shot uses the weapon's profile unless its own `presentation` is set.
4. **Character hit point.** A body hit is placed on the target's collider surface (`Collider.Raycast`), not at the snapshot aim point inside the body; a killing-blow target (already deactivated) uses an analytic capsule surface, and the aim point is the last fallback. The default burst for damage with no depicting shot sits on the capsule surface facing `Camera.main`.
5. **Camera impulse bound.** The offset is at most 0.08 m per axis (magnitude up to about 0.11 m), and the pointer-ray error it causes is a few centimetres (up to about 19 cm at the minimum pitch), not "at most the amplitude" (§14).
6. **Audio details.** The audio path keeps its own audio clock (the one unscaled read, allow-listed in the source audit); the per-cue minimum interval is tracked separately from the 0.15 s crowd window, and the crowd gain is `1/sqrt(n+1)` for `n` earlier plays in the window, recorded only once a voice is available. Dead victims are judged by the snapshot point (`ObservesAt`) because `CanTarget` is false for the dead. The recoil starts after the effects are spawned, and only when the shooter's effects were allowed.
