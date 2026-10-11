using System.Linq;
using Blackglass.AssetPipeline;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.AI;

namespace Blackglass.Tests
{
    /// <summary>The EnemyUnit model is wired into the friendly and hostile gameplay prefabs the way decision 034 describes for Darius (structure only).</summary>
    public class EnemyUnitAssetTests
    {
        const string Dir = "Assets/Art/Characters/EnemyUnit/";
        const string ControllerPath = Dir + "Animator/EnemyUnit.controller";

        GameObject instance;

        [TearDown]
        public void TearDown()
        {
            if (instance != null) Object.DestroyImmediate(instance);
        }

        static GameObject Prefab(string side) => AssetDatabase.LoadAssetAtPath<GameObject>($"{Dir}Prefabs/EnemyUnit_{side}.prefab");
        static GameObject Base(string side) => AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/_Project/Prefabs/{side}Unit.prefab");

        [TestCase("Friendly")]
        [TestCase("Hostile")]
        public void Unit_is_a_variant_of_its_gameplay_prefab_with_every_component_and_the_capsule_hidden(string side)
        {
            var unit = Prefab(side);
            Assert.That(unit, Is.Not.Null);
            Assert.That(PrefabUtility.GetPrefabAssetType(unit), Is.EqualTo(PrefabAssetType.Variant));
            Assert.That(PrefabUtility.GetCorrespondingObjectFromSource(unit), Is.SameAs(Base(side)));

            foreach (var component in Base(side).GetComponents<Component>())
                Assert.That(unit.GetComponent(component.GetType()), Is.Not.Null, component.GetType().Name);
            Assert.That(unit.GetComponent<UnitAnimationDriver>(), Is.Not.Null);
            Assert.That(unit.GetComponent<UnitTeamTint>(), Is.Not.Null);
            Assert.That(unit.GetComponent<NavMeshAgent>(), Is.Not.Null);
            Assert.That(unit.GetComponent<MeshRenderer>().enabled, Is.False, "the placeholder capsule is hidden");
            Assert.That(unit.GetComponent<CapsuleCollider>().enabled, Is.True, "but its hit volume stays");
        }

        [TestCase("Friendly")]
        [TestCase("Hostile")]
        public void Visual_is_humanoid_without_root_motion_collider_free_and_stands_on_the_units_feet(string side)
        {
            var unit = Prefab(side);
            instance = (GameObject)PrefabUtility.InstantiatePrefab(unit);
            instance.transform.position = Vector3.zero;
            var visual = instance.transform.Find("Visual");
            Assert.That(visual, Is.Not.Null);
            Assert.That(visual.GetComponentsInChildren<Collider>(true), Is.Empty, "gameplay owns every collider");

            var animator = visual.GetComponentInChildren<Animator>();
            Assert.That(animator.isHuman, Is.True);
            Assert.That(animator.applyRootMotion, Is.False);
            Assert.That(animator.cullingMode, Is.EqualTo(AnimatorCullingMode.AlwaysAnimate));
            Assert.That(AssetDatabase.GetAssetPath(animator.runtimeAnimatorController), Is.EqualTo(ControllerPath));
            Assert.That(instance.GetComponent<UnitAnimationDriver>().Animator, Is.SameAs(animator));

            var bounds = ModelMeasure.Measure(visual.GetComponentInChildren<SkinnedMeshRenderer>().gameObject); // the body only, not the rifle
            Assert.That(bounds.HasValue, Is.True);
            Assert.That(bounds.Value.min.y, Is.EqualTo(-1f).Within(0.05f), "feet on the capsule's floor");
            Assert.That(bounds.Value.max.y - bounds.Value.min.y, Is.InRange(1.7f, 2.0f), "about 1.85 m tall inside the 2 m capsule");
        }

        [Test]
        public void Both_sides_share_one_body_material_and_differ_only_by_team_colour()
        {
            var friendly = Prefab("Friendly").GetComponentInChildren<SkinnedMeshRenderer>(true);
            var hostile = Prefab("Hostile").GetComponentInChildren<SkinnedMeshRenderer>(true);
            Assert.That(friendly.sharedMaterial, Is.Not.Null);
            Assert.That(friendly.sharedMaterial, Is.SameAs(hostile.sharedMaterial), "one material for every team");
            Assert.That(Prefab("Friendly").GetComponent<UnitTeamTint>().TeamColor, Is.Not.EqualTo(Prefab("Hostile").GetComponent<UnitTeamTint>().TeamColor));
        }

        // Phase 12: the rifle is no longer baked into the visual. The WeaponSocket under the right hand carries its alignment
        // and the unit's UnitWeaponVisual places the shared rifle there (its default until a loadout says otherwise).
        // EditMode runs no Awake, so the tests show the default the way Awake does.
        static Transform ShowDefaultRifle(GameObject unit)
        {
            var visual = unit.GetComponent<UnitWeaponVisual>();
            Assert.That(visual, Is.Not.Null, "the unit manages its weapon model");
            var standard = new SerializedObject(visual).FindProperty("defaultVisual").objectReferenceValue as WeaponVisualAsset;
            Assert.That(standard != null, Is.True, "with the standard rifle as its default");
            Assert.That(AssetDatabase.GetAssetPath(standard.Prefab), Is.EqualTo("Assets/Art/Weapons/Rifle/Prefabs/Rifle.prefab"), "the shared rifle prefab");
            visual.Show(standard);
            return visual.Current != null ? visual.Current.transform : null;
        }

