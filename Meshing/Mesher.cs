using System;
using System.Collections.Generic;
using System.Numerics;

namespace Cubuild
{
    public sealed class Mesher
    {
        // Water AND lava are meshed by dedicated passes (sloped surfaces + stretched sides), so the
        // greedy cube pass must never emit faces owned by either fluid. Miss one and every surface
        // of that fluid is meshed twice: a full cube from the greedy pass plus the dedicated
        // surface, which for a flowing cell (surface below the top of its cube) leaves a leftover
        // full-height cube face poking up out of the lava. Neighbouring solid blocks still render
        // their faces toward a fluid through the normal RendersToward rules.
        private static readonly int WaterId = BlockRegistry.GetId("water");
        private static readonly int LavaId = BlockRegistry.GetId("lava");
        private static readonly int GrassId = BlockRegistry.GetId("grass");
        private static readonly int GrassSpreadingId = BlockRegistry.GetId("grass_spreading");
        private static readonly int LeavesId = BlockRegistry.GetId("leaves");
        private static readonly TextureRect WaterBaseTile = BlockRegistry.Get("water").AllTexture ?? default;
        private static readonly TextureRect LavaBaseTile = BlockRegistry.Get("lava").AllTexture ?? default;

        /// <summary>True for the fluids that own their geometry (see the note above).</summary>
        private static bool HasDedicatedPass(int id) => id == WaterId || id == LavaId;

        private static readonly (int dx, int dy, int dz)[] FaceOffsets =
        {
            (0, 0, -1), // back
            (0, 0, 1),  // front
            (0, -1, 0), // bottom
            (0, 1, 0),  // top
            (1, 0, 0),  // right
            (-1, 0, 0)  // left
        };

        private static readonly Point3D[][] FaceVertices =
        {
            new[] { new Point3D(0, 0, 0), new Point3D(1, 0, 0), new Point3D(1, 1, 0), new Point3D(0, 1, 0) },
            new[] { new Point3D(1, 0, 1), new Point3D(0, 0, 1), new Point3D(0, 1, 1), new Point3D(1, 1, 1) },
            new[] { new Point3D(0, 0, 0), new Point3D(1, 0, 0), new Point3D(1, 0, 1), new Point3D(0, 0, 1) },
            new[] { new Point3D(0, 1, 0), new Point3D(0, 1, 1), new Point3D(1, 1, 1), new Point3D(1, 1, 0) },
            new[] { new Point3D(1, 0, 1), new Point3D(1, 0, 0), new Point3D(1, 1, 0), new Point3D(1, 1, 1) },
            new[] { new Point3D(0, 0, 0), new Point3D(0, 0, 1), new Point3D(0, 1, 1), new Point3D(0, 1, 0) }
        };

        public static IReadOnlyList<MeshFace> GenerateMesh(Chunk chunk)
        {
            return GenerateMesh(new[] { chunk });
        }

        private static int FloorDiv(int value, int divisor)
        {
            int result = value / divisor;
            if ((value ^ divisor) < 0 && value % divisor != 0)
            {
                result--;
            }

            return result;
        }

        // Returns the 3rd-quartile height among the chunk's columns: the highest local Y that at
        // least 75% of columns reach. Using this for occlusion instead of the absolute max means a
        // single tall spike (tree, tower, lone peak) can't hide the whole chunk behind it, while
        // real ridges/mountains still occlude. Columns with no blocks (top=-1) count as -1.
        private static int RepresentativeColumnHeight(int[] columnTop)
        {
            var sorted = (int[])columnTop.Clone();
            Array.Sort(sorted);
            int idx = (int)Math.Floor(sorted.Length * 0.75);
            if (idx >= sorted.Length) idx = sorted.Length - 1;
            return sorted[idx];
        }

        public static IReadOnlyList<MeshFace> GenerateMesh(IEnumerable<Chunk> chunks)
            => GenerateMesh(chunks, out _);

        /// <summary>Builds a full-detail mesh with an optional biome-id lookup for face tints.</summary>
        public static IReadOnlyList<MeshFace> GenerateMesh(IEnumerable<Chunk> chunks, Func<int, int, string>? biomeIdAt)
            => GenerateMesh(chunks, biomeIdAt, out _);

        public static IReadOnlyList<MeshFace> GenerateMesh(IEnumerable<Chunk> chunks, out IReadOnlyList<MeshFace>? surfaceLod)
            => GenerateMesh(chunks, null, out surfaceLod);

