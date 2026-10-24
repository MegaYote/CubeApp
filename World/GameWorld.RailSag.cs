using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Cubuild
{
    /// <summary>
    /// Hanging rail track: when a rail loses the block beneath it, it does not break and it does
    /// not float. It hinges to whatever rail it connects to, and a run of them droops and gently
    /// sways downward - a rope bridge / chain-of-hinged-bricks, up/down only, never sideways.
    ///
    /// The shape is a hanging run measured out from its anchors: an anchored cell (a solid block
    /// directly under it) stays put, and every other rail sinks by a curve of its cell-distance to
    /// the nearest anchor, capped at <see cref="RailSag.MaxDrop"/>. A span between two anchors sags
    /// symmetrically in the middle like a rope bridge; a run hanging off a single anchor keeps
    /// dropping the further it gets, and just dangles once it bottoms out.
    ///
    /// A run may only hang so far, though: a rail whose cell-distance from the nearest anchor passes
    /// <see cref="RailMaxUnsupportedRun"/> (8) is past its hold and breaks off, dropping like any
    /// mined rail. So a span tops out at 8 cells either side of its anchors, and a line torn from
    /// its last support comes apart from the far end inward. A run with no anchor at all is past the
    /// cap everywhere and so breaks entirely. (This only fires on a run we can fully see - one that
    /// reaches into an unloaded chunk is left alone until the chunk returns.)
    ///
    /// The solve runs only when something changed (a block edit near rails, or a chunk loading in
    /// with rails in it). Between solves the stored value EASES toward its target with a small
    /// spring, so knocking a support out makes the track visibly drop and swing into its new shape.
    ///
    /// Storage: the target/current/velocity live here (runtime-only), and the current value is
    /// mirrored - quantized - into the chunk's RawRailDroop so the mesh workers can read it.
    /// </summary>
    public partial class GameWorld
    {
        // ---- tuning ----------------------------------------------------------

        /// <summary>How fast the droop grows with distance from an anchor, in blocks per cell^2.
        /// Drop = step * distance^2, capped at MaxDrop: a rail one cell from support barely dips,
        /// a long span sags hard and bottoms out. Sets how "heavy" the track feels.</summary>
        private const double RailSagSagStep = 0.08;

        /// <summary>Largest rail component we will solve in one go; beyond this the rest is left
        /// alone rather than letting one enormous track hitch the frame.</summary>
        private const int RailSagMaxComponent = 1500;

        /// <summary>Longest a run may hang from its nearest support, in rail cells. A cell whose
        /// cell-distance to the closest anchored rail exceeds this breaks off - exactly as if the
        /// far end of the line were cut - so track can never dangle further than this out from a
        /// support (8 cells either side of a span). Enforced only on a run wholly inside loaded
        /// chunks: a rail near a streaming boundary must never break just because its support sits
        /// in a neighbour that is not loaded yet.</summary>
        private const int RailMaxUnsupportedRun = 8;

        /// <summary>Spring the eased value toward its target. Underdamped on purpose: a little
        /// overshoot is the "slight physics" - the track drops, bounces, and settles.</summary>
        private const double RailSagStiffness = 55.0;
        private const double RailSagDamping = 7.0;

        /// <summary>Below this the cell counts as settled and stops ticking/re-meshing.</summary>
        private const double RailSagSettleEps = 0.003;

        // ---- state -----------------------------------------------------------

        private sealed class RailSagCell
        {
            public double Target;
            public double Pos;
            public double Vel;
        }

        private readonly Dictionary<(int X, int Y, int Z), RailSagCell> _railSag = new();
        // Seeds that still need (re)solving. Written from the main thread (edits) and drained
        // there too; a concurrent queue keeps it safe if a stray edit ever arrives off-thread.
        private readonly ConcurrentQueue<(int X, int Y, int Z)> _railSagDirty = new();
        // Chunks that just loaded with rails in them; scanned on the main thread (the load itself
        // happens on a gen worker, so all we do there is enqueue the chunk).
        private readonly ConcurrentQueue<Chunk> _railSagPendingChunks = new();
        // Generated cliff springs are activated only after the chunk and any save overlay are
        // published. The callback runs on a gen worker, so it enqueues chunks for the sim thread.
        private readonly ConcurrentQueue<Chunk> _worldgenFluidPendingChunks = new();
        private float _railSagPruneTimer;
        // Cells flagged this solve as hanging past RailMaxUnsupportedRun. Broken after the solve
        // finishes, so component data is never mutated while it is still being gathered/relaxed.
        private readonly List<(int X, int Y, int Z)> _railSagBreak = new();

        /// <summary>Called from ChunkManager when a chunk is created: we scan it for rails later,
        /// on the sim thread. Runs on a gen worker, so it must not touch the solver state.</summary>
        private void OnChunkLoadedForRailSag(Chunk chunk)
        {
            _railSagPendingChunks.Enqueue(chunk);
            if (chunk.WorldgenFluidSources.Count > 0)
                _worldgenFluidPendingChunks.Enqueue(chunk);
        }

        /// <summary>Wake only the explicitly generated cliff springs, never every naturally
        /// still-water block (oceans and sealed lake-caves are intentionally dormant).</summary>
        private void WakeWorldgenFluidSources()
        {
            if (BlockTicks == null) return;
            int waterId = BlockRegistry.GetId("water");
            int lavaId = BlockRegistry.GetId("lava");
            while (_worldgenFluidPendingChunks.TryDequeue(out var chunk))
            {
                foreach (var (x, y, z) in chunk.WorldgenFluidSources)
                {
                    // A saved chunk snapshot may have replaced the freshly generated outlet.
                    // Revalidate live data before scheduling so the save always remains authoritative.
                    if (!Chunks.TryGetLoadedBlock(x, y, z, out int id) || (id != waterId && id != lavaId))
                        continue;
                    BlockTicks.OnBlockChanged(x, y, z);
                }
            }
        }

        /// <summary>Mark the rail cells around an edit for a re-solve. Cheap: only the 27 cells
        /// that could possibly be affected (the changed cell, the one above whose SUPPORT changed,
        /// and the horizontal neighbours / steps that link to it).</summary>
        private void MarkRailSagAround(int x, int y, int z)
        {
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        int cx = x + dx, cy = y + dy, cz = z + dz;
                        if (Chunks.TryGetLoadedBlock(cx, cy, cz, out int id) && id == RailShape.RailId)
                            _railSagDirty.Enqueue((cx, cy, cz));
                    }
        }

        private bool IsRailCell(int x, int y, int z)
            => Chunks.TryGetLoadedBlock(x, y, z, out int id) && id == RailShape.RailId;

        /// <summary>Rails that link to (x,y,z): the four horizontal neighbours, each also sampled
        /// one block up and one block down (a step is a link). Mirrors CartEntity's connection test.</summary>
        private IEnumerable<(int X, int Y, int Z)> RailNeighbours(int x, int y, int z)
        {
            for (int i = 0; i < 4; i++)
            {
                int nx = x + RailShape.DirX[i], nz = z + RailShape.DirZ[i];
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = y + dy;
                    if (IsRailCell(nx, ny, nz)) yield return (nx, ny, nz);
                }
            }
        }

        /// <summary>A rail with a solid block straight under it does not move; it is the anchor the
        /// rest of the track hangs from.</summary>
        private bool RailIsAnchored(int x, int y, int z)
            => Chunks.TryGetLoadedBlock(x, y - 1, z, out int below)
               && BlockRegistry.IsSolid(below) && !BlockRegistry.IsRail(below);

        /// <summary>Per-tick rail sag: drain pending work, re-solve what changed, then ease every
        /// tracked cell toward its target and mirror the result into the chunk droop bytes.</summary>
        private void UpdateRailSag(float deltaSeconds)
        {
            WakeWorldgenFluidSources();

            // 1. Any chunk that just streamed in with rails: seed a solve for its rail cells.
            while (_railSagPendingChunks.TryDequeue(out var chunk))
                SeedRailSagFromChunk(chunk);

            // 2. Re-solve every dirty region, then break off any track hanging past the length cap.
            if (!_railSagDirty.IsEmpty)
                SolveRailSagRegions();
            ApplyRailLengthLimit();

            if (_railSag.Count == 0) return;

            // 3. Ease toward targets and write bytes. Clamp dt so a frame hitch can't launch the
            //    spring; the spring only ever moves values we already hold.
            double dt = Math.Min(deltaSeconds, 0.1);
            if (dt > 0)
            {
                foreach (var kv in _railSag)
                {
                    var (x, y, z) = kv.Key;
                    var cell = kv.Value;
                    if (cell.Pos == cell.Target && cell.Vel == 0.0) continue;   // settled

                    double accel = RailSagStiffness * (cell.Target - cell.Pos) - RailSagDamping * cell.Vel;
                    cell.Vel += accel * dt;
                    cell.Pos += cell.Vel * dt;

                    if (Math.Abs(cell.Pos - cell.Target) < RailSagSettleEps && Math.Abs(cell.Vel) < 0.02)
                    {
                        cell.Pos = cell.Target;
                        cell.Vel = 0.0;
                    }

                    WriteRailDroop(x, y, z, cell.Pos);
                }
            }

            // 4. Occasionally drop cells that are no longer rails (or whose chunk unloaded).
            _railSagPruneTimer += deltaSeconds;
            if (_railSagPruneTimer >= 1.0f)
            {
                _railSagPruneTimer = 0f;
                PruneRailSag();
            }
        }

        private void SeedRailSagFromChunk(Chunk chunk)
        {
            byte[] raw = chunk.RawBlocks;
            int railId = RailShape.RailId;
            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i] != railId) continue;
                int y = i % chunk.Height;
                int xz = i / chunk.Height;
                int z = xz % chunk.Depth;
                int x = xz / chunk.Depth;
                if (chunk.GetRailDroop(x, y, z) != 0) continue;   // already solved (e.g. re-load)
                _railSagDirty.Enqueue((chunk.OriginX + x, chunk.OriginY + y, chunk.OriginZ + z));
            }
        }

        private void SolveRailSagRegions()
        {
            var seeds = new List<(int X, int Y, int Z)>();
            while (_railSagDirty.TryDequeue(out var seed)) seeds.Add(seed);

            var seen = new HashSet<(int X, int Y, int Z)>();
            var queue = new Queue<(int X, int Y, int Z)>();
            foreach (var seed in seeds)
            {
                if (seen.Contains(seed) || !IsRailCell(seed.X, seed.Y, seed.Z)) continue;

                // Gather the connected component (loaded cells only; an unloaded neighbour reads
                // as a free end, which is exactly how a rail dangling into an unloaded chunk
                // should behave until that chunk returns).
                var comp = new List<(int X, int Y, int Z)>();
                bool open = false;
                queue.Clear();
                queue.Enqueue(seed);
                seen.Add(seed);
                while (queue.Count > 0 && comp.Count < RailSagMaxComponent)
                {
                    var c = queue.Dequeue();
                    comp.Add(c);
                    // If a horizontal neighbour column is unloaded, this run continues into terrain
                    // we cannot see, so its true distance to a support is unknowable and the length
                    // limit must not fire on it.
                    if (!open)
                        for (int i = 0; i < 4; i++)
                            if (!ColumnLoaded(c.X + RailShape.DirX[i], c.Y, c.Z + RailShape.DirZ[i]))
                            { open = true; break; }
                    foreach (var n in RailNeighbours(c.X, c.Y, c.Z))
                    {
                        if (seen.Contains(n)) continue;
                        seen.Add(n);
                        queue.Enqueue(n);
                    }
                }
                // A component we had to truncate is open by definition - we never saw all of it.
                if (comp.Count >= RailSagMaxComponent) open = true;

                RelaxRailSagComponent(comp, enforceLimit: !open);
            }
        }

        /// <summary>
        /// Set one connected run's hang from how far each cell is from the nearest anchor. A
        /// breadth-first sweep out from every anchored cell gives that distance; the droop is a
        /// quadratic of it (clamped), so a two-anchor span sags in the middle while a single-anchor
        /// tail keeps sinking to the max. Cells with no anchor anywhere simply hang. When
        /// <paramref name="enforceLimit"/> is set, any cell hanging past <see cref="RailMaxUnsupportedRun"/>
        /// is flagged to break after the solve.
        /// </summary>
        private void RelaxRailSagComponent(List<(int X, int Y, int Z)> comp, bool enforceLimit)
        {
            int n = comp.Count;
            if (n == 0) return;

            var index = new Dictionary<(int X, int Y, int Z), int>(n);
            for (int i = 0; i < n; i++) index[comp[i]] = i;

            var anchored = new bool[n];
            var adj = new int[n][];
            for (int i = 0; i < n; i++)
            {
                anchored[i] = RailIsAnchored(comp[i].X, comp[i].Y, comp[i].Z);
                // Precompute each cell's in-component neighbours so the sweeps stay cheap.
                var list = new List<int>(4);
                foreach (var nb in RailNeighbours(comp[i].X, comp[i].Y, comp[i].Z))
                    if (index.TryGetValue(nb, out int j)) list.Add(j);
                adj[i] = list.ToArray();
            }

            // Multi-source BFS from every anchor: dist[i] = cells to the nearest support.
            var dist = new int[n];
            var queue = new Queue<int>();
            for (int i = 0; i < n; i++)
            {
                if (anchored[i]) { dist[i] = 0; queue.Enqueue(i); }
                else dist[i] = int.MaxValue;
            }
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                foreach (int j in adj[i])
                    if (dist[j] == int.MaxValue) { dist[j] = dist[i] + 1; queue.Enqueue(j); }
            }

            for (int i = 0; i < n; i++)
            {
                // Length limit: a cell hanging further than the cap from EVERY support has lost its
                // hold and breaks off. dist == MaxValue means no support at all - past the cap too.
                if (enforceLimit && !anchored[i] && dist[i] > RailMaxUnsupportedRun)
                    _railSagBreak.Add(comp[i]);

                double target;
                if (anchored[i]) target = 0.0;
                else if (dist[i] == int.MaxValue) target = RailSag.MaxDrop;   // nothing to hang from
                else { double d = dist[i]; target = Math.Min(RailSag.MaxDrop, RailSagSagStep * d * d); }

                var key = comp[i];
                if (!_railSag.TryGetValue(key, out var cell))
                {
                    var (x, y, z) = key;
                    double start = 0;
                    if (Chunks.TryGetLoadedChunkAndDrop(x, y, z, out byte existing))
                        start = RailSag.Dequantize(existing);
                    cell = new RailSagCell { Pos = start, Target = start };
                    _railSag[key] = cell;
                }
                cell.Target = target;
            }
        }

        private void WriteRailDroop(int x, int y, int z, double drop)
        {
            if (!Chunks.TryGetLoadedChunkAndDrop(x, y, z, out _)) return;   // chunk gone
            byte q = RailSag.Quantize(drop);
            if (!Chunks.TrySetRailDroop(x, y, z, q)) return;
            Mesher.RequestImmediateRemesh(new ChunkCoordinates(
                ChunkManager.LayerForWorldY(y), WorldToChunkCoord(x), WorldToChunkCoord(z)));
        }

        /// <summary>Breaks off every rail flagged by the last solve as hanging past the length cap.
        /// Breaking (rather than silently deleting) keeps the normal side effects: the rail drops its
        /// item, neighbours re-resolve their shapes, and the sag re-solves from the shortened run.
        /// Run after the whole solve so component data is never mutated underneath the gather.</summary>
        private void ApplyRailLengthLimit()
        {
            if (_railSagBreak.Count == 0) return;
            var breaking = _railSagBreak.ToArray();
            _railSagBreak.Clear();
            foreach (var (x, y, z) in breaking)
                if (IsRailCell(x, y, z))
                    TryBreakBlockAt(x, y, z, out _);
        }

        /// <summary>True when the chunk holding (x,y,z) is loaded. Tells an OPEN run (one whose end
        /// we cannot see) from a fully-loaded one before enforcing the length cap.</summary>
        private bool ColumnLoaded(int x, int y, int z)
            => Chunks.TryGetLoadedChunk(
                   new ChunkCoordinates(ChunkManager.LayerForWorldY(y), WorldToChunkCoord(x), WorldToChunkCoord(z)),
                   out _);

        private void PruneRailSag()
        {
            List<(int X, int Y, int Z)>? dead = null;
            foreach (var key in _railSag.Keys)
            {
                if (IsRailCell(key.X, key.Y, key.Z)) continue;
                (dead ??= new List<(int, int, int)>()).Add(key);
            }
            if (dead == null) return;
            foreach (var key in dead) _railSag.Remove(key);
        }
    }
}
