namespace Blackglass.Backend.Core
{
    /// <summary>
    /// The backend's canonical text forms, each copied from the named backend member. Every check is an explicit character
    /// loop over the whole text, the equivalent of the backend's patterns anchored with \z (never $, which in .NET also
    /// matches before a final "\n"), so no form accepts a trailing newline.
    /// </summary>
    public static class CanonicalIds
    {
        /// <summary>CanonicalUuid.TryParse: 36 characters, lowercase hex, hyphens at 8, 13, 18 and 23.</summary>
        public static bool IsUuid(string text)
        {
            if (text == null || text.Length != 36) return false;
            for (var i = 0; i < 36; i++)
            {
                var c = text[i];
                if (i == 8 || i == 13 || i == 18 || i == 23) { if (c != '-') return false; }
                else if (!IsLowerHex(c)) return false;
            }
            return true;
        }

        /// <summary>ResultManifest.IsAttemptId: a canonical UUID of version 7 with the RFC variant.</summary>
        public static bool IsAttemptId(string text) =>
            IsUuid(text) && text[14] == '7' && (text[19] == '8' || text[19] == '9' || text[19] == 'a' || text[19] == 'b');

        /// <summary>^sha256:[0-9a-f]{64}\z</summary>
        public static bool IsSha256(string text)
        {
            if (text == null || text.Length != 71 || string.CompareOrdinal(text, 0, "sha256:", 0, 7) != 0) return false;
            for (var i = 7; i < 71; i++)
                if (!IsLowerHex(text[i])) return false;
            return true;
        }

        /// <summary>^[0-9a-f]{16}\z</summary>
        public static bool IsLayoutHash(string text)
        {
            if (text == null || text.Length != 16) return false;
            foreach (var c in text)
                if (!IsLowerHex(c)) return false;
            return true;
        }

        public const int MaxDefinitionIdLength = 64;

        /// <summary>CanonicalCatalogueJson.IsWellFormedId: ^[a-z0-9]+(?:[.-][a-z0-9]+)*\z, at most 64 characters.</summary>
        public static bool IsDefinitionId(string text) =>
            text != null && text.Length > 0 && text.Length <= MaxDefinitionIdLength && Separated(text, ".-");

        /// <summary>StrictJson.IsWord: ^[a-z0-9]+(?:[._-][a-z0-9]+)*\z, at most <paramref name="maxLength"/> characters.</summary>
        public static bool IsWord(string text, int maxLength) =>
            text != null && text.Length > 0 && text.Length <= maxLength && Separated(text, "._-");

        /// <summary>BuildIdentity.IsWellFormedRevision: ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z</summary>
        public static bool IsBuildRevision(string text)
        {
            if (text == null || text.Length == 0 || text.Length > 64 || !IsAsciiLetterOrDigit(text[0])) return false;
            for (var i = 1; i < text.Length; i++)
            {
                var c = text[i];
                if (!IsAsciiLetterOrDigit(c) && c != '.' && c != '_' && c != '-') return false;
            }
            return true;
        }

        /// <summary>Runs of [a-z0-9] joined by single separators, starting and ending with a run.</summary>
        static bool Separated(string text, string separators)
        {
            var previousWasSeparator = true; // the text may not start with a separator
            foreach (var c in text)
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) previousWasSeparator = false;
                else if (separators.IndexOf(c) >= 0 && !previousWasSeparator) previousWasSeparator = true;
                else return false;
            }
            return !previousWasSeparator;
        }

        static bool IsLowerHex(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');

        static bool IsAsciiLetterOrDigit(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
    }
}
