using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Bottom left: the squad as a column of up to six cards, in snapshot order. The cards read top to bottom (card 0 on
    /// top) and the group rests on the bottom of the zone, so a smaller squad leaves its free space above, not below. The
    /// zone is a fixed footprint (6 cards, 5 gaps) that later panels are laid out around; entries beyond six are dropped.
    /// </summary>
    internal sealed class SquadPanel : HudPanel
    {
        public const int MaxCards = 6;
        public const float CardGap = 8f;
        public static readonly Vector2 Size = new Vector2(SquadCardView.Width, MaxCards * SquadCardView.Height + (MaxCards - 1) * CardGap);

        readonly SquadCardView[] cards = new SquadCardView[MaxCards];
        int shownCount = -1;

        public SquadPanel(Transform parent) : base(HudFactory.Rect("Squad", parent))
        {
            HudFactory.Place(Root, new Vector2(0f, 0f), new Vector2(HudTheme.Margin, HudTheme.Margin), Size);
            for (var i = 0; i < MaxCards; i++)
            {
                cards[i] = new SquadCardView(Root, "Card" + i);
                cards[i].Root.gameObject.SetActive(false);
            }
        }

        internal int CardCapacity => MaxCards;
        internal SquadCardView CardAt(int i) => cards[i];

        public override void Apply(HudSnapshot s)
        {
            var count = Mathf.Min(s.Squad.Count, MaxCards);
            if (count != shownCount)
            {
                shownCount = count;
                Arrange(count);
            }
            for (var i = 0; i < MaxCards; i++)
            {
                var visible = i < count;
                HudFactory.SetActive(cards[i].Root.gameObject, visible);
                if (visible)
                    cards[i].Apply(s.Squad[i]);
            }
        }

        /// <summary>Card i sits (count - 1 - i) slots above the zone bottom, so card 0 is the top one of the group.</summary>
        void Arrange(int count)
        {
            for (var i = 0; i < MaxCards; i++)
            {
                var slotsAbove = Mathf.Max(count - 1 - i, 0);
                HudFactory.Place(cards[i].Root, new Vector2(0f, 0f), new Vector2(0f, slotsAbove * (SquadCardView.Height + CardGap)),
                    new Vector2(SquadCardView.Width, SquadCardView.Height));
            }
        }
    }
}
