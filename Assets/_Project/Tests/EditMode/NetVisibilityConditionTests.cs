using FishNet.Observing;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class NetVisibilityConditionTests
    {
        [Test]
        public void WithoutAnAvatar_TheConditionHidesEverything()
        {
            var condition = ScriptableObject.CreateInstance<NetVisibilityCondition>();
            try
            {
                Assert.That(condition.ConditionMet(null, false, out var notProcessed), Is.False, "fails closed");
                Assert.That(notProcessed, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(condition);
            }
        }

        [Test]
        public void TheCondition_IsCheckedOnlyOnRebuilds()
        {
            var condition = ScriptableObject.CreateInstance<NetVisibilityCondition>();
            try
            {
                Assert.That(condition.GetConditionType(), Is.EqualTo(ObserverConditionType.Normal));
            }
            finally
            {
                Object.DestroyImmediate(condition);
            }
        }
    }
}
