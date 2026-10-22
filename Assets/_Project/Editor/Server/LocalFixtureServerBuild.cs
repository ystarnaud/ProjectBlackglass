#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Blackglass.EditorTools
{
    /// <summary>
    /// The loopback fixture checkpoint's local server: ServerMission.unity as a Windows 64 player with the BLACKGLASS_NET_FIXTURE
    /// define, so -attemptFixture runs beside the net-test client on this PC without the Editor (run it with -batchmode
    /// -nographics). Not a Development build (that would open Unity's player connection on every interface) and not a server
    /// subtarget (this Editor has no Windows dedicated server module). Local only: never packaged or deployed, and the Linux
    /// release and fixture builds (ServerBuild) are unchanged. The active build target must already be Windows, so nothing switches.
    /// </summary>
    public static class LocalFixtureServerBuild
    {
        public const string OutputFolder = "Builds/LocalFixtureServer";
        public const string ExecutableName = "BlackglassServer.exe";
        const string Prefix = "[LocalFixtureServerBuild] ";

        /// <summary>Unity -batchmode -executeMethod entry; exits 0 on success, 1 otherwise.</summary>
        public static void BuildFromCommandLine()
        {
            var ok = false;
            string message;
            try
            {
                ok = Build(out message);
            }
            catch (Exception exception)
            {
                message = "exception: " + exception;
            }
            Debug.Log(Prefix + (ok ? "SUCCEEDED " : "FAILED ") + message);
            EditorApplication.Exit(ok ? 0 : 1);
        }

        static bool Build(out string message)
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
            {
                message = "the active build target is " + EditorUserBuildSettings.activeBuildTarget + ", not StandaloneWindows64";
                return false;
            }
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ServerSceneBuilder.ScenePath) == null)
            {
                message = ServerSceneBuilder.ScenePath + " is missing; run Blackglass/Server/Build Server Scene first";
                return false;
            }
            var root = Path.GetDirectoryName(Application.dataPath);
            var output = Path.Combine(root, OutputFolder);
            if (Directory.Exists(output))
                Directory.Delete(output, true);
            Directory.CreateDirectory(output);
            var executable = Path.Combine(output, ExecutableName);
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ServerSceneBuilder.ScenePath },
                target = BuildTarget.StandaloneWindows64,
                subtarget = (int)StandaloneBuildSubtarget.Player,
                locationPathName = executable,
                options = BuildOptions.StrictMode,
                extraScriptingDefines = new[] { ServerBuild.FixtureDefine },
            });
            var summary = report.summary;
            if (summary.result != BuildResult.Succeeded || !File.Exists(executable))
            {
                message = $"build {summary.result} with {summary.totalErrors} error(s); see the Editor log";
                return false;
            }
            // The stamp the server's identity line and the manifest's build revision read (ServerBuildInfo); the source values
            // come from Tools/build-local-fixture-server.sh, captured before Unity started.
            var source = ServerBuildInfo.SourceLines(Environment.GetEnvironmentVariable("BLACKGLASS_SOURCE_REVISION"),
                Environment.GetEnvironmentVariable("BLACKGLASS_SOURCE_TREE"), Environment.GetEnvironmentVariable("BLACKGLASS_SOURCE_DIRTY"));
            File.WriteAllText(Path.Combine(output, ServerBuildInfo.FileName), string.Join("\n", new[]
            {
                "product=" + PlayerSettings.productName,
                "version=" + PlayerSettings.bundleVersion,
                source[0],
                source[1],
                source[2],
                "builtUtc=" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
                "unity=" + Application.unityVersion,
                "target=StandaloneWindows64",
                "subtarget=Player",
                "variant=local-fixture",
                "backend=Mono",
                "buildGuid=" + summary.guid,
            }) + "\n");
            message = FormattableString.Invariant($"variant=local-fixture sizeMB={summary.totalSize / 1048576d:0.0} seconds={summary.totalTime.TotalSeconds:0} output={output}");
            return true;
        }
    }
}
#endif
