using System;
using System.Collections.Generic;
using Veldrid;

namespace Cubuild
{
    /// <summary>
    /// Manages entities (mobs) in the game world.
    /// Now uses a unified system where all mobs implement IMobRenderable.
    /// </summary>
    public sealed class EntityManager : IDisposable
    {
        private readonly ChunkManager _chunkManager;
        private readonly List<IMobRenderable> _mobs = new();
        private readonly List<MobRenderData> _mobRenderData = new();
        private readonly Dictionary<string, MobModel> _loadedModels = new();
        private readonly Random _rand = new();

        // Natural spawning + despawning. Each category from spawns.json gets its own spawner
        // (caps, biome-filtered table, light gate, cadence). Set to null to disable.
        private readonly List<MobSpawner> _spawners = new();
        private readonly Dictionary<MobSpawner, double> _spawnAccumulators = new();
        /// <summary>Mob type -> spawning category that owns it (caps follow the category).</summary>
        private readonly Dictionary<string, SpawnCategory> _typeCategory = new(StringComparer.OrdinalIgnoreCase);
        private readonly System.Diagnostics.Stopwatch _entityWatch = new();
        public float LastUpdateMs { get; private set; }
        public int MobCount => _mobs.Count;
        /// <summary>
        /// The live entity list, read-only. Lets gameplay code locate a specific entity by
        /// type - e.g. finding the boat under the crosshair so the player can board it.
        /// The list mutates during iteration, so callers should break out, not remove.
        /// </summary>
        public IReadOnlyList<IMobRenderable> Mobs => _mobs;
        // World->skylightSubtracted (0 day .. 11 night) for the monster darkness gate.
        private Func<int>? _skylightSubtractedFn;

        private const float BlockReach = 6.5f;

        public IReadOnlyList<MobRenderData> MobRenderData => _mobRenderData;

        public EntityManager(ChunkManager chunkManager, Func<int, int, BiomeDefinition>? biomeAt = null)
        {
            _chunkManager = chunkManager ?? throw new ArgumentNullException(nameof(chunkManager));

            // Build spawners from spawns.json (embedded resource or loose file). If the data file
            // is missing or malformed, fall back to a sensible hardcoded table so the game boots.
            List<SpawnCategory> categories;
            try
            {
                categories = SpawnTable.LoadDefault();
            }
            catch
            {
                categories = DefaultFallbackTable();
            }
            foreach (var cat in categories)
            {
                AddSpawner(cat, biomeAt);
            }
        }

        private void AddSpawner(SpawnCategory category, Func<int, int, BiomeDefinition>? biomeAt)
        {
            var spawner = new MobSpawner(
                category,
                AddMobAt,
                () => CountMobsOfCategory(category),
                CountMobsOfType,
                (chunkX, chunkZ) => CountMobsInChunkColumn(chunkX, chunkZ, category),
                biomeAt);
            _spawners.Add(spawner);
            _spawnAccumulators[spawner] = 0.0;
            foreach (var rule in category.Rules) _typeCategory[rule.MobId] = category;
        }

        /// <summary>Hardcoded safety net when spawns.json can't be loaded: one creature and one
        /// monster category mirroring the old behaviour.</summary>
        private static List<SpawnCategory> DefaultFallbackTable()
        {
            var creatures = new SpawnCategory
            {
                Id = "creature", MaxTotal = 20, MaxPerType = 12, MaxPerChunk = 6,
                YMode = SpawnYMode.Surface, LightGate = SpawnLightGate.Any, SpawnInterval = 2.0,
            };
            creatures.Rules.Add(new MobSpawnRule { MobId = "duck", Weight = 6, PackMin = 1, PackMax = 3, NearWater = false });
            creatures.Rules.Add(new MobSpawnRule { MobId = "coyote", Weight = 3, PackMin = 1, PackMax = 2 });
            creatures.Rules.Add(new MobSpawnRule { MobId = "steve", Weight = 1, PackMin = 1, PackMax = 1 });

            var monsters = new SpawnCategory
            {
                Id = "monster", MaxTotal = 100, MaxPerType = 20, MaxPerChunk = 8,
                YMode = SpawnYMode.DepthBias, LightGate = SpawnLightGate.Dark, SpawnInterval = 2.0,
            };
            monsters.Rules.Add(new MobSpawnRule { MobId = "zombie", Weight = 10, PackMin = 1, PackMax = 4, MinY = 8, MaxY = 119 });

            return new List<SpawnCategory> { creatures, monsters };
        }

        /// <summary>Total mobs spawned by one category (sum over its rules' type counts) - caps
        /// stay meaningful per category instead of bleeding across tables.</summary>
        private int CountMobsOfCategory(SpawnCategory category)
        {
            int count = 0;
            foreach (var rule in category.Rules)
            {
                count += CountMobsOfType(rule.MobId);
            }
            return count;
        }

        /// <summary>Mobs of THIS category standing in a chunk column (per-chunk density budget -
        /// categories no longer crowd each other out of a column).</summary>
        private int CountMobsInChunkColumn(int chunkX, int chunkZ, SpawnCategory category)
        {
            int count = 0;
            foreach (var mob in _mobs)
            {
                if (mob is not MobEntity me) continue;
                if (!_typeCategory.TryGetValue(me.MobTypeName, out var cat) || cat != category) continue;
                int mx = (int)Math.Floor(mob.Position.X / 16.0);
                int mz = (int)Math.Floor(mob.Position.Z / 16.0);
                if (mx == chunkX && mz == chunkZ) count++;
            }
            return count;
        }

        /// <summary>Total living mobs (for the spawn cap).</summary>
        public int CountMobs(Point3D ignore) => _mobs.Count;

        /// <summary>
        /// Supplies the current night-dim level (0..11) for the monster darkness gate.
        /// Wired to GameWorld.NightDimLevel so zombies know when the surface is dark.
        /// </summary>
        public void SetSkylightSource(Func<int> skylightSubtractedFn)
        {
            _skylightSubtractedFn = skylightSubtractedFn;
        }

        private int CountMobsOfType(string mobId)
        {
            int count = 0;
            foreach (var mob in _mobs)
            {
                if (mob is MobEntity me && string.Equals(me.MobTypeName, mobId, StringComparison.OrdinalIgnoreCase))
                    count++;
            }
            return count;
        }

        public void SpawnDuck(Point3D playerPosition, float playerYaw)
        {
            float yawRad = playerYaw * (float)Math.PI / 180f;
            double fx = Math.Sin(yawRad);
            double fz = -Math.Cos(yawRad);

            double spawnX = playerPosition.X + fx * 3.0;
            double spawnY = playerPosition.Y + 2.0;
            double spawnZ = playerPosition.Z + fz * 3.0;

            float duckYaw = playerYaw + 180f;
            _mobs.Add(new Duck(new Point3D(spawnX, spawnY, spawnZ), duckYaw));
        }

