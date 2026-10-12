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
        public void TheFourRoles_AreAccepted(string role)
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
    }
}
