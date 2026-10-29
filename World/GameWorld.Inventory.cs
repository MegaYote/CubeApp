using System;
using System.Collections.Generic;
using System.Numerics;

namespace Cubuild
{
    public sealed partial class GameWorld : IDisposable
    {
        public int InventoryCount(int itemId)
        {
            if (itemId <= 0) return 0;
            int total = 0;
            for (int i = 0; i < BagSlotCount; i++)
            {
                if (_bagSlots[i].ItemId == itemId) total += _bagSlots[i].Count;
            }
            for (int i = 0; i < HotbarSlots; i++)
            {
                if (Hotbar[i] == itemId) total += HotbarCounts[i];
            }
            return total;
        }

        // ---- dropped items (survival: mined blocks fall to the ground until collected) ----
        /// <summary>Leaves drop a sapling 1-in-10, otherwise nothing (Minecraft-style).</summary>
        private static readonly int _idLeaves = BlockRegistry.GetId("leaves");
        /// <summary>Gravel drops flint 1-in-10, otherwise gravel (Minecraft-style).</summary>
        private static readonly int _idGravel = BlockRegistry.GetId("gravel");
        private static readonly int _idSapling = BlockRegistry.GetId("sapling");
        private static readonly int _idStick = ItemRegistry.GetId("stick");
        private static readonly int _idLog = BlockRegistry.GetId("log");
        private static readonly int _idStone = BlockRegistry.GetId("stone");
        private static readonly int _idCobblestone = BlockRegistry.GetId("cobblestone");
        private static readonly int _idPlanks = BlockRegistry.GetId("planks");
        private int _idWorkbench;
        private int _idCarpentersWorkbench;
        private int _idMasonsWorkbench;
        private void RefreshWorkbenchIds()
        {
            _idWorkbench = BlockRegistry.GetId("workbench");
            _idCarpentersWorkbench = BlockRegistry.GetId("carpenters_workbench");
            _idMasonsWorkbench = BlockRegistry.GetId("masons_workbench");
        }
        // Flint is a GENUINE item (items.json), not a block - resolve through the item registry.
        private static readonly int _idFlint = ItemRegistry.GetId("flint");
        // Sap is a block whose DROPPED form uses the items.json sap entry's icon.
        private static readonly int _idSap = ItemRegistry.GetId("sap");
        private readonly List<DroppedItem> _droppedItems = new();
        private readonly List<ItemDropRenderData> _itemDropRenderScratch = new();
        public IReadOnlyList<DroppedItem> DroppedItems => _droppedItems;
        /// <summary>Reusable render snapshot of all current drops (no per-frame allocation).</summary>
        public IReadOnlyList<ItemDropRenderData> ItemDropRenderData
        {
            get
            {
                _itemDropRenderScratch.Clear();
                foreach (var d in _droppedItems)
                {
                    _itemDropRenderScratch.Add(new ItemDropRenderData(
                        d.ItemId, d.Position.X, d.Position.Y, d.Position.Z,
                        d.RotX, d.RotY, d.RotZ, d.RotW));
                }
                return _itemDropRenderScratch;
            }
        }
        /// <summary>How long before a drop despawns (seconds).</summary>
        public const float ItemDropDespawnTime = 60f;
        /// <summary>Grace period after spawning before the drop can be collected (seconds), so a
        /// block broken at your feet doesn't snap straight back into your inventory.</summary>
        public const float ItemDropPickupDelay = 0.5f;
        /// <summary>How long the magnet pickup flight lasts (seconds) before the drop is forced
        /// into your inventory. Usually collected earlier on arrival.</summary>
        public const float PickupFlyDuration = 0.45f;
        internal const double SteveDropPickupSearchDistance = 12.0;

        /// <summary>
        /// Player and Steve use the same short-range pickup volume as the existing player
        /// trigger. <paramref name="feetPosition"/> is feet-anchored (unlike PlayerPosition).
        /// </summary>
        internal static bool IsItemDropInPickupRange(DroppedItem drop, Point3D feetPosition)
        {
            if (drop == null) return false;
            double centerY = drop.Position.Y + 0.2;
            return Math.Abs(drop.Position.X - feetPosition.X) < 1.2
                && Math.Abs(drop.Position.Z - feetPosition.Z) < 1.2
                && centerY > feetPosition.Y - 0.5 && centerY < feetPosition.Y + 2.0;
        }

