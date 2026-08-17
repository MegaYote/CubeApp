using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using Veldrid;
using Veldrid.SPIRV;

namespace Cubuild.Renderer
{
    public sealed partial class VeldridRenderer : IRenderer, IDisposable
    {
        private void UpdateFog()
        {
            ComputeNightFactors();

            float fogEnd = _fogEnd;
            // 25% of fogEnd: the fog start. Cave walls are within a few blocks, so
            // the fog factor there is ~0.96-1.0 (clear). Fog only ramps up toward the horizon.
            float fogStart = fogEnd * 0.25f;

            // Fog color: Cubuild fog color 0xC0D8FF (192,216,255) dimmed by the celestial
            // angle - PALER/whiter than the deep sky blue, so the horizon reads lighter (like MC's
            // fog vs sky). The world fades into this color, so distant terrain becomes hazy-white.
            float fogR = (192f / 255f) * _nightSkyDim;
            float fogG = (216f / 255f) * _nightSkyDim;
            float fogB = 1f * _nightSkyDim;

            _fogParams[0] = fogR;
            _fogParams[1] = fogG;
            _fogParams[2] = fogB;
            _fogParams[3] = 1f;
            _fogParams[4] = fogStart;
            _fogParams[5] = fogEnd;
            if (_cameraPosition.HasValue)
            {
                // std140: vec4 cameraPos must start at a 16-byte boundary -> floats 8-11.
                _fogParams[8] = (float)_cameraPosition.Value.X;
                _fogParams[9] = (float)_cameraPosition.Value.Y;
                _fogParams[10] = (float)_cameraPosition.Value.Z;
            }
            _fogParams[11] = 1f;

            // Hidden mining cell: the world shaders discard fragments inside this cell so the
            // shrinking-block overlay shows through while a block is being mined.
            // std140: vec4 hiddenCell -> floats 12-15.
            if (_hud.MiningProgress > 0f && _hud.MiningBlockId > 0)
            {
                _fogParams[12] = _hud.MiningBlockPos.X;
                _fogParams[13] = _hud.MiningBlockPos.Y;
                _fogParams[14] = _hud.MiningBlockPos.Z;
                _fogParams[15] = 1f;
            }
            else
            {
                _fogParams[15] = 0f;
            }
            _gd.UpdateBuffer(_fogBuffer, 0, _fogParams);

            // Sky fog uses the SAME range + color so the horizon blends with terrain. (The sky
            // planes sit at +-16 blocks; the range is in world distance, matching the world fog.)
            _skyFogParams[0] = fogR;
            _skyFogParams[1] = fogG;
            _skyFogParams[2] = fogB;
            _skyFogParams[3] = 1f;
            _skyFogParams[4] = fogStart;
            _skyFogParams[5] = fogEnd;
            _skyFogParams[8] = _fogParams[8];
            _skyFogParams[9] = _fogParams[9];
            _skyFogParams[10] = _fogParams[10];
            _skyFogParams[11] = 1f;
            // Sky gradient: OVERHEAD is a bright, saturated sky blue; the
            // HORIZON (floats 0-2) is the paler fog color so the world fades into it seamlessly;
            // BELOW the horizon is the darkened undersky. The key difference from the broken
            // version: skyTop is the DEEPER sky color, NOT the fog color - so the sky has a real
            // gradient from vivid blue overhead down to pale at the horizon.
            _skyFogParams[12] = 136f / 255f * _nightSkyDim;  // 0x88BBFF deep sky blue
            _skyFogParams[13] = 187f / 255f * _nightSkyDim;
            _skyFogParams[14] = 1f * _nightSkyDim;
            _skyFogParams[15] = 1f;
            _skyFogParams[16] = fogR * 0.2f + 0.04f;
            _skyFogParams[17] = fogG * 0.2f + 0.04f;
            _skyFogParams[18] = fogB * 0.6f + 0.1f;
            _skyFogParams[19] = 1f;
            _gd.UpdateBuffer(_skyFogBuffer, 0, _skyFogParams);
        }

