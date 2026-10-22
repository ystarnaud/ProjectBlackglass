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
        };

        // A use of the server-only types anywhere in the runtime scripts.
        static readonly Regex ServerOnlyUse = new Regex(@"\bNetMapProjection\b");

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
            foreach (var file in Directory.GetFiles("Assets/_Project/Scripts", "*.cs", SearchOption.AllDirectories))
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
            Assert.That(checkedUses, Is.GreaterThan(0), "the audit must not pass vacuously");
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
