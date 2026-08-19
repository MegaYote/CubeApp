using System;

namespace Cubuild.World
{
    /// <summary>
    /// Faithful C# port of java.util.Random (the 48-bit LCG), so the Alpha biome's
    /// world/chunk seeding produces the EXACT same draw sequence as Minecraft 2010.
    /// </summary>
    public sealed class JavaRandom
    {
        private const ulong Multiplier = 0x5DEECE66DUL;
        private const ulong Addend = 0xBUL;
        private const ulong Mask = (1UL << 48) - 1;
        private ulong _seed;

        public JavaRandom(long seed) => SetSeed(seed);

        public void SetSeed(long seed) => _seed = ((ulong)seed ^ Multiplier) & Mask;

        private int Next(int bits)
        {
            _seed = (_seed * Multiplier + Addend) & Mask;
            return (int)(_seed >> (48 - bits));
        }

        public int NextInt(int n)
        {
            // java.util.Random.nextInt(int): power-of-two fast path + rejection sampling.
            if ((n & -n) == n) return (int)((n * (long)Next(31)) >> 31);
            int bits, val;
            do
            {
                bits = Next(31);
                val = bits % n;
            } while (bits - val + (n - 1) < 0);
            return val;
        }

        public long NextLong() => ((long)Next(32) << 32) + Next(32);

        public double NextDouble() => (((long)Next(26) << 27) + Next(27)) / 9007199254740992.0;

        public double NextFloat() => Next(24) / 16777216.0;
    }

    /// <summary>
    /// Byte-faithful port of Minecraft's 2010 NoiseGeneratorPerlin: quintic-fade Perlin with
    /// a 512-entry permutation built from java.util.Random draws, the x/y/z coordinate
    /// offsets, and the 4-bit gradient table (same draw/permutation order).
    /// </summary>
    public sealed class JavaPerlin
    {
        private readonly int[] _p = new int[512];
        private readonly double _xo, _yo, _zo;

        public JavaPerlin(JavaRandom r)
        {
            _xo = r.NextDouble() * 256.0;
            _yo = r.NextDouble() * 256.0;
            _zo = r.NextDouble() * 256.0;
            for (int i = 0; i < 256; i++) _p[i] = i;
            for (int i = 0; i < 256; i++)
            {
                int j = r.NextInt(256 - i) + i;
                int t = _p[i];
                _p[i] = _p[j];
                _p[j] = t;
                _p[i + 256] = _p[i];
            }
        }

        public double Noise(double x, double y, double z)
        {
            double vx = x + _xo, vy = y + _yo, vz = z + _zo;
            int xc = (int)Math.Floor(vx) & 255;
            int yc = (int)Math.Floor(vy) & 255;
            int zc = (int)Math.Floor(vz) & 255;
            double fx = vx - Math.Floor(vx);
            double fy = vy - Math.Floor(vy);
            double fz = vz - Math.Floor(vz);
            double u = Fade(fx), v = Fade(fy), w = Fade(fz);

            int a = _p[xc] + yc;
            int aa = _p[a] + zc;
            int b = _p[a + 1] + zc;
            int c = _p[xc + 1] + yc;
            int cc = _p[c] + zc;
            int d = _p[c + 1] + zc;

            double x1 = Lerp(u, Grad(_p[aa], fx, fy, fz), Grad(_p[cc], fx - 1, fy, fz));
            double x2 = Lerp(u, Grad(_p[b], fx, fy - 1, fz), Grad(_p[d], fx - 1, fy - 1, fz));
            double y1 = Lerp(v, x1, x2);
            double x3 = Lerp(u, Grad(_p[aa + 1], fx, fy, fz - 1), Grad(_p[cc + 1], fx - 1, fy, fz - 1));
            double x4 = Lerp(u, Grad(_p[b + 1], fx, fy - 1, fz - 1), Grad(_p[d + 1], fx - 1, fy - 1, fz - 1));
            return Lerp(w, y1, Lerp(v, x3, x4));
        }

        public double Noise(double x, double z) => Noise(x, z, 0.0);

        private static double Fade(double t) => t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
        private static double Lerp(double t, double a, double b) => a + t * (b - a);
        private static double Grad(int h, double x, double y, double z)
        {
            h &= 15;
            double u = h < 8 ? x : y;
            double v2 = h < 4 ? y : (h == 12 || h == 14 ? x : z);
            return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v2 : -v2);
        }
    }

    /// <summary>
    /// Byte-faithful port of Minecraft's 2010 NoiseGeneratorOctaves: octave i samples the
    /// Perlin field at 2^i scale and is weighted by 1/(2^i) (frequency doubles, amplitude
    /// halves each octave) - the signature old-alpha noise stack that made the 684.412
    /// terrain. Returns roughly [-2, +2] for a 5-octave stack.
    /// </summary>
    public sealed class JavaOctaves
    {
        private readonly JavaPerlin[] _gens;
        private readonly int _count;

        public JavaOctaves(JavaRandom r, int count)
        {
            _count = count;
            _gens = new JavaPerlin[count];
            for (int i = 0; i < count; i++) _gens[i] = new JavaPerlin(r);
        }

        public double Noise3D(double x, double y, double z)
        {
            double sum = 0.0, f = 1.0;
            for (int i = 0; i < _count; i++)
            {
                sum += _gens[i].Noise(x * f, y * f, z * f) / f;
                f *= 2.0;
            }
            return sum;
        }

        public double Noise2D(double x, double z)
        {
            double sum = 0.0, f = 1.0;
            for (int i = 0; i < _count; i++)
            {
                sum += _gens[i].Noise(x * f, z * f) / f;
                f *= 2.0;
            }
            return sum;
        }
    }

    /// <summary>
    /// Tunable knobs for the Alpha biome (Minecraft's "old terrain generator", last used by
    /// Alpha v1.1.2_01). Defaults are the EXACT constants from the 20100415 decompile.
    /// </summary>
    public sealed class AlphaTerrainParams
    {
        public int LowNoiseOctaves = 16;        // noiseGen1 - low terrain body
        public int HighNoiseOctaves = 16;       // noiseGen2 - high terrain body
        public int SelectorNoiseOctaves = 8;    // noiseGen3 - blends the two bodies
        public int SandGravelNoiseOctaves = 4;  // noiseGen4 - sand & gravel dice
        public int DirtDepthNoiseOctaves = 4;   // noiseGen5 - dirt depth
        public int TreeNoiseOctaves = 5;        // mobSpawnerNoise - forest density
        public double CoordinateScale = 684.412;           // body x/z frequency
        public double HeightScale = 984.412;               // body y frequency (big mountains)
        public double MainNoiseScaleXZ = 684.412 / 80.0;   // selector x/z frequency
        public double MainNoiseScaleY = 684.412 / 400.0;   // selector y frequency
        public double HeightStretch = 4.0;                 // the "y*4 - 64" ramp slope
    }
}