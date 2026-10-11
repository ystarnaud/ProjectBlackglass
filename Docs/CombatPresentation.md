# Combat presentation: authoring guide (Phase 12.5)

How combat is made visible and audible, and how to change a muzzle flash, tracer, impact, explosion, sound or a weapon's look **without touching combat code**. The decision record is `Docs/Decisions.md` decision 044; the design is `docs/superpowers/specs/2026-10-10-combat-presentation-vfx-design.md`; the owner's checks are `Docs/Phase12.5-ManualTests.md`.

Everything here is a placeholder: generated textures, particles and sounds, there to prove the pipeline. Gameplay never reads any of it. Turning the whole layer off (`PresentationSettings.enabled`), or deleting any asset, changes what is shown, never a hit, a health value, a cooldown or an outcome.

## Architecture in one picture

```
 gameplay (decides)                    presentation (shows)
 ------------------                    --------------------
 UnitAttacker.TryAttack
   Attacked / Missed  (037 exposure)
   Fired(ShotRecord) ----------+
 UnitAbilities.TryUse          |
   ShotResolved(ShotRecord) ---+--> UnitPresentation   (one per unit, added by MissionSpawner;
   AreaResolved(AreaRecord) ---+     picks the profile,    never in a prefab)
 Health.Damaged / Died / Healed -+   finds the Muzzle, keeps the recoil and the hit flash
                                      |
                                      v
                                CombatPresenter        (one per scene, on a root Presentation object)
                                  knowledge gate (PresentationVisibility + IntelligenceService)
                                  |-- EffectPool + EffectBudget  -> PooledEffect / TracerView (prefabs)
                                  |-- CombatAudio                -> 10 AudioSources, CueThrottle, VoicePicker
                                  '-- CameraImpulse              -> TacticalCameraController (view camera only)
```

- The records (`ShotRecord`, `AreaRecord`) are raised **after** gameplay resolved: `UnitAttacker.Fired` follows `Attacked` / `Missed`, so decision 037's exposure of a firing hostile is already in force when the gate runs. Presentation adds no reveal of its own.
- `UnitPresentation` listens to its own unit only; it ignores a record it has already seen (`SequenceFilter`), and delivers to live subscribers only (nothing is replayed or buffered).
- `CombatPresenter` is the only place that decides what the player may see and hear, spawns effects (pooled, budgeted), plays sounds and sends the camera impulse. Every part is optional: a missing prefab, clip or profile shows nothing and warns once.
- A **profile** (`CombatPresentationProfile`, a ScriptableObject) is shared immutable data. A unit's firing profile is: the ability's own `presentation` (Aimed Shot, only if set) -> the shown weapon's `WeaponVisualAsset.presentation` -> the presenter's fallback profile (`DefaultCombatPresentation`).
- `MissionDirector` calls `CombatPresenter.Clear()` on every teardown/regeneration (also while paused); it releases every effect, stops every sound, drops pending default bursts and clears the camera impulse.

## Where things live

| What | Path |
|---|---|
| Runtime code | `Assets/_Project/Scripts/Presentation/` |
| Editor builders | `Assets/_Project/Editor/Presentation/` |
| Profiles | `Assets/_Project/Presentation/Profiles/` (`RifleCombatPresentation`, `DefaultCombatPresentation`, `BlastCombatPresentation`) |
| Effect prefabs | `Assets/_Project/Presentation/Prefabs/` (`MuzzleFlashRifle`, `TracerRifle`, `ImpactConcrete`, `ImpactMetal`, `ImpactCharacter`, `ImpactDefault`, `ExplosionGrenade`, `HealPulse`) |
| Materials | `Assets/_Project/Presentation/Materials/` (`FxGlow`, `FxSmoke`, `FxDust`, `FxRing`, `FxTracer`, `FxSpark`, `FxChip`; URP Particles/Unlit, transparent, no depth write) |
| Textures | `Assets/_Project/Presentation/Textures/` (`SoftCircle`, `Smoke`, `Ring`) |
| Placeholder sounds | `Assets/_Project/Presentation/Audio/` (`PLACEHOLDER_*.wav` and a `README.md`) |
| Preview scene | `Assets/_Project/Scenes/Presentation Test Scene.unity` (and `Presentation/Preview/PreviewBlast.asset`, a cooldown-0 clone of Blast) |
| Profile hooks | `WeaponVisualAsset.presentation` (the rifle and marksman rifle visuals), `AbilityDefinition.presentation` (Blast) |

