using System;
using System.Collections.Generic;
using System.Numerics;

namespace CubeApp
{
    /// <summary>
    /// The authoritative simulation world. Everything the game simulates lives here and runs
    /// without any renderer, window or input device: chunk streaming, terrain generation, fluid
    /// ticking, entities, player physics and block editing, deep-fill, and saves. Rendering
    /// (Program) is a pure presentation layer over GameWorld; multiplayer networking (Phase 3+)
    /// hosts a GameWorld and broadcasts its state to clients.
    ///
    /// All player movement runs through <see cref="StepPlayer"/> which is written against
    /// <see cref="PlayerState"/>, so the exact same physics code drives the local player, remote
    /// players simulated by the host, and (optionally) client-side prediction.
    ///
    /// Block edits raise <see cref="BlockEdited"/> so listeners (renderer particles, network
    /// broadcaster) can react without the sim knowing anything about them.
    /// </summary>
    public sealed class GameWorld : IDisposable
    {
        // ---- world identity / services ----
        public int Seed { get; private set; }
        public string Name { get; private set; } = "World 1";
        public ChunkManager Chunks { get; private set; }
        public World.TerrainChunkProvider ChunkProvider { get; private set; }
        public World.SkyChunkProvider SkyChunkProvider { get; private set; }
        public EntityManager Entities { get; private set; }
        public float LastEntityMs { get; private set; }
        public MeshScheduler Mesher { get; private set; }
        public BlockTickScheduler BlockTicks { get; private set; }
        private ChunkGenWorker _chunkGenWorker;
        private IMeshQueue _meshQueue;
        private readonly Func<Renderer.IRenderer?> _getRenderer;

        // ---- events (networking / render hooks) ----
        /// <summary>Raised after ANY block edit is applied (local or remote). Args: x, y, z, blockId, meta.</summary>
        public event Action<int, int, int, int, int>? BlockEdited;
        /// <summary>Raised when chunk generation completes (renderer wants to re-upload).</summary>
        public event Action? ChunkGenerated;
        /// <summary>Raised after a chunk is unloaded (renderer wants to free GPU buffers).</summary>
        public event Action<ChunkCoordinates>? ChunkUnloaded;

        // ---- local player state ----
        public PlayerState LocalPlayer = new();

        /// <summary>
        /// The world's default spawn point (player EYE position, matching LocalPlayer.Position).
        /// Chosen once when a world starts (random safe spot near the origin on grass/sand); the
        /// camera returns here on respawn.
        /// </summary>
        public Point3D? SpawnPoint;

        // Convenience forwards for the presentation layer (Program.cs) so the local player's
        // common fields read/write exactly like the old standalone fields.
        public Point3D PlayerPosition { get => LocalPlayer.Position; set => LocalPlayer.Position = value; }
        public float PlayerYaw { get => LocalPlayer.Yaw; set => LocalPlayer.Yaw = value; }
        public float PlayerPitch { get => LocalPlayer.Pitch; set => LocalPlayer.Pitch = value; }
        public Point3D PlayerVelocity { get => LocalPlayer.Velocity; set => LocalPlayer.Velocity = value; }
        public bool PlayerGrounded { get => LocalPlayer.Grounded; set => LocalPlayer.Grounded = value; }
        public bool FlyMode { get => LocalPlayer.FlyMode; set => LocalPlayer.FlyMode = value; }
        public float PlayerWalkPhase { get => LocalPlayer.WalkPhase; set => LocalPlayer.WalkPhase = value; }
        public float PlayerWalkAmount { get => LocalPlayer.WalkAmount; set => LocalPlayer.WalkAmount = value; }

        // ---- remote player states (host-simulated clients, keyed by client id) ----
        private readonly Dictionary<int, PlayerState> _remotePlayers = new();
        private readonly object _remoteLock = new();
        public IReadOnlyCollection<PlayerState> RemotePlayers => _remotePlayers.Values;

        // ---- hotbar (local UI state; not simulated) ----
        public int SelectedSlot;
        public int SelectedBlock;
        public int[] Hotbar;

        // ---- game mode ----
        /// <summary>Creative (sandbox) or Survival (resource loop). Creative players fly, can't
        /// die, and place blocks freely; survival players must mine to collect before placing.</summary>
        public GameMode Mode { get; set; } = GameMode.Creative;
        public bool IsCreative => Mode == GameMode.Creative;

        // ---- survival inventory (slot-based, ported from Cubuild C++) ----
        public const int MaxStackSize = 64;
        private const int BagSlotCount = 40;   // 4 rows x 10, matching the C++ E-menu layout
        private readonly InventorySlot[] _bagSlots = new InventorySlot[BagSlotCount];
        /// <summary>Bag slots (index 0..39) for the E-menu grid.</summary>
        public IReadOnlyList<InventorySlot> BagSlots => _bagSlots;
        /// <summary>Per-hotbar-slot counts (parallel to <see cref="Hotbar"/> item ids).</summary>
        public int[] HotbarCounts = new int[HotbarSlots];
        /// <summary>Total count of an item across the bag and hotbar.</summary>
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
        /// <summary>Leaves drop nothing for now (sapling drops aren't implemented yet).</summary>
        private static readonly int _idLeaves = BlockRegistry.GetId("leaves");
        /// <summary>Gravel drops flint instead of more gravel (test item drop).</summary>
        private static readonly int _idGravel = BlockRegistry.GetId("gravel");
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
        public PlayerState AddRemotePlayer(int clientId)
        {
            lock (_remoteLock)
            {
                var state = new PlayerState();
                _remotePlayers[clientId] = state;
                return state;
            }
        }

        public bool TryGetRemotePlayer(int clientId, out PlayerState state)
        {
            lock (_remoteLock) return _remotePlayers.TryGetValue(clientId, out state!);
        }

        public void RemoveRemotePlayer(int clientId)
        {
            lock (_remoteLock) _remotePlayers.Remove(clientId);
        }

        // ------------------------------------------------------------------
        // lifecycle
        // ------------------------------------------------------------------

        public void EnsureVisibleChunks() => Chunks.EnsureChunksAround(
            WorldToChunkCoord(LocalPlayer.Position.X), WorldToChunkCoord(LocalPlayer.Position.Z), SpawnSyncRadius);

        public void PlaceCameraAtSafeSpawn()
        {
            if (SpawnPoint.HasValue)
            {
                LocalPlayer.Position = SpawnPoint.Value;
            }
            else
            {
                var spawn = FindSafeSpawnPosition();
                LocalPlayer.Position = spawn ?? new Point3D(0.5, 1.0 + EyeHeight, 0.5);
            }
            LocalPlayer.Velocity = new Point3D(0, 0, 0);
            LocalPlayer.Grounded = true;
        }

        /// <summary>
        /// Picks the world's default spawn point: a RANDOM spot within a ring near the world origin
        /// whose surface block is grass, grass_spreading, or sand, above sea level, with clear air
        /// for the player to stand in. Called once before the player enters the world; every respawn
        /// returns here. SpawnPoint stores the EYE position (matching LocalPlayer.Position).
        /// </summary>
        public bool SelectWorldSpawn()
        {
            int grassId = BlockRegistry.GetId("grass");
            int grassSpreadId = BlockRegistry.GetId("grass_spreading");
            int sandId = BlockRegistry.GetId("sand");
            var rand = new Random();

            // Try random spots in expanding-ish rings out to ~400 blocks from the origin - the
            // origin itself is often ocean, so land can be farther away. The cheap surface
            // estimator rejects ocean columns BEFORE generating any chunk, so the wide scan stays
            // fast.
            for (int attempt = 0; attempt < 2048; attempt++)
            {
                int range = 4 + rand.Next(396); // 4..399 blocks from origin
                double ang = rand.NextDouble() * Math.PI * 2.0;
                int wx = (int)Math.Round(Math.Cos(ang) * range);
                int wz = (int)Math.Round(Math.Sin(ang) * range);

                // Cheap reject: below/at sea level = ocean floor, never spawn there.
                if (ChunkProvider != null && ChunkProvider.EstimateSurfaceHeightAt(wx, wz) < 1) continue;

                // Make sure the chunk exists so the surface scan sees real terrain.
                Chunks.GetOrCreateChunk(WorldToChunkCoord(wx), WorldToChunkCoord(wz));
                int surfaceY = FindSurfaceWorldY(wx, wz);
                if (surfaceY < 0) continue; // below sea / no ground

                int surfaceBlock = Chunks.GetBlockAt(wx, surfaceY, wz);
                if (surfaceBlock != grassId && surfaceBlock != grassSpreadId && surfaceBlock != sandId) continue;

                // Must be open air above (feet just above the surface block, head above that), no ceiling.
                if (Chunks.GetBlockAt(wx, surfaceY + 1, wz) != BlockRegistry.AirId) continue;
                if (Chunks.GetBlockAt(wx, surfaceY + 2, wz) != BlockRegistry.AirId) continue;

                // Player AABB must be collision-free standing here. Eye = just above the block top +
                // eye height (matching FindSafeSpawnPosition's convention).
                double px = wx + 0.5, pz = wz + 0.5;
                var eye = new Point3D(px, surfaceY + 0.01 + EyeHeight, pz);
                if (IsPlayerColliding(eye)) continue;

                SpawnPoint = eye;
                return true;
            }

            // Fallback: no grass/sand found in the wide ring - use the old height-hunting search.
            var fallback = FindSafeSpawnPosition();
            if (fallback.HasValue)
            {
                SpawnPoint = fallback.Value;
                return true;
            }

            SpawnPoint = new Point3D(0.5, 1.0 + EyeHeight, 0.5);
            return true;
        }

