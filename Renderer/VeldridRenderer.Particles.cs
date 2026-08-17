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
        private void SortPassBackToFront(int passId, System.Collections.Generic.List<(Cubuild.ChunkCoordinates Coord, IndirectDrawIndexedArguments Cmd)> commands)
        {
            if (!_cameraPosition.HasValue) return;
            var cam = _cameraPosition.Value;
            int camChunkX = (int)Math.Floor(cam.X / (double)ChunkManager.ChunkSize);
            int camChunkZ = (int)Math.Floor(cam.Z / (double)ChunkManager.ChunkSize);
            if (camChunkX == _lastSortChunkX[passId] && camChunkZ == _lastSortChunkZ[passId] && commands.Count == _lastSortCount[passId])
            {
                return; // nothing changed: keep the existing order
            }
            _lastSortChunkX[passId] = camChunkX;
            _lastSortChunkZ[passId] = camChunkZ;
            _lastSortCount[passId] = commands.Count;

            commands.Sort((a, b) => ChunkCenterDistSq(b.Coord, cam).CompareTo(ChunkCenterDistSq(a.Coord, cam))); // far first
        }

        private static float ChunkCenterDistSq(Cubuild.ChunkCoordinates coord, Cubuild.Point3D cam)
        {
            float cx = coord.X * ChunkManager.ChunkSize + ChunkManager.ChunkSize * 0.5f;
            float cz = coord.Z * ChunkManager.ChunkSize + ChunkManager.ChunkSize * 0.5f;
            float cy = ChunkManager.OriginYForLayer(coord.Layer) + ChunkManager.HeightForLayer(coord.Layer) * 0.5f;
            float dx = cx - (float)cam.X;
            float dy = cy - (float)cam.Y;
            float dz = cz - (float)cam.Z;
            return dx * dx + dy * dy + dz * dz;
        }

        // Renders the block-break particles as camera-facing quads using the world pipeline
        // (atlas sampling + depth test), so they're occluded by terrain like any block face.
        private void DrawParticles(CommandList cl)
        {
            int n = _particleCount;
            if (n == 0) return;
            float atlasW = Math.Max(1f, _atlasWidth);
            float atlasH = Math.Max(1f, _atlasHeight);

            int vertFloats = n * 4 * 6;   // packed: Float3 pos + 3x UInt1 = 6 uint32s per vertex
            if (_particleVertexScratch.Length < vertFloats) _particleVertexScratch = new float[vertFloats];
            int indexCount = n * 6;
            if (_particleIndexScratch.Length < indexCount) _particleIndexScratch = new ushort[indexCount];

            var r = _cameraRight;
            var u = _cameraUp;
            int vf = 0;
            int ii = 0;
            for (int i = 0; i < n; i++)
            {
                ref var p = ref _particles[i];
                float half = p.Size * 0.5f;
                var rx = r.X * half; var ry = r.Y * half; var rz = r.Z * half;
                var ux = u.X * half; var uy = u.Y * half; var uz = u.Z * half;

                float oX = p.X, oY = p.Y, oZ = p.Z;
                // corners: bottom-left, bottom-right, top-right, top-left
                float[,] corners =
                {
                    { oX - rx - ux, oY - ry - uy, oZ - rz - uz },
                    { oX + rx - ux, oY + ry - uy, oZ + rz - uz },
                    { oX + rx + ux, oY + ry + uy, oZ + rz + uz },
                    { oX - rx + ux, oY - ry + uy, oZ - rz + uz }
                };
                // Tile rect as atlas texels (matches the packed chunk format's aPack2).
                uint tileX = (uint)Math.Clamp((int)p.TileX, 0, 255);
                uint tileY = (uint)Math.Clamp((int)p.TileY, 0, 255);
                uint tileW = (uint)Math.Clamp((int)p.TileW, 0, 255);
                uint tileH = (uint)Math.Clamp((int)p.TileH, 0, 255);
                uint pack2 = (tileX << 24) | (tileY << 16) | (tileW << 8) | tileH;
                uint shadeByte = (uint)Math.Clamp((int)Math.Round(p.Brightness * 255f), 0, 255);
                uint pack3 = shadeByte | (255u << 8); // alpha byte 255, alphaMode 0 (opaque)
                int baseV = i * 4;
                for (int c = 0; c < 4; c++)
                {
                    // UVs must never hit exactly 1.0 - the world shader samples via fract(vLocalUV),
                    // and fract(1.0) == 0.0 would collapse the whole quad onto one texel.
                    float du = (c == 1 || c == 2) ? 0.999f : 0f;
                    float dv = (c == 2 || c == 3) ? 0.999f : 0f;
                    uint duFixed = (uint)Math.Clamp((int)Math.Round(du * 256.0), 0, 0xFFFF);
                    uint dvFixed = (uint)Math.Clamp((int)Math.Round(dv * 256.0), 0, 0xFFFF);
                    uint pack1 = (duFixed << 16) | dvFixed;

                    _particleVertexScratch[vf++] = corners[c, 0];
                    _particleVertexScratch[vf++] = corners[c, 1];
                    _particleVertexScratch[vf++] = corners[c, 2];
                    _particleVertexScratch[vf++] = BitConverter.UInt32BitsToSingle(pack1);
                    _particleVertexScratch[vf++] = BitConverter.UInt32BitsToSingle(pack2);
                    _particleVertexScratch[vf++] = BitConverter.UInt32BitsToSingle(pack3);
                }
                _particleIndexScratch[ii++] = (ushort)(baseV + 0);
                _particleIndexScratch[ii++] = (ushort)(baseV + 1);
                _particleIndexScratch[ii++] = (ushort)(baseV + 2);
                _particleIndexScratch[ii++] = (ushort)(baseV + 0);
                _particleIndexScratch[ii++] = (ushort)(baseV + 2);
                _particleIndexScratch[ii++] = (ushort)(baseV + 3);
            }

            EnsureParticleBuffers((uint)(vertFloats * sizeof(float)), (uint)(indexCount * sizeof(ushort)));
            _gd.UpdateBuffer(_particleVertexBuffer, 0, _particleVertexScratch);
            _gd.UpdateBuffer(_particleIndexBuffer, 0, _particleIndexScratch);

            cl.SetPipeline(_pipeline);
            cl.SetGraphicsResourceSet(0, _projViewSet);
            if (_textureSet != null) cl.SetGraphicsResourceSet(1, _textureSet);
            cl.SetGraphicsResourceSet(2, _fogSet);
            cl.SetVertexBuffer(0, _particleVertexBuffer);
            cl.SetIndexBuffer(_particleIndexBuffer, IndexFormat.UInt16);
            cl.DrawIndexed((uint)indexCount);
        }

        private void EnsureParticleBuffers(uint vbBytes, uint ibBytes)
        {
            if (_particleVertexBuffer == null || _particleVertexCapacityBytes < vbBytes)
            {
                _particleVertexBuffer?.Dispose();
                _particleVertexBuffer = _gd.ResourceFactory.CreateBuffer(new BufferDescription(
                    Math.Max(vbBytes, 4096), BufferUsage.VertexBuffer | BufferUsage.Dynamic));
                _particleVertexCapacityBytes = Math.Max(vbBytes, 4096);
            }
            if (_particleIndexBuffer == null || _particleIndexCapacityBytes < ibBytes)
            {
                _particleIndexBuffer?.Dispose();
                _particleIndexBuffer = _gd.ResourceFactory.CreateBuffer(new BufferDescription(
                    Math.Max(ibBytes, 2048), BufferUsage.IndexBuffer | BufferUsage.Dynamic));
                _particleIndexCapacityBytes = Math.Max(ibBytes, 2048);
            }
        }

        // Pushes distance fog, tuned so NEAR geometry (like cave walls) is completely
        // clear - the fog only visibly kicks in at distance. Fog color = sky color, dimmed by the
        // celestial angle, so the horizon blends seamlessly. Raising fogStart to 25% of fogEnd
        // keeps close terrain (and cave walls 3-20 blocks away) at ~zero fog - that's why
        // Caves feel dark and creepy: the darkness comes from block light, and the fog
        // never brightens near walls.
    }
}