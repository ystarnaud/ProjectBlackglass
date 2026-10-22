using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Blackglass.Tests
{
    /// <summary>
    /// Owner ruling D1 (decision 052): the server-side emission of the visual map feature is compiled only in Editor and fixture
    /// builds. A release server never contains the projection, so no mission profile and no runtime flag can make it send.
    /// </summary>
    public class NetMapBuildGuardTests
    {
        const string FixtureGuard = "#if UNITY_EDITOR || BLACKGLASS_NET_FIXTURE";

        // The server-only files of the feature: each must open with the guard.
        static readonly string[] GuardedFiles =
        {
            "Assets/_Project/Scripts/Net/Core/NetMapProjection.cs",
            "Assets/_Project/Scripts/Net/Core/NetShotRelay.cs",
        };

        // A use of the feature's server side: the projection, the briefing messages and their encoder, the briefing hook.
        static readonly Regex ServerOnlyUse = new Regex(
            @"\bNetMapProjection\b|\bNetShotRelay\b|\bNetMapMessage\b|\bNetObjectivesMessage\b|\bNetMissionEndMessage\b|\bNetShotMessage\b|\bNetMapWire\b|\bBriefingMap\b");

        // The client side and the declarations: they decode and draw what a server sends, and never send it.
        static bool IsClientSide(string file)
        {
            var name = Path.GetFileName(file);
            return name == "NetMessages.cs" || name == "NetMapWire.cs" || name == "NetClient.cs" || name == "NetHarnessVisibility.cs" || name == "NetClientRecord.cs"
                || file.Replace('\\', '/').Contains("/Net/Play/");
        }

        [Test]
        public void TheServerOnlyFiles_OpenWithTheFixtureGuard()
        {
            foreach (var file in GuardedFiles)
                Assert.That(File.ReadLines(file).First().Trim(), Is.EqualTo(FixtureGuard), Path.GetFileName(file) + " must open with the fixture guard");
        }

        [Test]
        public void TheServerOnlyTypes_AreUsedOnlyInsideTheFixtureGuard()
        {
            var checkedUses = 0;
            foreach (var file in Directory.GetFiles("Assets/_Project/Scripts", "*.cs", SearchOption.AllDirectories).Where(f => !IsClientSide(f)))
            {
                var lines = File.ReadAllLines(file);
                for (var index = 0; index < lines.Length; index++)
                {
                    var code = StripLineComment(lines[index]);
                    if (code.TrimStart().StartsWith("#") || !ServerOnlyUse.IsMatch(code))
                        continue;
                    checkedUses++;
                    Assert.That(IsInsideFixtureGuard(lines, index), Is.True,
                        Path.GetFileName(file) + " line " + (index + 1) + " uses a server-only map type outside " + FixtureGuard + ": " + lines[index].Trim());
                }
            }
            Assert.That(checkedUses, Is.GreaterThan(5), "the audit must not pass vacuously");
        }

        [Test]
        public void TheServer_SendsTheBriefingOnlyFromGuardedCode()
        {
            var lines = File.ReadAllLines("Assets/_Project/Scripts/Net/Adapter/NetServer.cs");
            var sends = 0;
            for (var index = 0; index < lines.Length; index++)
            {
                if (!Regex.IsMatch(lines[index], @"NetMapWire\.ToMessage|\bparticipants\b|\bSendBriefing\b|\bBeginBriefing\b|\bFlushObjectives\b|\bFlushShots\b|\bOnFired\b|\bshotHandlers\b"))
                    continue;
                sends++;
                Assert.That(IsInsideFixtureGuard(lines, index), Is.True, "NetServer.cs line " + (index + 1) + " is outside " + FixtureGuard + ": " + lines[index].Trim());
            }
            Assert.That(sends, Is.GreaterThan(8), "the audit must not pass vacuously");
        }

        static string StripLineComment(string line)
        {
            var at = line.IndexOf("//", System.StringComparison.Ordinal);
            return at < 0 ? line : line.Substring(0, at);
        }

        // Line-based #if/#elif/#else/#endif tracking: true when some enclosing region is the true branch of the fixture guard.
        static bool IsInsideFixtureGuard(string[] lines, int target)
        {
            var stack = new System.Collections.Generic.List<bool>();
            for (var index = 0; index < target; index++)
            {
                var directive = lines[index].Trim();
                if (directive.StartsWith("#if"))
                    stack.Add(directive == FixtureGuard);
                else if ((directive.StartsWith("#elif") || directive.StartsWith("#else")) && stack.Count > 0)
                    stack[stack.Count - 1] = false;
                else if (directive.StartsWith("#endif") && stack.Count > 0)
                    stack.RemoveAt(stack.Count - 1);
            }
            return stack.Contains(true);
        }
    }
}
