using System;
using System.Collections.Generic;
using System.Numerics;

namespace Cubuild
{
    public sealed partial class GameWorld : IDisposable
    {
        public void AdvanceTime()
        {
            WorldTime += 6000;
            _worldTimeAccumulator = 0.0;
        }

        /// <summary>Fractional leftover for the 20 tps day/night clock.</summary>
        private double _worldTimeAccumulator;

        /// <summary>
        /// Sun position 0..1 across the day (0.25 = dawn, 0.75 = dusk). 0..1 where 0 = midnight-ish
        /// start of the cycle; eased so the sun lingers near the horizon rather than snapping.
        /// </summary>
        public float SunPosition(float partialTick)
        {
            long t = WorldTime % 24000;
            float ang = (float)(t + partialTick) / 24000.0f - 0.25f;
            if (ang < 0f) ang += 1f;
            if (ang > 1f) ang -= 1f;
            float raw = ang;
            ang = 1f - (float)((Math.Cos(ang * Math.PI) + 1.0) / 2.0);
            ang = raw + (ang - raw) / 3f;
            return ang;
        }

        /// <summary>
        /// Night dim level: 0 (noon) .. 11 (midnight) â€” how much daylight is removed from the sky
        /// light after the sun goes down.
        /// </summary>
        public int NightDimLevel(float partialTick)
        {
            float celestial = SunPosition(partialTick);
            float v = 1f - (float)(Math.Cos(celestial * Math.PI * 2.0) * 2.0 + 0.5);
            if (v < 0f) v = 0f;
            if (v > 1f) v = 1f;
            return (int)(v * 11f);
        }

        /// <summary>Sky brightness factor (1.0 at noon, near 0 at midnight).</summary>
        public float SkyBrightness(float partialTick)
        {
            float celestial = SunPosition(partialTick);
            float v = (float)(Math.Cos(celestial * Math.PI * 2.0) * 2.0 + 0.5);
            if (v < 0f) v = 0f;
            if (v > 1f) v = 1f;
            return v;
        }

        /// <summary>Simulates remote players without touching the local player's camera/chunks.
        /// The host calls this each frame with each client's received input.</summary>
        public void StepRemotePlayers(float deltaSeconds)
        {
            PlayerState[] states;
            lock (_remoteLock)
            {
                states = new PlayerState[_remotePlayers.Count];
                int i = 0;
                foreach (var s in _remotePlayers.Values) states[i++] = s;
            }
            foreach (var s in states)
            {
                // Remote players use the same physics; their input is applied by the network
                // layer via ApplyRemoteInput (latest received TickInputState).
                StepPlayer(s, s.PendingInput, deltaSeconds);
            }
        }

        // ------------------------------------------------------------------
        // player movement (generic on PlayerState; local + remote share this)
        // ------------------------------------------------------------------

