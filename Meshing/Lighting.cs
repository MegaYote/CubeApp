using System;
using System.Collections.Generic;

namespace CubeApp
{
    /// <summary>
    /// Computes per-block light levels on a discrete 0..15 scale for a region of chunks.
    ///
    ///  - TWO light channels per cell: daylight (skylight) and emitted light (torches, glow).
    ///    The value used for rendering is the brighter of the two.
    ///  - Per-block LIGHT OPACITY drives attenuation (not a binary opaque bool): air lets light
    ///    through at a cost of 1, water costs 3, leaves 1, glass 0, opaque blocks fully block.
    ///  - A per-column HEIGHT MAP (topmost light-blocking block) decides which cells see the sky:
    ///    anything at or above it gets full daylight, everything below is lit by the flood.
    ///  - Propagation is a 6-direction breadth-first flood (light spreads upward too). A cell's
    ///    light = max over its 6 neighbours of (neighbourLight - thisCellOpacity), with the sky
    ///    column walk as the starting daylight source. Per-face flat sampling, no smooth lighting.
    /// </summary>
    public sealed class ChunkLighting
    {
        public const int MaxLight = 15;

        // Light spreads in ALL 6 axis directions (upward included).
        private static readonly (int dx, int dy, int dz)[] Dirs =
        {
            (1, 0, 0), (-1, 0, 0),
            (0, 1, 0), (0, -1, 0),
            (0, 0, 1), (0, 0, -1)
        };

        private readonly int minX;
        private readonly int minZ;
        private readonly int height;
        private readonly int dimX;
        private readonly int dimZ;
        private readonly byte[] opacity;   // per-cell light opacity (0..255)
        private readonly byte[] sky;       // per-cell sky light (0..15)
        private readonly byte[] block;     // per-cell block light (0..15)
        private readonly int[] heightMap;  // per column: topmost light-blocking cell (local Y)

        /// <summary>World Y of the region's origin. All chunks in a mesh region share a layer, so
        /// local light-index Y = worldY - OriginY.</summary>
        public int OriginY { get; }

        // Meshing runs on a small fixed set of worker threads and each builds at most one
        // ChunkLighting at a time, so the big scratch arrays (~590k cells for a 3x3-chunk region)
        // are pooled per thread instead of being reallocated (and GC'd) on every single remesh.
        [ThreadStatic] private static byte[]? _opacityPool;
        [ThreadStatic] private static byte[]? _skyPool;
        [ThreadStatic] private static byte[]? _blockPool;
        [ThreadStatic] private static int[]? _heightMapPool;
        [ThreadStatic] private static Queue<int>? _queuePool;

