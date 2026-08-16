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
        private void LoadInventoryTexture()
        {
            try
            {
                byte[]? bytes = LoadImageBytes("inventory.png");
                if (bytes == null) return;
                var image = StbImageSharp.ImageResult.FromMemory(bytes, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
                var texDesc = TextureDescription.Texture2D((uint)image.Width, (uint)image.Height, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled);
                _inventoryTexture = _gd.ResourceFactory.CreateTexture(texDesc);
                _gd.UpdateTexture(_inventoryTexture, image.Data, 0, 0, 0, (uint)image.Width, (uint)image.Height, 1, 0, 0);
                _inventoryView = _gd.ResourceFactory.CreateTextureView(_inventoryTexture);
                if (_imguiRenderer != null)
                {
                    _inventoryImGuiId = _imguiRenderer.GetOrCreateImGuiBinding(_gd.ResourceFactory, _inventoryView);
                }
            }
            catch
            {
                // ignore; the E-menu falls back to the plain grid
            }
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

        // Loads the healthbar sprite sheet and exposes it to ImGui so the HUD can draw a heart.
        private void LoadHealthbarTexture()
        {
            try
            {
                byte[]? bytes = LoadImageBytes("healthbar.png");
                if (bytes == null) return;
                var img = StbImageSharp.ImageResult.FromMemory(bytes, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
                var desc = TextureDescription.Texture2D((uint)img.Width, (uint)img.Height, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled);
                _healthbarTexture = _gd.ResourceFactory.CreateTexture(desc);
                _gd.UpdateTexture(_healthbarTexture, img.Data, 0, 0, 0, (uint)img.Width, (uint)img.Height, 1, 0, 0);
                _healthbarView = _gd.ResourceFactory.CreateTextureView(_healthbarTexture);

                // Build the flash mask: copy the sheet pixels, but keep only near-black outline
                // pixels (brightness < 16) as opaque white; everything else is fully transparent.
                // Drawn over the heart for a beat on any health change so the outline flashes white.
                var flashData = new byte[img.Data.Length];
                for (int i = 0; i + 3 < img.Data.Length; i += 4)
                {
                    int r = img.Data[i], g = img.Data[i + 1], b = img.Data[i + 2], a = img.Data[i + 3];
                    bool isDark = a > 0 && r < 16 && g < 16 && b < 16;
                    flashData[i] = flashData[i + 1] = flashData[i + 2] = (byte)(isDark ? 255 : 0);
                    flashData[i + 3] = (byte)(isDark ? a : 0);
                }
                var flashDesc = TextureDescription.Texture2D((uint)img.Width, (uint)img.Height, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled);
                _healthbarFlashTexture = _gd.ResourceFactory.CreateTexture(flashDesc);
                _gd.UpdateTexture(_healthbarFlashTexture, flashData, 0, 0, 0, (uint)img.Width, (uint)img.Height, 1, 0, 0);
                _healthbarFlashView = _gd.ResourceFactory.CreateTextureView(_healthbarFlashTexture);

                if (_imguiRenderer != null)
                {
                    _healthbarImGuiId = _imguiRenderer.GetOrCreateImGuiBinding(_gd.ResourceFactory, _healthbarView);
                    _healthbarFlashImGuiId = _imguiRenderer.GetOrCreateImGuiBinding(_gd.ResourceFactory, _healthbarFlashView);
                }
            }
            catch
            {
                // ignore; the HUD simply won't draw a heart if the texture can't load
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

            // Extract the right arm as the first-person hand mesh (shoulder pivot at the origin).
            foreach (var bone in _playerBones)
            {
                if (bone.Id != PlayerBoneId.RightArm) continue;
                _handIndices = (ushort[])bone.Indices.Clone();
                _handMesh = new float[bone.Vertices.Length * 9];
                int h = 0;
                foreach (var v in bone.Vertices)
                {
                    _handMesh[h++] = v.X - bone.PivotX;
                    _handMesh[h++] = v.Y - bone.PivotY;
                    _handMesh[h++] = v.Z - bone.PivotZ;
                    _handMesh[h++] = v.U;
                    _handMesh[h++] = v.V;
                    _handMesh[h++] = v.Shade;
                    _handMesh[h++] = v.Shade;
                    _handMesh[h++] = v.Shade;
                    _handMesh[h++] = 1f;
                }
                break;
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

        // Loads a model + texture for EVERY discovered mob (MobRegistry scans MobEntities/).
        // Each mob type gets its own MobModelEntry so the renderer is fully data-driven: drop a
        // <Type>Mob folder with a .glb + .png (+ optional .json config) and it just renders.
        private void LoadMobResources()
        {
            foreach (var def in MobRegistry.All)
            {
                try
                {
                    string key = def.Id.ToLowerInvariant();
                    if (_modelMobs.ContainsKey(key)) continue;
                    if (!File.Exists(def.ModelPath)) continue;

                    var model = new MobModel(_gd);
                    if (!model.Load(def.ModelPath, def.TexturePath)) continue;

                    model.ModelScale = def.Scale > 0f ? def.Scale : 1.0f;
                    model.YawCorrection = def.YawCorrection;
                    _modelMobs[key] = new MobModelEntry
                    {
                        Model = model,
                        TextureSet = model.TextureSet,
                    };
                }
                catch
                {
                    // ignore; this mob simply won't render if its model fails to load
                }
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

        // Renders flat 2D icons for GENUINE items (tools, food, gems): each item's items.png
        // tile is copied into its own icon-atlas cell, then bound to ImGui. Unlike blocks (which
        // get software-rendered 3D cube icons), items are sprites, exactly like Minecraft.
        private void BuildItemIconAtlas()
        {
            int itemCount = ItemRegistry.Count;
            if (itemCount <= ItemRegistry.ItemIdBase) return;
            if (_itemsAtlasRgba.Length == 0) return; // items.png failed to load; item icons fall back

            const int iconSize = 48;
            const int cols = 12;
            int count = itemCount - ItemRegistry.ItemIdBase;
            int rows = Math.Max(1, (int)Math.Ceiling(count / (double)cols));
            int atlasW = cols * iconSize;
            int atlasH = rows * iconSize;
            var iconData = new byte[atlasW * atlasH * 4];

            _itemIconUv = new Vector4[count];
            int tileStride = _itemsAtlasPixelsW * 4;

            for (int k = 0; k < count; k++)
            {
                int itemId = ItemRegistry.ItemIdBase + k;
                var tile = ItemRegistry.GetTile(itemId, out _);
                if (tile.Width <= 0) continue;
                int cellX = (k % cols) * iconSize;
                int cellY = (k / cols) * iconSize;

                // Nearest-neighbour upscale of the 16x16 tile into the 48x48 cell.
                for (int y = 0; y < iconSize; y++)
                {
                    int sy = Math.Min(_itemsAtlasPixelsH - 1, tile.Y + (y * tile.Height) / iconSize);
                    for (int x = 0; x < iconSize; x++)
                    {
                        int sx = Math.Min(_itemsAtlasPixelsW - 1, tile.X + (x * tile.Width) / iconSize);
                        int si = sy * tileStride + sx * 4;
                        int di = ((cellY + y) * atlasW + (cellX + x)) * 4;
                        iconData[di + 0] = _itemsAtlasRgba[si + 0];
                        iconData[di + 1] = _itemsAtlasRgba[si + 1];
                        iconData[di + 2] = _itemsAtlasRgba[si + 2];
                        iconData[di + 3] = _itemsAtlasRgba[si + 3];
                    }
                }

                _itemIconUv[k] = new Vector4(
                    cellX / (float)atlasW, cellY / (float)atlasH,
                    iconSize / (float)atlasW, iconSize / (float)atlasH);
            }

            _itemIconAtlasTexture = _gd.ResourceFactory.CreateTexture(TextureDescription.Texture2D(
                (uint)atlasW, (uint)atlasH, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled));
            _gd.UpdateTexture(_itemIconAtlasTexture, iconData, 0, 0, 0, (uint)atlasW, (uint)atlasH, 1, 0, 0);
            _itemIconAtlasView = _gd.ResourceFactory.CreateTextureView(_itemIconAtlasTexture);
            if (_imguiRenderer != null)
            {
                _itemIconImGuiId = _imguiRenderer.GetOrCreateImGuiBinding(_gd.ResourceFactory, _itemIconAtlasView);
            }
        }

        // Resolves the ImGui icon (UVs + texture id) for a stack id: blocks use the isometric
        // cube icon atlas, genuine items use the flat sprite icon atlas.
        private Vector4 IconUv(int itemId, out IntPtr texId)
        {
            texId = _itemIconImGuiId;
            if (itemId >= 0 && itemId < BlockRegistry.Count && _blockIconUv != null && itemId < _blockIconUv.Length)
            {
                texId = _iconImGuiId;
                return _blockIconUv[itemId];
            }
            int rel = itemId - ItemRegistry.ItemIdBase;
            if (rel >= 0 && _itemIconUv != null && rel < _itemIconUv.Length)
            {
                texId = _itemIconImGuiId;
                return _itemIconUv[rel];
            }
            texId = IntPtr.Zero;
            return default;
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

        // Fixed per-face light multipliers: bottom 0.5 / top 1.0 /
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
            // Icons use STUDIO lighting - the fixed per-face multiplier (top 1.0, bottom 0.5,
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
        // Per-face shade multiplier (top 1.0, N+S 0.8, E+W 0.6) so the icon cubes read like
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

    }
}