        /// <summary>Builds the full gameplay mesh and an optional far-field surface LOD derived
        /// from this exact live chunk data. The LOD is only a visual approximation; full meshes
        /// remain authoritative for nearby rendering and gameplay.</summary>
        public static IReadOnlyList<MeshFace> GenerateMesh(IEnumerable<Chunk> chunks,
            Func<int, int, string>? biomeIdAt, out IReadOnlyList<MeshFace>? surfaceLod)
        {
            surfaceLod = null;
            var mesh = new List<MeshFace>();
            var hillsBiomeColumns = biomeIdAt == null ? null : new Dictionary<(int X, int Z), bool>();
            bool IsHillsGreenTint(int blockId, int worldX, int worldZ)
            {
                if ((blockId != GrassId && blockId != GrassSpreadingId && blockId != LeavesId)
                    || biomeIdAt == null || hillsBiomeColumns == null)
                    return false;

                var column = (worldX, worldZ);
                if (!hillsBiomeColumns.TryGetValue(column, out bool isHills))
                {
                    isHills = string.Equals(biomeIdAt(worldX, worldZ), "hills", StringComparison.OrdinalIgnoreCase);
                    hillsBiomeColumns.Add(column, isHills);
                }
                return isHills;
            }

            var chunkList = new List<Chunk>(chunks);
            if (chunkList.Count == 0)
            {
                return mesh;
            }

            var chunkLookup = new Dictionary<ChunkCoordinates, Chunk>();

            foreach (var c in chunkList)
            {
                int layer = ChunkManager.LayerForWorldY(c.OriginY);
                var chunkCoord = new ChunkCoordinates(layer, FloorDiv(c.OriginX, ChunkManager.ChunkSize), FloorDiv(c.OriginZ, ChunkManager.ChunkSize));
                chunkLookup[chunkCoord] = c;
            }

            // The first chunk is the target to mesh; additional chunks are neighbors used only for border occlusion checks.
            var chunk = chunkList[0];
            int width = chunk.Width;
            int height = chunk.Height;
            int depth = chunk.Depth;
            int targetLayer = ChunkManager.LayerForWorldY(chunk.OriginY);

            // Flood-fill light levels (0..15) across the target chunk and its SAME-LAYER neighbours
            // so each face can be shaded by how much light reaches the empty block it's exposed to.
            // Vertical (other-layer) neighbours are deliberately excluded from lighting: chunks in
            // different layers have different heights/origins, which the region's fixed-height
            // arrays can't represent. They're only used for OCCLUSION at the layer seam.
            var lightingChunks = new Dictionary<ChunkCoordinates, Chunk>();
            foreach (var kv in chunkLookup)
            {
                if (kv.Key.Layer == targetLayer) lightingChunks[kv.Key] = kv.Value;
            }
            var lighting = new ChunkLighting(lightingChunks, ChunkManager.ChunkSize, height);

            // Direct block storage for the target chunk and its +X/+Z neighbours. All mask samples
            // hit the target chunk except the final slice's B sample, which crosses into exactly
            // one of these neighbours - so the hot loop never touches the dictionary.
            byte[] raw = chunk.RawBlocks;
            int targetCX = FloorDiv(chunk.OriginX, ChunkManager.ChunkSize);
            int targetCZ = FloorDiv(chunk.OriginZ, ChunkManager.ChunkSize);
            chunkLookup.TryGetValue(new ChunkCoordinates(targetLayer, targetCX + 1, targetCZ), out var neighborPosX);
            chunkLookup.TryGetValue(new ChunkCoordinates(targetLayer, targetCX, targetCZ + 1), out var neighborPosZ);
            // The layer ABOVE this chunk (same X/Z): its bottom row is the block directly above the
            // target's top row. Used for Y-axis occlusion at the layer seam (deep top vs ground
            // bottom at world -65/-64, ground top vs sky bottom at 383/384).
            chunkLookup.TryGetValue(new ChunkCoordinates(targetLayer + 1, targetCX, targetCZ), out var neighborPosY);
            byte[]? rawPosY = neighborPosY?.RawBlocks;
            byte[]? rawPosX = neighborPosX?.RawBlocks;
            byte[]? rawPosZ = neighborPosZ?.RawBlocks;

            int[] dims = new[] { width, height, depth };

            // Occupied Y rows of the target chunk (any non-air block). Chunks are 448 tall but the
            // terrain band only occupies a slice of that, so most Y rows are pure air. The greedy
            // loops below skip those rows entirely - faces only ever exist where a block is. A
            // tower built up to y=430 is naturally included (we scan the LIVE blocks, not a cached
            // bound), so "players build up high" needs no special handling.
            bool[] blockAtY = new bool[height];
            bool anyBlock = false;
            int topSolidLocalY = -1;
            // Per-column solid height (world Y of the highest block in each 1x1 column). Used to
            // compute a REPRESENTATIVE chunk height for occlusion culling. The plain max is too
            // aggressive: one lone peak or tree on a chunk's far edge makes the whole 16x16 chunk
            // occlude a huge area behind it. A majority height (where most columns are solid) keeps
            // the cull effective for real mountains while valleys/trees stop hiding things.
            int[] columnTop = new int[width * depth];
            byte[] columnTopBlock = LodSettings.SurfaceLodEnabled ? new byte[width * depth] : Array.Empty<byte>();
            int stride = depth * height;
            for (int x = 0; x < width; x++)
            {
                int colBase = x * stride;
                for (int z = 0; z < depth; z++)
                {
                    int top = -1;
                    for (int y = height - 1; y >= 0; y--)
                    {
                        byte id = raw[colBase + z * height + y];
                        if (id != BlockRegistry.AirId)
                        {
                            top = y;
                            if (LodSettings.SurfaceLodEnabled) columnTopBlock[x * depth + z] = id;
                            break;
                        }
                    }
                    columnTop[x * depth + z] = top;
                    if (top > topSolidLocalY) topSolidLocalY = top;
                }
            }
            for (int y = 0; y < height; y++)
            {
                bool has = false;
                for (int c = 0; c < columnTop.Length; c++)
                {
                    if (columnTop[c] >= y) { has = true; break; }
                }
                blockAtY[y] = has;
                anyBlock |= has;
            }

            // Heightmap occlusion data: a conservative representative height. Use the 3rd-quartile
            // column height (>=75% of columns reach it) instead of the absolute max, so a lone tall
            // spike doesn't cull the view. The mesh worker owns this chunk exclusively, so writing
            // here is race-free (same lock as MeshFaces).
            int repLocalY = RepresentativeColumnHeight(columnTop);
            chunk.TopSolidY = repLocalY >= 0 ? chunk.OriginY + repLocalY : chunk.OriginY - 1;
            chunk.MaxSolidY = topSolidLocalY >= 0 ? chunk.OriginY + topSolidLocalY : chunk.OriginY - 1;

            for (int d = 0; d < 3; d++)
            {
                int u = (d + 1) % 3;
                int v = (d + 2) % 3;

                int dimD = dims[d];
                int dimU = dims[u];
                int dimV = dims[v];

                // Flat integer mask: 0 = no face, otherwise bit0 = set, bits1-8 = block type,
                // Two integer masks per slice: a "+d" face owned by block A (at slice) facing B,
                // and a "-d" face owned by B (at slice+1) facing A. They're independent under
                // transparency: water|air emits water's +d face only; stone|water emits stone's
                // +d face only; stone|air emits stone's +d face; glass|stone emits glass's +d face,
                // etc. Each cell is 0 (no face) or bit-packed: bit0=set, bits1-8=block id,
                // bit9=positive normal, bits10-13=light, bit14=hills grass tint. Merge equality is a single int compare.
                var maskPos = new int[dimU * dimV];
                var maskNeg = new int[dimU * dimV];

                // Skip pure-air slices along the slice axis (d==1 = Y). A face between slice and
                // slice+1 needs a block in at least one of them; both air => nothing to emit.
                bool[]? sliceHasContent = null;
                if (d == 1) sliceHasContent = blockAtY;

                for (int slice = 0; slice < dimD; slice++)
                {
                    // Y-axis: skip both-air slices (fully safe - the Y axis never crosses into a
                    // neighbor chunk; the block above the world top is always air).
                    if (sliceHasContent != null && !sliceHasContent[slice]
                        && (slice + 1 >= dimD || !sliceHasContent[slice + 1]))
                    {
                        continue;
                    }

                    Array.Clear(maskPos, 0, maskPos.Length);
                    Array.Clear(maskNeg, 0, maskNeg.Length);

                    // Build the masks comparing slice and slice+1. A is always inside the target
                    // chunk; B only leaves it on the final slice (into +X/+Z neighbour, or open air
                    // above the world for the Y axis). Face light samples the adjacent empty cell the
                    // face is exposed to.
                    bool lastSlice = slice + 1 >= dimD;
                    if (d == 0)
                    {
                        // X slices: u = local Y, v = local Z.
                        for (int iu = 0; iu < dimU; iu++)
                        {
                            // Y-axis rows with no block in this chunk can't produce faces on the
                            // non-final slices (A and B are both air). The FINAL slice is kept in
                            // full so the provisional border face into the +X neighbour is preserved
                            // exactly (the neighbour re-emits it authoritatively when it meshes).
                            if (!lastSlice && !blockAtY[iu]) continue;
                            for (int jv = 0; jv < dimV; jv++)
                            {
                                int A = raw[(slice * depth + jv) * height + iu];
                                int B = lastSlice
                                    ? (rawPosX != null ? rawPosX[jv * height + iu] : BlockRegistry.AirId)
                                    : raw[((slice + 1) * depth + jv) * height + iu];
                                int cell = iu * dimV + jv;
                                if (!HasDedicatedPass(A) && !BlockRegistry.IsCross(A) && !BlockRegistry.IsPartialShape(A) && RendersToward(A, B)) maskPos[cell] = Pack(A, true, lighting.GetSkyLight(chunk.OriginX + slice + 1, iu, chunk.OriginZ + jv), lighting.GetBlockLight(chunk.OriginX + slice + 1, iu, chunk.OriginZ + jv), IsHillsGreenTint(A, chunk.OriginX + slice, chunk.OriginZ + jv));
                                if (!HasDedicatedPass(B) && !BlockRegistry.IsCross(B) && !BlockRegistry.IsPartialShape(B) && RendersToward(B, A)) maskNeg[cell] = Pack(B, false, lighting.GetSkyLight(chunk.OriginX + slice, iu, chunk.OriginZ + jv), lighting.GetBlockLight(chunk.OriginX + slice, iu, chunk.OriginZ + jv), IsHillsGreenTint(B, chunk.OriginX + slice + 1, chunk.OriginZ + jv));
                            }
                        }
                    }
                    else if (d == 1)
                    {
                        // Y slices: u = local Z, v = local X. B above the chunk's top row is the
                        // layer-above chunk's bottom row (same local X/Z) when that layer is loaded;
                        // otherwise air (top of the world / unloaded neighbour).
                        int aboveHeight = neighborPosY != null ? neighborPosY.Height : 0;
                        for (int iu = 0; iu < dimU; iu++)
                        {
                            for (int jv = 0; jv < dimV; jv++)
                            {
                                int baseIdx = (jv * depth + iu) * height + slice;
                                int A = raw[baseIdx];
                                int B = lastSlice
                                    ? (rawPosY != null ? rawPosY[(jv * depth + iu) * aboveHeight + 0] : BlockRegistry.AirId)
                                    : raw[baseIdx + 1];
                                int cell = iu * dimV + jv;
                                if (!HasDedicatedPass(A) && !BlockRegistry.IsCross(A) && !BlockRegistry.IsPartialShape(A) && RendersToward(A, B)) maskPos[cell] = Pack(A, true, lighting.GetSkyLight(chunk.OriginX + jv, slice + 1, chunk.OriginZ + iu), lighting.GetBlockLight(chunk.OriginX + jv, slice + 1, chunk.OriginZ + iu), IsHillsGreenTint(A, chunk.OriginX + jv, chunk.OriginZ + iu));
                                if (!HasDedicatedPass(B) && !BlockRegistry.IsCross(B) && !BlockRegistry.IsPartialShape(B) && RendersToward(B, A)) maskNeg[cell] = Pack(B, false, lighting.GetSkyLight(chunk.OriginX + jv, slice, chunk.OriginZ + iu), lighting.GetBlockLight(chunk.OriginX + jv, slice, chunk.OriginZ + iu), IsHillsGreenTint(B, chunk.OriginX + jv, chunk.OriginZ + iu));
                            }
                        }
                    }
                    else
                    {
                        // Z slices: u = local X, v = local Y.
                        for (int iu = 0; iu < dimU; iu++)
                        {
                            for (int jv = 0; jv < dimV; jv++)
                            {
                                if (!lastSlice && !blockAtY[jv]) continue;
                                int A = raw[(iu * depth + slice) * height + jv];
                                int B = lastSlice
                                    ? (rawPosZ != null ? rawPosZ[iu * depth * height + jv] : BlockRegistry.AirId)
                                    : raw[(iu * depth + slice + 1) * height + jv];
                                int cell = iu * dimV + jv;
                                if (!HasDedicatedPass(A) && !BlockRegistry.IsCross(A) && !BlockRegistry.IsPartialShape(A) && RendersToward(A, B)) maskPos[cell] = Pack(A, true, lighting.GetSkyLight(chunk.OriginX + iu, jv, chunk.OriginZ + slice + 1), lighting.GetBlockLight(chunk.OriginX + iu, jv, chunk.OriginZ + slice + 1), IsHillsGreenTint(A, chunk.OriginX + iu, chunk.OriginZ + slice));
                                if (!HasDedicatedPass(B) && !BlockRegistry.IsCross(B) && !BlockRegistry.IsPartialShape(B) && RendersToward(B, A)) maskNeg[cell] = Pack(B, false, lighting.GetSkyLight(chunk.OriginX + iu, jv, chunk.OriginZ + slice), lighting.GetBlockLight(chunk.OriginX + iu, jv, chunk.OriginZ + slice), IsHillsGreenTint(B, chunk.OriginX + iu, chunk.OriginZ + slice + 1));
                            }
                        }
                    }

                    // Greedy-merge each mask into rectangles (cells merge only on exact-pack equality).
                    EmitMergedFaces(maskPos, dimU, dimV, slice, d, chunk, mesh);
                    EmitMergedFaces(maskNeg, dimU, dimV, slice, d, chunk, mesh);
                }
            }

            // Cache the per-block sky/block light channels for entity sampling (mobs read the light
            // of the block they stand on). Reuses the flood fill already computed for faces - no extra
            // lighting pass. Written under the same lock as MeshFaces. This has to cover the WHOLE
            // column including the air above the terrain, because mobs sample the light where they
            // stand, so it can't stop at the top block - but it does have to visit every cell
            // regardless, which makes it the cheapest place to also census which special passes
            // this chunk needs at all.
            bool hasWater = false, hasLava = false, hasCross = false, hasSpecial = false, hasNonOpaque = false;
            lock (chunk.MeshLock)
            {
                var grid = chunk.LightGrid;
                if (grid == null || grid.Length != chunk.RawBlocks.Length) grid = new byte[chunk.RawBlocks.Length];
                for (int x = 0; x < width; x++)
                {
                    for (int z = 0; z < depth; z++)
                    {
                        int column = (x * depth + z) * height;
                        for (int y = 0; y < height; y++)
                        {
                            int sky = lighting.GetSkyLight(chunk.OriginX + x, y, chunk.OriginZ + z);
                            int block = lighting.GetBlockLight(chunk.OriginX + x, y, chunk.OriginZ + z);
                            grid[column + y] = (byte)((sky << 4) | block);
                            int id = raw[column + y];
                            if (id == WaterId) hasWater = true;
                            else if (id == LavaId) hasLava = true;
                            else if (BlockRegistry.IsCross(id)) hasCross = true;
                            else if (BlockRegistry.IsPartialShape(id)) hasSpecial = true;
                            if (LodSettings.SurfaceLodEnabled && id != BlockRegistry.AirId && (!BlockRegistry.IsOpaque(id)
                                || BlockRegistry.IsTransparent(id) || BlockRegistry.IsCutout(id)
                                || BlockRegistry.IsGlass(id) || BlockRegistry.IsTranslucent(id)))
                            {
                                hasNonOpaque = true;
                            }
                        }
                    }
                }
                chunk.LightGrid = grid;
            }

            // Only use the surface representation for untouched, ground-layer chunks containing
            // opaque full cubes. Fluids, cutouts, glass, partial shapes, and edits keep the exact
            // existing mesh so their special geometry and player-built detail cannot disappear.
            if (LodSettings.SurfaceLodEnabled && targetLayer == ChunkManager.GroundLayer && !chunk.HasRuntimeEdits
                && !hasWater && !hasLava && !hasCross && !hasSpecial && !hasNonOpaque)
            {
                surfaceLod = BuildSurfaceLod(chunk, chunkLookup, lighting, columnTop, columnTopBlock, biomeIdAt);
            }

            // The four passes below are each a full-column scan, and most chunks need none of them:
            // a plain stone/dirt chunk has no fluid, no cross billboards and no partial shapes at
            // all. The census above says which are worth running, and each pass can stop at
            // topSolidLocalY, since nothing above the chunk's own highest block can be any of these
            // (the greedy pass above relies on the same fact via blockAtY). Between them that turns
            // four unconditional full-volume scans into usually none, and at worst a third of one.

            // Fluid pass: water blocks render as sloped surfaces, not cubes. This runs after the
            // greedy pass (which was told to skip water-owned faces) so solid blocks keep their
            // correct occlusion against water while water gets its own geometry.
            if (hasWater) EmitWaterFaces(chunk, chunkLookup, lighting, mesh, topSolidLocalY);

            // Lava pass: same as water but with lava-specific properties.
            if (hasLava) EmitLavaFaces(chunk, chunkLookup, lighting, mesh, topSolidLocalY);

            // Cross pass: saplings/flowers/mushrooms render as two crossed billboard quads.
            if (hasCross) EmitCrossFaces(chunk, chunkLookup, lighting, mesh, topSolidLocalY);

            // Special-solid pass: slabs and stairs render as partial boxes (not full cubes).
            if (hasSpecial) EmitSpecialFaces(chunk, chunkLookup, lighting, mesh, topSolidLocalY);

            return mesh;
        }

        private const int SurfaceLodStep = 4;

