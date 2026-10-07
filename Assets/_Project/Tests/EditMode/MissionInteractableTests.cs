using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class MissionInteractableTests
    {
        readonly List<GameObject> hosts = new List<GameObject>();

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

        MissionInteractable Terminal(float range = 1.8f, float duration = 2f)
        {
            var host = new GameObject("Terminal");
            hosts.Add(host);
            var terminal = host.AddComponent<MissionInteractable>();
            terminal.Initialize(range, duration);
            return terminal;
        }

        CommandableUnit Unit(string name = "Unit", Vector3 position = default)
        {
            var host = new GameObject(name);
            host.transform.position = position;
            hosts.Add(host);
            host.AddComponent<Health>();
            host.AddComponent<UnitInteractor>();
            return host.AddComponent<CommandableUnit>();
        }

        // ---- MissionInteractable

        [Test]
        public void ANewTerminal_IsUnavailable_UntilItsObjectiveOpensIt()
        {
            var terminal = Terminal();
            Assert.That(terminal.IsAvailable, Is.False);
            terminal.SetAvailable(true);
            Assert.That(terminal.IsAvailable, Is.True);
            Assert.That(terminal.IsCompleted, Is.False);
        }

        [Test]
        public void Advance_AccumulatesProgress_AndCompletesAtTheDuration()
        {
            var terminal = Terminal(duration: 2f);
            terminal.SetAvailable(true);
            var unit = Unit();

            Assert.That(terminal.TryBegin(unit), Is.True);
            Assert.That(terminal.Advance(unit, 0.5f), Is.True);
            Assert.That(terminal.Progress, Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(terminal.Fraction, Is.EqualTo(0.25f).Within(1e-4f));
            Assert.That(terminal.IsCompleted, Is.False);
            terminal.Advance(unit, 1.5f);

            Assert.That(terminal.IsCompleted, Is.True);
            Assert.That(terminal.IsAvailable, Is.False, "a completed terminal is used up");
            Assert.That(terminal.User == null, Is.True, "the claim is released on completion");
            Assert.That(terminal.TryBegin(unit), Is.False);
        }

        [Test]
        public void Release_ResetsProgress_AndFreesTheTerminalForAnotherUnit()
        {
            var terminal = Terminal();
            terminal.SetAvailable(true);
            var a = Unit("A");
            var b = Unit("B");
            terminal.TryBegin(a);
            terminal.Advance(a, 1f);

            terminal.Release(a);

            Assert.That(terminal.Progress, Is.Zero, "a cancelled interaction keeps no progress");
            Assert.That(terminal.TryBegin(b), Is.True);
        }

        [Test]
        public void AClaimedTerminal_RefusesAnotherUnit_UnlessTheHolderIsDead()
        {
            var terminal = Terminal();
            terminal.SetAvailable(true);
            var a = Unit("A");
            var b = Unit("B");
            Assert.That(terminal.TryBegin(a), Is.True);

            Assert.That(terminal.IsInUseByOther(b), Is.True);
            Assert.That(terminal.TryBegin(b), Is.False);
            Assert.That(terminal.TryBegin(a), Is.True, "the holder may begin again");

            var health = a.GetComponent<Health>();
            health.TakeDamage(health.Max);
            Assert.That(terminal.IsInUseByOther(b), Is.False, "a dead holder's claim is stale");
            Assert.That(terminal.TryBegin(b), Is.True);
        }

        [Test]
        public void TakingOverADeadHoldersClaim_StartsFromZeroProgress()
        {
            var terminal = Terminal();
            terminal.SetAvailable(true);
            var a = Unit("A");
            var b = Unit("B");
            terminal.TryBegin(a);
            terminal.Advance(a, 1f);
            Assert.That(terminal.Progress, Is.GreaterThan(0f));

            var health = a.GetComponent<Health>();
            health.TakeDamage(health.Max);

            Assert.That(terminal.TryBegin(b), Is.True);
            Assert.That(terminal.Progress, Is.Zero, "the dead holder's partial work is not inherited");
            Assert.That(terminal.User == b, Is.True);
        }

        [Test]
        public void TheHolderBeginningAgain_KeepsItsProgress()
        {
            var terminal = Terminal();
            terminal.SetAvailable(true);
            var a = Unit("A");
            terminal.TryBegin(a);
            terminal.Advance(a, 1f);

            Assert.That(terminal.TryBegin(a), Is.True);

            Assert.That(terminal.Progress, Is.EqualTo(1f).Within(1e-4f));
        }

        [Test]
        public void Advance_ByAUnitThatIsNotTheHolder_DoesNothing()
        {
            var terminal = Terminal();
            terminal.SetAvailable(true);
            var a = Unit("A");
            var b = Unit("B");
            terminal.TryBegin(a);

            Assert.That(terminal.Advance(b, 5f), Is.False);
            Assert.That(terminal.Progress, Is.Zero);
        }

        [Test]
        public void MakingTheTerminalUnavailableMidWork_StopsTheInteraction()
        {
            var terminal = Terminal();
            terminal.SetAvailable(true);
            var unit = Unit();
            terminal.TryBegin(unit);
            terminal.Advance(unit, 0.5f);

            terminal.SetAvailable(false);

            Assert.That(terminal.Advance(unit, 0.5f), Is.False);
            Assert.That(terminal.IsCompleted, Is.False);
        }

        [Test]
        public void AZeroDuration_CompletesOnTheFirstAdvance()
        {
            var terminal = Terminal(duration: 0f);
            terminal.SetAvailable(true);
            var unit = Unit();
            terminal.TryBegin(unit);

            terminal.Advance(unit, 0f);

            Assert.That(terminal.IsCompleted, Is.True);
            Assert.That(terminal.Fraction, Is.EqualTo(1f));
        }

        // ---- UnitInteractor

        [Test]
        public void Check_ReportsMissingUnavailableAndBusyTargets()
        {
            var interactor = Unit("A").GetComponent<UnitInteractor>();
            var other = Unit("B");
            var terminal = Terminal();

            Assert.That(interactor.Check(null), Is.EqualTo(InteractionFailure.NoTarget));
            Assert.That(interactor.Check(terminal), Is.EqualTo(InteractionFailure.NotAvailable));
            terminal.SetAvailable(true);
            Assert.That(interactor.Check(terminal), Is.EqualTo(InteractionFailure.None));
            terminal.TryBegin(other);
            Assert.That(interactor.Check(terminal), Is.EqualTo(InteractionFailure.InUse));
            Assert.That(interactor.Check(terminal, allowOtherUser: true), Is.EqualTo(InteractionFailure.None));
        }

        [Test]
        public void Check_ASafeNoTarget_ForADestroyedTerminal()
        {
            var interactor = Unit().GetComponent<UnitInteractor>();
            var terminal = Terminal();
            terminal.SetAvailable(true);
            Object.DestroyImmediate(terminal.gameObject);

            Assert.That(interactor.Check(terminal), Is.EqualTo(InteractionFailure.NoTarget));
            Assert.That(interactor.InRange(terminal, Vector3.zero), Is.False);
        }

        [Test]
        public void InRange_IsFlatDistanceWithinTheTerminalsRange()
        {
            var interactor = Unit().GetComponent<UnitInteractor>();
            var terminal = Terminal(range: 1.8f);
            terminal.transform.position = new Vector3(10f, 1.2f, 0f);

            Assert.That(interactor.InRange(terminal, new Vector3(8.5f, 0f, 0f)), Is.True);
            Assert.That(interactor.InRange(terminal, new Vector3(7.5f, 0f, 0f)), Is.False);
        }

        [Test]
        public void TryStart_Advance_ThenComplete_RunsTheInteraction()
        {
            var unit = Unit();
            var interactor = unit.GetComponent<UnitInteractor>();
            var terminal = Terminal(duration: 1f);
            terminal.SetAvailable(true);

            Assert.That(interactor.TryStart(terminal), Is.True);
            Assert.That(interactor.IsWorking, Is.True);
            Assert.That(interactor.Advance(0.4f), Is.EqualTo(InteractionStep.Working));
            Assert.That(interactor.Advance(0.7f), Is.EqualTo(InteractionStep.Completed));

            Assert.That(interactor.IsWorking, Is.False);
            Assert.That(terminal.IsCompleted, Is.True);
        }

        [Test]
        public void TryStart_FailsAndRecordsWhy_ForABusyTerminal()
        {
            var interactor = Unit("A").GetComponent<UnitInteractor>();
            var other = Unit("B");
            var terminal = Terminal();
            terminal.SetAvailable(true);
            terminal.TryBegin(other);

            Assert.That(interactor.TryStart(terminal), Is.False);
            Assert.That(interactor.LastFailure, Is.EqualTo(InteractionFailure.InUse));
            Assert.That(interactor.IsWorking, Is.False);
        }

        [Test]
        public void Advance_ReportsLost_WhenTheTerminalWasTakenAway_AndRelease_IsSafeToRepeat()
        {
            var interactor = Unit().GetComponent<UnitInteractor>();
            var terminal = Terminal();
            terminal.SetAvailable(true);
            interactor.TryStart(terminal);
            interactor.Advance(0.5f);
            terminal.SetAvailable(false);

            Assert.That(interactor.Advance(0.1f), Is.EqualTo(InteractionStep.Lost));
            Assert.That(terminal.User == null, Is.True, "a lost interaction leaves no ghost holder");
            Assert.That(terminal.Progress, Is.Zero);
            Assert.That(interactor.IsWorking, Is.False);
            Assert.DoesNotThrow(() => { interactor.Release(); interactor.Release(); });
        }

        [Test]
        public void Release_DropsTheClaim_AndResetsProgress()
        {
            var interactor = Unit().GetComponent<UnitInteractor>();
            var terminal = Terminal();
            terminal.SetAvailable(true);
            interactor.TryStart(terminal);
            interactor.Advance(0.5f);

            interactor.Release();

            Assert.That(terminal.Progress, Is.Zero);
            Assert.That(terminal.User == null, Is.True);
        }

        // ---- InteractCommand and InteractObjective

        [Test]
        public void InteractCommand_RequiresATarget()
        {
            Assert.Throws<ArgumentNullException>(() => new InteractCommand(null));
            var terminal = Terminal();
            Assert.That(new InteractCommand(terminal).Target, Is.EqualTo(terminal));
        }

        [Test]
        public void InteractObjective_OpensTheTerminalOnActivate_AndCompletesWhenItIsUsed()
        {
            var terminal = Terminal(duration: 0f);
            var objective = new InteractObjective("hack", "Access data terminal", terminal);
            Assert.That(terminal.IsAvailable, Is.False, "inactive objectives offer nothing");

            objective.Activate();
            Assert.That(terminal.IsAvailable, Is.True);
            Assert.That(objective.HasTarget, Is.True);
            Assert.That(objective.TargetPosition, Is.EqualTo(terminal.Position));

            var unit = Unit();
            terminal.TryBegin(unit);
            terminal.Advance(unit, 0f);
            objective.Evaluate();

            Assert.That(objective.State, Is.EqualTo(ObjectiveState.Completed));
            Assert.That(objective.Type, Is.EqualTo(ObjectiveType.Interact));
        }

        [Test]
        public void InteractObjective_EndMission_ClosesTheTerminal_AndADestroyedTerminalFailsIt()
        {
            var terminal = Terminal();
            var objective = new InteractObjective("hack", "Access data terminal", terminal);
            objective.Activate();
            objective.EndMission();
            Assert.That(terminal.IsAvailable, Is.False);

            var other = Terminal();
            var broken = new InteractObjective("hack2", "Access", other);
            broken.Activate();
            Object.DestroyImmediate(other.gameObject);
            broken.Evaluate();
            Assert.That(broken.State, Is.EqualTo(ObjectiveState.Failed));
            Assert.DoesNotThrow(() => broken.EndMission());
            Assert.DoesNotThrow(() => broken.Describe());
        }

        [Test]
        public void InteractObjective_DescribeShowsProgressWhileWorking()
        {
            var terminal = Terminal(duration: 2f);
            var objective = new InteractObjective("hack", "Access data terminal", terminal);
            objective.Activate();
            Assert.That(objective.Describe(), Is.EqualTo("Access data terminal"));

            var unit = Unit();
            terminal.TryBegin(unit);
            terminal.Advance(unit, 1f);

            Assert.That(objective.Describe(), Is.EqualTo("Access data terminal (50%)"));
        }
    }
}
