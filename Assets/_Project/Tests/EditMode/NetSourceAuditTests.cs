using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Blackglass.Tests
{
    /// <summary>Keeps the networking core independent of any networking library (spec section 3.2, CLAUDE.md).</summary>
    public class NetSourceAuditTests
    {
        const string Folder = "Assets/_Project/Scripts/Net/Core";

        [Test]
        public void TheCoreFiles_ReferenceNoNetworkingLibrary()
        {
            var files = Directory.GetFiles(Folder, "*.cs");
            Assert.That(files, Is.Not.Empty, "the audit must not pass vacuously");
            string[] forbidden = { "FishNet", "Unity.Netcode", "Unity.Networking", "Mirror", "LiteNetLib", "System.Net.Sockets", "Unity.Transport", "Unity.Collections", "GameKit" };
            foreach (var file in files)
            {
                var text = File.ReadAllText(file);
                foreach (var token in forbidden.Where(text.Contains))
                    Assert.Fail(Path.GetFileName(file) + " must not reference '" + token + "'");
            }
        }

        [Test]
        public void TheAdapter_NeverGivesOwnership_NorSpawnsWithAnOwner()
        {
            var files = Directory.GetFiles("Assets/_Project/Scripts/Net/Adapter", "*.cs");
            Assert.That(files, Is.Not.Empty, "the audit must not pass vacuously");
            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                var text = File.ReadAllText(file);
                Assert.That(text.Contains("GiveOwnership"), Is.False, name + " gives ownership of an object");
                Assert.That(text.Contains("_clientAuthoritative"), Is.False, name + " touches transform authority at runtime");
                foreach (Match call in Regex.Matches(text, @"\.Spawn\(([^()]*)\)"))
                    Assert.That(call.Groups[1].Value.Contains(","), Is.False, name + " spawns with an owner: " + call.Value);
            }
        }

        const string FixtureGuard = "#if UNITY_EDITOR || BLACKGLASS_NET_FIXTURE";

        // A use of the fixture types: NetFixtureContainer anywhere, or NetFixture as a type (a declaration, `new`, or a
        // static member). Not ServerOptions.NetFixture (a member access or the bool property) and not NetFixtureVerb etc.
        static readonly Regex FixtureTypeUse =
            new Regex(@"\bNetFixtureContainer\b|(?<![.\w])NetFixture(?=\s+[A-Za-z_]|\s*\.|\s*\()");

        [Test]
        public void NoSource_UsesDevelopmentBuild_TheFixtureHasItsOwnDefine()
        {
            var files = Directory.GetFiles("Assets/_Project", "*.cs", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles("Tools", "*.sh", SearchOption.AllDirectories))
                .Where(file => Path.GetFileName(file) != "NetSourceAuditTests.cs")
                .ToArray();
            Assert.That(files.Length, Is.GreaterThan(100), "the audit must not pass vacuously");
            foreach (var file in files)
                Assert.That(File.ReadAllText(file).Contains("DEVELOPMENT_BUILD"), Is.False,
                    file + " uses DEVELOPMENT_BUILD; the fixture is compiled under BLACKGLASS_NET_FIXTURE (a Development build opens Unity's player connection)");
        }

        [Test]
        public void TheFixtureTypes_AreUsedOnlyInsideTheFixtureGuard()
        {
            var files = Directory.GetFiles("Assets/_Project/Scripts", "*.cs", SearchOption.AllDirectories);
            var checkedUses = 0;
            foreach (var file in files)
            {
                var lines = File.ReadAllLines(file);
                for (var index = 0; index < lines.Length; index++)
                {
                    var code = StripLineComment(lines[index]);
                    if (code.TrimStart().StartsWith("#") || !FixtureTypeUse.IsMatch(code))
                        continue;
                    checkedUses++;
                    Assert.That(IsInsideFixtureGuard(lines, index), Is.True,
                        Path.GetFileName(file) + " line " + (index + 1) + " uses a fixture type outside " + FixtureGuard + ": " + lines[index].Trim());
                }
            }
            Assert.That(checkedUses, Is.GreaterThan(5), "the audit must not pass vacuously");
            foreach (var name in new[] { "NetFixture.cs", "NetFixtureContainer.cs" })
                Assert.That(File.ReadLines(Path.Combine(Folder, name)).First().Trim(), Is.EqualTo(FixtureGuard), name + " must open with the fixture guard");
        }

        [Test]
        public void TheFixtureServerBuild_UsesTheDefine_NotADevelopmentBuild()
        {
            var text = File.ReadAllText("Assets/_Project/Editor/Server/ServerBuild.cs");
            Assert.That(text.Contains("\"BLACKGLASS_NET_FIXTURE\""), Is.True, "ServerBuild must add the BLACKGLASS_NET_FIXTURE define");
            Assert.That(text.Contains("extraScriptingDefines"), Is.True, "ServerBuild must set the define through extraScriptingDefines");
            Assert.That(text.Contains("BuildOptions.Development"), Is.False, "a Development build opens Unity's player connection on every interface");
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
