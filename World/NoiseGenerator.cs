using System;

namespace Cubuild.World
{
    /// <summary>
    /// Classic improved Perlin noise: a 512-entry permutation table with the quintic fade and the
    /// classic 16-gradient hash. A standard, widely-used noise primitive.
    /// </summary>
    public sealed class PerlinNoise
    {
        private readonly int[] _permutations = new int[512];
        private readonly double _xOffset;
        private readonly double _yOffset;
        private readonly double _zOffset;

        public PerlinNoise(Random rand)
        {
            _xOffset = rand.NextDouble() * 256.0;
            _yOffset = rand.NextDouble() * 256.0;
            _zOffset = rand.NextDouble() * 256.0;
            for (int i = 0; i < 256; i++) _permutations[i] = i;
            for (int i = 0; i < 256; i++)
            {
                int j = rand.Next(256 - i) + i;
                int tmp = _permutations[i];
                _permutations[i] = _permutations[j];
                _permutations[j] = tmp;
                _permutations[i + 256] = _permutations[i];
            }
        }

        public double Noise(double x, double y, double z)
        {
            double nx = x + _xOffset;
            double ny = y + _yOffset;
            double nz = z + _zOffset;
            int ix = (int)nx;
            int iy = (int)ny;
            int iz = (int)nz;
            if (nx < ix) ix--;
            if (ny < iy) iy--;
            if (nz < iz) iz--;
            int X = ix & 255;
            int Y = iy & 255;
            int Z = iz & 255;
            nx -= ix;
            ny -= iy;
            nz -= iz;
            double u = Fade(nx);
            double v = Fade(ny);
            double w = Fade(nz);
            int a = _permutations[X] + Y;
            int aa = _permutations[a] + Z;
            int ab = _permutations[a + 1] + Z;
            int b = _permutations[X + 1] + Y;
            int ba = _permutations[b] + Z;
            int bb = _permutations[b + 1] + Z;

            double x1 = Lerp(Grad(_permutations[aa], nx, ny, nz), Grad(_permutations[ba], nx - 1, ny, nz), u);
            double x2 = Lerp(Grad(_permutations[ab], nx, ny - 1, nz), Grad(_permutations[bb], nx - 1, ny - 1, nz), u);
            double y1 = Lerp(x1, x2, v);
            x1 = Lerp(Grad(_permutations[aa + 1], nx, ny, nz - 1), Grad(_permutations[ba + 1], nx - 1, ny, nz - 1), u);
            x2 = Lerp(Grad(_permutations[ab + 1], nx, ny - 1, nz - 1), Grad(_permutations[bb + 1], nx - 1, ny - 1, nz - 1), u);
            double y2 = Lerp(x1, x2, v);
            return Lerp(y1, y2, w);
        }

        public double Noise2D(double x, double z) => Noise(x, z, 0.0);

        private static double Fade(double t) => t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
        private static double Lerp(double a, double b, double t) => a + t * (b - a);

        private static double Grad(int hash, double x, double y, double z)
        {
            int h = hash & 15;
            double u = h < 8 ? x : y;
            double v = h < 4 ? y : (h == 12 || h == 14 ? x : z);
            return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
        }
    }

    /// <summary>
    /// Classic 3D simplex noise (Stefan Gustavson's improved Perlin simplex), ~4 corner
    /// interpolations vs classic Perlin's 8 - faster in 3D with a similar organic character.
    /// Output is roughly [-1, 1].
    /// </summary>
    public sealed class SimplexNoise
    {
        private readonly short[] _perm = new short[512];
        private readonly double _offsetX;
        private readonly double _offsetY;
        private readonly double _offsetZ;

        public SimplexNoise(Random rand)
        {
            var p = new short[256];
            for (int i = 0; i < 256; i++) p[i] = (short)i;
            for (int i = 0; i < 256; i++)
            {
                int j = rand.Next(256 - i) + i;
                (p[i], p[j]) = (p[j], p[i]);
            }
            for (int i = 0; i < 512; i++) _perm[i] = p[i & 255];
            _offsetX = rand.NextDouble() * 256.0;
            _offsetY = rand.NextDouble() * 256.0;
            _offsetZ = rand.NextDouble() * 256.0;
        }

