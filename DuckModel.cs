using System;
using System.Collections.Generic;

namespace CubeApp
{
    /// <summary>
    /// The duck test-mob model, ported from Cubuild's Blockbench (box-UV) model. Each cube element
    /// is baked, in its rest pose (element + bone rotations applied), into a flat textured mesh in
    /// local model space: the feet sit at y = 0 and the body is centred on x = 0, facing -Z.
    ///
    /// Coordinates in the source model are in texture pixels (16 px = 1 block) against a 64x64
    /// texture; <see cref="Scale"/> converts them to blocks and UVs are normalised to 0..1 with the
    /// texture's top row at v = 0 (matching how the PNG is uploaded to the GPU).
    /// </summary>
    public static class DuckModel
    {
        public const float Scale = 1f / 16f;
        private const float TextureSize = 64f;
        private const float MinThickness = 0.02f;

        // Cubuild's ENTITY_FACE_SHADE: east/west 0.60, top 1.00, bottom 0.50, north/south 0.80.
        private static readonly float ShadeSide = 0.60f;
        private static readonly float ShadeTop = 1.00f;
        private static readonly float ShadeBottom = 0.50f;
        private static readonly float ShadeFrontBack = 0.80f;

        public const string TextureResourceName = "duck.png";

        private readonly struct DuckCube
        {
            public readonly float[] From;
            public readonly float[] To;
            public readonly float[] Origin;
            public readonly float[] Rotation;      // element rotation (degrees, around Origin)
            public readonly float[] GroupOrigin;
            public readonly float[] GroupRotation;  // bone rotation (degrees, around GroupOrigin)
            // Face UV rects in pixels [u0,v0,u1,v1], in order: east, west, up, down, south, north.
            public readonly float[] Faces;

            public DuckCube(float[] from, float[] to, float[] origin, float[] rotation, float[] groupOrigin, float[] groupRotation, float[] faces)
            {
                From = from;
                To = to;
                Origin = origin;
                Rotation = rotation;
                GroupOrigin = groupOrigin;
                GroupRotation = groupRotation;
                Faces = faces;
            }
        }

