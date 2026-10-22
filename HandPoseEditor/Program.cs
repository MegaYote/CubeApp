using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Veldrid;
using Veldrid.SPIRV;
using Veldrid.StartupUtilities;
using Veldrid.Sdl2;
using ImGuiNET;

namespace HandPoseEditor
{
    /// <summary>
    /// Standalone hand/held-item pose editor.
    /// Loads the exact same assets and rendering code as the game.
    /// </summary>
    internal sealed class Program
    {
        private static GraphicsDevice _gd;
        private static CommandList _cl;
        private static ImGuiRenderer _imgui;
        private static Sdl2Window _window;

        // --- Rendering state (mirrors VeldridRenderer's hand/held-item subset) ---
        private static DeviceBuffer _handVertexBuffer;
        private static DeviceBuffer _handIndexBuffer;
        private static float[] _handMesh = Array.Empty<float>();   // pos3+uv2+normal3 = 8 floats/vert
        private static ushort[] _handIndices = Array.Empty<ushort>();
        private static Pipeline _handPipeline;
        private static ResourceLayout _projViewLayout;
        private static ResourceSet _handProjSet;
        private static DeviceBuffer _projViewBuffer;
        private static Matrix4x4 _projView;

        // Held item pipelines
        private static Pipeline _heldBlockPipeline;
        private static Pipeline _heldItemPipeline;
        private static DeviceBuffer _heldInstanceBuffer;
        private static DeviceBuffer _heldCardVertexBuffer;
        private static DeviceBuffer _heldCardIndexBuffer;
        private static DeviceBuffer _itemDropVertexBuffer;
        private static DeviceBuffer _itemDropIndexBuffer;
        private static ResourceLayout _textureLayout;
        private static TextureView _itemsTextureView;
        private static TextureView _terrainTextureView;
        private static Sampler _textureSampler;
        private static ResourceSet _itemsTextureSet;
        private static ResourceSet _terrainTextureSet;

        // Atlas sizes
        private static int _atlasWidth = 256, _atlasHeight = 256;
        private static int _itemsAtlasWidth = 256, _itemsAtlasHeight = 256;

        // --- Tunable parameters (same as game) ---
        // Hand (arm pose)
        private static float _handScale = 1.0f;
        private static float _handSx = 0.5f;
        private static float _handSy = -0.75f;
        private static float _handSz = -0.6f;
        private static float _handBasePitch = -0.5f;
        private static float _handBaseYaw = 0.0f;

        // Held item/block - defaults are Minecraft's firstperson_righthand numbers
        private static float _heldOffsetX = 0f;
        private static float _heldOffsetY = 0f;
        private static float _heldOffsetZ = 0f;
        private static float _heldBlockScale = 1.3334f; // vanilla block: 0.40 wide (0.3 mesh)
        private static float _heldItemScale = 0.34f;    // vanilla item: 0.68 wide (quad spans +/-1)
        private static float _heldThickness = 1.0f;     // 1 sprite pixel of slab depth = vanilla
        private static float _heldYaw = 0f;             // user nudge only, vanilla values live in
        private static float _heldPitch = 0f;           // the per-class display transform below
        private static float _heldRoll = 0.0f;

        // Swing animation
        private static float _swingPhase = 0f;        // 0..1 over the swing
        private static float _swingDuration = 0.35f;  // seconds
        private static bool _playSwing = false;
        private static float _swingTimer = 0f;

        // Grip point (computed from hand mesh)
        private static float _gripX, _gripY, _gripZ;
        private static bool _gripComputed = false;

        // Current selection
        private static int _selectedItemId = 278; // diamond_pickaxe (genuine item)
        private static bool _isBlock = false;
        private static TextureRect _selectedTile;
        private static bool _selectedFromItemsAtlas = true;

        // UI state
        private static bool _showHand = true;
        private static bool _showHeld = true;
        private static bool _wireframe = false;
        private static float _cameraDistance = 1.5f;
        private static float _cameraYaw = 0f;
        private static float _cameraPitch = 0.3f;

        private const int HeldSlices = 8;
        private const int FallingCubeIndices = 36;

        [STAThread]
        static void Main()
        {
            VeldridStartup.CreateWindowAndGraphicsDevice(
                new WindowCreateInfo(100, 100, 1280, 800, WindowState.Normal, "Hand Pose Editor"),
                new GraphicsDeviceOptions(true, PixelFormat.D24_UNorm_S8_UInt, true, ResourceBindingModel.Improved, true, true),
                out _window, out _gd);

            _window.Resized += () => _gd.MainSwapchain.Resize((uint)_window.Width, (uint)_window.Height);
            _cl = _gd.ResourceFactory.CreateCommandList();
            _imgui = new ImGuiRenderer(_gd, _gd.MainSwapchain.Framebuffer.OutputDescription, (int)_window.Width, (int)_window.Height);

            LoadResources();
            CreatePipelines();
            BuildHandMesh();
            ComputeGripPoint();
            UpdateProjection();

            float lastTime = 0f;
            while (_window.Exists)
            {
                float now = (float)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
                float dt = now - lastTime; lastTime = now;

                var input = _window.PumpEvents();
                if (!_window.Exists) break;

                UpdateSwing(dt);
                _imgui.Update(dt, input);
                DrawUI();
                Render();
                _gd.SwapBuffers(_gd.MainSwapchain);
            }

            _imgui.Dispose();
            _cl.Dispose();
            _gd.Dispose();
        }

