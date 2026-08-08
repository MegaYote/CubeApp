using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using Veldrid;
using Veldrid.SPIRV;

namespace CubeApp.Renderer
{
    // Veldrid renderer implementing mesh upload, unlit texture shading, and an ImGui-based HUD overlay.
    // No GDI+/System.Drawing is used anywhere in this renderer.
    public sealed class VeldridRenderer : IRenderer, IDisposable
    {
        private GraphicsDevice _gd;
        private Swapchain _sc;

        private DeviceBuffer _projViewBuffer;
        private ResourceLayout _projViewLayout;
        private ResourceSet _projViewSet;
        private ResourceLayout _textureLayout;
        // Distance fog (Infdev-style): a per-frame uniform block + resource set bound to the world
        // pipelines. Linear from fogStart (25% of the far plane) to fogEnd (the far plane).
        private DeviceBuffer _fogBuffer;
        private ResourceLayout _fogLayout;
        private ResourceSet _fogSet;
        private readonly float[] _fogParams = new float[12];
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

        // Pipeline for chunk border wireframe rendering (F3 debug)
        private Pipeline _chunkBorderPipeline;
        private DeviceBuffer _chunkBorderVertexBuffer;
        private DeviceBuffer _chunkBorderIndexBuffer;
        private float[] _chunkBorderVertexScratch = new float[768]; // grown on demand for larger render distances

        // Infdev sky (RenderGlobal.renderSky): two GIANT fog-blended planes drawn before the world
        // with depth-write off. Top plane (glSkyList) at cameraY+16 uses the sky color, bottom plane
        // (glSkyList2) at cameraY-16 uses the darkened color (r*0.2+0.04, g*0.2+0.04, b*0.6+0.1).
        // Linear fog (start 0, end farPlane*0.8 = Infdev's setupFog(-1)) fades them toward the fog
        // color at the horizon - that's the whole Infdev sky gradient, no dome or shader magic.
        private Pipeline _skyPipeline;
        private DeviceBuffer _skyVertexBuffer;
        private DeviceBuffer _skyIndexBuffer;
        private DeviceBuffer _skyFogBuffer;
        private ResourceLayout _skyFogLayout;
        private ResourceSet _skyFogSet;
        private readonly float[] _skyFogParams = new float[12];
        // Infdev draws the sky in CAMERA space (the display lists sit at y=16 relative to the eye,
        // transformed only by the camera rotation + projection). We mirror that: a static
        // camera-space vertex buffer + this rotation-only view-projection, so the sky is
        // structurally locked to the camera and can never drift as the player walks.
        private DeviceBuffer _skyMatrixBuffer;
        private ResourceSet _skyMatrixSet;

        // ---- Clouds (world-space flat plane, buildable/flyable) -----------------------
        // One flat plane at a FIXED WORLD HEIGHT (like MC's cloud layer at y~128) that follows
        // the camera in X/Z so its edges stay beyond the far plane. The texture TILES (repeat
        // every few blocks) so clouds look like proper puffs, not one stretched smear. Drawn
        // with depth-write OFF after the sky, before the world; terrain paints over it when you
        // stand below, and you can build up into it or fly above it.
        private Pipeline? _cloudPipeline;
        private DeviceBuffer? _cloudVertexBuffer;
        private DeviceBuffer? _cloudIndexBuffer;
        private DeviceBuffer? _cloudParamsBuffer;
        private ResourceSet? _cloudParamsSet;
        private ResourceLayout? _cloudParamsLayout;
        private Texture? _cloudTexture;
        private TextureView? _cloudTextureView;
        private const float CloudWorldY = 128f;       // MC-like cloud altitude
        private const float CloudTileSize = 1024f;    // blocks per 256px texture tile (each pixel = one cloud cell ~4 blocks)
        private float _cloudScrollU;
        private float _cloudScrollV;
        private float _lastCloudTime;
        private readonly float[] _cloudParams = new float[4];
        private readonly System.Diagnostics.Stopwatch _cloudClock = System.Diagnostics.Stopwatch.StartNew();
        private int _cloudSeed = 12345;

        // Wide far plane for clouds / world-from-above: a dedicated projection with far = 3x the
        // world far plane, so the cloud deck and the fake "earth seen from above" stretch much
        // further than terrain without affecting depth precision of the world.
        private DeviceBuffer? _cloudMatrixBuffer;
        private ResourceSet? _cloudMatrixSet;
        private float _cloudFarPlane = 2100f;

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
        private IReadOnlyList<CubeApp.DuckInstance> _duckInstances = Array.Empty<CubeApp.DuckInstance>();
        private float[] _duckVertexScratch = Array.Empty<float>();
        private ushort[] _duckIndexScratch = Array.Empty<ushort>();
        private const int DuckFloatsPerVertex = 9; // pos(3) + uv(2) + color(4)
        private const float DuckModelScale = 1.05f; // visually petite duck
        private const float PlayerModelScale = 1.25f; // visually bigger player/Steve

        // Minecraft-style player model (shares the model pipeline; own texture + buffers).
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
        private uint _playerIndexCapacity;
        private IReadOnlyList<CubeApp.DuckInstance> _playerInstances = Array.Empty<CubeApp.DuckInstance>();
        private float[] _playerVertexScratch = Array.Empty<float>();
        private ushort[] _playerIndexScratch = Array.Empty<ushort>();

        // GLB-driven mobs (coyote): loaded from MobEntities/<Type>Mob/<type>.glb + .png at startup,
        // drawn through MobModel.Draw (which emits the same 9-float model-vertex layout).
        private MobModel? _coyoteModel;
        private ResourceSet? _coyoteTextureSet;
        private IReadOnlyList<CubeApp.DuckInstance> _coyoteInstances = Array.Empty<CubeApp.DuckInstance>();
        private DeviceBuffer? _coyoteVertexBuffer;
        private DeviceBuffer? _coyoteIndexBuffer;
        private uint _coyoteVertexCapacity;
        private uint _coyoteIndexCapacity;
        private float[] _coyoteVertexScratch = Array.Empty<float>();
        private ushort[] _coyoteIndexScratch = Array.Empty<ushort>();
        // Full mob snapshot kept for F3 nametag rendering (world -> screen projection).
        private IReadOnlyList<CubeApp.MobRenderData> _allMobRenderData = Array.Empty<CubeApp.MobRenderData>();

        // Current camera (so chunk frustum culling and the mob meshing can read it) and the six
        // view-frustum planes refreshed each frame from the view-projection matrix.
        private CubeApp.Point3D? _cameraPosition;
        private System.Numerics.Matrix4x4? _viewProjection;
        public CubeApp.Point3D? CameraPosition => _cameraPosition;
        public System.Numerics.Matrix4x4? ViewProjection => _viewProjection;
        private readonly Vector4[] _frustumPlanes = new Vector4[6];

        private CommandList _commandList;
        private ImGuiRenderer _imguiRenderer;
        private HudState _hud = HudState.Empty;
        private float _farPlane = 100f;
        private float _nearPlane = 0.1f;
        private float _atlasWidth = 256f;
        private float _atlasHeight = 256f;
        // CPU copy of the atlas pixels (for generating hotbar/inventory block icons) and the
        // icon atlas texture built from them (classic MC-style isometric cubes per block).
        private byte[] _atlasRgba = Array.Empty<byte>();
        private int _atlasPixelsW;
        private int _atlasPixelsH;
        private Texture? _iconAtlasTexture;
        private TextureView? _iconAtlasView;
        private IntPtr _iconImGuiId;
        private const int IconCellSize = 48;
        private Vector4[]? _blockIconUv;
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
        private byte[] _worldNameBuffer = new byte[64];
        private byte[] _seedBuffer = new byte[64];
        private byte[] _hostPortBuffer = new byte[16];
        private byte[] _joinAddressBuffer = new byte[128];
        private bool _menuBuffersInitialized;
        // Real input for the ImGui UI (only wired when the mouse is free, e.g. the E-menu
        // inventory); otherwise ImGui stays inert via NullInputSnapshot.
        private InputSnapshot? _uiInputSnapshot;
        private readonly System.Collections.Concurrent.ConcurrentQueue<int> _inventorySelections = new();

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

        // ---- Falling blocks (Minecraft falling sand/gravel) ------------------------------
        // Real 3D cubes of the block's tiles, drawn with an INSTANCED pipeline: one static cube
        // mesh is uploaded once, and each frame only a tiny per-instance buffer (world pos +
        // tile rect) is updated. For hundreds of falling blocks this uploads ~2.8KB instead of
        // rebuilding + re-uploading full cube geometry (~500KB) - the difference between a big
        // cave-in being smooth vs stuttering.
        private IReadOnlyList<CubeApp.FallingBlockData> _fallingBlocks = Array.Empty<CubeApp.FallingBlockData>();
        private DeviceBuffer? _fallingVertexBuffer;  // static cube mesh (once)
        private DeviceBuffer? _fallingIndexBuffer;   // static cube indices (once)
        private DeviceBuffer? _fallingInstanceBuffer; // per-frame instance data (dynamic)
        private uint _fallingInstanceCapacity;
        private float[] _fallingInstanceScratch = Array.Empty<float>();
        private Pipeline? _fallingPipeline;
        private const int FallingCubeVerts = 24;  // 6 faces x 4
        private const int FallingCubeIndices = 36;
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
        // Infdev per-face shading multipliers (top 1.0 / bottom 0.5 / E+W 0.6 / N+S 0.8).
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
        private const uint VertexStrideBytes = 52;   // 13 floats * 4 bytes per vertex
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
        private readonly Dictionary<CubeApp.ChunkCoordinates, ChunkRange> _chunkRanges = new();
        // Cutout (cross plants / leaves, alpha-tested, depth-writing) and transparent (water,
        // blended, no depth-write) faces live in separate ranges drawn as their own passes.
        private readonly Dictionary<CubeApp.ChunkCoordinates, ChunkRange> _cutoutRanges = new();
        private readonly Dictionary<CubeApp.ChunkCoordinates, ChunkRange> _glassRanges = new();
        private readonly Dictionary<CubeApp.ChunkCoordinates, ChunkRange> _transparentRanges = new();
        private readonly List<(uint VbOffset, uint VbBytes, uint IbOffset, uint IbBytes)> _freeBlocks = new();
        private readonly List<(CubeApp.ChunkCoordinates Coord, IndirectDrawIndexedArguments Cmd)> _drawCommands = new();
        private readonly List<(CubeApp.ChunkCoordinates Coord, IndirectDrawIndexedArguments Cmd)> _cutoutDrawCommands = new();
        private readonly List<(CubeApp.ChunkCoordinates Coord, IndirectDrawIndexedArguments Cmd)> _glassDrawCommands = new();
        private readonly List<(CubeApp.ChunkCoordinates Coord, IndirectDrawIndexedArguments Cmd)> _transparentDrawCommands = new();
        private IndirectDrawIndexedArguments[] _indirectScratch = Array.Empty<IndirectDrawIndexedArguments>();
        private IndirectDrawIndexedArguments[] _cutoutIndirectScratch = Array.Empty<IndirectDrawIndexedArguments>();
        private IndirectDrawIndexedArguments[] _glassIndirectScratch = Array.Empty<IndirectDrawIndexedArguments>();
        private IndirectDrawIndexedArguments[] _transparentIndirectScratch = Array.Empty<IndirectDrawIndexedArguments>();
        private bool _drawCommandsDirty = true;

        // Pending GPU-side buffer growth copies (old -> new, recorded after cl.Begin()).
        private readonly List<(DeviceBuffer Old, DeviceBuffer New, uint SizeBytes)> _pendingBufferCopies = new();

        private readonly System.Collections.Concurrent.ConcurrentQueue<PendingUpload> _pendingUploads = new();
        private readonly System.Collections.Concurrent.ConcurrentQueue<PendingUpload> _pendingPriorityUploads = new(); // player edits jump the line
        private readonly System.Collections.Concurrent.ConcurrentQueue<CubeApp.ChunkCoordinates> _pendingRemovals = new();
        private ChunkManager? _chunkManager; // set via SetChunkManager, used by MeshChunkImmediate
        // Upload budget per frame to avoid large spikes
        private int _maxUploadsPerFrame = 4;

        private readonly struct PendingUpload
        {
            public CubeApp.ChunkCoordinates Coord { get; }
            public float[] Vertices { get; }
            public ushort[] Indices { get; }
            public float[] CutoutVertices { get; }
            public ushort[] CutoutIndices { get; }
            public float[] GlassVertices { get; }
            public ushort[] GlassIndices { get; }
            public float[] TransparentVertices { get; }
            public ushort[] TransparentIndices { get; }

            public PendingUpload(CubeApp.ChunkCoordinates coord, float[] vertices, ushort[] indices,
                float[] cutoutVertices, ushort[] cutoutIndices, float[] glassVertices, ushort[] glassIndices,
                float[] transparentVertices, ushort[] transparentIndices)
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

            LoadDuckResources();
            LoadPlayerResources();
            LoadCoyoteResources();
            CreatePipeline();

            _imguiRenderer = new ImGuiRenderer(
                _gd,
                _sc.Framebuffer.OutputDescription,
                Math.Max(1, (int)_sc.Framebuffer.Width),
                Math.Max(1, (int)_sc.Framebuffer.Height));

            // Build the isometric block-icon atlas (needs the ImGui renderer for its texture binding).
            BuildIconAtlas();

            // Bind the terrain atlas to ImGui so the menus can draw the dirt background.
            if (_imguiRenderer != null && _atlasView != null)
            {
                _terrainImGuiId = _imguiRenderer.GetOrCreateImGuiBinding(_gd.ResourceFactory, _atlasView);
            }

            LoadLogo();
            LoadHotbarTextures();
        }

