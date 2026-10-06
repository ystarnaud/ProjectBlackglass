#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class PointerTargetResolverTests
    {
        static readonly Vector3 GroundPoint = new Vector3(2f, 0f, 2f);
        static readonly Vector3 CoverGroundPoint = new Vector3(-6f, 0f, 2f);

        TestWorld world;
        Camera viewCamera;
        SelectableUnit friendly;
        Health dummy;
        CoverLocation cover;
        CoverRegistry registry;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            world.CreateEnvironment();
            friendly = world.CreateFriendly(new Vector3(-8f, 0f, -6f));
            dummy = world.CreateDummy(new Vector3(6f, 0f, 6f));
            var wall = world.CreateObstacle(CoverGroundPoint + new Vector3(0f, 0.45f, 1f), new Vector3(2f, 0.9f, 0.5f));
            cover = world.CreateCoverPoint(CoverGroundPoint, Vector3.forward, wall.GetComponent<Collider>());
            registry = world.CreateRegistry(cover);
            var cameraObject = world.Track(new GameObject("Camera"));
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 25f, -20f), Quaternion.Euler(50f, 0f, 0f));
            viewCamera = cameraObject.AddComponent<Camera>();
        }

        [TearDown]
        public void TearDown() => world.Dispose();

        Vector2 ScreenPointOf(Vector3 worldPoint) => viewCamera.WorldToScreenPoint(worldPoint);

        PointerTarget Resolve(Vector3 worldPoint, CoverRegistry coverRegistry = null, float coverRadius = 0.5f,
            float maxDistance = 500f) =>
            PointerTargetResolver.Resolve(viewCamera, ScreenPointOf(worldPoint), maxDistance, ~0, coverRegistry, coverRadius);

        [Test]
        public void OverAFriendly_IsFriendly()
        {
            var target = Resolve(friendly.transform.position);
            Assert.That(target.Kind, Is.EqualTo(PointerTargetKind.Friendly));
            Assert.That(target.Friendly, Is.SameAs(friendly));
        }

        [Test]
        public void OverALivingHealth_IsHostile()
        {
            var target = Resolve(dummy.transform.position);
            Assert.That(target.Kind, Is.EqualTo(PointerTargetKind.Hostile));
            Assert.That(target.Hostile, Is.SameAs(dummy));
        }

        [Test]
        public void KilledTarget_ResolvesToGround()
        {
            dummy.TakeDamage(1000);
            Physics.SyncTransforms();
            var target = Resolve(new Vector3(6f, 0f, 6f));
            Assert.That(target.Kind, Is.EqualTo(PointerTargetKind.Ground));
        }

        [Test]
        public void NearACoverPoint_IsCover_WhenTheRegistryIsGiven()
        {
            var target = Resolve(CoverGroundPoint + new Vector3(0.3f, 0f, 0f), registry);
            Assert.That(target.Kind, Is.EqualTo(PointerTargetKind.Cover));
            Assert.That(target.Cover, Is.SameAs(cover));
        }

        [Test]
        public void NearACoverPoint_IsPlainGround_WithoutARegistryOrWithRadiusZero()
        {
            Assert.That(Resolve(CoverGroundPoint, null).Kind, Is.EqualTo(PointerTargetKind.Ground));
            Assert.That(Resolve(CoverGroundPoint + new Vector3(0.3f, 0f, 0f), registry, 0f).Kind, Is.EqualTo(PointerTargetKind.Ground));
        }

        [Test]
        public void OnOpenGround_IsGround_WithTheHitPoint()
        {
            var target = Resolve(GroundPoint, registry);
            Assert.That(target.Kind, Is.EqualTo(PointerTargetKind.Ground));
            Assert.That(Vector3.Distance(target.Point, GroundPoint), Is.LessThan(0.1f));
        }

        [Test]
        public void WhenTheRayHitsNothing_IsNone()
        {
            Assert.That(Resolve(GroundPoint, registry, 0.5f, maxDistance: 1f).Kind, Is.EqualTo(PointerTargetKind.None));
        }
    }
}
#endif
