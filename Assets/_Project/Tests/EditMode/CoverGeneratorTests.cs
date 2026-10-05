using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class CoverGeneratorTests
    {
        static readonly Func<Vector3, bool> Everywhere = _ => true;

        static CoverBox Box(string name, Vector3 center, Vector3 size, float yaw = 0f) =>
            new CoverBox(name, center, yaw, size * 0.5f, null);

        static List<CoverLocation> Generate(CoverBox box, Func<Vector3, bool> walkable = null, CoverGenerationSettings settings = null) =>
            CoverGenerator.Generate(new[] { box }, settings ?? new CoverGenerationSettings(), walkable ?? Everywhere);

        static Vector3 P(float x, float y, float z) => new Vector3(x, y, z);

        [Test]
        public void ThreeMetreLowWall_GivesTwoPointsOnEachLongFace_AndNoneOnItsEnds()
        {
            // LowWall_L of the prototype arena: x 5..8, z -3.25..-2.75, 0.9 m tall.
            var result = Generate(Box("LowWall_L", P(6.5f, 0.45f, -3f), P(3f, 0.9f, 0.5f)));

            Assert.That(result, Has.Count.EqualTo(4));
            Assert.That(result.Select(l => l.Name), Is.EquivalentTo(new[]
                { "Cover_LowWall_L_N1", "Cover_LowWall_L_N2", "Cover_LowWall_L_S1", "Cover_LowWall_L_S2" }));
            var s1 = result.Single(l => l.Name == "Cover_LowWall_L_S1");
            var s2 = result.Single(l => l.Name == "Cover_LowWall_L_S2");
            Assert.That(s1.Position.x, Is.EqualTo(5.5f).Within(0.001f));
            Assert.That(s1.Position.y, Is.EqualTo(0f).Within(0.001f));
            Assert.That(s1.Position.z, Is.EqualTo(-4f).Within(0.001f), "0.75 m south of the south face");
            Assert.That(s2.Position.x, Is.EqualTo(7.5f).Within(0.001f));
            Assert.That(s1.Facing.z, Is.EqualTo(1f).Within(0.001f), "South points face north, into the wall");
            var n1 = result.Single(l => l.Name == "Cover_LowWall_L_N1");
            Assert.That(n1.Position.z, Is.EqualTo(-2f).Within(0.001f));
            Assert.That(n1.Facing.z, Is.EqualTo(-1f).Within(0.001f));
            Assert.That(result.All(l => l.Height == CoverHeight.Low && l.Placement == CoverPlacement.Face && !l.HasPeek), Is.True);
        }

        [Test]
        public void FaceLength_DecidesHowManyPointsAFaceGets()
        {
            Assert.That(Generate(Box("Short", P(0f, 0.45f, 0f), P(2f, 0.9f, 0.5f))), Has.Count.EqualTo(2), "a 2 m face: one point each");
            Assert.That(Generate(Box("Four", P(0f, 0.45f, 0f), P(4f, 0.9f, 0.5f))), Has.Count.EqualTo(4), "a 4 m face: two each");
            Assert.That(Generate(Box("Six", P(0f, 0.45f, 0f), P(6f, 0.9f, 0.5f))), Has.Count.EqualTo(6), "a 6 m face: three each");
        }

        [Test]
        public void FacesShorterThanMinFaceLength_GetNoPoints()
        {
            var result = Generate(Box("Thin", P(0f, 0.45f, 0f), P(0.8f, 0.9f, 0.5f)));
            Assert.That(result, Is.Empty, "0.8 m by 0.5 m: every face is shorter than 1 m");
        }

        [Test]
        public void Pillar_GetsOnePointPerFace_AllTall_NoneWithPeek()
        {
            var result = Generate(Box("Pillar_G", P(-2f, 1.5f, -6f), P(1.5f, 3f, 1.5f)));
            Assert.That(result, Has.Count.EqualTo(4));
            Assert.That(result.All(l => l.Height == CoverHeight.Tall && l.Placement == CoverPlacement.Face && !l.HasPeek), Is.True);
            var south = result.Single(l => l.Name == "Cover_Pillar_G_S1");
            Assert.That(south.Position.x, Is.EqualTo(-2f).Within(0.001f));
            Assert.That(south.Position.z, Is.EqualTo(-7.5f).Within(0.001f), "0.75 m from the face at -6.75");
            var west = result.Single(l => l.Name == "Cover_Pillar_G_W1");
            Assert.That(west.Position.x, Is.EqualTo(-3.5f).Within(0.001f));
            Assert.That(west.Facing.x, Is.EqualTo(1f).Within(0.001f));
        }

        [Test]
        public void Crate_TwoByTwo_IsNotAWall_FourFacePointsNoCorners()
        {
            var result = Generate(Box("Crate_J", P(10f, 1f, -5f), P(2f, 2f, 2f)));
            Assert.That(result, Has.Count.EqualTo(4));
            Assert.That(result.Any(l => l.Placement == CoverPlacement.Corner), Is.False);
        }

        [Test]
        public void HeightThreshold_DecidesLowOrTall()
        {
            Assert.That(Generate(Box("A", P(0f, 0.6f, 0f), P(2f, 1.2f, 0.5f)))[0].Height, Is.EqualTo(CoverHeight.Low), "1.2 m is Low");
            Assert.That(Generate(Box("B", P(0f, 0.65f, 0f), P(2f, 1.3f, 0.5f)))[0].Height, Is.EqualTo(CoverHeight.Tall), "1.3 m is Tall");
        }

        [Test]
        public void NonWalkableCandidates_AreDropped()
        {
            var result = Generate(Box("Wall", P(0f, 0.45f, 0f), P(3f, 0.9f, 0.5f)), walkable: p => p.z < 0f);
            Assert.That(result, Has.Count.EqualTo(2));
            Assert.That(result.All(l => l.Position.z < 0f), Is.True);
        }

        [Test]
        public void DuplicateSurfaces_DoNotStackPoints()
        {
            var one = Box("A", P(0f, 0.45f, 0f), P(3f, 0.9f, 0.5f));
            var two = Box("B", P(0f, 0.45f, 0f), P(3f, 0.9f, 0.5f));
            var result = CoverGenerator.Generate(new[] { one, two }, new CoverGenerationSettings(), Everywhere);
            Assert.That(result, Has.Count.EqualTo(4), "The second surface's points all fall inside the merge distance of the first's");
            Assert.That(result.All(l => l.Name.StartsWith("Cover_A_")), Is.True);
        }

        [Test]
        public void HitChance_IsCopiedFromTheSettings()
        {
            var settings = new CoverGenerationSettings { hitChance = 0.3f };
            var result = Generate(Box("Wall", P(0f, 0.45f, 0f), P(3f, 0.9f, 0.5f)), settings: settings);
            Assert.That(result.All(l => Mathf.Approximately(l.HitChance, 0.3f)), Is.True);
        }

        [Test]
        public void Generate_RejectsNullArguments()
        {
            var boxes = new CoverBox[0];
            Assert.Throws<ArgumentNullException>(() => CoverGenerator.Generate(null, new CoverGenerationSettings(), Everywhere));
            Assert.Throws<ArgumentNullException>(() => CoverGenerator.Generate(boxes, null, Everywhere));
            Assert.Throws<ArgumentNullException>(() => CoverGenerator.Generate(boxes, new CoverGenerationSettings(), null));
        }

        [Test]
        public void Generate_IsDeterministic()
        {
            var box = Box("Wall", P(0f, 1f, 0f), P(8f, 2f, 1f), 30f);
            var first = Generate(box).Select(l => (l.Name, l.Position)).ToList();
            var second = Generate(box).Select(l => (l.Name, l.Position)).ToList();
            Assert.That(second, Is.EqualTo(first));
        }

        // Barrier_I of the arena: 6 x 1, 2 m tall, centred (-8, 1, 2).
        static CoverBox Barrier() => Box("Barrier_I", P(-8f, 1f, 2f), P(6f, 2f, 1f));

        [Test]
        public void TallWall_GivesFourCorners_OneAtEachEndOfEachLongFace()
        {
            var result = Generate(Barrier());
            var corners = result.Where(l => l.Placement == CoverPlacement.Corner).ToList();
            Assert.That(corners, Has.Count.EqualTo(4));
            Assert.That(corners.All(l => l.Height == CoverHeight.Tall && l.HasPeek), Is.True);
            Assert.That(corners.Select(l => l.Name), Is.EquivalentTo(new[]
                { "Cover_Barrier_I_NCorner1", "Cover_Barrier_I_NCorner2", "Cover_Barrier_I_SCorner1", "Cover_Barrier_I_SCorner2" }));
        }

        [Test]
        public void Corner_StandsInTheWallsShadow_AndPeeksOutPastTheEnd()
        {
            var south = Generate(Barrier()).Single(l => l.Name == "Cover_Barrier_I_SCorner2");   // the east end of the south face
            Assert.That(south.Position.x, Is.EqualTo(-5.35f).Within(0.001f), "0.35 m inside the wall end at x -5");
            Assert.That(south.Position.z, Is.EqualTo(0.75f).Within(0.001f), "0.75 m south of the south face at z 1.5");
            Assert.That(south.Facing.z, Is.EqualTo(1f).Within(0.001f), "Faces the wall");
            Assert.That(south.PeekDirection.x, Is.EqualTo(1f).Within(0.001f), "Peeks east, out past the end");
            Assert.That(south.PeekPoint.x, Is.EqualTo(-4.1f).Within(0.001f), "1.25 m from the stand point");
            Assert.That(south.PeekPoint.z, Is.EqualTo(0.75f).Within(0.001f));
            var west = Generate(Barrier()).Single(l => l.Name == "Cover_Barrier_I_SCorner1");
            Assert.That(west.Position.x, Is.EqualTo(-10.65f).Within(0.001f));
            Assert.That(west.PeekDirection.x, Is.EqualTo(-1f).Within(0.001f));
        }

        [Test]
        public void ACorner_SupersedesTheFacePointNearestTheWallEnd()
        {
            var result = Generate(Barrier());
            var southFace = result.Where(l => l.Facing.z > 0.5f).ToList();
            Assert.That(southFace.Count(l => l.Placement == CoverPlacement.Face), Is.EqualTo(1), "Only the centre face point survives");
            Assert.That(southFace.Single(l => l.Placement == CoverPlacement.Face).Position.x, Is.EqualTo(-8f).Within(0.001f));
            Assert.That(result, Has.Count.EqualTo(8), "4 corners + 2 centre points + 2 end-face points");
        }

        [Test]
        public void LowWalls_NeverGetCorners()
        {
            var result = Generate(Box("Low", P(0f, 0.45f, 0f), P(8f, 0.9f, 1f)));
            Assert.That(result.Any(l => l.Placement == CoverPlacement.Corner), Is.False);
        }

        [Test]
        public void ATallBoxThatIsNotMuchLongerThanItIsThick_GetsNoCorners()
        {
            Assert.That(Generate(Box("Stubby", P(0f, 1f, 0f), P(3f, 2f, 2f))).Any(l => l.Placement == CoverPlacement.Corner), Is.False, "3 x 2 is not twice as long as thick");
            Assert.That(Generate(Box("Short", P(0f, 1f, 0f), P(1.5f, 2f, 0.5f))).Any(l => l.Placement == CoverPlacement.Corner), Is.False, "1.5 m is under minCornerLength");
        }

        [Test]
        public void AWallTurnedFortyFiveDegrees_GivesTheSameCounts_AndUnitDirections()
        {
            var straight = Generate(Box("Wall", P(0f, 1f, 0f), P(8f, 2f, 1f), 0f));
            var turned = Generate(Box("Wall", P(0f, 1f, 0f), P(8f, 2f, 1f), 45f));
            Assert.That(turned, Has.Count.EqualTo(straight.Count));
            Assert.That(turned.Count(l => l.Placement == CoverPlacement.Corner), Is.EqualTo(4));
            foreach (var corner in turned.Where(l => l.Placement == CoverPlacement.Corner))
            {
                Assert.That(corner.Facing.magnitude, Is.EqualTo(1f).Within(1e-4f));
                Assert.That(corner.PeekDirection.magnitude, Is.EqualTo(1f).Within(1e-4f));
                Assert.That(Vector3.Dot(corner.Facing, corner.PeekDirection), Is.EqualTo(0f).Within(1e-4f), "Peek runs along the wall, not through it");
            }
        }

        [Test]
        public void ACornerWhosePeekPointIsOffTheNavMesh_StaysACornerWithoutPeekData()
        {
            // Reject only the south side's peek points (z 0.75, more than 3.5 m from the wall centre along x).
            Func<Vector3, bool> walkable = p => !(Mathf.Abs(p.x + 8f) > 3.5f && Mathf.Abs(p.z - 0.75f) < 0.1f);
            var result = Generate(Barrier(), walkable);

            var southCorners = result.Where(l => l.Placement == CoverPlacement.Corner && l.Facing.z > 0.5f).ToList();
            Assert.That(southCorners, Has.Count.EqualTo(2), "The corners themselves are still walkable");
            Assert.That(southCorners.All(l => !l.HasPeek && l.PeekPoint == Vector3.zero), Is.True);
            var northCorners = result.Where(l => l.Placement == CoverPlacement.Corner && l.Facing.z < -0.5f).ToList();
            Assert.That(northCorners, Has.Count.EqualTo(2));
            Assert.That(northCorners.All(l => l.HasPeek), Is.True);
        }

        [Test]
        public void ANonWalkableCornerStandPoint_DropsTheCorner()
        {
            var result = Generate(Barrier(), p => p.x > -9f);   // only the east half is walkable
            Assert.That(result.Count(l => l.Placement == CoverPlacement.Corner), Is.EqualTo(2), "Only the two east corners remain");
        }
    }
}
