using System;
using System.Collections.Generic;
using Veldrid;
using Veldrid.SPIRV;

namespace Cubuild.Renderer
{
    public sealed partial class VeldridRenderer
    {
        private const PixelFormat HzbPixelFormat = PixelFormat.R32_Float;

        private bool _hzbPipelinesSupported;
        private bool _hzbTargetsReady;
        private string _hzbUnavailableReason = "not initialized";
        private uint _hzbMaxWidth;
        private uint _hzbMaxHeight;
        private uint _hzbMaxMipLevels;
        private uint _hzbRenderWidth;
        private uint _hzbRenderHeight;
        private uint _hzbTextureWidth;
        private uint _hzbTextureHeight;

        private Texture? _hzbTexture;
        private TextureView? _hzbFullView;
        private Texture? _hzbPrepassDepthTexture;
        private Framebuffer? _hzbPrepassFramebuffer;
        private Sampler? _hzbSampler;
        private ResourceLayout? _hzbReduceSourceLayout;
        private ResourceLayout? _hzbReduceTargetLayout;
        private ResourceLayout? _hzbCullLayout;
        private Pipeline? _hzbReducePipeline;
        private Pipeline? _hzbCullPipeline;
        private Pipeline? _hzbDepthPrepassPipeline;
        private ResourceSet? _hzbCullResourceSet;
        private readonly List<TextureView> _hzbMipViews = new();
        private readonly List<ResourceSet> _hzbMipSourceSets = new();
        private readonly List<ResourceSet> _hzbMipTargetSets = new();

        /// <summary>
        /// Creates HZB-only pipelines after the ordinary frustum culler. Failure here must not
        /// disable or alter the existing GPU frustum path.
        /// </summary>
        private void InitializeHzbComputePipelines()
        {
            try
            {
                if (!_gd.Features.ComputeShader || !_gd.Features.StructuredBuffer || !_gd.Features.DrawIndirect)
                {
                    _hzbUnavailableReason = "GPU frustum culling is unsupported";
                    return;
                }
                if (!_gd.IsDepthRangeZeroToOne)
                {
                    _hzbUnavailableReason = "backend depth range is not zero-to-one";
                    return;
                }

                TextureUsage requiredUsage = TextureUsage.RenderTarget | TextureUsage.Sampled | TextureUsage.Storage;
                if (!_gd.GetPixelFormatSupport(HzbPixelFormat, TextureType.Texture2D, requiredUsage,
                        out PixelFormatProperties properties))
                {
                    _hzbUnavailableReason = "R32_Float RenderTarget|Sampled|Storage format unsupported";
                    return;
                }
                if (!_gd.GetPixelFormatSupport(HzbPixelFormat, TextureType.Texture2D,
                        TextureUsage.Sampled | TextureUsage.Storage))
                {
                    _hzbUnavailableReason = "R32_Float sampled/storage compute format unsupported";
                    return;
                }

                _hzbMaxWidth = properties.MaxWidth;
                _hzbMaxHeight = properties.MaxHeight;
                _hzbMaxMipLevels = properties.MaxMipLevels;
                CreateHzbComputePipelines();
                _hzbPipelinesSupported = true;
                _hzbUnavailableReason = "render targets not created";
            }
            catch (Exception ex)
            {
                DisposeHzbPipelines();
                _hzbPipelinesSupported = false;
                _hzbUnavailableReason = $"pipeline creation failed ({ex.GetType().Name})";
                Console.Error.WriteLine($"[HZB] compute pipeline unavailable: {ex}");
            }
        }

        private void CreateHzbComputePipelines()
        {
            var factory = _gd.ResourceFactory;
            _hzbSampler = factory.CreateSampler(SamplerDescription.Point);

            _hzbReduceSourceLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("SourceDepth", ResourceKind.TextureReadOnly, ShaderStages.Compute),
                new ResourceLayoutElementDescription("SourceSampler", ResourceKind.Sampler, ShaderStages.Compute)));
            _hzbReduceTargetLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("TargetDepth", ResourceKind.TextureReadWrite, ShaderStages.Compute)));
            _hzbCullLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("DepthPyramid", ResourceKind.TextureReadOnly, ShaderStages.Compute),
                new ResourceLayoutElementDescription("DepthSampler", ResourceKind.Sampler, ShaderStages.Compute)));

            const string reduceShader = @"#version 450
