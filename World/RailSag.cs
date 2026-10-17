using System;

namespace Cubuild
{
    /// <summary>
    /// Shared constants and (de)quantization for hanging rail track.
    ///
    /// Rails no longer float the moment the block under them is gone. A rail that loses its
    /// support becomes a plank hinged to whatever rail it connects to, and a run of them droops
    /// and swings downward like a rope bridge - only ever up/down, never sideways, so a cart can
    /// still be driven across it (badly). The per-cell amount of sink ("droop") is stored on the
    /// chunk as a byte (see <see cref="Chunk.RawRailDroop"/>), quantized over
    /// <see cref="MaxDrop"/> blocks; the solver in GameWorld.RailSag decides the values and
    /// <see cref="MaxDrop"/> is the deepest a fully unanchored run is allowed to hang.
    /// </summary>
    public static class RailSag
    {
        /// <summary>Deepest a rail may droop, in blocks. A run with no support at all bottoms
        /// out here and just dangles ("hanging by a thread").</summary>
        public const double MaxDrop = 6.0;

        /// <summary>Largest value the droop byte can hold.</summary>
        public const int ByteMax = 255;

        /// <summary>Blocks of droop per unit of the 0..255 byte.</summary>
        public const double BlocksPerUnit = MaxDrop / ByteMax;

        /// <summary>Pack a droop distance (blocks, clamped to [0, MaxDrop]) into a byte.</summary>
        public static byte Quantize(double drop)
        {
            if (drop <= 0) return 0;
            if (drop >= MaxDrop) return ByteMax;
            return (byte)(drop / BlocksPerUnit + 0.5);
        }

        /// <summary>Unpack a droop byte back to blocks.</summary>
        public static double Dequantize(byte value) => value * BlocksPerUnit;
    }
}
