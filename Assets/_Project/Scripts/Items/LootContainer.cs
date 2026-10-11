using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// A generated loot container: a solid prop with an ordinary MissionInteractable, so clicking it, the Interact key, the
    /// pad's context Confirm, queueing during pause, cursor snapping and the fog rules all work with no special input
    /// code. The first interaction searches it (SearchSeconds of scaled time); afterwards it stays interactable with a
    /// duration of 0, and every completed interaction raises OpenRequested with the unit that did it, which is how the
    /// loot panel learns what to show. Its contents are an uncapped ItemInventory; nothing can read them as "known" until
    /// IsSearched (LootKnowledge).
    /// </summary>
    [RequireComponent(typeof(MissionInteractable))]
    public sealed class LootContainer : MonoBehaviour
    {
        public const float SearchSeconds = 1.5f;
        public const string SearchLabel = "Search container";
        public const string OpenLabel = "Open container";

        ItemInventory contents = new ItemInventory(0);
        MissionInteractable interactable;
        bool searched;

        public ItemInventory Contents => contents;
        public bool IsSearched => searched;
        public bool IsEmpty => contents.Count == 0;
        public Vector3 Position => transform.position;

        public MissionInteractable Interactable
        {
            get
            {
                if (interactable == null)
                    interactable = GetComponent<MissionInteractable>();
                return interactable;
            }
        }

        /// <summary>Raised after every completed interaction (the search and each later open), with the unit that did it.</summary>
        public event Action<CommandableUnit> OpenRequested;

        /// <summary>Raised when the contents change (a take).</summary>
        public event Action Changed;

        internal void Initialize(IEnumerable<LootItemPlan> plan, ItemCatalogue catalogue, float searchSeconds = SearchSeconds)
        {
            contents = new ItemInventory(0);
            foreach (var item in plan)
            {
                var definition = catalogue != null ? catalogue.Find(item.DefinitionId) : null;
                if (definition == null)
                {
                    Debug.LogError($"{name}: loot refers to an unknown item '{item.DefinitionId}'.", this);
                    continue;
                }
                contents.Add(definition, item.Quantity);
            }
            Arm(searchSeconds);
        }

        /// <summary>Test and tool entry: use these contents as they are, optionally already searched.</summary>
        internal void InitializeWith(ItemInventory ready, bool searched)
        {
            contents = ready;
            // Arm re-initialises the interactable as an unsearched container (search label and duration, available).
            Arm(SearchSeconds);
            this.searched = searched;
            if (searched)
                Interactable.Initialize(MissionContent.InteractionRange, 0f, OpenLabel);
            RefreshAvailability();
        }

        // A searched container with nothing left is no longer an interact target (it must not win the Interact key or the
        // pad's context Confirm over a terminal); giving it contents again re-arms it through InitializeWith.
        void RefreshAvailability()
        {
            if (searched && IsEmpty)
                Interactable.SetAvailable(false);
        }

        void Arm(float searchSeconds)
        {
            var item = Interactable;
            item.Initialize(MissionContent.InteractionRange, searchSeconds, SearchLabel);
            item.SetRepeatable(0f);
            item.SetAvailable(true);
            item.Completed -= OnCompleted;
            item.Completed += OnCompleted;
        }

        void OnCompleted(MissionInteractable _)
        {
            if (!searched)
            {
                searched = true;
                Interactable.SetLabel(OpenLabel);
            }
            OpenRequested?.Invoke(Interactable.LastUser);
        }

        internal void NotifyChanged()
        {
            RefreshAvailability();
            Changed?.Invoke();
        }

        void OnDestroy()
        {
            if (interactable != null)
                interactable.Completed -= OnCompleted;
        }
    }
}
