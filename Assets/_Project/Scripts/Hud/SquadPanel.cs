using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Bottom left: the squad as a column of up to six cards, in snapshot order. The cards read top to bottom (card 0 on
    /// top) and the group rests on the bottom of the zone, so a smaller squad leaves its free space above, not below. The
    /// zone is a fixed footprint (6 cards, 5 gaps) that later panels are laid out around; entries beyond six are dropped.
    /// Cards is a non-drawing rect over the shown cards and the gaps between them (inactive without cards): it is what blocks
    /// world clicks, so the free space above a short squad does not.
    /// </summary>
    internal sealed class SquadPanel : HudPanel
    {
        public const int MaxCards = 6;
        public const float CardGap = 8f;
        public static readonly Vector2 Size = new Vector2(SquadCardView.Width, MaxCards * SquadCardView.Height + (MaxCards - 1) * CardGap);

        readonly SquadCardView[] cards = new SquadCardView[MaxCards];
        readonly RectTransform footprint;
        int shownCount = -1;

        public SquadPanel(Transform parent) : base(HudFactory.Rect("Squad", parent))
        {
            HudFactory.Place(Root, new Vector2(0f, 0f), new Vector2(HudTheme.Margin, HudTheme.Margin), Size);
            footprint = HudFactory.Rect("Cards", Root);
            footprint.gameObject.SetActive(false);
            for (var i = 0; i < MaxCards; i++)
            {
                cards[i] = new SquadCardView(Root, "Card" + i);
                cards[i].Root.gameObject.SetActive(false);
            }
        }

        internal int CardCapacity => MaxCards;
        internal SquadCardView CardAt(int i) => cards[i];
        /// <summary>The shown cards' bounds, gaps included; inactive without cards.</summary>
        internal RectTransform Footprint => footprint;

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
                else
                    cards[i].ReleaseUnit();   // a hidden card keeps no (possibly destroyed) unit
            }
        }

        /// <summary>Card i sits (count - 1 - i) slots above the zone bottom, so card 0 is the top one of the group.</summary>
        void Arrange(int count)
        {
            HudFactory.SetActive(footprint.gameObject, count > 0);
            if (count > 0)
                HudFactory.Place(footprint, new Vector2(0f, 0f), Vector2.zero,
                    new Vector2(SquadCardView.Width, count * SquadCardView.Height + (count - 1) * CardGap));
            for (var i = 0; i < MaxCards; i++)
            {
                var slotsAbove = Mathf.Max(count - 1 - i, 0);
                HudFactory.Place(cards[i].Root, new Vector2(0f, 0f), new Vector2(0f, slotsAbove * (SquadCardView.Height + CardGap)),
                    new Vector2(SquadCardView.Width, SquadCardView.Height));
            }
        }
    }
}
