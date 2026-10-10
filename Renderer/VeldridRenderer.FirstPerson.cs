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
            // Minecraft uses a SECOND envelope for the swing: f = sin(s*s*PI), alongside
            // f1 = sin(sqrt(s)*PI) which is exactly `env` above. Both drive the held item's
            // rotation; Cubuild previously animated only the arm.
            float swingF = (float)Math.Sin(Math.Max(s, 0f) * Math.Max(s, 0f) * Math.PI);
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

// The arm ALWAYS draws. A held item no longer replaces it - it is placed in the fist
            // and rides the arm's own transform, so there is a single source of truth for
            // first-person pose instead of a second, independent stack that has to be kept
            // in sync with this one.
            float[] verts = new float[_handMesh.Length];
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
            }

            // The held item is drawn in the fist, transformed by the SAME pitch/yaw/roll chain
            // as the arm above, so it moves with the hand for free.
            if (holdingBlock) DrawHeldItemInHand(cl, cp, sp, cy, syy, cr, sr,
                sx + tx + handSway, sy + ty + handBob, sz + tz);

            // The hand renders with depth OFF (it must always draw over the world), so the arm's
            // six faces need explicit back-to-front ordering - otherwise a hidden face can paint
            // over a visible one where they overlap on screen and a face looks invisible.
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
        /// <summary>
        /// The held flat-item orientation as a quaternion in the (x, y, z, w) order the shader
        /// consumes (it uses iRot.xyz as a raw axis, so any rotation works).
        ///
        /// Minecraft's ItemRenderer.renderItemInFirstPerson builds this from, applied to the
        /// vertex in order:
        ///     glRotatef( 45,  0, 1, 0)                 <- resting, unconditional
        ///     glRotatef(-20 * f,   0, 1, 0)             <- SWING
        ///     glRotatef(-20 * f1,  0, 0, 1)             <- SWING
        ///     glRotatef(-80 * f1,  1, 0, 0)             <- SWING
        ///     glRotatef( 50,  0, 1, 0)                 <- resting, flat-item branch
        ///     glRotatef(335,  0, 0, 1)                 <- resting, flat-item branch
        /// with f  = sin(s*s*PI) and f1 = sin(sqrt(s)*PI), s = swing progress.
        ///
        /// GL post-multiplies, so composing in application order gives
        /// qy(-70) * qx(-40) * qx(-80*f1) * qz(-20*f1) * qy(-20*f).
        ///
        /// The swing triple is what Cubuild was missing entirely: the arm animated, but the held
        /// item only ever inherited the shared translation offsets, so it never rotated at all.
        /// </summary>
        private static void ComposeMinecraftHoldRotation(float swingF, float swingF1,
            out float x, out float y, out float z, out float w)
        {
            // q = a * b, quaternions as scalars (x, y, z, w).
            void Mul(double ax, double ay, double az, double aw,
                     double bx, double by, double bz, double bw,
                     out double rx, out double ry, out double rz, out double rw)
            {
                rx = aw * bx + ax * bw + (ay * bz - az * by);
                ry = aw * by + ay * bw + (az * bx - ax * bz);
                rz = aw * bz + az * bw + (ax * by - ay * bx);
                rw = aw * bw - (ax * bx + ay * by + az * bz);
            }

            static void Axis(int axis, double deg, out double ox, out double oy, out double oz, out double ow)
            {
                double h = deg * Math.PI / 180.0 * 0.5;
                double s = Math.Sin(h), c = Math.Cos(h);
                ox = 0; oy = 0; oz = 0; ow = c;
                if (axis == 0) ox = s;
                else if (axis == 1) oy = s;
                else oz = s;
            }

            // RESTING pose. Minecraft's literal 95 deg Y / 335 deg Z does not transplant:
            // those degrees are applied inside a transform stack that also carries the camera
            // pitch/yaw and rotateArm, which Cubuild's held-item path has no equivalent for.
            // Taken verbatim the card ends up nearly edge-on with its front normal pointing
            // AWAY from the camera (-X), so you see the mirrored back face - upside down in one
            // axis, flipped in the other. These are the magnitudes that actually frame a readable
            // 3/4 view here; the swing below is Minecraft's exactly.
            Axis(1, -70.0, out double ax, out double ay, out double az, out double aw);   // yaw
            Axis(0, -40.0, out double bx, out double by, out double bz, out double bw);   // pitch
            Mul(ax, ay, az, aw, bx, by, bz, bw, out double x1, out double y1, out double z1, out double w1);
            Axis(0, -80.0 * swingF1, out bx, out by, out bz, out bw);
            Mul(x1, y1, z1, w1, bx, by, bz, bw, out double x2, out double y2, out double z2, out double w2);
            Axis(2, -20.0 * swingF1, out bx, out by, out bz, out bw);
            Mul(x2, y2, z2, w2, bx, by, bz, bw, out double x3, out double y3, out double z3, out double w3);
            Axis(1, -20.0 * swingF, out bx, out by, out bz, out bw);
            Mul(x3, y3, z3, w3, bx, by, bz, bw, out double fx, out double fy, out double fz, out double fw);

            x = (float)fx; y = (float)fy; z = (float)fz; w = (float)fw;
        }

        /// <summary>
        /// Minecraft's COMPLETE first-person transform for a held flat item, as a matrix.
        ///
        /// Reproduces ItemRenderer.renderItemInFirstPerson (1.6 decompile) operation for
        /// operation, in the order OpenGL applies them (each call post-multiplies, so the LAST
        /// call acts on the vertex FIRST). The scales and translates matter as much as the
        /// angles - they are sandwiched BETWEEN the rotations, so they cannot be folded into a
        /// single rotation. That is why transplanting the angles alone never lined up:
        ///
        ///   T(swing)                       if swinging
        ///   T(0.7k, -0.65k - (1-e)*0.6, -0.9k)     k = 0.8, e = equip progress
        ///   Ry(45)                         resting
        ///   Ry(-20f) Rz(-20f1) Rx(-80f1)   SWING
        ///   S(0.4)                         base scale
        ///   T(0, -0.3, 0)
        ///   S(1.5)
        ///   Ry(50) Rz(335)                 resting
        ///   T(-15/16, -1/16, 0)            recentres the 0..1 quad on the pivot
        ///
        /// with f = sin(s*s*PI), f1 = sin(sqrt(s)*PI), s = swing progress 0..1.
        /// </summary>
        private static Matrix4x4 BuildMinecraftHeldItemMatrix(float swing, float equip, out float f, out float f1)
        {
            double sp = Math.Max(swing, 0f);
            f = (float)Math.Sin(sp * sp * Math.PI);
            f1 = (float)Math.Sin(Math.Sqrt(sp) * Math.PI);

            var m = Matrix4x4.Identity;

            if (sp > 0f)
            {
                // Note the middle term: sin(sqrt(s) * PI * 2), a different harmonic again.
                float yBob = (float)Math.Sin(Math.Sqrt(sp) * Math.PI * 2.0);
                m *= Matrix4x4.CreateTranslation(-f1 * 0.3f, yBob * 0.4f, -f * 0.4f);
            }

            const float k = 0.8f;
            m *= Matrix4x4.CreateTranslation(0.7f * k, -0.65f * k - (1f - equip) * 0.6f, -0.9f * k);
            m *= Matrix4x4.CreateRotationY(MathHelper45);

            if (sp > 0f)
            {
                m *= Matrix4x4.CreateRotationY(-20f * f * MathF.PI / 180f);
                m *= Matrix4x4.CreateRotationZ(-20f * f1 * MathF.PI / 180f);
                m *= Matrix4x4.CreateRotationX(-80f * f1 * MathF.PI / 180f);
            }

            m *= Matrix4x4.CreateScale(0.4f);
            m *= Matrix4x4.CreateTranslation(0f, -0.3f, 0f);
            m *= Matrix4x4.CreateScale(1.5f);
            m *= Matrix4x4.CreateRotationY(50f * MathF.PI / 180f);
            m *= Matrix4x4.CreateRotationZ(335f * MathF.PI / 180f);
            m *= Matrix4x4.CreateTranslation(-15f / 16f, -1f / 16f, 0f);
            return m;
        }

        private const float MathHelper45 = 45f * MathF.PI / 180f;

        /// <summary>
        /// Draws the selected hotbar item positioned in the fist, riding the ARM's transform.
        ///
        /// The prism's vertices are offset to <c>_heldItemGrip</c> (a point in the same
        /// pivot-relative space the arm mesh uses), then pushed through the identical
        /// pitch -> yaw -> roll chain and shoulder placement the arm vertices just went
        /// through. Because both use one transform, the item stays in the hand through the
        /// swing, punch, bob and sway with no duplicated math to drift out of sync.
        /// </summary>
        private void DrawHeldItemInHand(CommandList cl,
            float cp, float sp, float cy, float syy, float cr, float sr,
            float ox, float oy, float oz)
        {
            if (_heldBlockSpritePipeline == null || _heldItemPrismVertexBuffer == null
                || _heldItemPrismIndexBuffer == null || _heldItemPrismBase.Length == 0) return;
            var hotbar = _hud.Hotbar;
            if (hotbar == null) return;
            int selected = _hud.SelectedSlot;
            if (selected < 0 || selected >= hotbar.Count) return;
            int bid = hotbar[selected];
            if (bid <= 0) return;

            bool fromItems;
            var tr = ItemRegistry.GetTile(bid, out fromItems);
            if (tr.Width <= 0) return;

            float atlasW = Math.Max(1f, fromItems ? _itemsAtlasPixelsW : _atlasWidth);
            float atlasH = Math.Max(1f, fromItems ? _itemsAtlasPixelsH : _atlasHeight);

            // Blocks keep their existing cube path; only flat 2D items ride the arm.
            if (!fromItems) { DrawHeldBlock(cl, _heldBlockX + ox, _heldBlockY + oy, _heldBlockZ + oz); return; }
            if (_itemsTextureSet == null) return;

            float gx = _heldItemGripX, gy = _heldItemGripY, gz = _heldItemGripZ;
            float itemScale = _heldItemScale;
            float spin = _heldItemSpin;

            float[] src = _heldItemPrismBase;
            float[] dst = _heldItemPrismScratch;
            for (int v = 0; v * 9 < src.Length; v++)
            {
                int o = v * 9;
                // Local: scale the card, spin it about Z (screen-facing tilt), then offset to the
                // grip point in the arm's pivot-relative space.
                float x = src[o] * itemScale;
                float y = src[o + 1] * itemScale;
                float z = src[o + 2] * itemScale;
                float cs = MathF.Cos(spin), sn = MathF.Sin(spin);
                float xs = x * cs - y * sn;
                float ys = x * sn + y * cs;
                x = xs + gx; y = ys + gy; z = z + gz;

                // Exactly the arm's chain: flip, pitch, yaw, roll.
                float xf = -x;
                float yf = -y;
                float zf = z;
                float y1 = yf * cp - zf * sp;
                float z1 = yf * sp + zf * cp;
                float x2 = xf * cy + z1 * syy;
                float z2 = -xf * syy + z1 * cy;
                float x3 = x2 * cr - y1 * sr;
                float y3 = x2 * sr + y1 * cr;

                dst[o] = x3 + ox;
                dst[o + 1] = y3 + oy;
                dst[o + 2] = z2 + oz;
            }
            _gd.UpdateBuffer(_heldItemPrismVertexBuffer, 0, dst);

            // iWorldPos is zero - the transform above already placed the verts in camera space.
            _heldBlockScratch[0] = 0f;
            _heldBlockScratch[1] = 0f;
            _heldBlockScratch[2] = 0f;
            _heldBlockScratch[3] = tr.X / atlasW;
            _heldBlockScratch[4] = tr.Y / atlasH;
            _heldBlockScratch[5] = tr.Width / atlasW;
            _heldBlockScratch[6] = tr.Height / atlasH;
            _heldBlockScratch[7] = 0f; _heldBlockScratch[8] = 0f;
            _heldBlockScratch[9] = 0f; _heldBlockScratch[10] = 1f;
            _gd.UpdateBuffer(_heldBlockBuffer, 0, _heldBlockScratch);

            cl.SetPipeline(_heldBlockSpritePipeline);
            cl.SetGraphicsResourceSet(0, _handProjSet);
            cl.SetGraphicsResourceSet(1, _itemsTextureSet);
            cl.SetVertexBuffer(0, _heldItemPrismVertexBuffer);
            cl.SetVertexBuffer(1, _heldBlockBuffer);
            cl.SetIndexBuffer(_heldItemPrismIndexBuffer, IndexFormat.UInt16);
            cl.DrawIndexed(36, 1, 0, 0, 0);
        }

        private void DrawHeldBlock(CommandList cl, float blockX, float blockY, float blockZ,
            float swing = 0f)
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

            // The hold angle, shared by BOTH paths so a held item and a held block sit
            // identically in the fist. A 3/4 view tilted toward the camera: ~-70 deg Y and
            // -40 deg X once the axis-angle -> quaternion halving is applied.
            const float holdYaw = -0.611f, holdPitch = -0.349f;
            float cyq = (float)Math.Cos(holdYaw * 0.5f), syq = (float)Math.Sin(holdYaw * 0.5f);
            float cxq = (float)Math.Cos(holdPitch * 0.5f), sxq = (float)Math.Sin(holdPitch * 0.5f);