        // Day/night dimming. The world light multiplier follows the night-dim level (0..11, how
        // much daylight is cut at night); the fog color and sky gradient use the sky-brightness
        // cosine factor (1 at noon -> 0 at midnight).
        private void ComputeNightFactors()
        {
            long t = _hud.WorldTime % 24000;
            float ang = (t) / 24000.0f - 0.25f;
            if (ang < 0f) ang += 1f;
            if (ang > 1f) ang -= 1f;
            float raw = ang;
            float eased = 1f - (float)((Math.Cos(ang * Math.PI) + 1.0) / 2.0);
            ang = raw + (eased - raw) / 3f;

            // Sky-brightness cosine factor (1 at noon -> 0 at midnight).
            float sky = (float)(Math.Cos(ang * Math.PI * 2.0) * 2.0 + 0.5);
            if (sky < 0f) sky = 0f;
            if (sky > 1f) sky = 1f;
            _nightSkyDim = sky;

            // World light: scale the baked brightness (which assumed full daylight 15) by the ratio
            // of brightness(15 - nightDim) / brightness(15), using the same curve as the mesher.
            float sub = 1f - (float)(Math.Cos(ang * Math.PI * 2.0) * 2.0 + 0.5);
            if (sub < 0f) sub = 0f;
            if (sub > 1f) sub = 1f;
            int dimmed = (int)(sub * 11f);
            float full = ChunkLighting.Brightness(15);
            float night = ChunkLighting.Brightness(Math.Max(0, 15 - dimmed));
            _nightDim = full > 1e-5f ? night / full : 0.12f;
            if (_nightDim < 0.05f) _nightDim = 0.05f;

            // Sky gradient base colors: sky base * sky-brightness factor, which reaches 0 at
            // midnight -> the night sky is genuinely black (no floor).
            _nightSkyR = (136f / 255f) * sky;
            _nightSkyG = (187f / 255f) * sky;
            _nightSkyB = 1f * sky;
        }

        // Renders the sky: two giant planes in CAMERA SPACE - the TOP plane sits 16 blocks
        // above the eye, the BOTTOM plane 16 below. The vertical gradient (bright overhead, fog
        // color at the horizon, darkened undersky) is computed PER-FRAGMENT in the shader from
        // vWorldPos.y (the vertex-color varying misreads on this pipeline). The vertices are
        // CAMERA-space (relative to the eye, spanning the far plane in every direction),
        // transformed by a ROTATION-ONLY view-projection, so the sky is structurally locked to the
        // camera and can never drift as the player walks.
        private void DrawSky(CommandList cl)
        {
            if (_skyPipeline == null) return;

            // Sky fog/gradient params are set once per frame in UpdateFog. Extent large enough to
            // cover the far plane from any camera yaw (a 64-step grid out to +-384,
            // we use the same scale).
            float extent = Math.Max(_farPlane * 2f, 768f);

            // 8 vertices in CAMERA space (eye at origin): top quad at y=+16 (verts 0-3), bottom
            // quad at y=-16 (verts 4-7). pos(3) + color(4). Reused buffer (no per-frame alloc).
            var v = _skyVertexScratch;
            if (v.Length < 56) v = _skyVertexScratch = new float[56];
            float skyR = _nightSkyR, skyG = _nightSkyG, skyB = _nightSkyB;
            SetSkyVertex(v, 0, -extent, 16f, -extent, skyR, skyG, skyB);
            SetSkyVertex(v, 1, extent, 16f, -extent, skyR, skyG, skyB);
            SetSkyVertex(v, 2, extent, 16f, extent, skyR, skyG, skyB);
            SetSkyVertex(v, 3, -extent, 16f, extent, skyR, skyG, skyB);
            // Bottom verts: vertex colors are unused by the shader (gradient is per-fragment), so
            // reuse the sky color - it only needs to be a valid vec4 for the layout.
            SetSkyVertex(v, 4, -extent, -16f, -extent, skyR, skyG, skyB);
            SetSkyVertex(v, 5, extent, -16f, -extent, skyR, skyG, skyB);
            SetSkyVertex(v, 6, extent, -16f, extent, skyR, skyG, skyB);
            SetSkyVertex(v, 7, -extent, -16f, extent, skyR, skyG, skyB);

            _gd.UpdateBuffer(_skyVertexBuffer, 0, v);

            cl.SetPipeline(_skyPipeline);
            cl.SetGraphicsResourceSet(0, _skyMatrixSet);
            cl.SetGraphicsResourceSet(1, _skyFogSet);
            cl.SetVertexBuffer(0, _skyVertexBuffer);
            cl.SetIndexBuffer(_skyIndexBuffer, IndexFormat.UInt16);
            cl.DrawIndexed(12, 1, 0, 0, 0);

            // Sun, moon, galaxies and stars, glued to the sky's rotation
            // Order mirrors CubuildC++: stars, galaxies, then sun/moon
            // on top.
            DrawStars(cl);
            DrawGalaxies(cl);
            DrawCelestialBodies(cl);
        }

