using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using Veldrid;
using Veldrid.SPIRV;

namespace Cubuild.Renderer
{
    public sealed partial class VeldridRenderer : IRenderer, IDisposable
    {
        private void CreatePipeline()
        {
            var factory = _gd.ResourceFactory;
            // Packed chunk vertex format (24 bytes vs the old 52): the vertex shader decodes
            // everything back into the same varyings, so the fragment shaders are unchanged.
            //   aPosition  : world-space Float3 (12 bytes)
            //   aPack1     : du/dv as 8.8 fixed point (uint16 each)
            //   aPack2     : tile rect as 4x uint8 ATLAS TEXELS (x,y,w,h)
            //   aPack3     : shade(8) | alphaByte(8) | alphaMode(2) | pad(14)
            //                alphaMode 0 = real alpha (opaque/water), 1 = glass frame (-100
            //                sentinel), 2 = translucent glass tint (-200 sentinel).
            // Atlas size is baked in (atlas loads before CreatePipeline) so the texel decode is exact.
            float atlasW = Math.Max(1f, _atlasWidth);
            float atlasH = Math.Max(1f, _atlasHeight);
            string vsCode = $@"#version 450
layout(location=0) in vec3 aPosition;
layout(location=1) in uint aPack1;
layout(location=2) in uint aPack2;
layout(location=3) in uint aPack3;
layout(location=0) out vec2 vLocalUV;
layout(location=1) out vec4 vTileRect;
layout(location=2) out vec4 vColor;
layout(location=3) out vec3 vWorldPos;
layout(set=0, binding=0) uniform ProjectionView {{ mat4 projView; }};
const float ATLAS_INV_X = 1.0 / {atlasW.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}.0;
const float ATLAS_INV_Y = 1.0 / {atlasH.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}.0;
void main() {{
    // localUV: 8.8 fixed point (block units; fract() in the fragment shader tiles it).
    float du = float((aPack1 >> 16) & 0xFFFFu) / 256.0;
    float dv = float(aPack1 & 0xFFFFu) / 256.0;
    vLocalUV = vec2(du, dv);
    // tileRect: 4x uint8 atlas texel coords -> atlas UV space.
    float tx = float((aPack2 >> 24) & 0xFFu) * ATLAS_INV_X;
    float ty = float((aPack2 >> 16) & 0xFFu) * ATLAS_INV_Y;
    float tw = float((aPack2 >> 8) & 0xFFu) * ATLAS_INV_X;
    float th = float(aPack2 & 0xFFu) * ATLAS_INV_Y;
    vTileRect = vec4(tx, ty, tw, th);
    // color: shade(8) replicated to rgb, alpha decoded per alphaMode so the glass sentinels
    // (-100 regular frame, -200 translucent tint) survive exactly for the tint pass.
    float shade = float(aPack3 & 0xFFu) / 255.0;
    uint alphaByte = (aPack3 >> 8) & 0xFFu;
    uint mode = (aPack3 >> 16) & 0x3u;
    float alpha = mode == 0u ? float(alphaByte) / 255.0 : (mode == 1u ? -100.0 : -200.0);
    vColor = vec4(shade, shade, shade, alpha);
    vWorldPos = aPosition;
    gl_Position = projView * vec4(aPosition, 1.0);
}}";

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
    vec4 hiddenCell; // xyz = mining cell (floor), w = 1 when a block is being hidden
};
layout(location=0) out vec4 outColor;
void main() {
    // Discard the mining cell with a tiny epsilon past the boundary. Exact bounds let neighbor
    // boundary faces (float-precision fragments landing a hair outside the cell) survive and
    // z-fight the cube. 0.01 was still marginally visible - trying 0.002 (~0.13 px on a 16px
    // tile, should be invisible while still swallowing the surviving boundary sliver).
    if (hiddenCell.w > 0.5) {
        if (vWorldPos.x >= hiddenCell.x - 0.002 && vWorldPos.x <= hiddenCell.x + 1.002 &&
            vWorldPos.y >= hiddenCell.y - 0.002 && vWorldPos.y <= hiddenCell.y + 1.002 &&
            vWorldPos.z >= hiddenCell.z - 0.002 && vWorldPos.z <= hiddenCell.z + 1.002) {
            discard;
        }
    }
    // fract() tiles the same atlas tile regardless of how many blocks the face spans.
    vec2 atlasUV = fract(vLocalUV) * vTileRect.zw + vTileRect.xy;
    vec4 tex = texture(uAtlas, atlasUV);
    // Block alpha (vColor.a) governs opacity - transparent tiles (water) are tinted by their
    // configured alpha; opaque blocks (alpha 1) sample the tile fully.
    outColor = vec4(tex.rgb * vColor.rgb, vColor.a);
    // Linear distance fog: fully fogged at fogRange.y, clear at fogRange.x.
    float dist = length(vWorldPos - cameraPos.xyz);
    float fog = clamp((fogRange.y - dist) / max(fogRange.y - fogRange.x, 1e-5), 0.0, 1.0);
    outColor.rgb = mix(fogColor.rgb, outColor.rgb, fog);
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
                new VertexElementDescription("aPack1", VertexElementSemantic.TextureCoordinate, VertexElementFormat.UInt1),
                new VertexElementDescription("aPack2", VertexElementSemantic.TextureCoordinate, VertexElementFormat.UInt1),
                new VertexElementDescription("aPack3", VertexElementSemantic.TextureCoordinate, VertexElementFormat.UInt1));

            var shaderSet = new ShaderSetDescription(new[] { vertexLayout }, new[] { vs, fs });

            // create texture resource layout (set 1)
            _textureLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("uAtlas", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("uAtlasSampler", ResourceKind.Sampler, ShaderStages.Fragment)));

