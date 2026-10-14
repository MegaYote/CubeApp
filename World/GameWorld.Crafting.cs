using System;

namespace Cubuild
{
    public sealed partial class GameWorld : IDisposable
    {
        // ---- workbench crafting (2x2 in the pack, 3x3 at a table + output) -------------------
        // The grid holds item stacks, shared with the inventory cursor (HeldStack) so items
        // can be dragged between the E-menu and the crafting grid. The result is recomputed
        // after every grid change from recipes.json via RecipeRegistry.

        /// <summary>
        /// The crafting grid, laid out as a fixed 3x3 row-major:
        /// <code>0 1 2 / 3 4 5 / 6 7 8</code>. Only the top-left <see cref="CraftingSize"/>x
        /// <see cref="CraftingSize"/> box is usable - the outer ring stays empty in the 2x2 case.
        /// </summary>
        public (int ItemId, int Count)[] CraftingGrid { get; } = new (int, int)[9];

        /// <summary>Active box size: 2 in the pack's E-menu, 3 at a crafting table.</summary>
        public int CraftingSize { get; private set; } = 2;

        /// <summary>Live result of the current grid (null = no recipe matches).</summary>
        public (int ItemId, int Count)? CraftingResult { get; private set; }

        /// <summary>True when the grid is a full 3x3 (a table is open).</summary>
        public bool CraftingIs3x3 => CraftingSize == 3;

        /// <summary>
        /// Switches the active box between 2x2 and 3x3. Shrinking returns anything sitting in
        /// the newly-unreachable outer ring to the player's bag, so a stack can never be
        /// stranded in a cell that no longer has a click target.
        /// </summary>
        public void SetCraftingSize(int size)
        {
            if (size != 2 && size != 3) size = 2;
            if (size == CraftingSize) return;
            if (size < CraftingSize)
            {
                for (int i = 0; i < 9; i++)
                {
                    if (SlotInBox(i, size)) continue;
                    var g = CraftingGrid[i];
                    if (g.ItemId > 0)
                    {
                        GiveOrDrop(g.ItemId, g.Count);
                        CraftingGrid[i] = (0, 0);
                    }
                }
            }
            CraftingSize = size;
            UpdateCraftingResult();
        }

        /// <summary>True when a flat slot index lies inside the active box (top-left aligned).</summary>
        private static bool SlotInBox(int index, int size)
        {
            int x = index % 3, y = index / 3;
            return x < size && y < size;
        }

        /// <summary>
        /// Maps a VISIBLE cell (0..size*size-1, reading the panel left-to-right, top-to-bottom)
        /// onto its flat index in the 3-wide <see cref="CraftingGrid"/>. On a 3x3 table the two
        /// coincide, but on the 2x2 pack grid they do not: the visible bottom-left cell is
        /// display index 2, which in a 3-wide layout is the top-RIGHT of row 0. Without this
        /// translation that cell would resolve outside the 2x2 box and refuse every click.
        /// </summary>
        public static int DisplaySlotToGrid(int displayIndex, int size)
        {
            if (size >= 3) return displayIndex;
            if (size <= 1) return displayIndex == 0 ? 0 : -1;
            int x = displayIndex & 1;
            int y = displayIndex >> 1;
            return y * 3 + x;
        }

        /// <summary>Dumps every grid stack back into the bag - called when the menu closes.</summary>
        public void RefundCraftingGrid()
        {
            for (int i = 0; i < 9; i++)
            {
                var g = CraftingGrid[i];
                if (g.ItemId > 0)
                {
                    GiveOrDrop(g.ItemId, g.Count);
                    CraftingGrid[i] = (0, 0);
                }
            }
            CraftingResult = null;
        }

        /// <summary>True when the block under the crosshair is a crafting table (opens the menu).</summary>
        public bool TryPickWorkbench(Point3D origin, Point3D direction, out int blockId)
        {
            blockId = 0;
            var pick = TryPickBlock(origin, direction);
            if (!pick.HasValue) return false;
            var pos = pick.Value.Remove;
            if (!Chunks.TryGetLoadedBlock(pos.x, pos.y, pos.z, out blockId)) return false;
            if (blockId != _idWorkbench && blockId != _idCarpentersWorkbench && blockId != _idMasonsWorkbench)
                return false;
            // Remember which station opened us - the UI picks its panel art from this.
            CraftingTableId = blockId;
            return true;
        }

        /// <summary>The table block that opened the crafting menu (0 when closed).</summary>
        public int CraftingTableId { get; private set; }

