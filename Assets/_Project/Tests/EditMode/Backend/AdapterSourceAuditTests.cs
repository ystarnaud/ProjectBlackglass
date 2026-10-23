using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Blackglass.Tests
{
    /// <summary>
    /// Where HTTP may live (Stage 13, S13-D5): UnityWebRequest only in Backend/Adapter, which references only the library-free core
    /// and no networking library; no adapter log line takes a body, credential, ticket or seed.
    /// </summary>
    public sealed class AdapterSourceAuditTests
    {
        const string Scripts = "Assets/_Project/Scripts";
        const string Adapter = "Assets/_Project/Scripts/Backend/Adapter";

        static string[] Sources(string root) => Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories).Select(p => p.Replace('\\', '/')).ToArray();

        [Test]
        public void UnityWebRequest_appears_only_in_the_adapter()
        {
            var outside = Sources(Scripts).Where(p => !p.StartsWith(Adapter + "/"))
                .Where(p => Regex.IsMatch(File.ReadAllText(p), @"\bUnityWebRequest\b|UnityEngine\.Networking")).ToArray();
            Assert.That(outside, Is.Empty);
        }

        [Test]
        public void The_adapter_uses_no_networking_library()
        {
            foreach (var path in Sources(Adapter))
            {
                var text = File.ReadAllText(path);
                Assert.That(Regex.IsMatch(text, @"\bFishNet\b|System\.Net\.Sockets|\bHttpClient\b|\bTcpClient\b|\bSocket\b"), Is.False, path);
            }
        }

        [Test]
        public void The_adapter_references_only_the_core()
        {
            var asmdef = File.ReadAllText(Adapter + "/Blackglass.Backend.Adapter.asmdef");
            var references = Regex.Matches(Regex.Match(asmdef, @"""references""\s*:\s*\[(.*?)\]", RegexOptions.Singleline).Groups[1].Value, @"""([^""]+)""")
                .Cast<Match>().Select(m => m.Groups[1].Value).ToArray();
            Assert.That(references, Is.EqualTo(new[] { "Blackglass.Backend.Core" }));
        }

        [Test]
        public void No_adapter_log_line_takes_a_secret_or_a_body()
        {
            foreach (var path in Sources(Adapter))
                foreach (var line in File.ReadAllLines(path).Where(l => Regex.IsMatch(l, @"\bDebug\.Log")))
                    Assert.That(Regex.IsMatch(line, @"\b(body|Body|credential|ticket|seed|text)\b"), Is.False, path + ": " + line.Trim());
        }
    }
}
