using System;
using System.IO;
using System.Text;
using Blackglass.Backend.Adapter;
using NUnit.Framework;

namespace Blackglass.Tests
{
    /// <summary>
    /// The process credential (Stage 13, §7): read once from $CREDENTIALS_DIRECTORY/process, exactly the 47 ASCII bytes the Stage 10
    /// broker writes (pc1_ and 43 canonical base64url characters, no newline), and never printed.
    /// </summary>
    public sealed class ProcessCredentialTests
    {
        // 32 random-looking bytes in canonical base64url: the last character encodes 2 zero bits.
        public const string Valid = "pc1_q7Xh3kP0vYzR8mN2bW5tLc9gJdF1sA4eU6iO_-VxyzQ";
        const string Tail = "q7Xh3kP0vYzR8mN2bW5tLc9gJdF1sA4eU6iO_-VxyzQ";

        string directory;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "bg-cred-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        void WriteFile(string text) => File.WriteAllBytes(Path.Combine(directory, "process"), Encoding.ASCII.GetBytes(text));

        string Refusal()
        {
            Assert.That(ProcessCredential.TryRead(directory, out var credential, out var refusal), Is.False);
            Assert.That(credential, Is.Null);
            Assert.That(refusal, Does.Not.Contain(Tail.Substring(0, 8)));
            return refusal;
        }

        [Test]
        public void The_brokers_file_is_read()
        {
            WriteFile(Valid);
            Assert.That(ProcessCredential.TryRead(directory, out var credential, out var refusal), Is.True, refusal);
            Assert.That(credential.AuthorizationHeaderValue(), Is.EqualTo("Bearer " + Valid));
        }

        [Test]
        public void ToString_never_shows_the_secret()
        {
            WriteFile(Valid);
            ProcessCredential.TryRead(directory, out var credential, out _);
            Assert.That(credential.ToString(), Is.EqualTo("pc1_<redacted>"));
            Assert.That(credential.ToString(), Does.Not.Contain(Tail.Substring(0, 6)));
        }

        [Test]
        public void No_directory_is_refused()
        {
            Assert.That(ProcessCredential.TryRead(null, out _, out var a), Is.False);
            Assert.That(a, Is.EqualTo("no_directory"));
            Assert.That(ProcessCredential.TryRead(Path.Combine(directory, "absent"), out _, out var b), Is.False);
            Assert.That(b, Is.EqualTo("no_directory"));
        }

        [Test]
        public void A_missing_file_is_refused() => Assert.That(Refusal(), Is.EqualTo("missing"));

        [Test]
        public void A_directory_named_process_is_refused()
        {
            Directory.CreateDirectory(Path.Combine(directory, "process"));
            Assert.That(Refusal(), Is.EqualTo("not_regular"));
        }

        [Test]
        public void A_symbolic_link_is_refused()
        {
            var target = Path.Combine(directory, "target");
            File.WriteAllBytes(target, Encoding.ASCII.GetBytes(Valid));
            if (!TryCreateSymbolicLink(Path.Combine(directory, "process"), target))
                Assert.Ignore("this platform or account can't create a symbolic link");
            Assert.That(Refusal(), Is.EqualTo("not_regular"));
        }

        [Test]
        public void The_credential_is_read_with_one_bounded_read()
        {
            // 47 valid bytes followed by more: the reader stops after its bound and refuses, it never reads a stream to its end.
            WriteFile(Valid + new string('A', 100));
            Assert.That(Refusal(), Is.EqualTo("malformed"));
        }

        [Test]
        public void A_large_file_is_refused()
        {
            WriteFile(new string('a', 5000));
            Assert.That(Refusal(), Is.EqualTo("too_large"));
        }

        [TestCase(Valid + "A")]                                            // 48 bytes
        [TestCase(Valid + "\n")]                                           // a trailing newline
        [TestCase("hs1_" + Tail)]                                          // another secret's prefix
        [TestCase("pc1_q7Xh3kP0vYzR8mN2bW5tLc9gJdF1sA4eU6iO_-VxyzR")]       // a non-canonical last character
        [TestCase("pc1_q7Xh3kP0vYzR8mN2bW5tLc9gJdF1sA4eU6iO_-Vxyz=")]       // padding
        [TestCase("pc1_q7Xh3kP0vYzR8mN2bW5tLc9gJdF1sA4eU6iO+/VxyzQ")]       // base64, not base64url
        [TestCase("pc1_" + "q7Xh3kP0vYzR8mN2bW5tLc9gJdF1sA4eU6iO_-Vxyz")]   // 42 characters
        [TestCase("")]
        public void A_malformed_credential_is_refused(string text)
        {
            WriteFile(text);
            Assert.That(Refusal(), Is.EqualTo("malformed"));
        }

        static bool TryCreateSymbolicLink(string link, string target)
        {
            try
            {
                using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Environment.OSVersion.Platform == PlatformID.Win32NT ? "cmd.exe" : "ln",
                    Arguments = Environment.OSVersion.Platform == PlatformID.Win32NT ? $"/c mklink \"{link}\" \"{target}\"" : $"-s \"{target}\" \"{link}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }))
                {
                    process.WaitForExit(10000);
                    return process.ExitCode == 0 && File.Exists(link);
                }
            }
            catch (Exception) { return false; }
        }
    }
}
