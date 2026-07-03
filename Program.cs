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
        private Sdl2Window? window;
        private GraphicsDevice? graphicsDevice;

        private Point3D cameraPosition = new Point3D(24.0, 12.0, -24.0);
        private float cameraYaw = 0f;
        private float cameraPitch = 0f;

        private readonly InputProcessor input = new();
        private bool mouseLook;

        private bool needsMeshUpdate = true;
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
        private const int ChunkRenderRadius = 2;
        private const double MaxFrameDeltaSeconds = 0.25;

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

        public Program()
        {
            manager = new ChunkManager(new InfdevChunkProvider(20100630));
            EnsureVisibleChunks();
            PlaceCameraAtSafeSpawn();
            meshWorker = new MeshWorker(manager, () => gpuRenderer);
            _ = UpdateMesh();
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
                        gpuRenderer.Render();
                    }
                    var t5 = stageStopwatch.ElapsedTicks;
                    lastRenderMs = (t5 - t4) * 1000f / Stopwatch.Frequency;

                    if (showFps && window != null)
                    {
                        window.Title = $"{baseTitle} - FPS: {lastFps:0.0}";
                    }
                    else if (window != null)
                    {
                        window.Title = baseTitle;
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

            if (frameInput.SelectedSlot.HasValue)
            {
                SetSelectedSlot(frameInput.SelectedSlot.Value);
            }

            if (frameInput.BreakBlockPressed)
            {
                DeleteHighlightedBlock();
            }

            if (frameInput.PlaceBlockPressed)
            {
                PlaceSelectedBlock();
            }
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

            int chunkX = WorldToChunkCoord(cameraPosition.X);
            int chunkZ = WorldToChunkCoord(cameraPosition.Z);
            if (manager.EnsureChunksAround(chunkX, chunkZ, ChunkRenderRadius))
            {
                needsMeshUpdate = true;
            }

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
            return manager.EnsureChunksAround(chunkX, chunkZ, ChunkRenderRadius);
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

            Vector2[]? highlightQuad = null;
            if (pickResult.HasValue)
            {
                highlightQuad = ComputeHighlightScreenQuad(pickResult.Value, forward);
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
                SelectedSlot = selectedSlot,
                HighlightQuad = highlightQuad,
            };
        }

        private Vector2[]? ComputeHighlightScreenQuad(PickBlockResult hit, Point3D forward)
        {
            int width = window != null ? Math.Max(1, window.Width) : 900;
            int height = window != null ? Math.Max(1, window.Height) : 720;

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

            var proj = Matrix4x4.CreatePerspectiveFieldOfView((float)(Math.PI / 2.0), (float)width / height, 0.1f, 100f);
            var cameraPos = new Vector3((float)cameraPosition.X, (float)cameraPosition.Y, (float)cameraPosition.Z);
            var cameraForward = new Vector3((float)forward.X, (float)forward.Y, (float)forward.Z);
            var target = cameraPos + cameraForward;
            var view = Matrix4x4.CreateLookAt(cameraPos, target, Vector3.UnitY);
            var viewProj = Matrix4x4.Multiply(view, proj);

            var result = new Vector2[4];
            for (int i = 0; i < 4; i++)
            {
                var pos = faceCorners[i];
                var world = new Vector4((float)pos.X, (float)pos.Y, (float)pos.Z, 1f);
                var clip = Vector4.Transform(world, viewProj);
                if (clip.W <= 0f)
                {
                    return null;
                }

                float ndcX = clip.X / clip.W;
                float ndcY = clip.Y / clip.W;
                var pt = new Vector2(
                    (ndcX * 0.5f + 0.5f) * width,
                    (1f - (ndcY * 0.5f + 0.5f)) * height);
                if (float.IsNaN(pt.X) || float.IsInfinity(pt.X) || float.IsNaN(pt.Y) || float.IsInfinity(pt.Y))
                {
                    return null;
                }

                result[i] = pt;
            }

            return result;
        }

        private bool IsHighlightFaceOccluded((int x, int y, int z) targetBlock, Point3D normal, Point3D[] faceCorners)
        {
            if (faceCorners.Length != 4)
            {
                return false;
            }

            var center = new Point3D(
                (faceCorners[0].X + faceCorners[1].X + faceCorners[2].X + faceCorners[3].X) * 0.25,
                (faceCorners[0].Y + faceCorners[1].Y + faceCorners[2].Y + faceCorners[3].Y) * 0.25,
                (faceCorners[0].Z + faceCorners[1].Z + faceCorners[2].Z + faceCorners[3].Z) * 0.25);

            // Push sample points slightly outward from the block so edge/boundary hits are stable.
            var sampleOffset = normal * 0.001;

            if (IsPointOccluded(targetBlock, center + sampleOffset))
            {
                return true;
            }

            for (int i = 0; i < 4; i++)
            {
                if (IsPointOccluded(targetBlock, faceCorners[i] + sampleOffset))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsPointOccluded((int x, int y, int z) targetBlock, Point3D targetPoint)
        {
            var delta = targetPoint - cameraPosition;
            var dist = delta.Length;
            if (dist <= 0.0001)
            {
                return false;
            }

            var direction = delta / dist;

            int blockX = (int)Math.Floor(cameraPosition.X);
            int blockY = (int)Math.Floor(cameraPosition.Y);
            int blockZ = (int)Math.Floor(cameraPosition.Z);

            int stepX = Math.Sign(direction.X);
            int stepY = Math.Sign(direction.Y);
            int stepZ = Math.Sign(direction.Z);

            double tDeltaX = stepX != 0 ? Math.Abs(1.0 / direction.X) : double.PositiveInfinity;
            double tDeltaY = stepY != 0 ? Math.Abs(1.0 / direction.Y) : double.PositiveInfinity;
            double tDeltaZ = stepZ != 0 ? Math.Abs(1.0 / direction.Z) : double.PositiveInfinity;

            double tMaxX = stepX > 0 ? (blockX + 1.0 - cameraPosition.X) * tDeltaX : (cameraPosition.X - blockX) * tDeltaX;
            double tMaxY = stepY > 0 ? (blockY + 1.0 - cameraPosition.Y) * tDeltaY : (cameraPosition.Y - blockY) * tDeltaY;
            double tMaxZ = stepZ > 0 ? (blockZ + 1.0 - cameraPosition.Z) * tDeltaZ : (cameraPosition.Z - blockZ) * tDeltaZ;

            double maxDistance = dist + 0.01;

            for (int iteration = 0; iteration < 256; iteration++)
            {
                if (manager.TryGetLoadedBlock(blockX, blockY, blockZ, out var block) && block != BlockType.Air)
                {
                    if (blockX == targetBlock.x && blockY == targetBlock.y && blockZ == targetBlock.z)
                    {
                        return false;
                    }

                    return true;
                }

                double tNext;
                if (tMaxX < tMaxY)
                {
                    if (tMaxX < tMaxZ)
                    {
                        blockX += stepX;
                        tNext = tMaxX;
                        tMaxX += tDeltaX;
                    }
                    else
                    {
                        blockZ += stepZ;
                        tNext = tMaxZ;
                        tMaxZ += tDeltaZ;
                    }
                }
                else
                {
                    if (tMaxY < tMaxZ)
                    {
                        blockY += stepY;
                        tNext = tMaxY;
                        tMaxY += tDeltaY;
                    }
                    else
                    {
                        blockZ += stepZ;
                        tNext = tMaxZ;
                        tMaxZ += tDeltaZ;
                    }
                }

                if (tNext > maxDistance)
                {
                    break;
                }
            }

            return false;
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
            try { meshWorker?.Dispose(); } catch { }
            try { gpuRenderer?.Dispose(); } catch { }
            try { graphicsDevice?.Dispose(); } catch { }
            try { window?.Close(); } catch { }
        }

        [STAThread]
        static void Main()
        {
            using var app = new Program();
            app.Run();
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
