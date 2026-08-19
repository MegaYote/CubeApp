using System;
using System.Collections.Generic;
using System.Numerics;

namespace Cubuild
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
    public sealed partial class GameWorld : IDisposable
    {
        // ---- world identity / services ----
        /// <summary>
        /// Generation version of the CURRENT build. Bump by 1 whenever world generation changes
        /// in any way: new ores, new structures, terrain algorithm tweaks, biome shifts. Saves
        /// stamp their own version (WorldSave.GenVersion); worlds stamped with an older version
        /// warn on load instead of silently mutating when generation changes.
        /// </summary>
        public const int GenerationVersion = 2;
        public int Seed { get; private set; }
        public string Name { get; private set; } = "World 1";
        /// <summary>The generation version this world was originally created with (from its save
        /// stamp, or <see cref="GenerationVersion"/> for freshly created worlds). Never upgraded
        /// on save, so a world keeps warning about its true age. 0 = made before versioning.</summary>
        public int WorldGenVersion { get; set; }
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

        // Wood sealant bucket use tracking: slot index -> number of uses (max 8)
        private readonly Dictionary<int, int> _sealantUses = new();

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


