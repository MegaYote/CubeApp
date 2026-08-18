using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using Veldrid;
using Veldrid.SPIRV;

namespace Cubuild.Renderer
{
    public sealed partial class VeldridRenderer : IRenderer, IDisposable
    {
        private void DrawInventoryWindow(Vector2 displaySize)
        {
            if (_iconImGuiId == IntPtr.Zero || _blockIconUv == null) return;
            _hoveredInventorySlot = -1;

            bool survival = _hud.Mode == GameMode.Survival;
            if (survival && _hud.BagSlots == null) return;

            bool textured = survival && _inventoryImGuiId != IntPtr.Zero;
            const float uiScale = 3.0f;

            // Saved grid geometry from the textured E-menu branch, used to snap the cursor-held
            // stack to the same pixel grid as the painted slots.
            Vector2 snapOrigin = Vector2.Zero;
            float snapStartX = 0f, snapStartY = 0f, snapStepX = 0f, snapStepY = 0f, snapHotbarY = 0f, snapSlotPx = 0f;

            // Full-screen semi-transparent gray overlay â€” dims the game world, hotbar, and
            // healthbar behind the inventory, exactly the way Minecraft does.
            // Must be on the Foreground draw list so it paints over the HUD (hotbar/health
            // are also Foreground), but the panel + slots draw later on the same list on top.
            var fgOverlay = ImGui.GetForegroundDrawList();
            uint dimCol = ImGui.ColorConvertFloat4ToU32(new Vector4(0.10f, 0.10f, 0.10f, 0.65f));
            fgOverlay.AddRectFilled(Vector2.Zero, displaySize, dimCol);

            float winW, winH;
            if (textured)
            {
                // The original C++ E-menu: 190x111 background + a small header strip. The window
                // itself is invisible (NoBackground) so only the panel texture shows, floating
                // over the paused world instead of a black ImGui box.
                winW = 190f * uiScale;
                winH = 111f * uiScale + 27f;
            }
            else
            {
                winW = Math.Min(680, displaySize.X - 32);
                winH = Math.Min(520, displaySize.Y - 64);
            }
            float winX = (displaySize.X - winW) / 2f;
            float winY = Math.Max(30f, (displaySize.Y - winH) / 2f); // centered, like the crafting menu
            ImGui.SetNextWindowPos(new Vector2(winX, winY), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new Vector2(winW, winH), ImGuiCond.Always);
            ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar;
            if (textured)
            {
                flags |= ImGuiWindowFlags.NoBackground;
                ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            }
            ImGui.Begin("##inventory", flags);
            ImGui.Text(survival
                ? "Left: pick/place   Right: half/drop 1   Shift: move   Outside: throw"
                : "Inventory - click a block to put it in the selected hotbar slot");

            if (textured)
            {
                // Draw the original E-menu background and overlay the slots at the exact texture
                // coordinates from the C++ Inventory.h (SLOT_START_X/Y, SLOT_SIZE, HOTBAR_GAP).
                // Everything visual goes on the Foreground draw list so it sits on top of the
                // screen-wide dim overlay; the window layer only carries InvisibleButtons for clicks.
                float topY = ImGui.GetCursorPosY();
                var contentScreen = ImGui.GetCursorScreenPos();
                var fg = ImGui.GetForegroundDrawList();
                float bgW = 190f * uiScale, bgH = 111f * uiScale;
                // contentScreen is the cursor AFTER the header text, so its Y already includes
                // topY - only the 4px gap is left to add.
                float imgTop = contentScreen.Y + 4f;
                uint fgTextCol = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f));
                fg.AddImage(_inventoryImGuiId, new Vector2(contentScreen.X, imgTop), new Vector2(contentScreen.X + bgW, imgTop + bgH), Vector2.Zero, Vector2.One);

                float slotPx = 16f * uiScale;
                float stepX = 18f * uiScale, stepY = 18f * uiScale;
                float startX = 5f * uiScale, startY = 6f * uiScale;
                float hotbarY = 88f * uiScale;
                float yBase = topY + 4f;
                // The painted slot cells have a 1px dark border on every side â€” inset the icon
                // by that border so the block art sits perfectly centered inside the cell opening.
                float borderPx = 1f * uiScale;
                snapOrigin = new Vector2(contentScreen.X, contentScreen.Y - topY);
                snapStartX = startX; snapStartY = startY; snapStepX = stepX; snapStepY = stepY;
                snapHotbarY = hotbarY; snapSlotPx = slotPx;

                for (int row = 0; row < 4; row++)
                {
                    for (int col = 0; col < 10; col++)
                    {
                        int slot = row * 10 + col;
                        var contents = (_hud.BagSlots != null && slot < _hud.BagSlots.Count)
                            ? _hud.BagSlots[slot] : default;
                        ImGui.SetCursorPos(new Vector2(startX + col * stepX + borderPx, yBase + startY + row * stepY + borderPx));
                        DrawInventorySlotCellAt($"bg{slot}", contents.ItemId, contents.Count, 0, slot, slotPx, fg, fgTextCol);
                    }
                }
                for (int i = 0; i < 10; i++)
                {
                    int bid = (_hud.Hotbar != null && i < _hud.Hotbar.Count) ? _hud.Hotbar[i] : 0;
                    int count = (_hud.HotbarCounts != null && i < _hud.HotbarCounts.Count) ? _hud.HotbarCounts[i] : 0;
                    ImGui.SetCursorPos(new Vector2(startX + i * stepX + borderPx, yBase + hotbarY + borderPx));
                    DrawInventorySlotCellAt($"hb{i}", bid, count, 1, i, slotPx, fg, fgTextCol);
                }
            }
            else if (survival)
            {
                // Fallback grid (no texture): 4 rows x 10 slots.
                for (int row = 0; row < 4; row++)
                {
                    for (int col = 0; col < 10; col++)
                    {
                        int slot = row * 10 + col;
                        var contents = (_hud.BagSlots != null && slot < _hud.BagSlots.Count)
                            ? _hud.BagSlots[slot] : default;
                        DrawInventorySlotCell($"bg{slot}", contents.ItemId, contents.Count, 0, slot);
                        if (col != 9) ImGui.SameLine(0, 4);
                    }
                    if (row != 3) ImGui.Dummy(new Vector2(0, 2));
                }
                ImGui.Dummy(new Vector2(0, 8));
                ImGui.Separator();
                ImGui.Text("Hotbar");
                for (int i = 0; i < 10; i++)
                {
                    int bid = (_hud.Hotbar != null && i < _hud.Hotbar.Count) ? _hud.Hotbar[i] : 0;
                    int count = (_hud.HotbarCounts != null && i < _hud.HotbarCounts.Count) ? _hud.HotbarCounts[i] : 0;
                    DrawInventorySlotCell($"hb{i}", bid, count, 1, i);
                    if (i != 9) ImGui.SameLine(0, 4);
                }
            }
            else
            {
                int perRow = Math.Max(1, (int)(winW / 64f));
                // Blocks (isometric cube icons)...
                for (int id = 1; id < BlockRegistry.Count; id++)
                {
                    if (!BlockRegistry.IsInInventory(id)) continue;
                    var uv = _blockIconUv[id];
                    string name = BlockRegistry.GetById(id).DisplayName;
                    ImGui.PushID(id);
                    if (ImGui.ImageButton($"##icon{id}", _iconImGuiId, new Vector2(48, 48),
                            new Vector2(uv.X, uv.Y), new Vector2(uv.X + uv.Z, uv.Y + uv.W),
                            Vector4.Zero, Vector4.One))
                    {
                        _inventorySelections.Enqueue(id);
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(name);
                    ImGui.PopID();
                    if (id % perRow != 0) ImGui.SameLine();
                }
                // ...then genuine items (flat sprites from the items atlas).
                for (int id = ItemRegistry.ItemIdBase; id < ItemRegistry.Count; id++)
                {
                    if (!ItemRegistry.IsInInventory(id)) continue;
                    var uv = IconUv(id, out IntPtr iconTex);
                    if (iconTex == IntPtr.Zero) continue;
                    string name = ItemRegistry.Get(id).DisplayName;
                    ImGui.PushID(id);
                    if (ImGui.ImageButton($"##itemicon{id}", iconTex, new Vector2(48, 48),
                            new Vector2(uv.X, uv.Y), new Vector2(uv.X + uv.Z, uv.Y + uv.W),
                            Vector4.Zero, Vector4.One))
                    {
                        _inventorySelections.Enqueue(id);
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(name);
                    ImGui.PopID();
                    if (id % perRow != 0) ImGui.SameLine();
                }
            }

            ImGui.End();

            if (textured)
            {
                ImGui.PopStyleVar(); // WindowPadding
            }

            if (survival)
            {
                // Clicks OUTSIDE the window rect throw items: left = whole stack, right = one.
                // (Checked against the window rect, not hover flags, so clicks on the hotbar row
                // inside the window never count as outside.)
                var mousePos = ImGui.GetMousePos();
                bool insideWindow = mousePos.X >= winX && mousePos.X <= winX + winW
                    && mousePos.Y >= winY && mousePos.Y <= winY + winH;
                if (!insideWindow)
                {
                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) _inventoryClicks.Enqueue((2, 0, 0));
                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Right)) _inventoryClicks.Enqueue((2, 0, 1));
                }