        public void SpawnCoyote(Point3D playerPosition, float playerYaw)
        {
            float yawRad = playerYaw * (float)Math.PI / 180f;
            double fx = Math.Sin(yawRad);
            double fz = -Math.Cos(yawRad);

            double spawnX = playerPosition.X + fx * 3.0;
            double spawnY = playerPosition.Y + 2.0;
            double spawnZ = playerPosition.Z + fz * 3.0;

            float mobYaw = playerYaw + 180f;
            _mobs.Add(new Coyote(new Point3D(spawnX, spawnY, spawnZ), mobYaw));
        }

        public void SpawnSteve(Point3D playerPosition, float playerYaw)
        {
            float yawRad = playerYaw * (float)Math.PI / 180f;
            double fx = Math.Sin(yawRad);
            double fz = -Math.Cos(yawRad);

            double spawnX = playerPosition.X + fx * 3.0;
            double spawnY = playerPosition.Y + 2.0;
            double spawnZ = playerPosition.Z + fz * 3.0;

            // MobEntity yaw is radians; face the mob back toward the player.
            float mobYaw = (playerYaw + 180f) * (float)Math.PI / 180f;
            _mobs.Add(new SteveMob(new Point3D(spawnX, spawnY, spawnZ), mobYaw));
        }

        public bool SpawnMobById(string mobId, Point3D playerPosition, float playerYaw)
        {
            // Built-in mobs spawn from hardcoded classes; registry mobs need a MobDefinition.
            if (mobId != "duck" && mobId != "coyote" && mobId != "coyotemob" && mobId != "steve"
                && mobId != "locomotive" && mobId != "locomotivehot" && mobId != "locomotivecold"
                && MobRegistry.Get(mobId) == null) return false;

            float yawRad = playerYaw * (float)Math.PI / 180f;
            double fx = Math.Sin(yawRad);
            double fz = -Math.Cos(yawRad);

            double spawnX = playerPosition.X + fx * 3.0;
            double spawnY = playerPosition.Y + 2.0;
            double spawnZ = playerPosition.Z + fz * 3.0;

            float mobYaw = playerYaw + 180f;
            return AddMobAt(mobId, new Point3D(spawnX, spawnY, spawnZ), mobYaw);
        }

        /// <summary>Creates a mob exactly at the given position (used by the natural spawner).</summary>
        public bool AddMobAt(string mobId, Point3D position, float yaw)
        {
            MobEntity? mob = null;
            if (mobId == "duck")
                mob = new Duck(position, yaw);
            else if (mobId == "coyote" || mobId == "coyotemob")
                mob = new Coyote(position, yaw);
            else if (mobId == "steve")
                mob = new SteveMob(position, yaw);
            else if (mobId == "cart")
                // Prototype: rides the rail network, driven by a seated player.
                mob = new CartEntity(position, yaw);
            else if (mobId == "orecart")
                // Passive variant: same rails, but no rider and no throttle - you push it by walking.
                mob = new OreCartEntity(position, yaw);
            else if (mobId == "locomotive" || mobId == "locomotivehot" || mobId == "locomotivecold")
                // Train engine: no seat, but drives its whole train when a rider sits in a carriage
                // coupled to it (see LocomotiveEntity / GameWorld.StepRidingCart).
                mob = new LocomotiveEntity(position, yaw, mobId == "locomotivecold" ? "locomotivecold" : "locomotivehot");
            else if (mobId == "boat")
                // Player-launched vehicle, not a spawned mob: no brain, floats, steered by a rider.
                mob = new BoatEntity(position, yaw);
            else
            {
                var def = MobRegistry.Get(mobId);
                if (def == null) return false;
                // Rare brute variant: 1 in 50 zombies spawn 2x size, half speed, double health.
                bool brute = string.Equals(def.Id, "zombie", StringComparison.OrdinalIgnoreCase)
                    && Random.Shared.Next(50) == 0;
                mob = new GenericMobEntity(def, position, yaw, brute);
            }

            // Give every mob the world's day/night source for environmental behaviors (sunburn).
            if (mob != null)
            {
                mob.SkylightSource = _skylightSubtractedFn;
                _mobs.Add(mob);
                return true;
            }
            return false;
        }

        public void Update(float deltaSeconds) => Update(deltaSeconds, new Point3D(0, 0, 0), false, true);

        /// <summary>
        /// Set by the owning world so hostile mobs can damage the local player. Receives the
        /// attack damage and the death cause to record if the hit is lethal. Null = attacks on the
        /// player are harmless (creative / pre-wiring).
        /// </summary>
        public Action<int, DeathCause>? PlayerDamageCallback { get; set; }

        // ---- player body for mob separation ----
        /// <summary>Player AABB center (world space, not the eye) used for player-mob repulsion; the world sets it each tick.</summary>
        public Point3D PlayerBodyCenter;
        /// <summary>Player XZ half-extent (radius) for repulsion.</summary>
        public double PlayerHalfWidth = 0.30;
        /// <summary>Player vertical half-extent for repulsion.</summary>
        public double PlayerHalfHeight = 0.90;
        /// <summary>Receives the player's XZ push velocity from mob separation (mobs shove the player back).</summary>
        public Action<Point3D>? PlayerPushCallback { get; set; }

        /// <summary>
        /// The vehicle the local player is currently riding, if any. GameWorld already stepped it
        /// (StepRidingCart / StepRiding) and pinned the player to it this frame, so ticking it a
        /// second time here would (a) run the vehicle at double speed and (b) leave the vehicle
        /// further along the track than the rider, which the separation pass would read as the
        /// rider being buried inside it.
        /// </summary>
        public MobEntity? LocallyDrivenMob;