            // Distance fog uniform block (set 2), shared by all world pipelines.
            _fogLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("FogParams", ResourceKind.UniformBuffer, ShaderStages.Fragment)));
            _fogBuffer = factory.CreateBuffer(new BufferDescription(64, BufferUsage.UniformBuffer | BufferUsage.Dynamic));
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
    vec4 hiddenCell;
};
layout(location=0) out vec4 outColor;
void main() {
    // Discard the mining cell with a tiny epsilon past the boundary (matches the opaque shader):
    // exact bounds let neighbor boundary faces survive and z-fight the shrink cube; 0.002 is
    // invisible (~0.13 px on a 16px tile) but swallows the surviving boundary sliver.
    if (hiddenCell.w > 0.5) {
        if (vWorldPos.x >= hiddenCell.x - 0.002 && vWorldPos.x <= hiddenCell.x + 1.002 &&
            vWorldPos.y >= hiddenCell.y - 0.002 && vWorldPos.y <= hiddenCell.y + 1.002 &&
            vWorldPos.z >= hiddenCell.z - 0.002 && vWorldPos.z <= hiddenCell.z + 1.002) {
            discard;
        }
    }
    vec2 atlasUV = fract(vLocalUV) * vTileRect.zw + vTileRect.xy;
    vec4 tex = texture(uAtlas, atlasUV);
    if (tex.a < 0.5) discard; // sprite background falls away; no blending
    outColor = vec4(tex.rgb * vColor.rgb, 1.0);
    float dist = length(vWorldPos - cameraPos.xyz);
    float fog = clamp((fogRange.y - dist) / max(fogRange.y - fogRange.x, 1e-5), 0.0, 1.0);
    outColor.rgb = mix(fogColor.rgb, outColor.rgb, fog);
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
    vec4 hiddenCell;
};
layout(location=0) out vec4 outColor;
void main() {
    // Discard the mining cell with a tiny epsilon past the boundary (matches the opaque shader):
    // exact bounds let neighbor boundary faces survive and z-fight the shrink cube; 0.002 is
    // invisible (~0.13 px on a 16px tile) but swallows the surviving boundary sliver.
    if (hiddenCell.w > 0.5) {
        if (vWorldPos.x >= hiddenCell.x - 0.002 && vWorldPos.x <= hiddenCell.x + 1.002 &&
            vWorldPos.y >= hiddenCell.y - 0.002 && vWorldPos.y <= hiddenCell.y + 1.002 &&
            vWorldPos.z >= hiddenCell.z - 0.002 && vWorldPos.z <= hiddenCell.z + 1.002) {
            discard;
        }
    }
    if (vColor.a > -150.0) discard; // only translucent (colored) glass - sentinel ~ -200
    vec2 atlasUV = fract(vLocalUV) * vTileRect.zw + vTileRect.xy;
    vec4 tex = texture(uAtlas, atlasUV);
    if (tex.a >= 0.5) discard;      // opaque frame pixels already drawn by the glass pass
    outColor = vec4(tex.rgb * vColor.rgb, tex.a);
    float dist = length(vWorldPos - cameraPos.xyz);
    float fog = clamp((fogRange.y - dist) / max(fogRange.y - fogRange.x, 1e-5), 0.0, 1.0);
    outColor.rgb = mix(fogColor.rgb, outColor.rgb, fog);
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

            // Item drops: same pipeline idea but with a per-instance quaternion so dropped blocks
            // TUMBLE as they fall. Same vertex layout and resources as the falling pipeline; the
            // instance layout adds iRot and the vertex shader rotates the corner around the cube
            // center (half of ItemDropScale = 0.15 baked here).
            {
                string itemDropVsCode = @"#version 450
layout(location=0) in vec3 aCorner;
layout(location=1) in vec2 aLocalUV;
layout(location=2) in vec4 aShade;
layout(location=3) in vec3 iWorldPos;
layout(location=4) in vec4 iTileRect;
layout(location=5) in vec4 iRot;
layout(location=0) out vec2 vLocalUV;
layout(location=1) out vec4 vTileRect;
layout(location=2) out vec4 vColor;
layout(location=3) out vec3 vWorldPos;
layout(set=0, binding=0) uniform ProjectionView { mat4 projView; };
void main() {
    vec3 local = aCorner - vec3(0.15);
    vec3 qv = iRot.xyz;
    vec3 t = 2.0 * cross(qv, local);
    vec3 rotated = local + iRot.w * t + cross(qv, t);
    vec3 worldPos = rotated + iWorldPos;
    vLocalUV = aLocalUV;
    vTileRect = iTileRect;
    vColor = aShade;
    vWorldPos = worldPos;
    gl_Position = projView * vec4(worldPos, 1.0);
}";
                var itemDropVsSpirv = SpirvCompilation.CompileGlslToSpirv(itemDropVsCode, "main", ShaderStages.Vertex, GlslCompileOptions.Default);
                var itemDropShaders = factory.CreateFromSpirv(
                    new ShaderDescription(ShaderStages.Vertex, itemDropVsSpirv.SpirvBytes, "main"),
                    new ShaderDescription(ShaderStages.Fragment, fsSpirv.SpirvBytes, "main"));
                var itemDropInstanceLayout = new VertexLayoutDescription(
                    new VertexElementDescription("iWorldPos", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                    new VertexElementDescription("iTileRect", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4),
                    new VertexElementDescription("iRot", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4));
                itemDropInstanceLayout.InstanceStepRate = 1;
                var itemDropShaderSet = new ShaderSetDescription(new[] { fallingVertexLayout, itemDropInstanceLayout }, new[] { itemDropShaders[0], itemDropShaders[1] });
                _itemDropPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription()
                {
                    BlendState = BlendStateDescription.SingleAlphaBlend,
                    DepthStencilState = new DepthStencilStateDescription(true, true, ComparisonKind.LessEqual),
                    RasterizerState = new RasterizerStateDescription(FaceCullMode.Back, PolygonFillMode.Solid, FrontFace.CounterClockwise, true, false),
                    PrimitiveTopology = PrimitiveTopology.TriangleList,
                    ResourceLayouts = new[] { _projViewLayout, _textureLayout, _fogLayout },
                    ShaderSet = itemDropShaderSet,
                    Outputs = _sc.Framebuffer.OutputDescription
                });
            }

            // Genuine-item DROPS: flat camera-facing sprites (like the hotbar icons) instead of
            // tumbling cubes. Same instance data as the cube pass (center + tile rect + rotation;
            // the rotation is ignored). The vertex shader extracts the camera basis from projView
            // and offsets the quad along right/up so it always faces the camera. Half-size baked:
            // 0.175 -> 0.35 world units (a hair larger than the 0.3 drop cube, since a flat sprite
            // reads smaller than a cube).
            {
                string spriteVsCode = @"#version 450
layout(location=0) in vec3 aCorner;
layout(location=1) in vec2 aLocalUV;
layout(location=2) in vec4 aShade;
layout(location=3) in vec3 iWorldPos;
layout(location=4) in vec4 iTileRect;
layout(location=5) in vec4 iRot;
layout(location=0) out vec2 vLocalUV;
layout(location=1) out vec4 vTileRect;
layout(location=2) out vec4 vColor;
layout(location=3) out vec3 vWorldPos;
layout(set=0, binding=0) uniform ProjectionView { mat4 projView; };
void main() {
    // projView's top 3x3 rows are the camera basis scaled by the projection factors;
    // normalizing recovers the world-space right/up vectors.
    vec3 right = normalize(vec3(projView[0][0], projView[1][0], projView[2][0]));
    vec3 up    = normalize(vec3(projView[0][1], projView[1][1], projView[2][1]));
    vec3 local = aCorner * 0.175;
    vec3 worldPos = iWorldPos + right * local.x + up * local.y;
    vLocalUV = aLocalUV;
    vTileRect = iTileRect;
    vColor = aShade;
    vWorldPos = worldPos;
    gl_Position = projView * vec4(worldPos, 1.0);
}";
                var spriteVsSpirv = SpirvCompilation.CompileGlslToSpirv(spriteVsCode, "main", ShaderStages.Vertex, GlslCompileOptions.Default);
                var spriteShaders = factory.CreateFromSpirv(
                    new ShaderDescription(ShaderStages.Vertex, spriteVsSpirv.SpirvBytes, "main"),
                    // Cutout fragment shader: transparent texels (alpha < 0.5) are DISCARDED so
                    // item art with transparency doesn't leak its black RGB, and writes depth.
                    new ShaderDescription(ShaderStages.Fragment, cutoutFsSpirv.SpirvBytes, "main"));
                var spriteInstanceLayout = new VertexLayoutDescription(
                    new VertexElementDescription("iWorldPos", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                    new VertexElementDescription("iTileRect", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4),
                    new VertexElementDescription("iRot", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4));
                spriteInstanceLayout.InstanceStepRate = 1;
                _itemDropSpritePipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription()
                {
                    BlendState = BlendStateDescription.SingleAlphaBlend,
                    DepthStencilState = new DepthStencilStateDescription(true, true, ComparisonKind.LessEqual),
                    RasterizerState = new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.CounterClockwise, true, false),
                    PrimitiveTopology = PrimitiveTopology.TriangleList,
                    ResourceLayouts = new[] { _projViewLayout, _textureLayout, _fogLayout },
                    ShaderSet = new ShaderSetDescription(new[] { fallingVertexLayout, spriteInstanceLayout }, new[] { spriteShaders[0], spriteShaders[1] }),
                    Outputs = _sc.Framebuffer.OutputDescription
                });
            }

            // Shadow pipeline for item drops - simple horizontal quad with shadow texture
            {
                string shadowVsCode = @"#version 450
layout(location=0) in vec3 aCorner;
layout(location=1) in vec2 aLocalUV;
layout(location=2) in vec4 aShade;
layout(location=3) in vec3 iWorldPos;
layout(location=4) in vec4 iTileRect;
layout(location=0) out vec2 vLocalUV;
layout(location=1) out vec4 vTileRect;
layout(location=2) out vec4 vColor;
layout(set=0, binding=0) uniform ProjectionView { mat4 projView; };
void main() {
    // The quad is flat on the ground (horizontal). Position it slightly below the item.
    vec3 worldPos = iWorldPos + vec3(aCorner.x * 0.15, -0.08, aCorner.y * 0.15);
    vLocalUV = aLocalUV;
    vTileRect = iTileRect;
    vColor = aShade;
    gl_Position = projView * vec4(worldPos, 1.0);
}";

                string shadowFsCode = @"#version 450
layout(location=0) in vec2 vLocalUV;
layout(location=1) in vec4 vTileRect;
layout(location=2) in vec4 vColor;
layout(set=0, binding=0) uniform ProjectionView { mat4 projView; };
layout(set=1, binding=0) uniform sampler2D uTexture;
layout(set=2, binding=0) uniform FogParams { vec3 fogColor; float fogStart; float fogEnd; float fogDensity; };
layout(location=0) out vec4 outColor;
void main() {
    vec2 uv = vLocalUV * vTileRect.zw + vTileRect.xy;
    vec4 texColor = texture(uTexture, uv);
    outColor = vColor * texColor;
    // Simple fog
    float fogFactor = clamp((vColor.w - 0.5) * 2.0, 0.0, 1.0);
    outColor.rgb = mix(outColor.rgb, fogColor.rgb, fogFactor);
}";

                var shadowVsSpirv = SpirvCompilation.CompileGlslToSpirv(shadowVsCode, "main", ShaderStages.Vertex, GlslCompileOptions.Default);
                var shadowFsSpirv = SpirvCompilation.CompileGlslToSpirv(shadowFsCode, "main", ShaderStages.Fragment, GlslCompileOptions.Default);
                var shadowShaders = factory.CreateFromSpirv(
                    new ShaderDescription(ShaderStages.Vertex, shadowVsSpirv.SpirvBytes, "main"),
                    new ShaderDescription(ShaderStages.Fragment, shadowFsSpirv.SpirvBytes, "main"));
                var shadowInstanceLayout = new VertexLayoutDescription(
                    new VertexElementDescription("iWorldPos", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                    new VertexElementDescription("iTileRect", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4));
                shadowInstanceLayout.InstanceStepRate = 1;
                _itemDropShadowPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription()
                {
                    BlendState = BlendStateDescription.SingleAlphaBlend,
                    DepthStencilState = new DepthStencilStateDescription(false, false, ComparisonKind.Always),
                    RasterizerState = new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.CounterClockwise, true, false),
                    PrimitiveTopology = PrimitiveTopology.TriangleList,
                    ResourceLayouts = new[] { _projViewLayout, _textureLayout, _fogLayout },
                    ShaderSet = new ShaderSetDescription(new[] { fallingVertexLayout, shadowInstanceLayout }, new[] { shadowShaders[0], shadowShaders[1] }),
                    Outputs = _sc.Framebuffer.OutputDescription
                });
            }

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

            // Scaled-down cube mesh for dropped items (same layout, same pipeline, SAME winding
            // correction as the falling cube - otherwise back-face culling eats some faces).
            {
                var smallVerts = new float[FallingCubeVerts * (3 + 2 + 4)];
                int sv = 0;
                for (int face = 0; face < 6; face++)
                {
                    var src = FallingCubeFaces[face];
                    float shade = FallingFaceShade[face];
                    const float uvMax = 0.999f;
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
                        smallVerts[sv++] = verts[c * 3 + 0] * ItemDropScale;
                        smallVerts[sv++] = verts[c * 3 + 1] * ItemDropScale;
                        smallVerts[sv++] = verts[c * 3 + 2] * ItemDropScale;
                        smallVerts[sv++] = (c == 1 || c == 2) ? uvMax : 0f;
                        smallVerts[sv++] = (c == 2 || c == 3) ? uvMax : 0f;
                        smallVerts[sv++] = shade; smallVerts[sv++] = shade; smallVerts[sv++] = shade; smallVerts[sv++] = 1f;
                    }
                }
                _itemDropVertexBuffer = factory.CreateBuffer(new BufferDescription((uint)smallVerts.Length * sizeof(float), BufferUsage.VertexBuffer));
                _gd.UpdateBuffer(_itemDropVertexBuffer, 0, smallVerts);

                // Unit quad (z=0, corners at +/-1) shared by the item-drop sprite and held-sprite
                // pipelines; each shader bakes its own half-size scale. Same 9-float vertex layout
                // as the cubes (aCorner + aLocalUV + aShade), full-bright shade so sprites never
                // pick up cube face shading.
                var quadVerts = new float[4 * (3 + 2 + 4)];
                int qv = 0;
                float[] quadCorners = { -1, -1, 1, -1, 1, 1, -1, 1 };
                float[] quadUvs = { 0, 1, 1, 1, 1, 0, 0, 0 };
                for (int c = 0; c < 4; c++)
                {
                    quadVerts[qv++] = quadCorners[c * 2 + 0];
                    quadVerts[qv++] = quadCorners[c * 2 + 1];
                    quadVerts[qv++] = 0f;
                    quadVerts[qv++] = quadUvs[c * 2 + 0];
                    quadVerts[qv++] = quadUvs[c * 2 + 1];
                    quadVerts[qv++] = 1f; quadVerts[qv++] = 1f; quadVerts[qv++] = 1f; quadVerts[qv++] = 1f;
                }
                _spriteVertexBuffer = factory.CreateBuffer(new BufferDescription((uint)quadVerts.Length * sizeof(float), BufferUsage.VertexBuffer));
                _gd.UpdateBuffer(_spriteVertexBuffer, 0, quadVerts);
                var quadIndices = new ushort[] { 0, 1, 2, 0, 2, 3 };
                _spriteIndexBuffer = factory.CreateBuffer(new BufferDescription((uint)quadIndices.Length * sizeof(ushort), BufferUsage.IndexBuffer));
                _gd.UpdateBuffer(_spriteIndexBuffer, 0, quadIndices);
                var smallIndices = new ushort[FallingCubeIndices];
                int si = 0;
                for (int face = 0; face < 6; face++)
                {
                    int fv = face * 4;
                    smallIndices[si++] = (ushort)(fv + 0);
                    smallIndices[si++] = (ushort)(fv + 1);
                    smallIndices[si++] = (ushort)(fv + 2);
                    smallIndices[si++] = (ushort)(fv + 0);
                    smallIndices[si++] = (ushort)(fv + 2);
                    smallIndices[si++] = (ushort)(fv + 3);
                }