        private static IReadOnlyList<MeshFace>? BuildSurfaceLod(Chunk chunk,
            Dictionary<ChunkCoordinates, Chunk> chunkLookup, ChunkLighting lighting,
            int[] columnTop, byte[] columnTopBlock, Func<int, int, string>? biomeIdAt)
        {
            int cellsX = chunk.Width / SurfaceLodStep;
            int cellsZ = chunk.Depth / SurfaceLodStep;
            if (cellsX == 0 || cellsZ == 0 || chunk.Width % SurfaceLodStep != 0 || chunk.Depth % SurfaceLodStep != 0)
                return null;

            // A shared, globally aligned height grid means adjacent LOD chunks sample the same
            // border columns and meet without cracks. Missing neighbors use the nearest local edge
            // until the normal neighbor-dirty path remeshes this chunk.
            var heights = new double[cellsX + 1, cellsZ + 1];
            for (int gx = 0; gx <= cellsX; gx++)
            {
                for (int gz = 0; gz <= cellsZ; gz++)
                {
                    int worldX = chunk.OriginX + gx * SurfaceLodStep;
                    int worldZ = chunk.OriginZ + gz * SurfaceLodStep;
                    heights[gx, gz] = GetSurfaceColumn(chunk, chunkLookup, columnTop, columnTopBlock, worldX, worldZ).TopY;
                }
            }

            var faces = new List<MeshFace>(cellsX * cellsZ);
            for (int gx = 0; gx < cellsX; gx++)
            {
                for (int gz = 0; gz < cellsZ; gz++)
                {
                    int centerX = gx * SurfaceLodStep + SurfaceLodStep / 2;
                    int centerZ = gz * SurfaceLodStep + SurfaceLodStep / 2;
                    int centerIndex = centerX * chunk.Depth + centerZ;
                    int centerTop = columnTop[centerIndex];
                    byte id = columnTopBlock[centerIndex];
                    if (id == BlockRegistry.AirId || !BlockRegistry.IsOpaque(id)) continue;

                    double x0 = chunk.OriginX + gx * SurfaceLodStep;
                    double x1 = x0 + SurfaceLodStep;
                    double z0 = chunk.OriginZ + gz * SurfaceLodStep;
                    double z1 = z0 + SurfaceLodStep;
                    var p0 = new Point3D(x0, heights[gx, gz], z0);
                    var p1 = new Point3D(x0, heights[gx, gz + 1], z1);
                    var p2 = new Point3D(x1, heights[gx + 1, gz + 1], z1);
                    var p3 = new Point3D(x1, heights[gx + 1, gz], z0);
                    var normal = Cross(p1 - p0, p2 - p1).Normalized();
                    if (normal.Y < 0) normal = new Point3D(-normal.X, -normal.Y, -normal.Z);

                    // Use the block's top tile even where the coarse surface is steep enough that
                    // its geometric normal would otherwise select the side texture.
                    var tile = BlockRegistry.FaceTexture(id, new Point3D(0, 1, 0));
                    var blockPosition = new Point3D((x0 + x1) * 0.5,
                        (heights[gx, gz] + heights[gx + 1, gz] + heights[gx, gz + 1] + heights[gx + 1, gz + 1]) * 0.25,
                        (z0 + z1) * 0.5);
                    bool biomeHillsGreenTint = (id == GrassId || id == GrassSpreadingId)
                        && biomeIdAt != null
                        && string.Equals(biomeIdAt(chunk.OriginX + centerX, chunk.OriginZ + centerZ), "hills", StringComparison.OrdinalIgnoreCase);
                    int lightX = chunk.OriginX + centerX;
                    int lightY = centerTop + 1;
                    int lightZ = chunk.OriginZ + centerZ;
                    faces.Add(new MeshFace(p0, p1, p2, p3, tile, normal, blockPosition,
                        1f, SurfaceLodStep, SurfaceLodStep,
                        lighting.GetSkyLight(lightX, lightY, lightZ), lighting.GetBlockLight(lightX, lightY, lightZ),
                        biomeHillsGreenTint: biomeHillsGreenTint));
                }
            }
            return faces.Count == 0 ? null : faces;
        }

        private static (double TopY, byte BlockId) GetSurfaceColumn(Chunk target,
            Dictionary<ChunkCoordinates, Chunk> chunkLookup, int[] targetTop, byte[] targetTopBlock,
            int worldX, int worldZ)
        {
            int localX = worldX - target.OriginX;
            int localZ = worldZ - target.OriginZ;
            if ((uint)localX < (uint)target.Width && (uint)localZ < (uint)target.Depth)
            {
                int index = localX * target.Depth + localZ;
                int top = targetTop[index];
                return (top >= 0 ? target.OriginY + top + 1 : target.OriginY, targetTopBlock[index]);
            }

            int chunkX = FloorDiv(worldX, ChunkManager.ChunkSize);
            int chunkZ = FloorDiv(worldZ, ChunkManager.ChunkSize);
            int layer = ChunkManager.LayerForWorldY(target.OriginY);
            if (chunkLookup.TryGetValue(new ChunkCoordinates(layer, chunkX, chunkZ), out var neighbor))
            {
                int nx = worldX - neighbor.OriginX;
                int nz = worldZ - neighbor.OriginZ;
                int column = (nx * neighbor.Depth + nz) * neighbor.Height;
                byte[] raw = neighbor.RawBlocks;
                for (int y = neighbor.Height - 1; y >= 0; y--)
                {
                    byte id = raw[column + y];
                    if (id != BlockRegistry.AirId) return (neighbor.OriginY + y + 1, id);
                }
                return (neighbor.OriginY, BlockRegistry.AirId);
            }

            // If the neighbor is not loaded yet, extend the closest edge sample temporarily. The
            // neighbor-dirty path will replace it with the exact shared border when it arrives.
            int clampedX = Math.Clamp(localX, 0, target.Width - 1);
            int clampedZ = Math.Clamp(localZ, 0, target.Depth - 1);
            int fallback = clampedX * target.Depth + clampedZ;
            int fallbackTop = targetTop[fallback];
            return (fallbackTop >= 0 ? target.OriginY + fallbackTop + 1 : target.OriginY, targetTopBlock[fallback]);
        }

        /// <summary>
        /// A block X renders a face toward neighbour N when X is non-air, the neighbour doesn't
        /// fully occlude it (neighbour is air or visually transparent), and it isn't an internal
        /// face between the same transparent block (water|water, glass|glass). Opaque neighbours
        /// hide the face; air and transparent neighbours show it.
        /// </summary>
        private static bool RendersToward(int xId, int nId)
            => xId != BlockRegistry.AirId
               // A fluid neighbour (see HasDedicatedPass) is meshed by its own dedicated pass and is
               // only ever a partial-height surface: the block next to it stays visible above the
               // fluid line, so its face must keep rendering. Lava counts as opaque, so without this
               // the wall beside a shallow lava stream would lose its face and show the void through.
               && (nId == BlockRegistry.AirId || BlockRegistry.IsTransparent(nId) || BlockRegistry.IsPartialShape(nId) || HasDedicatedPass(nId))
               && !(BlockRegistry.IsTransparent(nId) && nId == xId);

        /// <summary>
        /// Returns the quad's corners with p1/p3 swapped when its winding faces away from
        /// <paramref name="normal"/>, i.e. re-wound so V0->V1->V2 is counter-clockwise seen from the
        /// outside. The greedy pass's <see cref="FaceVertices"/> table has its +Z/-Z rows wound
        /// opposite their normals, and the renderer culls back faces with a counter-clockwise front
        /// face, so any quad built from the raw table order (fluid walls) would be invisible from
        /// outside - you would instead see the far inner wall of the fluid, which reads as the fluid
        /// rendering inside-out. The opaque pass culls, so this only became visible for lava when it
        /// stopped being translucent (the water pass is drawn with FaceCullMode.None).
        /// </summary>
        private static void WindTowardNormal(Point3D normal, ref Point3D p0, ref Point3D p1, ref Point3D p2, ref Point3D p3)
        {
            if (Dot(Cross(p1 - p0, p2 - p0), normal) < 0)
            {
                (p1, p3) = (p3, p1);
            }
        }

        // ---- Fluid (water) pass -----------------------------------------------------

        private static void EmitWaterFaces(Chunk chunk, Dictionary<ChunkCoordinates, Chunk> lookup, ChunkLighting lighting, List<MeshFace> mesh, int maxY)
        {
            byte[] raw = chunk.RawBlocks;
            int height = chunk.Height;
            int depth = chunk.Depth;
            int width = chunk.Width;
            float alpha = BlockRegistry.Alpha(WaterId);
            var sideTile = new TextureRect(WaterBaseTile.X + BlockRegistry.TileSize, WaterBaseTile.Y, WaterBaseTile.Width, WaterBaseTile.Height);

            // First sweep: every NON-TOP water face (bottoms + walls).
            for (int x = 0; x < width; x++)
            {
                for (int z = 0; z < depth; z++)
                {
                    int column = (x * depth + z) * height;
                    for (int y = 0; y <= maxY; y++)
                    {
                        if (raw[column + y] != WaterId) continue;

                        float h00 = GetFluidHeight(lookup, WaterId, chunk.OriginX + x, chunk.OriginY + y, chunk.OriginZ + z);
                        float h01 = GetFluidHeight(lookup, WaterId, chunk.OriginX + x, chunk.OriginY + y, chunk.OriginZ + z + 1);
                        float h11 = GetFluidHeight(lookup, WaterId, chunk.OriginX + x + 1, chunk.OriginY + y, chunk.OriginZ + z + 1);
                        float h10 = GetFluidHeight(lookup, WaterId, chunk.OriginX + x + 1, chunk.OriginY + y, chunk.OriginZ + z);
                        EmitWaterBottomFace(lookup, lighting, WaterId,
                            chunk.OriginX + x, chunk.OriginY + y, chunk.OriginZ + z,
                            WaterBaseTile, alpha, mesh);
                        EmitWaterSideFaces(lookup, lighting, WaterId,
                            chunk.OriginX + x, chunk.OriginY + y, chunk.OriginZ + z,
                            sideTile, alpha, mesh, h00, h01, h11, h10);
                    }
                }
            }
            // Second sweep: ALL top faces LAST. Water doesn't write depth, so face draw order is
            // what decides water-over-water blending; emitting the surface after the walls means
            // the water surface always blends over exposed lower faces (e.g. air pockets under
            // the ocean) instead of the far faces painting over the nearer water.
            for (int x = 0; x < width; x++)
            {
                for (int z = 0; z < depth; z++)
                {
                    int column = (x * depth + z) * height;
                    for (int y = 0; y <= maxY; y++)
                    {
                        if (raw[column + y] != WaterId) continue;

                        float h00 = GetFluidHeight(lookup, WaterId, chunk.OriginX + x, chunk.OriginY + y, chunk.OriginZ + z);
                        float h01 = GetFluidHeight(lookup, WaterId, chunk.OriginX + x, chunk.OriginY + y, chunk.OriginZ + z + 1);
                        float h11 = GetFluidHeight(lookup, WaterId, chunk.OriginX + x + 1, chunk.OriginY + y, chunk.OriginZ + z + 1);
                        float h10 = GetFluidHeight(lookup, WaterId, chunk.OriginX + x + 1, chunk.OriginY + y, chunk.OriginZ + z);
                        EmitWaterTopFace(lookup, lighting, WaterId,
                            chunk.OriginX + x, chunk.OriginY + y, chunk.OriginZ + z,
                            WaterBaseTile, sideTile, alpha, mesh, h00, h01, h11, h10);
                    }
                }
            }
        }

        // Top face of one water cell (only when the cell above isn't water). Flowing water uses
        // the side tile, still water the base tile, matching BlockFluid.getBlockTextureFromSideAndMetadata.
        private static void EmitWaterTopFace(Dictionary<ChunkCoordinates, Chunk> lookup, ChunkLighting lighting, int waterId,
            int wx, int wy, int wz, TextureRect baseTile, TextureRect sideTile, float alpha, List<MeshFace> mesh,
            float h00, float h01, float h11, float h10)
        {
            if (GetBlockAt(lookup, wx, wy + 1, wz) != waterId)
            {
                var topTile = GetFlowVector(lookup, waterId, wx, wy, wz) ? sideTile : baseTile;
                int ly = wy - lighting.OriginY;
                int topSky = Math.Max(lighting.GetSkyLight(wx, ly, wz), lighting.GetSkyLight(wx, ly + 1, wz));
                int topBlock = Math.Max(lighting.GetBlockLight(wx, ly, wz), lighting.GetBlockLight(wx, ly + 1, wz));
                mesh.Add(new MeshFace(
                    new Point3D(wx + 0, wy + h00, wz + 0),
                    new Point3D(wx + 0, wy + h01, wz + 1),
                    new Point3D(wx + 1, wy + h11, wz + 1),
                    new Point3D(wx + 1, wy + h10, wz + 0),
                    topTile, new Point3D(0, 1, 0), new Point3D(wx, wy, wz), 1f, 1, 1, topSky, topBlock, alpha));
            }
        }

