using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using Veldrid;
using Veldrid.SPIRV;

namespace CubeApp.Renderer
{
    public sealed partial class VeldridRenderer : IRenderer, IDisposable
    {
        public void Resize(int width, int height)
        {
            _sc?.Resize((uint)Math.Max(1, width), (uint)Math.Max(1, height));
            _imguiRenderer?.WindowResized(Math.Max(1, width), Math.Max(1, height));
            RecreateScaleTargets();
        }

        public void SetResolutionScale(float scale)
        {
            scale = Math.Clamp(scale, 0.25f, 1f);
            if (Math.Abs(scale - _resolutionScale) < 0.001f) return;
            _resolutionScale = scale;
            RecreateScaleTargets();
        }

        public void SetPixelatedUpscale(bool pixelated)
        {
            if (_pixelatedUpscale == pixelated) return;
            _pixelatedUpscale = pixelated;
            // Only the resource set binds the sampler, so just rebuild it with the new filter.
            RebuildBlitResourceSet();
        }

        private void RebuildBlitResourceSet()
        {
            if (_blitResourceSet != null) { _blitResourceSet.Dispose(); _blitResourceSet = null; }
            if (_blitLayout == null || _scaleColorView == null || _gd == null) return;
            var sampler = _pixelatedUpscale ? _blitSamplerNearest : _blitSamplerLinear;
            if (sampler != null)
            {
                _blitResourceSet = _gd.ResourceFactory.CreateResourceSet(
                    new ResourceSetDescription(_blitLayout, _scaleColorView, sampler));
            }
        }

        // (Re)creates the offscreen render target used when resolution scale < 1. At full scale
        // the renderer draws straight to the swapchain (zero overhead). Formats must match the
        // swapchain exactly so the existing world pipelines remain valid for the offscreen pass.
        private void RecreateScaleTargets()
        {
            if (_scaleColorTexture != null) { _scaleColorTexture.Dispose(); _scaleColorTexture = null; }
            if (_scaleColorView != null) { _scaleColorView.Dispose(); _scaleColorView = null; }
            if (_scaleDepthTexture != null) { _scaleDepthTexture.Dispose(); _scaleDepthTexture = null; }
            if (_scaleFramebuffer != null) { _scaleFramebuffer.Dispose(); _scaleFramebuffer = null; }
            if (_blitResourceSet != null) { _blitResourceSet.Dispose(); _blitResourceSet = null; }

            if (_resolutionScale >= 0.999f || _sc == null || _gd == null) return;

            uint w = Math.Max(1u, (uint)(_sc.Framebuffer.Width * _resolutionScale));
            uint h = Math.Max(1u, (uint)(_sc.Framebuffer.Height * _resolutionScale));
            var colorFmt = _sc.Framebuffer.OutputDescription.ColorAttachments[0].Format;
            var depthFmt = _sc.Framebuffer.OutputDescription.DepthAttachment.Value.Format;

            _scaleColorTexture = _gd.ResourceFactory.CreateTexture(TextureDescription.Texture2D(
                w, h, 1, 1, colorFmt, TextureUsage.RenderTarget | TextureUsage.Sampled));
            _scaleColorView = _gd.ResourceFactory.CreateTextureView(_scaleColorTexture);
            _scaleDepthTexture = _gd.ResourceFactory.CreateTexture(TextureDescription.Texture2D(
                w, h, 1, 1, depthFmt, TextureUsage.DepthStencil));
            _scaleFramebuffer = _gd.ResourceFactory.CreateFramebuffer(new FramebufferDescription(
                _scaleDepthTexture, _scaleColorTexture));
            RebuildBlitResourceSet();
        }

        // Draws the scaled world texture across the whole swapchain. Sampled with linear filtering
        // so the upscale looks smooth rather than blocky.
        private void BlitScaled(CommandList cl)
        {
            if (_blitPipeline == null || _blitResourceSet == null) return;
            cl.SetPipeline(_blitPipeline);
            cl.SetGraphicsResourceSet(0, _blitResourceSet);
            cl.Draw(3); // fullscreen triangle (no vertex buffer)
        }

