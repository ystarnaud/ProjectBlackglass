using System.Collections.Generic;
using System.Linq;
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
        public void Describe_InAnAttempt_NamesNoNumericSeed()
        {
            // The attempt's seed is a backend secret; the scene's default (or an ignored -seed) is not it, but must not look like it either.
            var plain = Parse("-attemptFixture", "f.json").Describe(7);
            var overridden = Parse("-attemptFixture", "f.json", "-seed", "9").Describe(7);
            Assert.That(plain, Does.StartWith("seed=attempt fps=60 diagnostic=no").And.EndWith(" attempt=fixture"));
            Assert.That(overridden, Does.StartWith("seed=attempt fps=60"));
            foreach (var line in new[] { plain, overridden })
            {
                Assert.That(line, Does.Not.Match(@"seed=-?\d"));
                Assert.That(line, Does.Not.Contain("(fixture)").And.Not.Contain("(override)"));
            }
        }

        [Test]
        public void With_ReplacesOnlyWhatItIsGiven()
        {
            var options = Parse("-fps", "30").With(diagnostic: true, diagnosticTimeoutSeconds: 5);
            Assert.That(options.FrameRate, Is.EqualTo(30));
            Assert.That(options.Diagnostic, Is.True);
            Assert.That(options.DiagnosticTimeoutSeconds, Is.EqualTo(5));
        }

        [Test]
        public void NetArguments_HaveTheirDefaults()
        {
            var options = Parse(out var warnings);
            Assert.That(options.Net, Is.False);
            Assert.That(options.NetPort, Is.EqualTo(7770));
            Assert.That(options.NetTokensPath, Is.Null);
            Assert.That(options.NetQuiet, Is.False);
            Assert.That(warnings, Is.Empty);
        }

        [Test]
        public void NetArguments_AreRead()
        {
            var options = Parse(out var warnings, "-net", "-netPort", "7801", "-netTokens", "/opt/blackglass/dev-tokens.txt", "-netQuiet");
            Assert.That(options.Net, Is.True);
            Assert.That(options.NetPort, Is.EqualTo(7801));
            Assert.That(options.NetTokensPath, Is.EqualTo("/opt/blackglass/dev-tokens.txt"));
            Assert.That(options.NetQuiet, Is.True);
            Assert.That(warnings, Is.Empty);
        }

        [TestCase("1023")]
        [TestCase("65536")]
        [TestCase("abc")]
        public void ABadNetPort_KeepsTheDefault_AndWarns(string value)
        {
            var options = Parse(out var warnings, "-net", "-netPort", value);
            Assert.That(options.NetPort, Is.EqualTo(7770));
            Assert.That(options.Net, Is.True);
            Assert.That(warnings, Has.Count.EqualTo(1));
            StringAssert.Contains("-netPort", warnings[0]);
        }

        [Test]
        public void NetTokens_WithoutAValue_KeepsNull_AndDoesNotSwallowTheNextFlag()
        {
            var missing = Parse(out var warnings, "-netTokens");
            Assert.That(missing.NetTokensPath, Is.Null);
            Assert.That(warnings, Has.Count.EqualTo(1));

            var flagNext = Parse(out warnings, "-netTokens", "-net");
            Assert.That(flagNext.NetTokensPath, Is.Null);
            Assert.That(flagNext.Net, Is.True, "the next flag is left for the loop to read");
            Assert.That(warnings, Has.Count.EqualTo(1));
        }

        [Test]
        public void Describe_ShowsTheNetOptions()
        {
            Assert.That(Parse().Describe(7), Does.EndWith("net=no"));
            Assert.That(Parse("-net", "-netQuiet").Describe(7), Does.EndWith("net=yes port=7770 quiet=yes"));
            Assert.That(Parse("-net", "-netPort", "7801").Describe(7), Does.EndWith("net=yes port=7801 quiet=no"));
        }

        [Test]
        public void With_ReplacesTheNetOptions()
        {
            var options = Parse().With(net: true, netPort: 7801, netTokensPath: "/tokens.txt", netQuiet: true);
            Assert.That(options.Net, Is.True);
            Assert.That(options.NetPort, Is.EqualTo(7801));
            Assert.That(options.NetTokensPath, Is.EqualTo("/tokens.txt"));
            Assert.That(options.NetQuiet, Is.True);
            Assert.That(Parse().With(diagnostic: true).Net, Is.False, "untouched values stay as they were");
        }

        [Test]
        public void NetFixture_IsParsed_Described_AndCopied()
        {
            var options = Parse("-net", "-netQuiet", "-netFixture");
            Assert.That(options.NetFixture, Is.True);
            Assert.That(options.Describe(7), Does.EndWith("net=yes port=7770 quiet=yes fixture=yes"));
            Assert.That(Parse("-net").NetFixture, Is.False);
            Assert.That(Parse().With(netFixture: true).NetFixture, Is.True);
            Assert.That(Parse("-netFixture").With(diagnostic: true).NetFixture, Is.True, "untouched values stay as they were");
        }
        // Stage 13: the backend attempt mode's options.
        const string BackendAttempt = "0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41";

        [Test]
        public void TheBackendOptions_HaveProvisionalDefaults()
        {
            var o = Parse();
            Assert.That(o.Attempt, Is.Null);
            Assert.That(o.Backend, Is.Null);
            Assert.That(o.BackendRequestTimeoutSeconds, Is.EqualTo(10));
            Assert.That(o.BackendRegisterBoundSeconds, Is.EqualTo(60));
            Assert.That(o.BackendReadyBoundSeconds, Is.EqualTo(30));
            Assert.That(o.BackendStartGraceSeconds, Is.EqualTo(30));
            Assert.That(o.BackendHeartbeatSeconds, Is.EqualTo(10));
            Assert.That(o.RecordedBoundSeconds, Is.EqualTo(30));
        }

        [Test]
        public void TheBackendOptions_AreRead()
        {
            var o = Parse(out var warnings, "-attempt", BackendAttempt, "-backend", "http://127.0.0.1:5081", "-backendRequestTimeout", "20", "-backendRegisterBound", "90",
                "-backendReadyBound", "40", "-backendStartGrace", "15", "-backendHeartbeat", "5", "-recordedBound", "60");
            Assert.That(warnings, Is.Empty);
            Assert.That(o.Attempt, Is.EqualTo(BackendAttempt));
            Assert.That(o.Backend, Is.EqualTo("http://127.0.0.1:5081"));
            Assert.That(o.BackendRequestTimeoutSeconds, Is.EqualTo(20));
            Assert.That(o.BackendRegisterBoundSeconds, Is.EqualTo(90));
            Assert.That(o.BackendReadyBoundSeconds, Is.EqualTo(40));
            Assert.That(o.BackendStartGraceSeconds, Is.EqualTo(15));
            Assert.That(o.BackendHeartbeatSeconds, Is.EqualTo(5));
            Assert.That(o.RecordedBoundSeconds, Is.EqualTo(60));
            var timings = BackendTimings.FromOptions(o);
            Assert.That(timings.RequestTimeoutSeconds, Is.EqualTo(20));
            Assert.That(timings.RegisterBoundSeconds, Is.EqualTo(90));
            Assert.That(timings.ReadyBoundSeconds, Is.EqualTo(40));
            Assert.That(timings.StartGraceSeconds, Is.EqualTo(15));
            Assert.That(timings.HeartbeatSeconds, Is.EqualTo(5));
            Assert.That(timings.RecordedBoundSeconds, Is.EqualTo(60));
            Assert.That(timings.Retry.AttemptTimeoutSeconds, Is.EqualTo(20));
        }

        [Test]
        public void AMissingAttemptId_StillSelectsBackendMode()
        {
            Assert.That(Parse(out var warnings, "-attempt").Attempt, Is.EqualTo(string.Empty), "never the plain mission by accident");
            Assert.That(warnings, Has.Count.EqualTo(1));
            Assert.That(Parse("-attempt", "-net").Attempt, Is.EqualTo(string.Empty));
        }

        [TestCase("-backendRequestTimeout", "1", 10)]
        [TestCase("-backendRequestTimeout", "61", 10)]
        [TestCase("-backendRegisterBound", "4", 60)]
        [TestCase("-backendReadyBound", "301", 30)]
        [TestCase("-backendStartGrace", "-1", 30)]
        [TestCase("-backendHeartbeat", "1", 10)]
        [TestCase("-backendHeartbeat", "31", 10)]
        [TestCase("-recordedBound", "0", 30)]
        [TestCase("-recordedBound", "121", 30)]
        public void ABackendTimingOutOfRange_KeepsItsDefault(string flag, string value, int expected)
        {
            var o = Parse(out var warnings, flag, value);
            Assert.That(warnings, Has.Count.EqualTo(1));
            var read = flag == "-backendRequestTimeout" ? o.BackendRequestTimeoutSeconds : flag == "-backendRegisterBound" ? o.BackendRegisterBoundSeconds
                : flag == "-backendReadyBound" ? o.BackendReadyBoundSeconds : flag == "-backendStartGrace" ? o.BackendStartGraceSeconds
                : flag == "-backendHeartbeat" ? o.BackendHeartbeatSeconds : o.RecordedBoundSeconds;
            Assert.That(read, Is.EqualTo(expected));
        }

        [Test]
        public void Describe_NamesTheBackendAttemptAndNoSeed()
        {
            var text = Parse("-attempt", BackendAttempt, "-backend", "http://127.0.0.1:5081", "-seed", "777").Describe(123);
            Assert.That(text, Does.Contain("attempt=backend"));
            Assert.That(text, Does.Contain("seed=attempt"));
            Assert.That(text, Does.Not.Contain("777"));
            Assert.That(text, Does.Not.Contain("123"));
        }

        [Test]
        public void NoOption_TakesACredentialASecretOrATicket()
        {
            var source = System.IO.File.ReadAllText("Assets/_Project/Scripts/Server/ServerOptions.cs");
            var names = System.Text.RegularExpressions.Regex.Matches(source, "case \"(-[A-Za-z]+)\"").Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).ToArray();
            Assert.That(names, Has.Member("-attempt"));
            Assert.That(names.Where(n => System.Text.RegularExpressions.Regex.IsMatch(n, "credential|secret|ticket|key|password", System.Text.RegularExpressions.RegexOptions.IgnoreCase)), Is.Empty);
            foreach (var property in typeof(ServerOptions).GetProperties())
                Assert.That(System.Text.RegularExpressions.Regex.IsMatch(property.Name, "Credential|Secret|Ticket|Password"), Is.False, property.Name);
        }
    }
}
