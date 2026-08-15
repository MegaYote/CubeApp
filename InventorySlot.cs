namespace CubeApp
{
    /// <summary>
    /// One inventory slot: an item id + count (Minecraft-style stacks; blocks are items too,
    /// so a slot can hold dirt or a pickaxe). A slot is empty when Count is 0. Slots stack up
    /// to <see cref="GameWorld.MaxStackSize"/> (or the item's own stack size).
    /// </summary>
    public struct InventorySlot
    {
        public int ItemId;
        public int Count;

        public bool IsEmpty => Count <= 0;

        public void Clear()
        {
            ItemId = 0;
            Count = 0;
        }
    }
}
