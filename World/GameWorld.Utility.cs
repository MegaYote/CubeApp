using System;
using System.Collections.Generic;
using System.Numerics;

namespace CubeApp
{
    public sealed partial class GameWorld : IDisposable
    {
        private Point3D? FindSafeSpawnPosition()
        {
            int baseX = (int)Math.Floor(LocalPlayer.Position.X);
            int baseZ = (int)Math.Floor(LocalPlayer.Position.Z);
            const int seaLevelWorldY = 0;
            // Search out to ~300 blocks so land is reachable even when the origin sits in a wide
            // ocean basin.
            for (int radius = 0; radius <= 300; radius++)
            {
                int bestY = int.MinValue, bestX = 0, bestZ = 0;
                for (int dx = -radius; dx <= radius; dx++)
                for (int dz = -radius; dz <= radius; dz++)
                {
                    if (radius > 0 && Math.Abs(dx) != radius && Math.Abs(dz) != radius) continue;
                    int wx = baseX + dx;
                    int wz = baseZ + dz;
                    Chunks.GetOrCreateChunk(WorldToChunkCoord(wx), WorldToChunkCoord(wz));
                    int surfaceY = FindSurfaceWorldY(wx, wz);
                    if (surfaceY < seaLevelWorldY) continue;
                    if (surfaceY > bestY)
                    {
                        bestY = surfaceY;
                        bestX = wx;
                        bestZ = wz;
                    }
                }
                if (bestY == int.MinValue) continue;

                double px = bestX + 0.5;
                double pz = bestZ + 0.5;
                double minEyeY = bestY + EyeHeight + 0.01;
                double maxEyeY = ChunkManager.ChunkHeight + 1.0;
                for (double eyeY = minEyeY; eyeY <= maxEyeY; eyeY += 0.25)
                {
                    var candidate = new Point3D(px, eyeY, pz);
                    if (!IsPlayerColliding(candidate)) return candidate;
                }
            }
            return null;
        }

        private int FindSurfaceWorldY(int wx, int wz)
        {
            for (int wy = ChunkManager.WorldOriginY + ChunkManager.ChunkHeight - 1; wy >= ChunkManager.WorldOriginY; wy--)
            {
                if (Chunks.TryGetLoadedBlock(wx, wy, wz, out var block) && BlockRegistry.IsSolid(block))
                {
                    return wy;
                }
            }
            return ChunkManager.WorldOriginY - 1;
        }

        // Lazy stratosphere fill (mirror of the deep-fill idea): while the player is very high up,
        // fill the upper zone (world ~512..1000) of nearby chunks with sky islands. New chunks
        // generated while high are born with their islands (AutoHighFill); already-loaded chunks
        // are filled once here. This keeps surface chunk gen cheap - the stratosphere is empty
        // air until the player climbs toward it.
        private void UpdateHighFill()
        {
            if (Chunks == null || SkyChunkProvider == null) return;
            // Start filling when the player is above the cloud deck by a good margin.
            const double highThreshold = 450.0;
            bool isHigh = LocalPlayer.Position.Y > highThreshold;
            SkyChunkProvider.Islands.AutoHighFill = isHigh;
            if (!isHigh) return;

            int cx = (int)Math.Floor(LocalPlayer.Position.X / ChunkManager.ChunkSize);
            int cz = (int)Math.Floor(LocalPlayer.Position.Z / ChunkManager.ChunkSize);

            foreach (var ch in Chunks.GetLoadedChunks())
            {
                if (ch.OriginY != ChunkManager.SkyOriginY) continue; // only sky-layer chunks
                int dx = ch.OriginX / ChunkManager.ChunkSize - cx;
                int dz = ch.OriginZ / ChunkManager.ChunkSize - cz;
                if (dx * dx + dz * dz > 64) continue; // within ~8 chunks of the player
                int layer = ChunkManager.SkyLayer;
                int chunkX = ch.OriginX / ChunkManager.ChunkSize;
                int chunkZ = ch.OriginZ / ChunkManager.ChunkSize;
                SkyChunkProvider.Islands.HighFillChunk(chunkX, chunkZ, ch, 16, ChunkManager.SkyHeight);
                if (ch.NeedsRemesh)
                {
                    ch.IsMeshingQueued = false;
                    Mesher?.RequestImmediateRemesh(new ChunkCoordinates(layer, chunkX, chunkZ));
                }
            }
        }

