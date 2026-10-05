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

        // The shape tests below (faces, corners, names, yaw, heights, walkability) pin the geometry rules at the original
        // spacing, 2 m, and merge distance, 1 m, so their hand-derived counts and positions stay valid. The tests further
        // down cover the current defaults.
        static CoverGenerationSettings Original() => new CoverGenerationSettings { spacing = 2f, mergeDistance = 1f };

        [Test]
        public void ThreeMetreLowWall_GivesTwoPointsOnEachLongFace_AndNoneOnItsEnds()
        {
            // LowWall_L of the prototype arena: x 5..8, z -3.25..-2.75, 0.9 m tall.
            var result = Generate(Box("LowWall_L", P(6.5f, 0.45f, -3f), P(3f, 0.9f, 0.5f)), settings: Original());

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
            Assert.That(Generate(Box("Short", P(0f, 0.45f, 0f), P(2f, 0.9f, 0.5f)), settings: Original()), Has.Count.EqualTo(2), "a 2 m face: one point each");
            Assert.That(Generate(Box("Four", P(0f, 0.45f, 0f), P(4f, 0.9f, 0.5f)), settings: Original()), Has.Count.EqualTo(4), "a 4 m face: two each");
            Assert.That(Generate(Box("Six", P(0f, 0.45f, 0f), P(6f, 0.9f, 0.5f)), settings: Original()), Has.Count.EqualTo(6), "a 6 m face: three each");
        }

        [Test]
        public void FacesShorterThanMinFaceLength_GetNoPoints()
        {
            var result = Generate(Box("Thin", P(0f, 0.45f, 0f), P(0.8f, 0.9f, 0.5f)), settings: Original());
            Assert.That(result, Is.Empty, "0.8 m by 0.5 m: every face is shorter than 1 m");
        }

        [Test]
        public void Pillar_GetsOnePointPerFace_AllTall_NoneWithPeek()
        {
            var result = Generate(Box("Pillar_G", P(-2f, 1.5f, -6f), P(1.5f, 3f, 1.5f)), settings: Original());
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
            var result = Generate(Box("Crate_J", P(10f, 1f, -5f), P(2f, 2f, 2f)), settings: Original());
            Assert.That(result, Has.Count.EqualTo(4));
            Assert.That(result.Any(l => l.Placement == CoverPlacement.Corner), Is.False);
        }

        [Test]
        public void HeightThreshold_DecidesLowOrTall()
        {
            Assert.That(Generate(Box("A", P(0f, 0.6f, 0f), P(2f, 1.2f, 0.5f)), settings: Original())[0].Height, Is.EqualTo(CoverHeight.Low), "1.2 m is Low");
            Assert.That(Generate(Box("B", P(0f, 0.65f, 0f), P(2f, 1.3f, 0.5f)), settings: Original())[0].Height, Is.EqualTo(CoverHeight.Tall), "1.3 m is Tall");
        }

        [Test]
        public void NonWalkableCandidates_AreDropped()
        {
            var result = Generate(Box("Wall", P(0f, 0.45f, 0f), P(3f, 0.9f, 0.5f)), walkable: p => p.z < 0f, settings: Original());
            Assert.That(result, Has.Count.EqualTo(2));
            Assert.That(result.All(l => l.Position.z < 0f), Is.True);
        }

        [Test]
        public void HitChance_IsCopiedFromTheSettings()
        {
            var settings = new CoverGenerationSettings { hitChance = 0.3f };
            var result = Generate(Box("Wall", P(0f, 0.45f, 0f), P(3f, 0.9f, 0.5f)), settings: settings);
            Assert.That(result.All(l => Mathf.Approximately(l.HitChance, 0.3f)), Is.True);
        }

        [Test]
        public void ASpacingOfZeroOrLess_StillGenerates_WithEveryLocationAtLeastTheMergeDistanceApart()
        {
            foreach (var spacing in new[] { 0f, -1f })
            {
                var settings = new CoverGenerationSettings { spacing = spacing };
                var result = Generate(Box("Wall", P(0f, 0.45f, 0f), P(3f, 0.9f, 0.5f)), settings: settings);
                Assert.That(result, Is.Not.Empty, $"spacing {spacing}");
                for (var i = 0; i < result.Count; i++)
                    for (var j = i + 1; j < result.Count; j++)
                        Assert.That(CoverRules.FlatDistance(result[i].Position, result[j].Position), Is.GreaterThanOrEqualTo(settings.mergeDistance),
                            $"spacing {spacing}: {result[i].Name} and {result[j].Name}");
            }
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
            var result = Generate(Barrier(), settings: Original());
            var corners = result.Where(l => l.Placement == CoverPlacement.Corner).ToList();
            Assert.That(corners, Has.Count.EqualTo(4));
            Assert.That(corners.All(l => l.Height == CoverHeight.Tall && l.HasPeek), Is.True);
            Assert.That(corners.Select(l => l.Name), Is.EquivalentTo(new[]
                { "Cover_Barrier_I_NCorner1", "Cover_Barrier_I_NCorner2", "Cover_Barrier_I_SCorner1", "Cover_Barrier_I_SCorner2" }));
        }

        [Test]
        public void Corner_StandsInTheWallsShadow_AndPeeksOutPastTheEnd()
        {
            var south = Generate(Barrier(), settings: Original()).Single(l => l.Name == "Cover_Barrier_I_SCorner2");   // the east end of the south face
            Assert.That(south.Position.x, Is.EqualTo(-5.35f).Within(0.001f), "0.35 m inside the wall end at x -5");
            Assert.That(south.Position.z, Is.EqualTo(0.75f).Within(0.001f), "0.75 m south of the south face at z 1.5");
            Assert.That(south.Facing.z, Is.EqualTo(1f).Within(0.001f), "Faces the wall");
            Assert.That(south.PeekDirection.x, Is.EqualTo(1f).Within(0.001f), "Peeks east, out past the end");
            Assert.That(south.PeekPoint.x, Is.EqualTo(-4.1f).Within(0.001f), "1.25 m from the stand point");
            Assert.That(south.PeekPoint.z, Is.EqualTo(0.75f).Within(0.001f));
            var west = Generate(Barrier(), settings: Original()).Single(l => l.Name == "Cover_Barrier_I_SCorner1");
            Assert.That(west.Position.x, Is.EqualTo(-10.65f).Within(0.001f));
            Assert.That(west.PeekDirection.x, Is.EqualTo(-1f).Within(0.001f));
        }

        [Test]
        public void ACorner_SupersedesTheFacePointNearestTheWallEnd()
        {
            var result = Generate(Barrier(), settings: Original());
            var southFace = result.Where(l => l.Facing.z > 0.5f).ToList();
            Assert.That(southFace.Count(l => l.Placement == CoverPlacement.Face), Is.EqualTo(1), "Only the centre face point survives");
            Assert.That(southFace.Single(l => l.Placement == CoverPlacement.Face).Position.x, Is.EqualTo(-8f).Within(0.001f));
            Assert.That(result, Has.Count.EqualTo(8), "4 corners + 2 centre points + 2 end-face points");
        }

        [Test]
        public void LowWalls_NeverGetCorners()
        {
            var result = Generate(Box("Low", P(0f, 0.45f, 0f), P(8f, 0.9f, 1f)), settings: Original());
            Assert.That(result.Any(l => l.Placement == CoverPlacement.Corner), Is.False);
        }

        [Test]
        public void ATallBoxThatIsNotMuchLongerThanItIsThick_GetsNoCorners()
        {
            Assert.That(Generate(Box("Stubby", P(0f, 1f, 0f), P(3f, 2f, 2f)), settings: Original()).Any(l => l.Placement == CoverPlacement.Corner), Is.False, "3 x 2 is not twice as long as thick");
            Assert.That(Generate(Box("Short", P(0f, 1f, 0f), P(1.5f, 2f, 0.5f)), settings: Original()).Any(l => l.Placement == CoverPlacement.Corner), Is.False, "1.5 m is under minCornerLength");
        }

        [Test]
        public void AWallTurnedFortyFiveDegrees_GivesTheSameCounts_AndUnitDirections()
        {
            var straight = Generate(Box("Wall", P(0f, 1f, 0f), P(8f, 2f, 1f), 0f), settings: Original());
            var turned = Generate(Box("Wall", P(0f, 1f, 0f), P(8f, 2f, 1f), 45f), settings: Original());
            Assert.That(turned, Has.Count.EqualTo(straight.Count));
            Assert.That(turned.Count(l => l.Placement == CoverPlacement.Corner), Is.EqualTo(4));
            foreach (var corner in turned.Where(l => l.Placement == CoverPlacement.Corner))
            {
                Assert.That(corner.Facing.magnitude, Is.EqualTo(1f).Within(1e-4f));
                Assert.That(corner.PeekDirection.magnitude, Is.EqualTo(1f).Within(1e-4f));
                Assert.That(Vector3.Dot(corner.Facing, corner.PeekDirection), Is.EqualTo(0f).Within(1e-4f), "Peek runs along the wall, not through it");
                Assert.That(Mathf.Abs(corner.Facing.x), Is.EqualTo(0.7071f).Within(1e-3f), "A wall turned 45 degrees faces diagonally");
                Assert.That(Mathf.Abs(corner.Facing.z), Is.EqualTo(0.7071f).Within(1e-3f));
            }
        }

        [Test]
        public void AWallTurnedFortyFiveDegrees_GivesTheYawZeroLocationsRotatedAboutItsCentre()
        {
            var centre = P(0f, 1f, 0f);
            var rotation = Quaternion.Euler(0f, 45f, 0f);
            var straight = Generate(Box("Wall", centre, P(8f, 2f, 1f), 0f), settings: Original());
            var turned = Generate(Box("Wall", centre, P(8f, 2f, 1f), 45f), settings: Original());
            Assert.That(straight, Is.Not.Empty);
            Assert.That(turned, Has.Count.EqualTo(straight.Count));

            Vector3 AboutCentre(Vector3 position)
            {
                var turnedPosition = centre + rotation * new Vector3(position.x - centre.x, 0f, position.z - centre.z);
                return new Vector3(turnedPosition.x, position.y, turnedPosition.z);
            }

            var unmatched = new List<CoverLocation>(turned);
            foreach (var location in straight)
            {
                var expectedPosition = AboutCentre(location.Position);
                var match = unmatched.OrderBy(t => Vector3.Distance(t.Position, expectedPosition)).First();
                Assert.That(Vector3.Distance(match.Position, expectedPosition), Is.LessThan(1e-3f),
                    $"{location.Name} should turn to {expectedPosition}, nearest was {match.Position}");
                Assert.That(Vector3.Distance(match.Facing, rotation * location.Facing), Is.LessThan(1e-3f), location.Name + " facing");
                Assert.That(match.Placement, Is.EqualTo(location.Placement), location.Name);
                Assert.That(match.HasPeek, Is.EqualTo(location.HasPeek), location.Name);
                if (location.HasPeek)
                {
                    Assert.That(Vector3.Distance(match.PeekDirection, rotation * location.PeekDirection), Is.LessThan(1e-3f), location.Name + " peek direction");
                    Assert.That(Vector3.Distance(match.PeekPoint, AboutCentre(location.PeekPoint)), Is.LessThan(1e-3f), location.Name + " peek point");
                }
                unmatched.Remove(match);
            }
            Assert.That(unmatched, Is.Empty, "One-to-one: every turned location matches a rotated yaw-0 one");
        }

        [Test]
        public void AWallTurnedNinetyDegrees_SwapsItsCompassLabels()
        {
            var straight = Generate(Box("Wall", P(0f, 1f, 0f), P(6f, 2f, 1f), 0f), settings: Original());
            var turned = Generate(Box("Wall", P(0f, 1f, 0f), P(6f, 2f, 1f), 90f), settings: Original());
            var longFaces = turned.Where(l => Mathf.Abs(l.Facing.x) > 0.5f).ToList();
            Assert.That(longFaces, Is.Not.Empty);
            Assert.That(longFaces.All(l => l.Name.StartsWith("Cover_Wall_E") || l.Name.StartsWith("Cover_Wall_W")), Is.True,
                "The long faces of a wall turned 90 degrees look east and west");
            Assert.That(longFaces.Any(l => l.Name.StartsWith("Cover_Wall_ECorner")), Is.True);
            Assert.That(longFaces.Any(l => l.Name.StartsWith("Cover_Wall_WCorner")), Is.True);
            Assert.That(turned.Where(l => l.Placement == CoverPlacement.Corner).All(l => Mathf.Abs(l.Facing.x) > 0.99f), Is.True);
            Assert.That(straight.Where(l => l.Placement == CoverPlacement.Corner).All(l => l.Name.StartsWith("Cover_Wall_NCorner") || l.Name.StartsWith("Cover_Wall_SCorner")), Is.True);
        }

        [Test]
        public void ACornerWhosePeekPointIsOffTheNavMesh_StaysACornerWithoutPeekData()
        {
            // Reject only the south side's peek points (z 0.75, more than 3.5 m from the wall centre along x).
            Func<Vector3, bool> walkable = p => !(Mathf.Abs(p.x + 8f) > 3.5f && Mathf.Abs(p.z - 0.75f) < 0.1f);
            var result = Generate(Barrier(), walkable, Original());

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
            var result = Generate(Barrier(), p => p.x > -9f, Original());   // only the east half is walkable
            Assert.That(result.Count(l => l.Placement == CoverPlacement.Corner), Is.EqualTo(2), "Only the two east corners remain");
        }

        // ---- Current defaults: spacing 1 m, merge distance 0.9 m, merging only within one object ----

        [Test]
        public void ThreeMetreLowWall_AtDefaults_GivesThreePointsOnEachLongFace()
        {
            // usable length 3 - 2 * 0.5 = 2 m; floor(2 / 1) + 1 = 3 points, 1 m apart, at x 5.5, 6.5, 7.5.
            var result = Generate(Box("LowWall_L", P(6.5f, 0.45f, -3f), P(3f, 0.9f, 0.5f)));

            Assert.That(result, Has.Count.EqualTo(6));
            foreach (var side in new[] { "S", "N" })
                for (var i = 1; i <= 3; i++)
                {
                    var location = result.Single(l => l.Name == $"Cover_LowWall_L_{side}{i}");
                    Assert.That(location.Position.x, Is.EqualTo(4.5f + i).Within(0.001f), location.Name);
                    Assert.That(location.Position.z, Is.EqualTo(side == "S" ? -4f : -2f).Within(0.001f), location.Name);
                }
        }

        [Test]
        public void NeighbouringPointsOnAFace_AreAtLeastOneMetreAndUnderTwoMetresApart_AtDefaults()
        {
            // Lengths 2, 3, 4, 6 and 8 give usable 1, 2, 3, 5 and 7 m: counts 2, 3, 4, 6 and 8 and a gap of exactly
            // usable / (count - 1) = 1 m. The odd lengths give gaps of 1.5, 1.35 and about 1.075 m. A gap is never
            // under spacing (the count is a floor) and never reaches twice spacing (usable / floor(usable) < 2).
            foreach (var length in new[] { 2f, 3f, 4f, 6f, 8f, 2.5f, 3.7f, 5.3f })
            {
                var result = Generate(Box("Wall", P(0f, 0.45f, 0f), P(length, 0.9f, 0.5f)));
                foreach (var normalZ in new[] { 1f, -1f })
                {
                    var face = result.Where(l => Mathf.Sign(l.Facing.z) == normalZ).OrderBy(l => l.Position.x).ToList();
                    var expectedCount = Mathf.FloorToInt((length - 1f) / 1f + 1e-4f) + 1;
                    Assert.That(face, Has.Count.EqualTo(expectedCount), $"length {length}");
                    for (var i = 1; i < face.Count; i++)
                    {
                        var gap = face[i].Position.x - face[i - 1].Position.x;
                        Assert.That(gap, Is.GreaterThanOrEqualTo(1f - 1e-4f), $"length {length}: {face[i - 1].Name} to {face[i].Name}");
                        Assert.That(gap, Is.LessThan(2f), $"length {length}: {face[i - 1].Name} to {face[i].Name}");
                    }
                }
            }
        }

        [Test]
        public void OverlappingSurfaces_KeepEveryPointOfEachObject()
        {
            var one = Box("A", P(0f, 0.45f, 0f), P(3f, 0.9f, 0.5f));
            var two = Box("B", P(0f, 0.45f, 0f), P(3f, 0.9f, 0.5f));
            var settings = new CoverGenerationSettings();
            var single = CoverGenerator.Generate(new[] { one }, settings, Everywhere);
            var result = CoverGenerator.Generate(new[] { one, two }, settings, Everywhere);

            Assert.That(single, Has.Count.EqualTo(6));
            Assert.That(result, Has.Count.EqualTo(2 * single.Count), "Points of different objects never remove each other");
            Assert.That(result.Count(l => l.Name.StartsWith("Cover_A_")), Is.EqualTo(single.Count));
            Assert.That(result.Count(l => l.Name.StartsWith("Cover_B_")), Is.EqualTo(single.Count));
        }

        [Test]
        public void ATallWallsCorner_AndANeighbouringObjectsEndFacePoint_BothSurvive_InEitherOrder()
        {
            // Barrier_I's east end-face point is at (-4.25, 0, 2); the central wall (8 x 1, turned 45 degrees about
            // (0, 1, 0)) has its south-west corner at (-3.46, 0, 1.70): 0.84 m apart, inside the 0.9 m merge distance.
            var barrier = Barrier();
            var wall = Box("Barrier_Central", P(0f, 1f, 0f), P(8f, 2f, 1f), 45f);
            var barrierEnd = P(-4.25f, 0f, 2f);
            var wallCorner = P(-3.46f, 0f, 1.70f);
            Assert.That(CoverRules.FlatDistance(barrierEnd, wallCorner), Is.LessThan(0.9f), "the scenario is inside the merge distance");

            foreach (var boxes in new[] { new[] { barrier, wall }, new[] { wall, barrier } })
            {
                var result = CoverGenerator.Generate(boxes, new CoverGenerationSettings(), Everywhere);
                var order = string.Join(", ", boxes.Select(b => b.Name));
                Assert.That(result.Any(l => l.Name.StartsWith("Cover_Barrier_Central_") && l.Placement == CoverPlacement.Corner
                    && CoverRules.FlatDistance(l.Position, wallCorner) < 0.05f), Is.True, "central wall corner kept, order " + order);
                Assert.That(result.Any(l => l.Name.StartsWith("Cover_Barrier_I_") && l.Placement == CoverPlacement.Face
                    && CoverRules.FlatDistance(l.Position, barrierEnd) < 0.05f), Is.True, "Barrier_I end-face point kept, order " + order);
            }
        }

        [Test]
        public void WithinOneObject_ACornerStillBeatsTheFacePointNextToIt_AtDefaults()
        {
            // Barrier_I: south face points at spacing 1 start 0.15 m from the corner and are merged into it.
            var southFace = Generate(Barrier()).Where(l => l.Facing.z > 0.5f).OrderBy(l => l.Position.x).ToList();
            Assert.That(southFace.Count(l => l.Placement == CoverPlacement.Corner), Is.EqualTo(2));
            for (var i = 1; i < southFace.Count; i++)
                Assert.That(southFace[i].Position.x - southFace[i - 1].Position.x, Is.GreaterThanOrEqualTo(0.9f), southFace[i].Name);
        }
    }
}
