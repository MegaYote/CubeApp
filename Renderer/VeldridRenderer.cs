using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using Veldrid;
using Veldrid.SPIRV;

namespace Cubuild.Renderer
{
    // Veldrid renderer implementing mesh upload, unlit texture shading, and an ImGui-based HUD overlay.
    // No GDI+/System.Drawing is used anywhere in this renderer.
    public sealed partial class VeldridRenderer : IRenderer, IDisposable
    {
        private GraphicsDevice _gd;
        private Swapchain _sc;

        // ---- resolution scale (low-end GPUs) ----
        // When < 1, the world renders into an offscreen framebuffer at fraction*swapchain size
        // and is upscaled to the swapchain with a fullscreen blit. The ImGui UI (and crosshair)
        // draw at native resolution so menus stay crisp. Big pixel-count savings for iGPUs.
        private float _resolutionScale = 1f;
        private bool _pixelatedUpscale;
        private Texture _scaleColorTexture;
        private TextureView _scaleColorView;
        private Texture _scaleDepthTexture;
        private Framebuffer _scaleFramebuffer;
        private Pipeline _blitPipeline;
        private ResourceLayout _blitLayout;
        private ResourceSet _blitResourceSet;
        // Two blit samplers: linear (smooth upscale) and point/nearest (chunky blocky pixels).
        // Only the active one is bound to the resource set; switching just rebuilds the set.
        private Sampler _blitSamplerLinear;
        private Sampler _blitSamplerNearest;

        private DeviceBuffer _projViewBuffer;
        private ResourceLayout _projViewLayout;
        private ResourceSet _projViewSet;
        private ResourceLayout _textureLayout;
        // Distance fog: a per-frame uniform block + resource set bound to the world
        // pipelines. Linear from fogStart (25% of the far plane) to fogEnd (the far plane).
        private DeviceBuffer _fogBuffer;
        private ResourceLayout _fogLayout;
        private ResourceSet _fogSet;
        private readonly float[] _fogParams = new float[16];
        // Day/night: computed from HudState.WorldTime each frame. _nightDim scales world light,
        // _nightSkyDim scales the fog color toward night dark.
        private float _nightDim = 1f;
        private float _nightSkyDim = 1f;
        private float _nightSkyR = 136f / 255f;
        private float _nightSkyG = 187f / 255f;
        private float _nightSkyB = 1f;
        private Texture _atlasTexture;
        private TextureView _atlasView;
        private Sampler _atlasSampler;
        private ResourceSet _textureSet;
        private Pipeline _pipeline;
        // Cutout pass (cross plants / leaves): alpha-test discard + depth-write ON so a nearer
        // sprite quad depth-occludes a farther one (matches Cubuild's worldMaterialCutout).
        private Pipeline _cutoutPipeline;
        // Glass pass: alpha-test discard but depth-write OFF and front-side culling, matching
        // Cubuild's worldMaterialGlass - the inside (back) faces of glass don't render through the
        // panes, and far glass shows through near glass.
        private Pipeline _glassPipeline;
        // Transparent pass for water: same shaders/state as _pipeline but with depth WRITES
        // disabled so blended faces tint whatever opaque geometry was already drawn instead of
        // blocking it from ever drawing (which made border water walls render as ghosty
        // see-through when their chunk happened to draw before the terrain behind).
        private Pipeline _transparentPipeline;
        // Translucent tint pass (colored glass): drawn AFTER water with depth-write OFF. Renders
        // only the semi-transparent pixels of colored-glass faces (sentinel alpha ~ -200), blending
        // the glass tint over whatever is behind it - so water behind glass stays visible through
        // the transparent panes while the opaque frames (drawn in the glass pass) still occlude.
        private Pipeline _translucentPipeline;
        private Pipeline _highlightPipeline;
        private DeviceBuffer _highlightVertexBuffer;
        private DeviceBuffer _highlightIndexBuffer;
        private readonly float[] _highlightVertexScratch = new float[12];

        // Shrinking-block mining overlay (Cubuild C++ BreakingBlockRenderer): a cube textured
        // with the mined block's tiles that scales from 1.0 down to 0.1 as progress goes 0->1,
        // drawn with the world pipeline so it depth-tests and fogs like terrain. The cube's
        // 24 vertices are packed into the world vertex format each frame with the scale baked
        // into the positions, centered in the block cell.
        private DeviceBuffer _shrinkCubeVertexBuffer;
        private DeviceBuffer _shrinkCubeIndexBuffer;
        private Pipeline _shrinkCubePipeline;
        private Pipeline _shrinkWallPipeline;
        private readonly float[] _shrinkCubeVertexScratch = new float[48 * 6]; // shrink cube (24) + neighbor walls (24) verts * 6 floats

        // Pipeline for chunk border wireframe rendering (F3 debug)
        private Pipeline _chunkBorderPipeline;
        private DeviceBuffer _chunkBorderVertexBuffer;
        private DeviceBuffer _chunkBorderIndexBuffer;
        private float[] _chunkBorderVertexScratch = new float[768]; // grown on demand for larger render distances

        // Sky: two GIANT fog-blended planes drawn before the world
        // with depth-write off. Top plane (glSkyList) at cameraY+16 uses the sky color, bottom plane
        // (glSkyList2) at cameraY-16 uses the darkened color (r*0.2+0.04, g*0.2+0.04, b*0.6+0.1).
        // Linear fog (start 0, end farPlane*0.8) fades them toward the fog
        // color at the horizon - the whole sky gradient, no dome or shader magic.
        private Pipeline _skyPipeline;
        private DeviceBuffer _skyVertexBuffer;
        private DeviceBuffer _skyIndexBuffer;
        private float[] _skyVertexScratch = new float[56];      // reused per frame (no alloc)
        private float[] _celestialVertexScratch = new float[40]; // reused per frame (no alloc)
        private DeviceBuffer _skyFogBuffer;
        private ResourceLayout _skyFogLayout;
        private ResourceSet _skyFogSet;
        private readonly float[] _skyFogParams = new float[20]; // fogColor(4)+fogRange(2)+cameraPos(4)+skyTop(4)+skyBottom(4)
        // The sky is drawn in CAMERA space (the display lists sit at y=16 relative to the eye,
        // transformed only by the camera rotation + projection). We mirror that: a static
        // camera-space vertex buffer + this rotation-only view-projection, so the sky is
        // structurally locked to the camera and can never drift as the player walks.
        private DeviceBuffer _skyMatrixBuffer;
        private ResourceSet _skyMatrixSet;

        // Sun/moon/stars: textured quads glued to the sky rotation.
        private Pipeline _celestialPipeline;
        private DeviceBuffer _celestialVertexBuffer;
        private DeviceBuffer _celestialIndexBuffer;
        private Texture _sunTexture;
        private TextureView _sunView;
        private Texture _moonTexture;
        private TextureView _moonView;
        private Sampler _celestialSampler;
        private ResourceSet _sunTextureSet;
        private ResourceSet _moonTextureSet;
        private ResourceLayout _celestialTextureLayout;

        // Starfield: a precompiled list of small quads on the unit sphere, drawn with
        // alpha = getStarBrightness when the sky is dark.
        private Pipeline _starPipeline;
        private DeviceBuffer _starVertexBuffer;
        private DeviceBuffer _starIndexBuffer;
        private int _starVertexCount;
        private bool _starsBuilt;
        private float[] _starVertexScratch = Array.Empty<float>();
        private float[] _starBaseScratch = Array.Empty<float>();
        private ushort[] _starIndexScratch = Array.Empty<ushort>();

        // Galaxy (deep-sky object) resources: spiral particle clusters seeded per world,
        // additive blend, drawn between stars and sun/moon (mirrors CubuildC++ SkyRenderer).
        private Pipeline _galaxyPipeline;
        private DeviceBuffer _galaxyVertexBuffer;
        private DeviceBuffer _galaxyIndexBuffer;
        private int _galaxyVertexCount;
        private bool _galaxiesBuilt;
        private int _galaxySeed = int.MinValue;
        private float[] _galaxyVertexScratch = Array.Empty<float>();
        private float[] _galaxyBaseScratch = Array.Empty<float>();
        private ushort[] _galaxyIndexScratch = Array.Empty<ushort>();
        private List<GalaxyDef> _galaxies = new();

        private const float GalaxyDistance = 400f;   // inside the 700 far plane
        private const float GalaxyScale = 400f / 1000f; // C++ renders at 1000; scale to 400

        private struct GalaxyParticleDef
        {
            public Vector3 Offset;
            public float Alpha;
            public float Size;
        }

        private struct GalaxyDef
        {
            public Vector3 BasePosition;
            public List<GalaxyParticleDef> Particles;
            public float SizeMultiplier;
            public float ElongationX;
            public float ElongationY;
            public float Rotation;
            public float SpiralTightness;
            public int NumArms;
        }

        // ---- Wide far plane for the "world from above" plane: a dedicated projection with far =
        // 3x the world far plane, so the fake earth stretches much further than terrain without
        // affecting depth precision of the world. (Clouds were removed; this matrix remains for
        // the world-plane backdrop.)
        private DeviceBuffer? _cloudMatrixBuffer;
        private ResourceSet? _cloudMatrixSet;
        private float _cloudFarPlane = 2100f;
        private int _cloudSeed = 12345;

        // ---- Clouds (flat MC-style deck) ------------------------------------------------
        // A single flat translucent plane at a fixed world height that follows the camera in
        // X/Z, textured with an ORIGINAL procedurally generated puff pattern (fractal value
        // noise - no external assets). Tiled UVs from world position keep the puffs anchored as
        // you walk; the deck's outer edge fades to zero so the horizon cut is soft. Drawn after
        // the world with depth test on / write off, using the SAME projection as terrain, so it
        // blends over the land from above and is hidden behind hills from below.
        private Pipeline? _cloudPipeline;
        private DeviceBuffer? _cloudVertexBuffer;
        private DeviceBuffer? _cloudIndexBuffer;
        private DeviceBuffer? _cloudParamsBuffer;
        private ResourceSet? _cloudParamsSet;
        private ResourceLayout? _cloudParamsLayout;
        private Texture? _cloudTexture;
        private TextureView? _cloudTextureView;
        private const float CloudWorldY = 128f;       // cloud deck altitude
        private const float CloudTileSize = 256f;     // blocks per 256px texture tile (1px ~ 1 block)
        private const float CloudFadeWidth = 300f;    // outer-edge fade so the horizon line is soft
        private float _cloudScrollU;
        private float _cloudScrollV;
        private float _lastCloudTime;
        private readonly float[] _cloudParams = new float[4];
        private readonly System.Diagnostics.Stopwatch _cloudClock = System.Diagnostics.Stopwatch.StartNew();

