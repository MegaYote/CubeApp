using System;
using System.Collections.Generic;

namespace CubeApp
{
    public sealed class Mesher
    {
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
                var chunkCoord = new ChunkCoordinates(FloorDiv(c.OriginX, ChunkManager.ChunkSize), FloorDiv(c.OriginZ, ChunkManager.ChunkSize));
                chunkLookup[chunkCoord] = c;
            }

            // The first chunk is the target to mesh; additional chunks are neighbors used only for border occlusion checks.
            var chunk = chunkList[0];
            int width = chunk.Width;
            int height = chunk.Height;
            int depth = chunk.Depth;

            int[] dims = new[] { width, height, depth };

            for (int d = 0; d < 3; d++)
            {
                int u = (d + 1) % 3;
                int v = (d + 2) % 3;

                int dimD = dims[d];
                int dimU = dims[u];
                int dimV = dims[v];

                var mask = new (BlockType type, bool positive)?[dimU, dimV];

                for (int slice = 0; slice < dimD; slice++)
                {
                    // build mask comparing slice and slice+1 in world coords
                    for (int iu = 0; iu < dimU; iu++)
                    {
                        for (int jv = 0; jv < dimV; jv++)
                        {
                            // compute world coordinates for A (slice) and B (slice+1)
                            int worldXA, worldYA, worldZA;
                            int worldXB, worldYB, worldZB;

                            if (d == 0)
                            {
                                // X slice: u=Y, v=Z
                                worldXA = chunk.OriginX + slice;
                                worldXB = chunk.OriginX + slice + 1;
                                worldYA = iu;
                                worldYB = iu;
                                worldZA = chunk.OriginZ + jv;
                                worldZB = chunk.OriginZ + jv;
                            }
                            else if (d == 1)
                            {
                                // Y slice: u=Z, v=X
                                worldYA = slice;
                                worldYB = slice + 1;
                                worldZA = chunk.OriginZ + iu;
                                worldZB = chunk.OriginZ + iu;
                                worldXA = chunk.OriginX + jv;
                                worldXB = chunk.OriginX + jv;
                            }
                            else
                            {
                                // Z slice: u=X, v=Y
                                worldZA = chunk.OriginZ + slice;
                                worldZB = chunk.OriginZ + slice + 1;
                                worldXA = chunk.OriginX + iu;
                                worldXB = chunk.OriginX + iu;
                                worldYA = jv;
                                worldYB = jv;
                            }

                            BlockType A = GetBlockAtWorld(chunkLookup, worldXA, worldYA, worldZA);
                            BlockType B = GetBlockAtWorld(chunkLookup, worldXB, worldYB, worldZB);

                            if (IsOpaque(A) && !IsOpaque(B))
                            {
                                mask[iu, jv] = (A, true);
                            }
                            else if (!IsOpaque(A) && IsOpaque(B))
                            {
                                mask[iu, jv] = (B, false);
                            }
                            else
                            {
                                mask[iu, jv] = null;
                            }
                        }
                    }

                    // greedy merge mask into rectangles
                    for (int i = 0; i < dimU; i++)
                    {
                        for (int j = 0; j < dimV; j++)
                        {
                            var entry = mask[i, j];
                            if (entry == null)
                                continue;

                            // compute width
                            int w;
                            for (w = 1; i + w < dimU && mask[i + w, j] != null && mask[i + w, j]?.type == entry?.type && mask[i + w, j]?.positive == entry?.positive; w++) { }

                            // compute height
                            int h;
                            bool done = false;
                            for (h = 1; j + h < dimV; h++)
                            {
                                for (int k = 0; k < w; k++)
                                {
                                    var m = mask[i + k, j + h];
                                    if (m == null || m?.type != entry?.type || m?.positive != entry?.positive)
                                    {
                                        done = true;
                                        break;
                                    }
                                }

                                if (done) break;
                            }

                            // Faces lie on the boundary between slice and slice+1, independent of normal sign.
                            int boundary = slice + 1;

                            // build four corners in world coordinates
                            var corners = new Point3D[4];
                            for (int cornerIdx = 0; cornerIdx < 4; cornerIdx++)
                            {
                                int iu = (cornerIdx == 0 || cornerIdx == 3) ? i : i + w;
                                int jv2 = (cornerIdx == 0 || cornerIdx == 1) ? j : j + h;

                                int wx, wy, wz;
                                // map back to world coords
                                switch (d)
                                {
                                    case 0:
                                        // X is slice axis
                                        wx = chunk.OriginX + boundary;
                                        wy = iu;
                                        wz = jv2 + chunk.OriginZ;
                                        break;
                                    case 1:
                                        // Y is slice axis (u=Z, v=X)
                                        // iu == Z, jv2 == X
                                        wx = chunk.OriginX + jv2;
                                        wy = boundary;
                                        wz = chunk.OriginZ + iu;
                                        break;
                                    default:
                                        // Z is slice axis
                                        wx = chunk.OriginX + iu;
                                        wy = jv2;
                                        wz = chunk.OriginZ + boundary;
                                        break;
                                }

                                corners[cornerIdx] = new Point3D(wx, wy, wz);
                            }

                            // desired normal direction for this face (axis-aligned)
                            var desiredNormal = d switch
                            {
                                0 => new Point3D(entry?.positive == true ? 1 : -1, 0, 0),
                                1 => new Point3D(0, entry?.positive == true ? 1 : -1, 0),
                                2 => new Point3D(0, 0, entry?.positive == true ? 1 : -1),
                                _ => new Point3D(0, 0, 0)
                            };

                            // Use exact axis-aligned normal to avoid floating rounding causing mismatches.
                            var axisNormal = desiredNormal.Normalized();

                            // Canonicalize corner ordering to stable Cubuild-like face axes.
                            int tileWidth = Math.Max(1, w);
                            int tileHeight = Math.Max(1, h);
                            if (TryGetCubuildFaceAxes(axisNormal, out var uAxis, out var vAxis))
                            {
                                CanonicalizeQuadByAxes(corners, uAxis, vAxis);

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
                                // Fallback: ensure vertex winding produces a normal that matches desiredNormal.
                                Point3D edge1 = corners[1] - corners[0];
                                Point3D edge2 = corners[2] - corners[0];
                                if (Dot(Cross(edge1, edge2), desiredNormal) < 0)
                                {
                                    var tmp = corners[1];
                                    corners[1] = corners[3];
                                    corners[3] = tmp;
                                }
                            }

                            // Compute canonical block position the face belongs to (integer block coords)
                            int bx = (int)Math.Floor(corners[0].X) - (axisNormal.X > 0 ? 1 : 0);
                            int by = (int)Math.Floor(corners[0].Y) - (axisNormal.Y > 0 ? 1 : 0);
                            int bz = (int)Math.Floor(corners[0].Z) - (axisNormal.Z > 0 ? 1 : 0);

                            var blockPos = new Point3D(bx, by, bz);

                            // Directional face shading matching Cubuild's faceShade:
                            // top 1.0, bottom 0.5, east/west (X) 0.6, north/south (Z) 0.8.
                            double shade = 0.8; // north/south (Z faces)
                            if (axisNormal.Y > 0.5) shade = 1.0; // top
                            else if (axisNormal.Y < -0.5) shade = 0.5; // bottom
                            else if (Math.Abs(axisNormal.X) > 0.5) shade = 0.6; // east/west

                            // compute atlas src rect for this block face
                            var src = GetAtlasSrcRect(entry.Value.type, axisNormal);
                            // pass tile span so renderers tile textures along face-local U/V axes.
                            mesh.Add(new MeshFace(corners, src, axisNormal, blockPos, (float)shade, tileWidth, tileHeight));

                            // zero-out mask
                            for (int aOff = 0; aOff < w; aOff++)
                            {
                                for (int bOff = 0; bOff < h; bOff++)
                                {
                                    mask[i + aOff, j + bOff] = null;
                                }
                            }
                        }
                    }
                }
            }