        /// <summary>
        /// Teleports the local player to the nearest location of the given biome (from the biome
        /// teleport menu). Searches outward in expanding rings around the player's current chunk;
        /// for the first chunk whose biome label matches, it finds a safe surface spot and moves
        /// the camera there.
        /// </summary>
        public void TeleportToNearestBiome(string biomeName)
        {
            if (ChunkProvider == null) return;

            int playerChunkX = WorldToChunkCoord(LocalPlayer.Position.X);
            int playerChunkZ = WorldToChunkCoord(LocalPlayer.Position.Z);

            for (int radius = 0; radius <= 64; radius++)
            {
                // Walk the ring at this radius (square ring; inside the ring was checked at a
                // smaller radius already, so scanning the whole square would re-check a lot).
                for (int dx = -radius; dx <= radius; dx++)
                {
                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        if (radius > 0 && Math.Abs(dx) != radius && Math.Abs(dz) != radius) continue;

                        int wx = (playerChunkX + dx) * ChunkManager.ChunkSize;
                        int wz = (playerChunkZ + dz) * ChunkManager.ChunkSize;
                        string biome = ChunkProvider.BiomeNameAt(wx, wz);
                        if (!string.Equals(biome, biomeName, StringComparison.OrdinalIgnoreCase)) continue;

                        // Found a matching chunk - try to land safely somewhere in it.
                        if (TryFindSafeSpotInChunk(wx, wz, out var eye))
                        {
                            LocalPlayer.Position = eye;
                            LocalPlayer.Velocity = new Point3D(0, 0, 0);
                            LocalPlayer.Grounded = true;
                            return;
                        }
                    }
                }
            }
        }

        // Teleports the local player to the Great Pyramid if this world has one (most don't),
        // otherwise to the first regular pyramid. Pyramids are SOLID brick, so we can't drop them
        // on the center column - land just outside the base instead so the monument is in view.
        public void TeleportToPyramid()
        {
            if (ChunkProvider == null) return;

            var great = ChunkProvider.Pyramids;
            if (great != null && great.Exists)
            {
                TeleportNear(great.Center.X + great.HalfWidth + 60, great.Center.Z);
                return;
            }

            var regulars = ChunkProvider.RegularPyramids?.Pyramids;
            if (regulars != null && regulars.Count > 0)
            {
                var p = regulars[0];
                TeleportNear(p.CenterX + p.HalfWidth + 40, p.CenterZ);
            }
        }

        private void TeleportNear(int worldX, int worldZ)
        {
            if (TryFindSafeSpotInChunk(worldX, worldZ, out var eye))
            {
                LocalPlayer.Position = eye;
                LocalPlayer.Velocity = new Point3D(0, 0, 0);
                LocalPlayer.Grounded = true;
            }
        }

        // Finds a safe landing spot in a biome chunk using the terrain generator's cheap surface
        // estimate (NO full chunk generation - that would stall the main thread for a far-away
        // biome). Returns the eye position. The player may fall a block or two after landing as the
        // estimate is slightly imprecise, which is fine.
        private bool TryFindSafeSpotInChunk(int chunkWorldX, int chunkWorldZ, out Point3D eye)
        {
            eye = default;

            // Ocean basins are underwater, so land the player at the WATER SURFACE (sea level)
            // instead of the basin floor - they shouldn't teleport to the bottom of the sea.
            if (string.Equals(ChunkProvider.BiomeNameAt(chunkWorldX, chunkWorldZ), "Ocean", StringComparison.OrdinalIgnoreCase))
            {
                double px = chunkWorldX + 0.5;
                double pz = chunkWorldZ + 0.5;
                // Sea level is at local Y 64 of the terrain band, which maps to world 0.
                double feetY = 0.0 + 0.01;
                eye = new Point3D(px, feetY + EyeHeight, pz);
                return true;
            }

            int surfaceY = ChunkProvider.EstimateSurfaceHeightAt(chunkWorldX, chunkWorldZ);
            if (surfaceY < ChunkManager.WorldOriginY) return false;

            double px2 = chunkWorldX + 0.5;
            double pz2 = chunkWorldZ + 0.5;
            // Feet just above the surface; eye = feet + EyeHeight.
            double feetY2 = surfaceY + 0.01;
            eye = new Point3D(px2, feetY2 + EyeHeight, pz2);
            return true;
        }

        public void SetSelectedSlot(int slot)
        {
            if (slot < 0 || slot >= HotbarSlots) return;
            SelectedSlot = slot;
            SelectedBlock = Hotbar[slot];
        }

        public void ApplyLookInput(Vector2 lookDelta) => ApplyLookInput(LocalPlayer, lookDelta);

        public void ApplyLookInput(PlayerState p, Vector2 lookDelta)
        {
            p.Yaw -= lookDelta.X;
            p.Yaw = NormalizeYaw(p.Yaw);
            p.Pitch = Math.Clamp(p.Pitch - lookDelta.Y, -89f, 89f);
        }

        /// <summary>
        /// Applies damage to the local player (mob hits, test key, falls...). Clamps at 0, resets
        /// the regen timer so healing has to wait the full delay again, and records the death cause
        /// when the hit drops health to 0.
        /// </summary>
        public void DamagePlayer(int amount, DeathCause cause = DeathCause.Unknown)
        {
            // Creative players are invulnerable: no mob hits, no falls, no damage.
            if (IsCreative) return;
            if (amount <= 0) return;
            LocalPlayer.Health = Math.Max(0, LocalPlayer.Health - amount);
            LocalPlayer.TimeSinceDamage = 0f;
            LocalPlayer.RegenAccumulator = 0f;
            // Hurt flash + flail on the third-person model (same 0.2s as the duck).
            LocalPlayer.HurtTimer = Math.Max(LocalPlayer.HurtTimer, 0.2f);
            if (LocalPlayer.Health <= 0)
            {
                LocalPlayer.DeathCause = cause;
                // Start the death roll (direction random, like mobs that die without an attacker).
                if (LocalPlayer.DeathTimer <= 0f)
                    LocalPlayer.DeathRollDir = _regenRandom.Next(2) == 0 ? -1f : 1f;
                LocalPlayer.DeathTimer += 1f / 60f;
            }
        }

        // Natural regeneration: after RegenDelay seconds without damage the player heals one heart
        // slice every RegenIntervalBase seconds, plus a random 1..2s fluctuation per slice.
        private const float RegenDelay = 15f;
        private const float RegenIntervalBase = 8.5f;
        private readonly Random _regenRandom = new();

