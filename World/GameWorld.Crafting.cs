using System;

namespace Cubuild
{
    public sealed partial class GameWorld : IDisposable
    {
        // ---- workbench crafting (2x2 grid + output) -------------------------------
        // The grid holds item stacks, shared with the inventory cursor (HeldStack) so items
        // can be dragged between the E-menu and the workbench grid. The result is recomputed
        // after every grid change from recipes.json via RecipeRegistry (rotation-insensitive).

        /// <summary>The 2x2 crafting grid, row-major: [ top-left, top-right, bottom-left, bottom-right ].</summary>
        public (int ItemId, int Count)[] CraftingGrid { get; } = new (int, int)[4];

        /// <summary>Live result of the current grid (null = no recipe matches).</summary>
        public (int ItemId, int Count)? CraftingResult { get; private set; }

        /// <summary>True when the block under the crosshair is a workbench (opens the menu).</summary>
        public bool TryPickWorkbench(Point3D origin, Point3D direction, out int blockId)
        {
            blockId = 0;
            var pick = TryPickBlock(origin, direction);
            if (!pick.HasValue) return false;
            var pos = pick.Value.Remove;
            if (!Chunks.TryGetLoadedBlock(pos.x, pos.y, pos.z, out blockId)) return false;
            return blockId == _idWorkbench;
        }

        /// <summary>
        /// Inventory-style drag interaction with one crafting grid slot (0..3). Left click:
        /// empty cursor picks up the whole stack, held cursor places/stacks/swaps. Right click:
        /// empty cursor picks up half, held cursor drops one (same type only). Same semantics as
        /// the E-menu bag slots (see <c>CursorClickSlot</c>).
        /// </summary>
        public void CraftingClickSlot(int slot, bool rightClick)
        {
            if (slot < 0 || slot >= 4) return;
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
                    if (gridItem == 0)
                    {
                        CraftingGrid[slot] = (heldId, heldCount);
                        HeldStack = null;
                    }
                    else if (gridItem == heldId && gridCount < MaxStackSize)
                    {
                        int add = Math.Min(heldCount, MaxStackSize - gridCount);
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
                    bool canPlace = gridItem == 0 || (gridItem == heldId && gridCount < MaxStackSize);
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

            for (int i = 0; i < 4; i++) CraftingGrid[i] = (0, 0);
            HeldStack = held.HasValue
                ? (outId, held.Value.Count + outCount)
                : (outId, outCount);
            UpdateCraftingResult();
            return true;
        }

        /// <summary>Recomputes CraftingResult from the current grid (called after every change).</summary>
        private void UpdateCraftingResult()
        {
            var ids = new string[4];
            for (int i = 0; i < 4; i++)
            {
                ids[i] = CraftingGrid[i].ItemId > 0 ? ItemRegistry.GetName(CraftingGrid[i].ItemId) : "";
            }
            if (RecipeRegistry.TryMatch(ids, out var recipe))
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