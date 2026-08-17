using System;
using System.Collections.Generic;

namespace Cubuild
{
    /// <summary>The animatable bones of the player model.</summary>
    public enum PlayerBoneId
    {
        Body,
        Head,
        RightArm,
        LeftArm,
        RightLeg,
        LeftLeg,
    }

    /// <summary>
    /// A classic voxel-style player model: 8x8x8 head, 8x12x4 body and 4x12x4 arms/legs, mapped
    /// against a standard modern 64x64 skin. Conventions match <see cref="DuckModel"/>:
    /// coordinates are texture pixels (16 px = 1 block), the feet sit at y = 0, the model is
    /// centred on x = 0 and faces -Z. The whole rig is x-mirrored: the player's RIGHT side is +X
    /// in model space, which makes the standard skin regions land on the correct sides.
    /// </summary>
    public static class PlayerModel
    {
        public const float Scale = 1f / 16f;
        private const float TextureSize = 64f;

        public const string TextureResourceName = "player.png";

        // Same directional shading as the duck: east/west 0.60, top 1.00, bottom 0.50, north/south 0.80.
        private static readonly float ShadeSide = 0.60f;
        private static readonly float ShadeTop = 1.00f;
        private static readonly float ShadeBottom = 0.50f;
        private static readonly float ShadeFrontBack = 0.80f;

        private readonly struct PlayerCube
        {
            public readonly PlayerBoneId Bone;
            public readonly float[] From;
            public readonly float[] To;
            public readonly float[] Pivot; // bone pivot (pixels)
            // Face UV rects in pixels [u0,v0,u1,v1], in order: east, west, up, down, south, north.
            public readonly float[] Faces;

            public PlayerCube(PlayerBoneId bone, float[] from, float[] to, float[] pivot, float[] faces)
            {
                Bone = bone;
                From = from;
                To = to;
                Pivot = pivot;
                Faces = faces;
            }
        }

        /// <summary>
        /// Expands a standard box-UV region at (u, v) for a box of size w x h x d into per-face
        /// pixel UV rects in the duck-model face order (east, west, up, down, south, north).
        /// The mapping accounts for the model being x-mirrored relative to the classic layout
        /// (front = -Z): the skin's "right" column lands on the +X face and texture wrap stays
        /// seamless.
        /// </summary>
        private static float[] BoxUV(float u, float v, float w, float h, float d)
        {
            return new[]
            {
                // east (+x, player's right) = skin "right" column
                u, v + d, u + d, v + d + h,
                // west (-x, player's left) = skin "left" column
                u + d + w, v + d, u + d + w + d, v + d + h,
                // up (+y) = skin "top", rotated 180 deg so shared edges line up with front/right
                u + d + w, v + d, u + d, v,
                // down (-y) = skin "bottom"
                u + d + w, v, u + d + 2f * w, v + d,
                // south (+z, back) = skin "back"
                u + 2f * d + w, v + d, u + 2f * d + 2f * w, v + d + h,
                // north (-z, front) = skin "front"
                u + d, v + d, u + d + w, v + d + h,
            };
        }

        // Classic Steve geometry (pixels). Right limbs at +X, left limbs at -X, front at -Z.
        private static readonly PlayerCube[] Cubes =
        {
            new PlayerCube(PlayerBoneId.Body, new[] { -4f, 12f, -2f }, new[] { 4f, 24f, 2f }, new[] { 0f, 12f, 0f }, BoxUV(16, 16, 8, 12, 4)),
            new PlayerCube(PlayerBoneId.Head, new[] { -4f, 24f, -4f }, new[] { 4f, 32f, 4f }, new[] { 0f, 24f, 0f }, BoxUV(0, 0, 8, 8, 8)),
            new PlayerCube(PlayerBoneId.RightArm, new[] { 4f, 12f, -2f }, new[] { 8f, 24f, 2f }, new[] { 5f, 22f, 0f }, BoxUV(40, 16, 4, 12, 4)),
            new PlayerCube(PlayerBoneId.LeftArm, new[] { -8f, 12f, -2f }, new[] { -4f, 24f, 2f }, new[] { -5f, 22f, 0f }, BoxUV(32, 48, 4, 12, 4)),
            new PlayerCube(PlayerBoneId.RightLeg, new[] { 0f, 0f, -2f }, new[] { 4f, 12f, 2f }, new[] { 2f, 12f, 0f }, BoxUV(0, 16, 4, 12, 4)),
            new PlayerCube(PlayerBoneId.LeftLeg, new[] { -4f, 0f, -2f }, new[] { 0f, 12f, 2f }, new[] { -2f, 12f, 0f }, BoxUV(16, 48, 4, 12, 4)),
        };