            return mesh;
        }

        private static BlockType GetBlockAtWorld(Dictionary<ChunkCoordinates, Chunk> chunkLookup, int worldX, int y, int worldZ)
        {
            int chunkX = FloorDiv(worldX, ChunkManager.ChunkSize);
            int chunkZ = FloorDiv(worldZ, ChunkManager.ChunkSize);
            if (!chunkLookup.TryGetValue(new ChunkCoordinates(chunkX, chunkZ), out var chunk))
                return BlockType.Air;

            int localX = worldX - chunk.OriginX;
            int localZ = worldZ - chunk.OriginZ;
            if (!chunk.IsInBounds(localX, y, localZ))
                return BlockType.Air;

            return chunk[localX, y, localZ];
        }

        private static bool IsOpaque(BlockType t)
        {
            return t != BlockType.Air;
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

        private static double GetAxisSpan(Point3D[] corners, Point3D axis)
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

        private static void CanonicalizeQuadByAxes(Point3D[] corners, Point3D uAxis, Point3D vAxis)
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

            var used = new bool[corners.Length];
            var ordered = new Point3D[4];
            ordered[0] = TakeClosestCorner(corners, used, uAxis, vAxis, minU, minV);
            ordered[1] = TakeClosestCorner(corners, used, uAxis, vAxis, maxU, minV);
            ordered[2] = TakeClosestCorner(corners, used, uAxis, vAxis, maxU, maxV);
            ordered[3] = TakeClosestCorner(corners, used, uAxis, vAxis, minU, maxV);

            for (int i = 0; i < 4; i++)
            {
                corners[i] = ordered[i];
            }
        }

        private static Point3D TakeClosestCorner(Point3D[] corners, bool[] used, Point3D uAxis, Point3D vAxis, double targetU, double targetV)
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

        private static BlockType GetBlockAt(Dictionary<ChunkCoordinates, Chunk> chunkLookup, int localX, int y, int localZ, Chunk originChunk)
        {
            // convert localX/localZ relative to originChunk
            int worldX = originChunk.OriginX + localX;
            int worldZ = originChunk.OriginZ + localZ;
            int chunkX = FloorDiv(worldX, ChunkManager.ChunkSize);
            int chunkZ = FloorDiv(worldZ, ChunkManager.ChunkSize);
            if (!chunkLookup.TryGetValue(new ChunkCoordinates(chunkX, chunkZ), out var chunk))
            {
                return BlockType.Air;
            }

            int lx = worldX - chunk.OriginX;
            int lz = worldZ - chunk.OriginZ;
            if (!chunk.IsInBounds(lx, y, lz))
                return BlockType.Air;
            return chunk[lx, y, lz];
        }

        private static TextureRect GetAtlasSrcRect(BlockType blockType, Point3D normal)
        {
            // Cubuild mapping (see local Cubuild reference): tile(row, col) with top-left origin.
            // Atlas coords here are tx=col, ty=row.
            const int tile = 16;
            int tx = 0, ty = 0;

            switch (blockType)
            {
                case BlockType.Grass:
                    // Use the grass top texture on every face.
                    tx = 0; ty = 0;
                    break;
                case BlockType.Dirt:
                    // Cubuild DIRT: tile(0,2)
                    tx = 2; ty = 0; break;
                case BlockType.Stone:
                    // Cubuild STONE: tile(0,1)
                    tx = 1; ty = 0; break;
                case BlockType.Cobblestone:
                    // Cubuild COBBLESTONE: tile(1,0)
                    tx = 0; ty = 1; break;
                case BlockType.Sand:
                    // Cubuild SAND: tile(1,2)
                    tx = 2; ty = 1; break;
                case BlockType.Planks:
                    // Cubuild PLANKS: tile(0,4)
                    tx = 4; ty = 0; break;
                case BlockType.Bedrock:
                    // Cubuild BEDROCK: tile(1,1)
                    tx = 1; ty = 1; break;
                case BlockType.Gravel:
                    // Cubuild GRAVEL: tile(1,3)
                    tx = 3; ty = 1; break;
                case BlockType.Obsidian:
                    // Cubuild OBSIDIAN: tile(2,5)
                    tx = 5; ty = 2; break;
                case BlockType.MossyCobblestone:
                    // Cubuild MOSSYCOBBLESTONE: tile(2,4)
                    tx = 4; ty = 2; break;
                default:
                    tx = 15; ty = 15; break;
            }

            return new TextureRect(tx * tile, ty * tile, tile, tile);
        }
    }
}
