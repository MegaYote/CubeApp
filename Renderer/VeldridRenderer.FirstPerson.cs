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
            if (_hud.HandSwingSerial != _lastHandSwingSerial)
            {
                // Timer state alone cannot distinguish a new click from a swing already in
                // progress. The event serial makes every fresh punch restart at its wind-up,
                // even when the previous animation has not returned to rest yet.
                _lastHandSwingSerial = _hud.HandSwingSerial;
                _handPunchTime = 0f;
            }
            if (_hud.MiningProgress > 0f || _hud.HandSwing > 0f)
            {
                // Same punch for mining AND air-punching: HandSwing just feeds the same
                // _handPunchTime cycle so clicking at nothing plays the identical swing.
                _handPunchTime += handDt;
                const float cycle = 0.45f;
                float t = _handPunchTime % cycle;
                if (t < 0.35f) s = t / 0.35f; // punch OUT, then snap back and repeat instantly
                else s = 1f;                  // brief hold at full extension (no rest gap)
            }
            else
            {
                _handPunchTime = 0f;
            }
            float pokeS = 0f;
            if (_hud.HandPoke > 0f)
            {
                float t = Math.Clamp(1f - _hud.HandPoke / 0.35f, 0f, 1f);
                pokeS = (float)Math.Sin(t * Math.PI);
            }
            s = Math.Min(1f, s + pokeS);

            // The punch aims the arm itself down the view-center ray. Rotating the current
            // arm direction toward that ray is important: a Z-only item nudge reads as a
            // sideways swipe, while rotating around the fist makes the shoulder sweep.
            // Keep the shoulder anchor fixed, rotate the whole solid arm from there, and let
            // the held item inherit that exact same shoulder rotation.
            float punch = (float)Math.Sin(Math.Sqrt(Math.Max(s, 0f)) * Math.PI);

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
            // The fist and shoulder are both measured from the idle pose. Shoulder stays
            // planted; the arm is aimed from that anchor toward the view-center ray.
            ComputeGripPoint();
            Vector3 grip = ArmToCamera(_gripX, _gripY, _gripZ, handScale,
                cp, sp, cy, syy, cr, sr,
                sx + handSway, sy + handBob, sz);
            Vector3 shoulder = new Vector3(sx + handSway, sy + handBob, sz);
            Vector3 targetOnViewRay = new Vector3(0f, 0f, shoulder.Z - 2.0f);
            Quaternion qAim = RotationBetween(grip - shoulder, targetOnViewRay - shoulder);
            Quaternion qArm = Quaternion.Slerp(Quaternion.Identity, qAim, punch);
            Vector3 punchLunge = new Vector3(0f, 0f, -0.08f * punch);

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
                    var v = new Vector3(x3 + sx + handSway, y3 + sy + handBob, z2 + sz);
                    v = shoulder + Vector3.Transform(v - shoulder, qArm) + punchLunge;
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

            Vector3 fistAfter = shoulder + Vector3.Transform(grip - shoulder, qArm) + punchLunge;
            DrawHeldItem(cl, fistAfter, qArm);
        }

        private static Quaternion RotationBetween(Vector3 from, Vector3 to)
        {
            if (from.LengthSquared() < 1e-8f || to.LengthSquared() < 1e-8f)
                return Quaternion.Identity;

            from = Vector3.Normalize(from);
            to = Vector3.Normalize(to);
            float dot = Math.Clamp(Vector3.Dot(from, to), -1f, 1f);
            if (dot > 0.99999f) return Quaternion.Identity;
            if (dot < -0.99999f)
            {
                Vector3 axis = Vector3.Cross(from, Vector3.UnitY);
                if (axis.LengthSquared() < 1e-8f) axis = Vector3.Cross(from, Vector3.UnitX);
                return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
            }

            Vector3 cross = Vector3.Cross(from, to);
            return Quaternion.Normalize(new Quaternion(cross, 1f + dot));
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
