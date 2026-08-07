using System;
using System.Collections.Generic;

namespace CubeApp
{
    /// <summary>
    /// The engine's analogue of Minecraft's scheduled-tick list. Fluid updates are bucketed per
    /// chunk (duplicates collapse to one entry) and fire at MC's fixed 20 ticks/second, so water
    /// spreads at exactly the same speed as the reference even though the render loop runs free.
    /// After each batch the mesh scheduler is flushed so touched chunks remesh immediately.
    /// </summary>
    public sealed class BlockTickScheduler
    {
        private const double TickIntervalSeconds = 1.0 / 20.0;
        // Hard per-tick budget so a huge disturbance (say, draining the ocean) can't stall a
        // frame; leftover due updates simply wait for the next tick, like MC's 100-update cap.
        private const int MaxUpdatesPerTick = 2048;

        private readonly ChunkManager _manager;
        private readonly MeshScheduler _meshScheduler;
        private readonly FluidSimulation _fluid;
        private readonly GravitySimulation _gravity;
        private readonly GrassSpreadSimulation _grass;
        private readonly Dictionary<ChunkCoordinates, Dictionary<(int x, int y, int z), int>> _pending = new();
        private readonly List<(int x, int y, int z)> _due = new();
        private readonly List<ChunkCoordinates> _emptyBuckets = new();
        private int _tick;
        private double _accumulator;

        public BlockTickScheduler(ChunkManager manager, MeshScheduler meshScheduler)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            _meshScheduler = meshScheduler ?? throw new ArgumentNullException(nameof(meshScheduler));
            _fluid = new FluidSimulation(manager, this);
            _gravity = new GravitySimulation(manager, this, meshScheduler);
            _grass = new GrassSpreadSimulation(manager, this);
        }

        public FluidSimulation Fluid => _fluid;
        public GravitySimulation Gravity => _gravity;
        public GrassSpreadSimulation Grass => _grass;

        /// <summary>True when a falling block currently occupies the cell (placement should wait).</summary>
        public bool IsCellOccupiedByFalling(int x, int y, int z) => _gravity.IsCellOccupiedByFalling(x, y, z);

        /// <summary>Schedule <paramref name="cell"/> to tick in <paramref name="delayTicks"/>
        /// game ticks. Duplicate schedules collapse; only the earliest due time survives.</summary>
        public void Schedule(int x, int y, int z, int delayTicks)
        {
            if (delayTicks <= 0)
            {
                return;
            }

            var key = new ChunkCoordinates(ChunkManager.LayerForWorldY(y),
                (int)Math.Floor(x / (double)ChunkManager.ChunkSize), (int)Math.Floor(z / (double)ChunkManager.ChunkSize));
            if (!_pending.TryGetValue(key, out var bucket))
            {
                bucket = new Dictionary<(int, int, int), int>();
                _pending[key] = bucket;
            }

            int due = _tick + delayTicks;
            if (bucket.TryGetValue((x, y, z), out var existing) && existing <= due)
            {
                return;
            }

            bucket[(x, y, z)] = due;
        }

        /// <summary>Wake nearby water + gravity + grass after any world change (block placed/removed).</summary>
        public void OnBlockChanged(int x, int y, int z)
        {
            _fluid.OnBlockChanged(x, y, z);
            _gravity.OnBlockChanged(x, y, z);
            _grass.OnBlockChanged(x, y, z);
        }

        /// <summary>Advance the simulation using real frame time, firing fixed 20 TPS tick steps.</summary>
        public void Tick(float deltaSeconds)
        {
            _gravity.UpdateFalling(deltaSeconds);
            _accumulator += deltaSeconds;
            if (_accumulator < TickIntervalSeconds)
            {
                return;
            }

            _accumulator = Math.Min(_accumulator, 1.0); // don't spiral after a long hitch
            int steps = 0;
            while (_accumulator >= TickIntervalSeconds && steps < 10)
            {
                TickOnce();
                _accumulator -= TickIntervalSeconds;
                steps++;
            }
        }

        private void TickOnce()
        {
            _tick++;
            _due.Clear();

            foreach (var (chunkKey, bucket) in _pending)
            {
                foreach (var (cell, due) in bucket)
                {
                    if (due <= _tick)
                    {
                        _due.Add(cell);
                    }
                }
            }

            if (_due.Count == 0)
            {
                return;
            }

            int budget = Math.Min(MaxUpdatesPerTick, _due.Count);
            int processed = 0;
            for (int i = 0; i < _due.Count && processed < budget; i++)
            {
                var (x, y, z) = _due[i];
            var key = new ChunkCoordinates(ChunkManager.LayerForWorldY(y),
                (int)Math.Floor(x / (double)ChunkManager.ChunkSize), (int)Math.Floor(z / (double)ChunkManager.ChunkSize));
                if (_pending.TryGetValue(key, out var bucket) && bucket.Remove((x, y, z)))
                {
                    _gravity.TickBlock(x, y, z);
                    _fluid.TickBlock(x, y, z);
                    _grass.TickBlock(x, y, z);
                    processed++;
                }
            }

            if (processed > 0)
            {
                _meshScheduler.Update();
            }
        }
    }
}
