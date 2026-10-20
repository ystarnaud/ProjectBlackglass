using System;

namespace Blackglass.Backend.Core
{
    /// <summary>
    /// The public form of a backend id that PostgreSQL generates as a positive bigint: the canonical Bitcoin Base58 encoding
    /// of the number itself. A port of Backend/src/Accounts.Domain/Ids/Base58Id.cs: parsing accepts only the exact canonical
    /// spelling (1 to 11 alphabet characters, ordinal so case-sensitive, no leading '1', a value from 1 to long.MaxValue).
    /// </summary>
    public static class Base58Id
    {
        public const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

        /// <summary>long.MaxValue is "NQm6nKp8qFC": 11 characters.</summary>
        public const int MaxLength = 11;

        static readonly sbyte[] Digits = BuildDigits();

        static sbyte[] BuildDigits()
        {
            var digits = new sbyte[128];
            for (var i = 0; i < digits.Length; i++) digits[i] = -1;
            for (var i = 0; i < Alphabet.Length; i++) digits[Alphabet[i]] = (sbyte)i;
            return digits;
        }

        public static string Format(long id)
        {
            if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id), id, "A Base58 id is positive.");
            var buffer = new char[MaxLength];
            var start = MaxLength;
            for (var rest = id; rest > 0; rest /= 58)
                buffer[--start] = Alphabet[(int)(rest % 58)];
            return new string(buffer, start, MaxLength - start);
        }

        public static bool TryParse(string text, out long id)
        {
            id = 0;
            if (text == null || text.Length == 0 || text.Length > MaxLength || text[0] == Alphabet[0]) return false;
            long value = 0;
            foreach (var c in text)
            {
                int digit = c < 128 ? Digits[c] : -1;
                if (digit < 0) return false;
                if (value > (long.MaxValue - digit) / 58) return false; // value * 58 + digit would pass long.MaxValue
                value = value * 58 + digit;
            }
            id = value;
            return true;
        }
    }
}