        public void SetHud(HudState hud)
        {
            _hud = hud;
            // Any health change (gain OR damage) triggers the outline flash.
            if (hud.PlayerHealth != _lastHudHealth)
            {
                int diff = hud.PlayerHealth - _lastHudHealth;
                _lastHudHealth = hud.PlayerHealth;
                _healthFlashTimer = HealthFlashDuration;
                // Taking damage: kick the camera with a decaying POV shake; bigger hits shake harder.
                if (diff < 0)
                {
                    _damageShakeTime = DamageShakeDuration;
                    _damageShakeElapsed = 0f;
                    _damageShakeMagnitude = Math.Clamp(0.4f + (-diff) * 0.25f, 0f, 1f);
                }
            }
            // Compute night factors here (before Render) so the clear color, sky, fog, clouds and
            // mobs all use the CURRENT frame's time - no one-frame lag at the clear.
            ComputeNightFactors();
        }

        /// <summary>Feeds the active falling-block entities to the renderer (drawn as 3D cubes
        /// using the world pipeline so they depth-test against terrain).</summary>
        public void SetFallingBlocks(IReadOnlyList<CubeApp.FallingBlockData> fallingBlocks)
        {
            _fallingBlocks = fallingBlocks ?? Array.Empty<CubeApp.FallingBlockData>();
        }

        /// <summary>Feeds the survival item drops to the renderer (drawn as small cubes using
        /// the falling-block pipeline so they depth-test against terrain).</summary>
        public void SetItemDrops(IReadOnlyList<CubeApp.ItemDropRenderData> itemDrops)
        {
            _itemDrops = itemDrops ?? Array.Empty<CubeApp.ItemDropRenderData>();
        }

        // Builds cube geometry for all falling blocks into the scratch buffers and draws them.
        // Modeled on DrawParticles but with real 3D cube faces (per-face tile + shading).
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

        // Draws survival item drops as small tumbling cubes (same vertex layout as falling
        // blocks, but with the scaled mesh + a per-instance quaternion). Genuine items (flint,
        // tools, food) draw their flat sprite from the items atlas; block items from terrain.
        private void DrawItemDrops(CommandList cl)
        {
            int n = _itemDrops.Count;
            if (n == 0 || _itemDropPipeline == null || _itemDropVertexBuffer == null) return;

            // Two passes: 0 = block drops (tumbling cubes), 1 = genuine item drops (flat
            // camera-facing sprites from items.png, like the hotbar icons).
            for (int pass = 0; pass < 2; pass++)
            {
                int passCount = 0;
                for (int i = 0; i < n; i++)
                {
                    ItemRegistry.GetTile(_itemDrops[i].ItemId, out bool fromItems);
                    if ((pass == 0 && fromItems) || (pass == 1 && !fromItems)) continue;
                    passCount++;
                }
                if (passCount == 0) continue;
                if (pass == 1 && (_itemsTextureSet == null || _itemDropSpritePipeline == null)) continue; // items atlas missing

                float atlasW = Math.Max(1f, pass == 1 ? _itemsAtlasPixelsW : _atlasWidth);
                float atlasH = Math.Max(1f, pass == 1 ? _itemsAtlasPixelsH : _atlasHeight);

                // 11 floats per instance: worldPos (3) + tileRect (4) + rotation quat (4).
                int instFloats = passCount * 11;
                if (_itemDropInstanceScratch.Length < instFloats) _itemDropInstanceScratch = new float[instFloats];
                int vf = 0;
                const float halfScale = ItemDropScale * 0.5f;
                for (int i = 0; i < n; i++)
                {
                    var it = _itemDrops[i];
                    var tr = ItemRegistry.GetTile(it.ItemId, out bool fromItems);
                    if ((pass == 0 && fromItems) || (pass == 1 && !fromItems)) continue;
                    // Rotate around the cube's CENTER, so pass base + half-scale.
                    _itemDropInstanceScratch[vf++] = it.X + halfScale;
                    _itemDropInstanceScratch[vf++] = it.Y + halfScale;
                    _itemDropInstanceScratch[vf++] = it.Z + halfScale;
                    _itemDropInstanceScratch[vf++] = tr.X / atlasW;
                    _itemDropInstanceScratch[vf++] = tr.Y / atlasH;
                    _itemDropInstanceScratch[vf++] = tr.Width / atlasW;
                    _itemDropInstanceScratch[vf++] = tr.Height / atlasH;
                    _itemDropInstanceScratch[vf++] = it.RotX;
                    _itemDropInstanceScratch[vf++] = it.RotY;
                    _itemDropInstanceScratch[vf++] = it.RotZ;
                    _itemDropInstanceScratch[vf++] = it.RotW;
                }

                if (_itemDropInstanceBuffer == null || _itemDropInstanceCapacity < (uint)(instFloats * sizeof(float)))
                {
                    _itemDropInstanceBuffer?.Dispose();
                    _itemDropInstanceBuffer = _gd.ResourceFactory.CreateBuffer(new BufferDescription(
                        Math.Max((uint)(instFloats * sizeof(float)), 512), BufferUsage.VertexBuffer | BufferUsage.Dynamic));
                    _itemDropInstanceCapacity = Math.Max((uint)(instFloats * sizeof(float)), 512);
                }
                _gd.UpdateBuffer(_itemDropInstanceBuffer, 0, _itemDropInstanceScratch);

                cl.SetPipeline(pass == 1 ? _itemDropSpritePipeline : _itemDropPipeline);
                cl.SetGraphicsResourceSet(0, _projViewSet);
                if (pass == 1)
                {
                    if (_itemsTextureSet != null) cl.SetGraphicsResourceSet(1, _itemsTextureSet);
                    else continue;
                }
                else if (_textureSet != null) cl.SetGraphicsResourceSet(1, _textureSet);
                cl.SetGraphicsResourceSet(2, _fogSet);
                cl.SetVertexBuffer(0, pass == 1 ? _spriteVertexBuffer : _itemDropVertexBuffer);
                cl.SetVertexBuffer(1, _itemDropInstanceBuffer);
                cl.SetIndexBuffer(pass == 1 ? _spriteIndexBuffer : _itemDropIndexBuffer, IndexFormat.UInt16);
                cl.DrawIndexed(pass == 1 ? 6u : FallingCubeIndices, (uint)passCount, 0, 0, 0);
            }
        }

