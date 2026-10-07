#if UNITY_EDITOR
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.AI;

namespace Blackglass.Tests
{
    /// <summary>The imported Darius art is wired into the gameplay prefab the way the design says (structure only, no playing).</summary>
    public class DariusAssetTests
    {
        const string Dir = "Assets/Art/Characters/Darius/";

        static GameObject Player() => AssetDatabase.LoadAssetAtPath<GameObject>(Dir + "Prefabs/Darius_Player.prefab");
        static AnimatorController Controller() => AssetDatabase.LoadAssetAtPath<AnimatorController>(Dir + "Animator/Darius_Player.controller");

        [Test]
        public void Player_IsAVariantOfFriendlyUnit_WithTheGameplayComponentsOnTheRoot_AndTheCapsuleRendererOff()
        {
            var player = Player();
            Assert.That(player, Is.Not.Null);
            var friendly = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/FriendlyUnit.prefab");
            Assert.That(PrefabUtility.GetCorrespondingObjectFromSource(player), Is.SameAs(friendly), "a variant of the gameplay prefab");
            Assert.That(PrefabUtility.GetPrefabAssetType(player), Is.EqualTo(PrefabAssetType.Variant));

            foreach (var type in new[] { typeof(CommandableUnit), typeof(SelectableUnit), typeof(Health), typeof(NavMeshAgent),
                         typeof(CapsuleCollider), typeof(UnitMover), typeof(UnitAttacker), typeof(UnitCover), typeof(HitFlash),
                         typeof(UnitAnimationDriver) })
                Assert.That(player.GetComponent(type), Is.Not.Null, type.Name);
            Assert.That(player.GetComponent<MeshRenderer>().enabled, Is.False, "the placeholder capsule is hidden");
            Assert.That(player.GetComponent<CapsuleCollider>().enabled, Is.True, "but its hit volume stays");
            Assert.That(player.transform.Find("SelectionRing"), Is.Not.Null);
            Assert.That(player.transform.Find("AttackLine"), Is.Not.Null);
        }

        [Test]
        public void Visual_IsHumanoid_WithoutRootMotion_AndStandsOnTheUnitsFeet()
        {
            var player = Player();
            var visual = player.transform.Find("Visual");
            Assert.That(visual, Is.Not.Null);
            Assert.That(visual.localPosition, Is.EqualTo(new Vector3(0f, -1f, 0f)));
            Assert.That(visual.localScale.x, Is.EqualTo(1.0639f).Within(1e-4f), "uniform scale that makes the 1.767 m model 1.88 m tall");
            Assert.That(visual.localScale.y, Is.EqualTo(visual.localScale.x));
            Assert.That(visual.localScale.z, Is.EqualTo(visual.localScale.x));
            var animator = visual.GetComponent<Animator>();
            Assert.That(animator.isHuman, Is.True);
            Assert.That(animator.applyRootMotion, Is.False);
            Assert.That(animator.cullingMode, Is.EqualTo(AnimatorCullingMode.AlwaysAnimate));
            Assert.That(animator.runtimeAnimatorController, Is.SameAs(Controller()));
            Assert.That(player.GetComponent<UnitAnimationDriver>().Animator, Is.SameAs(animator));
            Assert.That(visual.GetComponentsInChildren<Collider>(true), Is.Empty, "gameplay owns every collider");

            var body = visual.GetComponentInChildren<SkinnedMeshRenderer>();
            Assert.That(body.sharedMaterial, Is.SameAs(AssetDatabase.LoadAssetAtPath<Material>(Dir + "Materials/modddif_image_0.png.001.mat")));
            Assert.That(body.shadowCastingMode, Is.EqualTo(UnityEngine.Rendering.ShadowCastingMode.On));
            Assert.That(player.GetComponent<HitFlash>(), Is.Not.Null);
            var flashTarget = new SerializedObject(player.GetComponent<HitFlash>()).FindProperty("targetRenderer").objectReferenceValue;
            Assert.That(flashTarget, Is.SameAs(body), "the hit flash tints Darius, not the hidden capsule");
        }

        [Test]
        public void Rifle_IsASeparateMeshInTheRightHandSocket()
        {
            var animator = Player().transform.Find("Visual").GetComponent<Animator>();
            var hand = animator.GetBoneTransform(HumanBodyBones.RightHand);
            Assert.That(hand, Is.Not.Null);
            var socket = hand.Find("WeaponSocket");
            Assert.That(socket, Is.Not.Null, "the socket is a direct child of the right hand bone");
            Assert.That(socket.localPosition, Is.EqualTo(Vector3.zero));
            Assert.That(socket.localRotation, Is.EqualTo(Quaternion.identity));
            var rifle = socket.Find("Darius_Rifle");
            Assert.That(rifle, Is.Not.Null);
            Assert.That(rifle.GetComponentInChildren<MeshRenderer>(), Is.Not.Null);
            Assert.That(rifle.GetComponentInChildren<SkinnedMeshRenderer>(), Is.Null, "a rigid prop, not part of the skinned body");
            Assert.That(rifle.localPosition.x, Is.EqualTo(-0.047f).Within(1e-4f));
            Assert.That(rifle.localPosition.y, Is.EqualTo(0.3f).Within(1e-4f));
            Assert.That(rifle.localPosition.z, Is.EqualTo(0f).Within(1e-4f));
            var expected = new Quaternion(-0.70572317f, 0.67676085f, 0.16981347f, -0.12293512f);
            Assert.That(Quaternion.Angle(rifle.localRotation, expected), Is.LessThan(0.01f));
            var bodyRenderer = animator.GetComponentInChildren<SkinnedMeshRenderer>();
            Assert.That(rifle.IsChildOf(bodyRenderer.transform), Is.False);
        }

