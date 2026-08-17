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
        private static readonly int _idLog = BlockRegistry.GetId("log");
        private static readonly int _idWorkbench = BlockRegistry.GetId("workbench");
        // Flint is a GENUINE item (items.json), not a block - resolve through the item registry.
        private static readonly int _idFlint = ItemRegistry.GetId("flint");
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
                        d.ItemId, (float)d.Position.X, (float)d.Position.Y, (float)d.Position.Z,
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
        {
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
            return remaining < count;
        }

        /// <summary>Collects an item, preferring the first hotbar slot that already holds that item,
        /// then the first empty hotbar slot; otherwise it goes into the bag. If the player has
        /// nothing selected, picking up fills the selected slot so it is usable right away.</summary>
        public bool CollectItem(int itemId, int count = 1)
        {
            if (itemId <= 0 || count <= 0) return false;
            int air = BlockRegistry.AirId;

            for (int i = 0; i < HotbarSlots; i++)
            {
                if (Hotbar[i] == itemId)
                {
                    TryAddToInventory(itemId, count);
                    return true;
                }
            }
            for (int i = 0; i < HotbarSlots; i++)
            {
                if (Hotbar[i] == air)
                {
                    Hotbar[i] = itemId;
                    TryAddToInventory(itemId, count);
                    if (SelectedBlock <= 0)
                    {
                        SelectedBlock = itemId;
                        SelectedSlot = i;
                    }
                    return true;
                }
            }
            TryAddToInventory(itemId, count);
            return true;
        }

        // ---- inventory cursor (ported drag logic from the C++ E-menu) ----
        /// <summary>The stack riding the mouse cursor while the inventory is open.</summary>
        public (int ItemId, int Count)? HeldStack { get; set; }

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
                    if (clicked.IsEmpty)
                    {
                        SetSlot(slot, new InventorySlot { ItemId = heldId, Count = heldCount });
                        HeldStack = null;
                    }
                    else if (clicked.ItemId == heldId && clicked.Count < MaxStackSize)
                    {
                        int add = Math.Min(heldCount, MaxStackSize - clicked.Count);
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
                    bool canPlace = clicked.IsEmpty
                        || (clicked.ItemId == heldId && clicked.Count < MaxStackSize);
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
            var throwVel = ThrowVelocity();
            for (int i = 0; i < drop; i++)
            {
                SpawnItemDrop(held.Value.ItemId, 1,
                    new Point3D(LocalPlayer.Position.X, LocalPlayer.Position.Y, LocalPlayer.Position.Z), throwVel);
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

            if (slot < BagSlotCount)
            {
                // Bag -> hotbar.
                for (int i = 0; i < HotbarSlots && remaining > 0; i++)
                {
                    if (Hotbar[i] == contents.ItemId && HotbarCounts[i] < MaxStackSize)
                    {
                        int add = Math.Min(remaining, MaxStackSize - HotbarCounts[i]);
                        HotbarCounts[i] += add;
                        remaining -= add;
                    }
                }
                for (int i = 0; i < HotbarSlots && remaining > 0; i++)
                {
                    if (Hotbar[i] == air)
                    {
                        int add = Math.Min(remaining, MaxStackSize);
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
                    if (_bagSlots[i].ItemId == contents.ItemId && _bagSlots[i].Count < MaxStackSize)
                    {
                        int add = Math.Min(remaining, MaxStackSize - _bagSlots[i].Count);
                        _bagSlots[i].Count += add;
                        remaining -= add;
                    }
                }
                for (int i = 0; i < BagSlotCount && remaining > 0; i++)
                {
                    if (_bagSlots[i].IsEmpty)
                    {
                        int add = Math.Min(remaining, MaxStackSize);
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
        public const float WalkSpeed = 4.317f;
        public const float FlySpeed = 10.8f;
        public const float JumpVelocity = 8.0f;
        public const float Gravity = 24.0f;
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
            Name = string.IsNullOrWhiteSpace(name) ? "World 1" : name;
            ChunkRenderRadius = chunkRenderRadius;
            _getRenderer = getRenderer ?? throw new ArgumentNullException(nameof(getRenderer));

            ChunkProvider = new World.TerrainChunkProvider(seed);
            SkyChunkProvider = new World.SkyChunkProvider(seed);
            Chunks = new ChunkManager(new World.DeepChunkProvider(seed), ChunkProvider, SkyChunkProvider);
            Entities = new EntityManager(Chunks);
            // The local player's body participates in mob separation: the player shoves mobs
            // aside (and mobs give a light shove back). Velocity is added so the push decays via
            // the normal walk friction; fly mode ignores it (velocity is overwritten each frame).
            Entities.PlayerPushCallback = vel =>
            {
                if (LocalPlayer.Health > 0 && !LocalPlayer.FlyMode)
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
            BlockTicks = new BlockTickScheduler(Chunks, Mesher);
        }

        // ---- remote player management (host-side) ----
    }
}