using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Instantiates the planned visual modules from a theme under one "Visuals" node. The node lives under the mission root,
    /// so tearing the mission down removes every visual. Prefabs are visuals only: any collider on an instance is removed
    /// before the NavMesh bake, so imported art can never change navigation, cover or LOS.
    /// </summary>
    public static class EnvironmentVisualBuilder
    {
        public const string RootName = "Visuals";

        static Material placeholderMaterial;

        public static Transform Build(MissionLayout layout, EnvironmentTheme theme, Transform parent)
        {
            var visuals = new GameObject(RootName).transform;
            visuals.SetParent(parent, false);
            var groups = new Dictionary<EnvironmentElement, Transform>();
            var warned = new HashSet<EnvironmentElement>();
            foreach (var placement in EnvironmentVisualPlanner.Plan(layout))
            {
                if (!groups.TryGetValue(placement.Element, out var group))
                {
                    group = new GameObject(placement.Element.ToString()).transform;
                    group.SetParent(visuals, false);
                    groups[placement.Element] = group;
                }
                var prefab = theme.Resolve(placement.Element, placement.Tile, layout.Seed);
                GameObject instance;
                if (prefab != null)
                {
                    instance = Object.Instantiate(prefab, group);
                    instance.name = prefab.name;
                    if (!instance.activeSelf)
                        instance.SetActive(true);
                }
                else
                {
                    if (warned.Add(placement.Element))
                        Debug.LogWarning($"Environment theme '{theme.name}' has no prefab for {placement.Element}; using a placeholder.");
                    instance = Placeholder(placement.Element.ToString());
                    instance.transform.SetParent(group, false);
                }
                instance.transform.SetPositionAndRotation(placement.Position, Quaternion.Euler(0f, placement.YawDegrees, 0f));
                StripColliders(instance);
            }
            return visuals;
        }

        internal static void StripColliders(GameObject instance)
        {
            foreach (var collider in instance.GetComponentsInChildren<Collider>(true))
                Object.DestroyImmediate(collider);
        }

        // A 1 m magenta block with its base on the ground: obviously wrong, never invisible, never solid.
        internal static GameObject Placeholder(string label)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "Cube";
            Object.DestroyImmediate(cube.GetComponent<Collider>());
            if (placeholderMaterial == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null)
                    shader = Shader.Find("Sprites/Default");
                placeholderMaterial = new Material(shader) { name = "EnvironmentPlaceholder", hideFlags = HideFlags.HideAndDontSave };
                placeholderMaterial.SetColor("_BaseColor", Color.magenta);
                placeholderMaterial.color = Color.magenta;
            }
            cube.GetComponent<Renderer>().sharedMaterial = placeholderMaterial;
            var holder = new GameObject("Missing_" + label);
            cube.transform.SetParent(holder.transform, false);
            cube.transform.localPosition = new Vector3(0f, 0.5f, 0f);
            return holder;
        }
    }
}