        // Steps dropped items: gravity, ground settle, player pickup, and despawn. Purely
        // survival-facing - creative breaks don't even spawn drops.
        private void StepItemDrops(float dt)
        {
            for (int i = _droppedItems.Count - 1; i >= 0; i--)
            {
                var d = _droppedItems[i];
                d.Age += dt;
                if (d.Age > ItemDropDespawnTime)
                {
                    _droppedItems.RemoveAt(i);
                    continue;
                }

                // Magnet phase: flying toward the player. Ignores gravity, homes in fast, and
                // gets collected on arrival - the classic "item flies to you" pickup.
                if (d.FlyTime > 0f)
                {
                    d.FlyTime -= dt;
                    double feetX = LocalPlayer.Position.X;
                    double feetY = LocalPlayer.Position.Y - EyeHeight + 0.4;
                    double feetZ = LocalPlayer.Position.Z;
                    double hx = feetX - d.Position.X;
                    double hy = feetY - d.Position.Y;
                    double hz = feetZ - d.Position.Z;
                    double dist = Math.Sqrt(hx * hx + hy * hy + hz * hz);
                    if (dist <= 0.25 || d.FlyTime <= 0f)
                    {
                        CollectItem(d.ItemId, d.Count);
                        _droppedItems.RemoveAt(i);
                        continue;
                    }
                    double speed = Math.Min(18.0, 7.0 + (PickupFlyDuration - d.FlyTime) * 40.0);
                    d.Velocity = new Point3D(hx / dist * speed, hy / dist * speed, hz / dist * speed);
                    d.Position += d.Velocity * dt;
                    d.SpinSpeed = Math.Max(d.SpinSpeed, 14f); // spin up while flying to you
                    continue;
                }

                // Pickup trigger: within reach of the player, after the grace period. Instead of
                // vanishing instantly, the drop starts flying to the player.
                if (d.Age > ItemDropPickupDelay && !IsCreative)
                {
                    double feetX = LocalPlayer.Position.X;
                    double feetY = LocalPlayer.Position.Y - EyeHeight;
                    double feetZ = LocalPlayer.Position.Z;
                    double centerY = d.Position.Y + 0.2;
                    if (Math.Abs(d.Position.X - feetX) < 1.2
                        && Math.Abs(d.Position.Z - feetZ) < 1.2
                        && centerY > feetY - 0.5 && centerY < feetY + 2.0)
                    {
                        d.FlyTime = PickupFlyDuration;
                        continue;
                    }
                }

                // Gravity + horizontal drag.
                d.Velocity = new Point3D(
                    d.Velocity.X * (float)Math.Pow(0.5, dt * 4.0),
                    d.Velocity.Y - Gravity * dt,
                    d.Velocity.Z * (float)Math.Pow(0.5, dt * 4.0));
                d.Position += d.Velocity * dt;

                // Tumble while airborne: rotate the quaternion around the spin axis, with a
                // little drag so the spin dies down naturally.
                if (d.SpinSpeed > 0.01f)
                {
                    float angStep = d.SpinSpeed * dt;
                    float c = (float)Math.Cos(angStep * 0.5);
                    float s = (float)Math.Sin(angStep * 0.5);
                    float qx = d.SpinAxisX * s, qy = d.SpinAxisY * s, qz = d.SpinAxisZ * s, qw = c;
                    float nx = qw * d.RotX + qx * d.RotW + qy * d.RotZ - qz * d.RotY;
                    float ny = qw * d.RotY - qx * d.RotZ + qy * d.RotW + qz * d.RotX;
                    float nz = qw * d.RotZ + qx * d.RotY - qy * d.RotX + qz * d.RotW;
                    float nw = qw * d.RotW - qx * d.RotX - qy * d.RotY - qz * d.RotZ;
                    d.RotX = nx; d.RotY = ny; d.RotZ = nz; d.RotW = nw;
                    d.SpinSpeed *= (float)Math.Pow(0.5, dt * 2.0);
                }

                // Settle on the first solid block below.
                int bx = (int)Math.Floor(d.Position.X);
                int by = (int)Math.Floor(d.Position.Y);
                int bz = (int)Math.Floor(d.Position.Z);
                if (Chunks.TryGetLoadedBlock(bx, by, bz, out int groundId) && BlockRegistry.IsSolid(groundId))
                {
                    d.Position = new Point3D(d.Position.X, by + 1.0, d.Position.Z);
                    d.Velocity = new Point3D(d.Velocity.X, 0, d.Velocity.Z);
                    d.SpinSpeed = 0f; // it lands and stops tumbling
                }
                else if (d.Position.Y < ChunkManager.GroundOriginY - 10)
                {
                    _droppedItems.RemoveAt(i); // fell out of the world
                }
            }
        }

        private void StepRegen(float dt)
        {
            var p = LocalPlayer;
            if (p.Health <= 0) return; // dead players don't heal
            if (p.Health >= 10)
            {
                p.TimeSinceDamage = 0f;
                p.RegenAccumulator = 0f;
                return;
            }

            p.TimeSinceDamage += dt;
            if (p.TimeSinceDamage < RegenDelay) return;

            p.RegenAccumulator += dt;
            if (p.RegenAccumulator >= p.NextRegenInterval)
            {
                p.RegenAccumulator = 0f;
                p.Health = Math.Min(10, p.Health + 1);
                // Random fluctuation of 1..2 seconds per slice.
                p.NextRegenInterval = RegenIntervalBase + 1f + (float)_regenRandom.NextDouble();
            }
        }

        /// <summary>Advance the simulation by one frame. Pure logic; no rendering here.</summary>
        public void StepSimulation(TickInputState tickInput, float deltaSeconds)
        {
            // Day/night clock: worldTime advances at a fixed 20 ticks/sec (worldTime
            // advances once per tick, full cycle = 24000 ticks = 20 minutes). Accumulate the
            // fractional delta so time flows at exactly 20 tps regardless of frame rate.
            // Math.Round(deltaSeconds*20) froze the clock at high FPS (0.333 rounds to 0 every
            // frame), so the sun/moon/stars never rotated.
            _worldTimeAccumulator += deltaSeconds * 20.0;
            long advance = (long)_worldTimeAccumulator;
            WorldTime += advance;
            _worldTimeAccumulator -= advance;
            StepRegen(deltaSeconds);
            if (LocalPlayer.HurtTimer > 0f)
                LocalPlayer.HurtTimer = Math.Max(0f, LocalPlayer.HurtTimer - deltaSeconds);
            // Advance the death roll while dead (capped so it doesn't spin forever).
            if (LocalPlayer.Health <= 0)
                LocalPlayer.DeathTimer = Math.Min(1f, LocalPlayer.DeathTimer + deltaSeconds);
            StepItemDrops(deltaSeconds);
            BlockTicks?.Tick(deltaSeconds);
            StepPlayer(LocalPlayer, tickInput, deltaSeconds);
            // Third-person body yaw: the body lags the look direction (slowly while idle, faster
            // while moving/flying) so the head can swivel ahead of the body like a real person.
            if (LocalPlayer.Health > 0)
            {
                float camYaw = LocalPlayer.Yaw * (float)Math.PI / 180f;
                float bodyYaw = LocalPlayer.BodyYaw;
                float delta = NormalizeRadians(camYaw - bodyYaw);
                float turnRate = (LocalPlayer.WalkAmount > 0.05f || LocalPlayer.FlyMode) ? 9f : 3f;
                float maxStep = turnRate * deltaSeconds;
                LocalPlayer.BodyYaw = Math.Abs(delta) <= maxStep ? camYaw : bodyYaw + Math.Sign(delta) * maxStep;
            }
            // Player body center for mob separation: AABB runs from eye - EyeHeight up to
            // + PlayerHeight, so the center sits at eye - EyeHeight + half height.
            Entities.PlayerBodyCenter = new Point3D(
                LocalPlayer.Position.X,
                LocalPlayer.Position.Y - EyeHeight + PlayerHeight * 0.5,
                LocalPlayer.Position.Z);
            Entities.Update(deltaSeconds, LocalPlayer.Position, true, LocalPlayer.Health > 0);
            LastEntityMs = Entities.LastUpdateMs;
            int chunkX = WorldToChunkCoord(LocalPlayer.Position.X);
            int chunkZ = WorldToChunkCoord(LocalPlayer.Position.Z);
            // Request/unload scans cost O(radius^2) + O(loadedChunks); only run them when the
            // player actually enters a new chunk column, the render distance changed, OR the
            // player crosses into a different layer (digging straight down in one column
            // keeps X/Z constant but must still wake the new layer).
            double py = LocalPlayer.Position.Y;
            int playerLayer = ChunkManager.LayerForWorldY((int)py);
            bool crossedLayer = playerLayer != _lastPlayerLayer;
            if (_forceChunkStream || chunkX != _lastStreamChunkX || chunkZ != _lastStreamChunkZ || crossedLayer)
            {
                _forceChunkStream = false;
                _lastStreamChunkX = chunkX;
                _lastStreamChunkZ = chunkZ;
                _lastPlayerLayer = playerLayer;
                // Only stream the chunk layer the player is standing in — deep, ground, or sky.
                // The other two layers sit idle until the player crosses into them, saving CPU
                // and keeping generation focused on the one layer that matters right now.
                Chunks.RequestChunksAround(chunkX, chunkZ, ChunkRenderRadius, LocalPlayer.Position, playerLayer);
                var unloaded = Chunks.UnloadChunksOutside(chunkX, chunkZ, ChunkRenderRadius);
                foreach (var uc in unloaded) ChunkUnloaded?.Invoke(uc);
            }
            UpdateHighFill();
        }

