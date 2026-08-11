namespace CubeApp
{
    /// <summary>
    /// One survival inventory slot: a block type + count (the C++ Cubuild slot model, ported).
    /// A slot is empty when Count is 0. Slots stack up to <see cref="GameWorld.MaxStackSize"/>.
    /// </summary>
    public struct InventorySlot
    {
        public int BlockId;
        public int Count;

        public bool IsEmpty => Count <= 0;

        public void Clear()
        {
            BlockId = 0;
            Count = 0;
        }
    }
}