        // ---- Crosshair (pixel-art, colour-INVERTING) --------------------------------------
        // Drawn as a tiny 2D pass with a SUBTRACT blend (out = white - background), so it always
        // inverts whatever is behind it and stays visible on any colour. Drawn before the ImGui
        // UI pass, so menu windows (inventory, biome, pause, title) naturally paint over it.
        private Pipeline? _crosshairPipeline;
        private DeviceBuffer? _crosshairVertexBuffer;
        private DeviceBuffer? _crosshairIndexBuffer;

        // "World from above" ground plane: a giant flat green+water textured plane at the terrain
        // level that only appears when the player climbs high. Drawn with depth disabled BEFORE
        // the world, so real terrain always paints over it - mimics looking down on a distant
        // earth. Follows the camera in X/Z at a fixed world Y, extends to the wide far plane.
        private Pipeline? _worldPlanePipeline;
        private DeviceBuffer? _worldPlaneVertexBuffer;
        private DeviceBuffer? _worldPlaneIndexBuffer;
        private Texture? _worldPlaneTexture;
        private TextureView? _worldPlaneTextureView;
        private ResourceSet? _worldPlaneTextureSet;
        private ResourceLayout? _worldPlaneLayout;
        private ResourceSet? _worldPlaneMatrixSet;
        private const float WorldPlaneY = 60f;         // terrain-ish altitude the fake earth sits at
        private const double WorldPlaneShowThreshold = 260.0; // camera Y where it fades in

        // Textured entity-model pipeline (currently just the duck test mob).
        private Pipeline _modelPipeline;
        private Texture _duckTexture;
        private TextureView _duckView;
        private Sampler _duckSampler;
        private ResourceSet _duckTextureSet;
        private DuckModel.Bone[] _duckBones = Array.Empty<DuckModel.Bone>();
        private int _duckVertsPerInstance;
        private int _duckIndicesPerInstance;
        private DeviceBuffer? _duckVertexBuffer;
        private DeviceBuffer? _duckIndexBuffer;
        private uint _duckVertexCapacity;
        private uint _duckIndexCapacity;
        private IReadOnlyList<Cubuild.DuckInstance> _duckInstances = Array.Empty<Cubuild.DuckInstance>();
        // Reusable backing lists (cleared + refilled each frame) so SetEntities doesn't allocate
        // three Lists every frame (FPS roadmap #6). The draw loops read these via the fields above.
        private readonly List<Cubuild.DuckInstance> _duckList = new();
        private readonly List<Cubuild.DuckInstance> _playerList = new();
        private float[] _duckVertexScratch = Array.Empty<float>();
        private ushort[] _duckIndexScratch = Array.Empty<ushort>();
        private const int DuckFloatsPerVertex = 9; // pos(3) + uv(2) + color(4)
        private const float DuckModelScale = 1.05f; // visually petite duck
        // Classic Steve proportions: the model is 32px = 2.0 blocks tall unscaled (feet to head
        // top). 0.9x brings it to the classic 1.8 blocks, so the head sits at eye height (1.62)
        // instead of towering above the camera.
        private const float PlayerModelScale = 0.9f; // visually correct player/Steve

        // Voxel player model (shares the model pipeline; own texture + buffers).
        private Texture _playerTexture;
        private TextureView _playerView;
        private Sampler _playerSampler;
        private ResourceSet _playerTextureSet;
        private PlayerModel.Bone[] _playerBones = Array.Empty<PlayerModel.Bone>();
        private int _playerVertsPerInstance;
        private int _playerIndicesPerInstance;
        private DeviceBuffer? _playerVertexBuffer;
        private DeviceBuffer? _playerIndexBuffer;
        private uint _playerVertexCapacity;

        // ---- First-person hand viewmodel ----------------------------------------------
        // The player's right arm rendered attached to the camera (bottom-right corner) in CAMERA
        // space with depth testing OFF, so it's always visible like MC's hand. Animates: idle
        // sway, rhythmic mining chop while breaking, and a quick jab on placement.
        private Pipeline? _handPipeline;
        private DeviceBuffer? _handVertexBuffer;
        private DeviceBuffer? _handIndexBuffer;
        private DeviceBuffer? _handProjBuffer;
        private ResourceSet? _handProjSet;
        private Pipeline? _heldBlockPipeline; // no-fog camera-space pipeline for the held block
        private float[] _handMesh = Array.Empty<float>(); // pos3 + uv2 + shade4 per vertex, shoulder at origin
        private ushort[] _handIndices = Array.Empty<ushort>();
        private DeviceBuffer? _heldBlockBuffer; // instance data for the block held in the hand
        private readonly float[] _heldBlockScratch = new float[11];
        private bool _firstPersonCamera;
        private float _handSwingPhase;
        private float _handWalkPhase;
        private float _handWalkAmount;
        private float _handPunchTime;
        private float _lastHandTime;
        private readonly System.Diagnostics.Stopwatch _handClock = System.Diagnostics.Stopwatch.StartNew();

        // ---- Tunable hand viewmodel params (adjust in-game via the F3 Hand Editor) ----
        private float _handScale = 1.052f;     // arm scale
        private float _handSx = 0.854f;        // shoulder anchor X (right)
        private float _handSy = -1.023f;       // shoulder anchor Y (down)
        private float _handSz = -0.623f;       // shoulder anchor Z (forward)
        private float _handBasePitch = -0.678f; // idle arm pitch
        private float _handBaseYaw = 0.010f;   // idle arm yaw (toward center)
        // Held-block anchor is INDEPENDENT of the arm pose (camera space), so tuning one never
        // moves the other; the block still rides the shared punch/bob/sway animation.
        private float _heldBlockX = 0.648f;
        private float _heldBlockY = -0.345f;
        private float _heldBlockZ = -0.412f;
        private float _heldBlockSize = 0.289f; // held block cube size
        private uint _playerIndexCapacity;
        private IReadOnlyList<Cubuild.DuckInstance> _playerInstances = Array.Empty<Cubuild.DuckInstance>();
        private float[] _playerVertexScratch = Array.Empty<float>();
        private ushort[] _playerIndexScratch = Array.Empty<ushort>();

        // GLB-driven mobs (coyote + any future Blockbench mob): loaded from the MobRegistry at
        // startup (MobEntities/<Type>Mob/<type>.glb + .png) and drawn through MobModel.Draw's
        // generic path. One entry per mob type so ANY discovered mob renders automatically.
        private sealed class MobModelEntry
        {
            public MobModel? Model;
            public ResourceSet? TextureSet;
            public List<Cubuild.DuckInstance> Instances = new();
            public DeviceBuffer? VertexBuffer;
            public DeviceBuffer? IndexBuffer;
            public uint VertexCapacity;
            public uint IndexCapacity;
            public float[] VertexScratch = Array.Empty<float>();
            public ushort[] IndexScratch = Array.Empty<ushort>();
        }

        private readonly Dictionary<string, MobModelEntry> _modelMobs = new();
        // Full mob snapshot kept for F3 nametag rendering (world -> screen projection).
        private IReadOnlyList<Cubuild.MobRenderData> _allMobRenderData = Array.Empty<Cubuild.MobRenderData>();

        // Current camera (so chunk frustum culling and the mob meshing can read it) and the six
        // view-frustum planes refreshed each frame from the view-projection matrix.
        private Cubuild.Point3D? _cameraPosition;
        private System.Numerics.Matrix4x4? _viewProjection;
        public Cubuild.Point3D? CameraPosition => _cameraPosition;
        public System.Numerics.Matrix4x4? ViewProjection => _viewProjection;
        private readonly Vector4[] _frustumPlanes = new Vector4[6];

        // Last chunk the camera was in when the glass/water passes were sorted. The back-to-front
        // sort only needs re-running when the camera crosses a chunk boundary - within a chunk the
        // distance order of far-to-near chunks doesn't change enough to matter, and skipping the
        // O(n log n) sort every frame is a real win (FPS roadmap #2).
        // NOTE: the cache is PER-PASS (indexed by pass id). The glass, water and glass-tint passes
        // each sort their own command lists; sharing one cache keyed only on (camChunk, count)
        // made the water pass skip its sort whenever its chunk count matched the glass pass's,
        // leaving the water list in a stale draw order (far water painting over near water).
        private static readonly int SortPassGlass = 0;
        private static readonly int SortPassWater = 1;
        private static readonly int SortPassGlassTint = 2;
        private readonly int[] _lastSortChunkX = { int.MinValue, int.MinValue, int.MinValue };
        private readonly int[] _lastSortChunkZ = { int.MinValue, int.MinValue, int.MinValue };
        private readonly int[] _lastSortCount = { -1, -1, -1 };

