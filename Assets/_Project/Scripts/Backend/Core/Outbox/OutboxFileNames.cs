using System;

namespace Blackglass.Backend.Core
{
    /// <summary>
    /// The agent's naming contract (Stage 10 OutboxScanner, Stage 9 §H): "&lt;accountBase58&gt;.manifest.tmp|written|ready",
    /// the account in canonical Base58. Outbox operations take these names only, never a path.
    /// </summary>
    public static class OutboxFileNames
    {
        public const string TmpSuffix = ".manifest.tmp", WrittenSuffix = ".manifest.written", ReadySuffix = ".manifest.ready";

        public static string Tmp(string accountId) => Checked(accountId) + TmpSuffix;
        public static string Written(string accountId) => Checked(accountId) + WrittenSuffix;
        public static string Ready(string accountId) => Checked(accountId) + ReadySuffix;

        /// <summary>^[1-9A-HJ-NP-Za-km-z]{1,11}\.manifest\.(tmp|written|ready)\z with a canonical Base58 account.</summary>
        public static bool IsOutboxName(string name)
        {
            if (name == null) return false;
            foreach (var suffix in new[] { TmpSuffix, WrittenSuffix, ReadySuffix })
            {
                if (!name.EndsWith(suffix, StringComparison.Ordinal)) continue;
                var account = name.Substring(0, name.Length - suffix.Length);
                return Base58Id.TryParse(account, out var id) && Base58Id.Format(id) == account;
            }
            return false;
        }

        static string Checked(string accountId)
        {
            if (!Base58Id.TryParse(accountId, out var id) || Base58Id.Format(id) != accountId)
                throw new ArgumentException("The account id is not canonical Base58.", nameof(accountId));
            return accountId;
        }
    }
}