        public double Noise(double xin, double yin, double zin)
        {
            xin += _offsetX;
            yin += _offsetY;
            zin += _offsetZ;

            const double F3 = 1.0 / 3.0;
            const double G3 = 1.0 / 6.0;
            double s = (xin + yin + zin) * F3;
            int i = FastFloor(xin + s);
            int j = FastFloor(yin + s);
            int k = FastFloor(zin + s);
            double t = (i + j + k) * G3;
            double x0 = xin - (i - t);
            double y0 = yin - (j - t);
            double z0 = zin - (k - t);

            int i1, j1, k1, i2, j2, k2;
            if (x0 >= y0)
            {
                if (y0 >= z0) { i1 = 1; j1 = 0; k1 = 0; i2 = 1; j2 = 1; k2 = 0; }
                else if (x0 >= z0) { i1 = 1; j1 = 0; k1 = 0; i2 = 1; j2 = 0; k2 = 1; }
                else { i1 = 0; j1 = 0; k1 = 1; i2 = 1; j2 = 0; k2 = 1; }
            }
            else
            {
                if (y0 < z0) { i1 = 0; j1 = 0; k1 = 1; i2 = 0; j2 = 1; k2 = 1; }
                else if (x0 < z0) { i1 = 0; j1 = 1; k1 = 0; i2 = 0; j2 = 1; k2 = 1; }
                else { i1 = 0; j1 = 1; k1 = 0; i2 = 1; j2 = 1; k2 = 0; }
            }

            double x1 = x0 - i1 + G3;
            double y1 = y0 - j1 + G3;
            double z1 = z0 - k1 + G3;
            double x2 = x0 - i2 + 2.0 * G3;
            double y2 = y0 - j2 + 2.0 * G3;
            double z2 = z0 - k2 + 2.0 * G3;
            double x3 = x0 - 1.0 + 3.0 * G3;
            double y3 = y0 - 1.0 + 3.0 * G3;
            double z3 = z0 - 1.0 + 3.0 * G3;

            int ii = i & 255;
            int jj = j & 255;
            int kk = k & 255;

            double n0 = Corner(_perm[ii + _perm[jj + _perm[kk]]], x0, y0, z0);
            double n1 = Corner(_perm[ii + i1 + _perm[jj + j1 + _perm[kk + k1]]], x1, y1, z1);
            double n2 = Corner(_perm[ii + i2 + _perm[jj + j2 + _perm[kk + k2]]], x2, y2, z2);
            double n3 = Corner(_perm[ii + 1 + _perm[jj + 1 + _perm[kk + 1]]], x3, y3, z3);
            return 32.0 * (n0 + n1 + n2 + n3);
        }

        public double Noise2D(double x, double z) => Noise(x, z, 0.0);

        private static double Corner(int hash, double x, double y, double z)
        {
            double t = 0.6 - x * x - y * y - z * z;
            if (t < 0.0) return 0.0;
            t *= t;
            return t * t * Grad(hash, x, y, z);
        }

        private static int FastFloor(double x) => x >= 0 ? (int)x : (int)x - 1;
        private static double Grad(int hash, double x, double y, double z)
        {
            int h = hash & 15;
            double u = h < 8 ? x : y;
            double v = h < 4 ? y : (h == 12 || h == 14 ? x : z);
            return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
        }
    }

    /// <summary>
    /// 4D simplex noise (Gustavson's simplex, 5-corner interpolation). Used by the experimental
    /// Anomaly biome: the density field is sampled on a curved slice through 4D space (the w
    /// coordinate follows y with a slow fold), producing folded, weaving structures that plain
    /// 3D noise cannot express. Output is roughly [-1, 1].
    /// </summary>
    public sealed class SimplexNoise4D
    {
        private readonly short[] _perm = new short[512];
        private readonly double _offsetX;
        private readonly double _offsetY;
        private readonly double _offsetZ;
        private readonly double _offsetW;

        public SimplexNoise4D(Random rand)
        {
            var p = new short[256];
            for (int i = 0; i < 256; i++) p[i] = (short)i;
            for (int i = 0; i < 256; i++)
            {
                int j = rand.Next(256 - i) + i;
                (p[i], p[j]) = (p[j], p[i]);
            }
            for (int i = 0; i < 512; i++) _perm[i] = p[i & 255];
            _offsetX = rand.NextDouble() * 256.0;
            _offsetY = rand.NextDouble() * 256.0;
            _offsetZ = rand.NextDouble() * 256.0;
            _offsetW = rand.NextDouble() * 256.0;
        }

