#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class PointVisibilityPlayModeTests
    {
        [Test]
        public void WithoutAService_EverythingIsVisibleAndObserved()
        {
            var unit = new GameObject("U").AddComponent<Health>();
            try
            {
                Assert.That(Knowledge.PointVisibilityOf(null, Vector3.zero), Is.EqualTo(PointVisibility.Visible));
                Assert.That(Knowledge.IsObserved(null, unit), Is.True);
            }
            finally { Object.DestroyImmediate(unit.gameObject); }
        }

        [Test]
        public void ServiceWithNoMission_AnswersVisible()
        {
            var service = new GameObject("S").AddComponent<IntelligenceService>();
            try { Assert.That(service.VisibilityOfPoint(new Vector3(100f, 0f, 100f)), Is.EqualTo(PointVisibility.Visible)); }
            finally { Object.DestroyImmediate(service.gameObject); }
        }
    }
}
#endif
