using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Blackglass.Tests
{
    public class IntelligenceAssetTests
    {
        const string Root = "Assets/_Project/";

        [Test]
        public void ReconScan_HasTheSpecifiedNumbers()
        {
            var scan = AssetDatabase.LoadAssetAtPath<AbilityDefinition>(Root + "Data/Abilities/ReconScan.asset");
            Assert.That(scan, Is.Not.Null, "run Blackglass/Intelligence/Wire ProceduralMission Scene");
            Assert.That(scan.DisplayName, Is.EqualTo("Recon Scan"));
            Assert.That(scan.TargetMode, Is.EqualTo(AbilityTargetMode.Ground));
            Assert.That(scan.Effect, Is.EqualTo(AbilityEffect.Reveal));
            Assert.That(scan.Range, Is.EqualTo(18f));
            Assert.That(scan.Radius, Is.EqualTo(12f));
            Assert.That(scan.Cooldown, Is.EqualTo(25f));
            Assert.That(scan.RevealSeconds, Is.EqualTo(6f));
            Assert.That(scan.RequiresLineOfSight, Is.False);
        }

        [Test]
        public void Kestrel_CarriesTheScanAsHerSecondAbility()
        {
            var kestrel = AssetDatabase.LoadAssetAtPath<OperativeDefinition>(Root + "Data/Operatives/Definitions/Kestrel.asset");
            var scan = AssetDatabase.LoadAssetAtPath<AbilityDefinition>(Root + "Data/Abilities/ReconScan.asset");
            Assert.That(kestrel.Abilities.Length, Is.EqualTo(2));
            Assert.That(kestrel.Abilities[0].DisplayName, Is.EqualTo("Aimed Shot"));
            Assert.That(kestrel.Abilities[1], Is.SameAs(scan));
        }

        [Test]
        public void TheOtherOperatives_AreUnchanged()
        {
            var darius = AssetDatabase.LoadAssetAtPath<OperativeDefinition>(Root + "Data/Operatives/Definitions/Darius.asset");
            var sable = AssetDatabase.LoadAssetAtPath<OperativeDefinition>(Root + "Data/Operatives/Definitions/Sable.asset");
            Assert.That(darius.Abilities.Select(a => a.DisplayName), Is.EqualTo(new[] { "Aimed Shot", "Blast" }));
            Assert.That(sable.Abilities.Select(a => a.DisplayName), Is.EqualTo(new[] { "Mend", "Aimed Shot" }));
        }

        [TestCase("IntelFog")]
        [TestCase("IntelVeil")]
        [TestCase("IntelMarker")]
        public void TheIntelMaterials_Exist(string name)
        {
            Assert.That(AssetDatabase.LoadAssetAtPath<Material>(Root + "Materials/" + name + ".mat"), Is.Not.Null, name);
        }

        [Test]
        public void TheVeilMaterial_IsTransparent_AndTheFogIsNot()
        {
            var veil = AssetDatabase.LoadAssetAtPath<Material>(Root + "Materials/IntelVeil.mat");
            var fog = AssetDatabase.LoadAssetAtPath<Material>(Root + "Materials/IntelFog.mat");
            Assert.That(veil.renderQueue, Is.GreaterThanOrEqualTo(3000));
            Assert.That(fog.renderQueue, Is.LessThan(3000));
        }
    }
}
