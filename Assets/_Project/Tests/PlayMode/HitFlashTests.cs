using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class HitFlashTests
    {
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        GameObject target;

        [TearDown]
        public void TearDown()
        {
            if (target != null)
                Object.Destroy(target);
        }

        [UnityTest]
        public IEnumerator Damage_TintsRendererWhite_ThenClears()
        {
            target = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            var health = target.AddComponent<Health>();
            target.AddComponent<HitFlash>();
            var renderer = target.GetComponent<Renderer>();
            yield return null;

            health.TakeDamage(10);

            Assert.That(renderer.HasPropertyBlock(), Is.True);
            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block);
            Assert.That(block.GetColor(BaseColorId), Is.EqualTo(Color.white));

            yield return new WaitForSeconds(0.3f);

            Assert.That(renderer.HasPropertyBlock(), Is.False);
        }
    }
}