        /// <summary>
        /// Advance all mobs by one frame. When <paramref name="playerPosition"/> is supplied,
        /// natural spawning (near the player, 24-32 blocks out) and despawning (far-away mobs)
        /// also run. <paramref name="playerAlive"/> tells hostile mobs whether the player is a
        /// valid chase target (dead players are ignored until respawn).
        /// </summary>
        public void Update(float deltaSeconds, Point3D playerPosition, bool enableSpawning = true, bool playerAlive = true)
        {
            _entityWatch.Restart();

            // Vehicles do not run the ordinary death animation. Remove any vehicle that was
            // marked dead/removed by another damage source before chunk streaming can snapshot it.
            for (int i = _mobs.Count - 1; i >= 0; i--)
            {
                if (_mobs[i] is not MobEntity vehicle
                    || (vehicle is not CartEntity && vehicle is not BoatEntity)
                    || (!vehicle.IsDead && !vehicle.Removed)) continue;
                if (vehicle is CartEntity cart) SeverCartLink(cart);
                _idleTimeAccum.Remove(vehicle);
                _mobs.RemoveAt(i);
                if (ReferenceEquals(LocallyDrivenMob, vehicle)) LocallyDrivenMob = null;
            }

            // World-streaming persistence: detach mobs whose chunk just unloaded (they'd otherwise
            // keep simulating against empty air and fall into the void), and restore mobs saved
            // for chunks the player just returned to.
            SyncDetachedMobs(playerPosition);

            // Update all mobs. Every mob derives from MobEntity (Duck, Coyote, SteveMob, generic
            // registry mobs all share one universal AI/physics implementation).
            for (int i = _mobs.Count - 1; i >= 0; i--)
            {
                var mob = _mobs[i];

                if (mob is MobEntity mobEntity)
                {
                    // Hostiles hunt the nearest human: the local player plus any Steve NPCs. The
                    // zombie re-paths toward the target each frame (A* routes around cliffs/walls)
                    // and its OnAttack damages a Steve when it closes in - or the local player via
                    // PlayerDamageCallback (health, hurt flash, regen reset, death cause). A DEAD
                    // player is not a target: zombies lose interest and wander off until respawn.
                    if (mobEntity.Hostile)
                    {
                        IMobRenderable? steveTarget = null;
                        // Chase the player's BODY CENTER, not the eye. LocalPlayer.Position is the
                        // eye (EyeHeight above the feet); the mob's own Position is its feet. With
                        // the 3D attack-range check, aiming at the eye left dy ≈ 1.62 on flat
                        // ground, so dy² > AttackRange² and the zombie never landed a hit.
                        Point3D target = new(PlayerBodyCenter.X, PlayerBodyCenter.Y, PlayerBodyCenter.Z);
                        double bestSq = playerAlive ? DistSq(mobEntity.Position, playerPosition) : double.MaxValue;
                        for (int h = 0; h < _mobs.Count; h++)
                        {
                            if (h == i) continue;
                            var other = _mobs[h];
                            if (other.IsDead) continue;
                            if (other is SteveMob)
                            {
                                double d = DistSq(mobEntity.Position, other.Position);
                                if (d < bestSq) { bestSq = d; steveTarget = other; target = other.Position; }
                            }
                        }

                        if (!playerAlive && steveTarget == null)
                        {
                            // No living target: drop the chase (also clears the A* route).
                            mobEntity.OnAttack = null;
                            mobEntity.SetChaseTarget(null);
                        }
                        else
                        {
                            var capturedSteve = steveTarget;
                            var capturedMob = mobEntity;
                            mobEntity.OnAttack = capturedSteve is MobEntity steve
                                ? () => steve.Damage(capturedMob.AttackDamage, capturedMob.Position.X, capturedMob.Position.Z, true)
                                : () => PlayerDamageCallback?.Invoke(capturedMob.AttackDamage, DeathCause.Mob);
                            mobEntity.SetChaseTarget(target);
                        }
                    }
                    else
                    {
                        mobEntity.SetChaseTarget(null);
                        mobEntity.OnAttack = null;
                    }

                    if (mobEntity != LocallyDrivenMob)
                        mobEntity.Update(deltaSeconds, _chunkManager);

                    if (mobEntity.Removed)
                    {
                        // A coupling cannot outlive a cart - drop either half it belonged to.
                        if (mobEntity is CartEntity removedCart) SeverCartLink(removedCart);
                        _mobs.RemoveAt(i);
                        continue;
                    }

                    // Despawn: too far away, or idle too long at medium distance. Instead of
                    // deleting the mob, snapshot its state so it comes back when the player
                    // returns to its chunk (world-streaming persistence).
                    if (enableSpawning && ShouldDespawn(mobEntity, playerPosition))
                    {
                        if (mobEntity is CartEntity despawnedCart) SeverCartLink(despawnedCart);
                        DetachMob(mobEntity);
                        _mobs.RemoveAt(i);
                    }
                }
            }

            // Rigid train coupling: place every towed cart behind its leader, head-of-train first
            // so a whole chain resolves in one pass. Runs before the shove passes below so they see
            // the trains' settled positions.
            ResolveTrainLinks(deltaSeconds);

            LastUpdateMs = (float)_entityWatch.Elapsed.TotalMilliseconds;

            // Push mobs apart so they don't clump. Same algorithm as MC 1.12: inverse-distance
            // horizontal repulsion applied to both entities.
            PushMobsApart(deltaSeconds);

            // Carts shove each other separately (see PushCartsApart for why they cannot ride the
            // generic pass above).
            PushCartsApart(deltaSeconds);

            // Natural spawning: each category ticks on its own interval from spawns.json and runs
            // multiple passes per check, so an empty area fills quickly without hammering every
            // frame. The light probe combines the day/night-aware sky estimate with the baked
            // torch/glow block light (Chunk.LightGrid), so torch-lit rooms are protected.
            if (enableSpawning)
            {
                int skylight = _skylightSubtractedFn?.Invoke() ?? 0;
                foreach (var spawner in _spawners)
                {
                    _spawnAccumulators[spawner] += deltaSeconds;
                    if (_spawnAccumulators[spawner] < spawner.SpawnInterval) continue;
                    _spawnAccumulators[spawner] = 0;

                    Func<int, int, int, int>? lightProbe = null;
                    if (spawner.Category.LightGate != SpawnLightGate.Any && _skylightSubtractedFn != null)
                    {
                        lightProbe = (x, y, z) => _chunkManager.GetSpawnLight(x, y, z, skylight);
                    }
                    for (int pass = 0; pass < 10; pass++)
                    {
                        spawner.TrySpawn(_chunkManager, playerPosition, _rand, lightProbe);
                    }
                }
            }

            // Build render data
            _mobRenderData.Clear();
            foreach (var mob in _mobs)
            {
                _mobRenderData.Add(Cubuild.MobRenderData.FromMob(mob));
            }
        }

        /// <summary>Collects mining targets from all zombies actively breaking blocks.</summary>
        public void CollectMiningTargets(System.Collections.Generic.List<Cubuild.Renderer.ZombieMiningTarget> list)
        {
            list.Clear();
            foreach (var mob in _mobs)
            {
                var mb = ((IMobRenderable)mob).MiningBlock;
                if (mb.HasValue)
                {
                    list.Add(new Cubuild.Renderer.ZombieMiningTarget
                    {
                        X = mb.Value.X, Y = mb.Value.Y, Z = mb.Value.Z,
                        BlockId = mb.Value.BlockId,
                        Progress = mb.Value.Progress,
                    });
                }
            }
        }

