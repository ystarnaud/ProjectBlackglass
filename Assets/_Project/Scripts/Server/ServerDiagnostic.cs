using System;
using UnityEngine;
using UnityEngine.AI;

namespace Blackglass
{
    /// <summary>
    /// Checkpoint A's bounded navigation and combat check on a ready mission (decision 045). Prepare picks the first friendly
    /// (the active character) and the living hostile with the shortest complete NavMesh path, then removes chance from the
    /// run on the spawned instances only: hostiles' EnemyAI and AutoRetaliate and the other friendlies' CompanionAI and
    /// AutoRetaliate are disabled, and the mover's hit roll is forced to land (cover cannot turn the shot into a miss). Move
    /// issues a MoveCommand to a point 6 m along that path and needs the order finished, the unit within 0.75 m and at
    /// least 3 m travelled. Attack issues an AttackCommand and needs the target's health to drop and the mover's hit count to
    /// rise. Every step has a real-time deadline inside the overall timeout, so a paused or stalled simulation fails instead
    /// of hanging. Nothing here is a game rule: both orders go through CommandableUnit.Issue.
    /// </summary>
    internal sealed class ServerDiagnostic
    {
        public const string Prefix = "[Diagnostic] ";
        public const float MoveDistance = 6f;
        public const float ArrivalTolerance = 0.75f;
        public const float MinimumTravel = 3f;
        public const double MoveDeadlineSeconds = 30d;
        public const double AttackDeadlineSeconds = 45d;

        enum Step
        {
            Prepare,
            Move,
            Attack,
            Done,
        }

        readonly MissionDirector director;
        readonly double timeoutSeconds;
        readonly Action<string> log;
        readonly NavMeshPath path = new NavMeshPath();
        Step step = Step.Prepare;
        double overallDeadline;
        double stepStarted;
        double moveSeconds;
        UnitAttacker attacker;
        Vector3 start;
        Vector3 destination;
        int healthBefore;
        int hitsBefore;
        int shotsBefore;

        public ServerDiagnostic(MissionDirector director, double timeoutSeconds, Action<string> log)
        {
            this.director = director;
            this.timeoutSeconds = timeoutSeconds;
            this.log = log ?? (_ => { });
        }

        public bool IsFinished { get; private set; }
        public bool Passed { get; private set; }
        /// <summary>The final line: BLACKGLASS DIAGNOSTIC PASS ... or BLACKGLASS DIAGNOSTIC FAIL step=... reason=...</summary>
        public string Summary { get; private set; }
        public CommandableUnit Mover { get; private set; }
        public Health Target { get; private set; }

        /// <summary>Prepare and issue the move. Call once, in the frame the mission became ready.</summary>
        public void Begin(double now)
        {
            overallDeadline = now + timeoutSeconds;
            if (director == null || director.State != MissionState.Ready)
            {
                Fail("prepare", "the mission is not ready");
                return;
            }
            if (director.Friendlies.Count == 0 || director.Hostiles.Count == 0)
            {
                Fail("prepare", "the mission has no friendly or no hostile");
                return;
            }
            Mover = director.Friendlies[0];
            attacker = Mover.GetComponent<UnitAttacker>();
            var mover = Mover.GetComponent<UnitMover>();
            if (!TryChooseTarget(mover, out var length))
            {
                Fail("prepare", $"unreachable: no living hostile has a complete NavMesh path from {Mover.name}");
                return;
            }
            Freeze();
            attacker.HitRoll = () => 0f;
            log(Prefix + FormattableString.Invariant(
                $"prepare mover={Mover.name} target={Target.name} pathM={length:0.0} targetInCover={attacker.IsTargetInCover(Target)} simulationRunning={SimulationTime.IsRunning}"));

            var along = PointAlong(path, Mathf.Min(MoveDistance, length * 0.5f));
            if (!mover.TrySnap(along, out destination))
            {
                Fail("move", "the point along the path is not on the NavMesh");
                return;
            }
            start = Mover.transform.position;
            if (!Mover.Issue(new MoveCommand(destination)))
            {
                Fail("move", "CommandableUnit.Issue refused the move");
                return;
            }
            step = Step.Move;
            stepStarted = now;
            log(Prefix + FormattableString.Invariant($"move issued to ({destination.x:0.0}, {destination.z:0.0}), {Flat(start, destination):0.0} m away"));
        }

        public void Tick(double now)
        {
            if (IsFinished)
                return;
            switch (step)
            {
                case Step.Move:
                    TickMove(now);
                    break;
                case Step.Attack:
                    TickAttack(now);
                    break;
            }
        }

        void TickMove(double now)
        {
            if (Mover == null || !Mover.IsAlive)
            {
                Fail("move", "the mover died or was destroyed");
                return;
            }
            var position = Mover.transform.position;
            var travelled = Flat(start, position);
            var remaining = Flat(position, destination);
            if (Mover.CurrentCommand == null)
            {
                if (remaining <= ArrivalTolerance && travelled >= MinimumTravel)
                {
                    moveSeconds = now - stepStarted;
                    log(Prefix + FormattableString.Invariant($"move PASS travelled={travelled:0.0}m remaining={remaining:0.00}m seconds={moveSeconds:0.0}"));
                    BeginAttack(now);
                    return;
                }
                Fail("move", FormattableString.Invariant($"the order ended short: travelled={travelled:0.0}m remaining={remaining:0.00}m"));
                return;
            }
            if (TimedOut(now, MoveDeadlineSeconds, out var which))
                Fail("move", $"timed out ({which}): " + FormattableString.Invariant(
                    $"travelled={travelled:0.0}m remaining={remaining:0.00}m simulationRunning={SimulationTime.IsRunning} command={Mover.CurrentCommand.GetType().Name}"));
        }

