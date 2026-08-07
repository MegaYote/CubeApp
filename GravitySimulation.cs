using System;
using System.Collections.Generic;

namespace CubeApp
{
    /// <summary>
    /// Gravity for sand/gravel/dirt/red_clay (anything flagged "gravity" in blocks.json).
    ///
    /// The rule is deliberately lazy, per the design decision: blocks only fall when UPDATED.
    /// The world may generate floating overhangs and they stay perfectly still until the player
    /// (or water, or a cave-in) changes a block near them - then gravity remembers them and they
    /// drop. This makes cave-ins "waiting to happen" without making world-gen collapse on itself.
    ///
    /// PERFORMANCE (hundreds falling at once): when a block loses support the ENTIRE contiguous
    /// gravity column above it is popped into falling entities in a SINGLE tick (not one block
    /// per tick), so an avalanche starts moving immediately. The entity list uses swap-remove
    /// (O(1) removal, no List.RemoveAt shifts) and the landing pass marks landed blocks in place
    /// (no per-step allocations, no Contains() scans) - a 500-block collapse costs O(n) per step.
    ///
    /// SMOOTHNESS: falling integrates PER RENDERED FRAME with the real delta (no fixed 20Hz
    /// steps), sub-stepped to at most 0.5 blocks per integration so a low-framerate frame can't
    /// tunnel through a thin floor. On landing, the cube STAYS rendered (placed in the world
    /// grid + priority remesh requested) until the chunk's mesh actually refreshes - so there is
    /// never an invisible gap between the entity disappearing and the mesh catching up.
    ///
    /// Triggering: BlockTickScheduler.OnBlockChanged calls us with every changed cell. We check
    /// that cell and the cell directly above it; an unsupported gravity block is scheduled to
    /// tick, and TickBlock pops the whole column.
    ///
    /// NOTE: only ever touches the world on the main thread (the tick loop and UpdateFalling
    /// both run inside the game loop), so it is safe alongside the networking main-thread rule.
    /// </summary>
    public sealed class GravitySimulation
    {
        private const int FallDelayTicks = 1;
        private const float FallGravity = 24.0f;   // matches player gravity; snappy cave-ins
        private const float MaxFallSpeed = 32.0f;
        private const float MaxStepPerIntegration = 0.5f; // prevents tunneling at low framerate

        private readonly ChunkManager _manager;
        private readonly BlockTickScheduler _tickScheduler;
        private readonly MeshScheduler _meshScheduler;
        private readonly int _waterId;
        private readonly int _worldTopY; // exclusive: one past the highest block row

        // Active falling blocks (entity list; NOT in the world grid). Parallel speeds.
        // A block that has landed but is waiting for its chunk to remesh stays in this list
        // (speed 0, position snapped to the landing cell) so the renderer keeps drawing it.
        private readonly List<FallingBlockData> _falling = new();
        private readonly List<float> _fallSpeeds = new();
        // For each falling entry: mesh-wait state, or null if still falling. When non-null the
        // block has landed and we hold it until chunk.MeshVersion exceeds the snapshot.
        private readonly List<(ChunkCoordinates coords, int version)?> _meshWait = new();

        public GravitySimulation(ChunkManager manager, BlockTickScheduler tickScheduler, MeshScheduler meshScheduler)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            _tickScheduler = tickScheduler ?? throw new ArgumentNullException(nameof(tickScheduler));
            _meshScheduler = meshScheduler ?? throw new ArgumentNullException(nameof(meshScheduler));
            _waterId = BlockRegistry.GetId("water");
            _worldTopY = ChunkManager.WorldOriginY + ChunkManager.ChunkHeight;
        }

        /// <summary>Read-only view of the blocks currently falling or waiting for their landing
        /// mesh (for the renderer).</summary>
        public IReadOnlyList<FallingBlockData> FallingBlocks => _falling;

