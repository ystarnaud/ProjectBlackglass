using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>The bounded checks every map and objective list passes before it is sent and after it is received (decision 052).</summary>
    public class NetMapValidationTests
    {
        static NetMap Map(Action<List<RectInt>, List<NetMapBox>, List<Vector2Int>> change = null, int width = 10, int height = 8)
        {
            var floors = new List<RectInt> { new RectInt(1, 1, 4, 3), new RectInt(5, 1, 3, 3) };
            var boxes = new List<NetMapBox>
            {
                new NetMapBox(MissionBoxKind.Wall, new RectInt(0, 0, 10, 1), 3f),
                new NetMapBox(MissionBoxKind.Crate, new RectInt(2, 2, 1, 1), 1f),
            };
            var containers = new List<Vector2Int> { new Vector2Int(6, 2) };
            change?.Invoke(floors, boxes, containers);
            return new NetMap(width, height, floors, boxes, containers);
        }

        static List<NetObjective> Objectives() => new List<NetObjective>
        {
            new NetObjective(NetObjectiveKind.Eliminate, ObjectiveState.Active, "Eliminate security team", false, Vector3.zero, 0f),
            new NetObjective(NetObjectiveKind.Extraction, ObjectiveState.Inactive, "Extraction", true, new Vector3(-9.5f, 0f, 4.5f), 2f),
        };

        [Test] public void AWellFormedMap_HasNoProblem() => Assert.That(NetMapValidation.Problem(Map()), Is.Null);
        [Test] public void NoMap_IsAProblem() => Assert.That(NetMapValidation.Problem((NetMap)null), Is.Not.Null);

        [TestCase(0, 8)] [TestCase(10, 0)] [TestCase(129, 8)] [TestCase(10, 129)]
        public void AGridOutsideOneTo128_IsRefused(int width, int height) =>
            StringAssert.Contains("grid", NetMapValidation.Problem(Map(width: width, height: height)));

        [Test] public void AMapWithNoFloor_IsRefused() => StringAssert.Contains("floor", NetMapValidation.Problem(Map((f, b, c) => { f.Clear(); c.Clear(); })));
        [Test] public void AFloorOutsideTheGrid_IsRefused() => StringAssert.Contains("outside", NetMapValidation.Problem(Map((f, b, c) => f.Add(new RectInt(8, 6, 3, 3)))));
        [Test] public void AnEmptyFloorRectangle_IsRefused() => StringAssert.Contains("outside", NetMapValidation.Problem(Map((f, b, c) => f.Add(new RectInt(1, 5, 0, 2)))));
        [Test] public void OverlappingFloors_AreRefused() => StringAssert.Contains("overlaps", NetMapValidation.Problem(Map((f, b, c) => f.Add(new RectInt(2, 2, 1, 1)))));

        [Test]
        public void MoreThan1024Floors_AreRefusedBeforeAnyIsRead()
        {
            var floors = Enumerable.Range(0, NetMap.MaxFloors + 1).Select(i => new RectInt(i % 128, i / 128, 1, 1)).ToList();
            StringAssert.Contains("floors", NetMapValidation.Problem(new NetMap(128, 128, floors, new List<NetMapBox>(), new List<Vector2Int>())));
        }

        [Test] public void ABoxOutsideTheGrid_IsRefused() => StringAssert.Contains("box", NetMapValidation.Problem(Map((f, b, c) => b.Add(new NetMapBox(MissionBoxKind.Pillar, new RectInt(9, 7, 2, 2), 3f)))));
        [Test] public void AnUnknownBoxKind_IsRefused() => StringAssert.Contains("kind", NetMapValidation.Problem(Map((f, b, c) => b.Add(new NetMapBox((MissionBoxKind)9, new RectInt(3, 3, 1, 1), 1f)))));

        [TestCase(0f)] [TestCase(-1f)] [TestCase(4.5f)] [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)]
        public void ABoxRiseOutsideZeroToFour_IsRefused(float rise) =>
            StringAssert.Contains("rise", NetMapValidation.Problem(Map((f, b, c) => b.Add(new NetMapBox(MissionBoxKind.Crate, new RectInt(3, 3, 1, 1), rise)))));

        [Test]
        public void MoreThan2048Boxes_AreRefused() =>
            StringAssert.Contains("boxes", NetMapValidation.Problem(Map((f, b, c) => b.AddRange(Enumerable.Repeat(new NetMapBox(MissionBoxKind.Crate, new RectInt(2, 2, 1, 1), 1f), NetMap.MaxBoxes)))));

        [Test] public void AContainerOffTheFloor_IsRefused() => StringAssert.Contains("container", NetMapValidation.Problem(Map((f, b, c) => c.Add(new Vector2Int(0, 0)))));
        [Test] public void AContainerOutsideTheGrid_IsRefused() => StringAssert.Contains("container", NetMapValidation.Problem(Map((f, b, c) => c.Add(new Vector2Int(40, 2)))));
        [Test] public void MoreThan64Containers_AreRefused() => StringAssert.Contains("containers", NetMapValidation.Problem(Map((f, b, c) => c.AddRange(Enumerable.Repeat(new Vector2Int(6, 2), NetMap.MaxContainers)))));

        [Test] public void WellFormedObjectives_HaveNoProblem() => Assert.That(NetMapValidation.Problem(Objectives()), Is.Null);
        [Test] public void NoObjectiveList_IsAProblem() => Assert.That(NetMapValidation.Problem((IReadOnlyList<NetObjective>)null), Is.Not.Null);

        [Test]
        public void MoreThanEightObjectives_AreRefused() =>
            StringAssert.Contains("objectives", NetMapValidation.Problem(Enumerable.Repeat(Objectives()[0], NetObjective.MaxCount + 1).ToList()));

        [TestCase((NetObjectiveKind)0)] [TestCase((NetObjectiveKind)9)]
        public void AnUnknownObjectiveKind_IsRefused(NetObjectiveKind kind) =>
            StringAssert.Contains("kind", NetMapValidation.Problem(new List<NetObjective> { new NetObjective(kind, ObjectiveState.Active, "x", false, Vector3.zero, 0f) }));

        [Test]
        public void AnUnknownObjectiveState_IsRefused() =>
            StringAssert.Contains("state", NetMapValidation.Problem(new List<NetObjective> { new NetObjective(NetObjectiveKind.Eliminate, (ObjectiveState)7, "x", false, Vector3.zero, 0f) }));

        [TestCase("")] [TestCase("line\nbreak")] [TestCase("0123456789012345678901234567890123456789012345678")]
        public void ATitleThatIsEmptyTooLongOrUnprintable_IsRefused(string title) =>
            StringAssert.Contains("title", NetMapValidation.Problem(new List<NetObjective> { new NetObjective(NetObjectiveKind.Eliminate, ObjectiveState.Active, title, false, Vector3.zero, 0f) }));

        [Test]
        public void AnExtractionPointThatIsNotFiniteOrOffTheMap_IsRefused()
        {
            foreach (var point in new[] { new Vector3(float.NaN, 0f, 0f), new Vector3(300f, 0f, 0f), new Vector3(0f, 50f, 0f) })
                StringAssert.Contains("point", NetMapValidation.Problem(new List<NetObjective> { new NetObjective(NetObjectiveKind.Extraction, ObjectiveState.Active, "Extraction", true, point, 2f) }));
        }

        [TestCase(0f)] [TestCase(11f)] [TestCase(float.NaN)]
        public void AnExtractionRadiusOutsideZeroToTen_IsRefused(float radius) =>
            StringAssert.Contains("radius", NetMapValidation.Problem(new List<NetObjective> { new NetObjective(NetObjectiveKind.Extraction, ObjectiveState.Active, "Extraction", true, Vector3.zero, radius) }));

        [Test]
        public void OnlyExtractionCarriesAPoint() =>
            StringAssert.Contains("point", NetMapValidation.Problem(new List<NetObjective> { new NetObjective(NetObjectiveKind.Eliminate, ObjectiveState.Active, "x", true, Vector3.zero, 0f) }));
    }
}
