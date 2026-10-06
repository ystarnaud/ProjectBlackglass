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
        public void TallPillar_GetsNoCover_AtAll()
        {
            // Pillar_G of the prototype arena: 1.5 x 1.5, 3 m tall. A pillar does not hide a unit.
            Assert.That(Generate(Box("Pillar_G", P(-2f, 1.5f, -6f), P(1.5f, 3f, 1.5f)), settings: Original()), Is.Empty);
            Assert.That(Generate(Box("Pillar_1x1", P(0f, 1.5f, 0f), P(1f, 3f, 1f))), Is.Empty, "a 1 x 1 pillar at the defaults too");
        }

        [Test]
        public void TallCrate_TwoByTwo_IsNotAWall_GetsNoCover()
        {
            var result = Generate(Box("Crate_J", P(10f, 1f, -5f), P(2f, 2f, 2f)), settings: Original());
            Assert.That(result, Is.Empty, "2 m tall: no face points, and not twice as long as thick: no corners");
        }

        [Test]
        public void ALowCrateAtTheThreshold_StillGetsFacePoints()
        {
            var result = Generate(Box("Crate", P(0f, 0.6f, 0f), P(2f, 1.2f, 2f)), settings: Original());
            Assert.That(result, Has.Count.EqualTo(4));
            Assert.That(result.All(l => l.Height == CoverHeight.Low && l.Placement == CoverPlacement.Face), Is.True);
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
        public void TallWall_HasNoFaceLocations_OnlyItsFourCorners()
        {
            var result = Generate(Barrier(), settings: Original());
            Assert.That(result.Count(l => l.Placement == CoverPlacement.Face), Is.Zero, "no cover along a tall wall");
            Assert.That(result, Has.Count.EqualTo(4));
            Assert.That(result.All(l => l.Placement == CoverPlacement.Corner && l.Height == CoverHeight.Tall && l.HasPeek), Is.True);
            var atDefaults = Generate(Barrier());
            Assert.That(atDefaults, Has.Count.EqualTo(4), "also at the current defaults");
            Assert.That(atDefaults.All(l => l.Placement == CoverPlacement.Corner), Is.True);
        }

        [Test]
        public void LowWalls_NeverGetCorners()
        {
            var result = Generate(Box("Low", P(0f, 0.45f, 0f), P(8f, 0.9f, 1f)), settings: Original());
            Assert.That(result.Any(l => l.Placement == CoverPlacement.Corner), Is.False);
        }

        [Test]
        public void ATallBoxThatIsNotMuchLongerThanItIsThick_GetsNoCover()
        {
            Assert.That(Generate(Box("Stubby", P(0f, 1f, 0f), P(3f, 2f, 2f)), settings: Original()), Is.Empty, "3 x 2 is not twice as long as thick");
            Assert.That(Generate(Box("Short", P(0f, 1f, 0f), P(1.5f, 2f, 0.5f)), settings: Original()), Is.Empty, "1.5 m is under minCornerLength");
            Assert.That(Generate(Box("Short", P(0f, 1f, 0f), P(1.5f, 2f, 0.5f))), Is.Empty, "also at the current defaults");
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
        public void ACornerWhosePeekPointIsNotWalkable_IsDropped_AndOnlyThatEndsCandidates()
        {
            // Reject every point east of x -5 on both sides of the wall (the south and north stand lines are at z 0.75 and
            // z 3.25; the east peek points lie 1.25 m further east than the stands at x -5.35).
            Func<Vector3, bool> walkable = p => !(p.x > -5f);
            var result = Generate(Barrier(), walkable, Original());

            Assert.That(result.Select(l => l.Name), Is.EquivalentTo(new[] { "Cover_Barrier_I_NCorner1", "Cover_Barrier_I_SCorner1" }),
                "the two west ends open outward, both east ends do not");
            Assert.That(result.All(l => l.Placement == CoverPlacement.Corner && l.HasPeek && l.PeekDirection.x < -0.99f), Is.True);
            Assert.That(result.All(l => walkable(l.PeekPoint)), Is.True);
        }

        static bool InsideBox(CoverBox b, Vector3 p) =>
            Mathf.Abs(p.x - b.Center.x) < b.HalfExtents.x && Mathf.Abs(p.z - b.Center.z) < b.HalfExtents.z;

        [Test]
        public void ATallWallsEnd_ThatRunsIntoAnotherWall_GetsNoCorner_AtATJunction()
        {
            // A T: the x wall (x -4..4, z -0.5..0.5) ends against the middle of the z wall (x -5..-4, z -4..4). Walkable is
            // outside both boxes. The x wall's west candidates peek west into the z wall: dropped. Its east end is open.
            var xWall = Box("WallX", P(0f, 1.5f, 0f), P(8f, 3f, 1f));
            var zWall = Box("WallZ", P(-4.5f, 1.5f, 0f), P(1f, 3f, 8f));
            Func<Vector3, bool> walkable = p => !InsideBox(xWall, p) && !InsideBox(zWall, p);
            var result = CoverGenerator.Generate(new[] { xWall, zWall }, new CoverGenerationSettings(), walkable);

            Assert.That(result.Where(l => l.Name.StartsWith("Cover_WallX_")).All(l => l.PeekDirection.x > 0.99f), Is.True, "only the east end remains");
            Assert.That(result.Count(l => l.Name.StartsWith("Cover_WallX_")), Is.EqualTo(2), "one per long face at the open east end");
            Assert.That(result.Count(l => l.Name.StartsWith("Cover_WallZ_")), Is.EqualTo(4), "the z wall's ends are both open");
        }

        [Test]
        public void TwoTallWallsMeetingAtAClosedCorner_GetNoLocationAtTheClosedCorner()
        {
            // An L: the z wall (x -5..-4, z -0.5..7.5) closes the x wall's west end on the inside of the L (north side).
            // The inside corner is a room corner: both walls are fully exposed there, so neither wall offers cover in it.
            // (The x wall's south-west candidate stands outside the L and stays; it opens to the open floor south of it.)
            var xWall = Box("WallX", P(0f, 1.5f, 0f), P(8f, 3f, 1f));
            var zWall = Box("WallZ", P(-4.5f, 1.5f, 3.5f), P(1f, 3f, 8f));
            Func<Vector3, bool> walkable = p => !InsideBox(xWall, p) && !InsideBox(zWall, p);
            var result = CoverGenerator.Generate(new[] { xWall, zWall }, new CoverGenerationSettings(), walkable);

            var closedCorner = P(-3.5f, 0f, 0.5f);   // the inside corner of the L, where the two walls meet
            Assert.That(result.Where(l => CoverRules.FlatDistance(l.Position, closedCorner) < 1.6f), Is.Empty,
                "no location of either wall in the closed room corner");
            Assert.That(result.Where(l => l.Name.StartsWith("Cover_WallX_")).All(l => l.PeekDirection.x > 0.99f || l.Facing.z > 0.5f), Is.True,
                "the x wall keeps its east end and its south-west end, which stands outside the L");
        }

        [Test]
        public void ATallWallAgainstTheMapEdge_GetsNoCornerAtThatEnd()
        {
            // Nothing is walkable west of x -11.5: the wall's west end (x -11) is at the edge of the map, so the peek
            // points 1.25 m past it (x about -11.9) are off the map.
            var result = Generate(Barrier(), p => p.x > -11.5f, Original());
            Assert.That(result.Count(l => l.PeekDirection.x < -0.5f), Is.Zero);
            Assert.That(result.Select(l => l.Name), Is.EquivalentTo(new[] { "Cover_Barrier_I_NCorner1", "Cover_Barrier_I_SCorner1" }), "the two east ends stay");
        }

        [Test]
        public void ANonWalkableCornerStandPoint_DropsTheCorner()
        {
            var result = Generate(Barrier(), p => p.x > -9f || p.x < -11.5f, Original());   // the west stand points (x -10.65) are not walkable, the west peek points (x -11.9) are
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
        public void ATallWallsCorner_AndANeighbouringLowObjectsFacePoint_BothSurvive_InEitherOrder()
        {
            // The central wall (8 x 1, turned 45 degrees about (0, 1, 0)) has its south-west corner at (-3.46, 0, 1.70). A
            // low wall whose south face points sit at (-4.25, 0, 2) and (-3.25, 0, 2) is within the 0.9 m merge
            // distance of it: 0.84 and 0.37 m. Different objects never remove each other.
            var wall = Box("Barrier_Central", P(0f, 1f, 0f), P(8f, 2f, 1f), 45f);
            var low = Box("Low_Neighbour", P(-4.25f, 0.45f, 3f), P(3f, 0.9f, 0.5f));
            var wallCorner = P(-3.46f, 0f, 1.70f);
            var lowPoint = P(-4.25f, 0f, 2f);
            Assert.That(CoverRules.FlatDistance(lowPoint, wallCorner), Is.LessThan(0.9f), "the scenario is inside the merge distance");

            foreach (var boxes in new[] { new[] { low, wall }, new[] { wall, low } })
            {
                var result = CoverGenerator.Generate(boxes, new CoverGenerationSettings(), Everywhere);
                var order = string.Join(", ", boxes.Select(b => b.Name));
                Assert.That(result.Any(l => l.Name.StartsWith("Cover_Barrier_Central_") && l.Placement == CoverPlacement.Corner
                    && CoverRules.FlatDistance(l.Position, wallCorner) < 0.05f), Is.True, "central wall corner kept, order " + order);
                Assert.That(result.Any(l => l.Name.StartsWith("Cover_Low_Neighbour_") && l.Placement == CoverPlacement.Face
                    && CoverRules.FlatDistance(l.Position, lowPoint) < 0.05f), Is.True, "low wall face point kept, order " + order);
            }
        }

        [Test]
        public void ATwoMetreTallWall_KeepsAllFourCorners_AtDefaults()
        {
            // The shortest wall that gets corners: its two corners per face stand 2 - 2 * 0.35 = 1.3 m apart, over the
            // 0.9 m merge distance.
            var result = Generate(Box("Short", P(0f, 1f, 0f), P(2f, 2f, 0.5f)));
            Assert.That(result, Has.Count.EqualTo(4));
            Assert.That(result.All(l => l.Placement == CoverPlacement.Corner), Is.True);
        }

        [Test]
        public void LowWall_AtDefaults_KeepsFacePointsAlongEveryFace_AsBefore()
        {
            // 8 x 1, 0.9 m: usable 7 m gives 8 points per long face; the 1 m end faces get one point each.
            var result = Generate(Box("Low", P(0f, 0.45f, 0f), P(8f, 0.9f, 1f)));
            Assert.That(result.Count(l => l.Facing.z > 0.5f), Is.EqualTo(8));
            Assert.That(result.Count(l => l.Facing.z < -0.5f), Is.EqualTo(8));
            Assert.That(result.Any(l => l.Placement == CoverPlacement.Corner), Is.False);
        }
    }
}
