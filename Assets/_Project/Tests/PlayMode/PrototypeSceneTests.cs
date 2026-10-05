#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class PrototypeSceneTests : InputTestFixture
    {
        static readonly string[] FriendlyNames = { "FriendlyUnit_1", "FriendlyUnit_2", "FriendlyUnit_3" };
        static readonly string[] HostileNames = { "HostileUnit_1", "HostileUnit_2", "HostileUnit_3" };

        CommandableUnit unit;
        Health firstHostile;
        TacticalPause pause;
        UnitSelection selection;
        Encounter encounter;

        // Loaded from each test rather than [UnitySetUp]: the scene must load after InputTestFixture has isolated the
        // input system, otherwise the scene's actions (shared InputActionAsset) leak into later fixtures.
        IEnumerator LoadScene()
        {
            yield return SceneManager.LoadSceneAsync("Prototype", LoadSceneMode.Single);
            yield return null;
            unit = GameObject.Find(FriendlyNames[0]).GetComponent<CommandableUnit>();
            firstHostile = FindHostileHealth(HostileNames[0]);
            pause = Object.FindFirstObjectByType<TacticalPause>();
            selection = Object.FindFirstObjectByType<UnitSelection>();
            encounter = Object.FindFirstObjectByType<Encounter>();
        }

        internal static CommandableUnit[] FindSquad() =>
            FriendlyNames.Select(n => GameObject.Find(n).GetComponent<CommandableUnit>()).ToArray();

        internal static EnemyAI[] FindHostiles() =>
            HostileNames.Select(n => GameObject.Find(n).GetComponent<EnemyAI>()).ToArray();

        internal static Health FindHostileHealth(string name) => GameObject.Find(name).GetComponent<Health>();

        public override void TearDown()
        {
            Time.timeScale = 1f;
            DestroySceneObjects();
            base.TearDown();
        }

        // The scene's components share the project's InputActionAsset; destroy them while the input isolation is
        // still active so their actions are disabled and do not leak into later tests.
        internal static void DestroySceneObjects()
        {
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
                Object.DestroyImmediate(root);
        }

        [UnityTest]
        public IEnumerator Scene_ContainsWiredSquadAndHostiles_AndRunsWithoutErrors()
        {
            yield return LoadScene();
            var friendlies = Object.FindObjectsByType<SelectableUnit>(FindObjectsSortMode.None);
            Assert.That(friendlies.Select(f => f.name), Is.EquivalentTo(FriendlyNames));
            Assert.That(selection, Is.Not.Null, "UnitSelection missing");
            Assert.That(selection.Roster, Is.EquivalentTo(friendlies));
            Assert.That(selection.Selected, Is.Empty, "Nothing should be selected at start");
            foreach (var friendly in friendlies)
            {
                Assert.That(friendly.GetComponent<SelectionIndicator>(), Is.Not.Null, friendly.name);
                Assert.That(friendly.GetComponent<CommandQueueView>(), Is.Not.Null, friendly.name);
                Assert.That(friendly.GetComponent<Health>(), Is.Not.Null, $"{friendly.name} has no Health");
                Assert.That(friendly.GetComponent<Health>().Max, Is.EqualTo(100), friendly.name);
                Assert.That(friendly.GetComponent<HitFlash>(), Is.Not.Null, friendly.name);
                Assert.That(friendly.GetComponent<DeathMarker>(), Is.Not.Null, friendly.name);
                Assert.That(friendly.GetComponent<AutoRetaliate>(), Is.Not.Null, friendly.name);
                Assert.That(friendly.GetComponent<AttackLineView>(), Is.Not.Null, friendly.name);
                Assert.That(friendly.GetComponent<CompanionAI>(), Is.Not.Null, $"{friendly.name} has no CompanionAI");
                Assert.That(friendly.GetComponent<CompanionAI>().IsWired, Is.True, $"{friendly.name}'s CompanionAI is not wired");
                var expectedRole = friendly.name == "FriendlyUnit_3" ? CombatRole.Ranged : CombatRole.Melee;
                Assert.That(friendly.GetComponent<UnitAttacker>().Role, Is.EqualTo(expectedRole), friendly.name);
                Assert.That(friendly.GetComponent<UnitAttacker>().Range, Is.EqualTo(expectedRole == CombatRole.Ranged ? 8f : 2f), friendly.name);
                Assert.That(friendly.GetComponent<UnitAttacker>().Damage, Is.EqualTo(expectedRole == CombatRole.Ranged ? 15 : 25), friendly.name);
                Assert.That(friendly.GetComponent<EnemyAI>(), Is.Null, $"{friendly.name} must not have enemy AI");
                Assert.That(friendly.GetComponentsInChildren<Collider>(true), Has.Length.EqualTo(1),
                    $"{friendly.name}: only the capsule may have a collider, so debug visuals never block clicks");
            }

            Assert.That(GameObject.Find("TrainingDummy"), Is.Null, "The training dummy should be gone");
            var hostiles = Object.FindObjectsByType<EnemyAI>(FindObjectsSortMode.None);
            Assert.That(hostiles.Select(h => h.name), Is.EquivalentTo(HostileNames));
            foreach (var hostile in hostiles)
            {
                Assert.That(hostile.GetComponent<Health>().Max, Is.EqualTo(60), hostile.name);
                var ranged = hostile.name == "HostileUnit_3";
                Assert.That(hostile.GetComponent<UnitAttacker>().Role, Is.EqualTo(ranged ? CombatRole.Ranged : CombatRole.Melee), hostile.name);
                Assert.That(hostile.GetComponent<UnitAttacker>().Range, Is.EqualTo(ranged ? 8f : 2f), hostile.name);
                Assert.That(hostile.GetComponent<UnitAttacker>().Damage, Is.EqualTo(ranged ? 8 : 10), hostile.name);
                Assert.That(hostile.GetComponent<UnitAttacker>().Cooldown, Is.EqualTo(ranged ? 1.5f : 1.2f).Within(0.001f), hostile.name);
                Assert.That(hostile.GetComponent<CompanionAI>(), Is.Null, $"{hostile.name} must not have companion AI");
                Assert.That(hostile.GetComponent<HitFlash>(), Is.Not.Null, hostile.name);
                Assert.That(hostile.GetComponent<DeathMarker>(), Is.Not.Null, hostile.name);
                Assert.That(hostile.GetComponent<AutoRetaliate>(), Is.Not.Null, hostile.name);
                Assert.That(hostile.GetComponent<AttackLineView>(), Is.Not.Null, hostile.name);
                Assert.That(hostile.GetComponent<SelectableUnit>(), Is.Null, $"{hostile.name} must not be selectable");
                Assert.That(hostile.GetComponentsInChildren<Collider>(true), Has.Length.EqualTo(1), hostile.name);
                Assert.That(hostile.State, Is.EqualTo(EnemyState.Idle), $"{hostile.name} should start idle, out of range of the squad");
            }

            Assert.That(encounter, Is.Not.Null, "Encounter missing");
            Assert.That(encounter.Friendlies.Select(h => h.name), Is.EquivalentTo(FriendlyNames));
            Assert.That(encounter.Hostiles.Select(h => h.name), Is.EquivalentTo(HostileNames));
            Assert.That(encounter.Outcome, Is.EqualTo(EncounterOutcome.Ongoing));
            Assert.That(GameObject.Find("Obstacle_E"), Is.Not.Null);
            Assert.That(GameObject.Find("Obstacle_F"), Is.Not.Null);
            foreach (var obstacle in new[] { "Pillar_G", "Pillar_H", "Barrier_I", "Crate_J", "Crate_K" })
                Assert.That(GameObject.Find(obstacle), Is.Not.Null, $"{obstacle} missing");

            Assert.That(pause, Is.Not.Null, "TacticalPause missing");
            Assert.That(Object.FindFirstObjectByType<PlayerCommandInput>(), Is.Not.Null);
            Assert.That(Object.FindFirstObjectByType<TacticalCameraController>(), Is.Not.Null);
            var hud = Object.FindFirstObjectByType<PrototypeHud>();
            Assert.That(hud, Is.Not.Null);
            Assert.That(hud.IsEncounterWired, Is.True, "PrototypeHud.encounter is not wired in the scene");
            var active = Object.FindFirstObjectByType<ActiveCharacter>();
            Assert.That(active, Is.Not.Null, "ActiveCharacter missing");
            Assert.That(active.Unit, Is.Not.Null, "ActiveCharacter has no unit");
            Assert.That(active.Unit.name, Is.EqualTo(FriendlyNames[0]), "The game starts controlling FriendlyUnit_1");
            Assert.That(active.HasUnit, Is.True);
            Assert.That(active.IsTakeoverOn, Is.False, "The game starts in free mode");
            var marker = Object.FindFirstObjectByType<ActiveCharacterMarker>();
            Assert.That(marker, Is.Not.Null, "ActiveMarker missing");
            Assert.That(marker.GetComponentsInChildren<Collider>(true), Is.Empty, "The marker must not block clicks");
            Assert.That(Object.FindFirstObjectByType<DirectControlInput>(), Is.Not.Null, "DirectControlInput missing");
            Assert.That(Camera.main, Is.Not.Null);

            // Any error or exception logged during this second fails the test automatically.
            yield return new WaitForSeconds(1f);
            Assert.That(hostiles.All(h => h.State == EnemyState.Idle), Is.True, "Hostiles must stay idle while the squad is far away");
            var companions = FindSquad().Select(u => u.GetComponent<CompanionAI>()).ToArray();
            Assert.That(companions[0].State, Is.EqualTo(CompanionState.Controlled), "FriendlyUnit_1 is controlled");
            Assert.That(companions[1].State, Is.EqualTo(CompanionState.Idle), "FriendlyUnit_2 starts within follow distance");
            Assert.That(companions[2].State, Is.EqualTo(CompanionState.Idle), "FriendlyUnit_3 starts within follow distance");
        }

        [UnityTest]
        public IEnumerator ControlledCharacterMoves_CompanionsFollow()
        {
            yield return LoadScene();
            var squad = FindSquad();
            Assert.That(unit.Issue(new MoveCommand(new Vector3(-14f, 0f, 0f))), Is.True);

            // Waiting for the leader to arrive too: at load the companions already stand within 6 m of it.
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null && squad.Skip(1).All(u => u.CurrentCommand == null
                && TestWorld.HorizontalDistance(u.transform.position, unit.transform.position) < 6f), 25f);

            Assert.That(unit.CurrentCommand, Is.Null, "The controlled character did not arrive");
            foreach (var companion in squad.Skip(1))
                Assert.That(TestWorld.HorizontalDistance(companion.transform.position, unit.transform.position), Is.LessThan(6f),
                    $"{companion.name} did not follow the controlled character");
        }

        [UnityTest]
        public IEnumerator Tab_ThenMove_TheOldLeaderFollowsTheNewOne()
        {
            yield return LoadScene();
            var squad = FindSquad();
            var active = Object.FindFirstObjectByType<ActiveCharacter>();
            Assert.That(active.Cycle(1), Is.True);
            Assert.That(active.Unit, Is.SameAs(squad[1]));
            Assert.That(squad[1].Issue(new MoveCommand(new Vector3(-14f, 0f, 0f))), Is.True);

            yield return TestWorld.WaitUntil(() => squad[1].CurrentCommand == null && squad[0].CurrentCommand == null
                && TestWorld.HorizontalDistance(squad[0].transform.position, squad[1].transform.position) < 6f, 25f);

            Assert.That(squad[1].CurrentCommand, Is.Null, "FriendlyUnit_2 did not arrive");
            Assert.That(TestWorld.HorizontalDistance(squad[0].transform.position, squad[1].transform.position), Is.LessThan(6f),
                "FriendlyUnit_1 must follow once FriendlyUnit_2 is controlled");
            Assert.That(squad[0].GetComponent<CompanionAI>().State, Is.Not.EqualTo(CompanionState.Controlled));
        }

        [UnityTest]
        public IEnumerator Move_AroundTheCentralWall_Arrives()
        {
            yield return LoadScene();
            var destination = new Vector3(6f, 0f, 6f);
            Assert.That(unit.Issue(new MoveCommand(destination)), Is.True);

            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 15f);

            Assert.That(unit.CurrentCommand, Is.Null, "Move did not finish in time");
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, destination), Is.LessThan(0.3f));
        }

        [UnityTest]
        public IEnumerator Squad_AttacksEveryHostile_AndWins()
        {
            yield return LoadScene();
            var squad = FindSquad();
            var hostiles = HostileNames.Select(FindHostileHealth).ToArray();
            Assert.That(GroupOrders.Issue(squad, new AttackCommand(hostiles[0]), IssueMode.Replace), Is.EqualTo(3));
            Assert.That(GroupOrders.Issue(squad, new AttackCommand(hostiles[1]), IssueMode.Append), Is.EqualTo(3));
            Assert.That(GroupOrders.Issue(squad, new AttackCommand(hostiles[2]), IssueMode.Append), Is.EqualTo(3));

            yield return TestWorld.WaitUntil(() => encounter.Outcome != EncounterOutcome.Ongoing, 60f);

            Assert.That(encounter.Outcome, Is.EqualTo(EncounterOutcome.Victory), "The squad should beat three hostiles");
            Assert.That(hostiles.All(h => !h.IsAlive && !h.gameObject.activeSelf), Is.True);
            Assert.That(encounter.LivingFriendlies, Is.GreaterThan(0));
            foreach (var name in HostileNames)
                Assert.That(GameObject.Find($"{name} (dead)"), Is.Not.Null, $"{name} left no corpse marker");
            Assert.That(PrototypeHud.DescribeOutcome(encounter.Outcome), Does.StartWith("VICTORY"));
        }

        [UnityTest]
        public IEnumerator FriendlyWalksIntoView_HostilesEngageIt()
        {
            yield return LoadScene();
            var hostiles = FindHostiles();
            var health = unit.GetComponent<Health>();
            // The gap between Obstacle_E and Obstacle_F, in plain sight of HostileUnit_1.
            Assert.That(unit.Issue(new MoveCommand(new Vector3(10f, 0f, 6f))), Is.True);

            yield return TestWorld.WaitUntil(() => hostiles.Any(h => h.Target == health) || health.Current < health.Max, 25f);

            Assert.That(hostiles.Any(h => h.Target == health) || health.Current < health.Max, Is.True,
                "No hostile engaged the friendly that walked into view");
        }

        [UnityTest]
        public IEnumerator Pause_HoldsTheUnitUntilResume()
        {
            yield return LoadScene();
            var start = unit.transform.position;
            pause.Pause();
            unit.Issue(new MoveCommand(new Vector3(-10f, 0f, 0f)));

            yield return new WaitForSecondsRealtime(1f);
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, start), Is.LessThan(0.01f));

            pause.Resume();
            yield return TestWorld.WaitUntil(() => TestWorld.HorizontalDistance(unit.transform.position, start) > 1f, 5f);
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, start), Is.GreaterThan(1f));
        }

        [UnityTest]
        public IEnumerator RepeatedPauseDuringTheFight_InScene_LogsNoErrors()
        {
            yield return LoadScene();
            var squad = FindSquad();
            GroupOrders.Issue(squad, new AttackCommand(firstHostile), IssueMode.Replace);
            yield return TestWorld.WaitUntil(() => firstHostile.Current < firstHostile.Max, 20f);
            Assert.That(firstHostile.Current, Is.LessThan(firstHostile.Max), "Precondition: the fight started");

            for (var i = 0; i < 6; i++)
            {
                pause.Pause();
                yield return new WaitForSecondsRealtime(0.15f);
                pause.Resume();
                yield return new WaitForSeconds(0.2f);
            }

            Assert.That(pause.IsPaused, Is.False);
            Assert.That(encounter.Outcome, Is.Not.EqualTo(EncounterOutcome.Defeat));
        }

        [UnityTest]
        public IEnumerator GroupMove_TheSquadArrivesAtDistinctPoints()
        {
            yield return LoadScene();
            var squad = FindSquad();
            var destination = new Vector3(-6f, 0f, 0f);
            Assert.That(GroupOrders.Issue(squad, new MoveCommand(destination), IssueMode.Replace), Is.EqualTo(3));

            yield return TestWorld.WaitUntil(() => squad.All(u => u.CurrentCommand == null), 15f);

            Assert.That(squad.All(u => u.CurrentCommand == null), Is.True, "Group move did not finish in time");
            for (var i = 0; i < squad.Length; i++)
            for (var j = i + 1; j < squad.Length; j++)
                Assert.That(TestWorld.HorizontalDistance(squad[i].transform.position, squad[j].transform.position), Is.GreaterThan(1f));
        }
    }

    public class PrototypeSceneInputTests : InputTestFixture
    {
        public override void TearDown()
        {
            Time.timeScale = 1f;
            PrototypeSceneTests.DestroySceneObjects();
            base.TearDown();
        }

        static IEnumerator LoadScene()
        {
            yield return SceneManager.LoadSceneAsync("Prototype", LoadSceneMode.Single);
            yield return null;
        }

        static Health FirstHostile() => PrototypeSceneTests.FindHostileHealth("HostileUnit_1");

        IEnumerator LeftClickAt(Mouse mouse, Vector2 screenPoint)
        {
            Set(mouse.position, screenPoint);
            yield return null;
            Press(mouse.leftButton);
            yield return null;
            Release(mouse.leftButton);
            yield return null;
        }

        IEnumerator Tap(KeyControl key)
        {
            Press(key);
            yield return null;
            Release(key);
            yield return null;
        }

        [UnityTest]
        public IEnumerator PausedClickCompanionThenHostile_InScene_OnlyThatUnitAttacks()
        {
            var mouse = InputSystem.AddDevice<Mouse>();
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var squad = PrototypeSceneTests.FindSquad();
            var hostile = FirstHostile();

            yield return Tap(keyboard.spaceKey);
            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(squad[1].transform.position));
            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(hostile.transform.position));

            Assert.That(squad[1].CurrentCommand, Is.TypeOf<AttackCommand>());
            Assert.That(((AttackCommand)squad[1].CurrentCommand).Target, Is.SameAs(hostile));
            Assert.That(squad[0].CurrentCommand, Is.Null);
            Assert.That(squad[2].CurrentCommand, Is.Null);
        }

        // Drags a selection box around every squad member.
        IEnumerator BoxSelect(Mouse mouse, CommandableUnit[] squad)
        {
            var screenPoints = squad.Select(u => (Vector2)Camera.main.WorldToScreenPoint(u.transform.position)).ToArray();
            var margin = new Vector2(25f, 25f);
            var from = screenPoints.Aggregate(Vector2.Min) - margin;
            var to = screenPoints.Aggregate(Vector2.Max) + margin;

            Set(mouse.position, from);
            yield return null;
            Press(mouse.leftButton);
            yield return null;
            Set(mouse.position, to);
            yield return null;
            Release(mouse.leftButton);
            yield return null;
        }

        [UnityTest]
        public IEnumerator PausedBoxSelectSquadThenClickGround_InScene_AllThreeGetMoves()
        {
            var mouse = InputSystem.AddDevice<Mouse>();
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var squad = PrototypeSceneTests.FindSquad();

            yield return Tap(keyboard.spaceKey);
            yield return BoxSelect(mouse, squad);
            Assert.That(Object.FindFirstObjectByType<UnitSelection>().Selected, Has.Count.EqualTo(3));

            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(new Vector3(-6f, 0f, 0f)));

            foreach (var member in squad)
                Assert.That(member.CurrentCommand, Is.TypeOf<MoveCommand>(), member.name);
        }

        [UnityTest]
        public IEnumerator RealTimeBoxSelectSquadThenClickGround_InScene_AllThreeGetMoves()
        {
            var mouse = InputSystem.AddDevice<Mouse>();
            InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var squad = PrototypeSceneTests.FindSquad();

            yield return BoxSelect(mouse, squad);
            Assert.That(Object.FindFirstObjectByType<UnitSelection>().Selected, Has.Count.EqualTo(3));

            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(new Vector3(-6f, 0f, 0f)));

            foreach (var member in squad)
                Assert.That(member.CurrentCommand, Is.TypeOf<MoveCommand>(), $"{member.name}: real-time group orders must reach every selected unit");
        }

        [UnityTest]
        public IEnumerator StopAndClearSelection_InScene_UseTheSceneBindings()
        {
            var mouse = InputSystem.AddDevice<Mouse>();
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var squad = PrototypeSceneTests.FindSquad();
            var selection = Object.FindFirstObjectByType<UnitSelection>();
            yield return Tap(keyboard.spaceKey);   // paused: clicks order the selection

            yield return BoxSelect(mouse, squad);
            Assert.That(selection.Selected, Has.Count.EqualTo(3));
            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(new Vector3(-6f, 0f, 0f)));
            foreach (var member in squad)
                Assert.That(member.CurrentCommand, Is.TypeOf<MoveCommand>(), member.name);

            Press(keyboard.leftShiftKey);
            yield return null;
            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(new Vector3(-6f, 0f, -4f)));   // (-6, 0, 4) is hidden behind Barrier_I
            Release(keyboard.leftShiftKey);
            yield return null;
            foreach (var member in squad)
                Assert.That(member.PendingCommands, Has.Count.EqualTo(1), $"{member.name}: Shift-click did not queue an order");

            Press(keyboard.xKey);
            yield return null;
            Release(keyboard.xKey);
            yield return null;
            foreach (var member in squad)
            {
                Assert.That(member.CurrentCommand, Is.Null, $"{member.name}: X did not stop the unit");
                Assert.That(member.PendingCommands, Is.Empty, member.name);
            }

            Press(keyboard.escapeKey);
            yield return null;
            Release(keyboard.escapeKey);
            yield return null;
            Assert.That(selection.Selected, Is.Empty, "Esc did not clear the selection");
        }

        [UnityTest]
        public IEnumerator HoldingW_InScene_PansTheCameraRig()
        {
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var rig = Object.FindFirstObjectByType<TacticalCameraController>();
            var start = rig.transform.position;

            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.2f);
            Release(keyboard.wKey);
            yield return null;

            Assert.That(rig.transform.position.z, Is.GreaterThan(start.z + 0.5f));
        }

        [UnityTest]
        public IEnumerator RealTimeClickOnHostile_InScene_TheControlledCharacterAttacks()
        {
            var mouse = InputSystem.AddDevice<Mouse>();
            InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var squad = PrototypeSceneTests.FindSquad();
            var hostile = FirstHostile();

            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(hostile.transform.position));

            Assert.That(squad[0].CurrentCommand, Is.TypeOf<AttackCommand>(), "The controlled character did not attack");
            Assert.That(((AttackCommand)squad[0].CurrentCommand).Target, Is.SameAs(hostile));
            Assert.That(squad[1].CurrentCommand, Is.Null);
            Assert.That(squad[2].CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator VThenW_InScene_DrivesThePrimary_AndTheCameraFollows()
        {
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var primary = PrototypeSceneTests.FindSquad()[0];
            var rig = Object.FindFirstObjectByType<TacticalCameraController>();
            var start = primary.transform.position;

            yield return Tap(keyboard.vKey);
            Press(keyboard.wKey);
            yield return TestWorld.WaitUntil(() => TestWorld.HorizontalDistance(primary.transform.position, start) > 1.5f, 3f);
            Release(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.6f);

            Assert.That(TestWorld.HorizontalDistance(primary.transform.position, start), Is.GreaterThan(1.5f),
                "V then W did not drive the primary character");
            Assert.That(TestWorld.HorizontalDistance(rig.transform.position, primary.transform.position), Is.LessThan(0.5f),
                "The camera did not follow the primary character");
        }

        [UnityTest]
        public IEnumerator PausedSquadOrder_ThenTakeover_OnlyThePrimaryDropsItsOrders()
        {
            var mouse = InputSystem.AddDevice<Mouse>();
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var squad = PrototypeSceneTests.FindSquad();

            yield return Tap(keyboard.spaceKey);
            yield return BoxSelect(mouse, squad);
            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(new Vector3(-6f, 0f, 0f)));
            foreach (var member in squad)
                Assert.That(member.CurrentCommand, Is.TypeOf<MoveCommand>(), $"{member.name}: precondition");
            yield return Tap(keyboard.vKey);
            yield return Tap(keyboard.spaceKey);   // resume: the plan starts running

            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.3f);
            Release(keyboard.wKey);
            yield return null;

            Assert.That(squad[0].CurrentCommand, Is.Null, "Manual input must take the primary character back");
            Assert.That(squad[1].CurrentCommand, Is.TypeOf<MoveCommand>(), "Takeover cancelled a companion's order");
            Assert.That(squad[2].CurrentCommand, Is.TypeOf<MoveCommand>(), "Takeover cancelled a companion's order");
        }

        [UnityTest]
        public IEnumerator Tab_InScene_SwitchesToTheNextFriendly_AndItsClicksAttack()
        {
            var mouse = InputSystem.AddDevice<Mouse>();
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var squad = PrototypeSceneTests.FindSquad();
            var hostile = FirstHostile();
            var active = Object.FindFirstObjectByType<ActiveCharacter>();
            var selection = Object.FindFirstObjectByType<UnitSelection>();
            var marker = Object.FindFirstObjectByType<ActiveCharacterMarker>();

            yield return Tap(keyboard.tabKey);
            Assert.That(active.Unit, Is.SameAs(squad[1]), "Tab did not switch to FriendlyUnit_2");
            Assert.That(selection.Selected, Is.EqualTo(new[] { squad[1].GetComponent<SelectableUnit>() }));
            yield return new WaitForSecondsRealtime(1f);   // let the camera finish focusing before aiming the click

            Assert.That(TestWorld.HorizontalDistance(marker.transform.position, squad[1].transform.position), Is.LessThan(0.01f),
                "The marker did not move to the new active character");
            var hostileOnScreen = Camera.main.WorldToScreenPoint(hostile.transform.position);
            Assert.That(new Rect(0f, 0f, Screen.width, Screen.height).Contains(hostileOnScreen), Is.True,
                "Precondition: HostileUnit_1 is on screen after the camera focused FriendlyUnit_2");
            yield return LeftClickAt(mouse, hostileOnScreen);

            Assert.That(squad[1].CurrentCommand, Is.TypeOf<AttackCommand>(), "The attack must come from FriendlyUnit_2");
            Assert.That(((AttackCommand)squad[1].CurrentCommand).Target, Is.SameAs(hostile));
            Assert.That(squad[0].CurrentCommand, Is.Null);
            Assert.That(squad[2].CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator ShiftTab_InScene_SwitchesBackToTheLastFriendly()
        {
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var squad = PrototypeSceneTests.FindSquad();
            var active = Object.FindFirstObjectByType<ActiveCharacter>();

            Press(keyboard.leftShiftKey);
            yield return null;
            yield return Tap(keyboard.tabKey);
            Release(keyboard.leftShiftKey);
            yield return null;

            Assert.That(active.Unit, Is.SameAs(squad[2]), "Shift+Tab did not switch to FriendlyUnit_3");
        }
    }
}
#endif
