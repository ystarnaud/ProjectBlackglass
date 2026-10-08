using System;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace Blackglass
{
    /// <summary>What a built mission leaves in the scene: one root to destroy, the geometry parent and the actors parent.</summary>
    public sealed class GeneratedMission
    {
        public const string RootName = "GeneratedMissionRoot";

        public GameObject Root { get; internal set; }
        /// <summary>Floor and obstacle cubes. The NavMeshSurface sits here, so only this node is baked.</summary>
        public Transform Geometry { get; internal set; }
        /// <summary>Parent of every spawned unit.</summary>
        public Transform Actors { get; internal set; }
        public NavMeshSurface Surface { get; internal set; }
        public MissionLayout Layout { get; internal set; }
        public ObjectivePlan Plan { get; internal set; }
        /// <summary>The terminal, or null when the mission has no hack objective.</summary>
        public MissionInteractable Terminal { get; internal set; }
        public SecurityPlan Security { get; internal set; } = SecurityPlan.Empty;
        /// <summary>The camera-control terminal, or null when the mission has no cameras.</summary>
        public MissionInteractable CameraTerminal { get; internal set; }
        public CameraNetwork Network { get; internal set; }
        public Transform ExtractionZone { get; internal set; }

        /// <summary>The themed visual modules, or null when the mission was built without a theme (the cubes are then the look).</summary>
        public Transform Visuals { get; internal set; }

        /// <summary>
        /// Debug view: false hides the themed visuals and shows the gameplay cubes (the colliders, NavMesh and cover geometry
        /// that decide everything); true restores the themed look. Ignored when there are no visuals.
        /// </summary>
        public void SetVisualsVisible(bool visible)
        {
            if (Visuals == null)
                return;
            Visuals.gameObject.SetActive(visible);
            foreach (Transform child in Geometry)
                if (child.TryGetComponent<MeshRenderer>(out var renderer))
                    renderer.enabled = !visible;
        }
    }

    /// <summary>
    /// Turns a layout into primitive geometry and a runtime NavMesh. Floors are plain cubes; every obstacle cube is a
    /// CoverSurface (so cover discovery needs no special knowledge of missions) and a Not Walkable NavMeshModifier (so
    /// no walkable island forms on a wall or crate top). Materials are optional placeholders. With a theme the cubes stay
    /// the gameplay objects (colliders, cover, NavMesh) but stop rendering; the themed visuals are the look.
    /// </summary>
    public static class MissionBuilder
    {
        const int NotWalkableArea = 1;

        public static GeneratedMission Build(MissionLayout layout, Material groundMaterial, Material obstacleMaterial,
            Action<Transform> addContent = null, EnvironmentTheme theme = null)
        {
            var root = new GameObject(GeneratedMission.RootName);
            var geometry = new GameObject("Geometry");
            geometry.transform.SetParent(root.transform, false);
            var actors = new GameObject("Actors");
            actors.transform.SetParent(root.transform, false);

            var floorIndex = 0;
            foreach (var rect in layout.FloorRects)
            {
                var size = new Vector3(rect.width, MissionConstants.FloorThickness, rect.height);
                var center = layout.RectCenter(rect) + Vector3.down * (MissionConstants.FloorThickness * 0.5f);
                Cube($"Floor_{++floorIndex}", center, size, groundMaterial, geometry.transform, false, theme == null);
            }
            foreach (var box in layout.Boxes)
            {
                var size = new Vector3(box.Footprint.width, box.Height, box.Footprint.height);
                var center = layout.RectCenter(box.Footprint) + Vector3.up * (box.Height * 0.5f);
                Cube(box.Name, center, size, obstacleMaterial, geometry.transform, true, theme == null);
            }

            // Objective content that must exist when the NavMesh is baked (the terminal) is added by the caller here. If it
            // throws, the half-built root must not stay in the scene.
            Transform visuals = null;
            try
            {
                addContent?.Invoke(geometry.transform);
                if (theme != null)
                    visuals = EnvironmentVisualBuilder.Build(layout, theme, root.transform);
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(root);
                throw;
            }

            // Auto sync is off: without this the colliders sit where CreatePrimitive made them until the next physics step.
            Physics.SyncTransforms();

            var surface = geometry.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.Children;
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            surface.BuildNavMesh();

            return new GeneratedMission
            {
                Root = root,
                Geometry = geometry.transform,
                Actors = actors.transform,
                Surface = surface,
                Layout = layout,
                Visuals = visuals,
            };
        }

        static void Cube(string name, Vector3 center, Vector3 size, Material material, Transform parent, bool obstacle, bool visible)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            cube.transform.SetParent(parent, false);
            cube.transform.position = center;
            cube.transform.localScale = size;
            if (material != null)
                cube.GetComponent<Renderer>().sharedMaterial = material;
            if (!visible)
                cube.GetComponent<MeshRenderer>().enabled = false;
            if (!obstacle)
                return;
            cube.AddComponent<CoverSurface>();
            var modifier = cube.AddComponent<NavMeshModifier>();
            modifier.overrideArea = true;
            modifier.area = NotWalkableArea;
        }
    }
}
