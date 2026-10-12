#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class NetLoopbackPlayModeTests
    {
        NetLoopbackRig rig;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            rig = new NetLoopbackRig();
            yield return rig.Start();
            rig.BeginNet();
        }

        [UnityTearDown]
        public IEnumerator TearDown() => rig.Stop();

        IEnumerator Await(uint command, NetRejectReason expected)
        {
            yield return TestWorld.WaitUntil(() => rig.Client.TryGetResult(command, out _), 5f);
            Assert.That(rig.Client.TryGetResult(command, out var reason), Is.True, "no answer to command " + command);
            Assert.That(reason, Is.EqualTo(expected));
        }

        [UnityTest]
        public IEnumerator TheClient_IsWelcomed_WithItsSquad()
        {
            yield return rig.Connect(NetLoopbackRig.TokenA);

            Assert.That(rig.Client.State, Is.EqualTo(NetClientState.Ready), "state " + rig.Client.State + " " + rig.Client.Refusal);
            Assert.That(rig.Client.AttemptId, Is.EqualTo(rig.Director.InstanceId));
            Assert.That(rig.Client.FirstOwnedId, Is.EqualTo(1));
            Assert.That(rig.Client.OwnedCount, Is.EqualTo(2));
            Assert.That(rig.Server.ConnectionCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator AnUnknownToken_IsRefused()
        {
            yield return rig.Connect("tok-secret-probe");
            Assert.That(rig.Client.State, Is.EqualTo(NetClientState.Refused));
            Assert.That(rig.Client.Refusal, Is.EqualTo(NetRefusal.UnknownToken));
        }

        [UnityTest]
        public IEnumerator ATicketForAnotherAttempt_IsRefused()
        {
            yield return rig.Connect(NetLoopbackRig.TokenTicket);
            Assert.That(rig.Client.State, Is.EqualTo(NetClientState.Refused));
            Assert.That(rig.Client.Refusal, Is.EqualTo(NetRefusal.WrongAttempt));
        }

        [UnityTest]
        public IEnumerator ASteeringRequest_MovesTheUnit_AndExpiresWithoutARefresh()
        {
            yield return rig.Connect(NetLoopbackRig.TokenA);
            var unit = rig.Director.Friendlies[0];

            var command = rig.Client.Send(new NetMoveIntent(1, Vector3.right));
            yield return Await(command, NetRejectReason.None);
            Assert.That(unit.MoveIntent, Is.Not.EqualTo(Vector3.zero), "the intent reached the simulation unit");

            yield return TestWorld.WaitUntil(() => unit.MoveIntent == Vector3.zero, 3f);
            Assert.That(unit.MoveIntent, Is.EqualTo(Vector3.zero), "the server let go when the client stopped refreshing");
        }

        [UnityTest]
        public IEnumerator ARequestForAHostile_IsAnsweredNotOwner()
        {
            yield return rig.Connect(NetLoopbackRig.TokenA);
            var command = rig.Client.Send(new NetMoveTo(3, Vector3.zero));
            yield return Await(command, NetRejectReason.NotOwner);
            Assert.That(rig.Director.Hostiles[0].CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator AnAccountWithoutAUnit_CannotOrderAnyOfThem()
        {
            yield return rig.Connect(NetLoopbackRig.TokenB);
            Assert.That(rig.Client.State, Is.EqualTo(NetClientState.Ready));
            Assert.That(rig.Client.OwnedCount, Is.EqualTo(0));

            var command = rig.Client.Send(new NetStop(1));
            yield return Await(command, NetRejectReason.NotOwner);
        }

        [UnityTest]
        public IEnumerator NoTokenIsEverLogged()
        {
            yield return rig.Connect(NetLoopbackRig.TokenA);
            var command = rig.Client.Send(new NetMoveTo(3, Vector3.zero));
            yield return Await(command, NetRejectReason.NotOwner);
            rig.Client.Disconnect();
            yield return null;
            yield return rig.Connect("tok-secret-probe");
            yield return rig.Connect(NetLoopbackRig.TokenTicket);

            foreach (var token in new[] { NetLoopbackRig.TokenA, NetLoopbackRig.TokenB, NetLoopbackRig.TokenTicket, "tok-secret-probe" })
                Assert.That(rig.Logs.Where(line => line.Contains(token)).ToList(), Is.Empty, "a log line contains the token " + token);
            Assert.That(rig.Logs.Any(line => line.StartsWith(NetServer.Prefix)), Is.True, "the server did log its activity");
        }
    }
}
#endif
