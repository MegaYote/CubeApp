using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using CubeApp.Renderer;
using CubeApp.World;
using Veldrid;
using Veldrid.Sdl2;
using Veldrid.StartupUtilities;
using static CubeApp.ChunkManager;
using CubeApp;

namespace CubeApp
{
    public sealed partial class Program : IDisposable
    {
        private Vector3[]? ComputeHighlightWorldQuad(GameWorld.PickBlockResult hit)
        {
            var f = hit.Face;
            var n = hit.Normal;
            Point3D[] faceCorners = new Point3D[4];
            if (Math.Abs(n.X) > 0.5)
            {
                double xplane = f.minX;
                faceCorners[0] = new Point3D(xplane, f.minY, f.minZ);
                faceCorners[1] = new Point3D(xplane, f.minY, f.maxZ);
                faceCorners[2] = new Point3D(xplane, f.maxY, f.maxZ);
                faceCorners[3] = new Point3D(xplane, f.maxY, f.minZ);
            }
            else if (Math.Abs(n.Y) > 0.5)
            {
                double yplane = f.minY;
                faceCorners[0] = new Point3D(f.minX, yplane, f.minZ);
                faceCorners[1] = new Point3D(f.maxX, yplane, f.minZ);
                faceCorners[2] = new Point3D(f.maxX, yplane, f.maxZ);
                faceCorners[3] = new Point3D(f.minX, yplane, f.maxZ);
            }
            else
            {
                double zplane = f.minZ;
                faceCorners[0] = new Point3D(f.minX, f.minY, zplane);
                faceCorners[1] = new Point3D(f.maxX, f.minY, zplane);
                faceCorners[2] = new Point3D(f.maxX, f.maxY, zplane);
                faceCorners[3] = new Point3D(f.minX, f.maxY, zplane);
            }
            faceCorners = CanonicalizeFaceCornersByAxes(faceCorners, n);
            const double faceEpsilon = 0.002;
            var offset = n * faceEpsilon;
            var result = new Vector3[4];
            for (int i = 0; i < 4; i++)
            {
                var pos = faceCorners[i] + offset;
                result[i] = new Vector3((float)pos.X, (float)pos.Y, (float)pos.Z);
            }
            return result;
        }

        private static Point3D[] CanonicalizeFaceCornersByAxes(Point3D[] corners, Point3D normal)
        {
            if (corners.Length != 4) return corners;
            if (!TryGetHighlightFaceAxes(normal, out var uAxis, out var vAxis)) return corners;
            Span<(double U, double V)> uv = stackalloc (double U, double V)[4];
            double minU = double.PositiveInfinity, maxU = double.NegativeInfinity;
            double minV = double.PositiveInfinity, maxV = double.NegativeInfinity;
            for (int i = 0; i < 4; i++)
            {
                var c = corners[i];
                var u = Dot(c, uAxis);
                var v = Dot(c, vAxis);
                uv[i] = (u, v);
                if (u < minU) minU = u;
                if (u > maxU) maxU = u;
                if (v < minV) minV = v;
                if (v > maxV) maxV = v;
            }
            bool[] used = new bool[4];
            var result = new Point3D[4];
            result[0] = PickClosestCornerByUv(corners, uv, minU, minV, used);
            result[1] = PickClosestCornerByUv(corners, uv, maxU, minV, used);
            result[2] = PickClosestCornerByUv(corners, uv, maxU, maxV, used);
            result[3] = PickClosestCornerByUv(corners, uv, minU, maxV, used);
            return result;
        }

        private static Point3D PickClosestCornerByUv(Point3D[] corners, Span<(double U, double V)> uv, double targetU, double targetV, bool[] used)
        {
            int bestIndex = -1;
            double bestDistSq = double.PositiveInfinity;
            for (int i = 0; i < corners.Length; i++)
            {
                if (used[i]) continue;
                var du = uv[i].U - targetU;
                var dv = uv[i].V - targetV;
                var distSq = du * du + dv * dv;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestIndex = i;
                }
            }
            if (bestIndex < 0) return corners[0];
            used[bestIndex] = true;
            return corners[bestIndex];
        }

        private static bool TryGetHighlightFaceAxes(Point3D normal, out Point3D uAxis, out Point3D vAxis)
        {
            if (normal.X > 0.5) { uAxis = new Point3D(0, 0, -1); vAxis = new Point3D(0, 1, 0); return true; }
            if (normal.X < -0.5) { uAxis = new Point3D(0, 0, 1); vAxis = new Point3D(0, 1, 0); return true; }
            if (normal.Y > 0.5) { uAxis = new Point3D(1, 0, 0); vAxis = new Point3D(0, 0, 1); return true; }
            if (normal.Y < -0.5) { uAxis = new Point3D(1, 0, 0); vAxis = new Point3D(0, 0, -1); return true; }
            if (normal.Z > 0.5) { uAxis = new Point3D(1, 0, 0); vAxis = new Point3D(0, 1, 0); return true; }
            if (normal.Z < -0.5) { uAxis = new Point3D(-1, 0, 0); vAxis = new Point3D(0, 1, 0); return true; }
            uAxis = new Point3D(0, 0, 0);
            vAxis = new Point3D(0, 0, 0);
            return false;
        }

