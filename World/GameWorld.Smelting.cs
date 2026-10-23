using System;

namespace Cubuild
{
    public sealed partial class GameWorld : IDisposable
    {
        // ---- kiln / furnace smelting -----------------------------------------------
        // A mocked but functional furnace: one input stack, one fuel stack, one output slot.
        // The input burns down over time, consuming fuel seconds; when a recipe completes its
        // product pops into the output. Only a single kiln state exists for now (the world
        // tracks the kiln you are standing at), which is plenty for a survival loop and keeps
        // it out of the save format.

        private static readonly int _idKiln = BlockRegistry.GetId("kiln");
        private static readonly int _idForge = BlockRegistry.GetId("forge");

        /// <summary>True when the block under the crosshair is a kiln or forge (opens the screen).</summary>
        public bool TryPickSmelter(Point3D origin, Point3D direction, out int blockId)
        {
            blockId = 0;
            var pick = TryPickBlock(origin, direction);
            if (!pick.HasValue) return false;
            var pos = pick.Value.Remove;
            if (!Chunks.TryGetLoadedBlock(pos.x, pos.y, pos.z, out blockId)) return false;
            return blockId == _idKiln || blockId == _idForge;
        }

        /// <summary>True while a kiln screen is open.</summary>
        public bool SmeltOpen { get; private set; }
        /// <summary>The specific kiln/forge station currently in use.</summary>
        public int SmeltStationBlockId { get; private set; }
        private string SmeltStationName => SmeltStationBlockId == _idForge ? "forge" : "kiln";

        /// <summary>Stack waiting to be smelted (consumed from the top, one at a time).</summary>
        public (int ItemId, int Count)? SmeltInput { get; private set; }

        /// <summary>Stack being burnt for heat.</summary>
        public (int ItemId, int Count)? SmeltFuel { get; private set; }

        /// <summary>Finished goods waiting to be collected.</summary>
        public (int ItemId, int Count)? SmeltOutput { get; private set; }

        /// <summary>Seconds of heat left in the current fuel unit.</summary>
        public float SmeltBurnTime { get; private set; }

        /// <summary>
        /// The fuel item CURRENTLY burning, which outlives the stack it came from: consuming
        /// the last coal pops SmeltFuel to null, but the coal is still lit for its remaining
        /// burn seconds. The flame gauge must read this, not SmeltFuel, or it blanks at the
        /// exact moment the fuel is running out.
        /// </summary>
        public int SmeltActiveFuelId { get; private set; }

        /// <summary>0..1 progress through the current item being smelted.</summary>
        public float SmeltProgress { get; private set; }

        /// <summary>0..1 progress through the current fuel unit (for the flame gauge).</summary>
        public float SmeltBurnProgress
            => SmeltActiveFuelId > 0 && SmeltBurnTime > 0f
                ? Math.Clamp(SmeltBurnTime / SmeltRegistry.FuelSeconds(SmeltActiveFuelId), 0f, 1f)
                : 0f;

        /// <summary>What the current input would produce, or null when it can't smelt.</summary>
        public SmeltRecipe? SmeltRecipeForInput => SmeltInput is { } i ? SmeltRegistry.Find(i.ItemId, SmeltStationName) : null;

        public void OpenSmelter(int stationBlockId = 0)
        {
            SmeltStationBlockId = stationBlockId == _idForge ? _idForge : _idKiln;
            SmeltOpen = true;
        }

        /// <summary>Closes the screen, returning the input and fuel to the player (never lost).</summary>
        public void CloseSmelter()
        {
            if (SmeltInput is { } i) GiveOrDrop(i.ItemId, i.Count);
            if (SmeltFuel is { } f) GiveOrDrop(f.ItemId, f.Count);
            SmeltInput = null;
            SmeltFuel = null;
            SmeltActiveFuelId = 0;
            SmeltBurnTime = 0f;
            SmeltProgress = 0f;
            SmeltStationBlockId = 0;
            SmeltOpen = false;
        }

