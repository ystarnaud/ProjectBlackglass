using NUnit.Framework;

namespace Blackglass.Tests
{
    public class IntelligenceSettingsTests
    {
        [Test]
        public void Default_IsFogOffFullKnowledgeAndNoCameras()
        {
            var settings = new IntelligenceSettings();
            Assert.That(settings.fogEnabled, Is.False);
            Assert.That(settings.map, Is.EqualTo(MapKnowledge.Full));
            Assert.That(settings.objectives, Is.EqualTo(ObjectiveKnowledge.All));
            Assert.That(settings.cameraCount, Is.EqualTo(0));
            Assert.That(settings.observationRange, Is.EqualTo(14f));
            Assert.That(settings.cameraRange, Is.EqualTo(12f));
            Assert.That(settings.cameraFov, Is.EqualTo(90f));
            Assert.That(settings.cameraStaysLive, Is.True);
            Assert.That(settings.exposureSeconds, Is.EqualTo(3f));
        }

        [Test]
        public void Validated_ClampsEveryNumber_AndLeavesTheOriginalAlone()
        {
            var original = new IntelligenceSettings
            {
                observationRange = 1000f, cameraCount = 99, cameraRange = 0f, cameraFov = 500f, exposureSeconds = -4f, enemyMarkersAtStart = 99,
            };
            var copy = original.Validated();
            Assert.That(copy, Is.Not.SameAs(original));
            Assert.That(copy.observationRange, Is.EqualTo(40f));
            Assert.That(copy.cameraCount, Is.EqualTo(6));
            Assert.That(copy.cameraRange, Is.EqualTo(4f));
            Assert.That(copy.cameraFov, Is.EqualTo(180f));
            Assert.That(copy.exposureSeconds, Is.EqualTo(0f));
            Assert.That(copy.enemyMarkersAtStart, Is.EqualTo(8));
            Assert.That(original.cameraCount, Is.EqualTo(99), "the original is untouched");

            var low = new IntelligenceSettings { observationRange = 0f, cameraFov = 1f }.Validated();
            Assert.That(low.observationRange, Is.EqualTo(4f));
            Assert.That(low.cameraFov, Is.EqualTo(30f));
        }

        [Test]
        public void Presets_AreFiveAndNamed_AndOnlyFullHasFogOff()
        {
            Assert.That(IntelligenceSettings.PresetCount, Is.EqualTo(5));
            for (var i = 0; i < IntelligenceSettings.PresetCount; i++)
            {
                Assert.That(IntelligenceSettings.PresetName(i), Is.Not.Empty);
                Assert.That(IntelligenceSettings.Preset(i).fogEnabled, Is.EqualTo(i != 0), IntelligenceSettings.PresetName(i));
            }
            Assert.That(IntelligenceSettings.Preset(0).Describe(), Is.EqualTo(new IntelligenceSettings().Describe()));
        }

        [Test]
        public void Preset_WrapsAroundAndBlindIsFullFogWithCameras()
        {
            Assert.That(IntelligenceSettings.Preset(5).fogEnabled, Is.False, "index 5 wraps to 0");
            var blind = IntelligenceSettings.Blind();
            Assert.That(blind.map, Is.EqualTo(MapKnowledge.None));
            Assert.That(blind.objectives, Is.EqualTo(ObjectiveKnowledge.ExtractionOnly));
            Assert.That(blind.cameraCount, Is.EqualTo(3));
            Assert.That(IntelligenceSettings.LayoutKnown().map, Is.EqualTo(MapKnowledge.Full));
            Assert.That(IntelligenceSettings.LayoutKnown().objectives, Is.EqualTo(ObjectiveKnowledge.None));
            Assert.That(IntelligenceSettings.ObjectivesKnown().objectives, Is.EqualTo(ObjectiveKnowledge.All));
            Assert.That(IntelligenceSettings.ObjectivesKnown().map, Is.EqualTo(MapKnowledge.None));
            var briefed = IntelligenceSettings.Briefed();
            Assert.That(briefed.map, Is.EqualTo(MapKnowledge.Partial));
            Assert.That(briefed.objectives, Is.EqualTo(ObjectiveKnowledge.ExtractionAndTerminal));
            Assert.That(briefed.enemyMarkersAtStart, Is.EqualTo(1));
        }

        [Test]
        public void MissionSettingsValidated_DeepClonesTheIntelligenceSettings()
        {
            var original = new MissionSettings { intelligence = IntelligenceSettings.Blind() };
            var copy = original.Validated();
            Assert.That(copy.intelligence, Is.Not.SameAs(original.intelligence));
            copy.intelligence.cameraCount = 1;
            Assert.That(original.intelligence.cameraCount, Is.EqualTo(3));
        }

        [Test]
        public void MissionSettingsValidated_ReplacesANullIntelligenceWithDefaults()
        {
            var original = new MissionSettings { intelligence = null };
            Assert.That(original.Validated().intelligence.fogEnabled, Is.False);
        }

        [Test]
        public void MissionSettingsDescribe_IsUnchangedWhileFogAndCamerasAreOff_AndNamesThemWhenOn()
        {
            Assert.That(new MissionSettings().Describe(), Does.Not.Contain("intel"));
            var text = new MissionSettings { intelligence = IntelligenceSettings.Blind() }.Describe();
            Assert.That(text, Does.Contain("intel=[").And.Contain("fog=True"));
        }
    }
}
