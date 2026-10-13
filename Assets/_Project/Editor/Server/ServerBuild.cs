#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

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
        public static void BuildFromCommandLine() => BuildForCommandLine(development: false);

        /// <summary>
        /// Unity -batchmode -executeMethod entry for the development build the harness runs against: the release build plus
        /// BuildOptions.Development, so the development fixture (-netFixture) is compiled in (decision 047). Exits 0 or 1.
        /// </summary>
        public static void BuildFixtureFromCommandLine() => BuildForCommandLine(development: true);

        static void BuildForCommandLine(bool development)
        {
            var ok = false;
            string message;
            try
            {
                ok = Build(out message, development);
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

        public static bool Build(out string message) => Build(out message, development: false);

        public static bool Build(out string message, bool development)
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
                    options = BuildOptions.StrictMode | (development ? BuildOptions.Development : BuildOptions.None),
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
                WriteBuildInfo(root, output, summary, development);
                message = $"variant={(development ? "fixture" : "release")} platform={summary.platform} subtarget=Server backend={PlayerSettings.GetScriptingBackend(NamedBuildTarget.Server)} " +
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
        static void WriteBuildInfo(string root, string output, BuildSummary summary, bool development)
        {
            var revision = Git(root, "rev-parse HEAD") ?? "unknown";
            var status = Git(root, "status --porcelain --untracked-files=no");
            var dirty = status == null ? "unknown" : status.Length > 0 ? "true" : "false";
            var lines = new[]
            {
                "product=" + PlayerSettings.productName,
                "version=" + PlayerSettings.bundleVersion,
                "revision=" + revision,
                "dirty=" + dirty,
                "builtUtc=" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
                "unity=" + Application.unityVersion,
                "target=StandaloneLinux64",
                "subtarget=Server",
                "variant=" + (development ? "fixture" : "release"),
                "backend=Mono",
                "buildGuid=" + summary.guid,
            };
            File.WriteAllText(Path.Combine(output, ServerBuildInfo.FileName), string.Join("\n", lines) + "\n");
        }

        static string Git(string root, string arguments)
        {
            try
            {
                var info = new ProcessStartInfo("git", arguments)
                {
                    WorkingDirectory = root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using (var process = Process.Start(info))
                {
                    var text = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit(10000);
                    return process.ExitCode == 0 ? text : null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
#endif
