using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>Plain tuning data for cover discovery. Defaults are the values the prototype arena was tuned with.</summary>
    [Serializable]
    public sealed class CoverGenerationSettings
    {
        /// <summary>Stand point distance from a face (agent radius 0.5 m plus margin).</summary>
        public float standOffset = 0.75f;
        /// <summary>
        /// Minimum gap between neighbouring face points along a face. Points are spread evenly, so every gap is under
        /// twice this: no gap can fit another unit one metre wide. Values under a unit's width (1 m) are treated as 1 m,
        /// so two points of one face can always both hold a unit.
        /// </summary>
        public float spacing = 1f;
        /// <summary>Keep face points this far from a face's ends.</summary>
        public float endMargin = 0.5f;
        /// <summary>Faces of a Low box shorter than this get no points.</summary>
        public float minFaceLength = 1f;
        /// <summary>A box whose top is at or below this is Low cover, else Tall.</summary>
        public float lowMaxHeight = 1.2f;
        /// <summary>A Tall box gets corner cover only if its horizontal length is at least this and twice its thickness.</summary>
        public float minCornerLength = 2f;
        /// <summary>How far inside the wall end a corner stands, so it stays in the wall's shadow.</summary>
        public float cornerInset = 0.35f;
        /// <summary>Stand point to peek point.</summary>
        public float peekDistance = 1.25f;
        /// <summary>
        /// A face of a Tall box no wider than this is a column face: it gets one location centred in front of it (every
        /// face of a unit-wide pillar, the end caps of a thin wall). A unit's width (1 m) plus a tolerance.
        /// </summary>
        public float columnMaxWidth = 1.1f;
        /// <summary>Copied to every generated location.</summary>
        public float hitChance = 0.5f;
        /// <summary>NavMesh.SamplePosition radius CoverDiscovery uses to decide a point is walkable.</summary>
        public float walkableTolerance = 0.25f;
    }

    /// <summary>An upright box, any yaw: the only geometry the generator understands.</summary>
    public readonly struct CoverBox
    {
        public CoverBox(string name, Vector3 center, float yawDegrees, Vector3 halfExtents, Collider collider)
        {
            Name = name;
            Center = center;
            YawDegrees = yawDegrees;
            HalfExtents = halfExtents;
            Collider = collider;
        }

        public string Name { get; }
        public Vector3 Center { get; }
        public float YawDegrees { get; }
        /// <summary>Half sizes along the box's own x, y and z (z is "forward" at yaw 0).</summary>
        public Vector3 HalfExtents { get; }
        /// <summary>The collider that will protect locations built from this box. May be null in pure tests.</summary>
        public Collider Collider { get; }

        public float Height => HalfExtents.y * 2f;
        public float GroundY => Center.y - HalfExtents.y;
    }

    /// <summary>
    /// Turns boxes into cover locations. Pure: no scene access, the NavMesh arrives as a predicate. Cover exists only
    /// where it covers, and every location belongs to the box that provides it. Low boxes (top at or below
    /// <see cref="CoverGenerationSettings.lowMaxHeight"/>) get face locations along every usable face: a unit crouches
    /// behind them anywhere. Tall boxes get no face locations along a face wider than a unit: a unit cannot hide against
    /// the middle of a wall. A long tall wall offers corners at its ends, only where the end opens outward (the peek point
    /// past the end is walkable); an end that runs into another wall, a closed room corner or the map edge gets no corner.
    /// Every tall face no wider than a unit (<see cref="CoverGenerationSettings.columnMaxWidth"/>) is a column: one
    /// location centred in front of it (the four faces of a unit-wide pillar, the end caps of a thin wall, standing on the
    /// wall's axis beyond its end). A wall's end cap exists only where a unit can step round the end on both sides (the
    /// stand points beside both long faces at that end are walkable): a room-frame wall end, with floor on one side only,
    /// gets its corner point beside the wall and no end cap (decision 031). The fit test is the only filter: a candidate is kept if its stand point is walkable,
    /// i.e. on the NavMesh, which is eroded by the unit's radius, so a kept point always fits a unit however close it is to
    /// other geometry. Only exact duplicates of one box (closer than <see cref="DuplicateDistance"/>) collapse to the first;
    /// locations of different boxes never remove each other. The survivors are named and returned.
    /// </summary>
    public static class CoverGenerator
    {
        /// <summary>Two candidates of one box closer than this (flat) are the same point: the first is kept.</summary>
        public const float DuplicateDistance = 0.1f;

        /// <summary>A unit's width (the NavMesh agent radius is 0.5 m): the smallest spacing between face points.</summary>
        const float MinSpacing = 1f;

        struct Candidate
        {
            public Vector3 Position;
            public Vector3 Facing;
            public CoverPlacement Placement;
            public Vector3 PeekDirection;
            public Vector3 PeekPoint;
        }

        // A face of the box: outward normal, direction along the face, half the face's length, and the distance from
        // the box centre to the face plane.
        readonly struct Face
        {
            public Face(Vector3 normal, Vector3 tangent, float halfLength, float depth)
            {
                Normal = normal;
                Tangent = tangent;
                HalfLength = halfLength;
                Depth = depth;
            }

            public Vector3 Normal { get; }
            public Vector3 Tangent { get; }
            public float HalfLength { get; }
            public float Depth { get; }
        }

        public static List<CoverLocation> Generate(IReadOnlyList<CoverBox> boxes, CoverGenerationSettings settings,
            Func<Vector3, bool> isWalkable)
        {
            if (boxes == null)
                throw new ArgumentNullException(nameof(boxes));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            if (isWalkable == null)
                throw new ArgumentNullException(nameof(isWalkable));

            var result = new List<CoverLocation>();
            foreach (var box in boxes)
            {
                var candidates = new List<Candidate>();
                AddCorners(box, settings, isWalkable, candidates);   // Tall walls only
                AddColumns(box, settings, isWalkable, candidates);             // Tall boxes only
                AddFaces(box, settings, candidates);                 // Low boxes only

                var accepted = new List<Candidate>();
                foreach (var candidate in candidates)
                {
                    if (!isWalkable(candidate.Position) || IsNearAny(candidate.Position, accepted, DuplicateDistance))
                        continue;
                    accepted.Add(candidate);
                }
                AddNamed(box, settings, accepted, result);
            }
            return result;
        }

        static Face[] FacesOf(CoverBox box)
        {
            var rotation = Quaternion.Euler(0f, box.YawDegrees, 0f);
            var right = rotation * Vector3.right;
            var forward = rotation * Vector3.forward;
            var hx = box.HalfExtents.x;
            var hz = box.HalfExtents.z;
            return new[]
            {
                new Face(right, forward, hz, hx),
                new Face(-right, forward, hz, hx),
                new Face(forward, right, hx, hz),
                new Face(-forward, right, hx, hz),
            };
        }

        static Vector3 OnGround(CoverBox box, Vector3 point) => new Vector3(point.x, box.GroundY, point.z);

        // Low boxes only: a tall box gets no face locations (see the class summary).
        static void AddFaces(CoverBox box, CoverGenerationSettings settings, List<Candidate> candidates)
        {
            if (box.Height > settings.lowMaxHeight)
                return;
            foreach (var face in FacesOf(box))
            {
                var length = face.HalfLength * 2f;
                if (length < settings.minFaceLength)
                    continue;
                var usable = length - 2f * settings.endMargin;
                var count = usable <= 0f ? 1 : Mathf.FloorToInt(usable / Mathf.Max(settings.spacing, MinSpacing) + 1e-4f) + 1;
                for (var i = 0; i < count; i++)
                {
                    var along = count == 1 ? 0f : -usable * 0.5f + i * usable / (count - 1);
                    var point = box.Center + face.Normal * (face.Depth + settings.standOffset) + face.Tangent * along;
                    candidates.Add(new Candidate
                    {
                        Position = OnGround(box, point),
                        Facing = -face.Normal,
                        Placement = CoverPlacement.Face,
                    });
                }
            }
        }

        // Tall boxes only: every face no wider than a unit gets one location, centred, standOffset in front of the face,
        // facing into it. No peek data. A stand point inside other geometry is dropped later by the walkability test.
        // A pillar (both horizontal sizes within columnMaxWidth) has four open faces and keeps all four. For a wall, only
        // its narrow end faces qualify, and an end cap is emitted only where a unit can step round the end on both sides:
        // the stand points of both sibling corner candidates of that end (one per long face) must be walkable (031). At a
        // room-frame wall end the other side is solid or void, so no end cap: the corner point beside the wall serves.
        static void AddColumns(CoverBox box, CoverGenerationSettings settings, Func<Vector3, bool> isWalkable, List<Candidate> candidates)
        {
            if (box.Height <= settings.lowMaxHeight)
                return;
            var limit = settings.columnMaxWidth + 1e-4f;
            var pillar = box.HalfExtents.x * 2f <= limit && box.HalfExtents.z * 2f <= limit;
            foreach (var face in FacesOf(box))
            {
                if (face.HalfLength * 2f > limit)
                    continue;
                if (!pillar && !CanGoRoundEnd(box, face, settings, isWalkable))
                    continue;
                var point = box.Center + face.Normal * (face.Depth + settings.standOffset);
                candidates.Add(new Candidate
                {
                    Position = OnGround(box, point),
                    Facing = -face.Normal,
                    Placement = CoverPlacement.Column,
                });
            }
        }

        // True if the stand points where the corner candidates of this end would stand (beside each long face, cornerInset
        // back from the end) are both walkable. Peek points are not required: only that a unit can stand on both sides.
        static bool CanGoRoundEnd(CoverBox box, Face endFace, CoverGenerationSettings settings, Func<Vector3, bool> isWalkable)
        {
            var end = box.Center + endFace.Normal * (endFace.Depth - settings.cornerInset);
            var side = endFace.HalfLength + settings.standOffset;
            return isWalkable(OnGround(box, end + endFace.Tangent * side))
                && isWalkable(OnGround(box, end - endFace.Tangent * side));
        }

        // Tall walls only: one corner at each end of each long face, inset into the wall's shadow, peeking out past the
        // end. An end whose peek point is not walkable does not open outward (a closed corner, a junction with another
        // wall, the map edge) and gets no candidate.
        static void AddCorners(CoverBox box, CoverGenerationSettings settings, Func<Vector3, bool> isWalkable, List<Candidate> candidates)
        {
            if (box.Height <= settings.lowMaxHeight)
                return;
            var longHalf = Mathf.Max(box.HalfExtents.x, box.HalfExtents.z);
            var shortHalf = Mathf.Min(box.HalfExtents.x, box.HalfExtents.z);
            var length = longHalf * 2f;
            if (length < settings.minCornerLength || length < shortHalf * 4f)
                return;
            foreach (var face in FacesOf(box))
            {
                // The long faces are the ones that run the whole length of the box.
                if (!Mathf.Approximately(face.HalfLength, longHalf))
                    continue;
                for (var end = 0; end < 2; end++)
                {
                    var along = face.Tangent * (end == 0 ? 1f : -1f);
                    var stand = OnGround(box, box.Center + face.Normal * (face.Depth + settings.standOffset)
                        + along * (face.HalfLength - settings.cornerInset));
                    var peek = stand + along * settings.peekDistance;
                    if (!isWalkable(peek))
                        continue;
                    candidates.Add(new Candidate
                    {
                        Position = stand,
                        Facing = -face.Normal,
                        Placement = CoverPlacement.Corner,
                        PeekDirection = along,
                        PeekPoint = peek,
                    });
                }
            }
        }

        static bool IsNearAny(Vector3 position, List<Candidate> candidates, float distance)
        {
            foreach (var candidate in candidates)
            {
                if (CoverRules.FlatDistance(position, candidate.Position) < distance)
                    return true;
            }
            return false;
        }

        // The compass side a location stands on, from the outward normal (-Facing). Ties go to the z axis.
        static string Label(Vector3 outward)
        {
            if (Mathf.Abs(outward.x) > Mathf.Abs(outward.z) + 1e-4f)
                return outward.x > 0f ? "E" : "W";
            return outward.z >= 0f ? "N" : "S";
        }

        // Numbering runs along world +x on N/S sides and +z on E/W sides.
        static (float primary, float secondary) AlongKey(string label, Vector3 position) =>
            label == "N" || label == "S" ? (position.x, position.z) : (position.z, position.x);

        static int Compare(Candidate a, Candidate b)
        {
            var byPlacement = a.Placement.CompareTo(b.Placement);
            if (byPlacement != 0)
                return byPlacement;
            var la = Label(-a.Facing);
            var lb = Label(-b.Facing);
            var byLabel = string.CompareOrdinal(la, lb);
            if (byLabel != 0)
                return byLabel;
            var ka = AlongKey(la, a.Position);
            var kb = AlongKey(lb, b.Position);
            var byPrimary = ka.primary.CompareTo(kb.primary);
            return byPrimary != 0 ? byPrimary : ka.secondary.CompareTo(kb.secondary);
        }

        static void AddNamed(CoverBox box, CoverGenerationSettings settings, List<Candidate> accepted, List<CoverLocation> result)
        {
            var height = box.Height <= settings.lowMaxHeight ? CoverHeight.Low : CoverHeight.Tall;
            accepted.Sort(Compare);
            var counts = new Dictionary<string, int>();
            foreach (var candidate in accepted)
            {
                var label = Label(-candidate.Facing);
                var kind = candidate.Placement == CoverPlacement.Corner ? "Corner"
                    : candidate.Placement == CoverPlacement.Column ? "Column"
                    : string.Empty;
                var key = label + kind;
                counts.TryGetValue(key, out var number);
                number++;
                counts[key] = number;
                result.Add(new CoverLocation($"Cover_{box.Name}_{key}{number}", candidate.Position, candidate.Facing,
                    box.Collider, settings.hitChance, height, candidate.Placement, candidate.PeekDirection, candidate.PeekPoint));
            }
        }
    }
}
