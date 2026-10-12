#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FishNet.Managing;
using FishNet.Managing.Object;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Blackglass.Tests
{
    /// <summary>
    /// The server scene running in the Editor with the net server on a loopback port (networking experiment). One FishNet
    /// manager serves both sides (host mode), so the handshake, the gate, the budgets and the result path run end to end; the
    /// replication across a network is proven only by the VM acceptance run.
    /// </summary>
    internal sealed class NetLoopbackRig
    {
        public const string ScenePath = "Assets/_Project/Scenes/ServerMission.unity";
        public const string TokenA = "tok-a";
        public const string TokenB = "tok-b";
        public const string TokenTicket = "tok-t";

        static int nextPort = 7790;

        readonly List<string> logs = new List<string>();
        Scene scene;
        int frameRateBefore;
        int vSyncBefore;

        public ServerBootstrap Bootstrap { get; private set; }
        public NetServer Server { get; private set; }
        public MissionDirector Director { get; private set; }
        public int Port { get; private set; }
        public int ExitCode { get; private set; } = -1;
        public string TokenPath { get; private set; }
        public IReadOnlyList<string> Logs => logs;

        public IEnumerator Start()
        {
            frameRateBefore = Application.targetFrameRate;
            vSyncBefore = QualitySettings.vSyncCount;
            Port = nextPort++;
            Application.logMessageReceived += OnLog;
            TokenPath = Path.Combine(Application.temporaryCachePath, "net-rig-tokens-" + Port + ".txt");
            File.WriteAllText(TokenPath, "account-a " + TokenA + " squad\naccount-b " + TokenB + " none\naccount-t " + TokenTicket + " none other-attempt\n");

            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            scene = SceneManager.GetSceneByPath(ScenePath);
            var roots = scene.GetRootGameObjects();
            var serverRoot = roots.Single(g => g.name == "Server");
            Bootstrap = serverRoot.GetComponent<ServerBootstrap>();
            Server = serverRoot.GetComponent<NetServer>();
            Director = roots.Single(g => g.name == "Mission").GetComponent<MissionDirector>();
            Bootstrap.Exit = code => ExitCode = code;
            yield return TestWorld.WaitUntil(() => Director.State == MissionState.Ready || Director.State == MissionState.Failed, 60f);
            Assert.That(Director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", Director.Report.Failures));
        }

        public void BeginNet(bool quiet = true) =>
            Bootstrap.BeginNet(Bootstrap.Options.With(net: true, netPort: Port, netTokensPath: TokenPath, netQuiet: quiet));

        public IEnumerator Stop()
        {
            Application.logMessageReceived -= OnLog;
            StopClient();
            if (Server != null)
                Server.Stop();
            NetManagerFactory.DestroyAll();
            if (Director != null)
                Director.Clear();
            yield return null;
            if (scene.isLoaded)
                yield return SceneManager.UnloadSceneAsync(scene);
            Application.targetFrameRate = frameRateBefore;
            QualitySettings.vSyncCount = vSyncBefore;
            Time.timeScale = 1f;
        }

        public NetClient Client { get; private set; }

        public NetClient AddClient()
        {
            var host = new GameObject("NetClient");
            Client = host.AddComponent<NetClient>();
            Client.Initialize(AssetDatabase.LoadAssetAtPath<PrefabObjects>("Assets/DefaultPrefabObjects.asset"));
            return Client;
        }

        public IEnumerator Connect(string token)
        {
            if (Client == null)
                AddClient();
            // A previous connection may still be shutting down; FishNet cannot start the client again until it has stopped.
            yield return TestWorld.WaitUntil(() => NetworkManager.Instances.Count == 0 || !NetworkManager.Instances[0].ClientManager.Started, 5f);
            Assert.That(Client.Connect("127.0.0.1", (ushort)Port, token), Is.True);
            yield return TestWorld.WaitUntil(() => Client.State == NetClientState.Ready || Client.State == NetClientState.Refused
                || Client.State == NetClientState.Disconnected, 15f);
        }

        void StopClient()
        {
            if (Client == null)
                return;
            Client.Disconnect();
            Object.DestroyImmediate(Client.gameObject);
            Client = null;
        }

        void OnLog(string condition, string stackTrace, LogType type) => logs.Add(condition);
    }
}
#endif
