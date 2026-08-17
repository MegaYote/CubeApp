using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using Cubuild.Renderer;
using Cubuild.World;
using Veldrid;
using Veldrid.Sdl2;
using Veldrid.StartupUtilities;
using static Cubuild.ChunkManager;
using Cubuild;

namespace Cubuild
{
    public sealed partial class Program : IDisposable
    {
        private void StepSimulation(TickInputState tickInput, float deltaSeconds)
        {
            // The world only pauses for the ESC pause menu (singleplayer). The death screen keeps
            // the simulation running - the environment, mobs and time all continue while dead.
            if (screen == GameScreen.Paused || screen != GameScreen.Playing && screen != GameScreen.Dead) return;
            if (World == null) return;
            // Menu overlays free the mouse; don't move, mine, or place while they're open.
            if (handEditorOpen || inventoryOpen || craftingOpen || biomeMenuOpen)
            {
                tickInput = new TickInputState(false, false, false, false, false, false, false, false, false, Vector2.Zero);
            }
            UpdateNetworking(tickInput, deltaSeconds);
            World.StepSimulation(tickInput, deltaSeconds);
            if (_handPokeTimer > 0f) _handPokeTimer = Math.Max(0f, _handPokeTimer - deltaSeconds);

            // Death: when health is fully depleted, stop the sim and show the respawn screen.
            if (World.LocalPlayer.Health <= 0)
            {
                screen = GameScreen.Dead;
                menu.Screen = GameScreen.Dead;
                DisableMouseLook();
                return;
            }

            // Day/night: lower the daylight seed when the sun crosses into a new night-dim level,
            // then re-mesh so the flood fill bakes the dimmer light.
            int sub = World.NightDimLevel(deltaSeconds);
            if (sub != _lastSkylightSubtracted)
            {
                _lastSkylightSubtracted = sub;
                ChunkLighting.NightDimLevel = sub;
                foreach (var c in World.Chunks.GetLoadedChunks())
                {
                    c.NeedsRemesh = true;
                }
            }

            UpdateCaveAmbience(deltaSeconds);

            // Survival mining: progress accumulates while the left mouse is held.
            UpdateMining(tickInput, deltaSeconds);
        }

        // Cubuild C++ port: hold-to-mine. Progress = delta / (BASE_BREAK_TIME * hardness).
        // Switching target (or releasing) resets progress. Spawns shards every 20% and breaks the
        // block at 100%.
        // The flint hatchet: LEFT-click mines logs AND planks faster than before (which
        // itself was 15% over base, so ~32% total). Holding RIGHT-click on a log CHOPs it at
        // NORMAL speed (no bonus) and strips 1-4 planks on break; chopping a PLANK strips
        // it into 1-4 sticks instead. FLINT: left-click mining logs is 5% faster, and
        // right-clicking a log CHOPs it into a workbench - always, no chance roll.
        private static readonly int _hatchetItemId = ItemRegistry.GetId("flint_hatchet");
        private static readonly int _logBlockId = BlockRegistry.GetId("log");
        private static readonly int _plankBlockId = BlockRegistry.GetId("planks");
        private static readonly int _flintItemId = ItemRegistry.GetId("flint");
        private static readonly float _hatchetSpeedMul = 1.15f * 1.75f; // compounded: 15% faster than the previous 15%
        private const float FlintLogSpeedMul = 1.05f; // flint's weak 5% log-mining bonus