        private static void SetSkyVertex(float[] v, int index, float x, float y, float z, float r, float g, float b)
        {
            int o = index * 7;
            v[o] = x;
            v[o + 1] = y;
            v[o + 2] = z;
            v[o + 3] = r;
            v[o + 4] = g;
            v[o + 5] = b;
            v[o + 6] = 1f;
        }

        // Sun + moon: textured quads rotated by the celestial
        // angle around X, additive blend, drawn on the same camera-space sky matrix. Sun is 30
        // half-size at y=+100; moon is 20 half-size at y=-100 with its UVs flipped horizontally.
        private void DrawCelestialBodies(CommandList cl)
        {
            if (_celestialPipeline == null || _sunTextureSet == null || _moonTextureSet == null) return;

            float angle = ComputeNightCelestialAngle() * MathF.PI * 2.0f;
            float cosA = MathF.Cos(angle), sinA = MathF.Sin(angle);

            // One vertex buffer holds BOTH quads: sun at verts 0-3 (y=+100, size 30), moon at
            // verts 4-7 (y=-100, size 20, UVs flipped). A single UpdateBuffer writes them
            // together; each draw then reads only its own vertex range via the index buffer
            // ({0,1,2,0,2,3} = sun, {4,5,6,4,6,7} = moon). Drawing the moon at index start 6
            // with the SAME offset-0 data was the bug: both draws used verts 0-3, so the moon
            // always rendered on top of the sun regardless of the y position given.
            var v = _celestialVertexScratch;
            if (v.Length < 40) v = _celestialVertexScratch = new float[40];
            WriteCelestialQuad(v, 0, cosA, sinA, 100f, 15f, 0f, 0f, 1f, 1f);   // sun
            WriteCelestialQuad(v, 4, cosA, sinA, -100f, 10f, 1f, 0f, 0f, 1f);  // moon
            _gd.UpdateBuffer(_celestialVertexBuffer, 0, v);

            cl.SetPipeline(_celestialPipeline);
            cl.SetGraphicsResourceSet(0, _skyMatrixSet);
            cl.SetVertexBuffer(0, _celestialVertexBuffer);
            cl.SetIndexBuffer(_celestialIndexBuffer, IndexFormat.UInt16);

            // Sun quad (indices 0..5 -> vertices 0-3).
            cl.SetGraphicsResourceSet(1, _sunTextureSet);
            cl.DrawIndexed(6, 1, 0, 0, 0);

            // Moon quad (indices 6..11 -> vertices 4-7).
            cl.SetGraphicsResourceSet(1, _moonTextureSet);
            cl.DrawIndexed(6, 1, 6, 0, 0);
        }

        // Writes a camera-facing (billboard) quad for the sun/moon, positioned far out along the
        // celestial arc so it orbits the sky as the angle changes. This matches how MC actually
        // draws them: a flat sprite always facing the player, riding the sun/moon path.
        private static void WriteCelestialQuad(float[] v, int index, float cosA, float sinA,
            float centerY, float size, float u0, float v0, float u1, float v1)
        {
            // The sun/moon are FIXED horizontal XZ quads at
            // y=+100 (sun) / y=-100 (moon), and the celestial angle is a modelview rotation about
            // the X axis. Rotating a horizontal quad around X keeps its normal pointing along the
            // camera->body ray, so the quad always faces the camera at radius ~100 - no billboard,
            // no z=0 degeneracy at noon, and well inside the near/far planes. Mirror that here by
            // rotating each fixed corner (x, centerY, z) around X by the same angle.
            (float x, float z)[] corners =
            {
                (-size, -size),
                ( size, -size),
                ( size,  size),
                (-size,  size),
            };
            for (int c = 0; c < 4; c++)
            {
                int o = (index + c) * 5;
                float x = corners[c].x;
                float z = corners[c].z;
                // Rotate (x, centerY, z) around X: x'=x, y'=y*cosA - z*sinA, z'=y*sinA + z*cosA
                v[o] = x;
                v[o + 1] = centerY * cosA - z * sinA;
                v[o + 2] = centerY * sinA + z * cosA;
                v[o + 3] = (c == 0 || c == 3) ? u0 : u1;
                v[o + 4] = (c == 0 || c == 1) ? v0 : v1;
            }
        }

