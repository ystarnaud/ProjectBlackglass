#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>The non-sight sources: hostile fire, the camera network, scans and the briefing.</summary>
    public class IntelligenceServiceSourcesTests
    {
        static readonly Vector3 RoomB = new Vector3(6f, 0f, -1f);

        IntelRig rig;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        // ---- exposure (a hostile that fires reveals itself) ----

        [UnityTest]
        public IEnumerator AHostileThatFiresFromBeyondObservationRange_IsExposed_ThenBecomesLastKnown()
        {
            rig = new IntelRig(corridor: true);
            var (shooter, attacker) = rig.AddShooter(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog(s =>
            {
                s.observationRange = 8f;   // the shooter is 13 m away
                s.exposureSeconds = 1f;
            }));
            yield return null;
            Assert.That(rig.Service.CanTarget(shooter), Is.False, "out of observation range");

            Assert.That(attacker.TryAttack(rig.FriendlyHealth), Is.True, "the shot is fired");
            Assert.That(rig.Service.CanTarget(shooter), Is.True, "exposed the moment it fires");
            Assert.That(rig.Service.StateOfEnemy(shooter), Is.EqualTo(KnowledgeState.Observed));

            yield return new WaitForSeconds(1.4f);
            Assert.That(rig.Service.StateOfEnemy(shooter), Is.EqualTo(KnowledgeState.Discovered), "exposure ran out: last known");
            Assert.That(rig.Service.CanTarget(shooter), Is.False);
            Assert.That(rig.Service.TryLastKnown(shooter, out var at), Is.True);
            Assert.That(Vector3.Distance(at, shooter.transform.position), Is.LessThan(0.01f));
        }

        [UnityTest]
        public IEnumerator AnExposedShooter_ObservesTheRegionItStandsIn_SoItIsNeverDrawnInsideFog()
        {
            rig = new IntelRig(corridor: true);
            var (shooter, attacker) = rig.AddShooter(IntelRig.InLineGround);   // room B (region 1), 13 m away
            var fogMaterial = new Material(Shader.Find("Sprites/Default"));
            var host = rig.World.Track(new GameObject("FogHost"));
            host.SetActive(false);
            var fog = host.AddComponent<FogPresenter>();
            fog.Initialize(rig.Service, fogMaterial);
            host.SetActive(true);
            var root = rig.World.Track(new GameObject("MissionRoot")).transform;
            rig.Begin(IntelRig.Fog(s =>
            {
                s.observationRange = 8f;
                s.exposureSeconds = 1f;
            }), new IntelligenceMission { Root = root });
            try
            {
                yield return null;
                Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Unknown), "Precondition: nobody has seen room B");
                Assert.That(fog.TargetAt(RoomB), Is.EqualTo(0f), "Precondition: it is fogged");

                Assert.That(attacker.TryAttack(rig.FriendlyHealth), Is.True, "the shot is fired");
                Assert.That(rig.Service.CanTarget(shooter), Is.True);
                Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Observed), "the room of a seen shooter is seen");
                Assert.That(fog.TargetAt(RoomB), Is.EqualTo(1f), "so the shooter is not drawn inside the dark");

                yield return new WaitForSeconds(1.4f);
                Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Discovered), "exposure ran out: the room stays on the map");
                Assert.That(rig.Service.StateOfEnemy(shooter), Is.EqualTo(KnowledgeState.Discovered));
            }
            finally
            {
                Object.DestroyImmediate(fogMaterial);
            }
        }

        [UnityTest]
        public IEnumerator AMissedShot_ExposesTheShooterToo()
        {
            rig = new IntelRig(corridor: true);
            var (shooter, attacker) = rig.AddShooter(IntelRig.InLineGround);
            rig.PutFriendlyInCover();   // a low wall between the two: the shooter sees over it, but the friendly is protected
            rig.Begin(IntelRig.Fog(s => s.observationRange = 8f));
            yield return null;
            Assert.That(rig.Service.CanTarget(shooter), Is.False, "out of observation range");

            var missed = 0;
            var hit = 0;
            attacker.Missed += _ => missed++;
            attacker.Attacked += _ => hit++;
            attacker.HitRoll = () => 0.99f;   // above the cover's 0.5 hit chance: a miss
            Assert.That(attacker.TryAttack(rig.FriendlyHealth), Is.True);
            Assert.That(missed, Is.EqualTo(1), "Precondition: the shot was turned away by cover");
            Assert.That(hit, Is.EqualTo(0));
            Assert.That(rig.Service.CanTarget(shooter), Is.True, "a miss exposes the shooter as much as a hit");
        }

        [UnityTest]
        public IEnumerator Exposure_DoesNotExpireWhileTheGameIsPaused()
        {
            rig = new IntelRig(corridor: true);
            var (shooter, attacker) = rig.AddShooter(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog(s =>
            {
                s.observationRange = 8f;
                s.exposureSeconds = 1f;
            }));
            yield return null;
            attacker.TryAttack(rig.FriendlyHealth);

            Time.timeScale = 0f;
            yield return new WaitForSecondsRealtime(1.5f);
            Assert.That(rig.Service.StateOfEnemy(shooter), Is.EqualTo(KnowledgeState.Observed), "nothing hidden advances while paused");
            Time.timeScale = 1f;
            yield return new WaitForSeconds(1.4f);
            Assert.That(rig.Service.StateOfEnemy(shooter), Is.EqualTo(KnowledgeState.Discovered));
        }

        [UnityTest]
        public IEnumerator AShooterNotYetFiring_IsNotExposed()
        {
            rig = new IntelRig(corridor: true);
            var (shooter, _) = rig.AddShooter(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog(s => s.observationRange = 8f));
            yield return null;
            Assert.That(rig.Service.CanTarget(shooter), Is.False);
        }

        // ---- scans ----

        [UnityTest]
        public IEnumerator AScan_DiscoversTheRegionsItTouches_AndObservesEnemiesForAWhile_ThroughWalls()
        {
            rig = new IntelRig(corridor: false);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog());
            yield return null;
            Assert.That(rig.Service.CanTarget(hostile), Is.False);

            rig.Service.Scan(IntelRig.InLineGround, 4f, 1f);
            Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Observed), "scanned room B, behind a closed wall");
            Assert.That(rig.Service.CanTarget(hostile), Is.True, "a scan is not sight: walls do not matter");

            yield return new WaitForSeconds(1.4f);
            Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Discovered), "the layout stays");
            Assert.That(rig.Service.StateOfEnemy(hostile), Is.EqualTo(KnowledgeState.Discovered), "the enemy is a marker now");
            Assert.That(rig.Service.CanTarget(hostile), Is.False);
        }

        [UnityTest]
        public IEnumerator AScan_OnlyObservesEnemiesInsideItsRadius()
        {
            rig = new IntelRig(corridor: false);
            var near = rig.AddHostile(IntelRig.InLineGround);
            var far = rig.AddHostile(new Vector3(6.5f, 0f, 1.5f));
            rig.Begin(IntelRig.Fog());
            yield return null;
            rig.Service.Scan(IntelRig.InLineGround, 1.5f, 5f);
            Assert.That(rig.Service.CanTarget(near), Is.True);
            Assert.That(rig.Service.CanTarget(far), Is.False, "3 m from the scan centre");
        }

        [UnityTest]
        public IEnumerator AScan_WhenFogIsOff_DoesNothingAndDoesNotThrow()
        {
            rig = new IntelRig(corridor: false);
            rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(new IntelligenceSettings());
            yield return null;
            Assert.DoesNotThrow(() => rig.Service.Scan(IntelRig.InLineGround, 4f, 1f));
        }

        // ---- the camera network ----

        // A camera on room B's east wall, looking west across the room (and, with the corridor shut, only across room B).
        static IntelligenceMission CameraMission(MissionInteractable terminal, out CameraNetwork network, float range = 12f,
            Vector2Int? terminalTile = null)
        {
            var mount = new CameraMount(new Vector2Int(18, 3), new Vector2Int(1, 0), 1);
            var security = new SecurityPlan(true, 0, terminalTile ?? new Vector2Int(2, 2), new[] { mount });
            var layout = IntelLayouts.TwoRooms(false);
            var spec = new CameraSpec(security.CameraDeviceId(0), security.CameraPosition(layout, 0), security.CameraForward(0), range, 45f);
            network = new CameraNetwork(new[] { spec });
            return new IntelligenceMission { Security = security, Network = network, CameraTerminal = terminal };
        }

        MissionInteractable CameraTerminal()
        {
            var host = rig.World.Track(new GameObject("CameraTerminal"));
            var terminal = host.AddComponent<MissionInteractable>();
            terminal.Initialize(1.8f, 2f, "Camera control");
            terminal.SetAvailable(true);
            return terminal;
        }

        [UnityTest]
        public IEnumerator TheCameras_GiveNothingUntilTheTerminalIsUsed_ThenCoverTheirRooms_AndStayLive()
        {
            rig = new IntelRig(corridor: false);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            var terminal = CameraTerminal();
            rig.Begin(IntelRig.Fog(), CameraMission(terminal, out var network));
            yield return null;
            Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Unknown));
            Assert.That(rig.Service.CanTarget(hostile), Is.False);

            Assert.That(terminal.TryBegin(rig.Friendly.Unit), Is.True);
            terminal.Advance(rig.Friendly.Unit, 10f);
            Assert.That(network.Compromised, Is.True);
            Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Observed), "the camera sees room B");
            Assert.That(rig.Service.CanTarget(hostile), Is.True, "and the enemy in it, live");

            // Still inside room B, but 0.4 m in front of and 3 m to the side of the camera (at x 8.9, facing west): 82 degrees off axis.
            hostile.transform.position = new Vector3(8.5f, 1f, 1.5f);
            Physics.SyncTransforms();
            rig.Service.RunPass();
            Assert.That(rig.Service.CanTarget(hostile), Is.False, "outside the camera's cone");
        }

        [UnityTest]
        public IEnumerator ACamera_CannotSeeThroughTheWallIntoRoomA()
        {
            rig = new IntelRig(corridor: false);
            var inA = rig.AddHostile(new Vector3(-4f, 0f, -1.5f));   // in the squad's room, 2.5 m from the friendly
            var terminal = CameraTerminal();
            rig.Begin(IntelRig.Fog(s => s.observationRange = 4f), CameraMission(terminal, out var network, range: 30f));   // long camera range
            yield return null;
            Assert.That(rig.Service.CanTarget(inA), Is.True, "Precondition: the friendly sees it from 2.5 m");

            terminal.TryBegin(rig.Friendly.Unit);
            terminal.Advance(rig.Friendly.Unit, 10f);
            Assert.That(network.Compromised, Is.True);
            Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Observed), "room B is the camera's own room");

            // The friendly walks 5.4 m away from the hostile, out of its 4 m sight. The camera, 12.9 m away to the east with
            // range 30 and the hostile dead ahead in its cone, must still not see it: the closed wall is in the ray.
            rig.Friendly.transform.position = new Vector3(-8.5f, 1f, 1.5f);
            Physics.SyncTransforms();
            rig.Service.RunPass();
            Assert.That(rig.Service.CanTarget(inA), Is.False);
        }

        [UnityTest]
        public IEnumerator WithSnapshotCameras_TheHackRevealsTheRoomOnce_AndEnemiesAreNotObservedAfterwards()
        {
            rig = new IntelRig(corridor: false);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            var terminal = CameraTerminal();
            rig.Begin(IntelRig.Fog(s => s.cameraStaysLive = false), CameraMission(terminal, out _));
            yield return null;
            terminal.TryBegin(rig.Friendly.Unit);
            terminal.Advance(rig.Friendly.Unit, 10f);
            Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Discovered), "the layout is revealed for good");
            Assert.That(rig.Service.CanTarget(hostile), Is.False, "but nothing is watched live");
        }

        [UnityTest]
        public IEnumerator ACamera_IsADeviceTheSquadDiscoversBySeeingIt()
        {
            rig = new IntelRig(corridor: true);
            var terminal = CameraTerminal();
            rig.Begin(IntelRig.Fog(s => s.observationRange = 30f), CameraMission(terminal, out _));
            yield return null;
            var cameraDevice = 1;
            Assert.That(rig.Service.Model.StateOfDevice(cameraDevice), Is.EqualTo(KnowledgeState.Discovered),
                "visible down the open corridor from the friendly");
            Assert.That(rig.Service.IsDeviceShown(cameraDevice), Is.True);
        }

        // ---- the briefing ----

        static IntelligenceMission BriefingMission() => new IntelligenceMission
        {
            Objectives = new ObjectivePlan(1, new Vector2Int(16, 4), new Vector2Int[0], 1, new Vector2Int(16, 2)),
        };

        [UnityTest]
        public IEnumerator FullMapKnowledge_DiscoversEveryRegionAtTheStart()
        {
            rig = new IntelRig(corridor: false);
            rig.Begin(IntelRig.Fog(s => s.map = MapKnowledge.Full));
            yield return null;
            Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Discovered));
        }

        [UnityTest]
        public IEnumerator PartialMapKnowledge_IsTheStartRoomItsCorridorsAndTheExtractionRoom()
        {
            rig = new IntelRig(corridor: false);
            rig.Begin(IntelRig.Fog(s => s.map = MapKnowledge.Partial), BriefingMission());
            yield return null;
            Assert.That(rig.Service.StateOfRegion(0), Is.Not.EqualTo(KnowledgeState.Unknown));
            Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Discovered), "room 1 holds the extraction");
        }

        [UnityTest]
        public IEnumerator EnemyMarkersAtStart_MakeTheFirstHostilesLastKnown()
        {
            rig = new IntelRig(corridor: false);
            var first = rig.AddHostile(IntelRig.InLineGround);
            var second = rig.AddHostile(IntelRig.OffAxisGround);
            rig.Begin(IntelRig.Fog(s => s.enemyMarkersAtStart = 1));
            yield return null;
            Assert.That(rig.Service.StateOfEnemy(first), Is.EqualTo(KnowledgeState.Discovered));
            Assert.That(rig.Service.CanTarget(first), Is.False, "a marker is not a target");
            Assert.That(rig.Service.TryLastKnown(first, out _), Is.True);
            Assert.That(rig.Service.StateOfEnemy(second), Is.EqualTo(KnowledgeState.Unknown));
        }

        // ---- queries used by the other systems ----

        [UnityTest]
        public IEnumerator CanInteract_FollowsTheRegionOfADataTerminal_AndTheDeviceOfTheCameraTerminal()
        {
            rig = new IntelRig(corridor: false);
            var cameraTerminal = CameraTerminal();
            cameraTerminal.transform.position = rig.Layout.TileCenter(new Vector2Int(2, 2));
            var dataHost = rig.World.Track(new GameObject("DataTerminal"));
            dataHost.transform.position = rig.Layout.TileCenter(new Vector2Int(16, 4));   // room B
            var data = dataHost.AddComponent<MissionInteractable>();
            data.Initialize(1.8f, 2f);
            // The plan's terminal tile is the grid's far corner, out of the friendly's range and sight: its device stays undiscovered.
            rig.Begin(IntelRig.Fog(), CameraMission(cameraTerminal, out _, terminalTile: new Vector2Int(19, 9)));
            yield return null;
            Assert.That(rig.Service.CanInteract(data), Is.False, "room B is unknown");
            Assert.That(rig.Service.CanInteract(cameraTerminal), Is.False, "the camera terminal device is not discovered yet");
            rig.Service.Model.RevealRegion(1);
            rig.Service.Model.RevealDevice(SecurityPlan.TerminalDeviceId);
            Assert.That(rig.Service.CanInteract(data), Is.True);
            Assert.That(rig.Service.CanInteract(cameraTerminal), Is.True);
        }

        [UnityTest]
        public IEnumerator CanSeeCover_NeedsAKnownRegion()
        {
            rig = new IntelRig(corridor: false);
            rig.Begin(IntelRig.Fog());
            yield return null;
            var inA = new CoverLocation("A", rig.Layout.TileCenter(new Vector2Int(3, 3)), Vector3.forward, null);
            var inB = new CoverLocation("B", rig.Layout.TileCenter(new Vector2Int(16, 3)), Vector3.forward, null);
            Assert.That(rig.Service.CanSeeCover(inA), Is.True);
            Assert.That(rig.Service.CanSeeCover(inB), Is.False);
        }
    }
}
#endif