        // Loads the embedded title-screen logo and exposes it to ImGui.
        private void LoadLogo()
        {
            try
            {
                byte[]? bytes = LoadImageBytes("cubuild.png");
                if (bytes == null) return;
                var image = StbImageSharp.ImageResult.FromMemory(bytes, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
                var texDesc = TextureDescription.Texture2D((uint)image.Width, (uint)image.Height, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled);
                _logoTexture = _gd.ResourceFactory.CreateTexture(texDesc);
                _gd.UpdateTexture(_logoTexture, image.Data, 0, 0, 0, (uint)image.Width, (uint)image.Height, 1, 0, 0);
                _logoView = _gd.ResourceFactory.CreateTextureView(_logoTexture);
                if (_imguiRenderer != null)
                {
                    _logoImGuiId = _imguiRenderer.GetOrCreateImGuiBinding(_gd.ResourceFactory, _logoView);
                }
            }
            catch
            {
                // ignore; the title falls back to text if the logo can't load
            }
        }

        // Loads the embedded hotbar GUI textures (frame + selection highlight) from Cubuild.html
        // and exposes them to ImGui for the hotbar drawing.
        private void LoadHotbarTextures()
        {
            try
            {
                byte[]? frameBytes = LoadImageBytes("hotbar.png");
                if (frameBytes != null)
                {
                    var frame = StbImageSharp.ImageResult.FromMemory(frameBytes, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
                    var frameDesc = TextureDescription.Texture2D((uint)frame.Width, (uint)frame.Height, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled);
                    _hotbarTexture = _gd.ResourceFactory.CreateTexture(frameDesc);
                    _gd.UpdateTexture(_hotbarTexture, frame.Data, 0, 0, 0, (uint)frame.Width, (uint)frame.Height, 1, 0, 0);
                    _hotbarView = _gd.ResourceFactory.CreateTextureView(_hotbarTexture);
                }

                byte[]? selectBytes = LoadImageBytes("hotbar_select.png");
                if (selectBytes != null)
                {
                    var sel = StbImageSharp.ImageResult.FromMemory(selectBytes, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
                    var selDesc = TextureDescription.Texture2D((uint)sel.Width, (uint)sel.Height, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled);
                    _hotbarSelectTexture = _gd.ResourceFactory.CreateTexture(selDesc);
                    _gd.UpdateTexture(_hotbarSelectTexture, sel.Data, 0, 0, 0, (uint)sel.Width, (uint)sel.Height, 1, 0, 0);
                    _hotbarSelectView = _gd.ResourceFactory.CreateTextureView(_hotbarSelectTexture);
                }

                if (_imguiRenderer != null)
                {
                    if (_hotbarView != null) _hotbarImGuiId = _imguiRenderer.GetOrCreateImGuiBinding(_gd.ResourceFactory, _hotbarView);
                    if (_hotbarSelectView != null) _hotbarSelectImGuiId = _imguiRenderer.GetOrCreateImGuiBinding(_gd.ResourceFactory, _hotbarSelectView);
                }
            }
            catch
            {
                // ignore; the hotbar falls back to drawn rects if the textures can't load
            }
        }

        private static byte[]? LoadAtlasBytes()
        {
            // Embedded copy first, so a single self-contained .exe carries the atlas with it.
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            foreach (var name in asm.GetManifestResourceNames())
            {
                if (name.EndsWith("terrain.png", StringComparison.OrdinalIgnoreCase))
                {
                    using var stream = asm.GetManifestResourceStream(name);
                    if (stream != null)
                    {
                        using var ms = new System.IO.MemoryStream();
                        stream.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
            }

            // Fall back to a loose terrain.png next to the executable (local dev).
            string path = System.IO.File.Exists("terrain.png")
                ? "terrain.png"
                : System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "terrain.png");
            return System.IO.File.Exists(path) ? System.IO.File.ReadAllBytes(path) : null;
        }

        private static byte[]? LoadImageBytes(string fileName)
        {
            // Embedded copy first, so the single self-contained .exe carries the texture with it.
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            foreach (var name in asm.GetManifestResourceNames())
            {
                if (name.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
                {
                    using var stream = asm.GetManifestResourceStream(name);
                    if (stream != null)
                    {
                        using var ms = new System.IO.MemoryStream();
                        stream.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
            }

            string path = System.IO.File.Exists(fileName)
                ? fileName
                : System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
            return System.IO.File.Exists(path) ? System.IO.File.ReadAllBytes(path) : null;
        }

        private void LoadDuckResources()
        {
            _duckBones = DuckModel.Bones;
            _duckVertsPerInstance = 0;
            _duckIndicesPerInstance = 0;
            foreach (var bone in _duckBones)
            {
                _duckVertsPerInstance += bone.Vertices.Length;
                _duckIndicesPerInstance += bone.Indices.Length;
            }

            try
            {
                byte[]? bytes = LoadImageBytes(DuckModel.TextureResourceName);
                if (bytes == null)
                {
                    return;
                }

                var image = StbImageSharp.ImageResult.FromMemory(bytes, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
                var texDesc = TextureDescription.Texture2D((uint)image.Width, (uint)image.Height, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled);
                _duckTexture = _gd.ResourceFactory.CreateTexture(texDesc);
                _gd.UpdateTexture(_duckTexture, image.Data, 0, 0, 0, (uint)image.Width, (uint)image.Height, 1, 0, 0);
                _duckView = _gd.ResourceFactory.CreateTextureView(_duckTexture);
                _duckSampler = _gd.ResourceFactory.CreateSampler(new SamplerDescription(
                    SamplerAddressMode.Clamp,
                    SamplerAddressMode.Clamp,
                    SamplerAddressMode.Clamp,
                    SamplerFilter.MinPoint_MagPoint_MipPoint,
                    null,
                    1,
                    0,
                    0,
                    0,
                    SamplerBorderColor.TransparentBlack));
            }
            catch
            {
                // ignore; duck rendering is skipped if the texture fails to load
            }
        }

        private void LoadPlayerResources()
        {
            _playerBones = PlayerModel.Bones;
            _playerVertsPerInstance = 0;
            _playerIndicesPerInstance = 0;
            foreach (var bone in _playerBones)
            {
                _playerVertsPerInstance += bone.Vertices.Length;
                _playerIndicesPerInstance += bone.Indices.Length;
            }

            try
            {
                byte[]? bytes = LoadImageBytes(PlayerModel.TextureResourceName);
                if (bytes == null)
                {
                    return;
                }

                var image = StbImageSharp.ImageResult.FromMemory(bytes, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
                var texDesc = TextureDescription.Texture2D((uint)image.Width, (uint)image.Height, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled);
                _playerTexture = _gd.ResourceFactory.CreateTexture(texDesc);
                _gd.UpdateTexture(_playerTexture, image.Data, 0, 0, 0, (uint)image.Width, (uint)image.Height, 1, 0, 0);
                _playerView = _gd.ResourceFactory.CreateTextureView(_playerTexture);
                _playerSampler = _gd.ResourceFactory.CreateSampler(new SamplerDescription(
                    SamplerAddressMode.Clamp,
                    SamplerAddressMode.Clamp,
                    SamplerAddressMode.Clamp,
                    SamplerFilter.MinPoint_MagPoint_MipPoint,
                    null,
                    1,
                    0,
                    0,
                    0,
                    SamplerBorderColor.TransparentBlack));
            }
            catch
            {
                // ignore; player rendering is skipped if the texture fails to load
            }
        }

        // Loads the coyote GLB model + texture from MobEntities/CoyoteMob/. Coyote (and any future
        // Blockbench mob) renders through the generic MobModel.Draw path instead of hand-authored
        // cube bones like duck/player.
        private void LoadCoyoteResources()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string modelPath = Path.Combine(baseDir, "MobEntities", "CoyoteMob", "coyote.glb");
                string texPath = Path.Combine(baseDir, "MobEntities", "CoyoteMob", "Coyote.png");
                if (!File.Exists(modelPath)) return;

                var model = new MobModel(_gd);
                if (!model.Load(modelPath, texPath)) return;
                _coyoteModel = model;
                // Coyotes are drawn ~1.3x their raw Blockbench size (matches the bigger collision box).
                model.ModelScale = 1.3f;
                _coyoteTextureSet = model.TextureSet;
            }
            catch
            {
                // ignore; coyotes simply don't render if the model fails to load
            }
        }

        // Renders a classic MC-style isometric cube icon for every block into one RGBA texture,
        // then exposes it to ImGui for the hotbar/inventory. Uses separate horizontal (a) and
        // vertical (b) half-extents so the cube is a chunky ~1.5:1 ratio (like MC), showing the
        // top face as a diamond and the front-left/right faces as the two lower parallelograms.
        private void BuildIconAtlas()
        {
            if (_atlasRgba.Length == 0) return;
            const int iconSize = 48;
            const int cols = 12;
            int blockCount = BlockRegistry.Count;
            int rows = Math.Max(1, (int)Math.Ceiling((blockCount - 1) / (double)cols));
            int atlasW = cols * iconSize;
            int atlasH = rows * iconSize;
            var iconData = new byte[atlasW * atlasH * 4];

            _blockIconUv = new Vector4[blockCount];

            for (int id = 1; id < blockCount; id++)
            {
                int cellX = ((id - 1) % cols) * IconCellSize;
                int cellY = ((id - 1) / cols) * IconCellSize;
                int cellDi = (cellY * atlasW + cellX) * 4;

                // Icons are a SOFTWARE RENDER of the REAL mesher output: build a tiny chunk with the
                // block, run the same Mesher.GenerateMesh the world uses, and rasterize the actual
                // MeshFaces into the cell. This is the single source of truth - cubes, slabs, stairs,
                // cross plants, glass and water all come out exactly like they render in the world,
                // with no hand-drawn shape variants to drift out of sync.
                DrawMeshIcon(iconData, cellDi, atlasW, id);

                _blockIconUv[id] = new Vector4(
                    cellX / (float)atlasW, cellY / (float)atlasH,
                    IconCellSize / (float)atlasW, IconCellSize / (float)atlasH);
            }

            _iconAtlasTexture = _gd.ResourceFactory.CreateTexture(TextureDescription.Texture2D(
                (uint)atlasW, (uint)atlasH, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled));
            _gd.UpdateTexture(_iconAtlasTexture, iconData, 0, 0, 0, (uint)atlasW, (uint)atlasH, 1, 0, 0);
            _iconAtlasView = _gd.ResourceFactory.CreateTextureView(_iconAtlasTexture);
            if (_imguiRenderer != null)
            {
                _iconImGuiId = _imguiRenderer.GetOrCreateImGuiBinding(_gd.ResourceFactory, _iconAtlasView);
            }
        }

        // Software-renders a block icon from the REAL mesher output: builds a tiny 16x16x16 chunk
        // with the block at (8,8,8), runs Mesher.GenerateMesh (the same mesh builder the world uses),
        // then rasterizes the actual MeshFaces into the 48px cell. Every shape - full cubes, slabs,
        // stairs, cross plants, glass, water - renders exactly like it does in the world, so there is
        // ONE source of truth and the GUI can never drift from the game.
        private void DrawMeshIcon(byte[] dst, int cellDi, int atlasW, int blockId)
        {
            // Cross plants are drawn as their FLAT sprite tile (like Cubuild's cross shape), not as
            // the 3D crossed billboards - those project as thin diagonal slivers in an isometric icon.
            if (BlockRegistry.IsCross(blockId))
            {
                var def = BlockRegistry.GetById(blockId);
                var tile = def.FaceTexture(new Point3D(0, 0, -1));
                if (tile.Width == 0) tile = def.AllTexture ?? default;
                DrawCrossSprite(dst, cellDi, atlasW, tile);
                return;
            }

            var chunk = new Chunk(16, 16, 16, 0, 0, 0);
            chunk[8, 8, 8] = blockId;
            // Stairs use metadata for facing. The menu icon shows a canonical orientation: the LOW
            // step toward the viewer (front-bottom) and the HIGH step at the back - that is meta 1
            // for this icon camera (+X,+Y,-Z). Force it so every stair icon looks the same classic
            // way instead of whichever placement-facing the mesh happens to use.
            if (BlockRegistry.IsStair(blockId)) chunk.SetMeta(8, 8, 8, 1);
            var faces = Mesher.GenerateMesh(chunk);

            // Isometric projection matching the classic MC/Cubuild icon (48px cell). The block
            // occupies local (0,0,0)-(1,1,1) at world (8,8,8)-(9,9,9). +X right, +Y up, +Z front.
            // Derived from the old cube-icon diamond:
            //   front-bottom (0,0,0) -> (24,37.5), +X -> (37.5,30.75), +Z -> (10.5,30.75), +Y -> (24,18)
            // Affine: sx = 24 + 13.5*x - 13.5*z ; sy = 37.5 - 6.75*x - 6.75*z - 19.5*y
            // Camera is at +X,+Y,-Z so visible faces are +Y (top), +X (right), -Z (front-left).
            Span<MeshFace> sorted = faces.Count <= 64
                ? stackalloc MeshFace[64]
                : new MeshFace[faces.Count];
            for (int i = 0; i < faces.Count; i++) sorted[i] = faces[i];
            // Painter's algorithm: sort FAR-TO-NEAR so the nearest face paints last (on top).
            // Depth increases toward -X, +Z, -Y (the camera sits at +X,+Y,-Z), so the face depth
            // key is (-centroid.x + centroid.z - centroid.y): LARGER key = farther. Sort descending
            // so the farthest face is rasterized first and nearer faces cover it.
            for (int i = 1; i < faces.Count; i++)
            {
                var key = FaceDepthKey(sorted[i]);
                for (int j = i; j > 0 && FaceDepthKey(sorted[j - 1]) < key; j--)
                {
                    (sorted[j], sorted[j - 1]) = (sorted[j - 1], sorted[j]);
                }
            }

            for (int i = 0; i < faces.Count; i++)
            {
                RasterizeFace(dst, cellDi, atlasW, sorted[i]);
            }
        }

        // Infdev's fixed per-face light multipliers (RenderBlocks.java): bottom 0.5 / top 1.0 /
        // N+S 0.8 / E+W 0.6. The icon camera shows top (+Y), right (+X) and front-left (-Z).
        private static float FaceIconShade(Point3D normal)
        {
            if (normal.Y > 0.5) return 1.0f;
            if (normal.Y < -0.5) return 0.5f;
            if (Math.Abs(normal.X) > 0.5) return 0.6f;
            return 0.8f;
        }

        private static float FaceDepthKey(in MeshFace f)
        {
            double cx = (f.V0.X + f.V1.X + f.V2.X + f.V3.X) * 0.25;
            double cy = (f.V0.Y + f.V1.Y + f.V2.Y + f.V3.Y) * 0.25;
            double cz = (f.V0.Z + f.V1.Z + f.V2.Z + f.V3.Z) * 0.25;
            // Farther = smaller x, larger z, smaller y (camera sits +X,+Y,-Z).
            return (float)(-cx + cz - cy);
        }

        // Rasterizes one real MeshFace into the 48px icon cell. Projects the quad's four corners
        // through the isometric transform and fills them as two triangles. UVs use the SAME
        // convention as the GPU world path: du = dot(world, uAxis) - minU, dv = dot(world, vAxis)
        // - minV, normalized across the face - so the tile texture is oriented exactly like the
        // in-world block, not rotated by whatever vertex order the mesher chose.
        private void RasterizeFace(byte[] dst, int cellDi, int atlasW, in MeshFace f)
        {
            // Skip faces that point away from the icon camera (+X,+Y,-Z): dot(normal, (1,1,-1)) <= 0.
            if ((float)(f.Normal.X + f.Normal.Y - f.Normal.Z) <= 0f) return;

            bool hasAxes = TryGetCubuildFaceAxes(f.Normal, out var uAxis, out var vAxis);

            Span<Point3D> verts = stackalloc Point3D[4];
            verts[0] = f.V0;
            verts[1] = f.V1;
            verts[2] = f.V2;
            verts[3] = f.V3;

            double minU = 0.0, minV = 0.0, maxU = 1.0, maxV = 1.0;
            if (hasAxes)
            {
                minU = double.PositiveInfinity;
                minV = double.PositiveInfinity;
                maxU = double.NegativeInfinity;
                maxV = double.NegativeInfinity;
                for (int ci = 0; ci < 4; ci++)
                {
                    double u = Dot(verts[ci], uAxis);
                    double v = Dot(verts[ci], vAxis);
                    if (u < minU) minU = u;
                    if (u > maxU) maxU = u;
                    if (v < minV) minV = v;
                    if (v > maxV) maxV = v;
                }
            }

            // Project the four corners. World -> local (block spans 1 cell), then affine to screen.
            Span<Vector2> proj = stackalloc Vector2[4];
            for (int ci = 0; ci < 4; ci++)
            {
                proj[ci] = ProjectIconPoint(verts[ci]);
            }

            // Sample a pixel: interpolate the world position across the triangle, then compute the
            // face-axis UV exactly like the GPU path and nearest-sample the tile.
            // Icons use STUDIO lighting - the fixed Infdev per-face multiplier (top 1.0, bottom 0.5,
            // E/W 0.6, N/S 0.8) - NOT the mesher's Shade, which bakes in the tiny chunk's simulated
            // light that attenuates by the block's y position and leaves partial shapes looking dark.
            float shade = FaceIconShade(f.Normal);
            // Cutout = per-pixel sprite alpha (cross plants, glass, and translucent colored glass
            // sentinel -200) so the icon shows the PNG's real transparency.
            bool cutout = f.Alpha < 0f;
            bool transparent = !cutout && f.Alpha < 1f; // water etc.
            int spanU = Math.Max(1, f.TileWidth);
            int spanV = Math.Max(1, f.TileHeight);
            int tileW = Math.Max(1, f.SrcRect.Width);
            int tileH = Math.Max(1, f.SrcRect.Height);

            RasterizeTriangle(dst, cellDi, atlasW, proj[0], proj[1], proj[2], verts[0], verts[1], verts[2],
                hasAxes, uAxis, vAxis, minU, maxU, minV, maxV, spanU, spanV, tileW, tileH, f.SrcRect, shade, cutout, transparent);
            RasterizeTriangle(dst, cellDi, atlasW, proj[0], proj[2], proj[3], verts[0], verts[2], verts[3],
                hasAxes, uAxis, vAxis, minU, maxU, minV, maxV, spanU, spanV, tileW, tileH, f.SrcRect, shade, cutout, transparent);
        }

        private void RasterizeTriangle(byte[] dst, int cellDi, int atlasW,
            Vector2 p0, Vector2 p1, Vector2 p2,
            Point3D v0, Point3D v1, Point3D v2,
            bool hasAxes, Point3D uAxis, Point3D vAxis,
            double minU, double maxU, double minV, double maxV,
            int spanU, int spanV,
            int tileW, int tileH, TextureRect tile, float shade, bool cutout, bool transparent)
        {
            float minX = Math.Min(Math.Min(p0.X, p1.X), p2.X);
            float maxX = Math.Max(Math.Max(p0.X, p1.X), p2.X);
            float minY = Math.Min(Math.Min(p0.Y, p1.Y), p2.Y);
            float maxY = Math.Max(Math.Max(p0.Y, p1.Y), p2.Y);

            int ix0 = Math.Max(0, (int)Math.Floor(minX));
            int ix1 = Math.Min(IconCellSize - 1, (int)Math.Ceiling(maxX));
            int iy0 = Math.Max(0, (int)Math.Floor(minY));
            int iy1 = Math.Min(IconCellSize - 1, (int)Math.Ceiling(maxY));

            float e01 = p1.X - p0.X;
            float e02 = p1.Y - p0.Y;
            float e11 = p2.X - p0.X;
            float e12 = p2.Y - p0.Y;
            float area = e01 * e12 - e02 * e11;
            if (Math.Abs(area) < 1e-6f) return;

            // Screen space has Y pointing DOWN, which flips winding vs the world/NDC. The mesher
            // emits faces wound for the GPU's CounterClockwise front-face culling, so a visible
            // face can project as either clockwise or counter-clockwise here depending on its
            // normal. Instead of rejecting clockwise triangles (which would make a whole face
            // disappear), normalize to a positive area by swapping the two edge vertices.
            if (area < 0f)
            {
                area = -area;
                (p1, p2) = (p2, p1);
                (v1, v2) = (v2, v1);
                e01 = p1.X - p0.X;
                e02 = p1.Y - p0.Y;
                e11 = p2.X - p0.X;
                e12 = p2.Y - p0.Y;
            }

            for (int py = iy0; py <= iy1; py++)
            {
                for (int px = ix0; px <= ix1; px++)
                {
                    float fx = px - p0.X;
                    float fy = py - p0.Y;
                    float w1 = (fx * e12 - fy * e11) / area; // weight of vertex 1
                    float w2 = (e01 * fy - e02 * fx) / area; // weight of vertex 2
                    float w0 = 1f - w1 - w2;                 // weight of vertex 0
                    if (w0 < -0.001f || w1 < -0.001f || w2 < -0.001f) continue;

                    double wx = v0.X * w0 + v1.X * w1 + v2.X * w2;
                    double wy = v0.Y * w0 + v1.Y * w1 + v2.Y * w2;
                    double wz = v0.Z * w0 + v1.Z * w1 + v2.Z * w2;

                    double du, dv;
                    if (hasAxes)
                    {
                        du = (Dot(new Point3D(wx, wy, wz), uAxis) - minU) / Math.Max(maxU - minU, 1e-9) * spanU;
                        dv = (Dot(new Point3D(wx, wy, wz), vAxis) - minV) / Math.Max(maxV - minV, 1e-9) * spanV;
                    }
                    else
                    {
                        // Fallback: fraction of the face axes (rare - always has axes in practice).
                        du = (wx - Math.Floor(wx)) * spanU;
                        dv = (wy - Math.Floor(wy)) * spanV;
                    }
                    du -= Math.Floor(du);
                    dv -= Math.Floor(dv);
                    if (du < 0.0) du += 1.0;
                    if (dv < 0.0) dv += 1.0;

                    int tx = tile.X + (int)(du * (tileW - 0.001f));
                    int ty = tile.Y + (int)(dv * (tileH - 0.001f));
                    int si = (ty * _atlasPixelsW + tx) * 4;
                    int di = cellDi + (py * atlasW + px) * 4;
                    int alpha = _atlasRgba[si + 3];
                    if (cutout && alpha < 128) continue; // sprite background falls away

                    int a = transparent ? 255 : (cutout ? alpha : 255);
                    dst[di + 0] = (byte)(_atlasRgba[si + 0] * shade);
                    dst[di + 1] = (byte)(_atlasRgba[si + 1] * shade);
                    dst[di + 2] = (byte)(_atlasRgba[si + 2] * shade);
                    dst[di + 3] = (byte)a;
                }
            }
        }

        // Projects one world-space vertex into the 48px icon cell. Affine derived from the classic
        // MC/Cubuild cube icon corners (48px cell):
        //   front-bottom (0,0,0)->(10.5,30.75), +X->(24,37.5), +Z->(24,24 hidden), +Y->(10.5,11.25)
        //   => sx = 10.5 + 13.5*x + 13.5*z ; sy = 30.75 + 6.75*x - 6.75*z - 19.5*y
        // This puts +X (right face) on the screen RIGHT and -Z (front-left face) on the screen LEFT,
        // with +Y (top) as the diamond - the classic three-face isometric view.
        private static Vector2 ProjectIconPoint(Point3D p)
        {
            float lx = (float)(p.X - 8.0); // block occupies world (8,8,8)-(9,9,9)
            float ly = (float)(p.Y - 8.0);
            float lz = (float)(p.Z - 8.0);
            return new Vector2(
                10.5f + 13.5f * lx + 13.5f * lz,
                30.75f + 6.75f * lx - 6.75f * lz - 19.5f * ly);
        }

        // Cross-plant icon: draws the flat sprite tile stretched in the cell with Cubuild's
        // padding (10/6 on a 64 canvas => ~7.5/4.5 on 48), so flowers/mushrooms/saplings show as
        // their real sprite instead of a squished 3D diagonal.
        private void DrawCrossSprite(byte[] dst, int cellDi, int atlasW, TextureRect tile)
        {
            const float padX = 7.5f;
            const float padY = 4.5f;
            for (int py = 0; py < IconCellSize; py++)
            {
                for (int px = 0; px < IconCellSize; px++)
                {
                    float u = (px - padX) / (IconCellSize - padX * 2f);
                    float v = (py - padY) / (IconCellSize - padY * 2f);
                    if (u >= 0f && u <= 1f && v >= 0f && v <= 1f)
                    {
                        int di = cellDi + (py * atlasW + px) * 4;
                        SampleTile(dst, di, tile, u, v, 1.0f);
                    }
                }
            }
        }

        // Copies one nearest-sampled texel from the terrain atlas into the icon buffer, applying the
        // Infdev per-face shade multiplier (top 1.0, N+S 0.8, E+W 0.6) so the icon cubes read like
        // the shaded blocks in the world.
        private void SampleTile(byte[] dst, int di, TextureRect tile, float u, float v, float shade)
        {
            int tx = tile.X + (int)(u * 15.999f);
            int ty = tile.Y + (int)(v * 15.999f);
            int si = (ty * _atlasPixelsW + tx) * 4;
            dst[di + 0] = (byte)(_atlasRgba[si + 0] * shade);
            dst[di + 1] = (byte)(_atlasRgba[si + 1] * shade);
            dst[di + 2] = (byte)(_atlasRgba[si + 2] * shade);
            dst[di + 3] = _atlasRgba[si + 3];
        }

        private void CreatePipeline()
        {
            var factory = _gd.ResourceFactory;
            string vsCode = @"#version 450
layout(location=0) in vec3 aPosition;
layout(location=1) in vec2 aLocalUV;
layout(location=2) in vec4 aTileRect;
layout(location=3) in vec4 aColor;
layout(location=0) out vec2 vLocalUV;
layout(location=1) out vec4 vTileRect;
layout(location=2) out vec4 vColor;
layout(location=3) out vec3 vWorldPos;
layout(set=0, binding=0) uniform ProjectionView { mat4 projView; };
void main() { vLocalUV = aLocalUV; vTileRect = aTileRect; vColor = aColor; vWorldPos = aPosition; gl_Position = projView * vec4(aPosition, 1.0); }";

            string fsCode = @"#version 450
layout(location=0) in vec2 vLocalUV;
layout(location=1) in vec4 vTileRect;
layout(location=2) in vec4 vColor;
layout(location=3) in vec3 vWorldPos;
layout(set=1, binding=0) uniform sampler2D uAtlas;
layout(set=2, binding=0) uniform FogParams {
    vec4 fogColor;   // rgb + pad
    vec2 fogRange;   // start, end
    vec4 cameraPos;  // xyz + pad
    vec2 night;      // x = world light dim (Infdev skylight subtraction), y = sky dim
};
layout(location=0) out vec4 outColor;
void main() {
    // fract() tiles the same atlas tile regardless of how many blocks the face spans.
    vec2 atlasUV = fract(vLocalUV) * vTileRect.zw + vTileRect.xy;
    vec4 tex = texture(uAtlas, atlasUV);
    // Block alpha (vColor.a) governs opacity - transparent tiles (water) are tinted by their
    // configured alpha; opaque blocks (alpha 1) sample the tile fully.
    outColor = vec4(tex.rgb * vColor.rgb, vColor.a);
    // Linear distance fog, like Infdev: fully fogged at fogRange.y, clear at fogRange.x.
    float dist = length(vWorldPos - cameraPos.xyz);
    float fog = clamp((fogRange.y - dist) / max(fogRange.y - fogRange.x, 1e-5), 0.0, 1.0);
    outColor.rgb = mix(fogColor.rgb, outColor.rgb, fog);
    // Day/night: dim the world by Infdev's skylight-subtraction factor.
    outColor.rgb *= night.x;
}";

            var vsSpirv = SpirvCompilation.CompileGlslToSpirv(vsCode, "main", ShaderStages.Vertex, GlslCompileOptions.Default);
            var fsSpirv = SpirvCompilation.CompileGlslToSpirv(fsCode, "main", ShaderStages.Fragment, GlslCompileOptions.Default);

            var vsDesc = new ShaderDescription(ShaderStages.Vertex, vsSpirv.SpirvBytes, "main");
            var fsDesc = new ShaderDescription(ShaderStages.Fragment, fsSpirv.SpirvBytes, "main");

            // CreateFromSpirv cross-compiles the SPIR-V bytecode to the target backend's shading
            // language (HLSL for D3D11) internally. Calling factory.CreateShader directly with raw
            // SPIR-V bytes skips that translation and fails to compile on non-Vulkan backends.
            var shaders = factory.CreateFromSpirv(vsDesc, fsDesc);
            var vs = shaders[0];
            var fs = shaders[1];

            var vertexLayout = new VertexLayoutDescription(
                new VertexElementDescription("aPosition", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                new VertexElementDescription("aLocalUV", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
                new VertexElementDescription("aTileRect", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4),
                new VertexElementDescription("aColor", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4));

            var shaderSet = new ShaderSetDescription(new[] { vertexLayout }, new[] { vs, fs });

            // create texture resource layout (set 1)
            _textureLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("uAtlas", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("uAtlasSampler", ResourceKind.Sampler, ShaderStages.Fragment)));

            // Distance fog uniform block (set 2), shared by all world pipelines.
            _fogLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("FogParams", ResourceKind.UniformBuffer, ShaderStages.Fragment)));
            _fogBuffer = factory.CreateBuffer(new BufferDescription(48, BufferUsage.UniformBuffer | BufferUsage.Dynamic));
            _fogSet = factory.CreateResourceSet(new ResourceSetDescription(_fogLayout, _fogBuffer));

            var pipelineDesc = new GraphicsPipelineDescription()
            {
                // Alpha blend so blocks flagged transparent (water) tint see-through; opaque tiles
                // have alpha 1 so they look identical to the old override-blend behaviour.
                BlendState = BlendStateDescription.SingleAlphaBlend,
                DepthStencilState = new DepthStencilStateDescription(true, true, ComparisonKind.LessEqual),
                RasterizerState = new RasterizerStateDescription(FaceCullMode.Back, PolygonFillMode.Solid, FrontFace.CounterClockwise, true, false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _projViewLayout, _textureLayout, _fogLayout },
                ShaderSet = shaderSet,
                Outputs = _sc.Framebuffer.OutputDescription
            };

            _pipeline = factory.CreateGraphicsPipeline(pipelineDesc);

            // Cutout pass (cross plants + leaves): same vertex shader, but the fragment shader
            // DISCARDS texels below the alpha threshold instead of blending. Depth-write stays ON
            // (Cubuild's worldMaterialCutout: alphaTest 0.5, depthWrite true, DoubleSide), so when
            // the two quads of a cross plant overlap, the NEARER quad's depth occludes the FAR one
            // - no sorting or prepass needed. Culling is off for the billboard sprites.
            string cutoutFsCode = @"#version 450
layout(location=0) in vec2 vLocalUV;
layout(location=1) in vec4 vTileRect;
layout(location=2) in vec4 vColor;
layout(location=3) in vec3 vWorldPos;
layout(set=1, binding=0) uniform sampler2D uAtlas;
layout(set=2, binding=0) uniform FogParams {
    vec4 fogColor;
    vec2 fogRange;
    vec4 cameraPos;
    vec2 night;
};
layout(location=0) out vec4 outColor;
void main() {
    vec2 atlasUV = fract(vLocalUV) * vTileRect.zw + vTileRect.xy;
    vec4 tex = texture(uAtlas, atlasUV);
    if (tex.a < 0.5) discard; // sprite background falls away; no blending
    outColor = vec4(tex.rgb * vColor.rgb, 1.0);
    float dist = length(vWorldPos - cameraPos.xyz);
    float fog = clamp((fogRange.y - dist) / max(fogRange.y - fogRange.x, 1e-5), 0.0, 1.0);
    outColor.rgb = mix(fogColor.rgb, outColor.rgb, fog);
    outColor.rgb *= night.x;
}";
            var cutoutFsSpirv = SpirvCompilation.CompileGlslToSpirv(cutoutFsCode, "main", ShaderStages.Fragment, GlslCompileOptions.Default);
            var cutoutFsDesc = new ShaderDescription(ShaderStages.Fragment, cutoutFsSpirv.SpirvBytes, "main");
            var cutoutShaders = factory.CreateFromSpirv(vsDesc, cutoutFsDesc);
            var cutoutShaderSet = new ShaderSetDescription(new[] { vertexLayout }, new[] { vs, cutoutShaders[1] });
            _cutoutPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription()
            {
                BlendState = BlendStateDescription.SingleDisabled,
                DepthStencilState = new DepthStencilStateDescription(true, true, ComparisonKind.LessEqual),
                RasterizerState = new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.CounterClockwise, true, false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _projViewLayout, _textureLayout, _fogLayout },
                ShaderSet = cutoutShaderSet,
                Outputs = _sc.Framebuffer.OutputDescription
            });

            // Glass frame pass: cutout rules (0.5 alpha discard, no blend) with depth-write ON so
            // the opaque frames occlude things drawn later (water behind glass can't paint over the
            // frame), while the discarded panes leave no depth - water shows through the clear
            // panes. This pass handles BOTH regular glass and the opaque frame pixels of translucent
            // colored glass (whose sentinel vColor.a ~ -200 still discards tex.a < 0.5 normally).
            // BACK culling so inside faces never render through the panes.
            _glassPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription()
            {
                BlendState = BlendStateDescription.SingleDisabled,
                DepthStencilState = new DepthStencilStateDescription(true, true, ComparisonKind.LessEqual),
                RasterizerState = new RasterizerStateDescription(FaceCullMode.Back, PolygonFillMode.Solid, FrontFace.CounterClockwise, true, false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _projViewLayout, _textureLayout, _fogLayout },
                ShaderSet = cutoutShaderSet,
                Outputs = _sc.Framebuffer.OutputDescription
            });

            // Translucent tint pass (colored glass): drawn AFTER water. Only faces with the -200
            // sentinel (translucent) render here, and only their semi-transparent pixels (tex.a <
            // 0.5, which the frame pass discarded) - blended per-pixel with depth-write OFF, so the
            // glass tint paints OVER whatever is behind it (water, terrain) without blocking it.
            // Regular glass faces (sentinel -100) are culled by the shader and never reach here.
            string tintFsCode = @"#version 450
layout(location=0) in vec2 vLocalUV;
layout(location=1) in vec4 vTileRect;
layout(location=2) in vec4 vColor;
layout(location=3) in vec3 vWorldPos;
layout(set=1, binding=0) uniform sampler2D uAtlas;
layout(set=2, binding=0) uniform FogParams {
    vec4 fogColor;
    vec2 fogRange;
    vec4 cameraPos;
    vec2 night;
};
layout(location=0) out vec4 outColor;
void main() {
    if (vColor.a > -150.0) discard; // only translucent (colored) glass - sentinel ~ -200
    vec2 atlasUV = fract(vLocalUV) * vTileRect.zw + vTileRect.xy;
    vec4 tex = texture(uAtlas, atlasUV);
    if (tex.a >= 0.5) discard;      // opaque frame pixels already drawn by the glass pass
    outColor = vec4(tex.rgb * vColor.rgb, tex.a);
    float dist = length(vWorldPos - cameraPos.xyz);
    float fog = clamp((fogRange.y - dist) / max(fogRange.y - fogRange.x, 1e-5), 0.0, 1.0);
    outColor.rgb = mix(fogColor.rgb, outColor.rgb, fog);
    outColor.rgb *= night.x;
}";
            var tintFsSpirv = SpirvCompilation.CompileGlslToSpirv(tintFsCode, "main", ShaderStages.Fragment, GlslCompileOptions.Default);
            var tintShaders = factory.CreateFromSpirv(vsDesc, new ShaderDescription(ShaderStages.Fragment, tintFsSpirv.SpirvBytes, "main"));
            var tintShaderSet = new ShaderSetDescription(new[] { vertexLayout }, new[] { vs, tintShaders[1] });
            _translucentPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription()
            {
                BlendState = BlendStateDescription.SingleAlphaBlend,
                DepthStencilState = new DepthStencilStateDescription(true, false, ComparisonKind.LessEqual),
                RasterizerState = new RasterizerStateDescription(FaceCullMode.Back, PolygonFillMode.Solid, FrontFace.CounterClockwise, true, false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _projViewLayout, _textureLayout, _fogLayout },
                ShaderSet = tintShaderSet,
                Outputs = _sc.Framebuffer.OutputDescription
            });

            // Transparent pass (water): blended, depth-write OFF, drawn after opaque + cutout so it
            // tints whatever geometry already rendered instead of depth-blocking it (which made
            // border water walls render as ghosty see-through when their chunk drew before the
            // terrain behind). Culling off matches Cubuild's DoubleSide water material.
            _transparentPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription()
            {
                BlendState = BlendStateDescription.SingleAlphaBlend,
                DepthStencilState = new DepthStencilStateDescription(true, false, ComparisonKind.LessEqual),
                RasterizerState = new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.CounterClockwise, true, false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _projViewLayout, _textureLayout, _fogLayout },
                ShaderSet = shaderSet,
                Outputs = _sc.Framebuffer.OutputDescription
            });

            // Instanced falling-block pipeline: one static cube mesh per block, with per-instance
            // world position + tile rect. Vertex: local cube corner (3) + local UV (2) + shade (4).
            // Instance: worldPos (3) + tileRect (4). Same atlas+fog sets as the world.
            string fallingVsCode = @"#version 450
layout(location=0) in vec3 aCorner;
layout(location=1) in vec2 aLocalUV;
layout(location=2) in vec4 aShade;
layout(location=3) in vec3 iWorldPos;
layout(location=4) in vec4 iTileRect;
layout(location=0) out vec2 vLocalUV;
layout(location=1) out vec4 vTileRect;
layout(location=2) out vec4 vColor;
layout(location=3) out vec3 vWorldPos;
layout(set=0, binding=0) uniform ProjectionView { mat4 projView; };
void main() {
    vec3 worldPos = aCorner + iWorldPos;
    vLocalUV = aLocalUV;
    vTileRect = iTileRect;
    vColor = aShade;
    vWorldPos = worldPos;
    gl_Position = projView * vec4(worldPos, 1.0);
}";
            var fallingVsSpirv = SpirvCompilation.CompileGlslToSpirv(fallingVsCode, "main", ShaderStages.Vertex, GlslCompileOptions.Default);
            var fallingShaders = factory.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Vertex, fallingVsSpirv.SpirvBytes, "main"),
                new ShaderDescription(ShaderStages.Fragment, fsSpirv.SpirvBytes, "main")); // reuse world opaque fragment shader

