using System;
using System.Collections;
using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    /// <summary>Builds small throwaway worlds for PlayMode tests and destroys them afterwards.</summary>
    internal sealed class TestWorld : IDisposable
    {
        readonly List<Object> created = new List<Object>();

        public T Track<T>(T obj) where T : Object
        {
            created.Add(obj);
            return obj;
        }

        /// <summary>40 x 40 m ground at the origin plus box obstacles, with a NavMesh built at runtime.</summary>
        public GameObject CreateEnvironment(params (Vector3 position, Vector3 scale)[] obstacles)
        {
            var root = Track(new GameObject("TestEnvironment"));
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.SetParent(root.transform, false);
            ground.transform.localScale = new Vector3(4f, 1f, 4f);

            foreach (var (position, scale) in obstacles)
            {
                var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                box.name = "Obstacle";
                box.transform.SetParent(root.transform, false);
                box.transform.position = position;
                box.transform.localScale = scale;
            }

            // Auto sync is off, so until the next physics step the boxes' colliders would still sit where
            // CreatePrimitive made them (a 1 m cube at the origin) and same-frame sight rays would miss the walls.
            Physics.SyncTransforms();

            var surface = root.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.Children;
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            surface.BuildNavMesh();
            return root;
        }

        /// <summary>The n-th box obstacle CreateEnvironment made (creation order), for wiring a CoverPoint to it.</summary>
        public static BoxCollider ObstacleCollider(GameObject environment, int index = 0) =>
            environment.GetComponentsInChildren<BoxCollider>()[index];

        /// <summary>
        /// A box with a collider outside the NavMesh environment (built after the bake, so it carves nothing). For
        /// geometry tests that only need a collider to raycast against.
        /// </summary>
        public GameObject CreateObstacle(Vector3 position, Vector3 scale)
        {
            var box = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            box.name = "LooseObstacle";
            box.transform.position = position;
            box.transform.localScale = scale;
            Physics.SyncTransforms();
            return box;
        }

        /// <summary>A cover point at exactly the given stand position, facing `forward`, protected by `obstacle`.</summary>
        public CoverPoint CreateCoverPoint(Vector3 position, Vector3 forward, Collider obstacle, float hitChance = 0.5f)
        {
            var host = Track(new GameObject("TestCoverPoint"));
            host.transform.SetPositionAndRotation(position, Quaternion.LookRotation(forward));
            var point = host.AddComponent<CoverPoint>();
            point.Initialize(obstacle, hitChance);
            return point;
        }

        /// <summary>A registry on its own object listing the given points.</summary>
        public CoverRegistry CreateRegistry(params CoverPoint[] points)
        {
            var registry = Track(new GameObject("CoverRegistry")).AddComponent<CoverRegistry>();
            registry.Initialize(points);
            return registry;
        }

        public CommandableUnit CreateUnit(Vector3 groundPosition)
        {
            var unit = Track(GameObject.CreatePrimitive(PrimitiveType.Capsule));
            unit.name = "TestUnit";
            unit.transform.position = groundPosition + Vector3.up;
            unit.AddComponent<UnitMover>();
            unit.GetComponent<NavMeshAgent>().baseOffset = 1f;
            unit.AddComponent<UnitAttacker>();
            unit.AddComponent<UnitCover>();
            return unit.AddComponent<CommandableUnit>();
        }

        /// <summary>A unit the player can select (a CreateUnit unit plus SelectableUnit).</summary>
        public SelectableUnit CreateFriendly(Vector3 groundPosition)
        {
            var unit = CreateUnit(groundPosition);
            unit.name = "TestFriendly";
            return unit.gameObject.AddComponent<SelectableUnit>();
        }

        /// <summary>
        /// A unit with Health that can fight and die. Assembled while inactive so every component's OnEnable sees
        /// the others (CommandableUnit subscribes to its Health there).
        /// </summary>
        public CommandableUnit CreateFighter(Vector3 groundPosition, int maxHealth = 100, int damage = 25, float cooldown = 1f,
            CombatRole role = CombatRole.Melee, float range = 2f, CoverRegistry registry = null)
        {
            var host = Track(GameObject.CreatePrimitive(PrimitiveType.Capsule));
            host.name = "TestFighter";
            host.SetActive(false);
            host.transform.position = groundPosition + Vector3.up;
            host.AddComponent<UnitMover>();
            host.GetComponent<NavMeshAgent>().baseOffset = 1f;
            host.AddComponent<UnitAttacker>().Initialize(range, damage, cooldown, role);
            host.AddComponent<Health>().Initialize(maxHealth);
            var cover = host.AddComponent<UnitCover>();
            if (registry != null)
                cover.Initialize(registry);
            var unit = host.AddComponent<CommandableUnit>();
            host.AddComponent<AutoRetaliate>();
            host.SetActive(true);
            return unit;
        }

        /// <summary>A fighter the player can select and control (a CreateFighter unit plus SelectableUnit).</summary>
        public SelectableUnit CreateFriendlyFighter(Vector3 groundPosition, int maxHealth = 100)
        {
            var unit = CreateFighter(groundPosition, maxHealth);
            unit.name = "TestFriendlyFighter";
            return unit.gameObject.AddComponent<SelectableUnit>();
        }

        /// <summary>A fighter with EnemyAI wired to the encounter. Hostile prototype stats by default.</summary>
        public EnemyAI CreateHostile(Vector3 groundPosition, Encounter encounter, int maxHealth = 60, int damage = 10,
            float cooldown = 1.2f, float detectionRange = 12f, CombatRole role = CombatRole.Melee, float range = 2f)
        {
            var unit = CreateFighter(groundPosition, maxHealth, damage, cooldown, role, range);
            unit.name = "TestHostile";
            unit.gameObject.SetActive(false);
            var ai = unit.gameObject.AddComponent<EnemyAI>();
            ai.Initialize(encounter, detectionRange);
            unit.gameObject.SetActive(true);
            return ai;
        }

        /// <summary>An ActiveCharacter on its own object, controlling the given unit (no roster, so Cycle does nothing).</summary>
        public ActiveCharacter CreateActiveCharacter(CommandableUnit unit, TacticalPause pause = null)
        {
            var active = Track(new GameObject("ActiveCharacter")).AddComponent<ActiveCharacter>();
            active.Initialize(unit, pause);
            return active;
        }

        /// <summary>A friendly fighter with CompanionAI wired to the active character and the encounter.</summary>
        public CompanionAI CreateCompanion(Vector3 groundPosition, ActiveCharacter active, Encounter encounter, int maxHealth = 100)
        {
            var unit = CreateFighter(groundPosition, maxHealth);
            unit.name = "TestCompanion";
            unit.gameObject.SetActive(false);
            var ai = unit.gameObject.AddComponent<CompanionAI>();
            ai.Initialize(active, encounter);
            unit.gameObject.SetActive(true);
            return ai;
        }

        public Health CreateDummy(Vector3 groundPosition)
        {
            var dummy = Track(GameObject.CreatePrimitive(PrimitiveType.Cylinder));
            dummy.name = "TestDummy";
            dummy.transform.position = groundPosition + Vector3.up;
            var health = dummy.AddComponent<Health>();
            dummy.AddComponent<HitFlash>();
            return health;
        }

        /// <summary>An empty Encounter on its own object; call Initialize with the two sides once they exist.</summary>
        public Encounter CreateEncounter() => Track(new GameObject("Encounter")).AddComponent<Encounter>();

        /// <summary>Waits (in real time, so it also works while paused) until the condition holds or the timeout passes.</summary>
        public static IEnumerator WaitUntil(Func<bool> condition, float timeoutSeconds)
        {
            var deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!condition() && Time.realtimeSinceStartup < deadline)
                yield return null;
        }

        public static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        public void Dispose()
        {
            for (var i = created.Count - 1; i >= 0; i--)
            {
                if (created[i] != null)
                    Object.Destroy(created[i]);
            }
            created.Clear();
        }
    }
}