        /// <summary>Inventory-style click on one of the three kiln cells.</summary>
        /// <paramref name="slot"/>: 0 = input, 1 = fuel, 2 = output (collect only).</summary>
        public void SmeltClickSlot(int slot, bool rightClick)
        {
            if (slot == 2)
            {
                CollectSmeltOutput(rightClick);
                return;
            }
            if (slot != 0 && slot != 1) return;

            var held = HeldStack;
            bool isInput = slot == 0;
            var current = isInput ? SmeltInput : SmeltFuel;

            if (!rightClick)
            {
                if (held.HasValue)
                {
                    // Fuel only accepts fuel; the input only accepts something smeltable.
                    if (isInput ? SmeltRegistry.Find(held.Value.ItemId, SmeltStationName) == null : !SmeltRegistry.IsFuel(held.Value.ItemId))
                        return;
                    int heldId = held.Value.ItemId, heldCount = held.Value.Count;
                    if (current is { } c && c.ItemId == heldId)
                    {
                        int merged = c.Count + heldCount;
                        int cap = ItemRegistry.StackSizeOf(heldId);
                        int add = Math.Min(merged, cap) - c.Count;
                        if (add > 0)
                        {
                            if (isInput) SmeltInput = (heldId, c.Count + add); else SmeltFuel = (heldId, c.Count + add);
                            int left = heldCount - add;
                            HeldStack = left > 0 ? (heldId, left) : null;
                        }
                    }
                    else
                    {
                        if (isInput) SmeltInput = (heldId, heldCount); else SmeltFuel = (heldId, heldCount);
                        HeldStack = current is { } cc && cc.ItemId > 0 ? (cc.ItemId, cc.Count) : null;
                    }
                }
                else if (current is { } c2 && c2.ItemId > 0)
                {
                    HeldStack = (c2.ItemId, c2.Count);
                    if (isInput) SmeltInput = null; else SmeltFuel = null;
                    // Pulling fuel out before it's spent discards the unused burn time.
                    if (!isInput) { SmeltBurnTime = 0f; SmeltActiveFuelId = 0; }
                }
            }
            else
            {
                if (held.HasValue)
                {
                    if (isInput ? SmeltRegistry.Find(held.Value.ItemId, SmeltStationName) == null : !SmeltRegistry.IsFuel(held.Value.ItemId))
                        return;
                    int heldId = held.Value.ItemId;
                    if (current is not { } c || c.ItemId == 0)
                    {
                        if (isInput) SmeltInput = (heldId, 1); else SmeltFuel = (heldId, 1);
                        int left = held.Value.Count - 1;
                        HeldStack = left > 0 ? (heldId, left) : null;
                    }
                }
                else if (current is { } c3 && c3.Count > 1)
                {
                    // Right-click takes one item back onto the cursor.
                    if (isInput) SmeltInput = (c3.ItemId, c3.Count - 1); else SmeltFuel = (c3.ItemId, c3.Count - 1);
                    HeldStack = (c3.ItemId, 1);
                    if (!isInput && SmeltFuel!.Value.Count == 0) { SmeltBurnTime = 0f; SmeltActiveFuelId = 0; }
                }
            }
        }

        private void CollectSmeltOutput(bool all)
        {
            if (SmeltOutput is not { } o) return;
            var held = HeldStack;
            int cap = ItemRegistry.StackSizeOf(o.ItemId);
            if (held.HasValue && held.Value.ItemId != o.ItemId) return;
            int room = cap - (held?.Count ?? 0);
            if (room <= 0) return;
            int take = all ? Math.Min(o.Count, room) : Math.Min(1, room);
            if (take <= 0) return;
            HeldStack = (o.ItemId, (held?.Count ?? 0) + take);
            SmeltOutput = o.Count - take > 0 ? (o.ItemId, o.Count - take) : null;
        }

        /// <summary>
        /// Advances the burn by <paramref name="dt"/> seconds. Runs while the kiln screen is
        /// open, so a kiln only ever cooks while you stand at it - simple, and it keeps the
        /// whole thing out of the save format.
        /// </summary>
        public void TickSmelter(float dt)
        {
            if (!SmeltOpen || dt <= 0f) return;

            var recipe = SmeltRecipeForInput;
            if (recipe == null)
            {
                // Nothing to smelt. Extinguish rather than freezing the gauge: without this the
                // flame stayed part-lit forever, claiming fuel was burning while none was,
                // because the early return skipped the burn/clear steps below.
                SmeltActiveFuelId = 0;
                SmeltBurnTime = 0f;
                SmeltProgress = 0f;
                return;
            }

            // Light the next fuel unit when the previous one is spent.
            if (SmeltBurnTime <= 0f)
            {
                SmeltActiveFuelId = 0;
                if (SmeltFuel is { ItemId: > 0 } f && SmeltRegistry.FuelSeconds(f.ItemId) > 0f)
                {
                    SmeltBurnTime = SmeltRegistry.FuelSeconds(f.ItemId);
                    SmeltActiveFuelId = f.ItemId;
                    SmeltFuel = (f.ItemId, f.Count - 1);
                    if (SmeltFuel.Value.Count <= 0) SmeltFuel = null;
                }
                else
                {
                    SmeltProgress = 0f;
                    return;                     // nothing to burn: the smelt stalls
                }
            }

            float spend = Math.Min(dt, SmeltBurnTime);
            SmeltBurnTime -= spend;
            if (SmeltBurnTime <= 0f) SmeltActiveFuelId = 0;
            SmeltProgress += spend / recipe.Time;

            // At most one item finishes per tick, which keeps the output stack a clean 1:1
            // with the input and avoids a burst of completions on a long frame.
            if (SmeltProgress >= 1f)
            {
                SmeltProgress = 0f;
                // Only one product at a time can stack, same as a real furnace output slot.
                if (SmeltOutput is { } o && o.ItemId != recipe.OutputItemId)
                    return;                     // output is blocked - wait for it to be taken

                if (SmeltOutput is { } o2)
                {
                    int cap = ItemRegistry.StackSizeOf(recipe.OutputItemId);
                    if (o2.Count < cap) SmeltOutput = (recipe.OutputItemId, o2.Count + 1);
                }
                else
                {
                    SmeltOutput = (recipe.OutputItemId, 1);
                }

                if (SmeltInput is { } inp)
                {
                    int left = inp.Count - 1;
                    SmeltInput = left > 0 ? (inp.ItemId, left) : null;
                }
            }
        }
    }
}