        // Starfield: a field of small quads on the unit sphere (built once), drawn with
        // alpha = getStarBrightness. Rotated by the celestial angle so stars rise/set with the sky.
        private void DrawStars(CommandList cl)
        {
            if (_starPipeline == null) return;

            float starBrightness = GetStarBrightness();
            if (starBrightness <= 0.001f) return;

            if (!_starsBuilt)
            {
                BuildStars();
                _starsBuilt = true;
            }
            if (_starVertexCount == 0) return;

            // Rotate the prebuilt unit-sphere quads by the celestial angle around X. Always read
            // from the PRISTINE base copy and write into the scratch, otherwise the rotation
            // accumulates frame over frame (rotating already-rotated positions) and the stars
            // spin wildly faster than the sun/moon.
            float angle = ComputeNightCelestialAngle() * MathF.PI * 2.0f;
            float cosA = MathF.Cos(angle), sinA = MathF.Sin(angle);
            for (int i = 0; i < _starVertexCount; i++)
            {
                int o = i * 7;
                float x = _starBaseScratch[o];
                float y = _starBaseScratch[o + 1];
                float z = _starBaseScratch[o + 2];
                float ry = y * cosA - z * sinA;
                float rz = y * sinA + z * cosA;
                _starVertexScratch[o] = x;
                _starVertexScratch[o + 1] = ry;
                _starVertexScratch[o + 2] = rz;
                // Alpha rides star brightness.
                _starVertexScratch[o + 6] = starBrightness;
            }
            _gd.UpdateBuffer(_starVertexBuffer, 0, _starVertexScratch);

            cl.SetPipeline(_starPipeline);
            cl.SetGraphicsResourceSet(0, _skyMatrixSet);
            cl.SetVertexBuffer(0, _starVertexBuffer);
            cl.SetIndexBuffer(_starIndexBuffer, IndexFormat.UInt16);
            cl.DrawIndexed((uint)_starVertexCount, 1, 0, 0, 0);
        }