        private void UpdateMining(TickInputState tickInput, float deltaSeconds)
        {
            if (World == null) return;

            // Right-click CHOP modes (survival only): the hatchet strips logs/planks,
            // flint converts a log into a workbench. Left-click always wins over chop.
            bool hatchetHeld = World.SelectedBlock == _hatchetItemId;
            bool flintHeld = World.SelectedBlock == _flintItemId;
            var chopKind = GameWorld.WoodChopKind.None;
            if (!World.IsCreative && tickInput.PlaceHeld && !tickInput.BreakHeld)
            {
                if (hatchetHeld) chopKind = GameWorld.WoodChopKind.Hatchet;
                else if (flintHeld) chopKind = GameWorld.WoodChopKind.Flint;
            }
            bool miningHeld = tickInput.BreakHeld || chopKind != GameWorld.WoodChopKind.None;

            if (miningHeld && _ignoreInteractFrames == 0)
            {
                // Creative: near-instant breaking, but rate-limited so you can actually aim it
                // (a block every ~0.2s instead of one per frame). Bedrock-like (infinite-hardness)
                // blocks still can't be broken.
                if (World.IsCreative)
                {
                    if (_creativeBreakCooldown > 0f)
                    {
                        _creativeBreakCooldown -= deltaSeconds;
                        _miningTarget = null;
                        _miningProgress = 0f;
                        return;
                    }
                    var cpick = World.TryPickBlock(World.PlayerPosition, World.GetCameraForward());
                    if (cpick.HasValue)
                    {
                        var target = cpick.Value.Remove;
                        if (World.Chunks.TryGetLoadedBlock(target.x, target.y, target.z, out int id)
                            && !float.IsPositiveInfinity(BlockRegistry.HardnessOf(id)))
                        {
                            DeleteBlockAt(target.x, target.y, target.z);
                        }
                    }
                    _creativeBreakCooldown = CreativeBreakInterval;
                    _miningTarget = null;
                    _miningProgress = 0f;
                    return;
                }

                var pick = World.TryPickBlock(World.PlayerPosition, World.GetCameraForward());
                if (pick.HasValue)
                {
                    var target = pick.Value.Remove;
                    // Chop mode only strips wood: the hatchet accepts logs + planks, flint
                    // accepts only logs (it carves them into workbenches). Anything else
                    // under the crosshair is a no-op (no mining, no placement - tools never
                    // place anyway).
                    if (chopKind != GameWorld.WoodChopKind.None)
                    {
                        int chopProbe = 0;
                        bool probeOk = World.Chunks.TryGetLoadedBlock(target.x, target.y, target.z, out chopProbe);
                        bool validTarget = chopKind == GameWorld.WoodChopKind.Hatchet
                            ? chopProbe == _logBlockId || chopProbe == _plankBlockId
                            : chopProbe == _logBlockId;
                        if (!probeOk || !validTarget)
                        {
                            _miningTarget = null;
                            _miningProgress = 0f;
                            return;
                        }
                    }
                    bool sameTarget = _miningTarget.HasValue
                        && _miningTarget.Value.x == target.x
                        && _miningTarget.Value.y == target.y
                        && _miningTarget.Value.z == target.z;
                    if (!sameTarget)
                    {
                        // New target: reset progress.
                        _miningTarget = target;
                        _miningProgress = 0f;
                        _miningSlideDir = World.GetCameraForward();
                        if (World.Chunks.TryGetLoadedBlock(target.x, target.y, target.z, out int id))
                        {
                            _miningBlockId = id;
                            _miningBlockHardness = BlockRegistry.HardnessOf(id);
                        }
                        else
                        {
                            _miningBlockId = 0;
                            _miningBlockHardness = 1f;
                        }
                    }

                    if (float.IsPositiveInfinity(_miningBlockHardness))
                    {
                        return; // bedrock-like: unmineable
                    }

                    float breakTime = BaseBreakTime * _miningBlockHardness;
                    // Left-click speed bonuses (chops stay normal speed):
                    // hatchet: ~32% faster on logs + planks; flint: 5% faster on logs.
                    if (chopKind == GameWorld.WoodChopKind.None)
                    {
                        if (hatchetHeld && (_miningBlockId == _logBlockId || _miningBlockId == _plankBlockId))
                        {
                            breakTime /= _hatchetSpeedMul;
                        }
                        else if (flintHeld && _miningBlockId == _logBlockId)
                        {
                            breakTime /= FlintLogSpeedMul;
                        }
                    }
                    float oldProgress = _miningProgress;
                    _miningProgress += (float)(deltaSeconds / breakTime);

                    // Periodic shards while mining (every 20%).
                    int oldStage = (int)(oldProgress / BreakParticleInterval);
                    int newStage = (int)(_miningProgress / BreakParticleInterval);
                    if (newStage > oldStage && _miningProgress < 1f)
                    {
                        gpuRenderer?.SpawnBlockBreakParticles(target.x, target.y, target.z, _miningBlockId, 4);
                    }

                    if (_miningProgress >= 1f)
                    {
                        // Fully mined: break it (reuse the existing break path so particles,
                        // remesh and sound all fire). A chop passes the kind so the world
                        // strips the block (hatchet: planks/sticks; flint: workbench).
                        DeleteBlockAt(target.x, target.y, target.z, chopKind);
                        _miningTarget = null;
                        _miningProgress = 0f;
                    }
                }
                else
                {
                    _miningTarget = null;
                    _miningProgress = 0f;
                }
            }
            else
            {
                // Not holding (or interact locked): reset mining state.
                _miningTarget = null;
                _miningProgress = 0f;
            }
        }