        private int _lastPlayerLayer = ChunkManager.GroundLayer;

        /// <summary>Day/night clock in world ticks. Full cycle = 24000 ticks.</summary>
        public long WorldTime { get; private set; }

        /// <summary>Force-advance the day/night clock by 25% of its 24000-tick cycle (T key).</summary>
        public void AdvanceTime()
        {
            WorldTime += 6000;
            _worldTimeAccumulator = 0.0;
        }

        /// <summary>Fractional leftover for the 20 tps day/night clock.</summary>
        private double _worldTimeAccumulator;

        /// <summary>
        /// Sun position 0..1 across the day (0.25 = dawn, 0.75 = dusk). 0..1 where 0 = midnight-ish
        /// start of the cycle; eased so the sun lingers near the horizon rather than snapping.
        /// </summary>
        public float SunPosition(float partialTick)
        {
            long t = WorldTime % 24000;
            float ang = (float)(t + partialTick) / 24000.0f - 0.25f;
            if (ang < 0f) ang += 1f;
            if (ang > 1f) ang -= 1f;
            float raw = ang;
            ang = 1f - (float)((Math.Cos(ang * Math.PI) + 1.0) / 2.0);
            ang = raw + (ang - raw) / 3f;
            return ang;
        }

        /// <summary>
        /// Night dim level: 0 (noon) .. 11 (midnight) — how much daylight is removed from the sky
        /// light after the sun goes down.
        /// </summary>
        public int NightDimLevel(float partialTick)
        {
            float celestial = SunPosition(partialTick);
            float v = 1f - (float)(Math.Cos(celestial * Math.PI * 2.0) * 2.0 + 0.5);
            if (v < 0f) v = 0f;
            if (v > 1f) v = 1f;
            return (int)(v * 11f);
        }

        /// <summary>Sky brightness factor (1.0 at noon, near 0 at midnight).</summary>
        public float SkyBrightness(float partialTick)
        {
            float celestial = SunPosition(partialTick);
            float v = (float)(Math.Cos(celestial * Math.PI * 2.0) * 2.0 + 0.5);
            if (v < 0f) v = 0f;
            if (v > 1f) v = 1f;
            return v;
        }

        /// <summary>Simulates remote players without touching the local player's camera/chunks.
        /// The host calls this each frame with each client's received input.</summary>
        public void StepRemotePlayers(float deltaSeconds)
        {
            PlayerState[] states;
            lock (_remoteLock)
            {
                states = new PlayerState[_remotePlayers.Count];
                int i = 0;
                foreach (var s in _remotePlayers.Values) states[i++] = s;
            }
            foreach (var s in states)
            {
                // Remote players use the same physics; their input is applied by the network
                // layer via ApplyRemoteInput (latest received TickInputState).
                StepPlayer(s, s.PendingInput, deltaSeconds);
            }
        }

        // ------------------------------------------------------------------
        // player movement (generic on PlayerState; local + remote share this)
        // ------------------------------------------------------------------

        public void StepPlayer(PlayerState p, TickInputState tickInput, float deltaSeconds)
        {
            // A dead player is a corpse: no walking, flying, jumping, or input-driven motion.
            if (p.Health <= 0)
            {
                p.Velocity = new Point3D(0, 0, 0);
                p.WalkAmount = 0f;
                p.Grounded = false;
                return;
            }

            if (p.FlyMode)
            {
                var flyForward = GetCameraForward(p);
                var flyRight = GetCameraRight(p.Yaw);
                var flyDir = new Point3D(0, 0, 0);
                if (tickInput.MoveForward) flyDir += flyForward;
                if (tickInput.MoveBackward) flyDir -= flyForward;
                if (tickInput.MoveLeft) flyDir += flyRight;
                if (tickInput.MoveRight) flyDir -= flyRight;
                if (tickInput.MoveUp) flyDir += new Point3D(0, 1, 0);
                if (tickInput.MoveDown) flyDir += new Point3D(0, -1, 0);
                if (flyDir.X != 0 || flyDir.Y != 0 || flyDir.Z != 0)
                {
                    double len = Math.Sqrt(flyDir.X * flyDir.X + flyDir.Y * flyDir.Y + flyDir.Z * flyDir.Z);
                    flyDir *= 1.0 / len;
                }
                p.Velocity = flyDir * FlySpeed;
                p.Position = new Point3D(
                    p.Position.X + p.Velocity.X * deltaSeconds,
                    p.Position.Y + p.Velocity.Y * deltaSeconds,
                    p.Position.Z + p.Velocity.Z * deltaSeconds);
                p.Grounded = false;
                p.WalkAmount = 0f;
                return;
            }

            var forwardWalk = GetCameraForward(p);
            var forwardHorizontal = new Point3D(forwardWalk.X, 0, forwardWalk.Z).Normalized();
            var right = GetCameraRight(p.Yaw);
            var desiredDirection = new Point3D(0, 0, 0);
            if (tickInput.MoveForward) desiredDirection += forwardHorizontal;
            if (tickInput.MoveBackward) desiredDirection -= forwardHorizontal;
            if (tickInput.MoveLeft) desiredDirection += right;
            if (tickInput.MoveRight) desiredDirection -= right;
            if (desiredDirection.X != 0 || desiredDirection.Z != 0)
            {
                var length = Math.Sqrt(desiredDirection.X * desiredDirection.X + desiredDirection.Z * desiredDirection.Z);
                desiredDirection *= 1.0 / length;
            }

            bool feetInWater = PlayerSampleInWater(p, 0.05);
            bool bodyInWater = PlayerSampleInWater(p, PlayerHeight * 0.4);
            bool headInWater = PlayerSampleInWater(p, PlayerHeight * 0.85);
            bool inWater = feetInWater || bodyInWater || headInWater;
            if (inWater)
            {
                double submerged = (feetInWater ? 0.25 : 0) + (bodyInWater ? 0.5 : 0) + (headInWater ? 0.25 : 0);
                var swimSpeed = desiredDirection * (WalkSpeed * 0.42);
                p.Velocity = new Point3D(swimSpeed.X, p.Velocity.Y, swimSpeed.Z);
                p.Velocity = new Point3D(
                    p.Velocity.X,
                    p.Velocity.Y * Math.Pow(0.96, deltaSeconds * 60.0),
                    p.Velocity.Z);
                double waterGravity = Gravity * Math.Max(0.16, 0.42 - submerged * 0.20);
                p.Velocity = new Point3D(
                    p.Velocity.X,
                    p.Velocity.Y - waterGravity * deltaSeconds,
                    p.Velocity.Z);
                if (tickInput.MoveUp)
                {
                    double swimLift = bodyInWater ? 0.58 : 0.7;
                    p.Velocity = new Point3D(
                        p.Velocity.X,
                        Math.Max(p.Velocity.Y, JumpVelocity * swimLift),
                        p.Velocity.Z);
                }
                var swimDisplacement = p.Velocity * deltaSeconds;
                MovePlayerWithCollisions(p, swimDisplacement);
                double swimHSpeed = Math.Sqrt(p.Velocity.X * p.Velocity.X + p.Velocity.Z * p.Velocity.Z);
                p.WalkAmount = (float)Math.Min(1.0, swimHSpeed / WalkSpeed);
                p.WalkPhase += deltaSeconds * p.WalkAmount * 10f;
                return;
            }

            // Natural horizontal movement: accelerate toward the desired direction/speed instead
            // of snapping, and coast to a stop with friction when nothing is held. Walking
            // backward is a touch slower, which reads as more human.
            var horizontalVelocity = new Point3D(p.Velocity.X, 0, p.Velocity.Z);
            float speed = WalkSpeed;
            if (tickInput.MoveBackward) speed *= 0.72f;
            var targetH = desiredDirection * speed;
            bool moving = tickInput.MoveForward || tickInput.MoveBackward || tickInput.MoveLeft || tickInput.MoveRight;
            double accel = moving ? GroundAcceleration : GroundFriction;
            double dx = targetH.X - horizontalVelocity.X;
            double dz = targetH.Z - horizontalVelocity.Z;
            double dist = Math.Sqrt(dx * dx + dz * dz);
            double maxStep = accel * deltaSeconds;
            if (dist <= maxStep)
            {
                horizontalVelocity = new Point3D(targetH.X, 0, targetH.Z);
            }
            else
            {
                double f = maxStep / dist;
                horizontalVelocity = new Point3D(horizontalVelocity.X + dx * f, 0, horizontalVelocity.Z + dz * f);
            }

            var verticalVelocity = p.Velocity.Y;
            if (tickInput.JumpPressed && p.Grounded)
            {
                verticalVelocity = JumpVelocity;
                p.Grounded = false;
            }
            verticalVelocity -= Gravity * deltaSeconds;
            if (verticalVelocity < -MaxFallSpeed) verticalVelocity = -MaxFallSpeed;
            p.Velocity = new Point3D(horizontalVelocity.X, verticalVelocity, horizontalVelocity.Z);
            var frameDisplacement = p.Velocity * deltaSeconds;
            MovePlayerWithCollisions(p, frameDisplacement);

            double hSpeed = Math.Sqrt(p.Velocity.X * p.Velocity.X + p.Velocity.Z * p.Velocity.Z);
            p.WalkAmount = (float)Math.Min(1.0, hSpeed / WalkSpeed);
            p.WalkPhase += deltaSeconds * p.WalkAmount * 10f;
        }