        // Builds the starfield: 800 quads on the unit sphere.
        private void BuildStars()
        {
            var rng = new Random(10842);
            _starVertexScratch = new float[800 * 4 * 7];
            _starIndexScratch = new ushort[800 * 6];
            int vf = 0, ii = 0;
            ushort baseV = 0;
            for (int i = 0; i < 800; i++)
            {
                // Random direction on unit sphere.
                double rx = rng.NextDouble() * 2.0 - 1.0;
                double ry = rng.NextDouble() * 2.0 - 1.0;
                double rz = rng.NextDouble() * 2.0 - 1.0;
                double len2 = rx * rx + ry * ry + rz * rz;
                if (len2 >= 1.0 || len2 < 0.01) { i--; continue; }
                double inv = 1.0 / Math.Sqrt(len2);
                rx *= inv; ry *= inv; rz *= inv;

                // Position far away, small random size.
                double px = rx * 100.0, py = ry * 100.0, pz = rz * 100.0;
                double sz = 0.25 + rng.NextDouble() * 0.25;

                // Orientation quads (star rotation approach).
                double a1 = Math.Atan2(rx, rz);
                double s1 = Math.Sin(a1), c1 = Math.Cos(a1);
                double a2 = Math.Atan2(Math.Sqrt(rx * rx + rz * rz), ry);
                double s2 = Math.Sin(a2), c2 = Math.Cos(a2);
                double a3 = rng.NextDouble() * 2.0 * Math.PI;
                double s3 = Math.Sin(a3), c3 = Math.Cos(a3);

                for (int vert = 0; vert < 4; vert++)
                {
                    double vx = ((vert & 2) - 1) * sz;
                    double vy = (((vert + 1) & 2) - 1) * sz;
                    double rx1 = vx * c3 - vy * s3;
                    double ry1 = vy * c3 + vx * s3;
                    double rx2 = rx1 * s2;
                    double ry2 = -rx1 * c2;
                    double rz2 = ry1;
                    double fx = ry2 * s1 - rz2 * c1;
                    double fz = rz2 * s1 + ry2 * c1;

                    int o = vf;
                    _starVertexScratch[o] = (float)(px + fx);
                    _starVertexScratch[o + 1] = (float)(py + rx2);
                    _starVertexScratch[o + 2] = (float)(pz + fz);
                    _starVertexScratch[o + 3] = 1f; // r
                    _starVertexScratch[o + 4] = 1f; // g
                    _starVertexScratch[o + 5] = 1f; // b
                    _starVertexScratch[o + 6] = 1f; // alpha (set per frame)
                    vf += 7;
                }
                _starIndexScratch[ii++] = baseV; _starIndexScratch[ii++] = (ushort)(baseV + 1); _starIndexScratch[ii++] = (ushort)(baseV + 2);
                _starIndexScratch[ii++] = baseV; _starIndexScratch[ii++] = (ushort)(baseV + 2); _starIndexScratch[ii++] = (ushort)(baseV + 3);
                baseV += 4;
            }
            _starVertexCount = vf / 7;
            // Pristine copy WITHOUT the celestial rotation: DrawStars rotates from this base into
            // the scratch each frame, so rotation never accumulates.
            _starBaseScratch = (float[])_starVertexScratch.Clone();

            _starVertexBuffer = _gd.ResourceFactory.CreateBuffer(new BufferDescription(
                (uint)(_starVertexScratch.Length * sizeof(float)), BufferUsage.VertexBuffer | BufferUsage.Dynamic));
            _starIndexBuffer = _gd.ResourceFactory.CreateBuffer(new BufferDescription(
                (uint)(_starIndexScratch.Length * sizeof(ushort)), BufferUsage.IndexBuffer));
            _gd.UpdateBuffer(_starIndexBuffer, 0, _starIndexScratch);
        }

        // Draws the seeded galaxy field: 4 spiral galaxies (1 main + 3 random) built once per
        // world seed. Each particle is a FIXED tangent-plane quad on the celestial sphere (like
        // stars), rotated by the celestial angle each frame so galaxies wheel with the sky.
        private void DrawGalaxies(CommandList cl)
        {
            if (_galaxyPipeline == null) return;

            float starBrightness = GetStarBrightness();
            if (starBrightness <= 0.001f) return;

            // Same fade as stars: full night = visible, brightens away at dawn/dusk.
            float galaxyAlpha = 1.0f;
            if (starBrightness < 0.1f)
            {
                galaxyAlpha = 1.0f;
            }
            else if (starBrightness < 0.35f)
            {
                float fadeProgress = (starBrightness - 0.1f) / 0.25f;
                galaxyAlpha = 1.0f - fadeProgress;
            }
            if (galaxyAlpha <= 0.001f) return;

            if (!_galaxiesBuilt || _galaxySeed != _cloudSeed)
            {
                BuildGalaxies();
                _galaxiesBuilt = true;
                _galaxySeed = _cloudSeed;
            }
            if (_galaxies.Count == 0 || _galaxyVertexCount == 0) return;

            // Rotate the prebuilt sphere quads by the celestial angle around X, exactly like the
            // star field. Read from the pristine base, write into the scratch (no accumulation).
            float angle = ComputeNightCelestialAngle() * MathF.PI * 2.0f;
            float cosA = MathF.Cos(angle), sinA = MathF.Sin(angle);
            for (int i = 0; i < _galaxyVertexCount; i++)
            {
                int o = i * 7;
                float x = _galaxyBaseScratch[o];
                float y = _galaxyBaseScratch[o + 1];
                float z = _galaxyBaseScratch[o + 2];
                float ry = y * cosA - z * sinA;
                float rz = y * sinA + z * cosA;
                _galaxyVertexScratch[o] = x;
                _galaxyVertexScratch[o + 1] = ry;
                _galaxyVertexScratch[o + 2] = rz;
                // Alpha rides the night fade (base alpha baked at build time).
                _galaxyVertexScratch[o + 3] = _galaxyBaseScratch[o + 3];
                _galaxyVertexScratch[o + 4] = _galaxyBaseScratch[o + 4];
                _galaxyVertexScratch[o + 5] = _galaxyBaseScratch[o + 5];
                _galaxyVertexScratch[o + 6] = _galaxyBaseScratch[o + 6] * galaxyAlpha;
            }
            _gd.UpdateBuffer(_galaxyVertexBuffer, 0, _galaxyVertexScratch);

            cl.SetPipeline(_galaxyPipeline);
            cl.SetGraphicsResourceSet(0, _skyMatrixSet);
            cl.SetVertexBuffer(0, _galaxyVertexBuffer);
            cl.SetIndexBuffer(_galaxyIndexBuffer, IndexFormat.UInt16);
            cl.DrawIndexed((uint)_galaxyVertexCount, 1, 0, 0, 0);
        }

