using System;

namespace Cubuild
{
    /// <summary>
    /// Boats: launching, boarding, steering and the camera while riding.
    ///
    /// A boat is an ENTITY, not a block, so sailing never writes to the world grid (no chunk
    /// remeshes, no network block traffic) and crossing an ocean costs the same as walking.
    /// While occupied the player's own movement is suspended and their eye is pinned to the
    /// hull, so the boat - not the player - is what actually moves.
    /// </summary>
    public sealed partial class GameWorld : IDisposable
    {
        // Resolved LAZILY and defensively. ItemRegistry.GetId is a dictionary indexer that THROWS on
        // an unknown name, and the boat item only exists in the Classic (Minecraft) pack - a
        // static readonly initialiser would therefore throw TypeInitializationException for
        // every GameWorld created in the default pack, and 0 if it silently resolved.
        private static int _boatItemIdCache = -1;
        private static int BoatItemId
        {
            get
            {
                if (_boatItemIdCache < 0)
                    _boatItemIdCache = ItemRegistry.TryGet("boat", out var d) ? d.NumericId : 0;
                return _boatItemIdCache;
            }
            // The registry is swapped wholesale on a gamemode change, so the cached id must be
            // dropped with it or Classic's id would leak into a default-pack world.
            set => _boatItemIdCache = value;
        }

        private static int _idWater = -1;
        private static int WaterBlockId
        {
            get
            {
                if (_idWater < 0) _idWater = BlockRegistry.GetId("water");
                return _idWater;
            }
            set => _idWater = value;
        }

        /// <summary>The boat the local player is currently riding, or null.</summary>
        public BoatEntity? Riding { get; private set; }

        /// <summary>Distance from the seat to the rider's eyes, so you look out over the bow.</summary>
        private const double BoatSeatEyeHeight = 1.15;

        /// <summary>How far the crosshair may reach to grab a boat.</summary>
        private const double BoatReach = 3.5;

        public bool IsRiding => Riding != null;

        // ---- placing and boarding -------------------------------------------------

        /// <summary>
        /// True when the boat item is selected. Checked before the normal block-place path so
        /// right-clicking a boat never tries to put a "boat block" into the world.
        /// </summary>
        public bool HoldingBoat => SelectedBlock == BoatItemId;

        /// <summary>
        /// Handles a right-click while the boat item is in hand. Aiming at water launches a
        /// boat on the surface; aiming at nothing usable (dry land) does nothing. Returns true
        /// when the click was consumed so the caller skips block placement.
        /// </summary>
        public bool TryLaunchBoat(Point3D origin, Point3D direction)
        {
            if (!HoldingBoat) return false;
            var pick = TryPickFluid(origin, direction, WaterBlockId);
            if (!pick.HasValue) return false;

            // Spawn on the top face of the water cell that was hit.
            var cell = pick.Value.Remove;
            var spawn = new Point3D(cell.x + 0.5, cell.y + 1.0, cell.z + 0.5);
            if (Entities.AddMobAt("boat", spawn, LocalPlayer.Yaw))
            {
                TryConsumeFromInventory(BoatItemId, 1);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Boards the boat under the crosshair, or steps out of the one being ridden. Returns
        /// true when it consumed the click.
        /// </summary>
        public bool ToggleBoatUnderCrosshair(Point3D origin, Point3D direction)
        {
            if (Riding != null) { DismountBoat(); return true; }

            // An empty hand (or anything) right-clicks a boat to board it - like Minecraft.
            var boat = FindBoatUnderCrosshair(origin, direction);
            if (boat == null) return false;
            MountBoat(boat);
            return true;
        }

        /// <summary>The nearest boat within <see cref="BoatReach"/> of the crosshair ray.</summary>
        private BoatEntity? FindBoatUnderCrosshair(Point3D origin, Point3D direction)
        {
            BoatEntity? best = null;
            double bestDist = BoatReach;
            foreach (var mob in Entities.Mobs)
            {
                if (mob is not BoatEntity boat) continue;
                double dx = boat.Position.X - origin.X;
                double dy = boat.Position.Y + boat.Height * 0.5 - origin.Y;
                double dz = boat.Position.Z - origin.Z;
                double along = dx * direction.X + dy * direction.Y + dz * direction.Z;
                if (along < 0 || along > BoatReach) continue;
                double perp = Math.Sqrt(Math.Max(0, dx * dx + dy * dy + dz * dz - along * along));
                // Generous radius - the hull is wider than the ray is precise.
                if (perp < 1.4 && along < bestDist) { bestDist = along; best = boat; }
            }
            return best;
        }

        public void MountBoat(BoatEntity boat)
        {
            DismountBoat();
            Riding = boat;
            Entities.LocallyDrivenMob = boat;
            boat.Occupied = true;
            // Face the boat the way the player was already looking, so steering feels immediate.
            // BoatEntity stores yaw in RADIANS (it feeds Math.Sin/Cos and its own steering),
            // whereas the player's yaw is in DEGREES (Simulation converts with *PI/180).
            if (Math.Abs(boat.Yaw) > 0.001f)
            {
                float deg = boat.Yaw * 180f / (float)Math.PI;
                LocalPlayer.Yaw = (deg % 360f + 360f) % 360f;
            }
        }

        public void DismountBoat()
        {
            if (Riding is { } b)
            {
                b.Occupied = false;
                // Step the player off to the side so they don't end up inside the hull.
                double sx = b.Position.X + Math.Cos(b.Yaw) * 1.1;
                double sz = b.Position.Z - Math.Sin(b.Yaw) * 1.1;
                double feetY = b.Position.Y + 0.2;
                LocalPlayer.Position = new Point3D(sx, feetY + EyeHeight, sz);
                // Drop anything accumulated while carried, so stepping off never launches you.
                LocalPlayer.Velocity = new Point3D(0, 0, 0);
                Riding = null;
                Entities.LocallyDrivenMob = null;
            }
        }

        // ---- per-tick riding ------------------------------------------------------

        /// <summary>
        /// Moves the boat the player is steering and pins the player's eye to the hull. Called
        /// instead of StepPlayer while mounted.
        /// </summary>
        public void StepRiding(TickInputState tickInput, float deltaSeconds)
        {
            if (Riding is not { } boat) { StepPlayer(LocalPlayer, tickInput, deltaSeconds); return; }

            // Steering axes, relative to the boat's own heading.
            float fwd = (tickInput.MoveForward ? 1f : 0f) - (tickInput.MoveBackward ? 1f : 0f);
            float strafe = (tickInput.MoveRight ? 1f : 0f) - (tickInput.MoveLeft ? 1f : 0f);

            // A/D steer the hull; W/S drive it forward/back.
            if (Math.Abs(strafe) > 0.01f)
                boat.Yaw -= strafe * 1.9f * deltaSeconds;
            boat.SteerForward = fwd;
            boat.SteerStrafe = 0f;   // turning is handled by yaw, not lateral thrust

            // Keep the boat alive even though the normal sim path is skipped for the player.
            boat.Update(deltaSeconds, Chunks);

            // Pin the rider to the hull. Eye = hull feet + seat height.
            LocalPlayer.Position = new Point3D(
                boat.Position.X,
                boat.Position.Y + BoatSeatEyeHeight,
                boat.Position.Z);
            // Carried, not walking - nothing of the player's own motion applies while aboard.
            LocalPlayer.Velocity = new Point3D(0, 0, 0);
        }

        /// <summary>True when this cell is water - used to keep launches on the surface.</summary>
        private bool IsWaterCell(int x, int y, int z)
            => Chunks.TryGetLoadedBlock(x, y, z, out int id) && id == WaterBlockId;
    }
}