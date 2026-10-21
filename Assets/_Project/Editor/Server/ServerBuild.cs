#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Blackglass.EditorTools
{
    /// <summary>
    /// Builds the Linux Dedicated Server (Mono) player from ServerMission.unity only (Checkpoint A, decision 045). Refuses
    /// without changing anything when a module is missing or the Server scripting backend is not Mono; never edits Player
    /// Settings. Writes build-info.txt beside the executable, and always restores the Editor's active build target, so the
    /// Editor and Tools/run-tests.sh never reopen on the server target with UNITY_SERVER defined.
    /// </summary>
    public static class ServerBuild
    {
        public const string OutputFolder = "Builds/LinuxServer";
        public const string ExecutableName = "BlackglassServer.x86_64";
        const string ServerVariation = "linux64_server_nondevelopment_mono";
        const string Prefix = "[ServerBuild] ";

        [MenuItem("Blackglass/Server/Build Linux Dedicated Server")]
        public static void BuildMenu()
        {
            var ok = Build(out var message);
            Debug.Log(Prefix + message);
            EditorUtility.DisplayDialog("Linux dedicated server", (ok ? "Built. " : "Failed. ") + message, "OK");
        }

        /// <summary>Unity -batchmode -executeMethod entry; exits 0 on success, 1 otherwise.</summary>
        public static void BuildFromCommandLine() => BuildForCommandLine(fixture: false);

        /// <summary>
        /// Unity -batchmode -executeMethod entry for the fixture build the harness runs against: the release build plus the
        /// BLACKGLASS_NET_FIXTURE scripting define, so the development fixture (-netFixture) is compiled in (decision 047).
        /// Not a Development build: that would open Unity's player connection on every interface. Exits 0 or 1.
        /// </summary>
        public static void BuildFixtureFromCommandLine() => BuildForCommandLine(fixture: true);

        /// <summary>The scripting define that compiles the development fixture into a player build.</summary>
        public const string FixtureDefine = "BLACKGLASS_NET_FIXTURE";

        static void BuildForCommandLine(bool fixture)
        {
            var ok = false;
            string message;
            try
            {
                ok = Build(out message, fixture);
            }
            catch (Exception exception)
            {
                message = "exception: " + exception;
            }
            Debug.Log(Prefix + (ok ? "SUCCEEDED " : "FAILED ") + message);
            EditorApplication.Exit(ok ? 0 : 1);
        }

        /// <summary>Unity -batchmode -executeMethod entry: logs the active build target (checks that a build restored it).</summary>
        public static void ReportActiveTargetFromCommandLine()
        {
            Debug.Log(Prefix + $"active target={EditorUserBuildSettings.activeBuildTarget} subtarget={EditorUserBuildSettings.standaloneBuildSubtarget}");
            EditorApplication.Exit(0);
        }

        public static bool Build(out string message) => Build(out message, fixture: false);

        public static bool Build(out string message, bool fixture)
        {
            if (!Preflight(out message))
                return false;
            var previousTarget = EditorUserBuildSettings.activeBuildTarget;
            var previousGroup = BuildPipeline.GetBuildTargetGroup(previousTarget);
            var previousSubtarget = EditorUserBuildSettings.standaloneBuildSubtarget;
            try
            {
                var root = Path.GetDirectoryName(Application.dataPath);
                var output = Path.Combine(root, OutputFolder);
                if (Directory.Exists(output))
                    Directory.Delete(output, true);
                Directory.CreateDirectory(output);
                var executable = Path.Combine(output, ExecutableName);
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { ServerSceneBuilder.ScenePath },
                    target = BuildTarget.StandaloneLinux64,
                    subtarget = (int)StandaloneBuildSubtarget.Server,
                    locationPathName = executable,
                    options = BuildOptions.StrictMode,
                    extraScriptingDefines = fixture ? new[] { FixtureDefine } : null,
                });
                var summary = report.summary;
                if (summary.result != BuildResult.Succeeded)
                {
                    message = $"build {summary.result} with {summary.totalErrors} error(s); see the Editor log";
                    return false;
                }
                var data = Path.Combine(output, Path.GetFileNameWithoutExtension(ExecutableName) + "_Data");
                if (!File.Exists(executable) || !Directory.Exists(data))
                {
                    message = $"the build reported success but {executable} or {data} is missing";
                    return false;
                }
                WriteBuildInfo(output, summary, fixture);
                message = $"variant={(fixture ? "fixture" : "release")} platform={summary.platform} subtarget=Server backend={PlayerSettings.GetScriptingBackend(NamedBuildTarget.Server)} " +
                    FormattableString.Invariant($"sizeMB={summary.totalSize / 1048576d:0.0} seconds={summary.totalTime.TotalSeconds:0} output={output}");
                return true;
            }
            finally
            {
                Restore(previousGroup, previousTarget, previousSubtarget);
            }
        }

        static bool Preflight(out string message)
        {
            var install = $"Install with: \"C:\\Program Files\\Unity Hub\\Unity Hub.exe\" -- --headless install-modules --version {Application.unityVersion} -m linux-mono -m linux-server";
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneLinux64))
            {
                message = "Linux Build Support (Mono) is not installed for this Editor. " + install;
                return false;
            }
            string engine;
            try
            {
                engine = BuildPipeline.GetPlaybackEngineDirectory(BuildTarget.StandaloneLinux64, BuildOptions.None);
            }
            catch (Exception)
            {
                engine = null;
            }
            if (string.IsNullOrEmpty(engine) || !Directory.Exists(Path.Combine(engine, "Variations", ServerVariation)))
            {
                message = "Linux Dedicated Server Build Support (Mono variation) is not installed. " + install;
                return false;
            }
            var backend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Server);
            if (backend != ScriptingImplementation.Mono2x)
            {
                message = $"the Server scripting backend is {backend}; Checkpoint A builds with Mono. Change it deliberately in Player Settings (this build never does).";
                return false;
            }
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ServerSceneBuilder.ScenePath) == null)
            {
                message = $"{ServerSceneBuilder.ScenePath} is missing; run Blackglass/Server/Build Server Scene first.";
                return false;
            }
            message = null;
            return true;
        }

        static void Restore(BuildTargetGroup group, BuildTarget target, StandaloneBuildSubtarget subtarget)
        {
            if (EditorUserBuildSettings.standaloneBuildSubtarget != subtarget)
                EditorUserBuildSettings.standaloneBuildSubtarget = subtarget;
            if (EditorUserBuildSettings.activeBuildTarget != target)
                EditorUserBuildSettings.SwitchActiveBuildTarget(group, target);
            Debug.Log(Prefix + $"active target after build: {EditorUserBuildSettings.activeBuildTarget} subtarget={EditorUserBuildSettings.standaloneBuildSubtarget}");
        }

        // LF line endings: the file is read on Linux.
        static void WriteBuildInfo(string output, BuildSummary summary, bool fixture)
        {
            // The source state captured by Tools/build-server.sh before Unity started; never read from git here, because
            // Unity has already rewritten settings it switches and restores (Stage 12 rulings §1).
            var source = ServerBuildInfo.SourceLines(Environment.GetEnvironmentVariable("BLACKGLASS_SOURCE_REVISION"),
                Environment.GetEnvironmentVariable("BLACKGLASS_SOURCE_TREE"), Environment.GetEnvironmentVariable("BLACKGLASS_SOURCE_DIRTY"));
            var lines = new[]
            {
                "product=" + PlayerSettings.productName,
                "version=" + PlayerSettings.bundleVersion,
                source[0],
                source[1],
                source[2],
                "builtUtc=" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
                "unity=" + Application.unityVersion,
                "target=StandaloneLinux64",
                "subtarget=Server",
                "variant=" + (fixture ? "fixture" : "release"),
                "backend=Mono",
                "buildGuid=" + summary.guid,
            };
            File.WriteAllText(Path.Combine(output, ServerBuildInfo.FileName), string.Join("\n", lines) + "\n");
        }
    }
}
#endif
