#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Blackglass.EditorTools
{
    /// <summary>
    /// Builds the Windows play client from NetPlayClient.unity into Builds/NetPlayClient/ (git-ignored). Local, development-only,
    /// never deployed; not a Development build (no player connection). The Editor's build target must already be Windows Standalone.
    /// </summary>
    public static class NetPlayClientBuild
    {
        public const string OutputPath = "Builds/NetPlayClient/NetPlayClient.exe";

        [MenuItem("Blackglass/Net/Build Net Play Client")]
        public static void BuildMenu() => Build();

        /// <summary>Unity -batchmode -executeMethod entry: exits 0 on success, 1 on any failure.</summary>
        public static void BuildFromCommandLine() => EditorApplication.Exit(Build() ? 0 : 1);

        static bool Build()
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
            {
                Debug.LogError("[NetPlayClientBuild] the active build target is " + EditorUserBuildSettings.activeBuildTarget + ", not StandaloneWindows64.");
                return false;
            }
            var folder = Path.GetDirectoryName(OutputPath);
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
            Directory.CreateDirectory(folder);
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { NetPlayClientSceneBuilder.ScenePath },
                locationPathName = OutputPath,
                target = BuildTarget.StandaloneWindows64,
                subtarget = (int)StandaloneBuildSubtarget.Player,
                options = BuildOptions.None,
            });
            Debug.Log("[NetPlayClientBuild] " + report.summary.result + " " + (report.summary.totalSize / 1048576) + " MB " + OutputPath);
            return report.summary.result == BuildResult.Succeeded;
        }
    }
}
#endif
