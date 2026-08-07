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
        private const float MouseSensitivity = 0.5f;
        private const double MaxFrameDeltaSeconds = 0.25;
        private static readonly int[] RenderDistances = { 16, 8, 4, 2 };
        private static readonly string[] RenderDistanceNames = { "Far", "Normal", "Short", "Tiny" };
        private int renderDistanceIndex = 0; // Far by default, like Infdev
        private int ChunkRenderRadius => RenderDistances[renderDistanceIndex];
        private string RenderDistanceName => RenderDistanceNames[renderDistanceIndex];
        private int _ignoreInteractFrames; // skips break/place right after a menu click
        private GameScreen screen = GameScreen.Title;
        private readonly MenuState menu = new();
        private bool inventoryOpen;

        /// <summary>The simulation world (null on the title screen).</summary>
        public GameWorld World { get; private set; }

        public Program()
        {
            BlockRegistry.LoadDefault();
            MobRegistry.DiscoverMobs(AppDomain.CurrentDomain.BaseDirectory);
            RefreshSavedWorlds();
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
                gpuRenderer.ResetWorld();
            }
            World.EnsureVisibleChunks();
            World.PlaceCameraAtSafeSpawn();
            _lastMeshPosition = World.PlayerPosition;
            World.Mesher.Update();
            screen = GameScreen.Playing;
            menu.Screen = GameScreen.Playing;
            _ignoreInteractFrames = 2;
            EnableMouseLook();
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

        private void ReturnToTitle()
        {
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
            StartNewWorld(save.Seed, save.Name);
            foreach (var c in save.Chunks)
            {
                World.Chunks.ApplySavedChunk(c.X, c.Z, c.Blocks, c.Meta);
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
                    StepSimulation(input.CaptureTickInput(), (float)deltaSeconds);
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
                            var withPlayer = new List<MobRenderData>(World.Entities.MobRenderData.Count + 1);
                            withPlayer.AddRange(World.Entities.MobRenderData);
                            if (thirdPersonView) withPlayer.Add(BuildLocalPlayerRenderData());
                            gpuRenderer.SetEntities(withPlayer);
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
                if (frameInput.ToggleInventoryPressed)
                {
                    inventoryOpen = !inventoryOpen;
                    if (inventoryOpen) DisableMouseLook();
                    else EnableMouseLook();
                }
            }
            if (frameInput.CycleRenderDistancePressed) CycleRenderDistance();
            if (screen == GameScreen.Playing && World != null)
            {
                if (frameInput.SpawnMobPressed) World.Entities.SpawnDuck(World.PlayerPosition, World.PlayerYaw);
                if (frameInput.SpawnCoyotePressed) World.Entities.SpawnCoyote(World.PlayerPosition, World.PlayerYaw);
                if (frameInput.SpawnStevePressed) World.Entities.SpawnSteve(World.PlayerPosition, World.PlayerYaw);
            }
            if (frameInput.ToggleThirdPersonPressed) thirdPersonView = !thirdPersonView;
            if (frameInput.SelectedSlot.HasValue && World != null) World.SetSelectedSlot(frameInput.SelectedSlot.Value);
            if (screen == GameScreen.Playing && World != null && _ignoreInteractFrames == 0 && frameInput.BreakBlockPressed)
            {
                if (!World.Entities.TryAttackMob(World.PlayerPosition, World.GetCameraForward(), null))
                {
                    DeleteHighlightedBlock();
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
            if (screen != GameScreen.Playing || World == null) return;
            World.StepSimulation(tickInput, deltaSeconds);
        }

        private void ApplyLookInput(Vector2 lookDelta)
        {
            if (!mouseLook || lookDelta.X == 0f && lookDelta.Y == 0f) return;
            World?.ApplyLookInput(lookDelta);
        }

        // ------------------------------------------------------------------
        // block interaction (render-layer effects: particles + immediate meshes)
        // ------------------------------------------------------------------

        private void DeleteHighlightedBlock()
        {
            if (World == null) return;
            if (!World.TryBreakBlock(World.PlayerPosition, World.GetCameraForward(), out int removedBlockId, out var removedPos)) return;
            gpuRenderer?.SpawnBlockBreakParticles(removedPos.x, removedPos.y, removedPos.z, removedBlockId, 12);
            needsMeshUpdate = true;
        }

        private void PlaceSelectedBlock()
        {
            if (World == null) return;
            if (World.TryPlaceSelectedBlock(World.PlayerPosition, World.GetCameraForward()))
            {
                needsMeshUpdate = true;
            }
        }

        // ------------------------------------------------------------------
        // HUD / camera helpers (read world state; no sim logic)
        // ------------------------------------------------------------------

        private HudState BuildHud()
        {
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
                };
            }
            var forward = World.GetCameraForward();
            var pickResult = World.TryPickBlock(World.PlayerPosition, forward);
            Vector3[]? highlightQuad = null;
            if (pickResult.HasValue) highlightQuad = ComputeHighlightWorldQuad(pickResult.Value);
            return new HudState
            {
                ShowDebug = showFps, InventoryOpen = inventoryOpen, FlyMode = World.FlyMode, Menu = menu, Fps = lastFps, UpdateMs = lastUpdateMs,
                MeshMs = lastMeshMs, UploadMs = lastUploadMs, RenderMs = lastRenderMs,
                FacingText = $"{GetCompassDirection(World.PlayerYaw)} ({GameWorld.NormalizeYaw(World.PlayerYaw):0.0} deg)",
                SelectedBlockText = $"Selected: {BlockRegistry.GetName(World.SelectedBlock)}",
                RenderDistanceText = $"Render dist: {RenderDistanceName} ({ChunkRenderRadius})",
                SelectedSlot = World.SelectedSlot, WorldSeed = World.Seed,
                BiomeText = World.ChunkProvider?.BiomeNameAt((int)Math.Floor(World.PlayerPosition.X), (int)Math.Floor(World.PlayerPosition.Z)) ?? string.Empty,
                Hotbar = World.Hotbar, HighlightWorldQuad = highlightQuad,
                PlayerX = World.PlayerPosition.X,
                PlayerY = World.PlayerPosition.Y,
                PlayerZ = World.PlayerPosition.Z,
                PlayerChunkX = GameWorld.WorldToChunkCoord(World.PlayerPosition.X),
                PlayerChunkZ = GameWorld.WorldToChunkCoord(World.PlayerPosition.Z),
                RenderDistance = ChunkRenderRadius,
            };
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

        /// <summary>
        /// Snapshot of the local player for the third-person model: feet position, body yaw in
        /// radians and the walk-cycle state tracked in GameWorld.
        /// </summary>
        private MobRenderData BuildLocalPlayerRenderData()
        {
            var w = World;
            var feet = new Point3D(w.PlayerPosition.X, w.PlayerPosition.Y - GameWorld.EyeHeight, w.PlayerPosition.Z);
            float yawRad = w.PlayerYaw * (float)Math.PI / 180f;
            return new MobRenderData(
                "player", feet, yawRad, 0f,
                w.PlayerWalkPhase, w.PlayerWalkAmount, 0f,
                (float)w.PlayerVelocity.Y, w.PlayerGrounded,
                false, 0f, 0f, 0f);
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