                // Draw the cursor-held stack floating at the mouse.
                if (_hud.HeldStack.HasValue)
                {
                    int bid = _hud.HeldStack.Value.ItemId;
                    var heldUv = IconUv(bid, out IntPtr heldTex);
                    if (heldTex != IntPtr.Zero)
                    {
                        var mp = ImGui.GetMousePos();
                        var drawList = ImGui.GetForegroundDrawList();

                        // Follow the cursor freely, same pixel-size as the slot cells (48px),
                        // so the stack looks like it belongs to the UI even when dragged between slots.
                        float half = snapSlotPx > 0f ? snapSlotPx * 0.5f : 16f;
                        drawList.AddImage(heldTex, mp + new Vector2(-half, -half), mp + new Vector2(half, half),
                            new Vector2(heldUv.X, heldUv.Y), new Vector2(heldUv.X + heldUv.Z, heldUv.Y + heldUv.W));
                        int heldCount = _hud.HeldStack.Value.Count;
                        if (heldCount > 1)
                            drawList.AddText(mp + new Vector2(half - 16f, half - 17f), ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 1)),
                                heldCount.ToString());
                    }
                }
            }
        }

        // One inventory slot cell: a 48px item icon (or a blank cell) with its count, wired to
        // the drag-click queue. kind: 0=bag, 1=hotbar, 3=quick-move. target: unified slot index
        // for clicks (bag 0..39, hotbar 40..49).
        private void DrawInventorySlotCell(string id, int itemId, int count, int kind, int target)
        {
            int unified = kind == 1 ? 40 + target : target;
            bool shift = ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift);
            ImGui.PushID(id);

            // Check if this item has a flat sprite (items.png tile) - e.g., sap, flint, etc.
            // Also check for blocks with items.json overrides (like sap)
            IntPtr flatTex = IntPtr.Zero;
            Vector4 flatUv = default;
            bool hasFlatSprite = TryGetFlatSpriteUv(itemId, out flatUv, out flatTex);
            if (!hasFlatSprite && itemId < BlockRegistry.Count)
            {
                hasFlatSprite = TryGetBlockFlatSpriteUv(itemId, out var blockUv, out var blockTex);
                if (hasFlatSprite) { flatUv = blockUv; flatTex = blockTex; }
            }

            if (hasFlatSprite && flatTex != IntPtr.Zero)
            {
                ImGui.ImageButton($"##{id}", flatTex, new Vector2(48, 48),
                    new Vector2(flatUv.X, flatUv.Y), new Vector2(flatUv.X + flatUv.Z, flatUv.Y + flatUv.W),
                    Vector4.Zero, Vector4.One);
                if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && shift) _inventoryClicks.Enqueue((3, unified, 0));
                else if (ImGui.IsItemClicked(ImGuiMouseButton.Left)) _inventoryClicks.Enqueue((kind, unified, 0));
                if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) _inventoryClicks.Enqueue((kind, unified, 1));
                if (ImGui.IsItemHovered())
                {
                    _hoveredInventorySlot = unified;
                    ImGui.SetTooltip(ItemRegistry.Get(itemId).DisplayName);
                }
                if (ItemRegistry.StackSizeOf(itemId) > 1)
                {
                    ImGui.SameLine(0, 4);
                    ImGui.Text(count.ToString());
                }
            }
            else
            {
                // Fallback to block cube icon or item atlas
                var uv = IconUv(itemId, out IntPtr iconTex);
                if (iconTex != IntPtr.Zero)
                {
                    ImGui.ImageButton($"##{id}", iconTex, new Vector2(48, 48),
                        new Vector2(uv.X, uv.Y), new Vector2(uv.X + uv.Z, uv.Y + uv.W),
                        Vector4.Zero, Vector4.One);
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && shift) _inventoryClicks.Enqueue((3, unified, 0));
                    else if (ImGui.IsItemClicked(ImGuiMouseButton.Left)) _inventoryClicks.Enqueue((kind, unified, 0));
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) _inventoryClicks.Enqueue((kind, unified, 1));
                    if (ImGui.IsItemHovered())
                    {
                        _hoveredInventorySlot = unified;
                        ImGui.SetTooltip(ItemRegistry.Get(itemId).DisplayName);
                    }
                    if (ItemRegistry.StackSizeOf(itemId) > 1)
                    {
                        ImGui.SameLine(0, 4);
                        ImGui.Text(count.ToString());
                    }
                }
                else
                {
                    ImGui.ImageButton($"##{id}", _iconImGuiId, new Vector2(48, 48),
                        new Vector2(0, 0), new Vector2(1, 1),
                        new Vector4(0.12f, 0.12f, 0.12f, 1f), new Vector4(0.3f, 0.3f, 0.3f, 1f));
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && shift) _inventoryClicks.Enqueue((3, unified, 0));
                    else if (ImGui.IsItemClicked(ImGuiMouseButton.Left)) _inventoryClicks.Enqueue((kind, unified, 0));
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) _inventoryClicks.Enqueue((kind, unified, 1));
                    if (ImGui.IsItemHovered()) _hoveredInventorySlot = unified;
                }
            }
            ImGui.PopID();
        }

        // Positioned slot cell for the textured E-menu: an invisible click target (window layer)
        // whose item icon + count are drawn on the Foreground draw list so they sit on top of
        // the screen-wide dim overlay.
        private void DrawInventorySlotCellAt(string id, int itemId, int count, int kind, int target, float slotPx, ImDrawListPtr fg, uint fgTextCol)
        {
            int unified = kind == 1 ? 40 + target : target;
            bool shift = ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift);
            ImGui.PushID(id);

            // Always an invisible button â€” clicks are handled here, visuals are on the
            // foreground list so they paint over the dim overlay (and the hotbar/health).
            ImGui.InvisibleButton($"##{id}", new Vector2(slotPx, slotPx));
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && shift) _inventoryClicks.Enqueue((3, unified, 0));
            else if (ImGui.IsItemClicked(ImGuiMouseButton.Left)) _inventoryClicks.Enqueue((kind, unified, 0));
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) _inventoryClicks.Enqueue((kind, unified, 1));
            bool hovered = false;
            if (ImGui.IsItemHovered())
            {
                _hoveredInventorySlot = unified;
                hovered = true;
                if (itemId > 0)
                    ImGui.SetTooltip(ItemRegistry.Get(itemId).DisplayName);
            }

            // Foreground visuals: a subtle white highlight on hover, then the item icon + count.
            if (hovered)
            {
                uint hoverCol = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.22f));
                fg.AddRectFilled(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), hoverCol);
            }
            // Check for flat sprite (items.png tile) first - e.g., sap, flint, etc.
                bool hasFlatSprite = TryGetFlatSpriteUv(itemId, out var cellUv, out IntPtr cellTex);
                if (!hasFlatSprite && itemId < BlockRegistry.Count)
                {
                    hasFlatSprite = TryGetBlockFlatSpriteUv(itemId, out cellUv, out cellTex);
                }
                if (!hasFlatSprite)
                {
                    cellUv = IconUv(itemId, out cellTex);
                }
                if (cellTex != IntPtr.Zero)
                {
                    var rmin = ImGui.GetItemRectMin();
                    var rmax = ImGui.GetItemRectMax();
                    fg.AddImage(cellTex, rmin, rmax,
                        new Vector2(cellUv.X, cellUv.Y), new Vector2(cellUv.X + cellUv.Z, cellUv.Y + cellUv.W));
                    // Tools (stack size 1) don't show a count, like Minecraft.
                    if (count > 1 && ItemRegistry.StackSizeOf(itemId) > 1)
                        fg.AddText(rmax - new Vector2(16, 17), fgTextCol, count.ToString());
                }

            ImGui.PopID();
        }

        // Workbench crafting menu: the user's 111x49 design scaled 3x to match the
        // survival E-menu's uiScale (DrawInventoryWindow uses 3.0f). Layout (design px):
        //   2x2 grid cells at (7,7),(24,7),(7,25),(24,25), each 16x17
        //   result well (79,16) 11x17 — left click crafts the shown recipe
        //   cursor cell (91,16) 13x17 — shows the held stack (display only)
        // Below the panel: the player inventory, Minecraft-style — 4 rows x 10 bag slots
        // + a hotbar row (10-wide, so the window is wider than the panel art, which floats
        // centered above it). Bag/hotbar slots reuse E-menu click kinds 0/1 so the cursor
        // drags items straight from the inventory into the crafting grid.
        // Clicks ride the inventory click queue: kind 4 = grid slot (target 0..3),
        // kind 5 = result. Program maps them to GameWorld.CraftingClickSlot / TryCraft.
        private void DrawCraftingWindow(Vector2 displaySize)
        {
            // Same scale as the survival E-menu (3x) so both panels feel identical.
            const float uiScale = 3.0f;
            const float imgW = 111f, imgH = 49f;
            // Inventory section below the panel uses the E-menu texture (190x111 design),
            // which is wider than the crafting art (111x49); the art floats centered above it.
            const float invW = 190f * uiScale;
            float winW = invW;
            float winH = imgH * uiScale + 24f + 111f * uiScale + 24f;
            float winX = (displaySize.X - winW) / 2f;
            float winY = Math.Max(30f, (displaySize.Y - winH) / 2f);
            // Panel art sits centered above the inventory section.
            Vector2 artOffset = new((winW - imgW * uiScale) * 0.5f, 0f);

            // Dim the world behind the panel, like the E-menu.
            var fgOverlay = ImGui.GetForegroundDrawList();
            uint dimCol = ImGui.ColorConvertFloat4ToU32(new Vector4(0.10f, 0.10f, 0.10f, 0.65f));
            fgOverlay.AddRectFilled(Vector2.Zero, displaySize, dimCol);

            ImGui.SetNextWindowPos(new Vector2(winX, winY), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new Vector2(winW, winH), ImGuiCond.Always);
            ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar
                | ImGuiWindowFlags.NoBackground;
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            ImGui.Begin("##crafting", flags);
            var contentScreen = ImGui.GetCursorScreenPos();
            var fg = ImGui.GetForegroundDrawList();
            uint fgTextCol = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f));

            // The panel art (or a plain fallback if the texture is missing), centered
            // horizontally above the inventory section.
            var artTopLeft = contentScreen + artOffset;
            if (_craftingImGuiId != IntPtr.Zero)
            {
                fg.AddImage(_craftingImGuiId, artTopLeft, artTopLeft + new Vector2(imgW * uiScale, imgH * uiScale), Vector2.Zero, Vector2.One);
            }
            else
            {
                uint panelCol = ImGui.ColorConvertFloat4ToU32(new Vector4(0.33f, 0.34f, 0.34f, 1f));
                uint cellCol = ImGui.ColorConvertFloat4ToU32(new Vector4(0.20f, 0.21f, 0.21f, 1f));
                fg.AddRectFilled(artTopLeft, artTopLeft + new Vector2(46f * uiScale, 49f * uiScale), panelCol);
                fg.AddRectFilled(artTopLeft + new Vector2(73f * uiScale, 9f * uiScale), artTopLeft + new Vector2(109f * uiScale, 39f * uiScale), panelCol);
                fg.AddRectFilled(artTopLeft + new Vector2(7f * uiScale, 7f * uiScale), artTopLeft + new Vector2(40f * uiScale, 42f * uiScale), cellCol);
                fg.AddRectFilled(artTopLeft + new Vector2(79f * uiScale, 16f * uiScale), artTopLeft + new Vector2(104f * uiScale, 33f * uiScale), cellCol);
            }

            // 2x2 grid cells.
            var gridPos = new[] { new Vector2(7, 7), new Vector2(24, 7), new Vector2(7, 25), new Vector2(24, 25) };
            var gridSize = new Vector2(16, 17);
            for (int i = 0; i < 4; i++)
            {
                var cell = gridPos[i];
                var screenMin = artTopLeft + cell * uiScale;
                var screenMax = screenMin + gridSize * uiScale;
                (int ItemId, int Count) contents = _hud.CraftingSlots != null && i < _hud.CraftingSlots.Count
                    ? _hud.CraftingSlots[i] : (0, 0);
                ImGui.SetCursorPos(artOffset + cell * uiScale);
                DrawCraftingCellAt($"cg{i}", contents.ItemId, contents.Count, screenMin, screenMax,
                    4, i, 1f * uiScale, 16f * uiScale, fg, fgTextCol);
            }

            // Result well: shows the crafted product; left click crafts.
            {
                var screenMin = artTopLeft + new Vector2(79, 16) * uiScale;
                var screenMax = screenMin + new Vector2(11, 17) * uiScale;
                ImGui.SetCursorPos(artOffset + new Vector2(79, 16) * uiScale);
                int resId = _hud.CraftingResult?.ItemId ?? 0;
                int resCount = _hud.CraftingResult?.Count ?? 0;
                DrawCraftingCellAt("cres", resId, resCount, screenMin, screenMax, 5, 0,
                    4f * uiScale, 15f * uiScale, fg, fgTextCol);
            }

            // Cursor cell: display-only copy of the held stack so the design's right box
            // shows what's in your hand (the floating cursor also follows the mouse).
            if (_hud.HeldStack.HasValue)
            {
                var heldUv = IconUv(_hud.HeldStack.Value.ItemId, out IntPtr heldTex);
                if (heldTex != IntPtr.Zero)
                {
                    var center = artTopLeft + new Vector2(97.5f, 24.5f) * uiScale;
                    float iconSize = 13f * uiScale;
                    fg.AddImage(heldTex, center - new Vector2(iconSize * 0.5f, iconSize * 0.5f), center + new Vector2(iconSize * 0.5f, iconSize * 0.5f),
                        new Vector2(heldUv.X, heldUv.Y), new Vector2(heldUv.X + heldUv.Z, heldUv.Y + heldUv.W));
                }
            }

            // ---- player inventory below the panel ----
            // Uses the SAME UI texture as the regular E-menu (inventory.png), placed directly
            // beneath the crafting art. Its painted bag/hotbar slots get invisible buttons at
            // the exact E-menu coordinates (design px: bag (5+18c, 6+18r), hotbar (5+18i, 88),
            // cell 16x16, 1px border inset) and the same click kinds (0 = bag, 1 = hotbar).
            float slotPx = 16f * uiScale;
            float borderPx = 1f * uiScale;
            float invTop = imgH * uiScale + 24f;
            if (_inventoryImGuiId != IntPtr.Zero)
            {
                var invTopLeft = contentScreen + new Vector2(0f, invTop);
                fg.AddImage(_inventoryImGuiId, invTopLeft, invTopLeft + new Vector2(190f, 111f) * uiScale, Vector2.Zero, Vector2.One);
                for (int row = 0; row < 4; row++)
                {
                    for (int col = 0; col < 10; col++)
                    {
                        int slot = row * 10 + col;
                        var contents = _hud.BagSlots != null && slot < _hud.BagSlots.Count
                            ? _hud.BagSlots[slot] : default;
                        var pos = new Vector2((5f + col * 18f) * uiScale + borderPx,
                            invTop + (6f + row * 18f) * uiScale + borderPx);
                        ImGui.SetCursorPos(pos);
                        DrawInventorySlotCellAt($"cbg{slot}", contents.ItemId, contents.Count, 0, slot,
                            slotPx, fg, fgTextCol);
                    }
                }
                for (int i = 0; i < 10; i++)
                {
                    int bid = _hud.Hotbar != null && i < _hud.Hotbar.Count ? _hud.Hotbar[i] : 0;
                    int count = _hud.HotbarCounts != null && i < _hud.HotbarCounts.Count ? _hud.HotbarCounts[i] : 0;
                    var pos = new Vector2((5f + i * 18f) * uiScale + borderPx,
                        invTop + 88f * uiScale + borderPx);
                    ImGui.SetCursorPos(pos);
                    DrawInventorySlotCellAt($"chb{i}", bid, count, 1, i, slotPx, fg, fgTextCol);
                }
            }
            else
            {
                // No texture: plain grey cells so the menu is still usable.
                float stepX = 18f * uiScale, stepY = 18f * uiScale;
                uint slotBorder = ImGui.ColorConvertFloat4ToU32(new Vector4(0.55f, 0.58f, 0.62f, 0.9f));
                uint slotFill = ImGui.ColorConvertFloat4ToU32(new Vector4(0.13f, 0.16f, 0.19f, 1f));
                for (int row = 0; row < 4; row++)
                {
                    for (int col = 0; col < 10; col++)
                    {
                        int slot = row * 10 + col;
                        var contents = _hud.BagSlots != null && slot < _hud.BagSlots.Count
                            ? _hud.BagSlots[slot] : default;
                        var pos = new Vector2(col * stepX, invTop + row * stepY);
                        DrawCraftingInvCell($"cbg{slot}", contents.ItemId, contents.Count, pos, 0, slot,
                            slotPx, 3f, slotFill, slotBorder, contentScreen, fg, fgTextCol);
                    }
                }
                for (int i = 0; i < 10; i++)
                {
                    int bid = _hud.Hotbar != null && i < _hud.Hotbar.Count ? _hud.Hotbar[i] : 0;
                    int count = _hud.HotbarCounts != null && i < _hud.HotbarCounts.Count ? _hud.HotbarCounts[i] : 0;
                    var pos = new Vector2(i * stepX, invTop + 4f * stepY + 12f);
                    DrawCraftingInvCell($"chb{i}", bid, count, pos, 1, i,
                        slotPx, 3f, slotFill, slotBorder, contentScreen, fg, fgTextCol);
                }
            }

            ImGui.End();
            ImGui.PopStyleVar(); // WindowPadding

            // Clicks outside the window throw items, matching the E-menu.
            var mousePos = ImGui.GetMousePos();
            bool insideWindow = mousePos.X >= winX && mousePos.X <= winX + winW
                && mousePos.Y >= winY && mousePos.Y <= winY + winH;
            if (!insideWindow)
            {
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) _inventoryClicks.Enqueue((2, 0, 0));
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Right)) _inventoryClicks.Enqueue((2, 0, 1));
            }

            // The cursor-held stack floats at the mouse, like the E-menu.
            if (_hud.HeldStack.HasValue)
            {
                int bid = _hud.HeldStack.Value.ItemId;
                var heldUv2 = IconUv(bid, out IntPtr heldTex2);
                if (heldTex2 != IntPtr.Zero)
                {
                    var mp = ImGui.GetMousePos();
                    var drawList = ImGui.GetForegroundDrawList();
                    float half = 16f * uiScale * 0.5f;
                    drawList.AddImage(heldTex2, mp + new Vector2(-half, -half), mp + new Vector2(half, half),
                        new Vector2(heldUv2.X, heldUv2.Y), new Vector2(heldUv2.X + heldUv2.Z, heldUv2.Y + heldUv2.W));
                    int heldCount = _hud.HeldStack.Value.Count;
                    if (heldCount > 1)
                        drawList.AddText(mp + new Vector2(half - 16f, half - 17f),
                            ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 1)), heldCount.ToString());
                }
            }
        }

        // One inventory slot in the crafting menu's lower section: a painted cell background
        // (dark fill + light border, like Minecraft) on the Foreground list, then the shared
        // E-menu cell logic on top for clicks/hover/icon/count (kind 0 = bag, 1 = hotbar).
        private void DrawCraftingInvCell(string id, int itemId, int count, Vector2 pos,
            int kind, int target, float slotPx, float borderPx, uint fillCol, uint borderCol,
            Vector2 contentScreen, ImDrawListPtr fg, uint fgTextCol)
        {
            var screenMin = contentScreen + pos;
            var screenMax = screenMin + new Vector2(slotPx, slotPx);
            fg.AddRectFilled(screenMin, screenMax, fillCol);
            fg.AddRect(screenMin, screenMax, borderCol, 0f, 0, borderPx);
            ImGui.SetCursorPos(pos);
            DrawInventorySlotCellAt(id, itemId, count, kind, target, slotPx, fg, fgTextCol);
        }

        // One crafting cell: invisible click target (window layer) whose item icon + count are
        // drawn on the Foreground list so they sit on top of the dim overlay. kind 4 = grid slot
        // (target 0..3), kind 5 = result well. inset/size are in SCREEN pixels.
        private void DrawCraftingCellAt(string id, int itemId, int count, Vector2 screenMin, Vector2 screenMax,
            int kind, int target, float inset, float iconSize, ImDrawListPtr fg, uint fgTextCol)
        {
            ImGui.PushID(id);
            ImGui.InvisibleButton($"##{id}", screenMax - screenMin);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left)) _inventoryClicks.Enqueue((kind, target, 0));
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) _inventoryClicks.Enqueue((kind, target, 1));
            bool hovered = false;
            if (ImGui.IsItemHovered())
            {
                hovered = true;
                if (itemId > 0)
                {
                    string name = ItemRegistry.Get(itemId).DisplayName;
                    ImGui.SetTooltip(kind == 5 ? $"Craft {name}" : name);
                }
            }

            if (hovered)
            {
                uint hoverCol = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.22f));
                fg.AddRectFilled(screenMin, screenMax, hoverCol);
            }
            var cellUv = IconUv(itemId, out IntPtr cellTex);
            if (cellTex != IntPtr.Zero)
            {
                var iconMin = screenMin + new Vector2(inset, inset);
                fg.AddImage(cellTex, iconMin, iconMin + new Vector2(iconSize, iconSize),
                    new Vector2(cellUv.X, cellUv.Y), new Vector2(cellUv.X + cellUv.Z, cellUv.Y + cellUv.W));
                if (count > 1 && ItemRegistry.StackSizeOf(itemId) > 1)
                    fg.AddText(screenMax - new Vector2(16, 17), fgTextCol, count.ToString());
            }
            ImGui.PopID();
        }

        // Biome teleport menu (B key): lists every biome, clicking one queues a teleport request
        // that Program consumes to find and jump to the nearest location of that biome. The last
        // entry is special: the Great Pyramid has a fixed once-per-world location.
        private static readonly string[] BiomeMenuNames = { "Ocean", "Plains", "Hills", "Mountains", "Desert", "The Great Pyramid" };

        private void DrawBiomeMenu(Vector2 displaySize)
        {
            float winW = Math.Min(280, displaySize.X - 32);
            ImGui.SetNextWindowPos(new Vector2((displaySize.X - winW) / 2f, 120), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new Vector2(winW, 0), ImGuiCond.Always);
            ImGui.Begin("##biomemenu", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse);
            ImGui.Text("Teleport to nearest biome");
            ImGui.Separator();
            foreach (var name in BiomeMenuNames)
            {
                if (ImGui.Button(name, new Vector2(winW - 24, 32)))
                {
                    _biomeSelections.Enqueue(name);
                }
                ImGui.Spacing();
            }
            ImGui.End();
        }

        // Tiles the dirt block texture across the screen - the classic menu background.
        // Uses the BACKGROUND draw list so the ImGui menu windows render on top of it.
        private void DrawDirtBackground(Vector2 screenSize, string blockName = "dirt")
        {
            if (_terrainImGuiId == IntPtr.Zero) return;
            var dirt = BlockRegistry.Get(blockName).AllTexture;
            if (!dirt.HasValue) return;
            var tr = dirt.Value;
            float u0 = tr.X / _atlasWidth;
            float v0 = tr.Y / _atlasHeight;
            float uw = tr.Width / _atlasWidth;
            float vh = tr.Height / _atlasHeight;
            var drawList = ImGui.GetBackgroundDrawList();
            const float tile = 48f;
            uint tint = ImGui.ColorConvertFloat4ToU32(new Vector4(0.72f, 0.72f, 0.72f, 1f));
            for (float y = 0; y < screenSize.Y; y += tile)
            {
                for (float x = 0; x < screenSize.X; x += tile)
                {
                    drawList.AddImage(_terrainImGuiId,
                        new Vector2(x, y),
                        new Vector2(Math.Min(x + tile, screenSize.X), Math.Min(y + tile, screenSize.Y)),
                        new Vector2(u0, v0), new Vector2(u0 + uw, v0 + vh), tint);
                }
            }
        }

        // The title / create-world / pause menus. Driven by MenuState (shared with Program).
        private void DrawMenu()
        {
            var m = _hud.Menu;
            if (m == null) return;
            var io = ImGui.GetIO();
            var size = io.DisplaySize;

            // The loading screen uses the same dirt background as the title, with a phase name +
            // progress bars on top.
            if (m.Screen == GameScreen.Loading)
            {
                DrawLoadingScreen(m, size);
                return;
            }

            // The title/create screens get the dirt background; the pause and death menus just dim
            // the frozen world behind them with a translucent wash (no tiled image).
            if (m.Screen == GameScreen.Paused)
            {
                uint tint = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.45f));
                ImGui.GetBackgroundDrawList().AddRectFilled(Vector2.Zero, size, tint);
            }
            else if (m.Screen == GameScreen.Dead)
            {
                // Death screen: a red gradient over the player's existing camera view. Built from
                // stacked translucent rects (more opaque toward the bottom) so the frozen world
                // shows through dimly rather than being hidden behind a tiled image.
                var bg = ImGui.GetBackgroundDrawList();
                int bands = 24;
                for (int i = 0; i < bands; i++)
                {
                    float t0 = (float)i / bands;
                    float t1 = (float)(i + 1) / bands;
                    float alpha = 0.15f + 0.45f * t0; // darker toward the bottom
                    uint c = ImGui.ColorConvertFloat4ToU32(new Vector4(0.35f, 0f, 0f, alpha));
                    bg.AddRectFilled(
                        new Vector2(0, size.Y * t0),
                        new Vector2(size.X, size.Y * t1),
                        c);
                }
            }
            else
            {
                DrawDirtBackground(size);
            }

            ImGuiWindowFlags windowFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar
                | ImGuiWindowFlags.NoScrollWithMouse;

            if (m.Screen == GameScreen.Title)
            {
                // Logo sits at the top-center of the screen.
                const float logoW = 224f;
                const float logoH = 224f;
                if (_logoImGuiId != IntPtr.Zero)
                {
                    ImGui.SetNextWindowPos(new Vector2((size.X - logoW) / 2f, 30f), ImGuiCond.Always);
                    ImGui.SetNextWindowSize(new Vector2(logoW, logoH), ImGuiCond.Always);
                    ImGui.Begin("##logo", windowFlags | ImGuiWindowFlags.NoBackground);
                    ImGui.Image(_logoImGuiId, new Vector2(logoW, logoH));
                    ImGui.End();
                }
                else
                {
                    // Fallback text logo at the same spot.
                    ImGui.SetNextWindowPos(new Vector2((size.X - 200f) / 2f, 40f), ImGuiCond.Always);
                    ImGui.SetNextWindowSize(new Vector2(200, 60), ImGuiCond.Always);
                    ImGui.Begin("##logo", windowFlags | ImGuiWindowFlags.NoBackground);
                    ImGui.SetWindowFontScale(2.4f);
                    var titlePos = ImGui.GetCursorScreenPos();
                    var titleFont = ImGui.GetFont();
                    float titleSize = ImGui.GetFontSize();
                    uint shadowCol = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.55f));
                    uint whiteCol = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f));
                    var titleDraw = ImGui.GetWindowDrawList();
                    titleDraw.AddText(titleFont, titleSize, titlePos + new Vector2(3, 3), shadowCol, "Cubuild");
                    titleDraw.AddText(titleFont, titleSize, titlePos, whiteCol, "Cubuild");
                    ImGui.SetWindowFontScale(1f);
                    ImGui.End();
                }

                // Buttons hang lower, in the classic vertical column.
                ImGui.SetNextWindowPos(new Vector2((size.X - 220f) / 2f, size.Y / 4f + 72f), ImGuiCond.Always);
                ImGui.SetNextWindowSize(new Vector2(220, 250), ImGuiCond.Always);
                ImGui.Begin("##title", windowFlags);
                if (ImGui.Button("Singleplayer", new Vector2(200, 34)))
                {
                    m.Screen = GameScreen.WorldSelect;
                    _menuBuffersInitialized = false;
                }
                ImGui.Dummy(new Vector2(0, 18));
                if (ImGui.Button("Multiplayer", new Vector2(200, 34)))
                {
                    m.Screen = GameScreen.Multiplayer;
                    _menuBuffersInitialized = false;
                }
                ImGui.Dummy(new Vector2(0, 18));
                if (ImGui.Button("Settings", new Vector2(200, 34)))
                {
                    m.Screen = GameScreen.Settings;
                    m.SettingsReturnTo = GameScreen.Title;
                    m.SettingsOpen = true;
                    m.SelectedCullingMode = GetCullingMode();
                    _menuBuffersInitialized = false;
                }
                ImGui.Dummy(new Vector2(0, 18));
                if (ImGui.Button("Quit", new Vector2(200, 34))) m.QuitClicked = true;
                ImGui.End();
            }
            else if (m.Screen == GameScreen.CreateWorld)
            {
                ImGui.SetNextWindowPos(new Vector2(size.X / 2f - 150f, size.Y / 2f - 150f), ImGuiCond.Always);
                ImGui.SetNextWindowSize(new Vector2(300, 300), ImGuiCond.Always);
                ImGui.Begin("##createworld", windowFlags);
                ImGui.Text("Create World");
                ImGui.Spacing();
                if (!_menuBuffersInitialized)
                {
                    WriteBuffer(_worldNameBuffer, m.WorldName);
                    WriteBuffer(_seedBuffer, m.SeedInput);
                    _menuBuffersInitialized = true;
                }
                ImGui.InputText("World name", _worldNameBuffer, (uint)_worldNameBuffer.Length);
                m.WorldName = ReadBuffer(_worldNameBuffer);
                ImGui.InputText("Seed (optional)", _seedBuffer, (uint)_seedBuffer.Length);
                m.SeedInput = ReadBuffer(_seedBuffer);
                ImGui.Spacing();
                ImGui.Text("Mode");
                if (ImGui.RadioButton("Creative", m.SelectedMode == GameMode.Creative))
                {
                    m.SelectedMode = GameMode.Creative;
                }
                ImGui.SameLine();
                if (ImGui.RadioButton("Survival", m.SelectedMode == GameMode.Survival))
                {
                    m.SelectedMode = GameMode.Survival;
                }
                ImGui.Spacing();
                if (ImGui.Button("Create World", new Vector2(220, 34)))
                {
                    m.CreateWorldClicked = true;
                }
                ImGui.Spacing();
                if (ImGui.Button("Back", new Vector2(220, 28))) m.Screen = GameScreen.WorldSelect;
                ImGui.End();
            }
            else if (m.Screen == GameScreen.WorldSelect)
            {
                // World list screen: all saved worlds, each with load / rename / delete buttons,
                // plus a "Create New World" button and a Back arrow to the title.
                int count = m.SavedWorlds.Count;
                float rowH = 36f;
                float winW = 420f;
                float winH = Math.Max(140f, 60f + count * rowH + 60f);
                ImGui.SetNextWindowPos(new Vector2((size.X - winW) / 2f, size.Y / 4f + 32f), ImGuiCond.Always);
                ImGui.SetNextWindowSize(new Vector2(winW, winH), ImGuiCond.Always);
                ImGui.Begin("##worldselect", windowFlags);
                ImGui.SetWindowFontScale(1.3f);
                ImGui.Text("Select World");
                ImGui.SetWindowFontScale(1f);
                ImGui.Spacing();

                if (count == 0)
                {
                    ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), "No saved worlds yet.");
                    ImGui.Spacing();
                }
                else
                {
                    for (int i = 0; i < count; i++)
                    {
                        string worldName = m.SavedWorlds[i];
                        ImGui.PushID(i);
                        // World name button â€” clicking loads the world.
                        if (ImGui.Button(worldName, new Vector2(260, 28)))
                        {
                            m.SelectedWorldIndex = i;
                            m.LoadWorldClicked = true;
                        }
                        ImGui.SameLine();
                        if (ImGui.Button("Rename", new Vector2(60, 28)))
                        {
                            m.RenameWorldIndex = i;
                            m.RenameTarget = worldName;
                            m.RenameWorldClicked = false; // not committed yet â€” wait for the rename popup
                        }
                        ImGui.SameLine();
                        if (ImGui.Button("Delete", new Vector2(60, 28)))
                        {
                            m.DeleteWorldIndex = i;
                            m.DeleteWorldClicked = true;
                        }
                        ImGui.PopID();
                    }
                    ImGui.Spacing();
                }

                // Create New World button
                ImGui.Separator();
                ImGui.Spacing();
                if (ImGui.Button("Create New World", new Vector2(200, 32)))
                {
                    m.Screen = GameScreen.CreateWorld;
                    _menuBuffersInitialized = false;
                }
                ImGui.SameLine();
                if (ImGui.Button("Back to Title", new Vector2(140, 32))) m.Screen = GameScreen.Title;
                ImGui.End();

                // Rename popup (renders as a small modal-style window when a world is being renamed).
                if (m.RenameWorldIndex >= 0 && m.RenameWorldIndex < count)
                {
                    ImGui.SetNextWindowPos(new Vector2(size.X / 2f - 140f, size.Y / 2f - 50f), ImGuiCond.Always);
                    ImGui.SetNextWindowSize(new Vector2(280, 120), ImGuiCond.Always);
                    ImGui.Begin("##renameworld", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse);
                    ImGui.Text($"Rename '{m.SavedWorlds[m.RenameWorldIndex]}'");
                    if (!_renameBufferInit)
                    {
                        WriteBuffer(_renameBuffer, m.RenameTarget);
                        _renameBufferInit = true;
                    }
                    ImGui.InputText("New name", _renameBuffer, (uint)_renameBuffer.Length);
                    m.RenameTarget = ReadBuffer(_renameBuffer);
                    ImGui.Spacing();
                    if (ImGui.Button("OK", new Vector2(80, 28)))
                    {
                        m.RenameWorldClicked = true;
                        m.RenameWorldIndex = -1;
                        _renameBufferInit = false;
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("Cancel", new Vector2(80, 28)))
                    {
                        m.RenameWorldIndex = -1;
                        _renameBufferInit = false;
                    }
                    ImGui.End();
                }
                else
                {
                    _renameBufferInit = false;
                }
            }
            else if (m.Screen == GameScreen.Multiplayer)
            {
                ImGui.SetNextWindowPos(new Vector2(size.X / 2f - 170f, size.Y / 2f - 150f), ImGuiCond.Always);
                ImGui.SetNextWindowSize(new Vector2(340, 300), ImGuiCond.Always);
                ImGui.Begin("##multiplayer", windowFlags);
                ImGui.Text("Multiplayer");
                ImGui.Spacing();
                ImGui.TextWrapped("Host a game for friends to join, or connect to a host's IP.");
                ImGui.Spacing();
                if (!_menuBuffersInitialized)
                {
                    WriteBuffer(_hostPortBuffer, m.HostPort);
                    WriteBuffer(_joinAddressBuffer, m.JoinAddress);
                    _menuBuffersInitialized = true;
                }
                ImGui.InputText("Host port", _hostPortBuffer, (uint)_hostPortBuffer.Length);
                m.HostPort = ReadBuffer(_hostPortBuffer);
                if (ImGui.Button("Host Game", new Vector2(300, 34)))
                {
                    m.HostGameClicked = true;
                }
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
                ImGui.InputText("Server address", _joinAddressBuffer, (uint)_joinAddressBuffer.Length);
                m.JoinAddress = ReadBuffer(_joinAddressBuffer);
                if (ImGui.Button("Join Game", new Vector2(300, 34)))
                {
                    m.JoinGameClicked = true;
                }
                if (!string.IsNullOrEmpty(_hud.MultiplayerError))
                {
                    ImGui.Spacing();
                    ImGui.TextColored(new Vector4(1f, 0.45f, 0.45f, 1f), _hud.MultiplayerError);
                    ImGui.Spacing();
                }
                ImGui.Spacing();
                if (ImGui.Button("Back", new Vector2(300, 28))) m.MultiplayerBackClicked = true;
                ImGui.End();
            }
            else if (m.Screen == GameScreen.Paused)
            {
                ImGui.SetNextWindowPos(new Vector2(size.X / 2f - 120f, size.Y / 2f - 150f), ImGuiCond.Always);
                ImGui.SetNextWindowSize(new Vector2(240, 260), ImGuiCond.Always);
                ImGui.Begin("##paused", windowFlags);
                ImGui.SetWindowFontScale(1.6f);
                ImGui.TextColored(new Vector4(1f, 1f, 1f, 1f), "Paused");
                ImGui.SetWindowFontScale(1f);
                ImGui.Spacing();
                ImGui.Spacing();
                if (ImGui.Button("Resume", new Vector2(200, 32))) m.ResumeClicked = true;
                ImGui.Spacing();
                if (ImGui.Button("Open to LAN", new Vector2(200, 32))) m.OpenToLanClicked = true;
                ImGui.Spacing();
                if (ImGui.Button("Settings", new Vector2(200, 32)))
                {
                    m.Screen = GameScreen.Settings;
                    m.SettingsReturnTo = GameScreen.Paused;
                    m.SettingsOpen = true;
                    m.SelectedCullingMode = GetCullingMode();
                    _menuBuffersInitialized = false;
                }
                ImGui.Spacing();
                if (ImGui.Button("Quit to Title", new Vector2(200, 32))) m.QuitToTitleClicked = true;
                if (!string.IsNullOrEmpty(_hud.NetStatus))
                {
                    ImGui.Spacing();
                    ImGui.TextColored(new Vector4(0.6f, 1f, 0.6f, 1f), _hud.NetStatus);
                }
                ImGui.End();
            }
            else if (m.Screen == GameScreen.Dead)
            {
                // Death screen text: the red gradient over the frozen camera was drawn above.
                string deathMessage = _hud.DeathCause switch
                {
                    DeathCause.DebugSelf => "Your heart gave out...",
                    DeathCause.Fall => "You fell from a high place...",
                    DeathCause.Mob => "A zombie got you...",
                    _ => "You died...",
                };

                ImGui.SetNextWindowPos(new Vector2(size.X / 2f - 140f, size.Y / 2f - 90f), ImGuiCond.Always);
                ImGui.SetNextWindowSize(new Vector2(280, 180), ImGuiCond.Always);
                ImGui.Begin("##dead", windowFlags);
                ImGui.SetWindowFontScale(2.0f);
                ImGui.TextColored(new Vector4(0.9f, 0.25f, 0.25f, 1f), "You Died");
                ImGui.SetWindowFontScale(1f);
                ImGui.Spacing();
                ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1f), deathMessage);
                ImGui.Spacing();
                ImGui.Spacing();
                if (ImGui.Button("Respawn", new Vector2(200, 34))) m.RespawnClicked = true;
                ImGui.End();
            }
            else if (m.Screen == GameScreen.Settings)
            {
                // Settings screen: culling mode (player choice), render distance, and mouse
                // sensitivity. Changes set flags on the MenuState; Program applies them next tick.
                ImGui.SetNextWindowPos(new Vector2(size.X / 2f - 190f, size.Y / 2f - 290f), ImGuiCond.Always);
                ImGui.SetNextWindowSize(new Vector2(380, 580), ImGuiCond.Always);
                ImGui.Begin("##settings", windowFlags);
                ImGui.SetWindowFontScale(1.6f);
                ImGui.TextColored(new Vector4(1f, 1f, 1f, 1f), "Settings");
                ImGui.SetWindowFontScale(1f);
                ImGui.Spacing();
                ImGui.Spacing();

                // ---- Frustum culling mode ----
                ImGui.TextColored(new Vector4(0.85f, 0.85f, 0.85f, 1f), "Frustum Culling");
                ImGui.Spacing();
                if (ImGui.RadioButton("Auto (recommended)", m.SelectedCullingMode == CullingMode.Auto))
                {
                    m.SelectedCullingMode = CullingMode.Auto;
                    m.CullingModeChanged = true;
                }
                if (ImGui.RadioButton("CPU (most compatible)", m.SelectedCullingMode == CullingMode.Cpu))
                {
                    m.SelectedCullingMode = CullingMode.Cpu;
                    m.CullingModeChanged = true;
                }
                if (ImGui.RadioButton("GPU (fastest, needs support)", m.SelectedCullingMode == CullingMode.Gpu))
                {
                    m.SelectedCullingMode = CullingMode.Gpu;
                    m.CullingModeChanged = true;
                }
                ImGui.Spacing();
                if (!_gpuCullSupported)
                {
                    ImGui.TextColored(new Vector4(1f, 0.6f, 0.3f, 1f), "GPU culling not supported on this device");
                }
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                // ---- Render distance ----
                ImGui.TextColored(new Vector4(0.85f, 0.85f, 0.85f, 1f), "Render Distance");
                ImGui.Spacing();
                string[] rdNames = { "Far (16)", "Normal (8)", "Short (4)", "Tiny (2)" };
                for (int i = 0; i < rdNames.Length; i++)
                {
                    if (ImGui.RadioButton(rdNames[i], m.SelectedRenderDistance == i))
                    {
                        m.SelectedRenderDistance = i;
                        m.RenderDistanceChanged = true;
                    }
                }
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                // ---- Resolution scale ----
                ImGui.TextColored(new Vector4(0.85f, 0.85f, 0.85f, 1f), "Resolution Scale");
                ImGui.Spacing();
                string[] rsNames = { "100%", "75%", "50%", "25%" };
                float[] rsValues = { 1f, 0.75f, 0.5f, 0.25f };
                for (int i = 0; i < rsNames.Length; i++)
                {
                    if (ImGui.RadioButton(rsNames[i], Math.Abs(m.SelectedResolutionScale - rsValues[i]) < 0.01f))
                    {
                        m.SelectedResolutionScale = rsValues[i];
                        m.ResolutionScaleChanged = true;
                    }
                }
                ImGui.Spacing();
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f),
                    "Lower = faster on weak GPUs");
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                // ---- Low-res filter (only matters when scale < 100%) ----
                ImGui.TextColored(new Vector4(0.85f, 0.85f, 0.85f, 1f), "Low-Res Filter");
                ImGui.Spacing();
                if (ImGui.RadioButton("Smooth", !m.SelectedPixelatedUpscale))
                {
                    m.SelectedPixelatedUpscale = false;
                    m.PixelFilterChanged = true;
                }
                if (ImGui.RadioButton("Blocky", m.SelectedPixelatedUpscale))
                {
                    m.SelectedPixelatedUpscale = true;
                    m.PixelFilterChanged = true;
                }
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                // ---- Mouse sensitivity ----
                ImGui.TextColored(new Vector4(0.85f, 0.85f, 0.85f, 1f), $"Mouse Sensitivity ({m.SelectedMouseSensitivity:0.00})");
                float sens = m.SelectedMouseSensitivity;
                if (ImGui.SliderFloat("##sensitivity", ref sens, 0.05f, 2.0f, "%.2f"))
                {
                    m.SelectedMouseSensitivity = sens;
                    m.MouseSensitivityChanged = true;
                }

                ImGui.Spacing();
                ImGui.Spacing();
                if (ImGui.Button("Back", new Vector2(200, 34))) m.SettingsBackClicked = true;
                ImGui.End();
            }
        }

        // Copies a string into a null-terminated byte buffer for ImGui.InputText.
        private static void WriteBuffer(byte[] buffer, string value)
        {
            Array.Clear(buffer, 0, buffer.Length);
            if (string.IsNullOrEmpty(value)) return;
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            int n = Math.Min(bytes.Length, buffer.Length - 1);
            Array.Copy(bytes, buffer, n);
        }

        // Reads a null-terminated byte buffer back into a string. Never throws - a malformed or
        // truncated UTF-8 buffer (ImGui can leave partial sequences at the end) falls back to a
        // lossy decode instead of crashing the game.
        private static string ReadBuffer(byte[] buffer)
        {
            try
            {
                int end = Array.IndexOf(buffer, (byte)0);
                if (end < 0) end = buffer.Length;
                return System.Text.Encoding.UTF8.GetString(buffer, 0, end);
            }
            catch
            {
                try
                {
                    // Lossy ASCII fallback: replace any byte >= 128 with '?'.
                    int end = Array.IndexOf(buffer, (byte)0);
                    if (end < 0) end = buffer.Length;
                    var sb = new System.Text.StringBuilder(end);
                    for (int i = 0; i < end; i++)
                    {
                        byte b = buffer[i];
                        sb.Append(b < 128 && b > 0 ? (char)b : '?');
                    }
                    return sb.ToString();
                }
                catch
                {
                    return "";
                }
            }
        }

        // Loading screen: a centered phase label with a per-phase progress bar and a total bar.
        // The world renders normally behind it (it fills in as chunks generate), so the player
        // sees the terrain assembling before they're dropped in.
        private void DrawLoadingScreen(MenuState m, Vector2 size)
        {
            // Stone-tiled background (matches the underground theme of world loading); the title
            // screen keeps its dirt background via the default parameter.
            DrawDirtBackground(size, "stone");

            ImGuiWindowFlags windowFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar
                | ImGuiWindowFlags.NoScrollWithMouse;

            const float winW = 360f;
            const float winH = 120f;
            ImGui.SetNextWindowPos(new Vector2((size.X - winW) / 2f, (size.Y - winH) / 2f), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new Vector2(winW, winH), ImGuiCond.Always);
            ImGui.Begin("##loading", windowFlags);

            ImGui.SetWindowFontScale(1.6f);
            ImGui.Text("Building World...");
            ImGui.SetWindowFontScale(1f);
            ImGui.Dummy(new Vector2(0, 8));

            ImGui.Text(m.LoadingPhase);
            ImGui.ProgressBar(m.LoadingPhaseProgress, new Vector2(winW - 40f, 16f));

            ImGui.Dummy(new Vector2(0, 8));
            ImGui.Text("Overall progress");
            ImGui.ProgressBar(m.LoadingTotalProgress, new Vector2(winW - 40f, 16f));

            ImGui.End();
        }

        private void BuildHudUi()
        {
            var io = ImGui.GetIO();
            var displaySize = io.DisplaySize;
            var drawList = ImGui.GetForegroundDrawList();

            // Menus (title / create world / paused) take over the whole screen; the gameplay HUD
            // below only draws while actually playing.
            var menu = _hud.Menu;
            bool playing = menu == null || menu.Screen == GameScreen.Playing;
            if (!playing)
            {
                DrawMenu();
                return;
            }

            // Crosshair is drawn in Render() as an invert-blend pass BEFORE the UI (see
            // DrawCrosshair), so it never paints over menu windows and always inverts the world.

            // The targeted block face highlight is drawn as a depth-tested 3D quad in Render(),
            // not here, so that blocks in front of it occlude it correctly.

            // Hotbar - uses the Cubuild.html GUI frame texture (169x16, 10 slots of 16px + 1px
            // gap) at 3x scale (507x48 on screen, 48px slots / 3px gaps). The selected slot gets
            // the 18x18 yellow highlight texture stretched over it, and each slot draws its
            // isometric block icon + number on top - same functionality as before, just with the
            // real frame art.
            const int hotbarSlots = 10;
            const int hotbarScale = 3;
            const int slotSize = 16 * hotbarScale;     // 48
            const int slotGap = 1 * hotbarScale;        // 3
            int totalWidth = hotbarSlots * slotSize + (hotbarSlots - 1) * slotGap; // 507
            const int hotbarHeight = 16 * hotbarScale; // 48
            float startX = (displaySize.X - totalWidth) / 2f;
            float hotbarY = displaySize.Y - hotbarHeight - 16f;

            bool survival = _hud.Mode == GameMode.Survival;
            uint textColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f));

            if (_hotbarImGuiId != IntPtr.Zero)
            {
                // Draw the whole slot frame as one stretched image.
                drawList.AddImage(
                    _hotbarImGuiId,
                    new Vector2(startX, hotbarY),
                    new Vector2(startX + totalWidth, hotbarY + hotbarHeight),
                    Vector2.Zero,
                    Vector2.One);
            }
            else
            {
                // Fallback if the embedded texture is missing: draw plain slot rects.
                uint slotBg = ImGui.ColorConvertFloat4ToU32(new Vector4(36 / 255f, 45 / 255f, 52 / 255f, 1f));
                uint slotBorder = ImGui.ColorConvertFloat4ToU32(new Vector4(100 / 255f, 150 / 255f, 200 / 255f, 1f));
                for (int i = 0; i < hotbarSlots; i++)
                {
                    float x = startX + i * (slotSize + slotGap);
                    drawList.AddRectFilled(new Vector2(x, hotbarY), new Vector2(x + slotSize, hotbarY + slotSize), slotBg);
                    drawList.AddRect(new Vector2(x, hotbarY), new Vector2(x + slotSize, hotbarY + slotSize), slotBorder);
                }
            }

            for (int i = 0; i < hotbarSlots; i++)
            {
                float x = startX + i * (slotSize + slotGap);
                var slotTopLeft = new Vector2(x, hotbarY);

                // Dim the transparent slot interior so block icons read clearly against the world
                // behind the hotbar (drawn first, under the icon and selection ring).
                uint slotDim = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.35f));
                drawList.AddRectFilled(slotTopLeft + new Vector2(3, 3), slotTopLeft + new Vector2(slotSize - 3, slotSize - 3), slotDim);

                if (i == _hud.SelectedSlot && _hotbarSelectImGuiId != IntPtr.Zero)
                {
                    // The 18x18 highlight box stretches over the whole slot (48px here). The selected
                    // slot gets a slightly larger box so the active choice pops out from the others.
                    const float selScale = 1.25f;
                    float selSize = slotSize * selScale;
                    var selCenter = slotTopLeft + new Vector2(slotSize * 0.5f, slotSize * 0.5f);
                    drawList.AddImage(
                        _hotbarSelectImGuiId,
                        selCenter - new Vector2(selSize * 0.5f, selSize * 0.5f),
                        selCenter + new Vector2(selSize * 0.5f, selSize * 0.5f),
                        Vector2.Zero,
                        Vector2.One);
                }

                // The frame texture's visible opening per slot is 12x12 at 1x (36x36 at 3x), so the
                // block icon is centered inside that opening. A 1px inset lets the block nearly fill
                // the slot while staying centered; the extra +1 on Y nudges it down a touch so the
                // cube sits optically centered in the frame.
                const int iconInset = 1;
                const int iconDrop = 2;
                bool isSelected = i == _hud.SelectedSlot;
                if (_hud.Hotbar != null && i < _hud.Hotbar.Count)
                {
                    int bid = _hud.Hotbar[i];
                    var hotUv = IconUv(bid, out IntPtr hotTex);
                    if (bid > 0 && hotTex != IntPtr.Zero)
                    {
                        // The selected item grows along with its highlight ring so the active slot
                        // reads as one bigger, emphasized cube.
                        float iconSize2 = isSelected ? slotSize * 1.16f : slotSize - iconInset * 2f;
                        float iconX = isSelected ? slotTopLeft.X + (slotSize - iconSize2) * 0.5f : slotTopLeft.X + iconInset;
                        float iconY = isSelected ? slotTopLeft.Y + (slotSize - iconSize2) * 0.5f + iconDrop : slotTopLeft.Y + iconInset + iconDrop;
                        drawList.AddImage(
                            hotTex,
                            new Vector2(iconX, iconY),
                            new Vector2(iconX + iconSize2, iconY + iconSize2),
                            new Vector2(hotUv.X, hotUv.Y),
                            new Vector2(hotUv.X + hotUv.Z, hotUv.Y + hotUv.W));
                    }
                    else
                    {
                        uint iconColor = bid > 0 ? ItemRegistry.MapColorOf(bid) : 0;
                        float iconSize2 = isSelected ? slotSize * 1.16f : slotSize - iconInset * 2f;
                        float iconX = isSelected ? slotTopLeft.X + (slotSize - iconSize2) * 0.5f : slotTopLeft.X + iconInset;
                        float iconY = isSelected ? slotTopLeft.Y + (slotSize - iconSize2) * 0.5f + iconDrop : slotTopLeft.Y + iconInset + iconDrop;
                        drawList.AddRectFilled(new Vector2(iconX, iconY), new Vector2(iconX + iconSize2, iconY + iconSize2), iconColor);
                    }
                }

                // Survival: show the per-slot hotbar count in the corner (stackable items only -
                // tools carry no count number, like Minecraft).
                if (survival && _hud.HotbarCounts != null)
                {
                    int bid = (_hud.Hotbar != null && i < _hud.Hotbar.Count) ? _hud.Hotbar[i] : 0;
                    int count = (i < _hud.HotbarCounts.Count) ? _hud.HotbarCounts[i] : 0;
                    if (bid > 0 && count > 0 && ItemRegistry.StackSizeOf(bid) > 1)
                    {
                        string countText = count.ToString();
                        var textSize = ImGui.CalcTextSize(countText);
                        drawList.AddText(
                            new Vector2(slotTopLeft.X + slotSize - textSize.X - 3f, slotTopLeft.Y + slotSize - textSize.Y - 2f),
                            textColor, countText);
                    }
                }
            }

            // Mode label above the hotbar so you always know which world you're in.
            string modeLabel = survival ? "SURVIVAL" : "CREATIVE";
            uint modeColor = survival
                ? ImGui.ColorConvertFloat4ToU32(new Vector4(0.9f, 0.35f, 0.35f, 1f))
                : ImGui.ColorConvertFloat4ToU32(new Vector4(0.35f, 0.8f, 0.45f, 1f));
            drawList.AddText(new Vector2(startX, hotbarY - 20f), modeColor, modeLabel);

            // Healthbar: one heart centered directly above the hotbar. Health 10..1 maps to the ten
            // filled hearts in reading order (top row left->right skipping the blank cell, then
            // middle row left->right); health 0 (death) is the dark r1c5 heart. Each point of
            // damage removes one slice - the sprite gets progressively darker/emptier. Survival
            // only: invulnerable creative players don't need a healthbar.
            if (_healthbarImGuiId != IntPtr.Zero && survival)
            {
                const float heartScale = 3f;
                float heartSize = HealthbarSpriteSize * heartScale;
                // Snap to whole pixels so the linear sampler doesn't bleed between texels.
                float heartX = (float)Math.Floor((displaySize.X - heartSize) / 2f);
                float heartY = (float)Math.Floor(hotbarY - heartSize - 10f);

                // Heartbeat: when health is halfway depleted or worse (<=5) the heart bobs in the
                // classic "lub-dub" rhythm: bump, pause, bump bump, pause, bump, pause, bump bump.
                int hp = Math.Clamp(_hud.PlayerHealth, 0, 10);
                if (hp <= 5)
                {
                    double t = ImGui.GetTime();
                    double tInCycle = t % HealthbeatCycle;
                    double bob = 0;
                    for (int i = 0; i < HealthbeatTimes.Length; i++)
                    {
                        double u = tInCycle - HealthbeatTimes[i];
                        if (u >= 0 && u < HealthbeatBumpDur)
                        {
                            bob += HealthbeatAmps[i] * Math.Sin(Math.PI * u / HealthbeatBumpDur);
                        }
                    }
                    heartY += (float)bob;
                }

                // Sprite order (reading order, skipping the blank r0c5 cell):
                //   health 10..6 -> row 0, cols 0..4
                //   health 5..0  -> row 1, cols 0..5
                int spriteRow, spriteCol;
                if (hp >= 6)
                {
                    spriteRow = 0;
                    spriteCol = 10 - hp; // hp10->c0, hp9->c1, ... hp6->c4
                }
                else
                {
                    spriteRow = 1;
                    spriteCol = 5 - hp;  // hp5->c0, hp4->c1, ... hp0->c5 (death)
                }

                float u0 = (spriteCol * HealthbarGridPitch + 1f) / 90f;
                float v0 = (spriteRow * HealthbarGridPitch + 1f) / 45f;
                var uv0 = new Vector2(u0, v0);
                var uv1 = new Vector2(u0 + HealthbarSpriteSize / 90f, v0 + HealthbarSpriteSize / 45f);
                var heartPos0 = new Vector2(heartX, heartY);
                var heartPos1 = new Vector2(heartX + heartSize, heartY + heartSize);
                drawList.AddImage(
                    _healthbarImGuiId,
                    heartPos0,
                    heartPos1,
                    uv0,
                    uv1);

                // On any health change the near-black outline flashes white for a brief beat.
                if (_healthFlashTimer > 0f && _healthbarFlashImGuiId != IntPtr.Zero)
                {
                    drawList.AddImage(
                        _healthbarFlashImGuiId,
                        heartPos0,
                        heartPos1,
                        uv0,
                        uv1);
                }
            }

            // E-menu inventory: a grid of every block. Clicking one queues it to Program, which
            // drops it into the selected hotbar slot and closes the menu.
            if (_hud.InventoryOpen)
            {
                DrawInventoryWindow(displaySize);
            }

            // Workbench crafting menu (right-click a workbench block).
            if (_hud.CraftingOpen)
            {
                DrawCraftingWindow(displaySize);
            }

            // Biome teleport menu (B key).
            if (_hud.BiomeMenuOpen)
            {
                DrawBiomeMenu(displaySize);
            }

            // Selected block label
            string label = string.IsNullOrEmpty(_hud.SelectedBlockText) ? string.Empty : _hud.SelectedBlockText;
            if (label.Length > 0)
            {
                var labelPos = new Vector2(12, 12);
                var textSize = ImGui.CalcTextSize(label);
                uint bg = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.47f));
                drawList.AddRectFilled(labelPos - new Vector2(6, 3), labelPos + textSize + new Vector2(6, 3), bg);
                drawList.AddText(labelPos, textColor, label);
            }

            // Hand Editor (F8): tune the first-person hand/held-block pose live, then copy the
            // values line back to the dev. Frees the mouse (Program disables mouse look while
            // open) so the sliders are draggable.
            if (_hud.HandEditorOpen)
            {
                ImGui.SetNextWindowPos(new Vector2(8, 320), ImGuiCond.FirstUseEver);
                ImGui.SetNextWindowSize(new Vector2(420, 0), ImGuiCond.FirstUseEver);
                ImGui.Begin("Hand Editor", ImGuiWindowFlags.NoCollapse);
                ImGui.Text("Hand (arm pose)");
                ImGui.SliderFloat("handScale", ref _handScale, 0.4f, 1.4f);
                ImGui.SliderFloat("sx (right)", ref _handSx, 0.2f, 0.9f);
                ImGui.SliderFloat("sy (down)", ref _handSy, -1.2f, -0.3f);
                ImGui.SliderFloat("sz (forward)", ref _handSz, -1.0f, -0.2f);
                ImGui.SliderFloat("basePitch", ref _handBasePitch, -1.4f, 0.2f);
                ImGui.SliderFloat("baseYaw", ref _handBaseYaw, -0.9f, 0.9f);
                ImGui.Separator();
                ImGui.Text("Held block (own anchor, independent of the arm)");
                ImGui.SliderFloat("blockX (right)", ref _heldBlockX, 0.2f, 0.9f);
                ImGui.SliderFloat("blockY (down)", ref _heldBlockY, -0.8f, 0.1f);
                ImGui.SliderFloat("blockZ (forward)", ref _heldBlockZ, -1.2f, -0.3f);
                ImGui.SliderFloat("blockSize", ref _heldBlockSize, 0.2f, 0.6f);
                ImGui.Separator();
                string copyLine = string.Format(
                    "handScale={0:0.###}f, sx={1:0.###}f, sy={2:0.###}f, sz={3:0.###}f, basePitch={4:0.###}f, baseYaw={5:0.###}f, heldBlockX={6:0.###}f, heldBlockY={7:0.###}f, heldBlockZ={8:0.###}f, heldBlockSize={9:0.###}f",
                    _handScale, _handSx, _handSy, _handSz, _handBasePitch, _handBaseYaw,
                    _heldBlockX, _heldBlockY, _heldBlockZ, _heldBlockSize);
                ImGui.Text("Copy this:");
                ImGui.TextWrapped(copyLine);
                ImGui.End();
            }

            // Debug overlay (F3)
            if (_hud.ShowDebug)
            {
                uint debugColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 0f, 1f));
                float dy = 8f;
                void Line(string text)
                {
                    drawList.AddText(new Vector2(8, dy), debugColor, text);
                    dy += 16f;
                }

                Line($"FPS: {_hud.Fps:0.0}");
                if (_cameraPosition.HasValue)
                    Line($"FogCam: {_cameraPosition.Value.X:0.0}, {_cameraPosition.Value.Y:0.0}, {_cameraPosition.Value.Z:0.0}  range: {_fogParams[4]:0.0}-{_fogParams[5]:0.0}");
                Line($"Particles: {_particleCount}");
                Line($"Seed: {_hud.WorldSeed}");
                Line($"Fly: {(_hud.FlyMode ? "ON" : "OFF")}");
                Line($"Fullbright: {(_hud.Fullbright ? "ON" : "OFF")}  [F6]");
                Line($"Cull: {(_gpuCullEnabled ? "GPU" : "CPU")}  [F7]");                if (!string.IsNullOrEmpty(_hud.NetStatus)) Line($"Net: {_hud.NetStatus}");
                if (!string.IsNullOrEmpty(_hud.BiomeText)) Line($"Biome: {_hud.BiomeText}");
                Line($"XYZ: {_hud.PlayerX:0.000} / {_hud.PlayerY:0.000} / {_hud.PlayerZ:0.000}");
                Line($"Block: {(int)Math.Floor(_hud.PlayerX)} / {(int)Math.Floor(_hud.PlayerY)} / {(int)Math.Floor(_hud.PlayerZ)}");
                Line($"Chunk: {_hud.PlayerChunkX} / {_hud.PlayerChunkZ}");
                Line($"Upd: {_hud.UpdateMs:0.0} ms");
                Line($"Mesh: {_hud.MeshMs:0.0} ms");
                Line($"Entity: {_hud.EntityMs:0.0} ms  ({_hud.EntityCount} mobs)");
                Line($"Upload: {_hud.UploadMs:0.0} ms");
                Line($"Render: {_hud.RenderMs:0.0} ms");
                Line($"Facing: {_hud.FacingText}");
                if (!string.IsNullOrEmpty(_hud.RenderDistanceText))
                {
                    Line(_hud.RenderDistanceText);
                }

                // Nametags: project each mob's position above its head into screen space and draw
                // its type label, so invisible/broken mobs are still verifiable in the F3 overlay.
                if (_viewProjection.HasValue && _cameraPosition.HasValue)
                {
                    uint tagColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f));
                    for (int i = 0; i < _allMobRenderData.Count; i++)
                    {
                        var md = _allMobRenderData[i];
                        var screen = WorldToScreen(new System.Numerics.Vector3((float)md.Position.X, (float)md.Position.Y + 1.8f, (float)md.Position.Z));
                        if (screen.HasValue)
                        {
                            drawList.AddText(screen.Value - new Vector2(0, 14), tagColor, md.MobType);
                        }
                    }
                }
            }
        }

        // Projects a world-space point to screen pixel coordinates using the current
        // view-projection matrix (the renderer owns both the camera and the HUD pass). Returns
        // null when the point is behind the camera.
        private System.Numerics.Vector2? WorldToScreen(System.Numerics.Vector3 world)
        {
            if (!_viewProjection.HasValue) return null;
            var vp = _viewProjection.Value;
            var clip = System.Numerics.Vector4.Transform(new System.Numerics.Vector4(world, 1f), vp);
            if (clip.W <= 0f) return null;
            var ndc = new System.Numerics.Vector2(clip.X / clip.W, clip.Y / clip.W);
            var io = ImGui.GetIO();
            float x = (ndc.X * 0.5f + 0.5f) * io.DisplaySize.X;
            float y = (1f - ndc.Y * 0.5f - 0.5f) * io.DisplaySize.Y;
            return new System.Numerics.Vector2(x, y);
        }

    }
}