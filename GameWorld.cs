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
        public World.InfdevChunkProvider ChunkProvider { get; private set; }
        public World.SkyChunkProvider SkyChunkProvider { get; private set; }
        public EntityManager Entities { get; private set; }
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

        // ---- physics constants (shared with render layer for third-person view) ----
        public const float WalkSpeed = 4.317f;
        public const float FlySpeed = 10.8f;
        public const float JumpVelocity = 8.0f;
        public const float Gravity = 24.0f;
        public const float MaxFallSpeed = 36.0f;
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

            ChunkProvider = new World.InfdevChunkProvider(seed);
            SkyChunkProvider = new World.SkyChunkProvider(seed);
            Chunks = new ChunkManager(new World.DeepChunkProvider(seed), ChunkProvider, SkyChunkProvider);
            Entities = new EntityManager(Chunks);
            // Mesh workers scale with the machine: at least 2, up to ~cores/4. Chunk gen already
            // takes ProcessorCount-2 threads, so meshing gets a share of what's left without
            // starving the render thread on low-end machines.
            int meshWorkers = Math.Clamp(Environment.ProcessorCount / 4, 2, 8);
            _meshQueue = new MeshWorker(Chunks, getRenderer, meshWorkers);
            Mesher = new MeshScheduler(Chunks, _meshQueue);
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
            var spawn = FindSafeSpawnPosition();
            if (spawn.HasValue) LocalPlayer.Position = spawn.Value;
            LocalPlayer.Velocity = new Point3D(0, 0, 0);
            LocalPlayer.Grounded = true;
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

        /// <summary>Advance the simulation by one frame. Pure logic; no rendering here.</summary>
        public void StepSimulation(TickInputState tickInput, float deltaSeconds)
        {
            // Day/night clock: MC advances worldTime at a fixed 20 ticks/sec (Infdev: worldTime
            // advances once per tick, full cycle = 24000 ticks = 20 minutes). Advance by delta so
            // the sky (sun/moon/stars) moves at MC speed regardless of frame rate - per-frame
            // ++ made the whole 24000-tick cycle spin in seconds at high FPS.
            WorldTime += (long)Math.Round(deltaSeconds * 20.0);
            BlockTicks?.Tick(deltaSeconds);
            StepPlayer(LocalPlayer, tickInput, deltaSeconds);
            Entities.Update(deltaSeconds, LocalPlayer.Position, true);
            int chunkX = WorldToChunkCoord(LocalPlayer.Position.X);
            int chunkZ = WorldToChunkCoord(LocalPlayer.Position.Z);
            // Request/unload scans cost O(radius^2) + O(loadedChunks); only run them when the
            // player actually enters a new chunk column, the render distance changed, OR the
            // player crosses a vertical streaming threshold (digging straight down in one column
            // keeps X/Z constant but must still wake the deep/sky layer streams).
            double py = LocalPlayer.Position.Y;
            bool crossedDeep = (py < DeepStreamThreshold) != _lastBelowDeep;
            bool crossedSky = (py > SkyStreamThreshold) != _lastAboveSky;
            if (_forceChunkStream || chunkX != _lastStreamChunkX || chunkZ != _lastStreamChunkZ || crossedDeep || crossedSky)
            {
                _forceChunkStream = false;
                _lastStreamChunkX = chunkX;
                _lastStreamChunkZ = chunkZ;
                _lastBelowDeep = py < DeepStreamThreshold;
                _lastAboveSky = py > SkyStreamThreshold;
                Chunks.RequestChunksAround(chunkX, chunkZ, ChunkRenderRadius, LocalPlayer.Position, ChunkManager.GroundLayer);
                // The deep layer only streams when the player digs down (lazy allocation).
                if (py < DeepStreamThreshold)
                {
                    Chunks.RequestChunksAround(chunkX, chunkZ, ChunkRenderRadius, LocalPlayer.Position, ChunkManager.DeepLayer);
                }
                // The sky layer only streams when the player climbs into the stratosphere.
                if (py > SkyStreamThreshold)
                {
                    Chunks.RequestChunksAround(chunkX, chunkZ, ChunkRenderRadius, LocalPlayer.Position, ChunkManager.SkyLayer);
                }
                var unloaded = Chunks.UnloadChunksOutside(chunkX, chunkZ, ChunkRenderRadius);
                foreach (var uc in unloaded) ChunkUnloaded?.Invoke(uc);
            }
            UpdateHighFill();
        }

        private const double DeepStreamThreshold = 0.0;
        private const double SkyStreamThreshold = 350.0;
        private bool _lastBelowDeep;
        private bool _lastAboveSky;

        /// <summary>Day/night clock in world ticks. Full cycle = 24000 ticks (Infdev).</summary>
        public long WorldTime { get; private set; }

        /// <summary>
        /// Infdev's getCelestialAngle: 0..1 sun position across the day (0.25 = dawn, 0.75 = dusk).
        /// Faithful port of World.getCelestialAngle.
        /// </summary>
        public float GetCelestialAngle(float partialTick)
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
        /// Infdev's calculateSkylightSubtracted: 0 (noon) .. 11 (midnight) amount subtracted from
        /// sky light at render time. Faithful port of World.calculateSkylightSubtracted.
        /// </summary>
        public int CalculateSkylightSubtracted(float partialTick)
        {
            float celestial = GetCelestialAngle(partialTick);
            float v = 1f - (float)(Math.Cos(celestial * Math.PI * 2.0) * 2.0 + 0.5);
            if (v < 0f) v = 0f;
            if (v > 1f) v = 1f;
            return (int)(v * 11f);
        }

        /// <summary>Infdev's sky-color dimming factor (1.0 at noon, near 0 at midnight).</summary>
        public float GetSkyBrightnessFactor(float partialTick)
        {
            float celestial = GetCelestialAngle(partialTick);
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

            var horizontalVelocity = desiredDirection * WalkSpeed;
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
                if (p.Velocity.Y <= 0) p.Grounded = true;
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
            if (!Chunks.TryGetLoadedBlock(remove.x, remove.y, remove.z, out removedBlockId)) return false;
            if (!Chunks.TrySetBlock(remove.x, remove.y, remove.z, BlockRegistry.AirId)) return false;
            removedPos = remove;
            BlockTicks?.OnBlockChanged(remove.x, remove.y, remove.z);
            int rLayer = ChunkManager.LayerForWorldY(remove.y);
            var editedChunk = new ChunkCoordinates(rLayer, WorldToChunkCoord(remove.x), WorldToChunkCoord(remove.z));
            Mesher.RequestImmediateRemesh(editedChunk);
            BlockEdited?.Invoke(remove.x, remove.y, remove.z, 0, 0);
            return true;
        }

        /// <summary>Places the currently selected block at the targeted face. Returns true if placed.</summary>
        public bool TryPlaceSelectedBlock(PlayerState p, Point3D origin, Point3D direction)
        {
            var pickResult = TryPickBlock(origin, direction, out double hitDistance);
            if (!pickResult.HasValue) return false;
            var place = pickResult.Value.Place;
            var normal = pickResult.Value.Normal;
            var hitPoint = origin + direction * hitDistance;

            int blockToPlace = SelectedBlock;
            int meta = 0;

            // Minecraft's "wait for it to fall" rule: you can't place INTO a cell a falling
            // block is currently passing through. Otherwise a spam of placements in one column
            // would stack into a moving block / collide mid-air.
            if (BlockTicks != null && BlockTicks.IsCellOccupiedByFalling(place.x, place.y, place.z))
            {
                return false;
            }

            if (BlockRegistry.IsSlab(blockToPlace) || BlockRegistry.IsSlabTop(blockToPlace))
            {
                var hit = pickResult.Value.Remove;
                if (TryMergeSlab(hit.x, hit.y, hit.z, normal, blockToPlace)) return true;

                bool placeTop = normal.Y < 0 || (normal.Y == 0 && (hitPoint.Y - place.y) > 0.5);
                if (BlockRegistry.IsSlab(blockToPlace) && placeTop)
                {
                    blockToPlace = SlabTopIdFor(blockToPlace);
                }

                if (Chunks.TryGetLoadedBlockAndMeta(place.x, place.y, place.z, out var oldId, out _)
                    && oldId != BlockRegistry.AirId && !IsReplaceableFluid(oldId))
                {
                    if (TryFillSlabCell(place.x, place.y, place.z, blockToPlace)) return true;
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
            var distance = 0.0;
            for (int iteration = 0; iteration < 400 && distance <= maxDistance; iteration++)
            {
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
            for (int radius = 0; radius <= 64; radius++)
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
