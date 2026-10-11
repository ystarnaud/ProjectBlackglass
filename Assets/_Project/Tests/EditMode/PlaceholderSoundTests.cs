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
