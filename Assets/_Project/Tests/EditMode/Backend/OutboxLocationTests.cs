using System;
using System.IO;
using Blackglass.Backend.Core;
using NUnit.Framework;

namespace Blackglass.Tests
{
    /// <summary>The only production path rule: $STATE_DIRECTORY must be exactly &lt;parent&gt;/&lt;attemptId&gt; (owner ruling D6).</summary>
    public sealed class OutboxLocationTests
    {
        const string Parent = "/var/lib/blackglass-outbox", Attempt = "0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41";

        static string Refusal(string value, string attempt = Attempt)
        {
            Assert.That(OutboxLocation.TryResolve(value, Parent, attempt, out var path, out var refusal), Is.False, value);
            Assert.That(path, Is.Null);
            return refusal;
        }

        [Test]
        public void The_systemd_value_resolves()
        {
            Assert.That(OutboxLocation.TryResolve(Parent + "/" + Attempt, Parent, Attempt, out var path, out var refusal), Is.True);
            Assert.That(path, Is.EqualTo(Parent + "/" + Attempt));
            Assert.That(refusal, Is.Null);
        }

        [TestCase(null, "missing")]
        [TestCase("", "missing")]
        [TestCase("var/lib/blackglass-outbox/" + Attempt, "not_absolute")]
        [TestCase(Parent + "/" + Attempt + ":/var/lib/other", "multiple_paths")]
        [TestCase("/var/lib//blackglass-outbox/" + Attempt, "not_normalized")]
        [TestCase("/var/lib/./blackglass-outbox/" + Attempt, "not_normalized")]
        [TestCase("/var/lib/x/../blackglass-outbox/" + Attempt, "not_normalized")]
        [TestCase(Parent + "/" + Attempt + "/", "not_normalized")]
        [TestCase(Parent + "/" + Attempt + "/..", "not_normalized")]
        [TestCase("/var/lib/blackglass-outbox-evil/" + Attempt, "outside_parent")]
        [TestCase("/var/lib/" + Attempt, "outside_parent")]
        [TestCase(Parent + "/sub/" + Attempt, "wrong_attempt")]
        [TestCase(Parent + "/0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e42", "wrong_attempt")]
        [TestCase(Parent + "/0192F1A4-5C2E-7B1D-9A0E-3F6C1D2B8E41", "wrong_attempt")]
        public void Each_bad_value_is_refused_with_its_code(string value, string code) => Assert.That(Refusal(value), Is.EqualTo(code));

        [Test]
        public void An_overlong_or_nul_containing_value_is_refused()
        {
            Assert.That(Refusal(Parent + "/" + new string('a', 4097)), Is.EqualTo("too_long"));
            Assert.That(Refusal(Parent + "/" + Attempt + "\0"), Is.EqualTo("not_normalized"));
        }

        [Test]
        public void A_non_canonical_attempt_is_refused() => Assert.That(Refusal(Parent + "/" + Attempt, Attempt.ToUpperInvariant()), Is.EqualTo("wrong_attempt"));

        [Test]
        public void OpenForServer_refuses_off_linux_and_OpenForEditor_creates_nothing()
        {
            Assert.Throws<PlatformNotSupportedException>(() => OutboxDirectories.OpenForServer(Parent + "/" + Attempt, Parent, Attempt));
            var missing = Path.Combine(Path.GetTempPath(), "bg-outbox-missing-" + Guid.NewGuid().ToString("N"));
            Assert.Throws<DirectoryNotFoundException>(() => OutboxDirectories.OpenForEditor(missing));
            Assert.That(Directory.Exists(missing), Is.False);
        }
    }
}