        // Bottom face of one water cell (only when the cell below isn't water and doesn't occlude).
        private static void EmitWaterBottomFace(Dictionary<ChunkCoordinates, Chunk> lookup, ChunkLighting lighting, int waterId,
            int wx, int wy, int wz, TextureRect baseTile, float alpha, List<MeshFace> mesh)
        {
            int below = GetBlockAt(lookup, wx, wy - 1, wz);
            if (below != waterId && (!BlockRegistry.IsSolid(below) || BlockRegistry.IsTransparent(below)))
            {
                int ly = wy - lighting.OriginY;
                int skyLight = lighting.GetSkyLight(wx, ly - 1, wz);
                int blockLight = lighting.GetBlockLight(wx, ly - 1, wz);
                mesh.Add(new MeshFace(
                    new Point3D(wx + 0, wy + 0, wz + 0),
                    new Point3D(wx + 1, wy + 0, wz + 0),
                    new Point3D(wx + 1, wy + 0, wz + 1),
                    new Point3D(wx + 0, wy + 0, wz + 1),
                    baseTile, new Point3D(0, -1, 0), new Point3D(wx, wy, wz), 0.5f, 1, 1, skyLight, blockLight, alpha));
            }
        }

        // Four side faces of one water cell. Vertex order matches the greedy pass's FaceVertices
        // table exactly (bottom pair first, then the top pair) so the walls survive back-face
        // culling; the top edge follows the two corner heights so the side is sloped too.
        private static void EmitWaterSideFaces(Dictionary<ChunkCoordinates, Chunk> lookup, ChunkLighting lighting, int waterId,
            int wx, int wy, int wz, TextureRect sideTile, float alpha, List<MeshFace> mesh,
            float h00, float h01, float h11, float h10)
        {
            EmitWaterSide(lookup, lighting, waterId, wx, wy, wz, mesh,
                new Point3D(0, 0, -1), sideTile, alpha, 0.8, wx, wz - 1,
                new Point3D(wx + 0, wy + 0, wz + 0),
                new Point3D(wx + 1, wy + 0, wz + 0),
                new Point3D(wx + 1, wy + h10, wz + 0),
                new Point3D(wx + 0, wy + h00, wz + 0));
            EmitWaterSide(lookup, lighting, waterId, wx, wy, wz, mesh,
                new Point3D(0, 0, 1), sideTile, alpha, 0.8, wx, wz + 1,
                new Point3D(wx + 1, wy + 0, wz + 1),
                new Point3D(wx + 0, wy + 0, wz + 1),
                new Point3D(wx + 0, wy + h01, wz + 1),
                new Point3D(wx + 1, wy + h11, wz + 1));
            EmitWaterSide(lookup, lighting, waterId, wx, wy, wz, mesh,
                new Point3D(-1, 0, 0), sideTile, alpha, 0.6, wx - 1, wz,
                new Point3D(wx + 0, wy + 0, wz + 0),
                new Point3D(wx + 0, wy + 0, wz + 1),
                new Point3D(wx + 0, wy + h01, wz + 1),
                new Point3D(wx + 0, wy + h00, wz + 0));
            EmitWaterSide(lookup, lighting, waterId, wx, wy, wz, mesh,
                new Point3D(1, 0, 0), sideTile, alpha, 0.6, wx + 1, wz,
                new Point3D(wx + 1, wy + 0, wz + 1),
                new Point3D(wx + 1, wy + 0, wz + 0),
                new Point3D(wx + 1, wy + h10, wz + 0),
                new Point3D(wx + 1, wy + h11, wz + 1));
        }


        private static void EmitWaterSide(Dictionary<ChunkCoordinates, Chunk> lookup, ChunkLighting lighting, int waterId,
            int wx, int wy, int wz, List<MeshFace> mesh,
            Point3D normal, TextureRect tile, float alpha, double shade, int neighborX, int neighborZ,
            Point3D p0, Point3D p1, Point3D p2, Point3D p3)
        {
            int neighbor = GetBlockAt(lookup, neighborX, wy, neighborZ);
            if (neighbor == waterId || (BlockRegistry.IsSolid(neighbor) && !BlockRegistry.IsTransparent(neighbor)))
            {
                return;
            }

            // Sample the BRIGHTER of the water cell and the neighbor cell. At a cave mouth or
            // cliff the neighbor is air - lit only by whatever leaked through the water column -
            // so sampling only the neighbor made the wall render pitch black (invisible), letting
            // the cave walls behind show through untinted ("air pocket" artifact). The top face
            // already uses max(self, above); this keeps side walls consistent with it.
            int ly = wy - lighting.OriginY;
            int sideSky = Math.Max(lighting.GetSkyLight(wx, ly, wz), lighting.GetSkyLight(neighborX, ly, neighborZ));
            int sideBlock = Math.Max(lighting.GetBlockLight(wx, ly, wz), lighting.GetBlockLight(neighborX, ly, neighborZ));
            var blockPos = new Point3D(wx, wy, wz);
            WindTowardNormal(normal, ref p0, ref p1, ref p2, ref p3);
            mesh.Add(new MeshFace(
                p0, p1, p2, p3,
                tile, normal, blockPos, (float)shade, 1, 1, sideSky, sideBlock, alpha, anchorVBottom: true));
        }

// ---- Lava pass (same as water but with lava properties) ----

        private static void EmitLavaFaces(Chunk chunk, Dictionary<ChunkCoordinates, Chunk> lookup, ChunkLighting lighting, List<MeshFace> mesh, int maxY)
        {
            byte[] raw = chunk.RawBlocks;
            int height = chunk.Height;
            int depth = chunk.Depth;
            int width = chunk.Width;
            float alpha = BlockRegistry.Alpha(LavaId);
            var sideTile = new TextureRect(LavaBaseTile.X + BlockRegistry.TileSize, LavaBaseTile.Y, LavaBaseTile.Width, LavaBaseTile.Height);

            // First sweep: every NON-TOP lava face (bottoms + walls).
            for (int x = 0; x < width; x++)
            {
                for (int z = 0; z < depth; z++)
                {
                    int column = (x * depth + z) * height;
                    for (int y = 0; y <= maxY; y++)
                    {
                        if (raw[column + y] != LavaId) continue;

                        float h00 = GetFluidHeight(lookup, LavaId, chunk.OriginX + x, chunk.OriginY + y, chunk.OriginZ + z);
                        float h01 = GetFluidHeight(lookup, LavaId, chunk.OriginX + x, chunk.OriginY + y, chunk.OriginZ + z + 1);
                        float h11 = GetFluidHeight(lookup, LavaId, chunk.OriginX + x + 1, chunk.OriginY + y, chunk.OriginZ + z + 1);
                        float h10 = GetFluidHeight(lookup, LavaId, chunk.OriginX + x + 1, chunk.OriginY + y, chunk.OriginZ + z);
                        EmitLavaBottomFace(lookup, lighting, LavaId, chunk.OriginX + x, chunk.OriginY + y, chunk.OriginZ + z,
                            LavaBaseTile, alpha, mesh);
                        EmitLavaSideFaces(lookup, lighting, LavaId, chunk.OriginX + x, chunk.OriginY + y, chunk.OriginZ + z,
                            sideTile, alpha, mesh, h00, h01, h11, h10);
                    }
                }
            }
            // Second sweep: ALL top faces LAST. Lava draws opaque (alpha 1.0, so depth-write on) and
            // internal lava-over-lava walls are culled by EmitLavaSide, so face order no longer
            // decides anything here. The split is kept deliberately: it costs nothing, and emitting
            // the surface last is what keeps the look correct if lava is ever made translucent
            // again (it used to be alpha 0.9, where draw order was the only thing separating the
            // surface from the walls behind it).
            for (int x = 0; x < width; x++)
            {
                for (int z = 0; z < depth; z++)
                {
                    int column = (x * depth + z) * height;
                    for (int y = 0; y <= maxY; y++)
                    {
                        if (raw[column + y] != LavaId) continue;

                        float h00 = GetFluidHeight(lookup, LavaId, chunk.OriginX + x, chunk.OriginY + y, chunk.OriginZ + z);
                        float h01 = GetFluidHeight(lookup, LavaId, chunk.OriginX + x, chunk.OriginY + y, chunk.OriginZ + z + 1);
                        float h11 = GetFluidHeight(lookup, LavaId, chunk.OriginX + x + 1, chunk.OriginY + y, chunk.OriginZ + z + 1);
                        float h10 = GetFluidHeight(lookup, LavaId, chunk.OriginX + x + 1, chunk.OriginY + y, chunk.OriginZ + z);
                        EmitLavaTopFace(lookup, lighting, LavaId, chunk.OriginX + x, chunk.OriginY + y, chunk.OriginZ + z,
                            LavaBaseTile, sideTile, alpha, mesh, h00, h01, h11, h10);
                    }
                }
            }
        }

        private static void EmitLavaTopFace(Dictionary<ChunkCoordinates, Chunk> lookup, ChunkLighting lighting, int lavaId,
            int wx, int wy, int wz, TextureRect baseTile, TextureRect sideTile, float alpha, List<MeshFace> mesh,
            float h00, float h01, float h11, float h10)
        {
            if (GetBlockAt(lookup, wx, wy + 1, wz) != LavaId)
            {
                var topTile = GetFlowVector(lookup, lavaId, wx, wy, wz) ? sideTile : baseTile;
                int ly = wy - lighting.OriginY;
                int topSky = Math.Max(lighting.GetSkyLight(wx, ly, wz), lighting.GetSkyLight(wx, ly + 1, wz));
                int topBlock = Math.Max(lighting.GetBlockLight(wx, ly, wz), lighting.GetBlockLight(wx, ly + 1, wz));
                mesh.Add(new MeshFace(
                    new Point3D(wx + 0, wy + h00, wz + 0),
                    new Point3D(wx + 0, wy + h01, wz + 1),
                    new Point3D(wx + 1, wy + h11, wz + 1),
                    new Point3D(wx + 1, wy + h10, wz + 0),
                    topTile, new Point3D(0, 1, 0), new Point3D(wx, wy, wz), 1f, 1, 1, topSky, topBlock, alpha));
            }
        }

        private static void EmitLavaBottomFace(Dictionary<ChunkCoordinates, Chunk> lookup, ChunkLighting lighting, int lavaId,
            int wx, int wy, int wz, TextureRect baseTile, float alpha, List<MeshFace> mesh)
        {
            int below = GetBlockAt(lookup, wx, wy - 1, wz);
            if (below != LavaId && (!BlockRegistry.IsSolid(below) || BlockRegistry.IsTransparent(below)))
            {
                int ly = wy - lighting.OriginY;
                int skyLight = lighting.GetSkyLight(wx, ly - 1, wz);
                int blockLight = lighting.GetBlockLight(wx, ly - 1, wz);
                mesh.Add(new MeshFace(
                    new Point3D(wx + 0, wy + 0, wz + 0),
                    new Point3D(wx + 1, wy + 0, wz + 0),
                    new Point3D(wx + 1, wy + 0, wz + 1),
                    new Point3D(wx + 0, wy + 0, wz + 1),
                    baseTile, new Point3D(0, -1, 0), new Point3D(wx, wy, wz), 0.5f, 1, 1, skyLight, blockLight, alpha));
            }
        }

