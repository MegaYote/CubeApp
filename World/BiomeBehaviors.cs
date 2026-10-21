using System;

namespace Cubuild.World
{
    /// <summary>Plain biome: defers entirely to the data-driven BiomeDefinition knobs.</summary>
    public sealed class DefaultBiomeBehavior : IBiomeTerrainBehavior
    {
        public static readonly DefaultBiomeBehavior Instance = new();
        public double? ComputeDensity(double xq, double yq, double zq, double baseFreq) => null;
        public void ModifySurfaceLine(ref double centerHeight, double xq, double zq) { }
        public bool IsAlphaStyle => false;
        public string? AlphaTargetId => null;
        public AlphaBiomeGenerator? Generator => null;
    }

    /// <summary>Alpha-style biome: uses the byte-faithful 2010 generator override pipeline.</summary>
    public sealed class AlphaStyleBehavior : IBiomeTerrainBehavior
    {
        public AlphaBiomeGenerator Generator { get; }
        public string AlphaTargetId { get; }
        public AlphaStyleBehavior(long seed, AlphaTerrainParams? p, int bandBlocks, string targetId)
        {
            Generator = new AlphaBiomeGenerator(seed, p ?? new AlphaTerrainParams(), bandBlocks);
            AlphaTargetId = targetId;
        }
        public double? ComputeDensity(double xq, double yq, double zq, double baseFreq) => null; // handled by FillOverride
        public void ModifySurfaceLine(ref double centerHeight, double xq, double zq) { }
        public bool IsAlphaStyle => true;
        string? IBiomeTerrainBehavior.AlphaTargetId => AlphaTargetId;
        AlphaBiomeGenerator? IBiomeTerrainBehavior.Generator => Generator;
    }

    /// <summary>Classic biome: quantized 2D heightmap surface.</summary>
    public sealed class ClassicBehavior : IBiomeTerrainBehavior
    {
        private readonly TerrainChunkProvider _ctx;
        public ClassicBehavior(TerrainChunkProvider ctx) { _ctx = ctx; }
        public double? ComputeDensity(double xq, double yq, double zq, double baseFreq) => null;
        public void ModifySurfaceLine(ref double centerHeight, double xq, double zq)
        {
            // Delegated to TerrainChunkProvider which owns the classic noise + beach blending.
            _ctx.ApplyClassicSurface(ref centerHeight, xq, zq);
        }
        public bool IsAlphaStyle => false;
        public string? AlphaTargetId => null;
        public AlphaBiomeGenerator? Generator => null;
    }

    /// <summary>Anomaly biome: 4D density field.</summary>
    public sealed class AnomalyBehavior : IBiomeTerrainBehavior
    {
        private readonly TerrainChunkProvider _ctx;
        public AnomalyBehavior(TerrainChunkProvider ctx) { _ctx = ctx; }
        public double? ComputeDensity(double xq, double yq, double zq, double baseFreq)
            => _ctx.AnomalyDensity(xq, yq, zq, baseFreq);
        public void ModifySurfaceLine(ref double centerHeight, double xq, double zq) { }
        public bool IsAlphaStyle => false;
        public string? AlphaTargetId => null;
        public AlphaBiomeGenerator? Generator => null;
    }

    /// <summary>Chaos biome: 4D density field (heavier).</summary>
    public sealed class ChaosBehavior : IBiomeTerrainBehavior
    {
        private readonly TerrainChunkProvider _ctx;
        public ChaosBehavior(TerrainChunkProvider ctx) { _ctx = ctx; }
        public double? ComputeDensity(double xq, double yq, double zq, double baseFreq)
            => _ctx.ChaosDensity(xq, yq, zq, baseFreq);
        public void ModifySurfaceLine(ref double centerHeight, double xq, double zq) { }
        public bool IsAlphaStyle => false;
        public string? AlphaTargetId => null;
        public AlphaBiomeGenerator? Generator => null;
    }

    /// <summary>Pocket biome: simple MCPE-style gentle heightmap.</summary>
    public sealed class PocketBehavior : IBiomeTerrainBehavior
    {
        private readonly TerrainChunkProvider _ctx;
        public PocketBehavior(TerrainChunkProvider ctx) { _ctx = ctx; }
        public double? ComputeDensity(double xq, double yq, double zq, double baseFreq) => null;
        public void ModifySurfaceLine(ref double centerHeight, double xq, double zq)
        {
            _ctx.ApplyPocketSurface(ref centerHeight, xq, zq);
        }
        public bool IsAlphaStyle => false;
        public string? AlphaTargetId => null;
        public AlphaBiomeGenerator? Generator => null;
    }
}
