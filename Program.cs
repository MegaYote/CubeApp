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

namespace CubeApp
{
    public sealed class Program : IDisposable
    {
        private readonly ChunkManager manager;
        private IRenderer? gpuRenderer;
        private MeshWorker? meshWorker;
        private ChunkGenWorker? chunkGenWorker;
        private Sdl2Window? window;
        private GraphicsDevice? graphicsDevice;

        private Point3D cameraPosition = new Point3D(24.0, 12.0, -24.0);
        private float cameraYaw = 0f;
        private float cameraPitch = 0f;

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

        // Render-distance presets matching Minecraft's named settings (radius in chunks). The F key
        // cycles through them in Minecraft's order: Far -> Normal -> Short -> Tiny -> Far.
        private static readonly int[] RenderDistances = { 16, 8, 4, 2 };
        private static readonly string[] RenderDistanceNames = { "Far", "Normal", "Short", "Tiny" };
        private int renderDistanceIndex = 1; // default Normal (8 chunks)
        private int ChunkRenderRadius => RenderDistances[renderDistanceIndex];
        private string RenderDistanceName => RenderDistanceNames[renderDistanceIndex];

        // Chunks generated synchronously at startup so spawn placement sees real terrain; the rest
        // of the render distance streams in on the background worker pool.
        private const int SpawnSyncRadius = 2;

        private static readonly BlockType[] HotbarBlockTypes =
        {
            BlockType.Grass,
            BlockType.Dirt,
            BlockType.Stone,
            BlockType.Cobblestone,
            BlockType.Sand,
            BlockType.Planks,
            BlockType.Bedrock,
            BlockType.Gravel,
            BlockType.Obsidian,
            BlockType.MossyCobblestone,
        };

        private BlockType selectedBlock = BlockType.Grass;
        private int selectedSlot;
        private const int HotbarSlots = 10;
        private bool inventoryOpen;

        // Duck test mobs (press G to spawn). Static model + gravity + ground collision for now.
        private readonly List<Duck> ducks = new();
        private readonly List<DuckInstance> duckInstances = new();

        public Program()
        {
            manager = new ChunkManager(new InfdevChunkProvider(20100630));
            EnsureVisibleChunks();
            PlaceCameraAtSafeSpawn();
            meshWorker = new MeshWorker(manager, () => gpuRenderer);
            _ = UpdateMesh();

            // Generate chunks off the main thread so streaming new terrain doesn't stall the
            // render loop. Leave a core for the render/mesh threads.
            int genWorkers = Math.Max(1, Environment.ProcessorCount - 2);
            chunkGenWorker = new ChunkGenWorker(manager, () => needsMeshUpdate = true, genWorkers);
        }

        public void Run()
        {
            var windowCreateInfo = new WindowCreateInfo(
                x: 100,
                y: 100,
                windowWidth: 900,
                windowHeight: 720,
                windowInitialState: WindowState.Normal,
                windowTitle: baseTitle);
            var graphicsDeviceOptions = new GraphicsDeviceOptions(
                debug: false,
                swapchainDepthFormat: PixelFormat.D24_UNorm_S8_UInt,
                syncToVerticalBlank: false,
                resourceBindingModel: ResourceBindingModel.Improved,
                preferDepthRangeZeroToOne: true,
                preferStandardClipSpaceYDirection: true);

            VeldridStartup.CreateWindowAndGraphicsDevice(
                windowCreateInfo,
                graphicsDeviceOptions,
                GraphicsBackend.Direct3D11,
                out var createdWindow,
                out var createdGraphicsDevice);

            window = createdWindow;
            graphicsDevice = createdGraphicsDevice;
            baseTitle = window.Title;

            InitializeGpuRenderer(createdGraphicsDevice, createdGraphicsDevice.MainSwapchain);
            EnableMouseLook();
            RunMainLoop();
        }

