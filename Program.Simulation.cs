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
        private void StepSimulation(TickInputState tickInput, float deltaSeconds, bool advanceRealTimeEffects = true)
        {
            // The world only pauses for the ESC pause menu (singleplayer). The death screen keeps
            // the simulation running - the environment, mobs and time all continue while dead.
            if (screen == GameScreen.Paused || screen != GameScreen.Playing && screen != GameScreen.Dead) return;
            if (World == null) return;
            // Menu overlays free the mouse; don't move, mine, or place while they're open.
            if (handEditorOpen || inventoryOpen || craftingOpen || smeltOpen || oreCartOpen || biomeMenuOpen || chatOpen)
            {
                tickInput = new TickInputState(false, false, false, false, false, false, false, false, false, Vector2.Zero,
                    tickInput.NoclipMode, tickInput.CreativeFlightMode);
            }
            UpdateNetworking(tickInput, deltaSeconds);
            World.StepSimulation(tickInput, deltaSeconds);
            // The kiln keeps cooking while its screen is open (it only runs while you're at it).
            if (smeltOpen) World.TickSmelter(deltaSeconds);
            if (advanceRealTimeEffects)
                AdvanceHandAnimationTimers(deltaSeconds);

            // Death: when health is fully depleted, stop the sim and show the respawn screen.
            if (World.LocalPlayer.Health <= 0)
            {
                chatOpen = false;
                _chatJustOpened = false;
                input.SetChatOpen(false);
                screen = GameScreen.Dead;
                menu.Screen = GameScreen.Dead;
                DisableMouseLook();
                return;
            }

            // Keep the discrete day/night value for gameplay light checks. Visual daylight is
            // scaled continuously by the renderer, so time changes do not dirty every chunk mesh.
            int sub = World.NightDimLevel(deltaSeconds);
            if (sub != _lastSkylightSubtracted)
            {
                _lastSkylightSubtracted = sub;
            }

            if (advanceRealTimeEffects)
                UpdateCaveAmbience(deltaSeconds);

            // Survival mining: progress accumulates while the left mouse is held.
            UpdateMining(tickInput, deltaSeconds);
            TryBeginBoundaryHandoff();
        }

        private void AdvanceHandAnimationTimers(float deltaSeconds)
        {
            if (_handPokeTimer > 0f) _handPokeTimer = Math.Max(0f, _handPokeTimer - deltaSeconds);
            if (_handSwingTimer > 0f) _handSwingTimer = Math.Max(0f, _handSwingTimer - deltaSeconds);
        }

        private void AdvanceRealTimeEffects(float realDeltaSeconds)
        {
            AdvanceHandAnimationTimers(realDeltaSeconds);
            UpdateCaveAmbience(realDeltaSeconds);
        }

        // Hold-to-mine: progress = delta / (BASE_BREAK_TIME * hardness / matching tool speed).
        // Tool speed and harvest level come from item data; the block's configured toolType
        // decides which family receives the speed bonus.
        private static readonly int _logBlockId = BlockRegistry.GetId("log");
        private static readonly int _plankBlockId = BlockRegistry.GetId("planks");
        private static readonly int _flintItemId = ItemRegistry.GetId("flint");
        private static readonly int _stoneBlockId = BlockRegistry.GetId("stone");
        private static readonly int _cobbleBlockId = BlockRegistry.GetId("cobblestone");
        private const float FlintLogSpeedMul = 1.05f; // flint's weak 5% log-mining bonus

        private void UpdateMining(TickInputState tickInput, float deltaSeconds)
        {
            if (World == null) return;
            if (!tickInput.PlaceHeld) _flintWorkbenchTapConsumed = false;

// Right-click CHOP modes (survival only): the hatchet strips logs/planks,
        // flint converts a log into a workbench, pickaxe converts stone to cobblestone.
        // Left-click always wins over chop.
        string selectedToolType = ItemRegistry.ToolTypeOf(World.SelectedBlock);
        bool hatchetHeld = string.Equals(selectedToolType, "axe", StringComparison.OrdinalIgnoreCase);
        bool flintHeld = World.SelectedBlock == _flintItemId;
        bool pickaxeHeld = string.Equals(selectedToolType, "pickaxe", StringComparison.OrdinalIgnoreCase);
        var chopKind = GameWorld.WoodChopKind.None;
        if (!World.IsCreative && tickInput.PlaceHeld && !tickInput.BreakHeld && !_flintWorkbenchTapConsumed)
        {
            if (hatchetHeld) chopKind = GameWorld.WoodChopKind.Hatchet;
            else if (flintHeld) chopKind = GameWorld.WoodChopKind.Flint;
            else if (pickaxeHeld) chopKind = GameWorld.WoodChopKind.Pickaxe;
        }
        bool miningHeld = tickInput.BreakHeld || chopKind != GameWorld.WoodChopKind.None;

// Track the break-button press edge ONCE PER FRAME, BEFORE the miningHeld gate.
//
// This must not live inside `if (miningHeld)`: on the release frame miningHeld is
// false, so the block would be skipped and the flag would never reset. It would
// latch true forever, every later press would look like a held button, and creative
// breaking would silently stop working for the rest of the session.
bool breakHeldNow = tickInput.BreakHeld;
bool freshBreakPress = breakHeldNow && !_wasCreativeBreakHeld;
_wasCreativeBreakHeld = breakHeldNow;

            if (miningHeld && _ignoreInteractFrames == 0)
            {
// Creative: breaks on the press edge AND repeats while the button is held, like
                    // vanilla Minecraft - but throttled to CreativeBreakInterval so it is
                    // controllable instead of machine-gunning.
                    //
                    // The press edge (freshBreakPress, computed above the gate) is what makes a
                    // fresh click break instantly regardless of the cooldown, so tapping is
                    // always responsive and holding gives a steady, aimable cadence.
                    if (World.IsCreative)
                    {
                        long nowMs = Environment.TickCount64;
                        long gapMs = (long)(CreativeBreakInterval * 1000f);

                        // A fresh press always breaks instantly; a held button repeats only
                        // once the interval has genuinely elapsed since the last break.
                        bool allowBreak = freshBreakPress || (breakHeldNow && nowMs - _lastCreativeBreakMs >= gapMs);

                        if (allowBreak)
                        {
                            var cpick = World.TryPickBlock(World.PlayerPosition, World.GetCameraForward());
                            if (cpick.HasValue)
                            {
                                var target = cpick.Value.Remove;
                                if (World.Chunks.TryGetLoadedBlock(target.x, target.y, target.z, out int id)
                                    && !float.IsPositiveInfinity(BlockRegistry.HardnessOf(id)))
                                {
                                    _lastCreativeBreakMs = nowMs;

                                    // Belt and braces: a failure mid-break must never escalate
                                    // into a cascade of blocks behind the one you aimed at.
                                    try
                                    {
                                        DeleteBlockAt(target.x, target.y, target.z);
                                    }
                                    catch (Exception ex)
                                    {
                                        try { File.AppendAllText("creative_break_error.log", ex + Environment.NewLine); } catch { }
                                    }
                                }
                            }
                            else if (!freshBreakPress)
                            {
                                // Holding at nothing: still consume the interval so we do not
                                // retry the ray every frame.
                                _lastCreativeBreakMs = nowMs;
                            }
                        }
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
                        bool validTarget = chopKind switch
                        {
                            GameWorld.WoodChopKind.Hatchet => chopProbe == _logBlockId || chopProbe == _plankBlockId,
                            GameWorld.WoodChopKind.Flint => chopProbe == _logBlockId,
                            GameWorld.WoodChopKind.Pickaxe => chopProbe == _stoneBlockId || chopProbe == _cobbleBlockId,
                            _ => false
                        };
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
                    // The block catalogue defines its preferred tool family; item data supplies
                    // the multiplier. This keeps new materials/tools from needing ID lists here.
                    if (chopKind == GameWorld.WoodChopKind.None)
                    {
                        string heldType = ItemRegistry.ToolTypeOf(World.SelectedBlock);
                        string blockType = BlockRegistry.ToolTypeOf(_miningBlockId);
                        if (!string.IsNullOrEmpty(heldType)
                            && string.Equals(heldType, blockType, StringComparison.OrdinalIgnoreCase))
                        {
                            breakTime /= ItemRegistry.MiningSpeedOf(World.SelectedBlock);
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
                    if (newStage > oldStage && _miningProgress < 1f && gpuRenderer != null)
                    {
                        var particleLight = SampleParticleLightAround(target.x, target.y, target.z);
                        gpuRenderer.SpawnBlockBreakParticles(target.x, target.y, target.z, _miningBlockId, 4,
                            particleLight.Sky, particleLight.Block);
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
            var particleLight = gpuRenderer != null ? SampleParticleLightAround(x, y, z) : (Sky: 0, Block: 0);
            if (!World.TryBreakBlockAt(x, y, z, out int removedBlockId, chop)) return false;
            // Creative removes the block in one instant, so MiningProgress never drives the
            // renderer's continuous mining swing. Survival also gets a short finishing swing
            // after the block disappears, rather than snapping back to idle on that frame.
            TriggerHandSwing();
            gpuRenderer?.SpawnBlockBreakParticles(x, y, z, removedBlockId, 12,
                particleLight.Sky, particleLight.Block);
            needsMeshUpdate = true;

            // The hardcoded grass-break one-shot is intentionally disabled; cave ambience and
            // the rest of the audio engine remain active.
            return true;
        }

        private static readonly (int X, int Y, int Z)[] ParticleLightSampleOffsets =
        {
            (0, 0, 0), (-1, 0, 0), (1, 0, 0), (0, -1, 0), (0, 1, 0), (0, 0, -1), (0, 0, 1)
        };

        // Capture the brightest raw light around the source before breaking invalidates its grid.
        private (int Sky, int Block) SampleParticleLightAround(int worldX, int worldY, int worldZ)
        {
            if (World == null) return (0, 0);

            int maxSky = 0;
            int maxBlock = 0;
            foreach (var offset in ParticleLightSampleOffsets)
            {
                int x = worldX + offset.X;
                int y = worldY + offset.Y;
                int z = worldZ + offset.Z;
                int layer = ChunkManager.LayerForWorldY(y);
                int chunkX = (int)Math.Floor(x / (double)ChunkManager.ChunkSize);
                int chunkZ = (int)Math.Floor(z / (double)ChunkManager.ChunkSize);
                bool hasCurrentLight = false;

                if (World.Chunks.TryGetLoadedChunk(new ChunkCoordinates(layer, chunkX, chunkZ), out var chunk)
                    && chunk.TryGetCurrentLightGrid(out var grid) && grid != null)
                {
                    int localX = x - chunk.OriginX;
                    int localY = y - chunk.OriginY;
                    int localZ = z - chunk.OriginZ;
                    if (localX >= 0 && localX < chunk.Width
                        && localY >= 0 && localY < chunk.Height
                        && localZ >= 0 && localZ < chunk.Depth)
                    {
                        int index = (localX * chunk.Depth + localZ) * chunk.Height + localY;
                        if (index < grid.Length)
                        {
                            byte packedLight = grid[index];
                            maxSky = Math.Max(maxSky, packedLight >> 4);
                            maxBlock = Math.Max(maxBlock, packedLight & 0x0F);
                            hasCurrentLight = true;
                        }
                    }
                }

                if (!hasCurrentLight)
                {
                    maxSky = Math.Max(maxSky, World.Chunks.GetSkyLightEstimate(x, y, z, 0));
                }
            }

            return (maxSky, maxBlock);
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
        // The old cached-region lighting (keyed on chunk X/Z but not the chunk LAYER) is gone;
        // see PlayerIsInCaveDarkness for why it leaked daylight cave noises while flying.
        private void UpdateCaveAmbience(float deltaSeconds)
        {
            if (Sound == null || World == null) return;
            // Keep the listener at the camera, then play positioned sounds.
            Sound.UpdateListener((float)World.PlayerPosition.X, (float)World.PlayerPosition.Y, (float)World.PlayerPosition.Z);
            Sound.Update();

            // Gate on being enclosed AND dark. Flying above the terrain sees the sky, so this
            // stays false regardless of the cached-region bug that used to leak through.
            if (!PlayerIsInCaveDarkness())
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

        /// <summary>
        /// True when the player is genuinely enclosed and dark enough for cave ambience.
        ///
        /// This used to sample a hand-rolled ChunkLighting region cached on (chunkX, chunkZ)
        /// only - but NOT on the chunk LAYER. Flying is vertical movement, so climbing
        /// crossed layers while (cx, cz) stayed identical and the cache kept serving a region
        /// belonging to a different Y layer, reading the wrong column's light. That is what
        /// let cave noises play in broad daylight while flying around above the terrain.
        ///
        /// ChunkManager already answers this correctly and cheaply: GetSkyLightEstimate walks
        /// the column upward and reports sky exposure, and GetSpawnLight falls back to the
        /// baked block-light grid when shaded. Both are already used by the mob spawner.
        /// </summary>
        private bool PlayerIsInCaveDarkness()
        {
            var pos = World.PlayerPosition;
            int bx = (int)Math.Floor(pos.X);
            int by = (int)Math.Floor(pos.Y);
            int bz = (int)Math.Floor(pos.Z);

            // Outdoors means "sees the sky", at any time of day. Passing 0 here is deliberate:
            // we only want to know whether the sky reaches this column, not how dim it is.
            // At night that returns 15 - nightLevel, which is still > 0, so open air at night
            // is excluded too - cave drips outside would be just as wrong as in daylight.
            if (World.Chunks.GetSkyLightEstimate(bx, by, bz, 0) > 0) return false;

            // Enclosed. Now it genuinely is a light question: torch-lit rooms and lit
            // passages stay quiet, only unlit darkness drips.
            return World.Chunks.GetSpawnLight(bx, by, bz, _lastSkylightSubtracted) < 7;
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
            // Right-click use dispatch: food is eaten (heals), bucket fills from water,
            // items with a block behavior place, everything else (tools, gems) does nothing yet.
            if (World.TryEatSelectedFood())
            {
                _handPokeTimer = 0.3f; // first-person hand does a quick jab
                return;
            }
            // Bucket fill: right-click water with an empty bucket -> removes water, gives water bucket.
            if (TryFillBucketFromWater())
            {
                needsMeshUpdate = true;
                _handPokeTimer = 0.3f;
                return;
            }
            if (World.TryPlaceSelectedBlock(World.LocalPlayer, World.PlayerPosition, World.GetCameraForward()))
            {
                needsMeshUpdate = true;
                // A successful place is the same deliberate punch as a successful break.
                // Use the shared one-shot swing so creative and survival place identically.
                _handPokeTimer = 0f;
                TriggerHandSwing();
            }
        }

        // Attempts to fill an empty bucket with water from the targeted block.
        // Returns true if the bucket was filled (water block removed, bucket -> water_bucket).
        private bool TryFillBucketFromWater()
        {
            int bucketId = ItemRegistry.GetId("bucket");
            int waterBucketId = ItemRegistry.GetId("water_bucket");
            int waterBlockId = BlockRegistry.GetId("water");
            if (World.SelectedBlock != bucketId) return false;

            // Use a dedicated fluid raycast that stops at water (unlike the normal
            // block raycast which passes through water for placement).
            var pick = World.TryPickFluid(World.PlayerPosition, World.GetCameraForward(), waterBlockId);
            if (!pick.HasValue) return false;

            // Remove the water block
            World.Chunks.TrySetBlock(pick.Value.Remove.x, pick.Value.Remove.y, pick.Value.Remove.z, BlockRegistry.AirId);

            // In survival, replace bucket with water_bucket. If the selected slot has count 0
            // (can happen if obtained in creative before the fix), seed it first.
            if (!World.IsCreative)
            {
                int selected = World.SelectedSlot;
                if (World.Hotbar[selected] == bucketId && World.HotbarCounts[selected] <= 0)
                    World.HotbarCounts[selected] = 1;
                World.TryConsumeFromInventory(bucketId, 1);
                World.TryAddToInventory(waterBucketId, 1);
            }
            return true;
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