        // ------------------------------------------------------------------
        // helpers (camera math used by both sim and render)
        // ------------------------------------------------------------------

        public Point3D GetCameraForward() => GetCameraForward(LocalPlayer);

        public Point3D GetCameraForward(PlayerState p)
        {
            var yawRad = p.Yaw * Math.PI / 180.0;
            var pitchRad = p.Pitch * Math.PI / 180.0;
            var cosPitch = Math.Cos(pitchRad);
            return new Point3D(cosPitch * Math.Sin(yawRad), Math.Sin(pitchRad), cosPitch * Math.Cos(yawRad)).Normalized();
        }

        public static Point3D GetCameraRight(float yaw)
        {
            var yawRad = yaw * Math.PI / 180.0;
            return new Point3D(Math.Cos(yawRad), 0, -Math.Sin(yawRad)).Normalized();
        }

        public static int WorldToChunkCoord(double value) => (int)Math.Floor(value / ChunkManager.ChunkSize);

        public static float NormalizeYaw(float yaw)
        {
            float result = yaw % 360f;
            if (result < 0f) result += 360f;
            return result;
        }

        /// <summary>Normalizes an angle in radians to the [-PI, PI] range.</summary>
        private static float NormalizeRadians(float a)
        {
            const float twoPi = (float)(Math.PI * 2.0);
            while (a > (float)Math.PI) a -= twoPi;
            while (a < -(float)Math.PI) a += twoPi;
            return a;
        }

        private static bool RayBoxHit(Point3D o, Point3D d,
            double bMinX, double bMinY, double bMinZ, double bMaxX, double bMaxY, double bMaxZ,
            double tMinLimit, double tMaxLimit, out double t, out Point3D normal)
        {
            t = 0; normal = Point3D.Zero;
            double tMin = tMinLimit, tMax = tMaxLimit;
            int axis = -1;
            double ox = o.X, oy = o.Y, oz = o.Z, dx = d.X, dy = d.Y, dz = d.Z;
            double[] bmin = { bMinX, bMinY, bMinZ };
            double[] bmax = { bMaxX, bMaxY, bMaxZ };
            double[] oa = { ox, oy, oz };
            double[] da = { dx, dy, dz };
            for (int a = 0; a < 3; a++)
            {
                if (Math.Abs(da[a]) < 1e-12)
                {
                    if (oa[a] < bmin[a] || oa[a] > bmax[a]) return false;
                }
                else
                {
                    double t1 = (bmin[a] - oa[a]) / da[a];
                    double t2 = (bmax[a] - oa[a]) / da[a];
                    if (t1 > t2) { (t1, t2) = (t2, t1); }
                    if (t1 > tMin) { tMin = t1; axis = a; }
                    if (t2 < tMax) tMax = t2;
                    if (tMin > tMax) return false;
                }
            }
            t = tMin;
            normal = axis switch
            {
                0 => new Point3D(-Math.Sign(dx), 0, 0),
                1 => new Point3D(0, -Math.Sign(dy), 0),
                _ => new Point3D(0, 0, -Math.Sign(dz)),
            };
            return true;
        }

        private static (double minX, double minY, double minZ, double maxX, double maxY, double maxZ) ComputeFaceRect(
            int cx, int cy, int cz, (double minX, double minY, double minZ, double maxX, double maxY, double maxZ) b, Point3D n)
        {
            if (n.X > 0.5) return (cx + b.maxX, cy + b.minY, cz + b.minZ, cx + b.maxX, cy + b.maxY, cz + b.maxZ);
            if (n.X < -0.5) return (cx + b.minX, cy + b.minY, cz + b.minZ, cx + b.minX, cy + b.maxY, cz + b.maxZ);
            if (n.Y > 0.5) return (cx + b.minX, cy + b.maxY, cz + b.minZ, cx + b.maxX, cy + b.maxY, cz + b.maxZ);
            if (n.Y < -0.5) return (cx + b.minX, cy + b.minY, cz + b.minZ, cx + b.maxX, cy + b.minY, cz + b.maxZ);
            if (n.Z > 0.5) return (cx + b.minX, cy + b.minY, cz + b.maxZ, cx + b.maxX, cy + b.maxY, cz + b.maxZ);
            return (cx + b.minX, cy + b.minY, cz + b.minZ, cx + b.maxX, cy + b.maxY, cz + b.minZ);
        }

    }
}