            var fallingVertexLayout = new VertexLayoutDescription(
                new VertexElementDescription("aCorner", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                new VertexElementDescription("aLocalUV", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
                new VertexElementDescription("aShade", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4));
            var fallingInstanceLayout = new VertexLayoutDescription(
                new VertexElementDescription("iWorldPos", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                new VertexElementDescription("iTileRect", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4));
            fallingInstanceLayout.InstanceStepRate = 1;
            var fallingShaderSet = new ShaderSetDescription(new[] { fallingVertexLayout, fallingInstanceLayout }, new[] { fallingShaders[0], fallingShaders[1] });
            _fallingPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription()
            {
                BlendState = BlendStateDescription.SingleAlphaBlend,
                DepthStencilState = new DepthStencilStateDescription(true, true, ComparisonKind.LessEqual),
                RasterizerState = new RasterizerStateDescription(FaceCullMode.Back, PolygonFillMode.Solid, FrontFace.CounterClockwise, true, false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _projViewLayout, _textureLayout, _fogLayout },
                ShaderSet = fallingShaderSet,
                Outputs = _sc.Framebuffer.OutputDescription
            });

            // Static cube mesh (uploaded once): 24 verts (6 faces x 4) + 36 indices.
            // The FaceVertices table's +Z/-Z entries are wound opposite their normals (the greedy
            // pass corrects them); we must apply the SAME flip here or back-face culling culls
            // the back/front faces and falling blocks render half-invisible.
            var cubeVerts = new float[FallingCubeVerts * (3 + 2 + 4)];
            int cv = 0;
            for (int face = 0; face < 6; face++)
            {
                var src = FallingCubeFaces[face];
                float shade = FallingFaceShade[face];
                const float uvMax = 0.999f;
                // Winding correction on a local copy: if the face's first triangle winds opposite
                // its normal, swap verts 1 and 3 (same as Mesher.EmitBox).
                float[] verts = new float[12];
                Array.Copy(src, verts, 12);
                var p0 = new Point3D(verts[0], verts[1], verts[2]);
                var p1 = new Point3D(verts[3], verts[4], verts[5]);
                var p2 = new Point3D(verts[6], verts[7], verts[8]);
                var e1 = p1 - p0;
                var e2 = p2 - p0;
                var cross = new Point3D(e1.Y * e2.Z - e1.Z * e2.Y, e1.Z * e2.X - e1.X * e2.Z, e1.X * e2.Y - e1.Y * e2.X);
                var n = FallingFaceNormals[face];
                if (cross.X * n.X + cross.Y * n.Y + cross.Z * n.Z < 0)
                {
                    (verts[3], verts[4], verts[5], verts[9], verts[10], verts[11]) =
                        (verts[9], verts[10], verts[11], verts[3], verts[4], verts[5]);
                }
                for (int c = 0; c < 4; c++)
                {
                    cubeVerts[cv++] = verts[c * 3 + 0];
                    cubeVerts[cv++] = verts[c * 3 + 1];
                    cubeVerts[cv++] = verts[c * 3 + 2];
                    cubeVerts[cv++] = (c == 1 || c == 2) ? uvMax : 0f;
                    cubeVerts[cv++] = (c == 2 || c == 3) ? uvMax : 0f;
                    cubeVerts[cv++] = shade; cubeVerts[cv++] = shade; cubeVerts[cv++] = shade; cubeVerts[cv++] = 1f;
                }
            }
            _fallingVertexBuffer = factory.CreateBuffer(new BufferDescription((uint)cubeVerts.Length * sizeof(float), BufferUsage.VertexBuffer));
            _gd.UpdateBuffer(_fallingVertexBuffer, 0, cubeVerts);
            var cubeIndices = new ushort[FallingCubeIndices];
            int ci = 0;
            for (int face = 0; face < 6; face++)
            {
                int fv = face * 4;
                cubeIndices[ci++] = (ushort)(fv + 0);
                cubeIndices[ci++] = (ushort)(fv + 1);
                cubeIndices[ci++] = (ushort)(fv + 2);
                cubeIndices[ci++] = (ushort)(fv + 0);
                cubeIndices[ci++] = (ushort)(fv + 2);
                cubeIndices[ci++] = (ushort)(fv + 3);
            }
            _fallingIndexBuffer = factory.CreateBuffer(new BufferDescription((uint)cubeIndices.Length * sizeof(ushort), BufferUsage.IndexBuffer));
            _gd.UpdateBuffer(_fallingIndexBuffer, 0, cubeIndices);

            // create texture resource set if atlas loaded
            if (_atlasView != null && _atlasSampler != null)
            {
                _textureSet = factory.CreateResourceSet(new ResourceSetDescription(_textureLayout, _atlasView, _atlasSampler));
            }

            // Reuse a single command list across frames instead of allocating one per frame.
            _commandList = factory.CreateCommandList();

            CreateHighlightPipeline();
            CreateChunkBorderPipeline();
            CreateModelPipeline();
            CreateSkyPipeline();
            CreateCloudPipeline();
        }

        // Pipeline for textured entity models (the duck). Vertices are supplied in world space each
        // frame (transformed on the CPU per instance), so it shares the scene projection/view and
        // samples a dedicated per-entity texture rather than the block atlas.
        private void CreateModelPipeline()
        {
            var factory = _gd.ResourceFactory;

            string vsCode = @"#version 450
layout(location=0) in vec3 aPosition;
layout(location=1) in vec2 aUV;
layout(location=2) in vec4 aColor;
layout(location=0) out vec2 vUV;
layout(location=1) out vec4 vColor;
layout(set=0, binding=0) uniform ProjectionView { mat4 projView; };
void main() { vUV = aUV; vColor = aColor; gl_Position = projView * vec4(aPosition, 1.0); }";

            string fsCode = @"#version 450
layout(location=0) in vec2 vUV;
layout(location=1) in vec4 vColor;
layout(set=1, binding=0) uniform sampler2D uTex;
layout(location=0) out vec4 outColor;
void main() {
    vec4 tex = texture(uTex, vUV);
    if (tex.a < 0.5) discard;
    outColor = tex * vColor;
}";

            var vsSpirv = SpirvCompilation.CompileGlslToSpirv(vsCode, "main", ShaderStages.Vertex, GlslCompileOptions.Default);
            var fsSpirv = SpirvCompilation.CompileGlslToSpirv(fsCode, "main", ShaderStages.Fragment, GlslCompileOptions.Default);
            var shaders = factory.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Vertex, vsSpirv.SpirvBytes, "main"),
                new ShaderDescription(ShaderStages.Fragment, fsSpirv.SpirvBytes, "main"));

            var vertexLayout = new VertexLayoutDescription(
                new VertexElementDescription("aPosition", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                new VertexElementDescription("aUV", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
                new VertexElementDescription("aColor", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4));

            var pipelineDesc = new GraphicsPipelineDescription()
            {
                BlendState = BlendStateDescription.SingleOverrideBlend,
                DepthStencilState = new DepthStencilStateDescription(true, true, ComparisonKind.LessEqual),
                // Cull nothing: the model has thin, single-quad parts (legs/feet) whose winding
                // isn't consistently front-facing, and an opaque duck looks fine drawn double-sided.
                RasterizerState = new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.CounterClockwise, true, false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _projViewLayout, _textureLayout },
                ShaderSet = new ShaderSetDescription(new[] { vertexLayout }, new[] { shaders[0], shaders[1] }),
                Outputs = _sc.Framebuffer.OutputDescription
            };

            _modelPipeline = factory.CreateGraphicsPipeline(pipelineDesc);

            if (_duckView != null && _duckSampler != null)
            {
                _duckTextureSet = factory.CreateResourceSet(new ResourceSetDescription(_textureLayout, _duckView, _duckSampler));
            }

            if (_playerView != null && _playerSampler != null)
            {
                _playerTextureSet = factory.CreateResourceSet(new ResourceSetDescription(_textureLayout, _playerView, _playerSampler));
            }
        }

