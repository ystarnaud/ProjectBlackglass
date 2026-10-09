#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Blackglass.Tests
{
    /// <summary>
    /// The target panel and the world-mark layer driven from a snapshot (and, for the fog rules, from the real builder over
    /// the intelligence rig): shown fields, the hostile tag's shape, the cover-line rule, change-only writes, projected and
    /// pooled marks, hollow versus filled markers, and the layout.
    /// </summary>
    public class HudTargetAndMarksTests
    {
        const string SeenName = "SeenHostile";
        const string SecretName = "SecretHostile";

        readonly List<Object> created = new List<Object>();
        TacticalHud hud;
        RectTransform root;
        Camera viewCamera;
        IntelRig intelRig;

        [TearDown]
        public void TearDown()
        {
            intelRig?.Dispose();
            intelRig = null;
            foreach (var item in created)
            {
                if (item != null)
                    Object.DestroyImmediate(item);
            }
            created.Clear();
        }

        void Build(float width = 1920f, float height = 1080f)
        {
            var hudObject = new GameObject("hud");
            created.Add(hudObject);
            hudObject.SetActive(false);
            hud = hudObject.AddComponent<TacticalHud>();
            hud.Initialize(new HudSources(), null);
            root = HudLayout.CreateRoot(width, height);
            created.Add(root.gameObject);
            hud.BuildInto(root);
        }

        RectTransform Panel(string name) => (RectTransform)root.Find(name);

        /// <summary>
        /// A camera at `position` looking along +Z, and the HUD root moved onto the screen centre at scale 1, so a plain rect
        /// stands in for the overlay canvas: a world point on the camera axis projects to the root's centre.
        /// </summary>
        void AddCamera(Vector3 position, Quaternion rotation)
        {
            var cameraObject = new GameObject("HudTestCamera");
            created.Add(cameraObject);
            cameraObject.transform.SetPositionAndRotation(position, rotation);
            viewCamera = cameraObject.AddComponent<Camera>();
            viewCamera.fieldOfView = 60f;
            Assert.That(viewCamera.pixelWidth, Is.GreaterThan(0), "Precondition: the camera has a screen");
            root.position = new Vector3(viewCamera.pixelWidth * 0.5f, viewCamera.pixelHeight * 0.5f, 0f);
            hud.Marks.CameraSource = () => viewCamera;
        }

        static HudWorldMark Mark(HudMarkKind kind, Vector3 world, string text = "") => new HudWorldMark { Kind = kind, World = world, Text = text };

        static HudTarget SomeTarget(string tag = "HOVERED", string cover = "Cover 65%") => new HudTarget
        {
            Visible = true, Name = "Hostile Rifleman", Detail = "Ranged", Tag = tag, CoverText = cover, Health = 60, MaxHealth = 100,
        };

        Vector2 LocalOf(int i) => hud.Marks.View(i).Root.anchoredPosition;

        // ---- target panel ----

        [Test]
        public void TargetPanel_IsHidden_UnlessTheSnapshotHasATarget()
        {
            Build();
            hud.ApplySnapshot();
            Assert.That(hud.Target.Root.gameObject.activeSelf, Is.False);

            hud.Snapshot.Target = SomeTarget();
            hud.ApplySnapshot();
            Assert.That(hud.Target.Root.gameObject.activeSelf, Is.True);
            Assert.That(hud.Target.Root, Is.SameAs(Panel("Target")));

            hud.Snapshot.Target = default;
            hud.ApplySnapshot();
            Assert.That(hud.Target.Root.gameObject.activeSelf, Is.False);
        }

        [Test]
        public void TargetPanel_ShowsNameDetailHealthAndTag()
        {
            Build();
            hud.Snapshot.Target = SomeTarget("TARGETED");
            hud.ApplySnapshot();

            var panel = hud.Target;
            Assert.That(panel.NameLabel.text, Is.EqualTo("Hostile Rifleman"));
            Assert.That(panel.DetailLabel.text, Is.EqualTo("Ranged"));
            Assert.That(panel.HealthLabel.text, Is.EqualTo(HudText.Health(60, 100)));
            Assert.That(panel.HealthFill.fillAmount, Is.EqualTo(0.6f).Within(1e-4f));
            Assert.That(panel.TagLabel.text, Is.EqualTo("TARGETED"));

            hud.Snapshot.Target = new HudTarget { Visible = true, Name = "Zero", Detail = "", Tag = "AIMING", CoverText = "", Health = 5, MaxHealth = 0 };
            hud.ApplySnapshot();
            Assert.That(panel.HealthFill.fillAmount, Is.Zero, "no maximum draws an empty bar");
            Assert.That(panel.TagLabel.text, Is.EqualTo("AIMING"));
        }

        [Test]
        public void TargetPanel_BlanksItsTextsAsItHides_AndRefillsWhenShownAgain()
        {
            Build();
            hud.Snapshot.Target = SomeTarget("ATTACKING");
            hud.ApplySnapshot();
            var panel = hud.Target;
            Assert.That(panel.NameLabel.text, Is.EqualTo("Hostile Rifleman"), "precondition");

            hud.Snapshot.Target = default;   // the hostile was lost from sight
            hud.ApplySnapshot();
            Assert.That(panel.Root.gameObject.activeSelf, Is.False);
            foreach (var label in new[] { panel.NameLabel, panel.DetailLabel, panel.TagLabel, panel.HealthLabel, panel.CoverLabel })
                Assert.That(label.text, Is.Empty, $"the hidden panel keeps no {label.name}");
            Assert.That(panel.HealthFill.fillAmount, Is.Zero, "nor its health bar");

            hud.Snapshot.Target = SomeTarget("ATTACKING");
            hud.ApplySnapshot();
            Assert.That(panel.NameLabel.text, Is.EqualTo("Hostile Rifleman"));
            Assert.That(panel.HealthLabel.text, Is.EqualTo(HudText.Health(60, 100)), "the same health is written again");
            Assert.That(panel.HealthFill.fillAmount, Is.EqualTo(0.6f).Within(1e-4f));
        }

        [Test]
        public void TargetPanel_MarksHostility_WithTextAndAShape()
        {
            Build();
            hud.Snapshot.Target = SomeTarget();
            hud.ApplySnapshot();

            var panel = hud.Target;
            Assert.That(panel.HostileTag.text, Is.EqualTo("HOSTILE"));
            Assert.That(panel.Diamond.gameObject.activeInHierarchy, Is.True);
            Assert.That(panel.Diamond.rectTransform.localEulerAngles.z, Is.EqualTo(45f).Within(0.01f), "a square turned 45 degrees");
            Assert.That(panel.Diamond.type, Is.EqualTo(Image.Type.Simple));
            Assert.That(panel.Diamond.sprite, Is.Not.Null, "a filled shape");
            Assert.That(panel.Diamond.color, Is.EqualTo(HudTheme.Bad));
            Assert.That(panel.HostileTag.color, Is.EqualTo(HudTheme.Bad));
        }

        [Test]
        public void TargetPanel_CoverLine_ShowsWhilePausedOrAttackingOnly()
        {
            Build();
            var s = hud.Snapshot;

            s.IsPaused = false;
            s.Target = SomeTarget("HOVERED");
            hud.ApplySnapshot();
            Assert.That(hud.Target.CoverLabel.gameObject.activeSelf, Is.False, "real time and merely hovered: no cover line");

            s.Target = SomeTarget("ATTACKING");
            hud.ApplySnapshot();
            Assert.That(hud.Target.CoverLabel.gameObject.activeSelf, Is.True, "attacking shows it");
            Assert.That(hud.Target.CoverLabel.text, Is.EqualTo("Cover 65%"));

            s.Target = SomeTarget("TARGETED");
            hud.ApplySnapshot();
            Assert.That(hud.Target.CoverLabel.gameObject.activeSelf, Is.False, "targeted in real time: hidden again");

            s.IsPaused = true;
            hud.ApplySnapshot();
            Assert.That(hud.Target.CoverLabel.gameObject.activeSelf, Is.True, "paused shows it");

            s.Target = SomeTarget("TARGETED", cover: "");
            hud.ApplySnapshot();
            Assert.That(hud.Target.CoverLabel.gameObject.activeSelf, Is.False, "no cover text (a melee attacker): no line");

            s.Target = SomeTarget("TARGETED", cover: "Exposed");
            hud.ApplySnapshot();
            Assert.That(hud.Target.CoverLabel.text, Is.EqualTo("Exposed"));
        }

        [Test]
        public void TargetPanel_SecondIdenticalApply_WritesNoText()
        {
            Build();
            hud.Snapshot.IsPaused = true;
            hud.Snapshot.Target = SomeTarget("ATTACKING");
            hud.ApplySnapshot();

            var before = HudFactory.TextWrites;
            hud.ApplySnapshot();
            hud.ApplySnapshot();
            Assert.That(HudFactory.TextWrites, Is.EqualTo(before));

            hud.Snapshot.Target = SomeTarget("ATTACKING", cover: "Cover 40%");
            hud.ApplySnapshot();
            Assert.That(HudFactory.TextWrites, Is.EqualTo(before + 1), "only the changed line is written");
        }

        [Test]
        public void TargetPanel_BackgroundAndParts_IgnoreRaycasts()
        {
            Build();
            hud.Snapshot.Target = SomeTarget();
            hud.ApplySnapshot();
            foreach (var graphic in hud.Target.Root.GetComponentsInChildren<Graphic>(true))
                Assert.That(graphic.raycastTarget, Is.False, graphic.name);
        }

        [Test]
        public void TheLayers_KeepTheirOrder_MarksFirst()
        {
            Build();
            Assert.That(Panel("WorldMarks").GetSiblingIndex(), Is.Zero);
            Assert.That(hud.Marks.Root, Is.SameAs(Panel("WorldMarks")));
        }

        // ---- world marks ----

        [Test]
        public void Marks_ThreeHostiles_ShowThreeFilledDiamonds()
        {
            Build();
            AddCamera(Vector3.zero, Quaternion.identity);
            for (var i = 0; i < 3; i++)
                hud.Snapshot.Marks.Add(Mark(HudMarkKind.Hostile, new Vector3(i * 2f - 2f, 0f, 10f)));
            hud.ApplySnapshot();

            var layer = hud.Marks;
            Assert.That(layer.VisibleCount, Is.EqualTo(3));
            for (var i = 0; i < 3; i++)
            {
                var view = layer.View(i);
                Assert.That(view.Root.gameObject.activeSelf, Is.True);
                Assert.That(view.Filled.gameObject.activeSelf, Is.True, "a filled diamond");
                Assert.That(view.Filled.rectTransform.localEulerAngles.z, Is.EqualTo(45f).Within(0.01f));
                Assert.That(view.Filled.color, Is.EqualTo(HudTheme.Bad));
                Assert.That(view.Hollow.gameObject.activeSelf, Is.False);
                Assert.That(view.Square.gameObject.activeSelf, Is.False);
                Assert.That(view.Glyph.gameObject.activeSelf, Is.False);
                Assert.That(view.Label.gameObject.activeSelf, Is.False, "a hostile mark has no text");
            }
            Assert.That(layer.VisibleCount, Is.EqualTo(3));
        }

        [Test]
        public void Marks_LastKnown_UsesAHollowShapeAndOtherText_ThanAHostile()
        {
            Build();
            AddCamera(Vector3.zero, Quaternion.identity);
            hud.Snapshot.Marks.Add(Mark(HudMarkKind.Hostile, new Vector3(-2f, 0f, 10f)));
            hud.Snapshot.Marks.Add(Mark(HudMarkKind.LastKnown, new Vector3(2f, 0f, 10f), "LAST KNOWN"));
            hud.ApplySnapshot();

            var hostile = hud.Marks.View(0);
            var lastKnown = hud.Marks.View(1);

            Assert.That(hostile.Filled.gameObject.activeSelf, Is.True);
            Assert.That(lastKnown.Filled.gameObject.activeSelf, Is.False, "last known is not the filled hostile diamond");

            Assert.That(lastKnown.Hollow.gameObject.activeSelf, Is.True, "last known is a diamond frame");
            Assert.That(hostile.Hollow.gameObject.activeSelf, Is.False);
            Assert.That(lastKnown.Hollow.localEulerAngles.z, Is.EqualTo(45f).Within(0.01f));
            Assert.That(lastKnown.HollowBars.Length, Is.EqualTo(4), "four thin edges");
            foreach (var bar in lastKnown.HollowBars)
            {
                Assert.That(bar.gameObject.activeInHierarchy, Is.True);
                var size = bar.rectTransform.rect.size;
                Assert.That(Mathf.Min(size.x, size.y), Is.LessThanOrEqualTo(3f), "each edge is thin");
                Assert.That(bar.color, Is.EqualTo(HudTheme.Warn));
            }
            var frame = lastKnown.Hollow.rect;
            Assert.That(frame.width, Is.GreaterThan(8f));
            Assert.That(frame.height, Is.GreaterThan(8f));

            Assert.That(lastKnown.Glyph.gameObject.activeSelf, Is.True);
            Assert.That(lastKnown.Glyph.text, Is.EqualTo("?"));
            Assert.That(lastKnown.Label.gameObject.activeSelf, Is.True);
            Assert.That(lastKnown.Label.text, Is.EqualTo("LAST KNOWN"));
            Assert.That(lastKnown.Label.color, Is.EqualTo(HudTheme.Warn));
            Assert.That(hostile.Label.gameObject.activeSelf, Is.False);
            Assert.That(hostile.Glyph.gameObject.activeSelf, Is.False);
            Assert.That(hostile.Label.text, Is.Not.EqualTo(lastKnown.Label.text));
        }

        [Test]
        public void Marks_ObjectiveAndExtraction_ShowASquareAndTheLabel()
        {
            Build();
            AddCamera(Vector3.zero, Quaternion.identity);
            hud.Snapshot.Marks.Add(Mark(HudMarkKind.Objective, new Vector3(-3f, 0f, 10f), "Terminal Kappa"));
            hud.Snapshot.Marks.Add(Mark(HudMarkKind.Extraction, new Vector3(3f, 0f, 10f), "Extraction"));
            hud.ApplySnapshot();

            foreach (var (index, text) in new[] { (0, "Terminal Kappa"), (1, "Extraction") })
            {
                var view = hud.Marks.View(index);
                Assert.That(view.Square.gameObject.activeSelf, Is.True);
                Assert.That(view.Square.rectTransform.localEulerAngles.z, Is.Zero.Within(0.01f), "a plain square");
                Assert.That(view.Filled.gameObject.activeSelf, Is.False);
                Assert.That(view.Hollow.gameObject.activeSelf, Is.False);
                Assert.That(view.Glyph.gameObject.activeSelf, Is.False);
                Assert.That(view.Label.gameObject.activeSelf, Is.True);
                Assert.That(view.Label.text, Is.EqualTo(text));
            }
        }

        [Test]
        public void Marks_BehindTheCamera_AreHidden()
        {
            Build();
            AddCamera(Vector3.zero, Quaternion.identity);
            hud.Snapshot.Marks.Add(Mark(HudMarkKind.Hostile, new Vector3(0f, 0f, 10f)));
            hud.Snapshot.Marks.Add(Mark(HudMarkKind.Hostile, new Vector3(0f, 0f, -10f)));
            hud.Snapshot.Marks.Add(Mark(HudMarkKind.Objective, new Vector3(0f, 0f, 0f), "On the lens"));
            hud.ApplySnapshot();

            Assert.That(hud.Marks.View(0).Root.gameObject.activeSelf, Is.True);
            Assert.That(hud.Marks.View(1).Root.gameObject.activeSelf, Is.False, "behind the camera");
            Assert.That(hud.Marks.View(2).Root.gameObject.activeSelf, Is.False, "at the camera plane");
            Assert.That(hud.Marks.VisibleCount, Is.EqualTo(1));

            viewCamera.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
            hud.ApplySnapshot();
            Assert.That(hud.Marks.View(0).Root.gameObject.activeSelf, Is.False, "the camera turned away");
            Assert.That(hud.Marks.View(1).Root.gameObject.activeSelf, Is.True, "and the one behind it is now ahead");
        }

        [Test]
        public void Marks_FollowTheCamera_AndASightedMarkSitsAtTheRootCentre()
        {
            Build();
            AddCamera(Vector3.zero, Quaternion.identity);
            hud.Snapshot.Marks.Add(Mark(HudMarkKind.Hostile, new Vector3(0f, 0f, 10f)));
            hud.ApplySnapshot();

            Assert.That(LocalOf(0).magnitude, Is.LessThan(0.5f), "on the camera axis: the root centre");

            viewCamera.transform.position = new Vector3(2f, 0f, 0f);
            hud.ApplySnapshot();
            var moved = LocalOf(0);
            Assert.That(moved.x, Is.LessThan(-20f), "the camera moved right, so the mark moves left");
            Assert.That(moved.y, Is.EqualTo(0f).Within(0.5f));

            viewCamera.transform.position = Vector3.zero;
            hud.ApplySnapshot();
            Assert.That(LocalOf(0).magnitude, Is.LessThan(0.5f));

            hud.Snapshot.Marks[0] = Mark(HudMarkKind.Hostile, new Vector3(0f, 3f, 10f));
            hud.ApplySnapshot();
            Assert.That(LocalOf(0).y, Is.GreaterThan(20f), "higher in the world is higher on screen");
        }

        [Test]
        public void Marks_ProjectIntoTheRootsLocalSpace_WithTheCanvasScale()
        {
            Build();
            AddCamera(Vector3.zero, Quaternion.identity);
            var world = new Vector3(1.5f, 0.7f, 9f);
            hud.Snapshot.Marks.Add(Mark(HudMarkKind.Hostile, world));
            hud.ApplySnapshot();
            var unscaled = LocalOf(0);

            var screen = viewCamera.WorldToScreenPoint(world);
            Assert.That(unscaled.x, Is.EqualTo(screen.x - viewCamera.pixelWidth * 0.5f).Within(0.5f));
            Assert.That(unscaled.y, Is.EqualTo(screen.y - viewCamera.pixelHeight * 0.5f).Within(0.5f));

            root.localScale = new Vector3(2f, 2f, 1f);   // a canvas scale factor of 2: one unit is two pixels
            hud.ApplySnapshot();
            Assert.That(LocalOf(0).x, Is.EqualTo(unscaled.x / 2f).Within(0.5f));
            Assert.That(LocalOf(0).y, Is.EqualTo(unscaled.y / 2f).Within(0.5f));
        }

        [Test]
        public void Marks_ThePoolIsReused_NothingIsCreatedAfterGrowth()
        {
            Build();
            AddCamera(Vector3.zero, Quaternion.identity);
            for (var i = 0; i < 4; i++)
                hud.Snapshot.Marks.Add(Mark(i == 3 ? HudMarkKind.LastKnown : HudMarkKind.Hostile, new Vector3(i - 2f, 0f, 10f), i == 3 ? "LAST KNOWN" : ""));
            hud.ApplySnapshot();

            var layer = hud.Marks;
            var objects = Enumerable.Range(0, layer.PoolCount).Select(i => layer.View(i).Root.gameObject).ToArray();
            var count = layer.PoolCount;
            var children = root.GetComponentsInChildren<Transform>(true).Length;
            Assert.That(count, Is.EqualTo(4));

            for (var n = 0; n < 100; n++)
            {
                viewCamera.transform.position = new Vector3(Mathf.Sin(n) * 3f, 0f, 0f);
                hud.ApplySnapshot();
            }

            Assert.That(layer.PoolCount, Is.EqualTo(count));
            Assert.That(root.GetComponentsInChildren<Transform>(true).Length, Is.EqualTo(children));
            for (var i = 0; i < count; i++)
                Assert.That(layer.View(i).Root.gameObject, Is.SameAs(objects[i]), $"entry {i} is the same object");

            hud.Snapshot.Marks.RemoveRange(1, 3);
            hud.ApplySnapshot();
            Assert.That(layer.PoolCount, Is.EqualTo(count), "fewer marks hide entries, they do not destroy them");
            Assert.That(layer.VisibleCount, Is.EqualTo(1));
            Assert.That(objects[3] != null && !objects[3].activeSelf, Is.True);

            hud.Snapshot.Marks.Add(Mark(HudMarkKind.Objective, new Vector3(0f, 0f, 10f), "Back again"));
            hud.ApplySnapshot();
            Assert.That(layer.PoolCount, Is.EqualTo(count), "a returning mark reuses a hidden entry");
            Assert.That(layer.View(1).Label.text, Is.EqualTo("Back again"));
            Assert.That(layer.View(1).Root.gameObject, Is.SameAs(objects[1]));
        }

        [Test]
        public void Marks_GrowthStopsAtTheCap_AndExtraMarksAreDropped()
        {
            Build();
            AddCamera(Vector3.zero, Quaternion.identity);
            for (var i = 0; i < 40; i++)
                hud.Snapshot.Marks.Add(Mark(HudMarkKind.Hostile, new Vector3((i % 8) - 4f, (i / 8) - 2f, 10f)));
            Assert.DoesNotThrow(() => hud.ApplySnapshot());

            Assert.That(WorldMarkLayer.Capacity, Is.EqualTo(32));
            Assert.That(hud.Marks.PoolCount, Is.EqualTo(32));
            Assert.That(hud.Marks.VisibleCount, Is.EqualTo(32));

            hud.ApplySnapshot();
            Assert.That(hud.Marks.PoolCount, Is.EqualTo(32), "no further growth");
        }

        [Test]
        public void Marks_SteadyFrames_WriteNoText_AndRepositionOnlyOnRealMovement()
        {
            Build();
            AddCamera(Vector3.zero, Quaternion.identity);
            hud.Snapshot.Marks.Add(Mark(HudMarkKind.LastKnown, new Vector3(1f, 0f, 10f), "LAST KNOWN"));
            hud.Snapshot.Marks.Add(Mark(HudMarkKind.Objective, new Vector3(-1f, 0f, 10f), "Terminal Kappa"));
            hud.ApplySnapshot();

            var before = HudFactory.TextWrites;
            var position = LocalOf(0);
            hud.ApplySnapshot();
            hud.ApplySnapshot();
            Assert.That(HudFactory.TextWrites, Is.EqualTo(before));
            Assert.That(LocalOf(0), Is.EqualTo(position));

            viewCamera.transform.position = new Vector3(0.5f, 0f, 0f);
            hud.ApplySnapshot();
            Assert.That(LocalOf(0), Is.Not.EqualTo(position), "a real movement repositions");
            Assert.That(HudFactory.TextWrites, Is.EqualTo(before), "and rewrites no text");

            // A change of kind in the same slot writes its text once.
            hud.Snapshot.Marks[0] = Mark(HudMarkKind.Hostile, new Vector3(1f, 0f, 10f));
            hud.ApplySnapshot();
            Assert.That(hud.Marks.View(0).Filled.gameObject.activeSelf, Is.True);
            Assert.That(hud.Marks.View(0).Hollow.gameObject.activeSelf, Is.False);
            Assert.That(hud.Marks.View(0).Label.gameObject.activeSelf, Is.False);
        }

        [Test]
        public void Marks_WithoutACamera_ShowNothingAndDoNotThrow()
        {
            Build();
            hud.Marks.CameraSource = () => null;
            hud.Snapshot.Marks.Add(Mark(HudMarkKind.Hostile, new Vector3(0f, 0f, 10f)));
            Assert.DoesNotThrow(() => hud.ApplySnapshot());
            Assert.That(hud.Marks.VisibleCount, Is.Zero);
        }

        [Test]
        public void Marks_ImagesAndText_IgnoreRaycasts()
        {
            Build();
            AddCamera(Vector3.zero, Quaternion.identity);
            hud.Snapshot.Marks.Add(Mark(HudMarkKind.LastKnown, new Vector3(0f, 0f, 10f), "LAST KNOWN"));
            hud.ApplySnapshot();
            foreach (var graphic in Panel("WorldMarks").GetComponentsInChildren<Graphic>(true))
                Assert.That(graphic.raycastTarget, Is.False, graphic.name);
        }

        // ---- layout ----

        [TestCase(1920, 1080)]
        [TestCase(1920, 1200)]
        [TestCase(2560, 1080)]
        public void Layout_TargetPanel_StaysInsideAndClearOfTheOtherPanels(int screenWidth, int screenHeight)
        {
            var units = HudLayout.CanvasUnits(screenWidth, screenHeight);
            Build(units.x, units.y);
            var s = hud.Snapshot;
            s.HasMission = true;
            s.PhaseText = "extraction open";
            s.BannerText = MissionHudText.Banner(MissionPhase.Success);
            for (var i = 0; i < 8; i++)
                s.Objectives.Add(new HudObjectiveRow { Kind = HudObjectiveKind.Active, Text = "Download the security logs at Terminal Kappa " + i + " (45%)" });
            s.Extraction = HudExtractionState.Active;
            s.ExtractionInside = 3;
            s.ExtractionRequired = 4;
            s.IsPaused = true;
            s.ResumePrompt = "Options";
            s.HasFollow = true;
            s.HasPause = true;
            for (var i = 0; i < 6; i++)
                s.Squad.Add(new HudSquadCard { Name = "Operative Number " + i, Role = "Ranged", Initials = "ON", Tag = "FOLLOWING", Rank = 3, Health = 80, MaxHealth = 100 });
            for (var i = 0; i < 6; i++)
                s.Prompts.Add(new HudPromptEntry { Label = "Switch character", Prompt = "LB / RB" });
            s.HasControlled = true;
            s.ControlledName = "Darius";
            s.ControlledRole = "Ranged";
            s.ControlledHealth = 80;
            s.ControlledMaxHealth = 100;
            s.QueueOwner = "Darius";
            for (var i = 0; i < 8; i++)
                s.Queue.Add(new HudCommandStep { Number = i + 1, Text = "Attack (target lost)", IsCurrent = i == 0 });
            s.Target = new HudTarget
            {
                Visible = true, Name = "Hostile Rifleman Alpha", Detail = "Ranged Specialist Marksman", Tag = "ATTACKING",
                CoverText = "Cover 65%", Health = 60, MaxHealth = 100,
            };
            hud.ApplySnapshot();
            HudLayout.Rebuild(root);

            var target = hud.Target.Root;
            var status = hud.Status;
            HudLayout.AssertInside(root, target);
            HudLayout.AssertNoOverlap(target, Panel("Prompts"), Panel("Operative"), Panel("Squad"), Panel("Objectives"),
                status.PauseBanner, status.ResultBanner, status.RightBlock);
            Assert.That(HudLayout.WorldRect(target).size, Is.EqualTo(new Vector2(340f, 140f)));

            var panel = hud.Target;
            HudLayout.AssertInside(target, panel.NameLabel.rectTransform, panel.DetailLabel.rectTransform, panel.HealthBar,
                panel.HealthLabel.rectTransform, panel.TagLabel.rectTransform, panel.CoverLabel.rectTransform,
                panel.HostileTag.rectTransform);
            Assert.That(HudLayout.WorldRect(target).Contains(panel.Diamond.rectTransform.position), Is.True, "the diamond sits on the panel");
            HudLayout.AssertNoOverlap(panel.NameLabel.rectTransform, panel.DetailLabel.rectTransform, panel.HealthBar,
                panel.HealthLabel.rectTransform, panel.CoverLabel.rectTransform);
            Assert.That(panel.NameLabel.preferredHeight, Is.LessThanOrEqualTo(panel.NameLabel.rectTransform.rect.height + 0.5f), "the name fits one line");
            Assert.That(panel.DetailLabel.preferredHeight, Is.LessThanOrEqualTo(panel.DetailLabel.rectTransform.rect.height + 0.5f), "the detail fits one line");
            Assert.That(panel.CoverLabel.preferredHeight, Is.LessThanOrEqualTo(panel.CoverLabel.rectTransform.rect.height + 0.5f));
        }

        // ---- fog integration (the real builder over the intelligence rig) ----

        string AllMarkText() => string.Join("\n", Panel("WorldMarks").GetComponentsInChildren<Text>(true).Select(t => t.text));

        [UnityTest]
        public IEnumerator Fog_OnlyObservedHostilesGetDiamonds_ALostOneBecomesOneLastKnownMark_AndNothingNamesAHostile()
        {
            intelRig = new IntelRig(corridor: true);
            var seen = intelRig.AddHostile(IntelRig.InLineGround);
            seen.name = SeenName;
            var secret = intelRig.AddHostile(IntelRig.OffAxisGround);
            secret.name = SecretName;
            intelRig.Begin(IntelRig.Fog());
            yield return null;
            Assert.That(intelRig.Service.CanTarget(seen), Is.True, "Precondition");
            Assert.That(intelRig.Service.StateOfEnemy(secret), Is.EqualTo(KnowledgeState.Unknown), "Precondition");

            Build();
            AddCamera(new Vector3(0f, 40f, 0f), Quaternion.Euler(90f, 0f, 0f));
            var builder = new HudSnapshotBuilder();
            var sources = new HudSources { encounter = intelRig.Encounter, intelligence = intelRig.Service };

            builder.Build(sources, hud.Snapshot, 0f);
            hud.ApplySnapshot();
            Assert.That(hud.Marks.VisibleCount, Is.EqualTo(1), "the observed hostile only");
            Assert.That(hud.Marks.View(0).Filled.gameObject.activeSelf, Is.True);
            Assert.That(hud.Marks.View(0).Hollow.gameObject.activeSelf, Is.False);
            var screen = viewCamera.WorldToScreenPoint(seen.transform.position + Vector3.up * 2.3f);
            Assert.That(LocalOf(0).x, Is.EqualTo(screen.x - viewCamera.pixelWidth * 0.5f).Within(0.5f));
            Assert.That(LocalOf(0).y, Is.EqualTo(screen.y - viewCamera.pixelHeight * 0.5f).Within(0.5f));
            Assert.That(AllMarkText(), Does.Not.Contain(SecretName).And.Not.Contain(SeenName));

            intelRig.Friendly.transform.position = new Vector3(-6.5f, 1f, 1.5f);
            Physics.SyncTransforms();
            intelRig.Service.RunPass();
            Assert.That(intelRig.Service.StateOfEnemy(seen), Is.EqualTo(KnowledgeState.Discovered), "Precondition: lost");
            Assert.That(intelRig.Service.TryLastKnown(seen, out var lastSeen), Is.True);

            builder.Build(sources, hud.Snapshot, 0f);
            hud.ApplySnapshot();
            Assert.That(hud.Marks.VisibleCount, Is.EqualTo(1), "one last-known mark, nothing for the never-seen one");
            var view = hud.Marks.View(0);
            Assert.That(view.Filled.gameObject.activeSelf, Is.False, "no hostile diamond for a lost unit");
            Assert.That(view.Hollow.gameObject.activeSelf, Is.True);
            Assert.That(view.Label.text, Is.EqualTo("LAST KNOWN"));
            var lastScreen = viewCamera.WorldToScreenPoint(lastSeen);
            Assert.That(LocalOf(0).x, Is.EqualTo(lastScreen.x - viewCamera.pixelWidth * 0.5f).Within(0.5f), "at the last-known point");
            Assert.That(LocalOf(0).y, Is.EqualTo(lastScreen.y - viewCamera.pixelHeight * 0.5f).Within(0.5f));
            Assert.That(AllMarkText(), Does.Not.Contain(SecretName).And.Not.Contain(SeenName));
            Assert.That(hud.Target.Root.gameObject.activeSelf, Is.False);
        }

        [UnityTest]
        public IEnumerator Fog_TheTruthView_WidensNothing()
        {
            intelRig = new IntelRig(corridor: false);
            var secret = intelRig.AddHostile(IntelRig.InLineGround);
            secret.name = SecretName;
            intelRig.Begin(IntelRig.Fog());
            yield return null;
            Assert.That(intelRig.Service.CanTarget(secret), Is.False, "Precondition: behind the closed wall");

            Build();
            AddCamera(new Vector3(0f, 40f, 0f), Quaternion.Euler(90f, 0f, 0f));
            intelRig.Service.TruthView = true;
            Assert.That(Knowledge.IsShown(intelRig.Service, secret), Is.True, "Precondition: the truth view shows it in the debug views");

            var builder = new HudSnapshotBuilder();
            builder.Build(new HudSources { encounter = intelRig.Encounter, intelligence = intelRig.Service }, hud.Snapshot, 0f);
            hud.ApplySnapshot();

            Assert.That(hud.Marks.VisibleCount, Is.Zero);
            Assert.That(hud.Target.Root.gameObject.activeSelf, Is.False);
            Assert.That(AllMarkText(), Does.Not.Contain(SecretName));
        }
    }
}
#endif
