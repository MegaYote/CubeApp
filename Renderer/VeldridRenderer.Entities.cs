using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using Veldrid;
using Veldrid.SPIRV;

namespace CubeApp.Renderer
{
    public sealed partial class VeldridRenderer : IRenderer, IDisposable
    {
        private void DrawPlayers(CommandList cl)
        {
            var instances = _playerInstances;
            if (instances.Count == 0 || _modelPipeline == null || _playerTextureSet == null
                || _playerBones.Length == 0 || _playerVertsPerInstance == 0)
            {
                return;
            }

            int totalVertexFloats = instances.Count * _playerVertsPerInstance * DuckFloatsPerVertex;
            int totalIndices = instances.Count * _playerIndicesPerInstance;

            if (_playerVertexScratch.Length < totalVertexFloats)
            {
                _playerVertexScratch = new float[totalVertexFloats];
            }
            if (_playerIndexScratch.Length < totalIndices)
            {
                _playerIndexScratch = new ushort[totalIndices];
            }

            int vf = 0;
            int ii = 0;
            ushort baseVertex = 0;
            foreach (var inst in instances)
            {
                _entityLight = GetMobLight(inst.Position.X, inst.Position.Y, inst.Position.Z);
                WritePlayer(inst, ref vf, ref ii, ref baseVertex);
            }

            EnsurePlayerBuffers((uint)(totalVertexFloats * sizeof(float)), (uint)(totalIndices * sizeof(ushort)));
            _gd.UpdateBuffer(_playerVertexBuffer, 0, ref _playerVertexScratch[0], (uint)(totalVertexFloats * sizeof(float)));
            _gd.UpdateBuffer(_playerIndexBuffer, 0, ref _playerIndexScratch[0], (uint)(totalIndices * sizeof(ushort)));

            cl.SetPipeline(_modelPipeline);
            cl.SetGraphicsResourceSet(0, _projViewSet);
            cl.SetGraphicsResourceSet(1, _playerTextureSet);
            cl.SetVertexBuffer(0, _playerVertexBuffer);
            cl.SetIndexBuffer(_playerIndexBuffer, IndexFormat.UInt16);
            cl.DrawIndexed((uint)totalIndices, 1, 0, 0, 0);
        }

        // Draws every generic MobModel-driven mob type (coyote, zombie, ...) discovered in
        // MobEntities/. Each type is baked into its OWN vertex/index buffer and drawn with a single
        // call - the mobs of one type share a model, and a per-mob UpdateBuffer on a shared instance
        // buffer would corrupt earlier draws. A subtle walk-cycle bob + body sway is applied on the
        // CPU around the model origin (feet) to sell the motion.
        private void DrawModelMobs(CommandList cl)
        {
            foreach (var kvp in _modelMobs)
            {
                var entry = kvp.Value;
                var instances = entry.Instances;
                if (instances.Count == 0 || entry.Model == null || _modelPipeline == null || entry.TextureSet == null) continue;

                int vertsPer = entry.Model.VertexCount;
                int idxPer = entry.Model.IndexCount;
                int totalVertexFloats = instances.Count * vertsPer * DuckFloatsPerVertex;
                int totalIndices = instances.Count * idxPer;
                if (totalVertexFloats == 0 || totalIndices == 0) continue;

                if (entry.VertexScratch.Length < totalVertexFloats) entry.VertexScratch = new float[totalVertexFloats];
                if (entry.IndexScratch.Length < totalIndices) entry.IndexScratch = new ushort[totalIndices];

                int vf = 0, ii = 0;
                ushort baseVertex = 0;
                foreach (var inst in instances)
                {
                    // The mob's animation clock advances only while it actually walks, and AnimBlend
                    // eases back to 0 when idle - so the GLB trot plays while moving and the mob
                    // returns to its neutral stance when it stops (no frozen mid-stride). The mob is
                    // lit by its position (same block-light rules as terrain), multiplied by the
                    // global night dim.
                    float mobLight = GetMobLight(inst.Position.X, inst.Position.Y, inst.Position.Z);
                    entry.Model.WriteInstance(entry.VertexScratch, ref vf, entry.IndexScratch, ref ii, ref baseVertex,
                        (float)inst.Position.X, (float)inst.Position.Y, (float)inst.Position.Z, inst.Yaw,
                        inst.AnimTime, inst.AnimBlend, mobLight, inst.HeadYawLocal, inst.HurtTimer, inst.HeadPitchLocal,
                        inst.IsDead, inst.DeathT, inst.DeathRollDir, inst.Scale);
                }

                EnsureMobBuffers(entry, (uint)(totalVertexFloats * sizeof(float)), (uint)(totalIndices * sizeof(ushort)));
                _gd.UpdateBuffer(entry.VertexBuffer, 0, ref entry.VertexScratch[0], (uint)(totalVertexFloats * sizeof(float)));
                _gd.UpdateBuffer(entry.IndexBuffer, 0, ref entry.IndexScratch[0], (uint)(totalIndices * sizeof(ushort)));

                cl.SetPipeline(_modelPipeline);
                cl.SetGraphicsResourceSet(0, _projViewSet);
                cl.SetGraphicsResourceSet(1, entry.TextureSet);
                cl.SetVertexBuffer(0, entry.VertexBuffer);
                cl.SetIndexBuffer(entry.IndexBuffer, IndexFormat.UInt16);
                cl.DrawIndexed((uint)totalIndices, 1, 0, 0, 0);
            }
        }

