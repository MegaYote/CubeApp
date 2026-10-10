# Inventory & UI "Smartness" Audit

Audited against `Cubuild` (CubeApp) at commit `19e3903`. This document records all findings so
work can resume later. Reference this file when continuing — it should stay up to date with
anything implemented.

**Status:** #1, #2, #5, #6, #7, #10a, #11, #12 are fixed. #4 (durability), #8 (hotbar tooltips),
#9 (drop merging) and #10b (crafting conveniences) remain open; #4 is by far the largest.
#3 is moot by design — the creative palette hands picks to the cursor instead of closing the menu.

---

## 🔴 Bugs (caused data loss / broken behavior)

### 1. Hotbar counts are never saved or restored (DATA LOSS)
- **`World\WorldSave.cs`** — `Hotbar = new int[10]` (IDs only, line ~53). The save writes the 40
  bag slots WITH counts but the hotbar write writes only the 10 IDs (no `HotbarCounts` field).
- **`Program\Program.SaveLoad.cs`** (restore, ~178-183) — restores `World.Hotbar[i]` and
  `SetSelectedSlot`, but never restores counts.
- **`World\GameWorld.cs`** ~105 — `HotbarCounts` starts as a fresh `int[10]` all zeros.
- **Effect:** after reloading a Survival world, every hotbar slot holds its item with count `0`.
  - `GameWorld.BlockEdit.cs:309` `if (InventoryCount(spendId) < 1) return false;` → for an item
    living only in the hotbar, `InventoryCount` returns 0 → **cannot place selected blocks.**
  - Hotbar shows the icon but no count (`UI.cs:1301` shows count only when `count > 0`).
- **Fix:** add `HotbarCounts` to `WorldSave`, write it, and restore it in `LoadWorld`.

### 2. ~~Items silently destroyed when the inventory is full~~ ✅ FIXED
- **Root cause (three layers):**
  - **`World\GameWorld.Inventory.cs`** — `TryAddToInventory` returned only a `bool`, so a caller
    could never learn *how many* items were added. A drop of 64 into an inventory with one free
    slot returned `true` (something fit) while 63 items were unaccounted for.
  - **`World\GameWorld.Inventory.cs`** — `CollectItem` ignored that return value entirely and
    always returned `true`.
  - **`World\GameWorld.Simulation.cs`** — on magnet arrival it called `CollectItem(d.ItemId, d.Count)`
    then **unconditionally `_droppedItems.RemoveAt(i)`**.
- **Fix:** `TryAddToInventory` gained an `out int added` overload (the old `bool` signature is
  kept and delegates to it, so the one other caller is unaffected). `CollectItem` now returns
  `int` — the number actually pocketed. The magnet only deletes the drop when
  `added >= d.Count`, otherwise it shrinks the drop (`d.Count -= added`) and leaves it on the
  ground to retry, so a full inventory costs you nothing.
- **Note:** with a completely full inventory the drop now hovers at your feet and re-tries on the
  normal pickup cadence instead of being eaten. That is the intended behaviour.

### 3. Creative picker never closes the menu after selecting (BUG)
- **`Renderer\VeldridRenderer.UI.cs`** (~1391-1393) — comment says it "closes the menu" but it
  never does. Picking a block in the creative E-menu leaves the menu open.
- **`Program\Program.MainLoop.cs`** (~186-194) — the click handling only sets the block; no close.

### 4. Tool durability is an empty stub (INCOMPLETE)
- **`World\GameWorld.Inventory.cs`** ~504-510 — `DamageSelectedTool(int)` is an explicit
  `// TODO: durability mechanics` stub that does nothing.
- **`Items\ItemRegistry.cs`** ~251 — `DurabilityOf(itemId)` is exposed but never read anywhere.
- **`Assets\Data\items.json`** — defines real durability (flint tools 60, spear 80,
  wood_sealant_bucket 8).
- **Ad-hoc alternative:** `World\GameWorld.BlockEdit.cs` ~277-290 — `_sealantUses` keyed by hotbar
  **slot index** (not by item stack), and **not saved**.
- **Effect:** tools never wear/break; the "durability" that exists resets when the bucket moves slots.
- **Fix:** implement durability per item stack, show a durability indicator on the icon/tooltip,
  break at 0 (empty the slot), and remove the per-slot `_sealantUses` hack.

### 5. ~~"Selected: air" on the HUD when the slot is empty~~ ✅ FIXED
- **Was:** `Program.RenderData.cs` built `$"Selected: {ItemRegistry.GetName(World.SelectedBlock)}"`.
  An empty hotbar slot leaves `SelectedBlock` at 0, which is air, so the HUD read "Selected: air".
- **Fix:** guards on `SelectedBlock > 0` and falls back to `"Selected: -"`, matching the existing
  fallback text on the line above.

### 6. ~~Number keys (1-0) work while a menu is open~~ ✅ FIXED
- **Was:** `Program.Input.cs` called `World.SetSelectedSlot(...)` unguarded, unlike the scroll
  wheel which was already gated — so pressing a number with the E-menu open silently swapped the
  selection behind the panel.
- **Fix:** the number keys now carry the same guard as the scroll wheel
  (`screen == Playing && mouseLook && !inventoryOpen && !craftingOpen && !biomeMenuOpen &&
  !handEditorOpen`). The scroll guard was also missing `craftingOpen`; added.

### 7. ~~Cursor stack floats invisibly after closing the inventory~~ ✅ FIXED
- **Was:** `Program.Input.cs` toggled `inventoryOpen` without ever returning `World.HeldStack`
  to the player, so a stack picked up and then hidden with E was neither in the inventory nor on
  the ground — it reappeared when the menu reopened.