        public double Noise(double xin, double yin, double zin, double win)
        {
            xin += _offsetX;
            yin += _offsetY;
            zin += _offsetZ;
            win += _offsetW;

            // Skew/unskew factors for 4D.
            const double F4 = 0.30901699437494745; // (sqrt(5)-1)/4
            const double G4 = 0.1381966011250105;  // (5-sqrt(5))/20
            double s = (xin + yin + zin + win) * F4;
            int i = FastFloor(xin + s);
            int j = FastFloor(yin + s);
            int k = FastFloor(zin + s);
            int l = FastFloor(win + s);
            double t = (i + j + k + l) * G4;
            double x0 = xin - (i - t);
            double y0 = yin - (j - t);
            double z0 = zin - (k - t);
            double w0 = win - (l - t);

            // Rank the 4 coordinates; each bit marks a corner offset direction.
            int c = 0;
            if (x0 > y0) c = 0x20;
            if (x0 > z0) c |= 0x10;
            if (y0 > z0) c |= 0x08;
            if (x0 > w0) c |= 0x04;
            if (y0 > w0) c |= 0x02;
            if (z0 > w0) c |= 0x01;

            int i1 = (c & 0x20) == 0 ? 0 : 1, j1 = (c & 0x10) == 0 ? 0 : 1, k1 = (c & 0x08) == 0 ? 0 : 1, l1 = (c & 0x04) == 0 ? 0 : 1;
            int i2 = (c & 0x20) == 0 ? 0 : 1, j2 = (c & 0x10) == 0 ? 0 : 1, k2 = (c & 0x08) == 0 ? 0 : 1, l2 = (c & 0x02) == 0 ? 0 : 1;
            int i3 = (c & 0x20) == 0 ? 0 : 1, j3 = (c & 0x10) == 0 ? 0 : 1, k3 = (c & 0x08) == 0 ? 0 : 1, l3 = (c & 0x01) == 0 ? 0 : 1;

            double x1 = x0 - i1 + G4, y1 = y0 - j1 + G4, z1 = z0 - k1 + G4, w1 = w0 - l1 + G4;
            double x2 = x0 - i2 + 2.0 * G4, y2 = y0 - j2 + 2.0 * G4, z2 = z0 - k2 + 2.0 * G4, w2 = w0 - l2 + 2.0 * G4;
            double x3 = x0 - i3 + 3.0 * G4, y3 = y0 - j3 + 3.0 * G4, z3 = z0 - k3 + 3.0 * G4, w3 = w0 - l3 + 3.0 * G4;
            double x4 = x0 - 1.0 + 4.0 * G4, y4 = y0 - 1.0 + 4.0 * G4, z4 = z0 - 1.0 + 4.0 * G4, w4 = w0 - 1.0 + 4.0 * G4;

            int ii = i & 255, jj = j & 255, kk = k & 255, ll = l & 255;

            double n0 = Corner(_perm[ii + _perm[jj + _perm[kk + _perm[ll]]]], x0, y0, z0, w0);
            double n1 = Corner(_perm[ii + i1 + _perm[jj + j1 + _perm[kk + k1 + _perm[ll + l1]]]], x1, y1, z1, w1);
            double n2 = Corner(_perm[ii + i2 + _perm[jj + j2 + _perm[kk + k2 + _perm[ll + l2]]]], x2, y2, z2, w2);
            double n3 = Corner(_perm[ii + i3 + _perm[jj + j3 + _perm[kk + k3 + _perm[ll + l3]]]], x3, y3, z3, w3);
            double n4 = Corner(_perm[ii + 1 + _perm[jj + 1 + _perm[kk + 1 + _perm[ll + 1]]]], x4, y4, z4, w4);
            return 27.0 * (n0 + n1 + n2 + n3 + n4);
        }

        private static double Corner(int hash, double x, double y, double z, double w)
        {
            double t = 0.6 - x * x - y * y - z * z - w * w;
            if (t < 0.0) return 0.0;
            t *= t;
            return t * t * Grad4(hash, x, y, z, w);
        }