        void BeginAttack(double now)
        {
            healthBefore = Target.Current;
            hitsBefore = attacker.Hits;
            shotsBefore = attacker.ShotsFired;
            if (!Mover.Issue(new AttackCommand(Target)))
            {
                Fail("attack", "CommandableUnit.Issue refused the attack");
                return;
            }
            step = Step.Attack;
            stepStarted = now;
            log(Prefix + FormattableString.Invariant($"attack issued on {Target.name} health={healthBefore} distance={Flat(Mover.transform.position, Target.transform.position):0.0}m"));
        }

        void TickAttack(double now)
        {
            if (Target == null || Mover == null)
            {
                Fail("attack", "the mover or the target was destroyed");
                return;
            }
            if (Target.Current < healthBefore && attacker.Hits > hitsBefore)
            {
                var attackSeconds = now - stepStarted;
                log(Prefix + FormattableString.Invariant(
                    $"attack PASS health={healthBefore}->{Target.Current} hits={attacker.Hits - hitsBefore} shots={attacker.ShotsFired - shotsBefore} seconds={attackSeconds:0.0}"));
                Mover.Issue(new StopCommand());
                step = Step.Done;
                IsFinished = true;
                Passed = true;
                Summary = FormattableString.Invariant($"BLACKGLASS DIAGNOSTIC PASS moveS={moveSeconds:0.0} attackS={attackSeconds:0.0}");
                return;
            }
            if (TimedOut(now, AttackDeadlineSeconds, out var which))
                Fail("attack", $"timed out ({which}): " +
                    FormattableString.Invariant($"health={healthBefore}->{Target.Current} hits={attacker.Hits - hitsBefore} phase={Mover.AttackPhase} ") +
                    FormattableString.Invariant($"distance={Flat(Mover.transform.position, Target.transform.position):0.0}m canAttack={attacker.CanAttack(Target)} simulationRunning={SimulationTime.IsRunning}"));
        }

        bool TimedOut(double now, double stepDeadline, out string which)
        {
            if (now > overallDeadline)
            {
                which = FormattableString.Invariant($"overall timeout {timeoutSeconds:0} s");
                return true;
            }
            if (now - stepStarted > stepDeadline)
            {
                which = FormattableString.Invariant($"step deadline {stepDeadline:0} s");
                return true;
            }
            which = null;
            return false;
        }

        bool TryChooseTarget(UnitMover mover, out float bestLength)
        {
            bestLength = float.MaxValue;
            Health best = null;
            if (!mover.TrySnap(Mover.transform.position, out var from))
                return false;
            foreach (var hostile in director.Hostiles)
            {
                if (hostile == null || !hostile.IsAlive || !hostile.TryGetComponent<Health>(out var health))
                    continue;
                if (!mover.TrySnap(hostile.transform.position, out var to))
                    continue;
                if (!NavMesh.CalculatePath(from, to, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
                    continue;
                var length = Length(path);
                if (length < bestLength)
                {
                    bestLength = length;
                    best = health;
                }
            }
            if (best == null)
                return false;
            Target = best;
            // Leave `path` holding the chosen target's path for PointAlong.
            mover.TrySnap(Target.transform.position, out var chosen);
            NavMesh.CalculatePath(from, chosen, NavMesh.AllAreas, path);
            return true;
        }

        // Diagnostic-only: instance components, never assets; the mover keeps all of its own.
        void Freeze()
        {
            foreach (var hostile in director.Hostiles)
            {
                Disable<EnemyAI>(hostile);
                Disable<AutoRetaliate>(hostile);
            }
            foreach (var friendly in director.Friendlies)
            {
                if (friendly == Mover)
                    continue;
                Disable<CompanionAI>(friendly);
                Disable<AutoRetaliate>(friendly);
            }
        }

        static void Disable<T>(Component unit) where T : Behaviour
        {
            if (unit != null && unit.TryGetComponent<T>(out var component))
                component.enabled = false;
        }

        static float Length(NavMeshPath navPath)
        {
            var corners = navPath.corners;
            var length = 0f;
            for (var i = 1; i < corners.Length; i++)
                length += Vector3.Distance(corners[i - 1], corners[i]);
            return length;
        }

        static Vector3 PointAlong(NavMeshPath navPath, float distance)
        {
            var corners = navPath.corners;
            for (var i = 1; i < corners.Length; i++)
            {
                var segment = Vector3.Distance(corners[i - 1], corners[i]);
                if (distance <= segment)
                    return Vector3.Lerp(corners[i - 1], corners[i], segment > 0f ? distance / segment : 0f);
                distance -= segment;
            }
            return corners.Length > 0 ? corners[corners.Length - 1] : Vector3.zero;
        }

        static float Flat(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        void Fail(string failedStep, string reason)
        {
            step = Step.Done;
            IsFinished = true;
            Passed = false;
            Summary = $"BLACKGLASS DIAGNOSTIC FAIL step={failedStep} reason={reason}";
            log(Prefix + $"{failedStep} FAIL {reason}");
        }
    }
}