        private CommandList _commandList;
        private ImGuiRenderer _imguiRenderer;
        private HudState _hud = HudState.Empty;
        private float _farPlane = 100f;
        private float _nearPlane = 0.1f;
        // Fog end: the render distance in blocks (chunkRadius * 16). Terrain is fully fogged
        // at this distance; the far plane stays larger so geometry beyond the fog still exists
        // (it's just invisible), and fog hides the chunk loading edge.
        private float _fogEnd = 100f;
        private float _atlasWidth = 256f;
        private float _atlasHeight = 256f;
        // Items atlas (items.png): used to texture dropped items that define an "itemTile"
        // (flint, etc). Same 16px tile grid as the terrain atlas.
        private Texture? _itemsAtlasTexture;
        private TextureView? _itemsAtlasView;
        private ResourceSet? _itemsTextureSet;
        // CPU copy of the atlas pixels (for generating hotbar/inventory block icons) and the
        // icon atlas texture built from them (classic MC-style isometric cubes per block).
        private byte[] _atlasRgba = Array.Empty<byte>();
        private int _atlasPixelsW;
        private int _atlasPixelsH;
        // Water animation: the atlas carries 4 painted water frames in a row (tiles 12..15,14).
        // Each rendered frame we crossfade the two current frames into a 64x16 strip and
        // re-upload that region of the GPU atlas, so world water (and anything sampling the
        // water tile) slowly shimmers through the cycle - MC-style, but smooth-faded instead
        // of stepped. Purely visual; the source atlas copy is never mutated.
        private const int WaterTileX = 12;
        private const int WaterTileY = 14;
        private const int WaterFrameCount = 4;
        private const float WaterCycleSeconds = 8.0f;   // one full 4-frame fade cycle
        private byte[]? _waterFrames;                    // 4 x 16x16 RGBA frames (pristine copy)
        private byte[]? _waterStrip;                     // 64x16 RGBA blended strip for upload
        private readonly System.Diagnostics.Stopwatch _waterClock = new();
        private Texture? _iconAtlasTexture;
        private TextureView? _iconAtlasView;
        private IntPtr _iconImGuiId;
        private const int IconCellSize = 48;
        private Vector4[]? _blockIconUv;
        // Items atlas pixels + flat 2D icons for genuine items (tools, food, gems). Genuine
        // items get their items.png tile copied straight into a second icon atlas, so the
        // hotbar/inventory can show the real flat sprite instead of a block mesh.
        private byte[] _itemsAtlasRgba = Array.Empty<byte>();
        private int _itemsAtlasPixelsW;
        private int _itemsAtlasPixelsH;
        private Texture? _itemIconAtlasTexture;
        private TextureView? _itemIconAtlasView;
        private IntPtr _itemIconImGuiId;
        private Vector4[]? _itemIconUv;
        // Terrain atlas bound to ImGui for the title/pause menu's dirt background.
        private IntPtr _terrainImGuiId;
        // Title-screen logo graphic (cubuild.png, embedded).
        private Texture? _logoTexture;
        private TextureView? _logoView;
        private IntPtr _logoImGuiId;
        // Hotbar GUI textures (embedded from Cubuild.html): the 169x16 slot frame and the 18x18
        // selected-slot highlight. Exposed to ImGui so the hotbar can draw the real MC-style frame
        // with the isometric block icons on top.
        private Texture? _hotbarTexture;
        private TextureView? _hotbarView;
        private IntPtr _hotbarImGuiId;
        private Texture? _hotbarSelectTexture;
        private TextureView? _hotbarSelectView;
        private IntPtr _hotbarSelectImGuiId;
        // The original C++ E-menu background (190x111), loaded from inventory.png.
        private Texture? _inventoryTexture;
        private TextureView? _inventoryView;
        private IntPtr _inventoryImGuiId;
        // The workbench crafting menu background (user's design, 111x49), loaded from crafting.png.
        private Texture? _craftingTexture;
        private TextureView? _craftingView;
        private IntPtr _craftingImGuiId;
        // Healthbar sprite sheet (healthbar.png): 13px hearts on a 15px grid. The top-left sprite
        // (index 0) is the FULL heart - the one shown until the slice-countdown sprites are wired.
        private Texture? _healthbarTexture;
        private TextureView? _healthbarView;
        private IntPtr _healthbarImGuiId;
        // Flash mask: a copy of the sheet where only the near-black outline pixels are white (the
        // rest transparent). Drawn over the heart briefly whenever health changes, so the outline
        // flashes white like the classic MC damage flash.
        private Texture? _healthbarFlashTexture;
        private TextureView? _healthbarFlashView;
        private IntPtr _healthbarFlashImGuiId;
        private float _healthFlashTimer;
        private int _lastHudHealth = 10;
        private const float HealthFlashDuration = 0.25f;
        // Damage shake: a short decaying POV jitter whenever the player takes damage, so a hit
        // reads as an "ow". Triggered on health DROP only (regen/gain never shakes).
        private float _damageShakeTime;
        private float _damageShakeMagnitude;
        private float _damageShakeElapsed;
        private const float DamageShakeDuration = 0.4f;
        private readonly System.Random _shakeRandom = new();
        /// <summary>0..1 head-bob intensity; eases toward the target so landing/jumping doesn't
        /// snap the camera.</summary>
        private float _bobBlend;
        private const int HealthbarSpriteSize = 13;
        private const int HealthbarGridPitch = 15;
        // Heartbeat rhythm: "bump, pause, bump bump, pause, bump, pause, bump bump" repeating.
        // Each entry is (startTimeInCycle, amplitudePx); the second thump of each double is softer.
        private static readonly double[] HealthbeatTimes = { 0.00, 0.90, 1.05, 1.80, 2.40, 2.55 };
        private static readonly double[] HealthbeatAmps = { 1.5, 1.5, 1.0, 1.5, 1.5, 1.0 };
        private const double HealthbeatCycle = 2.8;    // seconds for the full pattern
        private const double HealthbeatBumpDur = 0.18; // seconds per thump
        private byte[] _worldNameBuffer = new byte[256];
        private byte[] _seedBuffer = new byte[256];
        private byte[] _renameBuffer = new byte[256];
        private bool _renameBufferInit;
        private byte[] _hostPortBuffer = new byte[16];
        private byte[] _joinAddressBuffer = new byte[128];
        private bool _menuBuffersInitialized;
        // Real input for the ImGui UI (only wired when the mouse is free, e.g. the E-menu
        // inventory); otherwise ImGui stays inert via NullInputSnapshot.
        private InputSnapshot? _uiInputSnapshot;
        private readonly System.Collections.Concurrent.ConcurrentQueue<int> _inventorySelections = new();
        // Survival drag/drop clicks: (kind, target, button). kind: 0=bag stack (target=blockId),
        // 1=hotbar slot (target=slot), 2=outside window (target unused). button: 0=left, 1=right.
        private readonly System.Collections.Concurrent.ConcurrentQueue<(int Kind, int Target, int Button)> _inventoryClicks = new();
        // Hovered unified inventory slot (-1 = none) while the E menu is open, for Q-to-drop.
        private int _hoveredInventorySlot = -1;
        // Biome teleport menu selections (biome name string).
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _biomeSelections = new();

        // ---- Block-break particles ---------------------------------------------------
        // Small camera-facing quads textured with the broken block's tile. Simulated on the CPU,
        // drawn with the world pipeline so they depth-test against terrain.
        private struct BlockParticle
        {
            public float X, Y, Z;
            public float VX, VY, VZ;
            public float Age, Lifetime;
            public float Size;
            public float TileX, TileY, TileW, TileH; // atlas pixels
            public float Brightness;
        }
        private readonly BlockParticle[] _particles = new BlockParticle[512];
        private int _particleCount;
        private DeviceBuffer? _particleVertexBuffer;
        private DeviceBuffer? _particleIndexBuffer;
        private uint _particleVertexCapacityBytes;
        private uint _particleIndexCapacityBytes;
        private float[] _particleVertexScratch = Array.Empty<float>();
        private ushort[] _particleIndexScratch = Array.Empty<ushort>();
        private Vector3 _cameraRight = Vector3.UnitX;
        private Vector3 _cameraUp = Vector3.UnitY;
        private readonly System.Diagnostics.Stopwatch _particleClock = System.Diagnostics.Stopwatch.StartNew();
        private long _lastParticleTicks;

        // ---- Falling blocks (gravity-simulated sand/gravel) ------------------------------
        // Real 3D cubes of the block's tiles, drawn with an INSTANCED pipeline: one static cube
        // mesh is uploaded once, and each frame only a tiny per-instance buffer (world pos +
        // tile rect) is updated. For hundreds of falling blocks this uploads ~2.8KB instead of
        // rebuilding + re-uploading full cube geometry (~500KB) - the difference between a big
        // cave-in being smooth vs stuttering.
        private IReadOnlyList<Cubuild.FallingBlockData> _fallingBlocks = Array.Empty<Cubuild.FallingBlockData>();
        private DeviceBuffer? _fallingVertexBuffer;  // static cube mesh (once)
        private DeviceBuffer? _fallingIndexBuffer;   // static cube indices (once)
        private DeviceBuffer? _fallingInstanceBuffer; // per-frame instance data (dynamic)
        private uint _fallingInstanceCapacity;
        private float[] _fallingInstanceScratch = Array.Empty<float>();
        private Pipeline? _fallingPipeline;
        private const int FallingCubeVerts = 24;  // 6 faces x 4
        private const int FallingCubeIndices = 36;

        // Dropped items reuse the falling-block pipeline with a SMALLER static cube mesh
        // (scale ~0.3) so survival mining drops read as little collectible blocks.
        private const float ItemDropScale = 0.3f;
        private IReadOnlyList<Cubuild.ItemDropRenderData> _itemDrops = Array.Empty<Cubuild.ItemDropRenderData>();
        private Pipeline? _itemDropPipeline;
        private DeviceBuffer? _itemDropVertexBuffer;
        private DeviceBuffer? _itemDropIndexBuffer;
        private DeviceBuffer? _itemDropInstanceBuffer;
        private uint _itemDropInstanceCapacity;
        private float[] _itemDropInstanceScratch = Array.Empty<float>();
        // Genuine items (2D sprites from items.png) drop as camera-facing flat quads instead of
        // tumbling cubes; held genuine items render as a camera-space flat sprite on the fist.
        private Pipeline? _itemDropSpritePipeline;
        private Pipeline? _heldBlockSpritePipeline;
        private DeviceBuffer? _spriteVertexBuffer; // unit quad: corner(3) + uv(2) + shade(4)
        private DeviceBuffer? _spriteIndexBuffer;   // 6 indices
        // Cube face geometry (same as Mesher.FaceVertices): back/front/bottom/top/right/left.
        private static readonly float[][] FallingCubeFaces = new float[][]
        {
            new[] { 0f,0f,0f, 1f,0f,0f, 1f,1f,0f, 0f,1f,0f }, // back (-Z)
            new[] { 1f,0f,1f, 0f,0f,1f, 0f,1f,1f, 1f,1f,1f }, // front (+Z)
            new[] { 0f,0f,0f, 1f,0f,0f, 1f,0f,1f, 0f,0f,1f }, // bottom (-Y)
            new[] { 0f,1f,0f, 0f,1f,1f, 1f,1f,1f, 1f,1f,0f }, // top (+Y)
            new[] { 1f,0f,1f, 1f,0f,0f, 1f,1f,0f, 1f,1f,1f }, // right (+X)
            new[] { 0f,0f,0f, 0f,0f,1f, 0f,1f,1f, 0f,1f,0f }, // left (-X)
        };
        // Per-face shading multipliers (top 1.0 / bottom 0.5 / E+W 0.6 / N+S 0.8).
        private static readonly float[] FallingFaceShade = new[] { 0.8f, 0.8f, 0.5f, 1.0f, 0.6f, 0.6f };
        private static readonly Point3D[] FallingFaceNormals = new Point3D[]
        {
            new Point3D(0,0,-1), new Point3D(0,0,1), new Point3D(0,-1,0),
            new Point3D(0,1,0), new Point3D(1,0,0), new Point3D(-1,0,0),
        };

