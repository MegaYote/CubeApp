using System.Numerics;
using System.Collections.Generic;

namespace Cubuild.Renderer
{
    /// <summary>
    /// A block being mined by a zombie. The renderer draws a shrink-cube overlay for each one,
    /// identical to the player's mining animation.
    /// </summary>
    public struct ZombieMiningTarget
    {
        public int X, Y, Z;
        public int BlockId;
        public float Progress; // 0..1
    }

    /// <summary>
    /// Plain data describing what the HUD overlay should currently display.
    /// Populated by Program each tick and consumed by VeldridRenderer's ImGui pass.
    /// This replaces the old GDI+-based DrawHotbar/DrawCrosshair/DrawSelectedBlockLabel overlay.
    /// </summary>
    public struct HudState
    {
        public bool ShowDebug;
        public bool InventoryOpen;
        /// <summary>Workbench crafting menu open (right-click a workbench block).</summary>
        public bool CraftingOpen;
        public bool BiomeMenuOpen;
        public bool HandEditorOpen;
        public bool FlyMode;
        public bool Fullbright;
        /// <summary>Day/night clock (world ticks). Used by the renderer to dim sky + world light.</summary>
        public long WorldTime;
        /// <summary>Shared menu state (screen + create-world form + button flags).</summary>
        public MenuState Menu;
        public float Fps;
        public float UpdateMs;
        public float MeshMs;
        public float UploadMs;
        public float RenderMs;
        public float EntityMs;     // time spent in entity/mob AI updates
        public int EntityCount;    // number of active mobs
        public string FacingText;
        public string SelectedBlockText;
        public string RenderDistanceText;
        public int SelectedSlot;
        public int WorldSeed;
        public string BiomeText;
        /// <summary>Current per-slot hotbar contents (block ids); may differ from the default list
        /// once the player drops inventory picks into slots.</summary>
        public IReadOnlyList<int> Hotbar;

        /// <summary>Which mode the world is in (Creative = sandbox, Survival = resource loop).</summary>
        public GameMode Mode;
        /// <summary>Survival bag slots (40-slot grid). Empty/ignored in creative.</summary>
        public IReadOnlyList<InventorySlot> BagSlots;
        /// <summary>Per-hotbar-slot counts (parallel to the hotbar block ids).</summary>
        public IReadOnlyList<int> HotbarCounts;
        /// <summary>The stack riding the cursor while the inventory is open (survival drag/drop).</summary>
        public (int ItemId, int Count)? HeldStack;

        /// <summary>Workbench crafting grid, row-major 2x2 (4 slots). Empty/ignored unless CraftingOpen.</summary>
        public IReadOnlyList<(int ItemId, int Count)> CraftingSlots;
        /// <summary>Live crafting result for the current grid (null = no recipe).</summary>
        public (int ItemId, int Count)? CraftingResult;

        /// <summary>Player health 0..10, drives the healthbar heart slice (10 = full heart).</summary>
        public int PlayerHealth;
        /// <summary>How the player last died (for the respawn screen message).</summary>
        public DeathCause DeathCause;

        /// <summary>
        /// Four world-space corners of the targeted block face, or null if nothing is currently
        /// targeted. The renderer draws these as a depth-tested 3D quad so the highlight is
        /// occluded per-pixel by any block in front of it (matching the rest of the scene),
        /// instead of always painting over everything as a 2D overlay would.
        /// </summary>
        public Vector3[]? HighlightWorldQuad;

        /// <summary>
        /// Player's current world position for the debug overlay.
        /// </summary>
        public double PlayerX;
        public double PlayerY;
        public double PlayerZ;

        /// <summary>
        /// Player's current chunk coordinates for rendering chunk borders when debug is enabled.
        /// </summary>
        public int PlayerChunkX;
        public int PlayerChunkZ;
        public int RenderDistance;

        /// <summary>Networking status line shown in the debug overlay (e.g. "Hosting :26065" or
        /// "Joined host"). Empty = no session.</summary>
        public string NetStatus;

        /// <summary>Multiplayer error to display on the multiplayer menu (e.g. failed join).
        /// Empty = no error.</summary>
        public string MultiplayerError;

        /// <summary>0..1 mining progress on the highlighted block (0 = not mining). The renderer
        /// darkens the block-highlight quad as the player mines it, like Cubuild's crack overlay.</summary>
        public float MiningProgress;
        /// <summary>Remaining seconds of the first-person hand's place-jab animation (set by
        /// Program when a block placement succeeds).</summary>
        public float HandPoke;
        /// <summary>Remaining seconds of the first-person hand's one-shot punch/swing animation
        /// (set by Program when the player attacks a mob or punches the air).</summary>
        public float HandSwing;

        /// <summary>Remaining seconds of the "World saved" toast (set when a save completes,
        /// manual or autosave). 0 = not showing.</summary>
        public float SaveToast;

        /// <summary>World position + block id of the block currently being mined (for the
        /// shrinking-block overlay). Only valid while MiningProgress > 0.</summary>
        public Vector3 MiningBlockPos;
        public int MiningBlockId;

        /// <summary>
        /// Face normal of the block currently being mined (from TryPickBlock). The shrinking cube
        /// anchors to this face: instead of collapsing toward the cell center it collapses toward
        /// the block behind the looked-at face. Zero when no face is targeted.
        /// </summary>
        public Point3D MiningBlockNormal;

        public static HudState Empty => new HudState
        {
            ShowDebug = false,
            InventoryOpen = false,
            CraftingOpen = false,
            BiomeMenuOpen = false,
            HandEditorOpen = false,
            FlyMode = false,
            Fullbright = false,
            Menu = new MenuState(),
            Fps = 0f,
            UpdateMs = 0f,
            MeshMs = 0f,
            UploadMs = 0f,
            RenderMs = 0f,
            EntityMs = 0f,
            EntityCount = 0,
            FacingText = string.Empty,
            SelectedBlockText = string.Empty,
            RenderDistanceText = string.Empty,
            SelectedSlot = 0,
            WorldSeed = 0,
            BiomeText = string.Empty,
            Hotbar = Array.Empty<int>(),
            Mode = GameMode.Creative,
            BagSlots = Array.Empty<InventorySlot>(),
            HotbarCounts = Array.Empty<int>(),
            HeldStack = null,
            CraftingSlots = Array.Empty<(int ItemId, int Count)>(),
            CraftingResult = null,
            PlayerHealth = 20,
            DeathCause = DeathCause.Unknown,
            HighlightWorldQuad = null,
            PlayerChunkX = 0,
            PlayerChunkZ = 0,
            RenderDistance = 0,
            NetStatus = string.Empty,
            MultiplayerError = string.Empty,
            MiningProgress = 0f,
            MiningBlockPos = Vector3.Zero,
            MiningBlockId = 0,
            MiningBlockNormal = new Point3D(0, 0, 0),
            SaveToast = 0f,
        };

        /// <summary>Blocks being mined by zombies this frame — each gets a shrink-cube overlay.</summary>
        public List<ZombieMiningTarget>? ZombieMiningTargets;
    }
}