        private static void EmitLavaSideFaces(Dictionary<ChunkCoordinates, Chunk> lookup, ChunkLighting lighting, int lavaId,
            int wx, int wy, int wz, TextureRect sideTile, float alpha, List<MeshFace> mesh,
            float h00, float h01, float h11, float h10)
        {
            EmitLavaSide(lookup, lighting, lavaId, wx, wy, wz, mesh,
                new Point3D(0, 0, -1), sideTile, alpha, 0.8, wx, wz - 1,
                new Point3D(wx + 0, wy + 0, wz + 0),
                new Point3D(wx + 1, wy + 0, wz + 0),
                new Point3D(wx + 1, wy + h10, wz + 0),
                new Point3D(wx + 0, wy + h00, wz + 0));
            EmitLavaSide(lookup, lighting, lavaId, wx, wy, wz, mesh,
                new Point3D(0, 0, 1), sideTile, alpha, 0.8, wx, wz + 1,
                new Point3D(wx + 1, wy + 0, wz + 1),
                new Point3D(wx + 0, wy + 0, wz + 1),
                new Point3D(wx + 0, wy + h01, wz + 1),
                new Point3D(wx + 1, wy + h11, wz + 1));
            EmitLavaSide(lookup, lighting, lavaId, wx, wy, wz, mesh,
                new Point3D(-1, 0, 0), sideTile, alpha, 0.6, wx - 1, wz,
                new Point3D(wx + 0, wy + 0, wz + 0),
                new Point3D(wx + 0, wy + 0, wz + 1),
                new Point3D(wx + 0, wy + h01, wz + 1),
                new Point3D(wx + 0, wy + h00, wz + 0));
            EmitLavaSide(lookup, lighting, lavaId, wx, wy, wz, mesh,
                new Point3D(1, 0, 0), sideTile, alpha, 0.6, wx + 1, wz,
                new Point3D(wx + 1, wy + 0, wz + 1),
                new Point3D(wx + 1, wy + 0, wz + 0),
                new Point3D(wx + 1, wy + h10, wz + 0),
                new Point3D(wx + 1, wy + h11, wz + 1));
        }

        private static void EmitLavaSide(Dictionary<ChunkCoordinates, Chunk> lookup, ChunkLighting lighting, int lavaId,
            int wx, int wy, int wz, List<MeshFace> mesh,
            Point3D normal, TextureRect tile, float alpha, double shade, int neighborX, int neighborZ,
            Point3D p0, Point3D p1, Point3D p2, Point3D p3)
        {
            int neighbor = GetBlockAt(lookup, neighborX, wy, neighborZ);
            if (neighbor == LavaId || (BlockRegistry.IsSolid(neighbor) && !BlockRegistry.IsTransparent(neighbor)))
            {
                return;
            }

            int ly = wy - lighting.OriginY;
            int sideSky = Math.Max(lighting.GetSkyLight(wx, ly, wz), lighting.GetSkyLight(neighborX, ly, neighborZ));
            int sideBlock = Math.Max(lighting.GetBlockLight(wx, ly, wz), lighting.GetBlockLight(neighborX, ly, neighborZ));
            var blockPos = new Point3D(wx, wy, wz);
            WindTowardNormal(normal, ref p0, ref p1, ref p2, ref p3);
            mesh.Add(new MeshFace(
                p0, p1, p2, p3,
                tile, normal, blockPos, (float)shade, 1, 1, sideSky, sideBlock, alpha, anchorVBottom: true));
        }

        // ---- Cross-shape pass (saplings, flowers, mushrooms, spikes) -------------------

        // Cross plants render as two diagonal billboard quads spanning the cell (classic MC).
        // Vertex order puts the TOP of the tile at the quad's top (edge-based UV maps v0->tile
        // top, so V0/V3 are ordered top-first then bottom). Alpha is stored NEGATIVE: the shader
        // interprets it as "sample the sprite's per-pixel alpha" (the plant tiles ship with
        // transparent backgrounds). The transparent pipeline is double-sided, so each quad is
        // emitted once with front-facing winding and mirrors correctly from behind.
        private static void EmitCrossFaces(Chunk chunk, Dictionary<ChunkCoordinates, Chunk> lookup, ChunkLighting lighting, List<MeshFace> mesh, int maxY)
        {
            byte[] raw = chunk.RawBlocks;
            int height = chunk.Height;
            int depth = chunk.Depth;
            int width = chunk.Width;
            int torchId = BlockRegistry.GetId("torch");
            int sapTorchId = BlockRegistry.GetId("sap_torch");
            int burntTorchId = BlockRegistry.GetId("burnt_torch");

            for (int x = 0; x < width; x++)
            {
                for (int z = 0; z < depth; z++)
                {
                    int column = (x * depth + z) * height;
                    for (int y = 0; y <= maxY; y++)
                    {
                        int id = raw[column + y];
                        if (!BlockRegistry.IsCross(id)) continue;

                        var tile = BlockRegistry.GetById(id).AllTexture ?? default;
                        float alpha = -BlockRegistry.Alpha(id); // negative -> per-pixel sprite alpha
                        int wx = chunk.OriginX + x;
                        int wy = chunk.OriginY + y;
                        int wz = chunk.OriginZ + z;
                        int ly = wy - lighting.OriginY;
                        int skyLight = lighting.GetSkyLight(wx, ly, wz);
                        int blockLight = lighting.GetBlockLight(wx, ly, wz);
                        var blockPos = new Point3D(wx, wy, wz);

                        // Torches with wall meta (1-4, set by placement against a side face)
                        // render as the full X-cross leaning away from the wall.
                        // Also applies to sap torches and burnt torches (meta 1-4).
                        if (id == torchId || id == sapTorchId || id == burntTorchId)
                        {
                            int meta = GetMetaAt(lookup, wx, wy, wz);
                            if (meta >= 1 && meta <= 4)
                            {
                                EmitTorchWallFace(mesh, wx, wy, wz, tile, alpha, skyLight, blockLight, meta);
                                continue;
                            }
                        }

                        // Diagonal 0,0 -> 1,1 (top vertices first so the sprite isn't flipped).
                        mesh.Add(new MeshFace(
                            new Point3D(wx + 1, wy + 1, wz + 1),
                            new Point3D(wx + 0, wy + 1, wz + 0),
                            new Point3D(wx + 0, wy + 0, wz + 0),
                            new Point3D(wx + 1, wy + 0, wz + 1),
                            tile, new Point3D(1, 0, -1), blockPos, 1f, 1, 1, skyLight, blockLight, alpha));
                        // Diagonal 0,1 -> 1,0.
                        mesh.Add(new MeshFace(
                            new Point3D(wx + 1, wy + 1, wz + 0),
                            new Point3D(wx + 0, wy + 1, wz + 1),
                            new Point3D(wx + 0, wy + 0, wz + 1),
                            new Point3D(wx + 1, wy + 0, wz + 0),
                            tile, new Point3D(1, 0, 1), blockPos, 1f, 1, 1, skyLight, blockLight, alpha));
                    }
                }
            }
        }

        // ---- Torch wall face (meta 1-4: leans +X/-X/+Z/-Z away from its wall) ---------

        // A wall torch renders as ONE flat full-cell quad perpendicular to the wall, leaning
        // ~22.5deg away from it (the Minecraft wall-torch look): the bottom edge sits flush
        // against the wall face and the top edge tilts out into the cell. The full-cell
        // extent keeps the sprite art at its natural size (the visible torch art is 2px wide
        // x 10px tall out of the 16px tile, same world scale as the floor X-cross), and the
        // axis-aligned normal gives clean axis UVs. Top vertices first, matching the cross
        // plants.
        private static void EmitTorchWallFace(List<MeshFace> mesh, int wx, int wy, int wz,
            TextureRect tile, float alpha, int skyLight, int blockLight, int meta)
        {
            const float lean = 0.414f;  // tan(22.5deg) - the classic wall-torch tilt
            const float wallLift = 0.2f; // wall torches hang slightly higher than floor torches
            var blockPos = new Point3D(wx, wy, wz);
            Point3D p0, p1, p2, p3;
            Point3D normal;
            switch (meta)
            {
                case 1: // lean +X, wall at -X face
                    p0 = new Point3D(wx + lean, wy + 1 + wallLift, wz + 0);
                    p1 = new Point3D(wx + lean, wy + 1 + wallLift, wz + 1);
                    p2 = new Point3D(wx, wy + wallLift, wz + 1);
                    p3 = new Point3D(wx, wy + wallLift, wz + 0);
                    normal = new Point3D(1, 0, 0);
                    break;
                case 2: // lean -X, wall at +X face
                    p0 = new Point3D(wx + 1 - lean, wy + 1 + wallLift, wz + 0);
                    p1 = new Point3D(wx + 1 - lean, wy + 1 + wallLift, wz + 1);
                    p2 = new Point3D(wx + 1, wy + wallLift, wz + 1);
                    p3 = new Point3D(wx + 1, wy + wallLift, wz + 0);
                    normal = new Point3D(-1, 0, 0);
                    break;
                case 3: // lean +Z, wall at -Z face
                    p0 = new Point3D(wx + 0, wy + 1 + wallLift, wz + lean);
                    p1 = new Point3D(wx + 1, wy + 1 + wallLift, wz + lean);
                    p2 = new Point3D(wx + 1, wy + wallLift, wz);
                    p3 = new Point3D(wx + 0, wy + wallLift, wz);
                    normal = new Point3D(0, 0, 1);
                    break;
                default: // meta 4: lean -Z, wall at +Z face
                    p0 = new Point3D(wx + 0, wy + 1 + wallLift, wz + 1 - lean);
                    p1 = new Point3D(wx + 1, wy + 1 + wallLift, wz + 1 - lean);
                    p2 = new Point3D(wx + 1, wy + wallLift, wz + 1);
                    p3 = new Point3D(wx + 0, wy + wallLift, wz + 1);
                    normal = new Point3D(0, 0, -1);
                    break;
            }

            mesh.Add(new MeshFace(p0, p1, p2, p3, tile, normal, blockPos, 1f, 1, 1, skyLight, blockLight, alpha));

            // Fold copy: the same sprite folded 90deg around the front quad's art line so the
            // torch reads solid from the sides. It's a tilted RECTANGLE (edges perpendicular,
            // at 22.5deg) so both quads share the art line at every height - the sprites
            // coincide with no double-image. The fudged normal (all components <= 0.5) forces
            // the edge-based UV path, which is what maps the sprite onto the leaning line.
            // Meta 1 offsets (mirrored/rotated for 2-4), all at z = +0.5:
            //   top:  (-0.04798, 1.19126) / (0.87598, 0.80874)
            //   bottom: (0.46198, -0.19126) / (-0.46198, 0.19126)
            var foldNormal = new Point3D(0.001f, 0.001f, 0.49f);
            Point3D q0, q1, q2, q3;
            switch (meta)
            {
                case 1:
                    q0 = new Point3D(wx - 0.04798f, wy + 1.19126f + wallLift, wz + 0.5f);
                    q1 = new Point3D(wx + 0.87598f, wy + 0.80874f + wallLift, wz + 0.5f);
                    q2 = new Point3D(wx + 0.46198f, wy - 0.19126f + wallLift, wz + 0.5f);
                    q3 = new Point3D(wx - 0.46198f, wy + 0.19126f + wallLift, wz + 0.5f);
                    break;
                case 2:
                    q0 = new Point3D(wx + 1.04798f, wy + 1.19126f + wallLift, wz + 0.5f);
                    q1 = new Point3D(wx + 0.12402f, wy + 0.80874f + wallLift, wz + 0.5f);
                    q2 = new Point3D(wx + 0.53802f, wy - 0.19126f + wallLift, wz + 0.5f);
                    q3 = new Point3D(wx + 1.46198f, wy + 0.19126f + wallLift, wz + 0.5f);
                    break;
                case 3:
                    q0 = new Point3D(wx + 0.5f, wy + 1.19126f + wallLift, wz - 0.04798f);
                    q1 = new Point3D(wx + 0.5f, wy + 0.80874f + wallLift, wz + 0.87598f);
                    q2 = new Point3D(wx + 0.5f, wy - 0.19126f + wallLift, wz + 0.46198f);
                    q3 = new Point3D(wx + 0.5f, wy + 0.19126f + wallLift, wz - 0.46198f);
                    break;
                default:
                    q0 = new Point3D(wx + 0.5f, wy + 1.19126f + wallLift, wz + 1.04798f);
                    q1 = new Point3D(wx + 0.5f, wy + 0.80874f + wallLift, wz + 0.12402f);
                    q2 = new Point3D(wx + 0.5f, wy - 0.19126f + wallLift, wz + 0.53802f);
                    q3 = new Point3D(wx + 0.5f, wy + 0.19126f + wallLift, wz + 1.46198f);
                    break;
            }

            mesh.Add(new MeshFace(q0, q1, q2, q3, tile, foldNormal, blockPos, 1f, 1, 1, skyLight, blockLight, alpha));
        }

