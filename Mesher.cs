using System;
using System.Collections.Generic;
using System.Numerics;

namespace CubeApp
{
    public sealed class Mesher
    {
        // Water is meshed by a dedicated pass (sloped surfaces + stretched sides), so the greedy
        // cube pass must never emit faces owned by a water block. Neighbouring solid blocks still
        // render their faces toward water through the normal RendersToward rules.
        private static readonly int WaterId = BlockRegistry.GetId("water");
        private static readonly TextureRect WaterBaseTile = BlockRegistry.Get("water").AllTexture ?? default;

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

        public static IReadOnlyList<MeshFace> GenerateMesh(IEnumerable<Chunk> chunks)
        {
            var mesh = new List<MeshFace>();
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
            for (int y = 0; y < height; y++)
            {
                bool has = false;
                int stride = depth * height;
                for (int x = 0; x < width && !has; x++)
                {
                    int colBase = x * stride;
                    for (int z = 0; z < depth && !has; z++)
                    {
                        if (raw[colBase + z * height + y] != BlockRegistry.AirId) { has = true; break; }
                    }
                }
                blockAtY[y] = has;
                anyBlock |= has;
                if (has) topSolidLocalY = y;
            }

            // Heightmap occlusion data: the highest solid world Y in this chunk. The mesh worker
            // owns this chunk exclusively, so writing here is race-free (same lock as MeshFaces).
            chunk.TopSolidY = topSolidLocalY >= 0 ? chunk.OriginY + topSolidLocalY : chunk.OriginY - 1;

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
                // bit9=positive normal, bits10-13=light. Merge equality is a single int compare.
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
                    // face is exposed to, matching classic Minecraft's neighbor sampling.
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
                                if (A != WaterId && !BlockRegistry.IsCross(A) && !BlockRegistry.IsPartialShape(A) && RendersToward(A, B)) maskPos[cell] = Pack(A, true, lighting.GetLight(chunk.OriginX + slice + 1, iu, chunk.OriginZ + jv));
                                if (B != WaterId && !BlockRegistry.IsCross(B) && !BlockRegistry.IsPartialShape(B) && RendersToward(B, A)) maskNeg[cell] = Pack(B, false, lighting.GetLight(chunk.OriginX + slice, iu, chunk.OriginZ + jv));
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
                                if (A != WaterId && !BlockRegistry.IsCross(A) && !BlockRegistry.IsPartialShape(A) && RendersToward(A, B)) maskPos[cell] = Pack(A, true, lighting.GetLight(chunk.OriginX + jv, slice + 1, chunk.OriginZ + iu));
                                if (B != WaterId && !BlockRegistry.IsCross(B) && !BlockRegistry.IsPartialShape(B) && RendersToward(B, A)) maskNeg[cell] = Pack(B, false, lighting.GetLight(chunk.OriginX + jv, slice, chunk.OriginZ + iu));
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
                                if (A != WaterId && !BlockRegistry.IsCross(A) && !BlockRegistry.IsPartialShape(A) && RendersToward(A, B)) maskPos[cell] = Pack(A, true, lighting.GetLight(chunk.OriginX + iu, jv, chunk.OriginZ + slice + 1));
                                if (B != WaterId && !BlockRegistry.IsCross(B) && !BlockRegistry.IsPartialShape(B) && RendersToward(B, A)) maskNeg[cell] = Pack(B, false, lighting.GetLight(chunk.OriginX + iu, jv, chunk.OriginZ + slice));
                            }
                        }
                    }

                    // Greedy-merge each mask into rectangles (cells merge only on exact-pack equality).
                    EmitMergedFaces(maskPos, dimU, dimV, slice, d, chunk, mesh);
                    EmitMergedFaces(maskNeg, dimU, dimV, slice, d, chunk, mesh);
                }
            }

            // Fluid pass: water blocks render as sloped surfaces, not cubes. This runs after the
            // greedy pass (which was told to skip water-owned faces) so solid blocks keep their
            // correct occlusion against water while water gets its own geometry.
            EmitWaterFaces(chunk, chunkLookup, lighting, mesh);

            // Cross pass: saplings/flowers/mushrooms render as two crossed billboard quads.
            EmitCrossFaces(chunk, chunkLookup, lighting, mesh);

            // Special-solid pass: slabs and stairs render as partial boxes (not full cubes).
            EmitSpecialFaces(chunk, chunkLookup, lighting, mesh);

            return mesh;
        }

        /// <summary>
        /// A block X renders a face toward neighbour N when X is non-air, the neighbour doesn't
        /// fully occlude it (neighbour is air or visually transparent), and it isn't an internal
        /// face between the same transparent block (water|water, glass|glass). Opaque neighbours
        /// hide the face; air and transparent neighbours show it.
        /// </summary>
        private static bool RendersToward(int xId, int nId)
            => xId != BlockRegistry.AirId
               && (nId == BlockRegistry.AirId || BlockRegistry.IsTransparent(nId) || BlockRegistry.IsPartialShape(nId))
               && !(BlockRegistry.IsTransparent(nId) && nId == xId);

        // ---- Fluid (water) pass -----------------------------------------------------

        private static void EmitWaterFaces(Chunk chunk, Dictionary<ChunkCoordinates, Chunk> lookup, ChunkLighting lighting, List<MeshFace> mesh)
        {
            byte[] raw = chunk.RawBlocks;
            int height = chunk.Height;
            int depth = chunk.Depth;
            int width = chunk.Width;
            float alpha = BlockRegistry.Alpha(WaterId);
            var sideTile = new TextureRect(WaterBaseTile.X + BlockRegistry.TileSize, WaterBaseTile.Y, WaterBaseTile.Width, WaterBaseTile.Height);

            for (int x = 0; x < width; x++)
            {
                for (int z = 0; z < depth; z++)
                {
                    int column = (x * depth + z) * height;
                    for (int y = 0; y < height; y++)
                    {
                        if (raw[column + y] != WaterId)
                        {
                            continue;
                        }

                        EmitWaterCellFaces(lookup, lighting, WaterId,
                            chunk.OriginX + x, chunk.OriginY + y, chunk.OriginZ + z,
                            WaterBaseTile, sideTile, alpha, mesh);
                    }
                }
            }
        }

        private static void EmitWaterCellFaces(Dictionary<ChunkCoordinates, Chunk> lookup, ChunkLighting lighting, int waterId,
            int wx, int wy, int wz, TextureRect baseTile, TextureRect sideTile, float alpha, List<MeshFace> mesh)
        {
            // Corner heights of this cell's top surface (0..1 above block bottom), MC order:
            // (x,z), (x,z+1), (x+1,z+1), (x+1,z).
            float h00 = GetFluidHeight(lookup, waterId, wx, wy, wz);
            float h01 = GetFluidHeight(lookup, waterId, wx, wy, wz + 1);
            float h11 = GetFluidHeight(lookup, waterId, wx + 1, wy, wz + 1);
            float h10 = GetFluidHeight(lookup, waterId, wx + 1, wy, wz);

            var blockPos = new Point3D(wx, wy, wz);
            // ChunkLighting indexes its light array by LOCAL y (0..chunkHeight-1) relative to the
            // region's layer origin, while wx/wy/wz here are world coordinates - convert via the
            // region origin (ground -256 or sky 384), not a hardcoded world origin.
            int ly = wy - lighting.OriginY;

            // Top face (only when the cell above isn't water). Flowing water uses the side tile,
            // still water the base tile, matching BlockFluid.getBlockTextureFromSideAndMetadata.
            if (GetBlockAt(lookup, wx, wy + 1, wz) != waterId)
            {
                var topTile = GetFlowVector(lookup, waterId, wx, wy, wz) ? sideTile : baseTile;
                int topLight = Math.Max(lighting.GetLight(wx, ly, wz), lighting.GetLight(wx, ly + 1, wz));
                double brightness = 1.0 * ChunkLighting.Brightness(topLight);
                mesh.Add(new MeshFace(
                    new Point3D(wx + 0, wy + h00, wz + 0),
                    new Point3D(wx + 0, wy + h01, wz + 1),
                    new Point3D(wx + 1, wy + h11, wz + 1),
                    new Point3D(wx + 1, wy + h10, wz + 0),
                    topTile, new Point3D(0, 1, 0), blockPos, (float)brightness, 1, 1, alpha));
            }

            // Bottom face (only when the cell below isn't water and doesn't occlude).
            int below = GetBlockAt(lookup, wx, wy - 1, wz);
            if (below != waterId && (!BlockRegistry.IsSolid(below) || BlockRegistry.IsTransparent(below)))
            {
                double brightness = 0.5 * ChunkLighting.Brightness(lighting.GetLight(wx, ly - 1, wz));
                mesh.Add(new MeshFace(
                    new Point3D(wx + 0, wy + 0, wz + 0),
                    new Point3D(wx + 1, wy + 0, wz + 0),
                    new Point3D(wx + 1, wy + 0, wz + 1),
                    new Point3D(wx + 0, wy + 0, wz + 1),
                    baseTile, new Point3D(0, -1, 0), blockPos, (float)brightness, 1, 1, alpha));
            }

            // Four side faces. Vertex order matches the greedy pass's FaceVertices table exactly
            // (bottom pair first, then the top pair) so the walls survive back-face culling;
            // the top edge follows the two corner heights so the side is sloped too.
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

            double brightness = shade * ChunkLighting.Brightness(lighting.GetLight(neighborX, wy - lighting.OriginY, neighborZ));
            var blockPos = new Point3D(wx, wy, wz);
            mesh.Add(new MeshFace(
                p0, p1, p2, p3,
                tile, normal, blockPos, (float)brightness, 1, 1, alpha, anchorVBottom: true));
        }

        // ---- Cross-shape pass (saplings, flowers, mushrooms, spikes) -------------------

        // Cross plants render as two diagonal billboard quads spanning the cell (classic MC).
        // Vertex order puts the TOP of the tile at the quad's top (edge-based UV maps v0->tile
        // top, so V0/V3 are ordered top-first then bottom). Alpha is stored NEGATIVE: the shader
        // interprets it as "sample the sprite's per-pixel alpha" (the plant tiles ship with
        // transparent backgrounds). The transparent pipeline is double-sided, so each quad is
        // emitted once with front-facing winding and mirrors correctly from behind.
        private static void EmitCrossFaces(Chunk chunk, Dictionary<ChunkCoordinates, Chunk> lookup, ChunkLighting lighting, List<MeshFace> mesh)
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
                    for (int y = 0; y < height; y++)
                    {
                        int id = raw[column + y];
                        if (!BlockRegistry.IsCross(id)) continue;

                        var tile = BlockRegistry.GetById(id).AllTexture ?? default;
                        float alpha = -BlockRegistry.Alpha(id); // negative -> per-pixel sprite alpha
                        int wx = chunk.OriginX + x;
                        int wy = chunk.OriginY + y;
                        int wz = chunk.OriginZ + z;
                        int ly = wy - lighting.OriginY;
                        double brightness = ChunkLighting.Brightness(lighting.GetLight(wx, ly, wz));
                        var blockPos = new Point3D(wx, wy, wz);

                        // Diagonal 0,0 -> 1,1 (top vertices first so the sprite isn't flipped).
                        mesh.Add(new MeshFace(
                            new Point3D(wx + 1, wy + 1, wz + 1),
                            new Point3D(wx + 0, wy + 1, wz + 0),
                            new Point3D(wx + 0, wy + 0, wz + 0),
                            new Point3D(wx + 1, wy + 0, wz + 1),
                            tile, new Point3D(1, 0, -1), blockPos, (float)brightness, 1, 1, alpha));
                        // Diagonal 0,1 -> 1,0.
                        mesh.Add(new MeshFace(
                            new Point3D(wx + 1, wy + 1, wz + 0),
                            new Point3D(wx + 0, wy + 1, wz + 1),
                            new Point3D(wx + 0, wy + 0, wz + 1),
                            new Point3D(wx + 1, wy + 0, wz + 0),
                            tile, new Point3D(1, 0, 1), blockPos, (float)brightness, 1, 1, alpha));
                    }
                }
            }
        }

        // ---- Special-solid pass (slabs, stairs) ---------------------------------------

        // Slabs and stairs render as partial boxes, not full cubes. Each visible face is emitted
        // with the greedy pass's exact vertex winding (FaceVertices) so back-face culling keeps
        // the correct side, and the face's light is sampled from the empty cell it faces into.
        private static void EmitSpecialFaces(Chunk chunk, Dictionary<ChunkCoordinates, Chunk> lookup, ChunkLighting lighting, List<MeshFace> mesh)
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
                    for (int y = 0; y < height; y++)
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
                        else // stairs - facing from metadata, two boxes (Infdev layout)
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
            int wx, int wy, int wz, int blockId, double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
        {
            var def = BlockRegistry.GetById(blockId);
            for (int i = 0; i < FaceOffsets.Length; i++)
            {
                var off = FaceOffsets[i];
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
                double brightness = shade * ChunkLighting.Brightness(lighting.GetLight(nx, nly, nz));

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

                var src = def.FaceTexture(normal);
                mesh.Add(new MeshFace(p0, p1, p2, p3, src, normal, new Point3D(wx, wy, wz), (float)brightness, 1, 1, 1f));
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

        private static int Pack(int blockId, bool positive, int light)
            => 1 | (blockId << 1) | (positive ? 0x200 : 0) | (light << 10);

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
                    int entryLight = (entry >> 10) & 0xF;

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

                    // Directional face shading matching classic Minecraft:
                    // top 1.0, bottom 0.5, east/west (X) 0.6, north/south (Z) 0.8.
                    double shade = 0.8;
                    if (axisNormal.Y > 0.5) shade = 1.0;
                    else if (axisNormal.Y < -0.5) shade = 0.5;
                    else if (Math.Abs(axisNormal.X) > 0.5) shade = 0.6;

                    // combine directional shade with the flood-filled light level
                    double brightness = shade * ChunkLighting.Brightness(entryLight);

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
                    mesh.Add(new MeshFace(corners[0], corners[1], corners[2], corners[3], src, axisNormal, blockPos, (float)brightness, tileWidth, tileHeight, alpha));

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