        private static void LoadResources()
        {
            var factory = _gd.ResourceFactory;
            // CubeApp is at C:\Users\jimza\CubeApp, HandPoseEditor builds to HandPoseEditor\bin\Release\...
            // Go up from HandPoseEditor output to find CubeApp Assets
            string handPoseDir = AppDomain.CurrentDomain.BaseDirectory;
            string cubeAppAssets = Path.GetFullPath(Path.Combine(handPoseDir, "..", "..", "..", "..", "Assets", "Textures"));

            // --- Load player skin (same as game) ---
            byte[] skinBytes = File.ReadAllBytes(Path.Combine(cubeAppAssets, "player.png"));
            var skinImg = StbImageSharp.ImageResult.FromMemory(skinBytes, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
            var skinTex = factory.CreateTexture(TextureDescription.Texture2D((uint)skinImg.Width, (uint)skinImg.Height, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled));
            _gd.UpdateTexture(skinTex, skinImg.Data, 0, 0, 0, (uint)skinImg.Width, (uint)skinImg.Height, 1, 0, 0);
            var skinView = factory.CreateTextureView(skinTex);

            // --- Load items.png ---
            byte[] itemsBytes = File.ReadAllBytes(Path.Combine(cubeAppAssets, "items.png"));
            var itemsImg = StbImageSharp.ImageResult.FromMemory(itemsBytes, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
            _itemsAtlasWidth = itemsImg.Width; _itemsAtlasHeight = itemsImg.Height;
            var itemsTex = factory.CreateTexture(TextureDescription.Texture2D((uint)itemsImg.Width, (uint)itemsImg.Height, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled));
            _gd.UpdateTexture(itemsTex, itemsImg.Data, 0, 0, 0, (uint)itemsImg.Width, (uint)itemsImg.Height, 1, 0, 0);
            _itemsTextureView = factory.CreateTextureView(itemsTex);

            // --- Load terrain.png ---
            byte[] terrainBytes = File.ReadAllBytes(Path.Combine(cubeAppAssets, "terrain.png"));
            var terrainImg = StbImageSharp.ImageResult.FromMemory(terrainBytes, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
            _atlasWidth = terrainImg.Width; _atlasHeight = terrainImg.Height;
            var terrainTex = factory.CreateTexture(TextureDescription.Texture2D((uint)terrainImg.Width, (uint)terrainImg.Height, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled));
            _gd.UpdateTexture(terrainTex, terrainImg.Data, 0, 0, 0, (uint)terrainImg.Width, (uint)terrainImg.Height, 1, 0, 0);
            _terrainTextureView = factory.CreateTextureView(terrainTex);

            // Sampler + layouts
            _textureSampler = factory.CreateSampler(new SamplerDescription(
                SamplerAddressMode.Clamp, SamplerAddressMode.Clamp, SamplerAddressMode.Clamp,
                SamplerFilter.MinPoint_MagPoint_MipLinear,
                ComparisonKind.Never, 0, 0, 0, 0, SamplerBorderColor.TransparentBlack));
            _projViewLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(new ResourceLayoutElementDescription("ProjectionView", ResourceKind.UniformBuffer, ShaderStages.Vertex)));
            _textureLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(new ResourceLayoutElementDescription("uTex", ResourceKind.TextureReadOnly, ShaderStages.Fragment), new ResourceLayoutElementDescription("uSampler", ResourceKind.Sampler, ShaderStages.Fragment)));

            // Texture sets
            _itemsTextureSet = factory.CreateResourceSet(new ResourceSetDescription(_textureLayout, _itemsTextureView, _textureSampler));
            _terrainTextureSet = factory.CreateResourceSet(new ResourceSetDescription(_textureLayout, _terrainTextureView, _textureSampler));

            // --- Projection uniform buffer (camera space) ---
            _projViewBuffer = factory.CreateBuffer(new BufferDescription(64, BufferUsage.UniformBuffer | BufferUsage.Dynamic));
            _handProjSet = factory.CreateResourceSet(new ResourceSetDescription(_projViewLayout, _projViewBuffer));

            // --- Meshes ---
            BuildItemDropCube(factory);      // 0.3 cube for blocks
            BuildHeldCardQuad(factory);      // flat quad for items (sprite stack)
        }

        private static void BuildItemDropCube(ResourceFactory factory)
        {
            // Same 0.3-unit cube as game's item drops, centered at origin
            float s = 0.15f;
            var verts = new float[24 * 8];
            int p = 0;
            void V(float x, float y, float z, float u, float v, float nx, float ny, float nz)
            {
                verts[p++] = x; verts[p++] = y; verts[p++] = z;
                verts[p++] = u; verts[p++] = v;
                verts[p++] = nx; verts[p++] = ny; verts[p++] = nz;
            }
            // Front (+Z)
            V(-s,-s, s, 0,1, 0,0,1); V(s,-s, s, 1,1, 0,0,1); V(s, s, s, 1,0, 0,0,1); V(-s, s, s, 0,0, 0,0,1);
            // Back (-Z)
            V(s,-s,-s, 0,1, 0,0,-1); V(-s,-s,-s, 1,1, 0,0,-1); V(-s, s,-s, 1,0, 0,0,-1); V(s, s,-s, 0,0, 0,0,-1);
            // Right (+X)
            V(s,-s, s, 0,1, 1,0,0); V(s,-s,-s, 1,1, 1,0,0); V(s, s,-s, 1,0, 1,0,0); V(s, s, s, 0,0, 1,0,0);
            // Left (-X)
            V(-s,-s,-s, 0,1, -1,0,0); V(-s,-s, s, 1,1, -1,0,0); V(-s, s, s, 1,0, -1,0,0); V(-s, s,-s, 0,0, -1,0,0);
            // Top (+Y)
            V(-s, s, s, 0,1, 0,1,0); V(s, s, s, 1,1, 0,1,0); V(s, s,-s, 1,0, 0,1,0); V(-s, s,-s, 0,0, 0,1,0);
            // Bottom (-Y)
            V(-s,-s,-s, 0,1, 0,-1,0); V(s,-s,-s, 1,1, 0,-1,0); V(s,-s, s, 1,0, 0,-1,0); V(-s,-s, s, 0,0, 0,-1,0);

            var indices = new ushort[36];
            int q = 0;
            void Quad(int f) { int b=f*4; indices[q++]=(ushort)(b+0); indices[q++]=(ushort)(b+1); indices[q++]=(ushort)(b+2); indices[q++]=(ushort)(b+0); indices[q++]=(ushort)(b+2); indices[q++]=(ushort)(b+3); }
            Quad(0); Quad(1); Quad(2); Quad(3); Quad(4); Quad(5);

            _itemDropVertexBuffer = factory.CreateBuffer(new BufferDescription((uint)verts.Length * 4, BufferUsage.VertexBuffer));
            _gd.UpdateBuffer(_itemDropVertexBuffer, 0, verts);
            _itemDropIndexBuffer = factory.CreateBuffer(new BufferDescription((uint)indices.Length * 2, BufferUsage.IndexBuffer));
            _gd.UpdateBuffer(_itemDropIndexBuffer, 0, indices);
        }

        private static void BuildHeldCardQuad(ResourceFactory factory)
        {
            // Flat quad at z=0, UVs with uvMax=0.999 to avoid fract wrap
            const float uvMax = 0.999f;
            var verts = new float[4 * 9]; // pos3+uv2+shade4
            int p = 0;
            void V(float x, float y, float u, float v)
            {
                verts[p++] = x; verts[p++] = y; verts[p++] = 0f;
                verts[p++] = u; verts[p++] = v;
                verts[p++] = 1f; verts[p++] = 1f; verts[p++] = 1f; verts[p++] = 1f;
            }
            V(-1,-1, 0, uvMax); V(1,-1, uvMax, uvMax); V(1,1, uvMax, 0); V(-1,1, 0, 0);
            var indices = new ushort[] { 0, 1, 2, 0, 2, 3 };

            _heldCardVertexBuffer = factory.CreateBuffer(new BufferDescription((uint)verts.Length * 4, BufferUsage.VertexBuffer));
            _gd.UpdateBuffer(_heldCardVertexBuffer, 0, verts);
            _heldCardIndexBuffer = factory.CreateBuffer(new BufferDescription((uint)indices.Length * 2, BufferUsage.IndexBuffer));
            _gd.UpdateBuffer(_heldCardIndexBuffer, 0, indices);
        }

        private static void BuildHandMesh()
        {
            // Exactly the same arm cube as the game: x in [-0.0625, 0.1875], y in [-0.625, 0.125], z in [-0.125, 0.125]
            float minX = -0.0625f, maxX = 0.1875f;
            float minY = -0.625f,  maxY = 0.125f;
            float minZ = -0.125f,  maxZ = 0.125f;

            var verts = new float[24 * 8]; // 6 faces * 4 verts, pos3+uv2+normal3
            int p = 0;
            void V(float x, float y, float z, float u, float v, float nx, float ny, float nz)
            {
                verts[p++] = x; verts[p++] = y; verts[p++] = z;
                verts[p++] = u; verts[p++] = v;
                verts[p++] = nx; verts[p++] = ny; verts[p++] = nz;
            }
            // Front (+Z)
            V(minX, minY, maxZ, 0,1, 0,0,1); V(maxX, minY, maxZ, 1,1, 0,0,1); V(maxX, maxY, maxZ, 1,0, 0,0,1); V(minX, maxY, maxZ, 0,0, 0,0,1);
            // Back (-Z)
            V(maxX, minY, minZ, 0,1, 0,0,-1); V(minX, minY, minZ, 1,1, 0,0,-1); V(minX, maxY, minZ, 1,0, 0,0,-1); V(maxX, maxY, minZ, 0,0, 0,0,-1);
            // Right (+X)
            V(maxX, minY, maxZ, 0,1, 1,0,0); V(maxX, minY, minZ, 1,1, 1,0,0); V(maxX, maxY, minZ, 1,0, 1,0,0); V(maxX, maxY, maxZ, 0,0, 1,0,0);
            // Left (-X)
            V(minX, minY, minZ, 0,1, -1,0,0); V(minX, minY, maxZ, 1,1, -1,0,0); V(minX, maxY, maxZ, 1,0, -1,0,0); V(minX, maxY, minZ, 0,0, -1,0,0);
            // Top (+Y)
            V(minX, maxY, maxZ, 0,1, 0,1,0); V(maxX, maxY, maxZ, 1,1, 0,1,0); V(maxX, maxY, minZ, 1,0, 0,1,0); V(minX, maxY, minZ, 0,0, 0,1,0);
            // Bottom (-Y)
            V(minX, minY, minZ, 0,1, 0,-1,0); V(maxX, minY, minZ, 1,1, 0,-1,0); V(maxX, minY, maxZ, 1,0, 0,-1,0); V(minX, minY, maxZ, 0,0, 0,-1,0);

            var indices = new ushort[36];
            int q = 0;
            void Quad(int f) { int b=f*4; indices[q++]=(ushort)(b+0); indices[q++]=(ushort)(b+1); indices[q++]=(ushort)(b+2); indices[q++]=(ushort)(b+0); indices[q++]=(ushort)(b+2); indices[q++]=(ushort)(b+3); }
            Quad(0); Quad(1); Quad(2); Quad(3); Quad(4); Quad(5);

            var factory = _gd.ResourceFactory;
            _handVertexBuffer = factory.CreateBuffer(new BufferDescription((uint)verts.Length * 4, BufferUsage.VertexBuffer));
            _gd.UpdateBuffer(_handVertexBuffer, 0, verts);
            _handIndexBuffer = factory.CreateBuffer(new BufferDescription((uint)indices.Length * 2, BufferUsage.IndexBuffer));
            _gd.UpdateBuffer(_handIndexBuffer, 0, indices);
            _handMesh = verts; _handIndices = indices;
        }

        private static void ComputeGripPoint()
        {
            if (_handMesh.Length < 9) return;
            float minY = float.MaxValue;
            for (int i = 0; i < _handMesh.Length; i += 8)
                if (_handMesh[i + 1] < minY) minY = _handMesh[i + 1];
            float sumX = 0, sumZ = 0; int count = 0;
            const float eps = 1e-5f;
            for (int i = 0; i < _handMesh.Length; i += 8)
            {
                if (_handMesh[i + 1] > minY + eps) continue;
                sumX += _handMesh[i]; sumZ += _handMesh[i + 2]; count++;
            }
            _gripX = count > 0 ? sumX / count : 0f;
            _gripY = minY;
            _gripZ = count > 0 ? sumZ / count : 0f;
            _gripComputed = true;
        }

        private static void CreatePipelines()
        {
            var factory = _gd.ResourceFactory;

            // Hand pipeline (skinned, camera-space)
            string handVs = @"#version 450
layout(location=0) in vec3 aPos; layout(location=1) in vec2 aUV; layout(location=2) in vec3 aNormal;
layout(location=0) out vec2 vUV; layout(set=0,binding=0) uniform ProjectionView { mat4 projView; };
void main() { vUV = aUV; gl_Position = projView * vec4(aPos, 1.0); }";
            string handFs = @"#version 450
layout(location=0) in vec2 vUV; layout(set=1,binding=0) uniform sampler2D uTex; layout(set=1,binding=1) uniform sampler uSampler; layout(location=0) out vec4 outColor;
void main() { vec4 c = texture(uTex, vUV); if (c.a < 0.5) discard; outColor = c; }";
            var handShaders = factory.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Vertex, SpirvCompilation.CompileGlslToSpirv(handVs, "main", ShaderStages.Vertex, GlslCompileOptions.Default).SpirvBytes, "main"),
                new ShaderDescription(ShaderStages.Fragment, SpirvCompilation.CompileGlslToSpirv(handFs, "main", ShaderStages.Fragment, GlslCompileOptions.Default).SpirvBytes, "main"));
            var handVLayout = new VertexLayoutDescription(
                new VertexElementDescription("aPos", VertexElementSemantic.Position, VertexElementFormat.Float3),
                new VertexElementDescription("aUV", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
                new VertexElementDescription("aNormal", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3));
            _handPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
            {
                BlendState = BlendStateDescription.SingleAlphaBlend,
                DepthStencilState = new DepthStencilStateDescription(true, true, ComparisonKind.LessEqual),
                RasterizerState = new RasterizerStateDescription(FaceCullMode.Back, _wireframe ? PolygonFillMode.Wireframe : PolygonFillMode.Solid, FrontFace.CounterClockwise, true, false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _projViewLayout, _textureLayout },
                ShaderSet = new ShaderSetDescription(new[] { handVLayout }, new[] { handShaders[0], handShaders[1] }),
                Outputs = _gd.MainSwapchain.Framebuffer.OutputDescription
            });

