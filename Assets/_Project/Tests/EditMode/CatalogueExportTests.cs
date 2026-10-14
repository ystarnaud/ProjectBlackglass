using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Blackglass.BackendTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>The backend catalogue export: the project's ids, the canonical writer and the committed file.</summary>
    public class CatalogueExportTests
    {
        static T Load<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            Assert.That(asset != null, Is.True, "missing asset " + path);
            return asset;
        }

        [Test]
        public void ProjectAbilitiesAndRoles_CarryTheirAuthoredIds()
        {
            var abilities = new Dictionary<string, string>
            {
                ["Assets/_Project/Data/Abilities/AimedShot.asset"] = "ability.aimed-shot",
                ["Assets/_Project/Data/Abilities/Blast.asset"] = "ability.blast",
                ["Assets/_Project/Data/Abilities/Mend.asset"] = "ability.mend",
                ["Assets/_Project/Data/Abilities/ReconScan.asset"] = "ability.recon-scan",
            };
            foreach (var pair in abilities)
            {
                var ability = Load<AbilityDefinition>(pair.Key);
                Assert.That(ability.Id, Is.EqualTo(pair.Value), pair.Key);
                Assert.That(ability.Id, Is.Not.EqualTo(ability.DisplayName));
            }
            var roles = new Dictionary<string, string>
            {
                ["Assets/_Project/Data/Operatives/Roles/Assault.asset"] = "role.assault",
                ["Assets/_Project/Data/Operatives/Roles/Recon.asset"] = "role.recon",
                ["Assets/_Project/Data/Operatives/Roles/Support.asset"] = "role.support",
            };
            foreach (var pair in roles)
            {
                var role = Load<OperativeRole>(pair.Key);
                Assert.That(role.Id, Is.EqualTo(pair.Value), pair.Key);
                Assert.That(role.Id, Is.Not.EqualTo(role.DisplayName));
            }
        }

        [Test]
        public void ProjectStarterLoadout_ListsItsThreeOperatives()
        {
            var starter = Load<StarterLoadout>("Assets/_Project/Data/Items/StarterLoadout.asset");
            Assert.That(starter.Operatives.Select(o => o.operative.Id), Is.EquivalentTo(new[]
            {
                "6f1d2a40-3b7c-4e95-8a1f-52c0d9e7b301",
                "a93e5c17-0d48-4f2b-b6a3-7e1c8d4f9a02",
                "2c84b7e9-51a6-4d03-9f7e-c3a0165d8b03",
            }));
        }

        const string One = "00000000-0000-4000-8000-000000000001";
        const string Two = "00000000-0000-4000-8000-000000000002";

        // The golden vector as a model, already in canonical order (the builder's sorting is CatalogueBuilderTests').
        static CatalogueDocument GoldenDocument()
        {
            var d = new CatalogueDocument();
            d.Items.Add(new CatalogueItem { Id = "item.alpha-rifle", Category = "weapon", Slot = "weapon", MaxStack = 1 });
            d.Items.Add(new CatalogueItem { Id = "item.beta-vest", Category = "armor", Slot = "armor", MaxStack = 1 });
            d.Items.Add(new CatalogueItem { Id = "item.delta-boots", Category = "utility", Slot = "utility", MaxStack = 1 });
            d.Items.Add(new CatalogueItem { Id = "item.gamma-kit", Category = "consumable", Slot = "none", Stackable = true, MaxStack = 3, HealAmount = 10 });
            d.LootTable.Version = 7;
            d.LootTable.ContainerCount = 2;
            d.LootTable.MinItems = 1;
            d.LootTable.MaxItems = 3;
            d.LootTable.Entries.Add(new CatalogueLootEntry { ItemId = "item.gamma-kit", MinQuantity = 1, MaxQuantity = 2, Weight = 5 });
            d.LootTable.Entries.Add(new CatalogueLootEntry { ItemId = "item.alpha-rifle", MinQuantity = 1, MaxQuantity = 1, Weight = 1 });
            d.Progression.XpThresholds.AddRange(new[] { 0, 10, 30 });
            d.Progression.MissionCompletionXp = 20;
            d.Progression.ChoiceIds.AddRange(new[] { "choice.one", "choice.two" });
            var one = new CatalogueOperative { Id = One, RoleId = "role.alpha" };
            one.AbilityIds.AddRange(new[] { "ability.two", "ability.one" });
            d.Operatives.Add(one);
            d.Operatives.Add(new CatalogueOperative { Id = Two, RoleId = "role.beta" });
            var starter = new CatalogueStarterOperative { OperativeId = One };
            starter.Grants.Add(new CatalogueGrant { ItemId = "item.alpha-rifle", Quantity = 1, Equip = true });
            starter.Grants.Add(new CatalogueGrant { ItemId = "item.gamma-kit", Quantity = 2 });
            d.Starter.Operatives.Add(starter);
            d.Starter.Stash.Add(new CatalogueStashGrant { ItemId = "item.beta-vest", Quantity = 1 });
            d.Starter.Stash.Add(new CatalogueStashGrant { ItemId = "item.gamma-kit", Quantity = 4 });
            return d;
        }

        [Test]
        public void Writer_WritesTheSharedGoldenVectorByteForByte()
        {
            var bytes = CanonicalCatalogueWriter.ToBytes(GoldenDocument());
            Assert.That(bytes.SequenceEqual(SharedVectors.GoldenBytes()), Is.True, Encoding.UTF8.GetString(bytes));
        }

        [Test]
        public void Writer_VersionIsTheSharedVectorsVersion()
        {
            Assert.That(SharedVectors.GoldenVersion(), Does.Match("^sha256:[0-9a-f]{64}$"));
            Assert.That(CanonicalCatalogueWriter.VersionOf(SharedVectors.GoldenBytes()), Is.EqualTo(SharedVectors.GoldenVersion()));
        }

        [Test]
        public void Writer_IsTheSameUnderOtherCultures()
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                foreach (var name in new[] { "tr-TR", "ar-SA", "fr-FR" })
                {
                    CultureInfo.CurrentCulture = new CultureInfo(name);
                    Assert.That(CanonicalCatalogueWriter.ToBytes(GoldenDocument()).SequenceEqual(SharedVectors.GoldenBytes()), Is.True, name);
                }
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        [Test]
        public void Writer_RefusesAStringThatIsNotAnId()
        {
            var document = GoldenDocument();
            document.Items[0].Id = "Item Alpha";
            Assert.That(() => CanonicalCatalogueWriter.Write(document), Throws.ArgumentException);
        }

        [Test]
        public void Writer_UsesUtf8WithoutABomAndOnlyLineFeeds()
        {
            var bytes = CanonicalCatalogueWriter.ToBytes(GoldenDocument());
            Assert.That(bytes[0], Is.EqualTo((byte)'{'));
            Assert.That(Array.IndexOf(bytes, (byte)'\r'), Is.EqualTo(-1));
            Assert.That(bytes[bytes.Length - 2], Is.EqualTo((byte)'}'));
            Assert.That(bytes[bytes.Length - 1], Is.EqualTo((byte)'\n'));
        }
    }

    /// <summary>The golden vector both sides check byte for byte (Backend/contracts/catalogue).</summary>
    internal static class SharedVectors
    {
        public static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;
        static string Folder => Path.Combine(ProjectRoot, "Backend", "contracts", "catalogue");
        public static byte[] GoldenBytes() => File.ReadAllBytes(Path.Combine(Folder, "golden-catalogue.json"));
        public static string GoldenVersion() => File.ReadAllText(Path.Combine(Folder, "golden-catalogue.version")).Trim();
    }
}
