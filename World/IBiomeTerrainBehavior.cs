using System;

namespace Cubuild.World
{
    /// <summary>
    /// A biome's terrain-generation behavior. Plain biomes use <see cref="DefaultBiomeBehavior"/>
    /// (which defers entirely to the data-driven <see cref="BiomeDefinition"/> knobs). Biomes with
    /// special terrain (alpha, classic, anomaly, chaos, pocket, ...) provide a subclass that
    /// overrides only the pieces it cares about. This keeps TerrainChunkProvider free of
    /// biome-id string checks: it just asks the registry what to do.
    /// </summary>
    public interface IBiomeTerrainBehavior
    {
        /// <summary>Returns a custom density value for this column, or null to use the engine's
        /// default 3D body noise.</summary>
        double? ComputeDensity(double xq, double yq, double zq, double baseFreq);

        /// <summary>Modifies the blended surface line in place (classic/pocket style). No-op by default.</summary>
        void ModifySurfaceLine(ref double centerHeight, double xq, double zq);

        /// <summary>True when this biome uses the alpha-style override pipeline (FillOverride +
        /// SurfacePass + alpha trees/ores).</summary>
        bool IsAlphaStyle { get; }

        /// <summary>For alpha-style biomes, the id used to filter chunk cells. Null otherwise.</summary>
        string? AlphaTargetId { get; }

        /// <summary>For alpha-style biomes, the generator instance. Null otherwise.</summary>
        AlphaBiomeGenerator? Generator { get; }
    }
}