        private static readonly DuckCube[] Cubes =
        {
            new DuckCube(new[]{ -4.50000f, 2.28000f, -6.68000f }, new[]{ 4.50000f, 10.28000f, 5.32000f }, new[]{ 0.00000f, 0.00000f, 1.00000f }, new[]{ 0.00000f, 0.00000f, 0.00000f }, new[]{ 0.00000f, 0.00000f, 0.00000f }, new[]{ 0.00000f, 0.00000f, 0.00000f }, // body
                new[]{ 0.00000f, 12.00000f, 12.00000f, 20.00000f, 21.00000f, 12.00000f, 33.00000f, 20.00000f, 21.00000f, 12.00000f, 12.00000f, 0.00000f, 30.00000f, 0.00000f, 21.00000f, 12.00000f, 33.00000f, 12.00000f, 42.00000f, 20.00000f, 12.00000f, 12.00000f, 21.00000f, 20.00000f }),
            new DuckCube(new[]{ -2.00000f, 10.30000f, -7.44000f }, new[]{ 2.00000f, 15.30000f, -2.44000f }, new[]{ 0.00000f, 10.00000f, -5.00000f }, new[]{ 0.00000f, 0.00000f, 0.00000f }, new[]{ 0.00000f, 14.72000f, -3.52000f }, new[]{ 0.00000f, 0.00000f, 0.00000f }, // head
                new[]{ 0.00000f, 40.00000f, 5.00000f, 45.00000f, 9.00000f, 40.00000f, 14.00000f, 45.00000f, 9.00000f, 40.00000f, 5.00000f, 35.00000f, 13.00000f, 35.00000f, 9.00000f, 40.00000f, 14.00000f, 40.00000f, 18.00000f, 45.00000f, 5.00000f, 40.00000f, 9.00000f, 45.00000f }),
            new DuckCube(new[]{ -2.00000f, 11.60000f, -11.34000f }, new[]{ 2.00000f, 12.60000f, -7.34000f }, new[]{ 0.00000f, -6.00000f, 2.00000f }, new[]{ 0.00000f, 0.00000f, 0.00000f }, new[]{ 0.00000f, 14.72000f, -3.52000f }, new[]{ 0.00000f, 0.00000f, 0.00000f }, // head (bill upper)
                new[]{ 18.00000f, 45.00000f, 22.00000f, 46.00000f, 26.00000f, 45.00000f, 30.00000f, 46.00000f, 26.00000f, 45.00000f, 22.00000f, 41.00000f, 30.00000f, 41.00000f, 26.00000f, 45.00000f, 30.00000f, 45.00000f, 34.00000f, 46.00000f, 22.00000f, 45.00000f, 26.00000f, 46.00000f }),
            new DuckCube(new[]{ -1.00000f, 10.84000f, -10.02000f }, new[]{ 1.00000f, 11.84000f, -4.02000f }, new[]{ 0.00000f, -6.00000f, 3.00000f }, new[]{ 0.00000f, 0.00000f, 0.00000f }, new[]{ 0.00000f, 14.72000f, -3.52000f }, new[]{ 0.00000f, 0.00000f, 0.00000f }, // head (bill lower)
                new[]{ 40.00000f, 32.00000f, 46.00000f, 33.00000f, 48.00000f, 32.00000f, 54.00000f, 33.00000f, 48.00000f, 32.00000f, 46.00000f, 26.00000f, 50.00000f, 26.00000f, 48.00000f, 32.00000f, 54.00000f, 32.00000f, 56.00000f, 33.00000f, 46.00000f, 32.00000f, 48.00000f, 33.00000f }),
            new DuckCube(new[]{ 4.29504f, 3.64190f, -5.86000f }, new[]{ 5.29504f, 9.64190f, 3.14000f }, new[]{ 4.00000f, 9.00000f, -1.00000f }, new[]{ 0.00000f, 0.00000f, 5.00000f }, new[]{ 6.50000f, 10.24000f, 0.64000f }, new[]{ 0.00000f, 0.00000f, 0.00000f }, // left_wing
                new[]{ 0.00000f, 29.00000f, 9.00000f, 35.00000f, 10.00000f, 29.00000f, 19.00000f, 35.00000f, 10.00000f, 29.00000f, 9.00000f, 20.00000f, 11.00000f, 20.00000f, 10.00000f, 29.00000f, 19.00000f, 29.00000f, 20.00000f, 35.00000f, 9.00000f, 29.00000f, 10.00000f, 35.00000f }),
            new DuckCube(new[]{ -5.29504f, 3.64190f, -5.86000f }, new[]{ -4.29504f, 9.64190f, 3.14000f }, new[]{ -4.00000f, 9.00000f, -1.00000f }, new[]{ 0.00000f, 0.00000f, -5.00000f }, new[]{ -6.50000f, 10.24000f, 0.64000f }, new[]{ 0.00000f, 0.00000f, 0.00000f }, // right_wing
                new[]{ 20.00000f, 29.00000f, 29.00000f, 35.00000f, 30.00000f, 29.00000f, 39.00000f, 35.00000f, 30.00000f, 29.00000f, 29.00000f, 20.00000f, 31.00000f, 20.00000f, 30.00000f, 29.00000f, 39.00000f, 29.00000f, 40.00000f, 35.00000f, 29.00000f, 29.00000f, 30.00000f, 35.00000f }),
            new DuckCube(new[]{ -2.50000f, 6.43601f, 4.44960f }, new[]{ 2.50000f, 8.43601f, 8.44960f }, new[]{ 0.00000f, 9.00000f, 5.00000f }, new[]{ -37.50000f, 0.00000f, 0.00000f }, new[]{ 0.00000f, 12.48000f, 7.60000f }, new[]{ 20.05000f, 0.00000f, 0.00000f }, // tail
                new[]{ 18.00000f, 39.00000f, 22.00000f, 41.00000f, 27.00000f, 39.00000f, 31.00000f, 41.00000f, 27.00000f, 39.00000f, 22.00000f, 35.00000f, 32.00000f, 35.00000f, 27.00000f, 39.00000f, 31.00000f, 39.00000f, 36.00000f, 41.00000f, 22.00000f, 39.00000f, 27.00000f, 41.00000f }),
            new DuckCube(new[]{ 1.22000f, 2.80000f, -1.00000f }, new[]{ 3.22000f, 2.80000f, 2.00000f }, new[]{ 2.00000f, 3.00000f, 2.00000f }, new[]{ -90.00000f, 0.00000f, 0.00000f }, new[]{ 2.72000f, 0.48000f, -0.80000f }, new[]{ 0.00000f, 0.00000f, 0.00000f }, // left_leg
                new[]{ 34.00000f, 44.00000f, 37.00000f, 44.00000f, 39.00000f, 44.00000f, 42.00000f, 44.00000f, 39.00000f, 44.00000f, 37.00000f, 41.00000f, 41.00000f, 41.00000f, 39.00000f, 44.00000f, 42.00000f, 44.00000f, 44.00000f, 44.00000f, 37.00000f, 44.00000f, 39.00000f, 44.00000f }),
            new DuckCube(new[]{ -3.78000f, -0.00100f, -3.80100f }, new[]{ -0.78000f, -0.00100f, 2.19900f }, new[]{ -1.88000f, 0.01900f, 1.99900f }, new[]{ 0.00000f, 0.00000f, 0.00000f }, new[]{ -2.72000f, 0.48000f, -0.80000f }, new[]{ 0.00000f, 0.00000f, 0.00000f }, // right_foot
                new[]{ 36.00000f, 41.00000f, 42.00000f, 41.00000f, 45.00000f, 41.00000f, 51.00000f, 41.00000f, 45.00000f, 41.00000f, 42.00000f, 35.00000f, 48.00000f, 35.00000f, 45.00000f, 41.00000f, 51.00000f, 41.00000f, 54.00000f, 41.00000f, 42.00000f, 41.00000f, 45.00000f, 41.00000f }),
            new DuckCube(new[]{ 1.22000f, -0.00100f, -3.80100f }, new[]{ 4.22000f, -0.00100f, 2.19900f }, new[]{ 2.00000f, 0.01900f, 1.99900f }, new[]{ 0.00000f, 0.00000f, 0.00000f }, new[]{ 2.72000f, 0.48000f, -0.80000f }, new[]{ 0.00000f, 0.00000f, 0.00000f }, // left_foot
                new[]{ 40.00000f, 26.00000f, 46.00000f, 26.00000f, 49.00000f, 26.00000f, 55.00000f, 26.00000f, 49.00000f, 26.00000f, 46.00000f, 20.00000f, 52.00000f, 20.00000f, 49.00000f, 26.00000f, 55.00000f, 26.00000f, 58.00000f, 26.00000f, 46.00000f, 26.00000f, 49.00000f, 26.00000f }),
            new DuckCube(new[]{ -2.78000f, 2.80000f, -1.00000f }, new[]{ -0.78000f, 2.80000f, 2.00000f }, new[]{ -2.00000f, 3.00000f, 2.00000f }, new[]{ -90.00000f, 0.00000f, 0.00000f }, new[]{ -2.72000f, 0.48000f, -0.80000f }, new[]{ 0.00000f, 0.00000f, 0.00000f }, // right_leg
                new[]{ 42.00000f, 3.00000f, 45.00000f, 3.00000f, 47.00000f, 3.00000f, 50.00000f, 3.00000f, 47.00000f, 3.00000f, 45.00000f, 0.00000f, 49.00000f, 0.00000f, 47.00000f, 3.00000f, 50.00000f, 3.00000f, 52.00000f, 3.00000f, 45.00000f, 3.00000f, 47.00000f, 3.00000f }),
        };

