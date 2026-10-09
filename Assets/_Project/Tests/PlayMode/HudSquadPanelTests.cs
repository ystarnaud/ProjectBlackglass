#if UNITY_EDITOR
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Blackglass.Tests
{
    /// <summary>
    /// The squad panel driven straight from a snapshot: card pool and order, the text of every card field, non-colour-only
    /// state, change-only writes and layout.
    /// </summary>
    public class HudSquadPanelTests
    {
        const string RankSeparator = " \u00B7 ";

        readonly List<Object> created = new List<Object>();
        TacticalHud hud;
        RectTransform root;

        [TearDown]
        public void TearDown()
        {
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

        static HudSquadCard Member(string name, string role = "Ranged", int rank = 2, int health = 80, int max = 100, string tag = "") =>
            new HudSquadCard
            {
                Name = name, Role = role, Initials = HudText.Initials(name), Tag = tag,
                Rank = rank, Health = health, MaxHealth = max,
            };

        static string RoleLine(string role, int rank) => rank > 0 ? role + RankSeparator + "Rank " + rank : role;

        static string VisibleText(SquadCardView card)
        {
            var text = new StringBuilder();
            foreach (var label in card.Root.GetComponentsInChildren<Text>(false))
                text.Append(label.text);
            return text.ToString();
        }

        [Test]
        public void Portraits_ResolveToNothingForNow()
        {
            Assert.That(HudPortraits.Resolve(null), Is.Null);
        }

        [Test]
        public void TheRankSeparator_IsInTheBuiltInFont()
        {
            Assert.That(HudFactory.Font.HasCharacter('\u00B7'), Is.True, "otherwise the card needs a plain ASCII separator");
        }

        [Test]
        public void Build_CreatesSixHiddenCardsInsideTheSquadZone()
        {
            Build();
            var panel = hud.Squad;
            Assert.That(panel.Root, Is.SameAs(Panel("Squad")));
            Assert.That(panel.CardCapacity, Is.EqualTo(6));
            Assert.That(panel.Root.GetComponents<Graphic>(), Is.Empty, "the zone itself draws nothing");
            for (var i = 0; i < panel.CardCapacity; i++)
                Assert.That(panel.CardAt(i).Root.gameObject.activeSelf, Is.False, $"card {i} starts hidden");
        }

        [TestCase(0, 0)]
        [TestCase(3, 3)]
        [TestCase(6, 6)]
        [TestCase(7, 6)]
        public void Cards_AreShownInSnapshotOrder_AndExtrasAreDropped(int entries, int expectedVisible)
        {
            Build();
            var s = hud.Snapshot;
            for (var i = 0; i < entries; i++)
                s.Squad.Add(Member("Operative " + (char)('A' + i)));

            Assert.DoesNotThrow(() => hud.ApplySnapshot());

            for (var i = 0; i < hud.Squad.CardCapacity; i++)
            {
                var card = hud.Squad.CardAt(i);
                Assert.That(card.Root.gameObject.activeSelf, Is.EqualTo(i < expectedVisible), $"card {i}");
                if (i < expectedVisible)
                    Assert.That(card.NameLabel.text, Is.EqualTo("Operative " + (char)('A' + i)), $"card {i} follows snapshot order");
            }

            s.Squad.Clear();
            hud.ApplySnapshot();
            for (var i = 0; i < hud.Squad.CardCapacity; i++)
                Assert.That(hud.Squad.CardAt(i).Root.gameObject.activeSelf, Is.False, $"card {i} hides when the squad empties");
        }

        [Test]
        public void ACard_ShowsNameRoleRankHealthAndInitials()
        {
            Build();
            var member = Member("Darius Vale", "Ranged", 3, 64, 100);
            hud.Snapshot.Squad.Add(member);
            hud.ApplySnapshot();

            var card = hud.Squad.CardAt(0);
            Assert.That(card.NameLabel.text, Is.EqualTo("Darius Vale"));
            Assert.That(card.RoleLabel.text, Is.EqualTo("Ranged" + RankSeparator + "Rank 3"));
            Assert.That(card.HealthLabel.text, Is.EqualTo(HudText.Health(64, 100)));
            Assert.That(card.HealthFill.fillAmount, Is.EqualTo(0.64f).Within(0.001f));
            Assert.That(card.InitialsLabel.text, Is.EqualTo(HudText.Initials("Darius Vale")));
            Assert.That(card.InitialsLabel.text, Is.EqualTo("DV"));
            Assert.That(card.Card.Name, Is.EqualTo("Darius Vale"), "the card keeps the last applied data");
        }

        [Test]
        public void RankZero_HidesTheRank()
        {
            Build();
            hud.Snapshot.Squad.Add(Member("Kestrel", "Support", 0));
            hud.ApplySnapshot();
            Assert.That(hud.Squad.CardAt(0).RoleLabel.text, Is.EqualTo("Support"));

            hud.Snapshot.Squad[0] = Member("Kestrel", "Support", 4);
            hud.ApplySnapshot();
            Assert.That(hud.Squad.CardAt(0).RoleLabel.text, Is.EqualTo("Support" + RankSeparator + "Rank 4"));

            hud.Snapshot.Squad[0] = Member("Kestrel", "Support", 0);
            hud.ApplySnapshot();
            Assert.That(hud.Squad.CardAt(0).RoleLabel.text, Is.EqualTo("Support"), "back to no rank");
        }

        [Test]
        public void ControlledSelectedAndDown_EachShowTheirWord()
        {
            Build();
            var s = hud.Snapshot;
            var controlled = Member("Alpha"); controlled.IsControlled = true;
            var selected = Member("Bravo"); selected.IsSelected = true;
            var down = Member("Charlie", health: 0); down.IsDown = true;
            var plain = Member("Delta");
            s.Squad.Add(controlled); s.Squad.Add(selected); s.Squad.Add(down); s.Squad.Add(plain);
            hud.ApplySnapshot();

            var a = hud.Squad.CardAt(0);
            Assert.That(a.ControlLabel.gameObject.activeSelf, Is.True);
            Assert.That(a.ControlLabel.text, Is.EqualTo("CONTROL"));
            Assert.That(a.ControlMark.gameObject.activeSelf, Is.True, "a filled square at the left edge");
            Assert.That(a.SelectedLabel.gameObject.activeSelf, Is.False);
            Assert.That(a.SelectedFrame.gameObject.activeSelf, Is.False);

            var b = hud.Squad.CardAt(1);
            Assert.That(b.SelectedLabel.gameObject.activeSelf, Is.True);
            Assert.That(b.SelectedLabel.text, Is.EqualTo("SELECTED"));
            Assert.That(b.SelectedFrame.gameObject.activeSelf, Is.True, "an outline frame");
            Assert.That(b.ControlLabel.gameObject.activeSelf, Is.False);
            Assert.That(b.ControlMark.gameObject.activeSelf, Is.False);

            var c = hud.Squad.CardAt(2);
            Assert.That(c.DownLabel.gameObject.activeSelf, Is.True);
            Assert.That(c.DownLabel.text, Is.EqualTo("DOWN"));
            Assert.That(c.HealthFill.fillAmount, Is.Zero, "the health bar is empty");
            Assert.That(c.HealthLabel.text, Is.EqualTo(HudText.Health(0, 100)));
            Assert.That(c.ContentAlpha, Is.LessThan(1f), "the card is dimmed");
            Assert.That(EffectiveAlpha(c.DownLabel), Is.GreaterThanOrEqualTo(0.8f), "the DOWN word stays legible on a dimmed card");
            Assert.That(EffectiveAlpha(c.TagLabel), Is.GreaterThanOrEqualTo(0.8f));
            Assert.That(EffectiveAlpha(c.NameLabel), Is.LessThan(0.8f), "the name is dimmed");

            var d = hud.Squad.CardAt(3);
            Assert.That(d.ContentAlpha, Is.EqualTo(1f));
            Assert.That(d.ControlLabel.gameObject.activeSelf, Is.False);
            Assert.That(d.SelectedLabel.gameObject.activeSelf, Is.False);
            Assert.That(d.DownLabel.gameObject.activeSelf, Is.False);
            Assert.That(d.TagLabel.gameObject.activeSelf, Is.False);
            Assert.That(d.ControlMark.gameObject.activeSelf, Is.False);
            Assert.That(d.SelectedFrame.gameObject.activeSelf, Is.False);

            // States go away again when the snapshot says so.
            s.Squad[0] = Member("Alpha");
            s.Squad[2] = Member("Charlie");
            hud.ApplySnapshot();
            Assert.That(hud.Squad.CardAt(0).ControlLabel.gameObject.activeSelf, Is.False);
            Assert.That(hud.Squad.CardAt(0).ControlMark.gameObject.activeSelf, Is.False);
            Assert.That(hud.Squad.CardAt(2).DownLabel.gameObject.activeSelf, Is.False);
            Assert.That(hud.Squad.CardAt(2).ContentAlpha, Is.EqualTo(1f));
        }

        static float EffectiveAlpha(Text label)
        {
            var alpha = 1f;
            foreach (var group in label.GetComponentsInParent<CanvasGroup>(true))
                alpha *= group.alpha;
            return alpha;
        }

        [Test]
        public void ARoleLessCard_ShowsTheRankWithoutASeparator()
        {
            Build();
            hud.Snapshot.Squad.Add(Member("Kestrel", null, 2));
            hud.ApplySnapshot();
            Assert.That(hud.Squad.CardAt(0).RoleLabel.text, Is.EqualTo("Rank 2"));
            hud.Snapshot.Squad[0] = Member("Kestrel", null, 0);
            hud.ApplySnapshot();
            Assert.That(hud.Squad.CardAt(0).RoleLabel.text, Is.EqualTo(string.Empty));
        }

        [Test]
        public void ACompanionTag_ShowsOnlyWhenNotEmpty()
        {
            Build();
            var s = hud.Snapshot;
            s.Squad.Add(Member("Kestrel", tag: "FOLLOWING"));
            s.Squad.Add(Member("Sable", tag: ""));
            hud.ApplySnapshot();

            Assert.That(hud.Squad.CardAt(0).TagLabel.gameObject.activeSelf, Is.True);
            Assert.That(hud.Squad.CardAt(0).TagLabel.text, Is.EqualTo("FOLLOWING"));
            Assert.That(hud.Squad.CardAt(1).TagLabel.gameObject.activeSelf, Is.False);

            s.Squad[0] = Member("Kestrel", tag: "HOLDING");
            hud.ApplySnapshot();
            Assert.That(hud.Squad.CardAt(0).TagLabel.text, Is.EqualTo("HOLDING"));

            s.Squad[0] = Member("Kestrel", tag: "");
            hud.ApplySnapshot();
            Assert.That(hud.Squad.CardAt(0).TagLabel.gameObject.activeSelf, Is.False);
        }

        [Test]
        public void EveryState_HasVisibleText_NotJustAColour()
        {
            Build();
            var s = hud.Snapshot;
            s.Squad.Add(Member("Alpha"));
            hud.ApplySnapshot();
            var card = hud.Squad.CardAt(0);
            var baseline = VisibleText(card);

            Variant(s, card, baseline, "CONTROL", m => { m.IsControlled = true; return m; });
            Variant(s, card, baseline, "SELECTED", m => { m.IsSelected = true; return m; });
            Variant(s, card, baseline, "DOWN", m => { m.IsDown = true; m.Health = 0; return m; });
            Variant(s, card, baseline, "PARKED", m => { m.Tag = "PARKED"; return m; });
        }

        void Variant(HudSnapshot s, SquadCardView card, string baseline, string word, System.Func<HudSquadCard, HudSquadCard> change)
        {
            var original = s.Squad[0];
            s.Squad[0] = change(original);
            hud.ApplySnapshot();
            var text = VisibleText(card);
            Assert.That(text, Does.Contain(word), $"{word} is written on the card");
            Assert.That(text, Is.Not.EqualTo(baseline), $"{word} changes the text, not only a colour");
            s.Squad[0] = original;
            hud.ApplySnapshot();
            Assert.That(VisibleText(card), Is.EqualTo(baseline), $"{word} leaves nothing behind");
        }

        [Test]
        public void ThePanelText_IsExactlyTheCardFields()
        {
            Build();
            var s = hud.Snapshot;
            var a = Member("Darius Vale", "Ranged", 3, 64, 100); a.IsControlled = true; a.IsSelected = true;
            var b = Member("Kestrel Moon", "Support", 0, 40, 90, "FOLLOWING");
            var c = Member("Sable", "Melee", 1, 0, 120, "PARKED"); c.IsDown = true;
            s.Squad.Add(a); s.Squad.Add(b); s.Squad.Add(c);
            hud.ApplySnapshot();

            var expected = new StringBuilder();
            AppendCard(expected, a);
            AppendCard(expected, b);
            AppendCard(expected, c);

            var actual = new StringBuilder();
            foreach (var label in hud.Squad.Root.GetComponentsInChildren<Text>(false))
                actual.Append(label.text);
            Assert.That(actual.ToString(), Is.EqualTo(expected.ToString()));
        }

        [Test]
        public void NoUnitNameOrId_AppearsInThePanelText()
        {
            Build();
            const string leak = "3f9a2c71-5b0e-4d38-a6c4-91e7d02b8f55";
            var unitObject = new GameObject("Unit-" + leak, typeof(CommandableUnit));
            created.Add(unitObject);
            var member = Member("Darius Vale");
            member.Unit = unitObject.GetComponent<CommandableUnit>();
            hud.Snapshot.Squad.Add(member);
            hud.ApplySnapshot();

            foreach (var label in hud.Squad.Root.GetComponentsInChildren<Text>(true))
                Assert.That(label.text, Does.Not.Contain(leak).And.Not.Contain("Unit-"), $"{label.name} leaks the unit object's name");
            Assert.That(VisibleText(hud.Squad.CardAt(0)), Does.Not.Contain(leak));
        }

        [Test]
        public void TheCard_FollowsTheUnit_EvenWhenEveryFieldIsTheSame()
        {
            Build();
            var first = new GameObject("first", typeof(CommandableUnit));
            var second = new GameObject("second", typeof(CommandableUnit));
            created.Add(first);
            created.Add(second);
            var member = Member("Alpha");
            member.Unit = first.GetComponent<CommandableUnit>();
            hud.Snapshot.Squad.Add(member);
            hud.ApplySnapshot();
            Assert.That(hud.Squad.CardAt(0).Card.Unit, Is.SameAs(first.GetComponent<CommandableUnit>()));

            var afterFirst = HudFactory.TextWrites;
            member.Unit = second.GetComponent<CommandableUnit>();
            hud.Snapshot.Squad[0] = member;
            hud.ApplySnapshot();
            Assert.That(hud.Squad.CardAt(0).Card.Unit, Is.SameAs(second.GetComponent<CommandableUnit>()), "no stale unit for a click");
            Assert.That(HudFactory.TextWrites, Is.EqualTo(afterFirst), "the text did not change");
        }

        [Test]
        public void ACardHiddenBecauseTheSquadShrank_DropsItsUnit_AndStillWritesOnlyChangesWhenShownAgain()
        {
            Build();
            var units = new CommandableUnit[3];
            for (var i = 0; i < 3; i++)
            {
                var unitObject = new GameObject("unit" + i, typeof(CommandableUnit));
                created.Add(unitObject);
                units[i] = unitObject.GetComponent<CommandableUnit>();
                var member = Member("Operative " + i);
                member.Unit = units[i];
                hud.Snapshot.Squad.Add(member);
            }
            hud.ApplySnapshot();
            Assert.That(hud.Squad.CardAt(2).Card.Unit, Is.SameAs(units[2]), "precondition");

            hud.Snapshot.Squad.RemoveAt(2);   // the squad shrank (a regeneration with fewer operatives)
            hud.ApplySnapshot();
            var hidden = hud.Squad.CardAt(2);
            Assert.That(hidden.Root.gameObject.activeSelf, Is.False);
            Assert.That(ReferenceEquals(hidden.Card.Unit, null), Is.True, "the hidden card keeps no unit, destroyed or not");
            Assert.That(hidden.Card.Name, Is.EqualTo("Operative 2"), "its shown fields stay for the change-only writes");

            var member2 = Member("Operative 2");
            member2.Unit = units[2];
            hud.Snapshot.Squad.Add(member2);
            var before = HudFactory.TextWrites;
            hud.ApplySnapshot();
            Assert.That(hidden.Card.Unit, Is.SameAs(units[2]), "shown again, it takes the unit back");
            Assert.That(HudFactory.TextWrites, Is.EqualTo(before), "the same fields write no text");
        }

        static void AppendCard(StringBuilder text, HudSquadCard m)
        {
            text.Append(m.Initials).Append(m.Name).Append(RoleLine(m.Role, m.Rank)).Append(HudText.Health(m.Health, m.MaxHealth));
            if (m.IsControlled) text.Append("CONTROL");
            if (m.IsSelected) text.Append("SELECTED");
            if (!string.IsNullOrEmpty(m.Tag)) text.Append(m.Tag);
            if (m.IsDown) text.Append("DOWN");
        }

        [Test]
        public void ACard_IsAClickTarget_ThatKeepsItsUnit()
        {
            Build();
            var unitObject = new GameObject("unit", typeof(CommandableUnit));
            created.Add(unitObject);
            var unit = unitObject.GetComponent<CommandableUnit>();
            var member = Member("Alpha");
            member.Unit = unit;
            hud.Snapshot.Squad.Add(member);
            hud.ApplySnapshot();

            var card = hud.Squad.CardAt(0);
            var button = card.Root.GetComponent<Button>();
            Assert.That(button, Is.Not.Null);
            Assert.That(button.targetGraphic, Is.SameAs(card.Background));
            Assert.That(card.Background.raycastTarget, Is.True);
            Assert.That(card.Card.Unit, Is.SameAs(unit));
            var clicks = 0;
            card.Clicked += (u, doubleClick) => clicks++;
            hud.ApplySnapshot();
            Assert.That(clicks, Is.Zero, "applying a snapshot is not a click");
            foreach (var graphic in card.Root.GetComponentsInChildren<Graphic>(true))
            {
                if (graphic != card.Background)
                    Assert.That(graphic.raycastTarget, Is.False, $"{graphic.name} must not catch pointer raycasts");
            }
        }

        [Test]
        public void ApplyingTheSameSnapshotTwice_WritesNoText()
        {
            Build();
            var s = hud.Snapshot;
            var a = Member("Darius Vale", tag: "FOLLOWING"); a.IsControlled = true;
            var b = Member("Sable"); b.IsDown = true; b.Health = 0;
            for (var i = 0; i < 7; i++)
                s.Squad.Add(i == 0 ? a : i == 1 ? b : Member("Operative " + i));
            var before = HudFactory.TextWrites;
            hud.ApplySnapshot();
            Assert.That(HudFactory.TextWrites, Is.GreaterThan(before));

            var afterFirst = HudFactory.TextWrites;
            hud.ApplySnapshot();
            Assert.That(HudFactory.TextWrites, Is.EqualTo(afterFirst), "an unchanged snapshot writes no text");

            var changed = s.Squad[3];
            changed.Health = 10;
            s.Squad[3] = changed;
            hud.ApplySnapshot();
            Assert.That(HudFactory.TextWrites, Is.EqualTo(afterFirst + 1), "one changed health writes one text");
        }

        [Test]
        public void ReadingOrder_IsTopToBottom_AndTheGroupSitsOnTheZoneBottom()
        {
            Build();
            for (var i = 0; i < 3; i++)
                hud.Snapshot.Squad.Add(Member("Operative " + i));
            hud.ApplySnapshot();
            HudLayout.Rebuild(root);

            var zone = HudLayout.WorldRect(Panel("Squad"));
            var first = HudLayout.WorldRect(hud.Squad.CardAt(0).Root);
            var second = HudLayout.WorldRect(hud.Squad.CardAt(1).Root);
            var third = HudLayout.WorldRect(hud.Squad.CardAt(2).Root);
            Assert.That(first.yMin, Is.GreaterThan(second.yMin), "card 0 reads first, at the top");
            Assert.That(second.yMin, Is.GreaterThan(third.yMin));
            Assert.That(third.yMin, Is.EqualTo(zone.yMin).Within(0.5f), "the last card rests on the zone bottom");
            Assert.That(first.xMin, Is.EqualTo(zone.xMin).Within(0.5f));
        }

        [TestCase(1920, 1080, TestName = "Squad_Layout_At1920x1080_16x9")]
        [TestCase(1920, 1200, TestName = "Squad_Layout_At1920x1200_16x10")]
        [TestCase(2560, 1080, TestName = "Squad_Layout_At2560x1080_21x9")]
        public void SixFullCards_StayInsideTheZone_AndDoNotOverlap(int screenWidth, int screenHeight)
        {
            var units = HudLayout.CanvasUnits(screenWidth, screenHeight);
            Build(units.x, units.y);
            var s = hud.Snapshot;
            s.HasMission = true;
            s.PhaseText = "extraction open";
            for (var i = 0; i < 8; i++)
                s.Objectives.Add(new HudObjectiveRow { Kind = HudObjectiveKind.Active, Text = "Download the security logs at Terminal Kappa " + i + " (45%)" });
            s.IsPaused = true;
            s.ResumePrompt = "Options";
            s.BannerText = MissionHudText.Banner(MissionPhase.Success);
            s.Extraction = HudExtractionState.Active;
            s.ExtractionInside = 3;
            s.ExtractionRequired = 4;
            s.HasFollow = true;
            s.HasPause = true;
            for (var i = 0; i < 6; i++)
            {
                var m = Member("Operative Number " + i, "Support", 12, 100, 100, "FOLLOWING");
                m.IsControlled = true; m.IsSelected = true; m.IsDown = true;
                s.Squad.Add(m);
            }
            hud.ApplySnapshot();
            HudLayout.Rebuild(root);

            var squad = Panel("Squad");
            var cards = new RectTransform[6];
            for (var i = 0; i < 6; i++)
            {
                var view = hud.Squad.CardAt(i);
                cards[i] = view.Root;
                Assert.That(view.Root.gameObject.activeInHierarchy, Is.True);
                Assert.That(HudLayout.WorldRect(view.Root).size, Is.EqualTo(new Vector2(460f, 72f)).Using(Vector2Comparer));
                HudLayout.AssertInside(view.Root, view.InitialsLabel.rectTransform, view.NameLabel.rectTransform, view.RoleLabel.rectTransform,
                    view.HealthLabel.rectTransform, view.ControlLabel.rectTransform, view.SelectedLabel.rectTransform,
                    view.TagLabel.rectTransform, view.DownLabel.rectTransform, view.ControlMark, view.SelectedFrame, view.HealthBar);
                HudLayout.AssertNoOverlap(view.ControlLabel.rectTransform, view.SelectedLabel.rectTransform,
                    view.TagLabel.rectTransform, view.DownLabel.rectTransform);
                HudLayout.AssertNoOverlap(view.Portrait, view.NameLabel.rectTransform, view.HealthBar, view.ControlMark);
                HudLayout.AssertNoOverlap(view.NameLabel.rectTransform, view.RoleLabel.rectTransform, view.HealthBar);
                HudLayout.AssertNoOverlap(view.HealthLabel.rectTransform, view.ControlLabel.rectTransform);
                Assert.That(view.NameLabel.preferredHeight, Is.LessThanOrEqualTo(view.NameLabel.rectTransform.rect.height + 0.5f), "the name fits one line");
                Assert.That(view.RoleLabel.preferredHeight, Is.LessThanOrEqualTo(view.RoleLabel.rectTransform.rect.height + 0.5f), "the role line fits one line");
            }
            HudLayout.AssertInside(squad, cards);
            HudLayout.AssertNoOverlap(cards);
            HudLayout.AssertNoOverlap(Panel("Objectives"), hud.Status.PauseBanner, hud.Status.ResultBanner, hud.Status.RightBlock,
                squad, Panel("Operative"), Panel("Prompts"), Panel("Target"));
            Assert.That(HudLayout.WorldRect(squad).size, Is.EqualTo(new Vector2(460f, 6 * 72f + 5 * 8f)).Using(Vector2Comparer));
        }

        static readonly IEqualityComparer<Vector2> Vector2Comparer = new NearComparer();

        sealed class NearComparer : IEqualityComparer<Vector2>
        {
            public bool Equals(Vector2 a, Vector2 b) => Mathf.Abs(a.x - b.x) < 0.5f && Mathf.Abs(a.y - b.y) < 0.5f;
            public int GetHashCode(Vector2 v) => 0;
        }
    }
}
#endif
