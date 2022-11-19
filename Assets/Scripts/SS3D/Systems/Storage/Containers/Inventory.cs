using System.Collections.Generic;
using System.Linq;
using FishNet.Connection;
using FishNet.Object;
using SS3D.Systems.Entities;
using SS3D.Systems.Storage.Items;
using UnityEngine;

namespace SS3D.Systems.Storage.Containers
{
    /**
     * This is the basic inventory system. Any inventory-capable creature should have this component.
     * The basic inventory system has to handle:
     *  - Aggregating all containers on the player and accessible to the player
     *  - The moving of items from one item-slot to another
    */
    public sealed class Inventory : NetworkBehaviour
    {

        /// <summary>
        /// The hands used by this inventory
        /// </summary>
        public Hands Hands;
        
        private readonly List<AttachedContainer> _openedContainers = new();
        private float _nextAccessCheck;

        public delegate void ContainerEventHandler(AttachedContainer container);

        public event ContainerEventHandler ContainerOpened;
        public event ContainerEventHandler ContainerClosed;

        public void Awake()
        {
            Hands.Inventory = this;
        }

        public void Update()
        {
            float time = Time.time;
            if (time > _nextAccessCheck)
            {
                Hands hands = GetComponent<Hands>();
                for (var i = 0; i < _openedContainers.Count; i++)
                {
                    AttachedContainer attachedContainer = _openedContainers[i];
                    if (!hands.CanInteract(attachedContainer.gameObject))
                    {
                        RemoveContainer(attachedContainer);
                        i--;
                    }
                }

                _nextAccessCheck = time + 0.5f;
            }
        }

        /// <summary>
        /// Use it to switch between active hands.
        /// </summary>
        /// <param name="container">This AttachedContainer should be the hand to activate.</param>
        public void ActivateHand(AttachedContainer container)
        {
            Hands.SetActiveHand(container);
        }

        /// <summary>
        /// Interacting with a container that has one "slot"
        /// </summary>
        public void ClientInteractWithSingleSlot(AttachedContainer container)
        {
            // no touchy ;)
            if (Hands == null)
            {
                return;
            }

            if (Hands.SelectedHandEmpty)
            {
                if (!container.Container.Empty)
                {
                    ClientTransferItem(container.Container.Items.First(), Vector2Int.zero, Hands.SelectedHand);
                }
            }
            else
            {
                if (container.Container.Empty)
                {
                    ClientTransferItem(Hands.ItemInHand, Vector2Int.zero, container);
                }
            }
        }

        /// <summary>
        /// Interact with a container at a certain position
        /// </summary>
        /// <param name="container">The container being interacted with</param>
        /// <param name="position">At which position the interaction happened</param>
        public void ClientInteractWithContainerSlot(AttachedContainer container, Vector2Int position)
        {
            if (Hands == null)
            {
                return;
            }

            Item item = container.Container.ItemAt(position);
            if (Hands.SelectedHandEmpty)
            {
                if (item != null)
                {
                    ClientTransferItem(item, Vector2Int.zero, Hands.SelectedHand);
                }
            }
            else
            {
                if (item == null)
                {
                    ClientTransferItem(Hands.ItemInHand, position, container);
                }
            }
        }

        public bool CanModifyContainer(AttachedContainer container)
        {
            // TODO: This root transform check might allow you to take out your own organs down the road O_O
            return _openedContainers.Contains(container) || container.transform.root == transform;
        }

        /// <summary>
        /// Requests the server to transfer an item
        /// </summary>
        /// <param name="item">The item to transfer</param>
        /// <param name="targetContainer">Into which container to move the item</param>
        public void ClientTransferItem(Item item, Vector2Int position, AttachedContainer targetContainer)
        {
            CmdTransferItem(item.gameObject, position, targetContainer);
        }

        /// <summary>
        /// Requests the server to drop an item out of a container
        /// </summary>
        /// <param name="item">The item to drop</param>
        public void ClientDropItem(Item item)
        {
            CmdDropItem(item.gameObject);
        }

        [ServerRpc]
        private void CmdTransferItem(GameObject itemObject, Vector2Int position, AttachedContainer container)
        {
            Item item = itemObject.GetComponent<Item>();
            if (item == null)
            {
                return;
            }

            Container itemContainer = item.Container;
            if (itemContainer == null)
            {
                return;
            }

            AttachedContainer attachedTo = itemContainer.AttachedTo;
            if (attachedTo == null)
            {
                return;
            }

            if (container == null)
            {
                Debug.LogError($"Client sent invalid container reference: NetId {container.ObjectId}");
                return;
            }

            if (!CanModifyContainer(attachedTo) || !CanModifyContainer(container))
            {
                return;
            }

            Hands hands = GetComponent<Hands>();
            if (hands == null || !hands.CanInteract(container.gameObject))
            {
                return;
            }
            
            container.Container.AddItemPosition(item, position);
        }

