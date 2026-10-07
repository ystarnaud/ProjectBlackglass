using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class EnvironmentThemeTests
    {
        GameObject a, b;
        EnvironmentTheme theme;

        [SetUp]
        public void SetUp()
        {
            a = new GameObject("A");
            b = new GameObject("B");
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(a);
            Object.DestroyImmediate(b);
            if (theme != null)
                Object.DestroyImmediate(theme);
        }

        [Test]
        public void Resolve_ReturnsAVariantOfTheElement_Deterministically()
        {
            theme = EnvironmentTheme.Create(3, new[]
            {
                new EnvironmentTheme.Entry { element = EnvironmentElement.Crate, variants = new[] { a, b } },
            });

            var first = theme.Resolve(EnvironmentElement.Crate, new Vector2Int(4, 5), 77);

            Assert.That(first, Is.SameAs(a).Or.SameAs(b));
            Assert.That(theme.Resolve(EnvironmentElement.Crate, new Vector2Int(4, 5), 77), Is.SameAs(first));
            Assert.That(theme.Has(EnvironmentElement.Crate), Is.True);
        }

        [Test]
        public void MissingEmptyAndNullEntries_CountAsMissing()
        {
            theme = EnvironmentTheme.Create(0, new[]
            {
                new EnvironmentTheme.Entry { element = EnvironmentElement.Crate, variants = new GameObject[0] },
                new EnvironmentTheme.Entry { element = EnvironmentElement.Floor, variants = null },
                new EnvironmentTheme.Entry { element = EnvironmentElement.Pillar, variants = new GameObject[] { null } },
            });

            foreach (var element in new[] { EnvironmentElement.Crate, EnvironmentElement.Floor, EnvironmentElement.Pillar, EnvironmentElement.Terminal })
            {
                Assert.That(theme.Has(element), Is.False, element.ToString());
                Assert.That(theme.Resolve(element, Vector2Int.zero, 1) == null, Is.True, element.ToString());
            }
        }

        [Test]
        public void ADifferentSalt_ChangesTheChoices_NotTheVariantSet()
        {
            var entries = new[] { new EnvironmentTheme.Entry { element = EnvironmentElement.LowCover, variants = new[] { a, b } } };
            var one = EnvironmentTheme.Create(1, entries);
            var two = EnvironmentTheme.Create(2, entries);
            try
            {
                var differs = false;
                for (var x = 0; x < 50; x++)
                    differs |= one.Resolve(EnvironmentElement.LowCover, new Vector2Int(x, 0), 5) != two.Resolve(EnvironmentElement.LowCover, new Vector2Int(x, 0), 5);
                Assert.That(differs, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(one);
                Object.DestroyImmediate(two);
            }
        }
    }
}
