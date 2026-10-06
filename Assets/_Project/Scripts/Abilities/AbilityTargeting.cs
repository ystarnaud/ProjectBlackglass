using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>The live answer to "what would happen if I confirmed now": the armed ability, what the pointer is on, its validation.</summary>
    public readonly struct AbilityPreview
    {
        public AbilityPreview(AbilityDefinition ability, PointerTargetKind kind, Health target, Vector3 point, bool hasAim,
            AbilityCheck check, bool queued, bool canWalk = true)
        {
            CanWalk = canWalk;
            Ability = ability;
            Kind = kind;
            Target = target;
            Point = point;
            HasAim = hasAim;
            Check = check;
            Queued = queued;
        }

        public static AbilityPreview None => default;

        /// <summary>The armed ability; null when nothing is armed.</summary>
        public AbilityDefinition Ability { get; }
        public PointerTargetKind Kind { get; }
        /// <summary>The unit pointed at (unit abilities), else null.</summary>
        public Health Target { get; }
        /// <summary>The aim: the target's position, or the ground point.</summary>
        public Vector3 Point { get; }
        /// <summary>False while the pointer is on nothing usable (no unit under it, or off the world).</summary>
        public bool HasAim { get; }
        public AbilityCheck Check { get; }
        /// <summary>The queue modifier is held and the caster has orders: only the static checks were run (it is judged when it runs).</summary>
        public bool Queued { get; }
        /// <summary>
        /// The caster may walk into position for this order: it is not being steered (ruling R10). While it is, an order
        /// that would have to walk is refused, so it previews as a refusal.
        /// </summary>
        public bool CanWalk { get; }

        public bool IsArmed => Ability != null;
        /// <summary>Usable from where the caster stands now.</summary>
        public bool IsValid => HasAim && Check.IsValid;

        /// <summary>
        /// Not usable from here, but confirming is accepted: the only problem is range or line of sight, so the caster
        /// first walks into range or to a firing position (decision 029). Neither valid nor refused. Never while the caster
        /// is being steered (CanWalk false): that order would be refused with its reason.
        /// </summary>
        public bool WillApproach => IsArmed && HasAim && CanWalk && AbilityRules.IsApproachable(Check.Failure);

        public AbilityFailure Failure =>
            !IsArmed ? AbilityFailure.None
            : HasAim ? Check.Failure
            : Ability.TargetMode == AbilityTargetMode.Ground ? AbilityFailure.NoPosition : AbilityFailure.NoTarget;
    }

    /// <summary>
    /// The armed ability and its aiming. Input (the Ability1..4 actions) arms a slot of the caster's abilities; while
    /// armed this resolves what the pointer is on every frame (the mouse through PointerTargetResolver, the controller
    /// through the TacticalCursor, which it also puts into aiming mode and tells what to snap to), validates it with the
    /// caster's UnitAbilities, and exposes the result as Preview for the views. Confirm turns a click or the cursor's
    /// Confirm into an AbilityCommand through the normal Issue path (so tactical queueing, pause and direct control all
    /// share it) and disarms. It holds no ability rules and no combat. The caster is the first eligible selected unit,
    /// else the active character; arming never survives a change of caster or the caster's death.
    /// </summary>
    public sealed class AbilityTargeting : MonoBehaviour
    {
        [SerializeField] Camera viewCamera;
        [SerializeField] UnitSelection selection;
        [SerializeField] ActiveCharacter activeCharacter;
        // Optional: the controller's pointer. Armed, it owns the right stick and snaps to the side the ability needs.
        [SerializeField] TacticalCursor cursor;
        [SerializeField, Min(1f)] float maxDistance = 500f;
        [SerializeField] LayerMask clickableLayers = ~0;

        [Header("Input")]
        // The pointer and the queue modifier are owned (and disabled) by PlayerCommandInput; this only enables and reads them.
        [SerializeField] InputActionReference pointerPositionAction;
        [SerializeField] InputActionReference queueModifierAction;
        [SerializeField] InputActionReference ability1Action;
        [SerializeField] InputActionReference ability2Action;
        [SerializeField] InputActionReference ability3Action;
        [SerializeField] InputActionReference ability4Action;

        readonly List<Health> areaHits = new List<Health>();
        CommandableUnit caster;
        UnitAbilities casterAbilities;
        int armedSlot = -1;
        AbilityPreview preview;

        public CommandableUnit Caster => caster;
        public UnitAbilities CasterAbilities => casterAbilities;
        public int ArmedSlot => armedSlot;
        public bool IsArmed => armedSlot >= 0;
        public AbilityDefinition ArmedAbility => IsArmed && casterAbilities != null ? casterAbilities.Definition(armedSlot) : null;
        public AbilityPreview Preview => preview;

        /// <summary>The hostiles a ground ability would hit at the current aim (valid until the next update).</summary>
        public IReadOnlyList<Health> AreaHits => areaHits;

        bool QueueHeld => InputActionUtility.IsPressed(queueModifierAction);

        internal void Initialize(Camera camera, UnitSelection unitSelection, ActiveCharacter active, TacticalCursor tacticalCursor,
            InputActionReference pointerPosition, InputActionReference queueModifier, InputActionReference ability1,
            InputActionReference ability2, InputActionReference ability3, InputActionReference ability4)
        {
            viewCamera = camera;
            selection = unitSelection;
            activeCharacter = active;
            cursor = tacticalCursor;
            pointerPositionAction = pointerPosition;
            queueModifierAction = queueModifier;
            ability1Action = ability1;
            ability2Action = ability2;
            ability3Action = ability3;
            ability4Action = ability4;
        }

        void OnEnable()
        {
            InputActionUtility.SetEnabled(true, pointerPositionAction, queueModifierAction, ability1Action, ability2Action,
                ability3Action, ability4Action);
            Subscribe(ability1Action, OnAbility1);
            Subscribe(ability2Action, OnAbility2);
            Subscribe(ability3Action, OnAbility3);
            Subscribe(ability4Action, OnAbility4);
        }

        void OnDisable()
        {
            Unsubscribe(ability1Action, OnAbility1);
            Unsubscribe(ability2Action, OnAbility2);
            Unsubscribe(ability3Action, OnAbility3);
            Unsubscribe(ability4Action, OnAbility4);
            InputActionUtility.SetEnabled(false, ability1Action, ability2Action, ability3Action, ability4Action);
            Disarm();
        }

        static void Subscribe(InputActionReference reference, System.Action<InputAction.CallbackContext> handler)
        {
            if (reference != null)
                reference.action.performed += handler;
        }

        static void Unsubscribe(InputActionReference reference, System.Action<InputAction.CallbackContext> handler)
        {
            if (reference != null)
                reference.action.performed -= handler;
        }

        void OnAbility1(InputAction.CallbackContext context) => Pick(0);
        void OnAbility2(InputAction.CallbackContext context) => Pick(1);
        void OnAbility3(InputAction.CallbackContext context) => Pick(2);
        void OnAbility4(InputAction.CallbackContext context) => Pick(3);

        void Update()
        {
            RefreshCaster();
            if (IsArmed && (caster == null || !caster.IsAlive || casterAbilities == null || casterAbilities.Definition(armedSlot) == null))
                Disarm();
            preview = IsArmed ? BuildPreview() : AbilityPreview.None;
        }

        /// <summary>Arms the slot for the current caster. False for an empty slot or when nobody can cast.</summary>
        public bool Arm(int slot)
        {
            RefreshCaster();
            if (caster == null || !caster.IsAlive || casterAbilities == null)
                return false;
            var ability = casterAbilities.Definition(slot);
            if (ability == null)
                return false;
            armedSlot = slot;
            if (cursor != null)
            {
                cursor.Aiming = true;
                cursor.SnapTo = SnapFor(ability);
            }
            return true;
        }

        /// <summary>Backs out of the armed ability and gives the cursor and the stick back.</summary>
        public void Disarm()
        {
            armedSlot = -1;
            preview = AbilityPreview.None;
            areaHits.Clear();
            if (cursor != null)
            {
                cursor.Aiming = false;
                cursor.SnapTo = PointerTargetKind.None;
            }
        }

        /// <summary>
        /// Uses the armed ability on what was clicked or confirmed: builds the command and issues it to the caster (queued
        /// behind its orders when `queue`, else replacing them). Disarms and returns true when the order was accepted (also
        /// when the caster first has to walk into range or sight, decision 029). On a refusal the reason is on
        /// CasterAbilities.LastFailure and the ability stays armed, so another target can be tried.
        /// Call it only from input callbacks (PlayerCommandInput's click and Confirm handlers): companion parking relies on
        /// CompanionAI (execution order -150) seeing the order before CommandableUnit (0) runs.
        /// </summary>
        public bool Confirm(PointerTarget pointed, bool queue)
        {
            RefreshCaster();
            var ability = ArmedAbility;
            if (ability == null || caster == null || pointed.Kind == PointerTargetKind.None)
                return false;

            AbilityCommand command;
            if (ability.TargetMode == AbilityTargetMode.Ground)
            {
                command = AbilityCommand.AtGround(ability, pointed.Point);
            }
            else
            {
                var target = TargetHealth(pointed);
                if (target == null)
                {
                    casterAbilities.ReportFailure(ability, AbilityFailure.NoTarget);
                    return false;
                }
                command = AbilityCommand.OnUnit(ability, target);
            }

            if (!caster.Issue(command, queue ? IssueMode.Append : IssueMode.Replace))
                return false;
            Disarm();
            return true;
        }

        /// <summary>What confirming `pointed` would do for `ability`: its validation, and for an area the hostiles it would hit.</summary>
        internal AbilityPreview Evaluate(AbilityDefinition ability, PointerTarget pointed, bool queued)
        {
            var scope = queued ? AbilityCheckScope.Static : AbilityCheckScope.Full;
            // The same test Issue makes (ruling R10): a caster being steered cannot walk into position.
            var canWalk = caster == null || caster.CanWalkToCast;
            areaHits.Clear();
            if (ability.TargetMode == AbilityTargetMode.Ground)
            {
                if (pointed.Kind == PointerTargetKind.None)
                    return new AbilityPreview(ability, pointed.Kind, null, default, false, default, queued, canWalk);
                var check = casterAbilities.Check(ability, null, pointed.Point, scope);
                casterAbilities.CollectArea(ability, pointed.Point, areaHits);
                return new AbilityPreview(ability, pointed.Kind, null, pointed.Point, true, check, queued, canWalk);
            }

            var target = TargetHealth(pointed);
            if (target == null)
                return new AbilityPreview(ability, pointed.Kind, null, pointed.Point, false, default, queued, canWalk);
            return new AbilityPreview(ability, pointed.Kind, target, target.transform.position, true,
                casterAbilities.Check(ability, target, null, scope), queued, canWalk);
        }

        /// <summary>Arms the slot, or backs out when that slot is the armed one.</summary>
        internal void Pick(int slot)
        {
            if (IsArmed && armedSlot == slot)
                Disarm();
            else
                Arm(slot);
        }

        AbilityPreview BuildPreview()
        {
            var queued = QueueHeld && caster.CurrentCommand != null;
            return Evaluate(ArmedAbility, ResolvePointer(), queued);
        }

        // The controller's cursor when it is the pointer in use, else the mouse.
        PointerTarget ResolvePointer()
        {
            if (cursor != null && cursor.IsActive)
                return cursor.Target;
            if (viewCamera == null)
                return PointerTarget.None;
            return PointerTargetResolver.Resolve(viewCamera, InputActionUtility.Read<Vector2>(pointerPositionAction),
                maxDistance, clickableLayers, null, 0f);
        }

        static Health TargetHealth(PointerTarget pointed)
        {
            switch (pointed.Kind)
            {
                case PointerTargetKind.Hostile:
                    return pointed.Hostile;
                case PointerTargetKind.Friendly:
                    return pointed.Friendly != null ? pointed.Friendly.GetComponent<Health>() : null;
                default:
                    return null;
            }
        }

        static PointerTargetKind SnapFor(AbilityDefinition ability) =>
            ability.TargetMode == AbilityTargetMode.Ground ? PointerTargetKind.Ground
            : ability.TargetSide == AbilityTargetSide.Friendly ? PointerTargetKind.Friendly
            : PointerTargetKind.Hostile;

        void RefreshCaster()
        {
            var current = ResolveCaster();
            if (current == caster)
                return;
            caster = current;
            casterAbilities = current != null ? current.GetComponent<UnitAbilities>() : null;
            Disarm();
        }

        // The first eligible selected unit, else the active character (also while paused: the controlled character is
        // the one highlighted, so a pause with nothing selected must not leave abilities unusable).
        CommandableUnit ResolveCaster()
        {
            if (selection != null)
            {
                for (var i = 0; i < selection.Selected.Count; i++)
                {
                    var selected = selection.Selected[i];
                    if (selected != null && ActiveCharacter.IsEligible(selected))
                        return selected.Unit;
                }
            }
            return activeCharacter != null && activeCharacter.HasUnit ? activeCharacter.Unit : null;
        }
    }
}