        private static string GetCompassDirection(float yaw)
        {
            float normalized = GameWorld.NormalizeYaw(yaw);
            if (normalized >= 315f || normalized < 45f) return "South (+Z)";
            if (normalized < 135f) return "East (+X)";
            if (normalized < 225f) return "North (-Z)";
            return "West (-X)";
        }

        private void InitializeGpuRenderer(GraphicsDevice gd, Swapchain sc)
        {
            try
            {
                gpuRenderer = new VeldridRenderer();
                gpuRenderer.Initialize(gd, sc);
                gpuRenderer.SetRenderDistance(ChunkRenderRadius);
                if (World != null) gpuRenderer.SetChunkManager(World.Chunks);
                if (window != null) gpuRenderer.Resize(window.Width, window.Height);
                if (World != null)
                {
                    var loaded = World.Chunks.GetLoadedChunks();
                    foreach (var ch in loaded)
                    {
                        if (ch.MeshFaces != null && ch.MeshFaces.Count > 0)
                        {
                            int chunkX = ch.OriginX / ChunkManager.ChunkSize;
                            int chunkZ = ch.OriginZ / ChunkManager.ChunkSize;
                            int layer = ChunkManager.LayerForWorldY(ch.OriginY);
                            gpuRenderer.UploadChunk(new ChunkCoordinates(layer, chunkX, chunkZ), ch.MeshFaces);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                try { System.IO.File.AppendAllText("gpu_init.log", DateTime.Now + " GPU init failed: " + ex + Environment.NewLine); } catch { }
                gpuRenderer?.Dispose();
                gpuRenderer = null;
                window?.Close();
            }
        }

        /// <summary>
        /// Snapshot of the local player for the third-person model: feet position, body yaw in
        /// radians and the walk-cycle state tracked in GameWorld.
        /// </summary>
        private MobRenderData BuildLocalPlayerRenderData()
        {
            var w = World;
            var feet = new Point3D(w.PlayerPosition.X, w.PlayerPosition.Y - GameWorld.EyeHeight, w.PlayerPosition.Z);
            float yawRad = w.PlayerYaw * (float)Math.PI / 180f;
            // Body faces the lagging BodyYaw; the head swivels the difference (clamped so it
            // doesn't rotate through the neck) - the classic MC third-person head/body split.
            float bodyYaw = w.LocalPlayer.BodyYaw;
            float headLocal = yawRad - bodyYaw;
            while (headLocal > (float)Math.PI) headLocal -= 2f * (float)Math.PI;
            while (headLocal < -(float)Math.PI) headLocal += 2f * (float)Math.PI;
            headLocal = Math.Clamp(headLocal, -1.22f, 1.22f); // ~70 deg max swivel
            // Head pitch follows the camera look (clamped so the neck doesn't bend absurdly).
            float headPitch = Math.Clamp(w.LocalPlayer.Pitch * (float)Math.PI / 180f, -1.05f, 1.05f);
            bool dead = w.LocalPlayer.Health <= 0;
            return new MobRenderData(
                "player", feet, bodyYaw, headLocal,
                w.PlayerWalkPhase, dead ? 0f : w.PlayerWalkAmount, 0f, 0f, 0f,
                (float)w.PlayerVelocity.Y, w.PlayerGrounded,
                dead, dead ? Math.Clamp(w.LocalPlayer.DeathTimer / 0.5f, 0f, 1f) : 0f,
                w.LocalPlayer.DeathRollDir, dead ? 0f : w.LocalPlayer.HurtTimer,
                headPitch);
        }

        /// <summary>
        /// Third-person camera: pull back along the view ray up to 4 blocks, stopping short of the
        /// first solid block so the camera never clips into terrain.
        /// </summary>
        private Point3D GetThirdPersonCameraPosition()
        {
            var w = World;
            var forward = w.GetCameraForward();
            const double maxDist = 4.0;
            const double step = 0.1;
            double dist = 0.0;
            while (dist < maxDist)
            {
                double next = Math.Min(maxDist, dist + step);
                var p = w.PlayerPosition - forward * next;
                int bx = (int)Math.Floor(p.X);
                int by = (int)Math.Floor(p.Y);
                int bz = (int)Math.Floor(p.Z);
                if (w.Chunks.TryGetLoadedBlock(bx, by, bz, out var block) && BlockRegistry.IsSolid(block))
                {
                    break;
                }
                dist = next;
            }
            dist = Math.Max(0.0, dist - 0.2);
            return w.PlayerPosition - forward * dist;
        }

        private bool thirdPersonView;

    }
}