using System.Collections.Generic;
using NUnit.Framework;

namespace Blackglass.Tests
{
    public class ServerOptionsTests
    {
        static ServerOptions Parse(params string[] args) => ServerOptions.Parse(args, out _);

        static ServerOptions Parse(out List<string> warnings, params string[] args) => ServerOptions.Parse(args, out warnings);

        [Test]
        public void NoArguments_GiveTheDefaults()
        {
            var options = Parse(out var warnings);
            Assert.That(options.Seed, Is.Null, "no override: the scene's fixture seed is used");
            Assert.That(options.FrameRate, Is.EqualTo(60));
            Assert.That(options.Diagnostic, Is.False);
            Assert.That(options.DiagnosticTimeoutSeconds, Is.EqualTo(120));
            Assert.That(options.StartupTimeoutSeconds, Is.EqualTo(60));
            Assert.That(options.HeartbeatSeconds, Is.EqualTo(60));
            Assert.That(warnings, Is.Empty);
        }

        [Test]
        public void NullArguments_GiveTheDefaults()
        {
            var options = ServerOptions.Parse(null, out var warnings);
            Assert.That(options.FrameRate, Is.EqualTo(60));
            Assert.That(warnings, Is.Empty);
        }

        [Test]
        public void EveryArgument_IsRead()
        {
            var options = Parse(out var warnings, "-seed", "42", "-fps", "30", "-diagnostic", "-diagnosticTimeout", "90",
                "-startupTimeout", "45", "-heartbeat", "10");
            Assert.That(options.Seed, Is.EqualTo(42));
            Assert.That(options.FrameRate, Is.EqualTo(30));
            Assert.That(options.Diagnostic, Is.True);
            Assert.That(options.DiagnosticTimeoutSeconds, Is.EqualTo(90));
            Assert.That(options.StartupTimeoutSeconds, Is.EqualTo(45));
            Assert.That(options.HeartbeatSeconds, Is.EqualTo(10));
            Assert.That(warnings, Is.Empty);
        }

        [Test]
        public void UnitysOwnArguments_AreIgnored()
        {
            var options = Parse(out var warnings, "/opt/blackglass/server/BlackglassServer.x86_64", "-batchmode", "-nographics",
                "-logFile", "-", "-diagnostic");
            Assert.That(options.Diagnostic, Is.True);
            Assert.That(options.FrameRate, Is.EqualTo(60));
            Assert.That(warnings, Is.Empty);
        }

        [TestCase("-fps", "0")]
        [TestCase("-fps", "1001")]
        [TestCase("-diagnosticTimeout", "4")]
        [TestCase("-startupTimeout", "3601")]
        [TestCase("-heartbeat", "3")]
        [TestCase("-fps", "sixty")]
        public void AnOutOfRangeOrNonNumericValue_KeepsTheDefault_AndWarns(string name, string value)
        {
            var options = Parse(out var warnings, name, value);
            Assert.That(options.FrameRate, Is.EqualTo(60));
            Assert.That(options.DiagnosticTimeoutSeconds, Is.EqualTo(120));
            Assert.That(options.StartupTimeoutSeconds, Is.EqualTo(60));
            Assert.That(options.HeartbeatSeconds, Is.EqualTo(60));
            Assert.That(warnings, Has.Count.EqualTo(1));
            StringAssert.Contains(name, warnings[0]);
        }

        [Test]
        public void AMissingValue_AtTheEnd_Warns()
        {
            var options = Parse(out var warnings, "-seed");
            Assert.That(options.Seed, Is.Null);
            Assert.That(warnings, Has.Count.EqualTo(1));
        }

        [Test]
        public void AFlagFollowedByAnotherFlag_DoesNotSwallowIt()
        {
            var options = Parse(out var warnings, "-seed", "-diagnostic");
            Assert.That(options.Seed, Is.Null);
            Assert.That(options.Diagnostic, Is.True, "-diagnostic is still read");
            Assert.That(warnings, Has.Count.EqualTo(1));
        }

        [Test]
        public void ANegativeSeed_IsAValue_NotAFlag()
        {
            Assert.That(Parse("-seed", "-5").Seed, Is.EqualTo(-5));
        }

        [Test]
        public void ARepeatedArgument_LastOneWins()
        {
            Assert.That(Parse("-fps", "30", "-fps", "90").FrameRate, Is.EqualTo(90));
        }

        [Test]
        public void HeartbeatZero_TurnsItOff()
        {
            var options = Parse(out var warnings, "-heartbeat", "0");
            Assert.That(options.HeartbeatSeconds, Is.EqualTo(0));
            Assert.That(warnings, Is.Empty);
        }

        [Test]
        public void ArgumentNames_AreCaseSensitive()
        {
            Assert.That(Parse("-Diagnostic").Diagnostic, Is.False);
        }

        [Test]
        public void Describe_NamesTheSeedSource()
        {
            Assert.That(Parse().Describe(7), Does.StartWith("seed=7(fixture) fps=60 diagnostic=no"));
            Assert.That(Parse("-seed", "9", "-heartbeat", "0").Describe(7), Does.Contain("seed=9(override)").And.Contain("heartbeat=off"));
        }

        [Test]
        public void With_ReplacesOnlyWhatItIsGiven()
        {
            var options = Parse("-fps", "30").With(diagnostic: true, diagnosticTimeoutSeconds: 5);
            Assert.That(options.FrameRate, Is.EqualTo(30));
            Assert.That(options.Diagnostic, Is.True);
            Assert.That(options.DiagnosticTimeoutSeconds, Is.EqualTo(5));
        }
    }
}
