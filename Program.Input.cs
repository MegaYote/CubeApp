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
        private void ApplyFrameInput(FrameInputState frameInput)
        {
            if (frameInput.ToggleMouseCapturePressed)
            {
                if (screen == GameScreen.Playing)
                {
                    // ESC closes the E-menu / crafting menu / biome menu first; only then does it pause.
                    if (inventoryOpen || craftingOpen || biomeMenuOpen)
                    {
                        inventoryOpen = false;
                        craftingOpen = false;
                        biomeMenuOpen = false;
                        EnableMouseLook();
                    }
                    else
                    {
                        SaveWorld(); // autosave whenever the pause menu opens
                        screen = GameScreen.Paused;
                        menu.Screen = GameScreen.Paused;
                        DisableMouseLook();
                    }
                }
                else if (menu.Screen == GameScreen.Settings)
                {
                    // ESC from settings behaves like clicking Back.
                    menu.Screen = menu.SettingsReturnTo;
                    menu.SettingsOpen = false;
                }
                else if (screen == GameScreen.Paused)
                {
                    ResumeToPlaying();
                }
            }
            // Clicking while a menu-style overlay is open must NOT re-capture the mouse - the
            // E-menu / crafting / biome menu are only closed by E/ESC now.
            if (screen == GameScreen.Playing && !mouseLook && !handEditorOpen && !inventoryOpen && !craftingOpen && !biomeMenuOpen
                && (frameInput.BreakBlockPressed || frameInput.PlaceBlockPressed))
            {
                EnableMouseLook();
                return;
            }
            if (frameInput.ToggleDebugPressed) showFps = !showFps;
            if (frameInput.ToggleFullscreenPressed) ToggleFullscreen();
            if (frameInput.ToggleCreativePressed && World != null)
            {
                World.Mode = World.Mode == GameMode.Creative ? GameMode.Survival : GameMode.Creative;
                World.FlyMode = World.IsCreative; // fly mode follows creative
            }
            if (_ignoreInteractFrames > 0) _ignoreInteractFrames--;
            if (screen == GameScreen.Playing && World != null)
            {
                // Flight is a creative privilege.
                if (frameInput.ToggleFlyPressed && World.IsCreative) World.FlyMode = !World.FlyMode;
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
                    if (inventoryOpen) { craftingOpen = false; DisableMouseLook(); }
                    else EnableMouseLook();
                }
                // Teleport menu is a creative sandbox convenience.
                if (frameInput.ToggleBiomeMenuPressed && World.IsCreative)
                {
                    biomeMenuOpen = !biomeMenuOpen;
                    if (biomeMenuOpen) DisableMouseLook();
                    else EnableMouseLook();
                }
                // Hand Editor (F8): frees the mouse so sliders can be dragged without looking around.
                if (frameInput.ToggleHandEditorPressed)
                {
                    handEditorOpen = !handEditorOpen;
                    if (handEditorOpen) DisableMouseLook();
                    else EnableMouseLook();
                }
            }
            if (frameInput.CycleRenderDistancePressed) CycleRenderDistance();
            if (screen == GameScreen.Playing && World != null)
            {
                // Debug spawn + damage-test keys are creative-only toys.
                if (frameInput.SpawnMobPressed && World.IsCreative) World.Entities.SpawnDuck(World.PlayerPosition, World.PlayerYaw);
                if (frameInput.SpawnCoyotePressed && World.IsCreative) World.Entities.SpawnCoyote(World.PlayerPosition, World.PlayerYaw);
                if (frameInput.SpawnStevePressed && World.IsCreative) World.Entities.SpawnSteve(World.PlayerPosition, World.PlayerYaw);
                if (frameInput.SpawnZombiePressed && World.IsCreative) World.Entities.SpawnMobById("zombie", World.PlayerPosition, World.PlayerYaw);
                // O = take 1 point of damage (healthbar slice test).
                if (frameInput.DamageSelfPressed && World.IsCreative) World.DamagePlayer(1, DeathCause.DebugSelf);
            }
            if (frameInput.ToggleThirdPersonPressed) thirdPersonView = !thirdPersonView;
            if (frameInput.SelectedSlot.HasValue && World != null) World.SetSelectedSlot(frameInput.SelectedSlot.Value);
            // Q: throw one item - from the hovered inventory slot, else the cursor, else the
            // selected hotbar stack (MC survival behavior).
            if (frameInput.DropItemPressed && World != null && screen == GameScreen.Playing)
            {
                if (inventoryOpen)
                {
                    int hover = gpuRenderer?.HoveredInventorySlot ?? -1;
                    if (hover >= 0) World.DropSlotItem(hover);
                    else if (World.HeldStack.HasValue) World.DropFromCursor(1);
                    else World.DropSelectedHotbarItem();
                }
                else
                {
                    if (World.HeldStack.HasValue) World.DropFromCursor(1);
                    else World.DropSelectedHotbarItem();
                }
            }
            // Mouse wheel cycles the hotbar while playing with the mouse captured (when menus are
            // open the wheel goes to ImGui instead).
            if (frameInput.HotbarScroll != 0 && World != null
                && screen == GameScreen.Playing && mouseLook
                && !inventoryOpen && !biomeMenuOpen && !handEditorOpen)
            {
                int slots = GameWorld.HotbarSlots;
                int next = (World.SelectedSlot + frameInput.HotbarScroll) % slots;
                if (next < 0) next += slots;
                World.SetSelectedSlot(next);
            }
            if (screen == GameScreen.Playing && mouseLook && World != null && _ignoreInteractFrames == 0 && frameInput.BreakBlockPressed)
            {
                if (!World.Entities.TryAttackMob(World.PlayerPosition, World.GetCameraForward(), null))
                {
                    // No mob hit: mining is driven by BreakHeld in StepSimulation, but the click
                    // primes the target so progress starts immediately this frame.
                    // If there's no block under the crosshair either, this is a punch at the air -
                    // play the one-shot swing animation (MC punches the air when nothing is targeted).
                    var airPunch = World.TryPickBlock(World.PlayerPosition, World.GetCameraForward());
                    if (!airPunch.HasValue)
                    {
                        _handSwingTimer = HandSwingDuration;
                    }
                }
                else
                {
                    // A mob was hit: play the same one-shot swing (MC swings on every attack).
                    _handSwingTimer = HandSwingDuration;
                }
            }
            if (screen == GameScreen.Playing && mouseLook && World != null && _ignoreInteractFrames == 0 && frameInput.PlaceBlockPressed)
            {
                // Right-click on a workbench opens the crafting menu; anywhere else places.
                if (World.TryPickWorkbench(World.PlayerPosition, World.GetCameraForward(), out _))
                {
                    craftingOpen = true;
                    inventoryOpen = false;
                    DisableMouseLook();
                }
                else
                {
                    PlaceSelectedBlock();
                }
            }
        }

        private void CycleRenderDistance()
        {
            renderDistanceIndex = (renderDistanceIndex + 1) % RenderDistances.Length;
            gpuRenderer?.SetRenderDistance(ChunkRenderRadius);
            needsMeshUpdate = true;
            _forceChunkStream = true;
            if (World != null) World.ChunkRenderRadius = ChunkRenderRadius;
        }

        // F11 / Alt+Enter: toggle between windowed and borderless fullscreen. Borderless is used
        // instead of exclusive FullScreen so alt-tabbing stays reliable on weak office iGPUs.
        private void ToggleFullscreen()
        {
            if (window == null) return;
            window.WindowState = window.WindowState == WindowState.BorderlessFullScreen
                ? WindowState.Normal
                : WindowState.BorderlessFullScreen;
        }

    }
}