        private void RunMainLoop()
        {
            if (window == null)
            {
                return;
            }

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
                    if (!activeWindow.Exists)
                    {
                        break;
                    }

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
                    if (deltaSeconds > MaxFrameDeltaSeconds)
                    {
                        deltaSeconds = MaxFrameDeltaSeconds;
                    }

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

                    float uploadMs = 0f;
                    var t2 = stageStopwatch.ElapsedTicks;
                    if (needsMeshUpdate)
                    {
                        var remeshed = UpdateMesh();
                        var t3 = stageStopwatch.ElapsedTicks;
                        lastMeshMs = (t3 - t2) * 1000f / Stopwatch.Frequency;

                        if (gpuRenderer != null && remeshed != null)
                        {
                            var upStart = stageStopwatch.ElapsedTicks;
                            foreach (var coord in remeshed)
                            {
                                if (manager.TryGetLoadedChunk(coord, out var ch) && ch.MeshFaces != null && ch.MeshFaces.Count > 0)
                                {
                                    gpuRenderer.UploadChunk(coord, ch.MeshFaces);
                                }
                            }
                            var upEnd = stageStopwatch.ElapsedTicks;
                            uploadMs = (upEnd - upStart) * 1000f / Stopwatch.Frequency;
                        }

                        needsMeshUpdate = false;
                    }
                    else
                    {
                        lastMeshMs = 0f;
                    }
                    lastUploadMs = uploadMs;

                    var t4 = stageStopwatch.ElapsedTicks;
                    if (gpuRenderer != null)
                    {
                        gpuRenderer.UpdateCamera(cameraPosition, cameraYaw, cameraPitch);
                        gpuRenderer.SetHud(BuildHud());
                        gpuRenderer.SetEntities(BuildDuckInstances());
                        gpuRenderer.Render();
                    }
                    var t5 = stageStopwatch.ElapsedTicks;
                    lastRenderMs = (t5 - t4) * 1000f / Stopwatch.Frequency;

                    if (window != null)
                    {
                        string rd = $"Render: {RenderDistanceName} ({ChunkRenderRadius})";
                        window.Title = showFps
                            ? $"{baseTitle} - FPS: {lastFps:0.0} - {rd}"
                            : $"{baseTitle} - {rd}";
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
            if (frameInput.ToggleMouseCapturePressed)
            {
                DisableMouseLook();
            }

            if (!mouseLook && (frameInput.BreakBlockPressed || frameInput.PlaceBlockPressed))
            {
                EnableMouseLook();
                return;
            }

            if (frameInput.ToggleDebugPressed)
            {
                showFps = !showFps;
            }

            if (frameInput.ToggleInventoryPressed)
            {
                inventoryOpen = !inventoryOpen;
            }

            if (frameInput.CycleRenderDistancePressed)
            {
                CycleRenderDistance();
            }

            if (frameInput.SpawnMobPressed)
            {
                SpawnDuck();
            }

            if (frameInput.SelectedSlot.HasValue)
            {
                SetSelectedSlot(frameInput.SelectedSlot.Value);
            }

            if (frameInput.BreakBlockPressed)
            {
                if (!TryAttackDuck())
                {
                    DeleteHighlightedBlock();
                }
            }

            if (frameInput.PlaceBlockPressed)
            {
                PlaceSelectedBlock();
            }
        }

        // Spawn a duck a couple of blocks ahead of and above the player so it drops in and lands
        // on the ground in view. It faces back toward the player.
        private void SpawnDuck()
        {
            float yawRad = cameraYaw * (float)Math.PI / 180f;
            double fx = Math.Sin(yawRad);
            double fz = Math.Cos(yawRad);

            double spawnX = Math.Floor(cameraPosition.X + fx * 2.0) + 0.5;
            double spawnZ = Math.Floor(cameraPosition.Z + fz * 2.0) + 0.5;
            double feetY = cameraPosition.Y - EyeHeight;
            double spawnY = Math.Floor(feetY) + 3.0;

            double toPlayerX = cameraPosition.X - spawnX;
            double toPlayerZ = cameraPosition.Z - spawnZ;
            float duckYaw = (float)Math.Atan2(-toPlayerX, -toPlayerZ);

            ducks.Add(new Duck(new Point3D(spawnX, spawnY, spawnZ), duckYaw));
        }

        private void UpdateDucks(float deltaSeconds)
        {
            for (int i = ducks.Count - 1; i >= 0; i--)
            {
                var duck = ducks[i];
                duck.Update(deltaSeconds, manager);
                if (duck.Removed)
                {
                    ducks.RemoveAt(i);
                }
            }
        }

        // Left-click melee: if the crosshair ray hits a duck no farther than the block it would
        // break, damage that duck instead of mining. Mirrors Cubuild's entity-priority targeting.
        private bool TryAttackDuck()
        {
            var forward = GetCameraForward();
            var duck = TryPickDuck(cameraPosition, forward, out double duckDistance);
            if (duck == null)
            {
                return false;
            }

            _ = TryPickBlock(cameraPosition, forward, out double blockDistance);
            if (duckDistance > blockDistance + 0.02)
            {
                return false;
            }

            duck.Damage(1, cameraPosition.X, cameraPosition.Z, true);
            return true;
        }

        // Nearest duck whose collision box the ray enters within reach. Returns null if none.
        private Duck? TryPickDuck(Point3D origin, Point3D direction, out double hitDistance)
        {
            hitDistance = double.PositiveInfinity;
            Duck? best = null;
            var dir = direction.Normalized();
            float half = Duck.Width * 0.5f;

            foreach (var duck in ducks)
            {
                if (duck.IsDead) continue;

                double minX = duck.Position.X - half;
                double maxX = duck.Position.X + half;
                double minY = duck.Position.Y;
                double maxY = duck.Position.Y + Duck.Height;
                double minZ = duck.Position.Z - half;
                double maxZ = duck.Position.Z + half;

                if (RayBox(origin, dir, minX, minY, minZ, maxX, maxY, maxZ, out double t)
                    && t <= BlockReach && t < hitDistance)
                {
                    hitDistance = t;
                    best = duck;
                }
            }

            return best;
        }

        // Slab-method ray/AABB intersection; outputs the entry distance along the (unit) ray.
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

        private IReadOnlyList<DuckInstance> BuildDuckInstances()
        {
            duckInstances.Clear();
            foreach (var duck in ducks)
            {
                duckInstances.Add(duck.ToInstance());
            }

            return duckInstances;
        }

        private void CycleRenderDistance()
        {
            renderDistanceIndex = (renderDistanceIndex + 1) % RenderDistances.Length;
            gpuRenderer?.SetRenderDistance(ChunkRenderRadius);
            // Newly requested chunks stream in; nudge a mesh pass so the change is reflected promptly.
            needsMeshUpdate = true;
        }

        private void SetSelectedSlot(int slot)
        {
            if (slot < 0 || slot >= HotbarSlots)
            {
                return;
            }

            selectedSlot = slot;
            if (slot < HotbarBlockTypes.Length)
            {
                selectedBlock = HotbarBlockTypes[slot];
            }
        }

        private void StepSimulation(TickInputState tickInput, float deltaSeconds)
        {
            UpdatePlayerMovement(tickInput, deltaSeconds);
            UpdateDucks(deltaSeconds);

            int chunkX = WorldToChunkCoord(cameraPosition.X);
            int chunkZ = WorldToChunkCoord(cameraPosition.Z);
            manager.RequestChunksAround(chunkX, chunkZ, ChunkRenderRadius, cameraPosition);

            var unloaded = manager.UnloadChunksOutside(chunkX, chunkZ, ChunkRenderRadius);
            if (gpuRenderer != null)
            {
                foreach (var uc in unloaded)
                {
                    gpuRenderer.RemoveChunk(uc);
                }
            }

            if (unloaded.Count > 0)
            {
                needsMeshUpdate = true;
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
            if (verticalVelocity < -MaxFallSpeed)
            {
                verticalVelocity = -MaxFallSpeed;
            }

            playerVelocity = new Point3D(horizontalVelocity.X, verticalVelocity, horizontalVelocity.Z);

            var frameDisplacement = playerVelocity * deltaSeconds;
            MovePlayerWithCollisions(frameDisplacement);
        }

        private void ApplyLookInput(Vector2 lookDelta)
        {
            if (!mouseLook)
            {
                return;
            }

            if (lookDelta.X == 0f && lookDelta.Y == 0f)
            {
                return;
            }

            cameraYaw -= lookDelta.X;
            cameraYaw = NormalizeYaw(cameraYaw);
            cameraPitch = Math.Clamp(cameraPitch - lookDelta.Y, -89f, 89f);
        }

        private void MovePlayerWithCollisions(Point3D displacement)
        {
            bool hitX = false;
            bool hitY = false;
            bool hitZ = false;

            cameraPosition = MoveAlongAxis(cameraPosition, displacement.X, Axis.X, ref hitX);
            cameraPosition = MoveAlongAxis(cameraPosition, displacement.Y, Axis.Y, ref hitY);
            cameraPosition = MoveAlongAxis(cameraPosition, displacement.Z, Axis.Z, ref hitZ);

            if (hitX)
            {
                playerVelocity = new Point3D(0, playerVelocity.Y, playerVelocity.Z);
            }

            if (hitZ)
            {
                playerVelocity = new Point3D(playerVelocity.X, playerVelocity.Y, 0);
            }

            if (hitY)
            {
                if (playerVelocity.Y <= 0)
                {
                    playerGrounded = true;
                }

                playerVelocity = new Point3D(playerVelocity.X, 0, playerVelocity.Z);
            }
            else
            {
                playerGrounded = false;
            }
        }

        private Point3D MoveAlongAxis(Point3D start, double amount, Axis axis, ref bool collided)
        {
            if (amount == 0.0)
            {
                return start;
            }

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
            {
                for (int y = blockMinY; y <= blockMaxY; y++)
                {
                    for (int z = blockMinZ; z <= blockMaxZ; z++)
                    {
                        if (manager.TryGetLoadedBlock(x, y, z, out var block) && block != BlockType.Air)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private bool EnsureVisibleChunks()
        {
            int chunkX = WorldToChunkCoord(cameraPosition.X);
            int chunkZ = WorldToChunkCoord(cameraPosition.Z);
            // Only the immediate spawn area is generated synchronously; the full render distance
            // streams in asynchronously so a large distance doesn't stall startup.
            return manager.EnsureChunksAround(chunkX, chunkZ, SpawnSyncRadius);
        }

        private void PlaceCameraAtSafeSpawn()
        {
            var spawn = FindSafeSpawnPosition();
            if (spawn.HasValue)
            {
                cameraPosition = spawn.Value;
            }

            playerVelocity = new Point3D(0, 0, 0);
            playerGrounded = true;
        }

        private Point3D? FindSafeSpawnPosition()
        {
            int baseX = (int)Math.Floor(cameraPosition.X);
            int baseZ = (int)Math.Floor(cameraPosition.Z);

            for (int radius = 0; radius <= 6; radius++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        if (radius > 0 && Math.Abs(dx) != radius && Math.Abs(dz) != radius)
                        {
                            continue;
                        }

                        int wx = baseX + dx;
                        int wz = baseZ + dz;

                        int highestSolidY = -1;
                        for (int y = ChunkManager.ChunkHeight - 1; y >= 0; y--)
                        {
                            if (manager.TryGetLoadedBlock(wx, y, wz, out var block) && block != BlockType.Air)
                            {
                                highestSolidY = y;
                                break;
                            }
                        }

                        if (highestSolidY < 0)
                        {
                            continue;
                        }

                        double px = wx + 0.5;
                        double pz = wz + 0.5;
                        double minEyeY = highestSolidY + EyeHeight + 0.01;
                        double maxEyeY = ChunkManager.ChunkHeight + EyeHeight;

                        for (double eyeY = minEyeY; eyeY <= maxEyeY; eyeY += 0.25)
                        {
                            var candidate = new Point3D(px, eyeY, pz);
                            if (!IsPlayerColliding(candidate))
                            {
                                return candidate;
                            }
                        }
                    }
                }
            }

            return null;
        }

        private List<ChunkCoordinates> UpdateMesh()
        {
            var remeshed = new List<ChunkCoordinates>();
            var chunks = manager.GetLoadedChunks();
            foreach (var c in chunks)
            {
                if (c.NeedsRemesh)
                {
                    var coord = new ChunkCoordinates(c.OriginX / ChunkManager.ChunkSize, c.OriginZ / ChunkManager.ChunkSize);
                    if (!c.IsMeshingQueued)
                    {
                        c.IsMeshingQueued = true;
                        meshWorker?.Enqueue(coord);
                    }
                    remeshed.Add(coord);
                }
            }

            return remeshed;
        }

        private static int WorldToChunkCoord(double value)
        {
            return (int)Math.Floor(value / ChunkManager.ChunkSize);
        }

        private void DeleteHighlightedBlock()
        {
            var pickResult = TryPickBlock(cameraPosition, GetCameraForward());
            if (!pickResult.HasValue) return;

            var remove = pickResult.Value.Remove;
            if (!manager.TrySetBlock(remove.x, remove.y, remove.z, BlockType.Air)) return;

            _ = UpdateMesh();
            needsMeshUpdate = true;
        }

        private void PlaceSelectedBlock()
        {
            var pickResult = TryPickBlock(cameraPosition, GetCameraForward());
            if (!pickResult.HasValue) return;

            var place = pickResult.Value.Place;
            if (WouldBlockIntersectPlayer(place.x, place.y, place.z)) return;
            if (!manager.TrySetBlock(place.x, place.y, place.z, selectedBlock)) return;

            _ = UpdateMesh();
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

        private static Vector2 Project(Point3D p, int cx, int cy, float scaleX, float scaleY)
        {
            float x = (float)(p.X * scaleX / p.Z + cx);
            float y = (float)(-p.Y * scaleY / p.Z + cy);
            return new Vector2(x, y);
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
                if (window != null)
                {
                    gpuRenderer.Resize(window.Width, window.Height);
                }

                var loaded = manager.GetLoadedChunks();
                foreach (var ch in loaded)
                {
                    if (ch.MeshFaces != null && ch.MeshFaces.Count > 0)
                    {
                        var chunkX = ch.OriginX / ChunkManager.ChunkSize;
                        var chunkZ = ch.OriginZ / ChunkManager.ChunkSize;
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
            if (pickResult.HasValue)
            {
                highlightQuad = ComputeHighlightWorldQuad(pickResult.Value);
            }

            return new HudState
            {
                ShowDebug = showFps,
                Fps = lastFps,
                UpdateMs = lastUpdateMs,
                MeshMs = lastMeshMs,
                UploadMs = lastUploadMs,
                RenderMs = lastRenderMs,
                FacingText = $"{GetCompassDirection(cameraYaw)} ({NormalizeYaw(cameraYaw):0.0} deg)",
                SelectedBlockText = $"Selected: {selectedBlock}",
                RenderDistanceText = $"Render dist: {RenderDistanceName} ({ChunkRenderRadius})",
                SelectedSlot = selectedSlot,
                HighlightWorldQuad = highlightQuad,
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

            // Nudge the quad a hair off the block face along its outward normal so it wins the
            // depth test against the coplanar block face (avoids z-fighting) while still being
            // occluded by any nearer block, which is what gives correct per-pixel occlusion.
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
            if (corners.Length != 4)
            {
                return corners;
            }

            if (!TryGetHighlightFaceAxes(normal, out var uAxis, out var vAxis))
            {
                return corners;
            }

            Span<(double U, double V)> uv = stackalloc (double U, double V)[4];
            double minU = double.PositiveInfinity;
            double maxU = double.NegativeInfinity;
            double minV = double.PositiveInfinity;
            double maxV = double.NegativeInfinity;

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
                if (used[i])
                {
                    continue;
                }

                var du = uv[i].U - targetU;
                var dv = uv[i].V - targetV;
                var distSq = du * du + dv * dv;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
            {
                return corners[0];
            }

            used[bestIndex] = true;
            return corners[bestIndex];
        }

        private static bool TryGetHighlightFaceAxes(Point3D normal, out Point3D uAxis, out Point3D vAxis)
        {
            if (normal.X > 0.5)
            {
                uAxis = new Point3D(0, 0, -1);
                vAxis = new Point3D(0, 1, 0);
                return true;
            }

            if (normal.X < -0.5)
            {
                uAxis = new Point3D(0, 0, 1);
                vAxis = new Point3D(0, 1, 0);
                return true;
            }

            if (normal.Y > 0.5)
            {
                uAxis = new Point3D(1, 0, 0);
                vAxis = new Point3D(0, 0, 1);
                return true;
            }

            if (normal.Y < -0.5)
            {
                uAxis = new Point3D(1, 0, 0);
                vAxis = new Point3D(0, 0, -1);
                return true;
            }

            if (normal.Z > 0.5)
            {
                uAxis = new Point3D(1, 0, 0);
                vAxis = new Point3D(0, 1, 0);
                return true;
            }

            if (normal.Z < -0.5)
            {
                uAxis = new Point3D(-1, 0, 0);
                vAxis = new Point3D(0, 1, 0);
                return true;
            }

            uAxis = new Point3D(0, 0, 0);
            vAxis = new Point3D(0, 0, 0);
            return false;
        }

        private void EnableMouseLook()
        {
            if (mouseLook)
            {
                return;
            }

            mouseLook = true;
            if (window != null)
            {
                ApplyMouseCapture(window, true);
            }
            input.ResetMouseTracking();
        }

        private void DisableMouseLook()
        {
            if (!mouseLook)
            {
                return;
            }

            mouseLook = false;
            if (window != null)
            {
                ApplyMouseCapture(window, false);
            }
            input.ResetMouseTracking();
        }

        private static void ApplyMouseCapture(Sdl2Window sdlWindow, bool captured)
        {
            // Always apply the stable cursor API.
            sdlWindow.CursorVisible = !captured;

            // Explicit SDL capture/relative mode prevents edge-clamping and keeps look smooth.
            Veldrid.Sdl2.Sdl2Native.SDL_ShowCursor(captured ? 0 : 1);
            Veldrid.Sdl2.Sdl2Native.SDL_CaptureMouse(captured);
            Veldrid.Sdl2.Sdl2Native.SDL_SetRelativeMouseMode(captured);

            // Keep compatibility with builds that expose convenience properties.
            TrySetBoolProperty(sdlWindow, "MouseCursorVisible", !captured);
            TrySetBoolProperty(sdlWindow, "MouseRelativeMode", captured);
            TrySetBoolProperty(sdlWindow, "InputGrabbed", captured);
            TrySetBoolProperty(sdlWindow, "MouseGrabbed", captured);
        }

        private static void TrySetBoolProperty(Sdl2Window sdlWindow, string propertyName, bool value)
        {
            var prop = sdlWindow.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (prop == null || !prop.CanWrite || prop.PropertyType != typeof(bool))
            {
                return;
            }

            prop.SetValue(sdlWindow, value);
        }

        private Point3D GetCameraForward()
        {
            var yawRad = cameraYaw * Math.PI / 180.0;
            var pitchRad = cameraPitch * Math.PI / 180.0;
            var cosPitch = Math.Cos(pitchRad);
            return new Point3D(
                cosPitch * Math.Sin(yawRad),
                Math.Sin(pitchRad),
                cosPitch * Math.Cos(yawRad)).Normalized();
        }

        private static Point3D GetCameraRight(float yaw)
        {
            var yawRad = yaw * Math.PI / 180.0;
            return new Point3D(Math.Cos(yawRad), 0, -Math.Sin(yawRad)).Normalized();
        }

        private static Point3D GetViewRight(Point3D forward)
        {
            var worldUp = new Point3D(0, 1, 0);
            var right = Cross(worldUp, forward);
            if (right.Length < 1e-6)
            {
                // Looking nearly straight up/down: fall back to a stable horizontal axis.
                return new Point3D(1, 0, 0);
            }

            return right.Normalized();
        }

        private static Point3D GetCameraUp(Point3D right, Point3D forward)
        {
            return Cross(forward, right).Normalized();
        }

        private static double Dot(Point3D a, Point3D b)
        {
            return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        }

        private static Point3D Cross(Point3D a, Point3D b)
        {
            return new Point3D(
                a.Y * b.Z - a.Z * b.Y,
                a.Z * b.X - a.X * b.Z,
                a.X * b.Y - a.Y * b.X);
        }

        private enum Axis
        {
            X,
            Y,
            Z,
        }

        private PickBlockResult? TryPickBlock(Point3D origin, Point3D direction)
        {
            return TryPickBlock(origin, direction, out _);
        }

        private PickBlockResult? TryPickBlock(Point3D origin, Point3D direction, out double hitDistance)
        {
            hitDistance = double.PositiveInfinity;
            direction = direction.Normalized();
            var blockX = (int)Math.Floor(origin.X);
            var blockY = (int)Math.Floor(origin.Y);
            var blockZ = (int)Math.Floor(origin.Z);

            var stepX = Math.Sign(direction.X);
            var stepY = Math.Sign(direction.Y);
            var stepZ = Math.Sign(direction.Z);

            var tDeltaX = stepX != 0 ? Math.Abs(1.0 / direction.X) : double.PositiveInfinity;
            var tDeltaY = stepY != 0 ? Math.Abs(1.0 / direction.Y) : double.PositiveInfinity;
            var tDeltaZ = stepZ != 0 ? Math.Abs(1.0 / direction.Z) : double.PositiveInfinity;

            var tMaxX = stepX > 0 ? (blockX + 1.0 - origin.X) * tDeltaX : (origin.X - blockX) * tDeltaX;
            var tMaxY = stepY > 0 ? (blockY + 1.0 - origin.Y) * tDeltaY : (origin.Y - blockY) * tDeltaY;
            var tMaxZ = stepZ > 0 ? (blockZ + 1.0 - origin.Z) * tDeltaZ : (origin.Z - blockZ) * tDeltaZ;

            var currentX = blockX;
            var currentY = blockY;
            var currentZ = blockZ;

            var maxDistance = BlockReach;
            var distance = 0.0;
            var lastX = currentX;
            var lastY = currentY;
            var lastZ = currentZ;
            var normal = new Point3D(0, 0, 0);

            for (int iteration = 0; iteration < 200 && distance <= maxDistance; iteration++)
            {
                if (manager.TryGetLoadedBlock(currentX, currentY, currentZ, out var block) && block != BlockType.Air)
                {
                    var remove = (currentX, currentY, currentZ);
                    var place = (lastX, lastY, lastZ);
                    hitDistance = distance;
                    return new PickBlockResult(remove, place, normal);
                }

                lastX = currentX;
                lastY = currentY;
                lastZ = currentZ;

                if (tMaxX < tMaxY)
                {
                    if (tMaxX < tMaxZ)
                    {
                        currentX += stepX;
                        distance = tMaxX;
                        tMaxX += tDeltaX;
                        normal = new Point3D(-stepX, 0, 0);
                    }
                    else
                    {
                        currentZ += stepZ;
                        distance = tMaxZ;
                        tMaxZ += tDeltaZ;
                        normal = new Point3D(0, 0, -stepZ);
                    }
                }
                else
                {
                    if (tMaxY < tMaxZ)
                    {
                        currentY += stepY;
                        distance = tMaxY;
                        tMaxY += tDeltaY;
                        normal = new Point3D(0, -stepY, 0);
                    }
                    else
                    {
                        currentZ += stepZ;
                        distance = tMaxZ;
                        tMaxZ += tDeltaZ;
                        normal = new Point3D(0, 0, -stepZ);
                    }
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

        // In a single-file self-contained build the native libraries are unpacked to a
        // temp directory that Veldrid's own name-based loader (NativeLibraryLoader / SDL2's
        // LoadSdl2) does not search, so it fails with "Could not find ... SDL2.dll". The .NET
        // runtime loader *does* search that directory, so pre-loading the natives by name here
        // pins them into the process; Veldrid's later by-name loads then resolve to the already
        // loaded modules. No-ops for a normal (non-single-file) build where the DLLs sit on disk.
        private static void PreloadNativeLibraries()
        {
            string[] names =
            {
                "SDL2", "cimgui", "veldrid-spirv", "libveldrid-spirv",
            };

            var asm = Assembly.GetExecutingAssembly();
            foreach (var name in names)
            {
                try
                {
                    System.Runtime.InteropServices.NativeLibrary.TryLoad(name, asm, null, out _);
                }
                catch
                {
                    // best-effort; ignore and let the normal loader try
                }
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
                // A single-file/self-contained build has no console attached, so a startup
                // crash would otherwise vanish silently. Persist it next to the exe so it
                // can be diagnosed.
                try
                {
                    string logPath = System.IO.Path.Combine(AppContext.BaseDirectory, "cubeapp-crash.log");
                    System.IO.File.WriteAllText(logPath, DateTime.Now + Environment.NewLine + ex);
                }
                catch
                {
                    // ignore logging failures
                }

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
                Remove = remove;
                Place = place;
                Normal = normal;
            }
        }
    }
}
