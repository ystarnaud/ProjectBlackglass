#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass.Tests
{
    /// <summary>
    /// The common arrangement of the ability input tests: a caster with the three prototype abilities, an ally, hostiles
    /// (one far, one behind a wall) in an open arena, a camera, and the input components wired like the scene. Nothing is
    /// selected at the start: the active character (the caster) is who abilities are cast by.
    /// </summary>
    internal sealed class AbilityRig
    {
        public static readonly Vector3 CasterGround = new Vector3(0f, 0f, -6f);
        public static readonly Vector3 AllyGround = new Vector3(4f, 0f, -6f);
        public static readonly Vector3 HostileGround = new Vector3(3f, 0f, 2f);
        public static readonly Vector3 FarHostileGround = new Vector3(7f, 0f, 8f);
        // Behind a 3 m wall (x -8..-4, z -0.25..0.25) as seen from the caster.
        public static readonly Vector3 WalledHostileGround = new Vector3(-6f, 0f, 2f);
        // Open ground 2 m east of the near hostile.
        public static readonly Vector3 BlastGround = new Vector3(5f, 0f, 2f);

        public TestWorld World;
        public Camera ViewCamera;
        public TacticalPause Pause;
        public UnitSelection Selection;
        public ActiveCharacter Active;
        public Encounter Encounter;
        public SelectableUnit Caster;
        public SelectableUnit Ally;
        public UnitAbilities Abilities;
        public Health CasterHealth;
        public Health AllyHealth;
        public Health Hostile;
        public Health FarHostile;
        public Health WalledHostile;
        public PlayerCommandInput Input;
        public AbilityTargeting Targeting;
        public TacticalCursor Cursor;
        public AbilityMenuGate Gate;
        public AbilityDefinition Aimed;
        public AbilityDefinition Blast;
        public AbilityDefinition Mend;

        public Vector2 ScreenPointOf(Vector3 worldPoint) => ViewCamera.WorldToScreenPoint(worldPoint);

        public static AbilityRig Build(InputActionAsset actions, bool withCursor)
        {
            var rig = new AbilityRig { World = new TestWorld() };
            var world = rig.World;
            world.CreateEnvironment((new Vector3(-6f, 1.5f, 0f), new Vector3(4f, 3f, 0.5f)));
            rig.Encounter = world.CreateEncounter();
            rig.Aimed = world.CreateAimedShot();
            rig.Blast = world.CreateBlast();
            rig.Mend = world.CreateMend();
            rig.Caster = world.CreateFriendlyFighter(CasterGround);
            rig.Ally = world.CreateFriendlyFighter(AllyGround);
            rig.CasterHealth = rig.Caster.GetComponent<Health>();
            rig.AllyHealth = rig.Ally.GetComponent<Health>();
            rig.Abilities = world.AddAbilities(rig.Caster.Unit, rig.Encounter, rig.Aimed, rig.Blast, rig.Mend);
            rig.Hostile = world.CreateDummy(HostileGround);
            rig.FarHostile = world.CreateDummy(FarHostileGround);
            rig.WalledHostile = world.CreateDummy(WalledHostileGround);
            rig.Encounter.Initialize(new[] { rig.CasterHealth, rig.AllyHealth }, new[] { rig.Hostile, rig.FarHostile, rig.WalledHostile });

            var cameraObject = world.Track(new GameObject("Camera"));
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 25f, -20f), Quaternion.Euler(50f, 0f, 0f));
            rig.ViewCamera = cameraObject.AddComponent<Camera>();

            var systems = world.Track(new GameObject("Systems"));
            systems.SetActive(false);
            rig.Pause = systems.AddComponent<TacticalPause>();
            rig.Selection = systems.AddComponent<UnitSelection>();
            rig.Selection.Initialize(rig.Caster, rig.Ally);
            rig.Active = systems.AddComponent<ActiveCharacter>();
            rig.Active.Initialize(rig.Caster.Unit, rig.Pause, rig.Selection);
            if (withCursor)
            {
                rig.Cursor = systems.AddComponent<TacticalCursor>();
                rig.Cursor.Initialize(rig.ViewCamera, rig.Active, rig.Selection, rig.Encounter, null, null,
                    TestControls.Ref(actions, "Commands/CursorMove"),
                    TestControls.Ref(actions, "Camera/CameraModifier"),
                    TestControls.Ref(actions, "Commands/NextTarget"),
                    TestControls.Ref(actions, "Commands/PreviousTarget"));
                rig.Gate = systems.AddComponent<AbilityMenuGate>();
                rig.Gate.Initialize(TestControls.Ref(actions, "Commands/AbilityMenu"),
                    TestControls.Ref(actions, "Commands/Stop"), TestControls.Ref(actions, "Character/ToggleFollow"),
                    TestControls.Ref(actions, "Commands/NextTarget"), TestControls.Ref(actions, "Commands/PreviousTarget"));
            }
            rig.Targeting = systems.AddComponent<AbilityTargeting>();
            rig.Targeting.Initialize(rig.ViewCamera, rig.Selection, rig.Active, rig.Cursor,
                TestControls.Ref(actions, "Commands/PointerPosition"), TestControls.Ref(actions, "Commands/QueueModifier"),
                TestControls.Ref(actions, "Commands/Ability1"), TestControls.Ref(actions, "Commands/Ability2"),
                TestControls.Ref(actions, "Commands/Ability3"), TestControls.Ref(actions, "Commands/Ability4"));
            rig.Input = systems.AddComponent<PlayerCommandInput>();
            rig.Input.Initialize(rig.ViewCamera, rig.Selection, rig.Pause,
                TestControls.Ref(actions, "Commands/Command"),
                TestControls.Ref(actions, "Commands/PointerPosition"),
                TestControls.Ref(actions, "Commands/ToggleTacticalPause"),
                TestControls.Ref(actions, "Commands/QueueModifier"),
                TestControls.Ref(actions, "Commands/Stop"),
                TestControls.Ref(actions, "Commands/Cancel"),
                rig.Active, null, rig.Cursor,
                withCursor ? TestControls.Ref(actions, "Commands/Confirm") : null,
                withCursor ? TestControls.Ref(actions, "Commands/Attack") : null,
                rig.Targeting);
            systems.SetActive(true);
            return rig;
        }
    }
}
#endif