        // Breaks the block at a world position (shared by mining completion). Returns true if a
        // block was removed. chop forwards to the world so a hatchet chop strips the log
        // into 1-4 planks (or planks into sticks), and a flint chop turns a log into a
        // workbench instead of the normal log drop.
        private bool DeleteBlockAt(int x, int y, int z, GameWorld.WoodChopKind chop = GameWorld.WoodChopKind.None)
        {
            if (World == null) return false;
            if (!World.TryBreakBlockAt(x, y, z, out int removedBlockId, chop)) return false;
            gpuRenderer?.SpawnBlockBreakParticles(x, y, z, removedBlockId, 12);
            needsMeshUpdate = true;

            // Only the sounds that exist are wired: grass.mp3 plays when a GRASS block breaks.
            if (Sound != null)
            {
                if (removedBlockId == BlockRegistry.GetId("grass") && Sound.HasSound("grass"))
                {
                    Sound.PlayAt("grass", x + 0.5f, y + 0.5f, z + 0.5f, 0.6f);
                }
            }
            return true;
        }

        // Plays a random ambient cave sound:
        //   - Trigger: the block light at the player's feet is < 7 (darkness, NOT depth). A lit
        //     cave or torch-lit tunnel makes no cave sounds; a dark one does.
        //   - Timing: a long, irregular mood-sound timer with a per-second probability roll, so
        //     sounds are rare and unpredictable - not a fixed 12-25s loop.
        //   - Position: the sound is placed at a RANDOM OFFSET near the player (a few blocks
        //     away), not AT the player - which is what made the old version read as "sounds
        //     follow me."
        private float _caveAmbienceTimer;
        private static readonly string[] CaveSoundNames =
        {
            "cavesound1", "cavesound2", "cavesound3", "cavesound4",
            "cavesound5", "cavesound6", "cavesound7",
        };
        // Cached lighting region around the player, rebuilt only on chunk-crossing (like the
        // mining-light cache) so the darkness gate never runs a full 3x3 flood fill per frame.
        private ChunkLighting? _caveLighting;
        private int _caveLightChunkX = int.MinValue;
        private int _caveLightChunkZ = int.MinValue;
        private void UpdateCaveAmbience(float deltaSeconds)
        {
            if (Sound == null || World == null) return;
            // Keep the listener at the camera, then play positioned sounds.
            Sound.UpdateListener((float)World.PlayerPosition.X, (float)World.PlayerPosition.Y, (float)World.PlayerPosition.Z);
            Sound.Update();

            // Gate on darkness, not depth. The block light at the player's feet must be < 7.
            if (!TryGetPlayerLight(out int light))
            {
                _caveAmbienceTimer = 0f;
                return;
            }
            if (light >= 7)
            {
                _caveAmbienceTimer = 0f;
                return;
            }

            // Random-offset position: a few blocks around the player so it doesn't track the ear.
            const float CaveSoundRadius = 6f;

            // Mood timer: count down in SECONDS; only roll the cave-sound chance when the
            // timer expires (once per second), NEVER per frame - a per-frame roll at 1/500 would
            // fire ~60x too often at 60fps.
            if (_caveAmbienceTimer > 0f)
            {
                _caveAmbienceTimer -= deltaSeconds;
                return;
            }

            // Roll ONCE per second at ~1/500 so the expected spacing while dark is ~8 minutes.
            if (Random.Shared.NextDouble() < 1.0 / 500.0)
            {
                string name = CaveSoundNames[Random.Shared.Next(CaveSoundNames.Length)];
                if (Sound.HasSound(name))
                {
                    float px = (float)World.PlayerPosition.X + (float)((Random.Shared.NextDouble() * 2.0 - 1.0) * CaveSoundRadius);
                    float py = (float)World.PlayerPosition.Y + (float)(Random.Shared.NextDouble() * 2.0);
                    float pz = (float)World.PlayerPosition.Z + (float)((Random.Shared.NextDouble() * 2.0 - 1.0) * CaveSoundRadius);
                    Sound.PlayAt(name, px, py, pz, 0.35f, SoundEngine.SoundCategory.Ambient);
                }
                // Re-roll interval after a hit: a short 1-6s wait, then the tiny per-second
                // chance dominates the spacing (so sounds stay rare, not clustered).
                _caveAmbienceTimer = 1f + (float)Random.Shared.NextDouble() * 5f;
            }
            else
            {
                // Failed roll: wait one full second before rolling again.
                _caveAmbienceTimer = 1f;
            }
        }