        public void SetEntities(IReadOnlyList<CubeApp.MobRenderData> mobRenderData)
        {
            // Route the unified MobRenderData snapshots to per-model instance lists. DuckInstance
            // carries exactly the fields both models need, so it doubles as the player instance.
            // Duck + player are hand-authored cube models; every OTHER mob type (coyote, zombie,
            // anything discovered in MobEntities/) renders through the generic MobModel entry.
            _allMobRenderData = mobRenderData ?? Array.Empty<CubeApp.MobRenderData>();
            _duckList.Clear();
            _playerList.Clear();
            foreach (var entry in _modelMobs.Values) entry.Instances.Clear();
            if (mobRenderData == null || mobRenderData.Count == 0)
            {
                _duckInstances = Array.Empty<CubeApp.DuckInstance>();
                _playerInstances = Array.Empty<CubeApp.DuckInstance>();
                return;
            }

            // Reuse the backing lists (no per-frame allocation - FPS roadmap #6).
            for (int i = 0; i < mobRenderData.Count; i++)
            {
                var md = mobRenderData[i];
                bool isDuck = string.Equals(md.MobType, "duck", StringComparison.OrdinalIgnoreCase);
                bool isPlayer = !isDuck && string.Equals(md.MobType, "player", StringComparison.OrdinalIgnoreCase);

                var inst = new CubeApp.DuckInstance(
                    md.Position, md.Yaw, md.HeadYawLocal,
                    md.WalkPhase, md.WalkAmount, md.AnimTime, md.AnimBlend, md.FlapPhase,
                    md.VelocityY, md.OnGround,
                    md.IsDead, md.DeathT, md.DeathRollDir, md.HurtTimer,
                    md.HeadPitchLocal, md.RenderScale);

                if (isDuck) { _duckList.Add(inst); continue; }
                if (isPlayer) { _playerList.Add(inst); continue; }

                // Generic data-driven path: any other registered mob type.
                string key = md.MobType.ToLowerInvariant();
                if (_modelMobs.TryGetValue(key, out var entry) && entry.Model != null)
                    entry.Instances.Add(inst);
            }

            _duckInstances = _duckList;
            _playerInstances = _playerList;
        }