        public void StepPlayer(PlayerState p, TickInputState tickInput, float deltaSeconds)
        {
            // A dead player is a corpse: no walking, flying, jumping, or input-driven motion.
            if (p.Health <= 0)
            {
                p.Velocity = new Point3D(0, 0, 0);
                p.WalkAmount = 0f;
                p.Grounded = false;
                return;
            }

            if (p.FlyMode)
            {
                var flyForward = GetCameraForward(p);
                var flyRight = GetCameraRight(p.Yaw);
                var flyDir = new Point3D(0, 0, 0);
                if (tickInput.MoveForward) flyDir += flyForward;
                if (tickInput.MoveBackward) flyDir -= flyForward;
                if (tickInput.MoveLeft) flyDir += flyRight;
                if (tickInput.MoveRight) flyDir -= flyRight;
                if (tickInput.MoveUp) flyDir += new Point3D(0, 1, 0);
                if (tickInput.MoveDown) flyDir += new Point3D(0, -1, 0);
                if (flyDir.X != 0 || flyDir.Y != 0 || flyDir.Z != 0)
                {
                    double len = Math.Sqrt(flyDir.X * flyDir.X + flyDir.Y * flyDir.Y + flyDir.Z * flyDir.Z);
                    flyDir *= 1.0 / len;
                }
                p.Velocity = flyDir * FlySpeed;
                p.Position = new Point3D(
                    p.Position.X + p.Velocity.X * deltaSeconds,
                    p.Position.Y + p.Velocity.Y * deltaSeconds,
                    p.Position.Z + p.Velocity.Z * deltaSeconds);
                p.Grounded = false;
                p.WalkAmount = 0f;
                return;
            }

            var forwardWalk = GetCameraForward(p);
            var forwardHorizontal = new Point3D(forwardWalk.X, 0, forwardWalk.Z).Normalized();
            var right = GetCameraRight(p.Yaw);
            var desiredDirection = new Point3D(0, 0, 0);
            if (tickInput.MoveForward) desiredDirection += forwardHorizontal;
            if (tickInput.MoveBackward) desiredDirection -= forwardHorizontal;
            if (tickInput.MoveLeft) desiredDirection += right;
            if (tickInput.MoveRight) desiredDirection -= right;
            if (desiredDirection.X != 0 || desiredDirection.Z != 0)
            {
                var length = Math.Sqrt(desiredDirection.X * desiredDirection.X + desiredDirection.Z * desiredDirection.Z);
                desiredDirection *= 1.0 / length;
            }

            bool feetInWater = PlayerSampleInWater(p, 0.05);
            bool bodyInWater = PlayerSampleInWater(p, PlayerHeight * 0.4);
            bool headInWater = PlayerSampleInWater(p, PlayerHeight * 0.85);
            bool inWater = feetInWater || bodyInWater || headInWater;
            if (inWater)
            {
                double submerged = (feetInWater ? 0.25 : 0) + (bodyInWater ? 0.5 : 0) + (headInWater ? 0.25 : 0);
                var swimSpeed = desiredDirection * (WalkSpeed * 0.42);
                p.Velocity = new Point3D(swimSpeed.X, p.Velocity.Y, swimSpeed.Z);
                p.Velocity = new Point3D(
                    p.Velocity.X,
                    p.Velocity.Y * Math.Pow(0.96, deltaSeconds * 60.0),
                    p.Velocity.Z);
                double waterGravity = Gravity * Math.Max(0.16, 0.42 - submerged * 0.20);
                p.Velocity = new Point3D(
                    p.Velocity.X,
                    p.Velocity.Y - waterGravity * deltaSeconds,
                    p.Velocity.Z);
                if (tickInput.MoveUp)
                {
                    double swimLift = bodyInWater ? 0.58 : 0.7;
                    p.Velocity = new Point3D(
                        p.Velocity.X,
                        Math.Max(p.Velocity.Y, JumpVelocity * swimLift),
                        p.Velocity.Z);
                }
                var swimDisplacement = p.Velocity * deltaSeconds;
                MovePlayerWithCollisions(p, swimDisplacement);
                double swimHSpeed = Math.Sqrt(p.Velocity.X * p.Velocity.X + p.Velocity.Z * p.Velocity.Z);
                p.WalkAmount = (float)Math.Min(1.0, swimHSpeed / WalkSpeed);
                p.WalkPhase += deltaSeconds * p.WalkAmount * 10f;
                return;
            }

            // Natural horizontal movement: accelerate toward the desired direction/speed instead
            // of snapping, and coast to a stop with friction when nothing is held. Walking
            // backward is a touch slower, which reads as more human.
            var horizontalVelocity = new Point3D(p.Velocity.X, 0, p.Velocity.Z);
            float speed = WalkSpeed;
            if (tickInput.MoveBackward) speed *= 0.72f;
            var targetH = desiredDirection * speed;
            bool moving = tickInput.MoveForward || tickInput.MoveBackward || tickInput.MoveLeft || tickInput.MoveRight;
            double accel = moving ? GroundAcceleration : GroundFriction;
            double dx = targetH.X - horizontalVelocity.X;
            double dz = targetH.Z - horizontalVelocity.Z;
            double dist = Math.Sqrt(dx * dx + dz * dz);
            double maxStep = accel * deltaSeconds;
            if (dist <= maxStep)
            {
                horizontalVelocity = new Point3D(targetH.X, 0, targetH.Z);
            }
            else
            {
                double f = maxStep / dist;
                horizontalVelocity = new Point3D(horizontalVelocity.X + dx * f, 0, horizontalVelocity.Z + dz * f);
            }

            var verticalVelocity = p.Velocity.Y;
            if (tickInput.JumpPressed && p.Grounded)
            {
                verticalVelocity = JumpVelocity;
                p.Grounded = false;
            }
            verticalVelocity -= Gravity * deltaSeconds;
            if (verticalVelocity < -MaxFallSpeed) verticalVelocity = -MaxFallSpeed;
            p.Velocity = new Point3D(horizontalVelocity.X, verticalVelocity, horizontalVelocity.Z);
            var frameDisplacement = p.Velocity * deltaSeconds;
            MovePlayerWithCollisions(p, frameDisplacement);

            double hSpeed = Math.Sqrt(p.Velocity.X * p.Velocity.X + p.Velocity.Z * p.Velocity.Z);
            p.WalkAmount = (float)Math.Min(1.0, hSpeed / WalkSpeed);
            p.WalkPhase += deltaSeconds * p.WalkAmount * 10f;
        }

