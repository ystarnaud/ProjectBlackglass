#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>A theme for tests: every element is a plain cube "prefab" (an ordinary scene object used as an Instantiate source).</summary>
    internal static class TestTheme
    {
        static readonly Dictionary<EnvironmentTheme, List<GameObject>> Sources = new Dictionary<EnvironmentTheme, List<GameObject>>();

        public static EnvironmentTheme Create(bool withColliders = true, params EnvironmentElement[] omit)
        {
            var sources = new List<GameObject>();
            var entries = new List<EnvironmentTheme.Entry>();
            foreach (EnvironmentElement element in System.Enum.GetValues(typeof(EnvironmentElement)))
            {
                if (omit.Contains(element))
                    continue;
                var source = GameObject.CreatePrimitive(PrimitiveType.Cube);
                source.name = "Test_" + element;
                source.transform.position = new Vector3(0f, -1000f, 0f);
                if (!withColliders)
                    Object.DestroyImmediate(source.GetComponent<Collider>());
                sources.Add(source);
                entries.Add(new EnvironmentTheme.Entry { element = element, variants = new[] { source } });
            }
            var theme = EnvironmentTheme.Create(1, entries.ToArray());
            Sources[theme] = sources;
            return theme;
        }

        public static void Dispose(EnvironmentTheme theme)
        {
            if (theme == null)
                return;
            if (Sources.TryGetValue(theme, out var sources))
            {
                foreach (var source in sources)
                    Object.DestroyImmediate(source);
                Sources.Remove(theme);
            }
            Object.DestroyImmediate(theme);
        }
    }
}
#endif
