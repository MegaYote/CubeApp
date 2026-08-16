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
        private void StartNewWorld(int seed, string name, GameMode mode = GameMode.Creative)
        {
            World?.Dispose();
            World = new GameWorld(seed, name, () => gpuRenderer, ChunkRenderRadius, Math.Max(1, Environment.ProcessorCount - 2));
            World.Mode = mode;
            // Creative starts flying; survival starts grounded with an empty hotbar (mine to earn).
            World.FlyMode = mode == GameMode.Creative;
            if (mode == GameMode.Survival)
            {
                for (int i = 0; i < GameWorld.HotbarSlots; i++) World.Hotbar[i] = BlockRegistry.AirId;
                World.SelectedBlock = BlockRegistry.AirId;
                World.SelectedSlot = 0;
            }
            World.ChunkGenerated += OnChunkGenerated;
            World.ChunkUnloaded += OnChunkUnloaded;
            if (gpuRenderer != null)
            {
                gpuRenderer.SetChunkManager(World.Chunks);
                gpuRenderer.SetWorldSeed(World.Seed);
                gpuRenderer.ResetWorld();
            }
            // Pre-generate + mesh a radius around spawn before entering Play so there's no pop-in.
            BeginWorldLoad();
        }

        // Begins the staged world-load. The main loop calls UpdateLoading while screen == Loading;
        // when the target radius is generated AND meshed AND uploaded, it flips to Playing.
        private void BeginWorldLoad()
        {
            _loadPhase = 0;
            _loadPhaseStart = 0f;
            _loadSkipSpawn = false;
            _loadTargetSet.Clear();
            _loadMeshedSet.Clear();
            _loadLastMeshedCount = 0;
            _loadMeshedCount = 0;
            _loadGroundRequested = 0;
            // Prepare a full render-distance radius (the default "Far") so the spawn view is done.
            _loadTargetRadius = ChunkRenderRadius;
            // Ground chunks in a circle of that radius: ~pi*r^2.
            _loadGroundTotal = 0;
            for (int dz = -_loadTargetRadius; dz <= _loadTargetRadius; dz++)
            {
                for (int dx = -_loadTargetRadius; dx <= _loadTargetRadius; dx++)
                {
                    if ((long)dx * dx + (long)dz * dz > (long)_loadTargetRadius * _loadTargetRadius) continue;
                    _loadGroundTotal++;
                }
            }

            // Phase 0: pick the spawn point first (generates a tiny ring of ground chunks).
            menu.LoadingPhase = "Preparing spawn";
            menu.LoadingPhaseProgress = 0f;
            menu.LoadingTotalProgress = 0f;
            screen = GameScreen.Loading;
            menu.Screen = GameScreen.Loading;
            DisableMouseLook();
        }

        // Advances the world-load staged pipeline. Runs every frame while screen == Loading.
        private void UpdateLoading(float deltaSeconds)
        {
            // If a world was requested from the menu this frame, paint the loading screen NOW
            // (so the player sees instant feedback) and defer the heavy construction to the
            // next frame. This eliminates the dead-frame freeze between click and first paint.
            if (!string.IsNullOrEmpty(_pendingName))
            {
                if (!_loadingScreenShown)
                {
                    menu.LoadingPhase = "Loading...";
                    menu.LoadingPhaseProgress = 0f;
                    menu.LoadingTotalProgress = 0f;
                    _loadingScreenShown = true;
                    return; // next frame does the work, this frame just paints "Loading..."
                }
                if (_pendingWorldFromSave && _pendingWorldSave != null)
                {
                    LoadWorld(_pendingWorldSave);
                }
                else
                {
                    StartNewWorld(_pendingSeed, _pendingName, _pendingMode);
                }
                _pendingName = "";
                _pendingWorldSave = null;
                _loadingScreenShown = false;
                return; // StartNewWorld / LoadWorld called BeginWorldLoad, normal pipeline resumes next frame
            }

            if (World == null) { FinishLoading(); return; }
            _loadPhaseStart += deltaSeconds;

            switch (_loadPhase)
            {
                case 0:
                    // Prepare spawn: pick the world's default spawn (random safe grass/sand spot near
                    // the origin) then place the camera there. When loading a save the position was
                    // already restored - just use it (SpawnPoint stays null, respawn falls back to
                    // the restored position's search).
                    if (!_loadSkipSpawn)
                    {
                        World.SelectWorldSpawn();
                        World.PlaceCameraAtSafeSpawn();
                    }
                    _lastMeshPosition = World.PlayerPosition;
                    _loadPhase = 1;
                    break;

                case 1:
                    // Generate terrain: request ground chunks in growing rings (workers generate
                    // them off-thread). Phase progress = rings completed / target radius.
                    {
                        int cx = GameWorld.WorldToChunkCoord(World.PlayerPosition.X);
                        int cz = GameWorld.WorldToChunkCoord(World.PlayerPosition.Z);
                        int ring = Math.Min(_loadTargetRadius, 1 + (int)(_loadPhaseStart * 4.0)); // ~4 rings/sec
                        World.Chunks.RequestChunksAround(cx, cz, ring, World.PlayerPosition, ChunkManager.GroundLayer);
                        _loadGroundRequested = CountGroundChunksInRadius(cx, cz, ring);
                        menu.LoadingPhase = "Generating terrain";
                        menu.LoadingPhaseProgress = Math.Clamp(ring / (float)_loadTargetRadius, 0f, 1f);
                        menu.LoadingTotalProgress = 0.15f * menu.LoadingPhaseProgress;
                        if (ring >= _loadTargetRadius) _loadPhase = 2;
                    }
                    break;

                case 2:
                    // Mesh chunks: wait until every ground chunk in the target radius is meshed.
                    // Precompute the expected set once (spawn chunk coords), then check each is
                    // loaded + meshed. Robust to chunk counts not matching a radius formula.
                    {
                        int cx = GameWorld.WorldToChunkCoord(World.PlayerPosition.X);
                        int cz = GameWorld.WorldToChunkCoord(World.PlayerPosition.Z);
                        if (_loadTargetSet.Count == 0)
                        {
                            for (int dz = -_loadTargetRadius; dz <= _loadTargetRadius; dz++)
                            {
                                for (int dx = -_loadTargetRadius; dx <= _loadTargetRadius; dx++)
                                {
                                    if ((long)dx * dx + (long)dz * dz > (long)_loadTargetRadius * _loadTargetRadius) continue;
                                    _loadTargetSet.Add(new ChunkCoordinates(ChunkManager.GroundLayer, cx + dx, cz + dz));
                                }
                            }
                            _loadGroundTotal = _loadTargetSet.Count;
                        }

                        World.Chunks.RequestChunksAround(cx, cz, _loadTargetRadius, World.PlayerPosition, ChunkManager.GroundLayer);
                        World.Mesher.Update();
                        gpuRenderer?.ProcessPendingPriorityMeshes();

                        _loadMeshedCount = 0;
                        foreach (var key in _loadTargetSet)
                        {
                            if (World.Chunks.TryGetLoadedChunk(key, out var ch)
                                && !ch.NeedsRemesh && !ch.IsMeshingQueued)
                            {
                                _loadMeshedSet.Add(key);
                            }
                        }
                        _loadMeshedCount = _loadMeshedSet.Count;
                        menu.LoadingPhase = "Meshing chunks";
                        menu.LoadingPhaseProgress = Math.Clamp(_loadMeshedCount / (float)_loadGroundTotal, 0f, 1f);
                        // 0.15 (generating) + 0.85 (meshing) -> reaches exactly 1.0 when done.
                        menu.LoadingTotalProgress = 0.15f + 0.85f * menu.LoadingPhaseProgress;
                        // Move on when all target chunks are meshed OR meshing has stalled (no new
                        // chunk meshed for a couple seconds) - the latter handles edge chunks that
                        // never produce a mesh (e.g. fully air columns) and won't flip their flags.
                        if (_loadMeshedCount >= _loadGroundTotal)
                        {
                            _loadPhaseStart = 0f; // fresh timer for the finishing grace period
                            _loadPhase = 3;
                        }
                        else if (_loadMeshedCount == _loadLastMeshedCount && _loadPhaseStart >= 2.0f)
                        {
                            _loadPhaseStart = 0f;
                            _loadPhase = 3;
                        }
                        else if (_loadMeshedCount != _loadLastMeshedCount)
                        {
                            _loadPhaseStart = 0f; // progress made: reset the stall timer
                        }
                        _loadLastMeshedCount = _loadMeshedCount;
                    }
                    break;

                case 3:
                    // Finishing: give uploads a moment to land, then start playing. A short fixed
                    // grace time (instead of waiting for pendingUploads == 0, which can stall if a
                    // re-mesh keeps queuing) - a few frames is plenty since all chunks are meshed.
                    World.Mesher.Update();
                    gpuRenderer?.ProcessPendingPriorityMeshes();
                    menu.LoadingPhase = "Finishing";
                    menu.LoadingPhaseProgress = 1f;
                    menu.LoadingTotalProgress = 1f;
                    if (_loadPhaseStart >= 0.5f) FinishLoading();
                    break;
            }
        }

        private void FinishLoading()
        {
            screen = GameScreen.Playing;
            menu.Screen = GameScreen.Playing;
            _ignoreInteractFrames = 2;
            EnableMouseLook();
            menu.LoadingPhase = "";
        }

        private static int CountGroundChunksInRadius(int cx, int cz, int radius)
        {
            int n = 0;
            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if ((long)dx * dx + (long)dz * dz > (long)radius * radius) continue;
                    n++;
                }
            }
            return n;
        }

        private void OnChunkGenerated() => needsMeshUpdate = true;

        private void OnChunkUnloaded(ChunkCoordinates coords) => gpuRenderer?.RemoveChunk(coords);

        private void ResumeToPlaying()
        {
            screen = GameScreen.Playing;
            menu.Screen = GameScreen.Playing;
            _ignoreInteractFrames = 2;
            EnableMouseLook();
        }

        /// <summary>Teleports the player back to the world spawn, refills health, and resumes.</summary>
        private void RespawnPlayer()
        {
            if (World == null) return;
            World.PlaceCameraAtSafeSpawn();
            World.LocalPlayer.Health = 10;
            World.LocalPlayer.DeathCause = DeathCause.Unknown;
            World.LocalPlayer.DeathTimer = 0f;
            World.LocalPlayer.TimeSinceDamage = 0f;
            World.LocalPlayer.RegenAccumulator = 0f;
            ResumeToPlaying();
        }

        private void ReturnToTitle()
        {
            StopNetworking();
            screen = GameScreen.Title;
            menu.Screen = GameScreen.Title;
            RefreshSavedWorlds();
            DisableMouseLook();
        }

        private static int ParseSeed(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Random.Shared.Next(0, int.MaxValue);
            // Any typed string becomes a DETERMINISTIC seed (like Minecraft string seeds), so the
            // box can accept whatever characters you like. A plain number is used directly.
            if (int.TryParse(text.Trim(), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int n))
            {
                return n;
            }
            unchecked
            {
                int hash = 0;
                foreach (char c in text)
                {
                    hash = hash * 31 + c;
                }
                return hash;
            }
        }

        private void ProcessMenuActions()
        {
            // When the Settings screen opens (or reopens), seed the radio/slider values from the
            // current live state so they display what is actually active.
            if (menu.Screen == GameScreen.Settings && !_settingsWasOpen)
            {
                menu.SelectedCullingMode = gpuRenderer?.GetCullingMode() ?? menu.SelectedCullingMode;
                menu.SelectedRenderDistance = renderDistanceIndex;
                menu.SelectedMouseSensitivity = MouseSensitivity;
            }
            _settingsWasOpen = menu.Screen == GameScreen.Settings;

            if (menu.CreateWorldClicked)
            {
                // Show the loading screen immediately so the player sees feedback, then defer
                // the heavy world construction to UpdateLoading (next call in this frame).
                _pendingSeed = ParseSeed(menu.SeedInput);
                _pendingName = menu.WorldName;
                _pendingMode = menu.SelectedMode;
                _pendingWorldFromSave = false;
                screen = GameScreen.Loading;
                menu.Screen = GameScreen.Loading;
                DisableMouseLook();
            }
            else if (menu.LoadWorldClicked)
            {
                int index = menu.SelectedWorldIndex;
                if (index >= 0 && index < menu.SavedWorlds.Count)
                {
                    string name = menu.SavedWorlds[index];
                    string path = Path.Combine(SavesFolder, SanitizeFileName(name) + ".cubuild");
                    if (File.Exists(path))
                    {
                        var save = WorldSave.Load(path);
                        if (save != null)
                        {
                            _pendingWorldSave = save;
                            _pendingWorldFromSave = true;
                            screen = GameScreen.Loading;
                            menu.Screen = GameScreen.Loading;
                            DisableMouseLook();
                        }
                    }
                }
            }
            else if (menu.DeleteWorldClicked)
            {
                DeleteWorld(menu.DeleteWorldIndex);
            }
            else if (menu.RenameWorldClicked)
            {
                RenameWorld(menu.RenameWorldIndex, menu.RenameTarget);
            }
            else if (menu.HostGameClicked)
            {
                HostGame();
            }
            else if (menu.JoinGameClicked)
            {
                JoinGame();
            }
            else if (menu.MultiplayerBackClicked)
            {
                menu.Screen = GameScreen.Title;
            }
            else if (menu.OpenToLanClicked)
            {
                OpenToLan();
            }
            else if (menu.ResumeClicked)
            {
                ResumeToPlaying();
            }
            else if (menu.RespawnClicked)
            {
                RespawnPlayer();
            }
            else if (menu.QuitToTitleClicked)
            {
                SaveWorld();
                ReturnToTitle();
            }
            else if (menu.QuitClicked)
            {
                SaveWorld();
                window?.Close();
            }
            else if (menu.SettingsBackClicked)
            {
                // Leave settings: return to the screen we came from (Title or Paused).
                menu.Screen = menu.SettingsReturnTo;
                menu.SettingsOpen = false;
            }
            if (menu.CullingModeChanged)
            {
                gpuRenderer?.SetCullingMode(menu.SelectedCullingMode);
                _forceChunkStream = true;
                if (World != null)
                {
                    foreach (var c in World.Chunks.GetLoadedChunks())
                    {
                        c.NeedsRemesh = true;
                    }
                }
            }
            if (menu.RenderDistanceChanged)
            {
                renderDistanceIndex = Math.Clamp(menu.SelectedRenderDistance, 0, RenderDistances.Length - 1);
                gpuRenderer?.SetRenderDistance(ChunkRenderRadius);
                needsMeshUpdate = true;
                _forceChunkStream = true;
                if (World != null) World.ChunkRenderRadius = ChunkRenderRadius;
            }
            if (menu.MouseSensitivityChanged)
            {
                MouseSensitivity = Math.Clamp(menu.SelectedMouseSensitivity, 0.05f, 2.0f);
            }
            menu.ResetFlags();
        }

        // ---- multiplayer ----

        // Hosts a new world and opens a listener on the configured port. Friends connect by
        // joining the host's IP:port; the host's world is authoritative.
    }
}