        // Chunk world mesh: one shared growable vertex/index buffer pair drawn with a single
        // DrawIndexedIndirect call (one IndirectDrawIndexedArguments per live chunk). Chunk-local
        // 16-bit indices stay zero-based; each draw command supplies the absolute FirstIndex
        // (index-buffer offset in index units) and VertexOffset (base vertex into the merged VB),
        // so chunks never need their indices remapped. Removed/re-meshed chunks leave reusable
        // holes tracked in _freeBlocks. Buffer growth is a GPU CopyBuffer into a 2x buffer,
        // recorded after Begin() and before the world draw; the old buffer is released via
        // DisposeWhenIdle once the GPU is done with it.
        private const uint VertexStrideBytes = 24;   // packed: Float3 + 3x UInt1 = 6 uint32s
        private const uint IndirectCommandStride = 20; // sizeof(IndirectDrawIndexedArguments)
        private DeviceBuffer? _megaVertexBuffer;
        private DeviceBuffer? _megaIndexBuffer;
        private DeviceBuffer? _indirectBuffer;
        private uint _vbTailBytes;
        private uint _ibTailBytes;
        private uint _vbCapacityBytes;
        private uint _ibCapacityBytes;
        private uint _indirectCapacityCommands;

        private struct ChunkRange
        {
            public uint VbOffsetBytes;
            public uint VbBytes;
            public uint IbOffsetBytes;
            public uint IndexCount;
        }
        private readonly Dictionary<Cubuild.ChunkCoordinates, ChunkRange> _chunkRanges = new();
        // Cutout (cross plants / leaves, alpha-tested, depth-writing) and transparent (water,
        // blended, no depth-write) faces live in separate ranges drawn as their own passes.
        private readonly Dictionary<Cubuild.ChunkCoordinates, ChunkRange> _cutoutRanges = new();
        private readonly Dictionary<Cubuild.ChunkCoordinates, ChunkRange> _glassRanges = new();
        private readonly Dictionary<Cubuild.ChunkCoordinates, ChunkRange> _transparentRanges = new();
        private readonly List<(uint VbOffset, uint VbBytes, uint IbOffset, uint IbBytes)> _freeBlocks = new();
        private readonly List<(Cubuild.ChunkCoordinates Coord, IndirectDrawIndexedArguments Cmd)> _drawCommands = new();
        private readonly List<(Cubuild.ChunkCoordinates Coord, IndirectDrawIndexedArguments Cmd)> _cutoutDrawCommands = new();
        private readonly List<(Cubuild.ChunkCoordinates Coord, IndirectDrawIndexedArguments Cmd)> _glassDrawCommands = new();
        private readonly List<(Cubuild.ChunkCoordinates Coord, IndirectDrawIndexedArguments Cmd)> _transparentDrawCommands = new();
        private IndirectDrawIndexedArguments[] _indirectScratch = Array.Empty<IndirectDrawIndexedArguments>();
        private IndirectDrawIndexedArguments[] _cutoutIndirectScratch = Array.Empty<IndirectDrawIndexedArguments>();
        private IndirectDrawIndexedArguments[] _glassIndirectScratch = Array.Empty<IndirectDrawIndexedArguments>();
        private IndirectDrawIndexedArguments[] _transparentIndirectScratch = Array.Empty<IndirectDrawIndexedArguments>();
        private bool _drawCommandsDirty = true;

        // GPU-assisted frustum culling: a compute pass reads each chunk's AABB + draw
        // command, tests the 6 frustum planes in parallel, and zeroes InstanceCount for culled
        // chunks. It writes args into a StructuredBufferReadWrite scratch, which is copied into
        // the IndirectBuffer for the draw - no CPU scan, no scratch copy on CPU, no readback.
        // The player picks the mode in Settings (Auto/Cpu/Gpu); Auto defaults to CPU-side
        // frustum culling as the safe baseline across all GPUs (some Intel drivers produce wrong
        // results from the GPU cull compute shader) and only enables GPU on NVIDIA/AMD.
        private CullingMode _cullMode = CullingMode.Auto;
        private bool _gpuCullEnabled = false;
        private bool _gpuCullSupported;
        private Pipeline _cullPipeline;
        private ResourceLayout _cullDataLayout;   // set 0: frustum planes (uniform)
        private ResourceLayout _cullChunkLayout;  // set 1: chunk AABB/command data + args out
        private DeviceBuffer _frustumBuffer;
        private ResourceSet _frustumSet;
        private DeviceBuffer _cullDataBuffer;     // per-pass chunk cull data (StructuredBufferReadOnly)
        private ResourceSet _cullDataReadSet;     // binds cull data (set 1 binding 0)
        private DeviceBuffer _cullArgsBuffer;     // compute output (StructuredBufferReadWrite)
        private ResourceSet _cullArgsWriteSet;    // binds cull args (set 1 binding 1)
        private uint _cullDataCapacityCommands;

        // Per-pass cull data is packed 11 uint32s per chunk: AABB min xyz (3 float bits), AABB
        // max xyz (3), then the IndirectDrawIndexedArguments (5). Refreshed when draw commands
        // change (RebuildDrawCommands / GPU-cull frame).
        private uint[] _opaqueCullData = Array.Empty<uint>();
        private uint[] _cutoutCullData = Array.Empty<uint>();
        private uint[] _glassCullData = Array.Empty<uint>();
        private uint[] _transparentCullData = Array.Empty<uint>();
        private readonly float[] _cullPlaneFloats = new float[24];
        private bool _gpuCullDataDirty = true;

        // Per-instance brightness multiplier (0..1) set before each mob write; combines the global
        // night dim with the mob's position-specific block light (GetMobLight).
        private float _entityLight = 1f;

        // Pending GPU-side buffer growth copies (old -> new, recorded after cl.Begin()).
        private readonly List<(DeviceBuffer Old, DeviceBuffer New, uint SizeBytes)> _pendingBufferCopies = new();

        private readonly System.Collections.Concurrent.ConcurrentQueue<PendingUpload> _pendingUploads = new();
        private readonly System.Collections.Concurrent.ConcurrentQueue<PendingUpload> _pendingPriorityUploads = new(); // player edits jump the line
        private readonly System.Collections.Concurrent.ConcurrentQueue<Cubuild.ChunkCoordinates> _pendingRemovals = new();
        private ChunkManager? _chunkManager; // set via SetChunkManager, used by MeshChunkImmediate
        // Cached mining-block light (0..15), captured once per mining CHUNK like the C++
        // startBreaking: the max of the 6 neighbors' combined light. Used to shade the shrink
        // cube + walls with the same brightness as regular world blocks.
        private int _miningLightLevel = 15;
        private long _miningLightChunkKey = long.MinValue;
        // Upload budget per frame to avoid large spikes
        private int _maxUploadsPerFrame = 4;

        private readonly struct PendingUpload
        {
            public Cubuild.ChunkCoordinates Coord { get; }
            public uint[] Vertices { get; }
            public ushort[] Indices { get; }
            public uint[] CutoutVertices { get; }
            public ushort[] CutoutIndices { get; }
            public uint[] GlassVertices { get; }
            public ushort[] GlassIndices { get; }
            public uint[] TransparentVertices { get; }
            public ushort[] TransparentIndices { get; }

            public PendingUpload(Cubuild.ChunkCoordinates coord, uint[] vertices, ushort[] indices,
                uint[] cutoutVertices, ushort[] cutoutIndices, uint[] glassVertices, ushort[] glassIndices,
                uint[] transparentVertices, ushort[] transparentIndices)
            {
                Coord = coord;
                Vertices = vertices;
                Indices = indices;
                CutoutVertices = cutoutVertices;
                CutoutIndices = cutoutIndices;
                GlassVertices = glassVertices;
                GlassIndices = glassIndices;
                TransparentVertices = transparentVertices;
                TransparentIndices = transparentIndices;
            }
        }

        public void Initialize(GraphicsDevice graphicsDevice, Swapchain swapchain)
        {
            _gd = graphicsDevice ?? throw new ArgumentNullException(nameof(graphicsDevice));
            _sc = swapchain ?? throw new ArgumentNullException(nameof(swapchain));

            _projViewBuffer = _gd.ResourceFactory.CreateBuffer(new BufferDescription(64, BufferUsage.UniformBuffer | BufferUsage.Dynamic));
            _projViewLayout = _gd.ResourceFactory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("ProjectionView", ResourceKind.UniformBuffer, ShaderStages.Vertex)));
            _projViewSet = _gd.ResourceFactory.CreateResourceSet(new ResourceSetDescription(_projViewLayout, _projViewBuffer));

