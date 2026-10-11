using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Developer preview for the Presentation Test Scene only. Every button drives the real gameplay path on scene dummies
    /// (a real UnitAttacker, a real Health, the real TacticalPause) or, for surface impacts, hands the same presentation path
    /// a synthetic shot record. It never touches missions, XP, inventory or operatives. The "fog" switch only ever adds a
    /// restriction (the right half of the arena becomes hidden ground); nothing here can widen what normal play shows.
    /// </summary>
    public sealed class PresentationPreviewPanel : MonoBehaviour
    {
        [SerializeField] CombatPresenter presenter;
        [SerializeField] TacticalPause pause;
        [SerializeField] UnitAttacker shooter;
        [SerializeField] UnitAttacker shooterRight;
        [SerializeField] Health target;
        [SerializeField] Health targetRight;
        [SerializeField] Health healee;
        [SerializeField] Transform concreteBlock;
        [SerializeField] Transform metalBlock;
        [SerializeField] UnitAbilities caster;
        [SerializeField] AbilityDefinition blast;
        [SerializeField] Transform blastPoint;

        int sequence;
        bool fogSimulated;

        public bool FogSimulated => fogSimulated;

        public void FireAtCharacter() => shooter.TryAttack(target);

        public void FireRightShooter() => shooterRight.TryAttack(targetRight);

        public void FireAtConcrete() => FireAtSurface(concreteBlock);

        public void FireAtMetal() => FireAtSurface(metalBlock);

        // A synthetic cover-miss record aimed just behind the block: the shot geometry finds the block's own surface.
        void FireAtSurface(Transform block)
        {
            var presentation = shooter.GetComponent<UnitPresentation>();
            var origin = shooter.transform.position + Vector3.up * LineOfSight.EyeHeight;
            var behind = block.position + Vector3.forward * (block.localScale.z * 0.5f + 3f);
            var record = new ShotRecord(GetInstanceID(), ++sequence, shooter.GetComponent<Health>(), null, origin,
                new Vector3(block.position.x, 1.3f, behind.z), ShotOutcome.CoverMiss, false, null);
            presentation.Deliver(record);
        }

        public void FireShotPastTarget()
        {
            var presentation = shooter.GetComponent<UnitPresentation>();
            var origin = shooter.transform.position + Vector3.up * LineOfSight.EyeHeight;
            var record = new ShotRecord(GetInstanceID(), ++sequence, shooter.GetComponent<Health>(), target, origin,
                target.transform.position + Vector3.up * ShotRecord.BodyHeight, ShotOutcome.CoverMiss, false, null);
            presentation.Deliver(record);
        }

        public void Stress()
        {
            for (var i = 0; i < 40; i++)
            {
                shooter.TryAttack(target);
                shooterRight.TryAttack(targetRight);
            }
        }

        public void CastBlast() => caster.TryUse(AbilityCommand.AtGround(blast, blastPoint.position));

        // Real damage then real healing on the dummy: the pulse comes from Health.Healed, as it does in a mission.
        public void HealDummy()
        {
            healee.TakeDamage(40);
            healee.Heal(40);
        }

        public void TogglePause() => pause.Toggle();

        public void ToggleFogSimulation()
        {
            fogSimulated = !fogSimulated;
            if (fogSimulated)
                presenter.UseKnowledge(p => p.x > 0f ? PointVisibility.Hidden : PointVisibility.Visible, u => u.transform.position.x <= 0f);
            else
                presenter.ClearKnowledgeOverride();
            presenter.Regate();
        }

        public void TogglePresentation() => presenter.Enabled = !presenter.Enabled;

        public void CycleQuality()
        {
            var next = (PresentationQuality)(((int)presenter.Settings.quality + 1) % 3);
            presenter.SetQuality(next);
        }

        void OnGUI()
        {
            GUILayout.BeginArea(new Rect(10f, 10f, 280f, Screen.height - 20f), GUI.skin.box);
            GUILayout.Label("Combat presentation preview (scene dummies only)");
            if (GUILayout.Button("Fire at character (real attack)")) FireAtCharacter();
            if (GUILayout.Button("Fire at concrete block")) FireAtConcrete();
            if (GUILayout.Button("Fire at metal block")) FireAtMetal();
            if (GUILayout.Button("Shot past the target (cover miss)")) FireShotPastTarget();
            if (GUILayout.Button("Fire from the right shooter")) FireRightShooter();
            if (GUILayout.Button("Stress: 40 shots x2")) Stress();
            if (GUILayout.Button("Cast Blast (real ability, radius 3)")) CastBlast();
            if (GUILayout.Button("Heal pulse (real Health.Heal)")) HealDummy();
            GUILayout.Space(6f);
            if (GUILayout.Button(pause.IsPaused ? "Resume" : "Tactical pause")) TogglePause();
            if (GUILayout.Button(fogSimulated ? "Fog simulation: ON (right half hidden)" : "Fog simulation: off")) ToggleFogSimulation();
            if (GUILayout.Button(presenter.Enabled ? "Presentation: ON" : "Presentation: OFF")) TogglePresentation();
            if (GUILayout.Button("Quality: " + presenter.Settings.quality)) CycleQuality();
            GUILayout.Label("Camera shake " + presenter.Settings.cameraShake.ToString("0.00"));
            presenter.Settings.cameraShake = GUILayout.HorizontalSlider(presenter.Settings.cameraShake, 0f, 1f);
            GUILayout.Label($"Active effects {presenter.ActiveEffects}  lights {presenter.Budget.LightsActive}  refused {presenter.Budget.Refused}");
            GUILayout.EndArea();
        }
    }
}
