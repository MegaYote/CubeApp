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
        public void Run()
        {
            var windowCreateInfo = new WindowCreateInfo(100, 100, 900, 720, WindowState.Normal, baseTitle);
            var graphicsDeviceOptions = new GraphicsDeviceOptions(
                debug: false, swapchainDepthFormat: PixelFormat.D24_UNorm_S8_UInt,
                syncToVerticalBlank: false, resourceBindingModel: ResourceBindingModel.Improved,
                preferDepthRangeZeroToOne: true, preferStandardClipSpaceYDirection: true);

            // Try graphics backends in order of preference: Direct3D11 first (best driver support
            // and matching behavior), then OpenGL, then Vulkan. This automatically selects the best
            // available backend for the current hardware.
            GraphicsBackend? selectedBackend = null;
            GraphicsDevice? gd = null;
            window = null;

            var backendsToTry = new[]
            {
                GraphicsBackend.Direct3D11,
                GraphicsBackend.OpenGL,
                GraphicsBackend.Vulkan
            };

            foreach (var backend in backendsToTry)
            {
                try
                {
                    VeldridStartup.CreateWindowAndGraphicsDevice(windowCreateInfo, graphicsDeviceOptions,
                        backend, out var createdWindow, out var createdGraphicsDevice);

                    window = createdWindow;
                    gd = createdGraphicsDevice;
                    selectedBackend = backend;
                    break;
                }
                catch (Exception)
                {
                    // Try next backend
                    window?.Close();
                }
            }

            if (gd == null)
            {
                throw new Exception("No supported graphics backend found on this system");
            }

            if (window != null)
            {
                window.Title = selectedBackend.ToString();
                baseTitle = window.Title;
            }
            graphicsDevice = gd;
            InitializeGpuRenderer(graphicsDevice, graphicsDevice.MainSwapchain);
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

                    // Sync mouse capture state with window focus.
                    // If window lost focus while mouse look was active, SDL auto-releases capture
                    // but our internal state gets out of sync. Disable to re-sync.
                    if (!activeWindow.Focused && mouseLook)
                    {
                        DisableMouseLook();
                    }

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
                        var hud = BuildHud();
                        if (World != null)
                            World.Entities.CollectMiningTargets(_zombieMiningScratch);
                        hud.ZombieMiningTargets = _zombieMiningScratch;
                        gpuRenderer.SetHud(hud);
                        if (World != null)
                        {
                            var lp = World.LocalPlayer;
                            gpuRenderer.UpdateCamera(thirdPersonView ? GetThirdPersonCameraPosition() : lp.Position,
                                lp.Yaw, lp.Pitch, lp.WalkPhase, lp.WalkAmount, !thirdPersonView, lp.Grounded);
                            _entityRenderScratch.Clear();
                            _entityRenderScratch.AddRange(World.Entities.MobRenderData);
                            if (thirdPersonView) _entityRenderScratch.Add(BuildLocalPlayerRenderData());
                            AddRemotePlayersToRender(_entityRenderScratch);
                            gpuRenderer.SetEntities(_entityRenderScratch);
                            gpuRenderer.SetFallingBlocks(World.BlockTicks.Gravity.FallingBlocks);
                            gpuRenderer.SetItemDrops(World.ItemDropRenderData);
                        }
                        gpuRenderer.ProcessPendingPriorityMeshes();
                        gpuRenderer.SetUiInputSnapshot(snapshot);
                        gpuRenderer.Render();

                        while (gpuRenderer.TryTakeInventorySelection(out int invBlock))
                        {
                            if (World != null && invBlock > 0 && invBlock < ItemRegistry.Count)
                            {
                                World.Hotbar[World.SelectedSlot] = invBlock;
                                World.HotbarCounts[World.SelectedSlot] = Math.Min(GameWorld.MaxStackSize, ItemRegistry.StackSizeOf(invBlock));
                                World.SelectedBlock = invBlock;
                            }
                        }
                        // Survival drag/drop clicks (cursor stacks) + workbench crafting clicks.
                        while (gpuRenderer.TryTakeInventoryClick(out var click))
                        {
                            if (World == null) continue;
                            // Crafting grid/result clicks work in both modes (the survival
                            // cursor just never gets populated in creative, so no-op there).
                            if (click.Kind == 4 || click.Kind == 5)
                            {
                                if (click.Kind == 4) World.CraftingClickSlot(click.Target, click.Button == 1);
                                else if (click.Button == 0) World.TryCraft();
                                continue;
                            }
                            if (World.IsCreative) continue;
                            switch (click.Kind)
                            {
                                case 0: // bag slot (target = slot index 0..39)
                                    World.CursorClickSlot(click.Target, click.Button == 1);
                                    break;
                                case 1: // hotbar slot (target = unified 40+i)
                                    World.CursorClickSlot(click.Target, click.Button == 1);
                                    break;
                                case 3: // shift-click quick move (target = unified slot index)
                                    World.QuickMoveSlot(click.Target);
                                    break;
                                case 2: // clicked outside the window: throw items
                                    if (click.Button == 0) World.DropFromCursor(int.MaxValue);
                                    else World.DropFromCursor(1);
                                    break;
                            }
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

    }
}