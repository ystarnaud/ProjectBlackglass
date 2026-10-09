#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Blackglass.Tests
{
    /// <summary>
    /// A shipped scene with its wired tactical HUD (Blackglass/HUD/Wire Scenes): loads ProceduralMission or Prototype,
    /// binds the HUD and the systems it reads, and reads back what the HUD shows. Load from the test body after the
    /// InputTestFixture has isolated the input system, and call TearDown from the fixture's TearDown.
    /// </summary>
    internal sealed class HudSceneRig
    {
        public const string MissionScene = "ProceduralMission";
        public const string PrototypeScene = "Prototype";
        public const int Seed = 12345;

        public MissionDirector Director;
        public IntelligenceService Service;
        public TacticalHud Hud;
        public PointerBlocker Blocker;
        public DeveloperOverlay Overlay;
        public ActiveCharacter Active;
        public UnitSelection Selection;
        public TacticalPause Pause;
        public Encounter Encounter;
        public PlayerCommandInput Input;
        public Camera ViewCamera;

        /// <summary>The ProceduralMission scene's first, drawn-seed mission, as the player gets it (Blind preset).</summary>
        public IEnumerator LoadMission()
        {
            yield return SceneManager.LoadSceneAsync(MissionScene, LoadSceneMode.Single);
            Director = Object.FindFirstObjectByType<MissionDirector>();
            Assert.That(Director, Is.Not.Null);
            yield return WaitForMission();
            Bind();
            yield return null;
        }

        /// <summary>The ProceduralMission scene regenerated on the known seed, with the given (else the scene's) intelligence.</summary>
        public IEnumerator LoadMission(int seed, IntelligenceSettings intelligence = null)
        {
            yield return LoadMission();
            if (intelligence != null)
                Director.Settings.intelligence = intelligence;
            Assert.That(Director.Generate(seed), Is.True, $"the first mission is still {Director.State}");
            yield return WaitForMission();
            Bind();
            yield return null;
            yield return null;
        }

        public IEnumerator WaitForMission()
        {
            yield return TestWorld.WaitUntil(() => Director.State == MissionState.Ready || Director.State == MissionState.Failed, 20f);
            Assert.That(Director.State, Is.EqualTo(MissionState.Ready), Director.Report.Failure);
        }

        public IEnumerator LoadPrototype()
        {
            yield return SceneManager.LoadSceneAsync(PrototypeScene, LoadSceneMode.Single);
            yield return null;
            Bind();
            yield return null;
        }

        public void Bind()
        {
            Service = Object.FindFirstObjectByType<IntelligenceService>();
            Hud = Object.FindFirstObjectByType<TacticalHud>();
            Assert.That(Hud, Is.Not.Null, "the scene has no TacticalHud: run Blackglass/HUD/Wire Scenes");
            Blocker = Object.FindFirstObjectByType<PointerBlocker>();
            Overlay = Object.FindFirstObjectByType<DeveloperOverlay>();
            Active = Object.FindFirstObjectByType<ActiveCharacter>();
            Selection = Object.FindFirstObjectByType<UnitSelection>();
            Pause = Object.FindFirstObjectByType<TacticalPause>();
            Encounter = Object.FindFirstObjectByType<Encounter>();
            Input = Object.FindFirstObjectByType<PlayerCommandInput>();
            ViewCamera = Camera.main;
        }

        public static void TearDown()
        {
            Time.timeScale = 1f;
            var name = SceneManager.GetActiveScene().name;
            // Only a shipped scene: without one the active scene is the test runner's own, which must survive.
            if (name == MissionScene || name == PrototypeScene)
                PrototypeSceneTests.DestroySceneObjects();
        }

        // ---- what the HUD shows ----

        /// <summary>Every text under the HUD canvas, hidden ones included (a hidden text must not hold a secret either).</summary>
        public string[] AllTexts() => Hud.Root.GetComponentsInChildren<Text>(true).Select(t => t.text ?? string.Empty).ToArray();

        public List<string> ShownObjectiveRows()
        {
            var rows = new List<string>();
            if (!Hud.Objectives.Root.gameObject.activeInHierarchy)
                return rows;
            for (var i = 0; i < Hud.Objectives.RowCapacity; i++)
            {
                if (Hud.Objectives.Row(i).activeSelf)
                    rows.Add(Hud.Objectives.RowText(i).text);
            }
            return rows;
        }

        /// <summary>The rows the runtime calls for now, through the same rule the HUD uses (the extraction has its own chip).</summary>
        public List<string> ExpectedObjectiveRows()
        {
            var rows = new List<string>();
            var runtime = Director.Runtime;
            var listUnknown = Service != null && Service.ListsUnknownObjectives;
            foreach (var objective in runtime.Objectives)
            {
                if (objective == null || objective == runtime.Extraction)
                    continue;
                if (HudText.TryObjectiveRow(objective, listUnknown, out _, out var text))
                    rows.Add(text);
            }
            return rows.Take(ObjectivesPanel.MaxRows).ToList();
        }

        public string ExpectedExtractionLabel()
        {
            var runtime = Director.Runtime;
            var extraction = runtime.Extraction;
            var inside = 0;
            var required = 0;
            if (extraction is ReachZoneObjective zone)
            {
                required = zone.RequiredUnits;
                if (zone.IsKnown)
                    inside = zone.Inside;
            }
            return HudText.ExtractionLabel(HudText.ExtractionOf(extraction, runtime.Phase, inside), inside, required);
        }

        public SquadCardView CardOf(CommandableUnit unit)
        {
            for (var i = 0; i < Hud.Squad.CardCapacity; i++)
            {
                var card = Hud.Squad.CardAt(i);
                if (card.Root.gameObject.activeInHierarchy && card.Card.Unit == unit)
                    return card;
            }
            Assert.Fail($"no visible card for {unit.name}");
            return null;
        }

        public int VisibleCards()
        {
            var count = 0;
            for (var i = 0; i < Hud.Squad.CardCapacity; i++)
            {
                if (Hud.Squad.CardAt(i).Root.gameObject.activeInHierarchy)
                    count++;
            }
            return count;
        }

        public int MarksOf(HudMarkKind kind) => Hud.Snapshot.Marks.Count(m => m.Kind == kind);

        /// <summary>The visible mark views of a kind (what is drawn, not just what the snapshot asked for).</summary>
        public int DrawnMarksOf(HudMarkKind kind)
        {
            var marks = Hud.Marks;
            var count = 0;
            for (var i = 0; i < marks.PoolCount; i++)
            {
                var view = marks.View(i);
                if (view.Root.gameObject.activeSelf && view.HasKind && view.ShownKind == kind)
                    count++;
            }
            return count;
        }

        public static Vector2 Centre(RectTransform rect) => HudLayout.WorldRect(rect).center;
    }
}
#endif