_itemDropIndexBuffer = factory.CreateBuffer(new BufferDescription((uint)smallIndices.Length * sizeof(ushort), BufferUsage.IndexBuffer));
                _gd.UpdateBuffer(_itemDropIndexBuffer, 0, smallIndices);
            }

            // Shadow mesh for item drops: a simple horizontal quad (shadow) at y = -0.02
            {
                // Shadow quad: slightly larger than item to cover it, flat on ground
                var shadowVerts = new float[4 * (3 + 2 + 4)];
                int sv = 0;
                float shadowSize = 0.25f; // larger shadow
                float shadowY = -0.02f; // just below item, less z-fighting
                float[] shadowCorners = { -shadowSize, shadowY, -shadowSize,  1f, 0f,  // bottom-left
                                          shadowSize, shadowY, -shadowSize,  1f, 1f,   // bottom-right
                                          shadowSize, shadowY,  shadowSize,  0f, 1f,   // top-right
                                         -shadowSize, shadowY,  shadowSize,  0f, 0f };  // top-left
                float[] shadowUvs = { 0, 1,  1, 1,  1, 0,  0, 0 };
                for (int c = 0; c < 4; c++)
                {
                    shadowVerts[sv++] = shadowCorners[c * 3 + 0];
                    shadowVerts[sv++] = shadowCorners[c * 3 + 1];
                    shadowVerts[sv++] = shadowCorners[c * 3 + 2];
                    shadowVerts[sv++] = shadowUvs[c * 2 + 0];
                    shadowVerts[sv++] = shadowUvs[c * 2 + 1];
                    shadowVerts[sv++] = 1f; shadowVerts[sv++] = 1f; shadowVerts[sv++] = 1f; shadowVerts[sv++] = 1f;
                }
                _shadowVertexBuffer = factory.CreateBuffer(new BufferDescription((uint)shadowVerts.Length * sizeof(float), BufferUsage.VertexBuffer));
                _gd.UpdateBuffer(_shadowVertexBuffer, 0, shadowVerts);
                var shadowIndices = new ushort[] { 0, 1, 2, 0, 2, 3 };
                _shadowIndexBuffer = factory.CreateBuffer(new BufferDescription((uint)shadowIndices.Length * sizeof(ushort), BufferUsage.IndexBuffer));
                _gd.UpdateBuffer(_shadowIndexBuffer, 0, shadowIndices);
            }

            // Create shadow texture (simple radial gradient circle) - darker, larger, stronger
            {
                int texSize = 64; // larger texture for smoother gradient
                byte[] shadowData = new byte[texSize * texSize * 4];
                float center = (texSize - 1) * 0.5f;
                float maxDist = center;
                for (int y = 0; y < texSize; y++)
                {
                    for (int x = 0; x < texSize; x++)
                    {
                        float dx = x - center;
                        float dy = y - center;
                        float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                        float alpha = Math.Max(0f, 1f - dist / maxDist);
                        // Smooth falloff
                        alpha = alpha * alpha;
                        byte a = (byte)(alpha * 200); // max ~80% opacity for visible shadows
                        int i = (y * texSize + x) * 4;
                        shadowData[i + 0] = 0;     // R
                        shadowData[i + 1] = 0;     // G
                        shadowData[i + 2] = 0;     // B
                        shadowData[i + 3] = a;     // A
                    }
                }
                var shadowTexDesc = TextureDescription.Texture2D((uint)64, (uint)64, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled);
                var shadowTexture = _gd.ResourceFactory.CreateTexture(shadowTexDesc);
                _gd.UpdateTexture<byte>(shadowTexture, shadowData, 0, 0, 0, (uint)shadowTexture.Width, (uint)shadowTexture.Height, 1, 0, 0);
                _shadowTextureView = _gd.ResourceFactory.CreateTextureView(shadowTexture);
            }

            // create texture resource set if atlas loaded
            if (_atlasView != null && _atlasSampler != null)
            {
                _textureSet = factory.CreateResourceSet(new ResourceSetDescription(_textureLayout, _atlasView, _atlasSampler));
            }

            // Items atlas resource set for item-tile drops (flint, etc).
            if (_itemsAtlasView != null && _atlasSampler != null)
            {
                _itemsTextureSet = factory.CreateResourceSet(new ResourceSetDescription(_textureLayout, _itemsAtlasView, _atlasSampler));
            }

            // Reuse a single command list across frames instead of allocating one per frame.
            _commandList = factory.CreateCommandList();

            CreateHighlightPipeline();
            CreateChunkBorderPipeline();
            CreateModelPipeline();
            CreateSkyPipeline();
            CreateCelestialPipelines();
            CreateWorldPlanePipeline(factory);
            CreateCloudPipeline(factory);
            CreateCrosshairPipeline(factory);
            CreateBlitPipeline(factory);
        }

        // Fullscreen blit used by resolution scale < 1: samples the offscreen world texture and
        // writes it across the swapchain. A vertex-less triangle (no vertex buffer; gl_VertexIndex
        // positions the corners) keeps this to a single Draw(3) with no per-frame CPU geometry.
        private void CreateBlitPipeline(Veldrid.ResourceFactory factory)
        {
            string blitVsCode = @"#version 450
layout(location=0) out vec2 vUV;
void main() {
    // Triangle corner from vertex index: (0,0) (2,0) (0,2) -> NDC (-1,-1) (3,-1) (-1,3).
    vec2 pos = vec2(float((gl_VertexIndex << 1) & 2), float(gl_VertexIndex & 2));
    vUV = vec2(pos.x, 1.0 - pos.y);
    gl_Position = vec4(pos * 2.0 - 1.0, 0.0, 1.0);
}";
            string blitFsCode = @"#version 450
layout(location=0) in vec2 vUV;
layout(set=0, binding=0) uniform sampler2D uScene;
layout(location=0) out vec4 outColor;
void main() {
    outColor = texture(uScene, vUV);
}";
            var vsSpirv = SpirvCompilation.CompileGlslToSpirv(blitVsCode, "main", ShaderStages.Vertex, GlslCompileOptions.Default);
            var fsSpirv = SpirvCompilation.CompileGlslToSpirv(blitFsCode, "main", ShaderStages.Fragment, GlslCompileOptions.Default);
            var shaders = factory.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Vertex, vsSpirv.SpirvBytes, "main"),
                new ShaderDescription(ShaderStages.Fragment, fsSpirv.SpirvBytes, "main"));

            _blitLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("uScene", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("uSceneSampler", ResourceKind.Sampler, ShaderStages.Fragment)));
            // Smooth = linear upscale (blurry); Blocky = nearest (chunky pixels). Both are kept
            // alive and the resource set picks the active one when scale targets are built.
            _blitSamplerLinear = factory.CreateSampler(SamplerDescription.Linear);
            _blitSamplerNearest = factory.CreateSampler(SamplerDescription.Point);

            // No vertex buffer: the shader generates its own triangle, so an EMPTY vertex layout
            // array is required (Veldrid rejects a null/omitted ShaderSet).
            var shaderSet = new ShaderSetDescription(Array.Empty<VertexLayoutDescription>(), new[] { shaders[0], shaders[1] });
            _blitPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription()
            {
                BlendState = BlendStateDescription.SingleDisabled,
                DepthStencilState = DepthStencilStateDescription.Disabled,
                RasterizerState = new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.CounterClockwise, false, false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _blitLayout },
                ShaderSet = shaderSet,
                Outputs = _sc.Framebuffer.OutputDescription
            });
        }

        // GPU-assisted frustum culling compute pipeline. The shader reads a per-chunk struct
        // (AABB min/max + the IndirectDrawIndexedArguments) from a structured buffer, tests all
        // six frustum planes with the positive-vertex trick (same math as ChunkInFrustum), and
        // zeroes InstanceCount for culled chunks. The draw then skips them via
        // DrawIndexedIndirect - no CPU scan, no compaction, no GPU->CPU readback.
        private void CreateCullComputePipeline()
        {
            try
            {
                CreateCullComputePipelineCore();
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine($"[GPU-Cull] compute pipeline failed: {ex}");
                _gpuCullSupported = false;
            }
        }

        private void CreateCullComputePipelineCore()
        {
            var factory = _gd.ResourceFactory;

            string cullCs = @"#version 450
layout(local_size_x = 64) in;

layout(set=0, binding=0) uniform FrustumPlanes {
    vec4 planes[6];
};

// One entry per chunk command, as a flat uint array (16 uint32s per chunk / 64 bytes).
//   [0..2]   aabbMin xyz (float bits), [3] pad
//   [4..6]   aabbMax xyz (float bits), [7] pad
//   [8..12]  IndirectDrawIndexedArguments (IndexCount, InstanceCount, FirstIndex, VertexOffset, FirstInstance)
//   [13..15] pad
layout(set=1, binding=0) readonly buffer ChunkCullData {
    uint data[];
} chunkData;

layout(set=1, binding=1) buffer IndirectArgs {
    uint args[];
} indirectArgs;

const uint CMD_STRIDE = 5;

void main() {
    uint gi = gl_GlobalInvocationID.x;
    uint base = gi * 16u;
    vec3 minAABB = vec3(uintBitsToFloat(chunkData.data[base + 0]),
                        uintBitsToFloat(chunkData.data[base + 1]),
                        uintBitsToFloat(chunkData.data[base + 2]));
    vec3 maxAABB = vec3(uintBitsToFloat(chunkData.data[base + 4]),
                        uintBitsToFloat(chunkData.data[base + 5]),
                        uintBitsToFloat(chunkData.data[base + 6]));

    bool visible = true;
    for (int p = 0; p < 6 && visible; p++) {
        vec4 pl = planes[p];
        float px = pl.x >= 0.0 ? maxAABB.x : minAABB.x;
        float py = pl.y >= 0.0 ? maxAABB.y : minAABB.y;
        float pz = pl.z >= 0.0 ? maxAABB.z : minAABB.z;
        if (pl.x * px + pl.y * py + pl.z * pz + pl.w < 0.0) {
            visible = false;
        }
    }

    uint outBase = gi * CMD_STRIDE;
    indirectArgs.args[outBase + 0] = chunkData.data[base + 8];                   // IndexCount
    indirectArgs.args[outBase + 1] = visible ? chunkData.data[base + 9] : 0u;    // InstanceCount
    indirectArgs.args[outBase + 2] = chunkData.data[base + 10];                  // FirstIndex
    indirectArgs.args[outBase + 3] = chunkData.data[base + 11];                  // VertexOffset
    indirectArgs.args[outBase + 4] = chunkData.data[base + 12];                  // FirstInstance
}";

            var csSpirv = SpirvCompilation.CompileGlslToSpirv(cullCs, "main", ShaderStages.Compute, GlslCompileOptions.Default);
            var csDesc = new ShaderDescription(ShaderStages.Compute, csSpirv.SpirvBytes, "main");
            var cs = factory.CreateFromSpirv(csDesc);

            _cullDataLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("FrustumPlanes", ResourceKind.UniformBuffer, ShaderStages.Compute)));
            _cullChunkLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("ChunkCullData", ResourceKind.StructuredBufferReadOnly, ShaderStages.Compute),
                new ResourceLayoutElementDescription("IndirectArgs", ResourceKind.StructuredBufferReadWrite, ShaderStages.Compute)));

            _frustumBuffer = factory.CreateBuffer(new BufferDescription(96, BufferUsage.UniformBuffer | BufferUsage.Dynamic));
            _frustumSet = factory.CreateResourceSet(new ResourceSetDescription(_cullDataLayout, _frustumBuffer));

            // One cull-data buffer (read-only in compute) and one args-output buffer (read-write).
            // Both start empty; sized in EnsureCullCapacity as chunk counts grow. Structured
            // buffers REQUIRE a non-zero StructureByteStride. The shader reads both as flat
            // uint[] arrays, so each buffer's structure stride is sizeof(uint)=4; the compute
            // writes 5 uints per command (20 bytes), which matches IndirectCommandStride when
            // copied into the real indirect buffer for the draw.
            const uint cullDataStride = sizeof(uint);
            const uint cullArgsStride = sizeof(uint);
            _cullDataBuffer = factory.CreateBuffer(new BufferDescription(
                cullDataStride, BufferUsage.StructuredBufferReadOnly, cullDataStride));
            _cullArgsBuffer = factory.CreateBuffer(new BufferDescription(
                cullArgsStride, BufferUsage.StructuredBufferReadWrite, cullArgsStride));
            _cullDataReadSet = factory.CreateResourceSet(new ResourceSetDescription(_cullChunkLayout, _cullDataBuffer, _cullArgsBuffer));
            _cullArgsWriteSet = factory.CreateResourceSet(new ResourceSetDescription(_cullChunkLayout, _cullDataBuffer, _cullArgsBuffer));
            _cullDataCapacityCommands = 0;

            _cullPipeline = factory.CreateComputePipeline(new ComputePipelineDescription(
                cs,
                new[] { _cullDataLayout, _cullChunkLayout },
                64, 1, 1));

            _gpuCullSupported = _gd.Features.ComputeShader && _gd.Features.StructuredBuffer && _gd.Features.DrawIndirect;
            ApplyCullingMode();
        }

        // Resolves the player-chosen culling mode into the effective _gpuCullEnabled flag and
        // invalidates every cached cull-data buffer so the next frame rebuilds them under the
        // new mode. No-op guard: Auto with no compute support stays CPU-side.
        private void ApplyCullingMode()
        {
            if (!_gpuCullSupported)
            {
                _gpuCullEnabled = false;
                return;
            }
            _gpuCullEnabled = _cullMode switch
            {
                CullingMode.Gpu => true,
                CullingMode.Cpu => false,
                _ => !IsIntelGpu(_gd.VendorName),
            };
            _gpuCullDataDirty = true;
            _opaqueCullData = Array.Empty<uint>();
            _cutoutCullData = Array.Empty<uint>();
            _glassCullData = Array.Empty<uint>();
            _transparentCullData = Array.Empty<uint>();
        }

        // Settings-menu API: player picks Auto / CPU / GPU.
        public void SetCullingMode(CullingMode mode)
        {
            if (_cullMode == mode) return;
            _cullMode = mode;
            ApplyCullingMode();
        }

        public CullingMode GetCullingMode() => _cullMode;

        // Veldrid reports VendorName like "id:00008086" (hex PCI vendor id) on D3D11, and the GL
        // vendor string ("Intel", "NVIDIA Corporation", ...) on OpenGL. 0x8086 = Intel, 0x10DE =
        // NVIDIA, 0x1002 = AMD. Both formats are checked so Auto still disables GPU culling on
        // Intel hardware even when the game falls back to the OpenGL backend.
        private static bool IsIntelGpu(string vendorName)
        {
            if (string.IsNullOrEmpty(vendorName)) return false;
            int idPos = vendorName.IndexOf("id:", StringComparison.OrdinalIgnoreCase);
            if (idPos >= 0)
            {
                string hex = vendorName.Substring(idPos + 3).Trim();
                if (hex.Length > 8) hex = hex.Substring(hex.Length - 8);
                return uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint id) && id == 0x8086;
            }
            // OpenGL backend: vendor string contains the Intel name.
            return vendorName.Contains("intel", StringComparison.OrdinalIgnoreCase);
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

            // First-person hand: same model shaders (camera-space positions + player skin), but
            // depth testing DISABLED so the hand always renders on top of the world, and set 0 is
            // a dedicated camera-space projection (no view transform - the hand is positioned
            // relative to the eye each frame on the CPU).
            string handVsCode = @"#version 450
