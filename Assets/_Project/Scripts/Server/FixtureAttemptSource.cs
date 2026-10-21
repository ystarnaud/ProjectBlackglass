#if UNITY_EDITOR || BLACKGLASS_NET_FIXTURE
using System;
using System.IO;
using Blackglass.Backend.Core;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The development attempt source (Stage 12, S12-D4): an attempt-v1 fixture description (Backend/contracts/attempt-v1) read as
    /// the register answer the backend would send for it, with one participant. Compiled only into the Editor and fixture builds
    /// (BLACKGLASS_NET_FIXTURE), so a release server can't start a fake attempt; the bootstrap also refuses -attemptFixture there.
    /// </summary>
    public sealed class FixtureAttemptSource : IAttemptSource
    {
        public const string TemplateId = "mission.standard";

        [Serializable]
        sealed class Fixture
        {
            public string attemptId;
            public string processSessionId;
            public string catalogueVersion;
            public int seed;
            public LoadoutSnapshotDto snapshot;
        }

        readonly string path;

        public FixtureAttemptSource(string path) => this.path = path;

        public bool TryLoad(out string attemptId, out SessionRegisterResponse response, out string problem)
        {
            attemptId = null;
            response = null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                problem = "the attempt fixture was not found: " + path;
                return false;
            }
            Fixture fixture;
            try { fixture = JsonUtility.FromJson<Fixture>(File.ReadAllText(path)); }
            catch (Exception e) when (e is ArgumentException || e is IOException || e is UnauthorizedAccessException)
            {
                problem = "the attempt fixture is unreadable: " + e.GetType().Name;
                return false;
            }
            if (fixture == null || fixture.snapshot == null || string.IsNullOrEmpty(fixture.snapshot.accountId))
            {
                problem = "the attempt fixture has no snapshot";
                return false;
            }
            attemptId = fixture.attemptId;
            response = new SessionRegisterResponse
            {
                processSessionId = fixture.processSessionId,
                seed = fixture.seed,
                missionTemplate = new MissionTemplateDto { id = TemplateId, version = 1 },
                catalogueVersion = fixture.catalogueVersion,
                participants = new[] { new RegisterParticipantDto { accountId = fixture.snapshot.accountId, loadout = fixture.snapshot } },
                deadlines = new LaunchDeadlineDto { launch = string.Empty },
            };
            problem = null;
            return true;
        }
    }
}
#endif
