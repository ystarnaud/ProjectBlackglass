using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Conversion between the briefing's core types and the wire (decision 052). Every message carries NetMap.Format. Decoding
    /// never throws: another format, or a missing, oversized, unknown or invalid payload decodes to nothing with a reason, so a
    /// client draws nothing from it.
    /// </summary>
    public static class NetMapWire
    {
        public static NetMapMessage ToMessage(NetMap map)
        {
            var floors = new NetRectWire[map.Floors.Count];
            for (var i = 0; i < floors.Length; i++)
            {
                var r = map.Floors[i];
                floors[i] = new NetRectWire { X = (short)r.x, Y = (short)r.y, Width = (short)r.width, Height = (short)r.height };
            }
            var boxes = new NetBoxWire[map.Boxes.Count];
            for (var i = 0; i < boxes.Length; i++)
            {
                var b = map.Boxes[i];
                boxes[i] = new NetBoxWire
                {
                    Kind = (byte)((int)b.Kind + 1), X = (short)b.Footprint.x, Y = (short)b.Footprint.y,
                    Width = (short)b.Footprint.width, Height = (short)b.Footprint.height, Rise = b.Rise,
                };
            }
            var containers = new NetTileWire[map.Containers.Count];
            for (var i = 0; i < containers.Length; i++)
                containers[i] = new NetTileWire { X = (short)map.Containers[i].x, Y = (short)map.Containers[i].y };
            return new NetMapMessage { Format = NetMap.Format, Width = (short)map.Width, Height = (short)map.Height, Floors = floors, Boxes = boxes, Containers = containers };
        }

        public static NetMap FromMessage(NetMapMessage message, out string problem)
        {
            try
            {
                if (message.Format != NetMap.Format)
                {
                    problem = "map format " + message.Format + " is not " + NetMap.Format;
                    return null;
                }
                if (message.Floors == null || message.Boxes == null || message.Containers == null)
                {
                    problem = "map arrays missing";
                    return null;
                }
                if (message.Floors.Length > NetMap.MaxFloors || message.Boxes.Length > NetMap.MaxBoxes || message.Containers.Length > NetMap.MaxContainers)
                {
                    problem = "map too large";
                    return null;
                }
                var floors = new List<RectInt>(message.Floors.Length);
                foreach (var f in message.Floors)
                    floors.Add(new RectInt(f.X, f.Y, f.Width, f.Height));
                var boxes = new List<NetMapBox>(message.Boxes.Length);
                for (var i = 0; i < message.Boxes.Length; i++)
                {
                    var b = message.Boxes[i];
                    if (b.Kind < 1 || b.Kind > 5)
                    {
                        problem = "box " + i + " has an unknown kind";
                        return null;
                    }
                    boxes.Add(new NetMapBox((MissionBoxKind)(b.Kind - 1), new RectInt(b.X, b.Y, b.Width, b.Height), b.Rise));
                }
                var containers = new List<Vector2Int>(message.Containers.Length);
                foreach (var c in message.Containers)
                    containers.Add(new Vector2Int(c.X, c.Y));
                var map = new NetMap(message.Width, message.Height, floors, boxes, containers);
                problem = NetMapValidation.Problem(map);
                return problem == null ? map : null;
            }
            catch (Exception exception)
            {
                problem = "map unreadable: " + exception.GetType().Name;
                return null;
            }
        }

        public static NetObjectivesMessage ToMessage(IReadOnlyList<NetObjective> objectives)
        {
            var wire = new NetObjectiveWire[objectives.Count];
            for (var i = 0; i < wire.Length; i++)
            {
                var o = objectives[i];
                wire[i] = new NetObjectiveWire { Kind = (byte)o.Kind, State = (byte)o.State, Title = o.Title, HasPoint = o.HasPoint, Point = o.Point, Radius = o.Radius };
            }
            return new NetObjectivesMessage { Format = NetMap.Format, Objectives = wire };
        }

        public static List<NetObjective> FromMessage(NetObjectivesMessage message, out string problem)
        {
            try
            {
                if (message.Format != NetMap.Format)
                {
                    problem = "objectives format " + message.Format + " is not " + NetMap.Format;
                    return null;
                }
                if (message.Objectives == null)
                {
                    problem = "objective array missing";
                    return null;
                }
                if (message.Objectives.Length > NetObjective.MaxCount)
                {
                    problem = "objectives too many";
                    return null;
                }
                var list = new List<NetObjective>(message.Objectives.Length);
                foreach (var o in message.Objectives)
                    list.Add(new NetObjective((NetObjectiveKind)o.Kind, (ObjectiveState)o.State, o.Title, o.HasPoint, o.Point, o.Radius));
                problem = NetMapValidation.Problem(list);
                return problem == null ? list : null;
            }
            catch (Exception exception)
            {
                problem = "objectives unreadable: " + exception.GetType().Name;
                return null;
            }
        }

        public static NetMissionEndMessage ToMessage(NetMissionResult result) =>
            new NetMissionEndMessage { Format = NetMap.Format, Result = (byte)result };

        /// <summary>Success or Failure; None for another format or any other value.</summary>
        public static NetMissionResult FromMessage(NetMissionEndMessage message) =>
            message.Format == NetMap.Format && (message.Result == (byte)NetMissionResult.Success || message.Result == (byte)NetMissionResult.Failure)
                ? (NetMissionResult)message.Result
                : NetMissionResult.None;

        public static NetShotMessage ToMessage(NetShot shot) =>
            new NetShotMessage { Format = NetMap.Format, AttackerId = shot.AttackerId, TargetId = shot.TargetId, Hit = shot.Hit };

        /// <summary>False for another format or a unit id that is not positive.</summary>
        public static bool FromMessage(NetShotMessage message, out NetShot shot)
        {
            shot = new NetShot(message.AttackerId, message.TargetId, message.Hit);
            return message.Format == NetMap.Format && message.AttackerId > 0 && message.TargetId > 0;
        }
    }
}