layout(location=0) in vec3 aPosition;
layout(location=1) in vec2 aUV;
layout(location=2) in vec4 aColor;
layout(location=0) out vec2 vUV;
layout(location=1) out vec4 vColor;
layout(set=0, binding=0) uniform ProjectionView { mat4 projView; };
void main() { vUV = aUV; vColor = aColor; gl_Position = projView * vec4(aPosition, 1.0); }";
            var handVsSpirv = SpirvCompilation.CompileGlslToSpirv(handVsCode, "main", ShaderStages.Vertex, GlslCompileOptions.Default);
            var handShaders = factory.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Vertex, handVsSpirv.SpirvBytes, "main"),
                new ShaderDescription(ShaderStages.Fragment, fsSpirv.SpirvBytes, "main"));

            _handPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription()
            {
                BlendState = BlendStateDescription.SingleOverrideBlend,
                DepthStencilState = DepthStencilStateDescription.Disabled,
                RasterizerState = new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.CounterClockwise, true, false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _projViewLayout, _textureLayout },
                ShaderSet = new ShaderSetDescription(new[] { vertexLayout }, new[] { handShaders[0], handShaders[1] }),
                Outputs = _sc.Framebuffer.OutputDescription
            });

            _handProjBuffer = factory.CreateBuffer(new BufferDescription(64, BufferUsage.UniformBuffer | BufferUsage.Dynamic));
            _handProjSet = factory.CreateResourceSet(new ResourceSetDescription(_projViewLayout, _handProjBuffer));

            // Held-block pipeline: camera-space projection + block atlas, NO world fog and no
            // mining-cell discard. The block sits in front of the eye so it must always render its
            // true texture - the item-drop pipeline's fog math (world-space) goes garbage on
            // camera-space coords and could wash the block out to the fog colour.
            string heldVsCode = @"#version 450
