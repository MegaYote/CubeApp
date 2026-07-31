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
        private readonly ChunkManager manager;
        private IRenderer? gpuRenderer;
        private MeshWorker? meshWorker;
        private MeshScheduler meshScheduler;
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
        private int renderDistanceIndex = 1;
        private int ChunkRenderRadius => RenderDistances[renderDistanceIndex];
        private string RenderDistanceName => RenderDistanceNames[renderDistanceIndex];
        private const int SpawnSyncRadius = 2;
        private int selectedBlock = 0; // numeric block id (BlockRegistry), set in ctor once the registry is loaded
        private int selectedSlot;
        private const int HotbarSlots = 10;
        private bool inventoryOpen;
        private bool thirdPersonView;
        private float playerWalkPhase;
        private float playerWalkAmount;
        private readonly EntityManager entityManager;

        public Program()
        {
            // Load block definitions first - chunks, terrain gen, mesher and the hotbar all read
            // numeric ids out of the registry, so it has to be ready before any block is touched.
            BlockRegistry.LoadDefault();
            selectedBlock = Math.Max(0, BlockRegistry.Hotbar.Count > 0 ? BlockRegistry.Hotbar[0] : 0);
            manager = new ChunkManager(new InfdevChunkProvider(20100630));
            entityManager = new EntityManager(manager);
            MobRegistry.DiscoverMobs(AppDomain.CurrentDomain.BaseDirectory);
            EnsureVisibleChunks();
            PlaceCameraAtSafeSpawn();
            meshWorker = new MeshWorker(manager, () => gpuRenderer);
            meshScheduler = new MeshScheduler(manager, meshWorker);
            meshScheduler.Update();
            int genWorkers = Math.Max(1, Environment.ProcessorCount - 2);
            chunkGenWorker = new ChunkGenWorker(manager, () => needsMeshUpdate = true, genWorkers);
            _lastMeshPosition = cameraPosition;
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
            EnableMouseLook();
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
            if (needsMeshUpdate)
            {
                meshScheduler.Update();
                needsMeshUpdate = false;
            }
            var t3 = stageStopwatch.ElapsedTicks;
            lastMeshMs = (t3 - t2) * 1000f / Stopwatch.Frequency;
            lastUploadMs = 0f;
            var t4 = stageStopwatch.ElapsedTicks;
            if (gpuRenderer != null)
            {
                gpuRenderer.UpdateCamera(thirdPersonView ? GetThirdPersonCameraPosition() : cameraPosition, cameraYaw, cameraPitch);
                gpuRenderer.SetHud(BuildHud());
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
                // Player edits already mesh immediately via MeshChunkImmediate();
                // Background MeshWorker handles all other meshing.
                gpuRenderer.ProcessPendingPriorityMeshes();
                gpuRenderer.Render();
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
        }

        private void ApplyFrameInput(FrameInputState frameInput)
        {
            if (frameInput.ToggleMouseCapturePressed) DisableMouseLook();
            if (!mouseLook && (frameInput.BreakBlockPressed || frameInput.PlaceBlockPressed))
            {
                EnableMouseLook();
                return;
            }
            if (frameInput.ToggleDebugPressed) showFps = !showFps;
            if (frameInput.ToggleInventoryPressed) inventoryOpen = !inventoryOpen;
            if (frameInput.CycleRenderDistancePressed) CycleRenderDistance();
            if (frameInput.SpawnMobPressed) SpawnDuck();
            if (frameInput.SpawnCoyotePressed) SpawnCoyote();
            if (frameInput.SpawnStevePressed) SpawnSteve();
            if (frameInput.ToggleThirdPersonPressed) thirdPersonView = !thirdPersonView;
            if (frameInput.SelectedSlot.HasValue) SetSelectedSlot(frameInput.SelectedSlot.Value);
            if (frameInput.BreakBlockPressed)
            {
                if (!entityManager.TryAttackMob(cameraPosition, GetCameraForward(), null))
                {
                    DeleteHighlightedBlock();
                }
            }
            if (frameInput.PlaceBlockPressed) PlaceSelectedBlock();
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
            if (slot < BlockRegistry.Hotbar.Count) selectedBlock = BlockRegistry.Hotbar[slot];
        }

        private void StepSimulation(TickInputState tickInput, float deltaSeconds)
        {
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
            var forward = GetCameraForward();
            var forwardHorizontal = new Point3D(forward.X, 0, forward.Z).Normalized();
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
            cameraPosition = MoveAlongAxis(cameraPosition, displacement.X, Axis.X, ref hitX);
            cameraPosition = MoveAlongAxis(cameraPosition, displacement.Y, Axis.Y, ref hitY);
            cameraPosition = MoveAlongAxis(cameraPosition, displacement.Z, Axis.Z, ref hitZ);
            if (hitX) playerVelocity = new Point3D(0, playerVelocity.Y, playerVelocity.Z);
            if (hitZ) playerVelocity = new Point3D(playerVelocity.X, playerVelocity.Y, 0);
            if (hitY)
            {
                if (playerVelocity.Y <= 0) playerGrounded = true;
                playerVelocity = new Point3D(playerVelocity.X, 0, playerVelocity.Z);
            }
            else playerGrounded = false;
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
                if (manager.TryGetLoadedBlock(x, y, z, out var block) && block != BlockRegistry.AirId)
                    return true;
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
            for (int radius = 0; radius <= 6; radius++)
            for (int dx = -radius; dx <= radius; dx++)
            for (int dz = -radius; dz <= radius; dz++)
            {
                if (radius > 0 && Math.Abs(dx) != radius && Math.Abs(dz) != radius) continue;
                int wx = baseX + dx;
                int wz = baseZ + dz;
                int highestSolidY = -1;
                for (int y = ChunkManager.ChunkHeight - 1; y >= 0; y--)
                {
                    if (manager.TryGetLoadedBlock(wx, y, wz, out var block) && block != BlockRegistry.AirId)
                    {
                        highestSolidY = y;
                        break;
                    }
                }
                if (highestSolidY < 0) continue;
                double px = wx + 0.5;
                double pz = wz + 0.5;
                double minEyeY = highestSolidY + EyeHeight + 0.01;
                double maxEyeY = ChunkManager.ChunkHeight + 1.0;
                for (double eyeY = minEyeY; eyeY <= maxEyeY; eyeY += 0.25)
                {
                    var candidate = new Point3D(px, eyeY, pz);
                    if (!IsPlayerColliding(candidate)) return candidate;
                }
            }
            return null;
        }

        private static int WorldToChunkCoord(double value) => (int)Math.Floor(value / ChunkManager.ChunkSize);

        private void DeleteHighlightedBlock()
        {
            var pickResult = TryPickBlock(cameraPosition, GetCameraForward());
            if (!pickResult.HasValue) return;
            var remove = pickResult.Value.Remove;
            if (!manager.TrySetBlock(remove.x, remove.y, remove.z, BlockRegistry.AirId)) return;
            var editedChunk = new ChunkCoordinates(WorldToChunkCoord(remove.x), WorldToChunkCoord(remove.z));
            meshScheduler.RequestImmediateRemesh(editedChunk);
            needsMeshUpdate = true;
        }

        private void PlaceSelectedBlock()
        {
            var pickResult = TryPickBlock(cameraPosition, GetCameraForward());
            if (!pickResult.HasValue) return;
            var place = pickResult.Value.Place;
            if (WouldBlockIntersectPlayer(place.x, place.y, place.z)) return;
            if (!manager.TrySetBlock(place.x, place.y, place.z, selectedBlock)) return;
            var editedChunk = new ChunkCoordinates(WorldToChunkCoord(place.x), WorldToChunkCoord(place.z));
            meshScheduler.RequestImmediateRemesh(editedChunk);
            // Priority queue ensures background worker updates this chunk quickly (within ~1-2 frames).
            // No immediate rendering to avoid visual artifacts from mesh replacement.
            needsMeshUpdate = true;
        }

        private bool WouldBlockIntersectPlayer(int x, int y, int z)
        {
            double minX = cameraPosition.X - PlayerRadius;
            double maxX = cameraPosition.X + PlayerRadius;
            double minY = cameraPosition.Y - EyeHeight;
            double maxY = minY + PlayerHeight;
            double minZ = cameraPosition.Z - PlayerRadius;
            double maxZ = cameraPosition.Z + PlayerRadius;
            bool overlapsX = (x + 1.0) > minX && x < maxX;
            bool overlapsY = (y + 1.0) > minY && y < maxY;
            bool overlapsZ = (z + 1.0) > minZ && z < maxZ;
            return overlapsX && overlapsY && overlapsZ;
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
                gpuRenderer.SetChunkManager(manager);
                if (window != null) gpuRenderer.Resize(window.Width, window.Height);
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
            var forward = GetCameraForward();
            var pickResult = TryPickBlock(cameraPosition, forward);
            Vector3[]? highlightQuad = null;
            if (pickResult.HasValue) highlightQuad = ComputeHighlightWorldQuad(pickResult.Value);
            return new HudState
            {
                ShowDebug = showFps, Fps = lastFps, UpdateMs = lastUpdateMs, MeshMs = lastMeshMs,
                UploadMs = lastUploadMs, RenderMs = lastRenderMs,
                FacingText = $"{GetCompassDirection(cameraYaw)} ({NormalizeYaw(cameraYaw):0.0} deg)",
                SelectedBlockText = $"Selected: {BlockRegistry.GetName(selectedBlock)}",
                RenderDistanceText = $"Render dist: {RenderDistanceName} ({ChunkRenderRadius})",
                SelectedSlot = selectedSlot, HighlightWorldQuad = highlightQuad,
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
            var remove = hit.Remove;
            var n = hit.Normal;
            Point3D[] faceCorners = new Point3D[4];
            if (Math.Abs(n.X) > 0.5)
            {
                double xplane = remove.x + (n.X > 0 ? 1.0 : 0.0);
                faceCorners[0] = new Point3D(xplane, remove.y, remove.z);
                faceCorners[1] = new Point3D(xplane, remove.y, remove.z + 1.0);
                faceCorners[2] = new Point3D(xplane, remove.y + 1.0, remove.z + 1.0);
                faceCorners[3] = new Point3D(xplane, remove.y + 1.0, remove.z);
            }
            else if (Math.Abs(n.Y) > 0.5)
            {
                double yplane = remove.y + (n.Y > 0 ? 1.0 : 0.0);
                faceCorners[0] = new Point3D(remove.x, yplane, remove.z);
                faceCorners[1] = new Point3D(remove.x + 1.0, yplane, remove.z);
                faceCorners[2] = new Point3D(remove.x + 1.0, yplane, remove.z + 1.0);
                faceCorners[3] = new Point3D(remove.x, yplane, remove.z + 1.0);
            }
            else
            {
                double zplane = remove.z + (n.Z > 0 ? 1.0 : 0.0);
                faceCorners[0] = new Point3D(remove.x, remove.y, zplane);
                faceCorners[1] = new Point3D(remove.x + 1.0, remove.y, zplane);
                faceCorners[2] = new Point3D(remove.x + 1.0, remove.y + 1.0, zplane);
                faceCorners[3] = new Point3D(remove.x, remove.y + 1.0, zplane);
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
                if (manager.TryGetLoadedBlock(bx, by, bz, out var block) && block != BlockRegistry.AirId)
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
            int lastX = currentX, lastY = currentY, lastZ = currentZ;
            var normal = new Point3D(0, 0, 0);
            for (int iteration = 0; iteration < 200 && distance <= maxDistance; iteration++)
            {
                if (manager.TryGetLoadedBlock(currentX, currentY, currentZ, out var block) && block != BlockRegistry.AirId)
                {
                    hitDistance = distance;
                    return new PickBlockResult((currentX, currentY, currentZ), (lastX, lastY, lastZ), normal);
                }
                lastX = currentX; lastY = currentY; lastZ = currentZ;
                if (tMaxX < tMaxY)
                {
                    if (tMaxX < tMaxZ) { currentX += stepX; distance = tMaxX; tMaxX += tDeltaX; normal = new Point3D(-stepX, 0, 0); }
                    else { currentZ += stepZ; distance = tMaxZ; tMaxZ += tDeltaZ; normal = new Point3D(0, 0, -stepZ); }
                }
                else
                {
                    if (tMaxY < tMaxZ) { currentY += stepY; distance = tMaxY; tMaxY += tDeltaY; normal = new Point3D(0, -stepY, 0); }
                    else { currentZ += stepZ; distance = tMaxZ; tMaxZ += tDeltaZ; normal = new Point3D(0, 0, -stepZ); }
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
            public PickBlockResult((int x, int y, int z) remove, (int x, int y, int z) place, Point3D normal)
            {
                Remove = remove; Place = place; Normal = normal;
            }
        }

        private enum Axis
        {
            X,
            Y,
            Z,
        }
    }
}