        private static double Grad4(int hash, double x, double y, double z, double w)
        {
            int h = hash & 31;
            double u = h < 24 ? x : y;
            double v = h < 16 ? y : z;
            double s = h < 8 ? z : w;
            return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v) + ((h & 4) == 0 ? s : -s);
        }

        private static int FastFloor(double x) => x >= 0 ? (int)x : (int)x - 1;
    }

    /// <summary>
    /// Layered octave noise (fractal Brownian motion with a low-frequency-dominant weighting):
    /// octave i samples at coord / 2^(startIndex+i) and accumulates noise * 2^(startIndex+i), so
    /// the LOW-frequency octaves dominate. The caller passes coord already scaled by the generator's
    /// base frequency; startIndex selects which octave range to use, letting callers skip a
    /// negligible high-frequency tail.
    /// </summary>
    public sealed class NoiseOctaves
    {
        private readonly SimplexNoise[] _octaves;
        private readonly double[] _invFreq;
        private readonly double[] _weight;

        public NoiseOctaves(Random rand, int octaveCount, int startIndex)
        {
            _octaves = new SimplexNoise[octaveCount];
            _invFreq = new double[octaveCount];
            _weight = new double[octaveCount];
            for (int i = 0; i < octaveCount; i++)
            {
                _octaves[i] = new SimplexNoise(rand);
                double f = Math.Pow(2.0, startIndex + i);
                _invFreq[i] = 1.0 / f;
                _weight[i] = f;
                _weightSum += f;
            }
        }

        public double Noise2D(double x, double z)
        {
            double sum = 0.0;
            for (int i = 0; i < _octaves.Length; i++)
                sum += _octaves[i].Noise(x * _invFreq[i], z * _invFreq[i], 0.0) * _weight[i];
            return sum;
        }

        public double Noise3D(double x, double y, double z)
        {
            double sum = 0.0;
            for (int i = 0; i < _octaves.Length; i++)
                sum += _octaves[i].Noise(x * _invFreq[i], y * _invFreq[i], z * _invFreq[i]) * _weight[i];
            return sum;
        }

        /// <summary>Normalized 2D noise: divides the octave accumulation by the total weight sum,
        /// so the result is approximately -1..1 (like standard FBM). The raw accumulation grows
        /// large because low-frequency octaves dominate; callers who want a bounded field (veins,
        /// features, spawn logic) should use this.</summary>
        public double Noise2DNormalized(double x, double z) => Noise2D(x, z) / _weightSum;

        /// <summary>Normalized 3D noise, see <see cref="Noise2DNormalized"/>.</summary>
        public double Noise3DNormalized(double x, double y, double z) => Noise3D(x, y, z) / _weightSum;

        private readonly double _weightSum;
    }

    /// <summary>
    /// Layered 4D octave noise (same low-frequency-dominant FBM weighting as NoiseOctaves, but
    /// every sample is 4D simplex). The Anomaly biome samples this on a curved slice through
    /// 4D space, which folds and weaves the density field in ways 3D noise cannot.
    /// </summary>
    public sealed class NoiseOctaves4D
    {
        private readonly SimplexNoise4D[] _octaves;
        private readonly double[] _invFreq;
        private readonly double[] _weight;
        private readonly double _weightSum;

        public NoiseOctaves4D(Random rand, int octaveCount, int startIndex)
        {
            _octaves = new SimplexNoise4D[octaveCount];
            _invFreq = new double[octaveCount];
            _weight = new double[octaveCount];
            for (int i = 0; i < octaveCount; i++)
            {
                _octaves[i] = new SimplexNoise4D(rand);
                double f = Math.Pow(2.0, startIndex + i);
                _invFreq[i] = 1.0 / f;
                _weight[i] = f;
                _weightSum += f;
            }
        }

        public double Noise4D(double x, double y, double z, double w)
        {
            double sum = 0.0;
            for (int i = 0; i < _octaves.Length; i++)
                sum += _octaves[i].Noise(x * _invFreq[i], y * _invFreq[i], z * _invFreq[i], w * _invFreq[i]) * _weight[i];
            return sum;
        }

        /// <summary>Normalized 4D noise, see <see cref="NoiseOctaves.Noise2DNormalized"/>.</summary>
        public double Noise4DNormalized(double x, double y, double z, double w) => Noise4D(x, y, z, w) / _weightSum;
    }
}
