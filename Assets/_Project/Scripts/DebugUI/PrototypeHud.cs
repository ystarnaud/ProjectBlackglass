using System.Globalization;
using UnityEngine;
using UnityEngine.Serialization;

namespace Blackglass
{
    /// <summary>Debug-only on-screen text (IMGUI). Not production UI. Works while paused.</summary>
    public sealed class PrototypeHud : MonoBehaviour
    {
        const string ControlHints =
            "WASD: pan camera   Q/E: rotate   Right-drag: rotate/tilt   Wheel: zoom   V: takeover (WASD moves character)\n" +
            "Left-click unit: select (Shift: add/remove)   Left-drag: box select   Esc: clear selection\n" +
            "Left-click ground/enemy: selected units move/attack (nothing selected: controlled character)   Shift: queue   X: stop selected\n" +
            "Space: tactical pause   Tab / Shift+Tab: switch controlled character";
        // Unit labels float this far above a unit's centre (the capsule is 2 m tall).
        const float UnitLabelHeight = 1.5f;

        [SerializeField] TacticalPause tacticalPause;
        [SerializeField] Encounter encounter;
        [SerializeField] Camera viewCamera;
        [SerializeField] UnitSelection selection;
        [SerializeField] PlayerCommandInput commandInput;
        [SerializeField, FormerlySerializedAs("primary")] ActiveCharacter activeCharacter;

        GUIStyle pausedStyle;
        GUIStyle outcomeStyle;
        GUIStyle unitLabelStyle;

        internal bool IsEncounterWired => encounter != null;

        /// <summary>Short summary of a unit's orders, such as "Move +2". Empty when idle.</summary>
        internal static string DescribeOrders(UnitCommand current, int pendingCount)
        {
            if (current == null)
                return string.Empty;
            var orderName = current.GetType().Name;
            if (orderName.EndsWith("Command"))
                orderName = orderName.Substring(0, orderName.Length - "Command".Length);
            return pendingCount > 0 ? $"{orderName} +{pendingCount}" : orderName;
        }

        /// <summary>One status line for the active character, such as "Controlled: Ana | Takeover ON (V) | Manual control".</summary>
        internal static string DescribeActive(string unitName, bool takeoverOn, bool isPaused, bool hasOrders)
        {
            var mode = !takeoverOn ? "Takeover OFF (V)" : isPaused ? "Takeover ON (after pause)" : "Takeover ON (V)";
            var activity = hasOrders ? "Following orders" : takeoverOn && !isPaused ? "Manual control" : "Idle";
            return $"Controlled: {unitName} | {mode} | {activity}";
        }

        internal static string DescribeNoActive() => "Controlled: none";

        internal static string DescribeSides(int livingFriendlies, int friendlies, int livingHostiles, int hostiles) =>
            $"Friendlies alive {livingFriendlies}/{friendlies} | Hostiles alive {livingHostiles}/{hostiles}";

        internal static string DescribeUnit(string unitName, int current, int max) => $"{unitName} {current}/{max}";

        /// <summary>A hostile's line: its AI state, its target if any, and the cooldown while one runs.</summary>
        internal static string DescribeEnemy(EnemyState state, string targetName, float cooldownRemaining)
        {
            var text = string.IsNullOrEmpty(targetName) ? state.ToString() : $"{state} -> {targetName}";
            return AppendCooldown(text, cooldownRemaining);
        }

        internal static string AppendCooldown(string text, float cooldownRemaining)
        {
            if (cooldownRemaining <= 0f)
                return text;
            // Invariant culture: debug text and its tests must not depend on the OS locale.
            var cooldown = "CD " + cooldownRemaining.ToString("0.0", CultureInfo.InvariantCulture);
            return text.Length == 0 ? cooldown : $"{text} {cooldown}";
        }

        internal static string DescribeOutcome(EncounterOutcome outcome)
        {
            switch (outcome)
            {
                case EncounterOutcome.Victory:
                    return "VICTORY - all hostiles are down";
                case EncounterOutcome.Defeat:
                    return "DEFEAT - the squad is down";
                default:
                    return string.Empty;
            }
        }

        void OnGUI()
        {
            GUI.Label(new Rect(10f, 10f, 820f, 80f), ControlHints);

            if (encounter != null)
            {
                GUI.Label(new Rect(10f, 95f, 420f, 22f), DescribeSides(encounter.LivingFriendlies, encounter.Friendlies.Count,
                    encounter.LivingHostiles, encounter.Hostiles.Count));
            }

            if (selection != null)
                GUI.Label(new Rect(10f, 115f, 320f, 22f), $"Selected: {selection.Selected.Count}");

            if (activeCharacter != null)
            {
                var text = activeCharacter.HasUnit
                    ? DescribeActive(activeCharacter.Unit.name, activeCharacter.IsTakeoverOn, activeCharacter.IsPaused,
                        activeCharacter.Unit.CurrentCommand != null)
                    : DescribeNoActive();
                GUI.Label(new Rect(10f, 135f, 640f, 22f), text);
            }

            DrawUnitLabels();

            if (commandInput != null && commandInput.IsDragging)
                GUI.Box(ScreenBox.ToGuiRect(commandInput.DragRect, Screen.height), GUIContent.none);

            if (tacticalPause != null && tacticalPause.IsPaused)
            {
                pausedStyle ??= new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.UpperCenter,
                    fontSize = 22,
                    fontStyle = FontStyle.Bold,
                };
                GUI.Label(new Rect(0f, 165f, Screen.width, 40f), "TACTICAL PAUSE - Space to resume", pausedStyle);
            }

            if (encounter != null)
            {
                var outcome = DescribeOutcome(encounter.Outcome);
                if (outcome.Length > 0)
                {
                    outcomeStyle ??= new GUIStyle(GUI.skin.label)
                    {
                        alignment = TextAnchor.UpperCenter,
                        fontSize = 30,
                        fontStyle = FontStyle.Bold,
                    };
                    GUI.Label(new Rect(0f, 205f, Screen.width, 50f), outcome, outcomeStyle);
                }
            }
        }

        void DrawUnitLabels()
        {
            if (encounter == null || viewCamera == null)
                return;
            unitLabelStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter };
            foreach (var health in encounter.Friendlies)
                DrawUnitLabel(health, false);
            foreach (var health in encounter.Hostiles)
                DrawUnitLabel(health, true);
        }

        // Debug only: a handful of units, so per-frame GetComponent calls are fine here.
        void DrawUnitLabel(Health health, bool hostile)
        {
            if (health == null || !health.IsAlive || !health.gameObject.activeInHierarchy)
                return;
            var text = DescribeUnit(health.name, health.Current, health.Max);
            var cooldown = health.TryGetComponent<UnitAttacker>(out var attacker) ? attacker.CooldownRemaining : 0f;
            string activity;
            if (hostile && health.TryGetComponent<EnemyAI>(out var ai))
                activity = DescribeEnemy(ai.State, ai.Target != null ? ai.Target.name : null, cooldown);
            else if (health.TryGetComponent<CommandableUnit>(out var unit))
                activity = AppendCooldown(DescribeOrders(unit.CurrentCommand, unit.PendingCommands.Count), cooldown);
            else
                activity = string.Empty;
            if (activity.Length > 0)
                text += "\n" + activity;

            var screen = viewCamera.WorldToScreenPoint(health.transform.position + Vector3.up * UnitLabelHeight);
            if (screen.z <= 0f)
                return;
            GUI.Label(new Rect(screen.x - 80f, Screen.height - screen.y - 22f, 160f, 44f), text, unitLabelStyle);
        }
    }
}
