using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;

namespace Blackglass
{
    /// <summary>One upright obstacle of the public map: its kind, its tile footprint and how high it rises, in metres.</summary>
    public readonly struct NetMapBox
    {
        public NetMapBox(MissionBoxKind kind, RectInt footprint, float rise)
        {
            Kind = kind;
            Footprint = footprint;
            Rise = rise;
        }

        public MissionBoxKind Kind { get; }
        public RectInt Footprint { get; }
        public float Rise { get; }
    }

    /// <summary>
    /// The public map of one mission (decision 052): what a client may draw under map knowledge Full and nothing else. The grid
    /// size, the floor as non-overlapping tile rectangles, every wall and obstacle box, and the tiles of the closed containers.
    /// No seed, attempt number, room graph, spawn, region, hash or container content exists here. Immutable.
    /// </summary>
    public sealed class NetMap
    {
        public const byte Format = 1;
        public const int MaxSide = 128;
        public const int MaxFloors = 1024;
        public const int MaxBoxes = 2048;
        public const int MaxContainers = 64;
        public const float MaxRise = 4f;

        public NetMap(int width, int height, IList<RectInt> floors, IList<NetMapBox> boxes, IList<Vector2Int> containers)
        {
            Width = width;
            Height = height;
            Floors = new ReadOnlyCollection<RectInt>(new List<RectInt>(floors ?? throw new ArgumentNullException(nameof(floors))));
            Boxes = new ReadOnlyCollection<NetMapBox>(new List<NetMapBox>(boxes ?? throw new ArgumentNullException(nameof(boxes))));
            Containers = new ReadOnlyCollection<Vector2Int>(new List<Vector2Int>(containers ?? throw new ArgumentNullException(nameof(containers))));
        }

        public int Width { get; }
        public int Height { get; }
        public IReadOnlyList<RectInt> Floors { get; }
        public IReadOnlyList<NetMapBox> Boxes { get; }
        public IReadOnlyList<Vector2Int> Containers { get; }
    }

    /// <summary>The kind of a known objective on the wire; 0 is never valid.</summary>
    public enum NetObjectiveKind : byte
    {
        Eliminate = 1,
        Interact = 2,
        Extraction = 3,
    }

    /// <summary>
    /// One objective the player knows (decision 052): its kind, state and title (never the counted description), and for
    /// extraction only, the zone's centre and radius.
    /// </summary>
    public readonly struct NetObjective
    {
        public const int MaxTitle = 48;
        public const int MaxCount = 8;
        public const float MaxRadius = 10f;

        public NetObjective(NetObjectiveKind kind, ObjectiveState state, string title, bool hasPoint, Vector3 point, float radius)
        {
            Kind = kind;
            State = state;
            Title = title;
            HasPoint = hasPoint;
            Point = point;
            Radius = radius;
        }

        public NetObjectiveKind Kind { get; }
        public ObjectiveState State { get; }
        public string Title { get; }
        public bool HasPoint { get; }
        public Vector3 Point { get; }
        public float Radius { get; }
    }

    /// <summary>The mission's terminal result as a client learns it; None until the end message.</summary>
    public enum NetMissionResult : byte
    {
        None = 0,
        Success = 1,
        Failure = 2,
    }

    /// <summary>One resolved basic attack between two units the receiving connection already observes.</summary>
    public readonly struct NetShot
    {
        public NetShot(int attackerId, int targetId, bool hit)
        {
            AttackerId = attackerId;
            TargetId = targetId;
            Hit = hit;
        }

        public int AttackerId { get; }
        public int TargetId { get; }
        public bool Hit { get; }
    }
}
