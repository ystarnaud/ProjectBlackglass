#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class HudNoWeaponPlayModeTests
    {
        const string Items = "Assets/_Project/Data/Items/";
        const string Ops = "Assets/_Project/Data/Operatives/";
        MissionRig rig;
        SquadRoster roster;
        readonly HudSnapshotBuilder builder = new HudSnapshotBuilder();
        readonly HudSnapshot snapshot = new HudSnapshot();

        static T Load<T>(string path) where T : Object => AssetDatabase.LoadAssetAtPath<T>(path);

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        IEnumerator Start(bool unequipWeapon)
        {
            rig = new MissionRig();
            var squad = new[] { "Darius", "Kestrel", "Sable" }.Select(n => Load<OperativeDefinition>($"{Ops}Definitions/{n}.asset")).ToArray();
            roster = rig.AddRoster(squad, Load<ProgressionTrack>(Ops + "Progression.asset"));
            var inventory = rig.AddInventory(Load<ItemCatalogue>(Items + "ItemCatalogue.asset"), Load<StarterLoadout>(Items + "StarterLoadout.asset"), roster);
            if (unequipWeapon)
                Assert.That(inventory.Core.Unequip(roster.Members[0].Id, ItemSlot.Weapon).Ok, Is.True);
            yield return rig.Generate(12345);
        }

        HudSources Sources() => new HudSources
        {
            activeCharacter = rig.Active, selection = rig.Selection, tacticalPause = rig.Pause, encounter = rig.Encounter,
            director = rig.Director, roster = roster,
        };

        [UnityTest]
        public IEnumerator AnUnarmedControlledUnit_SaysSo_OnTheOperativePanel()
        {
            yield return Start(unequipWeapon: true);

            builder.Build(Sources(), snapshot, 0f);
            Assert.That(snapshot.ControlledWeapon, Is.EqualTo("No weapon equipped"));

            var root = (RectTransform)new GameObject("Root", typeof(RectTransform)).transform;
            var panel = new OperativePanel(root);
            panel.Apply(snapshot);
            Assert.That(panel.CoverLabel.text, Does.Contain("No weapon equipped"));
            Object.DestroyImmediate(root.gameObject);
        }

        [UnityTest]
        public IEnumerator AnArmedControlledUnit_ShowsTheWeaponName()
        {
            yield return Start(unequipWeapon: false);

            builder.Build(Sources(), snapshot, 0f);

            Assert.That(snapshot.ControlledWeapon, Is.EqualTo("Service Rifle"));
        }

        [UnityTest]
        public IEnumerator AUnitWithoutAnOperative_ShowsNoWeaponLine()
        {
            rig = new MissionRig();
            yield return rig.Generate(12345);

            builder.Build(new HudSources { activeCharacter = rig.Active, selection = rig.Selection, director = rig.Director }, snapshot, 0f);

            Assert.That(snapshot.ControlledWeapon, Is.Empty);
        }
    }
}
#endif