            // Load atlas texture into a GPU texture (no GDI+/System.Drawing dependency).
            // Prefer the copy embedded in the assembly so a single self-contained .exe needs no
            // loose files; fall back to terrain.png next to the executable for local dev.
            try
            {
                byte[]? fileBytes = LoadAtlasBytes();
                if (fileBytes != null)
                {
                    var image = StbImageSharp.ImageResult.FromMemory(fileBytes, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
                    int w = image.Width;
                    int h = image.Height;
                    _atlasWidth = w;
                    _atlasHeight = h;
                    _atlasPixelsW = w;
                    _atlasPixelsH = h;
                    _atlasRgba = (byte[])image.Data.Clone();

                    // Extract the 4 water animation frames (16x16 each) so the per-frame
                    // crossfade can re-upload just the strip without touching the source copy.
                    if (w >= 256 && h >= 240)
                    {
                        int atlasRowBytes = w * 4;
                        _waterFrames = new byte[WaterFrameCount * 16 * 16 * 4];
                        for (int f = 0; f < WaterFrameCount; f++)
                        {
                            int srcX = (WaterTileX + f) * 16;
                            int srcY = WaterTileY * 16;
                            for (int y = 0; y < 16; y++)
                            {
                                Array.Copy(image.Data, (srcY + y) * atlasRowBytes + srcX * 4,
                                    _waterFrames, (f * 16 + y) * 16 * 4, 16 * 4);
                            }
                        }
                        _waterStrip = new byte[64 * 16 * 4];
                        _waterClock.Restart();
                    }

                    var texDesc = TextureDescription.Texture2D((uint)w, (uint)h, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled);
                    _atlasTexture = _gd.ResourceFactory.CreateTexture(texDesc);
                    _gd.UpdateTexture(_atlasTexture, image.Data, 0, 0, 0, (uint)w, (uint)h, 1, 0, 0);
                    _atlasView = _gd.ResourceFactory.CreateTextureView(_atlasTexture);
                    _atlasSampler = _gd.ResourceFactory.CreateSampler(new SamplerDescription(
                        SamplerAddressMode.Wrap,
                        SamplerAddressMode.Wrap,
                        SamplerAddressMode.Wrap,
                        SamplerFilter.MinPoint_MagPoint_MipPoint,
                        null,
                        1,
                        0,
                        0,
                        0,
                        SamplerBorderColor.TransparentBlack));
                }
            }
            catch
            {
                // ignore; texture optional
            }

            // Load the items atlas (items.png) the same way - embedded copy first, loose file
            // fallback. Only needed for item-tile drops (flint); harmless if it's missing.
            try
            {
                byte[]? itemBytes = LoadImageBytes("items.png");
                if (itemBytes != null)
                {
                    var itemImage = StbImageSharp.ImageResult.FromMemory(itemBytes, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
                    var itemDesc = TextureDescription.Texture2D((uint)itemImage.Width, (uint)itemImage.Height, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled);
                    _itemsAtlasTexture = _gd.ResourceFactory.CreateTexture(itemDesc);
                    _gd.UpdateTexture(_itemsAtlasTexture, itemImage.Data, 0, 0, 0, (uint)itemImage.Width, (uint)itemImage.Height, 1, 0, 0);
                    _itemsAtlasView = _gd.ResourceFactory.CreateTextureView(_itemsAtlasTexture);
                    // Keep the CPU copy + dims: flat 2D item icons are cut from these pixels.
                    _itemsAtlasRgba = (byte[])itemImage.Data.Clone();
                    _itemsAtlasPixelsW = itemImage.Width;
                    _itemsAtlasPixelsH = itemImage.Height;
                }
            }
            catch
            {
                // ignore; item drops fall back to terrain tiles
            }

            LoadDuckResources();
            LoadPlayerResources();
            LoadMobResources();
            CreatePipeline();
            CreateCullComputePipeline();

            // Resolution scale: create the offscreen target only when the player lowers the scale
            // (default 1 = render straight to the swapchain, zero extra cost).
            RecreateScaleTargets();

            _imguiRenderer = new ImGuiRenderer(
                _gd,
                _sc.Framebuffer.OutputDescription,
                Math.Max(1, (int)_sc.Framebuffer.Width),
                Math.Max(1, (int)_sc.Framebuffer.Height));

            // Build the isometric block-icon atlas (needs the ImGui renderer for its texture binding).
            BuildIconAtlas();
            // Build the flat item-icon atlas for genuine items (tools, food, gems).
            BuildItemIconAtlas();

            // Bind the terrain atlas to ImGui so the menus can draw the dirt background.
            if (_imguiRenderer != null && _atlasView != null)
            {
                _terrainImGuiId = _imguiRenderer.GetOrCreateImGuiBinding(_gd.ResourceFactory, _atlasView);
            }

            LoadLogo();
            LoadHotbarTextures();
            LoadInventoryTexture();
            LoadCraftingTexture();
            LoadHealthbarTexture();
        }

        // Loads the original C++ E-menu background (inventory.png) and exposes it to ImGui.
        public void Dispose()
        {
            _chunkRanges.Clear();
            _freeBlocks.Clear();
            _drawCommands.Clear();
            _megaVertexBuffer?.Dispose();
            _megaIndexBuffer?.Dispose();
            _indirectBuffer?.Dispose();
            _cullPipeline?.Dispose();
            _cullDataLayout?.Dispose();
            _cullChunkLayout?.Dispose();
            _frustumBuffer?.Dispose();
            _frustumSet?.Dispose();
            _cullDataBuffer?.Dispose();
            _cullArgsBuffer?.Dispose();
            _cullDataReadSet?.Dispose();
            _cullArgsWriteSet?.Dispose();
            _particleVertexBuffer?.Dispose();
            _particleIndexBuffer?.Dispose();

            _projViewSet?.Dispose();
            _projViewLayout?.Dispose();
            _projViewBuffer?.Dispose();
            _fogSet?.Dispose();
            _fogLayout?.Dispose();
            _fogBuffer?.Dispose();
            _skyPipeline?.Dispose();
            _skyVertexBuffer?.Dispose();
            _skyIndexBuffer?.Dispose();
            _skyFogSet?.Dispose();
            _skyFogLayout?.Dispose();
            _skyFogBuffer?.Dispose();
            _skyMatrixSet?.Dispose();
            _skyMatrixBuffer?.Dispose();
            _celestialPipeline?.Dispose();
            _celestialVertexBuffer?.Dispose();
            _celestialIndexBuffer?.Dispose();
            _sunTextureSet?.Dispose();
            _moonTextureSet?.Dispose();
            _celestialTextureLayout?.Dispose();
            _sunView?.Dispose();
            _sunTexture?.Dispose();
            _moonView?.Dispose();
            _moonTexture?.Dispose();
            _celestialSampler?.Dispose();
            _starPipeline?.Dispose();
            _starVertexBuffer?.Dispose();
            _starIndexBuffer?.Dispose();
            _galaxyPipeline?.Dispose();
            _galaxyVertexBuffer?.Dispose();
            _galaxyIndexBuffer?.Dispose();
            _cloudPipeline?.Dispose();
            _cloudVertexBuffer?.Dispose();
            _cloudIndexBuffer?.Dispose();
            _cloudParamsSet?.Dispose();
            _cloudParamsLayout?.Dispose();
            _cloudParamsBuffer?.Dispose();
            _cloudTextureView?.Dispose();
            _cloudTexture?.Dispose();
            _cloudMatrixBuffer?.Dispose();
            _cloudMatrixSet?.Dispose();
            _worldPlanePipeline?.Dispose();
            _worldPlaneVertexBuffer?.Dispose();
            _worldPlaneIndexBuffer?.Dispose();
            _worldPlaneTextureSet?.Dispose();
            _worldPlaneLayout?.Dispose();
            _worldPlaneTextureView?.Dispose();
            _worldPlaneTexture?.Dispose();
            _worldPlaneMatrixSet?.Dispose();
            _commandList?.Dispose();
            _imguiRenderer?.Dispose();
            _scaleColorTexture?.Dispose();
            _scaleColorView?.Dispose();
            _scaleDepthTexture?.Dispose();
            _scaleFramebuffer?.Dispose();
            _blitResourceSet?.Dispose();
            _blitPipeline?.Dispose();
            _blitLayout?.Dispose();
            _blitSamplerLinear?.Dispose();
            _blitSamplerNearest?.Dispose();
            _highlightVertexBuffer?.Dispose();
            _highlightIndexBuffer?.Dispose();
            _highlightPipeline?.Dispose();
            _shrinkCubeVertexBuffer?.Dispose();
            _shrinkCubeIndexBuffer?.Dispose();
            _shrinkCubePipeline?.Dispose();
            _shrinkWallPipeline?.Dispose();
            _duckVertexBuffer?.Dispose();
            _duckIndexBuffer?.Dispose();
            _duckTextureSet?.Dispose();
            _duckSampler?.Dispose();
            _duckView?.Dispose();
            _duckTexture?.Dispose();
            _playerVertexBuffer?.Dispose();
            _playerIndexBuffer?.Dispose();
            _playerTextureSet?.Dispose();
            _playerSampler?.Dispose();
            _playerView?.Dispose();
            _playerTexture?.Dispose();
            foreach (var kvp in _modelMobs)
            {
                kvp.Value.Model?.Dispose();
                kvp.Value.VertexBuffer?.Dispose();
                kvp.Value.IndexBuffer?.Dispose();
            }
            _modelPipeline?.Dispose();
            _pipeline?.Dispose();
            _cutoutPipeline?.Dispose();
            _glassPipeline?.Dispose();
            _transparentPipeline?.Dispose();
            _translucentPipeline?.Dispose();
            if (_iconAtlasTexture != null && _imguiRenderer != null) _imguiRenderer.RemoveImGuiBinding(_iconAtlasTexture);
            if (_atlasTexture != null && _imguiRenderer != null) _imguiRenderer.RemoveImGuiBinding(_atlasTexture);
            if (_logoTexture != null && _imguiRenderer != null) _imguiRenderer.RemoveImGuiBinding(_logoTexture);
            _logoView?.Dispose();
            _logoTexture?.Dispose();
            if (_hotbarTexture != null && _imguiRenderer != null) _imguiRenderer.RemoveImGuiBinding(_hotbarTexture);
            if (_hotbarSelectTexture != null && _imguiRenderer != null) _imguiRenderer.RemoveImGuiBinding(_hotbarSelectTexture);
            _hotbarView?.Dispose();
            _hotbarTexture?.Dispose();
            _hotbarSelectView?.Dispose();
            _hotbarSelectTexture?.Dispose();
            if (_healthbarTexture != null && _imguiRenderer != null) _imguiRenderer.RemoveImGuiBinding(_healthbarTexture);
            if (_healthbarFlashTexture != null && _imguiRenderer != null) _imguiRenderer.RemoveImGuiBinding(_healthbarFlashTexture);
            _healthbarView?.Dispose();
            _healthbarTexture?.Dispose();
            _healthbarFlashView?.Dispose();
            _healthbarFlashTexture?.Dispose();
            _iconAtlasView?.Dispose();
            _iconAtlasTexture?.Dispose();
            _sc?.Dispose();
            _gd?.Dispose();
        }

        public void UploadChunk(Cubuild.ChunkCoordinates coords, System.Collections.Generic.IReadOnlyList<Cubuild.MeshFace> faces)
        {
            BuildMesh(faces, out var vArr, out var iArr, out var cvArr, out var ciArr, out var gvArr, out var giArr, out var tvArr, out var tiArr);
            _pendingUploads.Enqueue(new PendingUpload(coords, vArr, iArr, cvArr, ciArr, gvArr, giArr, tvArr, tiArr));
        }

        // Player edits jump the line: same vertex data, but enqueued on the priority queue that
        // ProcessPendingPriorityMeshes drains every frame for instant feedback.
        public void UploadChunkPriority(Cubuild.ChunkCoordinates coords, System.Collections.Generic.IReadOnlyList<Cubuild.MeshFace> faces)
        {
            BuildMesh(faces, out var vArr, out var iArr, out var cvArr, out var ciArr, out var gvArr, out var giArr, out var tvArr, out var tiArr);
            _pendingPriorityUploads.Enqueue(new PendingUpload(coords, vArr, iArr, cvArr, ciArr, gvArr, giArr, tvArr, tiArr));
        }

        // Builds the packed 24-byte-per-vertex chunk mesh (Float3 pos + 3x UInt1 packs) from greedy
        // faces. Per-face alpha routes each face to one of four buffer pairs:
        //   alpha >= 1   -> opaque (culled, depth-writing)
        //   alpha < -10  -> glass (alpha-tested, depth-WRITE OFF, front-side; marker set by the
        //                   mesher as -(alpha) - 100)
        //   -10 <= a < 0 -> cutout (alpha-tested sprites: cross plants, leaves) - depth-writing so
        //                   near quads occlude far ones
        //   0 <= a < 1   -> transparent (water) - blended, depth-write-free
        // Sizes are deterministic (4 verts + 6 indices per face), so the target arrays are filled
        // directly - no List<T>, no ToArray() copies, no double allocation per chunk upload.
        private void BuildMesh(
            System.Collections.Generic.IReadOnlyList<Cubuild.MeshFace> faces,
            out uint[] vertsArr, out ushort[] indicesArr,
            out uint[] cutoutVertsArr, out ushort[] cutoutIndicesArr,
            out uint[] glassVertsArr, out ushort[] glassIndicesArr,
            out uint[] transVertsArr, out ushort[] transIndicesArr)
        {
            // vertex layout: position(3 floats) + aPack1(1 uint) + aPack2(1 uint) + aPack3(1 uint)
            // = 6 uint32s per vertex (24 bytes), decoded in the vertex shader.
            int faceCount = faces.Count;
            int opaqueFaces = 0;
            int cutoutFaces = 0;
            int glassFaces = 0;
            for (int i = 0; i < faceCount; i++)
            {
                if (faces[i].Alpha >= 1f) opaqueFaces++;
                else if (faces[i].Alpha < -10f) glassFaces++;
                else if (faces[i].Alpha < 0f) cutoutFaces++;
            }
            int transFaces = faceCount - opaqueFaces - cutoutFaces - glassFaces;

            var verts = new uint[opaqueFaces * 4 * 6];
            var indices = new ushort[opaqueFaces * 6];
            var cutoutVerts = new uint[cutoutFaces * 4 * 6];
            var cutoutIndices = new ushort[cutoutFaces * 6];
            var glassVerts = new uint[glassFaces * 4 * 6];
            var glassIndices = new ushort[glassFaces * 6];
            var transVerts = new uint[transFaces * 4 * 6];
            var transIndices = new ushort[transFaces * 6];
            // Hoisted out of the face loop: stackalloc reserves stack for the method, so a
            // per-face stackalloc would grow the frame with face count (CA2014).
            Span<Cubuild.Point3D> vertsSpan = stackalloc Cubuild.Point3D[4];
            int opaqueFace = 0;
            int cutoutFace = 0;
            int glassFace = 0;
            int transFace = 0;
            for (int fi = 0; fi < faceCount; fi++)
            {
                var f = faces[fi];
                bool isGlass = f.Alpha < -10f;
                bool isCutout = !isGlass && f.Alpha < 0f;
                bool isTrans = !isGlass && !isCutout && f.Alpha < 1f;
                var dstVerts = isGlass ? glassVerts : (isCutout ? cutoutVerts : (isTrans ? transVerts : verts));
                var dstIndices = isGlass ? glassIndices : (isCutout ? cutoutIndices : (isTrans ? transIndices : indices));
                int faceIdx = isGlass ? glassFace : (isCutout ? cutoutFace : (isTrans ? transFace : opaqueFace));
                int vertexStart = faceIdx * 4;

                vertsSpan[0] = f.V0;
                vertsSpan[1] = f.V1;
                vertsSpan[2] = f.V2;
                vertsSpan[3] = f.V3;
                int tileW = Math.Max(1, f.SrcRect.Width);
                int tileH = Math.Max(1, f.SrcRect.Height);
                int spanU = Math.Max(1, f.TileWidth);
                int spanV = Math.Max(1, f.TileHeight);

                bool hasAxes = TryGetCubuildFaceAxes(f.Normal, out var uAxis, out var vAxis);
                double minU = 0.0;
                double minV = 0.0;
                // For fluid side walls (AnchorVBottom) the tile is planted at the block bottom:
                // the surface vertex must sample (1 - wallHeight) down the tile and the bottom
                // vertex must sample the tile bottom. With vAxis=(0,-1,0) the raw dv measures
                // from the TOP, so we shift it by (1 - height)
                // ((var51 + (1.0F - var31) * 16.0F) / 256.0F).
                double anchorVOffset = 0.0;
                if (hasAxes)
                {
                    minU = double.PositiveInfinity;
                    minV = double.PositiveInfinity;
                    double maxU = double.NegativeInfinity;
                    double maxV = double.NegativeInfinity;

                    for (int ci = 0; ci < 4; ci++)
                    {
                        var c = vertsSpan[ci];
                        double u = Dot(c, uAxis);
                        double v = Dot(c, vAxis);
                        if (u < minU) minU = u;
                        if (u > maxU) maxU = u;
                        if (v < minV) minV = v;
                        if (v > maxV) maxV = v;
                    }

                    if (f.AnchorVBottom)
                    {
                        anchorVOffset = Math.Max(0.0, 1.0 - (maxV - minV));
                    }

                    spanU = Math.Max(1, (int)Math.Round(maxU - minU));
                    spanV = Math.Max(1, (int)Math.Round(maxV - minV));
                }

                // Compute the per-face alpha mode. Fragments keep the same 4-way split, but the
                // glass sentinels (-100 regular frame, -200 translucent tint) become a mode field
                // so they survive the 8-bit pack exactly.
                float shade = f.Shade;
                float alpha = f.Alpha;
                uint alphaMode;
                uint alphaByte;
                if (alpha >= 1f)
                {
                    alphaMode = 0;   // opaque
                    alphaByte = 255;
                }
                else if (alpha < -10f)
                {
                    alphaMode = alpha < -150f ? 2u : 1u; // translucent tint (-200) vs frame (-100)
                    alphaByte = 255;
                }
                else if (alpha < 0f)
                {
                    alphaMode = 0;   // cutout - alpha ignored by the sprite shader
                    alphaByte = 255;
                }
                else
                {
                    alphaMode = 0;   // transparent (water) - real blended alpha
                    alphaByte = (uint)Math.Clamp((int)Math.Round(alpha * 255f), 0, 255);
                }

                uint shadeByte = (uint)Math.Clamp((int)Math.Round(shade * 255f), 0, 255);
                // Tile rect as atlas texels: X/Y <= 240, W/H = 16 (256px atlas, 16px tiles).
                uint tileX = (uint)Math.Clamp(f.SrcRect.X, 0, 255);
                uint tileY = (uint)Math.Clamp(f.SrcRect.Y, 0, 255);
                uint pack2 = (tileX << 24) | (tileY << 16) | ((uint)Math.Clamp(tileW, 0, 255) << 8) | (uint)Math.Clamp(tileH, 0, 255);
                uint pack3 = shadeByte | (alphaByte << 8) | (alphaMode << 16);

                // Face-edge basis for the non-axis (fluid slope) UV fallback.
                var v0p = vertsSpan[0];
                var edgeU = vertsSpan[1] - v0p;
                var edgeV = vertsSpan[3] - v0p;
                double denomU = edgeU.X * edgeU.X + edgeU.Y * edgeU.Y + edgeU.Z * edgeU.Z;
                double denomV = edgeV.X * edgeV.X + edgeV.Y * edgeV.Y + edgeV.Z * edgeV.Z;

                int vertWrite = vertexStart * 6;
                for (int i = 0; i < 4; i++)
                {
                    var vv = vertsSpan[i];
                    double du;
                    double dv;
                    if (hasAxes)
                    {
                        du = Dot(vv, uAxis) - minU;
                        dv = Dot(vv, vAxis) - minV;
                        du = Math.Clamp(du, 0.0, spanU);
                        dv = Math.Clamp(dv + anchorVOffset, 0.0, spanV);
                    }
                    else
                    {
                        var rel = vv - v0p;
                        du = denomU > 0 ? (rel.X * edgeU.X + rel.Y * edgeU.Y + rel.Z * edgeU.Z) / denomU : 0.0;
                        dv = denomV > 0 ? (rel.X * edgeV.X + rel.Y * edgeV.Y + rel.Z * edgeV.Z) / denomV : 0.0;
                        du = Math.Clamp(du, 0.0, 1.0) * spanU;
                        dv = Math.Clamp(dv, 0.0, 1.0) * spanV;
                    }

                    // Pack du/dv as 8.8 fixed point (0..255 blocks, 1/256 block precision).
                    uint duFixed = (uint)Math.Clamp((int)Math.Round(du * 256.0), 0, 0xFFFF);
                    uint dvFixed = (uint)Math.Clamp((int)Math.Round(dv * 256.0), 0, 0xFFFF);
                    uint pack1 = (duFixed << 16) | dvFixed;

                    // Position keeps full float precision (world coords).
                    dstVerts[vertWrite] = BitConverter.SingleToUInt32Bits((float)vv.X);
                    dstVerts[vertWrite + 1] = BitConverter.SingleToUInt32Bits((float)vv.Y);
                    dstVerts[vertWrite + 2] = BitConverter.SingleToUInt32Bits((float)vv.Z);
                    dstVerts[vertWrite + 3] = pack1;
                    dstVerts[vertWrite + 4] = pack2;
                    dstVerts[vertWrite + 5] = pack3;
                    vertWrite += 6;
                }

                int ib = faceIdx * 6;
                dstIndices[ib + 0] = (ushort)(vertexStart + 0);
                dstIndices[ib + 1] = (ushort)(vertexStart + 1);
                dstIndices[ib + 2] = (ushort)(vertexStart + 2);
                dstIndices[ib + 3] = (ushort)(vertexStart + 0);
                dstIndices[ib + 4] = (ushort)(vertexStart + 2);
                dstIndices[ib + 5] = (ushort)(vertexStart + 3);

                if (isGlass) glassFace++;
                else if (isCutout) cutoutFace++;
                else if (isTrans) transFace++;
                else opaqueFace++;
            }

            vertsArr = verts;
            indicesArr = indices;
            cutoutVertsArr = cutoutVerts;
            cutoutIndicesArr = cutoutIndices;
            glassVertsArr = glassVerts;
            glassIndicesArr = glassIndices;
            transVertsArr = transVerts;
            transIndicesArr = transIndices;
        }

        public void RemoveChunk(Cubuild.ChunkCoordinates coords)
        {
            // Enqueue removal to be processed on render thread
            _pendingRemovals.Enqueue(coords);
        }

        private static bool TryGetCubuildFaceAxes(Cubuild.Point3D normal, out Cubuild.Point3D uAxis, out Cubuild.Point3D vAxis)
        {
            if (normal.X > 0.5)
            {
                uAxis = new Cubuild.Point3D(0, 0, -1);
                vAxis = new Cubuild.Point3D(0, -1, 0);
                return true;
            }

            if (normal.X < -0.5)
            {
                uAxis = new Cubuild.Point3D(0, 0, 1);
                vAxis = new Cubuild.Point3D(0, -1, 0);
                return true;
            }

            if (normal.Z > 0.5)
            {
                uAxis = new Cubuild.Point3D(1, 0, 0);
                vAxis = new Cubuild.Point3D(0, -1, 0);
                return true;
            }

            if (normal.Z < -0.5)
            {
                uAxis = new Cubuild.Point3D(-1, 0, 0);
                vAxis = new Cubuild.Point3D(0, -1, 0);
                return true;
            }

            if (normal.Y > 0.5)
            {
                uAxis = new Cubuild.Point3D(1, 0, 0);
                vAxis = new Cubuild.Point3D(0, 0, -1);
                return true;
            }

            if (normal.Y < -0.5)
            {
                uAxis = new Cubuild.Point3D(1, 0, 0);
                vAxis = new Cubuild.Point3D(0, 0, 1);
                return true;
            }

            uAxis = new Cubuild.Point3D(0, 0, 0);
            vAxis = new Cubuild.Point3D(0, 0, 0);
            return false;
        }

        private static double Dot(Cubuild.Point3D a, Cubuild.Point3D b)
        {
            return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        }

        public void UpdateCamera(Cubuild.Point3D position, float yaw, float pitch,
            float walkPhase = 0f, float walkAmount = 0f, bool firstPerson = false, bool grounded = true)
        {
            _firstPersonCamera = firstPerson;
            _handWalkPhase = walkPhase;
            _handWalkAmount = walkAmount;
            var proj = Matrix4x4.CreatePerspectiveFieldOfView((float)(Math.PI / 2.0), (float)_sc.Framebuffer.Width / _sc.Framebuffer.Height, _nearPlane, _farPlane);
            var yawRad = yaw * (float)Math.PI / 180f;
            var pitchRad = pitch * (float)Math.PI / 180f;
            var forward = new Vector3((float)(Math.Cos(pitchRad) * Math.Sin(yawRad)), (float)Math.Sin(pitchRad), (float)(Math.Cos(pitchRad) * Math.Cos(yawRad)));
            var cameraPos = new Vector3((float)position.X, (float)position.Y, (float)position.Z);
            // First-person head bob: a soft vertical bounce (one per step) plus a slight side sway
            // scaled by how fast you're actually moving. Only while grounded - never mid-air, and
            // the bob EASES in/out instead of snapping, so landing after a jump doesn't jolt.
            if (firstPerson)
            {
                float bobTarget = (grounded && walkAmount > 0.02f) ? 1f : 0f;
                _bobBlend += (bobTarget - _bobBlend) * 0.18f; // ease over ~0.2s at 60fps
                if (_bobBlend > 0.001f)
                {
                    float bobAmp = Math.Min(1f, walkAmount) * 0.09f * _bobBlend;
                    float swayAmp = Math.Min(1f, walkAmount) * 0.05f * _bobBlend;
                    float rightX = (float)Math.Cos(yawRad);
                    float rightZ = (float)-Math.Sin(yawRad);
                    float bobY = Math.Abs((float)Math.Sin(walkPhase)) * bobAmp;
                    float sway = (float)Math.Sin(walkPhase) * swayAmp;
                    cameraPos += new Vector3(rightX * sway, bobY, rightZ * sway);
                }
            }
            // Damage shake: a decaying random jitter plus a visible roll tilt (and a tiny flinch
            // back along the view axis), so a hit reads as a real "ow" in the POV. The strength
            // decays with t^2: a sharp kick at the moment of impact that settles quickly. Purely
            // visual - _cameraPosition (culling/collisions) keeps the real position below.
            if (_damageShakeTime > 0f)
            {
                float t = Math.Clamp(_damageShakeTime / DamageShakeDuration, 0f, 1f);
                float strength = _damageShakeMagnitude * t * t * 0.5f;
                cameraPos += new Vector3(
                    (float)((_shakeRandom.NextDouble() * 2.0 - 1.0) * strength),
                    (float)((_shakeRandom.NextDouble() * 2.0 - 1.0) * strength),
                    (float)((_shakeRandom.NextDouble() * 2.0 - 1.0) * strength));
                // Recoil flinch: shove the camera back along the view axis for a frame or two.
                float flinch = _damageShakeMagnitude * t * t * 0.18f;
                cameraPos -= forward * flinch;
            }
            var target = cameraPos + forward;
            var view = Matrix4x4.CreateLookAt(cameraPos, target, Vector3.UnitY);
            // Minecraft-style hit recoil: the whole view rolls around the camera axis with a
            // decaying wobble that settles back to level. Bumped the amplitude up (~18 deg peak)
            // and slowed the wobble so the tilt actually reads on screen.
            if (_damageShakeTime > 0f)
            {
                float t = Math.Clamp(_damageShakeTime / DamageShakeDuration, 0f, 1f);
                float rollAmp = _damageShakeMagnitude * t * t * 0.32f; // ~18 deg max
                float roll = (float)(Math.Sin(_damageShakeElapsed * 20f) * rollAmp) + rollAmp * 0.5f;
                view = view * Matrix4x4.CreateRotationZ(roll);
            }
            var viewProj = Matrix4x4.Multiply(view, proj);
            // Sky matrix: the view with its TRANSLATION removed (rotation only), so the camera-space
            // sky planes render locked to the eye.
            // are drawn with the camera transform applied - they follow the player automatically.
            var skyView = view;
            skyView.M41 = 0f;
            skyView.M42 = 0f;
            skyView.M43 = 0f;
            var skyViewProj = Matrix4x4.Multiply(skyView, proj);
            // Billboard basis for the particle system.
            _cameraRight = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
            _cameraUp = Vector3.Normalize(Vector3.Cross(_cameraRight, forward));
            // Cache the camera and view-projection so chunk frustum culling and the mob meshing
            // can read them without re-deriving.
            _cameraPosition = position;
            _viewProjection = viewProj;
            _gd.UpdateBuffer(_projViewBuffer, 0, ref viewProj);
            UpdateWaterAnimation();
            if (_skyMatrixBuffer != null)
                _gd.UpdateBuffer(_skyMatrixBuffer, 0, ref skyViewProj);
            if (_cloudMatrixBuffer != null)
            {
                // Same view, WIDER far plane (3x) so clouds / world-from-above stretch much
                // further than terrain. Near plane raised slightly to keep depth precision sane.
                var wideProj = Matrix4x4.CreatePerspectiveFieldOfView((float)(Math.PI / 2.0), (float)_sc.Framebuffer.Width / _sc.Framebuffer.Height, 1.0f, _cloudFarPlane);
                var wideVP = Matrix4x4.Multiply(view, wideProj);
                _gd.UpdateBuffer(_cloudMatrixBuffer, 0, ref wideVP);
            }
        }

        /// <summary>Crossfades the two current water frames into the atlas strip and re-uploads the
        /// 64x16 region. Runs once per rendered frame while the atlas exists; the upload is 4KB so
        /// the per-frame cost is negligible.</summary>
        private void UpdateWaterAnimation()
        {
            if (_waterFrames == null || _waterStrip == null || _atlasTexture == null) return;

            double elapsed = _waterClock.Elapsed.TotalSeconds;
            double phase = (elapsed / WaterCycleSeconds) * WaterFrameCount; // 0..4 looping
            int frameA = (int)Math.Floor(phase) % WaterFrameCount;
            int frameB = (frameA + 1) % WaterFrameCount;
            float fade = (float)(phase - Math.Floor(phase));

            // Blend frame A -> B. Still water (tile 12) and flowing water (tile 13, the mesher's
            // side/flow tile) share the animation, so the crossfade lands in BOTH columns 0 and
            // 1 of the strip (tiles 14-15 stay pristine). Writing into frameA's column instead
            // made the tile go stale for most of the cycle and snap back at the wrap.
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    int pixel = y * 16 + x;
                    int srcABase = (frameA * 256 + pixel) * 4;
                    int srcBBase = (frameB * 256 + pixel) * 4;
                    for (int c = 0; c < 4; c++)
                    {
                        int a = _waterFrames[srcABase + c];
                        int b = _waterFrames[srcBBase + c];
                        byte v = (byte)(a + (b - a) * fade);
                        _waterStrip[(y * 64 + x) * 4 + c] = v;          // tile 12 - still water
                        _waterStrip[(y * 64 + 16 + x) * 4 + c] = v;     // tile 13 - flowing water
                    }
                }
            }