        private void MovePlayerWithCollisions(PlayerState p, Point3D displacement)
        {
            bool hitX = false, hitY = false, hitZ = false;
            var start = p.Position;
            p.Position = MoveAlongAxis(p.Position, displacement.X, Axis.X, ref hitX);
            p.Position = MoveAlongAxis(p.Position, displacement.Y, Axis.Y, ref hitY);
            p.Position = MoveAlongAxis(p.Position, displacement.Z, Axis.Z, ref hitZ);
            if (hitX || hitZ)
            {
                var stepped = TryStepUp(p, start, displacement);
                if (stepped.HasValue)
                {
                    p.Position = stepped.Value;
                    hitX = hitZ = false;
                    hitY = true;
                    p.Grounded = true;
                }
            }
            if (hitX) p.Velocity = new Point3D(0, p.Velocity.Y, p.Velocity.Z);
            if (hitZ) p.Velocity = new Point3D(p.Velocity.X, p.Velocity.Y, 0);
            if (hitY)
            {
                if (p.Velocity.Y <= 0)
                {
                    p.Grounded = true;
                    // Survival fall damage: impact speed above the threshold hurts (creative is
                    // immune via DamagePlayer). One heart per ~2.5 speed over the threshold.
                    double impactSpeed = -p.Velocity.Y;
                    if (p == LocalPlayer && impactSpeed > FallDamageThreshold)
                    {
                        int damage = Math.Max(1, (int)Math.Round((impactSpeed - FallDamageThreshold) / FallDamageScale));
                        DamagePlayer(damage, DeathCause.Fall);
                    }
                }
                p.Velocity = new Point3D(p.Velocity.X, 0, p.Velocity.Z);
            }
            else p.Grounded = false;
        }

        private bool PlayerSampleInWater(PlayerState p, double heightOffset)
        {
            int id = BlockRegistry.GetId("water");
            int x = (int)Math.Floor(p.Position.X);
            int y = (int)Math.Floor(p.Position.Y - EyeHeight + heightOffset);
            int z = (int)Math.Floor(p.Position.Z);
            return Chunks.TryGetLoadedBlock(x, y, z, out var block) && block == id;
        }

        private Point3D? TryStepUp(PlayerState p, Point3D start, Point3D displacement)
        {
            const double maxStepHeight = 0.5;
            var raised = new Point3D(start.X, start.Y + maxStepHeight, start.Z);
            if (IsPlayerColliding(raised)) return null;
            bool hx = false, hz = false;
            var moved = MoveAlongAxis(raised, displacement.X, Axis.X, ref hx);
            moved = MoveAlongAxis(moved, displacement.Z, Axis.Z, ref hz);
            if (hx || hz) return null;
            var down = moved;
            while (down.Y > start.Y)
            {
                var candidate = new Point3D(down.X, down.Y - CollisionStep, down.Z);
                if (IsPlayerColliding(candidate)) break;
                down = candidate;
            }
            return down;
        }

