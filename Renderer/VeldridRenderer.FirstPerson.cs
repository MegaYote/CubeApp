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

            // ---- The chop: Minecraft's exact first-person swing --------------------------
            // Vanilla never re-poses the arm for a swing. The whole arm+item assembly first
            // drifts with the swing bob, then rotates TOGETHER around the FIST by the attack
            // transform: pitch dips 80° deep, yaw flicks 20° off its 45° base, roll sweeps
            // 20°. The item is glued to the fist, so it inherits all of it (DrawHeldItem
            // takes the same rotation) and arm + object chop as one rigid piece.
            const float D2R = (float)(Math.PI / 180.0);
            float f4 = (float)Math.Sin(s * s * Math.PI);                      // quick yaw flick
            float f5 = (float)Math.Sin((float)Math.Sqrt(Math.Max(s, 0f)) * Math.PI); // chop envelope
            float bobX = -0.4f * f5;
            float bobY = 0.2f * (float)Math.Sin((float)Math.Sqrt(Math.Max(s, 0f)) * Math.PI * 2f);
            float bobZ = -0.2f * (float)Math.Sin(Math.Max(s, 0f) * Math.PI);
            // Attack chain in vanilla's mulPose order: yaw1 -> roll -> pitch -> yaw2 (right
            // hand). Quaternion.Multiply(A, B) here rotates by B first, so the innermost yaw1
            // is multiplied in last and the wrap-up yaw2 goes on outermost.
            Quaternion qFist = Quaternion.CreateFromYawPitchRoll((45f - f4 * 20f) * D2R, 0f, 0f);
            qFist = Quaternion.Multiply(Quaternion.CreateFromYawPitchRoll(0f, 0f, -20f * f5 * D2R), qFist);
            qFist = Quaternion.Multiply(Quaternion.CreateFromYawPitchRoll(0f, -80f * f5 * D2R, 0f), qFist);
            qFist = Quaternion.Multiply(Quaternion.CreateFromYawPitchRoll(-45f * D2R, 0f, 0f), qFist);

            // The pose is purely the IDLE pose now: the swing doesn't re-angle the shoulder -
            // the vanilla chop lives entirely in the fist rotation + bob above.
            float pitch = basePitch + idle;
            float yaw = baseYaw;
            float roll = 0f;

            float cp = (float)Math.Cos(pitch), sp = (float)Math.Sin(pitch);
            float cy = (float)Math.Cos(yaw), syy = (float)Math.Sin(yaw);
            float cr = (float)Math.Cos(roll), sr = (float)Math.Sin(roll);

            // The arm is the first-person model. The selected hotbar stack (block or item) is
            // drawn on top of it, in the fist - see DrawHeldItem.
            // The held item AND the swing's pivot both hang on the fist: derive the grip point
            // (the arm mesh's far end through the idle pose) first, so the verts can rotate
            // around it exactly like vanilla rotates around its hand anchor.
            ComputeGripPoint();
            Vector3 grip = ArmToCamera(_gripX, _gripY, _gripZ, handScale,
                cp, sp, cy, syy, cr, sr,
                sx + handSway, sy + handBob, sz);
            // Pivot near the shoulder so it stays planted and the fist punches.
            Vector3 shoulder = new Vector3(sx + handSway, sy + handBob, sz);
            Vector3 pivot = shoulder + (grip - shoulder) * 0.25f;

            float[] verts = new float[_handMesh.Length];
            {
                for (int i = 0; i < _handMesh.Length; i += 9)
                {
                    // Flip the arm so it points UP from the shoulder, then rotate pitch -> yaw -> roll
                    // around the shoulder for the idle pose, then place it.
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
                    // Swing bob first: the whole assembly (arm, fist, item) drifts together.
                    var v = new Vector3(x3 + sx + handSway + bobX, y3 + sy + handBob + bobY, z2 + sz + bobZ);
                    // The chop pivots at the fist - but rotating the arm RIGIDLY around it
                    // sweeps the far (shoulder) end in a wide arc, so the shoulder thrusts
                    // itself outward instead of the fist. Vanilla gets away with it because
                    // most of its arm is off-screen; ours is not. Blend the rotation out
                    // Rotate the whole arm rigidly around the fist (vanilla style). Per-vertex
                    // blending sheared the arm faces (looked like stretching). Rigid rotation
                    // Don't rotate the arm mesh at all - keep it in idle pose. Only the item
                    // rotates (full -80 chop around the fist). This matches MC where the arm
                    // is mostly off-screen and only the item is visible chopping. The shoulder
                    // stays planted, no stretching, no sweep.
                    // (v already has the idle pose + bob, no rotation applied)
                    verts[i] = v.X;
                    verts[i + 1] = v.Y;
                    verts[i + 2] = v.Z;
                    verts[i + 3] = _handMesh[i + 3];
                    verts[i + 4] = _handMesh[i + 4];
                    verts[i + 5] = _handMesh[i + 5];
                    verts[i + 6] = _handMesh[i + 6];
                    verts[i + 7] = _handMesh[i + 7];
                    verts[i + 8] = _handMesh[i + 8];
                }
                _gd.UpdateBuffer(_handVertexBuffer, 0, verts);
            }

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

            // The held item / block rides in the fist: the fist stays in its idle position
            // (arm doesn't rotate), and only the item rotates (full -80 chop around the fist).
            DrawHeldItem(cl, grip, qFist);
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