        // Builds the seeded galaxy field. Mirrors CubuildC++ SkyRenderer::initializeGalaxy:
        // 1 main galaxy (elongated edge-on spiral near the moon's arc) + 3 random galaxies
        // (face-on/edge-on/tilted variety) spread across the night-sky hemisphere. Unlike the
        // C++ billboard quads, each particle here is a fixed quad tangent to the celestial
        // sphere at its position (the same orientation used by the star field), so the
        // whole galaxy stays anchored to the sky and rotates with the celestial angle.
        private void BuildGalaxies()
        {
            var rng = new Random(_cloudSeed);

            _galaxies.Clear();
            int numGalaxies = 4;
            for (int g = 0; g < numGalaxies; g++)
            {
                var galaxy = new GalaxyDef { Particles = new List<GalaxyParticleDef>() };
                bool isMainGalaxy = g == 0;

                if (isMainGalaxy)
                {
                    // Main galaxy - consistent elongated shape (edge-on spiral).
                    galaxy.SizeMultiplier = 1.0f;
                    galaxy.ElongationX = 3.0f;
                    galaxy.ElongationY = 0.4f;
                    galaxy.Rotation = 0.5f;
                    galaxy.SpiralTightness = 2.5f;
                    galaxy.NumArms = 2;
                    galaxy.BasePosition = Vector3.Normalize(new Vector3(0.5f, -0.7f, 0.3f));
                }
                else
                {
                    galaxy.SizeMultiplier = 0.2f + (float)rng.NextDouble() * 0.5f; // 0.2-0.7x

                    // Random shape: face-on / edge-on / tilted.
                    float shapeType = (float)rng.NextDouble();
                    if (shapeType < 0.3f)
                    {
                        galaxy.ElongationX = 0.8f + (float)rng.NextDouble() * 0.4f; // nearly circular
                        galaxy.ElongationY = 0.8f + (float)rng.NextDouble() * 0.4f;
                    }
                    else if (shapeType < 0.7f)
                    {
                        galaxy.ElongationX = 2.0f + (float)rng.NextDouble() * 2.5f; // very stretched
                        galaxy.ElongationY = 0.2f + (float)rng.NextDouble() * 0.3f; // very flat
                    }
                    else
                    {
                        galaxy.ElongationX = 1.2f + (float)rng.NextDouble() * 1.5f;
                        galaxy.ElongationY = 0.5f + (float)rng.NextDouble() * 0.5f;
                    }

                    galaxy.Rotation = (float)rng.NextDouble() * MathF.PI;
                    galaxy.SpiralTightness = 1.5f + (float)rng.NextDouble() * 2.5f;
                    galaxy.NumArms = 2 + rng.Next(3); // 2, 3, or 4 arms

                    // Grid-based position spread across the lower (night) hemisphere.
                    int gridIndex = g - 1; // 0, 1, or 2
                    float baseTheta = (gridIndex * 2.0f * MathF.PI / 3.0f) + (float)rng.NextDouble() * (2.0f * MathF.PI / 3.0f);
                    float basePhi = (MathF.PI / 2.0f) + (float)rng.NextDouble() * (MathF.PI / 2.5f);
                    galaxy.BasePosition = Vector3.Normalize(new Vector3(
                        MathF.Sin(basePhi) * MathF.Cos(baseTheta),
                        MathF.Cos(basePhi), // negative = night sky
                        MathF.Sin(basePhi) * MathF.Sin(baseTheta)));
                }

                int particlesPerArm = isMainGalaxy ? 150 : (40 + rng.Next(60));

                for (int arm = 0; arm < galaxy.NumArms; arm++)
                {
                    float armAngle = (arm / (float)galaxy.NumArms) * 2.0f * MathF.PI;
                    for (int i = 0; i < particlesPerArm; i++)
                    {
                        float t = i / (float)particlesPerArm;
                        float radius = t * 80.0f * galaxy.SizeMultiplier;
                        float angle = armAngle + t * galaxy.SpiralTightness * 2.0f * MathF.PI;
                        float randomRadius = ((float)rng.NextDouble() - 0.5f) * 15.0f * galaxy.SizeMultiplier;
                        float randomAngle = ((float)rng.NextDouble() - 0.5f) * 0.5f;

                        galaxy.Particles.Add(new GalaxyParticleDef
                        {
                            Offset = new Vector3(
                                (radius + randomRadius) * MathF.Cos(angle + randomAngle) * galaxy.ElongationX,
                                (radius + randomRadius) * MathF.Sin(angle + randomAngle) * galaxy.ElongationY,
                                ((float)rng.NextDouble() - 0.5f) * 8.0f * galaxy.SizeMultiplier),
                            Alpha = 0.15f + (1.0f - t) * 0.25f,
                            Size = 1.5f + (float)rng.NextDouble() * 1.5f,
                        });
                    }
                }

                // Dense core.
                int coreParticles = isMainGalaxy ? 80 : (20 + rng.Next(40));
                for (int i = 0; i < coreParticles; i++)
                {
                    float angle = (float)rng.NextDouble() * 2.0f * MathF.PI;
                    float radius = (float)rng.NextDouble() * 15.0f * galaxy.SizeMultiplier;
                    galaxy.Particles.Add(new GalaxyParticleDef
                    {
                        Offset = new Vector3(
                            radius * MathF.Cos(angle) * galaxy.ElongationX * 0.8f,
                            radius * MathF.Sin(angle) * galaxy.ElongationY * 0.8f,
                            ((float)rng.NextDouble() - 0.5f) * 5.0f * galaxy.SizeMultiplier),
                        Alpha = 0.3f + (float)rng.NextDouble() * 0.3f,
                        Size = 2.0f + (float)rng.NextDouble() * 2.0f,
                    });
                }

                _galaxies.Add(galaxy);
            }

            // Allocate the scratch + base + index buffers for the total particle count.
            int totalParticles = 0;
            foreach (var galaxy in _galaxies) totalParticles += galaxy.Particles.Count;
            _galaxyVertexScratch = new float[totalParticles * 4 * 7];
            _galaxyBaseScratch = new float[totalParticles * 4 * 7];
            _galaxyIndexScratch = new ushort[totalParticles * 6];

            // Build each galaxy's local frame ONCE (in unrotated sky space) so the particle
            // quads can be placed tangent to the celestial sphere, anchored to the sky.
            int vf = 0;
            int ii = 0;
            ushort baseV = 0;
            foreach (var galaxy in _galaxies)
            {
                var dir = Vector3.Normalize(galaxy.BasePosition);

                // Tangent frame at the galaxy center (independent of the camera).
                var refUp = Math.Abs(dir.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
                var galaxyRight = Vector3.Normalize(Vector3.Cross(dir, refUp));
                var galaxyUp = Vector3.Cross(dir, galaxyRight);
                float cosR = MathF.Cos(galaxy.Rotation), sinR = MathF.Sin(galaxy.Rotation);
                var gRight = galaxyRight * cosR + galaxyUp * sinR;
                var gUp = -galaxyRight * sinR + galaxyUp * cosR;

                foreach (var particle in galaxy.Particles)
                {
                    // Particle's position in sky space (unrotated). Offsets are the C++ spiral
                    // shape scaled from the 1000-unit shell down to GalaxyDistance.
                    var center = dir * GalaxyDistance
                        + gRight * (particle.Offset.X * GalaxyScale)
                        + gUp * (particle.Offset.Y * GalaxyScale)
                        + dir * (particle.Offset.Z * GalaxyScale);
                    var pd = Vector3.Normalize(center);
                    float sz = particle.Size * 1.5f * galaxy.SizeMultiplier * GalaxyScale;
                    float alpha = particle.Alpha * 0.6f * galaxy.SizeMultiplier;

                    // Star orientation: a small square around the particle's direction,
                    // tangent to the sphere. Same math as BuildStars.
                    double a1 = Math.Atan2(pd.X, pd.Z);
                    double s1 = Math.Sin(a1), c1 = Math.Cos(a1);
                    double a2 = Math.Atan2(Math.Sqrt(pd.X * pd.X + pd.Z * pd.Z), pd.Y);
                    double s2 = Math.Sin(a2), c2 = Math.Cos(a2);
                    double a3 = rng.NextDouble() * 2.0 * Math.PI;
                    double s3 = Math.Sin(a3), c3 = Math.Cos(a3);

                    for (int vert = 0; vert < 4; vert++)
                    {
                        double vx = ((vert & 2) - 1) * sz;
                        double vy = (((vert + 1) & 2) - 1) * sz;
                        double rx1 = vx * c3 - vy * s3;
                        double ry1 = vy * c3 + vx * s3;
                        double rx2 = rx1 * s2;
                        double ry2 = -rx1 * c2;
                        double rz2 = ry1;
                        double fx = ry2 * s1 - rz2 * c1;
                        double fz = rz2 * s1 + ry2 * c1;

                        int o = vf;
                        _galaxyBaseScratch[o] = (float)(center.X + fx);
                        _galaxyBaseScratch[o + 1] = (float)(center.Y + rx2);
                        _galaxyBaseScratch[o + 2] = (float)(center.Z + fz);
                        _galaxyBaseScratch[o + 3] = 0.8f;  // r
                        _galaxyBaseScratch[o + 4] = 0.85f; // g
                        _galaxyBaseScratch[o + 5] = 1.0f;  // b
                        _galaxyBaseScratch[o + 6] = alpha; // alpha (scaled per frame)
                        vf += 7;
                    }
                    _galaxyIndexScratch[ii++] = baseV; _galaxyIndexScratch[ii++] = (ushort)(baseV + 1); _galaxyIndexScratch[ii++] = (ushort)(baseV + 2);
                    _galaxyIndexScratch[ii++] = baseV; _galaxyIndexScratch[ii++] = (ushort)(baseV + 2); _galaxyIndexScratch[ii++] = (ushort)(baseV + 3);
                    baseV += 4;
                }
            }
            _galaxyVertexCount = totalParticles * 4;

            _galaxyVertexBuffer = _gd.ResourceFactory.CreateBuffer(new BufferDescription(
                (uint)(_galaxyVertexScratch.Length * sizeof(float)), BufferUsage.VertexBuffer | BufferUsage.Dynamic));
            _galaxyIndexBuffer = _gd.ResourceFactory.CreateBuffer(new BufferDescription(
                (uint)(_galaxyIndexScratch.Length * sizeof(ushort)), BufferUsage.IndexBuffer));
            _gd.UpdateBuffer(_galaxyIndexBuffer, 0, _galaxyIndexScratch);
        }

        // Star brightness: clamp(1 - (cos(cel*2pi)*2 + 0.75), 0, 1)^2 * 0.5.
        private float GetStarBrightness()
        {
            float cel = ComputeNightCelestialAngle();
            float v = 1f - (MathF.Cos(cel * MathF.PI * 2.0f) * 2.0f + 12f / 16f);
            if (v < 0f) v = 0f;
            if (v > 1f) v = 1f;
            return v * v * 0.5f;
        }

        // Returns the current celestial angle (0.0 = dawn ... 1.0 wraps) using the last HUD time.
        private float ComputeNightCelestialAngle()
        {
            long t = _hud.WorldTime % 24000;
            float ang = t / 24000.0f - 0.25f;
            if (ang < 0f) ang += 1f;
            if (ang > 1f) ang -= 1f;
            return ang;
        }

        // Issues one indirect world draw for a chunk-command pass (opaque or transparent) using the
        // given pipeline. Commands are frustum-culled this frame (on the CPU by default, or on the
        // GPU when F7 toggled); the indirect-args buffer contents are refreshed each frame since
        // the visible set changes with the camera.
    }
}