        private void EnsureMobBuffers(MobModelEntry entry, uint vertexBytes, uint indexBytes)
        {
            if (entry.VertexBuffer == null || entry.VertexCapacity < vertexBytes)
            {
                entry.VertexBuffer?.Dispose();
                entry.VertexBuffer = _gd.ResourceFactory.CreateBuffer(new BufferDescription(
                    Math.Max(vertexBytes, 512), BufferUsage.VertexBuffer | BufferUsage.Dynamic));
                entry.VertexCapacity = Math.Max(vertexBytes, 512);
            }
            if (entry.IndexBuffer == null || entry.IndexCapacity < indexBytes)
            {
                entry.IndexBuffer?.Dispose();
                entry.IndexBuffer = _gd.ResourceFactory.CreateBuffer(new BufferDescription(
                    Math.Max(indexBytes, 512), BufferUsage.IndexBuffer | BufferUsage.Dynamic));
                entry.IndexCapacity = Math.Max(indexBytes, 512);
            }
        }

        // Poses one player's bones (limb swing / head turn) and bakes them, with the body yaw,
        // hurt-flash tint and death roll, into the shared scratch buffers. Same scheme as WriteDuck
        // but with voxel-style limb animation and no in-air body tilt.
        private void WritePlayer(in CubeApp.DuckInstance inst, ref int vf, ref int ii, ref ushort baseVertex)
        {
            bool isDead = inst.IsDead;
            float hurtFactor = isDead ? 0f : (inst.HurtTimer > 0f ? Math.Clamp(inst.HurtTimer / 0.2f, 0f, 1f) : 0f);
            // Hurt makes the walk cycle erratic: faster, choppier limb swings, plus a panicked
            // jitter even while standing still. No body wobble - just the arms and legs acting up.
            float swing = isDead ? 0f
                : (float)Math.Sin(inst.WalkPhase * (1f + 2.8f * hurtFactor)) * inst.WalkAmount * (1f + 0.5f * hurtFactor)
                  + (float)Math.Sin(inst.HurtTimer * 55.0f) * hurtFactor * 0.22f;
            float bob = isDead ? 0f : Math.Abs((float)Math.Sin(inst.WalkPhase * 2.0f)) * 0.03f * inst.WalkAmount;
            float deathRoll = isDead ? inst.DeathRollDir * (float)(Math.PI * 0.5) * (float)Math.Pow(inst.DeathT, 0.9) : 0f;

            float tiltZ = isDead ? deathRoll : 0f;
            float cosR = (float)Math.Cos(tiltZ), sinR = (float)Math.Sin(tiltZ);

            float renderYaw = inst.Yaw + (float)Math.PI;
            float cosY = (float)Math.Cos(renderYaw), sinY = (float)Math.Sin(renderYaw);

            float px = (float)inst.Position.X;
            float py = (float)inst.Position.Y;
            float pz = (float)inst.Position.Z;

            float blink = isDead ? 1f : (inst.HurtTimer > 0f ? ((float)Math.Sin(inst.HurtTimer * 95.0f) > 0f ? 1f : 0.72f) : 0f);
            float flashBlend = isDead ? 1f : (inst.HurtTimer > 0f ? Math.Clamp((inst.HurtTimer / 0.20f) * blink, 0f, 1f) : 0f);
            float gbMul = 1f - 0.82f * flashBlend;

            foreach (var bone in _playerBones)
            {
                bool isHead = bone.Id == PlayerBoneId.Head;
                float ca, sa, cpa = 1f, spa = 0f;
                if (isHead)
                {
                    // Head: yaw (Y) AND pitch (X) so it swivels and nods like a real neck.
                    float headYaw = inst.HeadYawLocal;
                    ca = (float)Math.Cos(headYaw); sa = (float)Math.Sin(headYaw);
                    float headPitch = inst.HeadPitchLocal;
                    cpa = (float)Math.Cos(headPitch); spa = (float)Math.Sin(headPitch);
                }
                else
                {
                    float angle = PlayerBoneAnimDelta(bone.Id, swing, 0f);
                    ca = (float)Math.Cos(angle); sa = (float)Math.Sin(angle);
                }

                foreach (var v in bone.Vertices)
                {
                    float lx = v.X - bone.PivotX;
                    float ly = v.Y - bone.PivotY;
                    float lz = v.Z - bone.PivotZ;
                    float rx, ry, rz;
                    if (isHead)
                    {
                        // Pitch around X first (nod up/down), then yaw around Y (turn).
                        float pitY = ly * cpa - lz * spa;
                        float pitZ = ly * spa + lz * cpa;
                        ry = pitY;
                        rx = lx * ca + pitZ * sa;
                        rz = -lx * sa + pitZ * ca;
                    }
                    else
                    {
                        rx = lx; ry = ly; rz = lz;
                        switch (bone.Axis)
                        {
                            case DuckBoneAxis.X: ry = ly * ca - lz * sa; rz = ly * sa + lz * ca; break;
                            case DuckBoneAxis.Y: rx = lx * ca + lz * sa; rz = -lx * sa + lz * ca; break;
                            case DuckBoneAxis.Z: rx = lx * ca - ly * sa; ry = lx * sa + ly * ca; break;
                        }
                    }
                    float mx = bone.PivotX + rx;
                    float my = bone.PivotY + ry + bob;
                    float mz = bone.PivotZ + rz;

                    float ax = mx * cosR - my * sinR;
                    float ay = mx * sinR + my * cosR;
                    float az = mz;
                    float fx = ax * cosY + az * sinY;
                    float fz = -ax * sinY + az * cosY;

                    // Mobs are bigger now: scale the whole model about its feet origin.
                    fx *= PlayerModelScale;
                    ay *= PlayerModelScale;
                    fz *= PlayerModelScale;

                    _playerVertexScratch[vf++] = px + fx;
                    _playerVertexScratch[vf++] = py + ay;
                    _playerVertexScratch[vf++] = pz + fz;
                    _playerVertexScratch[vf++] = v.U;
                    _playerVertexScratch[vf++] = v.V;
                    _playerVertexScratch[vf++] = v.Shade * _entityLight;
                    _playerVertexScratch[vf++] = v.Shade * gbMul * _entityLight;
                    _playerVertexScratch[vf++] = v.Shade * gbMul * _entityLight;
                    _playerVertexScratch[vf++] = 1f;
                }

                for (int k = 0; k < bone.Indices.Length; k++)
                {
                    _playerIndexScratch[ii++] = (ushort)(bone.Indices[k] + baseVertex);
                }
                baseVertex += (ushort)bone.Vertices.Length;
            }
        }

