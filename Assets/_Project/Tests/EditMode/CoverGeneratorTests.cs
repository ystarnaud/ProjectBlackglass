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
        // spacing, 2 m, so their hand-derived counts and positions stay valid. The tests further down cover the current
        // defaults.
        static CoverGenerationSettings Original() => new CoverGenerationSettings { spacing = 2f };

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
        public void ATallPillarWiderThanAUnit_GetsNoCover()
        {
            // Pillar_G of the prototype arena: 1.5 x 1.5, 3 m tall. Every face is wider than columnMaxWidth (1.1 m) and the
            // box is no wall (030: only a face no wider than a unit is a column).
            Assert.That(Generate(Box("Pillar_G", P(-2f, 1.5f, -6f), P(1.5f, 3f, 1.5f)), settings: Original()), Is.Empty);
            Assert.That(Generate(Box("Pillar_G", P(-2f, 1.5f, -6f), P(1.5f, 3f, 1.5f))), Is.Empty, "at the defaults too");
            Assert.That(Generate(Box("Pillar_12", P(0f, 1.5f, 0f), P(1.2f, 3f, 1.2f))), Is.Empty, "1.2 m faces are above columnMaxWidth");
        }

        [Test]
        public void AOneByOneTallPillar_GetsOneColumnLocationOnEachFace_OwnedByThePillar()
        {
            var owner = new GameObject("Pillar").AddComponent<BoxCollider>();
            try
            {
                var pillar = new CoverBox("Pillar", P(4f, 1.5f, -2f), 0f, P(0.5f, 1.5f, 0.5f), owner);
                var result = CoverGenerator.Generate(new[] { pillar }, new CoverGenerationSettings(), Everywhere);

                Assert.That(result, Has.Count.EqualTo(4));
                Assert.That(result.Select(l => l.Name), Is.EquivalentTo(new[]
                    { "Cover_Pillar_EColumn1", "Cover_Pillar_NColumn1", "Cover_Pillar_SColumn1", "Cover_Pillar_WColumn1" }));
                Assert.That(result.All(l => l.Placement == CoverPlacement.Column && l.Height == CoverHeight.Tall && !l.HasPeek), Is.True);
                Assert.That(result.All(l => l.Obstacle == owner), Is.True, "every location is owned by the pillar");
                foreach (var location in result)
                {
                    // Centred on its face, 0.75 m in front of it (0.5 + 0.75 from the centre), facing into the face.
                    var outward = -location.Facing;
                    var expected = P(4f, 0f, -2f) + outward * 1.25f;
                    Assert.That(Vector3.Distance(location.Position, expected), Is.LessThan(1e-4f), location.Name);
                    Assert.That(Mathf.Abs(outward.x) + Mathf.Abs(outward.z), Is.EqualTo(1f).Within(1e-4f), location.Name + " faces its face square on");
                }
                var east = result.Single(l => l.Name == "Cover_Pillar_EColumn1");
                Assert.That(east.Facing.x, Is.EqualTo(-1f).Within(1e-4f), "the east location looks west, into the pillar");
                Assert.That(east.Position.x, Is.EqualTo(5.25f).Within(1e-4f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner.gameObject);
            }
        }

        [Test]
        public void AThinTallWall_GetsItsFourCorners_AndAColumnLocationBeyondEachEnd_OnItsAxis()
        {
            // 4 m long (x -2..2), 1 m thick (z -0.5..0.5), 3 m tall: the long faces keep their corners, each 1 m end cap
            // gets one column location 0.75 m past the end, on the wall's axis.
            var result = Generate(Box("Baffle", P(0f, 1.5f, 0f), P(4f, 3f, 1f)));

            Assert.That(result.Count(l => l.Placement == CoverPlacement.Corner && l.HasPeek), Is.EqualTo(4));
            var columns = result.Where(l => l.Placement == CoverPlacement.Column).ToList();
            Assert.That(columns.Select(l => l.Name), Is.EquivalentTo(new[] { "Cover_Baffle_EColumn1", "Cover_Baffle_WColumn1" }));
            Assert.That(result, Has.Count.EqualTo(6));
            var east = columns.Single(l => l.Name == "Cover_Baffle_EColumn1");
            Assert.That(Vector3.Distance(east.Position, P(2.75f, 0f, 0f)), Is.LessThan(1e-4f), "on the axis, beyond the east end");
            Assert.That(east.Facing.x, Is.EqualTo(-1f).Within(1e-4f), "looks along the wall, into the end face");
            var west = columns.Single(l => l.Name == "Cover_Baffle_WColumn1");
            Assert.That(Vector3.Distance(west.Position, P(-2.75f, 0f, 0f)), Is.LessThan(1e-4f));
            Assert.That(columns.All(l => l.Height == CoverHeight.Tall && !l.HasPeek), Is.True);
        }

        [Test]
        public void AnEndCapWhoseStandPointIsNotWalkable_GetsNoColumn()
        {
            // The baffle's west end abuts another wall (x -3..-2): the west column's stand point (-2.75, 0, 0) lies inside
            // it, so the fit test drops it. The east end cap keeps its column.
            var baffle = Box("Baffle", P(0f, 1.5f, 0f), P(4f, 3f, 1f));
            var wall = Box("Wall", P(-2.5f, 1.5f, 0f), P(1f, 3f, 8f));
            Func<Vector3, bool> walkable = p => !InsideBox(baffle, p) && !InsideBox(wall, p);
            var result = CoverGenerator.Generate(new[] { baffle, wall }, new CoverGenerationSettings(), walkable);

            var baffleColumns = result.Where(l => l.Name.StartsWith("Cover_Baffle_") && l.Placement == CoverPlacement.Column).ToList();
            Assert.That(baffleColumns.Select(l => l.Name), Is.EqualTo(new[] { "Cover_Baffle_EColumn1" }));
            Assert.That(result.All(l => walkable(l.Position)), Is.True, "no location stands inside a wall");
        }

        [Test]
        public void ColumnMaxWidth_DecidesWhichFacesAreColumns()
        {
            Assert.That(Generate(Box("Crate_2x2", P(0f, 1.5f, 0f), P(2f, 3f, 2f))), Is.Empty, "a tall 2 x 2 box: no columns, no corners");
            var wide = Generate(Box("Wide", P(0f, 1.5f, 0f), P(1.2f, 3f, 4f)));
            Assert.That(wide.Count(l => l.Placement == CoverPlacement.Column), Is.Zero, "1.2 m end caps are wider than a unit");
            Assert.That(wide.Count(l => l.Placement == CoverPlacement.Corner), Is.EqualTo(4), "its long faces keep their corners");
            var narrow = Generate(Box("Narrow", P(0f, 1.5f, 0f), P(1f, 3f, 4f)));
            Assert.That(narrow.Count(l => l.Placement == CoverPlacement.Column), Is.EqualTo(2), "1.0 m end caps: one column each");
            var wider = Generate(Box("Wide", P(0f, 1.5f, 0f), P(1.2f, 3f, 4f)), settings: new CoverGenerationSettings { columnMaxWidth = 1.3f });
            Assert.That(wider.Count(l => l.Placement == CoverPlacement.Column), Is.EqualTo(2), "the width is a setting");
        }

        [Test]
        public void ColumnMaxWidth_IsInclusive_WithinTheGeneratorsTolerance()
        {
            // The generator admits a face up to columnMaxWidth + 1e-4 m, so float noise on a 1.1 m face cannot drop it.
            Assert.That(Generate(Box("AtLimit", P(0f, 1.5f, 0f), P(4f, 3f, 1.1f))).Count(l => l.Placement == CoverPlacement.Column),
                Is.EqualTo(2), "1.1 m end caps are columns");
            Assert.That(Generate(Box("Over", P(0f, 1.5f, 0f), P(4f, 3f, 1.1002f))).Count(l => l.Placement == CoverPlacement.Column),
                Is.Zero, "1.1002 m end caps are beyond the tolerance");
        }

        [Test]
        public void AOneByOneTallPillarTurnedFortyFiveDegrees_GetsAColumnInFrontOfEachRotatedFace()
        {
            var centre = P(2f, 1.5f, -1f);
            var rotation = Quaternion.Euler(0f, 45f, 0f);
            var result = Generate(Box("Pillar", centre, P(1f, 3f, 1f), 45f));

            Assert.That(result, Has.Count.EqualTo(4));
            Assert.That(result.All(l => l.Placement == CoverPlacement.Column && l.Height == CoverHeight.Tall && !l.HasPeek), Is.True);
            var normals = new[] { rotation * Vector3.right, rotation * Vector3.left, rotation * Vector3.forward, rotation * Vector3.back };
            foreach (var normal in normals)
            {
                var expected = P(centre.x, 0f, centre.z) + normal * 1.25f;
                var match = result.OrderBy(l => Vector3.Distance(l.Position, expected)).First();
                Assert.That(Vector3.Distance(match.Position, expected), Is.LessThan(1e-4f), $"a column 0.75 m in front of the face with normal {normal}");
                Assert.That(Vector3.Distance(match.Facing, -normal), Is.LessThan(1e-4f), $"{match.Name} faces into its face");
            }
            Assert.That(result.Select(l => l.Position).Distinct().Count(), Is.EqualTo(4), "one per face");
        }

        [Test]
        public void ALowOneByOneCrate_GetsFaceLocations_NotColumns()
        {
            var result = Generate(Box("Crate", P(0f, 0.5f, 0f), P(1f, 1f, 1f)));
            Assert.That(result, Has.Count.EqualTo(4));
            Assert.That(result.All(l => l.Placement == CoverPlacement.Face && l.Height == CoverHeight.Low), Is.True);
            Assert.That(result.Select(l => l.Name), Is.EquivalentTo(new[] { "Cover_Crate_E1", "Cover_Crate_N1", "Cover_Crate_S1", "Cover_Crate_W1" }));
        }

        [Test]
        public void CandidatesOfOneObject_AreNotMergedByProximity_OnlyExactDuplicatesCollapse()
        {
            // A low 2 x 2 box with a negative end margin pushes the end points of neighbouring faces round the box corner.
            // endMargin -0.4: each face gets 3 points (usable 2.8 m), and at each of the 4 box corners the two end points
            // stand about 0.49 m apart. The old 0.9 m merge dropped one of each pair (8 kept); now all 12 stay.
            var box = Box("Low", P(0f, 0.45f, 0f), P(2f, 0.9f, 2f));
            var apart = Generate(box, settings: new CoverGenerationSettings { endMargin = -0.4f });
            Assert.That(apart, Has.Count.EqualTo(12), "candidates about half a metre apart are both kept");

            // endMargin -0.715: 4 points per face (usable 3.43 m), and the two end points at each box corner lie about
            // 0.05 m apart: one of each such pair is a duplicate, so 16 candidates give 12 locations.
            var coincident = Generate(box, settings: new CoverGenerationSettings { endMargin = -0.715f });
            Assert.That(coincident, Has.Count.EqualTo(12), "candidates 0.05 m apart collapse to one");
            for (var i = 0; i < coincident.Count; i++)
                for (var j = i + 1; j < coincident.Count; j++)
                    Assert.That(CoverRules.FlatDistance(coincident[i].Position, coincident[j].Position), Is.GreaterThan(0.1f),
                        $"{coincident[i].Name} and {coincident[j].Name}");
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
        public void ASpacingOfZeroOrLess_StillGenerates_WithEveryLocationAtLeastAUnitWidthApart()
        {
            foreach (var spacing in new[] { 0f, -1f })
            {
                var settings = new CoverGenerationSettings { spacing = spacing };
                var result = Generate(Box("Wall", P(0f, 0.45f, 0f), P(3f, 0.9f, 0.5f)), settings: settings);
                Assert.That(result, Is.Not.Empty, $"spacing {spacing}");
                for (var i = 0; i < result.Count; i++)
                    for (var j = i + 1; j < result.Count; j++)
                        Assert.That(CoverRules.FlatDistance(result[i].Position, result[j].Position), Is.GreaterThanOrEqualTo(1f - 1e-4f),
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
        public void TallWall_HasNoFaceLocations_OnlyItsFourCorners_AndItsTwoEndCapColumns()
        {
            var result = Generate(Barrier(), settings: Original());
            Assert.That(result.Count(l => l.Placement == CoverPlacement.Face), Is.Zero, "no cover along a tall wall");
            Assert.That(result, Has.Count.EqualTo(6));
            Assert.That(result.Count(l => l.Placement == CoverPlacement.Corner), Is.EqualTo(4));
            Assert.That(result.Where(l => l.Placement == CoverPlacement.Corner).All(l => l.Height == CoverHeight.Tall && l.HasPeek), Is.True);
            Assert.That(result.Where(l => l.Placement == CoverPlacement.Column).Select(l => l.Name), Is.EquivalentTo(new[]
                { "Cover_Barrier_I_EColumn1", "Cover_Barrier_I_WColumn1" }), "the 1 m end caps");
            var atDefaults = Generate(Barrier());
            Assert.That(atDefaults, Has.Count.EqualTo(6), "also at the current defaults");
            Assert.That(atDefaults.All(l => l.Placement == CoverPlacement.Corner || l.Placement == CoverPlacement.Column), Is.True);
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
            Assert.That(Generate(Box("Stubby", P(0f, 1f, 0f), P(3f, 2f, 2f))), Is.Empty, "also at the current defaults");
        }

        [Test]
        public void AShortTallStub_GetsNoCorners_ButItsUnitWideEndCapsAreColumns()
        {
            // 1.5 m long, 0.5 m thick: under minCornerLength, so no corners, and its 1.5 m long faces are wider than a
            // unit; each 0.5 m end cap is no wider than a unit, so it gets one column location (030).
            foreach (var settings in new[] { Original(), new CoverGenerationSettings() })
            {
                var result = Generate(Box("Short", P(0f, 1f, 0f), P(1.5f, 2f, 0.5f)), settings: settings);
                Assert.That(result.Count(l => l.Placement == CoverPlacement.Corner), Is.Zero, "1.5 m is under minCornerLength");
                Assert.That(result.Select(l => l.Name), Is.EquivalentTo(new[] { "Cover_Short_EColumn1", "Cover_Short_WColumn1" }));
            }
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

            var corners = result.Where(l => l.Placement == CoverPlacement.Corner).ToList();
            Assert.That(corners.Select(l => l.Name), Is.EquivalentTo(new[] { "Cover_Barrier_I_NCorner1", "Cover_Barrier_I_SCorner1" }),
                "the two west ends open outward, both east ends do not");
            Assert.That(corners.All(l => l.HasPeek && l.PeekDirection.x < -0.99f), Is.True);
            Assert.That(corners.All(l => walkable(l.PeekPoint)), Is.True);
            // The end caps are columns: the west one (stand x -11.75) is walkable, the east one (x -4.25) is not.
            Assert.That(result.Where(l => l.Placement == CoverPlacement.Column).Select(l => l.Name), Is.EqualTo(new[] { "Cover_Barrier_I_WColumn1" }));
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

            var xCorners = result.Where(l => l.Name.StartsWith("Cover_WallX_") && l.Placement == CoverPlacement.Corner).ToList();
            Assert.That(xCorners.All(l => l.PeekDirection.x > 0.99f), Is.True, "only the east end remains");
            Assert.That(xCorners, Has.Count.EqualTo(2), "one per long face at the open east end");
            Assert.That(result.Where(l => l.Name.StartsWith("Cover_WallX_") && l.Placement == CoverPlacement.Column).Select(l => l.Name),
                Is.EqualTo(new[] { "Cover_WallX_EColumn1" }), "the west end cap's stand point is inside the z wall");
            Assert.That(result.Count(l => l.Name.StartsWith("Cover_WallZ_") && l.Placement == CoverPlacement.Corner), Is.EqualTo(4), "the z wall's ends are both open");
            Assert.That(result.Count(l => l.Name.StartsWith("Cover_WallZ_") && l.Placement == CoverPlacement.Column), Is.EqualTo(2), "and both its end caps are free");
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
            Assert.That(result.Where(l => l.Name.StartsWith("Cover_WallX_") && l.Placement == CoverPlacement.Corner)
                    .All(l => l.PeekDirection.x > 0.99f || l.Facing.z > 0.5f), Is.True,
                "the x wall keeps its east end and its south-west end, which stands outside the L");
            Assert.That(result.Where(l => l.Name.StartsWith("Cover_WallX_") && l.Placement == CoverPlacement.Column).Select(l => l.Name),
                Is.EqualTo(new[] { "Cover_WallX_EColumn1" }), "the west end cap runs into the z wall: no column there");
        }

        [Test]
        public void ATallWallAgainstTheMapEdge_GetsNoCornerAtThatEnd()
        {
            // Nothing is walkable west of x -11.5: the wall's west end (x -11) is at the edge of the map, so the peek
            // points 1.25 m past it (x about -11.9) are off the map.
            var result = Generate(Barrier(), p => p.x > -11.5f, Original());
            Assert.That(result.Count(l => l.PeekDirection.x < -0.5f), Is.Zero);
            Assert.That(result.Select(l => l.Name), Is.EquivalentTo(new[] { "Cover_Barrier_I_NCorner1", "Cover_Barrier_I_SCorner1", "Cover_Barrier_I_EColumn1" }),
                "the two east corners and the east end cap stay; the west end cap's stand point (x -11.75) is off the map");
        }

        [Test]
        public void ANonWalkableCornerStandPoint_DropsTheCorner()
        {
            var result = Generate(Barrier(), p => p.x > -9f || p.x < -11.5f, Original());   // the west stand points (x -10.65) are not walkable, the west peek points (x -11.9) are
            Assert.That(result.Count(l => l.Placement == CoverPlacement.Corner), Is.EqualTo(2), "Only the two east corners remain");
        }

        // ---- Current defaults: spacing 1 m, no proximity merge (030) ----

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
            // low wall whose south face points sit at (-4.25, 0, 2) and (-3.25, 0, 2) is 0.84 and 0.37 m from it.
            // Different objects never remove each other.
            var wall = Box("Barrier_Central", P(0f, 1f, 0f), P(8f, 2f, 1f), 45f);
            var low = Box("Low_Neighbour", P(-4.25f, 0.45f, 3f), P(3f, 0.9f, 0.5f));
            var wallCorner = P(-3.46f, 0f, 1.70f);
            var lowPoint = P(-4.25f, 0f, 2f);
            Assert.That(CoverRules.FlatDistance(lowPoint, wallCorner), Is.LessThan(0.9f), "the scenario puts the two closer than a unit's width");

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
        public void ATwoMetreTallWall_KeepsAllFourCorners_AndBothEndCapColumns_AtDefaults()
        {
            // The shortest wall that gets corners: its two corners per face stand 2 - 2 * 0.35 = 1.3 m apart; its 0.5 m
            // end caps are columns.
            var result = Generate(Box("Short", P(0f, 1f, 0f), P(2f, 2f, 0.5f)));
            Assert.That(result, Has.Count.EqualTo(6));
            Assert.That(result.Count(l => l.Placement == CoverPlacement.Corner), Is.EqualTo(4));
            Assert.That(result.Count(l => l.Placement == CoverPlacement.Column), Is.EqualTo(2));
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