        [Test]
        public void The_rifle_in_the_visual_is_the_shared_rifle_prefab_with_a_muzzle()
        {
            instance = (GameObject)PrefabUtility.InstantiatePrefab(Prefab("Hostile"));
            var hand = instance.GetComponentInChildren<Animator>().GetBoneTransform(HumanBodyBones.RightHand);
            var socket = hand.Find(UnitWeaponVisual.SocketName);
            Assert.That(socket, Is.Not.Null);
            Assert.That(socket.childCount, Is.EqualTo(0), "no rifle is baked into the visual");
            var rifle = ShowDefaultRifle(instance);
            Assert.That(rifle, Is.Not.Null);
            Assert.That(rifle.parent, Is.SameAs(socket));
            Assert.That(rifle.Find("Muzzle"), Is.Not.Null);
        }

        [Test]
        public void In_the_idle_pose_the_rifle_is_held_near_the_hand_and_points_forward_like_Darius()
        {
            instance = (GameObject)PrefabUtility.InstantiatePrefab(Prefab("Hostile"));
            instance.transform.position = Vector3.zero;
            var animator = instance.GetComponentInChildren<Animator>();
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            var idle = (AnimationClip)((BlendTree)controller.layers[0].stateMachine.states.First(s => s.state.name == "Standing").state.motion).children[0].motion;
            idle.SampleAnimation(animator.gameObject, 0f);

            var hand = animator.GetBoneTransform(HumanBodyBones.RightHand);
            var rifle = ShowDefaultRifle(instance);
            var center = rifle.GetComponentInChildren<MeshRenderer>().bounds.center;
            Assert.That(Vector3.Distance(center, hand.position), Is.LessThan(0.45f), "the rifle is in the hand, not floating beside it");
            var muzzle = rifle.Find("Muzzle");
            Assert.That(Vector3.Dot(muzzle.forward, instance.transform.forward), Is.GreaterThan(0.6f), "the barrel points the way the unit faces");
        }

        [Test]
        public void Controller_has_the_same_parameters_and_states_as_Darius()
        {
            var enemy = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            var darius = AssetDatabase.LoadAssetAtPath<AnimatorController>("Assets/Art/Characters/Darius/Animator/Darius_Player.controller");
            Assert.That(enemy, Is.Not.Null);

            string Signature(AnimatorControllerParameter p) => p.name + ":" + p.type;
            Assert.That(enemy.parameters.Select(Signature), Is.EquivalentTo(darius.parameters.Select(Signature)));

            var enemyStates = enemy.layers[0].stateMachine.states.Select(s => s.state).ToList();
            var dariusStates = darius.layers[0].stateMachine.states.Select(s => s.state).ToList();
            Assert.That(enemyStates.Select(s => s.name), Is.EquivalentTo(dariusStates.Select(s => s.name)));
            Assert.That(enemy.layers[0].stateMachine.anyStateTransitions.Length, Is.EqualTo(darius.layers[0].stateMachine.anyStateTransitions.Length));
            Assert.That(enemy.layers[0].stateMachine.defaultState.name, Is.EqualTo("Standing"));

            foreach (var state in enemyStates)
            {
                Assert.That(state.motion, Is.Not.Null, state.name);
                if (state.motion is BlendTree tree)
                    Assert.That(tree.children.All(c => c.motion != null), Is.True, state.name + " blend tree has an empty slot");
            }
        }

        [Test]
        public void Controller_loops_idle_walk_and_run_and_plays_the_one_shots_once()
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            var states = controller.layers[0].stateMachine.states.Select(s => s.state).ToDictionary(s => s.name);
            foreach (var name in new[] { "Standing", "Crouching" })
                foreach (var child in ((BlendTree)states[name].motion).children)
                    Assert.That(((AnimationClip)child.motion).isLooping, Is.True, $"{name}/{child.motion.name} loops");
            foreach (var name in new[] { "Fire", "Reload", "Hit", "Death", "CrouchDeath" })
                Assert.That(((AnimationClip)states[name].motion).isLooping, Is.False, name + " plays once");
        }

        [Test]
        public void The_squad_members_Kestrel_and_Sable_use_the_friendly_EnemyUnit_prefab()
        {
            foreach (var name in new[] { "Kestrel", "Sable" })
            {
                var definition = AssetDatabase.LoadAssetAtPath<OperativeDefinition>($"Assets/_Project/Data/Operatives/Definitions/{name}.asset");
                Assert.That(definition, Is.Not.Null, name);
                Assert.That(AssetDatabase.GetAssetPath(definition.UnitPrefab), Is.EqualTo(Dir + "Prefabs/EnemyUnit_Friendly.prefab"), name);
            }
            var darius = AssetDatabase.LoadAssetAtPath<OperativeDefinition>("Assets/_Project/Data/Operatives/Definitions/Darius.asset");
            Assert.That(AssetDatabase.GetAssetPath(darius.UnitPrefab), Does.Contain("Darius_Player"), "Darius keeps his own model");
        }
    }
}