        public ChunkLighting(IReadOnlyDictionary<ChunkCoordinates, Chunk> chunks, int chunkSize, int chunkHeight)
        {
            if (chunks == null) throw new ArgumentNullException(nameof(chunks));

            int minChunkX = int.MaxValue, maxChunkX = int.MinValue;
            int minChunkZ = int.MaxValue, maxChunkZ = int.MinValue;
            int originY = 0;
            bool haveOrigin = false;
            foreach (var kv in chunks)
            {
                var c = kv.Key;
                if (c.X < minChunkX) minChunkX = c.X;
                if (c.X > maxChunkX) maxChunkX = c.X;
                if (c.Z < minChunkZ) minChunkZ = c.Z;
                if (c.Z > maxChunkZ) maxChunkZ = c.Z;
                if (!haveOrigin)
                {
                    originY = kv.Value.OriginY;
                    haveOrigin = true;
                }
            }
            OriginY = originY;

            if (chunks.Count == 0)
            {
                minChunkX = maxChunkX = 0;
                minChunkZ = maxChunkZ = 0;
            }

            height = chunkHeight;
            minX = minChunkX * chunkSize;
            minZ = minChunkZ * chunkSize;
            dimX = (maxChunkX - minChunkX + 1) * chunkSize;
            dimZ = (maxChunkZ - minChunkZ + 1) * chunkSize;

            int cells = dimX * height * dimZ;
            if (_opacityPool == null || _opacityPool.Length < cells)
            {
                _opacityPool = new byte[cells];
                _skyPool = new byte[cells];
                _blockPool = new byte[cells];
            }
            // The height map is sized by the XZ footprint only (no height term). It MUST be sized
            // independently of the byte-array pools: a short layer (deep=192) with a large XZ
            // region can have FEWER cells than a tall layer (ground=448) with a small region, so
            // the cells-based realloc would leave heightMap undersized -> IndexOutOfRange in
            // SeedSkyLight -> every deep-layer mesh fails -> the deep world vanishes.
            if (_heightMapPool == null || _heightMapPool.Length < dimX * dimZ)
            {
                _heightMapPool = new int[dimX * dimZ];
            }
            opacity = _opacityPool;
            sky = _skyPool!;
            block = _blockPool!;
            heightMap = _heightMapPool!;

            // LAZY-BAND: with 448-tall chunks the region arrays are ~3M cells but only the terrain
            // band has blocks. Light can only travel MaxLight cells from a source, so everything
            // below the lowest solid and above the highest solid is either dark (below) or full
            // sky (above) - the flood fill only needs the occupied band +/- MaxLight.
            int regionTopSolidY = -1;
            int regionBottomSolidY = height - 1;
            {
                foreach (var kv in chunks)
                {
                    var c = kv.Value;
                    byte[] raw = c.RawBlocks;
                    for (int x = 0; x < chunkSize; x++)
                    {
                        for (int z = 0; z < chunkSize; z++)
                        {
                            int src = (x * chunkSize + z) * chunkHeight;
                            for (int y = 0; y < chunkHeight; y++)
                            {
                                if (raw[src + y] != 0)
                                {
                                    if (y < regionBottomSolidY) regionBottomSolidY = y;
                                    if (y > regionTopSolidY) regionTopSolidY = y;
                                }
                            }
                        }
                    }
                }
                if (regionTopSolidY < 0)
                {
                    regionTopSolidY = height - 1;
                    regionBottomSolidY = 0;
                }
            }

            // The flood can carry light at most MaxLight cells vertically beyond the band, but the
            // seed walk already lights everything above the height map to 15 - so only a small
            // margin above the top solid needs to be filled (sky margin) and nothing below the
            // bottom (no blocks, and canSeeSky is false below the height map).
            int bandLo = Math.Max(0, regionBottomSolidY - MaxLight);
            int bandHi = Math.Min(height - 1, regionTopSolidY + 1);

            // Clear ONLY the band, column by column. The arrays are column-major
            // (Index = (lx*dimZ + lz)*height + y), so the band is NOT contiguous - a single
            // Array.Clear over "bandCells" cells would hit the wrong cells and leave the real
            // band holding STALE pooled values from a previous remesh. Stale light then blocks
            // the flood fill (light[nidx] >= next) and shows as glitchy cave lighting.
            // Clearing per column is a handful of small clears and keeps the pools honest.
            if (bandLo <= bandHi)
            {
                int span = bandHi - bandLo + 1;
                for (int lx = 0; lx < dimX; lx++)
                {
                    for (int lz = 0; lz < dimZ; lz++)
                    {
                        int colBase = Index(lx, 0, lz) + bandLo;
                        Array.Clear(opacity, colBase, span);
                        Array.Clear(sky, colBase, span);
                        Array.Clear(block, colBase, span);
                    }
                }
            }

            var queue = _queuePool ??= new Queue<int>();
            queue.Clear();

            // Fill per-block light opacity + the per-column height map (topmost light-blocking
            // cell), straight from each chunk's raw block bytes, only within the band.
            Array.Clear(heightMap, 0, heightMap.Length);
            int fillTop = Math.Min(regionTopSolidY + 1, bandHi);
            foreach (var kv in chunks)
            {
                var c = kv.Value;
                byte[] raw = c.RawBlocks;
                int baseLX = kv.Key.X * chunkSize - minX;
                int baseLZ = kv.Key.Z * chunkSize - minZ;
                for (int x = 0; x < chunkSize; x++)
                {
                    for (int z = 0; z < chunkSize; z++)
                    {
                        int src = (x * chunkSize + z) * chunkHeight;
                        int dst = Index(baseLX + x, 0, baseLZ + z);
                        int lxR = baseLX + x;
                        int lzR = baseLZ + z;
                        int hm = -1;
                        for (int y = bandLo; y <= fillTop; y++)
                        {
                            int id = raw[src + y];
                            byte o = LightOpacity(id);
                            opacity[dst + y] = o;
                            if (o != 0 && y > hm) hm = y;
                            // Record block light emission (torches, glowstone, ...). Seeded into
                            // the queue AFTER the sky pass so the sky flood can't consume them.
                            int em = BlockRegistry.LightEmissionOf(id);
                            if (em > 0) block[dst + y] = (byte)Math.Min(MaxLight, em);
                        }
                        heightMap[lxR * dimZ + lzR] = hm;
                    }
                }
            }

            SeedSkyLight(queue, bandLo, bandHi, fillTop);
            PropagateSky(queue);

            // Seed block-light emissions into a fresh queue AFTER the sky pass, then flood them.
            queue.Clear();
            for (int lx = 0; lx < dimX; lx++)
            {
                for (int lz = 0; lz < dimZ; lz++)
                {
                    int colBase = Index(lx, 0, lz);
                    for (int y = bandLo; y <= bandHi; y++)
                    {
                        int idx = colBase + y;
                        if (block[idx] > 0)
                        {
                            queue.Enqueue(idx);
                        }
                    }
                }
            }
            PropagateBlock(queue);
        }

