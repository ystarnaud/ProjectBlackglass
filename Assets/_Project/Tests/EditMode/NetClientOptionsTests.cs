using System.Collections.Generic;
using NUnit.Framework;

namespace Blackglass.Tests
{
    public class NetClientOptionsTests
    {
        static NetClientOptions Parse(out List<string> warnings, params string[] args) => NetClientOptions.Parse(args, out warnings);

        [Test]
        public void NoArguments_GiveTheDefaults()
        {
            var options = Parse(out var warnings);
            Assert.That(options.Server, Is.EqualTo("127.0.0.1"));
            Assert.That(options.Port, Is.EqualTo(7770));
            Assert.That(options.Token, Is.Null);
            Assert.That(options.Harness, Is.Null);
            Assert.That(warnings, Is.Empty);
        }

        [Test]
        public void EveryArgument_IsRead()
        {
            var options = Parse(out var warnings, "NetTestClient.exe", "-batchmode", "-server", "example.test", "-port", "7801", "-token", "abc", "-netharness", "a");
            Assert.That(options.Server, Is.EqualTo("example.test"));
            Assert.That(options.Port, Is.EqualTo(7801));
            Assert.That(options.Token, Is.EqualTo("abc"));
            Assert.That(options.Harness, Is.EqualTo("a"));
            Assert.That(warnings, Is.Empty);
        }

        [TestCase("1023")]
        [TestCase("65536")]
        [TestCase("x")]
        public void ABadPort_KeepsTheDefault_AndWarns(string value)
        {
            var options = Parse(out var warnings, "-port", value);
            Assert.That(options.Port, Is.EqualTo(7770));
            Assert.That(warnings, Has.Count.EqualTo(1));
        }

        [TestCase("a")]
        [TestCase("b")]
        [TestCase("ticket")]
        [TestCase("badtoken")]
        [TestCase("visibility-a")]
        [TestCase("visibility-b")]
        [TestCase("fixture-denied")]
        [TestCase("visibility-fault")]
        public void TheEightRoles_AreAccepted(string role)
        {
            var options = Parse(out var warnings, "-netharness", role);
            Assert.That(options.Harness, Is.EqualTo(role));
            Assert.That(warnings, Is.Empty);
        }

        [Test]
        public void AnUnknownRole_IsIgnored_WithAWarning()
        {
            var options = Parse(out var warnings, "-netharness", "z");
            Assert.That(options.Harness, Is.Null);
            Assert.That(warnings, Has.Count.EqualTo(1));
        }

        [Test]
        public void MissingValues_KeepTheDefaults_AndDoNotSwallowTheNextFlag()
        {
            var options = Parse(out var warnings, "-token", "-server", "host.test", "-netharness");
            Assert.That(options.Token, Is.Null);
            Assert.That(options.Server, Is.EqualTo("host.test"), "the flag after the missing value is still read");
            Assert.That(options.Harness, Is.Null);
            Assert.That(warnings, Has.Count.EqualTo(2));
        }

        // The loopback fixture checkpoint: the scripted, operator-assisted attempt role and its two ground coordinates.
        [Test]
        public void TheAttemptRole_IsAccepted_WithTheOperatorsHints()
        {
            var options = Parse(out var warnings, "-netharness", "attempt", "-assistHostile", "-12.5,3", "-assistExtraction", "4,-7.25");
            Assert.That(options.Harness, Is.EqualTo("attempt"));
            Assert.That(options.AssistHostile, Is.EqualTo(new UnityEngine.Vector3(-12.5f, 0f, 3f)), "a negative first value is a coordinate, not a flag");
            Assert.That(options.AssistExtraction, Is.EqualTo(new UnityEngine.Vector3(4f, 0f, -7.25f)));
            Assert.That(warnings, Is.Empty);
        }

        [Test]
        public void NoHints_AreNull()
        {
            var options = Parse(out _);
            Assert.That(options.AssistHostile, Is.Null);
            Assert.That(options.AssistExtraction, Is.Null);
        }

        [TestCase("3")]
        [TestCase("a,b")]
        [TestCase("1,2,3")]
        [TestCase("NaN,1")]
        [TestCase("1,Infinity")]
        public void ABadHint_IsIgnored_WithAWarning(string value)
        {
            var options = Parse(out var warnings, "-assistHostile", value);
            Assert.That(options.AssistHostile, Is.Null);
            Assert.That(warnings, Has.Count.EqualTo(1));
        }

        [Test]
        public void AMissingHint_DoesNotSwallowTheNextFlag()
        {
            var options = Parse(out var warnings, "-assistExtraction", "-token", "abc");
            Assert.That(options.AssistExtraction, Is.Null);
            Assert.That(options.Token, Is.EqualTo("abc"));
            Assert.That(warnings, Has.Count.EqualTo(1));
        }
    }
}
