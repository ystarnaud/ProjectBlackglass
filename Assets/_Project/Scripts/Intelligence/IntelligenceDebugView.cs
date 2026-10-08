using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Blackglass
{
    /// <summary>The truth-view text: what exists next to what the player knows. Pure strings, so tests can read it.</summary>
    public static class IntelligenceDebugText
    {
        public static string Describe(IntelligenceService service, IReadOnlyList<Health> hostiles, IReadOnlyList<MissionObjective> goals)
        {
            if (service == null || !service.HasMission)
                return "INTEL TRUTH VIEW | no mission";
            var text = new StringBuilder("INTEL TRUTH VIEW | ").Append(service.Settings.Describe());
            var map = service.Map;
            for (var region = 0; region < map.Count; region++)
            {
                var actual = 0;
                foreach (var hostile in hostiles)
                {
                    if (hostile != null && hostile.IsAlive && map.RegionAt(hostile.transform.position) == region)
                        actual++;
                }
                text.Append(FormattableString.Invariant($"\nR{region} {map[region].Kind} known={service.StateOfRegion(region)} hostiles(actual)={actual}"));
            }
            foreach (var hostile in hostiles)
            {
                if (hostile == null || !hostile.IsAlive)
                    continue;
                var p = hostile.transform.position;
                text.Append(FormattableString.Invariant($"\n{hostile.name}: {service.StateOfEnemy(hostile)} actual=({p.x:0.#}, {p.z:0.#})"));
                if (service.TryLastKnown(hostile, out var seen))
                    text.Append(FormattableString.Invariant($" lastKnown=({seen.x:0.#}, {seen.z:0.#})"));
            }
            foreach (var goal in goals)
                text.Append(FormattableString.Invariant($"\n{goal.Id}: {(goal.IsKnown ? "known" : "UNKNOWN")} {goal.State}"));
            var network = service.Network;
            if (network != null)
                text.Append(FormattableString.Invariant($"\nCameras: {(network.Compromised ? "hacked" : "intact")} x{network.Cameras.Count}"));
            text.Append(service.Pulses.Count == 0 ? "\nScans: none" : FormattableString.Invariant($"\nScans: {service.Pulses.Count}"));
            return text.ToString();
        }
    }

    /// <summary>Debug-only IMGUI: the truth-view text, drawn only while the developer truth view is on.</summary>
    public sealed class IntelligenceDebugView : MonoBehaviour
    {
        [SerializeField] IntelligenceService intelligence;
        [SerializeField] Encounter encounter;
        [SerializeField] MissionDirector director;

        GUIStyle style;

        void OnGUI()
        {
            if (intelligence == null || !intelligence.TruthView || encounter == null)
                return;
            style ??= new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true };
            var goals = director != null && director.Runtime != null ? (IReadOnlyList<MissionObjective>)director.Runtime.Objectives : Array.Empty<MissionObjective>();
            var text = IntelligenceDebugText.Describe(intelligence, encounter.Hostiles, goals);
            var area = new Rect(10f, 330f, 560f, Screen.height - 340f);
            GUI.color = Color.black;
            GUI.Label(new Rect(area.x + 1f, area.y + 1f, area.width, area.height), text, style);
            GUI.color = Color.white;
            GUI.Label(area, text, style);
        }
    }
}
