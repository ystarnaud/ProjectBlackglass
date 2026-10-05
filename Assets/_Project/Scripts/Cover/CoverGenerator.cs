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
        /// <summary>Target gap between face points along a face.</summary>
        public float spacing = 2f;
        /// <summary>Keep face points this far from a face's ends.</summary>
        public float endMargin = 0.5f;
        /// <summary>Faces shorter than this get no points (a wall's thin ends).</summary>
        public float minFaceLength = 1f;
        /// <summary>A box whose top is at or below this is Low cover, else Tall.</summary>
        public float lowMaxHeight = 1.2f;
        /// <summary>A Tall box gets corner cover only if its horizontal length is at least this and twice its thickness.</summary>
        public float minCornerLength = 2f;
        /// <summary>How far inside the wall end a corner stands, so it stays in the wall's shadow.</summary>
        public float cornerInset = 0.35f;
        /// <summary>Stand point to peek point.</summary>
        public float peekDistance = 1.25f;
        /// <summary>A location this close (flat) to an earlier accepted one is dropped.</summary>
        public float mergeDistance = 1f;
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
    /// Turns boxes into cover locations. Pure: no scene access, the NavMesh arrives as a predicate. Per box, corner
    /// candidates come first (so they win a merge), then face candidates; candidates that are not walkable or lie
    /// within mergeDistance of an earlier accepted location are dropped; the survivors are named and returned.
    /// </summary>
    public static class CoverGenerator
    {
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
                AddFaces(box, settings, candidates);

                var accepted = new List<Candidate>();
                foreach (var candidate in candidates)
                {
                    if (!isWalkable(candidate.Position) || IsNearAny(candidate.Position, result, accepted, settings.mergeDistance))
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

        static void AddFaces(CoverBox box, CoverGenerationSettings settings, List<Candidate> candidates)
        {
            foreach (var face in FacesOf(box))
            {
                var length = face.HalfLength * 2f;
                if (length < settings.minFaceLength)
                    continue;
                var usable = length - 2f * settings.endMargin;
                var count = usable <= 0f ? 1 : Mathf.FloorToInt(usable / settings.spacing + 1e-4f) + 1;
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

        static bool IsNearAny(Vector3 position, List<CoverLocation> locations, List<Candidate> candidates, float distance)
        {
            foreach (var location in locations)
            {
                if (CoverRules.FlatDistance(position, location.Position) < distance)
                    return true;
            }
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
                var kind = candidate.Placement == CoverPlacement.Corner ? "Corner" : string.Empty;
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