        /// <summary>
        /// Make this inventory open an container
        /// </summary>
        public void OpenContainer(AttachedContainer container)
        {
            container.AddObserver(GetComponent<PlayerControllable>());
            _openedContainers.Add(container);
            SetOpenState(container.gameObject, true);
            NetworkConnection client = Owner;
            if (client != null)
            {
                TargetOpenContainer(client, container);
            }
        }

        /// <summary>
        /// Removes an container from this inventory
        /// </summary>
        public void RemoveContainer(AttachedContainer container)
        {
            if (_openedContainers.Remove(container))
            {
                Debug.Log("client call remove");
                SetOpenState(container.gameObject, false);
                NetworkConnection client = Owner;
                if (client != null)
                {
                    TargetCloseContainer(client, container.gameObject);
                }
            }
        }

        [ServerRpc]
        public void CmdContainerClose(AttachedContainer container)
        {
            RemoveContainer(container);
        }

        /// <summary>
        /// Does this inventory have a specific container
        /// </summary>
        public bool HasContainer(AttachedContainer container)
        {
            return _openedContainers.Contains(container);
        }

        [ServerRpc]
        private void CmdDropItem(GameObject gameObject)
        {
            Item item = gameObject.GetComponent<Item>();
            if (item == null)
            {
                return;
            }

            AttachedContainer attachedTo = item.Container?.AttachedTo;
            if (attachedTo == null)
            {
                return;
            }

            if (!CanModifyContainer(attachedTo))
            {
                return;
            }

            item.Container = null;
        }

        [TargetRpc]
        private void TargetOpenContainer(NetworkConnection target, AttachedContainer container)
        {
            OnContainerOpened(container.GetComponent<AttachedContainer>());        
        }

        /// <summary>
        /// On containers having OpenWhenContainerViewed set true, this set the containers state appropriately.
        /// If the container is viewed by another entity, it's already opened, and therefore it does nothing.
        /// If this entity is the first to view it, it trigger the open animation of the object.
        /// If the entity is the last to view it, it closes the container.
        /// </summary>
        /// <param name="containerObject"> The container viewed by this entity.</param>
        /// <param name="state"> The state to set in the container, true is opened and false is closed.</param>
        [Server]
        private void SetOpenState(GameObject containerObject, bool state)
        {
            AttachedContainer container = containerObject.GetComponent<AttachedContainer>();

            if (!container.ContainerDescriptor.OpenWhenContainerViewed)
            {
                return;
            }

            Hands hands = GetComponent<Hands>();
            foreach (PlayerControllable observer in container.ObservingPlayers)
            {
                // checks if the container is already viewed by another entity
                if (hands.Inventory.HasContainer(container) && observer != hands)
                {
                    return;
                }
            }

            container.ContainerDescriptor.ContainerInteractive.SetOpenState(state);
        }


        [TargetRpc]
        private void TargetCloseContainer(NetworkConnection target, GameObject container)
        {
            OnContainerClosed(container.GetComponent<AttachedContainer>());    
        }

        /**
         * Graphically adds the item back into the world (for server and all clients).
         * Must be called from server initially
         */
        private void Spawn(GameObject item, Vector3 position, Quaternion rotation)
        {
            // World will be the parent
            item.transform.parent = null;

            Vector3 itemDimensions = item.GetComponentInChildren<Collider>().bounds.size;
            float itemSize = 0;

            for (int i = 0; i < 3; i++)
            {
                if (itemDimensions[i] > itemSize)
                {
                    itemSize = itemDimensions[i];
                }
            }

            float distance = Vector3.Distance(item.transform.position, position);
            position = distance > 0 ? position + new Vector3(0, itemSize * 0.5f, 0) : position;

            if (distance > 0)
            {
                item.transform.LookAt(transform);
            }
            else
            {
                item.transform.rotation = rotation;
            }

            item.transform.position = position;
            item.SetActive(true);

            if (IsServer)
            {
                RpcSpawn(item, position, rotation);
            }
        }

        [ObserversRpc]
        private void RpcSpawn(GameObject item, Vector3 position, Quaternion rotation)
        {
            if (!IsServer) // Silly thing to prevent looping when server and client are one
            {
                Spawn(item, position, rotation);
            }
        }

        private void OnContainerOpened(AttachedContainer container)
        {
            ContainerOpened?.Invoke(container);
        }

        private void OnContainerClosed(AttachedContainer container)
        {
            ContainerClosed?.Invoke(container);
        }
    }
}