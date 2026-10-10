# Inventory & UI "Smartness" Audit

Audited against `Cubuild` (CubeApp) at commit `19e3903`. This document records all findings so
work can resume later. Reference this file when continuing — it should stay up to date with
anything implemented.

**Status:** #1 (hotbar counts), #2 (drop loss on full inventory) and #7 (orphan cursor stack)
are fixed. #3-#6 and #8-#12 remain open. #4 (durability) is the largest of the remaining.

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

### 5. "Selected: air" on the HUD when the slot is empty (BUG/UX)
- **`Program\Program.RenderData.cs`** ~136 — `$"Selected: {ItemRegistry.GetName(World.SelectedBlock)}"`.
  When the selected slot is empty, `SelectedBlock` is air (id 0), so it prints "Selected: air".
- **Fix:** show nothing / "(none)" when the item is air.

### 6. Number keys (1-0) work while a menu is open (BUG)
- **`Input\InputProcessor.cs`** ~295-324 — keys 1-0 map to `selectedSlot`.
- **`Program\Program.Input.cs`** ~127 — calls `World.SetSelectedSlot(...)` UNGUARDED by
  `inventoryOpen`/`craftingOpen`, unlike the scroll wheel (guarded at ~147-149).
- **Effect:** pressing a number while a menu is open silently changes the selected hotbar slot.
- **Fix:** gate number keys behind "no menu open", like the scroll wheel.

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

### 10. No shift-click in the crafting grid / no "move all" / no double-click spread (MISSING)
- **`Renderer\VeldridRenderer.UI.cs`** `DrawCraftingCellAt` (~558-591) — only enqueues kinds `4`/`5`
  on left/right; no shift-click. `QuickMoveSlot` exists for inventory (`Inventory.cs` ~367-424) and
  inventory cells do handle shift (`UI.cs` ~249,272,292,312).
- Right-clicking the result well (`UI.cs` ~563-564) is enqueued, but `MainLoop.cs` ~201-205 only
  acts on `click.Button == 0` for kind 5, so a craft-result right-click is silently dropped.

### 11. Empty-slot tooltip shows "Air" in the fallback path (BUG, minor)
- **`Renderer\VeldridRenderer.UI.cs`** ~255 and ~278 — `DrawInventorySlotCell` calls
  `ItemRegistry.Get(itemId).DisplayName` WITHOUT an `itemId > 0` guard (the textured
  `DrawInventorySlotCellAt` at ~320 DOES guard).
- **Effect:** hovering an empty bag slot in the no-texture path shows tooltip of item 0 ("Air") and
  renders a trailing "0" count.

### 12. Count-display threshold inconsistent (MINOR)
- **`UI.cs`** ~347 (E-menu) shows count only when `count > 1 && StackSizeOf > 1`.
- **`UI.cs`** ~1301 (hotbar HUD) shows count when `count > 0 && StackSizeOf > 1`.
- A stack of 1 shows "1" in the hotbar but is unlabeled in the E-menu.

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