layout(location=0) in vec3 aCorner;
layout(location=1) in vec2 aLocalUV;
layout(location=2) in vec4 aShade;
layout(location=3) in vec3 iWorldPos;
layout(location=4) in vec4 iTileRect;
layout(location=5) in vec4 iRot;
layout(location=0) out vec2 vLocalUV;
layout(location=1) out vec4 vTileRect;
layout(location=2) out vec4 vColor;
layout(set=0, binding=0) uniform ProjectionView { mat4 projView; };
void main() {
    vec3 local = aCorner - vec3(0.15);
    vec3 qv = iRot.xyz;
    vec3 t = 2.0 * cross(qv, local);
    vec3 rotated = local + iRot.w * t + cross(qv, t);
    vec3 camPos = rotated + iWorldPos;
    vLocalUV = aLocalUV;
    vTileRect = iTileRect;
    vColor = aShade;
    gl_Position = projView * vec4(camPos, 1.0);
}";
            string heldFsCode = @"#version 450
layout(location=0) in vec2 vLocalUV;
layout(location=1) in vec4 vTileRect;
layout(location=2) in vec4 vColor;
layout(set=1, binding=0) uniform sampler2D uAtlas;
layout(location=0) out vec4 outColor;
void main() {
    vec2 atlasUV = fract(vLocalUV) * vTileRect.zw + vTileRect.xy;
    vec4 tex = texture(uAtlas, atlasUV);
    outColor = vec4(tex.rgb * vColor.rgb, vColor.a);
}";
            var heldVsSpirv = SpirvCompilation.CompileGlslToSpirv(heldVsCode, "main", ShaderStages.Vertex, GlslCompileOptions.Default);
            var heldFsSpirv = SpirvCompilation.CompileGlslToSpirv(heldFsCode, "main", ShaderStages.Fragment, GlslCompileOptions.Default);
            var heldShaders = factory.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Vertex, heldVsSpirv.SpirvBytes, "main"),
                new ShaderDescription(ShaderStages.Fragment, heldFsSpirv.SpirvBytes, "main"));
            var heldCubeLayout = new VertexLayoutDescription(
                new VertexElementDescription("aCorner", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                new VertexElementDescription("aLocalUV", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
                new VertexElementDescription("aShade", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4));
            var heldInstLayout = new VertexLayoutDescription(
                new VertexElementDescription("iWorldPos", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                new VertexElementDescription("iTileRect", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4),
                new VertexElementDescription("iRot", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4));
            heldInstLayout.InstanceStepRate = 1;
            _heldBlockPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription()
            {
                BlendState = BlendStateDescription.SingleOverrideBlend,
                DepthStencilState = DepthStencilStateDescription.Disabled,
                // The held block is a closed cube with correct outward winding, so back-face
                // culling makes it solid without needing depth (which would conflict with the
                // world's depth buffer under the camera-space projection).
                RasterizerState = new RasterizerStateDescription(FaceCullMode.Back, PolygonFillMode.Solid, FrontFace.CounterClockwise, true, false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _projViewLayout, _textureLayout },
                ShaderSet = new ShaderSetDescription(new[] { heldCubeLayout, heldInstLayout }, new[] { heldShaders[0], heldShaders[1] }),
                Outputs = _sc.Framebuffer.OutputDescription
            });

            // Held genuine items: a flat camera-space sprite riding the fist (screen-aligned,
            // like the hotbar icon), instead of the tilted cube. Same 11-float instance data
            // (center + tile rect + rotation; rotation unused). Half-size baked: 0.22 -> 0.44
            // world units, so the flat sprite reads about as big as the 0.289 held cube.
            {
                string heldSpriteVsCode = @"#version 450
layout(location=0) in vec3 aCorner;
layout(location=1) in vec2 aLocalUV;
layout(location=2) in vec4 aShade;
layout(location=3) in vec3 iWorldPos;
layout(location=4) in vec4 iTileRect;
layout(location=5) in vec4 iRot;
layout(location=0) out vec2 vLocalUV;
layout(location=1) out vec4 vTileRect;
layout(location=2) out vec4 vColor;
layout(set=0, binding=0) uniform ProjectionView { mat4 proj; };
void main() {
    // Camera space is already axis-aligned with the camera: offset the quad in xy only.
    vec3 local = aCorner * 0.22;
    vec3 camPos = iWorldPos + vec3(local.xy, 0.0);
    vLocalUV = aLocalUV;
    vTileRect = iTileRect;
    vColor = aShade;
    gl_Position = proj * vec4(camPos, 1.0);
}";
                var heldSpriteVsSpirv = SpirvCompilation.CompileGlslToSpirv(heldSpriteVsCode, "main", ShaderStages.Vertex, GlslCompileOptions.Default);
                // Held sprites must also cut out transparent texels (alpha < 0.5) - otherwise the
                // transparent art pixels render as black over the world.
                string heldSpriteFsCode = @"#version 450
layout(location=0) in vec2 vLocalUV;
layout(location=1) in vec4 vTileRect;
layout(location=2) in vec4 vColor;
layout(set=1, binding=0) uniform sampler2D uAtlas;
layout(location=0) out vec4 outColor;
void main() {
    vec2 atlasUV = fract(vLocalUV) * vTileRect.zw + vTileRect.xy;
    vec4 tex = texture(uAtlas, atlasUV);
    if (tex.a < 0.5) discard;
    outColor = vec4(tex.rgb * vColor.rgb, 1.0);
}";
                var heldSpriteFsSpirv = SpirvCompilation.CompileGlslToSpirv(heldSpriteFsCode, "main", ShaderStages.Fragment, GlslCompileOptions.Default);
                var heldSpriteShaders = factory.CreateFromSpirv(
                    new ShaderDescription(ShaderStages.Vertex, heldSpriteVsSpirv.SpirvBytes, "main"),
                    new ShaderDescription(ShaderStages.Fragment, heldSpriteFsSpirv.SpirvBytes, "main"));
                _heldBlockSpritePipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription()
                {
                    BlendState = BlendStateDescription.SingleOverrideBlend,
                    DepthStencilState = DepthStencilStateDescription.Disabled,
                    // Closed quad with correct outward winding, cull off so it never vanishes.
                    RasterizerState = new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.CounterClockwise, true, false),
                    PrimitiveTopology = PrimitiveTopology.TriangleList,
                    ResourceLayouts = new[] { _projViewLayout, _textureLayout },
                    ShaderSet = new ShaderSetDescription(new[] { heldCubeLayout, heldInstLayout }, new[] { heldSpriteShaders[0], heldSpriteShaders[1] }),
                    Outputs = _sc.Framebuffer.OutputDescription
                });
            }

            // Size from the actual hand mesh (24 verts / 36 indices for the arm cube) - the mesh
            // is built in LoadPlayerResources which runs before this.
            _handVertexBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)Math.Max(_handMesh.Length * sizeof(float), 512), BufferUsage.VertexBuffer | BufferUsage.Dynamic));
            _handIndexBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)Math.Max(_handIndices.Length * sizeof(ushort), 128), BufferUsage.IndexBuffer));
            _heldBlockBuffer = factory.CreateBuffer(new BufferDescription(11 * sizeof(float), BufferUsage.VertexBuffer | BufferUsage.Dynamic));

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

            // Shrinking-block mining overlay cube: 24 vertices (packed world format) + 36 indices.
            _shrinkCubeVertexBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)(_shrinkCubeVertexScratch.Length * sizeof(float)), BufferUsage.VertexBuffer | BufferUsage.Dynamic));
            _shrinkCubeIndexBuffer = factory.CreateBuffer(new BufferDescription(
                72 * sizeof(ushort), BufferUsage.IndexBuffer)); // 12 quads (cube + 6 walls)
            var cubeIndices = new ushort[72];
            for (int quad = 0; quad < 12; quad++)
            {
                int fv = quad * 4;
                cubeIndices[quad * 6 + 0] = (ushort)(fv + 0);
                cubeIndices[quad * 6 + 1] = (ushort)(fv + 1);
                cubeIndices[quad * 6 + 2] = (ushort)(fv + 2);
                cubeIndices[quad * 6 + 3] = (ushort)(fv + 0);
                cubeIndices[quad * 6 + 4] = (ushort)(fv + 2);
                cubeIndices[quad * 6 + 5] = (ushort)(fv + 3);
            }
            _gd.UpdateBuffer(_shrinkCubeIndexBuffer, 0, cubeIndices);

            // Dedicated pipeline for the shrinking cube: same packed vertex format + atlas + fog,
            // depth-tested like terrain (so it doesn't paint over blocks behind it), but WITHOUT
            // the hidden-cell discard so the cube itself is visible in the mined cell. Its vertex
            // shader applies a clip-space depth bias (the C++ used glPolygonOffset(-1,-1)) so the
            // cube always wins against coplanar/behind faces - no z-fighting at the cell walls.
            var worldVertexLayout = new VertexLayoutDescription(
                new VertexElementDescription("aPosition", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                new VertexElementDescription("aPack1", VertexElementSemantic.TextureCoordinate, VertexElementFormat.UInt1),
                new VertexElementDescription("aPack2", VertexElementSemantic.TextureCoordinate, VertexElementFormat.UInt1),
                new VertexElementDescription("aPack3", VertexElementSemantic.TextureCoordinate, VertexElementFormat.UInt1));
            float shrinkAtlasW = Math.Max(1f, _atlasWidth);
            float shrinkAtlasH = Math.Max(1f, _atlasHeight);
            // Build the shared vertex shader with a configurable clip-space depth bias: the CUBE
            // uses a STRONGER bias so it always wins its faces at the cell boundary; the WALLS use
            // a weaker bias so they show through only where the cube has shrunk away. This is the
            // C++ polygon-offset ordering (walls behind, cube on top).
            string MakeShrinkVs(string biasExpr) => $@"#version 450
layout(location=0) in vec3 aPosition;
layout(location=1) in uint aPack1;
layout(location=2) in uint aPack2;
layout(location=3) in uint aPack3;
layout(location=0) out vec2 vLocalUV;
layout(location=1) out vec4 vTileRect;
layout(location=2) out vec4 vColor;
layout(location=3) out vec3 vWorldPos;
layout(set=0, binding=0) uniform ProjectionView {{ mat4 projView; }};
const float ATLAS_INV_X = 1.0 / {shrinkAtlasW.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}.0;
const float ATLAS_INV_Y = 1.0 / {shrinkAtlasH.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}.0;
void main() {{
    float du = float((aPack1 >> 16) & 0xFFFFu) / 256.0;
    float dv = float(aPack1 & 0xFFFFu) / 256.0;
    vLocalUV = vec2(du, dv);
    float tx = float((aPack2 >> 24) & 0xFFu) * ATLAS_INV_X;
    float ty = float((aPack2 >> 16) & 0xFFu) * ATLAS_INV_Y;
    float tw = float((aPack2 >> 8) & 0xFFu) * ATLAS_INV_X;
    float th = float(aPack2 & 0xFFu) * ATLAS_INV_Y;
    vTileRect = vec4(tx, ty, tw, th);
    float shade = float(aPack3 & 0xFFu) / 255.0;
    uint alphaByte = (aPack3 >> 8) & 0xFFu;
    uint mode = (aPack3 >> 16) & 0x3u;
    float alpha = mode == 0u ? float(alphaByte) / 255.0 : (mode == 1u ? -100.0 : -200.0);
    vColor = vec4(shade, shade, shade, alpha);
    vWorldPos = aPosition;
    vec4 clip = projView * vec4(aPosition, 1.0);
    // Polygon-offset equivalent (C++ glPolygonOffset(-1,-1)): pull depth toward the camera.
    clip.z -= {biasExpr} * clip.w;
    gl_Position = clip;
}}";
            string shrinkVsCode = MakeShrinkVs("0.00002");
            string wallVsCode = MakeShrinkVs("0.00001");
            string shrinkFsCode = @"#version 450
layout(location=0) in vec2 vLocalUV;
layout(location=1) in vec4 vTileRect;
layout(location=2) in vec4 vColor;
layout(location=3) in vec3 vWorldPos;
layout(set=1, binding=0) uniform sampler2D uAtlas;
layout(set=2, binding=0) uniform FogParams {
    vec4 fogColor;
    vec2 fogRange;
    vec4 cameraPos;
    vec4 hiddenCell;
};
layout(location=0) out vec4 outColor;
void main() {
    // No hidden-cell discard here - the shrink cube must draw inside the mined cell.
    vec2 atlasUV = fract(vLocalUV) * vTileRect.zw + vTileRect.xy;
    vec4 tex = texture(uAtlas, atlasUV);
    outColor = vec4(tex.rgb * vColor.rgb, 1.0);
    float dist = length(vWorldPos - cameraPos.xyz);
    float fog = clamp((fogRange.y - dist) / max(fogRange.y - fogRange.x, 1e-5), 0.0, 1.0);
    outColor.rgb = mix(fogColor.rgb, outColor.rgb, fog);
}";
            var shrinkVsSpirv = SpirvCompilation.CompileGlslToSpirv(shrinkVsCode, "main", ShaderStages.Vertex, GlslCompileOptions.Default);
            var shrinkFsSpirv = SpirvCompilation.CompileGlslToSpirv(shrinkFsCode, "main", ShaderStages.Fragment, GlslCompileOptions.Default);
            var shrinkShaders = factory.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Vertex, shrinkVsSpirv.SpirvBytes, "main"),
                new ShaderDescription(ShaderStages.Fragment, shrinkFsSpirv.SpirvBytes, "main"));
            var wallVsSpirv = SpirvCompilation.CompileGlslToSpirv(wallVsCode, "main", ShaderStages.Vertex, GlslCompileOptions.Default);
            var wallShaders = factory.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Vertex, wallVsSpirv.SpirvBytes, "main"),
                new ShaderDescription(ShaderStages.Fragment, shrinkFsSpirv.SpirvBytes, "main"));
            _shrinkCubePipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription()
            {
                BlendState = BlendStateDescription.SingleDisabled,
                DepthStencilState = new DepthStencilStateDescription(true, true, ComparisonKind.LessEqual),
                // Culling OFF, matching the C++ breaking cube (it never enables GL_CULL_FACE).
                // The shrink cube's winding is camera-independent - back-face culling would kill
                // the faces whose winding reads clockwise from the viewer (e.g. north/south).
                RasterizerState = new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.CounterClockwise, true, false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _projViewLayout, _textureLayout, _fogLayout },
                ShaderSet = new ShaderSetDescription(new[] { worldVertexLayout }, new[] { shrinkShaders[0], shrinkShaders[1] }),
                Outputs = _sc.Framebuffer.OutputDescription
            });

            // Neighbor-wall pipeline: same fragment shader but a WEAKER vertex depth bias than the
            // cube. Faithful to C++ BreakingBlockRenderer::render, which renders adjacent faces
            // FIRST then the shrinking block LAST (both glPolygonOffset(-1,-1)): the walls (drawn
            // first, weaker bias) show through only where the cube has shrunk away, and the cube
            // (drawn after, stronger bias) wins everywhere it still covers. Walls write depth,
            // like the C++ (no glDepthMask(false) during the breaking render).
            _shrinkWallPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription()
            {
                BlendState = BlendStateDescription.SingleDisabled,
                DepthStencilState = new DepthStencilStateDescription(true, true, ComparisonKind.LessEqual),
                RasterizerState = new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.CounterClockwise, true, false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _projViewLayout, _textureLayout, _fogLayout },
                ShaderSet = new ShaderSetDescription(new[] { worldVertexLayout }, new[] { wallShaders[0], wallShaders[1] }),
                Outputs = _sc.Framebuffer.OutputDescription
            });
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

        // Pipeline for the sky: two huge flat planes (glSkyList at y+16, glSkyList2 at y-16,
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
layout(set=1, binding=0) uniform SkyFog {
    vec4 fogColor;     // horizon/fog color (matches world fog so terrain fades in seamlessly)
    vec2 fogRange;     // start, end (unused - sky is not distance-fogged)
    vec4 cameraPos;    // xyz + pad
    vec4 skyTop;       // overhead sky color
    vec4 skyBottom;    // below-horizon color
};
layout(location=0) out vec4 outColor;
void main() {
    // Vertical sky gradient in CAMERA space (planes at +-16): the horizon is the fog color so the
    // world fades into it exactly, overhead rises to the bright sky color, and below the horizon
    // falls to the darker undersky - the classic Cubuild gradient. Use the VIEW DIRECTION's
    // y (normalize(vWorldPos).y), not the plane's constant y: that varies smoothly per-fragment
    // from +1 overhead to 0 at the horizon to -1 below. Vertex colors are unused (that varying
    // misreads on this pipeline).
    // Sky gradient in CAMERA space: horizon = fog color (matches the clear gap), bright overhead,
    // dark undersky below. Both halves ramp from EXACTLY the horizon (edge 0) so the color AND its
    // slope are continuous across the seam - no hard line. The 0.55 factor spreads the transition
    // over a wider band so the horizon blends gently.
    float up = clamp(normalize(vWorldPos).y * 0.55, -1.0, 1.0);
    vec3 col = up >= 0.0
        ? mix(fogColor.rgb, skyTop.rgb, smoothstep(0.0, 1.0, up))
        : mix(fogColor.rgb, skyBottom.rgb, smoothstep(0.0, -1.0, up));
    outColor = vec4(col, 1.0);
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
            // world fog buffer. Layout: fogColor(16) + fogRange(8) + cameraPos(16) + skyTop(16) +
            // skyBottom(16) = 80 bytes.
            _skyFogLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("SkyFog", ResourceKind.UniformBuffer, ShaderStages.Fragment)));
            _skyFogBuffer = factory.CreateBuffer(new BufferDescription(80, BufferUsage.UniformBuffer | BufferUsage.Dynamic));
            _skyFogSet = factory.CreateResourceSet(new ResourceSetDescription(_skyFogLayout, _skyFogBuffer));

            // The sky matrix reuses the projView layout (mat4) but holds the ROTATION-ONLY view *
            // projection, so the camera-space sky quads render glued to the eye.
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

        // Sun, moon and stars. Sun/moon are textured quads drawn
        // with ADDITIVE blending into the sky's rotation (rotate by celestialAngle around X); stars
        // are a precompiled field of small quads with alpha = getStarBrightness.
        private void CreateCelestialPipelines()
        {
            var factory = _gd.ResourceFactory;

            // Sun/moon: pos(3) + uv(2), texture from set 1, additive blend, depth off (drawn on sky).
            string celestialVs = @"#version 450
layout(location=0) in vec3 aPosition;
layout(location=1) in vec2 aUV;
layout(location=0) out vec2 vUV;
layout(set=0, binding=0) uniform ProjectionView { mat4 projView; };
void main() { vUV = aUV; gl_Position = projView * vec4(aPosition, 1.0); }";
            string celestialFs = @"#version 450
layout(location=0) in vec2 vUV;
layout(set=1, binding=0) uniform sampler2D uTex;
layout(location=0) out vec4 outColor;
void main() { outColor = texture(uTex, vUV); }";
            var cvSpirv = SpirvCompilation.CompileGlslToSpirv(celestialVs, "main", ShaderStages.Vertex, GlslCompileOptions.Default);
            var cfSpirv = SpirvCompilation.CompileGlslToSpirv(celestialFs, "main", ShaderStages.Fragment, GlslCompileOptions.Default);
            var celestialShaders = factory.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Vertex, cvSpirv.SpirvBytes, "main"),
                new ShaderDescription(ShaderStages.Fragment, cfSpirv.SpirvBytes, "main"));
            var celestialLayout = new VertexLayoutDescription(
                new VertexElementDescription("aPosition", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                new VertexElementDescription("aUV", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2));
            _celestialTextureLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("uTex", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("uTexSampler", ResourceKind.Sampler, ShaderStages.Fragment)));
            var celestialPipelineDesc = new GraphicsPipelineDescription()
            {
                // Alpha-blend the 16x16 sun/moon sprites over the sky (additive
                // GL_ONE/GL_ONE; the texture has its own alpha so SingleAlphaBlend reads the same).
                BlendState = BlendStateDescription.SingleAlphaBlend,
                DepthStencilState = DepthStencilStateDescription.Disabled,
                RasterizerState = new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.CounterClockwise, true, false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _projViewLayout, _celestialTextureLayout },
                ShaderSet = new ShaderSetDescription(new[] { celestialLayout }, new[] { celestialShaders[0], celestialShaders[1] }),
                Outputs = _sc.Framebuffer.OutputDescription
            };
            _celestialPipeline = factory.CreateGraphicsPipeline(celestialPipelineDesc);
            _celestialVertexBuffer = factory.CreateBuffer(new BufferDescription(
                8 * 5 * sizeof(float), BufferUsage.VertexBuffer | BufferUsage.Dynamic));
            _celestialIndexBuffer = factory.CreateBuffer(new BufferDescription(
                12 * sizeof(ushort), BufferUsage.IndexBuffer));
            _gd.UpdateBuffer(_celestialIndexBuffer, 0, new ushort[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 });

            // Load the 16x16 sun/moon textures.
            try
            {
                byte[]? sunBytes = LoadImageBytes("sun.png");
                byte[]? moonBytes = LoadImageBytes("moon.png");
                if (sunBytes != null)
                {
                    var img = StbImageSharp.ImageResult.FromMemory(sunBytes, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
                    var tex = _gd.ResourceFactory.CreateTexture(TextureDescription.Texture2D((uint)img.Width, (uint)img.Height, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled));
                    _gd.UpdateTexture(tex, img.Data, 0, 0, 0, (uint)img.Width, (uint)img.Height, 1, 0, 0);
                    _sunTexture = tex;
                    _sunView = _gd.ResourceFactory.CreateTextureView(tex);
                }
                if (moonBytes != null)
                {
                    var img = StbImageSharp.ImageResult.FromMemory(moonBytes, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
                    var tex = _gd.ResourceFactory.CreateTexture(TextureDescription.Texture2D((uint)img.Width, (uint)img.Height, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled));
                    _gd.UpdateTexture(tex, img.Data, 0, 0, 0, (uint)img.Width, (uint)img.Height, 1, 0, 0);
                    _moonTexture = tex;
                    _moonView = _gd.ResourceFactory.CreateTextureView(tex);
                }
                _celestialSampler = _gd.ResourceFactory.CreateSampler(new SamplerDescription(
                    SamplerAddressMode.Clamp, SamplerAddressMode.Clamp, SamplerAddressMode.Clamp,
                    SamplerFilter.MinPoint_MagPoint_MipPoint, null, 1, 0, 0, 0, SamplerBorderColor.TransparentBlack));
                if (_sunView != null && _celestialSampler != null)
                    _sunTextureSet = factory.CreateResourceSet(new ResourceSetDescription(_celestialTextureLayout, _sunView, _celestialSampler));
                if (_moonView != null && _celestialSampler != null)
                    _moonTextureSet = factory.CreateResourceSet(new ResourceSetDescription(_celestialTextureLayout, _moonView, _celestialSampler));
            }
            catch { }

            // Stars: pos(3) + color(4) quads, white with alpha, drawn with additive blend.
            string starVs = @"#version 450
layout(location=0) in vec3 aPosition;
layout(location=1) in vec4 aColor;
layout(location=0) out vec4 vColor;
layout(set=0, binding=0) uniform ProjectionView { mat4 projView; };
void main() { vColor = aColor; gl_Position = projView * vec4(aPosition, 1.0); }";
            string starFs = @"#version 450
layout(location=0) in vec4 vColor;
layout(location=0) out vec4 outColor;
void main() { outColor = vColor; }";
            var svSpirv = SpirvCompilation.CompileGlslToSpirv(starVs, "main", ShaderStages.Vertex, GlslCompileOptions.Default);
            var sfSpirv = SpirvCompilation.CompileGlslToSpirv(starFs, "main", ShaderStages.Fragment, GlslCompileOptions.Default);
            var starShaders = factory.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Vertex, svSpirv.SpirvBytes, "main"),
                new ShaderDescription(ShaderStages.Fragment, sfSpirv.SpirvBytes, "main"));
            var starLayout = new VertexLayoutDescription(
                new VertexElementDescription("aPosition", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                new VertexElementDescription("aColor", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4));
            var starPipelineDesc = new GraphicsPipelineDescription()
            {
                BlendState = BlendStateDescription.SingleAlphaBlend,
                DepthStencilState = DepthStencilStateDescription.Disabled,
                RasterizerState = new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.CounterClockwise, true, false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _projViewLayout },
                ShaderSet = new ShaderSetDescription(new[] { starLayout }, new[] { starShaders[0], starShaders[1] }),
                Outputs = _sc.Framebuffer.OutputDescription
            };
            _starPipeline = factory.CreateGraphicsPipeline(starPipelineDesc);

            // Galaxies: same pos(3)+color(4) quad format as stars, but ADDITIVE blend
            // (GL_SRC_ALPHA, GL_ONE) so overlapping galaxy particles glow like the C++ build.
            var galaxyPipelineDesc = new GraphicsPipelineDescription()
            {
                BlendState = BlendStateDescription.SingleAdditiveBlend,
                DepthStencilState = DepthStencilStateDescription.Disabled,
                RasterizerState = new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.CounterClockwise, true, false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _projViewLayout },
                ShaderSet = new ShaderSetDescription(new[] { starLayout }, new[] { starShaders[0], starShaders[1] }),
                Outputs = _sc.Framebuffer.OutputDescription
            };
            _galaxyPipeline = factory.CreateGraphicsPipeline(galaxyPipelineDesc);
        }


        // The "world from above" plane: a giant flat green+water textured quad at WorldPlaneY,
        // shown only when the player is very high. Uses the wide-far matrix so it stretches past
        // terrain; depth disabled + drawn BEFORE the world so real terrain paints over it.
        private void CreateWorldPlanePipeline(ResourceFactory factory)
        {
            string vsCode = @"#version 450
layout(location=0) in vec3 aPosition;
layout(location=1) in vec2 aUV;
layout(location=0) out vec2 vUV;
layout(set=0, binding=0) uniform ProjectionView { mat4 projView; };
void main() { vUV = aUV; gl_Position = projView * vec4(aPosition, 1.0); }";
            var vsSpirv = SpirvCompilation.CompileGlslToSpirv(vsCode, "main", ShaderStages.Vertex, GlslCompileOptions.Default);

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
                new ShaderDescription(ShaderStages.Vertex, vsSpirv.SpirvBytes, "main"),
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
            // The wide-far matrix lives with the world plane (its only consumer): created HERE,
            // before the resource set that references it.
            _cloudMatrixBuffer = factory.CreateBuffer(new BufferDescription(64, BufferUsage.UniformBuffer | BufferUsage.Dynamic));
            _cloudMatrixSet = factory.CreateResourceSet(new ResourceSetDescription(_projViewLayout, _cloudMatrixBuffer));
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

        // Flat cloud deck (MC-style): one big translucent plane at CloudWorldY following the
        // camera. Drawn AFTER the world with depth TEST on / WRITE off and the SAME projection as
        // terrain, so depth comparison is exact: land occludes the deck from below, and from
        // above the deck blends over the land. The deck's outer edge fades to zero alpha so the
        // far-plane horizon cut is soft instead of a hard line.
        private void CreateCloudPipeline(ResourceFactory factory)
        {
            string vsCode = @"#version 450
layout(location=0) in vec3 aPosition;
layout(location=1) in vec2 aUV;
layout(location=2) in float aFade;
layout(location=0) out vec2 vUV;
layout(location=1) out float vFade;
layout(set=0, binding=0) uniform ProjectionView { mat4 projView; };
void main() {
    vUV = aUV;
    vFade = aFade;
    gl_Position = projView * vec4(aPosition, 1.0);
}";

            string fsCode = @"#version 450
layout(location=0) in vec2 vUV;
layout(location=1) in float vFade;
layout(set=1, binding=0) uniform sampler2D uClouds;
layout(set=1, binding=1) uniform CloudParams {
    vec4 scrollOpacity; // x=scrollU, y=scrollV, z=opacity, w=unused
};
layout(location=0) out vec4 outColor;
void main() {
    vec2 uv = fract(vUV + scrollOpacity.xy);
    float a = texture(uClouds, uv).a * scrollOpacity.z * vFade;
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
                new VertexElementDescription("aUV", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
                new VertexElementDescription("aFade", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float1));

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
                // Depth TEST on, WRITE off: from below, hills closer than the deck fail the test
                // and hide it; from above, the deck blends over the farther land.
                DepthStencilState = new DepthStencilStateDescription(true, false, ComparisonKind.LessEqual),
                RasterizerState = new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.CounterClockwise, true, false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _projViewLayout, _cloudParamsLayout },
                ShaderSet = new ShaderSetDescription(new[] { vertexLayout }, new[] { shaders[0], shaders[1] }),
                Outputs = _sc.Framebuffer.OutputDescription
            });

            // Deck geometry: a 5x5 grid (16 quads) so the edge fade can vary per vertex - a flat
            // 4-corner quad can't hold a radial gradient (bilinear interpolation of equal corner
            // values is constant).
            _cloudVertexBuffer = factory.CreateBuffer(new BufferDescription(
                5 * 5 * 6 * sizeof(float), BufferUsage.VertexBuffer | BufferUsage.Dynamic));
            _cloudIndexBuffer = factory.CreateBuffer(new BufferDescription(
                16 * 6 * sizeof(ushort), BufferUsage.IndexBuffer));
            var cloudIndices = new ushort[16 * 6];
            int ci = 0;
            for (int gy = 0; gy < 4; gy++)
            {
                for (int gx = 0; gx < 4; gx++)
                {
                    ushort a = (ushort)(gy * 5 + gx);
                    ushort b = (ushort)(gy * 5 + gx + 1);
                    ushort c = (ushort)((gy + 1) * 5 + gx);
                    ushort d = (ushort)((gy + 1) * 5 + gx + 1);
                    cloudIndices[ci++] = a; cloudIndices[ci++] = b; cloudIndices[ci++] = d;
                    cloudIndices[ci++] = a; cloudIndices[ci++] = d; cloudIndices[ci++] = c;
                }
            }
            _gd.UpdateBuffer(_cloudIndexBuffer, 0, cloudIndices);
        }

        // ORIGINAL procedural cloud pattern (no external assets): a COARSE 32x32 grid of fractal
        // value noise, HARD-thresholded so every cell is either solid white or empty, then each
        // cell is stamped as an 8x8 pixel square - crisp, blocky, Minecraft-Classic-style
        // rectangles instead of soft puffs. One coarse cell = CloudTileSize/32 blocks in the world.
        private static byte[] GenerateCloudTexture(int seed)
        {
            const int coarse = 32;
            const int scale = 8; // each coarse cell becomes an 8x8 square of pixels
            const int size = coarse * scale;
            var rng = new Random(seed);

            var noise = new float[coarse * coarse];
            float totalAmp = 0f;
            for (int octave = 0; octave < 2; octave++)
            {
                int cell = 2 << octave; // lattice spacing: 2, 4 coarse cells
                float amp = 1f / (1 << octave);
                totalAmp += amp;
                int stride = coarse / cell + 2;
                var lat = new float[stride * stride];
                for (int i = 0; i < lat.Length; i++) lat[i] = (float)rng.NextDouble();
                for (int y = 0; y < coarse; y++)
                {
                    int gy = y / cell;
                    float ty = (y % cell) / (float)cell;
                    float sy = ty * ty * (3f - 2f * ty);
                    for (int x = 0; x < coarse; x++)
                    {
                        int gx = x / cell;
                        float tx = (x % cell) / (float)cell;
                        float sx = tx * tx * (3f - 2f * tx);
                        float v00 = lat[gy * stride + gx];
                        float v10 = lat[gy * stride + gx + 1];
                        float v01 = lat[(gy + 1) * stride + gx];
                        float v11 = lat[(gy + 1) * stride + gx + 1];
                        float top = v00 + (v10 - v00) * sx;
                        float bot = v01 + (v11 - v01) * sx;
                        noise[y * coarse + x] += (top + (bot - top) * sy) * amp;
                    }
                }
            }

            var rgba = new byte[size * size * 4];
            for (int y = 0; y < coarse; y++)
            {
                for (int x = 0; x < coarse; x++)
                {
                    float v = Math.Clamp(noise[y * coarse + x] / totalAmp, 0f, 1f);
                    byte a = v > 0.52f ? (byte)255 : (byte)0; // HARD threshold: solid or empty
                    int bx = x * scale;
                    int by = y * scale;
                    for (int oy = 0; oy < scale; oy++)
                    {
                        for (int ox = 0; ox < scale; ox++)
                        {
                            int i = ((by + oy) * size + (bx + ox)) * 4;
                            rgba[i] = 255; rgba[i + 1] = 255; rgba[i + 2] = 255;
                            rgba[i + 3] = a;
                        }
                    }
                }
            }
            return rgba;
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

        // Draws the cloud deck: a 5x5 grid quad centered on the camera at CloudWorldY, UVs
        // tiled by world position so the puffs stay anchored. Each vertex carries an edge fade
        // (radial distance from the deck center) so the outer rim - which lands near the
        // far-plane horizon - fades out instead of cutting hard.
        private void DrawClouds(CommandList cl)
        {
            if (_cloudPipeline == null || _cloudVertexBuffer == null || !_cameraPosition.HasValue) return;

            float extent = Math.Max(_farPlane, 768f);
            float camX = (float)_cameraPosition.Value.X;
            float camZ = (float)_cameraPosition.Value.Z;
            float y = CloudWorldY;

            // 25 grid vertices: position (camX - extent + gx/4 * 2extent), tiled UV, radial fade.
            float[] verts = new float[25 * 6];
            int v = 0;
            for (int gy = 0; gy < 5; gy++)
            {
                float tz = gy / 4f;
                float wz = camZ - extent + tz * extent * 2f;
                for (int gx = 0; gx < 5; gx++)
                {
                    float tx = gx / 4f;
                    float wx = camX - extent + tx * extent * 2f;
                    float dx = wx - camX;
                    float dz = wz - camZ;
                    float dist = (float)Math.Sqrt(dx * dx + dz * dz);
                    float fade = Math.Clamp(1f - (dist - (extent - CloudFadeWidth)) / CloudFadeWidth, 0f, 1f);
                    verts[v++] = wx;
                    verts[v++] = y;
                    verts[v++] = wz;
                    verts[v++] = wx / CloudTileSize;
                    verts[v++] = wz / CloudTileSize;
                    verts[v++] = fade;
                }
            }
            _gd.UpdateBuffer(_cloudVertexBuffer, 0, verts);

            float now = (float)_cloudClock.Elapsed.TotalSeconds;
            _cloudScrollU = (float)Math.IEEERemainder(_cloudScrollU + 0.002f * (now - _lastCloudTime), 1.0);
            _cloudScrollV = (float)Math.IEEERemainder(_cloudScrollV + 0.0007f * (now - _lastCloudTime), 1.0);
            _lastCloudTime = now;
            _cloudParams[0] = _cloudScrollU;
            _cloudParams[1] = _cloudScrollV;
            _cloudParams[2] = 0.65f * Math.Max(_nightSkyDim, 0.02f); // translucent, dims at night
            _cloudParams[3] = 0f;
            _gd.UpdateBuffer(_cloudParamsBuffer, 0, _cloudParams);

            cl.SetPipeline(_cloudPipeline);
            // Same projection as the world, so cloud depth tests correctly against terrain.
            cl.SetGraphicsResourceSet(0, _projViewSet);
            if (_cloudParamsSet != null) cl.SetGraphicsResourceSet(1, _cloudParamsSet);
            cl.SetVertexBuffer(0, _cloudVertexBuffer);
            cl.SetIndexBuffer(_cloudIndexBuffer, IndexFormat.UInt16);
            cl.DrawIndexed(16 * 6, 1, 0, 0, 0);
        }

        // Pixel-art colour-INVERTING crosshair. The blend is SUBTRACT: out = src - dst = white -
        // background, so the crosshair is always the exact inverse of what's behind it. The
        // geometry is four 2px-thick rectangles in NDC; integer pixel coords keep the edges crisp.
        private void CreateCrosshairPipeline(ResourceFactory factory)
        {
            string vsCode = @"#version 450
layout(location=0) in vec2 aPosition;
void main() { gl_Position = vec4(aPosition, 0.0, 1.0); }";
            string fsCode = @"#version 450
layout(location=0) out vec4 outColor;
void main() { outColor = vec4(1.0); }";

            var vsSpirv = SpirvCompilation.CompileGlslToSpirv(vsCode, "main", ShaderStages.Vertex, GlslCompileOptions.Default);
            var fsSpirv = SpirvCompilation.CompileGlslToSpirv(fsCode, "main", ShaderStages.Fragment, GlslCompileOptions.Default);
            var shaders = factory.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Vertex, vsSpirv.SpirvBytes, "main"),
                new ShaderDescription(ShaderStages.Fragment, fsSpirv.SpirvBytes, "main"));

            var vertexLayout = new VertexLayoutDescription(
                new VertexElementDescription("aPosition", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2));

            _crosshairPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription()
            {
                // out = src - dst: white minus the background = its exact inverse.
                BlendState = new BlendStateDescription(RgbaFloat.White, false, new BlendAttachmentDescription(
                    true,
                    BlendFactor.One, BlendFactor.One, BlendFunction.Subtract,
                    BlendFactor.One, BlendFactor.One, BlendFunction.Subtract)),
                DepthStencilState = DepthStencilStateDescription.Disabled,
                RasterizerState = new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.CounterClockwise, false, false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = Array.Empty<ResourceLayout>(),
                ShaderSet = new ShaderSetDescription(new[] { vertexLayout }, new[] { shaders[0], shaders[1] }),
                Outputs = _sc.Framebuffer.OutputDescription
            });

            _crosshairVertexBuffer = factory.CreateBuffer(new BufferDescription(
                4 * 4 * 2 * sizeof(float), BufferUsage.VertexBuffer | BufferUsage.Dynamic));
            _crosshairIndexBuffer = factory.CreateBuffer(new BufferDescription(
                4 * 6 * sizeof(ushort), BufferUsage.IndexBuffer));
            var idx = new ushort[4 * 6];
            for (int q = 0; q < 4; q++)
            {
                int b = q * 4;
                int o = q * 6;
                idx[o] = (ushort)b; idx[o + 1] = (ushort)(b + 1); idx[o + 2] = (ushort)(b + 2);
                idx[o + 3] = (ushort)b; idx[o + 4] = (ushort)(b + 2); idx[o + 5] = (ushort)(b + 3);
            }
            _gd.UpdateBuffer(_crosshairIndexBuffer, 0, idx);
        }

        // First-person hand: the player's right arm rendered in CAMERA space with depth off so
        // it's always visible like MC's hand. The shoulder sits off-screen up-right and the arm
        // angles down toward the camera, so the HAND lands in the bottom-right quadrant.
    }
}