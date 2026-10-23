using System;
using System.IO;
using System.Text;

namespace Blackglass.Backend.Adapter
{
    /// <summary>
    /// The attempt-scoped process credential (spec §4.3, Stage 10 I3). systemd's LoadCredential=process:… gives the mission unit
    /// $CREDENTIALS_DIRECTORY/process, which the root broker wrote as exactly the credential's 47 ASCII bytes: "pc1_" and 43
    /// canonical base64url characters (32 bytes), no newline. It is read once, never from a command line or an environment value,
    /// and never printed: there is no public accessor, ToString is redacted, and refusals name a code, never a byte of the file.
    /// Only the transport (this assembly) builds the Authorization header from it.
    /// </summary>
    public sealed class ProcessCredential
    {
        public const string FileName = "process";
        const string Prefix = "pc1_";
        const int TailLength = 43;
        const int Length = 47;
        const int MaxFileBytes = 4096;
        // 43 base64url characters carry 258 bits for 256: the last one's two low bits are zero.
        const string CanonicalLast = "AEIMQUYcgkosw048";

        readonly string text;

        ProcessCredential(string text) => this.text = text;

        /// <summary>
        /// False with one code (no_directory, missing, not_regular, too_large, malformed, unreadable) when the directory has no usable
        /// credential file. A symbolic link or anything but a regular file is refused.
        /// </summary>
        public static bool TryRead(string credentialsDirectory, out ProcessCredential credential, out string refusal)
        {
            credential = null;
            if (string.IsNullOrEmpty(credentialsDirectory) || !Directory.Exists(credentialsDirectory))
            {
                refusal = "no_directory";
                return false;
            }
            var path = Path.Combine(credentialsDirectory, FileName);
            try
            {
                if (Directory.Exists(path)) { refusal = "not_regular"; return false; }
                if (!File.Exists(path)) { refusal = "missing"; return false; }
                var info = new FileInfo(path);
                if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device)) != 0) { refusal = "not_regular"; return false; }
                if (info.Length > MaxFileBytes) { refusal = "too_large"; return false; }
                // One open and one bounded read: at most Length + 1 bytes, whatever the entry turned out to be when it was opened.
                var bytes = ReadBounded(path, Length + 1);
                if (!IsWellFormed(bytes)) { refusal = "malformed"; return false; }
                credential = new ProcessCredential(Encoding.ASCII.GetString(bytes));
                refusal = null;
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is System.Security.SecurityException)
            {
                refusal = "unreadable";
                return false;
            }
        }

        static byte[] ReadBounded(string path, int max)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.None))
            {
                var buffer = new byte[max];
                var total = 0;
                int read;
                while (total < max && (read = stream.Read(buffer, total, max - total)) > 0) total += read;
                if (total == max) return buffer;
                var exact = new byte[total];
                Array.Copy(buffer, exact, total);
                return exact;
            }
        }

        /// <summary>Tests only (InternalsVisibleTo): a credential from text, checked like a file's.</summary>
        internal static ProcessCredential FromTextForTests(string text)
        {
            if (text == null || !IsWellFormed(Encoding.ASCII.GetBytes(text))) throw new ArgumentException("Not a well-formed process credential.");
            return new ProcessCredential(text);
        }

        /// <summary>"Bearer pc1_…": the transport only.</summary>
        internal string AuthorizationHeaderValue() => "Bearer " + text;

        public override string ToString() => Prefix + "<redacted>";

        static bool IsWellFormed(byte[] bytes)
        {
            if (bytes.Length != Length) return false;
            for (var i = 0; i < Prefix.Length; i++)
                if (bytes[i] != Prefix[i]) return false;
            for (var i = Prefix.Length; i < Length; i++)
            {
                var c = (char)bytes[i];
                var ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
                if (!ok) return false;
            }
            return CanonicalLast.IndexOf((char)bytes[Length - 1]) >= 0;
        }
    }
}
