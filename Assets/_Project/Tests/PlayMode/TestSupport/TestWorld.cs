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

            var surface = root.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.Children;
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            surface.BuildNavMesh();
            return root;
        }

        public CommandableUnit CreateUnit(Vector3 groundPosition)
        {
            var unit = Track(GameObject.CreatePrimitive(PrimitiveType.Capsule));
            unit.name = "TestUnit";
            unit.transform.position = groundPosition + Vector3.up;
            unit.AddComponent<UnitMover>();
            unit.GetComponent<NavMeshAgent>().baseOffset = 1f;
            unit.AddComponent<UnitAttacker>();
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
        public CommandableUnit CreateFighter(Vector3 groundPosition, int maxHealth = 100, int damage = 25, float cooldown = 1f)
        {
            var host = Track(GameObject.CreatePrimitive(PrimitiveType.Capsule));
            host.name = "TestFighter";
            host.SetActive(false);
            host.transform.position = groundPosition + Vector3.up;
            host.AddComponent<UnitMover>();
            host.GetComponent<NavMeshAgent>().baseOffset = 1f;
            host.AddComponent<UnitAttacker>().Initialize(2f, damage, cooldown);
            host.AddComponent<Health>().Initialize(maxHealth);
            var unit = host.AddComponent<CommandableUnit>();
            host.SetActive(true);
            return unit;
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
