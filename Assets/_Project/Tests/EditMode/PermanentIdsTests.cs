using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Blackglass.BackendTools;
using NUnit.Framework;
using UnityEditor;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    /// <summary>
    /// The approved permanent role and ability ids, and the rule Editor builders use to give a known asset its id: fill a
    /// blank id, keep a matching one, never overwrite a different one, never create a duplicate.
    /// </summary>
    public class PermanentIdsTests
    {
        readonly List<Object> created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var asset in created)
            {
                if (asset != null)
                    Object.DestroyImmediate(asset);
            }
            created.Clear();
        }

        OperativeRole Role(string name, string id)
        {
            var role = OperativeRole.Create(name, "", default);
            role.name = name;
            role.SetId(id);
            created.Add(role);
            return role;
        }

        [Test]
        public void ApprovedIds_AreTheSevenOwnerApprovedIds()
        {
            Assert.That(PermanentIds.Roles.Values, Is.EquivalentTo(new[] { "role.assault", "role.recon", "role.support" }));
            Assert.That(PermanentIds.Abilities.Values, Is.EquivalentTo(new[] { "ability.aimed-shot", "ability.blast", "ability.mend", "ability.recon-scan" }));
        }

        [Test]
        public void ApprovedIds_MatchTheProjectAssets()
        {
            foreach (var pair in PermanentIds.Roles)
            {
                var role = AssetDatabase.LoadAssetAtPath<OperativeRole>(pair.Key);
                Assert.That(role != null, Is.True, "missing " + pair.Key);
                Assert.That(role.Id, Is.EqualTo(pair.Value), pair.Key);
            }
            foreach (var pair in PermanentIds.Abilities)
            {
                var ability = AssetDatabase.LoadAssetAtPath<AbilityDefinition>(pair.Key);
                Assert.That(ability != null, Is.True, "missing " + pair.Key);
                Assert.That(ability.Id, Is.EqualTo(pair.Value), pair.Key);
            }
        }

        [Test]
        public void Ensure_FillsABlankId()
        {
            var role = Role("Fresh", "");
            Assert.That(PermanentIds.Ensure(role, "role.assault", new OperativeRole[0]), Is.EqualTo(PermanentIds.Outcome.Assigned));
            Assert.That(role.Id, Is.EqualTo("role.assault"));
        }

        [Test]
        public void Ensure_KeepsTheMatchingId()
        {
            var role = Role("Assault", "role.assault");
            Assert.That(PermanentIds.Ensure(role, "role.assault", new[] { role }), Is.EqualTo(PermanentIds.Outcome.AlreadySet));
            Assert.That(role.Id, Is.EqualTo("role.assault"));
        }

        [Test]
        public void Ensure_NeverOverwritesADifferentId()
        {
            var role = Role("Assault", "role.custom");
            var error = Assert.Throws<InvalidOperationException>(() => PermanentIds.Ensure(role, "role.assault", new OperativeRole[0]));
            Assert.That(error.Message, Does.Contain("role.custom").And.Contain("role.assault"));
            Assert.That(role.Id, Is.EqualTo("role.custom"), "the existing id must be left unchanged");
        }

        [Test]
        public void Ensure_RefusesAnIdAnotherAssetAlreadyUses()
        {
            var holder = Role("Holder", "role.assault");
            var fresh = Role("Fresh", "");
            var error = Assert.Throws<InvalidOperationException>(() => PermanentIds.Ensure(fresh, "role.assault", new[] { holder, fresh }));
            Assert.That(error.Message, Does.Contain("Holder"));
            Assert.That(fresh.Id, Is.Empty, "nothing is assigned when the id is taken");
        }

        [Test]
        public void Ensure_WorksForAbilities()
        {
            var ability = AbilityDefinition.Create("Scan", AbilityTargetMode.Ground, 18f, false, AbilityCoverRule.Ignored, 25f, AbilityEffect.Reveal, 0, 12f, 6f);
            created.Add(ability);
            Assert.That(PermanentIds.Ensure(ability, "ability.recon-scan", new AbilityDefinition[0]), Is.EqualTo(PermanentIds.Outcome.Assigned));
            Assert.That(ability.Id, Is.EqualTo("ability.recon-scan"));
        }

        [Test]
        public void ProjectAssets_ShareNoRoleOrAbilityId()
        {
            var ids = PermanentIds.AllInProject<AbilityDefinition>().Select(a => (a.Id, a.name))
                .Concat(PermanentIds.AllInProject<OperativeRole>().Select(r => (r.Id, r.name)))
                .Where(x => !string.IsNullOrEmpty(x.Id));
            var duplicates = ids.GroupBy(x => x.Id).Where(g => g.Count() > 1).Select(g => $"{g.Key}: {string.Join(", ", g.Select(x => x.name))}").ToList();
            Assert.That(duplicates, Is.Empty);
        }

        // CopySerialized/Instantiate copy every field, the permanent id included; the preview clone must not carry Blast's.
        [Test]
        public void PreviewBlastBuilder_ClearsTheCopiedId()
        {
            var source = File.ReadAllText(Path.Combine(SharedVectors.ProjectRoot, "Assets", "_Project", "Editor", "Presentation", "CombatPresentationSceneBuilder.cs"));
            Assert.That(source, Does.Contain("serialized.FindProperty(\"id\").stringValue = \"\";"));
        }

        // The builders live in Assembly-CSharp-Editor, which a test assembly can't reference, and running them would write
        // project assets; so check that each asset they create goes through Ensure with its approved id.
        [TestCase("OperativeDataBuilder.cs", "PermanentIds.Ensure(", "PermanentIds.Roles")]
        [TestCase("IntelligenceSceneBuilder.cs", "PermanentIds.Ensure(", "PermanentIds.Abilities")]
        public void Builders_GiveTheAssetsTheyCreateTheirApprovedIds(string file, string call, string table)
        {
            var source = File.ReadAllText(Path.Combine(SharedVectors.ProjectRoot, "Assets", "_Project", "Editor", file));
            Assert.That(source, Does.Contain(call).And.Contain(table), file);
        }
    }
}
