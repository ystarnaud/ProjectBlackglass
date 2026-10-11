using System;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class PresentationVisibilityTests
    {
        static readonly Func<Vector3, PointVisibility> AllVisible = _ => PointVisibility.Visible;
        static readonly Vector3 Muzzle = new Vector3(0f, 1.5f, 0f);

        // Everything with x > 5 is hidden, everything with x < -50 is outside the regions.
        static PointVisibility FogRight(Vector3 p) =>
            p.x > 5f ? PointVisibility.Hidden : p.x < -50f ? PointVisibility.Outside : PointVisibility.Visible;

        [Test]
        public void ObservedShooter_AllVisible_AllowsEverything()
        {
            var p = PresentationVisibility.EvaluateShot(true, true, AllVisible, Muzzle, true, new Vector3(0f, 1f, 10f), true, new Vector3(0f, 1f, 10f), false);
            Assert.That(p.Muzzle && p.Tracer && p.Impact, Is.True);
        }

        [Test]
        public void HiddenShooter_ShowsNoMuzzleAndNoTracer()
        {
            var p = PresentationVisibility.EvaluateShot(false, true, AllVisible, Muzzle, true, new Vector3(0f, 1f, 10f), true, new Vector3(0f, 1f, 10f), false);
            Assert.That(p.Muzzle, Is.False);
            Assert.That(p.Tracer, Is.False);
        }

        [Test]
        public void ImpactOnAVisibleFriend_FromAHiddenShooter_IsStillShown()
        {
            var p = PresentationVisibility.EvaluateShot(false, true, AllVisible, Muzzle, false, default, true, Vector3.zero, true);
            Assert.That(p.Impact, Is.True, "local damage feedback does not depend on seeing the shooter");
        }

        [Test]
        public void TracerIntoHiddenGround_IsSuppressed_ButMuzzleStays()
        {
            var p = PresentationVisibility.EvaluateShot(true, true, FogRight, Muzzle, true, new Vector3(20f, 1f, 0f), true, new Vector3(20f, 1f, 0f), false);
            Assert.That(p.Muzzle, Is.True);
            Assert.That(p.Tracer, Is.False);
            Assert.That(p.Impact, Is.False, "the impact point is hidden ground");
        }

        [Test]
        public void TracerCrossingAHiddenStretchInTheMiddle_IsSuppressed()
        {
            static PointVisibility Gap(Vector3 p) => p.x > 4f && p.x < 8f ? PointVisibility.Hidden : PointVisibility.Visible;
            Assert.That(PresentationVisibility.AllowsPath(Gap, new Vector3(0f, 1f, 0f), new Vector3(12f, 1f, 0f)), Is.False);
        }

        [Test]
        public void OutsideSamples_AreNeutral()
        {
            Assert.That(PresentationVisibility.AllowsPath(FogRight, new Vector3(-60f, 1f, 0f), new Vector3(0f, 1f, 0f)), Is.True);
        }

        [Test]
        public void CharacterImpact_UsesTheVictim_NotThePoint()
        {
            var hiddenVictim = PresentationVisibility.EvaluateShot(true, false, AllVisible, Muzzle, true, Vector3.forward, true, Vector3.forward, true);
            Assert.That(hiddenVictim.Impact, Is.False);
            var shownVictim = PresentationVisibility.EvaluateShot(true, true, FogRight, Muzzle, true, Vector3.forward, true, new Vector3(30f, 0f, 0f), true);
            Assert.That(shownVictim.Impact, Is.True);
        }

        [Test]
        public void WorldImpact_RequiresAVisiblePoint_NotJustANonHiddenOne()
        {
            var p = PresentationVisibility.EvaluateShot(true, true, FogRight, Muzzle, false, default, true, new Vector3(-60f, 0f, 0f), false);
            Assert.That(p.Impact, Is.False, "a point outside every region is not shown");
        }

        [Test]
        public void ZeroLengthPath_DoesNotThrow_AndTestsTheSinglePoint()
        {
            Assert.That(PresentationVisibility.AllowsPath(FogRight, new Vector3(9f, 0f, 0f), new Vector3(9f, 0f, 0f)), Is.False);
            Assert.That(PresentationVisibility.AllowsPath(AllVisible, Vector3.zero, Vector3.zero), Is.True);
        }
    }
}
