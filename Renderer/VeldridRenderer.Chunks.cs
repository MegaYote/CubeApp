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
        private void WriteChunkData(Cubuild.ChunkCoordinates coord, uint[] verts, ushort[] indices,
            uint[] cutoutVerts, ushort[] cutoutIndices, uint[] glassVerts, ushort[] glassIndices,
            uint[] transVerts, ushort[] transIndices)
        {
            long tWrite = PerfLog ? System.Diagnostics.Stopwatch.GetTimestamp() : 0; // TEMP streaming diagnostics

            if (_chunkRanges.TryGetValue(coord, out var prev))
            {
                FreeRange(prev);
            }
            if (_cutoutRanges.TryGetValue(coord, out var prevCutout))
            {
                FreeRange(prevCutout);
            }
            if (_glassRanges.TryGetValue(coord, out var prevGlass))
            {
                FreeRange(prevGlass);
            }
            if (_transparentRanges.TryGetValue(coord, out var prevTrans))
            {
                FreeRange(prevTrans);
            }

            uint vbBytes = (uint)(verts.Length * sizeof(float));
            uint ibBytes = (uint)(indices.Length * sizeof(ushort));
            var (vbo, _, ibo, _) = AllocateRange(vbBytes, ibBytes);

            _gd.UpdateBuffer(_megaVertexBuffer, vbo, verts);
            _gd.UpdateBuffer(_megaIndexBuffer, ibo, indices);

            _chunkRanges[coord] = new ChunkRange { VbOffsetBytes = vbo, VbBytes = vbBytes, IbOffsetBytes = ibo, IndexCount = (uint)indices.Length };

            // Cutout (cross plants / leaves) faces: only when the chunk actually has any.
            if (cutoutVerts != null && cutoutVerts.Length > 0 && cutoutIndices != null && cutoutIndices.Length > 0)
            {
                uint cvbBytes = (uint)(cutoutVerts.Length * sizeof(float));
                uint cibBytes = (uint)(cutoutIndices.Length * sizeof(ushort));
                var (cvbo, _, cibo, _) = AllocateRange(cvbBytes, cibBytes);

                _gd.UpdateBuffer(_megaVertexBuffer, cvbo, cutoutVerts);
                _gd.UpdateBuffer(_megaIndexBuffer, cibo, cutoutIndices);

                _cutoutRanges[coord] = new ChunkRange { VbOffsetBytes = cvbo, VbBytes = cvbBytes, IbOffsetBytes = cibo, IndexCount = (uint)cutoutIndices.Length };
            }
            else
            {
                _cutoutRanges.Remove(coord);
            }

            // Glass faces: only when the chunk actually has any.
            if (glassVerts != null && glassVerts.Length > 0 && glassIndices != null && glassIndices.Length > 0)
            {
                uint gvbBytes = (uint)(glassVerts.Length * sizeof(float));
                uint gibBytes = (uint)(glassIndices.Length * sizeof(ushort));
                var (gvbo, _, gibo, _) = AllocateRange(gvbBytes, gibBytes);

                _gd.UpdateBuffer(_megaVertexBuffer, gvbo, glassVerts);
                _gd.UpdateBuffer(_megaIndexBuffer, gibo, glassIndices);

                _glassRanges[coord] = new ChunkRange { VbOffsetBytes = gvbo, VbBytes = gvbBytes, IbOffsetBytes = gibo, IndexCount = (uint)glassIndices.Length };
            }
            else
            {
                _glassRanges.Remove(coord);
            }

            // Transparent (water) faces: only when the chunk actually has any.
            if (transVerts != null && transVerts.Length > 0 && transIndices != null && transIndices.Length > 0)
            {
                uint tvbBytes = (uint)(transVerts.Length * sizeof(float));
                uint tibBytes = (uint)(transIndices.Length * sizeof(ushort));
                var (tvbo, _, tibo, _) = AllocateRange(tvbBytes, tibBytes);

                _gd.UpdateBuffer(_megaVertexBuffer, tvbo, transVerts);
                _gd.UpdateBuffer(_megaIndexBuffer, tibo, transIndices);

                _transparentRanges[coord] = new ChunkRange { VbOffsetBytes = tvbo, VbBytes = tvbBytes, IbOffsetBytes = tibo, IndexCount = (uint)transIndices.Length };
            }
            else
            {
                _transparentRanges.Remove(coord);
            }

            // Mark the pass lists dirty; Render() rebuilds them once per frame from the range
            // dictionaries (see RebuildDrawCommands). This used to sync each pass incrementally
            // here (FPS roadmap #4), but that was an O(n) linear scan of the whole pass list per
            // pass - four scans per chunk, growing with the render distance. At the 64-chunk
            // Extreme preset (24k commands) a burst of ~200 unloads in one frame meant ~19M
            // struct comparisons and a 40-60ms stall, which is exactly the fps collapse seen
            // while flying. The lists are re-sorted back-to-front every frame anyway (SortPass*),
            // so incremental ordering bought nothing; one O(n) rebuild per dirty frame is far
            // cheaper than O(n) per chunk. Counts here are tiny (logical chunks, not commands).
            _drawCommandsDirty = true;
            _gpuCullDataDirty = true;
            if (PerfLog) // TEMP streaming diagnostics
            {
                double d = MsSince(tWrite);
                if (d > _perfWriteCallMsMax) _perfWriteCallMsMax = d;
            }
        }

        // First-fit allocator: reuse a freed hole if one's big enough, else append at the tail
        // (growing the GPU buffers 2x if the tail would overflow).
        private (uint vbo, uint vbBytes, uint ibo, uint ibBytes) AllocateRange(uint vbBytes, uint ibBytes)
        {
            for (int i = 0; i < _freeBlocks.Count; i++)
            {
                var b = _freeBlocks[i];
                if (b.VbBytes >= vbBytes && b.IbBytes >= ibBytes)
                {
                    _freeBlocks.RemoveAt(i);
                    return (b.VbOffset, vbBytes, b.IbOffset, ibBytes);
                }
            }

            EnsureVertexCapacity(_vbTailBytes + vbBytes);
            EnsureIndexCapacity(_ibTailBytes + ibBytes);
            uint vbo = _vbTailBytes;
            uint ibo = _ibTailBytes;
            _vbTailBytes += vbBytes;
            _ibTailBytes += ibBytes;
            return (vbo, vbBytes, ibo, ibBytes);
        }

        private void FreeRange(ChunkRange r)
        {
            // Callers (WriteChunkData / FreeChunkRange) sync the affected chunk's draw commands
            // incrementally - no full-rebuild flag here (FPS roadmap #4).
            _freeBlocks.Add((r.VbOffsetBytes, r.VbBytes, r.IbOffsetBytes, r.IndexCount * sizeof(ushort)));
        }

        private void FreeChunkRange(Cubuild.ChunkCoordinates coord)
        {
            if (_chunkRanges.TryGetValue(coord, out var r))
            {
                FreeRange(r);
                _chunkRanges.Remove(coord);
            }
            if (_cutoutRanges.TryGetValue(coord, out var cr))
            {
                FreeRange(cr);
                _cutoutRanges.Remove(coord);
            }
            if (_glassRanges.TryGetValue(coord, out var gr))
            {
                FreeRange(gr);
                _glassRanges.Remove(coord);
            }
            if (_transparentRanges.TryGetValue(coord, out var tr))
            {
                FreeRange(tr);
                _transparentRanges.Remove(coord);
            }

            // Drop this chunk's commands from every pass via the per-frame rebuild (see
            // WriteChunkData for why the old incremental O(n)-per-chunk sync was removed).
            _drawCommandsDirty = true;
            _gpuCullDataDirty = true;
        }

        // Grows the mega vertex buffer to 2x (or to the needed size) when the tail would overflow.
        // Records a GPU CopyBuffer of the live region [0, tail) so the old data survives the swap;
        // the old buffer is disposed once the GPU is finished with it.
        private void EnsureVertexCapacity(uint needed)
        {
            if (_megaVertexBuffer != null && _vbCapacityBytes >= needed) return;
            _perfBufferGrowths++; // TEMP streaming diagnostics
            uint newCap = Math.Max(needed, Math.Max(1024, _vbCapacityBytes * 2));
            var newBuf = _gd.ResourceFactory.CreateBuffer(new BufferDescription(newCap, BufferUsage.VertexBuffer));
            if (_megaVertexBuffer != null)
            {
                _pendingBufferCopies.Add((_megaVertexBuffer, newBuf, _vbTailBytes));
            }
            _megaVertexBuffer = newBuf;
            _vbCapacityBytes = newCap;
        }

        private void EnsureIndexCapacity(uint needed)
        {
            if (_megaIndexBuffer != null && _ibCapacityBytes >= needed) return;
            _perfBufferGrowths++; // TEMP streaming diagnostics
            uint newCap = Math.Max(needed, Math.Max(1024, _ibCapacityBytes * 2));
            var newBuf = _gd.ResourceFactory.CreateBuffer(new BufferDescription(newCap, BufferUsage.IndexBuffer));
            if (_megaIndexBuffer != null)
            {
                _pendingBufferCopies.Add((_megaIndexBuffer, newBuf, _ibTailBytes));
            }
            _megaIndexBuffer = newBuf;
            _ibCapacityBytes = newCap;
        }

        // Creates (or grows) the indirect argument buffer. D3D11 requires indirect-args buffers
        // to be USAGE_DEFAULT (no Dynamic flag), so contents are refreshed via CommandList.UpdateBuffer.
        private void EnsureIndirectCapacity(uint commandCount)
        {
            if (_indirectBuffer != null && _indirectCapacityCommands >= commandCount) return;

            uint newCap = _indirectCapacityCommands == 0
                ? Math.Max(256, commandCount * 2)
                : Math.Max(_indirectCapacityCommands * 2, commandCount);
            var newBuf = _gd.ResourceFactory.CreateBuffer(new BufferDescription(
                newCap * IndirectCommandStride, BufferUsage.IndirectBuffer));
            if (_indirectBuffer != null)
            {
                _gd.DisposeWhenIdle(_indirectBuffer);
            }
            _indirectBuffer = newBuf;
            _indirectCapacityCommands = newCap;
        }

        // F7 debug cycle: CPU -> GPU -> CPU (explicit modes, bypassing Auto). No-op if the
        // device lacks compute/structured-buffer/indirect support (D3D11 always has them).
        // Invalidates all cached cull data so the next frame refills it from the rebuilt commands.
        public void ToggleGpuCulling()
        {
            if (!_gpuCullSupported) return;
            _cullMode = _gpuCullEnabled ? CullingMode.Cpu : CullingMode.Gpu;
            ApplyCullingMode();
        }

        // Grows the cull-data and args-output buffers so they hold at least `commands` entries.
        // Resource sets are recreated because they capture the buffer instance.
        private void EnsureCullCapacity(uint commands)
        {
            if (_cullDataBuffer != null && _cullDataCapacityCommands >= commands) return;

            uint newCap = _cullDataCapacityCommands == 0
                ? Math.Max(256, commands * 2)
                : Math.Max(_cullDataCapacityCommands * 2, commands);
            // Shader reads both as flat uint[]; 16 uints per chunk for data, 5 uints (20 bytes)
            // per command for args (see CreateCullComputePipelineCore).
            const uint cullDataStride = sizeof(uint);
            const uint cullArgsStride = sizeof(uint);
            var newData = _gd.ResourceFactory.CreateBuffer(new BufferDescription(
                newCap * 16 * sizeof(uint), BufferUsage.StructuredBufferReadOnly, cullDataStride));
            var newArgs = _gd.ResourceFactory.CreateBuffer(new BufferDescription(
                newCap * IndirectCommandStride, BufferUsage.StructuredBufferReadWrite, cullArgsStride));
            if (_cullDataBuffer != null) _gd.DisposeWhenIdle(_cullDataBuffer);
            if (_cullArgsBuffer != null) _gd.DisposeWhenIdle(_cullArgsBuffer);
            _cullDataBuffer = newData;
            _cullArgsBuffer = newArgs;
            _cullDataReadSet?.Dispose();
            _cullArgsWriteSet?.Dispose();
            _cullDataReadSet = _gd.ResourceFactory.CreateResourceSet(new ResourceSetDescription(_cullChunkLayout, _cullDataBuffer, _cullArgsBuffer));
            _cullArgsWriteSet = _gd.ResourceFactory.CreateResourceSet(new ResourceSetDescription(_cullChunkLayout, _cullDataBuffer, _cullArgsBuffer));
            _cullDataCapacityCommands = newCap;
        }

        // Packs one pass's draw commands into the GPU cull-data layout. The shader struct is
        // std430: vec4 aabbMin + vec4 aabbMax + uvec4 cmd + uint firstInstance = 16 uint32s per
        // chunk. The array is sized exactly to the command count so it can be uploaded whole.
        private void FillCullData(
            System.Collections.Generic.List<(Cubuild.ChunkCoordinates Coord, IndirectDrawIndexedArguments Cmd)> commands,
            ref uint[] target)
        {
            int count = commands.Count;
            if (target.Length != count * 16)
            {
                target = new uint[count * 16];
            }
            for (int i = 0; i < count; i++)
            {
                var (coord, cmd) = commands[i];
                float minX = coord.X * ChunkManager.ChunkSize;
                float maxX = minX + ChunkManager.ChunkSize;
                float minZ = coord.Z * ChunkManager.ChunkSize;
                float maxZ = minZ + ChunkManager.ChunkSize;
                // Match ChunkInFrustum: layer-based Y bounds (ground -256..383, sky 384..1023).
                float minY = ChunkManager.OriginYForLayer(coord.Layer);
                float maxY = minY + ChunkManager.HeightForLayer(coord.Layer);

                int o = i * 16;
                target[o + 0] = BitConverter.SingleToUInt32Bits(minX);
                target[o + 1] = BitConverter.SingleToUInt32Bits(minY);
                target[o + 2] = BitConverter.SingleToUInt32Bits(minZ);
                target[o + 3] = 0; // vec4.w unused
                target[o + 4] = BitConverter.SingleToUInt32Bits(maxX);
                target[o + 5] = BitConverter.SingleToUInt32Bits(maxY);
                target[o + 6] = BitConverter.SingleToUInt32Bits(maxZ);
                target[o + 7] = 0; // vec4.w unused
                target[o + 8] = cmd.IndexCount;
                target[o + 9] = cmd.InstanceCount;
                target[o + 10] = cmd.FirstIndex;
                target[o + 11] = unchecked((uint)cmd.VertexOffset);
                target[o + 12] = cmd.FirstInstance;
                target[o + 13] = 0; // pad
                target[o + 14] = 0; // pad
                target[o + 15] = 0; // pad
            }
        }

        // Runs the GPU-cull compute pass for one draw pass. All four passes share ONE cull-data
        // buffer, so each pass must re-upload ITS OWN data via the CommandList (recorded in-order
        // before its dispatch) - a GraphicsDevice-level upload executes immediately and would be
        // overwritten by the last pass, making every dispatch read the wrong AABBs.
        private void ApplyHeightmapOcclusion(
            System.Collections.Generic.List<(Cubuild.ChunkCoordinates Coord, IndirectDrawIndexedArguments Cmd)> commands,
            ref uint[] cullData)
        {
            // cullData is indexed the same as commands (i*16 per chunk, InstanceCount at +9).
            if (cullData.Length < commands.Count * 16) return;
            for (int i = 0; i < commands.Count; i++)
            {
                if (IsHeightmapOccluded(commands[i].Coord))
                {
                    cullData[i * 16 + 9] = 0; // InstanceCount = 0 -> skipped by the GPU draw
                }
            }
        }

        private void RunGpuCull(
            CommandList cl,
            System.Collections.Generic.List<(Cubuild.ChunkCoordinates Coord, IndirectDrawIndexedArguments Cmd)> commands,
            ref uint[] cullData)
        {
            if (commands.Count == 0) return;
            EnsureCullCapacity((uint)commands.Count);
            EnsureIndirectCapacity((uint)commands.Count);

            // Rebuild this pass's CPU scratch only when its commands changed; the buffer itself
            // is re-uploaded every frame because all passes share it.
            if (_gpuCullDataDirty || cullData.Length == 0)
            {
                FillCullData(commands, ref cullData);
            }
            // Per-frame heightmap occlusion: zero the InstanceCount of chunks hidden behind
            // terrain. The GPU shader copies InstanceCount through, so 0 = skipped (Sodium-style
            // occlusion culling composes with the GPU frustum test).
            ApplyHeightmapOcclusion(commands, ref cullData);
            cl.UpdateBuffer(_cullDataBuffer, 0, cullData);

            // Update the frustum planes (row-vector view-projection -> 6 clip planes). Same
            // upload reasoning: record through the CommandList so it's ordered before the dispatch.
            if (_viewProjection.HasValue)
            {
                ExtractFrustumPlanes(_viewProjection.Value);
                for (int i = 0; i < 6; i++)
                {
                    _cullPlaneFloats[i * 4 + 0] = _frustumPlanes[i].X;
                    _cullPlaneFloats[i * 4 + 1] = _frustumPlanes[i].Y;
                    _cullPlaneFloats[i * 4 + 2] = _frustumPlanes[i].Z;
                    _cullPlaneFloats[i * 4 + 3] = _frustumPlanes[i].W;
                }
                cl.UpdateBuffer(_frustumBuffer, 0, _cullPlaneFloats);
            }

            cl.SetPipeline(_cullPipeline);
            cl.SetComputeResourceSet(0, _frustumSet);
            cl.SetComputeResourceSet(1, _cullArgsWriteSet);
            uint groups = (uint)((commands.Count + 63) / 64);
            cl.Dispatch(groups, 1, 1);

            // Copy the compute-written args into the indirect buffer for the draw.
            cl.CopyBuffer(_cullArgsBuffer, 0, _indirectBuffer, 0, (uint)commands.Count * IndirectCommandStride);
        }

        // Rebuilds all four pass lists from the range dictionaries. Called once per frame when a
        // chunk was uploaded or unloaded (see WriteChunkData / FreeChunkRange), which replaced the
        // old per-chunk incremental sync - that scanned each pass list per pass, so its cost grew
        // with the render distance and caused 40-60ms stalls while flying at the 64-chunk preset.
        private void RebuildDrawCommands()
        {
            _drawCommands.Clear();
            _gpuCullDataDirty = true;
            _opaqueCullData = Array.Empty<uint>();
            _cutoutCullData = Array.Empty<uint>();
            _glassCullData = Array.Empty<uint>();
            _transparentCullData = Array.Empty<uint>();
            foreach (var kv in _chunkRanges)
            {
                var r = kv.Value;
                _drawCommands.Add((kv.Key, new IndirectDrawIndexedArguments
                {
                    IndexCount = r.IndexCount,
                    InstanceCount = 1,
                    FirstIndex = r.IbOffsetBytes / 2,           // ushort index units
                    VertexOffset = (int)(r.VbOffsetBytes / VertexStrideBytes),
                    FirstInstance = 0,
                }));
            }
            if (_indirectScratch.Length < _drawCommands.Count)
            {
                _indirectScratch = new IndirectDrawIndexedArguments[Math.Max(256, _drawCommands.Count * 2)];
            }

            _cutoutDrawCommands.Clear();
            foreach (var kv in _cutoutRanges)
            {
                var r = kv.Value;
                _cutoutDrawCommands.Add((kv.Key, new IndirectDrawIndexedArguments
                {
                    IndexCount = r.IndexCount,
                    InstanceCount = 1,
                    FirstIndex = r.IbOffsetBytes / 2,           // ushort index units
                    VertexOffset = (int)(r.VbOffsetBytes / VertexStrideBytes),
                    FirstInstance = 0,
                }));
            }
            if (_cutoutIndirectScratch.Length < _cutoutDrawCommands.Count)
            {
                _cutoutIndirectScratch = new IndirectDrawIndexedArguments[Math.Max(256, _cutoutDrawCommands.Count * 2)];
            }

            _glassDrawCommands.Clear();
            foreach (var kv in _glassRanges)
            {
                var r = kv.Value;
                _glassDrawCommands.Add((kv.Key, new IndirectDrawIndexedArguments
                {
                    IndexCount = r.IndexCount,
                    InstanceCount = 1,
                    FirstIndex = r.IbOffsetBytes / 2,           // ushort index units
                    VertexOffset = (int)(r.VbOffsetBytes / VertexStrideBytes),
                    FirstInstance = 0,
                }));
            }
            if (_glassIndirectScratch.Length < _glassDrawCommands.Count)
            {
                _glassIndirectScratch = new IndirectDrawIndexedArguments[Math.Max(256, _glassDrawCommands.Count * 2)];
            }

            _transparentDrawCommands.Clear();
            foreach (var kv in _transparentRanges)
            {
                var r = kv.Value;
                _transparentDrawCommands.Add((kv.Key, new IndirectDrawIndexedArguments
                {
                    IndexCount = r.IndexCount,
                    InstanceCount = 1,
                    FirstIndex = r.IbOffsetBytes / 2,           // ushort index units
                    VertexOffset = (int)(r.VbOffsetBytes / VertexStrideBytes),
                    FirstInstance = 0,
                }));
            }
            if (_transparentIndirectScratch.Length < _transparentDrawCommands.Count)
            {
                _transparentIndirectScratch = new IndirectDrawIndexedArguments[Math.Max(256, _transparentDrawCommands.Count * 2)];
            }
        }

        // Heightmap occlusion: is the view from the camera to this chunk blocked by a NEARER
        // chunk whose terrain top subtends a larger angle than the target's own top? This is the
        // classic horizon test - a close tall ridge hides a far chunk; a far peak over a near
        // valley does not. Conservative on purpose: we only claim occlusion when the blocker's
        // angular height clearly beats the target's, with a margin, so nothing pops while visible.
        private const double BlockingMargin = 0.35; // angular margin (radians-ish, slope units) - was 0.12, too aggressive
        private const int NearSkipChunks = 4;
        // The horizon march walks one step per chunk of distance, so its cost scales with the
        // render distance even though its VALUE does not: past a few dozen chunks the fog is doing
        // the hiding anyway, and whether a chunk there is drawn or not is invisible. Bound the march
        // so the CPU cull stays affordable at the 64-chunk Extreme preset. Beyond the bound a chunk
        // is simply assumed visible, which is the safe direction - over-drawing an occluded chunk
        // costs fill rate, whereas wrongly hiding a visible one would pop. Every preset up to the
        // 16-chunk Far one is entirely inside this bound, so those are unaffected.
        private const int OcclusionMaxChunks = 32;
        private bool IsHeightmapOccluded(Cubuild.ChunkCoordinates coord)
        {
            if (_chunkManager == null || !_cameraPosition.HasValue) return false;
            if (coord.Layer != ChunkManager.GroundLayer) return false; // only ground terrain occludes

            var cam = _cameraPosition.Value;
            double camCx = cam.X / (double)ChunkManager.ChunkSize;
            double camCz = cam.Z / (double)ChunkManager.ChunkSize;
            double targetCx = coord.X + 0.5;
            double targetCz = coord.Z + 0.5;

            double dx = targetCx - camCx;
            double dz = targetCz - camCz;
            double dist = Math.Sqrt(dx * dx + dz * dz);
            if (dist < NearSkipChunks + 1.0) return false; // too close to the camera to ever hide
            if (dist > OcclusionMaxChunks) return false;   // too far for the horizon test to pay for itself

            // Target chunk terrain top (world Y). Unknown = don't occlude (conservative).
            if (!_chunkManager.TryGetLoadedChunk(coord, out var targetChunk)) return false;
            if (targetChunk.TopSolidY == int.MinValue) return false;
            double targetTop = targetChunk.TopSolidY;

            // Camera above the target's terrain can always see it (looking down).
            if (cam.Y >= targetTop) return false;

            // Angular height of the target's terrain top above the camera (rise over run).
            double targetSlope = (targetTop - cam.Y) / (dist * ChunkManager.ChunkSize);

            // March toward the target; if any nearer chunk's terrain top subtends a LARGER angle
            // (by the margin), the target is hidden behind it.
            double stepX = dx / dist;
            double stepZ = dz / dist;
            int steps = (int)Math.Ceiling(dist);
            for (int s = 1; s < steps; s++)
            {
                int cx = (int)Math.Floor(camCx + stepX * s + 0.5);
                int cz = (int)Math.Floor(camCz + stepZ * s + 0.5);
                if (cx == coord.X && cz == coord.Z) break; // reached the target column

                if (_chunkManager.TryGetLoadedChunk(new Cubuild.ChunkCoordinates(ChunkManager.GroundLayer, cx, cz), out var blocker))
                {
                    if (blocker.TopSolidY == int.MinValue) continue;
                    double blockDist = Math.Max(1.0, Math.Sqrt((cx - camCx) * (cx - camCx) + (cz - camCz) * (cz - camCz)));
                    double blockSlope = (blocker.TopSolidY - cam.Y) / (blockDist * ChunkManager.ChunkSize);
                    if (blockSlope > targetSlope + BlockingMargin && cam.Y < blocker.TopSolidY)
                    {
                        return true; // this nearer column clearly rises above the target's line of sight
                    }
                }
            }
            return false;
        }

        // Fills the given indirect scratch array with the commands from a pass list that are inside
        // the current view frustum. Returns the visible count; falls back to "everything" when no
        // camera is set.
        private uint CullDrawCommands(
            System.Collections.Generic.List<(Cubuild.ChunkCoordinates Coord, IndirectDrawIndexedArguments Cmd)> commands,
            IndirectDrawIndexedArguments[] scratch)
        {
            int n = 0;
            if (_viewProjection.HasValue)
            {
                ExtractFrustumPlanes(_viewProjection.Value);
                for (int i = 0; i < commands.Count; i++)
                {
                    if (ChunkInFrustum(commands[i].Coord) && !IsHeightmapOccluded(commands[i].Coord))
                    {
                        scratch[n++] = commands[i].Cmd;
                    }
                }
            }
            else
            {
                for (int i = 0; i < commands.Count; i++)
                {
                    scratch[n++] = commands[i].Cmd;
                }
            }
            return (uint)n;
        }

        // Extracts the six clip planes from a row-vector view-projection matrix (0..1 depth range).
        private void ExtractFrustumPlanes(in Matrix4x4 m)
        {
            _frustumPlanes[0] = new Vector4(m.M14 + m.M11, m.M24 + m.M21, m.M34 + m.M31, m.M44 + m.M41); // left
            _frustumPlanes[1] = new Vector4(m.M14 - m.M11, m.M24 - m.M21, m.M34 - m.M31, m.M44 - m.M41); // right
            _frustumPlanes[2] = new Vector4(m.M14 + m.M12, m.M24 + m.M22, m.M34 + m.M32, m.M44 + m.M42); // bottom
            _frustumPlanes[3] = new Vector4(m.M14 - m.M12, m.M24 - m.M22, m.M34 - m.M32, m.M44 - m.M42); // top
            _frustumPlanes[4] = new Vector4(m.M13, m.M23, m.M33, m.M43);                                 // near
            _frustumPlanes[5] = new Vector4(m.M14 - m.M13, m.M24 - m.M23, m.M34 - m.M33, m.M44 - m.M43); // far
        }

        // AABB-vs-frustum via the positive-vertex trick. The chunk AABB covers the full world
        // height, so this culls by horizontal footprint only - still rejects everything off-screen.
        private bool ChunkInFrustum(Cubuild.ChunkCoordinates coord)
        {            float minX = coord.X * ChunkManager.ChunkSize;
            float maxX = minX + ChunkManager.ChunkSize;
            float minZ = coord.Z * ChunkManager.ChunkSize;
            float maxZ = minZ + ChunkManager.ChunkSize;
            // World Y bounds come from the chunk's LAYER (ground -256..383, sky 384..1023), not
            // the ground origin. Using the ground bounds for sky chunks made them vanish when the
            // camera was up in the stratosphere (their frustum box sat below them).
            float minY = ChunkManager.OriginYForLayer(coord.Layer);
            float maxY = minY + ChunkManager.HeightForLayer(coord.Layer);

            for (int i = 0; i < 6; i++)
            {
                var p = _frustumPlanes[i];
                float px = p.X >= 0f ? maxX : minX;
                float py = p.Y >= 0f ? maxY : minY;
                float pz = p.Z >= 0f ? maxZ : minZ;
                if (p.X * px + p.Y * py + p.Z * pz + p.W < 0f)
                {
                    return false;
                }
            }
            return true;
        }

    }
}