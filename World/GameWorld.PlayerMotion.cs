using System;
using System.Collections.Generic;
using System.Numerics;

namespace Cubuild
{
    public sealed partial class GameWorld : IDisposable
    {
        public void AdvanceTime()
        {
            // T advances 1/64 of a 24000-tick daylight cycle (one eighth of the previous step).
            WorldTime += 24000 / 64;
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
                s.NoclipMode = IsCreative && s.PendingInput.NoclipMode;
                s.CreativeFlightMode = IsCreative && !s.NoclipMode && s.PendingInput.CreativeFlightMode;
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

            if (!IsCreative)
            {
                p.NoclipMode = false;
                p.CreativeFlightMode = false;
            }

            if (p.NoclipMode)
            {
                // Forward/back are HORIZONTAL: flatten the camera vector onto the XZ plane, exactly
                // as the walk path below does, so looking down while flying ahead still moves you
                // AHEAD instead of diving with the pitch. Altitude comes only from Space/Ctrl
                // (Space/Left Shift), which is why straight-and-level flight is possible at all.
                // Left/right were already horizontal (yaw only), so they needed no change.
                var noclipForward = GetCameraForward(p);
                var noclipHorizontal = new Point3D(noclipForward.X, 0, noclipForward.Z).Normalized();
                var noclipRight = GetCameraRight(p.Yaw);
                var noclipDirection = new Point3D(0, 0, 0);
                if (tickInput.MoveForward) noclipDirection += noclipHorizontal;
                if (tickInput.MoveBackward) noclipDirection -= noclipHorizontal;
                if (tickInput.MoveLeft) noclipDirection += noclipRight;
                if (tickInput.MoveRight) noclipDirection -= noclipRight;
                if (tickInput.MoveUp) noclipDirection += new Point3D(0, 1, 0);
                if (tickInput.MoveDown) noclipDirection += new Point3D(0, -1, 0);
                if (noclipDirection.X != 0 || noclipDirection.Y != 0 || noclipDirection.Z != 0)
                {
                    double len = Math.Sqrt(noclipDirection.X * noclipDirection.X + noclipDirection.Y * noclipDirection.Y + noclipDirection.Z * noclipDirection.Z);
                    noclipDirection *= 1.0 / len;
                }
                double noclipSpeed = NoclipSpeed * (tickInput.MoveFast ? 10.0 : 1.0);
                p.Velocity = new Point3D(
                    noclipDirection.X * noclipSpeed,
                    noclipDirection.Y * noclipSpeed * (noclipDirection.Y > 0 ? 1.2 : 1.0),
                    noclipDirection.Z * noclipSpeed);
                p.Position = new Point3D(
                    p.Position.X + p.Velocity.X * deltaSeconds,
                    p.Position.Y + p.Velocity.Y * deltaSeconds,
                    p.Position.Z + p.Velocity.Z * deltaSeconds);
                p.Grounded = false;
                p.WalkAmount = 0f;
                return;
            }

            if (p.CreativeFlightMode)
            {
                StepCreativeFlight(p, tickInput, deltaSeconds);
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
            // Encumbrance from carried ore: the load slows the player down, and at the top level
            // it takes swimming away entirely.
            float encSpeed = WeightSpeedMultiplier(p.WeightLevel);
            bool noSwim = WeightBlocksSwimming(p.WeightLevel);
            if (inWater)
            {
                double submerged = (feetInWater ? 0.25 : 0) + (bodyInWater ? 0.5 : 0) + (headInWater ? 0.25 : 0);

                // Minecraft-faithful: drag applies first to ALL axes, then forces on top.
                // 0.8 per tick at 20 tps = frame-rate-independent drag.
                double drag = Math.Pow(0.8, deltaSeconds * 20.0);
                double vx = p.Velocity.X * drag;
                double vy = p.Velocity.Y * drag;
                double vz = p.Velocity.Z * drag;

                // Horizontal: accelerate toward input direction. Much slower when only
                // wading (feet only) — prevents the surface-glide exploit.
                if (desiredDirection.X != 0 || desiredDirection.Z != 0)
                {
                    double swimH = (bodyInWater ? WalkSpeed * 0.5 : WalkSpeed * 0.35) * encSpeed;
                    double hx = desiredDirection.X * swimH - vx;
                    double hz = desiredDirection.Z * swimH - vz;
                    double hDist = Math.Sqrt(hx * hx + hz * hz);
                    double hAccel = (bodyInWater ? 12.0 : 8.0) * deltaSeconds;
                    if (hDist <= hAccel) { vx += hx; vz += hz; }
                    else { double f = hAccel / hDist; vx += hx * f; vz += hz * f; }
                }

                // Water gravity (Minecraft's 0.02/tick = 4 blocks/sec²).
                vy -= 4.0 * deltaSeconds;

                // Buoyancy: gentle upward force, but WEAKER than water gravity so the
                // player sinks when not pressing swim-up. Feels like treading water.
                // Too heavy to swim: no buoyancy at all, so the load drags the player down.
                vy += (noSwim ? 0.0 : submerged * 2.5) * deltaSeconds;

                // Swim up: only when body+ is submerged. Prevents gliding on the surface.
                // Must HOLD space to stay up — release and you sink.
                if (tickInput.MoveUp && bodyInWater && !noSwim) vy = Math.Max(vy, 2.0);

                // Swim down: remove any upward velocity and let gravity pull you down.
                if (tickInput.MoveDown) vy = Math.Min(vy, 0.0);

                // Water terminal velocity.
                if (vy < -5.0) vy = -5.0;
                if (vy > 5.0) vy = 5.0;

                p.Velocity = new Point3D(vx, vy, vz);
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
            float speed = WalkSpeed * encSpeed;
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

        // Separate from F2 noclip: creative flight keeps the player's collision box active and
        // uses yaw-only movement, acceleration, and strong per-tick drag like Java creative mode.
        private const double CreativeFlightSpeed = 5.6;
        private const double CreativeFlightAcceleration = 42.0;
        private const double CreativeFlightHorizontalDrag = 0.78;
        private const double CreativeFlightVerticalDrag = 0.60;

        private void StepCreativeFlight(PlayerState p, TickInputState tickInput, float deltaSeconds)
        {
            double yaw = p.Yaw * Math.PI / 180.0;
            var forward = new Point3D(Math.Sin(yaw), 0, Math.Cos(yaw));
            var right = GameWorld.GetCameraRight(p.Yaw);
            var direction = Point3D.Zero;
            if (tickInput.MoveForward) direction += forward;
            if (tickInput.MoveBackward) direction -= forward;
            if (tickInput.MoveLeft) direction += right;
            if (tickInput.MoveRight) direction -= right;
            if (tickInput.MoveUp) direction += new Point3D(0, 1, 0);
            if (tickInput.MoveDown) direction += new Point3D(0, -1, 0);

            bool hasInput = direction.X != 0 || direction.Y != 0 || direction.Z != 0;
            if (hasInput) direction = direction.Normalized();

            double dt = Math.Max(0, deltaSeconds);
            double vx = p.Velocity.X * Math.Pow(CreativeFlightHorizontalDrag, dt * 20.0);
            double vy = p.Velocity.Y * Math.Pow(CreativeFlightVerticalDrag, dt * 20.0);
            double vz = p.Velocity.Z * Math.Pow(CreativeFlightHorizontalDrag, dt * 20.0);
            if (hasInput)
            {
                double speedMultiplier = tickInput.MoveFast ? 10.0 : 1.0;
                double targetSpeed = CreativeFlightSpeed * speedMultiplier;
                var target = new Point3D(
                    direction.X * targetSpeed,
                    direction.Y * targetSpeed * (direction.Y > 0 ? 1.2 : 1.0),
                    direction.Z * targetSpeed);
                double dx = target.X - vx, dy = target.Y - vy, dz = target.Z - vz;
                double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                double maxStep = CreativeFlightAcceleration * dt;
                if (distance <= maxStep)
                {
                    vx = target.X; vy = target.Y; vz = target.Z;
                }
                else if (distance > 0)
                {
                    double ratio = maxStep / distance;
                    vx += dx * ratio; vy += dy * ratio; vz += dz * ratio;
                }
            }

            p.Velocity = new Point3D(vx, vy, vz);
            MovePlayerWithCollisions(p, p.Velocity * dt);
            double horizontalSpeed = Math.Sqrt(p.Velocity.X * p.Velocity.X + p.Velocity.Z * p.Velocity.Z);
            p.WalkAmount = (float)Math.Min(1.0, horizontalSpeed / WalkSpeed);
            p.WalkPhase += (float)dt * p.WalkAmount * 10f;
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
                    // No fall damage when landing in water — water cushions the impact.
                    double impactSpeed = -p.Velocity.Y;
                    if (p == LocalPlayer && impactSpeed > FallDamageThreshold
                        && !PlayerSampleInWater(p, 0.05))
                    {
                        int damage = Math.Max(1, (int)Math.Round((impactSpeed - FallDamageThreshold) / FallDamageScale));
                        // A body overloaded with ore hits the ground twice as hard.
                        damage = (int)Math.Round(damage * WeightFallDamageMultiplier(p.WeightLevel));
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
            // While riding a cart the VEHICLE is what moves and collides. The rider is carried
            // inside it, so their own body must pass through the world - otherwise the player's
            // wider box snags on the hill the track climbs over and drags the cart to a stop.
            // Normal collision returns the moment they step out (DismountCart hands control back
            // to StepPlayer).
            if (IsRidingCart) return false;

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

        private static readonly (double dx, double dz)[] PlayerUnstuckDirections =
        {
            (1, 0), (-1, 0), (0, 1), (0, -1),
        };

        private const double PlayerSuffocationDamagePerSecond = 0.75;
        private float _playerSuffocationAccumulator;

        /// <summary>
        /// Player counterpart to <see cref="MobEntity.UpdateBlockEmbedding"/>: if a solid block ends
        /// up sharing the player's hitbox (sand falling on your head, a cube placed into you), slide
        /// them out through the first free horizontal direction. With every side walled off they are
        /// being crushed and take suffocation damage instead. Creative and noclip ignore it.
        /// </summary>
        private void ResolvePlayerEmbedding(PlayerState p, float deltaSeconds)
        {
            if (IsCreative || p.NoclipMode || p.Health <= 0 || !IsPlayerColliding(p.Position))
            {
                _playerSuffocationAccumulator = 0f;
                return;
            }

            const double step = 0.25;
            foreach (var (dx, dz) in PlayerUnstuckDirections)
            {
                var candidate = new Point3D(
                    p.Position.X + dx * step, p.Position.Y, p.Position.Z + dz * step);
                if (IsPlayerColliding(candidate)) continue;

                p.Position = candidate;
                _playerSuffocationAccumulator = 0f;
                return;
            }

            _playerSuffocationAccumulator += (float)(PlayerSuffocationDamagePerSecond * deltaSeconds);
            if (_playerSuffocationAccumulator >= 1f)
            {
                int whole = (int)_playerSuffocationAccumulator;
                _playerSuffocationAccumulator -= whole;
                DamagePlayer(whole, DeathCause.Suffocation);
            }
        }

        public static (double minX, double minY, double minZ, double maxX, double maxY, double maxZ)[] GetBlockCollisionBoxes(int id, int meta)
        {
            if (BlockRegistry.IsSlab(id)) return new[] { (0.0, 0.0, 0.0, 1.0, 0.5, 1.0) };
            if (BlockRegistry.IsSlabTop(id)) return new[] { (0.0, 0.5, 0.0, 1.0, 1.0, 1.0) };
            // Rails render as almost nothing, so the clickable volume is a shallow quarter block
            // sitting on the floor of the cell rather than the full cube. Gated on nothing here,
            // but the player path checks IsSolid first and rails are not solid, so this only
            // ever affects the pick raycast and the highlight that follows it.
            if (BlockRegistry.IsRail(id)) return new[] { (0.0, 0.0, 0.0, 1.0, BlockRegistry.RailPickHeight, 1.0) };
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
            if (BlockRegistry.IsDoor(id))
            {
                // Same panel bounds the mesher draws, so what you see is what you collide with.
                // An open door is a thin strip pinned to its hinge, leaving the doorway walkable.
                BlockRegistry.GetDoorBounds(meta, out double dx0, out double dz0, out double dx1, out double dz1);
                return new[] { (dx0, 0.0, dz0, dx1, 1.0, dz1) };
            }
            if (BlockRegistry.IsCross(id))
            {
                // Wall torches (meta 1-4): raised like the render (0.2) and hugging the wall
                // plane they're attached to instead of centered in the cell.
                if ((id == _idTorch || id == _idSapTorch || id == _idBurntTorch) && meta >= 1 && meta <= 4)
                {
                    return meta switch
                    {
                        1 => new[] { (0.0, 0.2, 0.3, 0.5, 0.9, 0.7) },  // lean +X, wall at -X
                        2 => new[] { (0.5, 0.2, 0.3, 1.0, 0.9, 0.7) },  // lean -X, wall at +X
                        3 => new[] { (0.3, 0.2, 0.0, 0.7, 0.9, 0.5) },  // lean +Z, wall at -Z
                        _ => new[] { (0.3, 0.2, 0.5, 0.7, 0.9, 1.0) },  // lean -Z, wall at +Z
                    };
                }
                // Ceiling sap/burnt torch (meta 5): upside-down, hanging from ceiling
                if ((id == _idSapTorch || id == _idBurntTorch) && meta == 5)
                {
                    return new[] { (0.25, 0.3, 0.25, 0.75, 1.0, 0.7) }; // hanging from ceiling
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
