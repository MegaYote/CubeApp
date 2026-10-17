using System;

namespace Cubuild
{
    /// <summary>
    /// Water meets lava, following vanilla's fluid-mixing rules:
    ///
    ///  1. Flowing lava touching water in any direction EXCEPT straight down chills into
    ///     cobblestone.
    ///  2. Lava resting directly on top of water turns that water into stone. (This is vanilla's
    ///     "lava flows into water" case - rule 1 makes it reachable only from above.)
    ///  3. Water touching a lava SOURCE from above or the side sets that source into obsidian.
    ///
    /// A lava source is metadata 0. Anything else is a stream - levels 1..4, plus 8 for the
    /// falling flag - so "source vs flowing" is just <c>meta == 0</c>, the same test the lava
    /// simulation already uses when it folds neighbour levels.
    ///
    /// The two fluids are never allowed to cross: they only ever convert, and only while
    /// adjacent. That is enforced by ordering rather than by teaching each fluid about the
    /// other - the scheduler resolves contact for a cell before it ticks that cell's flow, so a
    /// reaction always wins the race against the flow that would have entered the cell. See
    /// <see cref="BlockTickScheduler.TickOnce"/>.
    /// </summary>
    public sealed class FluidInteraction
    {
        private readonly ChunkManager _manager;
        private readonly BlockTickScheduler _scheduler;
        private readonly int _waterId;
        private readonly int _lavaId;
        private readonly int _stoneId;
        private readonly int _cobblestoneId;
        private readonly int _obsidianId;

        public FluidInteraction(ChunkManager manager, BlockTickScheduler scheduler)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            _waterId = BlockRegistry.GetId("water");
            _lavaId = BlockRegistry.GetId("lava");
            _stoneId = BlockRegistry.GetId("stone");
            _cobblestoneId = BlockRegistry.GetId("cobblestone");
            _obsidianId = BlockRegistry.GetId("obsidian");
        }

        /// <summary>
        /// Resolves any lava/water contact involving this cell or the six cells around it. Used
        /// when the world changes (place/break), so a fluid dropped next to the other fluid reacts
        /// at once instead of waiting for a scheduled tick.
        /// </summary>
        public bool Resolve(int x, int y, int z)
        {
            bool any = TryReact(x, y, z);
            any |= TryReact(x + 1, y, z);
            any |= TryReact(x - 1, y, z);
            any |= TryReact(x, y + 1, z);
            any |= TryReact(x, y - 1, z);
            any |= TryReact(x, y, z + 1);
            any |= TryReact(x, y, z - 1);
            return any;
        }

        /// <summary>
        /// Resolves contact involving just this one cell. Enough on its own during the tick loop:
        /// every fluid cell re-schedules itself while it moves, and whichever half of a pair ticks
        /// first resolves the contact for both (a lava cell sets itself, a water cell sets the lava
        /// beside it).
        /// </summary>
        public bool TryReact(int x, int y, int z)
        {
            int id = _manager.GetBlockAt(x, y, z);
            if (id == _lavaId)
            {
                return ResolveLava(x, y, z);
            }

            // A water cell can't change itself, but a lava neighbour it just met can.
            if (id != _waterId)
            {
                return false;
            }

            bool any = false;
            any |= ResolveLava(x + 1, y, z);
            any |= ResolveLava(x - 1, y, z);
            any |= ResolveLava(x, y + 1, z);
            any |= ResolveLava(x, y - 1, z);
            any |= ResolveLava(x, y, z + 1);
            any |= ResolveLava(x, y, z - 1);
            return any;
        }

        private bool ResolveLava(int x, int y, int z)
        {
            if (_manager.GetBlockAt(x, y, z) != _lavaId)
            {
                return false;
            }

            bool isSource = _manager.GetMetaAt(x, y, z) == 0;

            // "Except straight down" - so the cell below is deliberately left out here and handled
            // by the stone rule afterwards.
            bool waterTopOrSide = IsWater(x, y + 1, z)
                || IsWater(x - 1, y, z)
                || IsWater(x + 1, y, z)
                || IsWater(x, y, z - 1)
                || IsWater(x, y, z + 1);

            if (isSource && waterTopOrSide)
            {
                return Convert(x, y, z, _obsidianId);
            }

            if (!isSource && waterTopOrSide)
            {
                return Convert(x, y, z, _cobblestoneId);
            }

            if (IsWater(x, y - 1, z))
            {
                return Convert(x, y - 1, z, _stoneId);
            }

            return false;
        }

        private bool Convert(int x, int y, int z, int blockId)
        {
            // The cell was just read as lava/water, so its chunk is loaded and TrySetBlock cannot
            // be forced to generate. It is also the overload that passes the old/new ids into the
            // dirty bookkeeping, which is what clears a light source's glow from the neighbours -
            // lava emits 15, so turning it to obsidian has to wake them.
            if (!_manager.TrySetBlock(x, y, z, blockId, 0))
            {
                return false;
            }

            _scheduler.NotifyBlockChanged(x, y, z);
            return true;
        }

        private bool IsWater(int x, int y, int z) => _manager.GetBlockAt(x, y, z) == _waterId;
    }
}
