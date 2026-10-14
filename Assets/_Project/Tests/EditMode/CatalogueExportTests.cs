using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;

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
    }
}
