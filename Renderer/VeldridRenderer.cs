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
        private Texture _atlasTexture;
        private TextureView _atlasView;
        private Sampler _atlasSampler;
        private ResourceSet _textureSet;
        private Pipeline _pipeline;
        // Second pass for transparent geometry (water): same shaders/state as _pipeline but with
        // depth WRITES disabled so blended faces (alpha 0.65) tint whatever opaque geometry was
        // already drawn instead of blocking it from ever drawing (which made border water walls
        // render as ghosty see-through when their chunk happened to draw before the terrain behind).
        private Pipeline _transparentPipeline;
        private Pipeline _highlightPipeline;
        private DeviceBuffer _highlightVertexBuffer;
        private DeviceBuffer _highlightIndexBuffer;
        private readonly float[] _highlightVertexScratch = new float[12];

        // Pipeline for chunk border wireframe rendering (F3 debug)
        private Pipeline _chunkBorderPipeline;
        private DeviceBuffer _chunkBorderVertexBuffer;
        private DeviceBuffer _chunkBorderIndexBuffer;
        private readonly float[] _chunkBorderVertexScratch = new float[768]; // 24 edges * 2 vertices * 3 coords * 4 chunks (max for small radius)

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
        private Vector4[]? _blockIconUv;

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
        // Transparent (water) faces are uploaded into the same mega buffers but tracked in a
        // separate range set and drawn as a second pass (depth-write off, back-to-front blend).
        private readonly Dictionary<CubeApp.ChunkCoordinates, ChunkRange> _transparentRanges = new();
        private readonly List<(uint VbOffset, uint VbBytes, uint IbOffset, uint IbBytes)> _freeBlocks = new();
        private readonly List<(CubeApp.ChunkCoordinates Coord, IndirectDrawIndexedArguments Cmd)> _drawCommands = new();
        private readonly List<(CubeApp.ChunkCoordinates Coord, IndirectDrawIndexedArguments Cmd)> _transparentDrawCommands = new();
        private IndirectDrawIndexedArguments[] _indirectScratch = Array.Empty<IndirectDrawIndexedArguments>();
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
            public float[] TransparentVertices { get; }
            public ushort[] TransparentIndices { get; }

            public PendingUpload(CubeApp.ChunkCoordinates coord, float[] vertices, ushort[] indices, float[] transparentVertices, ushort[] transparentIndices)
            {
                Coord = coord;
                Vertices = vertices;
                Indices = indices;
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
            CreatePipeline();

            _imguiRenderer = new ImGuiRenderer(
                _gd,
                _sc.Framebuffer.OutputDescription,
                Math.Max(1, (int)_sc.Framebuffer.Width),
                Math.Max(1, (int)_sc.Framebuffer.Height));

            // Build the isometric block-icon atlas (needs the ImGui renderer for its texture binding).
            BuildIconAtlas();
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

            // Exact Cubuild/Classic MC block-icon proportions (from drawProjectedBlockIcon,
            // scaled from its 64-unit canvas down to our 48px cells):
            //   top  = (24,4.5)(37.5,11.25)(24,18)(10.5,11.25)
            //   left = (10.5,11.25)(24,18)(24,37.5)(10.5,30.75)
            //   right= (24,18)(37.5,11.25)(37.5,30.75)(24,37.5)
            const float cx = 24f;                 // cube center x
            const float halfW = 13.5f;            // horizontal half-extent
            const float diamondTopY = 4.5f;       // diamond top vertex
            const float diamondMidY = 11.25f;     // diamond left/right vertices
            const float diamondBottomY = 18f;     // diamond bottom vertex
            const float cubeBottomY = 37.5f;      // bottom of the side faces
            const float sideDrop = cubeBottomY - diamondBottomY;      // 19.5
            const float diamondHalfDrop = diamondBottomY - diamondMidY; // 6.75
            const float topDenom = 2f * halfW;    // 27
            _blockIconUv = new Vector4[blockCount];

            for (int id = 1; id < blockCount; id++)
            {
                var def = BlockRegistry.GetById(id);
                var topTile = def.FaceTexture(new Point3D(0, 1, 0));
                var leftTile = def.FaceTexture(new Point3D(0, 0, -1));
                var rightTile = def.FaceTexture(new Point3D(1, 0, 0));

                int cellX = ((id - 1) % cols) * iconSize;
                int cellY = ((id - 1) / cols) * iconSize;

                for (int py = 0; py < iconSize; py++)
                {
                    for (int px = 0; px < iconSize; px++)
                    {
                        int di = ((cellY + py) * atlasW + (cellX + px)) * 4;

                        // Right face: u from front toward back, v straight down.
                        float u = (px - cx) / halfW;
                        if (u >= 0f && u <= 1f)
                        {
                            float v = (py - diamondBottomY + u * diamondHalfDrop) / sideDrop;
                            if (v >= 0f && v <= 1f)
                            {
                                SampleTile(iconData, di, rightTile, u, v);
                                continue;
                            }
                        }

                        // Front-left face: u from back toward front, v straight down.
                        u = (px - cx + halfW) / halfW;
                        if (u >= 0f && u <= 1f)
                        {
                            float v = (py - diamondMidY - u * diamondHalfDrop) / sideDrop;
                            if (v >= 0f && v <= 1f)
                            {
                                SampleTile(iconData, di, leftTile, u, v);
                                continue;
                            }
                        }

                        // Top face: diamond (affine inverse of the top parallelogram).
                        float su = px - cx;
                        float sv = py - diamondTopY;
                        u = (su + 2f * sv) / topDenom;
                        float tv = (2f * sv - su) / topDenom;
                        if (u >= 0f && u <= 1f && tv >= 0f && tv <= 1f)
                        {
                            SampleTile(iconData, di, topTile, u, tv);
                        }
                    }
                }

                _blockIconUv[id] = new Vector4(
                    cellX / (float)atlasW, cellY / (float)atlasH,
                    iconSize / (float)atlasW, iconSize / (float)atlasH);
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

        // Copies one nearest-sampled texel from the terrain atlas into the icon buffer.
        private void SampleTile(byte[] dst, int di, TextureRect tile, float u, float v)
        {
            int tx = tile.X + (int)(u * 15.999f);
            int ty = tile.Y + (int)(v * 15.999f);
            int si = (ty * _atlasPixelsW + tx) * 4;
            dst[di + 0] = _atlasRgba[si + 0];
            dst[di + 1] = _atlasRgba[si + 1];
            dst[di + 2] = _atlasRgba[si + 2];
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
layout(set=0, binding=0) uniform ProjectionView { mat4 projView; };
void main() { vLocalUV = aLocalUV; vTileRect = aTileRect; vColor = aColor; gl_Position = projView * vec4(aPosition, 1.0); }";

            string fsCode = @"#version 450
layout(location=0) in vec2 vLocalUV;
layout(location=1) in vec4 vTileRect;
layout(location=2) in vec4 vColor;
layout(set=1, binding=0) uniform sampler2D uAtlas;
layout(location=0) out vec4 outColor;
void main() {
    // fract() tiles the same atlas tile regardless of how many blocks the face spans.
    vec2 atlasUV = fract(vLocalUV) * vTileRect.zw + vTileRect.xy;
    vec4 tex = texture(uAtlas, atlasUV);
    // Block alpha (vColor.a) governs opacity - transparent tiles (water, glass, leaves) are
    // tinted by their configured alpha, not the atlas art's baked alpha. Opaque blocks
    // (alpha = 1) sample the tile fully. (Per-pixel alpha cutouts like classic leaves are a
    // future alpha-test feature.)
    outColor = vec4(tex.rgb * vColor.rgb, vColor.a);
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

            var pipelineDesc = new GraphicsPipelineDescription()
            {
                // Alpha blend so blocks flagged transparent (water) tint see-through; opaque tiles
                // have alpha 1 so they look identical to the old override-blend behaviour.
                BlendState = BlendStateDescription.SingleAlphaBlend,
                DepthStencilState = new DepthStencilStateDescription(true, true, ComparisonKind.LessEqual),
                RasterizerState = new RasterizerStateDescription(FaceCullMode.Back, PolygonFillMode.Solid, FrontFace.CounterClockwise, true, false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _projViewLayout, _textureLayout },
                ShaderSet = shaderSet,
                Outputs = _sc.Framebuffer.OutputDescription
            };

            _pipeline = factory.CreateGraphicsPipeline(pipelineDesc);

            // Same pipeline, depth-write OFF: drawn after all opaque chunks so blended water
            // tints the already-rendered terrain instead of depth-blocking it (see field comment).
            _transparentPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription()
            {
                BlendState = BlendStateDescription.SingleAlphaBlend,
                DepthStencilState = new DepthStencilStateDescription(true, false, ComparisonKind.LessEqual),
                RasterizerState = new RasterizerStateDescription(FaceCullMode.Back, PolygonFillMode.Solid, FrontFace.CounterClockwise, true, false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _projViewLayout, _textureLayout },
                ShaderSet = shaderSet,
                Outputs = _sc.Framebuffer.OutputDescription
            });

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

        public void Resize(int width, int height)
        {
            _sc?.Resize((uint)Math.Max(1, width), (uint)Math.Max(1, height));
            _imguiRenderer?.WindowResized(Math.Max(1, width), Math.Max(1, height));
        }

        public void SetHud(HudState hud)
        {
            _hud = hud;
        }

        public void SetEntities(IReadOnlyList<CubeApp.MobRenderData> mobRenderData)
        {
            // Route the unified MobRenderData snapshots to per-model instance lists. DuckInstance
            // carries exactly the fields both models need, so it doubles as the player instance.
            if (mobRenderData == null || mobRenderData.Count == 0)
            {
                _duckInstances = Array.Empty<CubeApp.DuckInstance>();
                _playerInstances = Array.Empty<CubeApp.DuckInstance>();
                return;
            }

            List<CubeApp.DuckInstance>? ducks = null;
            List<CubeApp.DuckInstance>? players = null;
            for (int i = 0; i < mobRenderData.Count; i++)
            {
                var md = mobRenderData[i];
                bool isDuck = string.Equals(md.MobType, "duck", StringComparison.OrdinalIgnoreCase);
                bool isPlayer = !isDuck && string.Equals(md.MobType, "player", StringComparison.OrdinalIgnoreCase);
                if (!isDuck && !isPlayer) continue;

                var inst = new CubeApp.DuckInstance(
                    md.Position, md.Yaw, md.HeadYawLocal,
                    md.WalkPhase, md.WalkAmount, md.FlapPhase,
                    md.VelocityY, md.OnGround,
                    md.IsDead, md.DeathT, md.DeathRollDir, md.HurtTimer);

                if (isDuck) (ducks ??= new List<CubeApp.DuckInstance>()).Add(inst);
                else (players ??= new List<CubeApp.DuckInstance>()).Add(inst);
            }

            _duckInstances = (IReadOnlyList<CubeApp.DuckInstance>?)ducks ?? Array.Empty<CubeApp.DuckInstance>();
            _playerInstances = (IReadOnlyList<CubeApp.DuckInstance>?)players ?? Array.Empty<CubeApp.DuckInstance>();
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
                WriteChunkData(pu.Coord, pu.Vertices, pu.Indices, pu.TransparentVertices, pu.TransparentIndices);
            }

            int uploadsThisFrame = 0;
            while (uploadsThisFrame < _maxUploadsPerFrame && _pendingUploads.TryDequeue(out var pu))
            {
                WriteChunkData(pu.Coord, pu.Vertices, pu.Indices, pu.TransparentVertices, pu.TransparentIndices);
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
            cl.ClearColorTarget(0, RgbaFloat.CornflowerBlue);
            cl.ClearDepthStencil(1f);

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
            // chunks draw first and depth-write; the transparent (water) pass draws afterwards with
            // depth-writes off so it tints terrain that already rendered.
            if (_megaVertexBuffer != null && _megaIndexBuffer != null)
            {
                DrawWorldPass(cl, _drawCommands, _indirectScratch, _pipeline);
                if (_transparentDrawCommands.Count > 0)
                {
                    DrawWorldPass(cl, _transparentDrawCommands, _transparentIndirectScratch, _transparentPipeline);
                }
            }

            DrawDucks(cl);
            DrawPlayers(cl);
            DrawHighlight(cl);
            DrawChunkBorders(cl);

            _imguiRenderer.Update(1f / 60f, NullInputSnapshot.Instance);
            BuildHudUi();
            _imguiRenderer.Render(_gd, cl);

            cl.End();
            _gd.SubmitCommands(cl);
            _gd.SwapBuffers(_sc);
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
private void WriteChunkData(CubeApp.ChunkCoordinates coord, float[] verts, ushort[] indices, float[] transVerts, ushort[] transIndices)
        {
            if (_chunkRanges.TryGetValue(coord, out var prev))
            {
                FreeRange(prev);
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

            // Transparent (water) faces: only when the chunk actually has any, uploaded into the
            // same mega buffers but tracked separately for the second draw pass.
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
            // World Y bounds come from the world origin, not 0. With minY=0 the chunk under the
            // camera gets culled the moment the eye drops below Y≈0 (near plane dips past the box).
            const float minY = ChunkManager.WorldOriginY;
            const float maxY = ChunkManager.WorldOriginY + ChunkManager.ChunkHeight;

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

        private void BuildHudUi()
        {
            var io = ImGui.GetIO();
            var displaySize = io.DisplaySize;
            var drawList = ImGui.GetForegroundDrawList();

            // Crosshair
            var center = new Vector2(displaySize.X / 2f, displaySize.Y / 2f);
            uint crosshairColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f));
            drawList.AddLine(new Vector2(center.X - 8, center.Y), new Vector2(center.X - 2, center.Y), crosshairColor, 2f);
            drawList.AddLine(new Vector2(center.X + 2, center.Y), new Vector2(center.X + 8, center.Y), crosshairColor, 2f);
            drawList.AddLine(new Vector2(center.X, center.Y - 8), new Vector2(center.X, center.Y - 2), crosshairColor, 2f);
            drawList.AddLine(new Vector2(center.X, center.Y + 2), new Vector2(center.X, center.Y + 8), crosshairColor, 2f);
            drawList.AddCircleFilled(center, 2f, crosshairColor);

            // The targeted block face highlight is drawn as a depth-tested 3D quad in Render(),
            // not here, so that blocks in front of it occlude it correctly.

            // Hotbar
            const int slotSize = 48;
            const int padding = 6;
            const int hotbarSlots = 10;
            int totalWidth = hotbarSlots * (slotSize + padding) - padding;
            float startX = (displaySize.X - totalWidth) / 2f;
            float hotbarY = displaySize.Y - slotSize - 16f;

            uint slotBg = ImGui.ColorConvertFloat4ToU32(new Vector4(36 / 255f, 45 / 255f, 52 / 255f, 1f));
            uint slotBorder = ImGui.ColorConvertFloat4ToU32(new Vector4(100 / 255f, 150 / 255f, 200 / 255f, 1f));
            uint activeBorder = ImGui.ColorConvertFloat4ToU32(new Vector4(255 / 255f, 215 / 255f, 110 / 255f, 1f));
            uint textColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f));

            for (int i = 0; i < hotbarSlots; i++)
            {
                float x = startX + i * (slotSize + padding);
                var topLeft = new Vector2(x, hotbarY);
                var bottomRight = new Vector2(x + slotSize, hotbarY + slotSize);
                drawList.AddRectFilled(topLeft, bottomRight, slotBg);
                drawList.AddRect(topLeft, bottomRight, slotBorder);

                if (i == _hud.SelectedSlot)
                {
                    drawList.AddRect(topLeft + new Vector2(4, 4), bottomRight - new Vector2(4, 4), activeBorder, 0f, ImDrawFlags.None, 2f);
                }

                if (i < BlockRegistry.Hotbar.Count)
                {
                    int bid = BlockRegistry.Hotbar[i];
                    if (_iconImGuiId != IntPtr.Zero && _blockIconUv != null && bid < _blockIconUv.Length)
                    {
                        var uv = _blockIconUv[bid];
                        drawList.AddImage(
                            _iconImGuiId,
                            topLeft + new Vector2(2, 2),
                            topLeft + new Vector2(46, 46),
                            new Vector2(uv.X, uv.Y),
                            new Vector2(uv.X + uv.Z, uv.Y + uv.W));
                    }
                    else
                    {
                        uint iconColor = BlockRegistry.MapColorOf(bid);
                        drawList.AddRectFilled(topLeft + new Vector2(8, 8), topLeft + new Vector2(40, 40), iconColor);
                    }
                }

                drawList.AddText(topLeft + new Vector2(4, 2), textColor, ((i + 1) % 10).ToString());
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
            }
        }

        public void Dispose()
        {
            _chunkRanges.Clear();
            _freeBlocks.Clear();
            _drawCommands.Clear();
            _megaVertexBuffer?.Dispose();
            _megaIndexBuffer?.Dispose();
            _indirectBuffer?.Dispose();

            _projViewSet?.Dispose();
            _projViewLayout?.Dispose();
            _projViewBuffer?.Dispose();
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
            _modelPipeline?.Dispose();
            _pipeline?.Dispose();
            _transparentPipeline?.Dispose();
            if (_iconAtlasTexture != null && _imguiRenderer != null) _imguiRenderer.RemoveImGuiBinding(_iconAtlasTexture);
            _iconAtlasView?.Dispose();
            _iconAtlasTexture?.Dispose();
            _sc?.Dispose();
            _gd?.Dispose();
        }

        public void UploadChunk(CubeApp.ChunkCoordinates coords, System.Collections.Generic.IReadOnlyList<CubeApp.MeshFace> faces)
        {
            BuildMesh(faces, out var vArr, out var iArr, out var tvArr, out var tiArr);
            _pendingUploads.Enqueue(new PendingUpload(coords, vArr, iArr, tvArr, tiArr));
        }

        // Player edits jump the line: same vertex data, but enqueued on the priority queue that
        // ProcessPendingPriorityMeshes drains every frame for instant feedback.
        public void UploadChunkPriority(CubeApp.ChunkCoordinates coords, System.Collections.Generic.IReadOnlyList<CubeApp.MeshFace> faces)
        {
            BuildMesh(faces, out var vArr, out var iArr, out var tvArr, out var tiArr);
            _pendingPriorityUploads.Enqueue(new PendingUpload(coords, vArr, iArr, tvArr, tiArr));
        }

        // Builds the 13-float-per-vertex chunk mesh (pos + localUV + tileRect + color) from greedy
        // faces. Per-face alpha comes from MeshFace.Alpha so transparent blocks (water) can blend.
        // Faces with alpha < 1 go into a separate transparent buffer pair that the renderer uploads
        // into its own range and draws in a second, depth-write-free pass.
        // Sizes are deterministic (4 verts + 6 indices per face), so the target arrays are filled
        // directly - no List<T>, no ToArray() copies, no double allocation per chunk upload.
        private void BuildMesh(
            System.Collections.Generic.IReadOnlyList<CubeApp.MeshFace> faces,
            out float[] vertsArr, out ushort[] indicesArr,
            out float[] transVertsArr, out ushort[] transIndicesArr)
        {
            // vertex layout: position(3) + localUV(2) + tileRect(4) + color(4) = 13 floats per vertex
            int faceCount = faces.Count;
            int opaqueFaces = 0;
            for (int i = 0; i < faceCount; i++)
            {
                if (faces[i].Alpha >= 1f) opaqueFaces++;
            }
            int transFaces = faceCount - opaqueFaces;

            var verts = new float[opaqueFaces * 4 * 13];
            var indices = new ushort[opaqueFaces * 6];
            var transVerts = new float[transFaces * 4 * 13];
            var transIndices = new ushort[transFaces * 6];
            float atlasW = Math.Max(1f, _atlasWidth);
            float atlasH = Math.Max(1f, _atlasHeight);
            // Hoisted out of the face loop: stackalloc reserves stack for the method, so a
            // per-face stackalloc would grow the frame with face count (CA2014).
            Span<CubeApp.Point3D> vertsSpan = stackalloc CubeApp.Point3D[4];
            int opaqueFace = 0;
            int transFace = 0;
            for (int fi = 0; fi < faceCount; fi++)
            {
                var f = faces[fi];
                bool isTrans = f.Alpha < 1f;
                var dstVerts = isTrans ? transVerts : verts;
                var dstIndices = isTrans ? transIndices : indices;
                int faceIdx = isTrans ? transFace : opaqueFace;
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

                if (isTrans) transFace++; else opaqueFace++;
            }

            vertsArr = verts;
            indicesArr = indices;
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
            var proj = Matrix4x4.CreatePerspectiveFieldOfView((float)(Math.PI / 2.0), (float)_sc.Framebuffer.Width / _sc.Framebuffer.Height, 0.1f, _farPlane);
            var yawRad = yaw * (float)Math.PI / 180f;
            var pitchRad = pitch * (float)Math.PI / 180f;
            var forward = new Vector3((float)(Math.Cos(pitchRad) * Math.Sin(yawRad)), (float)Math.Sin(pitchRad), (float)(Math.Cos(pitchRad) * Math.Cos(yawRad)));
            var cameraPos = new Vector3((float)position.X, (float)position.Y, (float)position.Z);
            var target = cameraPos + forward;
            var view = Matrix4x4.CreateLookAt(cameraPos, target, Vector3.UnitY);
            var viewProj = Matrix4x4.Multiply(view, proj);
            // Cache the camera and view-projection so chunk frustum culling and the mob meshing
            // can read them without re-deriving.
            _cameraPosition = position;
            _viewProjection = viewProj;
            _gd.UpdateBuffer(_projViewBuffer, 0, ref viewProj);
        }

        public void SetRenderDistance(int chunkRadius)
        {
            // Push the far clip past the farthest visible chunk corner so distant terrain isn't
            // clipped when the render distance grows. 16 blocks/chunk, ~1.5x for the diagonal + margin.
            _farPlane = Math.Max(100f, chunkRadius * 16f * 1.5f + 32f);
        }

        public void SetChunkManager(CubeApp.ChunkManager manager)
        {
            _chunkManager = manager;
        }

        // Drains priority (player-edit) uploads every frame so edits appear immediately, ahead of
        // the budget-limited background streaming uploads.
        public void ProcessPendingPriorityMeshes()
        {
            while (_pendingPriorityUploads.TryDequeue(out var pu))
            {
                WriteChunkData(pu.Coord, pu.Vertices, pu.Indices, pu.TransparentVertices, pu.TransparentIndices);
            }
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
