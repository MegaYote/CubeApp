using System;
using System.Collections.Generic;

namespace Cubuild
{
    /// <summary>
    /// A passive ore cart: a minecart that nobody rides and nobody drives. It rides the rail
    /// network exactly like <see cref="CartEntity"/> - the same heading snap, the same
    /// along-track gravity, the same coast-to-a-stop - because it IS a CartEntity; only the two
    /// controls are taken away.
    ///
    /// No rider: <see cref="CanBeRidden"/> is false, so right-clicking one never seats the player
    /// and it has no throttle. Its one control is physical. It takes part in the usual
    /// mob/player separation pass (<see cref="ParticipatesInSeparation"/>), which is what lets a
    /// walking player shove it; <see cref="AcceptsExternalPush"/> then turns that shove into
    /// motion along the rails. The minecart opts out of both, so it is completely unaffected.
    /// </summary>
    public sealed class OreCartEntity : CartEntity
    {
        public const int CargoSlotCount = 8;
        private readonly InventorySlot[] _cargoSlots = new InventorySlot[CargoSlotCount];

        public override string MobTypeName => "orecart";

        /// <summary>Eight-slot cargo hold for weight-bearing materials.</summary>
        public IReadOnlyList<InventorySlot> CargoSlots => _cargoSlots;

        /// <summary>No seat, no throttle - you cannot ride or drive an ore cart.</summary>
        public override bool CanBeRidden => false;

        /// <summary>
        /// Unlike the minecart, an ore cart has no rider pinned inside it, so it can safely take
        /// part in separation. That is the mechanism behind "walk into it to push it": the pass
        /// writes a world-space shove into _velX/_velZ, which the base loop folds into Speed.
        /// </summary>
        public override bool ParticipatesInSeparation => true;

        /// <summary>Turns a shove from the separation pass into motion along the rails.</summary>
        protected override bool AcceptsExternalPush => true;

        public OreCartEntity(Point3D position, float yaw) : base(position, yaw) { }

        public void RestoreCargo(ReadOnlySpan<InventorySlot> slots)
        {
            Array.Clear(_cargoSlots, 0, _cargoSlots.Length);
            for (int i = 0; i < Math.Min(slots.Length, CargoSlotCount); i++)
            {
                var slot = slots[i];
                if (slot.ItemId <= 0 || slot.Count <= 0 || ItemRegistry.WeightOf(slot.ItemId) <= 0) continue;
                slot.Count = Math.Min(slot.Count, ItemRegistry.StackSizeOf(slot.ItemId));
                _cargoSlots[i] = slot;
            }
        }

        /// <summary>Applies the shared inventory-cursor left/right click semantics to a cargo slot.</summary>
        public void CursorClickSlot(int slot, bool rightClick, GameWorld world)
        {
            if (slot < 0 || slot >= CargoSlotCount || world == null) return;
            var clicked = _cargoSlots[slot];
            var held = world.HeldStack;

            if (!rightClick)
            {
                if (held.HasValue)
                {
                    int heldId = held.Value.ItemId;
                    if (ItemRegistry.WeightOf(heldId) <= 0) return;
                    int cap = Math.Min(GameWorld.MaxStackSize, ItemRegistry.StackSizeOf(heldId));
                    if (clicked.IsEmpty)
                    {
                        int count = Math.Min(held.Value.Count, cap);
                        _cargoSlots[slot] = new InventorySlot { ItemId = heldId, Count = count };
                        int left = held.Value.Count - count;
                        world.HeldStack = left > 0 ? (heldId, left) : null;
                    }
                    else if (clicked.ItemId == heldId && clicked.Count < cap)
                    {
                        int add = Math.Min(held.Value.Count, cap - clicked.Count);
                        clicked.Count += add;
                        _cargoSlots[slot] = clicked;
                        int left = held.Value.Count - add;
                        world.HeldStack = left > 0 ? (heldId, left) : null;
                    }
                    else
                    {
                        _cargoSlots[slot] = new InventorySlot { ItemId = heldId, Count = Math.Min(held.Value.Count, cap) };
                        world.HeldStack = (clicked.ItemId, clicked.Count);
                    }
                }
                else if (!clicked.IsEmpty)
                {
                    world.HeldStack = (clicked.ItemId, clicked.Count);
                    _cargoSlots[slot] = default;
                }
            }
            else if (held.HasValue)
            {
                int heldId = held.Value.ItemId;
                if (ItemRegistry.WeightOf(heldId) <= 0) return;
                int cap = Math.Min(GameWorld.MaxStackSize, ItemRegistry.StackSizeOf(heldId));
                if (clicked.IsEmpty || (clicked.ItemId == heldId && clicked.Count < cap))
                {
                    if (clicked.IsEmpty) _cargoSlots[slot] = new InventorySlot { ItemId = heldId, Count = 1 };
                    else { clicked.Count++; _cargoSlots[slot] = clicked; }
                    int left = held.Value.Count - 1;
                    world.HeldStack = left > 0 ? (heldId, left) : null;
                }
            }
            else if (!clicked.IsEmpty && clicked.Count > 1)
            {
                int half = (clicked.Count + 1) / 2;
                world.HeldStack = (clicked.ItemId, half);
                clicked.Count -= half;
                _cargoSlots[slot] = clicked;
            }
        }

        public InventorySlot[] CopyCargoSlots()
        {
            var copy = new InventorySlot[CargoSlotCount];
            Array.Copy(_cargoSlots, copy, CargoSlotCount);
            return copy;
        }
    }
}
