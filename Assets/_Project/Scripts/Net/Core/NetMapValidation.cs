using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The checks a map and an objective list pass before the server sends them and after the client decodes them (decision
    /// 052). Counts are checked before any element, so the work is bounded by the limits whatever arrives. Null means valid;
    /// otherwise the first problem, which names no coordinate.
    /// </summary>
    public static class NetMapValidation
    {
        public static string Problem(NetMap map)
        {
            if (map == null)
                return "no map";
            if (map.Width < 1 || map.Height < 1 || map.Width > NetMap.MaxSide || map.Height > NetMap.MaxSide)
                return FormattableString.Invariant($"grid {map.Width}x{map.Height} outside 1..{NetMap.MaxSide}");
            if (map.Floors.Count < 1 || map.Floors.Count > NetMap.MaxFloors)
                return FormattableString.Invariant($"{map.Floors.Count} floors outside 1..{NetMap.MaxFloors}");
            if (map.Boxes.Count > NetMap.MaxBoxes)
                return FormattableString.Invariant($"{map.Boxes.Count} boxes above {NetMap.MaxBoxes}");
            if (map.Containers.Count > NetMap.MaxContainers)
                return FormattableString.Invariant($"{map.Containers.Count} containers above {NetMap.MaxContainers}");
            var floor = new bool[map.Width * map.Height];
            for (var i = 0; i < map.Floors.Count; i++)
            {
                var rect = map.Floors[i];
                if (!Inside(map, rect))
                    return FormattableString.Invariant($"floor {i} lies outside the grid");
                for (var y = rect.yMin; y < rect.yMax; y++)
                {
                    for (var x = rect.xMin; x < rect.xMax; x++)
                    {
                        if (floor[y * map.Width + x])
                            return FormattableString.Invariant($"floor {i} overlaps another");
                        floor[y * map.Width + x] = true;
                    }
                }
            }
            for (var i = 0; i < map.Boxes.Count; i++)
            {
                var box = map.Boxes[i];
                if (!Enum.IsDefined(typeof(MissionBoxKind), box.Kind))
                    return FormattableString.Invariant($"box {i} has an unknown kind");
                if (!Inside(map, box.Footprint))
                    return FormattableString.Invariant($"box {i} lies outside the grid");
                if (!(box.Rise > 0f && box.Rise <= NetMap.MaxRise))
                    return FormattableString.Invariant($"box {i} rise outside (0, {NetMap.MaxRise}]");
            }
            for (var i = 0; i < map.Containers.Count; i++)
            {
                var tile = map.Containers[i];
                if (tile.x < 0 || tile.y < 0 || tile.x >= map.Width || tile.y >= map.Height || !floor[tile.y * map.Width + tile.x])
                    return FormattableString.Invariant($"container {i} is not on a floor tile");
            }
            return null;
        }

        public static string Problem(IReadOnlyList<NetObjective> objectives)
        {
            if (objectives == null)
                return "no objective list";
            if (objectives.Count > NetObjective.MaxCount)
                return FormattableString.Invariant($"{objectives.Count} objectives above {NetObjective.MaxCount}");
            for (var i = 0; i < objectives.Count; i++)
            {
                var objective = objectives[i];
                if (!Enum.IsDefined(typeof(NetObjectiveKind), objective.Kind))
                    return FormattableString.Invariant($"objective {i} has an unknown kind");
                if (!Enum.IsDefined(typeof(ObjectiveState), objective.State))
                    return FormattableString.Invariant($"objective {i} has an unknown state");
                if (!Printable(objective.Title))
                    return FormattableString.Invariant($"objective {i} title is empty, longer than {NetObjective.MaxTitle} or unprintable");
                var extraction = objective.Kind == NetObjectiveKind.Extraction;
                if (objective.HasPoint != extraction)
                    return FormattableString.Invariant($"objective {i}: only extraction carries a point");
                if (!extraction)
                    continue;
                var p = objective.Point;
                if (!(Finite(p.x) && Finite(p.y) && Finite(p.z)) || Mathf.Abs(p.x) > NetMap.MaxSide || Mathf.Abs(p.z) > NetMap.MaxSide || Mathf.Abs(p.y) > 10f)
                    return FormattableString.Invariant($"objective {i} point is not finite or lies off the map");
                if (!(objective.Radius > 0f && objective.Radius <= NetObjective.MaxRadius))
                    return FormattableString.Invariant($"objective {i} radius outside (0, {NetObjective.MaxRadius}]");
            }
            return null;
        }

        static bool Inside(NetMap map, RectInt rect) =>
            rect.width >= 1 && rect.height >= 1 && rect.xMin >= 0 && rect.yMin >= 0 && rect.xMax <= map.Width && rect.yMax <= map.Height;

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        static bool Printable(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > NetObjective.MaxTitle)
                return false;
            foreach (var c in text)
            {
                if (char.IsControl(c))
                    return false;
            }
            return true;
        }
    }
}
