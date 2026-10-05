using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// A deliberately small search for a spot to shoot from: 8 compass points at 2 m and 4 m around the unit, tried in
    /// that order, so the first valid one is the nearest. The caller decides what "valid" means (on the NavMesh, in
    /// range, in sight, reachable). No cover value, no scoring, no search around the target.
    /// </summary>
    public static class FiringPositionFinder
    {
        /// <summary>Compass points per ring.</summary>
        public const int DirectionCount = 8;
        /// <summary>Ring radii in metres, nearest first, so the first valid candidate is also the nearest.</summary>
        public static readonly float[] Radii = { 2f, 4f };
        /// <summary>Total candidates: DirectionCount points on each of the two rings.</summary>
        public const int CandidateCount = 16;

        /// <summary>Fills the buffer with the candidates, nearest ring first, north first then clockwise. Returns the count.</summary>
        public static int Candidates(Vector3 origin, Vector3[] buffer)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (buffer.Length < CandidateCount)
                throw new ArgumentException($"The buffer needs {CandidateCount} entries.", nameof(buffer));
            var index = 0;
            foreach (var radius in Radii)
            {
                for (var i = 0; i < DirectionCount; i++)
                {
                    var angle = i * (2f * Mathf.PI / DirectionCount);
                    buffer[index++] = origin + new Vector3(Mathf.Sin(angle) * radius, 0f, Mathf.Cos(angle) * radius);
                }
            }
            return index;
        }

        /// <summary>
        /// A caller's validity test: accepts or rejects a raw candidate and, when accepting, returns the point to walk
        /// to (the candidate snapped onto the NavMesh).
        /// </summary>
        public delegate bool Validator(Vector3 candidate, out Vector3 accepted);

        /// <summary>The first candidate the validator accepts (the nearest, given the ring order), as its accepted point; or false.</summary>
        public static bool TryChoose(Vector3[] candidates, int count, Validator isValid, out Vector3 chosen)
        {
            if (candidates == null)
                throw new ArgumentNullException(nameof(candidates));
            if (isValid == null)
                throw new ArgumentNullException(nameof(isValid));
            for (var i = 0; i < count; i++)
            {
                if (isValid(candidates[i], out chosen))
                    return true;
            }
            chosen = default;
            return false;
        }
    }
}