        /// <summary>A single baked vertex of the duck mesh in local model space (blocks).</summary>
        public readonly struct Vertex
        {
            public readonly float X, Y, Z;   // local position (blocks); feet at y = 0, facing -Z
            public readonly float U, V;      // texture coordinate (0..1)
            public readonly float Shade;     // directional face shade (0..1)

            public Vertex(float x, float y, float z, float u, float v, float shade)
            {
                X = x; Y = y; Z = z; U = u; V = v; Shade = shade;
            }
        }

        private static Vertex[]? _vertices;
        private static ushort[]? _indices;

        public static Vertex[] Vertices => _vertices ??= BuildVertices();
        public static ushort[] Indices => _indices ??= BuildIndices();

        private static void NormalizeAxis(float lo, float hi, out float min, out float max)
        {
            if (Math.Abs(hi - lo) >= 0.0001f)
            {
                min = Math.Min(lo, hi);
                max = Math.Max(lo, hi);
                return;
            }

            float center = (lo + hi) * 0.5f;
            min = center - MinThickness * 0.5f;
            max = center + MinThickness * 0.5f;
        }

        private static (float x, float y, float z) RotateAround((float x, float y, float z) p, float[] origin, float[] rotationDeg)
        {
            float px = p.x - origin[0];
            float py = p.y - origin[1];
            float pz = p.z - origin[2];

            // Rotations in the source model are single-axis, so applying X then Y then Z is order-independent here.
            if (rotationDeg[0] != 0f)
            {
                float a = rotationDeg[0] * (float)Math.PI / 180f;
                float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
                float ny = py * c - pz * s;
                float nz = py * s + pz * c;
                py = ny; pz = nz;
            }
            if (rotationDeg[1] != 0f)
            {
                float a = rotationDeg[1] * (float)Math.PI / 180f;
                float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
                float nx = px * c + pz * s;
                float nz = -px * s + pz * c;
                px = nx; pz = nz;
            }
            if (rotationDeg[2] != 0f)
            {
                float a = rotationDeg[2] * (float)Math.PI / 180f;
                float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
                float nx = px * c - py * s;
                float ny = px * s + py * c;
                px = nx; py = ny;
            }

            return (px + origin[0], py + origin[1], pz + origin[2]);
        }