- **Fix:** new `GameWorld.ReturnHeldStackToInventory()` puts the cursor stack back into the
  inventory, and spawns any overflow at the player's feet rather than destroying it (skipped
  entirely in creative, which is infinite). Wired into **all three** container close paths in
  `Program.Input.cs`: ESC (`~27`), the E toggle (`~97`), and opening the E-menu over the
  crafting menu.

---

## 🟡 Missing "smart" conveniences

### 8. No tooltips on the gameplay hotbar (MISSING)
- **`Renderer\VeldridRenderer.UI.cs`** ~1235-1310 — hotbar icons drawn with `drawList.AddImage`
  and no `ImGui.Button`/`IsItemHovered`, so no hover tooltip. Inventory/crafting slots DO get
  tooltips (at ~321, ~572).
- **Fix:** hovering a hotbar slot should show the item name (+durability/status when implemented).

### 9. Dropped items never merge on the ground (MISSING)
- **`World\GameWorld.Inventory.cs`** `SpawnItemDrop` (~71-113) — always creates a fresh drop;
  `DropFromCursor` (~335-363) loops once per item.
- **Effect:** 64 thrown items sit as 64 separate tumbling cubes (perf + visual noise).
- **Fix:** coalesce same-type drops within a short radius into one stack (count = sum).

### 10. Crafting grid conveniences — PARTIALLY FIXED
- **~~10a. Craft-result right-click was silently dropped~~ ✅ FIXED.** `UI.cs` enqueued the
  right-click on the result well, but `MainLoop.cs` only acted on `click.Button == 0` for that
  action kind, so it reached the handler and got discarded. Now both buttons craft.
- **10b. Still open:** no shift-click in the crafting grid, no "move all", no double-click spread.
  Right-clicking a grid cell works (half/drop-one) and the inventory already has shift-click via
  `QuickMoveSlot`, so this is a consistency gap rather than a missing concept.

### 11. ~~Empty-slot tooltip shows "Air"~~ ✅ FIXED
- **Was:** `VeldridRenderer.UI.cs` ~255 and ~278 called `ItemRegistry.Get(itemId).DisplayName`
  with no `itemId > 0` guard, so hovering an empty bag slot in the untextured path toasted "Air"
  and drew a trailing `0`. The textured path (~L381) already guarded.
- **Fix:** both untextured branches now guard on `itemId > 0`.

### 12. ~~Count-display threshold inconsistent~~ ✅ FIXED
- **Was:** `UI.cs` ~347 (E-menu) showed the count only when `count > 1 && StackSizeOf > 1`, but
  `UI.cs` ~1301 (hotbar HUD) showed it at `count > 0`, so a stack of 1 read "1" on the hotbar but
  was blank in the menu.
- **Fix:** the untextured menus now route through one helper, `DrawSlotCountOverlay`, which uses
  `count > 1 && StackSizeOf > 1` — the same rule the textured E-menu already used.

### 13. Stack counts shoved the slots apart (FIXED along with the creative rework)
- The fallback slot cells drew the count as a **layout sibling** (`SameLine` + `Text`), which
  widened the row and pushed neighbouring slots apart instead of staying inside the cell.
- Now drawn on the foreground draw list at the cell's bottom-right, with a drop shadow so it
  stays legible over bright icons — matching the textured E-menu.

---

## ✅ Already working (do NOT regress)

- Auto-stacking on pickup: `Inventory.cs` ~118-163 (stacks hotbar → bag → empty hotbar → empty bag,
  capped by `Math.Min(MaxStackSize, StackSizeOf(itemId))`).
- Shift-click / quick move in inventory: `Inventory.cs` ~367-424.
- Stack splitting (right-click half / drop one): `CursorClickSlot` ~292-323.
- Cursor stacking (held stack at mouse): `UI.cs` ~201-220, ~521-537.
- Mouse-look gating on menu open/close: `Input.cs` ~25-31, 96-98, 181-184; the "click to re-capture"
  guard at ~53-58 prevents the E-menu/crafting/biome menus from re-capturing the mouse.
- Drop item (Q) correctly prefers hovered slot → cursor → selected hotbar (`Input.cs` ~130-144).
- Crafting drag/drop semantics mirror inventory correctly; `TryCraft` (~104-132) refuses when the
  cursor can't hold the result.

## Notes / design observations
- Recipes match on **any rotation** (`RecipeRegistry.TryMatch` ~102-130), looser than Minecraft's
  shape matching (`recipes.json:37` says "any arrangement" intentionally).
- Creative-mode workbench crafting is effectively dead (HeldStack never populated in creative).
- Default hotbar from `blocks.json` is never used — `WorldLifecycle.cs:27` overwrites every slot
  with air. Possibly intentional; the config is misleading.
- Auto-stacking picks the lowest-index hotbar slot, not the currently selected one (minor).

## File index
- `World\GameWorld.Inventory.cs` — CollectItem, TryAddToInventory, DamageSelectedTool stub,
  DropFromCursor, SpawnItemDrop, QuickMoveSlot, CursorClickSlot.
- `World\WorldSave.cs` — hotbar save bug.
- `Program\Program.SaveLoad.cs` — LoadWorld restore.
- `World\GameWorld.Simulation.cs` — item loss on full inventory.
- `Program\Program.MainLoop.cs` — creative menu close; crafting click routing.
- `Renderer\VeldridRenderer.UI.cs` — creative close comment, empty tooltip, no hotbar tooltip,
  count threshold, crafting shift support.
- `Program\Program.RenderData.cs` — "Selected: air".
- `Program\Program.Input.cs` — number keys not gated; held stack on close.
- `Items\ItemRegistry.cs` — durability accessors; StackSizeOf.
- `World\GameWorld.BlockEdit.cs` — sealant durability hack; placement count check.
- `World\GameWorld.Crafting.cs` — grid + TryCraft.