        /// <summary>
        /// MC-style entity repulsion. Pairs of nearby mobs push apart with inverse-distance
        /// horizontal force so they spread out instead of clumping into one spot.
        /// </summary>
        private void PushMobsApart(float dt)
        {
            int count = _mobs.Count;
            if (count == 0) return;
            double dtScale = dt * 60.0; // normalize to 60fps tick equivalent

            for (int i = 0; i < count; i++)
            {
                if (_mobs[i] is not MobEntity a || a.IsDead || !a.ParticipatesInSeparation) continue;
                double ax = a.Position.X, ay = a.Position.Y, az = a.Position.Z;

                for (int j = i + 1; j < count; j++)
                {
                    if (_mobs[j] is not MobEntity b || b.IsDead || !b.ParticipatesInSeparation) continue;
                    double dx = ax - b.Position.X, dz = az - b.Position.Z;
                    double dy = ay - b.Position.Y;
                    double distXZSq = dx * dx + dz * dz;
                    double minDistXZ = (a.Width * 0.5 + b.Width * 0.5);
                    double minDistXZSq = minDistXZ * minDistXZ;

                    if (distXZSq < minDistXZSq && distXZSq > 0.0001)
                    {
                        // Only push when vertically overlapping too — mobs at different Y levels
                        // (e.g. one on a platform above) shouldn't repel each other.
                        double minDistY = (a.Height * 0.5 + b.Height * 0.5);
                        if (Math.Abs(dy) >= minDistY) continue;

                        double dist = Math.Sqrt(distXZSq);
                        double invDist = 1.0 / dist;
                        double push = 3.0 * Math.Min(1.0, invDist) * dtScale;
                        a.AddVelocity(dx * invDist * push, 0, dz * invDist * push);
                        b.AddVelocity(-dx * invDist * push, 0, -dz * invDist * push);
                    }
                }

                // The local player participates in separation like a heavy mob: mobs touching
                // the player's AABB get pushed away, and the player receives a lighter shove back
                // (MC-style mass ratio ~2:1 - the player is heavier than most mobs).
                if (PlayerPushCallback != null)
                {
                    double pdx = ax - PlayerBodyCenter.X, pdz = az - PlayerBodyCenter.Z;
                    double pdy = ay - PlayerBodyCenter.Y;
                    double pMinXZ = a.Width * 0.5 + PlayerHalfWidth;
                    double pDistXZSq = pdx * pdx + pdz * pdz;

                    if (pDistXZSq < pMinXZ * pMinXZ && pDistXZSq > 0.0001)
                    {
                        double pMinY = a.Height * 0.5 + PlayerHalfHeight;
                        if (Math.Abs(pdy) < pMinY)
                        {
                            double pDist = Math.Sqrt(pDistXZSq);
                            double pInv = 1.0 / pDist;
                            double pPush = 3.0 * Math.Min(1.0, pInv) * dtScale;
                            a.AddVelocity(pdx * pInv * pPush, 0, pdz * pInv * pPush);
                            PlayerPushCallback.Invoke(new Point3D(
                                -pdx * pInv * pPush * 0.5, 0, -pdz * pInv * pPush * 0.5));
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Cart-against-cart shoving, kept OUT of <see cref="PushMobsApart"/> on purpose. The
        /// driven minecart opts out of the generic pass - its rider is pinned exactly onto it, so
        /// a separation shove would fire every frame, never decay (StepPlayer is skipped while
        /// riding) and fling the player away from the track on dismount. The cost of that opt-out
        /// was that a driven minecart and an ore cart had no way to notice each other at all: the
        /// cart drove straight through one. This pass closes that gap for carts only.
        ///
        /// Only a cart that accepts pushes is ever written to (the ore cart), so the minecart acts
        /// as an immovable pusher - exactly like an occupied cart in Minecraft - and the transfer
        /// is driven by the carts' OWN rail velocities rather than a fixed force: the closing
        /// speed is handed over as velocity, which the pushed cart folds into its rail Speed
        /// (RailPushFactor), and the leftover overlap is then resolved positionally through its
        /// collision-aware mover so the two bodies read as solid.
        /// </summary>
        private void PushCartsApart(float dt)
        {
            int count = _mobs.Count;
            if (count < 2) return;
            double dtScale = dt * 60.0;

            for (int i = 0; i < count; i++)
            {
                if (_mobs[i] is not CartEntity a || a.IsDead) continue;
                double ax = a.Position.X, ay = a.Position.Y, az = a.Position.Z;
                a.GetWorldVelocity(out double avx, out double avz);

                for (int j = i + 1; j < count; j++)
                {
                    if (_mobs[j] is not CartEntity b || b.IsDead) continue;

                    // Rigidly coupled carts are one train: never shove a linked pair apart (that
                    // would fight the rigid tow and could drag them out of their spacing).
                    if (a.Follower == b || b.Follower == a) continue;

                    // Two minecarts are both immovable, so the pair is left completely alone.
                    bool aPush = a.AcceptsCartPush;
                    bool bPush = b.AcceptsCartPush;
                    if (!aPush && !bPush) continue;

                    // Same contact test as the generic pass: horizontal overlap AND vertical
                    // overlap, so a cart on a bridge never shoves a cart on the track below.
                    double dx = ax - b.Position.X, dz = az - b.Position.Z;
                    double distXZSq = dx * dx + dz * dz;
                    double minDistXZ = a.Width * 0.5 + b.Width * 0.5;
                    if (distXZSq >= minDistXZ * minDistXZ || distXZSq <= 0.0001) continue;
                    if (Math.Abs(ay - b.Position.Y) >= a.Height * 0.5 + b.Height * 0.5) continue;

                    double dist = Math.Sqrt(distXZSq);
                    double nx = dx / dist, nz = dz / dist; // unit vector pointing from b to a

                    b.GetWorldVelocity(out double bvx, out double bvz);

                    // Closing speed, positive when the two approach along n. Handing THIS over
                    // instead of a fixed push is what makes a moving cart transfer real momentum
                    // while a parked pair does nothing at all - and because the transfer cancels
                    // the closure, a pushed cart settles at the pusher's speed instead of
                    // accelerating away from it.
                    double closing = (bvx - avx) * nx + (bvz - avz) * nz;
                    if (closing > 0)
                    {
                        if (aPush) a.AddVelocity(nx * closing, 0, nz * closing);
                        if (bPush) b.AddVelocity(-nx * closing, 0, -nz * closing);
                    }

                    // Resolve whatever overlap is left, so a driven cart pushes the one ahead of it
                    // along the rails rather than driving through it, and an ore cart coasting
                    // into a parked minecart bumps to a stop instead of clipping inside.
                    double move = Math.Min((minDistXZ - dist) * 0.5, CartEntity.CartPushStepCap * dtScale);
                    if (move <= 0) continue;
                    if (aPush) a.ShoveBy(nx * move, nz * move, _chunkManager);
                    if (bPush) b.ShoveBy(-nx * move, -nz * move, _chunkManager);
                }
            }
        }

        // ------------------------------------------------------------------
        // Cart coupling ("linkage" trains). A link is Head -> Trailer: the trailer's own Update is
        // skipped (see CartEntity.Update) and it is placed rigidly behind its head here. Links are
        // removed ONLY by a left-click on the beam (TryBreakLink) or by a cart ceasing to exist
        // (SeverCartLink) - never by the physics.
        // ------------------------------------------------------------------

        /// <summary>Half-thickness of the drawn coupling beam, in blocks (matches the renderer).</summary>
        private const double LinkBeamRadius = 0.06;

        /// <summary>How forgiving the left-click pick is: the ray may miss the beam's axis by this much.</summary>
        private const double LinkPickRadius = 0.35;

        /// <summary>
        /// Places every towed cart rigidly behind its leader. Walks each train from its HEAD (a cart
        /// with no leader of its own but with something coupled behind) down the chain, so each
        /// follower is placed against an already-placed leader within the same frame - a chain of N
        /// carts settles correctly in one pass.
        /// </summary>
        private void ResolveTrainLinks(float dt)
        {
            for (int i = 0; i < _mobs.Count; i++)
            {
                if (_mobs[i] is not CartEntity head) continue;
                if (head.IsDead || head.Removed) continue;
                if (head.Leader != null || head.Follower == null) continue;   // only start from a head

                var trailer = head.Follower;
                int guard = 0;
                while (trailer != null && guard++ < 256)
                {
                    if (trailer.IsDead || trailer.Removed)
                    {
                        SeverCartLink(trailer);
                        break;
                    }
                    trailer.FollowLeader(_chunkManager, dt);
                    trailer = trailer.Follower;
                }
            }
        }

        /// <summary>
        /// Clears both halves of a cart's couplings. Called ONLY when a cart is removed or despawned -
        /// the coupling cannot outlive a cart. Never called by the physics.
        /// </summary>
        private static void SeverCartLink(CartEntity c)
        {
            if (c.Leader is { } l && l.Follower == c) l.Follower = null;
            if (c.Follower is { } f && f.Leader == c) f.Leader = null;
            c.Leader = null;
            c.Follower = null;
        }

        /// <summary>
        /// The drawn/pickable coupling segment between two linked carts: each cart's coupling point,
        /// trimmed in so the beam fills the visible gap between the bodies rather than passing through
        /// them (so a left-click on a cart still hits the cart, and a left-click in the gap hits the
        /// beam). Shared by the renderer feed and the break test so they can never disagree.
        /// </summary>
        private static void LinkSegment(CartEntity a, CartEntity b, out Point3D pa, out Point3D pb)
        {
            // Render transform, not the raw physics one: a towed cart eases its drawn spot through a
            // bend (CartEntity.SmoothRenderPose), and the beam must stay attached to the cart the eye
            // actually sees. The head is not towed, so its RenderPosition is its exact spot.
            var ap = a.RenderPosition;
            var bp = b.RenderPosition;
            double ax = ap.X, ay = ap.Y + a.Height * 0.5, az = ap.Z;
            double bx = bp.X, by = bp.Y + b.Height * 0.5, bz = bp.Z;
            double dx = bx - ax, dy = by - ay, dz = bz - az;
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 1e-6) { pa = new Point3D(ax, ay, az); pb = pa; return; }
            double ux = dx / len, uy = dy / len, uz = dz / len;
            const double trim = 0.42;                      // ~half a cart's width
            double t = Math.Min(trim, len * 0.5);
            pa = new Point3D(ax + ux * t, ay + uy * t, az + uz * t);
            pb = new Point3D(bx - ux * t, by - uy * t, bz - uz * t);
        }

        /// <summary>
        /// Fills <paramref name="list"/> with the coupling beams of every active link, for the
        /// renderer. Called each frame from Program.BuildHud.
        /// </summary>
        public void CollectLinkBeams(List<Cubuild.Renderer.LinkBeam> list)
        {
            list.Clear();
            foreach (var mob in _mobs)
            {
                if (mob is not CartEntity a || a.IsDead || a.Removed) continue;
                if (a.Follower is not { } b || b.IsDead || b.Removed) continue;
                LinkSegment(a, b, out var pa, out var pb);
                list.Add(new Cubuild.Renderer.LinkBeam(
                    new System.Numerics.Vector3((float)pa.X, (float)pa.Y, (float)pa.Z),
                    new System.Numerics.Vector3((float)pb.X, (float)pb.Y, (float)pb.Z)));
            }
        }

        /// <summary>
        /// True when the pick ray strikes a link's beam: severs that coupling and reports the hit
        /// distance. This is the ONLY way a player removes a link. Tested BEFORE the mob attack so a
        /// click on the beam breaks it instead of damaging a cart.
        /// </summary>
        public bool TryBreakLink(Point3D origin, Point3D forward, out double hitDistance)
        {
            hitDistance = double.PositiveInfinity;
            var dir = forward.Normalized();
            CartEntity? hitA = null, hitB = null;
            double best = double.PositiveInfinity;

            foreach (var mob in _mobs)
            {
                if (mob is not CartEntity a || a.IsDead || a.Removed) continue;
                if (a.Follower is not { } b || b.IsDead || b.Removed) continue;

                LinkSegment(a, b, out var pa, out var pb);
                if (!RaySegmentHit(origin, dir, pa, pb, LinkPickRadius, BlockReach, out double t)) continue;
                if (t < best) { best = t; hitA = a; hitB = b; }
            }

            if (hitA == null || hitB == null) return false;
            if (hitA.Follower == hitB) hitA.Follower = null;
            if (hitB.Leader == hitA) hitB.Leader = null;
            hitDistance = best;
            return true;
        }

        /// <summary>
        /// Cheap ray-vs-segment proximity test: samples the segment and keeps the nearest point whose
        /// ray projection is in [0, maxDist] and whose perpendicular distance to the ray is within
        /// <paramref name="radius"/>. A handful of links with two dozen samples each is trivial, and
        /// it avoids the fragility of an analytic ray/segment solve for such a thin target.
        /// </summary>
        private static bool RaySegmentHit(Point3D origin, Point3D dir, Point3D p0, Point3D p1, double radius, double maxDist, out double hitT)
        {
            hitT = double.PositiveInfinity;
            const int samples = 24;
            for (int i = 0; i <= samples; i++)
            {
                double s = (double)i / samples;
                double px = p0.X + (p1.X - p0.X) * s;
                double py = p0.Y + (p1.Y - p0.Y) * s;
                double pz = p0.Z + (p1.Z - p0.Z) * s;
                double dx = px - origin.X, dy = py - origin.Y, dz = pz - origin.Z;
                double t = dx * dir.X + dy * dir.Y + dz * dir.Z;   // dir is normalized
                if (t < 0 || t > maxDist) continue;
                double ox = dx - dir.X * t, oy = dy - dir.Y * t, oz = dz - dir.Z * t;
                if (ox * ox + oy * oy + oz * oz <= radius * radius && t < hitT) hitT = t;
            }
            return !double.IsPositiveInfinity(hitT);
        }

        // Despawn (tuned so natural spawns don't instantly vanish): instant despawn
        // beyond 128 blocks; between 64 and 128 blocks, despawn after 600 idle ticks. The natural
        // spawn ring is 24-32 blocks, so mobs that wander a little don't cross the idle threshold.
        private static double DistSq(Point3D a, Point3D b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return dx * dx + dy * dy + dz * dz;
        }

        private bool ShouldDespawn(MobEntity mob, Point3D playerPosition)
        {
            // Boats are player property, not spawns. They must survive being parked for days, so
            // they ignore both distance and idle despawn; they come back with the world instead.
            if (mob is BoatEntity) return false;

            // Horizontal-only, mirroring the spawn gate and the 48-block restore radius: a mob
            // deep below the player is only as far away as its horizontal distance. Vertical
            // separation alone must never despawn cave mobs (it would undo the spawn gate fix).
            double dx = mob.Position.X - playerPosition.X;
            double dz = mob.Position.Z - playerPosition.Z;
            double distSq = dx * dx + dz * dz;
            if (distSq > 128.0 * 128.0) return true;
            if (distSq > 64.0 * 64.0)
            {
                _idleTimeAccum[mob] = _idleTimeAccum.TryGetValue(mob, out var t) ? t + 1 : 1;
                if (_idleTimeAccum[mob] > 600 && _rand.Next(800) == 0) return true;
            }
            else
            {
                _idleTimeAccum[mob] = 0;
            }
            return false;
        }

        private readonly Dictionary<IMobRenderable, int> _idleTimeAccum = new();

        // ---- Per-chunk mob persistence (world streaming) ----
        // Mobs whose chunk is not currently loaded (or that despawned by distance) are detached
        // here instead of being deleted: state is snapshotted and they come back when the player
        // returns to their chunk. Keeps them from falling through unloaded terrain and from
        // permanently vanishing when the player leaves the area.
        private readonly Dictionary<ChunkCoordinates, List<SavedMob>> _detachedMobs = new();
        private readonly Queue<ChunkCoordinates> _detachedOrder = new();
        private const int MaxDetachedMobs = 512;    // bound memory when roaming a large world
        // Re-activate a saved mob only when the player is genuinely close. Must be BELOW the
        // distance-despawn band (64-128 blocks) or a mob despawned there would be restored on the
        // next frame and immediately re-despawned, flickering forever.
        private const double RestoreRadiusBlocks = 48.0;

        public bool TryAttackMob(Point3D cameraPosition, Point3D forward,
            double blockDistance, bool creative, out MobEntity? brokenVehicle)
        {
            brokenVehicle = null;
            var mob = TryPickMob(cameraPosition, forward, out double mobDistance);
            if (mob == null) return false;

            if (mobDistance > blockDistance + 0.02) return false;

            if (mob is MobEntity mobEntity)
            {
                if (mobEntity is CartEntity or BoatEntity)
                {
                    // Vehicles use discrete successful melee hits, not MobEntity's hurt cooldown:
                    // a rejected generic damage event must never consume hit progress.
                    if (!mobEntity.RegisterVehicleMeleeHit(creative ? 1 : 3)) return false;
                    if (mobEntity.Removed)
                    {
                        if (mobEntity is CartEntity cart) SeverCartLink(cart);
                        _idleTimeAccum.Remove(mobEntity);
                        _mobs.Remove(mobEntity);
                        if (ReferenceEquals(LocallyDrivenMob, mobEntity)) LocallyDrivenMob = null;
                        brokenVehicle = mobEntity;
                    }
                    return true;
                }

                // Ordinary mobs retain the existing damage / invulnerability behavior.
                mobEntity.Damage(1, cameraPosition.X, cameraPosition.Z, true);
            }
            return true;
        }

        private IMobRenderable? TryPickMob(Point3D origin, Point3D direction, out double hitDistance)
        {
            hitDistance = double.PositiveInfinity;
            IMobRenderable? best = null;
            var dir = direction.Normalized();

            foreach (var mob in _mobs)
            {
                if (mob.IsDead || mob is MobEntity { Removed: true }) continue;

                // Every mob is a MobEntity now - one universal dimension source.
                if (mob is not MobEntity mobEntity) continue;

                float width = mobEntity.Width;
                float height = mobEntity.Height;

                float half = width * 0.5f;
                double minX = mob.Position.X - half;
                double maxX = mob.Position.X + half;
                double minY = mob.Position.Y;
                double maxY = mob.Position.Y + height;
                double minZ = mob.Position.Z - half;
                double maxZ = mob.Position.Z + half;

                if (RayBox(origin, dir, minX, minY, minZ, maxX, maxY, maxZ, out double t)
                    && t <= BlockReach && t < hitDistance)
                {
                    hitDistance = t;
                    best = mob;
                }
            }

            return best;
        }

        /// <summary>
        /// Tests the ray against every living mob AABB and returns the nearest hit distance.
        /// Used by the block-pick ray so a mob's hitbox stops the ray: the player can never
        /// break or place through a living mob (no more accidental wall-damage while fighting).
        /// AABB math matches TryPickMob exactly (Position.Y = feet).
        /// </summary>
        public bool TryRaycastMobs(Point3D origin, Point3D direction, double maxDistance, out double hitDistance)
        {
            hitDistance = double.PositiveInfinity;
            var dir = direction.Normalized();

            foreach (var mob in _mobs)
            {
                if (mob.IsDead || mob is MobEntity { Removed: true }) continue;
                if (mob is not MobEntity mobEntity) continue;

                float half = mobEntity.Width * 0.5f;
                double minX = mob.Position.X - half;
                double maxX = mob.Position.X + half;
                double minY = mob.Position.Y;
                double maxY = mob.Position.Y + mobEntity.Height;
                double minZ = mob.Position.Z - half;
                double maxZ = mob.Position.Z + half;

                if (RayBox(origin, dir, minX, minY, minZ, maxX, maxY, maxZ, out double t)
                    && t <= maxDistance && t < hitDistance)
                {
                    hitDistance = t;
                }
            }

            return hitDistance <= maxDistance;
        }

        private static bool RayBox(
            Point3D origin, Point3D dir,
            double minX, double minY, double minZ,
            double maxX, double maxY, double maxZ,
            out double tEntry)
        {
            tEntry = 0;
            double tMin = double.NegativeInfinity;
            double tMax = double.PositiveInfinity;

            for (int axis = 0; axis < 3; axis++)
            {
                double o = axis == 0 ? origin.X : (axis == 1 ? origin.Y : origin.Z);
                double d = axis == 0 ? dir.X : (axis == 1 ? dir.Y : dir.Z);
                double lo = axis == 0 ? minX : (axis == 1 ? minY : minZ);
                double hi = axis == 0 ? maxX : (axis == 1 ? maxY : maxZ);

                if (Math.Abs(d) < 1e-9)
                {
                    if (o < lo || o > hi) return false;
                }
                else
                {
                    double t1 = (lo - o) / d;
                    double t2 = (hi - o) / d;
                    if (t1 > t2) (t1, t2) = (t2, t1);
                    if (t1 > tMin) tMin = t1;
                    if (t2 < tMax) tMax = t2;
                    if (tMin > tMax) return false;
                }
            }

            if (tMax < 0) return false;
            tEntry = tMin < 0 ? 0 : tMin;
            return true;
        }

        public void Clear()
        {
            _mobs.Clear();
            _mobRenderData.Clear();
        }

        // Serializes the current mob state for a world save. Includes detached (chunk-unloaded)
        // mobs so roaming mobs survive a save/quit too.
        public List<SavedMob> SaveMobs()
        {
            var result = new List<SavedMob>();
            foreach (var mob in _mobs)
            {
                if (mob is MobEntity { Removed: true }) continue;
                if (mob is MobEntity { IsDead: true } && (mob is CartEntity or BoatEntity)) continue;
                result.Add(SnapshotMob(mob));
            }
            foreach (var list in _detachedMobs.Values)
            {
                result.AddRange(list);
            }
            return result;
        }

        private static SavedMob SnapshotMob(IMobRenderable mob)
        {
            string type = mob switch
            {
                Coyote => "coyote",
                SteveMob => "steve",
                GenericMobEntity g => g.MobId,
                MobEntity me => me.MobTypeName,
                _ => "duck",
            };
            int health = mob is MobEntity me2 ? me2.Health : 10;
            bool brute = mob is GenericMobEntity g2 && g2.IsBrute;
            return new SavedMob
            {
                Type = type, X = mob.Position.X, Y = mob.Position.Y, Z = mob.Position.Z,
                Yaw = mob.Yaw, Health = health, Brute = brute,
                VehicleMeleeHitCount = mob is MobEntity me3 ? me3.VehicleMeleeHitCount : 0,
            };
        }

        // Restores mobs from a world save.
        public void LoadMobs(IEnumerable<SavedMob> mobs)
        {
            _mobs.Clear();
            _detachedMobs.Clear();
            _detachedOrder.Clear();
            foreach (var m in mobs)
            {
                SpawnSavedMob(m);
            }
        }

        // ---- World-streaming mob persistence ----

        // The chunk a world position lives in (layer from Y, column from X/Z).
        private static ChunkCoordinates ChunkOf(Point3D pos)
        {
            int layer = ChunkManager.LayerForWorldY((int)Math.Floor(pos.Y));
            return new ChunkCoordinates(layer, GameWorld.WorldToChunkCoord(pos.X), GameWorld.WorldToChunkCoord(pos.Z));
        }

        // Snapshots a mob into the detached pool for its chunk (bounded so roaming a large world
        // can't grow memory without limit).
        private void DetachMob(MobEntity mob)
        {
            _idleTimeAccum.Remove(mob); // stale timers would leak one entry per despawned mob
            var cc = ChunkOf(mob.Position);
            if (!_detachedMobs.TryGetValue(cc, out var list))
            {
                list = new List<SavedMob>();
                _detachedMobs[cc] = list;
                _detachedOrder.Enqueue(cc);
            }
            list.Add(SnapshotMob(mob));

            if (_detachedMobs.Count > MaxDetachedMobs)
            {
                while (_detachedMobs.Count > MaxDetachedMobs && _detachedOrder.Count > 0)
                {
                    _detachedMobs.Remove(_detachedOrder.Dequeue());
                }
            }
        }

        // Runs every frame BEFORE mob physics: detaches mobs standing in unloaded chunks (they'd
        // fall through the void otherwise) and re-activates saved mobs whose chunk is loaded again
        // and the player is close enough to simulate.
        private void SyncDetachedMobs(Point3D playerPosition)
        {
            // Detach any active mob whose chunk is no longer loaded.
            for (int i = _mobs.Count - 1; i >= 0; i--)
            {
                if (_mobs[i] is not MobEntity me) continue;
                if (!_chunkManager.TryGetLoadedChunk(ChunkOf(me.Position), out _))
                {
                    DetachMob(me);
                    _mobs.RemoveAt(i);
                }
            }

            // Restore detached mobs for chunks that are loaded again, when within restore range.
            if (_detachedMobs.Count == 0) return;
            var loadedKeys = new List<ChunkCoordinates>();
            foreach (var key in _detachedMobs.Keys)
            {
                if (_chunkManager.TryGetLoadedChunk(key, out _)) loadedKeys.Add(key);
            }
            foreach (var key in loadedKeys)
            {
                var list = _detachedMobs[key];
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var m = list[i];
                    double dx = m.X - playerPosition.X;
                    double dz = m.Z - playerPosition.Z;
                    if (dx * dx + dz * dz > RestoreRadiusBlocks * RestoreRadiusBlocks) continue;

                    // Caps are honored on the way back in: restore only when the owning
                    // category's budgets have room, otherwise keep the snapshot and retry on a
                    // later frame. Kills the population spike when returning to a dense area.
                    if (_typeCategory.TryGetValue(m.Type, out var cat))
                    {
                        if (CountMobsOfCategory(cat) >= cat.MaxTotal) continue;
                        if (CountMobsOfType(m.Type) >= cat.MaxPerType) continue;
                        if (CountMobsInChunkColumn(key.X, key.Z, cat) >= cat.MaxPerChunk) continue;
                    }

                    SpawnSavedMob(m);
                    list.RemoveAt(i);
                }
                if (list.Count == 0) _detachedMobs.Remove(key);
            }

            // The eviction queue can accumulate stale entries as chunks repeatedly load/unload;
            // rebuild it from the live keys when it grows well past the actual map size.
            if (_detachedMobs.Count > 0 && _detachedOrder.Count > _detachedMobs.Count * 2)
            {
                _detachedOrder.Clear();
                foreach (var key in _detachedMobs.Keys) _detachedOrder.Enqueue(key);
            }
        }

        private void SpawnSavedMob(SavedMob m)
        {
            var pos = new Point3D(m.X, m.Y, m.Z);
            if (m.Type == "duck")
            {
                var mob = new Duck(pos, m.Yaw);
                mob.RestoreState(pos, m.Yaw, m.Health, m.VehicleMeleeHitCount);
                _mobs.Add(mob);
            }
            else if (m.Type == "coyote" || m.Type == "coyotemob")
            {
                var mob = new Coyote(pos, m.Yaw);
                mob.RestoreState(pos, m.Yaw, m.Health, m.VehicleMeleeHitCount);
                _mobs.Add(mob);
            }
            else if (m.Type == "steve")
            {
                var mob = new SteveMob(pos, m.Yaw);
                mob.RestoreState(pos, m.Yaw, m.Health, m.VehicleMeleeHitCount);
                _mobs.Add(mob);
            }
            else if (m.Type == "cart")
            {
                var cart = new CartEntity(pos, m.Yaw);
                cart.RestoreState(pos, m.Yaw, m.Health, m.VehicleMeleeHitCount);
                _mobs.Add(cart);
            }
            else if (m.Type == "orecart")
            {
                // Explicit branch for the same reason as the boat below: without it an ore cart
                // would fall through to the generic path and be rebuilt as an AI mob.
                var cart = new OreCartEntity(pos, m.Yaw);
                cart.RestoreState(pos, m.Yaw, m.Health, m.VehicleMeleeHitCount);
                _mobs.Add(cart);
            }
            else if (m.Type == "boat")
            {
                // Boats have no MobRegistry entry, so without this branch they would fall
                // through to the generic path below and be silently dropped on load.
                var boat = new BoatEntity(pos, m.Yaw);
                boat.RestoreState(pos, m.Yaw, m.Health, m.VehicleMeleeHitCount);
                _mobs.Add(boat);
            }
            else if (m.Type == "locomotivehot" || m.Type == "locomotivecold")
            {
                // Same reasoning as the ore cart above: a locomotive is a built-in vehicle with no
                // MobRegistry entry, so the generic fallback would rebuild it as an AI mob.
                var loco = new LocomotiveEntity(pos, m.Yaw, m.Type);
                loco.RestoreState(pos, m.Yaw, m.Health, m.VehicleMeleeHitCount);
                _mobs.Add(loco);
            }
            else
            {
                var def = MobRegistry.Get(m.Type);
                if (def == null) return;
                var generic = new GenericMobEntity(def, pos, m.Yaw, m.Brute);
                generic.RestoreState(pos, m.Yaw, m.Health, m.VehicleMeleeHitCount);
                _mobs.Add(generic);
            }
        }

        public void Dispose() { }
    }

    /// <summary>
    /// Generic mob entity that uses MobDefinition properties and loads models from MobRegistry.
    /// This allows any mob to be spawned just by having files in the MobEntities folder.
    /// </summary>
    public class GenericMobEntity : MobEntity, IMobRenderable
    {
        private readonly MobDefinition _definition;
        private MobModel? _model;

        /// <summary>True for the rare "brute" variant (1 in 50 zombies): 2x size, half speed,
        /// double health. The model renders at <see cref="ScaleOverride"/> instead of the
        /// definition's scale so the visual doubles with the collision box.</summary>
        public bool IsBrute { get; }

        /// <summary>Model scale override for the brute variant (definition.Scale * 2), applied
        /// when the model loads. 0 = use the definition's scale.</summary>
        public float ScaleOverride { get; }

        public GenericMobEntity(MobDefinition definition, Point3D position, float yaw, bool brute = false) : base(position, yaw)
        {
            _definition = definition ?? throw new ArgumentNullException(nameof(definition));
            IsBrute = brute;
            Width = definition.Width;
            Height = definition.Height;
            MaxHealth = definition.MaxHealth;
            Health = MaxHealth;
            Hostile = definition.Hostile;
            AttackDamage = definition.AttackDamage;
            AggroRange = definition.AggroRange;
            AttackRange = definition.AttackRange;
            AttackCooldown = definition.AttackCooldown;
            if (brute)
            {
                Width *= 2f;
                Height *= 2f;
                MaxHealth *= 2;
                Health = MaxHealth;
                MaxSpeed *= 0.5f;               // half their current effective speed
                ScaleOverride = definition.Scale * 2f;
            }
        }

        public string MobId => _definition.Id;

        // The renderer routes render data to the per-type MobModel entry by this name, so it MUST
        // be the registry id (e.g. "zombie"), not the class name "genericmobentity".
        public override string MobTypeName => _definition.Id;

        // The renderer's shared per-type model is baked at the definition's scale, so the brute's
        // extra size is expressed as a per-instance multiplier (2x for a brute, 1x otherwise).
        float IMobRenderable.RenderScale =>
            ScaleOverride > 0f ? ScaleOverride / (_definition.Scale > 0f ? _definition.Scale : 1f) : 1f;

        // Zombie sunburn: in daytime, if the sky is visible above and the brightness is high
        // enough, the zombie periodically catches fire.
        private float _burnAccumulator;

        protected override void UpdateEnvironment(float dt, ChunkManager manager)
        {
            if (!_definition.BurnsInDaylight) return;
            if (_dead) return;
            if (SkylightSource == null) return;

            int skylight = SkylightSource();
            if (skylight >= 4) return; // not daytime (night-dim 0..3 = daylight)

            // Nothing opaque above within the probe range (sky visible).
            for (int wy = (int)Math.Floor(Position.Y) + 1; wy <= (int)Math.Floor(Position.Y) + 8; wy++)
            {
                int id = manager.GetBlockAt((int)Math.Floor(Position.X), wy, (int)Math.Floor(Position.Z));
                if (id != BlockRegistry.AirId && BlockRegistry.IsOpaque(id)) return;
            }

            _burnAccumulator += dt;
            // Fire chance scales with how bright the daylight is (roughly 1/20s at noon).
            float brightness = Math.Max(0f, (15f - skylight) / 15f);
            float chancePerSecond = ((brightness - 0.4f) * 2f) / 30f;
            if (_burnAccumulator > 1f / Math.Max(0.001f, chancePerSecond))
            {
                _burnAccumulator = 0f;
                Damage(1, Position.X, Position.Z, false);
            }
        }

        public bool LoadModel(GraphicsDevice graphicsDevice)
        {
            // The model needs a live GraphicsDevice to create GPU buffers, so it is built here
            // (lazily) rather than in the constructor - constructing with null used to NRE inside
            // CreateBuffers, get swallowed by the catch, and leave every GLB mob model-less.
            if (graphicsDevice == null) return false;
            _model = new MobModel(graphicsDevice)
            {
                ModelScale = ScaleOverride > 0f ? ScaleOverride : (_definition.Scale > 0f ? _definition.Scale : 1.0f),
                YawCorrection = _definition.YawCorrection,
            };
            _model.Load(_definition.ModelPath, _definition.TexturePath);
            return _model.Loaded;
        }
    }
}