        public void Render()
        {
            // Process pending removals/uploads on render thread
            while (_pendingRemovals.TryDequeue(out var rem))
            {
                FreeChunkRange(rem);
            }

            // Decay the health-outline flash (1/60s per frame; ImGui is updated at 60Hz).
            if (_healthFlashTimer > 0f) _healthFlashTimer -= 1f / 60f;
            // Decay the damage shake.
            if (_damageShakeTime > 0f)
            {
                _damageShakeTime -= 1f / 60f;
                _damageShakeElapsed += 1f / 60f;
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

            // Resolution scale: when active, the world renders into an offscreen framebuffer at
            // scale*window and is upscaled to the swapchain afterwards; UI (crosshair, ImGui)
            // still draws at native res so menus stay crisp. At full scale this is a no-op and
            // everything draws straight to the swapchain as before.
            bool scaled = _scaleFramebuffer != null;
            cl.SetFramebuffer(scaled ? _scaleFramebuffer : _sc.Framebuffer);
            cl.SetFullViewport(0);
            // Clear to the FOG color (0xC0D8FF dimmed by the celestial angle) - the same color the
            // sky horizon fades to and the world fog fades into. The sky planes sit at +-16 blocks,
            // so at eye level there's a gap between them where only the clear color shows; it must
            // match the horizon fog color or a stripe appears there.
            cl.ClearColorTarget(0, new RgbaFloat(
                (192f / 255f) * _nightSkyDim,
                (216f / 255f) * _nightSkyDim,
                1f * _nightSkyDim, 1f));
            cl.ClearDepthStencil(1f);

            // During world LOADING, don't draw the world/sky at all - just a solid dark
            // background + the progress UI. The terrain isn't ready yet, and the user wants
            // nothing visible until loading finishes.
            if (_hud.Menu != null && _hud.Menu.Screen == GameScreen.Loading)
            {
                // Loading is UI-only: draw at native resolution regardless of world scale.
                cl.SetFramebuffer(_sc.Framebuffer);
                cl.SetFullViewport(0);
                cl.ClearColorTarget(0, new RgbaFloat(0.12f, 0.12f, 0.14f, 1f));
                _imguiRenderer.Update(1f / 60f, _uiInputSnapshot ?? NullInputSnapshot.Instance);
                BuildHudUi();
                _imguiRenderer.Render(_gd, cl);
                cl.End();
                _gd.SubmitCommands(cl);
                _gd.SwapBuffers(_sc);
                return;
            }

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
                DrawWorldPass(cl, _drawCommands, _indirectScratch, _pipeline, ref _opaqueCullData);
                if (_cutoutDrawCommands.Count > 0)
                {
                    DrawWorldPass(cl, _cutoutDrawCommands, _cutoutIndirectScratch, _cutoutPipeline, ref _cutoutCullData);
                }
                if (_glassDrawCommands.Count > 0)
                {
                    SortPassBackToFront(_glassDrawCommands);
                    DrawWorldPass(cl, _glassDrawCommands, _glassIndirectScratch, _glassPipeline, ref _glassCullData);
                }
                // Depth-writing entities (falling blocks, item drops, mobs) draw AFTER the opaque
                // world so terrain occludes them, but BEFORE the water pass - water doesn't write
                // depth, so a submerged mob would otherwise paint over the surface. With their
                // depth written first, the nearer water surface tints them correctly.
                DrawFallingBlocks(cl);
                DrawItemDrops(cl);
                DrawDucks(cl);
                DrawPlayers(cl);
                DrawModelMobs(cl);
                if (_transparentDrawCommands.Count > 0)
                {
                    SortPassBackToFront(_transparentDrawCommands);
                    DrawWorldPass(cl, _transparentDrawCommands, _transparentIndirectScratch, _transparentPipeline, ref _transparentCullData);
                }
                // Translucent tint (colored glass) draws AFTER water so its semi-transparent pixels
                // paint over the water/terrain behind it without depth-blocking anything.
                if (_glassDrawCommands.Count > 0)
                {
                    SortPassBackToFront(_glassDrawCommands);
                    DrawWorldPass(cl, _glassDrawCommands, _glassIndirectScratch, _translucentPipeline, ref _glassCullData);
                }
                // All passes have now had a chance to refill their cull data this frame.
                _gpuCullDataDirty = false;
            }

            // Clouds blend OVER the world (same projection, depth test on / write off) so terrain
            // hides them from below and they blend over the land from above.
            DrawClouds(cl);

            DrawParticles(cl);
            DrawHighlight(cl);
            DrawShrinkCube(cl);
            DrawChunkBorders(cl);

            // Hand is world-space, drawn in the scaled pass. Crosshair + UI are overlay-space,
            // drawn at native resolution AFTER the blit so they stay crisp.
            DrawHand(cl);

            if (scaled)
            {
                cl.SetFramebuffer(_sc.Framebuffer);
                cl.SetFullViewport(0);
                BlitScaled(cl);
            }
            DrawCrosshair(cl);

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
        // Gated (FPS roadmap #2): only re-sorts when the camera crosses into a new chunk OR the
        // command list changed (streaming added/removed chunks). Within a chunk, the far-to-near
        // order is stable, so the O(n log n) sort doesn't need to run every frame.
    }
}