        private void MovePlayerWithCollisions(PlayerState p, Point3D displacement)
        {
            bool hitX = false, hitY = false, hitZ = false;
            var start = p.Position;
            p.Position = MoveAlongAxis(p.Position, displacement.X, Axis.X, ref hitX);
            p.Position = MoveAlongAxis(p.Position, displacement.Y, Axis.Y, ref hitY);
            p.Position = MoveAlongAxis(p.Position, displacement.Z, Axis.Z, ref hitZ);
            if (hitX || hitZ)
            {
                var stepped = TryStepUp(p, start, displacement);
                if (stepped.HasValue)
                {
                    p.Position = stepped.Value;
                    hitX = hitZ = false;
                    hitY = true;
                    p.Grounded = true;
                }
            }
            if (hitX) p.Velocity = new Point3D(0, p.Velocity.Y, p.Velocity.Z);
            if (hitZ) p.Velocity = new Point3D(p.Velocity.X, p.Velocity.Y, 0);
            if (hitY)
            {
                if (p.Velocity.Y <= 0)
                {
                    p.Grounded = true;
                    // Survival fall damage: impact speed above the threshold hurts (creative is
                    // immune via DamagePlayer). One heart per ~2.5 speed over the threshold.
                    double impactSpeed = -p.Velocity.Y;
                    if (p == LocalPlayer && impactSpeed > FallDamageThreshold)
                    {
                        int damage = Math.Max(1, (int)Math.Round((impactSpeed - FallDamageThreshold) / FallDamageScale));
                        DamagePlayer(damage, DeathCause.Fall);
                    }
                }
                p.Velocity = new Point3D(p.Velocity.X, 0, p.Velocity.Z);
            }
            else p.Grounded = false;
        }

        private bool PlayerSampleInWater(PlayerState p, double heightOffset)
        {
            int id = BlockRegistry.GetId("water");
            int x = (int)Math.Floor(p.Position.X);
            int y = (int)Math.Floor(p.Position.Y - EyeHeight + heightOffset);
            int z = (int)Math.Floor(p.Position.Z);
            return Chunks.TryGetLoadedBlock(x, y, z, out var block) && block == id;
        }

        private Point3D? TryStepUp(PlayerState p, Point3D start, Point3D displacement)
        {
            const double maxStepHeight = 0.5;
            var raised = new Point3D(start.X, start.Y + maxStepHeight, start.Z);
            if (IsPlayerColliding(raised)) return null;
            bool hx = false, hz = false;
            var moved = MoveAlongAxis(raised, displacement.X, Axis.X, ref hx);
            moved = MoveAlongAxis(moved, displacement.Z, Axis.Z, ref hz);
            if (hx || hz) return null;
            var down = moved;
            while (down.Y > start.Y)
            {
                var candidate = new Point3D(down.X, down.Y - CollisionStep, down.Z);
                if (IsPlayerColliding(candidate)) break;
                down = candidate;
            }
            return down;
        }

