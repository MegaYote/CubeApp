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
            // Chat owns the keyboard before any gameplay hotkeys or Escape-to-pause behavior.
            if (chatOpen)
            {
                if (frameInput.ToggleMouseCapturePressed)
                {
                    chatOpen = false;
                    _chatJustOpened = false;
                    input.SetChatOpen(false);
                    EnableMouseLook();
                }
                return;
            }

            if (frameInput.OpenChatPressed && screen == GameScreen.Playing && World != null
                && !inventoryOpen && !craftingOpen && !smeltOpen && !oreCartOpen
                && !biomeMenuOpen && !handEditorOpen)
            {
                chatOpen = true;
                _chatJustOpened = true;
                input.SetChatOpen(true);
                DisableMouseLook();
                return;
            }

            if (frameInput.ToggleMouseCapturePressed)
            {
                if (screen == GameScreen.Playing)
                {
                    // ESC closes the E-menu / crafting menu / biome menu first; only then does it pause.
                    if (inventoryOpen || craftingOpen || smeltOpen || oreCartOpen || biomeMenuOpen)
                    {
                        // Never leave a stack orphaned on the cursor: return it to the player
                        // (dropping any overflow) as the container shuts. The crafting
                        // grid is drained too, so nothing can be stranded in a cell.
                        World?.ReturnHeldStackToInventory();
                        World?.RefundCraftingGrid();
                        World?.CloseSmelter();
                        inventoryOpen = false;
                        craftingOpen = false;
                        smeltOpen = false;
                        oreCartOpen = false;
                        _openOreCart = null;
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
            if (screen == GameScreen.Playing && !mouseLook && !handEditorOpen && !inventoryOpen && !craftingOpen && !smeltOpen && !oreCartOpen && !biomeMenuOpen
                && (frameInput.BreakBlockPressed || frameInput.PlaceBlockPressed))
            {
                EnableMouseLook();
                return;
            }
            if (frameInput.ToggleDebugPressed) showFps = !showFps;
            if (frameInput.ToggleFullscreenPressed) ToggleFullscreen();
            if (frameInput.ToggleCreativePressed && World != null && !oreCartOpen)
            {
                World.Mode = World.Mode == GameMode.Creative ? GameMode.Survival : GameMode.Creative;
            }
            if (_ignoreInteractFrames > 0) _ignoreInteractFrames--;
            if (screen == GameScreen.Playing && World != null)
            {
                // Flight is a creative privilege.
                if (frameInput.ToggleNoclipPressed && World.IsCreative && !oreCartOpen)
                    World.NoclipMode = !World.NoclipMode;
                if (frameInput.ToggleCreativeFlightPressed && World.IsCreative && !oreCartOpen)
                    World.CreativeFlightMode = !World.CreativeFlightMode;
                if (frameInput.AdvanceTimePressed && !oreCartOpen) World.AdvanceTime();
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
                    // Fullbright is a renderer light uniform now; the mesh and its light channels
                    // remain stable, so toggling it does not require a chunk remesh.
                }
                if (frameInput.ToggleInventoryPressed)
                {
                    if (oreCartOpen)
                    {
                        World.ReturnHeldStackToInventory();
                        oreCartOpen = false;
                        _openOreCart = null;
                        EnableMouseLook();
                    }
                    else
                    {
                        inventoryOpen = !inventoryOpen;
                        if (inventoryOpen)
                        {
                            // Opening the E-menu also shuts the crafting menu - hand the cursor
                            // stack back rather than stranding it, and drop back to 2x2.
                            if (craftingOpen) { World?.ReturnHeldStackToInventory(); World?.RefundCraftingGrid(); }
                            if (smeltOpen) { World?.ReturnHeldStackToInventory(); World?.CloseSmelter(); }
                            smeltOpen = false;
                            World?.SetCraftingSize(2);
                            craftingOpen = false;
                            DisableMouseLook();
                        }
                        else
                        {
                            World?.ReturnHeldStackToInventory();
                            EnableMouseLook();
                        }
                    }
                }
                // Teleport menu is a creative sandbox convenience.
                if (frameInput.ToggleBiomeMenuPressed && World.IsCreative && !oreCartOpen)
                {
                    biomeMenuOpen = !biomeMenuOpen;
                    if (biomeMenuOpen) DisableMouseLook();
                    else EnableMouseLook();
                }
                // Hand Editor (F8): frees the mouse so sliders can be dragged without looking around.
                if (frameInput.ToggleHandEditorPressed && !oreCartOpen)
                {
                    handEditorOpen = !handEditorOpen;
                    if (handEditorOpen) DisableMouseLook();
                    else EnableMouseLook();
                }
            }
            if (frameInput.CycleRenderDistancePressed) CycleRenderDistance();
            if (screen == GameScreen.Playing && World != null && !oreCartOpen)
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
            // Number keys, like the scroll wheel, must not silently change the selected slot while a
            // menu is open - otherwise pressing 3 to dismiss nothing just swaps your hotbar
            // selection behind the panel.
            if (frameInput.SelectedSlot.HasValue && World != null
                && screen == GameScreen.Playing && mouseLook
                && !inventoryOpen && !craftingOpen && !smeltOpen && !oreCartOpen && !biomeMenuOpen && !handEditorOpen)
            {
                World.SetSelectedSlot(frameInput.SelectedSlot.Value);
            }
            // Q: throw one item - from the hovered inventory slot, else the cursor, else the
            // selected hotbar stack (MC survival behavior).
            if (frameInput.DropItemPressed && World != null && screen == GameScreen.Playing && !oreCartOpen)
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
                && !inventoryOpen && !craftingOpen && !smeltOpen && !oreCartOpen && !biomeMenuOpen && !handEditorOpen)
            {
                int slots = GameWorld.HotbarSlots;
                int next = (World.SelectedSlot + frameInput.HotbarScroll) % slots;
                if (next < 0) next += slots;
                World.SetSelectedSlot(next);
            }
            if (screen == GameScreen.Playing && mouseLook && World != null && _ignoreInteractFrames == 0 && frameInput.BreakBlockPressed)
            {
                // Left-clicking the coupling beam between two linked carts severs the link. Tested
                // before the mob attack so the click breaks the link rather than damaging a cart.
                if (World.Entities.TryBreakLink(World.PlayerPosition, World.GetCameraForward(), out _))
                {
                    TriggerHandSwing();
                }
                else if (!World.TryMeleeAttackEntity(World.PlayerPosition, World.GetCameraForward()))
                {
                    // No mob hit: mining is driven by BreakHeld in StepSimulation, but the click
                    // primes the target so progress starts immediately this frame.
                    // If there's no block under the crosshair either, this is a punch at the air -
                    // play the one-shot swing animation (MC punches the air when nothing is targeted).
                    var airPunch = World.TryPickBlock(World.PlayerPosition, World.GetCameraForward());
                    if (!airPunch.HasValue)
                    {
                        TriggerHandSwing();
                    }
                }
                else
                {
                    // A mob was hit: play the same one-shot swing (MC swings on every attack).
                    TriggerHandSwing();
                }
            }
            if (screen == GameScreen.Playing && mouseLook && World != null && _ignoreInteractFrames == 0 && frameInput.PlaceBlockPressed)
            {
                // In Frontier Survival, tapping flint on a log carves the first rough bench
                // immediately. It is a deliberate use action, not a multi-second held mine.
                if (TryCarveRoughWorkbench())
                {
                    _flintWorkbenchTapConsumed = true;
                }
                // Ore carts are containers, not mountable carts or placeable blocks.
                else if (World.TryPickOreCart(World.PlayerPosition, World.GetCameraForward(), out var oreCart)
                    && oreCart != null)
                {
                    _openOreCart = oreCart;
                    oreCartOpen = true;
                    inventoryOpen = false;
                    craftingOpen = false;
                    smeltOpen = false;
                    DisableMouseLook();
                }
                // Right-click on a workbench opens the crafting menu; on a door it swings the door;
                // on a kiln/forge it opens the smelter; anywhere else places the selected block.
                else if (World.TryPickWorkbench(World.PlayerPosition, World.GetCameraForward(), out int workbenchId))
                {
                    int carpenterBenchId = BlockRegistry.GetId("carpenters_workbench");
                    int gridSize = World.FrontierProgression && workbenchId != carpenterBenchId ? 2 : 3;
                    World.SetCraftingSize(gridSize);
                    craftingOpen = true;
                    inventoryOpen = false;
                    DisableMouseLook();
                }
                else if (World.TryPickSmelter(World.PlayerPosition, World.GetCameraForward(), out int smelterId))
                {
                    World.OpenSmelter(smelterId);
                    smeltOpen = true;
                    inventoryOpen = false;
                    craftingOpen = false;
                    DisableMouseLook();
                }
                else if (TryToggleDoorUnderCrosshair())
                {
                    TriggerHandSwing();
                    _handPokeTimer = 0.3f;
                }
                // Linkage: while holding the coupler, right-click carts to couple them into a train.
                // Checked before the cart/boat branch so it never just seats the player instead.
                else if (World.TryLinkCarts(World.PlayerPosition, World.GetCameraForward()))
                {
                    TriggerHandSwing();
                    _handPokeTimer = 0.3f;
                }
                // Boats: right-click water to launch one, right-click a boat to board it, and
                // right-click again while riding to step out. Checked before placement so the
                // boat item never tries to place a block.
                else if (World.ToggleCartUnderCrosshair(World.PlayerPosition, World.GetCameraForward())
                      || World.ToggleBoatUnderCrosshair(World.PlayerPosition, World.GetCameraForward())
                      || World.TryPlaceCart(World.PlayerPosition, World.GetCameraForward())
                      || World.TryPlaceOreCart(World.PlayerPosition, World.GetCameraForward())
                      || World.TryPlaceLocomotive(World.PlayerPosition, World.GetCameraForward())
                      || World.TryLaunchBoat(World.PlayerPosition, World.GetCameraForward()))
                {
                    TriggerHandSwing();
                }
                else
                {
                    PlaceSelectedBlock();
                }
            }
        }

        private bool TryCarveRoughWorkbench()
        {
            if (World == null || !World.FrontierProgression || World.IsCreative
                || World.SelectedBlock != ItemRegistry.GetId("flint")) return false;
            var pick = World.TryPickBlock(World.PlayerPosition, World.GetCameraForward());
            if (!pick.HasValue) return false;
            var target = pick.Value.Remove;
            if (!World.Chunks.TryGetLoadedBlock(target.x, target.y, target.z, out int blockId)
                || blockId != BlockRegistry.GetId("log")) return false;

            return DeleteBlockAt(target.x, target.y, target.z, GameWorld.WoodChopKind.Flint);
        }

        /// <summary>
        /// Right-clicking a door swings it. Returns true when a door was toggled so the caller
        /// can skip block placement. Mirrors GameWorld.TryPickWorkbench: pick the targeted cell,
        /// then hand it to the world to flip.
        /// </summary>
        private bool TryToggleDoorUnderCrosshair()
        {
            var pick = World.TryPickBlock(World.PlayerPosition, World.GetCameraForward());
            if (!pick.HasValue) return false;
            var pos = pick.Value.Remove;
            return World.TryToggleDoor(pos.x, pos.y, pos.z);
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