        // Per-bone animation axis (head turns about Y, limbs swing about X).
        private static readonly Dictionary<PlayerBoneId, DuckBoneAxis> BoneAnimAxis = new()
        {
            { PlayerBoneId.Body, DuckBoneAxis.None },
            { PlayerBoneId.Head, DuckBoneAxis.Y },
            { PlayerBoneId.RightArm, DuckBoneAxis.X },
            { PlayerBoneId.LeftArm, DuckBoneAxis.X },
            { PlayerBoneId.RightLeg, DuckBoneAxis.X },
            { PlayerBoneId.LeftLeg, DuckBoneAxis.X },
        };

        /// <summary>A single baked vertex of the player mesh in local model space (blocks).</summary>
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

        /// <summary>One rigged part of the player: baked geometry, pivot and animation axis.</summary>
        public sealed class Bone
        {
            public PlayerBoneId Id { get; }
            public Vertex[] Vertices { get; }
            public ushort[] Indices { get; }
            public float PivotX { get; }
            public float PivotY { get; }
            public float PivotZ { get; }
            public DuckBoneAxis Axis { get; }

            public Bone(PlayerBoneId id, Vertex[] vertices, ushort[] indices, float pivotX, float pivotY, float pivotZ, DuckBoneAxis axis)
            {
                Id = id;
                Vertices = vertices;
                Indices = indices;
                PivotX = pivotX;
                PivotY = pivotY;
                PivotZ = pivotZ;
                Axis = axis;
            }
        }

        private static Bone[]? _bones;

        public static Bone[] Bones => _bones ??= BuildBones();

        private static Bone[] BuildBones()
        {
            var order = new[]
            {
                PlayerBoneId.Body, PlayerBoneId.Head, PlayerBoneId.RightArm,
                PlayerBoneId.LeftArm, PlayerBoneId.RightLeg, PlayerBoneId.LeftLeg,
            };

            var bones = new List<Bone>(order.Length);
            foreach (var id in order)
            {
                var verts = new List<Vertex>();
                float[]? pivot = null;

                foreach (var cube in Cubes)
                {
                    if (cube.Bone != id) continue;
                    pivot ??= cube.Pivot;
                    AddCubeFaces(verts, cube);
                }

                if (pivot == null) continue;

                bones.Add(new Bone(
                    id,
                    verts.ToArray(),
                    BuildIndices(verts.Count),
                    pivot[0] * Scale, pivot[1] * Scale, pivot[2] * Scale,
                    BoneAnimAxis[id]));
            }

            return bones.ToArray();
        }

        private static void AddCubeFaces(List<Vertex> verts, PlayerCube cube)
        {
            float x0 = Math.Min(cube.From[0], cube.To[0]);
            float x1 = Math.Max(cube.From[0], cube.To[0]);
            float y0 = Math.Min(cube.From[1], cube.To[1]);
            float y1 = Math.Max(cube.From[1], cube.To[1]);
            float z0 = Math.Min(cube.From[2], cube.To[2]);
            float z1 = Math.Max(cube.From[2], cube.To[2]);

            // Same corner ordering as DuckModel.AddCubeFaces.
            AddFace(verts, cube, x1, y1, z1, x1, y0, z1, x1, y1, z0, x1, y0, z0, 0, ShadeSide);       // east +x
            AddFace(verts, cube, x0, y1, z0, x0, y0, z0, x0, y1, z1, x0, y0, z1, 1, ShadeSide);       // west -x
            AddFace(verts, cube, x0, y1, z0, x0, y1, z1, x1, y1, z0, x1, y1, z1, 2, ShadeTop);        // up +y
            AddFace(verts, cube, x0, y0, z1, x0, y0, z0, x1, y0, z1, x1, y0, z0, 3, ShadeBottom);     // down -y
            AddFace(verts, cube, x0, y1, z1, x0, y0, z1, x1, y1, z1, x1, y0, z1, 4, ShadeFrontBack);  // south +z
            AddFace(verts, cube, x1, y1, z0, x1, y0, z0, x0, y1, z0, x0, y0, z0, 5, ShadeFrontBack);  // north -z
        }

        private static void AddFace(
            List<Vertex> verts, PlayerCube cube,
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

            verts.Add(new Vertex(ax * Scale, ay * Scale, az * Scale, u0, v0, shade));
            verts.Add(new Vertex(bx * Scale, by * Scale, bz * Scale, u0, v1, shade));
            verts.Add(new Vertex(cx * Scale, cy * Scale, cz * Scale, u1, v0, shade));
            verts.Add(new Vertex(dx * Scale, dy * Scale, dz * Scale, u1, v1, shade));
        }

        private static ushort[] BuildIndices(int vertexCount)
        {
            int faceCount = vertexCount / 4;
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