        [Test]
        public void Controller_HasTheParametersStatesAndLoopingLocomotion()
        {
            var controller = Controller();
            Assert.That(controller, Is.Not.Null);
            var parameters = controller.parameters.ToDictionary(p => p.name, p => p.type);
            Assert.That(parameters["Speed"], Is.EqualTo(AnimatorControllerParameterType.Float));
            Assert.That(parameters["Crouched"], Is.EqualTo(AnimatorControllerParameterType.Bool));
            Assert.That(parameters["Dead"], Is.EqualTo(AnimatorControllerParameterType.Bool));
            foreach (var trigger in new[] { "Fire", "Reload", "Hit" })
                Assert.That(parameters[trigger], Is.EqualTo(AnimatorControllerParameterType.Trigger), trigger);

            var machine = controller.layers[0].stateMachine;
            var states = machine.states.Select(s => s.state).ToDictionary(s => s.name);
            foreach (var name in new[] { "Standing", "Crouching", "Fire", "Reload", "Hit", "Death", "CrouchDeath" })
            {
                Assert.That(states.ContainsKey(name), Is.True, name);
                Assert.That(states[name].motion, Is.Not.Null, name);
            }
            Assert.That(machine.defaultState, Is.SameAs(states["Standing"]));

            var standing = (BlendTree)states["Standing"].motion;
            var crouching = (BlendTree)states["Crouching"].motion;
            Assert.That(standing.blendParameter, Is.EqualTo("Speed"));
            Assert.That(crouching.blendParameter, Is.EqualTo("Speed"));
            Assert.That(standing.children.Select(c => c.threshold), Is.EqualTo(new[] { 0f, 1f, 5f }));
            Assert.That(crouching.children.Select(c => c.threshold), Is.EqualTo(new[] { 0f, 1.3f }));
            foreach (var child in standing.children.Concat(crouching.children))
            {
                var clip = (AnimationClip)child.motion;
                Assert.That(clip.isLooping, Is.True, clip.name + " loops");
            }
            foreach (var name in new[] { "Fire", "Reload", "Hit", "Death", "CrouchDeath" })
                Assert.That(((AnimationClip)states[name].motion).isLooping, Is.False, name + " plays once");

            Assert.That(machine.anyStateTransitions.Length, Is.EqualTo(5), "Fire, Reload, Hit, Death, CrouchDeath");
            Assert.That(states["Death"].transitions, Is.Empty, "the corpse stays down");
            Assert.That(states["CrouchDeath"].transitions, Is.Empty);
        }

        [Test]
        public void Clips_AreTheFbxClips_WithLoopAndRootSettingsInTheImportSettings()
        {
            // clip name, source FBX, loops, bakes the vertical root motion, bakes the horizontal root motion
            var expected = new (string name, string fbx, bool loop, bool bakeY, bool bakeXZ)[]
            {
                ("StandIdle", "Darius Stand Idle Gun", true, false, false),
                ("Walk", "Darius Walk Gun", true, false, false),
                ("Run", "Darius Run Gun", true, false, false),
                ("CrouchIdle", "Darius Crouch Idle", true, false, false),
                ("CrouchWalk", "Darius Crouch Walk Gun", true, false, false),
                ("Fire", "Darius Fire", false, true, false),
                ("Reload", "Darius Reload", false, true, false),
                ("Hit", "Darius Hit Reaction", false, true, false),
                ("Death", "Darius Death", false, true, true),
                ("CrouchDeath", "Darius Crouch Death", false, true, true),
            };
            var machine = Controller().layers[0].stateMachine;
            var used = machine.states.Select(s => s.state.motion).SelectMany(m => m is BlendTree t ? t.children.Select(c => c.motion) : new[] { m })
                .OfType<AnimationClip>().ToDictionary(c => c.name);
            Assert.That(AssetDatabase.IsValidFolder(Dir + "Animations/Clips"), Is.False, "the generated clip copies are gone: the loop and root settings live in the FBX import settings");
            Assert.That(used.Count, Is.EqualTo(expected.Length));
            foreach (var e in expected)
            {
                var path = Dir + "Animations/" + e.fbx + ".fbx";
                Assert.That(used.ContainsKey(e.name), Is.True, e.name);
                Assert.That(AssetDatabase.GetAssetPath(used[e.name]), Is.EqualTo(path), e.name + " comes straight from its FBX");
                var importer = (ModelImporter)AssetImporter.GetAtPath(path);
                Assert.That(importer.clipAnimations.Length, Is.EqualTo(1), e.name);
                var clip = importer.clipAnimations[0];
                Assert.That(clip.name, Is.EqualTo(e.name));
                Assert.That(clip.loopTime, Is.EqualTo(e.loop), e.name + " loopTime");
                Assert.That(clip.lockRootHeightY, Is.EqualTo(e.bakeY), e.name + " bake root Y into the pose");
                Assert.That(clip.lockRootPositionXZ, Is.EqualTo(e.bakeXZ), e.name + " bake root XZ into the pose");
                Assert.That(clip.lockRootRotation, Is.False, e.name);
                Assert.That(importer.animationType, Is.EqualTo(ModelImporterAnimationType.Human), e.name);
            }
        }
    }
}
#endif
