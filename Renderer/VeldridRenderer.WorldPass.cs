using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using Veldrid;
using Veldrid.SPIRV;

namespace Cubuild.Renderer
{
    public sealed partial class VeldridRenderer : IRenderer, IDisposable
    {
        private void DrawWorldPass(
            CommandList cl,
            System.Collections.Generic.List<(Cubuild.ChunkCoordinates Coord, IndirectDrawIndexedArguments Cmd)> commands,
            IndirectDrawIndexedArguments[] scratch,
            Pipeline pipeline,
            ref uint[] cullData)
        {
            if (commands.Count == 0)
            {
                return;
            }

            uint drawCount;
            if (_gpuCullEnabled && _gpuCullSupported)
            {
                // GPU-assisted culling: compute pass writes the args, we draw ALL commands and
                // culled chunks simply have InstanceCount=0 (no CPU scan, no compaction).
                RunGpuCull(cl, commands, ref cullData);
                drawCount = (uint)commands.Count;
            }
            else
            {
                drawCount = CullDrawCommands(commands, scratch);
                if (drawCount == 0)
                {
                    return;
                }
            }

            cl.SetPipeline(pipeline);
            cl.SetGraphicsResourceSet(0, _projViewSet);
            if (_textureSet != null)
                cl.SetGraphicsResourceSet(1, _textureSet);
            cl.SetGraphicsResourceSet(2, _fogSet);
            cl.SetVertexBuffer(0, _megaVertexBuffer);
            cl.SetIndexBuffer(_megaIndexBuffer, IndexFormat.UInt16);
            if (_gd.Features.DrawIndirect)
            {
                EnsureIndirectCapacity(drawCount);
                if (!_gpuCullEnabled)
                {
                    // D3D11 indirect-args buffers are USAGE_DEFAULT (no Dynamic flag), so the
                    // contents are pushed via CommandList.UpdateBuffer (UpdateSubresource).
                    cl.UpdateBuffer(_indirectBuffer, 0, ref scratch[0], drawCount * IndirectCommandStride);
                }
                cl.DrawIndexedIndirect(_indirectBuffer, 0, drawCount, IndirectCommandStride);
            }
            else
            {
                // Fallback for backends without indirect draws (D3D11 has it).
                for (int i = 0; i < drawCount; i++)
                {
                    var cmd = _gpuCullEnabled ? commands[i].Cmd : scratch[i];
                    cl.DrawIndexed(cmd.IndexCount, cmd.InstanceCount, cmd.FirstIndex, (int)cmd.VertexOffset, cmd.FirstInstance);
                }
            }
        }

        private void DrawHighlight(CommandList cl)
        {
            // C++ Game.cpp hides the normal block highlighter while breaking - the shrink-cube
            // overlay (and its own highlight) replaces it. Otherwise the white quad would float in
            // the shrinking hole.
            if (_hud.MiningProgress > 0f && _hud.MiningBlockId > 0) return;

            var quad = _hud.HighlightWorldQuad;
            if (quad == null || quad.Length != 4 || _highlightPipeline == null)
            {
                return;
            }

            for (int i = 0; i < 4; i++)
            {
                _highlightVertexScratch[i * 3 + 0] = quad[i].X;
                _highlightVertexScratch[i * 3 + 1] = quad[i].Y;
                _highlightVertexScratch[i * 3 + 2] = quad[i].Z;
            }

            _gd.UpdateBuffer(_highlightVertexBuffer, 0, _highlightVertexScratch);

            cl.SetPipeline(_highlightPipeline);
            cl.SetGraphicsResourceSet(0, _projViewSet);
            cl.SetVertexBuffer(0, _highlightVertexBuffer);
            cl.SetIndexBuffer(_highlightIndexBuffer, IndexFormat.UInt16);
            cl.DrawIndexed(6, 1, 0, 0, 0);
        }

