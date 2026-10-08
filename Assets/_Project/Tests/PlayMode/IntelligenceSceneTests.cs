#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>The ProceduralMission scene as shipped: Blind preset, everything wired, the first mission fogged.</summary>
    public class IntelligenceSceneTests : InputTestFixture
    {
        Keyboard keyboard;
        InputActionAsset actions;
        MissionDirector director;
        IntelligenceService service;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            actions = TestControls.Load();
        }

        public override void TearDown()
        {
            Time.timeScale = 1f;
            if (SceneManager.GetActiveScene().name == "ProceduralMission")
                PrototypeSceneTests.DestroySceneObjects();
            TestControls.Reset(actions);
            base.TearDown();
        }

        IEnumerator Load()
        {
            yield return SceneManager.LoadSceneAsync("ProceduralMission", LoadSceneMode.Single);
            director = Object.FindFirstObjectByType<MissionDirector>();
            service = Object.FindFirstObjectByType<IntelligenceService>();
            Assert.That(director, Is.Not.Null);
            Assert.That(service, Is.Not.Null, "run Blackglass/Intelligence/Wire ProceduralMission Scene");
            yield return TestWorld.WaitUntil(() => director.State == MissionState.Ready || director.State == MissionState.Failed, 20f);
            Assert.That(director.State, Is.EqualTo(MissionState.Ready), director.Report.Failure);
            yield return null;
        }

        static bool Wired(Object component, string property)
        {
            var found = new SerializedObject(component).FindProperty(property);
            return found != null && found.objectReferenceValue != null;
        }

        [UnityTest]
        public IEnumerator TheScene_ShipsTheBlindPreset_AndTheFirstMissionIsFogged()
        {
            yield return Load();
            Assert.That(director.Settings.intelligence.Describe(), Is.EqualTo(IntelligenceSettings.Blind().Describe()));
            Assert.That(service.IsFogActive, Is.True);
            Assert.That(service.StateOfRegion(director.Current.Layout.FriendlyRoom), Is.EqualTo(KnowledgeState.Observed));
            foreach (var hostile in director.Hostiles)
                Assert.That(service.CanTarget(hostile.GetComponent<Health>()), Is.False, hostile.name);
            Assert.That(director.Current.CameraTerminal, Is.Not.Null);
            Assert.That(director.Runtime.Objectives.Single(g => g.Type == ObjectiveType.ReachZone).IsKnown, Is.True);
            Assert.That(director.Runtime.Objectives.Single(g => g.Type == ObjectiveType.Interact).IsKnown, Is.False);
        }

        [UnityTest]
        public IEnumerator EveryConsumer_IsWiredToTheService()
        {
            yield return Load();
            Assert.That(Wired(director, "systems.intelligence"), Is.True);
            foreach (var (component, name) in new (Object, string)[]
            {
                (Object.FindFirstObjectByType<TacticalCursor>(), "TacticalCursor"),
                (Object.FindFirstObjectByType<AbilityTargeting>(), "AbilityTargeting"),
                (Object.FindFirstObjectByType<PlayerCommandInput>(), "PlayerCommandInput"),
                (Object.FindFirstObjectByType<PrototypeHud>(), "PrototypeHud"),
                (Object.FindFirstObjectByType<MissionHud>(), "MissionHud"),
                (Object.FindFirstObjectByType<MissionDebugView>(), "MissionDebugView"),
                (Object.FindFirstObjectByType<CoverView>(), "CoverView"),
                (Object.FindFirstObjectByType<FogPresenter>(), "FogPresenter"),
                (Object.FindFirstObjectByType<IntelMapView>(), "IntelMapView"),
                (Object.FindFirstObjectByType<IntelligenceDebugView>(), "IntelligenceDebugView"),
                (Object.FindFirstObjectByType<IntelligenceGizmoView>(), "IntelligenceGizmoView"),
                (Object.FindFirstObjectByType<IntelligenceDeveloperInput>(), "IntelligenceDeveloperInput"),
            })
            {
                Assert.That(component, Is.Not.Null, name + " is missing from the scene");
                Assert.That(Wired(component, "intelligence"), Is.True, name + " has no intelligence service");
            }
            Assert.That(Wired(Object.FindFirstObjectByType<FogPresenter>(), "fogMaterial"), Is.True);
            Assert.That(Wired(Object.FindFirstObjectByType<FogPresenter>(), "veilMaterial"), Is.True);
            Assert.That(Wired(service, "markerMaterial"), Is.True);
        }

        [UnityTest]
        public IEnumerator TheSpawnedUnits_AreWiredToo()
        {
            yield return Load();
            foreach (var hostile in director.Hostiles)
                Assert.That(hostile.GetComponent<HostilePresenter>(), Is.Not.Null, hostile.name);
            foreach (var friendly in director.Friendlies)
                Assert.That(friendly.GetComponent<CompanionAI>(), Is.Not.Null);
            var kestrel = director.Friendlies.Select(f => f.GetComponent<UnitAbilities>())
                .FirstOrDefault(a => a != null && Enumerable.Range(0, a.Count).Any(i => a.Definition(i).Effect == AbilityEffect.Reveal));
            Assert.That(kestrel, Is.Not.Null, "one squad member carries the Recon Scan");
        }

        [UnityTest]
        public IEnumerator Kestrel_CanUseTheScanOnTheScene_AndRevealsARoom()
        {
            yield return Load();
            var casterAbilities = director.Friendlies.Select(f => f.GetComponent<UnitAbilities>())
                .First(a => a != null && Enumerable.Range(0, a.Count).Any(i => a.Definition(i).Effect == AbilityEffect.Reveal));
            var scan = Enumerable.Range(0, casterAbilities.Count).Select(casterAbilities.Definition).First(d => d.Effect == AbilityEffect.Reveal);
            var layout = director.Current.Layout;
            var farRoom = Enumerable.Range(0, layout.Rooms.Count)
                .Where(r => service.StateOfRegion(r) == KnowledgeState.Unknown)
                .OrderBy(r => Vector3.Distance(layout.RectCenter(layout.Rooms[r].Rect), casterAbilities.transform.position))
                .First();
            var centre = layout.RectCenter(layout.Rooms[farRoom].Rect);
            var inRange = casterAbilities.transform.position + (centre - casterAbilities.transform.position).normalized * Mathf.Min(scan.Range - 1f,
                Vector3.Distance(centre, casterAbilities.transform.position));
            Assert.That(casterAbilities.TryUse(AbilityCommand.AtGround(scan, inRange)), Is.True, casterAbilities.LastFailure.ToString());
            Assert.That(Enumerable.Range(0, service.Map.Count).Count(r => service.StateOfRegion(r) != KnowledgeState.Unknown),
                Is.GreaterThan(1), "the scan found more than the start room");
        }

        [UnityTest]
        public IEnumerator F12_ShowsTheTruth_ButTheHostilesStayUntargetable()
        {
            yield return Load();
            var hostile = director.Hostiles[0].GetComponent<Health>();
            Press(keyboard.f12Key);
            yield return null;
            Release(keyboard.f12Key);
            yield return null;
            Assert.That(service.TruthView, Is.True);
            Assert.That(service.IsUnitShown(hostile), Is.True);
            Assert.That(service.CanTarget(hostile), Is.False, "truth view is display only");
        }

        [UnityTest]
        public IEnumerator Regenerating_KeepsTheFogOn_AndStartsFromTheBriefing()
        {
            yield return Load();
            Assert.That(director.RegenerateSame(), Is.True);
            yield return TestWorld.WaitUntil(() => director.State == MissionState.Ready, 20f);
            Assert.That(service.IsFogActive, Is.True);
            foreach (var hostile in director.Hostiles)
                Assert.That(service.CanTarget(hostile.GetComponent<Health>()), Is.False);
        }
    }
}
#endif