        // ---- Special-solid pass (slabs, stairs) ---------------------------------------

        // Slabs and stairs render as partial boxes, not full cubes. Each visible face is emitted
        // with the greedy pass's exact vertex winding (FaceVertices) so back-face culling keeps
        // the correct side, and the face's light is sampled from the empty cell it faces into.
        private static void EmitSpecialFaces(Chunk chunk, Dictionary<ChunkCoordinates, Chunk> lookup, ChunkLighting lighting, List<MeshFace> mesh, int maxY)
        {
            byte[] raw = chunk.RawBlocks;
            int height = chunk.Height;
            int depth = chunk.Depth;
            int width = chunk.Width;

            for (int x = 0; x < width; x++)
            {
                for (int z = 0; z < depth; z++)
                {
                    int column = (x * depth + z) * height;
                    for (int y = 0; y <= maxY; y++)
                    {
                        int id = raw[column + y];
                        if (!BlockRegistry.IsPartialShape(id)) continue;
                        int wx = chunk.OriginX + x;
                        int wy = chunk.OriginY + y;
                        int wz = chunk.OriginZ + z;

                        if (BlockRegistry.IsSlab(id))
                        {
                            EmitBox(lookup, lighting, mesh, wx, wy, wz, id, 0, 0, 0, 1, 0.5, 1);
                        }
                        else if (BlockRegistry.IsSlabTop(id))
                        {
                            EmitBox(lookup, lighting, mesh, wx, wy, wz, id, 0, 0.5, 0, 1, 1, 1);
                        }
                        else if (BlockRegistry.IsRail(id))
                              {
                              // Rails lie flat on the floor of their cell rather than filling it.
                              // The SHAPE is carried by the texture: one straight tile and one
                              // curve tile, rotated per meta so a corner draws a corner and a
                              // straight draws a straight. A slope is drawn as a real ramp quad so
                              // a step up visibly connects the two heights.
                              int meta = chunk.GetMeta(x, y, z);
                              var railTex = RailShape.GetRailTexture(meta);
                              int railRot = RailShape.GetRailRotation(meta);
                              // Hanging track: how far this cell has sagged below the rail plane.
                              double railDrop = RailSag.Dequantize(chunk.GetRailDroop(x, y, z));
                              if (RailShape.IsSlope(meta))
                                  EmitRailSlope(lighting, mesh, wx, wy, wz, meta, railTex, railRot, railDrop);
                              else if (railDrop > 0.0 || RailNeighbourDrop(lookup, wx, wy, wz, meta) > 0.0)
                                  // Sagging (or tied to a neighbour that is): draw a real plank that
                                  // tilts to meet the rail on each side, so a drooping run reads as
                                  // one continuous hanging line instead of detached floating tiles.
                                  EmitRailFlat(lighting, mesh, wx, wy, wz, meta, railTex, railRot, railDrop, lookup);
                              else
                              {
                              // One sixteenth of a block high; cutout (-1) so the transparent parts
                              // of the rail texture show the ground underneath instead of black.
                              // Top face only - a rail's sides and bottom are meant to be invisible.
                              EmitBox(lookup, lighting, mesh, wx, wy, wz, id, 0, 0, 0, 1, BlockRegistry.RailHeight, 1, -1f, railTex, railRot, true);
                              }
                              }
                              else if (BlockRegistry.IsDoor(id))
                        {
                            // Doors are two-cell vertical panels: each half fills its own cell's
                            // full height and shares the same X/Z panel bounds. Orientation,
                            // hinge side and open/closed all come from the meta byte.
                            int meta = chunk.GetMeta(x, y, z);
                            BlockRegistry.GetDoorBounds(meta, out double dx0, out double dz0, out double dx1, out double dz1);
                            // Cutout (-1) so the door's glass panes show the sky/room behind them
                            // via the atlas PNG's own alpha rather than being blended opaque.
                            EmitBox(lookup, lighting, mesh, wx, wy, wz, id, dx0, 0, dz0, dx1, 1, dz1, -1f);
                        }
                        else // stairs - facing from metadata, two boxes
                        {
                            int meta = chunk.GetMeta(x, y, z);
                            switch (meta)
                            {
                                case 0:
                                    EmitBox(lookup, lighting, mesh, wx, wy, wz, id, 0, 0, 0, 0.5, 0.5, 1);
                                    EmitBox(lookup, lighting, mesh, wx, wy, wz, id, 0.5, 0, 0, 1, 1, 1);
                                    break;
                                case 1:
                                    EmitBox(lookup, lighting, mesh, wx, wy, wz, id, 0, 0, 0, 0.5, 1, 1);
                                    EmitBox(lookup, lighting, mesh, wx, wy, wz, id, 0.5, 0, 0, 1, 0.5, 1);
                                    break;
                                case 2:
                                    EmitBox(lookup, lighting, mesh, wx, wy, wz, id, 0, 0, 0, 1, 0.5, 0.5);
                                    EmitBox(lookup, lighting, mesh, wx, wy, wz, id, 0, 0, 0.5, 1, 1, 1);
                                    break;
                                default: // 3
                                    EmitBox(lookup, lighting, mesh, wx, wy, wz, id, 0, 0, 0, 1, 1, 0.5);
                                    EmitBox(lookup, lighting, mesh, wx, wy, wz, id, 0, 0, 0.5, 1, 0.5, 1);
                                    break;
                            }
                        }
                    }
                }
            }
        }

        private static void EmitBox(Dictionary<ChunkCoordinates, Chunk> lookup, ChunkLighting lighting, List<MeshFace> mesh,
            int wx, int wy, int wz, int blockId, double minX, double minY, double minZ, double maxX, double maxY, double maxZ,
            TextureRect? texOverride = null, int textureRotation = 0)
            => EmitBox(lookup, lighting, mesh, wx, wy, wz, blockId, minX, minY, minZ, maxX, maxY, maxZ, 1f, texOverride, textureRotation);

        /// <summary>
        /// As <see cref="EmitBox(Dictionary{ChunkCoordinates,Chunk},ChunkLighting,List{MeshFace},int,int,int,int,double,double,double,double,double,double)"/>,
        /// but with an explicit render alpha. Negative values are sentinels understood by the
        /// renderer's pass bucketing (-1 = cutout, i.e. honour the atlas PNG's per-pixel alpha).
        /// </summary>
        /// <summary>
        /// As <see cref="EmitBox(Dictionary{ChunkCoordinates,Chunk},ChunkLighting,List{MeshFace},int,int,int,int,double,double,double,double,double,double)"/>,
        /// but with an explicit render alpha. Negative values are sentinels understood by the
        /// renderer's pass bucketing (-1 = cutout, i.e. honour the atlas PNG's per-pixel alpha).
        /// </summary>
        private static void EmitBox(Dictionary<ChunkCoordinates, Chunk> lookup, ChunkLighting lighting, List<MeshFace> mesh,
            int wx, int wy, int wz, int blockId, double minX, double minY, double minZ, double maxX, double maxY, double maxZ, float alpha,
            TextureRect? texOverride = null, int textureRotation = 0, bool topFaceOnly = false)
        {
            var def = BlockRegistry.GetById(blockId);
            for (int i = 0; i < FaceOffsets.Length; i++)
            {
                var off = FaceOffsets[i];
                // A rail is a single flat quad lying on the floor of its cell. Emitting the box's
                // sides and bottom would smear slivers of the rail texture up the edges, so only
                // the upward face is drawn - the rest of the slab is invisible by design.
                if (topFaceOnly && off.dy <= 0) continue;
                int nx = wx + off.dx;
                int ny = wy + off.dy;
                int nz = wz + off.dz;

                // A full opaque cube neighbour only hides this face when the face lies ON the
                // block-cell boundary (e.g. a slab's bottom/sides). Partial faces inside the cell
                // (a slab's top, a stair's riser) face their own cell's space and are never
                // occluded by a neighbouring cube - otherwise a block above a slab would wrongly
                // make the slab's top vanish.
                bool onCellBoundary =
                    (off.dx > 0 && maxX >= 1.0 - 1e-6) || (off.dx < 0 && minX <= 1e-6) ||
                    (off.dy > 0 && maxY >= 1.0 - 1e-6) || (off.dy < 0 && minY <= 1e-6) ||
                    (off.dz > 0 && maxZ >= 1.0 - 1e-6) || (off.dz < 0 && minZ <= 1e-6);
                if (onCellBoundary)
                {
                    int nId = GetBlockAt(lookup, nx, ny, nz);
                    if (nId != BlockRegistry.AirId && BlockRegistry.IsOpaque(nId) && BlockRegistry.IsSolid(nId)
                        && !BlockRegistry.IsPartialShape(nId) && !BlockRegistry.IsCross(nId))
                    {
                        continue;
                    }
                }

                int nly = ny - lighting.OriginY;
                double shade = off.dy > 0 ? 1.0 : (off.dy < 0 ? 0.5 : (off.dx != 0 ? 0.6 : 0.8));
                int skyLight = lighting.GetSkyLight(nx, nly, nz);
                int blockLight = lighting.GetBlockLight(nx, nly, nz);

                var normal = new Point3D(off.dx, off.dy, off.dz);
                var verts = FaceVertices[i];
                var p0 = new Point3D(wx + minX + (maxX - minX) * verts[0].X, wy + minY + (maxY - minY) * verts[0].Y, wz + minZ + (maxZ - minZ) * verts[0].Z);
                var p1 = new Point3D(wx + minX + (maxX - minX) * verts[1].X, wy + minY + (maxY - minY) * verts[1].Y, wz + minZ + (maxZ - minZ) * verts[1].Z);
                var p2 = new Point3D(wx + minX + (maxX - minX) * verts[2].X, wy + minY + (maxY - minY) * verts[2].Y, wz + minZ + (maxZ - minZ) * verts[2].Z);
                var p3 = new Point3D(wx + minX + (maxX - minX) * verts[3].X, wy + minY + (maxY - minY) * verts[3].Y, wz + minZ + (maxZ - minZ) * verts[3].Z);

                // The FaceVertices table's +Z/-Z entries are wound opposite their normals (the
                // greedy pass corrects them); do the same here so back-face culling keeps the
                // front faces visible on slabs/stairs.
                if (Dot(Cross(p1 - p0, p2 - p0), normal) < 0)
                {
                    var tmp = p1;
                    p1 = p3;
                    p3 = tmp;
                }

                var src = texOverride ?? def.FaceTexture(normal);
                mesh.Add(new MeshFace(p0, p1, p2, p3, src, normal, new Point3D(wx, wy, wz), (float)shade, 1, 1,
                    skyLight, blockLight, alpha, false, textureRotation));
            }
        }