        // Pipeline for the targeted block-face highlight: a translucent quad drawn in world space
        // that shares the scene's projection/view and depth buffer. Depth testing (LessEqual) makes
        // any nearer block occlude it per-pixel, so a partially hidden face is only shown where it
        // is actually visible. Depth writes are disabled so it doesn't perturb subsequent draws.
        private void CreateHighlightPipeline()
        {
            var factory = _gd.ResourceFactory;

            string vsCode = @"#version 450
layout(location=0) in vec3 aPosition;
layout(set=0, binding=0) uniform ProjectionView { mat4 projView; };
void main() { gl_Position = projView * vec4(aPosition, 1.0); }";

            string fsCode = @"#version 450
layout(location=0) out vec4 outColor;
void main() { outColor = vec4(1.0, 1.0, 1.0, 0.35); }";

            var vsSpirv = SpirvCompilation.CompileGlslToSpirv(vsCode, "main", ShaderStages.Vertex, GlslCompileOptions.Default);
            var fsSpirv = SpirvCompilation.CompileGlslToSpirv(fsCode, "main", ShaderStages.Fragment, GlslCompileOptions.Default);
            var shaders = factory.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Vertex, vsSpirv.SpirvBytes, "main"),
                new ShaderDescription(ShaderStages.Fragment, fsSpirv.SpirvBytes, "main"));

            var vertexLayout = new VertexLayoutDescription(
                new VertexElementDescription("aPosition", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3));

            var pipelineDesc = new GraphicsPipelineDescription()
            {
                BlendState = BlendStateDescription.SingleAlphaBlend,
                DepthStencilState = new DepthStencilStateDescription(true, false, ComparisonKind.LessEqual),
                RasterizerState = new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.CounterClockwise, true, false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _projViewLayout },
                ShaderSet = new ShaderSetDescription(new[] { vertexLayout }, new[] { shaders[0], shaders[1] }),
                Outputs = _sc.Framebuffer.OutputDescription
            };

            _highlightPipeline = factory.CreateGraphicsPipeline(pipelineDesc);

            _highlightVertexBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)(_highlightVertexScratch.Length * sizeof(float)), BufferUsage.VertexBuffer | BufferUsage.Dynamic));
            _highlightIndexBuffer = factory.CreateBuffer(new BufferDescription(
                6 * sizeof(ushort), BufferUsage.IndexBuffer));
            _gd.UpdateBuffer(_highlightIndexBuffer, 0, new ushort[] { 0, 1, 2, 0, 2, 3 });
        }

        // Pipeline for chunk border wireframe rendering (F3 debug)
        private void CreateChunkBorderPipeline()
        {
            var factory = _gd.ResourceFactory;

            string vsCode = @"#version 450
layout(location=0) in vec3 aPosition;
layout(set=0, binding=0) uniform ProjectionView { mat4 projView; };
void main() { gl_Position = projView * vec4(aPosition, 1.0); }";

            string fsCode = @"#version 450
layout(location=0) out vec4 outColor;
void main() { outColor = vec4(0.0, 1.0, 0.0, 0.5); }"; // Green wireframe

            var vsSpirv = SpirvCompilation.CompileGlslToSpirv(vsCode, "main", ShaderStages.Vertex, GlslCompileOptions.Default);
            var fsSpirv = SpirvCompilation.CompileGlslToSpirv(fsCode, "main", ShaderStages.Fragment, GlslCompileOptions.Default);
            var shaders = factory.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Vertex, vsSpirv.SpirvBytes, "main"),
                new ShaderDescription(ShaderStages.Fragment, fsSpirv.SpirvBytes, "main"));

            var vertexLayout = new VertexLayoutDescription(
                new VertexElementDescription("aPosition", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3));

            var pipelineDesc = new GraphicsPipelineDescription()
            {
                BlendState = BlendStateDescription.SingleAlphaBlend,
                DepthStencilState = new DepthStencilStateDescription(true, false, ComparisonKind.LessEqual),
                RasterizerState = new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.CounterClockwise, true, false),
                PrimitiveTopology = PrimitiveTopology.LineList,
                ResourceLayouts = new[] { _projViewLayout },
                ShaderSet = new ShaderSetDescription(new[] { vertexLayout }, new[] { shaders[0], shaders[1] }),
                Outputs = _sc.Framebuffer.OutputDescription
            };

            _chunkBorderPipeline = factory.CreateGraphicsPipeline(pipelineDesc);

            _chunkBorderVertexBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)(_chunkBorderVertexScratch.Length * sizeof(float)), BufferUsage.VertexBuffer | BufferUsage.Dynamic));
            _chunkBorderIndexBuffer = factory.CreateBuffer(new BufferDescription(
                256 * sizeof(ushort), BufferUsage.IndexBuffer | BufferUsage.Dynamic));
        }

        // Pipeline for the Infdev sky: two huge flat planes (glSkyList at y+16, glSkyList2 at y-16,
        // centered on the camera) that get linear fog applied per-fragment - bright sky color
        // overhead, fog color at the horizon, and a darkened indigo below the horizon. Drawn with
        // depth-write OFF before the world so terrain always paints over it.
        private void CreateSkyPipeline()
        {
            var factory = _gd.ResourceFactory;

            string vsCode = @"#version 450
layout(location=0) in vec3 aPosition;
layout(location=1) in vec4 aColor;
layout(location=0) out vec3 vWorldPos;
layout(location=1) out vec4 vColor;
layout(set=0, binding=0) uniform ProjectionView { mat4 projView; };
void main() { vWorldPos = aPosition; vColor = aColor; gl_Position = projView * vec4(aPosition, 1.0); }";

            string fsCode = @"#version 450
layout(location=0) in vec3 vWorldPos;
layout(location=1) in vec4 vColor;
layout(set=1, binding=0) uniform SkyFog {
    vec4 fogColor;   // rgb + pad
    vec2 fogRange;   // start, end
    vec4 cameraPos;  // xyz + pad
};
layout(location=0) out vec4 outColor;
void main() {
    // Infdev's sky fog is setupFog(-1): linear from 0 to farPlane*0.8. Distance to the camera
    // drives the gradient - overhead (16 blocks) is clear, the horizon is fully fogged.
    float dist = length(vWorldPos - cameraPos.xyz);
    float fog = clamp((fogRange.y - dist) / max(fogRange.y - fogRange.x, 1e-5), 0.0, 1.0);
    outColor = vec4(mix(fogColor.rgb, vColor.rgb, fog), 1.0);
}";

            var vsSpirv = SpirvCompilation.CompileGlslToSpirv(vsCode, "main", ShaderStages.Vertex, GlslCompileOptions.Default);
            var fsSpirv = SpirvCompilation.CompileGlslToSpirv(fsCode, "main", ShaderStages.Fragment, GlslCompileOptions.Default);
            var shaders = factory.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Vertex, vsSpirv.SpirvBytes, "main"),
                new ShaderDescription(ShaderStages.Fragment, fsSpirv.SpirvBytes, "main"));

            var vertexLayout = new VertexLayoutDescription(
                new VertexElementDescription("aPosition", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                new VertexElementDescription("aColor", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4));

            // Dedicated fog uniform for the sky so its range (0 .. farPlane*0.8) doesn't disturb the
            // world fog buffer (which is currently disabled but shares the same 48-byte layout).
            _skyFogLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("SkyFog", ResourceKind.UniformBuffer, ShaderStages.Fragment)));
            _skyFogBuffer = factory.CreateBuffer(new BufferDescription(48, BufferUsage.UniformBuffer | BufferUsage.Dynamic));
            _skyFogSet = factory.CreateResourceSet(new ResourceSetDescription(_skyFogLayout, _skyFogBuffer));

            // The sky matrix reuses the projView layout (mat4) but holds the ROTATION-ONLY view *
            // projection, so the camera-space sky quads render glued to the eye (Infdev's approach).
            _skyMatrixBuffer = factory.CreateBuffer(new BufferDescription(64, BufferUsage.UniformBuffer | BufferUsage.Dynamic));
            _skyMatrixSet = factory.CreateResourceSet(new ResourceSetDescription(_projViewLayout, _skyMatrixBuffer));

            var pipelineDesc = new GraphicsPipelineDescription()
            {
                BlendState = BlendStateDescription.SingleDisabled,
                DepthStencilState = DepthStencilStateDescription.Disabled,
                RasterizerState = new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.CounterClockwise, true, false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _projViewLayout, _skyFogLayout },
                ShaderSet = new ShaderSetDescription(new[] { vertexLayout }, new[] { shaders[0], shaders[1] }),
                Outputs = _sc.Framebuffer.OutputDescription
            };

            _skyPipeline = factory.CreateGraphicsPipeline(pipelineDesc);

            // Two quads (top + bottom sky planes): 8 vertices of pos(3)+color(4) = 56 floats.
            // The buffer is filled in DrawSky with CAMERA-SPACE coordinates (relative to the eye),
            // so it never needs world-space rebuilding.
            _skyVertexBuffer = factory.CreateBuffer(new BufferDescription(
                8 * 7 * sizeof(float), BufferUsage.VertexBuffer | BufferUsage.Dynamic));
            _skyIndexBuffer = factory.CreateBuffer(new BufferDescription(
                12 * sizeof(ushort), BufferUsage.IndexBuffer));
            _gd.UpdateBuffer(_skyIndexBuffer, 0, new ushort[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 });
        }

        // A real flat cloud plane at a FIXED WORLD HEIGHT (CloudWorldY) that follows the camera
        // in X/Z. The key to it looking right is TILED UVs: the texture repeats every CloudTileSize
        // blocks, so clouds are proper puffs instead of one stretched smear. World-space vertices
        // + full view-projection, depth-write OFF so terrain paints over it from below.
        private void CreateCloudPipeline()
        {
            var factory = _gd.ResourceFactory;

            string vsCode = @"#version 450
layout(location=0) in vec3 aPosition;
layout(location=1) in vec2 aUV;
layout(location=0) out vec2 vUV;
layout(set=0, binding=0) uniform ProjectionView { mat4 projView; };
void main() { vUV = aUV; gl_Position = projView * vec4(aPosition, 1.0); }";

            string fsCode = @"#version 450
layout(location=0) in vec2 vUV;
layout(set=1, binding=0) uniform sampler2D uClouds;
layout(set=1, binding=1) uniform CloudParams {
    vec4 scrollOpacity; // x=scrollU, y=scrollV, z=opacity, w=unused
};
layout(location=0) out vec4 outColor;
void main() {
    vec2 uv = fract(vUV + scrollOpacity.xy);
    float a = texture(uClouds, uv).a * scrollOpacity.z;
    if (a < 0.01) discard;
    outColor = vec4(vec3(1.0), a);
}";

            var vsSpirv = SpirvCompilation.CompileGlslToSpirv(vsCode, "main", ShaderStages.Vertex, GlslCompileOptions.Default);
            var fsSpirv = SpirvCompilation.CompileGlslToSpirv(fsCode, "main", ShaderStages.Fragment, GlslCompileOptions.Default);
            var shaders = factory.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Vertex, vsSpirv.SpirvBytes, "main"),
                new ShaderDescription(ShaderStages.Fragment, fsSpirv.SpirvBytes, "main"));

            var vertexLayout = new VertexLayoutDescription(
                new VertexElementDescription("aPosition", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                new VertexElementDescription("aUV", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2));

            _cloudParamsLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("uClouds", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("uCloudsSampler", ResourceKind.Sampler, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("CloudParams", ResourceKind.UniformBuffer, ShaderStages.Fragment)));
            _cloudParamsBuffer = factory.CreateBuffer(new BufferDescription(16, BufferUsage.UniformBuffer | BufferUsage.Dynamic));
            _cloudTexture = factory.CreateTexture(TextureDescription.Texture2D(
                256, 256, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled));
            _cloudTextureView = factory.CreateTextureView(_cloudTexture);
            _gd.UpdateTexture(_cloudTexture, GenerateCloudTexture(_cloudSeed), 0, 0, 0, 256, 256, 1, 0, 0);
            _cloudParamsSet = factory.CreateResourceSet(new ResourceSetDescription(_cloudParamsLayout, _cloudTextureView,
                factory.CreateSampler(new SamplerDescription(
                    SamplerAddressMode.Wrap,
                    SamplerAddressMode.Wrap,
                    SamplerAddressMode.Wrap,
                    SamplerFilter.MinPoint_MagPoint_MipPoint,
                    null,
                    1,
                    0,
                    uint.MaxValue,
                    0,
                    SamplerBorderColor.TransparentBlack)),
                _cloudParamsBuffer));

            _cloudPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription()
            {
                BlendState = BlendStateDescription.SingleAlphaBlend,
                // Depth TEST on, WRITE off: clouds draw AFTER the world and blend over it, so
                // terrain shows through the translucent pixels. From below, closer terrain fails
                // the cloud fragments (clouds hidden behind hills); from above, the cloud layer
                // blends over the farther terrain - you see the world THROUGH the clouds.
                DepthStencilState = new DepthStencilStateDescription(true, false, ComparisonKind.LessEqual),
                RasterizerState = new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.CounterClockwise, true, false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                // Set 0 uses the WIDE-far matrix (see _cloudMatrixBuffer) so the cloud deck
                // stretches ~3x beyond terrain before clipping.
                ResourceLayouts = new[] { _projViewLayout, _cloudParamsLayout },
                ShaderSet = new ShaderSetDescription(new[] { vertexLayout }, new[] { shaders[0], shaders[1] }),
                Outputs = _sc.Framebuffer.OutputDescription
            });

            // Dedicated wide-far matrix for the cloud deck (set 0), updated in UpdateCamera.
            _cloudMatrixBuffer = factory.CreateBuffer(new BufferDescription(64, BufferUsage.UniformBuffer | BufferUsage.Dynamic));
            _cloudMatrixSet = factory.CreateResourceSet(new ResourceSetDescription(_projViewLayout, _cloudMatrixBuffer));

            // One big world-space quad (2 triangles). Filled in DrawClouds each frame around the
            // camera's X/Z so the plane follows you while staying at the fixed world height.
            _cloudVertexBuffer = factory.CreateBuffer(new BufferDescription(
                4 * 5 * sizeof(float), BufferUsage.VertexBuffer | BufferUsage.Dynamic));
            _cloudIndexBuffer = factory.CreateBuffer(new BufferDescription(
                6 * sizeof(ushort), BufferUsage.IndexBuffer));
            _gd.UpdateBuffer(_cloudIndexBuffer, 0, new ushort[] { 0, 1, 2, 0, 2, 3 });

            CreateWorldPlanePipeline(factory, vsSpirv.SpirvBytes);
        }

        // The "world from above" plane: a giant flat green+water textured quad at WorldPlaneY,
        // shown only when the player is very high. It uses the SAME wide-far matrix as clouds,
        // depth-write OFF + drawn BEFORE the world, so real terrain always paints over it and the
        // plane only shows in the distance (mimicking looking down on a distant earth).
        private void CreateWorldPlanePipeline(ResourceFactory factory, byte[] cloudVsSpirv)
        {
            string fsCode = @"#version 450
layout(location=0) in vec2 vUV;
layout(set=1, binding=0) uniform sampler2D uEarth;
layout(location=0) out vec4 outColor;
void main() {
    vec4 tex = texture(uEarth, vUV);
    outColor = vec4(tex.rgb, 1.0);
}";

            var fsSpirv = SpirvCompilation.CompileGlslToSpirv(fsCode, "main", ShaderStages.Fragment, GlslCompileOptions.Default);
            var shaders = factory.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Vertex, cloudVsSpirv, "main"),
                new ShaderDescription(ShaderStages.Fragment, fsSpirv.SpirvBytes, "main"));

            var vertexLayout = new VertexLayoutDescription(
                new VertexElementDescription("aPosition", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                new VertexElementDescription("aUV", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2));

            _worldPlaneLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("uEarth", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("uEarthSampler", ResourceKind.Sampler, ShaderStages.Fragment)));
            _worldPlaneTexture = factory.CreateTexture(TextureDescription.Texture2D(
                256, 256, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled));
            _worldPlaneTextureView = factory.CreateTextureView(_worldPlaneTexture);
            _gd.UpdateTexture(_worldPlaneTexture, GenerateWorldPlaneTexture(_cloudSeed), 0, 0, 0, 256, 256, 1, 0, 0);
            _worldPlaneTextureSet = factory.CreateResourceSet(new ResourceSetDescription(_worldPlaneLayout, _worldPlaneTextureView,
                factory.CreateSampler(new SamplerDescription(
                    SamplerAddressMode.Wrap,
                    SamplerAddressMode.Wrap,
                    SamplerAddressMode.Wrap,
                    SamplerFilter.MinLinear_MagLinear_MipLinear,
                    null,
                    1,
                    0,
                    uint.MaxValue,
                    0,
                    SamplerBorderColor.TransparentBlack))));
            _worldPlaneMatrixSet = factory.CreateResourceSet(new ResourceSetDescription(_projViewLayout, _cloudMatrixBuffer));

            _worldPlanePipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription()
            {
                BlendState = BlendStateDescription.SingleDisabled,
                // Drawn BEFORE the world with depth disabled: it only shows where terrain isn't.
                DepthStencilState = DepthStencilStateDescription.Disabled,
                RasterizerState = new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.CounterClockwise, true, false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _projViewLayout, _worldPlaneLayout },
                ShaderSet = new ShaderSetDescription(new[] { vertexLayout }, new[] { shaders[0], shaders[1] }),
                Outputs = _sc.Framebuffer.OutputDescription
            });

            _worldPlaneVertexBuffer = factory.CreateBuffer(new BufferDescription(
                4 * 5 * sizeof(float), BufferUsage.VertexBuffer | BufferUsage.Dynamic));
            _worldPlaneIndexBuffer = factory.CreateBuffer(new BufferDescription(
                6 * sizeof(ushort), BufferUsage.IndexBuffer));
            _gd.UpdateBuffer(_worldPlaneIndexBuffer, 0, new ushort[] { 0, 1, 2, 0, 2, 3 });
        }

        // Generates the green+water "earth seen from above" texture (noise blob walkers again):
        // green land with blue water patches and lighter coastline, seeded per world.
        private static byte[] GenerateWorldPlaneTexture(int seed)
        {
            const int size = 256;
            const int cols = size;
            const int rows = size;
            var mask = new byte[cols * rows];
            int CellIndex(int x, int y) => y * cols + x;
            var rng = new Random(seed * 7 + 13);

            void StampSquare(int x, int y, int half)
            {
                for (int oy = -half; oy <= half; oy++)
                for (int ox = -half; ox <= half; ox++)
                {
                    int nx = x + ox, ny = y + oy;
                    if (nx < 1 || ny < 1 || nx >= cols - 1 || ny >= rows - 1) continue;
                    mask[CellIndex(nx, ny)] = 1;
                }
            }

            // Water blobs (blue) on green land. Bigger patches + small lakes.
            for (int i = 0; i < 10; i++)
            {
                int x = 4 + rng.Next(cols - 8);
                int y = 4 + rng.Next(rows - 8);
                int half = 2 + rng.Next(6);
                int steps = 4 + rng.Next(12);
                for (int s = 0; s < steps; s++)
                {
                    StampSquare(x, y, half);
                    x = Math.Max(2, Math.Min(cols - 3, x + rng.Next(5) - 2));
                    y = Math.Max(2, Math.Min(rows - 3, y + rng.Next(5) - 2));
                }
            }
            for (int i = 0; i < 30; i++)
            {
                int x = 2 + rng.Next(cols - 4);
                int y = 2 + rng.Next(rows - 4);
                StampSquare(x, y, 1 + rng.Next(2));
            }

            var rgba = new byte[size * size * 4];
            for (int y = 0; y < rows; y++)
            for (int x = 0; x < cols; x++)
            {
                bool water = mask[CellIndex(x, y)] != 0;
                // land: green; water: blue; coastline slightly lighter.
                byte r = (byte)(water ? 40 : 60);
                byte g = (byte)(water ? 110 : 150);
                byte b = (byte)(water ? 180 : 60);
                int dst = (y * size + x) * 4;
                rgba[dst] = r; rgba[dst + 1] = g; rgba[dst + 2] = b; rgba[dst + 3] = 255;
            }
            return rgba;
        }

        // Draws the cloud plane. Vertices are WORLD-space, centered on the camera's X/Z at
        // CloudWorldY; UVs are tiled by world position (CloudTileSize blocks per tile) so the
        // puffs repeat like MC clouds instead of one stretched texture.
        private void DrawClouds(CommandList cl)
        {
            if (_cloudPipeline == null || _cloudVertexBuffer == null || !_cameraPosition.HasValue) return;
            // Big enough that the plane's edges stay beyond the WIDE far plane (3x the world far).
            float extent = Math.Max(_cloudFarPlane * 1.5f, 1536f);
            float camX = (float)_cameraPosition.Value.X;
            float camZ = (float)_cameraPosition.Value.Z;
            float y = CloudWorldY;

            // Tiled UVs from WORLD position (so clouds stay put as you walk; the scroll uniform
            // drifts them slowly). u = worldX / tileSize, v = worldZ / tileSize.
            float u0 = (camX - extent) / CloudTileSize;
            float u1 = (camX + extent) / CloudTileSize;
            float v0 = (camZ - extent) / CloudTileSize;
            float v1 = (camZ + extent) / CloudTileSize;

            float[] verts =
            {
                camX - extent, y, camZ - extent, u0, v0,
                camX + extent, y, camZ - extent, u1, v0,
                camX + extent, y, camZ + extent, u1, v1,
                camX - extent, y, camZ + extent, u0, v1,
            };
            _gd.UpdateBuffer(_cloudVertexBuffer, 0, verts);

            // Scroll + opacity.
            float now = (float)_cloudClock.Elapsed.TotalSeconds;
            _cloudScrollU = (float)Math.IEEERemainder(_cloudScrollU + 0.002f * (now - _lastCloudTime), 1.0);
            _cloudScrollV = (float)Math.IEEERemainder(_cloudScrollV + 0.0007f * (now - _lastCloudTime), 1.0);
            _lastCloudTime = now;
            _cloudParams[0] = _cloudScrollU;
            _cloudParams[1] = _cloudScrollV;
            _cloudParams[2] = 0.7f; // cloud opacity: translucent but still clearly visible
            _cloudParams[3] = 0f;
            _gd.UpdateBuffer(_cloudParamsBuffer, 0, _cloudParams);

            cl.SetPipeline(_cloudPipeline);
            // Use the WIDE-far matrix so the cloud deck extends ~3x beyond the terrain.
            cl.SetGraphicsResourceSet(0, _cloudMatrixSet ?? _projViewSet);
            if (_cloudParamsSet != null) cl.SetGraphicsResourceSet(1, _cloudParamsSet);
            cl.SetVertexBuffer(0, _cloudVertexBuffer);
            cl.SetIndexBuffer(_cloudIndexBuffer, IndexFormat.UInt16);
            cl.DrawIndexed(6, 1, 0, 0, 0);
        }

        // Draws the "world from above" plane when the player is high: a giant flat green+water
        // textured quad at WorldPlaneY, using the WIDE-far matrix. Drawn BEFORE the world with
        // depth disabled, so real terrain always paints over it - it only shows in the distance,
        // mimicking looking down on a distant earth. Fades away as the player descends.
        private void DrawWorldPlane(CommandList cl)
        {
            if (_worldPlanePipeline == null || _worldPlaneVertexBuffer == null || !_cameraPosition.HasValue) return;
            var cam = _cameraPosition.Value;
            // Only show when the camera is well above the fake earth altitude.
            double alt = cam.Y - WorldPlaneY;
            if (alt < WorldPlaneShowThreshold) return;
            float fade = (float)Math.Clamp((alt - WorldPlaneShowThreshold) / 60.0, 0.0, 1.0);
            if (fade <= 0.01f) return;

            float extent = Math.Max(_cloudFarPlane * 1.5f, 1536f);
            float camX = (float)cam.X;
            float camZ = (float)cam.Z;
            float y = WorldPlaneY;
            float u0 = camX / 256f, u1 = (camX + extent * 2f) / 256f;
            float v0 = camZ / 256f, v1 = (camZ + extent * 2f) / 256f;

            float[] verts =
            {
                camX - extent, y, camZ - extent, u0, v0,
                camX + extent, y, camZ - extent, u1, v0,
                camX + extent, y, camZ + extent, u1, v1,
                camX - extent, y, camZ + extent, u0, v1,
            };
            _gd.UpdateBuffer(_worldPlaneVertexBuffer, 0, verts);

            cl.SetPipeline(_worldPlanePipeline);
            cl.SetGraphicsResourceSet(0, _cloudMatrixSet ?? _projViewSet);
            if (_worldPlaneTextureSet != null) cl.SetGraphicsResourceSet(1, _worldPlaneTextureSet);
            cl.SetVertexBuffer(0, _worldPlaneVertexBuffer);
            cl.SetIndexBuffer(_worldPlaneIndexBuffer, IndexFormat.UInt16);
            cl.DrawIndexed(6, 1, 0, 0, 0);
        }

        // Clouds (Cubuild port, camera-locked): three flat planes at fixed heights relative to the
        // eye, textured with a procedurally generated white-puff texture that scrolls via shader
        // offsets. Depth-write OFF and drawn right after the sky so terrain always paints over.
        // Cubuild's createCloudMaskGrid ported to CPU RGBA: random blob walkers paint diamond-ish
        // puffs, then 2 rounds of cellular smoothing. White (255,255,255) with soft alpha.
        // 256x256 texture where each PIXEL is one cloud cell (no supersampling) - so a pixel is
        // a whole cloud when the tile is mapped to CloudTileSize blocks.
        private static byte[] GenerateCloudTexture(int seed)
        {
            const int size = 256;
            const int cols = size; // one pixel = one cell
            const int rows = size;
            var mask = new byte[cols * rows];

            int CellIndex(int x, int y) => y * cols + x;
            // Seeded per-world so each world has its own cloud pattern.
            var rng = new Random(seed);

            // Multi-scale blobs for organic variation:
            //  - a few BIG billowy masses (radius 3-6) that carve out large cloud banks
            //  - medium clusters (radius 2-3) that add density and texture
            //  - small puffs (radius 1-2) that scatter detail between the masses
            // Each scale walks a wandering path and stamps a hard SQUARE (authentic Infdev
            // clouds were square grid cells, not round puffs), so the result mixes huge square
            // cloud chunks, dense patches and sparse wisps instead of soft blobs.
            void StampSquare(int x, int y, int half)
            {
                for (int oy = -half; oy <= half; oy++)
                for (int ox = -half; ox <= half; ox++)
                {
                    int nx = x + ox;
                    int ny = y + oy;
                    if (nx < 1 || ny < 1 || nx >= cols - 1 || ny >= rows - 1) continue;
                    mask[CellIndex(nx, ny)] = 1;
                }
            }

            void WalkBlobs(int count, int minR, int maxR, int minSteps, int maxSteps)
            {
                for (int i = 0; i < count; i++)
                {
                    int x = 2 + rng.Next(cols - 4);
                    int y = 2 + rng.Next(rows - 4);
                    int steps = minSteps + rng.Next(maxSteps - minSteps + 1);
                    int radius = minR + rng.Next(maxR - minR + 1);
                    for (int s = 0; s < steps; s++)
                    {
                        int half = radius + (rng.NextDouble() > 0.6 ? 1 : 0);
                        // Occasional elongation for streaky square cloud banks (wider than deep).
                        if (rng.NextDouble() > 0.75)
                        {
                            int wide = half + 1;
                            for (int oy = -half; oy <= half; oy++)
                            for (int ox = -wide; ox <= wide; ox++)
                            {
                                int nx = x + ox;
                                int ny = y + oy;
                                if (nx < 1 || ny < 1 || nx >= cols - 1 || ny >= rows - 1) continue;
                                mask[CellIndex(nx, ny)] = 1;
                            }
                        }
                        else
                        {
                            StampSquare(x, y, half);
                        }
                        x = Math.Max(1, Math.Min(cols - 2, x + rng.Next(5) - 2));
                        y = Math.Max(1, Math.Min(rows - 2, y + rng.Next(5) - 2));
                    }
                }
            }

            // Big masses: few but huge, soft (low hardness) so they read as billowy banks.
            WalkBlobs(count: 10, minR: 4, maxR: 8, minSteps: 12, maxSteps: 30);
            // Medium clusters: many, denser, fill out the masses' interior.
            WalkBlobs(count: 20, minR: 2, maxR: 4, minSteps: 8, maxSteps: 20);
            // Small puffs: plenty, scattered, hard-edged detail.
            WalkBlobs(count: 30, minR: 1, maxR: 2, minSteps: 5, maxSteps: 12);

            // NO smoothing pass: the square stamps are already cohesive, and smoothing would
            // round off the hard corners. Infdev clouds are blocky grid cells.

            // Paint each pixel directly (one pixel = one cloud cell). Hard-edged squares: every
            // on pixel gets the same near-solid alpha - no edge softening (Infdev clouds are
            // flat grid squares, not fluffy puffs).
            var rgba = new byte[size * size * 4];
            for (int y = 0; y < rows; y++)
            for (int x = 0; x < cols; x++)
            {
                bool on = mask[CellIndex(x, y)] != 0;
                byte alpha = (byte)(on ? 245 : 0);
                int dst = (y * size + x) * 4;
                rgba[dst] = 255;
                rgba[dst + 1] = 255;
                rgba[dst + 2] = 255;
                rgba[dst + 3] = alpha;
            }
            return rgba;
        }

        public void Resize(int width, int height)
        {
            _sc?.Resize((uint)Math.Max(1, width), (uint)Math.Max(1, height));
            _imguiRenderer?.WindowResized(Math.Max(1, width), Math.Max(1, height));
        }

        public void SetHud(HudState hud)
        {
            _hud = hud;
        }

        /// <summary>Feeds the active falling-block entities to the renderer (drawn as 3D cubes
        /// using the world pipeline so they depth-test against terrain).</summary>
        public void SetFallingBlocks(IReadOnlyList<CubeApp.FallingBlockData> fallingBlocks)
        {
            _fallingBlocks = fallingBlocks ?? Array.Empty<CubeApp.FallingBlockData>();
        }

        // Builds cube geometry for all falling blocks into the scratch buffers and draws them.
        // Modeled on DrawParticles but with real 3D cube faces (per-face tile + Infdev shading).
        // Instanced draw: the static cube mesh is bound once; only the per-instance buffer
        // (worldPos + tileRect per block) is re-uploaded each frame. For n falling blocks that's
        // 7 floats of instance data vs. 312 floats of full geometry - the cave-in-friendly path.
        private void DrawFallingBlocks(CommandList cl)
        {
            int n = _fallingBlocks.Count;
            if (n == 0 || _fallingPipeline == null) return;
            float atlasW = Math.Max(1f, _atlasWidth);
            float atlasH = Math.Max(1f, _atlasHeight);

            // 7 floats per instance: worldPos (3) + tileRect (4).
            int instFloats = n * 7;
            if (_fallingInstanceScratch.Length < instFloats) _fallingInstanceScratch = new float[instFloats];
            int vf = 0;
            for (int i = 0; i < n; i++)
            {
                var fb = _fallingBlocks[i];
                var def = BlockRegistry.GetById(fb.BlockId);
                var tr = def.AllTexture ?? default;
                _fallingInstanceScratch[vf++] = fb.X;
                _fallingInstanceScratch[vf++] = fb.Y;
                _fallingInstanceScratch[vf++] = fb.Z;
                _fallingInstanceScratch[vf++] = tr.X / atlasW;
                _fallingInstanceScratch[vf++] = tr.Y / atlasH;
                _fallingInstanceScratch[vf++] = tr.Width / atlasW;
                _fallingInstanceScratch[vf++] = tr.Height / atlasH;
            }

            EnsureFallingInstanceBuffer((uint)(instFloats * sizeof(float)));
            _gd.UpdateBuffer(_fallingInstanceBuffer, 0, _fallingInstanceScratch);

            cl.SetPipeline(_fallingPipeline);
            cl.SetGraphicsResourceSet(0, _projViewSet);
            if (_textureSet != null) cl.SetGraphicsResourceSet(1, _textureSet);
            cl.SetGraphicsResourceSet(2, _fogSet);
            cl.SetVertexBuffer(0, _fallingVertexBuffer);
            cl.SetVertexBuffer(1, _fallingInstanceBuffer);
            cl.SetIndexBuffer(_fallingIndexBuffer, IndexFormat.UInt16);
            cl.DrawIndexed(FallingCubeIndices, (uint)n, 0, 0, 0);
        }

        private void EnsureFallingInstanceBuffer(uint bytes)
        {
            if (_fallingInstanceBuffer == null || _fallingInstanceCapacity < bytes)
            {
                _fallingInstanceBuffer?.Dispose();
                _fallingInstanceBuffer = _gd.ResourceFactory.CreateBuffer(new BufferDescription(
                    Math.Max(bytes, 512), BufferUsage.VertexBuffer | BufferUsage.Dynamic));
                _fallingInstanceCapacity = Math.Max(bytes, 512);
            }
        }

        public void SetEntities(IReadOnlyList<CubeApp.MobRenderData> mobRenderData)
        {
            // Route the unified MobRenderData snapshots to per-model instance lists. DuckInstance
            // carries exactly the fields both models need, so it doubles as the player instance.
            _allMobRenderData = mobRenderData ?? Array.Empty<CubeApp.MobRenderData>();
            if (mobRenderData == null || mobRenderData.Count == 0)
            {
                _duckInstances = Array.Empty<CubeApp.DuckInstance>();
                _playerInstances = Array.Empty<CubeApp.DuckInstance>();
                _coyoteInstances = Array.Empty<CubeApp.DuckInstance>();
                return;
            }

            List<CubeApp.DuckInstance>? ducks = null;
            List<CubeApp.DuckInstance>? players = null;
            List<CubeApp.DuckInstance>? coyotes = null;
            for (int i = 0; i < mobRenderData.Count; i++)
            {
                var md = mobRenderData[i];
                bool isDuck = string.Equals(md.MobType, "duck", StringComparison.OrdinalIgnoreCase);
                bool isPlayer = !isDuck && string.Equals(md.MobType, "player", StringComparison.OrdinalIgnoreCase);
                bool isCoyote = !isDuck && !isPlayer && string.Equals(md.MobType, "coyote", StringComparison.OrdinalIgnoreCase);
                if (!isDuck && !isPlayer && !isCoyote) continue;

                var inst = new CubeApp.DuckInstance(
                    md.Position, md.Yaw, md.HeadYawLocal,
                    md.WalkPhase, md.WalkAmount, md.AnimTime, md.AnimBlend, md.FlapPhase,
                    md.VelocityY, md.OnGround,
                    md.IsDead, md.DeathT, md.DeathRollDir, md.HurtTimer);

                if (isDuck) (ducks ??= new List<CubeApp.DuckInstance>()).Add(inst);
                else if (isPlayer) (players ??= new List<CubeApp.DuckInstance>()).Add(inst);
                else (coyotes ??= new List<CubeApp.DuckInstance>()).Add(inst);
            }

            _duckInstances = (IReadOnlyList<CubeApp.DuckInstance>?)ducks ?? Array.Empty<CubeApp.DuckInstance>();
            _playerInstances = (IReadOnlyList<CubeApp.DuckInstance>?)players ?? Array.Empty<CubeApp.DuckInstance>();
            _coyoteInstances = (IReadOnlyList<CubeApp.DuckInstance>?)coyotes ?? Array.Empty<CubeApp.DuckInstance>();
        }

        public void Render()
        {
            // Process pending removals/uploads on render thread
            while (_pendingRemovals.TryDequeue(out var rem))
            {
                FreeChunkRange(rem);
            }

            // Process priority uploads (player edits) first - no limit for instant feedback
            while (_pendingPriorityUploads.TryDequeue(out var pu))
            {
                WriteChunkData(pu.Coord, pu.Vertices, pu.Indices, pu.CutoutVertices, pu.CutoutIndices, pu.GlassVertices, pu.GlassIndices, pu.TransparentVertices, pu.TransparentIndices);
            }

            int uploadsThisFrame = 0;
            while (uploadsThisFrame < _maxUploadsPerFrame && _pendingUploads.TryDequeue(out var pu))
            {
                WriteChunkData(pu.Coord, pu.Vertices, pu.Indices, pu.CutoutVertices, pu.CutoutIndices, pu.GlassVertices, pu.GlassIndices, pu.TransparentVertices, pu.TransparentIndices);
                uploadsThisFrame++;
            }

            if (_drawCommandsDirty)
            {
                RebuildDrawCommands();
                _drawCommandsDirty = false;
            }

            var cl = _commandList;
            cl.Begin();
            cl.SetFramebuffer(_sc.Framebuffer);
            // Infdev clears to the FOG color (EntityRenderer.updateFogColor -> glClearColor), NOT the
            // sky color. The sky planes fade to this same fog color at the far plane, so clearing to
            // fog color makes the horizon band (where the flat sky planes are clipped) blend
            // seamlessly instead of showing a bright sky-blue ring that follows the camera.
            cl.ClearColorTarget(0, new RgbaFloat(192f / 255f, 216f / 255f, 1f, 1f));
            cl.ClearDepthStencil(1f);

            // Advance the block-break particle simulation with the real frame delta.
            long now = _particleClock.ElapsedTicks;
            float particleDt = (float)((now - _lastParticleTicks) / (double)System.Diagnostics.Stopwatch.Frequency);
            _lastParticleTicks = now;
            if (particleDt > 0.1f) particleDt = 0.1f;
            if (_particleCount > 0) UpdateParticles(particleDt);

            UpdateFog();

            DrawSky(cl);
            DrawWorldPlane(cl);

            cl.SetPipeline(_pipeline);
            cl.SetGraphicsResourceSet(0, _projViewSet);
            if (_textureSet != null)
                cl.SetGraphicsResourceSet(1, _textureSet);

            // Record any GPU-side mega-buffer growth copies before drawing so the world draw
            // always sees the fully populated replacement buffer.
            foreach (var cp in _pendingBufferCopies)
            {
                cl.CopyBuffer(cp.Old, 0, cp.New, 0, cp.SizeBytes);
                _gd.DisposeWhenIdle(cp.Old);
            }
            _pendingBufferCopies.Clear();

            // One draw call for the visible chunk world via multi-draw indirect. Commands are
            // frustum-culled per frame, so chunks behind/off-screen never reach the GPU. Opaque
            // chunks draw first and depth-write; cutout sprites (cross plants / leaves) draw next,
            // alpha-testing and depth-writing so nearer quads occlude farther ones; glass alpha-tests
            // without depth-writing, so its chunks are sorted back-to-front (far first) like
            // Cubuild/THREE does for transparent objects - otherwise a far chunk drawn after a near
            // one would paint its frame on top. Water blends last, likewise sorted.
            if (_megaVertexBuffer != null && _megaIndexBuffer != null)
            {
                DrawWorldPass(cl, _drawCommands, _indirectScratch, _pipeline);
                if (_cutoutDrawCommands.Count > 0)
                {
                    DrawWorldPass(cl, _cutoutDrawCommands, _cutoutIndirectScratch, _cutoutPipeline);
                }
                if (_glassDrawCommands.Count > 0)
                {
                    SortPassBackToFront(_glassDrawCommands);
                    DrawWorldPass(cl, _glassDrawCommands, _glassIndirectScratch, _glassPipeline);
                }
                if (_transparentDrawCommands.Count > 0)
                {
                    SortPassBackToFront(_transparentDrawCommands);
                    DrawWorldPass(cl, _transparentDrawCommands, _transparentIndirectScratch, _transparentPipeline);
                }
                // Translucent tint (colored glass) draws AFTER water so its semi-transparent pixels
                // paint over the water/terrain behind it without depth-blocking anything.
                if (_glassDrawCommands.Count > 0)
                {
                    SortPassBackToFront(_glassDrawCommands);
                    DrawWorldPass(cl, _glassDrawCommands, _glassIndirectScratch, _translucentPipeline);
                }
            }

            // Clouds blend OVER the world (depth test on, write off) so terrain shows through them
            // from above and they're hidden behind hills from below.
            DrawClouds(cl);

            DrawParticles(cl);
            DrawFallingBlocks(cl);
            DrawDucks(cl);
            DrawPlayers(cl);
            DrawCoyotes(cl);
            DrawHighlight(cl);
            DrawChunkBorders(cl);

            _imguiRenderer.Update(1f / 60f, _uiInputSnapshot ?? NullInputSnapshot.Instance);
            BuildHudUi();
            _imguiRenderer.Render(_gd, cl);

            cl.End();
            _gd.SubmitCommands(cl);
            _gd.SwapBuffers(_sc);
        }

        // Sorts a no-depth-write pass (glass/water) far-to-near from the camera so the nearest
        // chunk draws last and paints over farther ones - the back-to-front order THREE.js applies
        // to transparent objects in Cubuild.
        private void SortPassBackToFront(System.Collections.Generic.List<(CubeApp.ChunkCoordinates Coord, IndirectDrawIndexedArguments Cmd)> commands)
        {
            if (!_cameraPosition.HasValue) return;
            var cam = _cameraPosition.Value;
            commands.Sort((a, b) => ChunkCenterDistSq(b.Coord, cam).CompareTo(ChunkCenterDistSq(a.Coord, cam))); // far first
        }

        private static float ChunkCenterDistSq(CubeApp.ChunkCoordinates coord, CubeApp.Point3D cam)
        {
            float cx = coord.X * ChunkManager.ChunkSize + ChunkManager.ChunkSize * 0.5f;
            float cz = coord.Z * ChunkManager.ChunkSize + ChunkManager.ChunkSize * 0.5f;
            float cy = ChunkManager.OriginYForLayer(coord.Layer) + ChunkManager.HeightForLayer(coord.Layer) * 0.5f;
            float dx = cx - (float)cam.X;
            float dy = cy - (float)cam.Y;
            float dz = cz - (float)cam.Z;
            return dx * dx + dy * dy + dz * dz;
        }

        // Renders the block-break particles as camera-facing quads using the world pipeline
        // (atlas sampling + depth test), so they're occluded by terrain like any block face.
        private void DrawParticles(CommandList cl)
        {
            int n = _particleCount;
            if (n == 0) return;
            float atlasW = Math.Max(1f, _atlasWidth);
            float atlasH = Math.Max(1f, _atlasHeight);

            int vertFloats = n * 4 * 13;
            if (_particleVertexScratch.Length < vertFloats) _particleVertexScratch = new float[vertFloats];
            int indexCount = n * 6;
            if (_particleIndexScratch.Length < indexCount) _particleIndexScratch = new ushort[indexCount];

            var r = _cameraRight;
            var u = _cameraUp;
            int vf = 0;
            int ii = 0;
            for (int i = 0; i < n; i++)
            {
                ref var p = ref _particles[i];
                float half = p.Size * 0.5f;
                var rx = r.X * half; var ry = r.Y * half; var rz = r.Z * half;
                var ux = u.X * half; var uy = u.Y * half; var uz = u.Z * half;

                float oX = p.X, oY = p.Y, oZ = p.Z;
                // corners: bottom-left, bottom-right, top-right, top-left
                float[,] corners =
                {
                    { oX - rx - ux, oY - ry - uy, oZ - rz - uz },
                    { oX + rx - ux, oY + ry - uy, oZ + rz - uz },
                    { oX + rx + ux, oY + ry + uy, oZ + rz + uz },
                    { oX - rx + ux, oY - ry + uy, oZ - rz + uz }
                };
                float u0 = p.TileX / atlasW;
                float v0 = p.TileY / atlasH;
                float uw = p.TileW / atlasW;
                float vh = p.TileH / atlasH;
                int baseV = i * 4;
                // UVs must never hit exactly 1.0 - the world shader samples via fract(vLocalUV),
                // and fract(1.0) == 0.0 would collapse the whole quad onto one texel.
                const float uvMax = 0.999f;
                for (int c = 0; c < 4; c++)
                {
                    _particleVertexScratch[vf++] = corners[c, 0];
                    _particleVertexScratch[vf++] = corners[c, 1];
                    _particleVertexScratch[vf++] = corners[c, 2];
                    _particleVertexScratch[vf++] = (c == 1 || c == 2) ? uvMax : 0f;
                    _particleVertexScratch[vf++] = (c == 2 || c == 3) ? uvMax : 0f;
                    _particleVertexScratch[vf++] = u0;
                    _particleVertexScratch[vf++] = v0;
                    _particleVertexScratch[vf++] = uw;
                    _particleVertexScratch[vf++] = vh;
                    _particleVertexScratch[vf++] = p.Brightness;
                    _particleVertexScratch[vf++] = p.Brightness;
                    _particleVertexScratch[vf++] = p.Brightness;
                    _particleVertexScratch[vf++] = 1f;
                }
                _particleIndexScratch[ii++] = (ushort)(baseV + 0);
                _particleIndexScratch[ii++] = (ushort)(baseV + 1);
                _particleIndexScratch[ii++] = (ushort)(baseV + 2);
                _particleIndexScratch[ii++] = (ushort)(baseV + 0);
                _particleIndexScratch[ii++] = (ushort)(baseV + 2);
                _particleIndexScratch[ii++] = (ushort)(baseV + 3);
            }

            EnsureParticleBuffers((uint)(vertFloats * sizeof(float)), (uint)(indexCount * sizeof(ushort)));
            _gd.UpdateBuffer(_particleVertexBuffer, 0, _particleVertexScratch);
            _gd.UpdateBuffer(_particleIndexBuffer, 0, _particleIndexScratch);

            cl.SetPipeline(_pipeline);
            cl.SetGraphicsResourceSet(0, _projViewSet);
            if (_textureSet != null) cl.SetGraphicsResourceSet(1, _textureSet);
            cl.SetGraphicsResourceSet(2, _fogSet);
            cl.SetVertexBuffer(0, _particleVertexBuffer);
            cl.SetIndexBuffer(_particleIndexBuffer, IndexFormat.UInt16);
            cl.DrawIndexed((uint)indexCount);
        }

        private void EnsureParticleBuffers(uint vbBytes, uint ibBytes)
        {
            if (_particleVertexBuffer == null || _particleVertexCapacityBytes < vbBytes)
            {
                _particleVertexBuffer?.Dispose();
                _particleVertexBuffer = _gd.ResourceFactory.CreateBuffer(new BufferDescription(
                    Math.Max(vbBytes, 4096), BufferUsage.VertexBuffer | BufferUsage.Dynamic));
                _particleVertexCapacityBytes = Math.Max(vbBytes, 4096);
            }
            if (_particleIndexBuffer == null || _particleIndexCapacityBytes < ibBytes)
            {
                _particleIndexBuffer?.Dispose();
                _particleIndexBuffer = _gd.ResourceFactory.CreateBuffer(new BufferDescription(
                    Math.Max(ibBytes, 2048), BufferUsage.IndexBuffer | BufferUsage.Dynamic));
                _particleIndexCapacityBytes = Math.Max(ibBytes, 2048);
            }
        }

        // Pushes the Infdev-style distance fog: linear from 25% of the far plane to the far
        // plane, colored to match the sky (the clear color), so distant terrain fades away.
        // The far plane scales with the render distance, so the F key moves the fog with it.
        private void UpdateFog()
        {
            // Fog DISABLED by request - fogStart 0 + huge fogEnd keeps the fog factor ~1 = clear at
            // every distance. The uniform plumbing stays so it's trivial to re-enable later.
            _fogParams[0] = 192f / 255f;
            _fogParams[1] = 216f / 255f;
            _fogParams[2] = 1f;
            _fogParams[3] = 1f;
            _fogParams[4] = 0f;
            _fogParams[5] = 1e6f;
            if (_cameraPosition.HasValue)
            {
                _fogParams[6] = (float)_cameraPosition.Value.X;
                _fogParams[7] = (float)_cameraPosition.Value.Y;
                _fogParams[8] = (float)_cameraPosition.Value.Z;
            }
            _fogParams[9] = 1f;

            // Day/night dim factor (Infdev skylight subtraction at render time). Reused slot: the
            // world fragment shaders multiply their final color by this 0..1 value. The fog color
            // itself also rides down at night so the horizon darkens with the sky.
            ComputeNightFactors();
            _fogParams[10] = _nightDim;      // world light multiplier
            _fogParams[11] = _nightSkyDim;   // fog color multiplier (fades to dark)
            _gd.UpdateBuffer(_fogBuffer, 0, _fogParams);
        }

        // Infdev's celestial-angle based dimming. The world light multiplier follows
        // calculateSkylightSubtracted (sky light 15 loses up to 11 at midnight); the fog color and
        // sky gradient use getSkyColor's cosine factor. Faithful to World.java.
        private void ComputeNightFactors()
        {
            long t = _hud.WorldTime % 24000;
            float ang = (t) / 24000.0f - 0.25f;
            if (ang < 0f) ang += 1f;
            if (ang > 1f) ang -= 1f;
            float raw = ang;
            float eased = 1f - (float)((Math.Cos(ang * Math.PI) + 1.0) / 2.0);
            ang = raw + (eased - raw) / 3f;

            // getSkyColor cosine factor (1 at noon -> 0 at midnight).
            float sky = (float)(Math.Cos(ang * Math.PI * 2.0) * 2.0 + 0.5);
            if (sky < 0f) sky = 0f;
            if (sky > 1f) sky = 1f;
            _nightSkyDim = sky;

            // World light: Infdev subtracts up to 11 from sky light at night, so brightness runs
            // from full to a dim floor. Map subtracted 0..11 onto a 1..~0.15 multiplier.
            float sub = 1f - (float)(Math.Cos(ang * Math.PI * 2.0) * 2.0 + 0.5);
            if (sub < 0f) sub = 0f;
            if (sub > 1f) sub = 1f;
            int subtracted = (int)(sub * 11f);
            _nightDim = 1f - subtracted / 15f;
            if (_nightDim < 0.12f) _nightDim = 0.12f;

            // Sky gradient base colors ride the sky factor (Infdev skyColor * sky factor).
            _nightSkyR = (136f / 255f) * Math.Max(sky, 0.1f);
            _nightSkyG = (187f / 255f) * Math.Max(sky, 0.1f);
            _nightSkyB = 1f * Math.Max(sky, 0.1f);
        }

        // Renders the Infdev sky: two giant fog-blended planes in CAMERA SPACE - the TOP plane sits
        // 16 blocks above the eye in Infdev's sky color (0x88BBFF); the BOTTOM plane sits 16 below
        // in the darkened color ((r*0.2+0.04, g*0.2+0.04, b*0.6+0.1)). Both fade to the fog color
        // (0xC0D8FF) with distance - the classic Infdev sky gradient. The vertices are CAMERA-space
        // (relative to the eye, spanning the far plane in every direction), transformed by a
        // ROTATION-ONLY view-projection, so the sky is structurally locked to the camera and can
        // never drift as the player walks - exactly how Infdev's display lists work.
        private void DrawSky(CommandList cl)
        {
            if (_skyPipeline == null) return;

            // Infdev sky colors, dimmed by the celestial angle (World.skyColor = 0x88BBFF at noon,
            // multiplied by getSkyColor's cosine factor at night). ComputeNightFactors ran just
            // before this in UpdateFog.
            float skyR = _nightSkyR, skyG = _nightSkyG, skyB = _nightSkyB;
            float darkR = skyR * 0.2f + 0.04f;
            float darkG = skyG * 0.2f + 0.04f;
            float darkB = skyB * 0.6f + 0.1f;

            // Infdev's sky fog: setupFog(-1) = linear 0 .. farPlane*0.8. The camera sits at the
            // origin of camera space, so the fog distance is just the fragment's position length.
            // The fog color darkens with the sky at night so the horizon blends seamlessly.
            _skyFogParams[0] = (192f / 255f) * Math.Max(_nightSkyDim, 0.1f);
            _skyFogParams[1] = (216f / 255f) * Math.Max(_nightSkyDim, 0.1f);
            _skyFogParams[2] = 1f * Math.Max(_nightSkyDim, 0.1f);
            _skyFogParams[3] = 1f;
            _skyFogParams[4] = 0f;
            _skyFogParams[5] = _farPlane * 0.8f;
            _skyFogParams[6] = 0f;
            _skyFogParams[7] = 0f;
            _skyFogParams[8] = 0f;
            _skyFogParams[9] = 1f;
            _gd.UpdateBuffer(_skyFogBuffer, 0, _skyFogParams);

            // Extent large enough to cover the far plane from any camera yaw (Infdev uses a 64-step
            // grid out to +-384, well past the far plane; we use the same scale).
            float extent = Math.Max(_farPlane * 2f, 768f);

            // 8 vertices in CAMERA space (eye at origin): top quad at y=+16 (verts 0-3), bottom
            // quad at y=-16 (verts 4-7). pos(3) + color(4).
            var v = new float[56];
            SetSkyVertex(v, 0, -extent, 16f, -extent, skyR, skyG, skyB);
            SetSkyVertex(v, 1, extent, 16f, -extent, skyR, skyG, skyB);
            SetSkyVertex(v, 2, extent, 16f, extent, skyR, skyG, skyB);
            SetSkyVertex(v, 3, -extent, 16f, extent, skyR, skyG, skyB);
            SetSkyVertex(v, 4, -extent, -16f, -extent, darkR, darkG, darkB);
            SetSkyVertex(v, 5, extent, -16f, -extent, darkR, darkG, darkB);
            SetSkyVertex(v, 6, extent, -16f, extent, darkR, darkG, darkB);
            SetSkyVertex(v, 7, -extent, -16f, extent, darkR, darkG, darkB);

            _gd.UpdateBuffer(_skyVertexBuffer, 0, v);

            cl.SetPipeline(_skyPipeline);
            cl.SetGraphicsResourceSet(0, _skyMatrixSet);
            cl.SetGraphicsResourceSet(1, _skyFogSet);
            cl.SetVertexBuffer(0, _skyVertexBuffer);
            cl.SetIndexBuffer(_skyIndexBuffer, IndexFormat.UInt16);
            cl.DrawIndexed(12, 1, 0, 0, 0);
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

        // Issues one indirect world draw for a chunk-command pass (opaque or transparent) using the
        // given pipeline. Commands are frustum-culled this frame; the indirect-args buffer contents
        // are refreshed each frame since the visible set changes with the camera.
        private void DrawWorldPass(
            CommandList cl,
            System.Collections.Generic.List<(CubeApp.ChunkCoordinates Coord, IndirectDrawIndexedArguments Cmd)> commands,
            IndirectDrawIndexedArguments[] scratch,
            Pipeline pipeline)
        {
            if (commands.Count == 0)
            {
                return;
            }

            uint visibleCount = CullDrawCommands(commands, scratch);
            if (visibleCount == 0)
            {
                return;
            }

            cl.SetPipeline(pipeline);
            cl.SetGraphicsResourceSet(0, _projViewSet);
            if (_textureSet != null)
                cl.SetGraphicsResourceSet(1, _textureSet);
            cl.SetGraphicsResourceSet(2, _fogSet);
            cl.SetVertexBuffer(0, _megaVertexBuffer);
            cl.SetIndexBuffer(_megaIndexBuffer, IndexFormat.UInt16);
            if (_gd.Features.DrawIndirect)
            {
                EnsureIndirectCapacity(visibleCount);
                // D3D11 indirect-args buffers are USAGE_DEFAULT (no Dynamic flag), so the contents
                // are pushed via CommandList.UpdateBuffer (UpdateSubresource).
                cl.UpdateBuffer(_indirectBuffer, 0, ref scratch[0], visibleCount * IndirectCommandStride);
                cl.DrawIndexedIndirect(_indirectBuffer, 0, visibleCount, IndirectCommandStride);
            }
            else
            {
                // Fallback for backends without indirect draws (D3D11 has it).
                for (int i = 0; i < visibleCount; i++)
                {
                    var cmd = scratch[i];
                    cl.DrawIndexed(cmd.IndexCount, cmd.InstanceCount, cmd.FirstIndex, (int)cmd.VertexOffset, cmd.FirstInstance);
                }
            }
        }

        private void DrawHighlight(CommandList cl)
        {
            var quad = _hud.HighlightWorldQuad;
            if (quad == null || quad.Length != 4 || _highlightPipeline == null)
            {
                return;
            }

            for (int i = 0; i < 4; i++)
            {
                _highlightVertexScratch[i * 3 + 0] = quad[i].X;
                _highlightVertexScratch[i * 3 + 1] = quad[i].Y;
                _highlightVertexScratch[i * 3 + 2] = quad[i].Z;
            }

            _gd.UpdateBuffer(_highlightVertexBuffer, 0, _highlightVertexScratch);

            cl.SetPipeline(_highlightPipeline);
            cl.SetGraphicsResourceSet(0, _projViewSet);
            cl.SetVertexBuffer(0, _highlightVertexBuffer);
            cl.SetIndexBuffer(_highlightIndexBuffer, IndexFormat.UInt16);
            cl.DrawIndexed(6, 1, 0, 0, 0);
        }

        private void DrawChunkBorders(CommandList cl)
        {
            if (!_hud.ShowDebug || _chunkBorderPipeline == null)
            {
                return;
            }

            int vertexIndex = 0;
            int chunkSize = ChunkManager.ChunkSize;
            int chunkHeight = ChunkManager.ChunkHeight;

            // Size the scratch + GPU buffer for every chunk in the render radius: each chunk
            // draws 12 border lines = 72 floats. The old fixed 768-float buffer silently dropped
            // lines once full - which left only the far chunks (drawn first) visible.
            int chunksWide = (2 * _hud.RenderDistance + 1) * (2 * _hud.RenderDistance + 1);
            int neededFloats = chunksWide * 12 * 6;
            if (_chunkBorderVertexScratch.Length < neededFloats)
            {
                _chunkBorderVertexScratch = new float[neededFloats];
                _chunkBorderVertexBuffer?.Dispose();
                _chunkBorderVertexBuffer = _gd.ResourceFactory.CreateBuffer(new BufferDescription(
                    (uint)(neededFloats * sizeof(float)), BufferUsage.VertexBuffer | BufferUsage.Dynamic));
            }

            // Draw chunk borders for loaded chunks around player
            for (int dz = -_hud.RenderDistance; dz <= _hud.RenderDistance; dz++)
            {
                for (int dx = -_hud.RenderDistance; dx <= _hud.RenderDistance; dx++)
                {
                    int chunkX = _hud.PlayerChunkX + dx;
                    int chunkZ = _hud.PlayerChunkZ + dz;

                    // Calculate chunk world bounds
                    float minX = chunkX * chunkSize;
                    float maxX = minX + chunkSize;
                    float minZ = chunkZ * chunkSize;
                    float maxZ = minZ + chunkSize;
                    float minY = ChunkManager.WorldOriginY;
                    float maxY = ChunkManager.WorldOriginY + chunkHeight;

                    // Add vertical edges (4 corners)
                    AddLine(minX, minY, minZ, minX, maxY, minZ, ref vertexIndex);
                    AddLine(maxX, minY, minZ, maxX, maxY, minZ, ref vertexIndex);
                    AddLine(minX, minY, maxZ, minX, maxY, maxZ, ref vertexIndex);
                    AddLine(maxX, minY, maxZ, maxX, maxY, maxZ, ref vertexIndex);

                    // Add horizontal edges at bottom
                    AddLine(minX, minY, minZ, maxX, minY, minZ, ref vertexIndex);
                    AddLine(minX, minY, maxZ, maxX, minY, maxZ, ref vertexIndex);
                    AddLine(minX, minY, minZ, minX, minY, maxZ, ref vertexIndex);
                    AddLine(maxX, minY, minZ, maxX, minY, maxZ, ref vertexIndex);

                    // Add horizontal edges at top
                    AddLine(minX, maxY, minZ, maxX, maxY, minZ, ref vertexIndex);
                    AddLine(minX, maxY, maxZ, maxX, maxY, maxZ, ref vertexIndex);
                    AddLine(minX, maxY, minZ, minX, maxY, maxZ, ref vertexIndex);
                    AddLine(maxX, maxY, minZ, maxX, maxY, maxZ, ref vertexIndex);
                }
            }

            if (vertexIndex > 0)
            {
                _gd.UpdateBuffer(_chunkBorderVertexBuffer, 0, _chunkBorderVertexScratch);

                cl.SetPipeline(_chunkBorderPipeline);
                cl.SetGraphicsResourceSet(0, _projViewSet);
                cl.SetVertexBuffer(0, _chunkBorderVertexBuffer);
                cl.Draw((uint)vertexIndex / 3, 1, 0, 0);
            }
        }

        private void AddLine(float x1, float y1, float z1, float x2, float y2, float z2, ref int vertexIndex)
        {
            if (vertexIndex + 6 > _chunkBorderVertexScratch.Length)
                return;

            _chunkBorderVertexScratch[vertexIndex++] = x1;
            _chunkBorderVertexScratch[vertexIndex++] = y1;
            _chunkBorderVertexScratch[vertexIndex++] = z1;
            _chunkBorderVertexScratch[vertexIndex++] = x2;
            _chunkBorderVertexScratch[vertexIndex++] = y2;
            _chunkBorderVertexScratch[vertexIndex++] = z2;
        }

        private void DrawDucks(CommandList cl)
        {
            var instances = _duckInstances;
            if (instances.Count == 0 || _modelPipeline == null || _duckTextureSet == null
                || _duckBones.Length == 0 || _duckVertsPerInstance == 0)
            {
                return;
            }

            int totalVertexFloats = instances.Count * _duckVertsPerInstance * DuckFloatsPerVertex;
            int totalIndices = instances.Count * _duckIndicesPerInstance;

            if (_duckVertexScratch.Length < totalVertexFloats)
            {
                _duckVertexScratch = new float[totalVertexFloats];
            }
            if (_duckIndexScratch.Length < totalIndices)
            {
                _duckIndexScratch = new ushort[totalIndices];
            }

            int vf = 0;
            int ii = 0;
            ushort baseVertex = 0;
            foreach (var inst in instances)
            {
                WriteDuck(inst, ref vf, ref ii, ref baseVertex);
            }

            EnsureDuckBuffers((uint)(totalVertexFloats * sizeof(float)), (uint)(totalIndices * sizeof(ushort)));
            _gd.UpdateBuffer(_duckVertexBuffer, 0, ref _duckVertexScratch[0], (uint)(totalVertexFloats * sizeof(float)));
            _gd.UpdateBuffer(_duckIndexBuffer, 0, ref _duckIndexScratch[0], (uint)(totalIndices * sizeof(ushort)));

            cl.SetPipeline(_modelPipeline);
            cl.SetGraphicsResourceSet(0, _projViewSet);
            cl.SetGraphicsResourceSet(1, _duckTextureSet);
            cl.SetVertexBuffer(0, _duckVertexBuffer);
            cl.SetIndexBuffer(_duckIndexBuffer, IndexFormat.UInt16);
            cl.DrawIndexed((uint)totalIndices, 1, 0, 0, 0);
        }

        // Poses one duck's bones (walk/flap/head-turn) and bakes them, with the body yaw, in-air /
        // death tilt and hurt-flash tint, into the shared vertex/index scratch buffers. Mirrors
        // Cubuild's updateDuckEntityVisual ('blockbench_duck' branch).
        private void WriteDuck(in CubeApp.DuckInstance inst, ref int vf, ref int ii, ref ushort baseVertex)
        {
            bool isDead = inst.IsDead;
            float walkPhase = inst.WalkPhase;
            float walkAmount = inst.WalkAmount;
            float flapPhase = inst.FlapPhase;

            float wingSwing = isDead ? 0f : (inst.OnGround ? (float)Math.Sin(walkPhase) * 0.55f * walkAmount : (float)Math.Sin(flapPhase) * 0.95f);
            float swing = isDead ? 0f : (float)Math.Sin(walkPhase) * 0.55f * walkAmount;
            float bob = isDead ? 0f : (Math.Abs((float)Math.Sin(walkPhase * 2.0f)) * 0.06f * walkAmount
                + (!inst.OnGround ? 0.03f + Math.Abs((float)Math.Sin(flapPhase * 0.5f)) * 0.03f : 0f));
            float hurtTilt = isDead ? 0f : (inst.HurtTimer > 0f ? (float)Math.Sin(inst.HurtTimer * 60.0f) * 0.06f : 0f);
            float deathRoll = isDead ? inst.DeathRollDir * (float)(Math.PI * 0.5) * (float)Math.Pow(inst.DeathT, 0.9) : 0f;

            float tiltZ = isDead ? deathRoll : ((inst.OnGround ? 0f : Math.Clamp(-inst.VelocityY * 0.03f, -0.2f, 0.2f)) + hurtTilt);
            float cosR = (float)Math.Cos(tiltZ), sinR = (float)Math.Sin(tiltZ);

            float renderYaw = inst.Yaw + (float)Math.PI;
            float cosY = (float)Math.Cos(renderYaw), sinY = (float)Math.Sin(renderYaw);

            float px = (float)inst.Position.X;
            float py = (float)inst.Position.Y;
            float pz = (float)inst.Position.Z;

            // Hurt / death flash: red channel unchanged, green/blue driven toward the tint.
            float blink = isDead ? 1f : (inst.HurtTimer > 0f ? ((float)Math.Sin(inst.HurtTimer * 95.0f) > 0f ? 1f : 0.72f) : 0f);
            float flashBlend = isDead ? 1f : (inst.HurtTimer > 0f ? Math.Clamp((inst.HurtTimer / 0.20f) * blink, 0f, 1f) : 0f);
            float gbMul = 1f - 0.82f * flashBlend;

            foreach (var bone in _duckBones)
            {
                float angle = bone.BaseAngle + BoneAnimDelta(bone.Id, wingSwing, swing, walkAmount, inst.HeadYawLocal);
                float ca = (float)Math.Cos(angle), sa = (float)Math.Sin(angle);
                float headExtraBob = bone.Id == DuckBoneId.Head ? bob * 0.15f : 0f;

                foreach (var v in bone.Vertices)
                {
                    // Rotate the vertex about the bone pivot on the bone's animation axis.
                    float lx = v.X - bone.PivotX;
                    float ly = v.Y - bone.PivotY;
                    float lz = v.Z - bone.PivotZ;
                    float rx = lx, ry = ly, rz = lz;
                    switch (bone.Axis)
                    {
                        case DuckBoneAxis.X: ry = ly * ca - lz * sa; rz = ly * sa + lz * ca; break;
                        case DuckBoneAxis.Y: rx = lx * ca + lz * sa; rz = -lx * sa + lz * ca; break;
                        case DuckBoneAxis.Z: rx = lx * ca - ly * sa; ry = lx * sa + ly * ca; break;
                    }
                    float mx = bone.PivotX + rx;
                    float my = bone.PivotY + ry + bob + headExtraBob;
                    float mz = bone.PivotZ + rz;

            // Body roll (Z) then body yaw (Y), matching three.js Euler 'XYZ' order.
            float ax = mx * cosR - my * sinR;
            float ay = mx * sinR + my * cosR;
            float az = mz;
            float fx = ax * cosY + az * sinY;
            float fz = -ax * sinY + az * cosY;

            // Mobs are bigger now: scale the whole model about its feet origin.
            fx *= DuckModelScale;
            ay *= DuckModelScale;
            fz *= DuckModelScale;

            _duckVertexScratch[vf++] = px + fx;
            _duckVertexScratch[vf++] = py + ay;
            _duckVertexScratch[vf++] = pz + fz;
                    _duckVertexScratch[vf++] = v.U;
                    _duckVertexScratch[vf++] = v.V;
                    _duckVertexScratch[vf++] = v.Shade;
                    _duckVertexScratch[vf++] = v.Shade * gbMul;
                    _duckVertexScratch[vf++] = v.Shade * gbMul;
                    _duckVertexScratch[vf++] = 1f;
                }

                for (int k = 0; k < bone.Indices.Length; k++)
                {
                    _duckIndexScratch[ii++] = (ushort)(bone.Indices[k] + baseVertex);
                }
                baseVertex += (ushort)bone.Vertices.Length;
            }
        }

        private static float BoneAnimDelta(DuckBoneId id, float wingSwing, float swing, float walkAmount, float headYawLocal)
        {
            switch (id)
            {
                case DuckBoneId.Head: return headYawLocal;
                case DuckBoneId.LeftWing: return -0.16f - wingSwing * 0.35f;
                case DuckBoneId.RightWing: return 0.16f + wingSwing * 0.35f;
                case DuckBoneId.LeftFoot: return swing * 1.25f;
                case DuckBoneId.RightFoot: return -swing * 1.25f;
                case DuckBoneId.Tail: return -0.12f * walkAmount;
                default: return 0f;
            }
        }

        private void EnsureDuckBuffers(uint vbSize, uint ibSize)
        {
            if (_duckVertexBuffer == null || _duckVertexCapacity < vbSize)
            {
                _duckVertexBuffer?.Dispose();
                _duckVertexBuffer = _gd.ResourceFactory.CreateBuffer(new BufferDescription(vbSize, BufferUsage.VertexBuffer | BufferUsage.Dynamic));
                _duckVertexCapacity = vbSize;
            }
            if (_duckIndexBuffer == null || _duckIndexCapacity < ibSize)
            {
                _duckIndexBuffer?.Dispose();
                _duckIndexBuffer = _gd.ResourceFactory.CreateBuffer(new BufferDescription(ibSize, BufferUsage.IndexBuffer | BufferUsage.Dynamic));
                _duckIndexCapacity = ibSize;
            }
        }

        // Uploads one chunk's vertices/indices into a region of the shared mega vertex/index buffers,
        // pooling the previous buffers for reuse.
        // Uploads one chunk's vertices/indices into a region of the shared mega vertex/index buffers,
// reusing freed holes or appending at the tail. The previous range (if any) is recycled.
        private void WriteChunkData(CubeApp.ChunkCoordinates coord, float[] verts, ushort[] indices,
            float[] cutoutVerts, ushort[] cutoutIndices, float[] glassVerts, ushort[] glassIndices,
            float[] transVerts, ushort[] transIndices)
        {
            if (_chunkRanges.TryGetValue(coord, out var prev))
            {
                FreeRange(prev);
            }
            if (_cutoutRanges.TryGetValue(coord, out var prevCutout))
            {
                FreeRange(prevCutout);
            }
            if (_glassRanges.TryGetValue(coord, out var prevGlass))
            {
                FreeRange(prevGlass);
            }
            if (_transparentRanges.TryGetValue(coord, out var prevTrans))
            {
                FreeRange(prevTrans);
            }

            uint vbBytes = (uint)(verts.Length * sizeof(float));
            uint ibBytes = (uint)(indices.Length * sizeof(ushort));
            var (vbo, _, ibo, _) = AllocateRange(vbBytes, ibBytes);

            _gd.UpdateBuffer(_megaVertexBuffer, vbo, verts);
            _gd.UpdateBuffer(_megaIndexBuffer, ibo, indices);

            _chunkRanges[coord] = new ChunkRange { VbOffsetBytes = vbo, VbBytes = vbBytes, IbOffsetBytes = ibo, IndexCount = (uint)indices.Length };

            // Cutout (cross plants / leaves) faces: only when the chunk actually has any.
            if (cutoutVerts != null && cutoutVerts.Length > 0 && cutoutIndices != null && cutoutIndices.Length > 0)
            {
                uint cvbBytes = (uint)(cutoutVerts.Length * sizeof(float));
                uint cibBytes = (uint)(cutoutIndices.Length * sizeof(ushort));
                var (cvbo, _, cibo, _) = AllocateRange(cvbBytes, cibBytes);

                _gd.UpdateBuffer(_megaVertexBuffer, cvbo, cutoutVerts);
                _gd.UpdateBuffer(_megaIndexBuffer, cibo, cutoutIndices);

                _cutoutRanges[coord] = new ChunkRange { VbOffsetBytes = cvbo, VbBytes = cvbBytes, IbOffsetBytes = cibo, IndexCount = (uint)cutoutIndices.Length };
            }
            else
            {
                _cutoutRanges.Remove(coord);
            }

            // Glass faces: only when the chunk actually has any.
            if (glassVerts != null && glassVerts.Length > 0 && glassIndices != null && glassIndices.Length > 0)
            {
                uint gvbBytes = (uint)(glassVerts.Length * sizeof(float));
                uint gibBytes = (uint)(glassIndices.Length * sizeof(ushort));
                var (gvbo, _, gibo, _) = AllocateRange(gvbBytes, gibBytes);

                _gd.UpdateBuffer(_megaVertexBuffer, gvbo, glassVerts);
                _gd.UpdateBuffer(_megaIndexBuffer, gibo, glassIndices);

                _glassRanges[coord] = new ChunkRange { VbOffsetBytes = gvbo, VbBytes = gvbBytes, IbOffsetBytes = gibo, IndexCount = (uint)glassIndices.Length };
            }
            else
            {
                _glassRanges.Remove(coord);
            }

            // Transparent (water) faces: only when the chunk actually has any.
            if (transVerts != null && transVerts.Length > 0 && transIndices != null && transIndices.Length > 0)
            {
                uint tvbBytes = (uint)(transVerts.Length * sizeof(float));
                uint tibBytes = (uint)(transIndices.Length * sizeof(ushort));
                var (tvbo, _, tibo, _) = AllocateRange(tvbBytes, tibBytes);

                _gd.UpdateBuffer(_megaVertexBuffer, tvbo, transVerts);
                _gd.UpdateBuffer(_megaIndexBuffer, tibo, transIndices);

                _transparentRanges[coord] = new ChunkRange { VbOffsetBytes = tvbo, VbBytes = tvbBytes, IbOffsetBytes = tibo, IndexCount = (uint)transIndices.Length };
            }
            else
            {
                _transparentRanges.Remove(coord);
            }

            _drawCommandsDirty = true;
        }

        // First-fit allocator: reuse a freed hole if one's big enough, else append at the tail
        // (growing the GPU buffers 2x if the tail would overflow).
        private (uint vbo, uint vbBytes, uint ibo, uint ibBytes) AllocateRange(uint vbBytes, uint ibBytes)
        {
            for (int i = 0; i < _freeBlocks.Count; i++)
            {
                var b = _freeBlocks[i];
                if (b.VbBytes >= vbBytes && b.IbBytes >= ibBytes)
                {
                    _freeBlocks.RemoveAt(i);
                    return (b.VbOffset, vbBytes, b.IbOffset, ibBytes);
                }
            }

            EnsureVertexCapacity(_vbTailBytes + vbBytes);
            EnsureIndexCapacity(_ibTailBytes + ibBytes);
            uint vbo = _vbTailBytes;
            uint ibo = _ibTailBytes;
            _vbTailBytes += vbBytes;
            _ibTailBytes += ibBytes;
            return (vbo, vbBytes, ibo, ibBytes);
        }

        private void FreeRange(ChunkRange r)
        {
            _freeBlocks.Add((r.VbOffsetBytes, r.VbBytes, r.IbOffsetBytes, r.IndexCount * sizeof(ushort)));
            _drawCommandsDirty = true;
        }

        private void FreeChunkRange(CubeApp.ChunkCoordinates coord)
        {
            if (_chunkRanges.TryGetValue(coord, out var r))
            {
                FreeRange(r);
                _chunkRanges.Remove(coord);
            }
            if (_cutoutRanges.TryGetValue(coord, out var cr))
            {
                FreeRange(cr);
                _cutoutRanges.Remove(coord);
            }
            if (_glassRanges.TryGetValue(coord, out var gr))
            {
                FreeRange(gr);
                _glassRanges.Remove(coord);
            }
            if (_transparentRanges.TryGetValue(coord, out var tr))
            {
                FreeRange(tr);
                _transparentRanges.Remove(coord);
            }
        }

        // Grows the mega vertex buffer to 2x (or to the needed size) when the tail would overflow.
        // Records a GPU CopyBuffer of the live region [0, tail) so the old data survives the swap;
        // the old buffer is disposed once the GPU is finished with it.
        private void EnsureVertexCapacity(uint needed)
        {
            if (_megaVertexBuffer != null && _vbCapacityBytes >= needed) return;
            uint newCap = Math.Max(needed, Math.Max(1024, _vbCapacityBytes * 2));
            var newBuf = _gd.ResourceFactory.CreateBuffer(new BufferDescription(newCap, BufferUsage.VertexBuffer | BufferUsage.Dynamic));
            if (_megaVertexBuffer != null)
            {
                _pendingBufferCopies.Add((_megaVertexBuffer, newBuf, _vbTailBytes));
            }
            _megaVertexBuffer = newBuf;
            _vbCapacityBytes = newCap;
        }

        private void EnsureIndexCapacity(uint needed)
        {
            if (_megaIndexBuffer != null && _ibCapacityBytes >= needed) return;
            uint newCap = Math.Max(needed, Math.Max(1024, _ibCapacityBytes * 2));
            var newBuf = _gd.ResourceFactory.CreateBuffer(new BufferDescription(newCap, BufferUsage.IndexBuffer | BufferUsage.Dynamic));
            if (_megaIndexBuffer != null)
            {
                _pendingBufferCopies.Add((_megaIndexBuffer, newBuf, _ibTailBytes));
            }
            _megaIndexBuffer = newBuf;
            _ibCapacityBytes = newCap;
        }

        // Creates (or grows) the indirect argument buffer. D3D11 requires indirect-args buffers
        // to be USAGE_DEFAULT (no Dynamic flag), so contents are refreshed via CommandList.UpdateBuffer.
        private void EnsureIndirectCapacity(uint commandCount)
        {
            if (_indirectBuffer != null && _indirectCapacityCommands >= commandCount) return;

            uint newCap = _indirectCapacityCommands == 0
                ? Math.Max(256, commandCount * 2)
                : Math.Max(_indirectCapacityCommands * 2, commandCount);
            var newBuf = _gd.ResourceFactory.CreateBuffer(new BufferDescription(
                newCap * IndirectCommandStride, BufferUsage.IndirectBuffer));
            if (_indirectBuffer != null)
            {
                _gd.DisposeWhenIdle(_indirectBuffer);
            }
            _indirectBuffer = newBuf;
            _indirectCapacityCommands = newCap;
        }

        private void RebuildDrawCommands()
        {
            _drawCommands.Clear();
            foreach (var kv in _chunkRanges)
            {
                var r = kv.Value;
                _drawCommands.Add((kv.Key, new IndirectDrawIndexedArguments
                {
                    IndexCount = r.IndexCount,
                    InstanceCount = 1,
                    FirstIndex = r.IbOffsetBytes / 2,           // ushort index units
                    VertexOffset = (int)(r.VbOffsetBytes / VertexStrideBytes),
                    FirstInstance = 0,
                }));
            }
            if (_indirectScratch.Length < _drawCommands.Count)
            {
                _indirectScratch = new IndirectDrawIndexedArguments[Math.Max(256, _drawCommands.Count * 2)];
            }

            _cutoutDrawCommands.Clear();
            foreach (var kv in _cutoutRanges)
            {
                var r = kv.Value;
                _cutoutDrawCommands.Add((kv.Key, new IndirectDrawIndexedArguments
                {
                    IndexCount = r.IndexCount,
                    InstanceCount = 1,
                    FirstIndex = r.IbOffsetBytes / 2,           // ushort index units
                    VertexOffset = (int)(r.VbOffsetBytes / VertexStrideBytes),
                    FirstInstance = 0,
                }));
            }
            if (_cutoutIndirectScratch.Length < _cutoutDrawCommands.Count)
            {
                _cutoutIndirectScratch = new IndirectDrawIndexedArguments[Math.Max(256, _cutoutDrawCommands.Count * 2)];
            }

            _glassDrawCommands.Clear();
            foreach (var kv in _glassRanges)
            {
                var r = kv.Value;
                _glassDrawCommands.Add((kv.Key, new IndirectDrawIndexedArguments
                {
                    IndexCount = r.IndexCount,
                    InstanceCount = 1,
                    FirstIndex = r.IbOffsetBytes / 2,           // ushort index units
                    VertexOffset = (int)(r.VbOffsetBytes / VertexStrideBytes),
                    FirstInstance = 0,
                }));
            }
            if (_glassIndirectScratch.Length < _glassDrawCommands.Count)
            {
                _glassIndirectScratch = new IndirectDrawIndexedArguments[Math.Max(256, _glassDrawCommands.Count * 2)];
            }

            _transparentDrawCommands.Clear();
            foreach (var kv in _transparentRanges)
            {
                var r = kv.Value;
                _transparentDrawCommands.Add((kv.Key, new IndirectDrawIndexedArguments
                {
                    IndexCount = r.IndexCount,
                    InstanceCount = 1,
                    FirstIndex = r.IbOffsetBytes / 2,           // ushort index units
                    VertexOffset = (int)(r.VbOffsetBytes / VertexStrideBytes),
                    FirstInstance = 0,
                }));
            }
            if (_transparentIndirectScratch.Length < _transparentDrawCommands.Count)
            {
                _transparentIndirectScratch = new IndirectDrawIndexedArguments[Math.Max(256, _transparentDrawCommands.Count * 2)];
            }
        }

        // Fills the given indirect scratch array with the commands from a pass list that are inside
        // the current view frustum. Returns the visible count; falls back to "everything" when no
        // camera is set.
        private uint CullDrawCommands(
            System.Collections.Generic.List<(CubeApp.ChunkCoordinates Coord, IndirectDrawIndexedArguments Cmd)> commands,
            IndirectDrawIndexedArguments[] scratch)
        {
            int n = 0;
            if (_viewProjection.HasValue)
            {
                ExtractFrustumPlanes(_viewProjection.Value);
                for (int i = 0; i < commands.Count; i++)
                {
                    if (ChunkInFrustum(commands[i].Coord))
                    {
                        scratch[n++] = commands[i].Cmd;
                    }
                }
            }
            else
            {
                for (int i = 0; i < commands.Count; i++)
                {
                    scratch[n++] = commands[i].Cmd;
                }
            }
            return (uint)n;
        }

        // Extracts the six clip planes from a row-vector view-projection matrix (0..1 depth range).
        private void ExtractFrustumPlanes(in Matrix4x4 m)
        {
            _frustumPlanes[0] = new Vector4(m.M14 + m.M11, m.M24 + m.M21, m.M34 + m.M31, m.M44 + m.M41); // left
            _frustumPlanes[1] = new Vector4(m.M14 - m.M11, m.M24 - m.M21, m.M34 - m.M31, m.M44 - m.M41); // right
            _frustumPlanes[2] = new Vector4(m.M14 + m.M12, m.M24 + m.M22, m.M34 + m.M32, m.M44 + m.M42); // bottom
            _frustumPlanes[3] = new Vector4(m.M14 - m.M12, m.M24 - m.M22, m.M34 - m.M32, m.M44 - m.M42); // top
            _frustumPlanes[4] = new Vector4(m.M13, m.M23, m.M33, m.M43);                                 // near
            _frustumPlanes[5] = new Vector4(m.M14 - m.M13, m.M24 - m.M23, m.M34 - m.M33, m.M44 - m.M43); // far
        }

        // AABB-vs-frustum via the positive-vertex trick. The chunk AABB covers the full world
        // height, so this culls by horizontal footprint only - still rejects everything off-screen.
        private bool ChunkInFrustum(CubeApp.ChunkCoordinates coord)
        {
            float minX = coord.X * ChunkManager.ChunkSize;
            float maxX = minX + ChunkManager.ChunkSize;
            float minZ = coord.Z * ChunkManager.ChunkSize;
            float maxZ = minZ + ChunkManager.ChunkSize;
            // World Y bounds come from the chunk's LAYER (ground -256..383, sky 384..1023), not
            // the ground origin. Using the ground bounds for sky chunks made them vanish when the
            // camera was up in the stratosphere (their frustum box sat below them).
            float minY = ChunkManager.OriginYForLayer(coord.Layer);
            float maxY = minY + ChunkManager.HeightForLayer(coord.Layer);

            for (int i = 0; i < 6; i++)
            {
                var p = _frustumPlanes[i];
                float px = p.X >= 0f ? maxX : minX;
                float py = p.Y >= 0f ? maxY : minY;
                float pz = p.Z >= 0f ? maxZ : minZ;
                if (p.X * px + p.Y * py + p.Z * pz + p.W < 0f)
                {
                    return false;
                }
            }
            return true;
        }

        private void DrawPlayers(CommandList cl)
        {
            var instances = _playerInstances;
            if (instances.Count == 0 || _modelPipeline == null || _playerTextureSet == null
                || _playerBones.Length == 0 || _playerVertsPerInstance == 0)
            {
                return;
            }

            int totalVertexFloats = instances.Count * _playerVertsPerInstance * DuckFloatsPerVertex;
            int totalIndices = instances.Count * _playerIndicesPerInstance;

            if (_playerVertexScratch.Length < totalVertexFloats)
            {
                _playerVertexScratch = new float[totalVertexFloats];
            }
            if (_playerIndexScratch.Length < totalIndices)
            {
                _playerIndexScratch = new ushort[totalIndices];
            }

            int vf = 0;
            int ii = 0;
            ushort baseVertex = 0;
            foreach (var inst in instances)
            {
                WritePlayer(inst, ref vf, ref ii, ref baseVertex);
            }

            EnsurePlayerBuffers((uint)(totalVertexFloats * sizeof(float)), (uint)(totalIndices * sizeof(ushort)));
            _gd.UpdateBuffer(_playerVertexBuffer, 0, ref _playerVertexScratch[0], (uint)(totalVertexFloats * sizeof(float)));
            _gd.UpdateBuffer(_playerIndexBuffer, 0, ref _playerIndexScratch[0], (uint)(totalIndices * sizeof(ushort)));

            cl.SetPipeline(_modelPipeline);
            cl.SetGraphicsResourceSet(0, _projViewSet);
            cl.SetGraphicsResourceSet(1, _playerTextureSet);
            cl.SetVertexBuffer(0, _playerVertexBuffer);
            cl.SetIndexBuffer(_playerIndexBuffer, IndexFormat.UInt16);
            cl.DrawIndexed((uint)totalIndices, 1, 0, 0, 0);
        }

        // Draws GLB-driven mobs (coyote) via the generic MobModel path. All instances are baked into
        // ONE vertex/index buffer and drawn with a single call - the mobs share a model, and a
        // per-mob UpdateBuffer on a shared instance buffer would corrupt earlier draws. A subtle
        // walk-cycle bob + body sway is applied on the CPU around the model origin (feet) to sell
        // the motion (the GLB has no skeletal animation).
        private void DrawCoyotes(CommandList cl)
        {
            var instances = _coyoteInstances;
            if (instances.Count == 0 || _coyoteModel == null || _modelPipeline == null || _coyoteTextureSet == null) return;

            int vertsPer = _coyoteModel.VertexCount;
            int idxPer = _coyoteModel.IndexCount;
            int totalVertexFloats = instances.Count * vertsPer * DuckFloatsPerVertex;
            int totalIndices = instances.Count * idxPer;
            if (totalVertexFloats == 0 || totalIndices == 0) return;

            if (_coyoteVertexScratch.Length < totalVertexFloats) _coyoteVertexScratch = new float[totalVertexFloats];
            if (_coyoteIndexScratch.Length < totalIndices) _coyoteIndexScratch = new ushort[totalIndices];

            int vf = 0, ii = 0;
            ushort baseVertex = 0;
            foreach (var inst in instances)
            {
                // The mob's animation clock advances only while it actually walks, and AnimBlend
                // eases back to 0 when idle - so the GLB trot plays while moving and the coyote
                // returns to its neutral stance when it stops (no frozen mid-stride).
                _coyoteModel.WriteInstance(_coyoteVertexScratch, ref vf, _coyoteIndexScratch, ref ii, ref baseVertex,
                    (float)inst.Position.X, (float)inst.Position.Y, (float)inst.Position.Z, inst.Yaw,
                    inst.AnimTime, inst.AnimBlend);
            }

            EnsureCoyoteBuffers((uint)(totalVertexFloats * sizeof(float)), (uint)(totalIndices * sizeof(ushort)));
            _gd.UpdateBuffer(_coyoteVertexBuffer, 0, ref _coyoteVertexScratch[0], (uint)(totalVertexFloats * sizeof(float)));
            _gd.UpdateBuffer(_coyoteIndexBuffer, 0, ref _coyoteIndexScratch[0], (uint)(totalIndices * sizeof(ushort)));

            cl.SetPipeline(_modelPipeline);
            cl.SetGraphicsResourceSet(0, _projViewSet);
            cl.SetGraphicsResourceSet(1, _coyoteTextureSet);
            cl.SetVertexBuffer(0, _coyoteVertexBuffer);
            cl.SetIndexBuffer(_coyoteIndexBuffer, IndexFormat.UInt16);
            cl.DrawIndexed((uint)totalIndices, 1, 0, 0, 0);
        }

        private void EnsureCoyoteBuffers(uint vertexBytes, uint indexBytes)
        {
            if (_coyoteVertexBuffer == null || _coyoteVertexCapacity < vertexBytes)
            {
                _coyoteVertexBuffer?.Dispose();
                _coyoteVertexBuffer = _gd.ResourceFactory.CreateBuffer(new BufferDescription(
                    Math.Max(vertexBytes, 512), BufferUsage.VertexBuffer | BufferUsage.Dynamic));
                _coyoteVertexCapacity = Math.Max(vertexBytes, 512);
            }
            if (_coyoteIndexBuffer == null || _coyoteIndexCapacity < indexBytes)
            {
                _coyoteIndexBuffer?.Dispose();
                _coyoteIndexBuffer = _gd.ResourceFactory.CreateBuffer(new BufferDescription(
                    Math.Max(indexBytes, 512), BufferUsage.IndexBuffer | BufferUsage.Dynamic));
                _coyoteIndexCapacity = Math.Max(indexBytes, 512);
            }
        }

        // Poses one player's bones (limb swing / head turn) and bakes them, with the body yaw,
        // hurt-flash tint and death roll, into the shared scratch buffers. Same scheme as WriteDuck
        // but with Minecraft-style limb animation and no in-air body tilt.
        private void WritePlayer(in CubeApp.DuckInstance inst, ref int vf, ref int ii, ref ushort baseVertex)
        {
            bool isDead = inst.IsDead;
            float swing = isDead ? 0f : (float)Math.Sin(inst.WalkPhase) * inst.WalkAmount;
            float bob = isDead ? 0f : Math.Abs((float)Math.Sin(inst.WalkPhase * 2.0f)) * 0.03f * inst.WalkAmount;
            float hurtTilt = isDead ? 0f : (inst.HurtTimer > 0f ? (float)Math.Sin(inst.HurtTimer * 60.0f) * 0.06f : 0f);
            float deathRoll = isDead ? inst.DeathRollDir * (float)(Math.PI * 0.5) * (float)Math.Pow(inst.DeathT, 0.9) : 0f;

            float tiltZ = isDead ? deathRoll : hurtTilt;
            float cosR = (float)Math.Cos(tiltZ), sinR = (float)Math.Sin(tiltZ);

            float renderYaw = inst.Yaw + (float)Math.PI;
            float cosY = (float)Math.Cos(renderYaw), sinY = (float)Math.Sin(renderYaw);

            float px = (float)inst.Position.X;
            float py = (float)inst.Position.Y;
            float pz = (float)inst.Position.Z;

            float blink = isDead ? 1f : (inst.HurtTimer > 0f ? ((float)Math.Sin(inst.HurtTimer * 95.0f) > 0f ? 1f : 0.72f) : 0f);
            float flashBlend = isDead ? 1f : (inst.HurtTimer > 0f ? Math.Clamp((inst.HurtTimer / 0.20f) * blink, 0f, 1f) : 0f);
            float gbMul = 1f - 0.82f * flashBlend;

            foreach (var bone in _playerBones)
            {
                float angle = PlayerBoneAnimDelta(bone.Id, swing, inst.HeadYawLocal);
                float ca = (float)Math.Cos(angle), sa = (float)Math.Sin(angle);

                foreach (var v in bone.Vertices)
                {
                    float lx = v.X - bone.PivotX;
                    float ly = v.Y - bone.PivotY;
                    float lz = v.Z - bone.PivotZ;
                    float rx = lx, ry = ly, rz = lz;
                    switch (bone.Axis)
                    {
                        case DuckBoneAxis.X: ry = ly * ca - lz * sa; rz = ly * sa + lz * ca; break;
                        case DuckBoneAxis.Y: rx = lx * ca + lz * sa; rz = -lx * sa + lz * ca; break;
                        case DuckBoneAxis.Z: rx = lx * ca - ly * sa; ry = lx * sa + ly * ca; break;
                    }
                    float mx = bone.PivotX + rx;
                    float my = bone.PivotY + ry + bob;
                    float mz = bone.PivotZ + rz;

                    float ax = mx * cosR - my * sinR;
                    float ay = mx * sinR + my * cosR;
                    float az = mz;
                    float fx = ax * cosY + az * sinY;
                    float fz = -ax * sinY + az * cosY;

                    // Mobs are bigger now: scale the whole model about its feet origin.
                    fx *= PlayerModelScale;
                    ay *= PlayerModelScale;
                    fz *= PlayerModelScale;

                    _playerVertexScratch[vf++] = px + fx;
                    _playerVertexScratch[vf++] = py + ay;
                    _playerVertexScratch[vf++] = pz + fz;
                    _playerVertexScratch[vf++] = v.U;
                    _playerVertexScratch[vf++] = v.V;
                    _playerVertexScratch[vf++] = v.Shade;
                    _playerVertexScratch[vf++] = v.Shade * gbMul;
                    _playerVertexScratch[vf++] = v.Shade * gbMul;
                    _playerVertexScratch[vf++] = 1f;
                }

                for (int k = 0; k < bone.Indices.Length; k++)
                {
                    _playerIndexScratch[ii++] = (ushort)(bone.Indices[k] + baseVertex);
                }
                baseVertex += (ushort)bone.Vertices.Length;
            }
        }

        // Minecraft-style limb swing: opposite arm/leg pairs, head follows the local head yaw.
        private static float PlayerBoneAnimDelta(PlayerBoneId id, float swing, float headYawLocal)
        {
            switch (id)
            {
                case PlayerBoneId.Head: return headYawLocal;
                case PlayerBoneId.RightArm: return swing * 0.9f;
                case PlayerBoneId.LeftArm: return -swing * 0.9f;
                case PlayerBoneId.RightLeg: return -swing * 1.2f;
                case PlayerBoneId.LeftLeg: return swing * 1.2f;
                default: return 0f;
            }
        }

        private void EnsurePlayerBuffers(uint vbSize, uint ibSize)
        {
            if (_playerVertexBuffer == null || _playerVertexCapacity < vbSize)
            {
                _playerVertexBuffer?.Dispose();
                _playerVertexBuffer = _gd.ResourceFactory.CreateBuffer(new BufferDescription(vbSize, BufferUsage.VertexBuffer | BufferUsage.Dynamic));
                _playerVertexCapacity = vbSize;
            }
            if (_playerIndexBuffer == null || _playerIndexCapacity < ibSize)
            {
                _playerIndexBuffer?.Dispose();
                _playerIndexBuffer = _gd.ResourceFactory.CreateBuffer(new BufferDescription(ibSize, BufferUsage.IndexBuffer | BufferUsage.Dynamic));
                _playerIndexCapacity = ibSize;
            }
        }

        // The E-menu inventory: a scrollable grid of every registered block rendered with its
        // isometric cube icon. Clicks are queued and consumed by Program on the next frame.
        private void DrawInventoryWindow(Vector2 displaySize)
        {
            if (_iconImGuiId == IntPtr.Zero || _blockIconUv == null) return;

            float winW = Math.Min(680, displaySize.X - 32);
            float winH = Math.Min(480, displaySize.Y - 64);
            ImGui.SetNextWindowPos(new Vector2((displaySize.X - winW) / 2f, 24), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new Vector2(winW, winH), ImGuiCond.Always);
            ImGui.Begin("##inventory", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse);
            ImGui.Text("Inventory - click a block to put it in the selected hotbar slot");
            ImGui.Separator();
            ImGui.BeginChild("##invgrid", new Vector2(0, -4));

            float avail = ImGui.GetContentRegionAvail().X;
            const float cellW = 64f;
            int perRow = Math.Max(1, (int)(avail / cellW));
            for (int id = 1; id < BlockRegistry.Count; id++)
            {
                if (!BlockRegistry.IsInInventory(id)) continue;
                var uv = _blockIconUv[id];
                string name = BlockRegistry.GetById(id).DisplayName;
                ImGui.PushID(id);
                if (ImGui.ImageButton($"##icon{id}", _iconImGuiId, new Vector2(48, 48),
                        new Vector2(uv.X, uv.Y), new Vector2(uv.X + uv.Z, uv.Y + uv.W),
                        Vector4.Zero, Vector4.One))
                {
                    _inventorySelections.Enqueue(id);
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(name);
                ImGui.PopID();
                if (id % perRow != 0) ImGui.SameLine();
            }

            ImGui.EndChild();
            ImGui.End();
        }

        // Tiles the dirt block texture across the screen - the classic Infdev menu background.
        // Uses the BACKGROUND draw list so the ImGui menu windows render on top of it.
        private void DrawDirtBackground(Vector2 screenSize)
        {
            if (_terrainImGuiId == IntPtr.Zero) return;
            var dirt = BlockRegistry.Get("dirt").AllTexture;
            if (!dirt.HasValue) return;
            var tr = dirt.Value;
            float u0 = tr.X / _atlasWidth;
            float v0 = tr.Y / _atlasHeight;
            float uw = tr.Width / _atlasWidth;
            float vh = tr.Height / _atlasHeight;
            var drawList = ImGui.GetBackgroundDrawList();
            const float tile = 48f;
            uint tint = ImGui.ColorConvertFloat4ToU32(new Vector4(0.72f, 0.72f, 0.72f, 1f));
            for (float y = 0; y < screenSize.Y; y += tile)
            {
                for (float x = 0; x < screenSize.X; x += tile)
                {
                    drawList.AddImage(_terrainImGuiId,
                        new Vector2(x, y),
                        new Vector2(Math.Min(x + tile, screenSize.X), Math.Min(y + tile, screenSize.Y)),
                        new Vector2(u0, v0), new Vector2(u0 + uw, v0 + vh), tint);
                }
            }
        }

        // The title / create-world / pause menus. Driven by MenuState (shared with Program).
        private void DrawMenu()
        {
            var m = _hud.Menu;
            if (m == null) return;
            var io = ImGui.GetIO();
            var size = io.DisplaySize;

            // The title/create screens get the dirt background; the pause menu just dims the
            // frozen world behind it with a translucent gray wash.
            if (m.Screen == GameScreen.Paused)
            {
                uint tint = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.45f));
                ImGui.GetBackgroundDrawList().AddRectFilled(Vector2.Zero, size, tint);
            }
            else
            {
                DrawDirtBackground(size);
            }

            ImGuiWindowFlags windowFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar
                | ImGuiWindowFlags.NoScrollWithMouse;

            if (m.Screen == GameScreen.Title)
            {
                // Logo sits at the top-center of the screen, like Infdev's main menu.
                const float logoW = 224f;
                const float logoH = 224f;
                if (_logoImGuiId != IntPtr.Zero)
                {
                    ImGui.SetNextWindowPos(new Vector2((size.X - logoW) / 2f, 30f), ImGuiCond.Always);
                    ImGui.SetNextWindowSize(new Vector2(logoW, logoH), ImGuiCond.Always);
                    ImGui.Begin("##logo", windowFlags | ImGuiWindowFlags.NoBackground);
                    ImGui.Image(_logoImGuiId, new Vector2(logoW, logoH));
                    ImGui.End();
                }
                else
                {
                    // Fallback text logo at the same spot.
                    ImGui.SetNextWindowPos(new Vector2((size.X - 200f) / 2f, 40f), ImGuiCond.Always);
                    ImGui.SetNextWindowSize(new Vector2(200, 60), ImGuiCond.Always);
                    ImGui.Begin("##logo", windowFlags | ImGuiWindowFlags.NoBackground);
                    ImGui.SetWindowFontScale(2.4f);
                    var titlePos = ImGui.GetCursorScreenPos();
                    var titleFont = ImGui.GetFont();
                    float titleSize = ImGui.GetFontSize();
                    uint shadowCol = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.55f));
                    uint whiteCol = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f));
                    var titleDraw = ImGui.GetWindowDrawList();
                    titleDraw.AddText(titleFont, titleSize, titlePos + new Vector2(3, 3), shadowCol, "Cubuild");
                    titleDraw.AddText(titleFont, titleSize, titlePos, whiteCol, "Cubuild");
                    ImGui.SetWindowFontScale(1f);
                    ImGui.End();
                }

                // Buttons hang lower, in the classic Minecraft vertical column, with any saved
                // worlds listed beneath so one click loads a world.
                int shownWorlds = Math.Min(m.SavedWorlds.Count, 6);
                float winH = 130f + 60f + (shownWorlds > 0 ? 42f + shownWorlds * 30f : 0f);
                ImGui.SetNextWindowPos(new Vector2((size.X - 220f) / 2f, size.Y / 4f + 72f), ImGuiCond.Always);
                ImGui.SetNextWindowSize(new Vector2(220, winH), ImGuiCond.Always);
                ImGui.Begin("##title", windowFlags);
                if (ImGui.Button("Singleplayer", new Vector2(200, 34)))
                {
                    m.Screen = GameScreen.CreateWorld;
                    _menuBuffersInitialized = false;
                }
                ImGui.Dummy(new Vector2(0, 18));
                if (ImGui.Button("Multiplayer", new Vector2(200, 34)))
                {
                    m.Screen = GameScreen.Multiplayer;
                    _menuBuffersInitialized = false;
                }
                ImGui.Dummy(new Vector2(0, 18));
                if (ImGui.Button("Quit", new Vector2(200, 34))) m.QuitClicked = true;
                if (shownWorlds > 0)
                {
                    ImGui.Dummy(new Vector2(0, 10));
                    uint dimCol = ImGui.ColorConvertFloat4ToU32(new Vector4(0.8f, 0.8f, 0.8f, 1f));
                    ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1f), "Saved Worlds");
                    ImGui.Dummy(new Vector2(0, 4));
                    for (int i = 0; i < shownWorlds; i++)
                    {
                        if (ImGui.Button(m.SavedWorlds[i], new Vector2(200, 26)))
                        {
                            m.SelectedWorldIndex = i;
                            m.LoadWorldClicked = true;
                        }
                    }
                }
                ImGui.End();
            }
            else if (m.Screen == GameScreen.CreateWorld)
            {
                ImGui.SetNextWindowPos(new Vector2(size.X / 2f - 150f, size.Y / 2f - 120f), ImGuiCond.Always);
                ImGui.SetNextWindowSize(new Vector2(300, 240), ImGuiCond.Always);
                ImGui.Begin("##createworld", windowFlags);
                ImGui.Text("Create World");
                ImGui.Spacing();
                if (!_menuBuffersInitialized)
                {
                    WriteBuffer(_worldNameBuffer, m.WorldName);
                    WriteBuffer(_seedBuffer, m.SeedInput);
                    _menuBuffersInitialized = true;
                }
                ImGui.InputText("World name", _worldNameBuffer, (uint)_worldNameBuffer.Length);
                m.WorldName = ReadBuffer(_worldNameBuffer);
                ImGui.InputText("Seed (optional)", _seedBuffer, (uint)_seedBuffer.Length);
                m.SeedInput = ReadBuffer(_seedBuffer);
                ImGui.Spacing();
                if (ImGui.Button("Create World", new Vector2(220, 34)))
                {
                    m.CreateWorldClicked = true;
                }
                ImGui.Spacing();
                if (ImGui.Button("Back", new Vector2(220, 28))) m.Screen = GameScreen.Title;
                ImGui.End();
            }
            else if (m.Screen == GameScreen.Multiplayer)
            {
                ImGui.SetNextWindowPos(new Vector2(size.X / 2f - 170f, size.Y / 2f - 150f), ImGuiCond.Always);
                ImGui.SetNextWindowSize(new Vector2(340, 300), ImGuiCond.Always);
                ImGui.Begin("##multiplayer", windowFlags);
                ImGui.Text("Multiplayer");
                ImGui.Spacing();
                ImGui.TextWrapped("Host a game for friends to join, or connect to a host's IP.");
                ImGui.Spacing();
                if (!_menuBuffersInitialized)
                {
                    WriteBuffer(_hostPortBuffer, m.HostPort);
                    WriteBuffer(_joinAddressBuffer, m.JoinAddress);
                    _menuBuffersInitialized = true;
                }
                ImGui.InputText("Host port", _hostPortBuffer, (uint)_hostPortBuffer.Length);
                m.HostPort = ReadBuffer(_hostPortBuffer);
                if (ImGui.Button("Host Game", new Vector2(300, 34)))
                {
                    m.HostGameClicked = true;
                }
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
                ImGui.InputText("Server address", _joinAddressBuffer, (uint)_joinAddressBuffer.Length);
                m.JoinAddress = ReadBuffer(_joinAddressBuffer);
                if (ImGui.Button("Join Game", new Vector2(300, 34)))
                {
                    m.JoinGameClicked = true;
                }
                if (!string.IsNullOrEmpty(_hud.MultiplayerError))
                {
                    ImGui.Spacing();
                    ImGui.TextColored(new Vector4(1f, 0.45f, 0.45f, 1f), _hud.MultiplayerError);
                    ImGui.Spacing();
                }
                ImGui.Spacing();
                if (ImGui.Button("Back", new Vector2(300, 28))) m.MultiplayerBackClicked = true;
                ImGui.End();
            }
            else if (m.Screen == GameScreen.Paused)
            {
                ImGui.SetNextWindowPos(new Vector2(size.X / 2f - 120f, size.Y / 2f - 110f), ImGuiCond.Always);
                ImGui.SetNextWindowSize(new Vector2(240, 220), ImGuiCond.Always);
                ImGui.Begin("##paused", windowFlags);
                ImGui.SetWindowFontScale(1.6f);
                ImGui.TextColored(new Vector4(1f, 1f, 1f, 1f), "Paused");
                ImGui.SetWindowFontScale(1f);
                ImGui.Spacing();
                ImGui.Spacing();
                if (ImGui.Button("Resume", new Vector2(200, 32))) m.ResumeClicked = true;
                ImGui.Spacing();
                if (ImGui.Button("Open to LAN", new Vector2(200, 32))) m.OpenToLanClicked = true;
                ImGui.Spacing();
                if (ImGui.Button("Quit to Title", new Vector2(200, 32))) m.QuitToTitleClicked = true;
                if (!string.IsNullOrEmpty(_hud.NetStatus))
                {
                    ImGui.Spacing();
                    ImGui.TextColored(new Vector4(0.6f, 1f, 0.6f, 1f), _hud.NetStatus);
                }
                ImGui.End();
            }
        }

        // Copies a string into a null-terminated byte buffer for ImGui.InputText.
        private static void WriteBuffer(byte[] buffer, string value)
        {
            Array.Clear(buffer, 0, buffer.Length);
            if (string.IsNullOrEmpty(value)) return;
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            int n = Math.Min(bytes.Length, buffer.Length - 1);
            Array.Copy(bytes, buffer, n);
        }

        // Reads a null-terminated byte buffer back into a string.
        private static string ReadBuffer(byte[] buffer)
        {
            int end = Array.IndexOf(buffer, (byte)0);
            if (end < 0) end = buffer.Length;
            return System.Text.Encoding.UTF8.GetString(buffer, 0, end);
        }

        private void BuildHudUi()
        {
            var io = ImGui.GetIO();
            var displaySize = io.DisplaySize;
            var drawList = ImGui.GetForegroundDrawList();

            // Menus (title / create world / paused) take over the whole screen; the gameplay HUD
            // below only draws while actually playing.
            var menu = _hud.Menu;
            bool playing = menu == null || menu.Screen == GameScreen.Playing;
            if (!playing)
            {
                DrawMenu();
                return;
            }

            // Crosshair: classic four-arm + (like Minecraft) - a clean gap in the center, no dot.
            var center = new Vector2(displaySize.X / 2f, displaySize.Y / 2f);
            uint crosshairColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f));
            float arm = 6f;   // arm length from the center gap
            float gap = 3f;   // empty space around the exact center
            const float thickness = 1.5f;
            // Subtle dark outline so the white shows against bright sky/water.
            uint outlineColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.65f));
            for (int pass = 0; pass < 2; pass++)
            {
                uint c = pass == 0 ? outlineColor : crosshairColor;
                float w = pass == 0 ? thickness + 1.5f : thickness;
                drawList.AddLine(new Vector2(center.X - arm - gap - 0.75f, center.Y), new Vector2(center.X - gap + 0.75f, center.Y), c, w);
                drawList.AddLine(new Vector2(center.X + gap - 0.75f, center.Y), new Vector2(center.X + arm + gap + 0.75f, center.Y), c, w);
                drawList.AddLine(new Vector2(center.X, center.Y - arm - gap - 0.75f), new Vector2(center.X, center.Y - gap + 0.75f), c, w);
                drawList.AddLine(new Vector2(center.X, center.Y + gap - 0.75f), new Vector2(center.X, center.Y + arm + gap + 0.75f), c, w);
            }

            // The targeted block face highlight is drawn as a depth-tested 3D quad in Render(),
            // not here, so that blocks in front of it occlude it correctly.

            // Hotbar - uses the Cubuild.html GUI frame texture (169x16, 10 slots of 16px + 1px
            // gap) at 3x scale (507x48 on screen, 48px slots / 3px gaps). The selected slot gets
            // the 18x18 yellow highlight texture stretched over it, and each slot draws its
            // isometric block icon + number on top - same functionality as before, just with the
            // real frame art.
            const int hotbarSlots = 10;
            const int hotbarScale = 3;
            const int slotSize = 16 * hotbarScale;     // 48
            const int slotGap = 1 * hotbarScale;        // 3
            int totalWidth = hotbarSlots * slotSize + (hotbarSlots - 1) * slotGap; // 507
            const int hotbarHeight = 16 * hotbarScale; // 48
            float startX = (displaySize.X - totalWidth) / 2f;
            float hotbarY = displaySize.Y - hotbarHeight - 16f;

            uint textColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f));

            if (_hotbarImGuiId != IntPtr.Zero)
            {
                // Draw the whole slot frame as one stretched image.
                drawList.AddImage(
                    _hotbarImGuiId,
                    new Vector2(startX, hotbarY),
                    new Vector2(startX + totalWidth, hotbarY + hotbarHeight),
                    Vector2.Zero,
                    Vector2.One);
            }
            else
            {
                // Fallback if the embedded texture is missing: draw plain slot rects.
                uint slotBg = ImGui.ColorConvertFloat4ToU32(new Vector4(36 / 255f, 45 / 255f, 52 / 255f, 1f));
                uint slotBorder = ImGui.ColorConvertFloat4ToU32(new Vector4(100 / 255f, 150 / 255f, 200 / 255f, 1f));
                for (int i = 0; i < hotbarSlots; i++)
                {
                    float x = startX + i * (slotSize + slotGap);
                    drawList.AddRectFilled(new Vector2(x, hotbarY), new Vector2(x + slotSize, hotbarY + slotSize), slotBg);
                    drawList.AddRect(new Vector2(x, hotbarY), new Vector2(x + slotSize, hotbarY + slotSize), slotBorder);
                }
            }

            for (int i = 0; i < hotbarSlots; i++)
            {
                float x = startX + i * (slotSize + slotGap);
                var slotTopLeft = new Vector2(x, hotbarY);

                // Dim the transparent slot interior so block icons read clearly against the world
                // behind the hotbar (drawn first, under the icon and selection ring).
                uint slotDim = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.35f));
                drawList.AddRectFilled(slotTopLeft + new Vector2(3, 3), slotTopLeft + new Vector2(slotSize - 3, slotSize - 3), slotDim);

                if (i == _hud.SelectedSlot && _hotbarSelectImGuiId != IntPtr.Zero)
                {
                    // The 18x18 highlight box stretches over the whole slot (48px here). The selected
                    // slot gets a slightly larger box so the active choice pops out from the others.
                    const float selScale = 1.25f;
                    float selSize = slotSize * selScale;
                    var selCenter = slotTopLeft + new Vector2(slotSize * 0.5f, slotSize * 0.5f);
                    drawList.AddImage(
                        _hotbarSelectImGuiId,
                        selCenter - new Vector2(selSize * 0.5f, selSize * 0.5f),
                        selCenter + new Vector2(selSize * 0.5f, selSize * 0.5f),
                        Vector2.Zero,
                        Vector2.One);
                }

                // The frame texture's visible opening per slot is 12x12 at 1x (36x36 at 3x), so the
                // block icon is centered inside that opening. A 1px inset lets the block nearly fill
                // the slot while staying centered; the extra +1 on Y nudges it down a touch so the
                // cube sits optically centered in the frame.
                const int iconInset = 1;
                const int iconDrop = 2;
                bool isSelected = i == _hud.SelectedSlot;
                if (_hud.Hotbar != null && i < _hud.Hotbar.Count)
                {
                    int bid = _hud.Hotbar[i];
                    if (bid > 0 && _iconImGuiId != IntPtr.Zero && _blockIconUv != null && bid < _blockIconUv.Length)
                    {
                        var uv = _blockIconUv[bid];
                        // The selected block grows along with its highlight ring so the active slot
                        // reads as one bigger, emphasized cube.
                        float iconSize2 = isSelected ? slotSize * 1.16f : slotSize - iconInset * 2f;
                        float iconX = isSelected ? slotTopLeft.X + (slotSize - iconSize2) * 0.5f : slotTopLeft.X + iconInset;
                        float iconY = isSelected ? slotTopLeft.Y + (slotSize - iconSize2) * 0.5f + iconDrop : slotTopLeft.Y + iconInset + iconDrop;
                        drawList.AddImage(
                            _iconImGuiId,
                            new Vector2(iconX, iconY),
                            new Vector2(iconX + iconSize2, iconY + iconSize2),
                            new Vector2(uv.X, uv.Y),
                            new Vector2(uv.X + uv.Z, uv.Y + uv.W));
                    }
                    else
                    {
                        uint iconColor = bid > 0 ? BlockRegistry.MapColorOf(bid) : 0;
                        float iconSize2 = isSelected ? slotSize * 1.16f : slotSize - iconInset * 2f;
                        float iconX = isSelected ? slotTopLeft.X + (slotSize - iconSize2) * 0.5f : slotTopLeft.X + iconInset;
                        float iconY = isSelected ? slotTopLeft.Y + (slotSize - iconSize2) * 0.5f + iconDrop : slotTopLeft.Y + iconInset + iconDrop;
                        drawList.AddRectFilled(new Vector2(iconX, iconY), new Vector2(iconX + iconSize2, iconY + iconSize2), iconColor);
                    }
                }
            }

            // E-menu inventory: a grid of every block. Clicking one queues it to Program, which
            // drops it into the selected hotbar slot and closes the menu.
            if (_hud.InventoryOpen)
            {
                DrawInventoryWindow(displaySize);
            }

            // Selected block label
            string label = string.IsNullOrEmpty(_hud.SelectedBlockText) ? string.Empty : _hud.SelectedBlockText;
            if (label.Length > 0)
            {
                var labelPos = new Vector2(12, 12);
                var textSize = ImGui.CalcTextSize(label);
                uint bg = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.47f));
                drawList.AddRectFilled(labelPos - new Vector2(6, 3), labelPos + textSize + new Vector2(6, 3), bg);
                drawList.AddText(labelPos, textColor, label);
            }

            // Debug overlay (F3)
            if (_hud.ShowDebug)
            {
                uint debugColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 0f, 1f));
                float dy = 8f;
                void Line(string text)
                {
                    drawList.AddText(new Vector2(8, dy), debugColor, text);
                    dy += 16f;
                }

                Line($"FPS: {_hud.Fps:0.0}");
                if (_cameraPosition.HasValue)
                    Line($"FogCam: {_cameraPosition.Value.X:0.0}, {_cameraPosition.Value.Y:0.0}, {_cameraPosition.Value.Z:0.0}  range: {_fogParams[4]:0.0}-{_fogParams[5]:0.0}");
                Line($"Particles: {_particleCount}");
                Line($"Seed: {_hud.WorldSeed}");
                Line($"Fly: {(_hud.FlyMode ? "ON" : "OFF")}");
                Line($"Fullbright: {(_hud.Fullbright ? "ON" : "OFF")}  [F6]");
                if (!string.IsNullOrEmpty(_hud.NetStatus)) Line($"Net: {_hud.NetStatus}");
                if (!string.IsNullOrEmpty(_hud.BiomeText)) Line($"Biome: {_hud.BiomeText}");
                Line($"XYZ: {_hud.PlayerX:0.000} / {_hud.PlayerY:0.000} / {_hud.PlayerZ:0.000}");
                Line($"Block: {(int)Math.Floor(_hud.PlayerX)} / {(int)Math.Floor(_hud.PlayerY)} / {(int)Math.Floor(_hud.PlayerZ)}");
                Line($"Chunk: {_hud.PlayerChunkX} / {_hud.PlayerChunkZ}");
                Line($"Upd: {_hud.UpdateMs:0.0} ms");
                Line($"Mesh: {_hud.MeshMs:0.0} ms");
                Line($"Upload: {_hud.UploadMs:0.0} ms");
                Line($"Render: {_hud.RenderMs:0.0} ms");
                Line($"Facing: {_hud.FacingText}");
                if (!string.IsNullOrEmpty(_hud.RenderDistanceText))
                {
                    Line(_hud.RenderDistanceText);
                }

                // Nametags: project each mob's position above its head into screen space and draw
                // its type label, so invisible/broken mobs are still verifiable in the F3 overlay.
                if (_viewProjection.HasValue && _cameraPosition.HasValue)
                {
                    uint tagColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f));
                    for (int i = 0; i < _allMobRenderData.Count; i++)
                    {
                        var md = _allMobRenderData[i];
                        var screen = WorldToScreen(new System.Numerics.Vector3((float)md.Position.X, (float)md.Position.Y + 1.8f, (float)md.Position.Z));
                        if (screen.HasValue)
                        {
                            drawList.AddText(screen.Value - new Vector2(0, 14), tagColor, md.MobType);
                        }
                    }
                }
            }
        }

        // Projects a world-space point to screen pixel coordinates using the current
        // view-projection matrix (the renderer owns both the camera and the HUD pass). Returns
        // null when the point is behind the camera.
        private System.Numerics.Vector2? WorldToScreen(System.Numerics.Vector3 world)
        {
            if (!_viewProjection.HasValue) return null;
            var vp = _viewProjection.Value;
            var clip = System.Numerics.Vector4.Transform(new System.Numerics.Vector4(world, 1f), vp);
            if (clip.W <= 0f) return null;
            var ndc = new System.Numerics.Vector2(clip.X / clip.W, clip.Y / clip.W);
            var io = ImGui.GetIO();
            float x = (ndc.X * 0.5f + 0.5f) * io.DisplaySize.X;
            float y = (1f - ndc.Y * 0.5f - 0.5f) * io.DisplaySize.Y;
            return new System.Numerics.Vector2(x, y);
        }

        public void Dispose()
        {
            _chunkRanges.Clear();
            _freeBlocks.Clear();
            _drawCommands.Clear();
            _megaVertexBuffer?.Dispose();
            _megaIndexBuffer?.Dispose();
            _indirectBuffer?.Dispose();
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
            _highlightVertexBuffer?.Dispose();
            _highlightIndexBuffer?.Dispose();
            _highlightPipeline?.Dispose();
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
            _coyoteModel?.Dispose();
            _coyoteVertexBuffer?.Dispose();
            _coyoteIndexBuffer?.Dispose();
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
            _iconAtlasView?.Dispose();
            _iconAtlasTexture?.Dispose();
            _sc?.Dispose();
            _gd?.Dispose();
        }

        public void UploadChunk(CubeApp.ChunkCoordinates coords, System.Collections.Generic.IReadOnlyList<CubeApp.MeshFace> faces)
        {
            BuildMesh(faces, out var vArr, out var iArr, out var cvArr, out var ciArr, out var gvArr, out var giArr, out var tvArr, out var tiArr);
            _pendingUploads.Enqueue(new PendingUpload(coords, vArr, iArr, cvArr, ciArr, gvArr, giArr, tvArr, tiArr));
        }

        // Player edits jump the line: same vertex data, but enqueued on the priority queue that
        // ProcessPendingPriorityMeshes drains every frame for instant feedback.
        public void UploadChunkPriority(CubeApp.ChunkCoordinates coords, System.Collections.Generic.IReadOnlyList<CubeApp.MeshFace> faces)
        {
            BuildMesh(faces, out var vArr, out var iArr, out var cvArr, out var ciArr, out var gvArr, out var giArr, out var tvArr, out var tiArr);
            _pendingPriorityUploads.Enqueue(new PendingUpload(coords, vArr, iArr, cvArr, ciArr, gvArr, giArr, tvArr, tiArr));
        }

        // Builds the 13-float-per-vertex chunk mesh (pos + localUV + tileRect + color) from greedy
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
            System.Collections.Generic.IReadOnlyList<CubeApp.MeshFace> faces,
            out float[] vertsArr, out ushort[] indicesArr,
            out float[] cutoutVertsArr, out ushort[] cutoutIndicesArr,
            out float[] glassVertsArr, out ushort[] glassIndicesArr,
            out float[] transVertsArr, out ushort[] transIndicesArr)
        {
            // vertex layout: position(3) + localUV(2) + tileRect(4) + color(4) = 13 floats per vertex
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

            var verts = new float[opaqueFaces * 4 * 13];
            var indices = new ushort[opaqueFaces * 6];
            var cutoutVerts = new float[cutoutFaces * 4 * 13];
            var cutoutIndices = new ushort[cutoutFaces * 6];
            var glassVerts = new float[glassFaces * 4 * 13];
            var glassIndices = new ushort[glassFaces * 6];
            var transVerts = new float[transFaces * 4 * 13];
            var transIndices = new ushort[transFaces * 6];
            float atlasW = Math.Max(1f, _atlasWidth);
            float atlasH = Math.Max(1f, _atlasHeight);
            // Hoisted out of the face loop: stackalloc reserves stack for the method, so a
            // per-face stackalloc would grow the frame with face count (CA2014).
            Span<CubeApp.Point3D> vertsSpan = stackalloc CubeApp.Point3D[4];
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
                // from the TOP, so we shift it by (1 - height) to match Infdev's
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

                float shade = f.Shade;
                float rf = shade;
                float gf = shade;
                float bf = shade;
                float alpha = f.Alpha;

                float tileOriginX = f.SrcRect.X / atlasW;
                float tileOriginY = f.SrcRect.Y / atlasH;
                float tileSzX = tileW / atlasW;
                float tileSzY = tileH / atlasH;

                var v0p = vertsSpan[0];
                var edgeU = vertsSpan[1] - v0p;
                var edgeV = vertsSpan[3] - v0p;
                double denomU = edgeU.X * edgeU.X + edgeU.Y * edgeU.Y + edgeU.Z * edgeU.Z;
                double denomV = edgeV.X * edgeV.X + edgeV.Y * edgeV.Y + edgeV.Z * edgeV.Z;

                int vertWrite = vertexStart * 13;
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

                    dstVerts[vertWrite] = (float)vv.X;
                    dstVerts[vertWrite + 1] = (float)vv.Y;
                    dstVerts[vertWrite + 2] = (float)vv.Z;
                    dstVerts[vertWrite + 3] = (float)du;
                    dstVerts[vertWrite + 4] = (float)dv;
                    dstVerts[vertWrite + 5] = tileOriginX;
                    dstVerts[vertWrite + 6] = tileOriginY;
                    dstVerts[vertWrite + 7] = tileSzX;
                    dstVerts[vertWrite + 8] = tileSzY;
                    dstVerts[vertWrite + 9] = rf;
                    dstVerts[vertWrite + 10] = gf;
                    dstVerts[vertWrite + 11] = bf;
                    dstVerts[vertWrite + 12] = alpha;
                    vertWrite += 13;
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

        public void RemoveChunk(CubeApp.ChunkCoordinates coords)
        {
            // Enqueue removal to be processed on render thread
            _pendingRemovals.Enqueue(coords);
        }

        private static bool TryGetCubuildFaceAxes(CubeApp.Point3D normal, out CubeApp.Point3D uAxis, out CubeApp.Point3D vAxis)
        {
            if (normal.X > 0.5)
            {
                uAxis = new CubeApp.Point3D(0, 0, -1);
                vAxis = new CubeApp.Point3D(0, -1, 0);
                return true;
            }

            if (normal.X < -0.5)
            {
                uAxis = new CubeApp.Point3D(0, 0, 1);
                vAxis = new CubeApp.Point3D(0, -1, 0);
                return true;
            }

            if (normal.Z > 0.5)
            {
                uAxis = new CubeApp.Point3D(1, 0, 0);
                vAxis = new CubeApp.Point3D(0, -1, 0);
                return true;
            }

            if (normal.Z < -0.5)
            {
                uAxis = new CubeApp.Point3D(-1, 0, 0);
                vAxis = new CubeApp.Point3D(0, -1, 0);
                return true;
            }

            if (normal.Y > 0.5)
            {
                uAxis = new CubeApp.Point3D(1, 0, 0);
                vAxis = new CubeApp.Point3D(0, 0, -1);
                return true;
            }

            if (normal.Y < -0.5)
            {
                uAxis = new CubeApp.Point3D(1, 0, 0);
                vAxis = new CubeApp.Point3D(0, 0, 1);
                return true;
            }

            uAxis = new CubeApp.Point3D(0, 0, 0);
            vAxis = new CubeApp.Point3D(0, 0, 0);
            return false;
        }

        private static double Dot(CubeApp.Point3D a, CubeApp.Point3D b)
        {
            return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        }

        public void UpdateCamera(CubeApp.Point3D position, float yaw, float pitch)
        {
            var proj = Matrix4x4.CreatePerspectiveFieldOfView((float)(Math.PI / 2.0), (float)_sc.Framebuffer.Width / _sc.Framebuffer.Height, _nearPlane, _farPlane);
            var yawRad = yaw * (float)Math.PI / 180f;
            var pitchRad = pitch * (float)Math.PI / 180f;
            var forward = new Vector3((float)(Math.Cos(pitchRad) * Math.Sin(yawRad)), (float)Math.Sin(pitchRad), (float)(Math.Cos(pitchRad) * Math.Cos(yawRad)));
            var cameraPos = new Vector3((float)position.X, (float)position.Y, (float)position.Z);
            var target = cameraPos + forward;
            var view = Matrix4x4.CreateLookAt(cameraPos, target, Vector3.UnitY);
            var viewProj = Matrix4x4.Multiply(view, proj);
            // Sky matrix: the view with its TRANSLATION removed (rotation only), so the camera-space
            // sky planes render locked to the eye. This mirrors Infdev, where the sky display lists
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
            _nearPlane = 0.3f;
        }

        public void SetChunkManager(CubeApp.ChunkManager manager)
        {
            _chunkManager = manager;
        }

        /// <summary>Regenerates the cloud texture from the world seed, so every world has its own
        /// cloud pattern.</summary>
        public void SetWorldSeed(int seed)
        {
            if (_cloudTexture == null) return;
            _cloudSeed = seed;
            _gd.UpdateTexture(_cloudTexture, GenerateCloudTexture(seed), 0, 0, 0, 256, 256, 1, 0, 0);
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
                // like Minecraft's break particles - not the whole texture.
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
        public void MeshChunkImmediate(CubeApp.ChunkCoordinates coords)
        {
            if (_chunkManager == null) return;
            if (!_chunkManager.TryGetLoadedChunk(coords, out var chunk)) return;

            var chunksToPass = new System.Collections.Generic.List<CubeApp.Chunk> { chunk };
            int chunkX = chunk.OriginX / ChunkManager.ChunkSize;
            int chunkZ = chunk.OriginZ / ChunkManager.ChunkSize;
            if (_chunkManager.TryGetLoadedChunk(new CubeApp.ChunkCoordinates(chunkX - 1, chunkZ), out var left)) chunksToPass.Add(left);
            if (_chunkManager.TryGetLoadedChunk(new CubeApp.ChunkCoordinates(chunkX + 1, chunkZ), out var right)) chunksToPass.Add(right);
            if (_chunkManager.TryGetLoadedChunk(new CubeApp.ChunkCoordinates(chunkX, chunkZ - 1), out var back)) chunksToPass.Add(back);
            if (_chunkManager.TryGetLoadedChunk(new CubeApp.ChunkCoordinates(chunkX, chunkZ + 1), out var front)) chunksToPass.Add(front);

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