        private Point3D MoveAlongAxis(Point3D start, double amount, Axis axis, ref bool collided)
        {
            if (amount == 0.0) return start;
            int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(amount) / CollisionStep));
            double step = amount / steps;
            var current = start;
            for (int i = 0; i < steps; i++)
            {
                var next = axis switch
                {
                    Axis.X => new Point3D(current.X + step, current.Y, current.Z),
                    Axis.Y => new Point3D(current.X, current.Y + step, current.Z),
                    Axis.Z => new Point3D(current.X, current.Y, current.Z + step),
                    _ => current,
                };
                if (IsPlayerColliding(next))
                {
                    collided = true;
                    return current;
                }
                current = next;
            }
            return current;
        }

        public bool IsPlayerColliding(Point3D eyePosition)
        {
            double minX = eyePosition.X - PlayerRadius;
            double maxX = eyePosition.X + PlayerRadius;
            double minY = eyePosition.Y - EyeHeight;
            double maxY = minY + PlayerHeight;
            double minZ = eyePosition.Z - PlayerRadius;
            double maxZ = eyePosition.Z + PlayerRadius;
            int blockMinX = (int)Math.Floor(minX);
            int blockMaxX = (int)Math.Floor(maxX);
            int blockMinY = (int)Math.Floor(minY);
            int blockMaxY = (int)Math.Floor(maxY - 1e-5);
            int blockMinZ = (int)Math.Floor(minZ);
            int blockMaxZ = (int)Math.Floor(maxZ);
            for (int x = blockMinX; x <= blockMaxX; x++)
            for (int y = blockMinY; y <= blockMaxY; y++)
            for (int z = blockMinZ; z <= blockMaxZ; z++)
            {
                if (Chunks.TryGetLoadedBlockAndMeta(x, y, z, out var block, out var meta) && BlockRegistry.IsSolid(block))
                {
                    if (BoxesOverlapPlayer(GetBlockCollisionBoxes(block, meta), x, y, z, minX, maxX, minY, maxY, minZ, maxZ))
                        return true;
                }
            }
            return false;
        }

        public static (double minX, double minY, double minZ, double maxX, double maxY, double maxZ)[] GetBlockCollisionBoxes(int id, int meta)
        {
            if (BlockRegistry.IsSlab(id)) return new[] { (0.0, 0.0, 0.0, 1.0, 0.5, 1.0) };
            if (BlockRegistry.IsSlabTop(id)) return new[] { (0.0, 0.5, 0.0, 1.0, 1.0, 1.0) };
            if (BlockRegistry.IsStair(id))
            {
                return meta switch
                {
                    0 => new[] { (0.0, 0.0, 0.0, 0.5, 0.5, 1.0), (0.5, 0.0, 0.0, 1.0, 1.0, 1.0) },
                    1 => new[] { (0.0, 0.0, 0.0, 0.5, 1.0, 1.0), (0.5, 0.0, 0.0, 1.0, 0.5, 1.0) },
                    2 => new[] { (0.0, 0.0, 0.0, 1.0, 0.5, 0.5), (0.0, 0.0, 0.5, 1.0, 1.0, 1.0) },
                    _ => new[] { (0.0, 0.0, 0.0, 1.0, 1.0, 0.5), (0.0, 0.0, 0.5, 1.0, 0.5, 1.0) },
                };
            }
            if (BlockRegistry.IsCross(id))
            {
                // Wall torches (meta 1-4): raised like the render (0.2) and hugging the wall
                // plane they're attached to instead of centered in the cell.
                if (id == _idTorch && meta >= 1 && meta <= 4)
                {
                    return meta switch
                    {
                        1 => new[] { (0.0, 0.2, 0.3, 0.5, 0.9, 0.7) },  // lean +X, wall at -X
                        2 => new[] { (0.5, 0.2, 0.3, 1.0, 0.9, 0.7) },  // lean -X, wall at +X
                        3 => new[] { (0.3, 0.2, 0.0, 0.7, 0.9, 0.5) },  // lean +Z, wall at -Z
                        _ => new[] { (0.3, 0.2, 0.5, 0.7, 0.9, 1.0) },  // lean -Z, wall at +Z
                    };
                }
                return new[] { (0.25, 0.0, 0.25, 0.75, 0.8, 0.75) };
            }
            return new[] { (0.0, 0.0, 0.0, 1.0, 1.0, 1.0) };
        }

        private static bool BoxesOverlapPlayer((double minX, double minY, double minZ, double maxX, double maxY, double maxZ)[] boxes,
            int bx, int by, int bz, double pMinX, double pMaxX, double pMinY, double pMaxY, double pMinZ, double pMaxZ)
        {
            foreach (var b in boxes)
            {
                if (bx + b.maxX > pMinX && bx + b.minX < pMaxX
                    && by + b.maxY > pMinY && by + b.minY < pMaxY
                    && bz + b.maxZ > pMinZ && bz + b.minZ < pMaxZ)
                {
                    return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------------
        // block editing (single source of truth; raises BlockEdited)
        // ------------------------------------------------------------------

        /// <summary>Breaks the block the player is looking at. Returns true if a block was removed,
        /// with the block's world position and previous id (for particle spawning).</summary>
    }
}