        private Point3D MoveAlongAxis(Point3D start, double amount, Axis axis, ref bool collided)
        {
            if (amount == 0.0) return start;
            int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(amount) / CollisionStep));
            double step = amount / steps;
            var current = start;
            for (int i = 0; i < steps; i++)
            {
                var next = axis switch
                {
                    Axis.X => new Point3D(current.X + step, current.Y, current.Z),
                    Axis.Y => new Point3D(current.X, current.Y + step, current.Z),
                    Axis.Z => new Point3D(current.X, current.Y, current.Z + step),
                    _ => current,
                };
                if (IsPlayerColliding(next))
                {
                    collided = true;
                    return current;
                }
                current = next;
            }
            return current;
        }

        public bool IsPlayerColliding(Point3D eyePosition)
        {
            double minX = eyePosition.X - PlayerRadius;
            double maxX = eyePosition.X + PlayerRadius;
            double minY = eyePosition.Y - EyeHeight;
            double maxY = minY + PlayerHeight;
            double minZ = eyePosition.Z - PlayerRadius;
            double maxZ = eyePosition.Z + PlayerRadius;
            int blockMinX = (int)Math.Floor(minX);
            int blockMaxX = (int)Math.Floor(maxX);
            int blockMinY = (int)Math.Floor(minY);
            int blockMaxY = (int)Math.Floor(maxY - 1e-5);
            int blockMinZ = (int)Math.Floor(minZ);
            int blockMaxZ = (int)Math.Floor(maxZ);
            for (int x = blockMinX; x <= blockMaxX; x++)
            for (int y = blockMinY; y <= blockMaxY; y++)
            for (int z = blockMinZ; z <= blockMaxZ; z++)
            {
                if (Chunks.TryGetLoadedBlockAndMeta(x, y, z, out var block, out var meta) && BlockRegistry.IsSolid(block))
                {
                    if (BoxesOverlapPlayer(GetBlockCollisionBoxes(block, meta), x, y, z, minX, maxX, minY, maxY, minZ, maxZ))
                        return true;
                }
            }
            return false;
        }

        public static (double minX, double minY, double minZ, double maxX, double maxY, double maxZ)[] GetBlockCollisionBoxes(int id, int meta)
        {
            if (BlockRegistry.IsSlab(id)) return new[] { (0.0, 0.0, 0.0, 1.0, 0.5, 1.0) };
            if (BlockRegistry.IsSlabTop(id)) return new[] { (0.0, 0.5, 0.0, 1.0, 1.0, 1.0) };
            if (BlockRegistry.IsStair(id))
            {
                return meta switch
                {
                    0 => new[] { (0.0, 0.0, 0.0, 0.5, 0.5, 1.0), (0.5, 0.0, 0.0, 1.0, 1.0, 1.0) },
                    1 => new[] { (0.0, 0.0, 0.0, 0.5, 1.0, 1.0), (0.5, 0.0, 0.0, 1.0, 0.5, 1.0) },
                    2 => new[] { (0.0, 0.0, 0.0, 1.0, 0.5, 0.5), (0.0, 0.0, 0.5, 1.0, 1.0, 1.0) },
                    _ => new[] { (0.0, 0.0, 0.0, 1.0, 1.0, 0.5), (0.0, 0.0, 0.5, 1.0, 0.5, 1.0) },
                };
            }
            if (BlockRegistry.IsCross(id)) return new[] { (0.25, 0.0, 0.25, 0.75, 0.8, 0.75) };
            return new[] { (0.0, 0.0, 0.0, 1.0, 1.0, 1.0) };
        }

        private static bool BoxesOverlapPlayer((double minX, double minY, double minZ, double maxX, double maxY, double maxZ)[] boxes,
            int bx, int by, int bz, double pMinX, double pMaxX, double pMinY, double pMaxY, double pMinZ, double pMaxZ)
        {
            foreach (var b in boxes)
            {
                if (bx + b.maxX > pMinX && bx + b.minX < pMaxX
                    && by + b.maxY > pMinY && by + b.minY < pMaxY
                    && bz + b.maxZ > pMinZ && bz + b.minZ < pMaxZ)
                {
                    return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------------
        // block editing (single source of truth; raises BlockEdited)
        // ------------------------------------------------------------------

        /// <summary>Breaks the block the player is looking at. Returns true if a block was removed,
        /// with the block's world position and previous id (for particle spawning).</summary>
        public bool TryBreakBlock(PlayerState p, Point3D origin, Point3D direction, out int removedBlockId, out (int x, int y, int z) removedPos)
        {
            removedBlockId = 0;
            removedPos = default;
            var pickResult = TryPickBlock(origin, direction);
            if (!pickResult.HasValue) return false;
            var remove = pickResult.Value.Remove;
            if (TryBreakBlockAt(remove.x, remove.y, remove.z, out removedBlockId))
            {
                if (!IsCreative) DamageSelectedTool(removedBlockId);
                removedPos = remove;
                return true;
            }
            return false;
        }

        /// <summary>Breaks the block at an exact world position (used by the mining system).
        /// Returns true if a block was removed, with its previous id for particle spawning.</summary>
        public bool TryBreakBlockAt(int x, int y, int z, out int removedBlockId)
        {
            removedBlockId = 0;
            if (!Chunks.TryGetLoadedBlock(x, y, z, out removedBlockId)) return false;
            if (!Chunks.TrySetBlock(x, y, z, BlockRegistry.AirId)) return false;
            // Survival: mining a block drops a physical item you have to collect - no teleporting
            // into the inventory. Leaves drop nothing yet; gravel drops flint instead.
            int dropId = removedBlockId;
            if (dropId == _idLeaves) dropId = 0;
            else if (dropId == _idGravel) dropId = _idFlint;
            if (!IsCreative && dropId > 0)
            {
                SpawnItemDrop(dropId, 1, new Point3D(x + 0.5, y + 0.5, z + 0.5));
            }
            BlockTicks?.OnBlockChanged(x, y, z);
            int rLayer = ChunkManager.LayerForWorldY(y);
            var editedChunk = new ChunkCoordinates(rLayer, WorldToChunkCoord(x), WorldToChunkCoord(z));
            Mesher.RequestImmediateRemesh(editedChunk);
            BlockEdited?.Invoke(x, y, z, 0, 0);
            return true;
        }

        /// <summary>Places the currently selected item at the targeted face. Items without a
        /// block behavior (tools, food, gems) can't place - those are handled by
        /// <see cref="TryEatSelectedFood"/> and future item-use hooks. Returns true if placed.</summary>
        public bool TryPlaceSelectedBlock(PlayerState p, Point3D origin, Point3D direction)
        {
            var pickResult = TryPickBlock(origin, direction, out double hitDistance);
            if (!pickResult.HasValue) return false;
            var place = pickResult.Value.Place;
            var normal = pickResult.Value.Normal;
            var hitPoint = origin + direction * hitDistance;

            // The hotbar holds ITEM ids now; resolve to the block this item places (-1 = not a
            // block item, e.g. tools/food/gemstones - nothing to place).
            int blockToPlace = ItemRegistry.ResolveBlockId(SelectedBlock);
            if (blockToPlace < 0) return false;
            int spendId = SelectedBlock; // consume the ORIGINAL selected item id (slabs can become top variants)
            int meta = 0;

            // Survival: you can only place blocks you actually own.
            if (!IsCreative)
            {
                if (spendId <= 0) return false;
                if (InventoryCount(spendId) < 1) return false;
            }

            // Can't place INTO a cell a falling block is currently passing through. Otherwise a
            // spam of placements in one column would stack into a moving block / collide mid-air.
            if (BlockTicks != null && BlockTicks.IsCellOccupiedByFalling(place.x, place.y, place.z))
            {
                return false;
            }

            if (BlockRegistry.IsSlab(blockToPlace) || BlockRegistry.IsSlabTop(blockToPlace))
            {
                var hit = pickResult.Value.Remove;
                if (TryMergeSlab(hit.x, hit.y, hit.z, normal, blockToPlace))
                {
                    if (!IsCreative) TryConsumeFromInventory(spendId, 1);
                    return true;
                }

                bool placeTop = normal.Y < 0 || (normal.Y == 0 && (hitPoint.Y - place.y) > 0.5);
                if (BlockRegistry.IsSlab(blockToPlace) && placeTop)
                {
                    blockToPlace = SlabTopIdFor(blockToPlace);
                }

                if (Chunks.TryGetLoadedBlockAndMeta(place.x, place.y, place.z, out var oldId, out _)
                    && oldId != BlockRegistry.AirId && !IsReplaceableFluid(oldId))
                {
                    if (TryFillSlabCell(place.x, place.y, place.z, blockToPlace))
                    {
                        if (!IsCreative) TryConsumeFromInventory(spendId, 1);
                        return true;
                    }
                    return false;
                }
            }
            else if (BlockRegistry.IsStair(blockToPlace))
            {
                meta = StairFacingMeta(p);
            }
            else
            {
                if (Chunks.TryGetLoadedBlockAndMeta(place.x, place.y, place.z, out var occupied, out _)
                    && occupied != BlockRegistry.AirId && !IsReplaceableFluid(occupied))
                {
                    return false;
                }
            }

            if (WouldBlockIntersectPlayer(p, place.x, place.y, place.z, blockToPlace, meta)) return false;
            if (!Chunks.TrySetBlock(place.x, place.y, place.z, blockToPlace, meta)) return false;
            if (!IsCreative) TryConsumeFromInventory(spendId, 1);
            BlockTicks?.OnBlockChanged(place.x, place.y, place.z);
            int placeLayer = ChunkManager.LayerForWorldY(place.y);
            var editedChunk = new ChunkCoordinates(placeLayer, WorldToChunkCoord(place.x), WorldToChunkCoord(place.z));
            Mesher.RequestImmediateRemesh(editedChunk);
            BlockEdited?.Invoke(place.x, place.y, place.z, blockToPlace, meta);
            return true;
        }

        /// <summary>Applies a block edit from the network (host authority). Same side effects as a
        /// local edit: sets the block, wakes fluids, remeshes, fires BlockEdited.</summary>
        public bool ApplyBlockEdit(int x, int y, int z, int blockId, int meta)
        {
            // Same "wait for it to fall" rule on the authoritative path (host applying a client's
            // edit): never place into a cell a falling block is passing through.
            if (BlockTicks != null && BlockTicks.IsCellOccupiedByFalling(x, y, z)) return false;
            if (!Chunks.TrySetBlockLoadedOnly(x, y, z, blockId, meta)) return false;
            BlockTicks?.OnBlockChanged(x, y, z);
            int ebLayer = ChunkManager.LayerForWorldY(y);
            var editedChunk = new ChunkCoordinates(ebLayer, WorldToChunkCoord(x), WorldToChunkCoord(z));
            Mesher.RequestImmediateRemesh(editedChunk);
            BlockEdited?.Invoke(x, y, z, blockId, meta);
            return true;
        }

        private bool TryMergeSlab(int x, int y, int z, Point3D normal, int heldBlock)
        {
            if (!Chunks.TryGetLoadedBlockAndMeta(x, y, z, out var hitId, out _)) return false;
            if (!BlockRegistry.IsSlab(hitId) && !BlockRegistry.IsSlabTop(hitId)) return false;
            if (SlabMaterialOf(hitId) != SlabMaterialOf(heldBlock)) return false;
            if (!((BlockRegistry.IsSlab(hitId) && normal.Y > 0) || (BlockRegistry.IsSlabTop(hitId) && normal.Y < 0))) return false;

            int fullId = BlockRegistry.GetId(SlabMaterialOf(hitId));
            if (!Chunks.TrySetBlock(x, y, z, fullId, 0)) return false;
            BlockTicks?.OnBlockChanged(x, y, z);
            int msLayer = ChunkManager.LayerForWorldY(y);
            Mesher.RequestImmediateRemesh(new ChunkCoordinates(msLayer, WorldToChunkCoord(x), WorldToChunkCoord(z)));
            BlockEdited?.Invoke(x, y, z, fullId, 0);
            return true;
        }

        private bool TryFillSlabCell(int x, int y, int z, int placingId)
        {
            if (!Chunks.TryGetLoadedBlockAndMeta(x, y, z, out var oldId, out _)) return false;
            if (!BlockRegistry.IsSlab(oldId) && !BlockRegistry.IsSlabTop(oldId)) return false;
            if (SlabMaterialOf(oldId) != SlabMaterialOf(placingId)) return false;
            bool oldTop = BlockRegistry.IsSlabTop(oldId);
            bool newTop = BlockRegistry.IsSlabTop(placingId);
            if (oldTop == newTop) return false;

            int fullId = BlockRegistry.GetId(SlabMaterialOf(oldId));
            if (!Chunks.TrySetBlock(x, y, z, fullId, 0)) return false;
            BlockTicks?.OnBlockChanged(x, y, z);
            int fsLayer = ChunkManager.LayerForWorldY(y);
            Mesher.RequestImmediateRemesh(new ChunkCoordinates(fsLayer, WorldToChunkCoord(x), WorldToChunkCoord(z)));
            BlockEdited?.Invoke(x, y, z, fullId, 0);
            return true;
        }

        private static bool IsReplaceableFluid(int id) => id == BlockRegistry.GetId("water");

        private static string SlabMaterialOf(int id)
        {
            string name = BlockRegistry.GetName(id);
            return name.EndsWith("_slab_top", StringComparison.Ordinal)
                ? name[..^"_slab_top".Length]
                : name.EndsWith("_slab", StringComparison.Ordinal) ? name[..^"_slab".Length] : name;
        }

        private static int SlabTopIdFor(int slabId)
            => BlockRegistry.GetId(SlabMaterialOf(slabId) + "_slab_top");

        private int StairFacingMeta(PlayerState p)
        {
            float yawRad = p.Yaw * (float)Math.PI / 180f;
            double dirX = Math.Sin(yawRad);
            double dirZ = Math.Cos(yawRad);
            if (Math.Abs(dirX) > Math.Abs(dirZ))
                return dirX > 0 ? 0 : 1;
            return dirZ > 0 ? 2 : 3;
        }

        private bool WouldBlockIntersectPlayer(PlayerState p, int x, int y, int z, int blockId, int meta)
        {
            double minX = p.Position.X - PlayerRadius;
            double maxX = p.Position.X + PlayerRadius;
            double minY = p.Position.Y - EyeHeight;
            double maxY = minY + PlayerHeight;
            double minZ = p.Position.Z - PlayerRadius;
            double maxZ = p.Position.Z + PlayerRadius;
            return BoxesOverlapPlayer(GetBlockCollisionBoxes(blockId, meta), x, y, z, minX, maxX, minY, maxY, minZ, maxZ);
        }

        // ------------------------------------------------------------------
        // ray picking (ported from Program.cs)
        // ------------------------------------------------------------------

        public PickBlockResult? TryPickBlock(Point3D origin, Point3D direction) => TryPickBlock(origin, direction, out _);

        public PickBlockResult? TryPickBlock(Point3D origin, Point3D direction, out double hitDistance)
        {
            hitDistance = double.PositiveInfinity;
            direction = direction.Normalized();
            int blockX = (int)Math.Floor(origin.X);
            int blockY = (int)Math.Floor(origin.Y);
            int blockZ = (int)Math.Floor(origin.Z);
            var stepX = Math.Sign(direction.X);
            var stepY = Math.Sign(direction.Y);
            var stepZ = Math.Sign(direction.Z);
            var tDeltaX = stepX != 0 ? Math.Abs(1.0 / direction.X) : double.PositiveInfinity;
            var tDeltaY = stepY != 0 ? Math.Abs(1.0 / direction.Y) : double.PositiveInfinity;
            var tDeltaZ = stepZ != 0 ? Math.Abs(1.0 / direction.Z) : double.PositiveInfinity;
            var tMaxX = stepX > 0 ? (blockX + 1.0 - origin.X) * tDeltaX : (origin.X - blockX) * tDeltaX;
            var tMaxY = stepY > 0 ? (blockY + 1.0 - origin.Y) * tDeltaY : (origin.Y - blockY) * tDeltaY;
            var tMaxZ = stepZ > 0 ? (blockZ + 1.0 - origin.Z) * tDeltaZ : (origin.Z - blockZ) * tDeltaZ;
            int currentX = blockX, currentY = blockY, currentZ = blockZ;
            var maxDistance = BlockReach;
            // A living mob's hitbox consumes the ray: blocks behind it are unreachable, so
            // fighting a mob can never accidentally break the wall behind it.
            bool mobInFront = Entities.TryRaycastMobs(origin, direction, maxDistance, out double mobDist);
            var distance = 0.0;
            for (int iteration = 0; iteration < 400 && distance <= maxDistance; iteration++)
            {
                // The mob sits strictly before this cell - everything beyond is behind it.
                if (mobInFront && distance > mobDist) return null;

                if (Chunks.TryGetLoadedBlockAndMeta(currentX, currentY, currentZ, out var block, out var meta)
                    && block != BlockRegistry.AirId
                    && block != BlockRegistry.GetId("water"))
                {
                    double cellExit = Math.Min(tMaxX, Math.Min(tMaxY, tMaxZ));
                    var boxes = GetBlockCollisionBoxes(block, meta);
                    foreach (var b in boxes)
                    {
                        if (RayBoxHit(origin, direction,
                                currentX + b.minX, currentY + b.minY, currentZ + b.minZ,
                                currentX + b.maxX, currentY + b.maxY, currentZ + b.maxZ,
                                distance - 1e-9, cellExit + 1e-9, out double t, out var n))
                        {
                            hitDistance = Math.Max(0.0, t);
                            // Mob closer than (or overlapping) the block face blocks the pick.
                            if (mobInFront && mobDist <= t) return null;
                            var face = ComputeFaceRect(currentX, currentY, currentZ, b, n);
                            var place = ((int)Math.Floor(currentX + n.X + 0.5), (int)Math.Floor(currentY + n.Y + 0.5), (int)Math.Floor(currentZ + n.Z + 0.5));
                            return new PickBlockResult((currentX, currentY, currentZ), place, n, face);
                        }
                    }
                }

                if (tMaxX < tMaxY)
                {
                    if (tMaxX < tMaxZ) { currentX += stepX; distance = tMaxX; tMaxX += tDeltaX; }
                    else { currentZ += stepZ; distance = tMaxZ; tMaxZ += tDeltaZ; }
                }
                else
                {
                    if (tMaxY < tMaxZ) { currentY += stepY; distance = tMaxY; tMaxY += tDeltaY; }
                    else { currentZ += stepZ; distance = tMaxZ; tMaxZ += tDeltaZ; }
                }
            }
            return null;
        }

        // ------------------------------------------------------------------
        // spawn / deep-fill
        // ------------------------------------------------------------------

        private Point3D? FindSafeSpawnPosition()
        {
            int baseX = (int)Math.Floor(LocalPlayer.Position.X);
            int baseZ = (int)Math.Floor(LocalPlayer.Position.Z);
            const int seaLevelWorldY = 0;
            // Search out to ~300 blocks so land is reachable even when the origin sits in a wide
            // ocean basin.
            for (int radius = 0; radius <= 300; radius++)
            {
                int bestY = int.MinValue, bestX = 0, bestZ = 0;
                for (int dx = -radius; dx <= radius; dx++)
                for (int dz = -radius; dz <= radius; dz++)
                {
                    if (radius > 0 && Math.Abs(dx) != radius && Math.Abs(dz) != radius) continue;
                    int wx = baseX + dx;
                    int wz = baseZ + dz;
                    Chunks.GetOrCreateChunk(WorldToChunkCoord(wx), WorldToChunkCoord(wz));
                    int surfaceY = FindSurfaceWorldY(wx, wz);
                    if (surfaceY < seaLevelWorldY) continue;
                    if (surfaceY > bestY)
                    {
                        bestY = surfaceY;
                        bestX = wx;
                        bestZ = wz;
                    }
                }
                if (bestY == int.MinValue) continue;

                double px = bestX + 0.5;
                double pz = bestZ + 0.5;
                double minEyeY = bestY + EyeHeight + 0.01;
                double maxEyeY = ChunkManager.ChunkHeight + 1.0;
                for (double eyeY = minEyeY; eyeY <= maxEyeY; eyeY += 0.25)
                {
                    var candidate = new Point3D(px, eyeY, pz);
                    if (!IsPlayerColliding(candidate)) return candidate;
                }
            }
            return null;
        }

        private int FindSurfaceWorldY(int wx, int wz)
        {
            for (int wy = ChunkManager.WorldOriginY + ChunkManager.ChunkHeight - 1; wy >= ChunkManager.WorldOriginY; wy--)
            {
                if (Chunks.TryGetLoadedBlock(wx, wy, wz, out var block) && BlockRegistry.IsSolid(block))
                {
                    return wy;
                }
            }
            return ChunkManager.WorldOriginY - 1;
        }

        // Lazy stratosphere fill (mirror of the deep-fill idea): while the player is very high up,
        // fill the upper zone (world ~512..1000) of nearby chunks with sky islands. New chunks
        // generated while high are born with their islands (AutoHighFill); already-loaded chunks
        // are filled once here. This keeps surface chunk gen cheap - the stratosphere is empty
        // air until the player climbs toward it.
        private void UpdateHighFill()
        {
            if (Chunks == null || SkyChunkProvider == null) return;
            // Start filling when the player is above the cloud deck by a good margin.
            const double highThreshold = 450.0;
            bool isHigh = LocalPlayer.Position.Y > highThreshold;
            SkyChunkProvider.Islands.AutoHighFill = isHigh;
            if (!isHigh) return;

            int cx = (int)Math.Floor(LocalPlayer.Position.X / ChunkManager.ChunkSize);
            int cz = (int)Math.Floor(LocalPlayer.Position.Z / ChunkManager.ChunkSize);

            foreach (var ch in Chunks.GetLoadedChunks())
            {
                if (ch.OriginY != ChunkManager.SkyOriginY) continue; // only sky-layer chunks
                int dx = ch.OriginX / ChunkManager.ChunkSize - cx;
                int dz = ch.OriginZ / ChunkManager.ChunkSize - cz;
                if (dx * dx + dz * dz > 64) continue; // within ~8 chunks of the player
                int layer = ChunkManager.SkyLayer;
                int chunkX = ch.OriginX / ChunkManager.ChunkSize;
                int chunkZ = ch.OriginZ / ChunkManager.ChunkSize;
                SkyChunkProvider.Islands.HighFillChunk(chunkX, chunkZ, ch, 16, ChunkManager.SkyHeight);
                if (ch.NeedsRemesh)
                {
                    ch.IsMeshingQueued = false;
                    Mesher?.RequestImmediateRemesh(new ChunkCoordinates(layer, chunkX, chunkZ));
                }
            }
        }

        // ------------------------------------------------------------------
        // helpers (camera math used by both sim and render)
        // ------------------------------------------------------------------

        public Point3D GetCameraForward() => GetCameraForward(LocalPlayer);

        public Point3D GetCameraForward(PlayerState p)
        {
            var yawRad = p.Yaw * Math.PI / 180.0;
            var pitchRad = p.Pitch * Math.PI / 180.0;
            var cosPitch = Math.Cos(pitchRad);
            return new Point3D(cosPitch * Math.Sin(yawRad), Math.Sin(pitchRad), cosPitch * Math.Cos(yawRad)).Normalized();
        }

        public static Point3D GetCameraRight(float yaw)
        {
            var yawRad = yaw * Math.PI / 180.0;
            return new Point3D(Math.Cos(yawRad), 0, -Math.Sin(yawRad)).Normalized();
        }

        public static int WorldToChunkCoord(double value) => (int)Math.Floor(value / ChunkManager.ChunkSize);

        public static float NormalizeYaw(float yaw)
        {
            float result = yaw % 360f;
            if (result < 0f) result += 360f;
            return result;
        }

        /// <summary>Normalizes an angle in radians to the [-PI, PI] range.</summary>
        private static float NormalizeRadians(float a)
        {
            const float twoPi = (float)(Math.PI * 2.0);
            while (a > (float)Math.PI) a -= twoPi;
            while (a < -(float)Math.PI) a += twoPi;
            return a;
        }

        private static bool RayBoxHit(Point3D o, Point3D d,
            double bMinX, double bMinY, double bMinZ, double bMaxX, double bMaxY, double bMaxZ,
            double tMinLimit, double tMaxLimit, out double t, out Point3D normal)
        {
            t = 0; normal = Point3D.Zero;
            double tMin = tMinLimit, tMax = tMaxLimit;
            int axis = -1;
            double ox = o.X, oy = o.Y, oz = o.Z, dx = d.X, dy = d.Y, dz = d.Z;
            double[] bmin = { bMinX, bMinY, bMinZ };
            double[] bmax = { bMaxX, bMaxY, bMaxZ };
            double[] oa = { ox, oy, oz };
            double[] da = { dx, dy, dz };
            for (int a = 0; a < 3; a++)
            {
                if (Math.Abs(da[a]) < 1e-12)
                {
                    if (oa[a] < bmin[a] || oa[a] > bmax[a]) return false;
                }
                else
                {
                    double t1 = (bmin[a] - oa[a]) / da[a];
                    double t2 = (bmax[a] - oa[a]) / da[a];
                    if (t1 > t2) { (t1, t2) = (t2, t1); }
                    if (t1 > tMin) { tMin = t1; axis = a; }
                    if (t2 < tMax) tMax = t2;
                    if (tMin > tMax) return false;
                }
            }
            t = tMin;
            normal = axis switch
            {
                0 => new Point3D(-Math.Sign(dx), 0, 0),
                1 => new Point3D(0, -Math.Sign(dy), 0),
                _ => new Point3D(0, 0, -Math.Sign(dz)),
            };
            return true;
        }

        private static (double minX, double minY, double minZ, double maxX, double maxY, double maxZ) ComputeFaceRect(
            int cx, int cy, int cz, (double minX, double minY, double minZ, double maxX, double maxY, double maxZ) b, Point3D n)
        {
            if (n.X > 0.5) return (cx + b.maxX, cy + b.minY, cz + b.minZ, cx + b.maxX, cy + b.maxY, cz + b.maxZ);
            if (n.X < -0.5) return (cx + b.minX, cy + b.minY, cz + b.minZ, cx + b.minX, cy + b.maxY, cz + b.maxZ);
            if (n.Y > 0.5) return (cx + b.minX, cy + b.maxY, cz + b.minZ, cx + b.maxX, cy + b.maxY, cz + b.maxZ);
            if (n.Y < -0.5) return (cx + b.minX, cy + b.minY, cz + b.minZ, cx + b.maxX, cy + b.minY, cz + b.maxZ);
            if (n.Z > 0.5) return (cx + b.minX, cy + b.minY, cz + b.maxZ, cx + b.maxX, cy + b.maxY, cz + b.maxZ);
            return (cx + b.minX, cy + b.minY, cz + b.minZ, cx + b.maxX, cy + b.maxY, cz + b.minZ);
        }

        public void Dispose()
        {
            try { _chunkGenWorker?.Dispose(); } catch { }
            try { (_meshQueue as MeshWorker)?.Dispose(); } catch { }
        }

        private enum Axis { X, Y, Z }

        public readonly struct PickBlockResult
        {
            public (int x, int y, int z) Remove { get; }
            public (int x, int y, int z) Place { get; }
            public Point3D Normal { get; }
            public (double minX, double minY, double minZ, double maxX, double maxY, double maxZ) Face { get; }
            public PickBlockResult((int x, int y, int z) remove, (int x, int y, int z) place, Point3D normal,
                (double minX, double minY, double minZ, double maxX, double maxY, double maxZ) face)
            {
                Remove = remove; Place = place; Normal = normal; Face = face;
            }
        }
    }
}



