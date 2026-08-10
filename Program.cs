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
    /// <summary>
    /// Presentation layer: window, GPU, input, HUD and screen state. All simulation lives in
    /// <see cref="GameWorld"/> so the same world can run headless (dedicated server / network
    /// host). Program only orchestrates: it feeds input into the world and pushes world state to
    /// the renderer.
    /// </summary>
    public sealed class Program : IDisposable
    {
        private IRenderer? gpuRenderer;
        private Sdl2Window? window;
        private GraphicsDevice? graphicsDevice;
        private Point3D? _lastMeshPosition;
        private bool _forceChunkStream = true;
        private int _lastSkylightSubtracted = -1;
        private readonly InputProcessor input = new();
        private bool mouseLook;
        private volatile bool needsMeshUpdate = true;
        private string baseTitle = "Chunk Mesh Example";
        private bool showFps;
        private int frameCount;
        // Reusable per-frame entity render list (avoids one List allocation every frame).
        private readonly List<MobRenderData> _entityRenderScratch = new();

        // ---- world loading screen ----
        // Staged loader state: pre-generates + meshes a chunk radius around spawn so the player
        // drops into a fully ready world (no pop-in), showing a phase progress bar. The loader
        // runs in the main loop while screen == Loading, then flips to Playing.
        private int _loadTargetRadius;          // chunk radius to fully prepare
        private int _loadGroundRequested;       // ground chunks requested so far
        private int _loadGroundTotal;           // total ground chunks in the target radius
        private readonly HashSet<ChunkCoordinates> _loadTargetSet = new(); // exact chunk set to prepare
        private readonly HashSet<ChunkCoordinates> _loadMeshedSet = new(); // meshed+uploaded chunks
        private int _loadLastMeshedCount;       // for the meshing-stall detector
        private int _loadMeshedCount;
        private int _loadPhase;                 // 0 preparing, 1 generating, 2 meshing, 3 finishing
        private float _loadPhaseStart;
        private bool _loadSkipSpawn;            // true when loading a save (position already restored)

        private float lastFps;
        private readonly Stopwatch fpsStopwatch = new();
        private float lastUpdateMs;
        private float lastMeshMs;
        private float lastUploadMs;
        private float lastRenderMs;
        private readonly Stopwatch stageStopwatch = new();
        private const float MouseSensitivity = 0.5f;
        private const double MaxFrameDeltaSeconds = 0.25;
        private static readonly int[] RenderDistances = { 16, 8, 4, 2 };
        private static readonly string[] RenderDistanceNames = { "Far", "Normal", "Short", "Tiny" };
        private int renderDistanceIndex = 0; // Far by default
        private int ChunkRenderRadius => RenderDistances[renderDistanceIndex];
        private string RenderDistanceName => RenderDistanceNames[renderDistanceIndex];
        private int _ignoreInteractFrames; // skips break/place right after a menu click
        // ---- Survival mining (Cubuild C++ port) -------------------------------------
        // Hold left click to mine: progress accumulates toward breakTime = BASE_BREAK_TIME *
        // hardness, particles pop every 20%, and the block breaks at progress >= 1.
        private const float BaseBreakTime = 1.5f;    // seconds for hardness 1.0 (C++ BASE_BREAK_TIME)
        private const float BreakParticleInterval = 0.2f; // spawn shards every 20% progress
        private (int x, int y, int z)? _miningTarget;
        private float _miningProgress;
        private int _miningBlockId;
        private float _miningBlockHardness;
        // Camera ray direction captured once when mining starts (the line from the camera THROUGH
        // the mined block to the block behind it). The shrink cube slides along this direction so
        // it clamps toward the block behind the crosshair, not the hit face's normal.
        private Point3D _miningSlideDir;
        private GameScreen screen = GameScreen.Title;
        private readonly MenuState menu = new();
        private bool inventoryOpen;
        private bool biomeMenuOpen;

        // ---- multiplayer session ----
        private Net.NetHost? _netHost;
        private Net.NetClient? _netClient;
        private string _joinError = "";
        private float _inputSendTimer;
        private bool _netConnected;
        private int _activeHostPort = Net.NetHost.DefaultPort;
        private string _playerName = "Player" + Environment.ProcessId % 1000;

        /// <summary>The simulation world (null on the title screen).</summary>
        public GameWorld World { get; private set; }

        /// <summary>Zero-lag background-thread audio (grass break, cave ambience).</summary>
        public SoundEngine Sound { get; private set; }

        public Program()
        {
            BlockRegistry.LoadDefault();
            BiomeRegistry.LoadDefault();
            MobRegistry.DiscoverMobs(AppDomain.CurrentDomain.BaseDirectory);
            RefreshSavedWorlds();
            Sound = new SoundEngine();
            Sound.RegisterAllEmbedded();
        }

        // ------------------------------------------------------------------
        // world lifecycle
        // ------------------------------------------------------------------

        private void StartNewWorld(int seed, string name)
        {
            World?.Dispose();
            World = new GameWorld(seed, name, () => gpuRenderer, ChunkRenderRadius, Math.Max(1, Environment.ProcessorCount - 2));
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
            return int.TryParse(text.Trim(), out int seed) ? seed : Random.Shared.Next(0, int.MaxValue);
        }

        private void ProcessMenuActions()
        {
            if (menu.CreateWorldClicked)
            {
                StartNewWorld(ParseSeed(menu.SeedInput), menu.WorldName);
            }
            else if (menu.LoadWorldClicked)
            {
                LoadWorldFromList();
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
            menu.ResetFlags();
        }

        // ---- multiplayer ----

        // Hosts a new world and opens a listener on the configured port. Friends connect by
        // joining the host's IP:port; the host's world is authoritative.
        private void HostGame()
        {            if (!int.TryParse(menu.HostPort.Trim(), out int port) || port < 1024 || port > 65535)
            {
                _joinError = $"Invalid port '{menu.HostPort}' (use 1024-65535)";
                return;
            }
            StopNetworking();
            StartNewWorld(ParseSeed(menu.SeedInput), menu.WorldName + " (host)");
            _netHost = new Net.NetHost(World, port);
            _netHost.Log += msg => System.Console.WriteLine($"[NET] {msg}");
            _netHost.SetLocalPlayerState(World.LocalPlayer);
            if (!_netHost.Start())
            {
                _joinError = $"Could not listen on port {port}. Is it in use?";
                _netHost.Dispose();
                _netHost = null;
                return;
            }
            _activeHostPort = port;
        }

        // Opens the CURRENT singleplayer world to multiplayer (like MC's Open to LAN). Friends
        // join and get this world's seed + all modified chunks (their edits from the session).
        // The host keeps playing; pause menu stays open so the friend can see the port.
        private void OpenToLan()
        {
            if (World == null) return;
            if (_netHost != null && _netHost.IsRunning)
            {
                // Already hosting - just resume playing.
                ResumeToPlaying();
                return;
            }
            int port = Net.NetHost.DefaultPort;
            if (int.TryParse(menu.HostPort.Trim(), out int parsed) && parsed >= 1024 && parsed <= 65535)
            {
                port = parsed;
            }
            _joinError = "";
            _netHost = new Net.NetHost(World, port);
            _netHost.Log += msg => System.Console.WriteLine($"[NET] {msg}");
            _netHost.SetLocalPlayerState(World.LocalPlayer);
            if (!_netHost.Start())
            {
                _joinError = $"Could not listen on port {port}. Is it in use?";
                _netHost.Dispose();
                _netHost = null;
                return;
            }
            _activeHostPort = port;
            // Stay paused so the friend can read the port; Resume returns to play.
        }

        // Joins a host: creates a world from the host's seed (received in Welcome), positions at
        // the host's spawn, and starts streaming input upstream.
        private void JoinGame()
        {
            string addr = menu.JoinAddress.Trim();
            if (string.IsNullOrWhiteSpace(addr))
            {
                _joinError = "Enter a server address (e.g. 192.168.1.5:26065)";
                return;
            }
            string host = addr;
            int port = Net.NetHost.DefaultPort;
            int colon = addr.LastIndexOf(':');
            if (colon > 0 && int.TryParse(addr[(colon + 1)..], out int parsed))
            {
                host = addr[..colon];
                port = parsed;
            }
            StopNetworking();
            // Start a placeholder world now; once Welcome arrives we get the real seed. If the
            // seed differs we rebuild. This keeps the client renderable while connecting.
            StartNewWorld(0, "Connecting...");
            _joinError = "";
            _netClient = new Net.NetClient(World);
            _netClient.Log += msg => System.Console.WriteLine($"[NET] {msg}");
            _netClient.Connected += OnClientConnected;
            _netClient.Disconnected += OnClientDisconnected;
            if (!_netClient.Connect(host, port, _playerName))
            {
                _joinError = "Could not connect. Check the address and that the host is running.";
                _netClient.Dispose();
                _netClient = null;
                // Undo the placeholder world and go back to the multiplayer menu so the player
                // can fix the address instead of being stuck in a fake "Connecting..." world.
                ReturnToTitle();
                menu.Screen = GameScreen.Multiplayer;
            }
        }

        private void OnClientConnected()
        {
            _netConnected = true;
            // Rebuild the world with the host's real seed (same terrain), then sit at spawn.
            if (World == null || World.Seed != _netClient!.WorldSeed)
            {
                StartNewWorld(_netClient.WorldSeed, _netClient.WorldName);
            }
            World.PlayerPosition = new Point3D(_netClient.SpawnX, _netClient.SpawnY, _netClient.SpawnZ);
            World.PlayerVelocity = new Point3D(0, 0, 0);
            _lastMeshPosition = World.PlayerPosition;
            World.EnsureVisibleChunks();
            // Any local edit now goes to the host for authoritative application + broadcast.
            World.BlockEdited += OnLocalEdit;
        }

        private void OnClientDisconnected(string reason)
        {
            bool wasConnected = _netConnected;
            _netConnected = false;
            _joinError = reason;
            // If the connection drops while playing, don't strand the player in a frozen world.
            // Return to the title screen; the error is surfaced via BuildNetStatus on the menu.
            if (wasConnected && screen == GameScreen.Playing)
            {
                try { World.BlockEdited -= OnLocalEdit; } catch { }
                screen = GameScreen.Title;
                menu.Screen = GameScreen.Title;
                DisableMouseLook();
            }
        }

        private void StopNetworking()
        {
            _netConnected = false;
            try { _netHost?.Dispose(); } catch { }
            try { _netClient?.Dispose(); } catch { }
            _netHost = null;
            _netClient = null;
        }

        private void LoadWorldFromList()
        {
            int index = menu.SelectedWorldIndex;
            if (index < 0 || index >= menu.SavedWorlds.Count) return;
            string name = menu.SavedWorlds[index];
            string path = Path.Combine(SavesFolder, SanitizeFileName(name) + ".cubuild");
            if (!File.Exists(path)) return;
            var save = WorldSave.Load(path);
            if (save == null) return;
            LoadWorld(save);
        }

        private void SaveWorld()
        {
            if (World == null) return;
            try
            {
                Directory.CreateDirectory(SavesFolder);
                var save = new WorldSave
                {
                    Name = World.Name,
                    Seed = World.Seed,
                    PlayerX = World.PlayerPosition.X,
                    PlayerY = World.PlayerPosition.Y,
                    PlayerZ = World.PlayerPosition.Z,
                    Yaw = World.PlayerYaw,
                    Pitch = World.PlayerPitch,
                    SelectedSlot = World.SelectedSlot,
                    Hotbar = (int[])World.Hotbar.Clone(),
                };
                foreach (var coord in World.Chunks.ModifiedChunks)
                {
                    if (World.Chunks.TryGetLoadedChunk(coord, out var chunk))
                    {
                        save.Chunks.Add(new SavedChunk
                        {
                            Layer = coord.Layer,
                            X = coord.X,
                            Z = coord.Z,
                            Blocks = (byte[])chunk.RawBlocks.Clone(),
                            Meta = (byte[])chunk.RawMeta.Clone(),
                        });
                    }
                }
                save.Mobs = World.Entities.SaveMobs();
                save.Save(Path.Combine(SavesFolder, SanitizeFileName(save.Name) + ".cubuild"));
            }
            catch (Exception ex)
            {
                try { System.IO.File.AppendAllText("save_error.log", DateTime.Now + " Save failed: " + ex + Environment.NewLine); } catch { }
            }
        }

        private void LoadWorld(WorldSave save)
        {
            _loadSkipSpawn = true;
            StartNewWorld(save.Seed, save.Name);
            foreach (var c in save.Chunks)
            {
                World.Chunks.ApplySavedChunk(c.Layer, c.X, c.Z, c.Blocks, c.Meta);
            }
            World.PlayerPosition = new Point3D(save.PlayerX, save.PlayerY, save.PlayerZ);
            World.PlayerYaw = save.Yaw;
            World.PlayerPitch = save.Pitch;
            World.PlayerVelocity = new Point3D(0, 0, 0);
            if (save.Hotbar != null && save.Hotbar.Length == GameWorld.HotbarSlots)
            {
                for (int i = 0; i < GameWorld.HotbarSlots; i++) World.Hotbar[i] = save.Hotbar[i];
            }
            World.SetSelectedSlot(Math.Clamp(save.SelectedSlot, 0, GameWorld.HotbarSlots - 1));
            World.Entities.LoadMobs(save.Mobs);
            needsMeshUpdate = true;
        }

        private void RefreshSavedWorlds()
        {
            menu.SavedWorlds.Clear();
            try
            {
                if (!Directory.Exists(SavesFolder)) return;
                foreach (var file in Directory.GetFiles(SavesFolder, "*.cubuild"))
                {
                    menu.SavedWorlds.Add(Path.GetFileNameWithoutExtension(file));
                }
            }
            catch
            {
            }
        }

        private static string SavesFolder => System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "saves");

        private static string SanitizeFileName(string name)
        {
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "World" : name;
        }

        // ------------------------------------------------------------------
        // main loop
        // ------------------------------------------------------------------

        public void Run()
        {
            var windowCreateInfo = new WindowCreateInfo(100, 100, 900, 720, WindowState.Normal, baseTitle);
            var graphicsDeviceOptions = new GraphicsDeviceOptions(
                debug: false, swapchainDepthFormat: PixelFormat.D24_UNorm_S8_UInt,
                syncToVerticalBlank: false, resourceBindingModel: ResourceBindingModel.Improved,
                preferDepthRangeZeroToOne: true, preferStandardClipSpaceYDirection: true);
            VeldridStartup.CreateWindowAndGraphicsDevice(windowCreateInfo, graphicsDeviceOptions,
                GraphicsBackend.Direct3D11, out var createdWindow, out var createdGraphicsDevice);
            window = createdWindow;
            graphicsDevice = createdGraphicsDevice;
            baseTitle = window.Title;
            InitializeGpuRenderer(createdGraphicsDevice, createdGraphicsDevice.MainSwapchain);
            RunMainLoop();
        }

        private void RunMainLoop()
        {
            if (window == null) return;
            var activeWindow = window;
            var timer = Stopwatch.StartNew();
            long lastTicks = timer.ElapsedTicks;
            int lastWidth = activeWindow.Width;
            int lastHeight = activeWindow.Height;
            fpsStopwatch.Restart();
            frameCount = 0;
            while (activeWindow.Exists)
            {
                try
                {
                    input.BeginFrame();
                    var snapshot = activeWindow.PumpEvents();
                    if (!activeWindow.Exists) break;
                    if (activeWindow.Width != lastWidth || activeWindow.Height != lastHeight)
                    {
                        lastWidth = activeWindow.Width;
                        lastHeight = activeWindow.Height;
                        gpuRenderer?.Resize(lastWidth, lastHeight);
                    }
                    input.ProcessSnapshot(snapshot, mouseLook, MouseSensitivity);
                    ProcessMenuActions();
                    ApplyFrameInput(input.CaptureFrameInput());
                    ApplyLookInput(input.CaptureLookDelta());
                    long nowTicks = timer.ElapsedTicks;
                    double deltaSeconds = (nowTicks - lastTicks) / (double)Stopwatch.Frequency;
                    lastTicks = nowTicks;
                    if (deltaSeconds > MaxFrameDeltaSeconds) deltaSeconds = MaxFrameDeltaSeconds;
                    stageStopwatch.Restart();
                    frameCount++;
                    if (fpsStopwatch.ElapsedMilliseconds >= 1000)
                    {
                        lastFps = frameCount * 1000f / fpsStopwatch.ElapsedMilliseconds;
                        frameCount = 0;
                        fpsStopwatch.Restart();
                    }
                    var t0 = stageStopwatch.ElapsedTicks;
                    if (screen == GameScreen.Loading)
                    {
                        UpdateLoading((float)deltaSeconds);
                    }
                    else
                    {
                        StepSimulation(input.CaptureTickInput(), (float)deltaSeconds);
                    }
                    var t1 = stageStopwatch.ElapsedTicks;
                    lastUpdateMs = (t1 - t0) * 1000f / Stopwatch.Frequency;
                    var t2 = stageStopwatch.ElapsedTicks;
                    if (_lastMeshPosition.HasValue)
                    {
                        var delta = World != null ? World.PlayerPosition - _lastMeshPosition.Value : Point3D.Zero;
                        double posDelta = delta.X * delta.X + delta.Y * delta.Y + delta.Z * delta.Z;
                        if (posDelta > 4.0)
                        {
                            needsMeshUpdate = true;
                            _lastMeshPosition = World?.PlayerPosition ?? _lastMeshPosition.Value;
                        }
                    }
                    else if (World != null)
                    {
                        _lastMeshPosition = World.PlayerPosition;
                    }
                    if (needsMeshUpdate && World?.Mesher != null)
                    {
                        World.Mesher.Update();
                        needsMeshUpdate = false;
                    }
                    var t3 = stageStopwatch.ElapsedTicks;
                    lastMeshMs = (t3 - t2) * 1000f / Stopwatch.Frequency;
                    lastUploadMs = 0f;
                    var t4 = stageStopwatch.ElapsedTicks;
                    if (gpuRenderer != null)
                    {
                        // Always push the HUD (even without a world: menu-only state). On the title
                        // screen the renderer MUST operate on Program's real MenuState instance, not
                        // HudState.Empty's detached copy - otherwise button clicks set flags nobody
                        // reads. BuildHud handles the null-world case safely.
                        gpuRenderer.SetHud(BuildHud());
                        if (World != null)
                        {
                            gpuRenderer.UpdateCamera(thirdPersonView ? GetThirdPersonCameraPosition() : World.PlayerPosition, World.PlayerYaw, World.PlayerPitch);
                            _entityRenderScratch.Clear();
                            _entityRenderScratch.AddRange(World.Entities.MobRenderData);
                            if (thirdPersonView) _entityRenderScratch.Add(BuildLocalPlayerRenderData());
                            AddRemotePlayersToRender(_entityRenderScratch);
                            gpuRenderer.SetEntities(_entityRenderScratch);
                            gpuRenderer.SetFallingBlocks(World.BlockTicks.Gravity.FallingBlocks);
                        }
                        gpuRenderer.ProcessPendingPriorityMeshes();
                        gpuRenderer.SetUiInputSnapshot(snapshot);
                        gpuRenderer.Render();

                        while (gpuRenderer.TryTakeInventorySelection(out int invBlock))
                        {
                            if (World != null && invBlock > 0 && invBlock < BlockRegistry.Count)
                            {
                                World.Hotbar[World.SelectedSlot] = invBlock;
                                World.SelectedBlock = invBlock;
                            }
                            inventoryOpen = false;
                            EnableMouseLook();
                        }
                        while (gpuRenderer.TryTakeBiomeSelection(out string biomeName))
                        {
                            if (World != null)
                            {
                                if (biomeName == "The Great Pyramid")
                                    World.TeleportToPyramid();
                                else
                                    World.TeleportToNearestBiome(biomeName);
                            }
                            biomeMenuOpen = false;
                            EnableMouseLook();
                        }
                    }
                    var t5 = stageStopwatch.ElapsedTicks;
                    lastRenderMs = (t5 - t4) * 1000f / Stopwatch.Frequency;
                    if (window != null)
                    {
                        string rd = $"Render: {RenderDistanceName} ({ChunkRenderRadius})";
                        window.Title = showFps ? $"{baseTitle} - FPS: {lastFps:0.0} - {rd}" : $"{baseTitle} - {rd}";
                    }
                }
                catch (Exception ex)
                {
                    try { System.IO.File.AppendAllText("app_error.log", DateTime.Now + " Tick error: " + ex + Environment.NewLine); } catch { }
                }
            }

            SaveWorld();
        }

        private void ApplyFrameInput(FrameInputState frameInput)
        {
            if (frameInput.ToggleMouseCapturePressed)
            {
                if (screen == GameScreen.Playing)
                {
                    SaveWorld(); // autosave whenever the pause menu opens
                    screen = GameScreen.Paused;
                    menu.Screen = GameScreen.Paused;
                    DisableMouseLook();
                }
                else if (screen == GameScreen.Paused)
                {
                    ResumeToPlaying();
                }
            }
            if (screen == GameScreen.Playing && !mouseLook && (frameInput.BreakBlockPressed || frameInput.PlaceBlockPressed))
            {
                EnableMouseLook();
                return;
            }
            if (frameInput.ToggleDebugPressed) showFps = !showFps;
            if (_ignoreInteractFrames > 0) _ignoreInteractFrames--;
            if (screen == GameScreen.Playing && World != null)
            {
                if (frameInput.ToggleFlyPressed) World.FlyMode = !World.FlyMode;
                if (frameInput.AdvanceTimePressed) World.AdvanceTime();
                if (frameInput.ToggleGpuCullPressed)
                {
                    gpuRenderer?.ToggleGpuCulling();
                    // A cull-mode switch changes which per-pass command buffer is authoritative, so
                    // every loaded chunk is re-meshed and re-uploaded - this rebuilds the draw
                    // commands and flushes the GPU cull data fresh, avoiding stale-args glitches.
                    _forceChunkStream = true;
                    foreach (var c in World.Chunks.GetLoadedChunks())
                    {
                        c.NeedsRemesh = true;
                    }
                }
                if (frameInput.ToggleFullbrightPressed)
                {
                    ChunkLighting.Fullbright = !ChunkLighting.Fullbright;
                    // Brightness is baked into each face's shade at mesh time, so flipping the
                    // flag must re-mesh every loaded chunk to take effect.
                    foreach (var c in World.Chunks.GetLoadedChunks())
                    {
                        c.NeedsRemesh = true;
                    }
                }
                if (frameInput.ToggleInventoryPressed)
                {
                    inventoryOpen = !inventoryOpen;
                    if (inventoryOpen) DisableMouseLook();
                    else EnableMouseLook();
                }
                if (frameInput.ToggleBiomeMenuPressed)
                {
                    biomeMenuOpen = !biomeMenuOpen;
                    if (biomeMenuOpen) DisableMouseLook();
                    else EnableMouseLook();
                }
            }
            if (frameInput.CycleRenderDistancePressed) CycleRenderDistance();
            if (screen == GameScreen.Playing && World != null)
            {
                if (frameInput.SpawnMobPressed) World.Entities.SpawnDuck(World.PlayerPosition, World.PlayerYaw);
                if (frameInput.SpawnCoyotePressed) World.Entities.SpawnCoyote(World.PlayerPosition, World.PlayerYaw);
                if (frameInput.SpawnStevePressed) World.Entities.SpawnSteve(World.PlayerPosition, World.PlayerYaw);
                if (frameInput.SpawnZombiePressed) World.Entities.SpawnMobById("zombie", World.PlayerPosition, World.PlayerYaw);
                // O = take 1 point of damage (healthbar slice test).
                if (frameInput.DamageSelfPressed) World.DamagePlayer(1, DeathCause.DebugSelf);
            }
            if (frameInput.ToggleThirdPersonPressed) thirdPersonView = !thirdPersonView;
            if (frameInput.SelectedSlot.HasValue && World != null) World.SetSelectedSlot(frameInput.SelectedSlot.Value);
            if (screen == GameScreen.Playing && World != null && _ignoreInteractFrames == 0 && frameInput.BreakBlockPressed)
            {
                if (!World.Entities.TryAttackMob(World.PlayerPosition, World.GetCameraForward(), null))
                {
                    // No mob hit: mining is driven by BreakHeld in StepSimulation, but the click
                    // primes the target so progress starts immediately this frame.
                }
            }
            if (screen == GameScreen.Playing && World != null && _ignoreInteractFrames == 0 && frameInput.PlaceBlockPressed) PlaceSelectedBlock();
        }

        private void CycleRenderDistance()
        {
            renderDistanceIndex = (renderDistanceIndex + 1) % RenderDistances.Length;
            gpuRenderer?.SetRenderDistance(ChunkRenderRadius);
            needsMeshUpdate = true;
            _forceChunkStream = true;
            if (World != null) World.ChunkRenderRadius = ChunkRenderRadius;
        }

        private void StepSimulation(TickInputState tickInput, float deltaSeconds)
        {
            // The world only pauses for the ESC pause menu (singleplayer). The death screen keeps
            // the simulation running - the environment, mobs and time all continue while dead.
            if (screen == GameScreen.Paused || screen != GameScreen.Playing && screen != GameScreen.Dead) return;
            if (World == null) return;
            UpdateNetworking(tickInput, deltaSeconds);
            World.StepSimulation(tickInput, deltaSeconds);

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
        private void UpdateMining(TickInputState tickInput, float deltaSeconds)
        {
            if (World == null) return;

            if (tickInput.BreakHeld && _ignoreInteractFrames == 0)
            {
                var pick = World.TryPickBlock(World.PlayerPosition, World.GetCameraForward());
                if (pick.HasValue)
                {
                    var target = pick.Value.Remove;
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
                        // remesh and sound all fire).
                        DeleteBlockAt(target.x, target.y, target.z);
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
        // block was removed.
        private bool DeleteBlockAt(int x, int y, int z)
        {
            if (World == null) return false;
            if (!World.TryBreakBlockAt(x, y, z, out int removedBlockId)) return false;
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
            if (World.TryPlaceSelectedBlock(World.LocalPlayer, World.PlayerPosition, World.GetCameraForward()))
            {
                needsMeshUpdate = true;
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
        private void AddRemotePlayersToRender(List<MobRenderData> list)
        {
            if (World == null) return;
            // Host side: render each connected client's simulated state.
            if (_netHost != null)
            {
                foreach (var p in World.RemotePlayers)
                {
                    list.Add(new MobRenderData(
                        "player",
                        new Point3D(p.Position.X, p.Position.Y - GameWorld.EyeHeight, p.Position.Z),
                        p.Yaw * (float)Math.PI / 180f,
                        0f, p.WalkPhase, p.WalkAmount, 0f, 0f, 0f,
                        (float)p.Velocity.Y, p.Grounded, false, 0f, 0f, 0f));
                }
                return;
            }
            // Client side: render everyone in the snapshot except ourselves.
            if (_netClient == null || !_netConnected) return;
            var snap = _netClient.LatestSnapshot;
            if (snap == null) return;
            foreach (var p in snap.Players)
            {
                if (p.Id == _netClient.ClientId) continue; // that's us
                list.Add(new MobRenderData(
                    "player",
                    new Point3D(p.X, p.Y - GameWorld.EyeHeight, p.Z),
                    p.Yaw * (float)Math.PI / 180f,
                    0f, p.WalkPhase, p.WalkAmount, 0f, 0f, 0f,
                    p.VelY, p.Grounded, false, 0f, 0f, 0f));
            }
        }

        // Sends the client's input + look to the host (~20Hz), and pushes the host's own local
        // player state into the broadcast. Called every frame while playing.
        private void UpdateNetworking(TickInputState tickInput, float deltaSeconds)
        {
            if (World == null) return;
            if (_netHost != null)
            {
                _netHost.DrainIncomingEdits();
                _netHost.SetLocalPlayerState(World.LocalPlayer);
            }
            if (_netClient != null && _netConnected)
            {
                _netClient.DrainIncomingEdits(World);
                _inputSendTimer += deltaSeconds;
                if (_inputSendTimer >= 0.05f)
                {
                    _inputSendTimer = 0f;
                    _netClient.SendInput(tickInput, World.PlayerYaw, World.PlayerPitch);
                }
            }
        }

        // ------------------------------------------------------------------
        // HUD / camera helpers (read world state; no sim logic)
        // ------------------------------------------------------------------

        private HudState BuildHud()
        {
            string netStatus = BuildNetStatus();
            string mpError = BuildMultiplayerError();
            if (World == null)
            {
                return new HudState
                {
                    ShowDebug = showFps, FlyMode = false, Menu = menu, Fps = lastFps,
                    UpdateMs = lastUpdateMs, MeshMs = lastMeshMs, UploadMs = lastUploadMs, RenderMs = lastRenderMs,
                    SelectedBlockText = "Selected: -",
                    SelectedSlot = 0, WorldSeed = 0,
                    Hotbar = Array.Empty<int>(),
                    PlayerX = 0, PlayerY = 0, PlayerZ = 0,
                    RenderDistance = ChunkRenderRadius,
                    NetStatus = netStatus,
                    MultiplayerError = mpError,
                };
            }
            var forward = World.GetCameraForward();
            var pickResult = World.TryPickBlock(World.PlayerPosition, forward);
            Vector3[]? highlightQuad = null;
            if (pickResult.HasValue) highlightQuad = ComputeHighlightWorldQuad(pickResult.Value);
            // Mining overlay only shows on the block actually being mined.
            float miningProgress = 0f;
            Vector3 miningBlockPos = Vector3.Zero;
            int miningBlockId = 0;
            var miningBlockNormal = new Point3D(0, 0, 0);
            if (_miningTarget.HasValue && pickResult.HasValue)
            {
                var t = pickResult.Value.Remove;
                if (_miningTarget.Value.x == t.x && _miningTarget.Value.y == t.y && _miningTarget.Value.z == t.z)
                {
                    miningProgress = _miningProgress;
                    miningBlockPos = new Vector3(t.x, t.y, t.z);
                    miningBlockId = _miningBlockId;
                    miningBlockNormal = _miningSlideDir;
                }
            }
            return new HudState
            {
                ShowDebug = showFps, InventoryOpen = inventoryOpen, BiomeMenuOpen = biomeMenuOpen, FlyMode = World.FlyMode, Fullbright = ChunkLighting.Fullbright, WorldTime = World.WorldTime, Menu = menu, Fps = lastFps, UpdateMs = lastUpdateMs,
                MeshMs = lastMeshMs, UploadMs = lastUploadMs, RenderMs = lastRenderMs,
                FacingText = $"{GetCompassDirection(World.PlayerYaw)} ({GameWorld.NormalizeYaw(World.PlayerYaw):0.0} deg)",
                SelectedBlockText = $"Selected: {BlockRegistry.GetName(World.SelectedBlock)}",
                RenderDistanceText = $"Render dist: {RenderDistanceName} ({ChunkRenderRadius})",
                SelectedSlot = World.SelectedSlot, WorldSeed = World.Seed,
                BiomeText = World.ChunkProvider?.BiomeNameAt((int)Math.Floor(World.PlayerPosition.X), (int)Math.Floor(World.PlayerPosition.Z)) ?? string.Empty,
                Hotbar = World.Hotbar, HighlightWorldQuad = highlightQuad,
                PlayerHealth = World.LocalPlayer.Health,
                DeathCause = World.LocalPlayer.DeathCause,
                PlayerX = World.PlayerPosition.X,
                PlayerY = World.PlayerPosition.Y,
                PlayerZ = World.PlayerPosition.Z,
                PlayerChunkX = GameWorld.WorldToChunkCoord(World.PlayerPosition.X),
                PlayerChunkZ = GameWorld.WorldToChunkCoord(World.PlayerPosition.Z),
                RenderDistance = ChunkRenderRadius,
                NetStatus = netStatus,
                MultiplayerError = mpError,
                MiningProgress = miningProgress,
                MiningBlockPos = miningBlockPos,
                MiningBlockId = miningBlockId,
                MiningBlockNormal = miningBlockNormal,
            };
        }

        private string BuildNetStatus()
        {
            if (_netHost != null && _netHost.IsRunning) return "Hosting on " + GetLanAddresses() + ":" + _activeHostPort;
            if (_netClient != null)
            {
                if (_netConnected) return "Joined " + menu.JoinAddress + " as #" + _netClient.ClientId;
                return "Join error: " + _joinError;
            }
            return string.Empty;
        }

        // The host's LAN IPs, so the friend knows what address to type on Join Game.
        private static string GetLanAddresses()
        {
            try
            {
                var addrs = new List<string>();
                foreach (var ip in System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()).AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(ip))
                    {
                        addrs.Add(ip.ToString());
                    }
                }
                if (addrs.Count == 0) addrs.Add("127.0.0.1");
                return string.Join("/", addrs);
            }
            catch { return "127.0.0.1"; }
        }

        private string BuildMultiplayerError()
        {
            if (string.IsNullOrEmpty(_joinError)) return string.Empty;
            if (_netClient == null && _netHost == null) return _joinError;
            return string.Empty;
        }

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
            bool dead = w.LocalPlayer.Health <= 0;
            return new MobRenderData(
                "player", feet, yawRad, 0f,
                w.PlayerWalkPhase, dead ? 0f : w.PlayerWalkAmount, 0f, 0f, 0f,
                (float)w.PlayerVelocity.Y, w.PlayerGrounded,
                dead, dead ? Math.Clamp(w.LocalPlayer.DeathTimer / 0.5f, 0f, 1f) : 0f,
                w.LocalPlayer.DeathRollDir, 0f);
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

        private static double Dot(Point3D a, Point3D b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        // ------------------------------------------------------------------
        // input / mouse capture (unchanged, window-layer only)
        // ------------------------------------------------------------------

        private void EnableMouseLook()
        {
            if (mouseLook) return;
            mouseLook = true;
            if (window != null) ApplyMouseCapture(window, true);
            input.ResetMouseTracking();
        }

        private void DisableMouseLook()
        {
            if (!mouseLook) return;
            mouseLook = false;
            if (window != null) ApplyMouseCapture(window, false);
            input.ResetMouseTracking();
        }

        private static void ApplyMouseCapture(Sdl2Window sdlWindow, bool captured)
        {
            sdlWindow.CursorVisible = !captured;
            Veldrid.Sdl2.Sdl2Native.SDL_ShowCursor(captured ? 0 : 1);
            Veldrid.Sdl2.Sdl2Native.SDL_CaptureMouse(captured);
            Veldrid.Sdl2.Sdl2Native.SDL_SetRelativeMouseMode(captured);
            TrySetBoolProperty(sdlWindow, "MouseCursorVisible", !captured);
            TrySetBoolProperty(sdlWindow, "MouseRelativeMode", captured);
            TrySetBoolProperty(sdlWindow, "InputGrabbed", captured);
            TrySetBoolProperty(sdlWindow, "MouseGrabbed", captured);
        }

        private static void TrySetBoolProperty(Sdl2Window sdlWindow, string propertyName, bool value)
        {
            var prop = sdlWindow.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (prop != null && prop.CanWrite && prop.PropertyType == typeof(bool)) prop.SetValue(sdlWindow, value);
        }

        public void Dispose()
        {
            StopNetworking();
            try { World?.Dispose(); } catch { }
            try { gpuRenderer?.Dispose(); } catch { }
            try { graphicsDevice?.Dispose(); } catch { }
            try { window?.Close(); } catch { }
        }

        private static void PreloadNativeLibraries()
        {
            string[] names = { "SDL2", "cimgui", "veldrid-spirv", "libveldrid-spirv" };
            var asm = Assembly.GetExecutingAssembly();
            foreach (var name in names)
            {
                try { System.Runtime.InteropServices.NativeLibrary.TryLoad(name, asm, null, out _); } catch { }
            }
        }

        [STAThread]
        static void Main()
        {
            try
            {
                PreloadNativeLibraries();
                using var app = new Program();
                app.Run();
            }
            catch (Exception ex)
            {
                try
                {
                    string logPath = System.IO.Path.Combine(AppContext.BaseDirectory, "cubeapp-crash.log");
                    System.IO.File.WriteAllText(logPath, DateTime.Now + Environment.NewLine + ex);
                }
                catch { }
                throw;
            }
        }
    }
}