        /// <summary>True when a falling block currently occupies the given world cell. Placement
        /// refuses cells a falling block is passing through (Minecraft's "wait for it to fall out
        /// of the way" behaviour - you can't stack a new block into a moving one).</summary>
        public bool IsCellOccupiedByFalling(int x, int y, int z)
        {
            // n is small (a handful during normal play); a big cave-in rarely sees simultaneous
            // placements, so a linear scan is fine and avoids a per-frame occupancy structure.
            for (int i = 0; i < _falling.Count; i++)
            {
                var f = _falling[i];
                if ((int)Math.Floor(f.X) == x && (int)Math.Floor(f.Y) == y && (int)Math.Floor(f.Z) == z)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Called when any block in the world changes. If the changed cell (or the cell
        /// above it) is now an unsupported gravity block, schedule it to fall.</summary>
        public void OnBlockChanged(int x, int y, int z)
        {
            CheckCell(x, y, z);
            CheckCell(x, y + 1, z);
        }

        private void CheckCell(int x, int y, int z)
        {
            if (_manager.TryGetLoadedBlock(x, y, z, out var id) && BlockRegistry.IsGravity(id) && IsUnsupported(x, y, z))
            {
                _tickScheduler.Schedule(x, y, z, FallDelayTicks);
            }
        }

        /// <summary>True when the block at (x,y,z) has no solid support directly below (air or
        /// water underneath = unsupported).</summary>
        private bool IsUnsupported(int x, int y, int z)
        {
            if (!_manager.TryGetLoadedBlock(x, y - 1, z, out var belowId)) return true;
            return belowId == BlockRegistry.AirId || belowId == _waterId;
        }

        /// <summary>One scheduled tick for a gravity block: if still unsupported, pop the ENTIRE
        /// contiguous gravity column above it into falling entities in a single operation.</summary>
        public void TickBlock(int x, int y, int z)
        {
            if (!_manager.TryGetLoadedBlock(x, y, z, out var id) || !BlockRegistry.IsGravity(id)) return;
            if (!IsUnsupported(x, y, z)) return;

            // Walk up the column while the block above is also an (now-unsupported) gravity
            // block, popping each into a falling entity. O(column height) total - a 100-block
            // tower starts falling in ONE tick, not 100.
            int top = y;
            while (top < _worldTopY)
            {
                if (!_manager.TryGetLoadedBlock(x, top, z, out var upId) || !BlockRegistry.IsGravity(upId)) break;
                if (!_manager.TrySetBlockLoadedOnly(x, top, z, BlockRegistry.AirId, 0)) break;
                _falling.Add(new FallingBlockData(upId, x + 0f, top, z + 0f));
                _fallSpeeds.Add(0f);
                _meshWait.Add(null);
                top++;
            }

            // The vacated column wakes what's above it (a gravity block resting on this column's
            // top) and the water below (displaced air lets it flow in).
            _tickScheduler.OnBlockChanged(x, top, z);
            _tickScheduler.OnBlockChanged(x, y - 1, z);
        }

        /// <summary>Advance all falling blocks by one rendered frame (main thread). Integrates
        /// with the real delta for smooth motion; sub-steps prevent tunneling at low framerates.</summary>
        public void UpdateFalling(float deltaSeconds)
        {
            // First, release any landed blocks whose chunk has finished remeshing.
            ReleaseMeshedLandings();

            if (_falling.Count == 0) return;

            deltaSeconds = Math.Min(deltaSeconds, 0.1f);

            // Integrate velocities, then resolve movement + landings in one pass.
            for (int i = 0; i < _falling.Count;)
            {
                // Skipped while waiting for a mesh (landed, speed 0, no physics).
                if (_meshWait[i] != null)
                {
                    i++;
                    continue;
                }

                var f = _falling[i];
                float vel = _fallSpeeds[i] + FallGravity * deltaSeconds;
                if (vel > MaxFallSpeed) vel = MaxFallSpeed;
                _fallSpeeds[i] = vel;

                float remaining = vel * deltaSeconds;
                float newY = f.Y;
                int floorX = (int)Math.Floor(f.X);
                int floorZ = (int)Math.Floor(f.Z);
                bool landed = false;

                // Sub-step: at most MaxStepPerIntegration blocks per move so a heavy frame can't
                // punch through a floor between two checks.
                while (remaining > 0f)
                {
                    float step = Math.Min(remaining, MaxStepPerIntegration);
                    newY -= step;
                    remaining -= step;

                    int cellY = (int)Math.Floor(newY);
                    if (HasSupportBelow(floorX, cellY - 1, floorZ))
                    {
                        // Landed. If the landing cell is already occupied (another block settled
                        // there this frame), push up onto it so columns restack.
                        int finalY = cellY;
                        if (_manager.TryGetLoadedBlock(floorX, finalY, floorZ, out var occ) && IsSupport(occ))
                        {
                            finalY = cellY + 1;
                        }
                        _manager.TrySetBlockLoadedOnly(floorX, finalY, floorZ, f.BlockId, 0);
                        _tickScheduler.OnBlockChanged(floorX, finalY, floorZ);

                        // Request a PRIORITY remesh and keep this cube rendered until it lands.
                        var coords = new ChunkCoordinates(GameWorld.WorldToChunkCoord(floorX), GameWorld.WorldToChunkCoord(floorZ));
                        int before = 0;
                        if (_manager.TryGetLoadedChunk(coords, out var chunk)) before = chunk.MeshVersion;
                        _meshScheduler.RequestImmediateRemesh(coords);

                        // Snap to the landing cell and hold until the chunk's mesh refreshes.
                        _falling[i] = new FallingBlockData(f.BlockId, floorX, finalY, floorZ);
                        _fallSpeeds[i] = 0f;
                        _meshWait[i] = (coords, before);
                        landed = true;
                        break;
                    }
                }

                if (!landed)
                {
                    _falling[i] = new FallingBlockData(f.BlockId, f.X, newY, f.Z);
                    i++;
                }
            }
        }

        // Removes landed cubes once their chunk's mesh has been rebuilt (MeshVersion changed),
        // so the cube never flickers out before the world shows the new block. Headless (no-op
        // mesh queue) never advances versions - release immediately in that case.
        private void ReleaseMeshedLandings()
        {
            bool waitForMesh = _meshScheduler.HasRealMeshQueue;
            for (int i = _falling.Count - 1; i >= 0; i--)
            {
                var wait = _meshWait[i];
                if (wait == null) continue;

                bool release = !waitForMesh;
                if (!release && _manager.TryGetLoadedChunk(wait.Value.coords, out var chunk))
                {
                    if (chunk.MeshVersion != wait.Value.version) release = true;
                }
                else if (!release)
                {
                    release = true; // chunk unloaded; cube is gone anyway
                }

                if (release)
                {
                    SwapRemove(i);
                }
            }
        }

        private void SwapRemove(int i)
        {
            int last = _falling.Count - 1;
            _falling[i] = _falling[last];
            _fallSpeeds[i] = _fallSpeeds[last];
            _meshWait[i] = _meshWait[last];
            _falling.RemoveAt(last);
            _fallSpeeds.RemoveAt(last);
            _meshWait.RemoveAt(last);
        }

        private bool HasSupportBelow(int x, int y, int z)
        {
            if (!_manager.TryGetLoadedBlock(x, y, z, out var below)) return false;
            return IsSupport(below);
        }

        /// <summary>Solid enough to hold up a gravity block: any non-air solid block, including
        /// slabs/stairs (their partial geometry still supports).</summary>
        private static bool IsSupport(int id)
            => id != BlockRegistry.AirId && (BlockRegistry.IsSolid(id) || BlockRegistry.IsPartialShape(id));
    }
}