        private static Vertex[] BuildVertices()
        {
            var verts = new List<Vertex>(Cubes.Length * 24);

            foreach (var cube in Cubes)
            {
                NormalizeAxis(cube.From[0], cube.To[0], out float x0, out float x1);
                NormalizeAxis(cube.From[1], cube.To[1], out float y0, out float y1);
                NormalizeAxis(cube.From[2], cube.To[2], out float z0, out float z1);

                // Face definitions: 4 corners (a,b,c,d) and the pixel UV rect index into cube.Faces.
                // Order matches Cubuild's createDuckElementGeometry.
                AddFace(verts, cube, x1, y1, z1, x1, y0, z1, x1, y1, z0, x1, y0, z0, 0, ShadeSide);       // east +x
                AddFace(verts, cube, x0, y1, z0, x0, y0, z0, x0, y1, z1, x0, y0, z1, 1, ShadeSide);       // west -x
                AddFace(verts, cube, x0, y1, z0, x0, y1, z1, x1, y1, z0, x1, y1, z1, 2, ShadeTop);        // up +y
                AddFace(verts, cube, x0, y0, z1, x0, y0, z0, x1, y0, z1, x1, y0, z0, 3, ShadeBottom);     // down -y
                AddFace(verts, cube, x0, y1, z1, x0, y0, z1, x1, y1, z1, x1, y0, z1, 4, ShadeFrontBack);  // south +z
                AddFace(verts, cube, x1, y1, z0, x1, y0, z0, x0, y1, z0, x0, y0, z0, 5, ShadeFrontBack);  // north -z
            }

            return verts.ToArray();
        }

        private static void AddFace(
            List<Vertex> verts, DuckCube cube,
            float ax, float ay, float az,
            float bx, float by, float bz,
            float cx, float cy, float cz,
            float dx, float dy, float dz,
            int faceIndex, float shade)
        {
            int uvBase = faceIndex * 4;
            float u0 = cube.Faces[uvBase + 0] / TextureSize;
            float v0 = cube.Faces[uvBase + 1] / TextureSize;
            float u1 = cube.Faces[uvBase + 2] / TextureSize;
            float v1 = cube.Faces[uvBase + 3] / TextureSize;

            AddVertex(verts, cube, ax, ay, az, u0, v0, shade);
            AddVertex(verts, cube, bx, by, bz, u0, v1, shade);
            AddVertex(verts, cube, cx, cy, cz, u1, v0, shade);
            AddVertex(verts, cube, dx, dy, dz, u1, v1, shade);
        }

        private static void AddVertex(List<Vertex> verts, DuckCube cube, float x, float y, float z, float u, float v, float shade)
        {
            var p = (x, y, z);
            p = RotateAround(p, cube.Origin, cube.Rotation);
            p = RotateAround(p, cube.GroupOrigin, cube.GroupRotation);
            verts.Add(new Vertex(p.Item1 * Scale, p.Item2 * Scale, p.Item3 * Scale, u, v, shade));
        }

        private static ushort[] BuildIndices()
        {
            int faceCount = Vertices.Length / 4;
            var indices = new ushort[faceCount * 6];
            for (int f = 0; f < faceCount; f++)
            {
                ushort b = (ushort)(f * 4);
                int i = f * 6;
                indices[i + 0] = (ushort)(b + 0);
                indices[i + 1] = (ushort)(b + 1);
                indices[i + 2] = (ushort)(b + 2);
                indices[i + 3] = (ushort)(b + 2);
                indices[i + 4] = (ushort)(b + 1);
                indices[i + 5] = (ushort)(b + 3);
            }
            return indices;
        }
    }
}