        // y is the fastest-varying index so vertical columns are contiguous (matches Chunk's
        // internal layout, which makes the occupancy fill and sky-light seeding sequential).
        private int Index(int lx, int y, int lz)
        {
            return (lx * dimZ + lz) * height + y;
        }

        /// <summary>Light opacity for a block id. Air=0, water=3, leaves=1, glass=0,
        /// partial shapes=0, everything opaque=255. During propagation an opacity below 1 is
        /// treated as 1 (so light loses one level per air cell).</summary>
        private static readonly int _idWater = BlockRegistry.GetId("water");
        private static readonly int _idLeaves = BlockRegistry.GetId("leaves");

        private static byte LightOpacity(int id)
        {
            if (id == BlockRegistry.AirId) return 0;
            if (id == _idWater) return 3;
            if (id == _idLeaves) return 1;
            if (!BlockRegistry.IsOpaque(id)) return 0;
            if (BlockRegistry.IsCross(id)) return 0;
            if (BlockRegistry.IsPartialShape(id)) return 0;
            if (BlockRegistry.IsTranslucent(id)) return 0;
            return 255;
        }

        // Daylight seeding: walk each column down from just above the highest solid. Every cell at
        // or above the column's height map sees the sky -> full daylight (reduced by the night dim
        // at dusk). Cells below that are lit by the 6-way flood from those sources.
        private void SeedSkyLight(Queue<int> queue, int bandLo, int bandHi, int fillTop)
        {
            int startY = Math.Max(bandLo, Math.Min(fillTop + 1, bandHi));
            byte skySeed = (byte)Math.Max(0, MaxLight - NightDimLevel);
            for (int lx = 0; lx < dimX; lx++)
            {
                for (int lz = 0; lz < dimZ; lz++)
                {
                    int hm = heightMap[lx * dimZ + lz]; // x-major, matches the fill
                    int colBase = Index(lx, 0, lz);
                    for (int y = startY; y >= bandLo; y--)
                    {
                        if (y > hm)
                        {
                            sky[colBase + y] = skySeed;
                            queue.Enqueue(colBase + y);
                        }
                        else
                        {
                            break;
                        }
                    }
                }
            }
        }

        // Daylight flood: a cell's raw light is max(neighbours) - thisCellOpacity (opacity clamped
        // to >= 1). 6 directions.
        private void PropagateSky(Queue<int> queue)
        {
            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                int level = sky[idx];
                if (level <= 1) continue;

                Decode(idx, out int lx, out int y, out int lz);
                byte next = (byte)(level - 1);

                foreach (var (dx, dy, dz) in Dirs)
                {
                    int nlx = lx + dx;
                    int ny = y + dy;
                    int nlz = lz + dz;
                    if (nlx < 0 || nlx >= dimX || nlz < 0 || nlz >= dimZ || ny < 0 || ny >= height) continue;

                    int nidx = Index(nlx, ny, nlz);
                    byte o = opacity[nidx];
                    if (o >= 255) continue;              // fully opaque: no light passes
                    byte att = o < 1 ? (byte)1 : o;      // air/glass cost 1, water 3, leaves 1
                    if (next < att) continue;            // not enough light left to enter
                    byte cand = (byte)(level - att);
                    if (cand > sky[nidx])
                    {
                        sky[nidx] = cand;
                        queue.Enqueue(nidx);
                    }
                }
            }
        }