        // Cubuild C++ shrinking-block mining overlay: while a block is mined, the WORLD shader
        // discards every face in the mining cell (hideBreakingBlock -> basic.frag, inclusive
        // bounds), so the block is completely invisible. This pass then draws ONLY the shrinking
        // cube - the mined block's tiles, scaled 1.0 -> 0.1 as progress -> 1 - with a tiny
        // clip-space depth bias (the C++ glPolygonOffset(-1,-1) equivalent, ~1 ULP) so nothing
        // coplanar can ever z-fight. No walls, no fake faces.
        // C++ Game.cpp startBreaking light capture: sample the 6 neighbors' combined light and
        // keep the brightest, so the shrink cube matches the block's surroundings (the block
        // itself is solid -> light 0 inside). ChunkLighting is a full 3x3-chunk flood fill, so
        // cache the result PER CHUNK: walking across blocks in the same chunk (rapidly re-targeting
        // while holding mine) reuses the cached light instead of recomputing ~590k cells every
        // few frames. Only crossing into a new chunk (or a new mining block in a different chunk)
        // triggers the rebuild.
        private void CaptureMiningLight(Vector3 blockPos, long chunkKey)
        {
            if (chunkKey == _miningLightChunkKey)
            {
                return; // already captured for this chunk
            }
            _miningLightChunkKey = chunkKey;
            _miningLightLevel = 15;
            if (_chunkManager == null) return;

            try
            {
                int bx = (int)blockPos.X, by = (int)blockPos.Y, bz = (int)blockPos.Z;
                int layer = ChunkManager.LayerForWorldY(by);
                int cx = (int)Math.Floor(bx / (double)ChunkManager.ChunkSize);
                int cz = (int)Math.Floor(bz / (double)ChunkManager.ChunkSize);

                var region = new Dictionary<ChunkCoordinates, Chunk>();
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        var key = new ChunkCoordinates(layer, cx + dx, cz + dz);
                        if (_chunkManager.TryGetLoadedChunk(key, out var c)) region[key] = c;
                    }
                }
                if (region.Count == 0) return;