// Genuine items render as a shallow extruded CARD, oriented to match Minecraft.
            //
            // From ItemRenderer.renderItemInFirstPerson (the 1.6 decompile kept as Cubuild
            // reference material):
            //     GL11.glRotatef(45.0F,  0, 1, 0);        <- unconditional, outside the swing block
            //     ... flat-item branch only:
            //     GL11.glRotatef(50.0F,  0, 1, 0);
            //     GL11.glRotatef(335.0F, 0, 0, 1);
            // GL rotations POST-multiply the current matrix, so the net transform applied to
            // the vertex is R_z(335) * R_y(50) * R_y(45) == R_z(335) * R_y(95). Only lines
            // further up (the -20/-20/-80 triple) are swing-dependent; the resting pose is
            // this constant tilt, which is why a vanilla held tool reads as a solid object
            // rather than a sprite pasted on the screen.
            //
            // This previously used the held-BLOCK cube's angle (~-70 deg Y, -40 deg X), which
            // tilts on the wrong axis entirely: Minecraft's is Y and Z with no X component.
            if (fromItems)
            {
                if (_heldBlockSpritePipeline == null || _heldItemPrismVertexBuffer == null
                    || _heldItemPrismIndexBuffer == null || _heldItemPrismBase.Length == 0) return;

                // Minecraft's entire stack, applied to the prism on the CPU so every translate
                // and scale lands in the right place between the rotations. The shader only
                // projects and samples, so iRot stays identity.
                //
                // System.Numerics is ROW-vector convention - Vector4.Transform(v, M) computes
                // v*M, which equals M-transpose*v in OpenGL's COLUMN convention. Passing the
                // matrix straight through therefore applied the INVERSE of Minecraft's whole
                // transform: every rotation ran backwards and every translation landed wrong,
                // which is why the item vanished. Transpose first so this is a true M*v.
                var mc = BuildMinecraftHeldItemMatrix(swing, 1f, out float mf, out float mf1);
                var mcT = Matrix4x4.Transpose(mc);
                float[] src = _heldItemPrismBase;
                float[] dst = _heldItemPrismScratch;
                for (int v = 0; v * 9 < src.Length; v++)
                {
                    int o = v * 9;
                    var p = Vector4.Transform(
                        new Vector4(src[o], src[o + 1], src[o + 2], 0f), mcT);
                    dst[o] = p.X;
                    dst[o + 1] = p.Y;
                    dst[o + 2] = p.Z;
                }
                _gd.UpdateBuffer(_heldItemPrismVertexBuffer, 0, dst);

                // Minecraft's matrix already positions the item (it carries its own anchor
                // translate, 0.7k/-0.65k/-0.9k). Adding Cubuild's _heldBlockX/Y/Z on top
                // double-positions it and shoves it off-screen, so for the 1:1 item path the
                // instance origin is zero and MC's stack is the sole placement.
                _heldBlockScratch[0] = 0f;
                _heldBlockScratch[1] = 0f;
                _heldBlockScratch[2] = 0f;
                _heldBlockScratch[3] = tr.X / atlasW;
                _heldBlockScratch[4] = tr.Y / atlasH;
                _heldBlockScratch[5] = tr.Width / atlasW;
                _heldBlockScratch[6] = tr.Height / atlasH;
                // identity - the matrix above already did the rotation
                _heldBlockScratch[7] = 0f;
                _heldBlockScratch[8] = 0f;
                _heldBlockScratch[9] = 0f;
                _heldBlockScratch[10] = 1f;
                _gd.UpdateBuffer(_heldBlockBuffer, 0, _heldBlockScratch);

                cl.SetPipeline(_heldBlockSpritePipeline);
                cl.SetGraphicsResourceSet(0, _handProjSet);   // camera-space projection
                if (_itemsTextureSet != null) cl.SetGraphicsResourceSet(1, _itemsTextureSet); // items atlas
                cl.SetVertexBuffer(0, _heldItemPrismVertexBuffer);
                cl.SetVertexBuffer(1, _heldBlockBuffer);
                cl.SetIndexBuffer(_heldItemPrismIndexBuffer, IndexFormat.UInt16);
                cl.DrawIndexed(36, 1, 0, 0, 0); // 6 faces of the extruded card, one pass
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
            // Fixed orientation: the same 3/4 view tilted toward the camera, shared with the
            // held-item path above via holdYaw/holdPitch.
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