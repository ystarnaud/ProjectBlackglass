using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Debug-only operative inspector (IMGUI, works while paused; hidden until F9). Shows the controlled operative's name,
    /// role, short id, rank, XP, effective stats ("base -> effective"), abilities and picks, one button per advancement
    /// choice while a pick is pending, and a compact line for every operative in the roster. Not the final character
    /// sheet. All wording is built by pure internal static methods, which the tests cover.
    /// </summary>
    public sealed class OperativePanelView : MonoBehaviour
    {
        [SerializeField] SquadRoster roster;
        [SerializeField] ActiveCharacter activeCharacter;
        // Optional: tells the roster list whether an operative is deployed and alive.
        [SerializeField] MissionDirector director;

        bool visible;

        public bool IsVisible => visible;

        internal void Initialize(SquadRoster squad, ActiveCharacter active, MissionDirector missionDirector)
        {
            roster = squad;
            activeCharacter = active;
            director = missionDirector;
        }

        public void SetVisible(bool show) => visible = show;

        public void Toggle() => visible = !visible;

        internal static string ShortId(string id) =>
            string.IsNullOrEmpty(id) ? "-" : id.Length <= 8 ? id : id.Substring(0, 8);

        internal static string DescribeHeader(string displayName, string roleName, string id) =>
            $"{displayName} - {roleName} [{ShortId(id)}]";

        internal static string DescribeProgress(int rank, int maxRank, int experience, int? nextThreshold) =>
            nextThreshold.HasValue
                ? $"Rank {rank}/{maxRank} | XP {experience}/{nextThreshold.Value}"
                : $"Rank {rank}/{maxRank} (max) | XP {experience}";

        internal static string DescribeStat(string label, string baseValue, string effectiveValue) =>
            baseValue == effectiveValue ? $"{label}: {effectiveValue}" : $"{label}: {baseValue} -> {effectiveValue}";

        internal static string DescribePicks(IReadOnlyList<string> pickNames, int pending)
        {
            var picked = pickNames.Count == 0 ? "none" : string.Join(", ", pickNames);
            return pending > 0 ? $"Picks: {picked} | PICK AVAILABLE x{pending}" : $"Picks: {picked}";
        }

        internal static string DescribeStatus(bool deployed, bool alive) => !deployed ? "not deployed" : alive ? "alive" : "dead";

        internal static string DescribeRosterRow(string displayName, string roleName, int rank, int experience, string status) =>
            $"{displayName} ({roleName}) rank {rank} XP {experience} {status}";

        static string Number(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        void OnGUI()
        {
            if (!visible || roster == null || roster.Track == null)
                return;
            string pickedChoiceId = null;
            GUILayout.BeginArea(new Rect(10f, 200f, 400f, Screen.height - 220f), GUI.skin.box);
            GUILayout.Label("OPERATIVES  (F9 hide | F10 +XP | F11 reset progression)");
            var member = ControlledMember();
            if (member == null)
                GUILayout.Label("Controlled: none");
            else
                pickedChoiceId = DrawDetail(member);
            GUILayout.Space(8f);
            foreach (var other in roster.Members)
            {
                var deployed = TryFindUnit(other.Id, out var unit);
                var alive = deployed && unit.TryGetComponent<Health>(out var health) && health.IsAlive;
                GUILayout.Label(DescribeRosterRow(other.Definition.DisplayName, RoleName(other.Definition), roster.Rank(other),
                    other.State.Experience, DescribeStatus(deployed, alive)));
            }
            GUILayout.EndArea();

            // Applied after the layout pass, so the number of controls cannot change in the middle of it.
            if (pickedChoiceId != null && member != null)
                roster.TryPickChoice(member.Id, pickedChoiceId);
        }

        // Returns the id of the choice whose button was clicked this frame, or null.
        string DrawDetail(RosterMember member)
        {
            var definition = member.Definition;
            var state = member.State;
            var track = roster.Track;
            var rank = roster.Rank(member);
            var pending = roster.PendingPicks(member);
            var baseConfig = EffectiveConfiguration.Base(definition);
            var now = roster.Evaluate(member);

            GUILayout.Label(DescribeHeader(definition.DisplayName, RoleName(definition), state.OperativeId));
            GUILayout.Label(DescribeProgress(rank, track.MaxRank, state.Experience,
                track.TryGetNextThreshold(state.Experience, out var next) ? next : (int?)null));
            GUILayout.Label(DescribeStat("Max health", baseConfig.MaxHealth.ToString(CultureInfo.InvariantCulture), now.MaxHealth.ToString(CultureInfo.InvariantCulture)));
            GUILayout.Label(DescribeStat("Move speed", Number(baseConfig.MoveSpeed), Number(now.MoveSpeed)));
            GUILayout.Label($"Weapon: {definition.Archetype.DisplayName} ({now.AttackRole})");
            GUILayout.Label(DescribeStat("Attack damage", baseConfig.AttackDamage.ToString(CultureInfo.InvariantCulture), now.AttackDamage.ToString(CultureInfo.InvariantCulture)));
            GUILayout.Label(DescribeStat("Attack range", Number(baseConfig.AttackRange), Number(now.AttackRange)));
            GUILayout.Label(DescribeStat("Attack interval", Number(baseConfig.AttackInterval), Number(now.AttackInterval)));
            GUILayout.Label(DescribeStat("Ability power", "x" + Number(baseConfig.AbilityPower), "x" + Number(now.AbilityPower)));
            GUILayout.Label(DescribeStat("Ability cooldown", "x" + Number(baseConfig.AbilityCooldownMultiplier), "x" + Number(now.AbilityCooldownMultiplier)));

            var abilityNames = new List<string>();
            foreach (var ability in definition.Abilities)
                abilityNames.Add(ability.DisplayName);
            GUILayout.Label("Abilities: " + (abilityNames.Count == 0 ? "none" : string.Join(", ", abilityNames)));

            var pickNames = new List<string>();
            foreach (var id in state.ChoiceIds)
            {
                var choice = track.FindChoice(id);
                pickNames.Add(choice != null ? choice.DisplayName : id);
            }
            GUILayout.Label(DescribePicks(pickNames, pending));

            string clicked = null;
            if (pending > 0)
            {
                foreach (var choice in track.Choices)
                {
                    if (GUILayout.Button($"{choice.DisplayName}: {choice.Description}") && clicked == null)
                        clicked = choice.Id;
                }
            }
            return clicked;
        }

        RosterMember ControlledMember()
        {
            if (activeCharacter == null || activeCharacter.Unit == null)
                return null;
            return activeCharacter.Unit.TryGetComponent<UnitIdentity>(out var identity) ? roster.Find(identity.OperativeId) : null;
        }

        bool TryFindUnit(string operativeId, out CommandableUnit unit)
        {
            unit = null;
            if (director == null)
                return false;
            foreach (var candidate in director.Friendlies)
            {
                if (candidate != null && candidate.TryGetComponent<UnitIdentity>(out var identity) && identity.OperativeId == operativeId)
                {
                    unit = candidate;
                    return true;
                }
            }
            return false;
        }

        static string RoleName(OperativeDefinition definition) => definition.Role != null ? definition.Role.DisplayName : "-";
    }
}
