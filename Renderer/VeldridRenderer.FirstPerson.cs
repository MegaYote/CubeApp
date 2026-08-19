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
        private void DrawHand(CommandList cl)
        {
            if (_handPipeline == null || _handVertexBuffer == null || _handMesh.Length == 0 || _playerTextureSet == null) return;
            var menu = _hud.Menu;
            bool playing = menu == null || menu.Screen == GameScreen.Playing;
            if (!playing || !_firstPersonCamera || _hud.InventoryOpen || _hud.BiomeMenuOpen) return;

            float handScale = _handScale;   // arm sized like MC's first-person arm
            float sx = _handSx;             // shoulder (camera space): lower, further right
            float sy = _handSy;
            float sz = _handSz;
            float basePitch = _handBasePitch; // idle: arm tilted forward, fist aimed at the block
            float baseYaw = _handBaseYaw;     // idle: arm angled toward the screen center (left)

            // Walk bob: the hand rides the SAME wave as the camera (same phase + _bobBlend ease),
            // but at half amplitude so it's a subtler echo of the POV sway - same direction, same
            // rhythm, just gentler.
            float walkAmt = Math.Min(1f, _handWalkAmount);
            float handBob = (float)Math.Abs(Math.Sin(_handWalkPhase)) * 0.045f * walkAmt * _bobBlend;
            float handSway = (float)Math.Sin(_handWalkPhase) * 0.025f * walkAmt * _bobBlend;

            // Idle: gentle breathing sway.
            float now = (float)_handClock.Elapsed.TotalSeconds;
            float idle = 0.03f * (float)Math.Sin(now * 2.0);

            // Discrete swing envelope, MC-style: each jab is 0..1 progress and the sqrt() easing
            // makes the strike snap out fast then decelerate. Driven by REAL delta time (not a
            // per-frame constant) so the speed is identical at any framerate. The punch ONLY plays
            // forward - it punches out, holds, then snaps back to rest and repeats (no reverse
            // playback), so it reads as a rhythm of strikes.
            float handDt = now - _lastHandTime;
            _lastHandTime = now;
            if (handDt > 0.1f) handDt = 0.1f; // clamp long stalls so a hitch doesn't teleport the arm
            float s = 0f; // combined strike progress
            if (_hud.MiningProgress > 0f || _hud.HandSwing > 0f)
            {
                // Same punch for mining AND air-punching: HandSwing just feeds the same
                // _handPunchTime cycle so clicking at nothing plays the identical swing.
                // A fresh air-punch click (swing went 0 -> >0) restarts the phase so the
                // punch starts over from the top, exactly like repeatedly clicking a block.
                if (_hud.HandSwing > 0f && _prevHandSwing <= 0f) _handPunchTime = 0f;
                _prevHandSwing = _hud.HandSwing;
                _handPunchTime += handDt;
                const float cycle = 0.45f;
                float t = _handPunchTime % cycle;
                if (t < 0.35f) s = t / 0.35f; // punch OUT, then snap back and repeat instantly
                else s = 1f;                  // brief hold at full extension (no rest gap)
            }
            else
            {
                _handPunchTime = 0f;
                _prevHandSwing = 0f;
            }
            float pokeS = 0f;
            if (_hud.HandPoke > 0f)
            {
                float t = Math.Clamp(1f - _hud.HandPoke / 0.35f, 0f, 1f);
                pokeS = (float)Math.Sin(t * Math.PI);
            }
            s = Math.Min(1f, s + pokeS);

            // The punch: the fist STRETCHES FORWARD (deep toward the block) and rises toward the
            // crosshair, with a moderate forward pitch on the arm. Then it swings back to rest.
            float env = (float)Math.Sin(Math.Sqrt(Math.Max(s, 0f)) * Math.PI);
            float pitch = basePitch + idle - env * 0.6f;
            float yaw = baseYaw + env * 0.3f;
            float roll = env * 0.2f;
            float tx = -env * 0.08f;  // sweep toward screen center
            float ty = env * 0.12f + (float)Math.Sin(Math.Sqrt(Math.Max(s, 0f)) * Math.PI * 2.0) * 0.06f;
            float tz = -env * 0.25f;  // the big forward stretch

            float cp = (float)Math.Cos(pitch), sp = (float)Math.Sin(pitch);
            float cy = (float)Math.Cos(yaw), syy = (float)Math.Sin(yaw);
            float cr = (float)Math.Cos(roll), sr = (float)Math.Sin(roll);

            // Holding a hotbar item REPLACES the hand - the item rides the fist and uses the same
            // swing/punch/bob motion instead of the arm (blocks AND genuine items like tools).
            bool holdingBlock = false;
            {
                var hotbar = _hud.Hotbar;
                if (hotbar != null)
                {
                    int sel = _hud.SelectedSlot;
                    if (sel >= 0 && sel < hotbar.Count)
                    {
                        int bid = hotbar[sel];
                        var tile = ItemRegistry.GetTile(bid, out _);
                        if (bid > 0 && tile.Width > 0)
                        {
                            holdingBlock = true;
                        }
                    }
                }
            }

            float[] verts = new float[_handMesh.Length];
            if (!holdingBlock)
            {
                for (int i = 0; i < _handMesh.Length; i += 9)
            {
                // Flip the arm so it points UP from the shoulder, then rotate pitch -> yaw -> roll
                // around the shoulder (origin) for the chop, then place + sweep toward the block.
                float x = _handMesh[i] * handScale;
                float y = _handMesh[i + 1] * handScale;
                float z = _handMesh[i + 2] * handScale;
                float xf = -x;
                float yf = -y;
                float zf = z;
                float y1 = yf * cp - zf * sp;
                float z1 = yf * sp + zf * cp;
                float x2 = xf * cy + z1 * syy;
                float z2 = -xf * syy + z1 * cy;
                float x3 = x2 * cr - y1 * sr;
                float y3 = x2 * sr + y1 * cr;
                verts[i] = x3 + sx + tx + handSway;
                verts[i + 1] = y3 + sy + ty + handBob;
                verts[i + 2] = z2 + sz + tz;
                verts[i + 3] = _handMesh[i + 3];
                verts[i + 4] = _handMesh[i + 4];
                verts[i + 5] = _handMesh[i + 5];
                verts[i + 6] = _handMesh[i + 6];
                verts[i + 7] = _handMesh[i + 7];
                verts[i + 8] = _handMesh[i + 8];
            }
                _gd.UpdateBuffer(_handVertexBuffer, 0, verts);
            } // end if (!holdingBlock)

            // The held block has its own anchor (independent of the arm pose) and rides the same
            // punch/bob/sway motion.
            float blockX = _heldBlockX + tx + handSway;
            float blockY = _heldBlockY + ty + handBob;
            float blockZ = _heldBlockZ + tz;

            // A selected hotbar block replaces the hand and uses the same motion.
            if (holdingBlock) DrawHeldBlock(cl, blockX, blockY, blockZ);

            // The hand renders with depth OFF (it must always draw over the world), so the arm's
            // six faces need explicit back-to-front ordering - otherwise a hidden face can paint
            // over a visible one where they overlap on screen and a face looks invisible.
            if (!holdingBlock)
            {
                float[] faceZ = new float[6];
                for (int f = 0; f < 6; f++)
                {
                    float zSum = 0f;
                    for (int k = 0; k < 4; k++)
                    {
                        zSum += verts[(f * 4 + k) * 9 + 2];
                    }
                    faceZ[f] = zSum / 4f;
                }
                int[] order = { 0, 1, 2, 3, 4, 5 };
                Array.Sort(order, (a, b) => faceZ[a].CompareTo(faceZ[b])); // ascending z: farthest first, nearest last
                ushort[] sortedIdx = new ushort[_handIndices.Length];
                for (int f = 0; f < 6; f++)
                {
                    int src = order[f] * 6;
                    int dst = f * 6;
                    for (int k = 0; k < 6; k++) sortedIdx[dst + k] = _handIndices[src + k];
                }
                _gd.UpdateBuffer(_handIndexBuffer, 0, sortedIdx);

                // Camera-space projection: no view transform (hand positioned relative to the eye),
                // with a close near plane so the hand never clips.
                float aspect = _sc.Framebuffer.Width / (float)Math.Max(1f, _sc.Framebuffer.Height);
                var proj = Matrix4x4.CreatePerspectiveFieldOfView((float)(Math.PI / 2.0), aspect, 0.05f, 100f);
                _gd.UpdateBuffer(_handProjBuffer, 0, ref proj);

                cl.SetPipeline(_handPipeline);
                cl.SetGraphicsResourceSet(0, _handProjSet);
                if (_playerTextureSet != null) cl.SetGraphicsResourceSet(1, _playerTextureSet);
                cl.SetVertexBuffer(0, _handVertexBuffer);
                cl.SetIndexBuffer(_handIndexBuffer, IndexFormat.UInt16);
                cl.DrawIndexed((uint)_handIndices.Length, 1, 0, 0, 0);
            }
        }

        // Draws the hotbar-selected ITEM held at the first-person fist: a small cube using the
        // item-drop pipeline (block atlas OR items atlas for genuine items) with the camera-space
        // hand projection, so it rides the arm and punches with it.
        private void DrawHeldBlock(CommandList cl, float blockX, float blockY, float blockZ)
        {
            if (_heldBlockPipeline == null || _heldBlockBuffer == null || _itemDropVertexBuffer == null) return;
            var hotbar = _hud.Hotbar;
            if (hotbar == null) return;
            int selected = _hud.SelectedSlot;
            if (selected < 0 || selected >= hotbar.Count) return;
            int bid = hotbar[selected];
            if (bid <= 0) return;
            var tr = ItemRegistry.GetTile(bid, out bool fromItems);
            if (tr.Width <= 0) return;
            if (fromItems && _itemsTextureSet == null) return; // items atlas missing

            float atlasW = Math.Max(1f, fromItems ? _itemsAtlasPixelsW : _atlasWidth);
            float atlasH = Math.Max(1f, fromItems ? _itemsAtlasPixelsH : _atlasHeight);
            float blockSize = _heldBlockSize; // held block reads big up close

            // Genuine items render as a flat camera-space sprite (screen-aligned, like the hotbar
            // icon) instead of the tilted cube; blocks keep the cube.
            if (fromItems)
            {
                if (_heldBlockSpritePipeline == null || _spriteVertexBuffer == null || _spriteIndexBuffer == null) return;
                // iWorldPos is the sprite CENTER (camera space); the shader offsets the quad.
                _heldBlockScratch[0] = blockX;
                _heldBlockScratch[1] = blockY;
                _heldBlockScratch[2] = blockZ;
                _heldBlockScratch[3] = tr.X / atlasW;
                _heldBlockScratch[4] = tr.Y / atlasH;
                _heldBlockScratch[5] = tr.Width / atlasW;
                _heldBlockScratch[6] = tr.Height / atlasH;
                _heldBlockScratch[7] = 0f; _heldBlockScratch[8] = 0f; _heldBlockScratch[9] = 0f; _heldBlockScratch[10] = 1f;
                _gd.UpdateBuffer(_heldBlockBuffer, 0, _heldBlockScratch);

                cl.SetPipeline(_heldBlockSpritePipeline);
                cl.SetGraphicsResourceSet(0, _handProjSet);   // camera-space projection
                if (_itemsTextureSet != null) cl.SetGraphicsResourceSet(1, _itemsTextureSet); // items atlas
                cl.SetVertexBuffer(0, _spriteVertexBuffer);
                cl.SetVertexBuffer(1, _heldBlockBuffer);
                cl.SetIndexBuffer(_spriteIndexBuffer, IndexFormat.UInt16);
                cl.DrawIndexed(6, 1, 0, 0, 0);
                return;
            }

            // Block base corner (the cube spans blockSize^3, centered on the given position).
            _heldBlockScratch[0] = blockX - blockSize * 0.5f;
            _heldBlockScratch[1] = blockY - blockSize * 0.5f;
            _heldBlockScratch[2] = blockZ - blockSize * 0.5f;
            _heldBlockScratch[3] = tr.X / atlasW;
            _heldBlockScratch[4] = tr.Y / atlasH;
            _heldBlockScratch[5] = tr.Width / atlasW;
            _heldBlockScratch[6] = tr.Height / atlasH;
            // Fixed orientation: a 3/4 view tilted toward the camera (rotate ~-35 deg Y, -20 deg X).
            float hy = -0.611f, hx = -0.349f;
            float cyq = (float)Math.Cos(hy * 0.5f), syq = (float)Math.Sin(hy * 0.5f);
            float cxq = (float)Math.Cos(hx * 0.5f), sxq = (float)Math.Sin(hx * 0.5f);
            _heldBlockScratch[7] = sxq * cyq;
            _heldBlockScratch[8] = cxq * syq;
            _heldBlockScratch[9] = sxq * syq;
            _heldBlockScratch[10] = cxq * cyq;
            _gd.UpdateBuffer(_heldBlockBuffer, 0, _heldBlockScratch);

            cl.SetPipeline(_heldBlockPipeline);
            cl.SetGraphicsResourceSet(0, _handProjSet);   // camera-space projection
            if (fromItems) cl.SetGraphicsResourceSet(1, _itemsTextureSet); // items atlas
            else if (_textureSet != null) cl.SetGraphicsResourceSet(1, _textureSet); // block atlas
            cl.SetVertexBuffer(0, _itemDropVertexBuffer);
            cl.SetVertexBuffer(1, _heldBlockBuffer);
            cl.SetIndexBuffer(_itemDropIndexBuffer, IndexFormat.UInt16);
            cl.DrawIndexed(FallingCubeIndices, 1, 0, 0, 0);
        }

        private void DrawCrosshair(CommandList cl)
        {
            if (_crosshairPipeline == null || _crosshairVertexBuffer == null) return;
            // Only while actually playing, and never behind the inventory/biome menus (they draw
            // after this pass anyway, but skip it entirely for cleanliness).
            var menu = _hud.Menu;
            bool playing = menu == null || menu.Screen == GameScreen.Playing;
            if (!playing || _hud.InventoryOpen || _hud.BiomeMenuOpen) return;

            float w = _sc.Framebuffer.Width;
            float h = _sc.Framebuffer.Height;
            if (w <= 0 || h <= 0) return;
            float cx = (float)Math.Floor(w * 0.5f);
            float cy = (float)Math.Floor(h * 0.5f);
            const float arm = 6f;
            const float gap = 3f;
            const float halfT = 1f; // 2px-thick arms

            // Four rectangles in integer screen pixels (classic + shape, clean centre gap).
            float[] px =
            {
                cx - arm - gap, cy - halfT, cx - gap, cy - halfT, cx - gap, cy + halfT, cx - arm - gap, cy + halfT,
                cx + gap, cy - halfT, cx + arm + gap, cy - halfT, cx + arm + gap, cy + halfT, cx + gap, cy + halfT,
                cx - halfT, cy - arm - gap, cx + halfT, cy - arm - gap, cx + halfT, cy - gap, cx - halfT, cy - gap,
                cx - halfT, cy + gap, cx + halfT, cy + gap, cx + halfT, cy + arm + gap, cx - halfT, cy + arm + gap,
            };
            float[] ndc = new float[px.Length];
            for (int i = 0; i < px.Length; i += 2)
            {
                ndc[i] = (px[i] / w) * 2f - 1f;
                ndc[i + 1] = 1f - (px[i + 1] / h) * 2f;
            }
            _gd.UpdateBuffer(_crosshairVertexBuffer, 0, ndc);

            cl.SetPipeline(_crosshairPipeline);
            cl.SetVertexBuffer(0, _crosshairVertexBuffer);
            cl.SetIndexBuffer(_crosshairIndexBuffer, IndexFormat.UInt16);
            cl.DrawIndexed(4 * 6, 1, 0, 0, 0);
        }

    }
}