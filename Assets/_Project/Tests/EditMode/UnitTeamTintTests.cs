using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class UnitTeamTintTests
    {
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        GameObject unit;
        SkinnedMeshRenderer body;
        MeshRenderer weapon;
        UnitTeamTint tint;

        [SetUp]
        public void SetUp()
        {
            unit = new GameObject("Unit");
            var visual = new GameObject("Visual");
            visual.transform.SetParent(unit.transform, false);
            body = visual.AddComponent<SkinnedMeshRenderer>();
            var gun = new GameObject("Rifle");
            gun.transform.SetParent(visual.transform, false);
            gun.AddComponent<MeshFilter>();
            weapon = gun.AddComponent<MeshRenderer>();
            tint = unit.AddComponent<UnitTeamTint>();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(unit);

        static Color BlockColor(Renderer r)
        {
            var block = new MaterialPropertyBlock();
            r.GetPropertyBlock(block);
            return block.GetColor(BaseColorId);
        }

        [Test]
        public void SetTeamColor_tints_the_skinned_body()
        {
            var red = new Color(1f, 0.5f, 0.5f);
            tint.SetTeamColor(red);
            Assert.That(tint.TeamColor, Is.EqualTo(red));
            Assert.That(BlockColor(body), Is.EqualTo(red));
        }

        [Test]
        public void The_weapon_is_not_tinted()
        {
            tint.SetTeamColor(Color.red);
            Assert.That(weapon.HasPropertyBlock(), Is.False);
        }

        [Test]
        public void A_new_colour_replaces_the_old_one()
        {
            tint.SetTeamColor(Color.red);
            tint.SetTeamColor(Color.blue);
            Assert.That(BlockColor(body), Is.EqualTo(Color.blue));
        }

        [Test]
        public void Two_units_can_share_one_model_with_different_colours()
        {
            var other = Object.Instantiate(unit);
            try
            {
                tint.SetTeamColor(Color.red);
                other.GetComponent<UnitTeamTint>().SetTeamColor(Color.blue);
                Assert.That(BlockColor(body), Is.EqualTo(Color.red));
                Assert.That(BlockColor(other.GetComponentInChildren<SkinnedMeshRenderer>()), Is.EqualTo(Color.blue));
            }
            finally { Object.DestroyImmediate(other); }
        }
    }
}