layout(local_size_x=8, local_size_y=8) in;
layout(set=0, binding=0) uniform sampler2D sourceDepth;
layout(r32f, set=1, binding=0) uniform writeonly image2D targetDepth;
void main() {
    ivec2 dst = ivec2(gl_GlobalInvocationID.xy);
    ivec2 dstSize = imageSize(targetDepth);
    if (dst.x >= dstSize.x || dst.y >= dstSize.y) return;
    ivec2 srcSize = textureSize(sourceDepth, 0);
    ivec2 srcStart = dst * srcSize / dstSize;
    ivec2 srcEnd = (dst + ivec2(1)) * srcSize / dstSize;
    float farthest = 0.0;
    for (int y = srcStart.y; y < srcEnd.y; y++) {
        for (int x = srcStart.x; x < srcEnd.x; x++) {
            farthest = max(farthest, texelFetch(sourceDepth, ivec2(x, y), 0).r);
        }
    }
    imageStore(targetDepth, dst, vec4(farthest, 0.0, 0.0, 1.0));
}";

            const string hzbCullShader = @"#version 450
layout(local_size_x=64) in;
layout(set=0, binding=0, std140) uniform CullParams {
    vec4 planes[6];
    uvec4 params; // command count, flags (bit0=HZB, bit1=screen-Y flip), render width, height
    mat4 projView;
};
layout(set=1, binding=0) readonly buffer ChunkCullData { uint data[]; } chunkData;
layout(set=1, binding=1) buffer IndirectArgs { uint args[]; } indirectArgs;
layout(set=2, binding=0) uniform sampler2D depthPyramid;
const uint CMD_STRIDE = 5u;
const float DEPTH_SAFETY_BIAS = 0.0001;
bool allFinite(vec4 v) { return !any(isnan(v)) && !any(isinf(v)); }

bool hzbOccluded(vec3 lo, vec3 hi) {
    uint width = params.z;
    uint height = params.w;
    if (width == 0u || height == 0u) return false;

    vec2 ndcLo = vec2(1e30);
    vec2 ndcHi = vec2(-1e30);
    float nearestDepth = 1.0;
    for (int corner = 0; corner < 8; corner++) {
        vec3 p = vec3((corner & 1) != 0 ? hi.x : lo.x,
                      (corner & 2) != 0 ? hi.y : lo.y,
                      (corner & 4) != 0 ? hi.z : lo.z);
        vec4 clip = projView * vec4(p, 1.0);
        // Boxes touching/crossing near/far planes or producing invalid projection data fail open.
        if (!allFinite(clip) || clip.w <= 1e-6 || clip.z <= 1e-6 || clip.z >= clip.w) return false;
        float invW = 1.0 / clip.w;
        vec2 ndc = clip.xy * invW;
        if (any(isnan(ndc)) || any(isinf(ndc))) return false;
        ndcLo = min(ndcLo, ndc);
        ndcHi = max(ndcHi, ndc);
        nearestDepth = min(nearestDepth, clip.z * invW);
    }

    // Do not attempt to clip a projected rectangle at the screen edge: any off-screen or
    // ambiguous projection remains visible.
    if (ndcLo.x < -1.0 || ndcLo.y < -1.0 || ndcHi.x > 1.0 || ndcHi.y > 1.0) return false;
    vec2 uvLo = vec2(ndcLo.x * 0.5 + 0.5, 0.0);
    vec2 uvHi = vec2(ndcHi.x * 0.5 + 0.5, 0.0);
    if ((params.y & 2u) != 0u) {
        uvLo.y = 0.5 - ndcHi.y * 0.5;
        uvHi.y = 0.5 - ndcLo.y * 0.5;
    } else {
        uvLo.y = ndcLo.y * 0.5 + 0.5;
        uvHi.y = ndcHi.y * 0.5 + 0.5;
    }
    vec2 pixelScale = vec2(float(width), float(height));
    ivec2 pLo = ivec2(floor(uvLo * pixelScale)) - ivec2(1);
    ivec2 pHi = ivec2(floor(uvHi * pixelScale)) + ivec2(1);
    pLo = clamp(pLo, ivec2(0), ivec2(int(width) - 1, int(height) - 1));
    pHi = clamp(pHi, ivec2(0), ivec2(int(width) - 1, int(height) - 1));

    int mip = 0;
    int covered = max(pHi.x - pLo.x + 1, pHi.y - pLo.y + 1);
    while (covered > 8 && mip < 30) {
        covered = (covered + 1) / 2;
        mip++;
    }
    ivec2 texelLo = pLo;
    ivec2 texelHi = pHi;
    for (int level = 0; level < mip; level++) {
        ivec2 srcSize = textureSize(depthPyramid, level);
        ivec2 dstSize = textureSize(depthPyramid, level + 1);
        // Match the max-reduction's exact proportional partitioning, including odd dimensions.
        texelLo = ((texelLo + ivec2(1)) * dstSize + srcSize - ivec2(1)) / srcSize - ivec2(1);
        texelHi = ((texelHi + ivec2(1)) * dstSize + srcSize - ivec2(1)) / srcSize - ivec2(1);
    }
    float blockerDepthLimit = nearestDepth - DEPTH_SAFETY_BIAS;
    for (int y = texelLo.y; y <= texelHi.y; y++) {
        for (int x = texelLo.x; x <= texelHi.x; x++) {
            float maxDepth = texelFetch(depthPyramid, ivec2(x, y), mip).r;
            // Clear depth (1.0) and any non-finite sample make the candidate visible.
            if (isnan(maxDepth) || isinf(maxDepth) || !(maxDepth < blockerDepthLimit)) return false;
        }
    }
    return true;
}

