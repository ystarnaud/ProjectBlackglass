#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Blackglass.EditorTools
{
    /// <summary>
    /// Builds the Windows net-test client player from NetTestClient.unity into Builds/NetTestClient/ (git-ignored). The Editor's
    /// build target must already be Windows Standalone; the build never switches it.
    /// </summary>
    public static class NetClientBuild
    {
        public const string OutputPath = "Builds/NetTestClient/NetTestClient.exe";

        [MenuItem("Blackglass/Net/Build Net Test Client")]
        public static void BuildMenu() => Build();

        /// <summary>Unity -batchmode -executeMethod entry: exits 0 on success, 1 on any failure.</summary>
        public static void BuildFromCommandLine() => EditorApplication.Exit(Build() ? 0 : 1);

        static bool Build()
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
            {
                Debug.LogError("[NetClientBuild] the active build target is " + EditorUserBuildSettings.activeBuildTarget + ", not StandaloneWindows64.");
                return false;
            }
            var folder = Path.GetDirectoryName(OutputPath);
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
            Directory.CreateDirectory(folder);
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { NetClientSceneBuilder.ScenePath },
                locationPathName = OutputPath,
                target = BuildTarget.StandaloneWindows64,
                subtarget = (int)StandaloneBuildSubtarget.Player,
                options = BuildOptions.None,
            });
            Debug.Log("[NetClientBuild] " + report.summary.result + " " + (report.summary.totalSize / 1048576) + " MB " + OutputPath);
            return report.summary.result == BuildResult.Succeeded;
        }
    }
}
#endif