        // ---- Fluid world reads (bounded by the chunk set the worker handed us) ----------

        private static int GetBlockAt(Dictionary<ChunkCoordinates, Chunk> lookup, int wx, int wy, int wz)
        {
            var chunk = FindChunk(lookup, wx, wy, wz);
            if (chunk == null)
            {
                return BlockRegistry.AirId;
            }

            int lx = wx - chunk.OriginX;
            int lz = wz - chunk.OriginZ;
            int ly = chunk.WorldYToLocal(wy);
            return chunk.IsInBounds(lx, ly, lz) ? chunk[lx, ly, lz] : BlockRegistry.AirId;
        }

        private static int GetMetaAt(Dictionary<ChunkCoordinates, Chunk> lookup, int wx, int wy, int wz)
        {
            var chunk = FindChunk(lookup, wx, wy, wz);
            if (chunk == null)
            {
                return 0;
            }

            int lx = wx - chunk.OriginX;
            int lz = wz - chunk.OriginZ;
            int ly = chunk.WorldYToLocal(wy);
            return chunk.IsInBounds(lx, ly, lz) ? chunk.GetMeta(lx, ly, lz) : 0;
        }

        /// <summary>Rail droop, in blocks, at a world cell (0 when it isn't a loaded rail cell).
        /// Reads the runtime-only droop byte off the chunk, mirroring GetBlockAt/GetMetaAt.</summary>
        private static double GetRailDroopAt(Dictionary<ChunkCoordinates, Chunk> lookup, int wx, int wy, int wz)
        {
            var chunk = FindChunk(lookup, wx, wy, wz);
            if (chunk == null) return 0;
            int lx = wx - chunk.OriginX;
            int lz = wz - chunk.OriginZ;
            int ly = chunk.WorldYToLocal(wy);
            return chunk.IsInBounds(lx, ly, lz) ? RailSag.Dequantize(chunk.GetRailDroop(lx, ly, lz)) : 0;
        }

        private static Chunk? FindChunk(Dictionary<ChunkCoordinates, Chunk> lookup, int wx, int wy, int wz)
        {
            int cx = FloorDiv(wx, ChunkManager.ChunkSize);
            int cz = FloorDiv(wz, ChunkManager.ChunkSize);
            // Route by the block's world Y to the correct layer (deep/ground/sky). Water and
            // terrain live in the ground layer; without this, lookups hit the deep layer and
            // neighbor sampling (fluid heights, occlusion) reads wrong blocks.
            int layer = ChunkManager.LayerForWorldY(wy);
            lookup.TryGetValue(new ChunkCoordinates(layer, cx, cz), out var chunk);
            return chunk;
        }

        /// <summary>BlockFluid.getFluidHeight: weighted average of the four cells around a corner,
        /// full height when any of them has water directly above.</summary>
        private static float GetFluidHeight(Dictionary<ChunkCoordinates, Chunk> lookup, int waterId, int wx, int wy, int wz)
        {
            int divisor = 0;
            float sum = 0f;
            for (int i = 0; i < 4; i++)
            {
                int cx = wx - (i & 1);
                int cz = wz - ((i >> 1) & 1);
                if (GetBlockAt(lookup, cx, wy + 1, cz) == waterId)
                {
                    return 1f;
                }

                int mat = GetBlockAt(lookup, cx, wy, cz);
                if (mat != waterId)
                {
                    if (!BlockRegistry.IsSolid(mat))
                    {
                        sum += 1f;
                        divisor += 1;
                    }
                }
                else
                {
                    int m = GetMetaAt(lookup, cx, wy, cz);
                    if (m >= 8 || m == 0)
                    {
                        sum += PercentAir(m) * 10f;
                        divisor += 10;
                    }

                    sum += PercentAir(m);
                    divisor += 1;
                }
            }

            return divisor == 0 ? 1f : 1f - sum / divisor;
        }

        /// <summary>BlockFluid.getPercentAir: air fraction of a flow level, falling water = 0.</summary>
        private static float PercentAir(int meta)
        {
            if (meta >= 8)
            {
                meta = 0;
            }

            return (meta + 1) / 9f;
        }

        /// <summary>True when the top-surface texture should use the side tile, i.e. the flow
        /// vector has a horizontal component (BlockFluid.getFlowDirection != -1000 sentinel).</summary>
        private static bool GetFlowVector(Dictionary<ChunkCoordinates, Chunk> lookup, int waterId, int wx, int wy, int wz)
        {
            int self = GetEffectiveFlowDecay(lookup, waterId, wx, wy, wz);
            double vx = 0, vz = 0;
            for (int i = 0; i < 4; i++)
            {
                int nx = wx, nz = wz;
                if (i == 0) nx = wx - 1;
                else if (i == 1) nz = wz - 1;
                else if (i == 2) nx = wx + 1;
                else nz = wz + 1;

                int decay = GetEffectiveFlowDecay(lookup, waterId, nx, wy, nz);
                int d;
                if (decay < 0)
                {
                    decay = GetEffectiveFlowDecay(lookup, waterId, nx, wy - 1, nz);
                    if (decay >= 0)
                    {
                        d = decay - (self - 8);
                        vx += (nx - wx) * d;
                        vz += (nz - wz) * d;
                    }
                }
                else
                {
                    d = decay - self;
                    vx += (nx - wx) * d;
                    vz += (nz - wz) * d;
                }
            }

            return Math.Abs(vx) > 1e-9 || Math.Abs(vz) > 1e-9;
        }

        private static int GetEffectiveFlowDecay(Dictionary<ChunkCoordinates, Chunk> lookup, int waterId, int wx, int wy, int wz)
        {
            if (GetBlockAt(lookup, wx, wy, wz) != waterId)
            {
                return -1;
            }

            int m = GetMetaAt(lookup, wx, wy, wz);
            return m >= 8 ? 0 : m;
        }

        private static int Pack(int blockId, bool positive, int skyLight, int blockLight, bool biomeHillsGreenTint = false)
            => 1 | (blockId << 1) | (positive ? 0x200 : 0)
                | ((skyLight & 0xF) << 10) | (biomeHillsGreenTint ? 1 << 14 : 0)
                | ((blockLight & 0xF) << 15);

