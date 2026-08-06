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
    public sealed class Program : IDisposable
    {
        private ChunkManager manager;        private IRenderer? gpuRenderer;
        private MeshWorker? meshWorker;
        private MeshScheduler meshScheduler;
        private BlockTickScheduler? blockTickScheduler;
        private ChunkGenWorker? chunkGenWorker;
        private Sdl2Window? window;
        private GraphicsDevice? graphicsDevice;
        private Point3D cameraPosition = new Point3D(24.0, 10.0, -24.0);
        private float cameraYaw = 0f;
        private float cameraPitch = 0f;
        private Point3D? _lastMeshPosition;
        // Chunk streaming (request/unload) only needs to run when the player crosses a chunk
        // boundary (or when the render distance changes), not every frame.
        private int _lastStreamChunkX = int.MinValue;
        private int _lastStreamChunkZ = int.MinValue;
        private bool _forceChunkStream = true;
        private readonly InputProcessor input = new();
        private bool mouseLook;
        private volatile bool needsMeshUpdate = true;
        private string baseTitle = "Chunk Mesh Example";
        private bool showFps;
        private int frameCount;
        private float lastFps;
        private readonly Stopwatch fpsStopwatch = new();
        private float lastUpdateMs;
        private float lastMeshMs;
        private float lastUploadMs;
        private float lastRenderMs;
        private readonly Stopwatch stageStopwatch = new();
        private Point3D playerVelocity = new Point3D(0, 0, 0);
        private bool playerGrounded;
        private const float WalkSpeed = 4.317f;
        private const float FlySpeed = 10.8f;
        private const float JumpVelocity = 8.0f;
        private const float Gravity = 24.0f;
        private const float MaxFallSpeed = 36.0f;
        private const double PlayerHeight = 1.8;
        private const double PlayerRadius = 0.30;
        private const double EyeHeight = 1.62;
        private const double CollisionStep = 0.05;
        private const float BlockReach = 6.5f;
        private const float MouseSensitivity = 0.5f;
        private const double MaxFrameDeltaSeconds = 0.25;
        private static readonly int[] RenderDistances = { 16, 8, 4, 2 };
        private static readonly string[] RenderDistanceNames = { "Far", "Normal", "Short", "Tiny" };
        private int renderDistanceIndex = 0; // Far by default, like Infdev
        private int ChunkRenderRadius => RenderDistances[renderDistanceIndex];
        private string RenderDistanceName => RenderDistanceNames[renderDistanceIndex];
        private const int SpawnSyncRadius = 2;
        private int selectedBlock = 0; // numeric block id (BlockRegistry), set in ctor once the registry is loaded
        private int selectedSlot;
        private const int HotbarSlots = 10;
        private readonly int[] _hotbarBlocks = new int[HotbarSlots];
        private int worldSeed;
        private string worldName = "World 1";
        private World.InfdevChunkProvider chunkProvider;
        private GameScreen screen = GameScreen.Title;
        private readonly MenuState menu = new();
        private bool inventoryOpen;
        private bool flyMode;
        private bool thirdPersonView;
        private int _ignoreInteractFrames; // skips break/place right after a menu click
        private float playerWalkPhase;
        private float playerWalkAmount;
        private EntityManager entityManager;

        public Program()
        {
            // Load block definitions first - chunks, terrain gen, mesher and the hotbar all read
            // numeric ids out of the registry, so it has to be ready before any block is touched.
            BlockRegistry.LoadDefault();
            for (int i = 0; i < HotbarSlots; i++)
            {
                _hotbarBlocks[i] = i < BlockRegistry.Hotbar.Count ? BlockRegistry.Hotbar[i] : BlockRegistry.AirId;
            }
            selectedBlock = Math.Max(0, _hotbarBlocks[0]);
            MobRegistry.DiscoverMobs(AppDomain.CurrentDomain.BaseDirectory);
            RefreshSavedWorlds();
            // The world isn't created until the player picks "Create World" on the title screen.
        }

        // Creates the world from a seed (the title screen's "Create World" action): rebuilds the
        // chunk pipeline, spawns the player on dry land, and enters the Playing screen.
        private void StartNewWorld(int seed, string name)
        {
            worldSeed = seed;
            worldName = string.IsNullOrWhiteSpace(name) ? "World 1" : name;
            chunkProvider = new World.InfdevChunkProvider(seed);
            manager = new ChunkManager(chunkProvider);
            entityManager = new EntityManager(manager);
            meshWorker = new MeshWorker(manager, () => gpuRenderer);
            meshScheduler = new MeshScheduler(manager, meshWorker);
            blockTickScheduler = new BlockTickScheduler(manager, meshScheduler);
            chunkGenWorker = new ChunkGenWorker(manager, () => needsMeshUpdate = true, Math.Max(1, Environment.ProcessorCount - 2));
            if (gpuRenderer != null)
            {
                gpuRenderer.SetChunkManager(manager);
                gpuRenderer.ResetWorld();
            }
            EnsureVisibleChunks();
            PlaceCameraAtSafeSpawn();
            _lastMeshPosition = cameraPosition;
            meshScheduler.Update();
            screen = GameScreen.Playing;
            menu.Screen = GameScreen.Playing;
            _ignoreInteractFrames = 2;
            EnableMouseLook();
        }

        private void ResumeToPlaying()
        {
            screen = GameScreen.Playing;
            menu.Screen = GameScreen.Playing;
            _ignoreInteractFrames = 2;
            EnableMouseLook();
        }

        private void ReturnToTitle()
        {
            screen = GameScreen.Title;
            menu.Screen = GameScreen.Title;
            RefreshSavedWorlds();
            DisableMouseLook();
        }

        // Parses the seed text field; blank or invalid rolls a random seed.
        private static int ParseSeed(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Random.Shared.Next(0, int.MaxValue);
            return int.TryParse(text.Trim(), out int seed) ? seed : Random.Shared.Next(0, int.MaxValue);
        }

        // Consumes the renderer's menu button presses and transitions screens.
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
            else if (menu.ResumeClicked)
            {
                ResumeToPlaying();
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

        // Saves the current world to the saves folder (modified chunks only).
        private void SaveWorld()
        {
            if (manager == null) return;
            try
            {
                Directory.CreateDirectory(SavesFolder);
                var save = new WorldSave
                {
                    Name = worldName,
                    Seed = worldSeed,
                    PlayerX = cameraPosition.X,
                    PlayerY = cameraPosition.Y,
                    PlayerZ = cameraPosition.Z,
                    Yaw = cameraYaw,
                    Pitch = cameraPitch,
                    SelectedSlot = selectedSlot,
                    Hotbar = (int[])_hotbarBlocks.Clone(),
                };
                foreach (var coord in manager.ModifiedChunks)
                {
                    if (manager.TryGetLoadedChunk(coord, out var chunk))
                    {
                        save.Chunks.Add(new SavedChunk
                        {
                            X = coord.X,
                            Z = coord.Z,
                            Blocks = (byte[])chunk.RawBlocks.Clone(),
                            Meta = (byte[])chunk.RawMeta.Clone(),
                        });
                    }
                }
                if (entityManager != null) save.Mobs = entityManager.SaveMobs();
                save.Save(Path.Combine(SavesFolder, SanitizeFileName(save.Name) + ".cubuild"));
            }
            catch (Exception ex)
            {
                try { System.IO.File.AppendAllText("save_error.log", DateTime.Now + " Save failed: " + ex + Environment.NewLine); } catch { }
            }
        }

        // Loads a saved world: regenerates from its seed, stamps saved chunks on top, restores
        // the player and mobs.
        private void LoadWorld(WorldSave save)
        {
            StartNewWorld(save.Seed, save.Name);
            foreach (var c in save.Chunks)
            {
                manager.ApplySavedChunk(c.X, c.Z, c.Blocks, c.Meta);
            }
            cameraPosition = new Point3D(save.PlayerX, save.PlayerY, save.PlayerZ);
            cameraYaw = save.Yaw;
            cameraPitch = save.Pitch;
            playerVelocity = new Point3D(0, 0, 0);
            if (save.Hotbar != null && save.Hotbar.Length == HotbarSlots)
            {
                for (int i = 0; i < HotbarSlots; i++) _hotbarBlocks[i] = save.Hotbar[i];
            }
            selectedSlot = Math.Clamp(save.SelectedSlot, 0, HotbarSlots - 1);
            selectedBlock = _hotbarBlocks[selectedSlot];
            entityManager?.LoadMobs(save.Mobs);
            needsMeshUpdate = true;
        }

        // Refreshes the title screen's saved-world list from the saves folder.
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
                    StepSimulation(input.CaptureTickInput(), (float)deltaSeconds);
                    var t1 = stageStopwatch.ElapsedTicks;
                    lastUpdateMs = (t1 - t0) * 1000f / Stopwatch.Frequency;
                    var t2 = stageStopwatch.ElapsedTicks;
                    // Meshing is not view-dependent, so camera rotation never needs to trigger a
                    // scheduler pass - only movement (and chunk-gen completion via the worker).
                    if (_lastMeshPosition.HasValue)
                    {
                        var delta = cameraPosition - _lastMeshPosition.Value;
                        double posDelta = delta.X * delta.X + delta.Y * delta.Y + delta.Z * delta.Z;
                        if (posDelta > 4.0)
                        {
                            needsMeshUpdate = true;
                            _lastMeshPosition = cameraPosition;
                        }
                    }
                    else
                    {
                        _lastMeshPosition = cameraPosition;
                    }
            if (needsMeshUpdate && meshScheduler != null)
            {
                meshScheduler.Update();
                needsMeshUpdate = false;
            }
            UpdateDeepFill();
            var t3 = stageStopwatch.ElapsedTicks;
            lastMeshMs = (t3 - t2) * 1000f / Stopwatch.Frequency;
            lastUploadMs = 0f;
            var t4 = stageStopwatch.ElapsedTicks;
            if (gpuRenderer != null)
            {
                gpuRenderer.UpdateCamera(thirdPersonView ? GetThirdPersonCameraPosition() : cameraPosition, cameraYaw, cameraPitch);
                gpuRenderer.SetHud(BuildHud());
                if (entityManager != null)
                {
                    if (thirdPersonView)
                    {
                        var withPlayer = new List<MobRenderData>(entityManager.MobRenderData.Count + 1);
                        withPlayer.AddRange(entityManager.MobRenderData);
                        withPlayer.Add(BuildLocalPlayerRenderData());
                        gpuRenderer.SetEntities(withPlayer);
                    }
                    else
                    {
                        gpuRenderer.SetEntities(entityManager.MobRenderData);
                    }
                }
                // Player edits already mesh immediately via MeshChunkImmediate();
                // Background MeshWorker handles all other meshing.
                gpuRenderer.ProcessPendingPriorityMeshes();
                gpuRenderer.SetUiInputSnapshot(snapshot);
                gpuRenderer.Render();

                // Apply any block the player picked from the inventory (renderer queues it during
                // its ImGui pass): drop it into the currently selected hotbar slot and close.
                while (gpuRenderer.TryTakeInventorySelection(out int invBlock))
                {
                    if (invBlock > 0 && invBlock < BlockRegistry.Count)
                    {
                        _hotbarBlocks[selectedSlot] = invBlock;
                        selectedBlock = invBlock;
                    }
                    inventoryOpen = false;
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

            // The window closed - save the world if one is active.
            SaveWorld();
        }

        private void ApplyFrameInput(FrameInputState frameInput)
        {
            // ESC toggles pause while playing; on the pause screen it resumes. It also releases
            // the cursor so the pause menu can be clicked.
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
            // Don't auto-enroll mouse look from a menu click; only when actually playing.
            if (screen == GameScreen.Playing && !mouseLook && (frameInput.BreakBlockPressed || frameInput.PlaceBlockPressed))
            {
                EnableMouseLook();
                return;
            }
            if (frameInput.ToggleDebugPressed) showFps = !showFps;
            if (_ignoreInteractFrames > 0) _ignoreInteractFrames--;
            if (screen == GameScreen.Playing)
            {
                if (frameInput.ToggleFlyPressed) flyMode = !flyMode;
                if (frameInput.ToggleInventoryPressed)
                {
                    inventoryOpen = !inventoryOpen;
                    if (inventoryOpen)
                    {
                        DisableMouseLook(); // free the cursor for the inventory
                    }
                    else
                    {
                        EnableMouseLook();
                    }
                }
            }
            if (frameInput.CycleRenderDistancePressed) CycleRenderDistance();
            if (screen == GameScreen.Playing)
            {
                if (frameInput.SpawnMobPressed) SpawnDuck();
                if (frameInput.SpawnCoyotePressed) SpawnCoyote();
                if (frameInput.SpawnStevePressed) SpawnSteve();
            }
            if (frameInput.ToggleThirdPersonPressed) thirdPersonView = !thirdPersonView;
            if (frameInput.SelectedSlot.HasValue) SetSelectedSlot(frameInput.SelectedSlot.Value);
            if (screen == GameScreen.Playing && _ignoreInteractFrames == 0 && frameInput.BreakBlockPressed)
            {
                if (!entityManager.TryAttackMob(cameraPosition, GetCameraForward(), null))
                {
                    DeleteHighlightedBlock();
                }
            }
            if (screen == GameScreen.Playing && _ignoreInteractFrames == 0 && frameInput.PlaceBlockPressed) PlaceSelectedBlock();
        }

        private void SpawnDuck() => entityManager.SpawnDuck(cameraPosition, cameraYaw);
        private void UpdateDucks(float deltaSeconds) => entityManager.Update(deltaSeconds);
        private void SpawnCoyote() => entityManager.SpawnCoyote(cameraPosition, cameraYaw);
        private void SpawnSteve() => entityManager.SpawnSteve(cameraPosition, cameraYaw);

        private static bool RayBox(Point3D origin, Point3D dir,
            double minX, double minY, double minZ, double maxX, double maxY, double maxZ, out double tEntry)
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

        private void CycleRenderDistance()
        {
            renderDistanceIndex = (renderDistanceIndex + 1) % RenderDistances.Length;
            gpuRenderer?.SetRenderDistance(ChunkRenderRadius);
            needsMeshUpdate = true;
            _forceChunkStream = true;
        }

        private void SetSelectedSlot(int slot)
        {
            if (slot < 0 || slot >= HotbarSlots) return;
            selectedSlot = slot;
            selectedBlock = _hotbarBlocks[slot];
        }

        private void StepSimulation(TickInputState tickInput, float deltaSeconds)
        {
            // The world sim only runs while actually playing - paused/title freeze everything.
            if (screen != GameScreen.Playing || manager == null) return;
            blockTickScheduler?.Tick(deltaSeconds);
            UpdatePlayerMovement(tickInput, deltaSeconds);
            UpdateDucks(deltaSeconds);
            int chunkX = WorldToChunkCoord(cameraPosition.X);
            int chunkZ = WorldToChunkCoord(cameraPosition.Z);
            // Request/unload scans cost O(radius^2) + O(loadedChunks); only run them when the
            // player actually enters a new chunk column (or the render distance changed).
            if (_forceChunkStream || chunkX != _lastStreamChunkX || chunkZ != _lastStreamChunkZ)
            {
                _forceChunkStream = false;
                _lastStreamChunkX = chunkX;
                _lastStreamChunkZ = chunkZ;
                manager.RequestChunksAround(chunkX, chunkZ, ChunkRenderRadius, cameraPosition);
                var unloaded = manager.UnloadChunksOutside(chunkX, chunkZ, ChunkRenderRadius);
                if (gpuRenderer != null)
                {
                    foreach (var uc in unloaded) gpuRenderer.RemoveChunk(uc);
                }
                if (unloaded.Count > 0) needsMeshUpdate = true;
            }
        }

        private void UpdatePlayerMovement(TickInputState tickInput, float deltaSeconds)
        {
            if (flyMode)
            {
                // Free 3D flight + NOCLIP: full look-direction movement (pitch-aware), space up /
                // shift down, no gravity, no jump. The player phases straight through blocks so you
                // can fly underground and see what's hidden - position moves directly, no collision
                // checks at all (like Cubuild's noclipFlyMode).
                var flyForward = GetCameraForward();
                var flyRight = GetCameraRight(cameraYaw);
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
                playerVelocity = flyDir * FlySpeed;
                cameraPosition = new Point3D(
                    cameraPosition.X + playerVelocity.X * deltaSeconds,
                    cameraPosition.Y + playerVelocity.Y * deltaSeconds,
                    cameraPosition.Z + playerVelocity.Z * deltaSeconds);
                playerGrounded = false;
                playerWalkAmount = 0f;
                return;
            }

            var forwardWalk = GetCameraForward();
            var forwardHorizontal = new Point3D(forwardWalk.X, 0, forwardWalk.Z).Normalized();
            var right = GetCameraRight(cameraYaw);
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

            bool feetInWater = PlayerSampleInWater(0.05);
            bool bodyInWater = PlayerSampleInWater(PlayerHeight * 0.4);
            bool headInWater = PlayerSampleInWater(PlayerHeight * 0.85);
            bool inWater = feetInWater || bodyInWater || headInWater;
            if (inWater)
            {
                // ---- Cubuild player water feel (Cubuild.html updatePlayerPhysics) ----
                // Snappy and buoyant: horizontal velocity is set INSTANTLY to a fraction of walk
                // speed, vertical gravity is scaled down by how deep you are, and holding Space is a
                // hard upward thrust each frame. No velocity-damped float like Infdev's.
                double submerged = (feetInWater ? 0.25 : 0) + (bodyInWater ? 0.5 : 0) + (headInWater ? 0.25 : 0);

                // Horizontal: instant wish velocity at 42% walk speed.
                var swimSpeed = desiredDirection * (WalkSpeed * 0.42);
                playerVelocity = new Point3D(swimSpeed.X, playerVelocity.Y, swimSpeed.Z);

                // Vertical: mild per-frame drag (0.96), then depth-scaled gravity
                // (GRAVITY * max(0.16, 0.42 - submerged*0.20)) - deeper = lighter.
                playerVelocity = new Point3D(
                    playerVelocity.X,
                    playerVelocity.Y * Math.Pow(0.96, deltaSeconds * 60.0),
                    playerVelocity.Z);
                double waterGravity = Gravity * Math.Max(0.16, 0.42 - submerged * 0.20);
                playerVelocity = new Point3D(
                    playerVelocity.X,
                    playerVelocity.Y - waterGravity * deltaSeconds,
                    playerVelocity.Z);

                // Holding Space: hard upward thrust (JUMP_SPEED * 0.58 with body submerged, 0.7 in
                // shallows) - sets the upward velocity, so you bob up briskly and stay up while held.
                if (tickInput.MoveUp)
                {
                    double swimLift = bodyInWater ? 0.58 : 0.7;
                    playerVelocity = new Point3D(
                        playerVelocity.X,
                        Math.Max(playerVelocity.Y, JumpVelocity * swimLift),
                        playerVelocity.Z);
                }

                var swimDisplacement = playerVelocity * deltaSeconds;
                MovePlayerWithCollisions(swimDisplacement);

                // Walk cycle driven from horizontal speed like ground movement.
                double swimHSpeed = Math.Sqrt(playerVelocity.X * playerVelocity.X + playerVelocity.Z * playerVelocity.Z);
                playerWalkAmount = (float)Math.Min(1.0, swimHSpeed / WalkSpeed);
                playerWalkPhase += deltaSeconds * playerWalkAmount * 10f;
                return;
            }

            var horizontalVelocity = desiredDirection * WalkSpeed;
            var verticalVelocity = playerVelocity.Y;
            if (tickInput.JumpPressed && playerGrounded)
            {
                verticalVelocity = JumpVelocity;
                playerGrounded = false;
            }
            verticalVelocity -= Gravity * deltaSeconds;
            if (verticalVelocity < -MaxFallSpeed) verticalVelocity = -MaxFallSpeed;
            playerVelocity = new Point3D(horizontalVelocity.X, verticalVelocity, horizontalVelocity.Z);
            var frameDisplacement = playerVelocity * deltaSeconds;
            MovePlayerWithCollisions(frameDisplacement);

            // Drive the third-person player model's walk cycle from horizontal speed.
            double hSpeed = Math.Sqrt(playerVelocity.X * playerVelocity.X + playerVelocity.Z * playerVelocity.Z);
            playerWalkAmount = (float)Math.Min(1.0, hSpeed / WalkSpeed);
            playerWalkPhase += deltaSeconds * playerWalkAmount * 10f;
        }

        // Lazy deep-fill (Proposal A): while the player is underground (below world ~-32), fill the
        // empty deep zone of chunks near them with stone + caves. New chunks generated while deep
        // are auto-filled at generation time (provider.AutoDeepFill) so they're born with terrain.
        // Already-loaded chunks are filled once when the player first crosses below the threshold
        // (and any later-loaded-but-unfilled stragglers on subsequent frames). Cheap: DeepFillChunk
        // is idempotent (returns immediately once a chunk's zone is already stone).
        private void UpdateDeepFill()
        {
            if (manager == null || chunkProvider == null) return;
            // Terrain band is world -64..63; start filling the deep zone when the player descends
            // below -32 so there's a solid floor waiting just beneath the lowest reachable rock.
            const double deepThreshold = -32.0;
            bool isDeep = cameraPosition.Y < deepThreshold;
            chunkProvider.AutoDeepFill = isDeep;
            if (!isDeep) return;

            int cx = (int)Math.Floor(cameraPosition.X / ChunkManager.ChunkSize);
            int cz = (int)Math.Floor(cameraPosition.Z / ChunkManager.ChunkSize);

            foreach (var ch in manager.GetLoadedChunks())
            {
                int dx = ch.OriginX / ChunkManager.ChunkSize - cx;
                int dz = ch.OriginZ / ChunkManager.ChunkSize - cz;
                if (dx * dx + dz * dz > 49) continue; // within ~7 chunks of the player
                chunkProvider.DeepFillChunk(ch.OriginX / ChunkManager.ChunkSize, ch.OriginZ / ChunkManager.ChunkSize, ch);
                // Only request a remesh if this fill actually did something (idempotent check inside).
                if (ch.NeedsRemesh)
                {
                    ch.IsMeshingQueued = false;
                    meshScheduler?.RequestImmediateRemesh(new ChunkCoordinates(ch.OriginX / ChunkManager.ChunkSize, ch.OriginZ / ChunkManager.ChunkSize));
                }
            }
        }

        private void ApplyLookInput(Vector2 lookDelta)
        {
            if (!mouseLook || lookDelta.X == 0f && lookDelta.Y == 0f) return;
            cameraYaw -= lookDelta.X;
            cameraYaw = NormalizeYaw(cameraYaw);
            cameraPitch = Math.Clamp(cameraPitch - lookDelta.Y, -89f, 89f);
        }

        private void MovePlayerWithCollisions(Point3D displacement)
        {
            bool hitX = false, hitY = false, hitZ = false;
            var start = cameraPosition;
            cameraPosition = MoveAlongAxis(cameraPosition, displacement.X, Axis.X, ref hitX);
            cameraPosition = MoveAlongAxis(cameraPosition, displacement.Y, Axis.Y, ref hitY);
            cameraPosition = MoveAlongAxis(cameraPosition, displacement.Z, Axis.Z, ref hitZ);

            // Step-up onto low obstacles (slabs, stair steps): when a horizontal move is blocked
            // by something no taller than a half block, lift the player, retry the move, and
            // settle back down onto it - Minecraft's auto step-up. Full-height walls still block.
            // (No !hitY gate: a grounded player collides downward every frame, so that would
            // disable stepping entirely.)
            if (hitX || hitZ)
            {
                var stepped = TryStepUp(start, displacement);
                if (stepped.HasValue)
                {
                    cameraPosition = stepped.Value;
                    hitX = hitZ = false;
                    hitY = true;
                    playerGrounded = true;
                }
            }

            if (hitX) playerVelocity = new Point3D(0, playerVelocity.Y, playerVelocity.Z);
            if (hitZ) playerVelocity = new Point3D(playerVelocity.X, playerVelocity.Y, 0);
            if (hitY)
            {
                if (playerVelocity.Y <= 0) playerGrounded = true;
                playerVelocity = new Point3D(playerVelocity.X, 0, playerVelocity.Z);
            }
            else playerGrounded = false;
        }

        // Samples a single block at the player's X/Z and feet+offset (matching Cubuild's
        // playerFeetInWater/playerBodyInWater/playerHeadInWater: sample at pos.y + 0.05,
        // + HEIGHT*0.4, + HEIGHT*0.85, where pos.y is the FEET). Our cameraPosition is the eye,
        // so feet = eye - EyeHeight.
        private bool PlayerSampleInWater(double heightOffset)
        {
            int id = BlockRegistry.GetId("water");
            int x = (int)Math.Floor(cameraPosition.X);
            int y = (int)Math.Floor(cameraPosition.Y - EyeHeight + heightOffset);
            int z = (int)Math.Floor(cameraPosition.Z);
            return manager.TryGetLoadedBlock(x, y, z, out var block) && block == id;
        }

        // Auto step-up: raise the player up to MaxStepHeight, retry the horizontal move, then
        // settle down onto whatever's beneath. Returns the stepped position, or null if the
        // obstacle is taller than a step (full wall) or there's no headroom.
        private Point3D? TryStepUp(Point3D start, Point3D displacement)
        {
            const double maxStepHeight = 0.5;
            var raised = new Point3D(start.X, start.Y + maxStepHeight, start.Z);
            if (IsPlayerColliding(raised)) return null;

            bool hx = false, hz = false;
            var moved = MoveAlongAxis(raised, displacement.X, Axis.X, ref hx);
            moved = MoveAlongAxis(moved, displacement.Z, Axis.Z, ref hz);
            if (hx || hz) return null; // still blocked after lifting -> full-height obstacle

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

        private bool IsPlayerColliding(Point3D eyePosition)
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
                if (manager.TryGetLoadedBlockAndMeta(x, y, z, out var block, out var meta) && BlockRegistry.IsSolid(block))
                {
                    if (BoxesOverlapPlayer(GetBlockCollisionBoxes(block, meta), x, y, z, minX, maxX, minY, maxY, minZ, maxZ))
                        return true;
                }
            }
            return false;
        }

        // Collision boxes for a block (full cube, or partial boxes for slabs/stairs). Boxes are
        // in 0..1 block-relative units, matching the mesher's special-solid geometry. Also used
        // for ray picking: cross plants get a small centered box so you can aim past them.
        private static (double minX, double minY, double minZ, double maxX, double maxY, double maxZ)[] GetBlockCollisionBoxes(int id, int meta)
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
            if (BlockRegistry.IsCross(id)) return new[] { (0.25, 0.0, 0.25, 0.75, 0.8, 0.75) };
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

        private bool EnsureVisibleChunks()
        {
            int chunkX = WorldToChunkCoord(cameraPosition.X);
            int chunkZ = WorldToChunkCoord(cameraPosition.Z);
            return manager.EnsureChunksAround(chunkX, chunkZ, SpawnSyncRadius);
        }

        private void PlaceCameraAtSafeSpawn()
        {
            var spawn = FindSafeSpawnPosition();
            if (spawn.HasValue) cameraPosition = spawn.Value;
            playerVelocity = new Point3D(0, 0, 0);
            playerGrounded = true;
        }

        private Point3D? FindSafeSpawnPosition()
        {
            int baseX = (int)Math.Floor(cameraPosition.X);
            int baseZ = (int)Math.Floor(cameraPosition.Z);
            // Scan outward ring by ring (generating chunks as needed) and take the FIRST ring
            // that has dry land - the surface must be at or above sea level, so the player never
            // spawns underwater. Within a ring, the highest dry spot wins (solid footing, not a
            // cliff edge). Mirrors Infdev's "find a beach" spawn spirit.
            const int seaLevelWorldY = 0;
            for (int radius = 0; radius <= 64; radius++)
            {
                int bestY = int.MinValue, bestX = 0, bestZ = 0;
                for (int dx = -radius; dx <= radius; dx++)
                for (int dz = -radius; dz <= radius; dz++)
                {
                    if (radius > 0 && Math.Abs(dx) != radius && Math.Abs(dz) != radius) continue;
                    int wx = baseX + dx;
                    int wz = baseZ + dz;
                    manager.GetOrCreateChunk(WorldToChunkCoord(wx), WorldToChunkCoord(wz));
                    int surfaceY = FindSurfaceWorldY(wx, wz);
                    if (surfaceY < seaLevelWorldY) continue; // submerged - keep searching
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

        // Topmost solid block in a column, in world Y (-64..191). Returns OriginY-1 if empty.
        private int FindSurfaceWorldY(int wx, int wz)
        {
            for (int wy = 191; wy >= ChunkManager.WorldOriginY; wy--)
            {
                if (manager.TryGetLoadedBlock(wx, wy, wz, out var block) && BlockRegistry.IsSolid(block))
                {
                    return wy;
                }
            }
            return ChunkManager.WorldOriginY - 1;
        }

        private static int WorldToChunkCoord(double value) => (int)Math.Floor(value / ChunkManager.ChunkSize);

        private void DeleteHighlightedBlock()
        {
            var pickResult = TryPickBlock(cameraPosition, GetCameraForward());
            if (!pickResult.HasValue) return;
            var remove = pickResult.Value.Remove;
            if (!manager.TryGetLoadedBlock(remove.x, remove.y, remove.z, out var blockId)) return;
            // Little chunks of the block's tile fly out as it breaks.
            gpuRenderer?.SpawnBlockBreakParticles(remove.x, remove.y, remove.z, blockId, 12);
            if (!manager.TrySetBlock(remove.x, remove.y, remove.z, BlockRegistry.AirId)) return;
            blockTickScheduler?.OnBlockChanged(remove.x, remove.y, remove.z);
            var editedChunk = new ChunkCoordinates(WorldToChunkCoord(remove.x), WorldToChunkCoord(remove.z));
            meshScheduler.RequestImmediateRemesh(editedChunk);
            needsMeshUpdate = true;
        }

        private void PlaceSelectedBlock()
        {
            var pickResult = TryPickBlock(cameraPosition, GetCameraForward(), out double hitDistance);
            if (!pickResult.HasValue) return;
            var place = pickResult.Value.Place;
            var normal = pickResult.Value.Normal;
            var hitPoint = cameraPosition + GetCameraForward() * hitDistance;

            int blockToPlace = selectedBlock;
            int meta = 0;

            if (BlockRegistry.IsSlab(blockToPlace) || BlockRegistry.IsSlabTop(blockToPlace))
            {
                // Same-material slab merge: hitting the top of a bottom slab (or bottom of a top
                // slab) turns it into the full material block - Cubuild's mergeTo behavior.
                var hit = pickResult.Value.Remove;
                if (TryMergeSlab(hit.x, hit.y, hit.z, normal, blockToPlace)) return;

                // Top vs bottom slab by where the ray hits the target cell.
                bool placeTop = normal.Y < 0 || (normal.Y == 0 && (hitPoint.Y - place.y) > 0.5);
                if (BlockRegistry.IsSlab(blockToPlace) && placeTop)
                {
                    blockToPlace = SlabTopIdFor(blockToPlace);
                }

                // The shape-aware pick can land on an ALREADY OCCUPIED cell (the ray passed
                // through the empty half of a partial block). Filling the opposite half of a
                // same-material slab merges into a full block; water is replaceable (placing
                // displaces it, like MC); anything else can't be placed into.
                if (manager.TryGetLoadedBlockAndMeta(place.x, place.y, place.z, out var oldId, out _)
                    && oldId != BlockRegistry.AirId && !IsReplaceableFluid(oldId))
                {
                    if (TryFillSlabCell(place.x, place.y, place.z, blockToPlace)) return;
                    return; // occupied by a block we can't merge with
                }
            }
            else if (BlockRegistry.IsStair(blockToPlace))
            {
                meta = StairFacingMeta();
            }
            else
            {
                // General safety: never overwrite an occupied cell (water is replaceable).
                if (manager.TryGetLoadedBlockAndMeta(place.x, place.y, place.z, out var occupied, out _)
                    && occupied != BlockRegistry.AirId && !IsReplaceableFluid(occupied))
                {
                    return;
                }
            }

            if (WouldBlockIntersectPlayer(place.x, place.y, place.z, blockToPlace, meta)) return;
            if (!manager.TrySetBlock(place.x, place.y, place.z, blockToPlace, meta)) return;
            blockTickScheduler?.OnBlockChanged(place.x, place.y, place.z);
            var editedChunk = new ChunkCoordinates(WorldToChunkCoord(place.x), WorldToChunkCoord(place.z));
            meshScheduler.RequestImmediateRemesh(editedChunk);
            // Priority queue ensures background worker updates this chunk quickly (within ~1-2 frames).
            // No immediate rendering to avoid visual artifacts from mesh replacement.
            needsMeshUpdate = true;
        }

        // Merges a hit same-material slab into its full block when the face placement matches
        // (top of a bottom slab, bottom of a top slab). Returns true if the merge happened.
        private bool TryMergeSlab(int x, int y, int z, Point3D normal, int heldBlock)
        {
            if (!manager.TryGetLoadedBlockAndMeta(x, y, z, out var hitId, out _)) return false;
            if (!BlockRegistry.IsSlab(hitId) && !BlockRegistry.IsSlabTop(hitId)) return false;
            if (SlabMaterialOf(hitId) != SlabMaterialOf(heldBlock)) return false;
            if (!((BlockRegistry.IsSlab(hitId) && normal.Y > 0) || (BlockRegistry.IsSlabTop(hitId) && normal.Y < 0))) return false;

            int fullId = BlockRegistry.GetId(SlabMaterialOf(hitId));
            if (!manager.TrySetBlock(x, y, z, fullId, 0)) return false;
            blockTickScheduler?.OnBlockChanged(x, y, z);
            meshScheduler.RequestImmediateRemesh(new ChunkCoordinates(WorldToChunkCoord(x), WorldToChunkCoord(z)));
            needsMeshUpdate = true;
            return true;
        }

        // Fills the empty half of an existing same-material slab with the slab being placed
        // (opposite halves only), turning the cell into the full material block. Returns false if
        // the cell isn't a mergeable opposite-half slab.
        private bool TryFillSlabCell(int x, int y, int z, int placingId)
        {
            if (!manager.TryGetLoadedBlockAndMeta(x, y, z, out var oldId, out _)) return false;
            if (!BlockRegistry.IsSlab(oldId) && !BlockRegistry.IsSlabTop(oldId)) return false;
            if (SlabMaterialOf(oldId) != SlabMaterialOf(placingId)) return false;
            bool oldTop = BlockRegistry.IsSlabTop(oldId);
            bool newTop = BlockRegistry.IsSlabTop(placingId);
            if (oldTop == newTop) return false; // same half -> can't stack

            int fullId = BlockRegistry.GetId(SlabMaterialOf(oldId));
            if (!manager.TrySetBlock(x, y, z, fullId, 0)) return false;
            blockTickScheduler?.OnBlockChanged(x, y, z);
            meshScheduler.RequestImmediateRemesh(new ChunkCoordinates(WorldToChunkCoord(x), WorldToChunkCoord(z)));
            needsMeshUpdate = true;
            return true;
        }

        // Fluids that blocks can be placed into (displacing them), but that can't be picked/dug.
        private static bool IsReplaceableFluid(int id) => id == BlockRegistry.GetId("water");

        private static string SlabMaterialOf(int id)
        {
            string name = BlockRegistry.GetName(id);
            return name.EndsWith("_slab_top", StringComparison.Ordinal)
                ? name[..^"_slab_top".Length]
                : name.EndsWith("_slab", StringComparison.Ordinal) ? name[..^"_slab".Length] : name;
        }

        private static int SlabTopIdFor(int slabId)
            => BlockRegistry.GetId(SlabMaterialOf(slabId) + "_slab_top");

        // Stair facing from the player's horizontal look direction. The stair's low step is
        // toward the player, so the high step rises in the direction the player faces.
        private int StairFacingMeta()
        {
            float yawRad = cameraYaw * (float)Math.PI / 180f;
            double dirX = Math.Sin(yawRad);
            double dirZ = Math.Cos(yawRad);
            if (Math.Abs(dirX) > Math.Abs(dirZ))
                return dirX > 0 ? 0 : 1; // east / west
            return dirZ > 0 ? 2 : 3;     // south / north
        }

        private bool WouldBlockIntersectPlayer(int x, int y, int z, int blockId, int meta)
        {
            double minX = cameraPosition.X - PlayerRadius;
            double maxX = cameraPosition.X + PlayerRadius;
            double minY = cameraPosition.Y - EyeHeight;
            double maxY = minY + PlayerHeight;
            double minZ = cameraPosition.Z - PlayerRadius;
            double maxZ = cameraPosition.Z + PlayerRadius;
            return BoxesOverlapPlayer(GetBlockCollisionBoxes(blockId, meta), x, y, z, minX, maxX, minY, maxY, minZ, maxZ);
        }

        private static float NormalizeYaw(float yaw)
        {
            float result = yaw % 360f;
            if (result < 0f) result += 360f;
            return result;
        }

        private static string GetCompassDirection(float yaw)
        {
            float normalized = NormalizeYaw(yaw);
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
                if (manager != null) gpuRenderer.SetChunkManager(manager);
                if (window != null) gpuRenderer.Resize(window.Width, window.Height);
                if (manager != null)
                {
                    var loaded = manager.GetLoadedChunks();
                    foreach (var ch in loaded)
                    {
                        if (ch.MeshFaces != null && ch.MeshFaces.Count > 0)
                        {
                            int chunkX = ch.OriginX / ChunkManager.ChunkSize;
                            int chunkZ = ch.OriginZ / ChunkManager.ChunkSize;
                            gpuRenderer.UploadChunk(new ChunkCoordinates(chunkX, chunkZ), ch.MeshFaces);
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

        private HudState BuildHud()
        {
            // Before a world exists (title/create screens) there's nothing to pick or highlight.
            if (manager == null)
            {
                return new HudState
                {
                    ShowDebug = showFps, FlyMode = flyMode, Menu = menu, Fps = lastFps,
                    UpdateMs = lastUpdateMs, MeshMs = lastMeshMs, UploadMs = lastUploadMs, RenderMs = lastRenderMs,
                    SelectedBlockText = "Selected: -",
                    SelectedSlot = selectedSlot, WorldSeed = worldSeed,
                    Hotbar = _hotbarBlocks,
                    PlayerX = cameraPosition.X, PlayerY = cameraPosition.Y, PlayerZ = cameraPosition.Z,
                    RenderDistance = ChunkRenderRadius,
                };
            }
            var forward = GetCameraForward();
            var pickResult = TryPickBlock(cameraPosition, forward);
            Vector3[]? highlightQuad = null;
            if (pickResult.HasValue) highlightQuad = ComputeHighlightWorldQuad(pickResult.Value);
            return new HudState
            {
                ShowDebug = showFps, InventoryOpen = inventoryOpen, FlyMode = flyMode, Menu = menu, Fps = lastFps, UpdateMs = lastUpdateMs,
                MeshMs = lastMeshMs, UploadMs = lastUploadMs, RenderMs = lastRenderMs,
                FacingText = $"{GetCompassDirection(cameraYaw)} ({NormalizeYaw(cameraYaw):0.0} deg)",
                SelectedBlockText = $"Selected: {BlockRegistry.GetName(selectedBlock)}",
                RenderDistanceText = $"Render dist: {RenderDistanceName} ({ChunkRenderRadius})",
                SelectedSlot = selectedSlot, WorldSeed = worldSeed,
                BiomeText = chunkProvider?.BiomeNameAt((int)Math.Floor(cameraPosition.X), (int)Math.Floor(cameraPosition.Z)) ?? string.Empty,
                Hotbar = _hotbarBlocks, HighlightWorldQuad = highlightQuad,
                PlayerX = cameraPosition.X,
                PlayerY = cameraPosition.Y,
                PlayerZ = cameraPosition.Z,
                PlayerChunkX = WorldToChunkCoord(cameraPosition.X),
                PlayerChunkZ = WorldToChunkCoord(cameraPosition.Z),
                RenderDistance = ChunkRenderRadius,
            };
        }

        private Vector3[]? ComputeHighlightWorldQuad(PickBlockResult hit)
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

        /// <summary>
        /// Snapshot of the local player for the third-person model: feet position, body yaw in
        /// radians and the walk-cycle state tracked in <see cref="UpdatePlayerMovement"/>.
        /// </summary>
        private MobRenderData BuildLocalPlayerRenderData()
        {
            var feet = new Point3D(cameraPosition.X, cameraPosition.Y - EyeHeight, cameraPosition.Z);
            float yawRad = cameraYaw * (float)Math.PI / 180f;
            return new MobRenderData(
                "player", feet, yawRad, 0f,
                playerWalkPhase, playerWalkAmount, 0f,
                (float)playerVelocity.Y, playerGrounded,
                false, 0f, 0f, 0f);
        }

        /// <summary>
        /// Third-person camera: pull back along the view ray up to 4 blocks, stopping short of the
        /// first solid block so the camera never clips into terrain.
        /// </summary>
        private Point3D GetThirdPersonCameraPosition()
        {
            var forward = GetCameraForward();
            const double maxDist = 4.0;
            const double step = 0.1;
            double dist = 0.0;
            while (dist < maxDist)
            {
                double next = Math.Min(maxDist, dist + step);
                var p = cameraPosition - forward * next;
                int bx = (int)Math.Floor(p.X);
                int by = (int)Math.Floor(p.Y);
                int bz = (int)Math.Floor(p.Z);
                if (manager.TryGetLoadedBlock(bx, by, bz, out var block) && BlockRegistry.IsSolid(block))
                {
                    break;
                }
                dist = next;
            }
            dist = Math.Max(0.0, dist - 0.2);
            return cameraPosition - forward * dist;
        }

        private Point3D GetCameraForward()
        {
            var yawRad = cameraYaw * Math.PI / 180.0;
            var pitchRad = cameraPitch * Math.PI / 180.0;
            var cosPitch = Math.Cos(pitchRad);
            return new Point3D(cosPitch * Math.Sin(yawRad), Math.Sin(pitchRad), cosPitch * Math.Cos(yawRad)).Normalized();
        }

        private static Point3D GetCameraRight(float yaw)
        {
            var yawRad = yaw * Math.PI / 180.0;
            return new Point3D(Math.Cos(yawRad), 0, -Math.Sin(yawRad)).Normalized();
        }

        private static double Dot(Point3D a, Point3D b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        private static Point3D Cross(Point3D a, Point3D b) => new Point3D(
            a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        private PickBlockResult? TryPickBlock(Point3D origin, Point3D direction) => TryPickBlock(origin, direction, out _);
        private PickBlockResult? TryPickBlock(Point3D origin, Point3D direction, out double hitDistance)
        {
            hitDistance = double.PositiveInfinity;
            direction = direction.Normalized();
            int blockX = (int)Math.Floor(origin.X);
            int blockY = (int)Math.Floor(origin.Y);
            int blockZ = (int)Math.Floor(origin.Z);
            var stepX = Math.Sign(direction.X);
            var stepY = Math.Sign(direction.Y);
            var stepZ = Math.Sign(direction.Z);
            var tDeltaX = stepX != 0 ? Math.Abs(1.0 / direction.X) : double.PositiveInfinity;
            var tDeltaY = stepY != 0 ? Math.Abs(1.0 / direction.Y) : double.PositiveInfinity;
            var tDeltaZ = stepZ != 0 ? Math.Abs(1.0 / direction.Z) : double.PositiveInfinity;
            var tMaxX = stepX > 0 ? (blockX + 1.0 - origin.X) * tDeltaX : (origin.X - blockX) * tDeltaX;
            var tMaxY = stepY > 0 ? (blockY + 1.0 - origin.Y) * tDeltaY : (origin.Y - blockY) * tDeltaY;
            var tMaxZ = stepZ > 0 ? (blockZ + 1.0 - origin.Z) * tDeltaZ : (origin.Z - blockZ) * tDeltaZ;
            int currentX = blockX, currentY = blockY, currentZ = blockZ;
            var maxDistance = BlockReach;
            var distance = 0.0;
            for (int iteration = 0; iteration < 400 && distance <= maxDistance; iteration++)
            {
                // Test the block's ACTUAL shape (full cube, slab half-box, stair boxes, small
                // cross-plant box). A ray that passes through the empty part of a partial block
                // (e.g. the air above a bottom slab) continues to the next cell instead of
                // stopping at the cell. Fluids (water) are never pickable - the ray passes
                // straight through to the block behind/underneath, like Infdev.
                if (manager.TryGetLoadedBlockAndMeta(currentX, currentY, currentZ, out var block, out var meta)
                    && block != BlockRegistry.AirId
                    && block != BlockRegistry.GetId("water"))
                {
                    double cellExit = Math.Min(tMaxX, Math.Min(tMaxY, tMaxZ));
                    var boxes = GetBlockCollisionBoxes(block, meta);
                    foreach (var b in boxes)
                    {
                        if (RayBoxHit(origin, direction,
                                currentX + b.minX, currentY + b.minY, currentZ + b.minZ,
                                currentX + b.maxX, currentY + b.maxY, currentZ + b.maxZ,
                                distance - 1e-9, cellExit + 1e-9, out double t, out var n))
                        {
                            hitDistance = Math.Max(0.0, t);
                            var face = ComputeFaceRect(currentX, currentY, currentZ, b, n);
                            var place = ((int)Math.Floor(currentX + n.X + 0.5), (int)Math.Floor(currentY + n.Y + 0.5), (int)Math.Floor(currentZ + n.Z + 0.5));
                            return new PickBlockResult((currentX, currentY, currentZ), place, n, face);
                        }
                    }
                }

                if (tMaxX < tMaxY)
                {
                    if (tMaxX < tMaxZ) { currentX += stepX; distance = tMaxX; tMaxX += tDeltaX; }
                    else { currentZ += stepZ; distance = tMaxZ; tMaxZ += tDeltaZ; }
                }
                else
                {
                    if (tMaxY < tMaxZ) { currentY += stepY; distance = tMaxY; tMaxY += tDeltaY; }
                    else { currentZ += stepZ; distance = tMaxZ; tMaxZ += tDeltaZ; }
                }
            }
            return null;
        }

        public void Dispose()
        {
            try { chunkGenWorker?.Dispose(); } catch { }
            try { meshWorker?.Dispose(); } catch { }
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

        private readonly struct PickBlockResult
        {
            public (int x, int y, int z) Remove { get; }
            public (int x, int y, int z) Place { get; }
            public Point3D Normal { get; }
            /// <summary>World-space rectangle of the actual face that was hit (matches the block's
            /// real shape, e.g. a slab's top at y+0.5 or a stair riser).</summary>
            public (double minX, double minY, double minZ, double maxX, double maxY, double maxZ) Face { get; }
            public PickBlockResult((int x, int y, int z) remove, (int x, int y, int z) place, Point3D normal,
                (double minX, double minY, double minZ, double maxX, double maxY, double maxZ) face)
            {
                Remove = remove; Place = place; Normal = normal; Face = face;
            }
        }

        // Ray vs axis-aligned box, restricted to [tMinLimit, tMaxLimit] (the cell the ray is
        // currently crossing). Returns the entry distance and the face normal of the box entered.
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

        // The rectangle (on the face plane) of a block box face, in world coordinates - used for
        // the targeted-face highlight on partial shapes.
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

        private enum Axis
        {
            X,
            Y,
            Z,
        }
    }
}