        /// <summary>True for a drop in the player's current pickup volume or already magnetized.</summary>
        internal bool IsDropInLocalPlayerPickupRange(DroppedItem drop)
        {
            if (drop == null || IsCreative) return false;
            if (drop.FlyTime > 0f) return true;
            var playerFeet = new Point3D(LocalPlayer.Position.X,
                LocalPlayer.Position.Y - EyeHeight, LocalPlayer.Position.Z);
            return IsItemDropInPickupRange(drop, playerFeet);
        }

        /// <summary>
        /// Recipe feasibility probe: is there a non-player-claimed copy of this item within the
        /// bounded area where Steve's pickup planner is willing to route?
        /// </summary>
        internal bool HasNearbyStevePickupDrop(SteveMob steve, int itemId)
        {
            if (!IsAuthoritative || steve == null || itemId <= 0) return false;
            double maxRouteDistanceSquared = SteveDropPickupSearchDistance * SteveDropPickupSearchDistance;
            foreach (var drop in _droppedItems)
            {
                if (drop.ItemId != itemId || drop.Count <= 0 || drop.Age <= ItemDropPickupDelay
                    || drop.FlyTime > 0f || IsDropInLocalPlayerPickupRange(drop)) continue;
                if (IsItemDropInPickupRange(drop, steve.Position)) return true;

                int centerX = (int)Math.Floor(drop.Position.X);
                int centerY = (int)Math.Floor(drop.Position.Y);
                int centerZ = (int)Math.Floor(drop.Position.Z);
                for (int sy = centerY - 1; sy <= centerY + 1; sy++)
                for (int sz = centerZ - 1; sz <= centerZ + 1; sz++)
                for (int sx = centerX - 1; sx <= centerX + 1; sx++)
                {
                    if (!SteveMob.IsWalkableStand(this, sx, sy, sz)
                        || !IsItemDropInPickupRange(drop, new Point3D(sx + 0.5, sy, sz + 0.5))) continue;
                    double dx = sx + 0.5 - steve.Position.X;
                    double dy = sy - steve.Position.Y;
                    double dz = sz + 0.5 - steve.Position.Z;
                    if (dx * dx + dy * dy + dz * dz <= maxRouteDistanceSquared) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Transfers a useful physical drop to Steve's private eight-slot inventory. Every check
        /// is repeated on the authoritative simulation thread; a partial pickup shrinks the world
        /// stack and never touches the local player's inventory.
        /// </summary>
        internal bool TryPickupDropForSteve(SteveMob steve, DroppedItem drop, out int pickedUp)
        {
            pickedUp = 0;
            if (!IsAuthoritative || steve == null || steve.IsDead || steve.Removed || drop == null
                || drop.Count <= 0 || drop.Age <= ItemDropPickupDelay || drop.FlyTime > 0f
                || !_droppedItems.Contains(drop) || IsDropInLocalPlayerPickupRange(drop)
                || !IsItemDropInPickupRange(drop, steve.Position)) return false;

            int usefulDemand = steve.GetUsefulShelterDropDemand(this, drop.ItemId);
            int capacity = steve.GetShelterItemCapacity(drop.ItemId);
            int amount = Math.Min(drop.Count, Math.Min(usefulDemand, capacity));
            if (amount <= 0 || !steve.AddShelterItem(drop.ItemId, amount)) return false;

            drop.Count -= amount;
            pickedUp = amount;
            if (drop.Count <= 0) _droppedItems.Remove(drop);
            return true;
        }

        /// <summary>Spawns a physical item drop at a world position (survival only). If
        /// <paramref name="throwVelocity"/> is given the drop flies that way (thrown items);
        /// otherwise it gets a small random kick.</summary>
        public void SpawnItemDrop(int itemId, int count, Point3D worldPos, Point3D? throwVelocity = null)
        {
            if (itemId <= 0 || count <= 0) return;
            if (_droppedItems.Count > 256) _droppedItems.RemoveAt(0); // hard cap
            var rand = _regenRandom;
            Point3D vel;
            if (throwVelocity.HasValue)
            {
                vel = throwVelocity.Value;
            }
            else
            {
                vel = new Point3D(
                    (rand.NextDouble() * 4.0 - 2.0) * 0.6,
                    3.2,
                    (rand.NextDouble() * 4.0 - 2.0) * 0.6);
            }
            var drop = new DroppedItem
            {
                ItemId = itemId,
                Count = count,
                Position = worldPos,
                Velocity = vel,
                Age = 0f,
            };
            // Random tumble: a random axis and a decent spin rate so it reads physical.
            drop.SpinAxisX = (float)(rand.NextDouble() * 2.0 - 1.0);
            drop.SpinAxisY = (float)(rand.NextDouble() * 2.0 - 1.0);
            drop.SpinAxisZ = (float)(rand.NextDouble() * 2.0 - 1.0);
            double axLen = Math.Sqrt(drop.SpinAxisX * drop.SpinAxisX + drop.SpinAxisY * drop.SpinAxisY + drop.SpinAxisZ * drop.SpinAxisZ);
            if (axLen > 0.0001)
            {
                drop.SpinAxisX /= (float)axLen;
                drop.SpinAxisY /= (float)axLen;
                drop.SpinAxisZ /= (float)axLen;
            }
            else
            {
                drop.SpinAxisX = 0; drop.SpinAxisY = 1; drop.SpinAxisZ = 0;
            }
            drop.SpinSpeed = 5f + (float)rand.NextDouble() * 6f; // 5..11 rad/s
            _droppedItems.Add(drop);
        }

        /// <summary>Adds items to the inventory (e.g. a mined block or collected drop), stacking up to
        /// the item's own stack size (tools cap at 1): hotbar first (matching then empty), then
        /// the bag.</summary>
        public bool TryAddToInventory(int itemId, int count)
            => TryAddToInventory(itemId, count, out _);

        /// <summary>
        /// As the bool overload, but reports how many items were ACTUALLY added via
        /// <paramref name="added"/>. A bool can't express a partial add: a drop of 64 into an
        /// inventory with one free slot returns true (something fit) while 63 items are still
        /// unaccounted for, which is how ground drops used to get silently destroyed.
        /// </summary>
        public bool TryAddToInventory(int itemId, int count, out int added)
        {
            added = 0;
            if (itemId <= 0 || count <= 0) return false;
            int remaining = count;
            int air = BlockRegistry.AirId;
            int cap = Math.Min(MaxStackSize, ItemRegistry.StackSizeOf(itemId));

            for (int i = 0; i < HotbarSlots && remaining > 0; i++)
            {
                if (Hotbar[i] == itemId && HotbarCounts[i] < cap)
                {
                    int add = Math.Min(remaining, cap - HotbarCounts[i]);
                    HotbarCounts[i] += add;
                    remaining -= add;
                }
            }
            for (int i = 0; i < BagSlotCount && remaining > 0; i++)
            {
                if (_bagSlots[i].ItemId == itemId && _bagSlots[i].Count < cap)
                {
                    int add = Math.Min(remaining, cap - _bagSlots[i].Count);
                    _bagSlots[i].Count += add;
                    remaining -= add;
                }
            }
            for (int i = 0; i < HotbarSlots && remaining > 0; i++)
            {
                if (Hotbar[i] == air)
                {
                    int add = Math.Min(remaining, cap);
                    Hotbar[i] = itemId;
                    HotbarCounts[i] = add;
                    remaining -= add;
                }
            }
            for (int i = 0; i < BagSlotCount && remaining > 0; i++)
            {
                if (_bagSlots[i].IsEmpty)
                {
                    int add = Math.Min(remaining, cap);
                    _bagSlots[i] = new InventorySlot { ItemId = itemId, Count = add };
                    remaining -= add;
                }
            }
            added = count - remaining;
            return added > 0;
        }

        /// <summary>Collects an item, preferring the first hotbar slot that already holds that item,
        /// then the first empty hotbar slot; otherwise it goes into the bag. If the player has
        /// nothing selected, picking up fills the selected slot so it is usable right away.
        ///
        /// Returns how many items were actually taken. This can be LESS than
        /// <paramref name="count"/> when the inventory is full or partly full - callers that
        /// destroy a source (like the ground-drop magnet) must use this return value to decide
        /// whether to remove it or shrink it, never just assume everything fitted.</summary>
        public int CollectItem(int itemId, int count = 1)
        {
            if (itemId <= 0 || count <= 0) return 0;
            int air = BlockRegistry.AirId;

            // A hotbar slot already holding this item is the best merge target.
            for (int i = 0; i < HotbarSlots; i++)
            {
                if (Hotbar[i] == itemId)
                {
                    TryAddToInventory(itemId, count, out int added);
                    return added;
                }
            }
            // Otherwise claim an empty hotbar slot so it lands somewhere immediately usable.
            for (int i = 0; i < HotbarSlots; i++)
            {
                if (Hotbar[i] == air)
                {
                    Hotbar[i] = itemId;
                    TryAddToInventory(itemId, count, out int added);
                    if (SelectedBlock <= 0)
                    {
                        SelectedBlock = itemId;
                        SelectedSlot = i;
                    }
                    return added;
                }
            }
            TryAddToInventory(itemId, count, out int toBag);
            return toBag;
        }

        /// <summary>
        /// Puts the stack riding the inventory cursor back into the player when a container UI
        /// closes, so it can't be orphaned on a phantom "held stack" that is neither in the
        /// inventory nor on the ground. Anything the inventory can't take is dropped at the
        /// player's feet rather than destroyed.
        /// </summary>
        public void ReturnHeldStackToInventory()
        {
            if (HeldStack is not { } held) return;
            int itemId = held.ItemId;
            int count = held.Count;
            if (itemId <= 0 || count <= 0) { HeldStack = null; return; }

            HeldStack = null; // clear first so a failed add can't recurse back into the cursor
            TryAddToInventory(itemId, count, out int added);
            int leftover = count - added;
            // Creative is infinite anyway, so overflow is discarded rather than dropped on the
            // floor - and survival drops don't magnetise in creative, so a spawned one would
            // just sit there until it despawned.
            if (leftover > 0 && !IsCreative)
            {
                SpawnItemDrop(itemId, leftover, new Point3D(
                    LocalPlayer.Position.X, LocalPlayer.Position.Y - EyeHeight + 0.4, LocalPlayer.Position.Z));
            }
        }

        // ---- inventory cursor (ported drag logic from the C++ E-menu) ----
        /// <summary>
        /// Puts a stack back in the bag, spilling any overflow on the floor at the player's
        /// feet. Used when a container (the crafting grid) hands items back - same rules as
        /// <see cref="ReturnHeldStackToInventory"/>, so nothing is ever silently destroyed.
        /// </summary>
        public void GiveOrDrop(int itemId, int count)
        {
            if (itemId <= 0 || count <= 0) return;
            TryAddToInventory(itemId, count, out int added);
            int leftover = count - added;
            if (leftover > 0 && !IsCreative)
            {
                SpawnItemDrop(itemId, leftover, new Point3D(
                    LocalPlayer.Position.X, LocalPlayer.Position.Y - EyeHeight + 0.4, LocalPlayer.Position.Z));
            }
        }

        /// <summary>The stack riding the mouse cursor while the inventory is open.</summary>
        public (int ItemId, int Count)? HeldStack { get; set; }

        /// <summary>Restores the whole bag from a save (Minecraft-style: everything persists).</summary>
        public void RestoreBag(ReadOnlySpan<InventorySlot> slots)
        {
            for (int i = 0; i < BagSlotCount; i++)
            {
                _bagSlots[i] = i < slots.Length ? slots[i] : default;
            }
        }

        // Unified slot access: 0..39 = bag, 40..49 = hotbar.
        private InventorySlot GetSlot(int slot)
        {
            if (slot < BagSlotCount) return _bagSlots[slot];
            int hi = slot - BagSlotCount;
            if (hi >= 0 && hi < HotbarSlots)
            {
                return new InventorySlot { ItemId = Hotbar[hi], Count = Hotbar[hi] > 0 ? HotbarCounts[hi] : 0 };
            }
            return default;
        }

        private void SetSlot(int slot, InventorySlot contents)
        {
            if (slot < BagSlotCount)
            {
                _bagSlots[slot] = contents;
                return;
            }
            int hi = slot - BagSlotCount;
            if (hi < 0 || hi >= HotbarSlots) return;
            if (contents.IsEmpty)
            {
                Hotbar[hi] = BlockRegistry.AirId;
                HotbarCounts[hi] = 0;
            }
            else
            {
                Hotbar[hi] = contents.ItemId;
                HotbarCounts[hi] = contents.Count;
            }
        }

        private void ClearSlot(int slot) => SetSlot(slot, default);

        /// <summary>
        /// The C++ drag interaction on one slot. Left click: empty cursor picks up the whole
        /// stack; held cursor places / stacks / swaps. Right click: empty cursor picks up half;
        /// held cursor drops one (same type only).
        /// </summary>
        public void CursorClickSlot(int slot, bool rightClick)
        {
            if (slot < 0 || slot >= BagSlotCount + HotbarSlots) return;
            var clicked = GetSlot(slot);
            var held = HeldStack;

            if (!rightClick)
            {
                // Left click.
                if (held.HasValue)
                {
                    int heldId = held.Value.ItemId;
                    int heldCount = held.Value.Count;
                    int cap = ItemRegistry.StackSizeOf(heldId);
                    if (clicked.IsEmpty)
                    {
                        SetSlot(slot, new InventorySlot { ItemId = heldId, Count = heldCount });
                        HeldStack = null;
                    }
                    else if (clicked.ItemId == heldId && clicked.Count < cap)
                    {
                        int add = Math.Min(heldCount, cap - clicked.Count);
                        clicked.Count += add;
                        SetSlot(slot, clicked);
                        int nc = heldCount - add;
                        HeldStack = nc > 0 ? (heldId, nc) : null;
                    }
                    else
                    {
                        // Swap.
                        SetSlot(slot, new InventorySlot { ItemId = heldId, Count = heldCount });
                        HeldStack = (clicked.ItemId, clicked.Count);
                    }
                }
                else if (!clicked.IsEmpty)
                {
                    HeldStack = (clicked.ItemId, clicked.Count);
                    ClearSlot(slot);
                }
            }
            else
            {
                // Right click.
                if (held.HasValue)
                {
                    int heldId = held.Value.ItemId;
                    int cap = ItemRegistry.StackSizeOf(heldId);
                    bool canPlace = clicked.IsEmpty
                        || (clicked.ItemId == heldId && clicked.Count < cap);
                    if (canPlace)
                    {
                        if (clicked.IsEmpty)
                        {
                            SetSlot(slot, new InventorySlot { ItemId = heldId, Count = 1 });
                        }
                        else
                        {
                            clicked.Count++;
                            SetSlot(slot, clicked);
                        }
                        int nc = held.Value.Count - 1;
                        HeldStack = nc > 0 ? (heldId, nc) : null;
                    }
                }
                else if (!clicked.IsEmpty && clicked.Count > 1)
                {
                    int half = (clicked.Count + 1) / 2;
                    HeldStack = (clicked.ItemId, half);
                    clicked.Count -= half;
                    SetSlot(slot, clicked);
                }
            }

            // Slots 40..49 are the hotbar. Dropping a stack onto the CURRENTLY SELECTED hotbar
            // slot changed Hotbar[] but left SelectedBlock pointing at the old item, so you
            // couldn't build with what you'd just put there until you switched away and back
            // (that path re-syncs, which is why the switch 'fixed' it). Re-sync whenever the
            // selected slot is the one we just wrote to.
            if (slot >= BagSlotCount && slot - BagSlotCount == SelectedSlot)
            {
                SelectedBlock = Hotbar[SelectedSlot];
            }
        }

        // Throw velocity along the player's facing direction (spawned at the feet): fast enough
        // to clear the magnet pickup range so you don't instantly grab the item back.
        private Point3D ThrowVelocity(double speed = 5.5, double up = 0.35)
        {
            double yawRad = LocalPlayer.Yaw * Math.PI / 180.0;
            return new Point3D(Math.Sin(yawRad) * speed, up, Math.Cos(yawRad) * speed);
        }

        /// <summary>Drops items from the cursor into the world as physical drops.</summary>
        public void DropFromCursor(int count)
        {
            var held = HeldStack;
            if (!held.HasValue || count <= 0) return;
            int drop = Math.Min(count, held.Value.Count);
            // Base throw direction from the player's facing.
            double baseYawRad = LocalPlayer.Yaw * Math.PI / 180.0;
            for (int i = 0; i < drop; i++)
            {
                var rand = _regenRandom;
                // Skew the heading by up to ±10° so each item takes its own arc and the
                // pile fans out into a loose cluster.
                double yawRad = baseYawRad + (rand.NextDouble() - 0.5) * 0.35;
                // Speed wobble (±15%) so they land at noticeably different distances too.
                double speed = 5.5 * (1.0 + (rand.NextDouble() - 0.5) * 0.3);
                double up = 0.35 + (rand.NextDouble() - 0.5) * 0.15;
                var throwVel = new Point3D(Math.Sin(yawRad) * speed, up, Math.Cos(yawRad) * speed);
                // Tiny spawn offset so even items on near-identical paths don't land on the
                // exact same spot (throws only travel ~1 block, so angle alone can't spread
                // them - this guarantees visible separation).
                double ox = (rand.NextDouble() - 0.5) * 0.3;
                double oz = (rand.NextDouble() - 0.5) * 0.3;
                SpawnItemDrop(held.Value.ItemId, 1,
                    new Point3D(LocalPlayer.Position.X + ox, LocalPlayer.Position.Y, LocalPlayer.Position.Z + oz), throwVel);
            }
            int nc = held.Value.Count - drop;
            if (nc <= 0) HeldStack = null;
            else HeldStack = (held.Value.ItemId, nc);
        }

        /// <summary>Shift-click quick move (MC): moves a whole stack between the bag and the
        /// hotbar, stacking onto same-type slots first then empty ones.</summary>
        public void QuickMoveSlot(int slot)
        {
            if (slot < 0 || slot >= BagSlotCount + HotbarSlots) return;
            var contents = GetSlot(slot);
            if (contents.IsEmpty) return;
            int remaining = contents.Count;
            int air = BlockRegistry.AirId;
            int cap = ItemRegistry.StackSizeOf(contents.ItemId);

            if (slot < BagSlotCount)
            {
                // Bag -> hotbar.
                for (int i = 0; i < HotbarSlots && remaining > 0; i++)
                {
                    if (Hotbar[i] == contents.ItemId && HotbarCounts[i] < cap)
                    {
                        int add = Math.Min(remaining, cap - HotbarCounts[i]);
                        HotbarCounts[i] += add;
                        remaining -= add;
                    }
                }
                for (int i = 0; i < HotbarSlots && remaining > 0; i++)
                {
                    if (Hotbar[i] == air)
                    {
                        int add = Math.Min(remaining, cap);
                        Hotbar[i] = contents.ItemId;
                        HotbarCounts[i] = add;
                        remaining -= add;
                    }
                }
            }
            else
            {
                // Hotbar -> bag.
                for (int i = 0; i < BagSlotCount && remaining > 0; i++)
                {
                    if (_bagSlots[i].ItemId == contents.ItemId && _bagSlots[i].Count < cap)
                    {
                        int add = Math.Min(remaining, cap - _bagSlots[i].Count);
                        _bagSlots[i].Count += add;
                        remaining -= add;
                    }
                }
                for (int i = 0; i < BagSlotCount && remaining > 0; i++)
                {
                    if (_bagSlots[i].IsEmpty)
                    {
                        int add = Math.Min(remaining, cap);
                        _bagSlots[i] = new InventorySlot { ItemId = contents.ItemId, Count = add };
                        remaining -= add;
                    }
                }
            }

            if (remaining <= 0) ClearSlot(slot);
            else SetSlot(slot, new InventorySlot { ItemId = contents.ItemId, Count = remaining });
        }

        /// <summary>Q while hovering an inventory slot: throws one item from that slot.</summary>
        public void DropSlotItem(int slot)
        {
            if (slot < 0 || slot >= BagSlotCount + HotbarSlots) return;
            var contents = GetSlot(slot);
            if (contents.IsEmpty) return;
            SpawnItemDrop(contents.ItemId, 1, new Point3D(LocalPlayer.Position.X, LocalPlayer.Position.Y, LocalPlayer.Position.Z), ThrowVelocity());
            int nc = contents.Count - 1;
            if (nc <= 0) ClearSlot(slot);
            else SetSlot(slot, new InventorySlot { ItemId = contents.ItemId, Count = nc });
        }

        /// <summary>Q: throws one item from the selected hotbar stack into the world.</summary>
        public void DropSelectedHotbarItem()
        {
            int air = BlockRegistry.AirId;
            int itemId = Hotbar[SelectedSlot];
            if (itemId == air || HotbarCounts[SelectedSlot] <= 0) return;
            SpawnItemDrop(itemId, 1, new Point3D(LocalPlayer.Position.X, LocalPlayer.Position.Y, LocalPlayer.Position.Z), ThrowVelocity());
            HotbarCounts[SelectedSlot]--;
            if (HotbarCounts[SelectedSlot] <= 0)
            {
                Hotbar[SelectedSlot] = air;
                if (SelectedBlock == itemId) SelectedBlock = air;
            }
        }

        /// <summary>Spends items from the inventory (e.g. placing a block or eating food). Prefers the
        /// selected hotbar stack, then other hotbar stacks, then bag stacks.</summary>
        public bool TryConsumeFromInventory(int itemId, int count = 1)
        {
            if (itemId <= 0 || count <= 0) return false;
            int remaining = count;
            int air = BlockRegistry.AirId;

            void ConsumeHotbar(int i)
            {
                if (remaining <= 0 || i < 0 || i >= HotbarSlots) return;
                if (Hotbar[i] != itemId || HotbarCounts[i] <= 0) return;
                int take = Math.Min(remaining, HotbarCounts[i]);
                HotbarCounts[i] -= take;
                remaining -= take;
                if (HotbarCounts[i] <= 0)
                {
                    Hotbar[i] = air;
                    if (SelectedBlock == itemId && SelectedSlot == i) SelectedBlock = air;
                }
            }

            ConsumeHotbar(SelectedSlot);
            for (int i = 0; i < HotbarSlots && remaining > 0; i++) ConsumeHotbar(i);
            for (int i = 0; i < BagSlotCount && remaining > 0; i++)
            {
                if (_bagSlots[i].ItemId == itemId)
                {
                    int take = Math.Min(remaining, _bagSlots[i].Count);
                    _bagSlots[i].Count -= take;
                    remaining -= take;
                    if (_bagSlots[i].Count <= 0) _bagSlots[i].Clear();
                }
            }
            return remaining == 0;
        }

        /// <summary>Right-click use of the selected hotbar stack when it's food: consumes one and
        /// restores the item's foodValue in health (max 10 hearts). Returns true if eaten.</summary>
        public bool TryEatSelectedFood()
        {
            int itemId = Hotbar[SelectedSlot];
            if (itemId <= 0 || HotbarCounts[SelectedSlot] <= 0) return false;
            int food = ItemRegistry.FoodValueOf(itemId);
            if (food <= 0) return false;
            if (LocalPlayer.Health >= 10) return false; // full - don't waste it
            if (!TryConsumeFromInventory(itemId, 1)) return false;
            LocalPlayer.Health = Math.Min(10, LocalPlayer.Health + food);
            return true;
        }

        /// <summary>Stub: mining durability for tools. The data model already carries toolType /
        /// toolLevel / durability per item; decrementing durability on block breaks and breaking
        /// the tool at 0 is the next behaviour layer to wire up here.</summary>
        public void DamageSelectedTool(int brokenBlockId)
        {
            // TODO: durability mechanics (needs a durability field on InventorySlot + save support).
        }

        // ---- physics constants (shared with render layer for third-person view) ----
        public const float WalkSpeed = 4.0f;
        public const float NoclipSpeed = 10.8f;
        public const float JumpVelocity = 8.0f;
        public const float Gravity = 28.0f;
        public const float MaxFallSpeed = 36.0f;
        /// <summary>Horizontal acceleration (units/s^2) toward the desired walk speed, so movement
        /// ramps in smoothly instead of snapping to full speed instantly.</summary>
        public const float GroundAcceleration = 35f;
        /// <summary>Deceleration (units/s^2) when no direction is held, so you coast to a stop.</summary>
        public const float GroundFriction = 22f;
        /// <summary>Impact speed (positive downward, units/s) below which a landing is safe.
        /// With gravity 24, this is ~a 3-block fall.</summary>
        public const double FallDamageThreshold = 12.0;
        /// <summary>Hearts lost per unit of impact speed above the threshold.</summary>
        public const double FallDamageScale = 2.5;
        public const double PlayerHeight = 1.8;
        public const double PlayerRadius = 0.30;
        public const double EyeHeight = 1.62;
        public const double CollisionStep = 0.05;
        public const float BlockReach = 6.5f;

        // ---- streaming ----
        private int _lastStreamChunkX = int.MinValue;
        private int _lastStreamChunkZ = int.MinValue;
        private bool _forceChunkStream = true;
        public const int SpawnSyncRadius = 2;
        public int ChunkRenderRadius = 16;

        public const int HotbarSlots = 10;

        public GameWorld(int seed, string name, Func<Renderer.IRenderer?> getRenderer, int chunkRenderRadius, int chunkGenWorkers)
        {
            Seed = seed;
            WorldGenVersion = GenerationVersion; // fresh world: stamped with the current build
            Name = string.IsNullOrWhiteSpace(name) ? "World 1" : name;
            ChunkRenderRadius = chunkRenderRadius;
            _getRenderer = getRenderer ?? throw new ArgumentNullException(nameof(getRenderer));

            RefreshWorkbenchIds();
            ChunkProvider = new World.TerrainChunkProvider(seed);
            SkyChunkProvider = new World.SkyChunkProvider(seed);
            Chunks = new ChunkManager(new World.DeepChunkProvider(seed), ChunkProvider, SkyChunkProvider);
            Entities = new EntityManager(Chunks, (wx, wz) => ChunkProvider.BiomeAt(wx, wz));
            // The local player's body participates in mob separation: the player shoves mobs
            // aside (and mobs give a light shove back). Velocity is added so the push decays via
            // the normal walk friction; flight modes ignore it (noclip overwrites velocity and
            // collision-flight applies its own acceleration/drag).
            Entities.PlayerPushCallback = vel =>
            {
                if (LocalPlayer.Health > 0 && !LocalPlayer.NoclipMode && !LocalPlayer.CreativeFlightMode)
                {
                    LocalPlayer.Velocity = new Point3D(
                        LocalPlayer.Velocity.X + vel.X, LocalPlayer.Velocity.Y,
                        LocalPlayer.Velocity.Z + vel.Z);
                }
            };
            // Hostile mobs (zombies/brutes) damage the player through the same survival damage
            // path as falls: health loss, hurt flash, regen reset, death cause + roll.
            Entities.PlayerDamageCallback = DamagePlayer;
            // Monster spawning gates on darkness; give the spawner the time-of-day so
            // zombies flood caves during the day and the surface at night.
            Entities.SetSkylightSource(() => NightDimLevel(0f));
            // Mesh workers scale with the machine: at least 2, up to ~cores/4. Chunk gen already
            // takes ProcessorCount-2 threads, so meshing gets a share of what's left without
            // starving the render thread on low-end machines.
            int meshWorkers = Math.Clamp(Environment.ProcessorCount / 4, 2, 8);
            _meshQueue = new MeshWorker(Chunks, getRenderer, meshWorkers);
            Mesher = new MeshScheduler(Chunks, _meshQueue);
            // Dirty-list wiring: any chunk whose NeedsRemesh flips true registers itself with the
            // scheduler instead of the scheduler scanning every loaded chunk each update.
            Chunks.ChunkDirty = Mesher.MarkDirtyChunk;
            // Hanging rails: when a chunk streams in with rails in it, seed them for a sag solve
            // (they come back hanging where they were left). Scanning is deferred to the sim thread.
            Chunks.ChunkLoaded = OnChunkLoadedForRailSag;
            BlockTicks = new BlockTickScheduler(Chunks, Mesher);
            _chunkGenWorker = new ChunkGenWorker(Chunks, () => ChunkGenerated?.Invoke(), Math.Max(1, chunkGenWorkers));

            Hotbar = new int[HotbarSlots];
            for (int i = 0; i < HotbarSlots; i++)
            {
                Hotbar[i] = i < BlockRegistry.Hotbar.Count ? BlockRegistry.Hotbar[i] : BlockRegistry.AirId;
            }
            SelectedBlock = Math.Max(0, Hotbar[0]);
        }

        /// <summary>Headless constructor for the dedicated server: no renderer, no mesh uploads.</summary>
        public GameWorld(int seed, string name, int chunkRenderRadius, int chunkGenWorkers)
            : this(seed, name, () => null, chunkRenderRadius, chunkGenWorkers)
        {
            _meshQueue = new NoOpMeshQueue();
            Mesher = new MeshScheduler(Chunks, _meshQueue);
            Chunks.ChunkDirty = Mesher.MarkDirtyChunk;
            Chunks.ChunkLoaded = OnChunkLoadedForRailSag;
            BlockTicks = new BlockTickScheduler(Chunks, Mesher);
        }

        // ---- remote player management (host-side) ----
    }
}