            // Held block pipeline (camera-space cube)
            string hbVs = @"#version 450
layout(location=0) in vec3 aCorner; layout(location=1) in vec2 aLocalUV; layout(location=2) in vec4 aShade;
layout(location=3) in vec3 iPos; layout(location=4) in vec4 iTileRect; layout(location=5) in vec4 iRot; layout(location=6) in float iScale;
layout(location=0) out vec2 vLocalUV; layout(location=1) out vec4 vTileRect; layout(location=2) out vec4 vColor;
layout(set=0,binding=0) uniform ProjectionView { mat4 projView; };
void main() {
    vec3 local = (aCorner - vec3(0.15)) * iScale;
    vec3 qv = iRot.xyz; vec3 t = 2.0 * cross(qv, local);
    vec3 rotated = local + iRot.w * t + cross(qv, t);
    vec3 pos = rotated + iPos;
    vLocalUV = aLocalUV; vTileRect = iTileRect; vColor = aShade;
    gl_Position = projView * vec4(pos, 1.0);
}";
            // Held item pipeline (sprite stack)
            string hiVs = @"#version 450
layout(location=0) in vec3 aCorner; layout(location=1) in vec2 aLocalUV; layout(location=2) in vec4 aShade;
layout(location=3) in vec3 iPos; layout(location=4) in vec4 iTileRect; layout(location=5) in vec4 iRot; layout(location=6) in float iScale; layout(location=7) in float iThickness;
layout(location=0) out vec2 vLocalUV; layout(location=1) out vec4 vTileRect; layout(location=2) out vec4 vColor; layout(location=3) out float vSlice;
layout(set=0,binding=0) uniform ProjectionView { mat4 projView; };
void main() {
    float t = float(gl_InstanceIndex) / " + (HeldSlices - 1) + @".0;
    vec3 local = aCorner * iScale;
    local.z = (1.0 - t * 2.0) * (iThickness * 0.0625) * iScale;
    vec3 qv = iRot.xyz; vec3 tq = 2.0 * cross(qv, local);
    vec3 rotated = local + iRot.w * tq + cross(qv, tq);
    vec3 pos = rotated + iPos;
    vLocalUV = aLocalUV; vTileRect = iTileRect; vColor = aShade; vSlice = t;
    gl_Position = projView * vec4(pos, 1.0);
}";
            string hFs = @"#version 450
layout(location=0) in vec2 vLocalUV; layout(location=1) in vec4 vTileRect; layout(location=2) in vec4 vColor; layout(location=3) in float vSlice;
layout(set=1,binding=0) uniform sampler2D uTex; layout(location=0) out vec4 outColor;
void main() {
    vec2 atlasUV = fract(vLocalUV) * vTileRect.zw + vTileRect.xy;
    vec4 tex = texture(uTex, atlasUV); if (tex.a < 0.5) discard;
    outColor = tex * vColor * mix(0.45, 1.0, vSlice);
}";