        // Samples the block light at the player's feet using a cached lighting region. Rebuilds
        // the ChunkLighting only when the player crosses into a new chunk (expensive otherwise).
        private bool TryGetPlayerLight(out int light)
        {
            light = 15;
            var pos = World.PlayerPosition;
            int bx = (int)Math.Floor(pos.X);
            int by = (int)Math.Floor(pos.Y);
            int bz = (int)Math.Floor(pos.Z);
            int layer = ChunkManager.LayerForWorldY(by);
            int cx = (int)Math.Floor(pos.X / (double)ChunkManager.ChunkSize);
            int cz = (int)Math.Floor(pos.Z / (double)ChunkManager.ChunkSize);

            if (_caveLighting == null || cx != _caveLightChunkX || cz != _caveLightChunkZ)
            {
                _caveLightChunkX = cx;
                _caveLightChunkZ = cz;
                var region = new Dictionary<ChunkCoordinates, Chunk>();
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        var key = new ChunkCoordinates(layer, cx + dx, cz + dz);
                        if (World.Chunks.TryGetLoadedChunk(key, out var c)) region[key] = c;
                    }
                }
                if (region.Count == 0) return false;
                try
                {
                    _caveLighting = new ChunkLighting(region, ChunkManager.ChunkSize, ChunkManager.HeightForLayer(layer));
                }
                catch
                {
                    return false;
                }
            }

            int ly = by - _caveLighting.OriginY;
            light = _caveLighting.GetLight(bx, ly, bz);
            return true;
        }

        private void ApplyLookInput(Vector2 lookDelta)
        {
            if (!mouseLook || lookDelta.X == 0f && lookDelta.Y == 0f) return;
            World?.ApplyLookInput(lookDelta);
        }

        // ------------------------------------------------------------------
        // block interaction (render-layer effects: particles + immediate meshes)
        // ------------------------------------------------------------------

        private void PlaceSelectedBlock()
        {
            if (World == null) return;
            // Right-click use dispatch: food is eaten (heals), items with a block behavior place,
            // everything else (tools, gems) does nothing yet.
            if (World.TryEatSelectedFood())
            {
                _handPokeTimer = 0.3f; // first-person hand does a quick jab
                return;
            }
            if (World.TryPlaceSelectedBlock(World.LocalPlayer, World.PlayerPosition, World.GetCameraForward()))
            {
                needsMeshUpdate = true;
                _handPokeTimer = 0.3f; // first-person hand does a quick jab
            }
        }

        // Sends local block edits to the host so they're applied authoritatively + echoed to all
        // clients. Subscribed to GameWorld.BlockEdited when connected as a client.
        private void OnLocalEdit(int x, int y, int z, int blockId, int meta)
        {
            _netClient?.SendBlockEdit(x, y, z, blockId, meta);
        }

        // Builds MobRenderData for remote players: from the host snapshot (as a client) or from
        // the host's own simulated RemotePlayers (as a host). Both directions get rendered.
    }
}