        /// <summary>
        /// Inventory-style drag interaction with one crafting grid slot (0..8). Cells outside
        /// the active box are inert. Left click: empty cursor picks up the whole stack, held
        /// cursor places/stacks/swaps. Right click: empty cursor picks up half, held cursor
        /// drops one (same type only). Same semantics as the E-menu bag slots
        /// (see <c>CursorClickSlot</c>).
        /// </summary>
        public void CraftingClickSlot(int slot, bool rightClick)
        {
            if (slot < 0 || slot >= 9) return;
            if (!SlotInBox(slot, CraftingSize)) return;
            var held = HeldStack;
            var gridItem = CraftingGrid[slot].ItemId;
            var gridCount = CraftingGrid[slot].Count;

            if (!rightClick)
            {
                // Left click.
                if (held.HasValue)
                {
                    int heldId = held.Value.ItemId;
                    int heldCount = held.Value.Count;
                    int cap = ItemRegistry.StackSizeOf(heldId);
                    if (gridItem == 0)
                    {
                        CraftingGrid[slot] = (heldId, heldCount);
                        HeldStack = null;
                    }
                    else if (gridItem == heldId && gridCount < cap)
                    {
                        int add = Math.Min(heldCount, cap - gridCount);
                        CraftingGrid[slot] = (gridItem, gridCount + add);
                        int nc = heldCount - add;
                        HeldStack = nc > 0 ? (heldId, nc) : null;
                    }
                    else
                    {
                        CraftingGrid[slot] = (heldId, heldCount);
                        HeldStack = (gridItem, gridCount);
                    }
                }
                else if (gridItem != 0)
                {
                    HeldStack = (gridItem, gridCount);
                    CraftingGrid[slot] = (0, 0);
                }
            }
            else
            {
                // Right click.
                if (held.HasValue)
                {
                    int heldId = held.Value.ItemId;
                    int cap = ItemRegistry.StackSizeOf(heldId);
                    bool canPlace = gridItem == 0 || (gridItem == heldId && gridCount < cap);
                    if (canPlace)
                    {
                        if (gridItem == 0) CraftingGrid[slot] = (heldId, 1);
                        else CraftingGrid[slot] = (gridItem, gridCount + 1);
                        int nc = held.Value.Count - 1;
                        HeldStack = nc > 0 ? (heldId, nc) : null;
                    }
                }
                else if (gridItem != 0 && gridCount > 1)
                {
                    int half = (gridCount + 1) / 2;
                    HeldStack = (gridItem, half);
                    CraftingGrid[slot] = (gridItem, gridCount - half);
                }
            }
            UpdateCraftingResult();
        }

        /// <summary>
        /// Takes the crafted result: clears the grid and moves the output onto the cursor.
        /// Refuses when the cursor can't hold it (full with a different item), like Minecraft.
        /// </summary>
        public bool TryCraft()
        {
            if (!CraftingResult.HasValue) return false;
            int outId = CraftingResult.Value.ItemId;
            int outCount = CraftingResult.Value.Count;

            var held = HeldStack;
            bool canHold = !held.HasValue
                || (held.Value.ItemId == outId
                    && held.Value.Count + outCount <= Math.Min(MaxStackSize, ItemRegistry.StackSizeOf(outId)));
            if (!canHold) return false;

            // Consume one of each used grid slot (a matched recipe needs exactly 1 per
            // non-empty slot), leaving the rest of the stack untouched.
            for (int i = 0; i < 9; i++)
            {
                var g = CraftingGrid[i];
                if (g.ItemId > 0)
                {
                    int nc = g.Count - 1;
                    CraftingGrid[i] = nc > 0 ? (g.ItemId, nc) : (0, 0);
                }
            }
            HeldStack = held.HasValue
                ? (outId, held.Value.Count + outCount)
                : (outId, outCount);
            UpdateCraftingResult();
            return true;
        }

        /// <summary>Recomputes CraftingResult from the current grid (called after every change).</summary>
        private void UpdateCraftingResult()
        {
            // Always hand the matcher the full 3x3 backing array; CraftingSize tells it how much
            // of it is actually live.
            var ids = new string[9];
            for (int i = 0; i < 9; i++)
            {
                ids[i] = CraftingGrid[i].ItemId > 0 ? ItemRegistry.GetName(CraftingGrid[i].ItemId) : "";
            }
            if (RecipeRegistry.TryMatch(ids, CraftingSize, out var recipe))
            {
                CraftingResult = (recipe.OutputItemId, recipe.OutputCount);
            }
            else
            {
                CraftingResult = null;
            }
        }
    }
}