void main() {
    uint gi = gl_GlobalInvocationID.x;
    if (gi >= params.x) return;
    uint base = gi * 16u;
    vec3 lo = vec3(uintBitsToFloat(chunkData.data[base + 0u]),
                   uintBitsToFloat(chunkData.data[base + 1u]),
                   uintBitsToFloat(chunkData.data[base + 2u]));
    vec3 hi = vec3(uintBitsToFloat(chunkData.data[base + 4u]),
                   uintBitsToFloat(chunkData.data[base + 5u]),
                   uintBitsToFloat(chunkData.data[base + 6u]));
    bool visible = true;
    for (int p = 0; p < 6 && visible; p++) {
        vec4 plane = planes[p];
        vec3 positive = vec3(plane.x >= 0.0 ? hi.x : lo.x,
                             plane.y >= 0.0 ? hi.y : lo.y,
                             plane.z >= 0.0 ? hi.z : lo.z);
        if (dot(plane.xyz, positive) + plane.w < 0.0) visible = false;
    }
    if (visible && (params.y & 1u) != 0u && chunkData.data[base + 13u] != 0u)
        visible = !hzbOccluded(lo, hi);

    uint outBase = gi * CMD_STRIDE;
    indirectArgs.args[outBase + 0u] = chunkData.data[base + 8u];
    indirectArgs.args[outBase + 1u] = visible ? chunkData.data[base + 9u] : 0u;
    indirectArgs.args[outBase + 2u] = chunkData.data[base + 10u];
    indirectArgs.args[outBase + 3u] = chunkData.data[base + 11u];
    indirectArgs.args[outBase + 4u] = chunkData.data[base + 12u];
}";

            var reduceSpirv = SpirvCompilation.CompileGlslToSpirv(reduceShader, "main", ShaderStages.Compute, GlslCompileOptions.Default);
            var reduceCs = factory.CreateFromSpirv(new ShaderDescription(ShaderStages.Compute, reduceSpirv.SpirvBytes, "main"));
            _hzbReducePipeline = factory.CreateComputePipeline(new ComputePipelineDescription(
                reduceCs,
                new[] { _hzbReduceSourceLayout, _hzbReduceTargetLayout },
                8, 8, 1));

            var cullSpirv = SpirvCompilation.CompileGlslToSpirv(hzbCullShader, "main", ShaderStages.Compute, GlslCompileOptions.Default);
            var cullCs = factory.CreateFromSpirv(new ShaderDescription(ShaderStages.Compute, cullSpirv.SpirvBytes, "main"));
            _hzbCullPipeline = factory.CreateComputePipeline(new ComputePipelineDescription(
                cullCs,
                new[] { _cullDataLayout, _cullChunkLayout, _hzbCullLayout },
                64, 1, 1));
        }

        /// <summary>Rebuilds same-frame depth/HZB targets at the active world-render resolution.</summary>
        private void RecreateHzbTargets(uint renderWidth, uint renderHeight, PixelFormat depthFormat)
        {
            DisposeHzbTargets();
            if (!_hzbPipelinesSupported || renderWidth == 0 || renderHeight == 0)
            {
                if (_hzbPipelinesSupported) _hzbUnavailableReason = "invalid render-target size";
                return;
            }

            uint mipLevels = 1u + (uint)Log2(Math.Max(renderWidth, renderHeight));
            if (renderWidth > _hzbMaxWidth || renderHeight > _hzbMaxHeight || mipLevels > _hzbMaxMipLevels)
            {
                _hzbUnavailableReason = "render size exceeds R32_Float texture limits";
                return;
            }
            if (!_gd.GetPixelFormatSupport(depthFormat, TextureType.Texture2D, TextureUsage.DepthStencil))
            {
                _hzbUnavailableReason = "prepass depth format unsupported";
                return;
            }

            try
            {
                _hzbRenderWidth = renderWidth;
                _hzbRenderHeight = renderHeight;
                _hzbTextureWidth = renderWidth;
                _hzbTextureHeight = renderHeight;
                TextureUsage usage = TextureUsage.RenderTarget | TextureUsage.Sampled | TextureUsage.Storage;
                _hzbTexture = _gd.ResourceFactory.CreateTexture(TextureDescription.Texture2D(
                    renderWidth, renderHeight, mipLevels, 1, HzbPixelFormat, usage));
                _hzbFullView = _gd.ResourceFactory.CreateTextureView(new TextureViewDescription(
                    _hzbTexture, 0, mipLevels, 0, 1));
                _hzbPrepassDepthTexture = _gd.ResourceFactory.CreateTexture(TextureDescription.Texture2D(
                    renderWidth, renderHeight, 1, 1, depthFormat, TextureUsage.DepthStencil));
                _hzbPrepassFramebuffer = _gd.ResourceFactory.CreateFramebuffer(new FramebufferDescription(
                    new FramebufferAttachmentDescription(_hzbPrepassDepthTexture, 0, 0),
                    new[] { new FramebufferAttachmentDescription(_hzbTexture, 0, 0) }));

                _hzbCullResourceSet = _gd.ResourceFactory.CreateResourceSet(new ResourceSetDescription(
                    _hzbCullLayout!, _hzbFullView, _hzbSampler));
                for (uint level = 1; level < mipLevels; level++)
                {
                    var sourceView = _gd.ResourceFactory.CreateTextureView(new TextureViewDescription(
                        _hzbTexture, level - 1, 1, 0, 1));
                    var targetView = _gd.ResourceFactory.CreateTextureView(new TextureViewDescription(
                        _hzbTexture, level, 1, 0, 1));
                    _hzbMipViews.Add(sourceView);
                    _hzbMipViews.Add(targetView);
                    _hzbMipSourceSets.Add(_gd.ResourceFactory.CreateResourceSet(new ResourceSetDescription(
                        _hzbReduceSourceLayout!, sourceView, _hzbSampler)));
                    _hzbMipTargetSets.Add(_gd.ResourceFactory.CreateResourceSet(new ResourceSetDescription(
                        _hzbReduceTargetLayout!, targetView)));
                }

                if (_hzbDepthPrepassPipeline == null)
                    _hzbDepthPrepassPipeline = CreateHzbDepthPrepassPipeline(_hzbPrepassFramebuffer.OutputDescription);
                _hzbTargetsReady = true;
                _hzbUnavailableReason = string.Empty;
            }
            catch (Exception ex)
            {
                DisposeHzbTargets();
                _hzbUnavailableReason = $"resource creation failed ({ex.GetType().Name})";
                Console.Error.WriteLine($"[HZB] targets unavailable: {ex}");
            }
        }

        private Pipeline CreateHzbDepthPrepassPipeline(OutputDescription outputDescription)
        {
            var factory = _gd.ResourceFactory;
            const string vertexShader = @"#version 450
layout(location=0) in vec3 aPosition;
layout(location=4) in ivec3 aOrigin;
layout(location=0) out vec3 vWorldPos;
layout(set=0, binding=0) uniform ProjectionView { mat4 projView; ivec4 renderOrigin; };
void main() {
    vec3 relativePos = aPosition + vec3(aOrigin - renderOrigin.xyz);
    vWorldPos = relativePos;
    gl_Position = projView * vec4(relativePos, 1.0);
}";
            const string fragmentShader = @"#version 450
layout(location=0) in vec3 vWorldPos;
layout(set=1, binding=0) uniform FogParams {
    vec4 fogColor;
    vec2 fogRange;
    vec2 lightParams;
    vec4 cameraPos;
    vec4 hiddenCell;
};
layout(location=0) out float outDepth;
void main() {
    // This is the same inclusive mining-cell discard as the opaque color shader. Mined-away
    // geometry must not become an occluder in the current frame's depth source.
    if (hiddenCell.w > 0.5 &&
        vWorldPos.x >= hiddenCell.x - 0.002 && vWorldPos.x <= hiddenCell.x + 1.002 &&
        vWorldPos.y >= hiddenCell.y - 0.002 && vWorldPos.y <= hiddenCell.y + 1.002 &&
        vWorldPos.z >= hiddenCell.z - 0.002 && vWorldPos.z <= hiddenCell.z + 1.002) discard;
    outDepth = gl_FragCoord.z;
}";
            var vsSpirv = SpirvCompilation.CompileGlslToSpirv(vertexShader, "main", ShaderStages.Vertex, GlslCompileOptions.Default);
            var fsSpirv = SpirvCompilation.CompileGlslToSpirv(fragmentShader, "main", ShaderStages.Fragment, GlslCompileOptions.Default);
            var shaders = factory.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Vertex, vsSpirv.SpirvBytes, "main"),
                new ShaderDescription(ShaderStages.Fragment, fsSpirv.SpirvBytes, "main"));

            var vertexLayout = new VertexLayoutDescription(
                new VertexElementDescription("aPosition", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                new VertexElementDescription("aPack1", VertexElementSemantic.TextureCoordinate, VertexElementFormat.UInt1),
                new VertexElementDescription("aPack2", VertexElementSemantic.TextureCoordinate, VertexElementFormat.UInt1),
                new VertexElementDescription("aPack3", VertexElementSemantic.TextureCoordinate, VertexElementFormat.UInt1),
                new VertexElementDescription("aOrigin", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Int3));
            var shaderSet = new ShaderSetDescription(new[] { vertexLayout }, new[] { shaders[0], shaders[1] });
            return factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
            {
                BlendState = BlendStateDescription.SingleDisabled,
                DepthStencilState = new DepthStencilStateDescription(true, true, ComparisonKind.LessEqual),
                RasterizerState = new RasterizerStateDescription(FaceCullMode.Back, PolygonFillMode.Solid,
                    FrontFace.CounterClockwise, true, false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _projViewLayout, _fogLayout },
                ShaderSet = shaderSet,
                Outputs = outputDescription,
            });
        }

        /// <summary>
        /// Records prepass, max-reduction pyramid, and framebuffer restoration on this frame's
        /// command list. The prepass compacts opaque commands using only CPU frustum culling.
        /// </summary>
        private bool RecordHzbDepthAndPyramid(CommandList cl, Framebuffer worldFramebuffer)
        {
            if (!_hzbTargetsReady || _hzbPrepassFramebuffer == null || _hzbDepthPrepassPipeline == null
                || _hzbTexture == null || _megaVertexBuffer == null || _megaIndexBuffer == null)
            {
                cl.SetFramebuffer(worldFramebuffer);
                cl.SetFullViewport(0);
                return false;
            }

            try
            {
                cl.SetFramebuffer(_hzbPrepassFramebuffer);
                cl.SetFullViewport(0);
                cl.SetViewport(0, new Viewport(0, 0, _hzbRenderWidth, _hzbRenderHeight, 0, 1));
                // Clear to far depth so uncovered pixels can never block.
                cl.ClearColorTarget(0, new RgbaFloat(1f, 0f, 0f, 1f));
                cl.ClearDepthStencil(1f);

                uint prepassDrawCount = CullDrawCommands(_drawCommands, _indirectScratch);
                if (prepassDrawCount > 0)
                {
                    EnsureIndirectCapacity(prepassDrawCount);
                    cl.UpdateBuffer(_indirectBuffer!, 0, ref _indirectScratch[0], prepassDrawCount * IndirectCommandStride);
                    cl.SetPipeline(_hzbDepthPrepassPipeline);
                    cl.SetGraphicsResourceSet(0, _projViewSet);
                    cl.SetGraphicsResourceSet(1, _fogSet);
                    cl.SetVertexBuffer(0, _megaVertexBuffer);
                    cl.SetIndexBuffer(_megaIndexBuffer, IndexFormat.UInt16);
                    cl.DrawIndexedIndirect(_indirectBuffer!, 0, prepassDrawCount, IndirectCommandStride);
                }

                cl.SetPipeline(_hzbReducePipeline!);
                for (int i = 0; i < _hzbMipSourceSets.Count; i++)
                {
                    uint mipWidth = Math.Max(1u, _hzbTextureWidth >> (i + 1));
                    uint mipHeight = Math.Max(1u, _hzbTextureHeight >> (i + 1));
                    cl.SetComputeResourceSet(0, _hzbMipSourceSets[i]);
                    cl.SetComputeResourceSet(1, _hzbMipTargetSets[i]);
                    cl.Dispatch((mipWidth + 7) / 8, (mipHeight + 7) / 8, 1);
                }

                cl.SetFramebuffer(worldFramebuffer);
                cl.SetFullViewport(0);
                return true;
            }
            catch (Exception ex)
            {
                _hzbTargetsReady = false;
                _hzbUnavailableReason = $"frame recording failed ({ex.GetType().Name})";
                Console.Error.WriteLine($"[HZB] disabled for frame: {ex}");
                try
                {
                    cl.SetFramebuffer(worldFramebuffer);
                    cl.SetFullViewport(0);
                }
                catch { }
                return false;
            }
        }

        private string GetHzbDiagnosticStatus()
        {
            if (_cullMode != CullingMode.Gpu)
                return "inactive (Auto/CPU mode; frustum-only)";
            if (!_gpuCullEnabled)
                return "unavailable (GPU culling unsupported; frustum-only)";
            if (!_hzbPipelinesSupported || !_hzbTargetsReady)
                return $"unavailable ({(_hzbUnavailableReason.Length == 0 ? "resource unavailable" : _hzbUnavailableReason)}; frustum-only)";
            return "active (explicit GPU; same-frame opaque depth prepass; no per-chunk readback)";
        }

        private static int Log2(uint value)
        {
            int result = 0;
            while (value > 1) { value >>= 1; result++; }
            return result;
        }

        private void DisposeHzbTargets()
        {
            _hzbTargetsReady = false;
            _hzbCullResourceSet?.Dispose();
            _hzbCullResourceSet = null;
            foreach (var set in _hzbMipSourceSets) set.Dispose();
            foreach (var set in _hzbMipTargetSets) set.Dispose();
            _hzbMipSourceSets.Clear();
            _hzbMipTargetSets.Clear();
            foreach (var view in _hzbMipViews) view.Dispose();
            _hzbMipViews.Clear();
            _hzbPrepassFramebuffer?.Dispose();
            _hzbPrepassFramebuffer = null;
            _hzbFullView?.Dispose();
            _hzbFullView = null;
            _hzbPrepassDepthTexture?.Dispose();
            _hzbPrepassDepthTexture = null;
            _hzbTexture?.Dispose();
            _hzbTexture = null;
        }

        private void DisposeHzbPipelines()
        {
            DisposeHzbTargets();
            _hzbDepthPrepassPipeline?.Dispose();
            _hzbDepthPrepassPipeline = null;
            _hzbReducePipeline?.Dispose();
            _hzbReducePipeline = null;
            _hzbCullPipeline?.Dispose();
            _hzbCullPipeline = null;
            _hzbReduceSourceLayout?.Dispose();
            _hzbReduceSourceLayout = null;
            _hzbReduceTargetLayout?.Dispose();
            _hzbReduceTargetLayout = null;
            _hzbCullLayout?.Dispose();
            _hzbCullLayout = null;
            _hzbSampler?.Dispose();
            _hzbSampler = null;
        }
    }
}
