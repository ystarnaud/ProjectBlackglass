using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Blackglass.BackendTools
{
    /// <summary>
    /// The approved permanent, backend-facing ids of the prototype roles and abilities (owner, 2026-10-14), by asset path,
    /// and the one way an Editor builder gives a known asset its id. Ensure fills a blank id, keeps a matching one, and
    /// refuses (changing nothing) when the asset already has a different id or another asset of the same kind already
    /// uses the approved one: a permanent id is never overwritten and never duplicated.
    /// </summary>
    public static class PermanentIds
    {
        public static readonly IReadOnlyDictionary<string, string> Roles = new Dictionary<string, string>
        {
            ["Assets/_Project/Data/Operatives/Roles/Assault.asset"] = "role.assault",
            ["Assets/_Project/Data/Operatives/Roles/Recon.asset"] = "role.recon",
            ["Assets/_Project/Data/Operatives/Roles/Support.asset"] = "role.support",
        };

        public static readonly IReadOnlyDictionary<string, string> Abilities = new Dictionary<string, string>
        {
            ["Assets/_Project/Data/Abilities/AimedShot.asset"] = "ability.aimed-shot",
            ["Assets/_Project/Data/Abilities/Blast.asset"] = "ability.blast",
            ["Assets/_Project/Data/Abilities/Mend.asset"] = "ability.mend",
            ["Assets/_Project/Data/Abilities/ReconScan.asset"] = "ability.recon-scan",
        };

        public enum Outcome { Assigned, AlreadySet }

        /// <summary>
        /// Gives a role or ability asset its approved id through its serialized "id" field. others is the duplicate check's
        /// scope (the asset itself may be in it). Throws InvalidOperationException, leaving the asset unchanged, for a
        /// different existing id or an id another asset already uses.
        /// </summary>
        public static Outcome Ensure<T>(T asset, string approvedId, IEnumerable<T> others) where T : ScriptableObject
        {
            if (!CanonicalCatalogueWriter.IsWellFormedId(approvedId))
                throw new ArgumentException($"'{approvedId}' is not a well-formed catalogue id.", nameof(approvedId));
            var serialized = new SerializedObject(asset);
            var id = serialized.FindProperty("id")
                ?? throw new ArgumentException($"{asset.name} has no serialized id field.", nameof(asset));
            var current = id.stringValue;
            if (!string.IsNullOrWhiteSpace(current) && current != approvedId)
                throw new InvalidOperationException(
                    $"{asset.name} already has id '{current}', not the approved '{approvedId}'. It is left unchanged: permanent ids are never overwritten.");
            foreach (var other in others)
            {
                if (other == null || ReferenceEquals(other, asset))
                    continue;
                if (new SerializedObject(other).FindProperty("id")?.stringValue == approvedId)
                    throw new InvalidOperationException(
                        $"{other.name} already uses id '{approvedId}', so {asset.name} can't take it. Nothing was changed.");
            }
            if (!string.IsNullOrWhiteSpace(current))
                return Outcome.AlreadySet;
            id.stringValue = approvedId;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
            return Outcome.Assigned;
        }

        /// <summary>Every asset of this type in the project: the scope of Ensure's duplicate check for a builder.</summary>
        public static List<T> AllInProject<T>() where T : ScriptableObject =>
            AssetDatabase.FindAssets("t:" + typeof(T).Name)
                .Select(guid => AssetDatabase.LoadAssetAtPath<T>(AssetDatabase.GUIDToAssetPath(guid)))
                .Where(asset => asset != null)
                .ToList();
    }
}
