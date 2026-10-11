#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>
    /// A small arena with a CombatPresenter whose profiles use primitive stand-in effect prefabs (a particle system, a light,
    /// a tracer line), so presentation behaviour can be tested without the generated art. Optional 0.9 m wall for cover.
    /// It can share another rig's world (then that rig owns it and builds the environment; pass createEnvironment false).
    /// </summary>
    internal sealed class PresentationRig : IDisposable
    {
        public readonly TestWorld World;
        readonly bool ownsWorld;
        public readonly TacticalPause Pause;
        public readonly CombatPresenter Presenter;
        public readonly CombatPresentationProfile RifleProfile;
        public readonly CombatPresentationProfile FallbackProfile;
        public readonly CameraImpulse Impulse;
        public readonly GameObject EnvironmentRoot;
        public readonly GameObject MuzzlePrefab;
        public readonly GameObject TracerPrefab;
        public readonly GameObject AreaPrefab;
        public readonly GameObject PulsePrefab;
        readonly List<AudioClip> clips = new List<AudioClip>();
        public CommandableUnit Defender { get; private set; }
        public CoverRegistry Registry { get; private set; }
        public CoverLocation CoverPoint { get; private set; }

        public PresentationRig(bool withWall = false, IntelligenceService intelligence = null, TestWorld world = null,
            bool createEnvironment = true)
        {
            World = world != null ? world : new TestWorld();
            ownsWorld = world == null;
            if (createEnvironment)
            {
                EnvironmentRoot = withWall
                    ? World.CreateEnvironment((new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f)))
                    : World.CreateEnvironment();
                foreach (var box in EnvironmentRoot.GetComponentsInChildren<BoxCollider>())
                    box.gameObject.AddComponent<ImpactSurface>().Set(ImpactSurfaceKind.Concrete);
            }
            Pause = World.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            World.Track(new GameObject("Listener")).AddComponent<AudioListener>();   // combat audio needs one, or the engine logs every frame

            MuzzlePrefab = Fx("MuzzleFx", light: true);
            TracerPrefab = Fx("TracerFx", tracer: true);
            AreaPrefab = Fx("AreaFx", light: true);
            PulsePrefab = Fx("PulseFx");
            RifleProfile = CombatPresentationProfile.CreateEmpty();
            RifleProfile.SetMuzzle(MuzzlePrefab, 1.5f);
            RifleProfile.SetTracer(TracerStyle.Create(TracerPrefab, 0.04f, 3f, 200f));
            RifleProfile.SetImpacts(
                SurfaceImpact.Create(ImpactSurfaceKind.Concrete, Fx("ConcreteFx"), Cue(0.2f, 160)),
                SurfaceImpact.Create(ImpactSurfaceKind.Metal, Fx("MetalFx"), Cue(0.2f, 160)),
                SurfaceImpact.Create(ImpactSurfaceKind.Character, Fx("CharacterFx"), Cue(0.2f, 160)),
                SurfaceImpact.Create(ImpactSurfaceKind.Default, Fx("DefaultFx"), Cue(0.2f, 160)));
            RifleProfile.SetFireCue(Cue(0.3f, 100));
            RifleProfile.SetRecoil(0.05f, 0.2f);
            RifleProfile.SetArea(AreaPrefab, 3f, Cue(1f, 40), 0.6f);
            FallbackProfile = CombatPresentationProfile.CreateEmpty();
            FallbackProfile.SetImpacts(SurfaceImpact.Create(ImpactSurfaceKind.Character, Fx("FallbackCharacterFx"), Cue(0.2f, 160)),
                SurfaceImpact.Create(ImpactSurfaceKind.Default, Fx("FallbackDefaultFx"), Cue(0.2f, 160)));
            FallbackProfile.SetDeathCue(Cue(0.3f, 150));
            FallbackProfile.SetPulse(PulsePrefab, Cue(0.4f, 140));

            var systems = World.Track(new GameObject("Systems"));
            systems.SetActive(false);
            Presenter = systems.AddComponent<CombatPresenter>();
            Impulse = World.Track(new GameObject("CameraImpulse")).AddComponent<CameraImpulse>();
            Presenter.Bind(intelligence, Pause, Impulse, FallbackProfile, FallbackProfile);
            systems.SetActive(true);
        }

        AudioCue Cue(float seconds = 0.3f, int priority = 128, float minInterval = 0.02f)
        {
            var clip = AudioClip.Create("rig", Mathf.Max(1, (int)(44100 * seconds)), 1, 44100, false);
            clips.Add(clip);
            return AudioCue.Create(1f, priority, minInterval, clip);
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

        /// <summary>
        /// A ranged shooter (retaliation off) holding a weapon with a muzzle, with a UnitPresentation bound to the presenter
        /// (unless `present` is false: a caller adding components the presentation listens to, such as UnitAbilities, binds
        /// it afterwards, as the mission spawner does).
        /// </summary>
        public CommandableUnit CreateShooter(Vector3 ground, bool weapon = true, bool withMuzzle = true,
            CombatRole role = CombatRole.Ranged, int damage = 10, float cooldown = 0.1f, bool present = true)
        {
            var unit = World.CreateFighter(ground, 1000, damage, cooldown, role, role == CombatRole.Melee ? 2f : 30f);
            unit.GetComponent<AutoRetaliate>().enabled = false;
            if (weapon)
                Equip(unit.gameObject, withMuzzle);
            if (present)
                unit.gameObject.AddComponent<UnitPresentation>().Bind(Presenter);
            Physics.SyncTransforms();
            return unit;
        }

        /// <summary>Gives a unit made elsewhere (another rig's) a weapon socket and the rig's rifle, whose profile is RifleProfile.</summary>
        public void Equip(GameObject unit, bool withMuzzle = true)
        {
            var socket = new GameObject(UnitWeaponVisual.SocketName);
            socket.transform.SetParent(unit.transform, false);
            socket.transform.localPosition = new Vector3(0.3f, 0.5f, 0.4f);
            unit.AddComponent<UnitWeaponVisual>().Show(WeaponAsset(withMuzzle));
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
            if (ownsWorld)
                World.Dispose();
            foreach (var clip in clips)
                UnityEngine.Object.DestroyImmediate(clip);
            clips.Clear();
            Time.timeScale = 1f;
        }
    }
}
#endif