                var lighting = new ChunkLighting(region, ChunkManager.ChunkSize, ChunkManager.HeightForLayer(layer));
                int best = 0;
                (int dx, int dy, int dz)[] dirs =
                {
                    (-1,0,0), (1,0,0), (0,-1,0), (0,1,0), (0,0,-1), (0,0,1)
                };
                foreach (var d in dirs)
                {
                    int l = lighting.GetLight(bx + d.dx, by - lighting.OriginY + d.dy, bz + d.dz);
                    if (l > best) best = l;
                }
                _miningLightLevel = best;
            }
            catch
            {
                _miningLightLevel = 15; // fail safe: full bright
            }
        }

        // Returns the brightness multiplier (0..1) at a world position using the SAME baked light
        // rules as block faces: ChunkLighting.Brightness(light) where light comes from the chunk's
        // cached per-block LightGrid (filled by the mesher's existing flood fill). This is a plain
        // array lookup - no lighting rebuild at render time. Falls back to the global _nightDim
        // when the chunk isn't loaded / hasn't meshed yet.
        private float GetMobLight(double x, double y, double z)
        {
            if (_chunkManager == null) return _nightDim;

            int layer = ChunkManager.LayerForWorldY((int)Math.Floor(y));
            int cx = (int)Math.Floor(x / (double)ChunkManager.ChunkSize);
            int cz = (int)Math.Floor(z / (double)ChunkManager.ChunkSize);
            if (!_chunkManager.TryGetLoadedChunk(new ChunkCoordinates(layer, cx, cz), out var chunk) || chunk.LightGrid == null)
            {
                return _nightDim;
            }

            int localX = (int)Math.Floor(x) - chunk.OriginX;
            int localY = (int)Math.Floor(y) - chunk.OriginY;
            int localZ = (int)Math.Floor(z) - chunk.OriginZ;
            if (localX < 0 || localX >= chunk.Width || localY < 0 || localY >= chunk.Height || localZ < 0 || localZ >= chunk.Depth)
            {
                return _nightDim;
            }

            int idx = (localX * chunk.Depth + localZ) * chunk.Height + localY;
            return ChunkLighting.MobBrightness(chunk.LightGrid[idx]);
        }

        private void DrawShrinkCube(CommandList cl)
        {
            if (_pipeline == null || _shrinkCubeVertexBuffer == null || _shrinkCubeIndexBuffer == null) return;
            float p = _hud.MiningProgress;
            if (p <= 0.001f || _hud.MiningBlockId <= 0) return;

            float scale = 1f - p * 0.9f; // C++: 1.0 -> 0.1
            if (scale < 0.001f) return;

            // Capture the mining block's light once per mining CHUNK (C++ startBreaking does the
            // same) and shade the overlay like the world mesh: brightness = faceShade *
            // Brightness(light). Cached per chunk so rapid re-targeting while walking doesn't
            // rebuild the 3x3 flood-fill light every few frames.
            var mbp = _hud.MiningBlockPos;
            long mchunkKey = ((long)(int)Math.Floor(mbp.X / (double)ChunkManager.ChunkSize) << 32)
                           | (uint)(int)Math.Floor(mbp.Z / (double)ChunkManager.ChunkSize);
            CaptureMiningLight(mbp, mchunkKey);
            float lightMult = ChunkLighting.Brightness(_miningLightLevel);

            var center = _hud.MiningBlockPos + new Vector3(0.5f);
            var def = BlockRegistry.GetById(_hud.MiningBlockId);
            // Per-face shade (top 1.0 / bottom 0.5 / N+S 0.8 / E+W 0.6), same as the mesher.
            float[] faceShade = { 0.8f, 0.8f, 0.5f, 1.0f, 0.6f, 0.6f };
            // Unit-cube face corners (back/front/bottom/top/right/left), same as FallingCubeFaces.
            float[][] faces =
            {
                new[] { 0f,0f,0f, 1f,0f,0f, 1f,1f,0f, 0f,1f,0f }, // back (-Z)
                new[] { 1f,0f,1f, 0f,0f,1f, 0f,1f,1f, 1f,1f,1f }, // front (+Z)
                new[] { 0f,0f,0f, 1f,0f,0f, 1f,0f,1f, 0f,0f,1f }, // bottom (-Y)
                new[] { 0f,1f,0f, 0f,1f,1f, 1f,1f,1f, 1f,1f,0f }, // top (+Y)
                new[] { 1f,0f,1f, 1f,0f,0f, 1f,1f,0f, 1f,1f,1f }, // right (+X)
                new[] { 0f,0f,0f, 0f,0f,1f, 0f,1f,1f, 0f,1f,0f }, // left (-X)
            };
            Point3D[] faceNormals =
            {
                new Point3D(0,0,-1), new Point3D(0,0,1), new Point3D(0,-1,0),
                new Point3D(0,1,0), new Point3D(1,0,0), new Point3D(-1,0,0),
            };

            // Anchor the shrink to the looked-at face (C++ startBreaking hitNormal): the cube
            // collapses toward the block behind the crosshair instead of the cell center. The hit
            // face plane stays put at the cell wall; the cube's depth along the hit axis shrinks
            // onto it. Perpendicular axes shrink toward the center as before.
            var hitN = _hud.MiningBlockNormal;
            int hitAxis = 0; // 0=X, 1=Y, 2=Z
            float hitSign = 1f;
            if (Math.Abs(hitN.Y) > Math.Abs(hitN.X) && Math.Abs(hitN.Y) > Math.Abs(hitN.Z)) { hitAxis = 1; hitSign = (float)hitN.Y; }
            else if (Math.Abs(hitN.Z) > Math.Abs(hitN.X)) { hitAxis = 2; hitSign = (float)hitN.Z; }
            else hitSign = (float)hitN.X;
            // Corner of the cell the hit face sits on (0 = min, 1 = max).
            // 1 = no anchor (degenerate normal -> old center collapse).
            bool anchored = Math.Abs(hitSign) > 0.5f;
            // C++ BreakingBlockRenderer::renderAdjacentFaces table: neighbor offset, which face of
            // the NEIGHBOR looks into the mined cell, and the directional brightness of that face.
            // faceIndex indexes faces[]/faceNormals[] above.
            (int dx, int dy, int dz, int faceIndex, float brightness)[] neighbors =
            {
                ( 0, 1, 0, 2, 0.5f),  // top -> neighbor BOTTOM (faces down into the cell)
                ( 0,-1, 0, 3, 1.0f),  // bottom -> neighbor TOP
                ( 1, 0, 0, 5, 0.85f), // right -> neighbor LEFT
                (-1, 0, 0, 4, 0.85f), // left -> neighbor RIGHT
                ( 0, 0, 1, 0, 0.92f), // front -> neighbor BACK
                ( 0, 0,-1, 1, 0.92f), // back -> neighbor FRONT
            };

            int vf = 0;

            // The shrinking cube itself (24 verts = quads 0-5).
            for (int face = 0; face < 6; face++)
            {
                var tr = def.FaceTexture(faceNormals[face]);
                uint tileX = (uint)Math.Clamp(tr.X, 0, 255);
                uint tileY = (uint)Math.Clamp(tr.Y, 0, 255);
                uint tileW = (uint)Math.Clamp(Math.Max(1, tr.Width), 0, 255);
                uint tileH = (uint)Math.Clamp(Math.Max(1, tr.Height), 0, 255);
                uint pack2 = (tileX << 24) | (tileY << 16) | (tileW << 8) | tileH;
                uint shadeByte = (uint)Math.Clamp((int)Math.Round(faceShade[face] * lightMult * 255f), 0, 255);
                uint pack3 = shadeByte | (255u << 8); // opaque

                var src = faces[face];
                // UVs from the SAME axis projection the world mesher uses (TryGetCubuildFaceAxes
                // + dot-product), so every face's texture orientation matches terrain exactly.
                // The old fixed du/dv-by-corner-index convention put dv=0.999 at the top of +X/-X
                // side faces, rendering their tiles upside down.
                TryGetCubuildFaceAxes(faceNormals[face], out var uAxis, out var vAxis);
                double minU = double.PositiveInfinity, minV = double.PositiveInfinity;
                for (int ci = 0; ci < 4; ci++)
                {
                    var c = new Point3D(src[ci * 3 + 0], src[ci * 3 + 1], src[ci * 3 + 2]);
                    double u = Dot(c, uAxis), v = Dot(c, vAxis);
                    if (u < minU) minU = u;
                    if (v < minV) minV = v;
                }
                for (int c = 0; c < 4; c++)
                {
                    float u = src[c * 3 + 0]; // 0..1
                    float v = src[c * 3 + 1];
                    float w = src[c * 3 + 2];
                    // Normal uniform shrink toward the cube's own center...
                    float x = center.X + (u * 2f - 1f) * scale * 0.5f;
                    float y = center.Y + (v * 2f - 1f) * scale * 0.5f;
                    float z = center.Z + (w * 2f - 1f) * scale * 0.5f;
                    // ...then slide the whole cube along the hit axis so the face being looked at
                    // stays pinned to the cell wall. At scale 1 the offset is 0 (cube fills the
                    // cell); as it shrinks, the center moves toward that wall by (1-scale)/2.
                    if (anchored)
                    {
                        float slide = (1f - scale) * 0.5f * hitSign;
                        if (hitAxis == 0) x += slide;
                        else if (hitAxis == 1) y += slide;
                        else z += slide;
                    }
                    var corner = new Point3D(src[c * 3 + 0], src[c * 3 + 1], src[c * 3 + 2]);
                    float du = (float)Math.Clamp(Dot(corner, uAxis) - minU, 0.0, 1.0);
                    float dv = (float)Math.Clamp(Dot(corner, vAxis) - minV, 0.0, 1.0);
                    // Never hit exactly 1.0: fract(1.0)==0.0 collapses the quad onto one texel.
                    if (du >= 0.999f) du = 0.999f;
                    if (dv >= 0.999f) dv = 0.999f;
                    uint duFixed = (uint)Math.Clamp((int)Math.Round(du * 256.0), 0, 0xFFFF);
                    uint dvFixed = (uint)Math.Clamp((int)Math.Round(dv * 256.0), 0, 0xFFFF);
                    uint pack1 = (duFixed << 16) | dvFixed;
                    _shrinkCubeVertexScratch[vf++] = x;
                    _shrinkCubeVertexScratch[vf++] = y;
                    _shrinkCubeVertexScratch[vf++] = z;
                    _shrinkCubeVertexScratch[vf++] = BitConverter.UInt32BitsToSingle(pack1);
                    _shrinkCubeVertexScratch[vf++] = BitConverter.UInt32BitsToSingle(pack2);
                    _shrinkCubeVertexScratch[vf++] = BitConverter.UInt32BitsToSingle(pack3);
                }
            }

            // 2) The adjacent faces (24 verts max = quads 6+): for each solid neighbor, draw the
            // face that looks into the mined cell at the FULL cell wall, textured with the
            // NEIGHBOR's tile (the C++ renderAdjacentFaces). The C++ gates walls until the cube
            // shrinks 10% (shrinkAmount > 0.05), which made them feel late and left faces
            // unmasked. The cube draws AFTER with a stronger bias, so it wins any coplanar
            // overlap - walls can appear almost immediately without z-fighting.
            float shrinkAmount = (1f - scale) / 2f;
            bool wallsVisible = shrinkAmount > 0.001f;
            if (wallsVisible && _chunkManager != null)
            {
                int bx = (int)_hud.MiningBlockPos.X;
                int by = (int)_hud.MiningBlockPos.Y;
                int bz = (int)_hud.MiningBlockPos.Z;
                for (int i = 0; i < neighbors.Length; i++)
                {
                    int nx = bx + neighbors[i].dx;
                    int ny = by + neighbors[i].dy;
                    int nz = bz + neighbors[i].dz;
                    if (!_chunkManager.TryGetLoadedBlock(nx, ny, nz, out int nid) || nid <= 0) continue;
                    if (nid == BlockRegistry.GetId("water")) continue; // no wall for fluids

                    var ndef = BlockRegistry.GetById(nid);
                    int nFace = neighbors[i].faceIndex;
                    var tr = ndef.FaceTexture(faceNormals[nFace]);
                    uint tileX = (uint)Math.Clamp(tr.X, 0, 255);
                    uint tileY = (uint)Math.Clamp(tr.Y, 0, 255);
                    uint tileW = (uint)Math.Clamp(Math.Max(1, tr.Width), 0, 255);
                    uint tileH = (uint)Math.Clamp(Math.Max(1, tr.Height), 0, 255);
                    uint pack2 = (tileX << 24) | (tileY << 16) | (tileW << 8) | tileH;
                    uint shadeByte = (uint)Math.Clamp((int)Math.Round(neighbors[i].brightness * lightMult * 255f), 0, 255);
                    uint pack3 = shadeByte | (255u << 8); // opaque

                    var src = faces[nFace];
                    // Same axis-projection UV baking as the cube + world mesher: the wall is the
                    // neighbor's face, so its texture must orient exactly like that face would in
                    // terrain. (The old flipped-V hack was the C++'s vBase=uv.w convention but it
                    // doesn't match the C# mesher's axis-based UVs.)
                    TryGetCubuildFaceAxes(faceNormals[nFace], out var wAxis, out var wVAxis);
                    double wMinU = double.PositiveInfinity, wMinV = double.PositiveInfinity;
                    for (int ci = 0; ci < 4; ci++)
                    {
                        var c = new Point3D(src[ci * 3 + 0], src[ci * 3 + 1], src[ci * 3 + 2]);
                        double u = Dot(c, wAxis), v = Dot(c, wVAxis);
                        if (u < wMinU) wMinU = u;
                        if (v < wMinV) wMinV = v;
                    }
                    // The discard epsilon (0.002) removes a hairline of the NEIGHBOR's
                    // perpendicular faces at the cell corners. Expand the wall's two IN-PLANE axes
                    // past the discarded region so the mask fully covers those slivers - no
                    // hairline cracks around the hole. 0.003 = discard 0.002 + a 0.001 overlap.
                    const float wallEps = 0.003f;
                    for (int c = 0; c < 4; c++)
                    {
                        float u = src[c * 3 + 0]; // 0..1
                        float v = src[c * 3 + 1];
                        float w = src[c * 3 + 2];
                        if (neighbors[i].dx != 0)
                        {
                            // x fixed on the boundary plane; expand y and z.
                            v = -wallEps + v * (1f + 2f * wallEps);
                            w = -wallEps + w * (1f + 2f * wallEps);
                        }
                        else if (neighbors[i].dy != 0)
                        {
                            // y fixed; expand x and z.
                            u = -wallEps + u * (1f + 2f * wallEps);
                            w = -wallEps + w * (1f + 2f * wallEps);
                        }
                        else
                        {
                            // z fixed; expand x and y.
                            u = -wallEps + u * (1f + 2f * wallEps);
                            v = -wallEps + v * (1f + 2f * wallEps);
                        }
                        float x = bx + neighbors[i].dx + u;
                        float y = by + neighbors[i].dy + v;
                        float z = bz + neighbors[i].dz + w;
                        var corner = new Point3D(src[c * 3 + 0], src[c * 3 + 1], src[c * 3 + 2]);
                        float du = (float)Math.Clamp(Dot(corner, wAxis) - wMinU, 0.0, 1.0);
                        float dv = (float)Math.Clamp(Dot(corner, wVAxis) - wMinV, 0.0, 1.0);
                        if (du >= 0.999f) du = 0.999f;
                        if (dv >= 0.999f) dv = 0.999f;
                        uint duFixed = (uint)Math.Clamp((int)Math.Round(du * 256.0), 0, 0xFFFF);
                        uint dvFixed = (uint)Math.Clamp((int)Math.Round(dv * 256.0), 0, 0xFFFF);
                        uint pack1 = (duFixed << 16) | dvFixed;
                        _shrinkCubeVertexScratch[vf++] = x;
                        _shrinkCubeVertexScratch[vf++] = y;
                        _shrinkCubeVertexScratch[vf++] = z;
                        _shrinkCubeVertexScratch[vf++] = BitConverter.UInt32BitsToSingle(pack1);
                        _shrinkCubeVertexScratch[vf++] = BitConverter.UInt32BitsToSingle(pack2);
                        _shrinkCubeVertexScratch[vf++] = BitConverter.UInt32BitsToSingle(pack3);
                    }
                }
            }

            _gd.UpdateBuffer(_shrinkCubeVertexBuffer, 0, _shrinkCubeVertexScratch);

            // Draw order matches the C++ (adjacent faces FIRST, then the shrinking block): the
            // walls (weaker bias) show through only where the cube has shrunk away; the cube
            // (stronger bias, drawn after) wins everywhere it still covers.
            // indexStart=36: the index buffer is 12 quads (0-5 cube, 6-11 walls); wall quads
            // start at element 36. Starting at 24 (quad 4) drew two cube faces and dropped the
            // last two wall quads - the front/back (north/south) masks.
            int wallQuads = vf / (4 * 6) - 6;
            if (wallQuads > 0 && _shrinkWallPipeline != null)
            {
                cl.SetPipeline(_shrinkWallPipeline);
                cl.SetGraphicsResourceSet(0, _projViewSet);
                if (_textureSet != null) cl.SetGraphicsResourceSet(1, _textureSet);
                cl.SetGraphicsResourceSet(2, _fogSet);
                cl.SetVertexBuffer(0, _shrinkCubeVertexBuffer);
                cl.SetIndexBuffer(_shrinkCubeIndexBuffer, IndexFormat.UInt16);
                cl.DrawIndexed((uint)(wallQuads * 6), 1, 36, 0, 0);
            }

            cl.SetPipeline(_shrinkCubePipeline ?? _pipeline);
            cl.SetGraphicsResourceSet(0, _projViewSet);
            if (_textureSet != null) cl.SetGraphicsResourceSet(1, _textureSet);
            cl.SetGraphicsResourceSet(2, _fogSet);
            cl.SetVertexBuffer(0, _shrinkCubeVertexBuffer);
            cl.SetIndexBuffer(_shrinkCubeIndexBuffer, IndexFormat.UInt16);
            cl.DrawIndexed(36, 1, 0, 0, 0);
        }

        private void DrawChunkBorders(CommandList cl)
        {
            if (!_hud.ShowDebug || _chunkBorderPipeline == null)
            {
                return;
            }

            int vertexIndex = 0;
            int chunkSize = ChunkManager.ChunkSize;
            int chunkHeight = ChunkManager.ChunkHeight;

            // Size the scratch + GPU buffer for every chunk in the render radius: each chunk
            // draws 12 border lines = 72 floats. The old fixed 768-float buffer silently dropped
            // lines once full - which left only the far chunks (drawn first) visible.
            int chunksWide = (2 * _hud.RenderDistance + 1) * (2 * _hud.RenderDistance + 1);
            int neededFloats = chunksWide * 12 * 6;
            if (_chunkBorderVertexScratch.Length < neededFloats)
            {
                _chunkBorderVertexScratch = new float[neededFloats];
                _chunkBorderVertexBuffer?.Dispose();
                _chunkBorderVertexBuffer = _gd.ResourceFactory.CreateBuffer(new BufferDescription(
                    (uint)(neededFloats * sizeof(float)), BufferUsage.VertexBuffer | BufferUsage.Dynamic));
            }

            // Draw chunk borders for loaded chunks around player
            for (int dz = -_hud.RenderDistance; dz <= _hud.RenderDistance; dz++)
            {
                for (int dx = -_hud.RenderDistance; dx <= _hud.RenderDistance; dx++)
                {
                    int chunkX = _hud.PlayerChunkX + dx;
                    int chunkZ = _hud.PlayerChunkZ + dz;

                    // Calculate chunk world bounds
                    float minX = chunkX * chunkSize;
                    float maxX = minX + chunkSize;
                    float minZ = chunkZ * chunkSize;
                    float maxZ = minZ + chunkSize;
                    float minY = ChunkManager.WorldOriginY;
                    float maxY = ChunkManager.WorldOriginY + chunkHeight;

                    // Add vertical edges (4 corners)
                    AddLine(minX, minY, minZ, minX, maxY, minZ, ref vertexIndex);
                    AddLine(maxX, minY, minZ, maxX, maxY, minZ, ref vertexIndex);
                    AddLine(minX, minY, maxZ, minX, maxY, maxZ, ref vertexIndex);
                    AddLine(maxX, minY, maxZ, maxX, maxY, maxZ, ref vertexIndex);

                    // Add horizontal edges at bottom
                    AddLine(minX, minY, minZ, maxX, minY, minZ, ref vertexIndex);
                    AddLine(minX, minY, maxZ, maxX, minY, maxZ, ref vertexIndex);
                    AddLine(minX, minY, minZ, minX, minY, maxZ, ref vertexIndex);
                    AddLine(maxX, minY, minZ, maxX, minY, maxZ, ref vertexIndex);

                    // Add horizontal edges at top
                    AddLine(minX, maxY, minZ, maxX, maxY, minZ, ref vertexIndex);
                    AddLine(minX, maxY, maxZ, maxX, maxY, maxZ, ref vertexIndex);
                    AddLine(minX, maxY, minZ, minX, maxY, maxZ, ref vertexIndex);
                    AddLine(maxX, maxY, minZ, maxX, maxY, maxZ, ref vertexIndex);
                }
            }

            if (vertexIndex > 0)
            {
                _gd.UpdateBuffer(_chunkBorderVertexBuffer, 0, _chunkBorderVertexScratch);

                cl.SetPipeline(_chunkBorderPipeline);
                cl.SetGraphicsResourceSet(0, _projViewSet);
                cl.SetVertexBuffer(0, _chunkBorderVertexBuffer);
                cl.Draw((uint)vertexIndex / 3, 1, 0, 0);
            }
        }

        private void AddLine(float x1, float y1, float z1, float x2, float y2, float z2, ref int vertexIndex)
        {
            if (vertexIndex + 6 > _chunkBorderVertexScratch.Length)
                return;

            _chunkBorderVertexScratch[vertexIndex++] = x1;
            _chunkBorderVertexScratch[vertexIndex++] = y1;
            _chunkBorderVertexScratch[vertexIndex++] = z1;
            _chunkBorderVertexScratch[vertexIndex++] = x2;
            _chunkBorderVertexScratch[vertexIndex++] = y2;
            _chunkBorderVertexScratch[vertexIndex++] = z2;
        }

        private void DrawDucks(CommandList cl)
        {
            var instances = _duckInstances;
            if (instances.Count == 0 || _modelPipeline == null || _duckTextureSet == null
                || _duckBones.Length == 0 || _duckVertsPerInstance == 0)
            {
                return;
            }

            int totalVertexFloats = instances.Count * _duckVertsPerInstance * DuckFloatsPerVertex;
            int totalIndices = instances.Count * _duckIndicesPerInstance;

            if (_duckVertexScratch.Length < totalVertexFloats)
            {
                _duckVertexScratch = new float[totalVertexFloats];
            }
            if (_duckIndexScratch.Length < totalIndices)
            {
                _duckIndexScratch = new ushort[totalIndices];
            }

            int vf = 0;
            int ii = 0;
            ushort baseVertex = 0;
            foreach (var inst in instances)
            {
                _entityLight = GetMobLight(inst.Position.X, inst.Position.Y, inst.Position.Z);
                WriteDuck(inst, ref vf, ref ii, ref baseVertex);
            }

            EnsureDuckBuffers((uint)(totalVertexFloats * sizeof(float)), (uint)(totalIndices * sizeof(ushort)));
            _gd.UpdateBuffer(_duckVertexBuffer, 0, ref _duckVertexScratch[0], (uint)(totalVertexFloats * sizeof(float)));
            _gd.UpdateBuffer(_duckIndexBuffer, 0, ref _duckIndexScratch[0], (uint)(totalIndices * sizeof(ushort)));

            cl.SetPipeline(_modelPipeline);
            cl.SetGraphicsResourceSet(0, _projViewSet);
            cl.SetGraphicsResourceSet(1, _duckTextureSet);
            cl.SetVertexBuffer(0, _duckVertexBuffer);
            cl.SetIndexBuffer(_duckIndexBuffer, IndexFormat.UInt16);
            cl.DrawIndexed((uint)totalIndices, 1, 0, 0, 0);
        }

        // Poses one duck's bones (walk/flap/head-turn) and bakes them, with the body yaw, in-air /
        // death tilt and hurt-flash tint, into the shared vertex/index scratch buffers. Mirrors
        // Cubuild's updateDuckEntityVisual ('blockbench_duck' branch).
        private void WriteDuck(in Cubuild.DuckInstance inst, ref int vf, ref int ii, ref ushort baseVertex)
        {
            bool isDead = inst.IsDead;
            float walkPhase = inst.WalkPhase;
            float walkAmount = inst.WalkAmount;
            float flapPhase = inst.FlapPhase;

            float wingSwing = isDead ? 0f : (inst.OnGround ? (float)Math.Sin(walkPhase) * 0.55f * walkAmount : (float)Math.Sin(flapPhase) * 0.95f);
            float swing = isDead ? 0f : (float)Math.Sin(walkPhase) * 0.55f * walkAmount;
            float bob = isDead ? 0f : (Math.Abs((float)Math.Sin(walkPhase * 2.0f)) * 0.06f * walkAmount
                + (!inst.OnGround ? 0.03f + Math.Abs((float)Math.Sin(flapPhase * 0.5f)) * 0.03f : 0f));
            float hurtTilt = isDead ? 0f : (inst.HurtTimer > 0f ? (float)Math.Sin(inst.HurtTimer * 60.0f) * 0.06f : 0f);
            float deathRoll = isDead ? inst.DeathRollDir * (float)(Math.PI * 0.5) * (float)Math.Pow(inst.DeathT, 0.9) : 0f;

            float tiltZ = isDead ? deathRoll : ((inst.OnGround ? 0f : Math.Clamp(-inst.VelocityY * 0.03f, -0.2f, 0.2f)) + hurtTilt);
            float cosR = (float)Math.Cos(tiltZ), sinR = (float)Math.Sin(tiltZ);

            float renderYaw = inst.Yaw + (float)Math.PI;
            float cosY = (float)Math.Cos(renderYaw), sinY = (float)Math.Sin(renderYaw);

            float px = (float)inst.Position.X;
            float py = (float)inst.Position.Y;
            float pz = (float)inst.Position.Z;

            // Hurt / death flash: red channel unchanged, green/blue driven toward the tint.
            float blink = isDead ? 1f : (inst.HurtTimer > 0f ? ((float)Math.Sin(inst.HurtTimer * 95.0f) > 0f ? 1f : 0.72f) : 0f);
            float flashBlend = isDead ? 1f : (inst.HurtTimer > 0f ? Math.Clamp((inst.HurtTimer / 0.20f) * blink, 0f, 1f) : 0f);
            float gbMul = 1f - 0.82f * flashBlend;

            foreach (var bone in _duckBones)
            {
                float angle = bone.BaseAngle + BoneAnimDelta(bone.Id, wingSwing, swing, walkAmount, inst.HeadYawLocal);
                float ca = (float)Math.Cos(angle), sa = (float)Math.Sin(angle);
                float headExtraBob = bone.Id == DuckBoneId.Head ? bob * 0.15f : 0f;

                foreach (var v in bone.Vertices)
                {
                    // Rotate the vertex about the bone pivot on the bone's animation axis.
                    float lx = v.X - bone.PivotX;
                    float ly = v.Y - bone.PivotY;
                    float lz = v.Z - bone.PivotZ;
                    float rx = lx, ry = ly, rz = lz;
                    switch (bone.Axis)
                    {
                        case DuckBoneAxis.X: ry = ly * ca - lz * sa; rz = ly * sa + lz * ca; break;
                        case DuckBoneAxis.Y: rx = lx * ca + lz * sa; rz = -lx * sa + lz * ca; break;
                        case DuckBoneAxis.Z: rx = lx * ca - ly * sa; ry = lx * sa + ly * ca; break;
                    }
                    // Head pitch: rotate around X axis for looking up/down.
                    // AI gives positive=up, renderer convention is negative=up.
                    if (bone.Id == DuckBoneId.Head && inst.HeadPitchLocal != 0f)
                    {
                        float cp = (float)Math.Cos(-inst.HeadPitchLocal);
                        float sp = (float)Math.Sin(-inst.HeadPitchLocal);
                        float pry = ry * cp + rz * sp;
                        float prz = -ry * sp + rz * cp;
                        ry = pry; rz = prz;
                    }
                    float mx = bone.PivotX + rx;
                    float my = bone.PivotY + ry + bob + headExtraBob;
                    float mz = bone.PivotZ + rz;

            // Body roll (Z) then body yaw (Y), matching three.js Euler 'XYZ' order.
            float ax = mx * cosR - my * sinR;
            float ay = mx * sinR + my * cosR;
            float az = mz;
            float fx = ax * cosY + az * sinY;
            float fz = -ax * sinY + az * cosY;

            // Mobs are bigger now: scale the whole model about its feet origin.
            fx *= DuckModelScale;
            ay *= DuckModelScale;
            fz *= DuckModelScale;

            _duckVertexScratch[vf++] = px + fx;
            _duckVertexScratch[vf++] = py + ay;
            _duckVertexScratch[vf++] = pz + fz;
                    _duckVertexScratch[vf++] = v.U;
                    _duckVertexScratch[vf++] = v.V;
                    _duckVertexScratch[vf++] = v.Shade * _entityLight;
                    _duckVertexScratch[vf++] = v.Shade * gbMul * _entityLight;
                    _duckVertexScratch[vf++] = v.Shade * gbMul * _entityLight;
                    _duckVertexScratch[vf++] = 1f;
                }

                for (int k = 0; k < bone.Indices.Length; k++)
                {
                    _duckIndexScratch[ii++] = (ushort)(bone.Indices[k] + baseVertex);
                }
                baseVertex += (ushort)bone.Vertices.Length;
            }
        }

        private static float BoneAnimDelta(DuckBoneId id, float wingSwing, float swing, float walkAmount, float headYawLocal)
        {
            switch (id)
            {
                case DuckBoneId.Head: return headYawLocal;
                case DuckBoneId.LeftWing: return -0.16f - wingSwing * 0.35f;
                case DuckBoneId.RightWing: return 0.16f + wingSwing * 0.35f;
                case DuckBoneId.LeftFoot: return swing * 1.25f;
                case DuckBoneId.RightFoot: return -swing * 1.25f;
                case DuckBoneId.Tail: return -0.12f * walkAmount;
                default: return 0f;
            }
        }

        private void EnsureDuckBuffers(uint vbSize, uint ibSize)
        {
            if (_duckVertexBuffer == null || _duckVertexCapacity < vbSize)
            {
                _duckVertexBuffer?.Dispose();
                _duckVertexBuffer = _gd.ResourceFactory.CreateBuffer(new BufferDescription(vbSize, BufferUsage.VertexBuffer | BufferUsage.Dynamic));
                _duckVertexCapacity = vbSize;
            }
            if (_duckIndexBuffer == null || _duckIndexCapacity < ibSize)
            {
                _duckIndexBuffer?.Dispose();
                _duckIndexBuffer = _gd.ResourceFactory.CreateBuffer(new BufferDescription(ibSize, BufferUsage.IndexBuffer | BufferUsage.Dynamic));
                _duckIndexCapacity = ibSize;
            }
        }

        // Uploads one chunk's vertices/indices into a region of the shared mega vertex/index buffers,
        // pooling the previous buffers for reuse.
        // Uploads one chunk's vertices/indices into a region of the shared mega vertex/index buffers,
// reusing freed holes or appending at the tail. The previous range (if any) is recycled.
    }
}