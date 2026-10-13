#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>The real intelligence model feeding per-connection visibility (spec section 3.2), separate from the fixture.</summary>
    public class NetSquadIntelPlayModeTests
    {
        const int Shooter = 2;

        IntelRig rig;
        Health shooter;
        UnitAttacker attacker;
        NetVisibility visibility;
        NetConnection a;
        NetConnection b;
        readonly List<string> logs = new List<string>();
        readonly List<NetKnowledgeChange> changes = new List<NetKnowledgeChange>();

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        void Build(IntelligenceSettings settings)
        {
            rig = new IntelRig(corridor: true);
            (shooter, attacker) = rig.AddShooter(IntelRig.InLineGround);
            rig.Begin(settings);
            var units = NetUnits.FromMission(new List<CommandableUnit> { rig.Friendly.GetComponent<CommandableUnit>() },
                new List<CommandableUnit> { shooter.GetComponent<CommandableUnit>() });
            logs.Clear();
            visibility = new NetVisibility(units, new NetSquadIntel(rig.Service, units, 1, logs.Add));
            a = new NetConnection("account-a", new[] { 1 });
            b = new NetConnection("account-b", new int[0]);
            visibility.Add(a);
            visibility.Add(b);
        }

        static IntelligenceSettings Fog() => IntelRig.Fog(s =>
        {
            s.observationRange = 8f;   // the shooter is 13 m away: only exposure can reveal it
            s.exposureSeconds = 1f;
        });

        void Tick()
        {
            changes.Clear();
            visibility.Update(changes);
        }

        [UnityTest]
        public IEnumerator TheRealModel_HidesTheHostileAtStart_FromEveryone()
        {
            Build(Fog());
            yield return null;
            Assert.That(NetSquadIntel.Problem(rig.Service), Is.Null);
            Tick();
            Assert.That(changes, Is.Empty);
            Assert.That(visibility.KnowledgeOf(a, Shooter), Is.EqualTo(NetKnowledge.Unknown));
            Assert.That(visibility.KnowledgeOf(b, Shooter), Is.EqualTo(NetKnowledge.Unknown));
        }

        [UnityTest]
        public IEnumerator AShot_ExposesTheShooter_ToTheSquadsOwnerOnly_ThenLeavesAFrozenRecord()
        {
            Build(Fog());
            yield return null;
            Tick();

            Assert.That(attacker.TryAttack(rig.FriendlyHealth), Is.True, "the shot is fired (decision 037: firing exposes)");
            Tick();
            Assert.That(visibility.KnowledgeOf(a, Shooter), Is.EqualTo(NetKnowledge.Observed), "exposure reaches the owner");
            Assert.That(visibility.KnowledgeOf(b, Shooter), Is.EqualTo(NetKnowledge.Unknown), "and nobody else");

            yield return new WaitForSeconds(1.4f);
            Tick();
            Assert.That(visibility.KnowledgeOf(a, Shooter), Is.EqualTo(NetKnowledge.LastKnown));
            Assert.That(visibility.TryLastKnown(a, Shooter, out var record), Is.True);
            Assert.That(Vector3.Distance(record, shooter.transform.position), Is.LessThan(0.01f));

            Assert.That(shooter.GetComponent<NavMeshAgent>().Warp(IntelRig.OffAxisGround), Is.True);
            yield return null;
            Tick();
            Assert.That(changes, Is.Empty, "hidden movement is not reported");
            Assert.That(visibility.TryLastKnown(a, Shooter, out var after), Is.True);
            Assert.That(after, Is.EqualTo(record), "the record does not follow the hidden hostile");
            Assert.That(visibility.KnowledgeOf(b, Shooter), Is.EqualTo(NetKnowledge.Unknown));
        }

        [UnityTest]
        public IEnumerator FogOff_FailsClosed_AndLogsOnce()
        {
            Build(new IntelligenceSettings { fogEnabled = false });
            yield return null;
            StringAssert.Contains("fog is off", NetSquadIntel.Problem(rig.Service));
            Tick();
            Tick();
            Assert.That(visibility.KnowledgeOf(a, Shooter), Is.EqualTo(NetKnowledge.Unknown), "an open model must not reveal everything");
            Assert.That(logs, Has.Count.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator DisablingTheServiceMidMission_HidesEverything_AndLogsOnce()
        {
            Build(Fog());
            yield return null;
            Assert.That(attacker.TryAttack(rig.FriendlyHealth), Is.True);
            Tick();
            Assert.That(visibility.KnowledgeOf(a, Shooter), Is.EqualTo(NetKnowledge.Observed));

            rig.Service.enabled = false;
            Tick();
            Assert.That(visibility.KnowledgeOf(a, Shooter), Is.EqualTo(NetKnowledge.LastKnown), "no live state without a model");
            Tick();
            Assert.That(changes, Is.Empty);
            Assert.That(logs, Has.Count.EqualTo(1));
            StringAssert.Contains("disabled", logs[0]);
        }
    }
}
#endif
