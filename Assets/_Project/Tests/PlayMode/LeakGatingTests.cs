#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>The display channels: every one of them must stay silent about what the player has not observed.</summary>
    public class LeakGatingTests
    {
        IntelRig rig;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
            Time.timeScale = 1f;
        }

        [UnityTest]
        public IEnumerator UnitLabels_AreDrawnForFriendliesAndObservedHostiles_NeverForUnobservedOnes()
        {
            rig = new IntelRig(corridor: true);
            var shown = rig.AddHostile(IntelRig.InLineGround);
            var hidden = rig.AddHostile(IntelRig.OffAxisGround);
            rig.Begin(IntelRig.Fog());
            yield return null;
            Assert.That(PrototypeHud.ShowsUnitLabel(rig.Service, shown, hostile: true), Is.True);
            Assert.That(PrototypeHud.ShowsUnitLabel(rig.Service, hidden, hostile: true), Is.False);
            Assert.That(PrototypeHud.ShowsUnitLabel(rig.Service, rig.FriendlyHealth, hostile: false), Is.True);

            rig.Service.TruthView = true;
            Assert.That(PrototypeHud.ShowsUnitLabel(rig.Service, hidden, hostile: true), Is.True, "truth view labels everything");
        }

        [UnityTest]
        public IEnumerator AShotAtAnUnshownTarget_DrawsNoAttackLine_AShotAtAShownOneDoes()
        {
            rig = new IntelRig(corridor: true);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog(s => s.observationRange = 8f));   // 13 m: out of sight, but in the friendly's weapon range
            var attacker = rig.Friendly.GetComponent<UnitAttacker>();
            attacker.Initialize(30f, 10, 0.1f, CombatRole.Ranged);
            var lineObject = new GameObject("Line");
            lineObject.transform.SetParent(rig.Friendly.transform, false);
            var line = lineObject.AddComponent<LineRenderer>();
            var view = rig.Friendly.gameObject.AddComponent<AttackLineView>();
            view.Initialize(line);
            line.enabled = false;   // the view's OnEnable ran before the line was handed over, so a fresh LineRenderer is still enabled
            view.SetIntelligence(rig.Service);
            yield return null;
            Assert.That(rig.Service.CanTarget(hostile), Is.False, "Precondition");

            Assert.That(attacker.TryAttack(hostile), Is.True, "Precondition: the friendly can shoot what it cannot see");
            Assert.That(view.IsShowing, Is.False, "the line would show where the hidden hostile stands");

            rig.Service.Scan(hostile.transform.position, 3f, 5f);
            yield return new WaitForSeconds(0.2f);
            Assert.That(attacker.TryAttack(hostile), Is.True);
            Assert.That(view.IsShowing, Is.True, "now it is observed: the line shows as before");
        }

        [UnityTest]
        public IEnumerator AnOrderLineToAnUnshownTarget_IsNotDrawn()
        {
            rig = new IntelRig(corridor: true);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog(s => s.observationRange = 8f));
            rig.Friendly.gameObject.AddComponent<LineRenderer>();
            var view = rig.Friendly.gameObject.AddComponent<CommandQueueView>();
            view.SetIntelligence(rig.Service);
            Assert.That(rig.Friendly.Unit.Issue(new AttackCommand(hostile)), Is.True);
            yield return null;
            yield return null;
            Assert.That(view.LinePointCount, Is.EqualTo(0), "no line to an unobserved hostile's position");

            rig.Service.Scan(hostile.transform.position, 3f, 5f);
            yield return null;
            Assert.That(view.LinePointCount, Is.EqualTo(2), "a line once it is observed");
        }

        // Two cover points in room A (world x -6 and -4, z 1) and one in room B; the rig's wall closes the gap.
        CoverRegistry MakeCover(out CoverLocation a1, out CoverLocation a2, out CoverLocation b)
        {
            CoverLocation At(string name, Vector3 ground)
            {
                var wall = rig.World.CreateObstacle(ground + new Vector3(0f, 0.45f, 1f), new Vector3(1f, 0.9f, 0.4f));
                return rig.World.CreateCoverPoint(ground, Vector3.forward, wall.GetComponent<Collider>());
            }
            a1 = At("A1", new Vector3(-6f, 0f, -3f));
            a2 = At("A2", new Vector3(-4f, 0f, -3f));
            b = At("B1", new Vector3(6f, 0f, -3f));
            return rig.World.CreateRegistry(a1, a2, b);
        }

        CoverView MakeCoverView(CoverRegistry registry, TacticalPause pause)
        {
            var host = rig.World.Track(new GameObject("CoverView"));
            host.SetActive(false);
            var view = host.AddComponent<CoverView>();
            view.Initialize(registry, pause, null);
            view.SetIntelligence(rig.Service);
            host.SetActive(true);
            return view;
        }

        [UnityTest]
        public IEnumerator WhilePaused_CoverMarkersAppearOnlyInKnownRegions()
        {
            rig = new IntelRig(corridor: false);
            rig.Begin(IntelRig.Fog());
            var registry = MakeCover(out _, out _, out _);
            var pause = rig.World.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            var view = MakeCoverView(registry, pause);
            pause.Pause();
            yield return null;
            yield return null;
            Assert.That(view.VisibleMarkerCount, Is.EqualTo(2), "the two points in the known room; the one in the unknown room is not drawn");

            rig.Service.Model.RevealRegion(1);
            yield return null;
            Assert.That(view.VisibleMarkerCount, Is.EqualTo(3));

            rig.Service.Clear();
            rig.Begin(IntelRig.Fog());
            rig.Service.TruthView = true;
            yield return null;
            Assert.That(view.VisibleMarkerCount, Is.EqualTo(3), "truth view draws all");
        }

        [UnityTest]
        public IEnumerator ACoverPointHeldByAnUnshownHostile_IsNotDrawn_OneHeldByAFriendlyIs()
        {
            rig = new IntelRig(corridor: false);
            var (shooter, _) = rig.AddShooter(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog());
            var registry = MakeCover(out var a1, out var a2, out _);
            var hostileCover = shooter.GetComponent<UnitCover>();
            hostileCover.Initialize(registry);
            Assert.That(hostileCover.TryReserve(a1), Is.True);
            var friendlyCover = rig.Friendly.GetComponent<UnitCover>();
            friendlyCover.Initialize(registry);
            Assert.That(friendlyCover.TryReserve(a2), Is.True);
            var pause = rig.World.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            var view = MakeCoverView(registry, pause);   // not paused: only claimed points show
            yield return null;
            yield return null;
            Assert.That(view.VisibleMarkerCount, Is.EqualTo(1), "the friendly's point only; the hostile's claim would show where it hides");
        }
    }
}
#endif
