using System;

namespace CubeApp
{
    /// <summary>
    /// A fully data-driven block definition loaded from blocks.json. A block is identified by a
    /// stable string id ("grass") and a numeric id (its index in the JSON; air is reserved 0 and
    /// is what the chunk's byte storage actually holds). Per-block flags drive collision, light
    /// propagation and meshing; face textures can either be a single "all" tile or overridden per
    /// top/bottom/side, so a future block can say "top grass, side dirt" without touching code.
    /// </summary>
    public sealed class BlockDefinition
    {
        public string Id { get; set; } = "";
        public int NumericId { get; set; }
        public string DisplayName { get; set; } = "";

        /// <summary>Default atlas tile used for any face without an explicit override.</summary>
        public TextureRect? AllTexture { get; set; }
        /// <summary>Optional override for the +Y face (top). Null falls back to AllTexture.</summary>
        public TextureRect? TopTexture { get; set; }
        /// <summary>Optional override for the -Y face (bottom). Null falls back to AllTexture.</summary>
        public TextureRect? BottomTexture { get; set; }
        /// <summary>Optional override for the four horizontal faces. Null falls back to AllTexture.</summary>
        public TextureRect? SideTexture { get; set; }

        /// <summary>Collides with mob/player AABBs. Air and true fluids are non-solid.</summary>
        public bool Solid { get; set; } = true;
        /// <summary>Blocks skylight flood-fill propagation. Non-opaque (water, glass, leaves) lets light through.</summary>
        public bool Opaque { get; set; } = true;
        /// <summary>Visually transparent: neighbours render their faces toward it, and two of the
        /// same transparent block don't render the internal face between them (no water|water faces).</summary>
        public bool Transparent { get; set; } = false;
        /// <summary>Vertex-color alpha used to make transparent tiles see-through when the atlas
        /// itself is opaque (the water tile has no alpha, so this tints it). 1.0 = fully opaque.</summary>
        public float Alpha { get; set; } = 1f;
        /// <summary>Mesh shape: "" (full cube), "cross" (two crossed billboard quads, like
        /// saplings/flowers), future "slab"/"torch"/"fire".</summary>
        public string Shape { get; set; } = "";
        /// <summary>Whether the block shows up in the E-menu inventory. Placement-only variants
        /// (like the auto-picked top slabs) set this false.</summary>
        public bool Inventory { get; set; } = true;
        /// <summary>Per-pixel translucent (colored glass): the atlas texture's OWN alpha is used for
        /// blending per fragment instead of the block's uniform vColor.a. Regular glass uses the
        /// cutout pipeline (0.5 discard, no blend).</summary>
        public bool Translucent { get; set; } = false;
        /// <summary>Reserved for block-light emission (torches, glowstone). 0..15, currently unused.</summary>
        public int LightEmission { get; set; }
        /// <summary>Hotbar / debug swatch colour, an ImGui packed U32 (0xAABBGGRR, full alpha).</summary>
        public uint MapColor { get; set; }
        /// <summary>Falls when unsupported (sand, gravel, dirt, red clay): if the block below is
        /// removed or updated out from under it, it drops until it finds support.</summary>
        public bool Gravity { get; set; }

        /// <summary>Survival mining hardness (Cubuild C++ port): break time = BASE_BREAK_TIME *
        /// hardness. 1.0 = default; soft blocks (dirt/grass ~0.5-0.6) mine fast, stone ~4 takes a
        /// while, bedrocks are unbreakable.</summary>
        public float Hardness { get; set; } = 1f;

        /// <summary>Picks the atlas tile for a given face normal, honouring top/bottom/side overrides.</summary>
        public TextureRect FaceTexture(Point3D normal)
        {
            if (normal.Y > 0.5 && TopTexture.HasValue) return TopTexture.Value;
            if (normal.Y < -0.5 && BottomTexture.HasValue) return BottomTexture.Value;
            if ((Math.Abs(normal.X) > 0.5 || Math.Abs(normal.Z) > 0.5) && SideTexture.HasValue) return SideTexture.Value;
            return AllTexture ?? default;
        }
    }
}