Profile defaults as shipped: `RifleCombatPresentation` = muzzle flash (scale 1), tracer (width 0.035 m, length 6 m, 300 m/s), fire sound, Concrete / Metal / Character / Default impacts, recoil kick 0.035 m over 0.12 s. `BlastCombatPresentation` = `ExplosionGrenade` at reference radius 3 m, boom, camera impulse 0.7. `DefaultCombatPresentation` = Character and Default impacts, the body-fall death sound, **and the heal pulse** (see below). Every prefab carries a `PooledEffect` on its root and has no collider or rigidbody (`CombatPresentationProfile.Problems()` and the asset tests enforce it).

## Change a muzzle flash

The flash, the cone, the smoke puff and a capped point light are children of `Prefabs/MuzzleFlashRifle.prefab`. Open the prefab and edit the particle systems, or build a different prefab (keep a `PooledEffect` on the root, no colliders) and drop it into `RifleCombatPresentation.muzzleEffect`. `muzzleScale` on the profile scales the whole effect. The effect is spawned unparented at the weapon's `Muzzle` child (the rifle prefab has one, +Z out of the barrel) so the model's import scale never reaches it. A weapon model with no `Muzzle` falls back to a point 0.6 m in front of the unit and logs one warning per unit.

Keep particle systems on **scaled time** (`useUnscaledTime` off): the asset tests fail otherwise, and a tactical pause relies on it.

## Change the tracer

`Prefabs/TracerRifle.prefab` is a `LineRenderer` plus a `TracerView`; its material is `Materials/FxTracer.mat`. The look and motion come from the profile's **tracer entry** (`RifleCombatPresentation` -> Tracer): `width`, `length` of the streak, `speed` in m/s (0 or less draws it at once, over about 0.05 s), start/end colour. To change only the look, edit those fields; to change the shape, edit the prefab. The tracer is purely cosmetic; damage was applied before it was spawned.

## Change or add an impact

Impacts are per surface, on the profile (`impacts` list): one entry per `ImpactSurfaceKind` with a prefab, a `scale` and a sound cue. A surface with no entry uses the entry for `Default`, then the fallback profile, then shows nothing.

- Which surface a collider is: an `ImpactSurface` component on the collider or a parent; else `Character` when it belongs to a unit (`Health`); else `Default`. Generated missions tag walls, baffles and pillars `Concrete`, low walls and crates `Metal` (`ImpactSurfaces.ForBox`).
- To restyle an existing surface, edit its prefab (`ImpactConcrete`, `ImpactMetal`, `ImpactCharacter`, `ImpactDefault`) or point the entry at another prefab.
- To add a surface (say, Glass): add the value **at the end** of `ImpactSurfaceKind` (the values are serialized in profiles, never renumber), add an entry to each profile that should react, and put `ImpactSurface` (kind Glass) on the colliders. Nothing else changes.

## Change the explosion