        /// <summary>
        /// Greedy-merges a mask of packed face codes into rectangles and appends them as
        /// <see cref="MeshFace"/>s. The normal sign (and thus which face tile is used) is read
        /// from each cell's positive bit, so the same routine serves the +d and -d masks.
        /// </summary>
        private static void EmitMergedFaces(int[] mask, int dimU, int dimV, int slice, int d, Chunk chunk, List<MeshFace> mesh)
        {
            int height = chunk.Height;
            // Scratch buffers hoisted out of the face loop: stackalloc reserves space for the
            // whole method, so doing it per-face would let stack use grow with face count.
            Span<Point3D> corners = stackalloc Point3D[4];
            Span<bool> usedScratch = stackalloc bool[4];
            Span<Point3D> orderedScratch = stackalloc Point3D[4];
            for (int i = 0; i < dimU; i++)
            {
                for (int j = 0; j < dimV; j++)
                {
                    int entry = mask[i * dimV + j];
                    if (entry == 0)
                        continue;

                    int entryType = (entry >> 1) & 0xFF;
                    bool entryPositive = (entry & 0x200) != 0;
                    int entrySkyLight = (entry >> 10) & 0xF;
                    int entryBlockLight = (entry >> 15) & 0xF;
                    bool entryBiomeHillsGreenTint = (entry & (1 << 14)) != 0;

                    // compute width
                    int w;
                    for (w = 1; i + w < dimU && mask[(i + w) * dimV + j] == entry; w++) { }

                    // compute height
                    int h;
                    bool done = false;
                    for (h = 1; j + h < dimV; h++)
                    {
                        for (int k = 0; k < w; k++)
                        {
                            if (mask[(i + k) * dimV + j + h] != entry)
                            {
                                done = true;
                                break;
                            }
                        }
                        if (done) break;
                    }

                    // Faces lie on the boundary between slice and slice+1, independent of normal sign.
                    int boundary = slice + 1;

                    // build four corners in world coordinates (reusing the hoisted buffer)
                    for (int cornerIdx = 0; cornerIdx < 4; cornerIdx++)
                    {
                        int cu = (cornerIdx == 0 || cornerIdx == 3) ? i : i + w;
                        int cv = (cornerIdx == 0 || cornerIdx == 1) ? j : j + h;

                        int wx, wy, wz;
                        switch (d)
                        {
                            case 0:
                                wx = chunk.OriginX + boundary;
                                wy = chunk.OriginY + cu;
                                wz = chunk.OriginZ + cv;
                                break;
                            case 1:
                                wx = chunk.OriginX + cv;
                                wy = chunk.OriginY + boundary;
                                wz = chunk.OriginZ + cu;
                                break;
                            default:
                                wx = chunk.OriginX + cu;
                                wy = chunk.OriginY + cv;
                                wz = chunk.OriginZ + boundary;
                                break;
                        }
                        corners[cornerIdx] = new Point3D(wx, wy, wz);
                    }

                    var desiredNormal = d switch
                    {
                        0 => new Point3D(entryPositive ? 1 : -1, 0, 0),
                        1 => new Point3D(0, entryPositive ? 1 : -1, 0),
                        2 => new Point3D(0, 0, entryPositive ? 1 : -1),
                        _ => new Point3D(0, 0, 0)
                    };
                    var axisNormal = desiredNormal.Normalized();

                    int tileWidth = Math.Max(1, w);
                    int tileHeight = Math.Max(1, h);
                    if (TryGetCubuildFaceAxes(axisNormal, out var uAxis, out var vAxis))
                    {
                        CanonicalizeQuadByAxes(corners, usedScratch, orderedScratch, uAxis, vAxis);
                        var canonicalCross = Cross(corners[1] - corners[0], corners[2] - corners[0]);
                        if (Dot(canonicalCross, axisNormal) < 0)
                        {
                            var tmp = corners[1];
                            corners[1] = corners[3];
                            corners[3] = tmp;
                        }
                        tileWidth = Math.Max(1, (int)Math.Round(GetAxisSpan(corners, uAxis)));
                        tileHeight = Math.Max(1, (int)Math.Round(GetAxisSpan(corners, vAxis)));
                    }
                    else
                    {
                        Point3D edge1 = corners[1] - corners[0];
                        Point3D edge2 = corners[2] - corners[0];
                        if (Dot(Cross(edge1, edge2), desiredNormal) < 0)
                        {
                            var tmp = corners[1];
                            corners[1] = corners[3];
                            corners[3] = tmp;
                        }
                    }

                    int bx = (int)Math.Floor(corners[0].X) - (axisNormal.X > 0 ? 1 : 0);
                    int by = (int)Math.Floor(corners[0].Y) - (axisNormal.Y > 0 ? 1 : 0);
                    int bz = (int)Math.Floor(corners[0].Z) - (axisNormal.Z > 0 ? 1 : 0);
                    var blockPos = new Point3D(bx, by, bz);

                    // Directional face shading: top 1.0, bottom 0.5, east/west (X) 0.6, north/south (Z) 0.8.
                    double shade = 0.8;
                    if (axisNormal.Y > 0.5) shade = 1.0;
                    else if (axisNormal.Y < -0.5) shade = 0.5;
                    else if (Math.Abs(axisNormal.X) > 0.5) shade = 0.6;

                    // per-block atlas tile (honouring top/bottom/side overrides) and render alpha.
                    // The alpha sign routes the face to its pass in the renderer:
                    //   cutout blocks (leaves)      -> negative alpha
                    //   glass                       -> negative alpha minus 100 (its own pass:
                    //                                  alpha-test, depth-write OFF, front-side)
                    var src = BlockRegistry.FaceTexture(entryType, axisNormal);
                    float alpha = BlockRegistry.Alpha(entryType);
                    // Translucent (colored glass): sentinel alpha of -alpha - 200 routes to the GLASS
                    // bucket (alpha < -10) so it draws with depth-write ON before water - its opaque
                    // pixels occlude water behind it like regular glass. The shader branches on the
                    // -200 offset to use the ATLAS PNG's per-pixel alpha instead of the cutout rule.
                    if (BlockRegistry.IsTranslucent(entryType)) alpha = -alpha - 200f;
                    else if (BlockRegistry.IsGlass(entryType)) alpha = -alpha - 100f;
                    else if (BlockRegistry.IsCutout(entryType)) alpha = -alpha;
                    mesh.Add(new MeshFace(corners[0], corners[1], corners[2], corners[3], src, axisNormal, blockPos,
                        (float)shade, tileWidth, tileHeight, entrySkyLight, entryBlockLight, alpha,
                        biomeHillsGreenTint: entryBiomeHillsGreenTint));

                    // zero-out mask
                    for (int aOff = 0; aOff < w; aOff++)
                    {
                        for (int bOff = 0; bOff < h; bOff++)
                        {
                            mask[(i + aOff) * dimV + j + bOff] = 0;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Draws a sloped rail as a real ramp quad so a step up visibly connects the lower track
        /// to the higher one. The quad runs from the cell's low edge at ground level to its high
        /// edge a block up, spanning the full cell across the track.
        /// </summary>
        private static void EmitRailSlope(ChunkLighting lighting, List<MeshFace> mesh,
            int wx, int wy, int wz, int meta, TextureRect tex, int textureRotation, double drop)
        {
            // Four corners in cell-local space, wound so the surface normal points up.
            double[] c = RailShape.AscendDirection(meta) switch
            {
                0 => new double[] { 0,0,0,  0,0,1,  1,1,1,  1,1,0 },  // ascends East
                1 => new double[] { 0,0,0,  0,1,1,  1,1,1,  1,0,0 },  // ascends South
                2 => new double[] { 1,0,0,  0,1,0,  0,1,1,  1,0,1 },  // ascends West
                _ => new double[] { 0,0,1,  1,0,1,  1,1,0,  0,1,0 },  // ascends North
            };
            // A drooping ramp sinks as a whole (its low end applies the drop; the climb span is
            // unchanged, so the ramp keeps connecting the same two heights, just lowered).
            var p0 = new Point3D(wx + c[0], wy + c[1] - drop, wz + c[2]);
            var p1 = new Point3D(wx + c[3], wy + c[4] - drop, wz + c[5]);
            var p2 = new Point3D(wx + c[6], wy + c[7] - drop, wz + c[8]);
            var p3 = new Point3D(wx + c[9], wy + c[10] - drop, wz + c[11]);
            var normal = Cross(p1 - p0, p2 - p1).Normalized();
            // ChunkLighting indexes by chunk-LOCAL y, not world y - every other emitter does
            // `wy - lighting.OriginY` first. Passing the world y sampled a row off the top of the
            // light array (which reads as zero light), which is why slopes came out black.
            // The ramp also climbs a whole block across its cell, so its high end reaches the
            // top of the cell above; taking the brighter of the two keeps a slope from being
            // darker than the flat track it continues from. Shade 1.0: a ramp faces up.
            int ly = wy - lighting.OriginY;
            int rampSky = Math.Max(lighting.GetSkyLight(wx, ly, wz), lighting.GetSkyLight(wx, ly + 1, wz));
            int rampBlock = Math.Max(lighting.GetBlockLight(wx, ly, wz), lighting.GetBlockLight(wx, ly + 1, wz));
            mesh.Add(new MeshFace(p0, p1, p2, p3, tex, normal, new Point3D(wx, wy, wz), 1f, 1, 1,
                rampSky, rampBlock, -1f, false, textureRotation));
        }

        /// <summary>Largest droop among the rail neighbours this cell links to (same Y only). Used
        /// as a cheap "is anything near me sagging" test so a flat cell still tilts toward a
        /// drooping neighbour instead of leaving a step in the run.</summary>
        private static double RailNeighbourDrop(Dictionary<ChunkCoordinates, Chunk> lookup, int wx, int wy, int wz, int meta)
        {
            var (a, b) = RailShape.Connections(meta);
            double best = 0;
            best = Math.Max(best, RailEdgeDrop(lookup, wx, wy, wz, 0, a));
            best = Math.Max(best, RailEdgeDrop(lookup, wx, wy, wz, 0, b));
            return best;
        }

        /// <summary>The droop height of the track where it crosses the edge of this cell toward
        /// <paramref name="dir"/>: the midpoint between this cell's droop and the rail neighbour
        /// in that direction, or just this cell's droop when there is no rail there (a free end,
        /// which hangs at its own height rather than bending toward absent track).</summary>
        private static double RailEdgeDrop(Dictionary<ChunkCoordinates, Chunk> lookup, int wx, int wy, int wz, double ownDrop, int dir)
        {
            int nx = wx + RailShape.DirX[dir], nz = wz + RailShape.DirZ[dir];
            if (GetBlockAt(lookup, nx, wy, nz) == RailShape.RailId)
                return (ownDrop + GetRailDroopAt(lookup, nx, wy, nz)) * 0.5;
            return ownDrop;
        }

        /// <summary>
        /// Draws a straight (or curve) rail as a plank that tilts along the track so a sagging run
        /// stays visually connected end to end - the hanging-chain look. The corners use the SAME
        /// winding as the flat top face (FaceVertices[3]) so the atlas tile and its per-meta
        /// rotation are unchanged; only the corner heights move.
        /// </summary>
        private static void EmitRailFlat(ChunkLighting lighting, List<MeshFace> mesh,
            int wx, int wy, int wz, int meta, TextureRect tex, int textureRotation,
            double drop, Dictionary<ChunkCoordinates, Chunk> lookup)
        {
            double hPos = drop, hNeg = drop;
            bool axisZ = false;
            if (!RailShape.IsCurve(meta))
            {
                var (a, b) = RailShape.Connections(meta);
                int posDir = (a == 0 || a == 1) ? a : b;   // the +X/+Z end of the track
                int negDir = (a == 0 || a == 1) ? b : a;
                axisZ = posDir == 1 || posDir == 3;
                hPos = RailEdgeDrop(lookup, wx, wy, wz, drop, posDir);
                hNeg = RailEdgeDrop(lookup, wx, wy, wz, drop, negDir);
            }

            double top = wy + BlockRegistry.RailHeight;
            // Corner heights: each corner takes the edge it lies on (x or z = 0/1).
            var p0 = new Point3D(wx + 0, top - hNeg, wz + 0);
            var p1 = new Point3D(wx + 0, top - (axisZ ? hPos : hNeg), wz + 1);
            var p2 = new Point3D(wx + 1, top - hPos, wz + 1);
            var p3 = new Point3D(wx + 1, top - (axisZ ? hNeg : hPos), wz + 0);

            // The normal here does exactly ONE job: it picks the UV axes in
            // TryGetCubuildFaceAxes (lighting comes from `brightness` below, and culling runs off
            // the winding, never the normal). A sagging plank's true normal tilts along the track,
            // and once that tilt passes ~30 degrees the axis pick falls through from the TOP-face
            // branch to a SIDE branch and quarter-turns the tile - which is why a hanging run's
            // texture flipped orientation only once it drooped past a certain angle. Pin the same
            // top convention the flat rail uses so the sprite holds its orientation at every droop
            // angle. (EmitRailSlope keeps its true diagonal normal: a ramp's tilt is fixed, so its
            // branch never changes out from under it.)
            var normal = new Point3D(0, 1, 0);

            int nly = wy + 1 - lighting.OriginY;
            int skyLight = lighting.GetSkyLight(wx, nly, wz);
            int blockLight = lighting.GetBlockLight(wx, nly, wz);
            mesh.Add(new MeshFace(p0, p1, p2, p3, tex, normal, new Point3D(wx, wy, wz), 1f, 1, 1,
                skyLight, blockLight, -1f, false, textureRotation));
        }

        private static double Dot(Point3D a, Point3D b)
        {
            return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        }

        private static Point3D Cross(Point3D a, Point3D b)
        {
            return new Point3D(
                a.Y * b.Z - a.Z * b.Y,
                a.Z * b.X - a.X * b.Z,
                a.X * b.Y - a.Y * b.X);
        }

        private static bool TryGetCubuildFaceAxes(Point3D normal, out Point3D uAxis, out Point3D vAxis)
        {
            if (normal.X > 0.5)
            {
                uAxis = new Point3D(0, 0, -1);
                vAxis = new Point3D(0, -1, 0);
                return true;
            }

            if (normal.X < -0.5)
            {
                uAxis = new Point3D(0, 0, 1);
                vAxis = new Point3D(0, -1, 0);
                return true;
            }

            if (normal.Z > 0.5)
            {
                uAxis = new Point3D(1, 0, 0);
                vAxis = new Point3D(0, -1, 0);
                return true;
            }

            if (normal.Z < -0.5)
            {
                uAxis = new Point3D(-1, 0, 0);
                vAxis = new Point3D(0, -1, 0);
                return true;
            }

            if (normal.Y > 0.5)
            {
                uAxis = new Point3D(1, 0, 0);
                vAxis = new Point3D(0, 0, -1);
                return true;
            }

            if (normal.Y < -0.5)
            {
                uAxis = new Point3D(1, 0, 0);
                vAxis = new Point3D(0, 0, 1);
                return true;
            }

            uAxis = new Point3D(0, 0, 0);
            vAxis = new Point3D(0, 0, 0);
            return false;
        }

        private static double GetAxisSpan(ReadOnlySpan<Point3D> corners, Point3D axis)
        {
            double min = double.PositiveInfinity;
            double max = double.NegativeInfinity;
            for (int i = 0; i < corners.Length; i++)
            {
                double value = Dot(corners[i], axis);
                if (value < min) min = value;
                if (value > max) max = value;
            }

            return Math.Max(0.0, max - min);
        }

        private static void CanonicalizeQuadByAxes(Span<Point3D> corners, Span<bool> used, Span<Point3D> ordered, Point3D uAxis, Point3D vAxis)
        {
            double minU = double.PositiveInfinity;
            double maxU = double.NegativeInfinity;
            double minV = double.PositiveInfinity;
            double maxV = double.NegativeInfinity;

            for (int i = 0; i < corners.Length; i++)
            {
                double u = Dot(corners[i], uAxis);
                double v = Dot(corners[i], vAxis);
                if (u < minU) minU = u;
                if (u > maxU) maxU = u;
                if (v < minV) minV = v;
                if (v > maxV) maxV = v;
            }

            used.Clear();
            ordered[0] = TakeClosestCorner(corners, used, uAxis, vAxis, minU, minV);
            ordered[1] = TakeClosestCorner(corners, used, uAxis, vAxis, maxU, minV);
            ordered[2] = TakeClosestCorner(corners, used, uAxis, vAxis, maxU, maxV);
            ordered[3] = TakeClosestCorner(corners, used, uAxis, vAxis, minU, maxV);

            for (int i = 0; i < 4; i++)
            {
                corners[i] = ordered[i];
            }
        }

        private static Point3D TakeClosestCorner(ReadOnlySpan<Point3D> corners, Span<bool> used, Point3D uAxis, Point3D vAxis, double targetU, double targetV)
        {
            int bestIndex = -1;
            double bestDistSq = double.PositiveInfinity;

            for (int i = 0; i < corners.Length; i++)
            {
                if (used[i])
                {
                    continue;
                }

                double u = Dot(corners[i], uAxis);
                double v = Dot(corners[i], vAxis);
                double du = u - targetU;
                double dv = v - targetV;
                double distSq = du * du + dv * dv;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
            {
                return corners[0];
            }

            used[bestIndex] = true;
            return corners[bestIndex];
        }
    }
}