            var hbShaders = factory.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Vertex, SpirvCompilation.CompileGlslToSpirv(hbVs, "main", ShaderStages.Vertex, GlslCompileOptions.Default).SpirvBytes, "main"),
                new ShaderDescription(ShaderStages.Fragment, SpirvCompilation.CompileGlslToSpirv(hFs, "main", ShaderStages.Fragment, GlslCompileOptions.Default).SpirvBytes, "main"));
            var hiShaders = factory.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Vertex, SpirvCompilation.CompileGlslToSpirv(hiVs, "main", ShaderStages.Vertex, GlslCompileOptions.Default).SpirvBytes, "main"),
                new ShaderDescription(ShaderStages.Fragment, SpirvCompilation.CompileGlslToSpirv(hFs, "main", ShaderStages.Fragment, GlslCompileOptions.Default).SpirvBytes, "main"));

            var heldVLayout = new VertexLayoutDescription(
                new VertexElementDescription("aCorner", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                new VertexElementDescription("aLocalUV", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
                new VertexElementDescription("aShade", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4));
            var heldIL = new VertexLayoutDescription(
                new VertexElementDescription("iPos", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                new VertexElementDescription("iTileRect", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4),
                new VertexElementDescription("iRot", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4),
                new VertexElementDescription("iScale", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float1),
                new VertexElementDescription("iThickness", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float1));
            heldIL.InstanceStepRate = 1;

            _heldBlockPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
            {
                BlendState = BlendStateDescription.SingleAlphaBlend,
                DepthStencilState = DepthStencilStateDescription.Disabled,
                RasterizerState = new RasterizerStateDescription(FaceCullMode.Back, _wireframe ? PolygonFillMode.Wireframe : PolygonFillMode.Solid, FrontFace.CounterClockwise, true, false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _projViewLayout, _textureLayout },
                ShaderSet = new ShaderSetDescription(new[] { heldVLayout, heldIL }, new[] { hbShaders[0], hbShaders[1] }),
                Outputs = _gd.MainSwapchain.Framebuffer.OutputDescription
            });
            _heldItemPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
            {
                BlendState = BlendStateDescription.SingleAlphaBlend,
                DepthStencilState = DepthStencilStateDescription.Disabled,
                RasterizerState = new RasterizerStateDescription(FaceCullMode.None, _wireframe ? PolygonFillMode.Wireframe : PolygonFillMode.Solid, FrontFace.CounterClockwise, true, false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _projViewLayout, _textureLayout },
                ShaderSet = new ShaderSetDescription(new[] { heldVLayout, heldIL }, new[] { hiShaders[0], hiShaders[1] }),
                Outputs = _gd.MainSwapchain.Framebuffer.OutputDescription
            });

            _heldInstanceBuffer = factory.CreateBuffer(new BufferDescription((uint)(13 * 4 * HeldSlices), BufferUsage.VertexBuffer | BufferUsage.Dynamic));
        }

        private static void UpdateProjection()
        {
            float aspect = (float)_window.Width / _window.Height;
            // Camera-space perspective: we view from (0,0,0) looking down -Z
            // The hand/held are positioned around -1.0 Z
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(1.0472f, aspect, 0.01f, 10f);
            var view = Matrix4x4.CreateTranslation(0, 0, 0); // camera at origin
            _projView = view * proj; // Veldrid uses row-major * column vector? Actually Veldrid shaders expect column-major, so proj * view
            // Wait: in the game, the hand is drawn with projView = proj * view. Let me match.
            _projView = proj * view;
            _gd.UpdateBuffer(_projViewBuffer, 0, _projView);
        }

        private static void UpdateSwing(float dt)
        {
            if (_playSwing)
            {
                _swingTimer += dt;
                _swingPhase = MathF.Min(1f, _swingTimer / _swingDuration);
                if (_swingPhase >= 1f)
                {
                    _playSwing = false;
                    _swingTimer = 0f;
                    _swingPhase = 0f;
                }
            }
        }

        private static void DrawUI()
        {
            // Controls overlay (right side) - rendered on top of the 3D scene
            ImGui.SetNextWindowPos(new Vector2(_window.Width - 640, 0), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new Vector2(640, _window.Height), ImGuiCond.Always);
            ImGui.Begin("Controls", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse);
            {
                // --- Selection ---
                ImGui.Text("Selection");
                ImGui.Separator();
                string[] items = { "Diamond Pickaxe (item)", "Sand (block)", "Stone (block)", "Grass (block)", "Raw Coal (item)", "Flint Pickaxe (item)" };
                int[] ids = { 278, 1, 2, 3, 285, 37 }; // numeric ids from game
                bool[] isBlocks = { false, true, true, true, false, false };
                int idx = Array.IndexOf(ids, _selectedItemId);
                if (idx < 0) idx = 0;
                if (ImGui.Combo("Item", ref idx, items, items.Length))
                {
                    _selectedItemId = ids[idx];
                    _isBlock = isBlocks[idx];
                    UpdateSelectedTile();
                }
                ImGui.Checkbox("Show Hand", ref _showHand);
                ImGui.Checkbox("Show Held", ref _showHeld);
                ImGui.Checkbox("Wireframe", ref _wireframe);
                ImGui.Separator();

                // --- Swing Animation ---
                ImGui.Text("Swing Animation");
                ImGui.Separator();
                ImGui.SliderFloat("Swing Phase", ref _swingPhase, 0f, 1f);
                ImGui.SliderFloat("Swing Duration (s)", ref _swingDuration, 0.1f, 1f);
                if (ImGui.Button("Play Swing")) { _playSwing = true; _swingTimer = 0f; _swingPhase = 0f; }
                ImGui.SameLine();
                if (ImGui.Button("Reset")) { _playSwing = false; _swingTimer = 0f; _swingPhase = 0f; }
                ImGui.Separator();

                // --- Hand (arm pose) ---
                ImGui.Text("Hand (Arm Pose)");
                ImGui.Separator();
                ImGui.SliderFloat("handScale", ref _handScale, 0.4f, 1.4f);
                ImGui.SliderFloat("sx (right)", ref _handSx, 0.2f, 0.9f);
                ImGui.SliderFloat("sy (down)", ref _handSy, -1.2f, -0.3f);
                ImGui.SliderFloat("sz (forward)", ref _handSz, -1.0f, -0.2f);
                ImGui.SliderFloat("basePitch", ref _handBasePitch, -1.4f, 0.2f);
                ImGui.SliderFloat("baseYaw", ref _handBaseYaw, -0.9f, 0.9f);
                ImGui.Separator();

                // --- Held Item/Block ---
                ImGui.Text("Held Item / Block (in fist)");
                ImGui.Separator();
                ImGui.SliderFloat("heldX (left/right)", ref _heldOffsetX, -0.8f, 0.8f);
                ImGui.SliderFloat("heldY (up/down)", ref _heldOffsetY, -0.8f, 0.8f);
                ImGui.SliderFloat("heldZ (forward/back)", ref _heldOffsetZ, -1.0f, 0.5f);
                ImGui.SliderFloat("heldBlockScale", ref _heldBlockScale, 0.2f, 4f);
                ImGui.SliderFloat("heldItemScale", ref _heldItemScale, 0.02f, 1.2f);
                ImGui.SliderFloat("heldThickness (sprite px)", ref _heldThickness, 0f, 4f);
                ImGui.Separator();
                ImGui.Text("Global angles (applied after item's own display transform)");
                ImGui.SliderFloat("heldYaw (turn)", ref _heldYaw, -3.14f, 3.14f);
                ImGui.SliderFloat("heldPitch (tilt)", ref _heldPitch, -3.14f, 3.14f);
                ImGui.SliderFloat("heldRoll (twist)", ref _heldRoll, -3.14f, 3.14f);
                ImGui.Separator();

                // --- Per-item display transform (read-only display) ---
                ImGui.Text("Per-Item Display Transform (from items.json)");
                ImGui.Text($"  rotation:    0, 0, 0 deg"); // would need ItemRegistry for real values
                ImGui.Text($"  translation: 0, 0, 0");
                ImGui.Text($"  scale:       1.0");
                ImGui.Separator();

                // --- Export ---
                ImGui.Text("Export (copy to game)");
                if (ImGui.Button("Copy Hand Values"))
                {
                    string s = $"handScale={_handScale:0.###}f, sx={_handSx:0.###}f, sy={_handSy:0.###}f, sz={_handSz:0.###}f, basePitch={_handBasePitch:0.###}f, baseYaw={_handBaseYaw:0.###}f";
                    ImGui.SetClipboardText(s);
                }
                ImGui.SameLine();
                if (ImGui.Button("Copy Held Values"))
                {
                    string s = $"heldX={_heldOffsetX:0.###}f, heldY={_heldOffsetY:0.###}f, heldZ={_heldOffsetZ:0.###}f, heldBlockScale={_heldBlockScale:0.###}f, heldItemScale={_heldItemScale:0.###}f, heldThickness={_heldThickness:0.###}f, heldYaw={_heldYaw:0.###}f, heldPitch={_heldPitch:0.###}f, heldRoll={_heldRoll:0.###}f";
                    ImGui.SetClipboardText(s);
                }
                ImGui.Separator();

                // --- Camera ---
                ImGui.Text("Camera");
                ImGui.SliderFloat("Distance", ref _cameraDistance, 0.3f, 5f);
                ImGui.SliderFloat("Yaw", ref _cameraYaw, -3.14f, 3.14f);
                ImGui.SliderFloat("Pitch", ref _cameraPitch, -1.5f, 1.5f);
            }
            ImGui.End();
        }

        private static void UpdateSelectedTile()
        {
            // Simplified: just use a known tile for demo. In reality would query ItemRegistry.
            if (_isBlock)
            {
                // Block items use terrain atlas, tile depends on block id
                _selectedTile = new TextureRect(0, 0, 16, 16); // placeholder
                _selectedFromItemsAtlas = false;
            }
            else
            {
                // Genuine items use items.png
                // diamond_pickaxe = tile 1,8 (col 1, row 8) in items.png
                int col = 1, row = 8;
                if (_selectedItemId == 285) { col = 4; row = 3; } // raw_coal
                else if (_selectedItemId == 37) { col = 2; row = 0; } // flint_pickaxe
                _selectedTile = new TextureRect(col * 16, row * 16, 16, 16);
                _selectedFromItemsAtlas = true;
            }
        }

        private static void Render()
        {
            _cl.Begin();
            _cl.SetFramebuffer(_gd.MainSwapchain.Framebuffer);
            _cl.ClearColorTarget(0, new RgbaFloat(0.1f, 0.12f, 0.15f, 1f));
            _cl.ClearDepthStencil(1f);

            // Update projection for current camera
            float aspect = (float)_window.Width / _window.Height;
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(1.0472f, aspect, 0.01f, 10f);
            var camPos = new Vector3(
                _cameraDistance * MathF.Sin(_cameraYaw) * MathF.Cos(_cameraPitch),
                _cameraDistance * MathF.Sin(_cameraPitch),
                _cameraDistance * MathF.Cos(_cameraYaw) * MathF.Cos(_cameraPitch));
            var view = Matrix4x4.CreateLookAt(camPos, Vector3.Zero, Vector3.UnitY);
            _projView = proj * view;
            _gd.UpdateBuffer(_projViewBuffer, 0, _projView);

            // --- Draw Hand --- (per-frame pose like the game: idle pose -> bob -> chop around fist)
            if (_showHand)
            {
                ComputeFist(_swingPhase, out var handGrip, out var handQ, out var handBob);
                TransformHandMesh(handGrip, handQ, handBob);

                _cl.SetPipeline(_handPipeline);
                _cl.SetGraphicsResourceSet(0, _handProjSet);
                _cl.SetGraphicsResourceSet(1, _terrainTextureSet); // hand uses player skin, but for demo use terrain
                _cl.SetVertexBuffer(0, _handVertexBuffer);
                _cl.SetIndexBuffer(_handIndexBuffer, IndexFormat.UInt16);
                _cl.DrawIndexed((uint)_handIndices.Length, 1, 0, 0, 0);
            }

            // --- Draw Held Item/Block ---
            if (_showHeld)
            {
                DrawHeld(_cl, _swingPhase);
            }

            // Render ImGui on top
            _imgui.Render(_gd, _cl);

            _cl.End();
            _gd.SubmitCommands(_cl);
        }

        private static void DrawHeld(CommandList cl, float swing)
        {
            // Same structure as the game: grip carries the swing bob, and the vanilla attack
            // rotation around the fist is what the held object follows (rigid parenting).
            ComputeFist(swing, out Vector3 grip, out Quaternion qFist, out _);

            // Vanilla per-class display (firstperson_righthand), matching the values the game
            // reads from items.json: items rotation (0,-90,25) + translation (1.13/16,
            // 3.2/16, 1.13/16); blocks rotation (0,45,0), no translation.
            var dRot = _isBlock ? (pitch: 0f, yaw: 45f, roll: 0f) : (pitch: 0f, yaw: -90f, roll: 25f);
            var dTrans = _isBlock ? (x: 0f, y: 0f, z: 0f)
                                  : (x: 0.070625f, y: 0.2f, z: 0.070625f); // 1.13/16
            float dScale = 1f;
            const float D2R = MathF.PI / 180f;

            // Attack rotation wraps the display rotation; F8 nudge rides outermost.
            var qDisplay = Quaternion.CreateFromYawPitchRoll(dRot.yaw * D2R, dRot.pitch * D2R, dRot.roll * D2R);
            var q = Quaternion.Multiply(qFist, qDisplay);
            q = Quaternion.Multiply(Quaternion.CreateFromYawPitchRoll(_heldYaw, _heldPitch, _heldRoll), q);
            float scale = (_isBlock ? _heldBlockScale : _heldItemScale) * dScale;

            // Position: vanilla anchors by the object's ORIGIN CORNER (translate the attack
            // frame by the display translation, grow the sprite cube from there). Our meshes
            // are centered, so draw at the vanilla object's CENTER: corner + half extents
            // rotated by the same final rotation.
            var dVec = new Vector3(dTrans.x, dTrans.y, dTrans.z);
            Vector3 pos = grip + Vector3.Transform(dVec, Matrix4x4.CreateFromQuaternion(qFist));
            float worldSize = (_isBlock ? 0.3f : 2f) * scale;
            pos += Vector3.Transform(new Vector3(0.5f, 0.5f, 0.5f) * worldSize,
                Matrix4x4.CreateFromQuaternion(q));
            pos += new Vector3(_heldOffsetX, _heldOffsetY, _heldOffsetZ);

            // Instance data
            float atlasW = _selectedFromItemsAtlas ? _itemsAtlasWidth : _atlasWidth;
            float atlasH = _selectedFromItemsAtlas ? _itemsAtlasHeight : _atlasHeight;
            float tx = _selectedTile.X / atlasW;
            float ty = _selectedTile.Y / atlasH;
            float tw = _selectedTile.Width / atlasW;
            float th = _selectedTile.Height / atlasH;

            var data = new float[13 * HeldSlices];
            for (int s = 0; s < HeldSlices; s++)
            {
                int f = s * 13;
                data[f++] = pos.X; data[f++] = pos.Y; data[f++] = pos.Z;
                data[f++] = tx; data[f++] = ty; data[f++] = tw; data[f++] = th;
                data[f++] = q.X; data[f++] = q.Y; data[f++] = q.Z; data[f++] = q.W;
                data[f++] = scale;
                data[f++] = _isBlock ? 0f : _heldThickness;
            }
            _gd.UpdateBuffer(_heldInstanceBuffer, 0, data);

            bool sprite = !_isBlock;
            var textureSet = sprite ? _itemsTextureSet : _terrainTextureSet;
            var pipeline = sprite ? _heldItemPipeline : _heldBlockPipeline;
            var vb = sprite ? _heldCardVertexBuffer : _itemDropVertexBuffer;
            var ib = sprite ? _heldCardIndexBuffer : _itemDropIndexBuffer;
            uint indexCount = sprite ? 6u : (uint)FallingCubeIndices;
            uint instanceCount = sprite ? (uint)HeldSlices : 1u;

            cl.SetPipeline(pipeline);
            cl.SetGraphicsResourceSet(0, _handProjSet);
            cl.SetGraphicsResourceSet(1, textureSet);
            cl.SetVertexBuffer(0, vb);
            cl.SetVertexBuffer(1, _heldInstanceBuffer, 0);
            cl.SetIndexBuffer(ib, IndexFormat.UInt16);
            cl.DrawIndexed(indexCount, instanceCount, 0, 0, 0);
        }

        /// <summary>
        /// The vanilla first-person swing, split out so hand mesh and held item share it:
        /// <paramref name="grip"/> is the fist (idle pose + swing bob), <paramref name="qFist"/>
        /// is the attack rotation around it, <paramref name="bob"/> the assembly drift.
        /// </summary>
        private static void ComputeFist(float swing, out Vector3 grip, out Quaternion qFist, out Vector3 bob)
        {
            // Fist through the idle pose (mirror of the game's ArmToCamera).
            float x = _gripX * _handScale, y = _gripY * _handScale, z = _gripZ * _handScale;
            float cp = MathF.Cos(_handBasePitch), sp = MathF.Sin(_handBasePitch);
            float cy = MathF.Cos(_handBaseYaw), syy = MathF.Sin(_handBaseYaw);
            float xf = -x, yf = -y, zf = z;
            float y1 = yf * cp - zf * sp, z1 = yf * sp + zf * cp;
            float x2 = xf * cy + z1 * syy, z2 = -xf * syy + z1 * cy;
            Vector3 anchor = new(x2 + _handSx, y1 + _handSy, z2 + _handSz);

            // Vanilla swing bob: the whole arm+item assembly drifts together.
            float s2 = MathF.Max(swing, 0f);
            float f5 = MathF.Sin(MathF.Sqrt(s2) * MathF.PI);
            bob = new Vector3(
                -0.4f * f5,
                0.2f * MathF.Sin(MathF.Sqrt(s2) * MathF.PI * 2f),
                -0.2f * MathF.Sin(s2 * MathF.PI));
            grip = anchor + bob;

            // Attack chain in vanilla mulPose order: yaw1 -> roll -> pitch -> yaw2 (right hand).
            // Multiply(A, B) rotates by B first here, so yaw1 is the innermost.
            const float D2R = MathF.PI / 180f;
            float f4 = MathF.Sin(s2 * s2 * MathF.PI);
            qFist = Quaternion.CreateFromYawPitchRoll((45f - f4 * 20f) * D2R, 0f, 0f);
            qFist = Quaternion.Multiply(Quaternion.CreateFromYawPitchRoll(0f, 0f, -20f * f5 * D2R), qFist);
            qFist = Quaternion.Multiply(Quaternion.CreateFromYawPitchRoll(0f, -80f * f5 * D2R, 0f), qFist);
            qFist = Quaternion.Multiply(Quaternion.CreateFromYawPitchRoll(-45f * D2R, 0f, 0f), qFist);
        }

        /// <summary>
        /// Reposes the static arm cube exactly like the game's per-frame vertex pass:
        /// idle pose around the shoulder, swing bob, then the chop rotates every vertex
        /// around the fist.
        /// </summary>
        private static void TransformHandMesh(Vector3 grip, Quaternion qFist, Vector3 bob)
        {
            if (_handMesh.Length == 0 || _handVertexBuffer == null) return;
            var verts = new float[_handMesh.Length];
            float cp = MathF.Cos(_handBasePitch), sp = MathF.Sin(_handBasePitch);
            float cy = MathF.Cos(_handBaseYaw), syy = MathF.Sin(_handBaseYaw);
            for (int i = 0; i < _handMesh.Length; i += 8)
            {
                float x = _handMesh[i] * _handScale;
                float y = _handMesh[i + 1] * _handScale;
                float z = _handMesh[i + 2] * _handScale;
                float xf = -x, yf = -y, zf = z;
                float y1 = yf * cp - zf * sp, z1 = yf * sp + zf * cp;
                float x2 = xf * cy + z1 * syy, z2 = -xf * syy + z1 * cy;
                var v = new Vector3(x2 + _handSx + bob.X, y1 + _handSy + bob.Y, z2 + _handSz + bob.Z);
                v = grip + Vector3.Transform(v - grip, qFist);
                verts[i] = v.X; verts[i + 1] = v.Y; verts[i + 2] = v.Z;
                for (int k = 3; k < 8; k++) verts[i + k] = _handMesh[i + k];
            }
            _gd.UpdateBuffer(_handVertexBuffer, 0, verts);
        }

        // Simple TextureRect for this tool
        private struct TextureRect { public int X, Y, Width, Height; public TextureRect(int x, int y, int w, int h) { X=x; Y=y; Width=w; Height=h; } }
    }
}