            var handle = System.Runtime.InteropServices.GCHandle.Alloc(_waterStrip, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                _gd.UpdateTexture(_atlasTexture, handle.AddrOfPinnedObject(), (uint)_waterStrip.Length,
                    (uint)(WaterTileX * 16), (uint)(WaterTileY * 16), 0, 64, 16, 1, 0, 0);
            }
            finally
            {
                handle.Free();
            }
        }

        public void SetRenderDistance(int chunkRadius)
        {
            // The far plane must enclose the FULL loaded square, not a circle: chunks load within
            // +-chunkRadius in BOTH x and z (RequestChunksAround uses |dx|<=r AND |dz|<=r), so the
            // farthest loaded block sits at the square corner, chunkRadius*16*sqrt(2) away. If the
            // far plane were only chunkRadius*16 (a circle), looking diagonally would clip terrain
            // at 256 while chunks exist to 362 - a circular, camera-locked "invisible terrain" line
            // that does NOT follow chunk shapes. Enclosing the square makes the visible edge the
            // chunk boundary itself.
            // Far plane target is 700 blocks (user preference): sky/clouds/terrain stretch further
            // before clipping. Keep the chunk square fully enclosed even if the render distance
            // ever grows beyond 700's coverage. Near plane raised 0.1 -> 0.3 to keep depth
            // precision healthy at the wider range (error ~ far^2 / near / 2^24).
            float chunkEnclosure = (float)(chunkRadius * ChunkManager.ChunkSize * Math.Sqrt(2.0));
            _farPlane = Math.Max(700f, chunkEnclosure);
            // Near plane 0.3 -> 0.1: the player's collision radius is 0.30, so at maximum approach
            // the eye sits exactly 0.30 from a block face. A 0.3 near plane puts that face AT the
            // clip plane and it vanishes (you see through the block you're touching). 0.1 keeps the
            // closest face inside the clip volume while depth precision at 700 blocks stays healthy
            // (error ~ far^2 / near / 2^24 ~ 0.29 at max range - no distant z-fighting).
            _nearPlane = 0.1f;
            // Fog: full fog at the render distance (chunkRadius * 16 blocks), linear from 0.
            _fogEnd = chunkRadius * ChunkManager.ChunkSize;
        }

        public void SetChunkManager(Cubuild.ChunkManager manager)
        {
            _chunkManager = manager;
        }

        /// <summary>Regenerates seed-derived visuals (cloud pattern, galaxy) when a new world
        /// starts, so every world has its own sky.</summary>
        public void SetWorldSeed(int seed)
        {
            _cloudSeed = seed;
            if (_cloudTexture != null)
            {
                _gd.UpdateTexture(_cloudTexture, GenerateCloudTexture(seed), 0, 0, 0, 256, 256, 1, 0, 0);
            }
            _galaxySeed = int.MinValue; // force galaxies to rebuild from the new seed
        }

        // Drains priority (player-edit) uploads every frame so edits appear immediately, ahead of
        // the budget-limited background streaming uploads.
        public void ProcessPendingPriorityMeshes()
        {
            while (_pendingPriorityUploads.TryDequeue(out var pu))
            {
                WriteChunkData(pu.Coord, pu.Vertices, pu.Indices, pu.CutoutVertices, pu.CutoutIndices, pu.GlassVertices, pu.GlassIndices, pu.TransparentVertices, pu.TransparentIndices);
            }
        }

        /// <summary>Total chunk meshes still queued for GPU upload. Used by the world-loading
        /// screen to know when all pre-generated chunks are fully ready.</summary>
        public int CountPendingUploads()
        {
            return _pendingUploads.Count + _pendingPriorityUploads.Count;
        }

        /// <summary>Feeds the real input snapshot to ImGui (for the interactive E-menu inventory).
        /// Called every frame; pass null/never to keep ImGui inert.</summary>
        public void SetUiInputSnapshot(InputSnapshot snapshot)
        {
            _uiInputSnapshot = snapshot;
        }

        /// <summary>Pops one block id the player clicked in the inventory, or false.</summary>
        public bool TryTakeInventorySelection(out int blockId)
        {
            return _inventorySelections.TryDequeue(out blockId);
        }

        /// <summary>Pops one survival drag/drop click: (kind, target, button).</summary>
        public bool TryTakeInventoryClick(out (int Kind, int Target, int Button) click)
        {
            return _inventoryClicks.TryDequeue(out click);
        }

        /// <summary>The unified slot index the mouse is hovering in the E menu (-1 = none), for
        /// Q-to-drop.</summary>
        public int HoveredInventorySlot => _hoveredInventorySlot;

        /// <summary>Pops one biome name the player clicked in the biome menu, or false.</summary>
        public bool TryTakeBiomeSelection(out string biomeName)
        {
            return _biomeSelections.TryDequeue(out biomeName);
        }

        // Drops all chunk geometry when starting a brand new world over the previous one.
        public void ResetWorld()
        {
            _chunkRanges.Clear();
            _cutoutRanges.Clear();
            _glassRanges.Clear();
            _transparentRanges.Clear();
            _freeBlocks.Clear();
            _drawCommands.Clear();
            _cutoutDrawCommands.Clear();
            _glassDrawCommands.Clear();
            _transparentDrawCommands.Clear();
            _vbTailBytes = 0;
            _ibTailBytes = 0;
            _drawCommandsDirty = true;
        }

        // Spawns little textured cubes of the block's tile flying out of a broken block.
        public void SpawnBlockBreakParticles(int worldX, int worldY, int worldZ, int blockId, int count)
        {
            var def = BlockRegistry.GetById(blockId);
            var tile = def.AllTexture;
            if (!tile.HasValue || _particles.Length == 0) return;
            var tr = tile.Value;
            for (int i = 0; i < count && _particleCount < _particles.Length; i++)
            {
                ref var p = ref _particles[_particleCount++];
                p.X = (float)(worldX + Random.Shared.NextDouble());
                p.Y = (float)(worldY + Random.Shared.NextDouble());
                p.Z = (float)(worldZ + Random.Shared.NextDouble());
                p.VX = (float)((Random.Shared.NextDouble() * 2.0 - 1.0) * 2.2);
                p.VY = (float)(0.8 + Random.Shared.NextDouble() * 3.0);
                p.VZ = (float)((Random.Shared.NextDouble() * 2.0 - 1.0) * 2.2);
                p.Age = 0f;
                p.Lifetime = (float)(0.6 + Random.Shared.NextDouble() * 0.5);
                p.Size = (float)(0.14 + Random.Shared.NextDouble() * 0.08);
                // Each particle shows a small random piece of the block tile (a ~4x4 texel crop),
                // like break particles - not the whole texture.
                int pieceW = Math.Max(2, tr.Width / 4);
                int pieceH = Math.Max(2, tr.Height / 4);
                int ox = Random.Shared.Next(0, Math.Max(1, tr.Width - pieceW + 1));
                int oy = Random.Shared.Next(0, Math.Max(1, tr.Height - pieceH + 1));
                p.TileX = tr.X + ox;
                p.TileY = tr.Y + oy;
                p.TileW = pieceW;
                p.TileH = pieceH;
                p.Brightness = (float)(0.85 + Random.Shared.NextDouble() * 0.15);
            }
        }

        // Advances the particle pool (gravity, motion, ground stop, expiry).
        private void UpdateParticles(float dt)
        {
            int write = 0;
            for (int i = 0; i < _particleCount; i++)
            {
                ref var p = ref _particles[i];
                p.Age += dt;
                if (p.Age >= p.Lifetime) continue;
                p.VY -= 18f * dt;
                p.X += p.VX * dt;
                p.Y += p.VY * dt;
                p.Z += p.VZ * dt;
                // Rest on the first solid block the particle lands in; it fades via lifetime.
                if (_chunkManager != null
                    && _chunkManager.GetBlockAt((int)Math.Floor(p.X), (int)Math.Floor(p.Y), (int)Math.Floor(p.Z)) != 0)
                {
                    p.VX = 0f;
                    p.VY = 0f;
                    p.VZ = 0f;
                }
                _particles[write++] = p;
            }
            _particleCount = write;
        }

        // Synchronously re-mesh one chunk (used for instant player edits). The greedy mesh from the
        // mesher is uploaded via the priority path; the background worker later refines neighbours.
        public void MeshChunkImmediate(Cubuild.ChunkCoordinates coords)
        {
            if (_chunkManager == null) return;
            if (!_chunkManager.TryGetLoadedChunk(coords, out var chunk)) return;

            var chunksToPass = new System.Collections.Generic.List<Cubuild.Chunk> { chunk };
            int chunkX = chunk.OriginX / ChunkManager.ChunkSize;
            int chunkZ = chunk.OriginZ / ChunkManager.ChunkSize;
            if (_chunkManager.TryGetLoadedChunk(new Cubuild.ChunkCoordinates(chunkX - 1, chunkZ), out var left)) chunksToPass.Add(left);
            if (_chunkManager.TryGetLoadedChunk(new Cubuild.ChunkCoordinates(chunkX + 1, chunkZ), out var right)) chunksToPass.Add(right);
            if (_chunkManager.TryGetLoadedChunk(new Cubuild.ChunkCoordinates(chunkX, chunkZ - 1), out var back)) chunksToPass.Add(back);
            if (_chunkManager.TryGetLoadedChunk(new Cubuild.ChunkCoordinates(chunkX, chunkZ + 1), out var front)) chunksToPass.Add(front);

            var faces = Mesher.GenerateMesh(chunksToPass);
            if (faces != null && faces.Count > 0)
            {
                UploadChunkPriority(coords, faces);
            }
            else
            {
                RemoveChunk(coords);
            }
        }
    }
}