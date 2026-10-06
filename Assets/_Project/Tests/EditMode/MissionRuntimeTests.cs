using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class MissionRuntimeTests
    {
        internal sealed class FakeObjective : MissionObjective
        {
            public FakeObjective(string id, bool required) : base(id, ObjectiveType.Interact, id, required) { }

            public bool CompleteNow;
            public bool FailNow;
            public bool Ended;

            public override string Describe() => Id;

            protected override void OnEvaluate()
            {
                if (FailNow)
                    Fail();
                else if (CompleteNow)
                    Complete();
            }

            protected override void OnMissionEnded() => Ended = true;
        }

        readonly List<GameObject> hosts = new List<GameObject>();
        Health[] squad;
        FakeObjective first;
        FakeObjective second;
        FakeObjective extraction;

        [SetUp]
        public void SetUp()
        {
            squad = new[] { NewUnit("A"), NewUnit("B") };
            first = new FakeObjective("first", true);
            second = new FakeObjective("second", true);
            extraction = new FakeObjective("extraction", false);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var host in hosts)
            {
                if (host != null)
                    Object.DestroyImmediate(host);
            }
            hosts.Clear();
        }

        Health NewUnit(string name)
        {
            var host = new GameObject(name);
            hosts.Add(host);
            return host.AddComponent<Health>();
        }

        static void Kill(Health unit) => unit.TakeDamage(unit.Max);

        MissionRuntime Make(params MissionObjective[] goals)
        {
            var runtime = new MissionRuntime(goals, extraction, squad);
            runtime.Start();
            return runtime;
        }

        [Test]
        public void Start_ActivatesTheGoals_ButNotTheExtraction()
        {
            var runtime = Make(first, second);

            Assert.That(runtime.Phase, Is.EqualTo(MissionPhase.Active));
            Assert.That(first.State, Is.EqualTo(ObjectiveState.Active));
            Assert.That(second.State, Is.EqualTo(ObjectiveState.Active));
            Assert.That(extraction.State, Is.EqualTo(ObjectiveState.Inactive));
            Assert.That(runtime.Objectives, Is.EqualTo(new MissionObjective[] { first, second, extraction }));
        }

        [Test]
        public void BeforeStart_ThePhaseIsInactive_AndTickDoesNothing()
        {
            var runtime = new MissionRuntime(new[] { first }, extraction, squad);
            first.CompleteNow = true;

            runtime.Tick();

            Assert.That(runtime.Phase, Is.EqualTo(MissionPhase.Inactive));
            Assert.That(first.State, Is.EqualTo(ObjectiveState.Inactive));
        }

        [Test]
        public void ExtractionStaysLocked_UntilEveryRequiredObjectiveIsComplete()
        {
            var runtime = Make(first, second);
            first.CompleteNow = true;
            extraction.CompleteNow = true;

            runtime.Tick();

            Assert.That(first.State, Is.EqualTo(ObjectiveState.Completed));
            Assert.That(runtime.Phase, Is.EqualTo(MissionPhase.Active));
            Assert.That(extraction.State, Is.EqualTo(ObjectiveState.Inactive), "an unlocked-too-early extraction must stay inactive");
        }

        [Test]
        public void ExtractionOpens_WhenTheLastRequiredObjectiveCompletes()
        {
            var runtime = Make(first, second);
            first.CompleteNow = true;
            second.CompleteNow = true;

            runtime.Tick();

            Assert.That(runtime.Phase, Is.EqualTo(MissionPhase.ExtractionOpen));
            Assert.That(extraction.State, Is.EqualTo(ObjectiveState.Active));
        }

        [Test]
        public void TheMissionSucceeds_WhenTheExtractionCompletes_AndEveryObjectiveIsToldTheMissionEnded()
        {
            var runtime = Make(first);
            first.CompleteNow = true;
            runtime.Tick();
            extraction.CompleteNow = true;

            runtime.Tick();

            Assert.That(runtime.Phase, Is.EqualTo(MissionPhase.Success));
            Assert.That(runtime.IsOver, Is.True);
            Assert.That(first.Ended && extraction.Ended, Is.True);
        }

        [Test]
        public void AnExtractionConditionAlreadySatisfied_OpensAndSucceedsInTheSameTick()
        {
            var runtime = Make(first);
            first.CompleteNow = true;
            extraction.CompleteNow = true;

            runtime.Tick();

            Assert.That(runtime.Phase, Is.EqualTo(MissionPhase.Success));
        }

        [Test]
        public void TheWholeSquadDead_IsFailure_AndTheExtractionNeverOpens()
        {
            var runtime = Make(first);
            Kill(squad[0]);
            runtime.Tick();
            Assert.That(runtime.Phase, Is.EqualTo(MissionPhase.Active), "one survivor keeps the mission going");

            Kill(squad[1]);
            first.CompleteNow = true;
            runtime.Tick();

            Assert.That(runtime.Phase, Is.EqualTo(MissionPhase.Failure));
            Assert.That(extraction.State, Is.EqualTo(ObjectiveState.Inactive));
        }

        [Test]
        public void FailureWinsOverSuccess_WhenBothHappenInOneTick()
        {
            var runtime = Make(first);
            first.CompleteNow = true;
            runtime.Tick();
            extraction.CompleteNow = true;
            Kill(squad[0]);
            Kill(squad[1]);

            runtime.Tick();

            Assert.That(runtime.Phase, Is.EqualTo(MissionPhase.Failure));
        }

        [Test]
        public void ARequiredObjectiveFailing_IsFailure()
        {
            var runtime = Make(first, second);
            second.FailNow = true;

            runtime.Tick();

            Assert.That(second.State, Is.EqualTo(ObjectiveState.Failed));
            Assert.That(runtime.Phase, Is.EqualTo(MissionPhase.Failure));
        }

        [Test]
        public void AnOptionalGoal_NeverBlocksTheExtraction()
        {
            var optional = new FakeObjective("optional", false);
            var runtime = Make(first, optional);
            first.CompleteNow = true;

            runtime.Tick();

            Assert.That(runtime.Phase, Is.EqualTo(MissionPhase.ExtractionOpen));
            Assert.That(optional.State, Is.EqualTo(ObjectiveState.Active));
        }

        [Test]
        public void AnUnknownObjective_StillCountsAsRequired()
        {
            var runtime = Make(first);
            first.SetKnown(false);
            runtime.Tick();
            Assert.That(runtime.Phase, Is.EqualTo(MissionPhase.Active));

            first.CompleteNow = true;
            runtime.Tick();
            Assert.That(runtime.Phase, Is.EqualTo(MissionPhase.ExtractionOpen));
        }

        [Test]
        public void SuccessAndFailureAreTerminal()
        {
            var runtime = Make(first);
            first.CompleteNow = true;
            runtime.Tick();
            extraction.CompleteNow = true;
            runtime.Tick();
            Kill(squad[0]);
            Kill(squad[1]);

            runtime.Tick();

            Assert.That(runtime.Phase, Is.EqualTo(MissionPhase.Success), "later deaths change nothing");
        }

        [Test]
        public void AnEmptySquad_NeverFails()
        {
            var runtime = new MissionRuntime(new[] { first }, extraction, Array.Empty<Health>());
            runtime.Start();

            runtime.Tick();

            Assert.That(runtime.Phase, Is.EqualTo(MissionPhase.Active));
        }

        [Test]
        public void Detach_StopsTickingAndStopsForwardingChanges()
        {
            var runtime = Make(first);
            var changes = 0;
            runtime.ObjectiveChanged += _ => changes++;
            runtime.Detach();
            first.CompleteNow = true;

            runtime.Tick();
            first.Evaluate();

            Assert.That(runtime.IsDetached, Is.True);
            Assert.That(runtime.Phase, Is.EqualTo(MissionPhase.Active));
            Assert.That(changes, Is.Zero, "a detached runtime forwards nothing");
        }

        [Test]
        public void PhaseChanged_ReportsEachTransitionInOrder()
        {
            var phases = new List<MissionPhase>();
            var runtime = new MissionRuntime(new[] { first }, extraction, squad);
            runtime.PhaseChanged += phases.Add;
            runtime.Start();
            first.CompleteNow = true;
            runtime.Tick();
            extraction.CompleteNow = true;
            runtime.Tick();

            Assert.That(phases, Is.EqualTo(new[] { MissionPhase.Active, MissionPhase.ExtractionOpen, MissionPhase.Success }));
        }

        [Test]
        public void ObjectiveChanged_ForwardsObjectiveStateChanges()
        {
            var seen = new List<MissionObjective>();
            var runtime = Make(first);
            runtime.ObjectiveChanged += seen.Add;
            first.CompleteNow = true;

            runtime.Tick();

            Assert.That(seen, Does.Contain(first));
            Assert.That(seen, Does.Contain(extraction), "the extraction activating is a change too");
        }

        [Test]
        public void TheConstructor_RejectsABadExtraction_OrSquad()
        {
            Assert.Throws<ArgumentNullException>(() => new MissionRuntime(new[] { first }, null, squad));
            Assert.Throws<ArgumentException>(() => new MissionRuntime(new[] { first }, new FakeObjective("x", true), squad));
            Assert.Throws<ArgumentNullException>(() => new MissionRuntime(new[] { first }, extraction, null));
            Assert.Throws<ArgumentNullException>(() => new MissionRuntime(null, extraction, squad));
        }

        [Test]
        public void ANullOrDestroyedSquadMember_CountsAsDead()
        {
            var runtime = Make(first);
            Object.DestroyImmediate(squad[0].gameObject);
            runtime.Tick();
            Assert.That(runtime.LivingSquad, Is.EqualTo(1));

            Kill(squad[1]);
            runtime.Tick();
            Assert.That(runtime.Phase, Is.EqualTo(MissionPhase.Failure));
        }
    }
}