        // Emitted-light (torch/glow) propagation: seeded from LightEmission blocks, then the same
        // 6-way max(neighbours) - thisCellOpacity flood (opacity clamped to >= 1, fully opaque
        // blocks stop it). Emitters themselves always shine.
        private void PropagateBlock(Queue<int> queue)
        {
            // Seed from light-emitting blocks in the band.
            for (int lx = 0; lx < dimX; lx++)
            {
                for (int lz = 0; lz < dimZ; lz++)
                {
                    int colBase = Index(lx, 0, lz);
                    for (int y = 0; y < height; y++)
                    {
                        // We don't store block ids here; only block-light emission matters.
                        // Block light is emitted by blocks in the region - but this region
                        // lighting pass only has opacity, not ids. Emission is 0 for all blocks
                        // until LightEmission is used, so the loop body stays a no-op seed.
                        int idx = colBase + y;
                        if (block[idx] > 0)
                        {
                            queue.Enqueue(idx);
                        }
                    }
                }
            }

            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                int level = block[idx];
                if (level <= 1) continue;

                Decode(idx, out int lx, out int y, out int lz);
                foreach (var (dx, dy, dz) in Dirs)
                {
                    int nlx = lx + dx;
                    int ny = y + dy;
                    int nlz = lz + dz;
                    if (nlx < 0 || nlx >= dimX || nlz < 0 || nlz >= dimZ || ny < 0 || ny >= height) continue;

                    int nidx = Index(nlx, ny, nlz);
                    byte o = opacity[nidx];
                    if (o >= 255) continue;
                    byte att = o < 1 ? (byte)1 : o;
                    if (level - 1 < att) continue;
                    byte cand = (byte)(level - att);
                    if (cand > block[nidx])
                    {
                        block[nidx] = cand;
                        queue.Enqueue(nidx);
                    }
                }
            }
        }

        private void Decode(int idx, out int lx, out int y, out int lz)
        {
            y = idx % height;
            int t = idx / height;
            lz = t % dimZ;
            lx = t / dimZ;
        }

        /// <summary>
        /// Combined light level (0..15) at the given world coordinates: max(daylight, emitted).
        /// Cells outside the computed region return full sky light if above the region's terrain,
        /// else 0.
        /// </summary>
        public int GetLight(int worldX, int y, int worldZ)
        {
            int lx = worldX - minX;
            int lz = worldZ - minZ;
            if (lx < 0 || lx >= dimX || lz < 0 || lz >= dimZ || y < 0 || y >= height)
            {
                // Outside the region: unknown terrain. Assume open sky above the region floor.
                return MaxLight;
            }

            int s = sky[Index(lx, y, lz)];
            int b = block[Index(lx, y, lz)];
            return s > b ? s : b;
        }

        /// <summary>
        /// Maps a discrete light level to a brightness multiplier. The curve is a smooth power law
        /// with a shallow low-end so dark caves read as genuinely dark while near-full light is
        /// bright: brightness = (light/15)^1.4 with a slight knee lifted by 0.04*light/15 so level
        /// 1 isn't pure black. Level 15 = 1.0, level 0 = 0.0 (deep caves are pitch black).
        ///
        /// When <see cref="Fullbright"/> is set (F6 debug/peek mode), every light level maps to
        /// 1.0 so the whole world renders as if fully lit - lets you see into the pitch-black
        /// deep without torches. The mesher bakes brightness per face, so toggling it must also
        /// flag all loaded chunks for remesh.
        /// </summary>
        public static bool Fullbright { get; set; }

        /// <summary>
        /// Night dim level (0..11): how much daylight is removed after the sun goes down. We bake
        /// light into meshes, so this lowers the DAYLIGHT SEED (15 - dim) and the flood fill
        /// propagates dimmer light everywhere. Changing it must re-mesh all loaded chunks.
        /// </summary>
        public static int NightDimLevel { get; set; }

        public static float Brightness(int lightLevel)
        {
            if (Fullbright) return 1.0f;
            int clamped = Math.Clamp(lightLevel, 0, MaxLight);
            float t = clamped / (float)MaxLight;
            // Power curve + a small linear knee: brighter near the top, dark near the bottom.
            return t * t * t * 0.55f + t * 0.45f;
        }

        /// <summary>
        /// Mob/entity brightness: a gentler falloff than the terrain curve so mobs don't go
        /// pitch-black in moderate shadow, with a small floor so they're dimly visible even in
        /// total darkness.
        /// </summary>
        public static float MobBrightness(int lightLevel)
        {
            if (Fullbright) return 1.0f;
            int clamped = Math.Clamp(lightLevel, 0, MaxLight);
            float t = clamped / (float)MaxLight;
            return t * t * 0.42f + t * 0.50f + 0.08f;
        }
    }
}
