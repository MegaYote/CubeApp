using System;

namespace Cubuild
{
    /// <summary>
    /// Random drop-count rolls for blocks that drop a variable stack (an ore block drops 1-8
    /// raw chunks instead of a single item).
    /// </summary>
    public static class DropRoll
    {
        /// <summary>
        /// Rolls a count in [min,max] with a TRIANGULAR distribution: the smallest count is the
        /// most likely and every step up is one step less likely (the weight of a value is
        /// max - value + 1). For 1..8 that is 22%, 19%, 17%, 14%, 11%, 8%, 6%, 3% - mostly
        /// random, but leaning toward the smaller amounts.
        /// </summary>
        public static int WeightedCount(Random rng, int min, int max)
        {
            if (max <= min) return min;
            int span = max - min + 1;
            int totalWeight = span * (span + 1) / 2; // 1 + 2 + ... + span
            int roll = rng.Next(totalWeight);
            int cumulative = 0;
            for (int n = 0; n < span; n++)
            {
                cumulative += span - n;
                if (roll < cumulative) return min + n;
            }
            return max;
        }
    }
}
