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
        private bool TryApplyPendingUpload(PendingUpload upload)
        {
            if (upload.WorldGeneration != System.Threading.Interlocked.Read(ref _worldGeneration)
                || !ReferenceEquals(upload.SourceManager, _chunkManager))
                return false;

            lock (upload.SourceChunk.MeshLock)
            {
                if (upload.WorldGeneration != System.Threading.Interlocked.Read(ref _worldGeneration)
                    || !ReferenceEquals(upload.SourceManager, _chunkManager)
                    || !upload.SourceManager.TryGetLoadedChunk(upload.Coord, out var current)
                    || !ReferenceEquals(current, upload.SourceChunk)
                    || !upload.SourceChunk.IsCleanMeshStampCurrent(
                        upload.MeshVersion, upload.MeshInputRevision))
                    return false;

                if (_appliedMeshVersions.TryGetValue(upload.Coord, out var applied)
                    && ReferenceEquals(applied.Source, upload.SourceChunk)
                    && applied.Version >= upload.MeshVersion)
                    return false;

                WriteChunkData(upload.Coord, upload.Vertices, upload.Indices,
                    upload.CutoutVertices, upload.CutoutIndices, upload.GlassVertices, upload.GlassIndices,
                    upload.TransparentVertices, upload.TransparentIndices, upload.TransparentNonTopIndexCount,
                    upload.SurfaceLodVertices, upload.SurfaceLodIndices);
                _appliedMeshVersions[upload.Coord] = (upload.SourceChunk, upload.MeshVersion);
                return true;
            }
        }

        private void ApplyPendingRemoval(PendingRemoval removal)
        {
            if (removal.WorldGeneration != System.Threading.Interlocked.Read(ref _worldGeneration)
                || !ReferenceEquals(removal.SourceManager, _chunkManager))
                return;

            if (removal.SourceChunk == null)
            {
                // Streaming unloads are queued after the source is removed from the active
                // manager. A chunk already reloaded at this coordinate owns the GPU range now.
                if (removal.SourceManager.TryGetLoadedChunk(removal.Coord, out _)) return;
                FreeChunkRange(removal.Coord);
                _appliedMeshVersions.Remove(removal.Coord);
                return;
            }

            lock (removal.SourceChunk.MeshLock)
            {
                if (removal.WorldGeneration != System.Threading.Interlocked.Read(ref _worldGeneration)
                    || !ReferenceEquals(removal.SourceManager, _chunkManager)
                    || !removal.SourceManager.TryGetLoadedChunk(removal.Coord, out var current)
                    || !ReferenceEquals(current, removal.SourceChunk)
                    || !removal.SourceChunk.IsCleanMeshStampCurrent(
                        removal.MeshVersion, removal.MeshInputRevision))
                    return;

                if (_appliedMeshVersions.TryGetValue(removal.Coord, out var applied)
                    && ReferenceEquals(applied.Source, removal.SourceChunk)
                    && applied.Version >= removal.MeshVersion)
                    return;

                FreeChunkRange(removal.Coord);
                _appliedMeshVersions[removal.Coord] = (removal.SourceChunk, removal.MeshVersion);
            }
        }

        private void WriteChunkData(Cubuild.ChunkCoordinates coord, uint[] verts, ushort[] indices,
            uint[] cutoutVerts, ushort[] cutoutIndices, uint[] glassVerts, ushort[] glassIndices,
            uint[] transVerts, ushort[] transIndices, uint transNonTopIndexCount,
            uint[] surfaceLodVerts, ushort[] surfaceLodIndices)
        {
            InvalidateSurfacePatchFor(coord);

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
            if (_surfaceLodRanges.TryGetValue(coord, out var prevLod))
            {
                FreeRange(prevLod);
                _surfaceLodRanges.Remove(coord);
            }
            _surfaceLodMeshes.Remove(coord);

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

                _transparentRanges[coord] = new ChunkRange
                {
                    VbOffsetBytes = tvbo,
                    VbBytes = tvbBytes,
                    IbOffsetBytes = tibo,
                    IndexCount = (uint)transIndices.Length,
                    NonTopIndexCount = transNonTopIndexCount,
                };
            }
            else
            {
                _transparentRanges.Remove(coord);
            }

            // The far-field heightfield is opaque-only and intentionally has a separate range so
            // the full chunk mesh remains available for the near-field representation.
            if (Cubuild.LodSettings.SurfaceLodEnabled && surfaceLodVerts != null && surfaceLodVerts.Length > 0
                && surfaceLodIndices != null && surfaceLodIndices.Length > 0)
            {
                uint lvbBytes = (uint)(surfaceLodVerts.Length * sizeof(float));
                uint libBytes = (uint)(surfaceLodIndices.Length * sizeof(ushort));
                var (lvbo, _, libo, _) = AllocateRange(lvbBytes, libBytes);
                _gd.UpdateBuffer(_megaVertexBuffer, lvbo, surfaceLodVerts);
                _gd.UpdateBuffer(_megaIndexBuffer, libo, surfaceLodIndices);
                _surfaceLodRanges[coord] = new ChunkRange
                {
                    VbOffsetBytes = lvbo,
                    VbBytes = lvbBytes,
                    IbOffsetBytes = libo,
                    IndexCount = (uint)surfaceLodIndices.Length,
                };
                _surfaceLodMeshes[coord] = (surfaceLodVerts, surfaceLodIndices);
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
        }

        // First-fit allocator: reuse a freed hole if one's big enough, else append at the tail
        // (growing the GPU buffers 2x if the tail would overflow).
        private (uint vbo, uint vbBytes, uint ibo, uint ibBytes) AllocateRange(uint vbBytes, uint ibBytes)
        {
            // Buffer growth copies are queued and recorded into the next frame's CommandList.
            // Chunk uploads, however, update the current buffer immediately. If we reused a free
            // range inside the old buffer's copied prefix during this interval, that later copy
            // would overwrite the freshly uploaded mesh with stale bytes from the old buffer.
            // Keep allocations append-only until all queued copies are recorded; the holes remain
            // available for reuse on the next frame.
            if (_pendingBufferCopies.Count == 0)
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
            // Callers mark draw commands dirty; Render() rebuilds them once after the current
            // batch of uploads/removals instead of scanning pass lists once per changed chunk.
            _freeBlocks.Add((r.VbOffsetBytes, r.VbBytes, r.IbOffsetBytes, r.IndexCount * sizeof(ushort)));
        }

        private void FreeChunkRange(Cubuild.ChunkCoordinates coord)
        {
            InvalidateSurfacePatchFor(coord);

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
            if (_surfaceLodRanges.TryGetValue(coord, out var lod))
            {
                FreeRange(lod);
                _surfaceLodRanges.Remove(coord);
            }
            _surfaceLodMeshes.Remove(coord);

            // Drop this chunk's commands from every pass via the per-frame rebuild (see
            // WriteChunkData for why the old incremental O(n)-per-chunk sync was removed).
            _drawCommandsDirty = true;
            _gpuCullDataDirty = true;
        }

        private static int FloorDiv(int value, int divisor)
        {
            int quotient = value / divisor;
            if (value % divisor < 0) quotient--;
            return quotient;
        }

        private static Cubuild.ChunkCoordinates SurfacePatchCoord(Cubuild.ChunkCoordinates coord)
        {
            return new Cubuild.ChunkCoordinates(ChunkManager.GroundLayer,
                FloorDiv(coord.X, 4) * 4, FloorDiv(coord.Z, 4) * 4);
        }

        // The patch range is removed only on the first invalidation in a batch. Later uploads or
        // removals in that same group merely keep its dirty key set, avoiding duplicate frees.
        private void InvalidateSurfacePatchFor(Cubuild.ChunkCoordinates coord)
        {
            if (!Cubuild.LodSettings.SurfaceLodEnabled || coord.Layer != ChunkManager.GroundLayer) return;
            var patchCoord = SurfacePatchCoord(coord);
            if (_surfacePatchRanges.TryGetValue(patchCoord, out var patchRange))
            {
                FreeRange(patchRange);
                _surfacePatchRanges.Remove(patchCoord);
            }
            _dirtySurfacePatches.Add(patchCoord);
            _drawCommandsDirty = true;
            _gpuCullDataDirty = true;
        }

        // Aggregate existing packed per-chunk surface LOD arrays. No block data is read or meshed
        // here; each chunk's local ushort indices are rebased into the combined 16-bit vertex set.
        private void RebuildDirtySurfacePatches()
        {
            if (!Cubuild.LodSettings.SurfaceLodEnabled)
            {
                _dirtySurfacePatches.Clear();
                return;
            }

            foreach (var patchCoord in _dirtySurfacePatches)
            {
                var memberMeshes = new (uint[] Vertices, ushort[] Indices)[16];
                int vertexCount = 0;
                int indexCount = 0;
                bool complete = true;
                for (int dx = 0; dx < 4 && complete; dx++)
                {
                    for (int dz = 0; dz < 4; dz++)
                    {
                        var member = new Cubuild.ChunkCoordinates(ChunkManager.GroundLayer,
                            patchCoord.X + dx, patchCoord.Z + dz);
                        if (!_surfaceLodMeshes.TryGetValue(member, out var mesh)
                            || mesh.Vertices.Length == 0 || mesh.Indices.Length == 0
                            || mesh.Vertices.Length % 9 != 0)
                        {
                            complete = false;
                            break;
                        }

                        int memberVertices = mesh.Vertices.Length / 9;
                        if (vertexCount > 65536 - memberVertices)
                        {
                            complete = false;
                            break;
                        }
                        memberMeshes[dx * 4 + dz] = mesh;
                        vertexCount += memberVertices;
                        indexCount += mesh.Indices.Length;
                    }
                }

                if (!complete || vertexCount == 0 || indexCount == 0) continue;

                var vertices = new uint[vertexCount * 9];
                var indices = new ushort[indexCount];
                int vertexWrite = 0;
                int indexWrite = 0;
                int vertexBase = 0;
                for (int memberIndex = 0; memberIndex < memberMeshes.Length; memberIndex++)
                {
                    var mesh = memberMeshes[memberIndex];
                    Array.Copy(mesh.Vertices, 0, vertices, vertexWrite, mesh.Vertices.Length);
                    vertexWrite += mesh.Vertices.Length;
                    int memberVertexCount = mesh.Vertices.Length / 9;
                    for (int i = 0; i < mesh.Indices.Length; i++)
                    {
                        int rebased = vertexBase + mesh.Indices[i];
                        if ((uint)rebased >= (uint)vertexCount || rebased > ushort.MaxValue)
                        {
                            complete = false;
                            break;
                        }
                        indices[indexWrite++] = (ushort)rebased;
                    }
                    if (!complete) break;
                    vertexBase += memberVertexCount;
                }
                if (!complete || indexWrite != indices.Length) continue;

                uint vbBytes = (uint)(vertices.Length * sizeof(uint));
                uint ibBytes = (uint)(indices.Length * sizeof(ushort));
                var (vbOffset, _, ibOffset, _) = AllocateRange(vbBytes, ibBytes);
                _gd.UpdateBuffer(_megaVertexBuffer, vbOffset, vertices);
                _gd.UpdateBuffer(_megaIndexBuffer, ibOffset, indices);
                _surfacePatchRanges[patchCoord] = new ChunkRange
                {
                    VbOffsetBytes = vbOffset,
                    VbBytes = vbBytes,
                    IbOffsetBytes = ibOffset,
                    IndexCount = (uint)indices.Length,
                };
            }
            _dirtySurfacePatches.Clear();
        }

        // Grows the mega vertex buffer to 2x (or to the needed size) when the tail would overflow.
        // Records a GPU CopyBuffer of the live region [0, tail) so the old data survives the swap;
        // the old buffer is disposed once the GPU is finished with it.
        private void EnsureVertexCapacity(uint needed)
        {
            if (_megaVertexBuffer != null && _vbCapacityBytes >= needed) return;
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
            System.Collections.Generic.List<DrawEntry> commands,
            ref uint[] target)
        {
            int count = commands.Count;
            if (target.Length != count * 16)
            {
                target = new uint[count * 16];
            }
            for (int i = 0; i < count; i++)
            {
                var entry = commands[i];
                var coord = entry.Coord;
                var cmd = entry.Cmd;
                float minX = (float)((long)coord.X * ChunkManager.ChunkSize - _renderOriginX);
                float maxX = minX + ChunkManager.ChunkSize * entry.ChunkSpan;
                float minZ = (float)((long)coord.Z * ChunkManager.ChunkSize - _renderOriginZ);
                float maxZ = minZ + ChunkManager.ChunkSize * entry.ChunkSpan;
                // Match ChunkInFrustum: layer-based Y bounds (ground -256..383, sky 384..1023).
                float minY = ChunkManager.OriginYForLayer(coord.Layer) - _renderOriginY;
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

        // Runs the GPU-cull compute pass for one draw pass. All draw passes share ONE cull-data
        // buffer, so each pass must re-upload ITS OWN data via the CommandList (recorded in-order
        // before its dispatch) - a GraphicsDevice-level upload executes immediately and would be
        // overwritten by the last pass, making every dispatch read the wrong AABBs.
        private void ApplyHeightmapOcclusion(
            System.Collections.Generic.List<DrawEntry> commands,
            ref uint[] cullData)
        {
            // cullData is indexed the same as commands (i*16 per chunk, InstanceCount at +9).
            if (cullData.Length < commands.Count * 16) return;
            for (int i = 0; i < commands.Count; i++)
            {
                // Heightmap occlusion is transient: restore the command's real instance count
                // every frame before deciding whether it is hidden from this camera position.
                // Otherwise an occluded chunk stays at zero in this cached array forever.
                cullData[i * 16 + 9] = commands[i].Cmd.InstanceCount;
                if (IsDrawEntryHeightmapOccluded(commands[i]))
                {
                    cullData[i * 16 + 9] = 0; // InstanceCount = 0 -> skipped by the GPU draw
                }
            }
        }

        private void RunGpuCull(
            CommandList cl,
            System.Collections.Generic.List<DrawEntry> commands,
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
                    _cullUniformData[i * 4 + 0] = BitConverter.SingleToUInt32Bits(_frustumPlanes[i].X);
                    _cullUniformData[i * 4 + 1] = BitConverter.SingleToUInt32Bits(_frustumPlanes[i].Y);
                    _cullUniformData[i * 4 + 2] = BitConverter.SingleToUInt32Bits(_frustumPlanes[i].Z);
                    _cullUniformData[i * 4 + 3] = BitConverter.SingleToUInt32Bits(_frustumPlanes[i].W);
                }
            }
            _cullUniformData[24] = (uint)commands.Count;
            _cullUniformData[25] = 0;
            _cullUniformData[26] = 0;
            _cullUniformData[27] = 0;
            cl.UpdateBuffer(_frustumBuffer, 0, _cullUniformData);

            cl.SetPipeline(_cullPipeline);
            cl.SetComputeResourceSet(0, _frustumSet);
            cl.SetComputeResourceSet(1, _cullArgsWriteSet);
            uint groups = (uint)((commands.Count + 63) / 64);
            cl.Dispatch(groups, 1, 1);

            // Copy the compute-written args into the indirect buffer for the draw.
            cl.CopyBuffer(_cullArgsBuffer, 0, _indirectBuffer, 0, (uint)commands.Count * IndirectCommandStride);
        }

        // Rebuilds all chunk draw lists from the range dictionaries. Called once per frame when a
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
            _transparentNonTopCullData = Array.Empty<uint>();
            _transparentTopCullData = Array.Empty<uint>();
            for (int i = 0; i < _lastSortCount.Length; i++) _lastSortCount[i] = -1;

            var patchedChunks = new HashSet<Cubuild.ChunkCoordinates>();
            foreach (var kv in _surfacePatchRanges)
            {
                bool usePatch = true;
                for (int dx = 0; dx < 4 && usePatch; dx++)
                {
                    for (int dz = 0; dz < 4; dz++)
                    {
                        var member = new Cubuild.ChunkCoordinates(ChunkManager.GroundLayer,
                            kv.Key.X + dx, kv.Key.Z + dz);
                        if (!_chunkRanges.ContainsKey(member) || !_surfaceLodRanges.ContainsKey(member)
                            || !ShouldUseSurfaceLod(member))
                        {
                            usePatch = false;
                            break;
                        }
                    }
                }

                if (!usePatch) continue;
                var patchRange = kv.Value;
                _drawCommands.Add(new DrawEntry(kv.Key, new IndirectDrawIndexedArguments
                {
                    IndexCount = patchRange.IndexCount,
                    InstanceCount = 1,
                    FirstIndex = patchRange.IbOffsetBytes / 2,
                    VertexOffset = (int)(patchRange.VbOffsetBytes / VertexStrideBytes),
                    FirstInstance = 0,
                }, 4));
                for (int dx = 0; dx < 4; dx++)
                {
                    for (int dz = 0; dz < 4; dz++)
                    {
                        patchedChunks.Add(new Cubuild.ChunkCoordinates(ChunkManager.GroundLayer,
                            kv.Key.X + dx, kv.Key.Z + dz));
                    }
                }
            }

            foreach (var kv in _chunkRanges)
            {
                if (patchedChunks.Contains(kv.Key)) continue;
                var r = ShouldUseSurfaceLod(kv.Key) && _surfaceLodRanges.TryGetValue(kv.Key, out var lodRange)
                    ? lodRange
                    : kv.Value;
                _drawCommands.Add(new DrawEntry(kv.Key, new IndirectDrawIndexedArguments
                {
                    IndexCount = r.IndexCount,
                    InstanceCount = 1,
                    FirstIndex = r.IbOffsetBytes / 2,           // ushort index units
                    VertexOffset = (int)(r.VbOffsetBytes / VertexStrideBytes),
                    FirstInstance = 0,
                }, 1));
            }
            if (_indirectScratch.Length < _drawCommands.Count)
            {
                _indirectScratch = new IndirectDrawIndexedArguments[Math.Max(256, _drawCommands.Count * 2)];
            }

            _cutoutDrawCommands.Clear();
            foreach (var kv in _cutoutRanges)
            {
                var r = kv.Value;
                _cutoutDrawCommands.Add(new DrawEntry(kv.Key, new IndirectDrawIndexedArguments
                {
                    IndexCount = r.IndexCount,
                    InstanceCount = 1,
                    FirstIndex = r.IbOffsetBytes / 2,           // ushort index units
                    VertexOffset = (int)(r.VbOffsetBytes / VertexStrideBytes),
                    FirstInstance = 0,
                }, 1));
            }
            if (_cutoutIndirectScratch.Length < _cutoutDrawCommands.Count)
            {
                _cutoutIndirectScratch = new IndirectDrawIndexedArguments[Math.Max(256, _cutoutDrawCommands.Count * 2)];
            }

            _glassDrawCommands.Clear();
            foreach (var kv in _glassRanges)
            {
                var r = kv.Value;
                _glassDrawCommands.Add(new DrawEntry(kv.Key, new IndirectDrawIndexedArguments
                {
                    IndexCount = r.IndexCount,
                    InstanceCount = 1,
                    FirstIndex = r.IbOffsetBytes / 2,           // ushort index units
                    VertexOffset = (int)(r.VbOffsetBytes / VertexStrideBytes),
                    FirstInstance = 0,
                }, 1));
            }
            if (_glassIndirectScratch.Length < _glassDrawCommands.Count)
            {
                _glassIndirectScratch = new IndirectDrawIndexedArguments[Math.Max(256, _glassDrawCommands.Count * 2)];
            }

            _transparentNonTopDrawCommands.Clear();
            _transparentTopDrawCommands.Clear();
            foreach (var kv in _transparentRanges)
            {
                var r = kv.Value;
                uint nonTopIndexCount = Math.Min(r.NonTopIndexCount, r.IndexCount);
                uint topIndexCount = r.IndexCount - nonTopIndexCount;
                int vertexOffset = (int)(r.VbOffsetBytes / VertexStrideBytes);
                uint firstIndex = r.IbOffsetBytes / 2;
                if (nonTopIndexCount > 0)
                {
                    _transparentNonTopDrawCommands.Add(new DrawEntry(kv.Key, new IndirectDrawIndexedArguments
                    {
                        IndexCount = nonTopIndexCount,
                        InstanceCount = 1,
                        FirstIndex = firstIndex, // ushort index units
                        VertexOffset = vertexOffset,
                        FirstInstance = 0,
                    }, 1));
                }
                if (topIndexCount > 0)
                {
                    _transparentTopDrawCommands.Add(new DrawEntry(kv.Key, new IndirectDrawIndexedArguments
                    {
                        IndexCount = topIndexCount,
                        InstanceCount = 1,
                        FirstIndex = firstIndex + nonTopIndexCount,
                        VertexOffset = vertexOffset,
                        FirstInstance = 0,
                    }, 1));
                }
            }
            if (_transparentNonTopIndirectScratch.Length < _transparentNonTopDrawCommands.Count)
            {
                _transparentNonTopIndirectScratch = new IndirectDrawIndexedArguments[Math.Max(256, _transparentNonTopDrawCommands.Count * 2)];
            }
            if (_transparentTopIndirectScratch.Length < _transparentTopDrawCommands.Count)
            {
                _transparentTopIndirectScratch = new IndirectDrawIndexedArguments[Math.Max(256, _transparentTopDrawCommands.Count * 2)];
            }
        }

        private bool ShouldUseSurfaceLod(Cubuild.ChunkCoordinates coord)
        {
            if (!Cubuild.LodSettings.SurfaceLodEnabled
                || coord.Layer != ChunkManager.GroundLayer
                || _surfaceLodCameraLayer != ChunkManager.GroundLayer
                || !_surfaceLodAllowed
                || _surfaceLodStartChunks == int.MaxValue)
            {
                return false;
            }

            long dx = (long)coord.X - _surfaceLodCameraX;
            long dz = (long)coord.Z - _surfaceLodCameraZ;
            long threshold = _surfaceLodStartChunks;
            return dx * dx + dz * dz > threshold * threshold;
        }

        private bool IsSurfacePatchActive(Cubuild.ChunkCoordinates patchCoord)
        {
            if (!_surfacePatchRanges.ContainsKey(patchCoord)) return false;
            for (int dx = 0; dx < 4; dx++)
            {
                for (int dz = 0; dz < 4; dz++)
                {
                    var member = new Cubuild.ChunkCoordinates(ChunkManager.GroundLayer,
                        patchCoord.X + dx, patchCoord.Z + dz);
                    if (!_chunkRanges.ContainsKey(member) || !_surfaceLodRanges.ContainsKey(member)
                        || !ShouldUseSurfaceLod(member))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// Captures only a 9x9 neighborhood plus the selected target. GPU compute results are
        /// intentionally not read back: they are reported as unknown, while CPU frustum math is
        /// labeled as a prediction whenever the GPU path is active.
        /// </summary>
        public ChunkDiagnosticsSnapshot CaptureChunkDiagnostics(
            int playerLayer, int playerChunkX, int playerChunkZ,
            int targetLayer, int targetChunkX, int targetChunkZ, bool hasBlockTarget,
            int renderRadius, int dirtyMeshQueueCount)
        {
            const int mapRadius = 4;
            var mapRows = new List<string>(mapRadius * 2 + 1);
            if (_chunkManager != null)
            {
                for (int dz = mapRadius; dz >= -mapRadius; dz--)
                {
                    var row = new char[mapRadius * 2 + 1];
                    for (int dx = -mapRadius; dx <= mapRadius; dx++)
                    {
                        int cx = playerChunkX + dx;
                        int cz = playerChunkZ + dz;
                        char status = GetMapChunkStatus(new Cubuild.ChunkCoordinates(playerLayer, cx, cz));
                        if (cx == playerChunkX && cz == playerChunkZ) status = '@';
                        if (hasBlockTarget && targetLayer == playerLayer
                            && targetChunkX == cx && targetChunkZ == cz
                            && (cx != playerChunkX || cz != playerChunkZ))
                        {
                            status = 'X';
                        }
                        row[dx + mapRadius] = status;
                    }
                    mapRows.Add($"z{dz,3:+0;-0;0} {new string(row)}");
                }
            }

            var target = new Cubuild.ChunkCoordinates(targetLayer, targetChunkX, targetChunkZ);
            var targetLines = BuildTargetDiagnosticLines(target, hasBlockTarget);
            int drawCommandCount = _drawCommands.Count + _cutoutDrawCommands.Count + _glassDrawCommands.Count
                + _transparentNonTopDrawCommands.Count + _transparentTopDrawCommands.Count;
            return new ChunkDiagnosticsSnapshot(
                _chunkManager?.GetLoadedChunks().Count ?? 0,
                renderRadius,
                dirtyMeshQueueCount,
                CountPendingUploads(),
                _lastFrameStreamUploads,
                _lastFramePriorityUploads,
                _chunkRanges.Count,
                drawCommandCount,
                _vbCapacityBytes,
                _ibCapacityBytes,
                _vbTailBytes,
                _ibTailBytes,
                _pendingBufferCopies.Count,
                _cullMode.ToString(),
                _gpuCullEnabled,
                _gpuCullSupported,
                mapRows,
                targetLines);
        }

        private char GetMapChunkStatus(Cubuild.ChunkCoordinates coord)
        {
            if (_chunkManager == null || !_chunkManager.TryGetLoadedChunk(coord, out var chunk)) return '.';
            if (chunk.IsMeshingQueued) return 'm';
            if (chunk.NeedsRemesh || chunk.MeshVersion == 0) return 'd';
            if (!_chunkRanges.ContainsKey(coord)) return 'M';
            return HasDrawableRange(coord) ? 'U' : '0';
        }

        private bool HasDrawableRange(Cubuild.ChunkCoordinates coord)
        {
            return _chunkRanges.TryGetValue(coord, out var opaque) && opaque.IndexCount > 0
                || _cutoutRanges.TryGetValue(coord, out var cutout) && cutout.IndexCount > 0
                || _glassRanges.TryGetValue(coord, out var glass) && glass.IndexCount > 0
                || _transparentRanges.TryGetValue(coord, out var transparent) && transparent.IndexCount > 0
                || ShouldUseSurfaceLod(coord) && _surfaceLodRanges.TryGetValue(coord, out var lod) && lod.IndexCount > 0
                || coord.Layer == ChunkManager.GroundLayer && IsSurfacePatchActive(SurfacePatchCoord(coord));
        }

        private IReadOnlyList<string> BuildTargetDiagnosticLines(Cubuild.ChunkCoordinates coord, bool hasBlockTarget)
        {
            var lines = new List<string>(5);
            if (_chunkManager == null || !_chunkManager.TryGetLoadedChunk(coord, out var chunk))
            {
                lines.Add($"Target {(hasBlockTarget ? "block" : "player")}: L{coord.Layer} chunk {coord.X},{coord.Z} | not loaded");
                lines.Add("Range: not uploaded; no drawable range");
                lines.Add("CPU frustum: unknown (no drawable mesh)");
                lines.Add("Heightmap: not tested (chunk unavailable)");
                lines.Add($"LOD: {(Cubuild.LodSettings.SurfaceLodEnabled ? "enabled" : "disabled")} | GPU: {GpuVerdictText()}");
                return lines;
            }

            string meshState = chunk.IsMeshingQueued ? "queued/meshing"
                : chunk.NeedsRemesh ? "needs mesh"
                : chunk.MeshVersion > 0 ? "meshed" : "not meshed";
            bool uploaded = _chunkRanges.ContainsKey(coord);
            bool drawable = HasDrawableRange(coord);
            uint indexCount = 0;
            var activePatch = coord.Layer == ChunkManager.GroundLayer ? SurfacePatchCoord(coord) : default;
            ChunkRange activePatchRange = default;
            bool patchActive = coord.Layer == ChunkManager.GroundLayer && IsSurfacePatchActive(activePatch)
                && _surfacePatchRanges.TryGetValue(activePatch, out activePatchRange);
            if (patchActive)
            {
                indexCount += activePatchRange.IndexCount;
            }
            else if (ShouldUseSurfaceLod(coord) && _surfaceLodRanges.TryGetValue(coord, out var lodRange))
            {
                indexCount += lodRange.IndexCount;
            }
            else if (_chunkRanges.TryGetValue(coord, out var opaqueRange))
            {
                indexCount += opaqueRange.IndexCount;
            }
            if (_cutoutRanges.TryGetValue(coord, out var cutoutRange)) indexCount += cutoutRange.IndexCount;
            if (_glassRanges.TryGetValue(coord, out var glassRange)) indexCount += glassRange.IndexCount;
            if (_transparentRanges.TryGetValue(coord, out var transparentRange)) indexCount += transparentRange.IndexCount;

            lines.Add($"Target {(hasBlockTarget ? "block" : "player")}: L{coord.Layer} chunk {coord.X},{coord.Z} | loaded, {meshState}, v{chunk.MeshVersion}");
            lines.Add($"Range: uploaded={(uploaded ? "yes" : "no")}, drawable={(drawable ? "yes" : "no")} ({indexCount} indices)");

            var drawEntry = GetDiagnosticDrawEntry(coord);
            if (!_viewProjection.HasValue || !drawEntry.HasValue)
            {
                lines.Add("CPU frustum: unknown (no current draw entry/view)");
            }
            else
            {
                ExtractFrustumPlanes(_viewProjection.Value);
                int rejectPlane = GetFrustumRejectPlane(drawEntry.Value);
                string decision = rejectPlane < 0 ? "inside" : $"rejected {FrustumPlaneName(rejectPlane)}";
                lines.Add($"CPU frustum: {decision} ({(_gpuCullEnabled ? "prediction" : "CPU path")})");
            }

            bool occluded = EvaluateHeightmapOcclusion(coord, out var blocker, out var occlusionStatus);
            string occlusionText = occlusionStatus switch
            {
                HeightmapOcclusionStatus.Occluded => $"hidden by {blocker!.Value.X},{blocker.Value.Z}",
                HeightmapOcclusionStatus.Clear => "clear",
                HeightmapOcclusionStatus.NearSkip => "not culled (near skip)",
                HeightmapOcclusionStatus.DistanceLimit => "not tested (>32 chunks)",
                HeightmapOcclusionStatus.CameraAbove => "clear (camera above terrain)",
                HeightmapOcclusionStatus.EmptyTerrain => "clear (no solid height)",
                HeightmapOcclusionStatus.NotApplicable => "n/a (ground layer only)",
                HeightmapOcclusionStatus.TargetUnavailable => "not tested (no target height)",
                _ => "unknown (camera unavailable)",
            };
            // Keep this explicitly separate from the draw-entry result: a 4x4 LOD patch is culled
            // only when every member chunk is hidden, but this field reports the selected chunk.
            lines.Add($"Heightmap (target chunk): {(occluded ? "occluded" : occlusionText)}");
            lines.Add($"LOD: {GetLodDiagnosticStatus(coord)} | GPU: {GpuVerdictText()}");
            return lines;
        }

        private string GetLodDiagnosticStatus(Cubuild.ChunkCoordinates coord)
        {
            if (!Cubuild.LodSettings.SurfaceLodEnabled) return "disabled";
            if (coord.Layer != ChunkManager.GroundLayer) return "n/a (ground only)";
            if (ShouldUseSurfaceLod(coord))
            {
                var patch = SurfacePatchCoord(coord);
                if (IsSurfacePatchActive(patch)) return "4x4 surface patch active";
                if (_surfaceLodRanges.ContainsKey(coord)) return "per-chunk surface LOD active";
                return "far-field, LOD mesh missing (full mesh fallback)";
            }
            return _surfaceLodRanges.ContainsKey(coord) ? "near full-detail (LOD cached)" : "full-detail";
        }

        private DrawEntry? GetDiagnosticDrawEntry(Cubuild.ChunkCoordinates coord)
        {
            if (!HasDrawableRange(coord)) return null;

            if (coord.Layer == ChunkManager.GroundLayer)
            {
                var patchCoord = SurfacePatchCoord(coord);
                if (IsSurfacePatchActive(patchCoord)
                    && _surfacePatchRanges.TryGetValue(patchCoord, out var patchRange))
                {
                    return new DrawEntry(patchCoord, new IndirectDrawIndexedArguments
                    {
                        IndexCount = patchRange.IndexCount,
                        InstanceCount = 1,
                    }, 4);
                }
            }

            return new DrawEntry(coord, new IndirectDrawIndexedArguments { InstanceCount = 1 }, 1);
        }

        private string GpuVerdictText()
        {
            if (!_gpuCullEnabled) return "n/a (CPU path; no GPU cull verdict)";
            return "unknown (GPU compute; no readback)";
        }

        private static string FrustumPlaneName(int plane) => plane switch
        {
            0 => "left",
            1 => "right",
            2 => "bottom",
            3 => "top",
            4 => "near",
            5 => "far",
            _ => "none",
        };

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
        // so the CPU cull stays affordable even at the former 64-chunk maximum. Beyond the bound a chunk
        // is simply assumed visible, which is the safe direction - over-drawing an occluded chunk
        // costs fill rate, whereas wrongly hiding a visible one would pop. Every preset up to the
        // 16-chunk Far one is entirely inside this bound, so those are unaffected.
        private const int OcclusionMaxChunks = 32;
        private enum HeightmapOcclusionStatus
        {
            Unknown,
            NotApplicable,
            NearSkip,
            DistanceLimit,
            TargetUnavailable,
            EmptyTerrain,
            CameraAbove,
            Clear,
            Occluded,
        }

        private bool IsHeightmapOccluded(Cubuild.ChunkCoordinates coord)
            => EvaluateHeightmapOcclusion(coord, out _, out _);

        private bool EvaluateHeightmapOcclusion(Cubuild.ChunkCoordinates coord,
            out Cubuild.ChunkCoordinates? blockerCoord, out HeightmapOcclusionStatus status)
        {
            blockerCoord = null;
            status = HeightmapOcclusionStatus.Unknown;
            if (_chunkManager == null || !_cameraPosition.HasValue) return false;
            if (coord.Layer != ChunkManager.GroundLayer)
            {
                status = HeightmapOcclusionStatus.NotApplicable;
                return false; // only ground terrain occludes
            }

            var cam = _cameraPosition.Value;
            double camCx = cam.X / (double)ChunkManager.ChunkSize;
            double camCz = cam.Z / (double)ChunkManager.ChunkSize;
            double targetCx = coord.X + 0.5;
            double targetCz = coord.Z + 0.5;

            double dx = targetCx - camCx;
            double dz = targetCz - camCz;
            double dist = Math.Sqrt(dx * dx + dz * dz);
            if (dist < NearSkipChunks + 1.0)
            {
                status = HeightmapOcclusionStatus.NearSkip;
                return false; // too close to the camera to ever hide
            }
            if (dist > OcclusionMaxChunks)
            {
                status = HeightmapOcclusionStatus.DistanceLimit;
                return false; // too far for the horizon test to pay for itself
            }

            // Use the target's actual maximum height, not its representative height: the
            // representative can sit below a narrow mountain summit, causing this whole chunk
            // to be hidden while that summit is still visible above the nearer hill. Overstating
            // target height only reduces culling, which is the safe direction.
            if (!_chunkManager.TryGetLoadedChunk(coord, out var targetChunk))
            {
                status = HeightmapOcclusionStatus.TargetUnavailable;
                return false;
            }
            if (targetChunk.MaxSolidY == int.MinValue)
            {
                status = HeightmapOcclusionStatus.EmptyTerrain;
                return false;
            }
            double targetTop = targetChunk.MaxSolidY;

            // Camera above the target's terrain can always see it (looking down).
            if (cam.Y >= targetTop)
            {
                status = HeightmapOcclusionStatus.CameraAbove;
                return false;
            }

            // Angular height of the target's terrain top above the camera (rise over run).
            double targetSlope = (targetTop - cam.Y) / (dist * ChunkManager.ChunkSize);

            // March toward the target; if any nearer chunk's terrain top subtends a LARGER angle
            // (by the margin), the target is hidden behind it.
            double stepX = dx / dist;
            double stepZ = dz / dist;
            int steps = (int)Math.Ceiling(dist);
            status = HeightmapOcclusionStatus.Clear;
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
                        blockerCoord = new Cubuild.ChunkCoordinates(ChunkManager.GroundLayer, cx, cz);
                        status = HeightmapOcclusionStatus.Occluded;
                        return true; // this nearer column clearly rises above the target's line of sight
                    }
                }
            }
            return false;
        }

        // A patch is hidden only when every constituent chunk independently passes the existing
        // conservative horizon test. Any visible or unknown member keeps the full patch visible.
        private bool IsDrawEntryHeightmapOccluded(DrawEntry entry)
        {
            for (int dx = 0; dx < entry.ChunkSpan; dx++)
            {
                for (int dz = 0; dz < entry.ChunkSpan; dz++)
                {
                    var member = new Cubuild.ChunkCoordinates(entry.Coord.Layer,
                        entry.Coord.X + dx, entry.Coord.Z + dz);
                    if (!IsHeightmapOccluded(member)) return false;
                }
            }
            return true;
        }

        // Fills the given indirect scratch array with the commands from a pass list that are inside
        // the current view frustum. Returns the visible count; falls back to "everything" when no
        // camera is set.
        private uint CullDrawCommands(
            System.Collections.Generic.List<DrawEntry> commands,
            IndirectDrawIndexedArguments[] scratch)
        {
            int n = 0;
            if (_viewProjection.HasValue)
            {
                ExtractFrustumPlanes(_viewProjection.Value);
                for (int i = 0; i < commands.Count; i++)
                {
                    var entry = commands[i];
                    if (DrawEntryInFrustum(entry) && !IsDrawEntryHeightmapOccluded(entry))
                    {
                        scratch[n++] = entry.Cmd;
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
        private bool DrawEntryInFrustum(DrawEntry entry)
            => GetFrustumRejectPlane(entry) < 0;

        private int GetFrustumRejectPlane(DrawEntry entry)
        {
            var coord = entry.Coord;
            float minX = (float)((long)coord.X * ChunkManager.ChunkSize - _renderOriginX);
            float maxX = minX + ChunkManager.ChunkSize * entry.ChunkSpan;
            float minZ = (float)((long)coord.Z * ChunkManager.ChunkSize - _renderOriginZ);
            float maxZ = minZ + ChunkManager.ChunkSize * entry.ChunkSpan;
            // World Y bounds come from the chunk's LAYER (ground -256..383, sky 384..1023), not
            // the ground origin. Using the ground bounds for sky chunks made them vanish when the
            // camera was up in the stratosphere (their frustum box sat below them).
            float minY = ChunkManager.OriginYForLayer(coord.Layer) - _renderOriginY;
            float maxY = minY + ChunkManager.HeightForLayer(coord.Layer);

            for (int i = 0; i < 6; i++)
            {
                var p = _frustumPlanes[i];
                float px = p.X >= 0f ? maxX : minX;
                float py = p.Y >= 0f ? maxY : minY;
                float pz = p.Z >= 0f ? maxZ : minZ;
                if (p.X * px + p.Y * py + p.Z * pz + p.W < 0f)
                {
                    return i;
                }
            }
            return -1;
        }

    }
}
