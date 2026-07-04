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
        private static readonly BlockType[] HotbarBlockTypes =
        {
            BlockType.Grass,
            BlockType.Dirt,
            BlockType.Stone,
            BlockType.Cobblestone,
            BlockType.Sand,
            BlockType.Planks,
            BlockType.Bedrock,
            BlockType.Gravel,
            BlockType.Obsidian,
            BlockType.MossyCobblestone,
        };

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
        private Pipeline _highlightPipeline;
        private DeviceBuffer _highlightVertexBuffer;
        private DeviceBuffer _highlightIndexBuffer;
        private readonly float[] _highlightVertexScratch = new float[12];
        private CommandList _commandList;
        private ImGuiRenderer _imguiRenderer;
        private HudState _hud = HudState.Empty;
        private float _farPlane = 100f;
        private float _atlasWidth = 256f;
        private float _atlasHeight = 256f;

        private struct GpuChunk
        {
            public DeviceBuffer VertexBuffer;
            public DeviceBuffer IndexBuffer;
            public uint IndexCount;
            public uint VertexCapacity;
            public uint IndexCapacity;
        }
        private readonly Dictionary<CubeApp.ChunkCoordinates, GpuChunk> _chunks = new();
        private readonly System.Collections.Concurrent.ConcurrentQueue<PendingUpload> _pendingUploads = new();
        private readonly System.Collections.Concurrent.ConcurrentQueue<CubeApp.ChunkCoordinates> _pendingRemovals = new();
        // Upload budget per frame to avoid large spikes
        private int _maxUploadsPerFrame = 4;

        // Simple buffer pools (render-thread only)
        private readonly List<(DeviceBuffer Buffer, uint Capacity)> _vbPool = new();
        private readonly List<(DeviceBuffer Buffer, uint Capacity)> _ibPool = new();

        private readonly struct PendingUpload
        {
            public CubeApp.ChunkCoordinates Coord { get; }
            public float[] Vertices { get; }
            public ushort[] Indices { get; }

            public PendingUpload(CubeApp.ChunkCoordinates coord, float[] vertices, ushort[] indices)
            {
                Coord = coord;
                Vertices = vertices;
                Indices = indices;
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

            CreatePipeline();

            _imguiRenderer = new ImGuiRenderer(
                _gd,
                _sc.Framebuffer.OutputDescription,
                Math.Max(1, (int)_sc.Framebuffer.Width),
                Math.Max(1, (int)_sc.Framebuffer.Height));
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
    outColor = tex * vColor;
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
                BlendState = BlendStateDescription.SingleOverrideBlend,
                DepthStencilState = new DepthStencilStateDescription(true, true, ComparisonKind.LessEqual),
                RasterizerState = new RasterizerStateDescription(FaceCullMode.Back, PolygonFillMode.Solid, FrontFace.CounterClockwise, true, false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _projViewLayout, _textureLayout },
                ShaderSet = shaderSet,
                Outputs = _sc.Framebuffer.OutputDescription
            };

            _pipeline = factory.CreateGraphicsPipeline(pipelineDesc);

            // create texture resource set if atlas loaded
            if (_atlasView != null && _atlasSampler != null)
            {
                _textureSet = factory.CreateResourceSet(new ResourceSetDescription(_textureLayout, _atlasView, _atlasSampler));
            }

            // Reuse a single command list across frames instead of allocating one per frame.
            _commandList = factory.CreateCommandList();

            CreateHighlightPipeline();
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

        public void Resize(int width, int height)
        {
            _sc?.Resize((uint)Math.Max(1, width), (uint)Math.Max(1, height));
            _imguiRenderer?.WindowResized(Math.Max(1, width), Math.Max(1, height));
        }

        public void SetHud(HudState hud)
        {
            _hud = hud;
        }

        public void Render()
        {
            // Process pending removals/uploads on render thread
            while (_pendingRemovals.TryDequeue(out var rem))
            {
                if (_chunks.TryGetValue(rem, out var existing))
                {
                    existing.VertexBuffer.Dispose();
                    existing.IndexBuffer.Dispose();
                    _chunks.Remove(rem);
                }
            }

            int uploadsThisFrame = 0;
            while (uploadsThisFrame < _maxUploadsPerFrame && _pendingUploads.TryDequeue(out var pu))
            {
                // dispose existing if present
                if (_chunks.TryGetValue(pu.Coord, out var existing))
                {
                    // return previous buffers to pool
                    ReturnVertexBuffer(existing.VertexBuffer, existing.VertexCapacity);
                    ReturnIndexBuffer(existing.IndexBuffer, existing.IndexCapacity);
                    _chunks.Remove(pu.Coord);
                }

                uint vbSize = (uint)(pu.Vertices.Length * sizeof(float));
                uint ibSize = (uint)(pu.Indices.Length * sizeof(ushort));

                var vb = AcquireVertexBuffer(vbSize);
                _gd.UpdateBuffer(vb, 0, pu.Vertices);
                var ib = AcquireIndexBuffer(ibSize);
                _gd.UpdateBuffer(ib, 0, pu.Indices);

                _chunks[pu.Coord] = new GpuChunk { VertexBuffer = vb, IndexBuffer = ib, IndexCount = (uint)pu.Indices.Length, VertexCapacity = vbSize, IndexCapacity = ibSize };
                uploadsThisFrame++;
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

            foreach (var kv in _chunks)
            {
                var c = kv.Value;
                cl.SetVertexBuffer(0, c.VertexBuffer);
                cl.SetIndexBuffer(c.IndexBuffer, IndexFormat.UInt16);
                cl.DrawIndexed(c.IndexCount, 1, 0, 0, 0);
            }

            DrawHighlight(cl);

            _imguiRenderer.Update(1f / 60f, NullInputSnapshot.Instance);
            BuildHudUi();
            _imguiRenderer.Render(_gd, cl);

            cl.End();
            _gd.SubmitCommands(cl);
            _gd.SwapBuffers(_sc);
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

                if (i < HotbarBlockTypes.Length)
                {
                    var blockType = HotbarBlockTypes[i];
                    uint iconColor = GetBlockRgba(blockType);
                    drawList.AddRectFilled(topLeft + new Vector2(8, 8), topLeft + new Vector2(40, 40), iconColor);
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

        private static uint GetBlockRgba(BlockType blockType)
        {
            return blockType switch
            {
                BlockType.Grass => ImGui.ColorConvertFloat4ToU32(new Vector4(34 / 255f, 139 / 255f, 34 / 255f, 1f)),
                BlockType.Dirt => ImGui.ColorConvertFloat4ToU32(new Vector4(139 / 255f, 69 / 255f, 19 / 255f, 1f)),
                BlockType.Stone => ImGui.ColorConvertFloat4ToU32(new Vector4(128 / 255f, 128 / 255f, 128 / 255f, 1f)),
                BlockType.Cobblestone => ImGui.ColorConvertFloat4ToU32(new Vector4(112 / 255f, 112 / 255f, 112 / 255f, 1f)),
                BlockType.Sand => ImGui.ColorConvertFloat4ToU32(new Vector4(210 / 255f, 196 / 255f, 140 / 255f, 1f)),
                BlockType.Planks => ImGui.ColorConvertFloat4ToU32(new Vector4(160 / 255f, 120 / 255f, 70 / 255f, 1f)),
                BlockType.Bedrock => ImGui.ColorConvertFloat4ToU32(new Vector4(78 / 255f, 78 / 255f, 78 / 255f, 1f)),
                BlockType.Gravel => ImGui.ColorConvertFloat4ToU32(new Vector4(132 / 255f, 132 / 255f, 132 / 255f, 1f)),
                BlockType.Obsidian => ImGui.ColorConvertFloat4ToU32(new Vector4(60 / 255f, 45 / 255f, 90 / 255f, 1f)),
                BlockType.MossyCobblestone => ImGui.ColorConvertFloat4ToU32(new Vector4(94 / 255f, 108 / 255f, 94 / 255f, 1f)),
                _ => ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0f)),
            };
        }

        public void Dispose()
        {
            foreach (var kv in _chunks)
            {
                kv.Value.VertexBuffer.Dispose();
                kv.Value.IndexBuffer.Dispose();
            }
            _chunks.Clear();

            foreach (var vb in _vbPool)
            {
                vb.Buffer.Dispose();
            }
            _vbPool.Clear();
            foreach (var ib in _ibPool)
            {
                ib.Buffer.Dispose();
            }
            _ibPool.Clear();

            _projViewSet?.Dispose();
            _projViewLayout?.Dispose();
            _projViewBuffer?.Dispose();
            _commandList?.Dispose();
            _imguiRenderer?.Dispose();
            _highlightVertexBuffer?.Dispose();
            _highlightIndexBuffer?.Dispose();
            _highlightPipeline?.Dispose();
            _pipeline?.Dispose();
            _sc?.Dispose();
            _gd?.Dispose();
        }

        public void UploadChunk(CubeApp.ChunkCoordinates coords, System.Collections.Generic.IReadOnlyList<CubeApp.MeshFace> faces)
        {
            // vertex layout: position(3) + localUV(2) + tileRect(4) + color(4) = 13 floats per vertex
            var verts = new List<float>(faces.Count * 4 * 13);
            var indices = new List<ushort>(faces.Count * 6);
            ushort vi = 0;
            foreach (var f in faces)
            {
                float atlasW = Math.Max(1f, _atlasWidth);
                float atlasH = Math.Max(1f, _atlasHeight);
                int tileW = Math.Max(1, f.SrcRect.Width);
                int tileH = Math.Max(1, f.SrcRect.Height);
                int spanU = Math.Max(1, f.TileWidth);
                int spanV = Math.Max(1, f.TileHeight);

                bool hasAxes = TryGetCubuildFaceAxes(f.Normal, out var uAxis, out var vAxis);
                double minU = 0.0;
                double minV = 0.0;
                if (hasAxes)
                {
                    minU = double.PositiveInfinity;
                    minV = double.PositiveInfinity;
                    double maxU = double.NegativeInfinity;
                    double maxV = double.NegativeInfinity;

                    for (int ci = 0; ci < 4; ci++)
                    {
                        var c = f.Vertices[ci];
                        double u = Dot(c, uAxis);
                        double v = Dot(c, vAxis);
                        if (u < minU) minU = u;
                        if (u > maxU) maxU = u;
                        if (v < minV) minV = v;
                        if (v > maxV) maxV = v;
                    }

                    spanU = Math.Max(1, (int)Math.Round(maxU - minU));
                    spanV = Math.Max(1, (int)Math.Round(maxV - minV));
                }

                // color tint from shade
                float shade = f.Shade;
                float rf = shade;
                float gf = shade;
                float bf = shade;

                // Tile origin and size in atlas UV space; same for all 4 vertices of this face.
                // The fragment shader uses fract(localUV) * tileSize + tileOrigin so the
                // same tile repeats across greedy-merged multi-block faces.
                float tileOriginX = f.SrcRect.X / atlasW;
                float tileOriginY = f.SrcRect.Y / atlasH;
                float tileSzX = tileW / atlasW;
                float tileSzY = tileH / atlasH;

                var v0p = f.Vertices[0];
                var edgeU = f.Vertices[1] - v0p;
                var edgeV = f.Vertices[3] - v0p;
                double denomU = edgeU.X * edgeU.X + edgeU.Y * edgeU.Y + edgeU.Z * edgeU.Z;
                double denomV = edgeV.X * edgeV.X + edgeV.Y * edgeV.Y + edgeV.Z * edgeV.Z;

                for (int i = 0; i < 4; i++)
                {
                    var vv = f.Vertices[i];
                    double du;
                    double dv;
                    if (hasAxes)
                    {
                        // Continuous tile-unit coordinates; fract() in shader tiles the texture.
                        du = Dot(vv, uAxis) - minU;
                        dv = Dot(vv, vAxis) - minV;
                        du = Math.Clamp(du, 0.0, spanU);
                        dv = Math.Clamp(dv, 0.0, spanV);
                    }
                    else
                    {
                        var rel = vv - v0p;
                        du = denomU > 0 ? (rel.X * edgeU.X + rel.Y * edgeU.Y + rel.Z * edgeU.Z) / denomU : 0.0;
                        dv = denomV > 0 ? (rel.X * edgeV.X + rel.Y * edgeV.Y + rel.Z * edgeV.Z) / denomV : 0.0;
                        du = Math.Clamp(du, 0.0, 1.0) * spanU;
                        dv = Math.Clamp(dv, 0.0, 1.0) * spanV;
                    }

                    verts.Add((float)vv.X);
                    verts.Add((float)vv.Y);
                    verts.Add((float)vv.Z);
                    verts.Add((float)du);    // localUV.x  (0..spanU per-face tile units)
                    verts.Add((float)dv);    // localUV.y  (0..spanV per-face tile units)
                    verts.Add(tileOriginX);  // tileRect.x
                    verts.Add(tileOriginY);  // tileRect.y
                    verts.Add(tileSzX);      // tileRect.z
                    verts.Add(tileSzY);      // tileRect.w
                    verts.Add(rf); verts.Add(gf); verts.Add(bf); verts.Add(1f);
                }

                indices.Add((ushort)(vi + 0));
                indices.Add((ushort)(vi + 1));
                indices.Add((ushort)(vi + 2));
                indices.Add((ushort)(vi + 0));
                indices.Add((ushort)(vi + 2));
                indices.Add((ushort)(vi + 3));
                vi += 4;
            }

            var vArr = verts.ToArray();
            var iArr = indices.ToArray();
            _pendingUploads.Enqueue(new PendingUpload(coords, vArr, iArr));
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

        private DeviceBuffer AcquireVertexBuffer(uint sizeBytes)
        {
            for (int i = 0; i < _vbPool.Count; i++)
            {
                if (_vbPool[i].Capacity >= sizeBytes)
                {
                    var buf = _vbPool[i].Buffer;
                    _vbPool.RemoveAt(i);
                    return buf;
                }
            }

            return _gd.ResourceFactory.CreateBuffer(new BufferDescription(sizeBytes, BufferUsage.VertexBuffer | BufferUsage.Dynamic));
        }

        private void ReturnVertexBuffer(DeviceBuffer buf, uint capacity)
        {
            _vbPool.Add((buf, capacity));
        }

        private DeviceBuffer AcquireIndexBuffer(uint sizeBytes)
        {
            for (int i = 0; i < _ibPool.Count; i++)
            {
                if (_ibPool[i].Capacity >= sizeBytes)
                {
                    var buf = _ibPool[i].Buffer;
                    _ibPool.RemoveAt(i);
                    return buf;
                }
            }

            return _gd.ResourceFactory.CreateBuffer(new BufferDescription(sizeBytes, BufferUsage.IndexBuffer | BufferUsage.Dynamic));
        }

        private void ReturnIndexBuffer(DeviceBuffer buf, uint capacity)
        {
            _ibPool.Add((buf, capacity));
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
            _gd.UpdateBuffer(_projViewBuffer, 0, ref viewProj);
        }

        public void SetRenderDistance(int chunkRadius)
        {
            // Push the far clip past the farthest visible chunk corner so distant terrain isn't
            // clipped when the render distance grows. 16 blocks/chunk, ~1.5x for the diagonal + margin.
            _farPlane = Math.Max(100f, chunkRadius * 16f * 1.5f + 32f);
        }
    }
}