        // Limb swing: opposite arm/leg pairs, head follows the local head yaw.
        private static float PlayerBoneAnimDelta(PlayerBoneId id, float swing, float headYawLocal)
        {
            switch (id)
            {
                case PlayerBoneId.Head: return headYawLocal;
                case PlayerBoneId.RightArm: return swing * 0.9f;
                case PlayerBoneId.LeftArm: return -swing * 0.9f;
                case PlayerBoneId.RightLeg: return -swing * 1.2f;
                case PlayerBoneId.LeftLeg: return swing * 1.2f;
                default: return 0f;
            }
        }

        private void EnsurePlayerBuffers(uint vbSize, uint ibSize)
        {
            if (_playerVertexBuffer == null || _playerVertexCapacity < vbSize)
            {
                _playerVertexBuffer?.Dispose();
                _playerVertexBuffer = _gd.ResourceFactory.CreateBuffer(new BufferDescription(vbSize, BufferUsage.VertexBuffer | BufferUsage.Dynamic));
                _playerVertexCapacity = vbSize;
            }
            if (_playerIndexBuffer == null || _playerIndexCapacity < ibSize)
            {
                _playerIndexBuffer?.Dispose();
                _playerIndexBuffer = _gd.ResourceFactory.CreateBuffer(new BufferDescription(ibSize, BufferUsage.IndexBuffer | BufferUsage.Dynamic));
                _playerIndexCapacity = ibSize;
            }
        }

        // The E-menu inventory: a scrollable grid of block icons rendered with their isometric
        // cube icon. Clicks are queued and consumed by Program on the next frame. Creative shows
        // every block; survival shows only the blocks the player actually owns, with counts.
    }
}