`Prefabs/ExplosionGrenade.prefab` has separate child systems: Flash, Fireball, Dust, Sparks, Fragments, Smoke, a flat Ring and a capped light. It is authored for a gameplay radius of **3 m** (`referenceRadius` on `BlastCombatPresentation`; Blast's gameplay radius is also 3). The presenter scales the whole prefab by `gameplay radius / referenceRadius`, so a bigger ability reuses it. The ring grows to exactly the reference radius, so it reads as the real boundary: do not size the visual past it, and do not add any damage radius, collider or script to the prefab (the VFX never owns gameplay numbers). Strength levers if the owner finds it too subtle: the burst counts, sizes and speeds of each system in `EffectPrefabs.ExplosionGrenade` (or in the prefab), and `cameraImpulse` (0..1) on the profile. Blast reaches the profile through `AbilityDefinition.presentation`.

## Change a sound

A sound is an `AudioCue`: a list of clips (one is picked at random), `volume`, `pitchMin`/`pitchMax` (random spread), `priority` (0 most important; loud sounds steal quiet ones when all 10 voices are busy), `minDistance`/`maxDistance` (falloff in metres), `minInterval` (seconds before the same cue can start again). Cues live in the profile: `fireCue`, each impact entry's cue, `areaCue`, `pulseCue`, `deathCue`. To replace a sound, assign your clip in the cue. **Use a new file name**, not `PLACEHOLDER_*.wav`: a `Rebuild Effects` run overwrites those files. The master volume on the presenter's settings scales every cue (default 0.8). Two cues of the same kind close together are softened (gain 1/sqrt(n+1) over 0.15 s), so a squad volley does not stack.

## Add a weapon's look

1. Create a new profile (Assets -> Create -> Blackglass -> Combat Presentation Profile). Fill only the parts that differ (muzzle, tracer, fire cue, impacts, recoil); reuse the existing prefabs with a different `muzzleScale`, tracer style or cue.
2. Assign it to the weapon's `WeaponVisualAsset.presentation`.

No code. A weapon with no profile uses the fallback profile (character and default impacts only). An ability can carry its own profile in `AbilityDefinition.presentation`; Aimed Shot uses the weapon's profile unless its own `presentation` is set.

The **heal pulse** is not on the ability: Mend and the Medkit both end in `Health.Healed`, and the pulse (a green ring and motes at the unit's feet, plus the chime) comes from the presenter's `supportProfile`, which is `DefaultCombatPresentation` in both shipped scenes. Mend's `AbilityDefinition.presentation` is not read.

## Budgets and quality

Caps on *concurrently active* effects (`EffectBudget`). When a category is full the new request is skipped (cosmetic degradation; nothing blocks gameplay, nothing is logged per frame; one aggregated `Refused` counter). The one exception: a fifth explosion recycles the oldest explosion.

| Category | High | Medium (0.6x) | Low (0.3x) |
|---|---|---|---|
| Muzzle | 12 | 7 | 4 |
| Tracer | 24 | 14 | 7 |
| Impact | 24 | 14 | 7 |
| Area (explosion) | 4 | 2 | 1 |
| Pulse (heal) | 8 | 5 | 2 |
| Lights (all effects together) | 6 | 4 | 2 |
| Audio voices | 10 | 10 | 10 |

Every cap is at least 1. A light beyond the cap is simply left disabled on its effect. Audio is capped by `CombatAudio.DefaultVoices` (10) at every tier, with a per-cue minimum interval and the crowd gain above. There are no persistent marks (no decals).

`PresentationSettings` (serialized on `CombatPresenter`): `enabled`, `quality` (Low / Medium / High), `masterVolume` (0..1, default 0.8), `cameraShake` (0..1, default 0.5; 0 turns the impulse off). Both shipped scenes are saved at quality High. The budget is built when the presenter wakes, so set `quality` in the Inspector **before** pressing Play; at run time call `CombatPresenter.SetQuality` (the preview scene's Quality button does; it also clears everything playing). There is no player-facing settings screen yet.

## The knowledge gate

What the player may see follows what the squad knows (decision 037); the gate never looks at the F12 truth view and never reveals anything. The shooter counts as *observed* by `IntelligenceService.CanTarget` (friendlies always; a hostile that is currently seen, or exposed for 3 s by having fired). A point is judged at **region level**: `Visible` (its region is observed), `Hidden` (a known region that is not observed) or `Outside` (in no region).

| Element | Shown when |
|---|---|
| Muzzle flash, smoke, light, fire sound, recoil | the shooter is observed |
| Tracer | the shooter is observed **and** no sample is `Hidden`: the path is sampled at both ends and every 3 m (`Outside` samples are neutral). Otherwise it is suppressed whole; there is no partial masking |
| Impact on the world and its sound | its point, lifted 0.3 m off the surface, is `Visible` |
| Impact on a character, hit flash, default burst, hit sound | the victim is observed. A victim that has just died is judged by the snapshot point of the hit (`CanTarget` is false for the dead) |
| Heal pulse, death sound | the unit is observed (the death sound: the death point is `Visible`) |
| Explosion, its sound, camera impulse | the centre is `Visible` (impulse also fades with distance to the camera rig, to 0 at 40 m) |
| Lingering effects (impacts, explosion, pulse) | re-gated whenever `IntelligenceService.Changed` fires: hidden with `forceRenderingOff` and their light off, never restarted or released |

Hidden one-shot events are dropped, never buffered. A missing input means "do not show". With no `IntelligenceService` (the preview scene) everything is visible. Consequences: a hostile that fires from fog shows its flash for its 3 s exposure (037), but its tracer only if the whole path is lit; a visible friendly shooting into the dark shows its flash and sound but no tracer and no impact; the Recon Scan reveals but shows no effects.

## Time and pause

Effects run on **scaled time**: a tactical pause freezes particles, tracers, the hit flash, recoil, the camera impulse and the light envelope where they are, and resume continues them. A `PooledEffect` lifetime is a `Time.time` deadline checked only while `SimulationTime.IsRunning`. Audio follows `TacticalPause.Changed`: combat voices are paused and unpaused explicitly (never `AudioListener.pause`), and `CombatAudio` keeps its own audio clock (the one place in `Presentation/` that reads unscaled time, allow-listed by the source audit) so a paused voice is never mistaken for a finished one. The camera's own pan, zoom and rotate stay on unscaled time. No presentation script writes `Time.timeScale`; `PresentationSourceAuditTests` fails on `timeScale =`, `unscaledTime`, `realtimeSinceStartup`, `useUnscaledTime = true` and any other `unscaledDeltaTime`.

## Preview scene

Open `Assets/_Project/Scenes/Presentation Test Scene.unity` and press Play. It is isolated (no mission, no XP, no inventory): a floor, a concrete block (left), a metal block (right), two shooters with the rifle (left one at x=-3, "right shooter" at x=6), dummies with `Health`, a Blast caster, a healee, the real `TacticalPause`, `CombatPresenter`, and the gameplay camera rig. The IMGUI panel on the left:

| Button | What it does |
|---|---|
| Fire at character (real attack) | the left shooter makes a real `UnitAttacker` attack on the left target: muzzle flash, tracer, body hit, sounds, recoil, hit flash |
| Fire at concrete block | a synthetic cover-miss record aimed just behind the concrete block, handed to `UnitPresentation.Deliver`; the shot geometry finds the block's surface: concrete impact |
| Fire at metal block | the same on the metal block: sparks |
| Shot past the target (cover miss) | a synthetic cover-miss record at the left target: the line passes beside it and ends on the first surface or the 30 m cap |
| Fire from the right shooter | a real attack from the right shooter (right half of the arena, used with the fog switch) |
| Stress: 40 shots x2 | 40 real attacks from each shooter in one go: watch the budgets and the voice limit (the counter shows active effects, lights and refused requests) |
| Cast Blast (real ability, radius 3) | a real `UnitAbilities` Blast from the caster (a cooldown-0 clone, `PreviewBlast`) at the point between the blocks: explosion, boom, camera impulse |
| Heal pulse (real Health.Heal) | 40 real damage then 40 real heal on the healee: the pulse comes from `Health.Healed` as in a mission |
| Tactical pause / Resume | the real `TacticalPause`: effects and combat sounds freeze, resume continues them |
| Fog simulation | replaces the knowledge queries: the right half (x > 0) becomes hidden ground and units there become unobserved. It only ever adds a restriction. Toggling it re-gates lingering effects |
| Presentation: ON / OFF | `CombatPresenter.Enabled`; off releases everything and shows nothing |
| Quality: Low / Medium / High | cycles the tier (`SetQuality`: clears and rebuilds the budget) |
| Camera shake slider | `PresentationSettings.cameraShake` live (0 = off) |
| counter line | `Active effects N  lights N  refused N` |

The surface-impact buttons (concrete, metal, past-target) use a synthetic record; the character, Blast, heal and stress buttons use the real gameplay path.

## Regenerating the assets

Everything under `Assets/_Project/Presentation/` is generated by an idempotent Editor builder. It creates only what is missing and fills only **empty** references, so hand-tuned prefabs, profiles and cues survive. Menu items:

- `Blackglass/Presentation/Build Effects`: create what is missing (textures, materials, prefabs, sounds, profiles) and wire the profile references on the rifle and marksman rifle visuals and on Blast.
- `Blackglass/Presentation/Rebuild Effects (overwrite prefabs and materials)`: regenerate textures, materials, prefabs **and the `PLACEHOLDER_*.wav` files** in place (GUIDs kept). Profiles keep their values. This discards hand edits to those generated assets and to the placeholder files: assign replacement sounds under a new file name first.
- `Blackglass/Presentation/Build Preview Scene`: rebuild the Presentation Test Scene (and `PreviewBlast`).
- `Blackglass/Presentation/Wire ProceduralMission Scene`: add the presenter and `CameraImpulse` to the mission scene and connect them.

The same from the command line (Git Bash, from the repo root, Unity Editor closed; each prints `EXIT=0` when it worked, log in `Logs/Method-*.log`):

```
Tools/run-method.sh Blackglass.EditorTools.CombatPresentationAssetBuilder.BuildFromCommandLine
Tools/run-method.sh Blackglass.EditorTools.CombatPresentationAssetBuilder.RebuildFromCommandLine
Tools/run-method.sh Blackglass.EditorTools.CombatPresentationSceneBuilder.BuildPreviewFromCommandLine
Tools/run-method.sh Blackglass.EditorTools.CombatPresentationSceneBuilder.WireProceduralMissionFromCommandLine
```

The tests: `Tools/run-tests.sh EditMode` / `PlayMode` (optionally a class filter such as `Blackglass.Tests.CombatPresentationAssetTests`). `PresentationPerformanceReportTests` appends frame times to `.claude/scratchpad/phase125/performance.txt`.

## Known limits

- **Subtle at gameplay zoom.** The default body burst, the body hit and the muzzle smoke are small and faint; the explosion's fireball, sparks, fragments and smoke are subtle (levers: `EffectPrefabs.ExplosionGrenade`); the heal ring is about 45 px; the Darius hit flash is subtle (`FlashBoost` 2.5 on a white `_BaseColor`); rifle recoil is not visible in a still (tested numerically). The owner may want any of these stronger.
- Dust quads show faint banding where soft particles meet the ground (soft particles are not enabled). Additive sparks wash toward white or pink on bright or saturated surfaces.
- Killing-blow impacts use an analytic capsule fallback (the unit is already deactivated, nothing to raycast); a live target's hit point is a real raycast on its collider surface.
- `BurstPlacement` reads a `CapsuleCollider` on the unit root only (else a 0.5 m radius).
- Presentation is wired into `ProceduralMission` and the preview scene only; the Prototype scene and the test rigs have none.
- `EffectPool.Rent(null)` throws; `PooledEffect.Build` does not enforce scaled time on prefabs (the asset test does); trail renderers are not reset (none exist).
- The fire cue's `minInterval` is 0.05 s, so a same-frame volley of one cue collapses to one voice (and a pile of cues shares 10 voices).
- Quality has no in-game setting; the camera pointer ray moves a few cm under shake (up to about 19 cm on the ground at the minimum camera pitch of 25 degrees, at full strength).
- All sounds are generated placeholders that were never listened to in batch mode.
