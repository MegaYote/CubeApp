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

        // Loads a crafting panel art file into the given GPU slots. Returns false (leaving the
        // handles zero) when the file is missing or unreadable, so callers can fall back to
        // plain ImGui-drawn slots.
        private bool LoadCraftingPanel(string fileName, out Texture? texture, out TextureView? view, out IntPtr imguiId)
        {
            texture = null; view = null; imguiId = IntPtr.Zero;
            try
            {
                byte[]? bytes = LoadImageBytes(fileName);
                if (bytes == null) return false;
                var image = StbImageSharp.ImageResult.FromMemory(bytes, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
                var texDesc = TextureDescription.Texture2D((uint)image.Width, (uint)image.Height, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled);
                texture = _gd.ResourceFactory.CreateTexture(texDesc);
                _gd.UpdateTexture(texture, image.Data, 0, 0, 0, (uint)image.Width, (uint)image.Height, 1, 0, 0);
                view = _gd.ResourceFactory.CreateTextureView(texture);
                if (_imguiRenderer != null)
                    imguiId = _imguiRenderer.GetOrCreateImGuiBinding(_gd.ResourceFactory, view);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Loads the workbench crafting menu background (user's 111x49 design) and exposes it to
        // ImGui so the crafting window can paint it as its panel.
        private void LoadCraftingTexture()
        {
            if (!LoadCraftingPanel("crafting.png", out _craftingTexture, out _craftingView, out _craftingImGuiId))
            {
                // ignore; the crafting menu falls back to plain ImGui slots
            }

            // The 3x3 table panels. Either may be absent; the window then draws the 3x3 grid
            // with plain rectangles instead.
            LoadCraftingPanel("bigcrafting.png", out _bigCraftingTexture, out _bigCraftingView, out _bigCraftingImGuiId);
            LoadCraftingPanel("masonrycrafting.png", out _masonryCraftingTexture, out _masonryCraftingView, out _masonryCraftingImGuiId);
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
        /// <summary>
        /// Rebuilds the inventory icon atlases if the block/item registries have been swapped
        /// since they were last built (which happens whenever a world is started in the other
        /// gamemode). Cheap to call every frame: it is two integer compares unless something
        /// actually changed, and the rebuild itself is a no-op when the generations match.
        /// </summary>
        public void EnsureIconAtlasesCurrent()
        {
            if (_gd == null || _imguiRenderer == null) return;
            int blockGen = BlockRegistry.LoadGeneration;
            int itemGen = ItemRegistry.LoadGeneration;
            if (blockGen == _iconAtlasBlockGeneration && itemGen == _iconAtlasItemGeneration) return;

            DisposeIconAtlases();
            BuildIconAtlas();
            BuildItemIconAtlas();
            _iconAtlasBlockGeneration = blockGen;
            _iconAtlasItemGeneration = itemGen;
        }

        /// <summary>
        /// Releases ONLY the resources that BuildIconAtlas and BuildItemIconAtlas allocate, so
        /// they can be rebuilt without leaking. Deliberately does NOT touch
        /// _itemsAtlasTexture/_itemsAtlasView/_itemsImGuiId - those are the RAW items.png
        /// binding created once in Initialize and owned by Dispose(); nothing on this rebuild
        /// path recreates them, and both TryGetFlatSpriteUv and TryGetBlockFlatSpriteUv (the
        /// flat-sprite render used for items and for blocks like sap that carry an items.json
        /// itemTile override) return that ImGui id, so dropping it would blank every flat
        /// sprite in the game.
        /// </summary>
        private void DisposeIconAtlases()
        {
            // Block icon atlas (BuildIconAtlas).
            if (_iconAtlasTexture != null && _imguiRenderer != null) _imguiRenderer.RemoveImGuiBinding(_iconAtlasTexture);
            _iconAtlasView?.Dispose();
            _iconAtlasTexture?.Dispose();
            _iconAtlasTexture = null;
            _iconAtlasView = null;
            _iconImGuiId = IntPtr.Zero;
            _blockIconUv = null;

            // Flat item icon atlas (BuildItemIconAtlas).
            if (_itemIconAtlasTexture != null && _imguiRenderer != null) _imguiRenderer.RemoveImGuiBinding(_itemIconAtlasTexture);
            _itemIconAtlasView?.Dispose();
            _itemIconAtlasTexture?.Dispose();
            _itemIconAtlasTexture = null;
            _itemIconAtlasView = null;
            _itemIconImGuiId = IntPtr.Zero;
            _itemIconUv = null;
        }

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

        // Item id -> mob id, for the items whose icon should be a render of their ENTITY rather
        // than the items.png sprite: the vehicles. Both halves are looked up in the registries, so
        // a pack that has no boat (or no BoatMob folder) simply keeps the flat sprite.
        private static readonly (string Item, string Mob)[] EntityIconItems =
        {
            ("cart", "cart"),
            ("boat", "boat"),
        };

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

            // Vehicles are the one kind of item that is really an ENTITY: a flat items.png sprite
            // says nothing about what you are about to set down on the rails or in the water, so
            // these draw the real model instead - the same GLB/bbmodel the world loads,
            // software-rasterized by DrawEntityIcon below. The models are loaded here and disposed
            // once the atlas is baked, because the renderer's shared mob models are built lazily,
            // long after this atlas exists. Missing item or model simply falls back to the tile.
            var entityIcons = new Dictionary<int, MobModel>();
            foreach (var (itemName, mobId) in EntityIconItems)
            {
                try
                {
                    if (!ItemRegistry.TryGet(itemName, out var itemDef)) continue;
                    var mobDef = MobRegistry.Get(mobId);
                    if (mobDef == null) continue;

                    var model = new MobModel(_gd);
                    if (model.Load(mobDef.ModelPath, mobDef.TexturePath)) entityIcons[itemDef.NumericId] = model;
                    else model.Dispose();
                }
                catch
                {
                    // An icon is never worth failing the atlas for; the flat tile still shows.
                }
            }

            for (int k = 0; k < count; k++)
            {
                int itemId = ItemRegistry.ItemIdBase + k;
                int cellX = (k % cols) * iconSize;
                int cellY = (k / cols) * iconSize;

                if (entityIcons.TryGetValue(itemId, out var iconModel))
                {
                    int di = (cellY * atlasW + cellX) * 4;
                    DrawEntityIcon(iconData, di, atlasW, iconModel);
                }
                else
                {
                    var tile = ItemRegistry.GetTile(itemId, out _);
                    if (tile.Width <= 0) continue;

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
                }

                _itemIconUv[k] = new Vector4(
                    cellX / (float)atlasW, cellY / (float)atlasH,
                    iconSize / (float)atlasW, iconSize / (float)atlasH);
            }

            foreach (var model in entityIcons.Values) model.Dispose();

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
            // Check if this item has a dedicated items.png tile (like sap, flint, etc.)
            // If it has an items.png tile, use the item atlas instead of the block atlas.
            ItemRegistry.GetTile(itemId, out bool fromItemsAtlas);
            if (fromItemsAtlas)
            {
                int rel = itemId - ItemRegistry.ItemIdBase;
                if (rel >= 0 && _itemIconUv != null && rel < _itemIconUv.Length)
                {
                    texId = _itemIconImGuiId;
                    return _itemIconUv[rel];
                }
                // For blocks with items.json overrides (like sap), they're not in the item atlas
                // Cut their tile straight from the raw items.png atlas (same UV math as the
                // flat-sprite slot renderers) so they render everywhere, not just in inventory.
                var tile = ItemRegistry.GetTile(itemId, out _);
                if (_itemsImGuiId != IntPtr.Zero && tile.Width > 0 && tile.Height > 0)
                {
                    texId = _itemsImGuiId;
                    float atlasW = Math.Max(1f, _itemsAtlasPixelsW);
                    float atlasH = Math.Max(1f, _itemsAtlasPixelsH);
                    return new Vector4(
                        tile.X / atlasW, tile.Y / atlasH,
                        tile.Width / atlasW, tile.Height / atlasH);
                }
                return default;
            }
            // Otherwise use block atlas for blocks
            if (itemId >= 0 && itemId < BlockRegistry.Count && _blockIconUv != null && itemId < _blockIconUv.Length)
            {
                texId = _iconImGuiId;
                return _blockIconUv[itemId];
            }
            // Fallback to genuine items (appended after blocks)
            int rel2 = itemId - ItemRegistry.ItemIdBase;
            if (rel2 >= 0 && _itemIconUv != null && rel2 < _itemIconUv.Length)
            {
                texId = _itemIconImGuiId;
                return _itemIconUv[rel2];
            }
            texId = IntPtr.Zero;
            return default;
        }

        // Gets the flat sprite UV + texture for an item that has an items.png tile.
        // Returns true if the item has a flat sprite representation (items.png tile).
        public bool TryGetFlatSpriteUv(int itemId, out Vector4 uv, out IntPtr texId)
        {
            texId = IntPtr.Zero;
            uv = default;
            ItemRegistry.GetTile(itemId, out bool fromItemsAtlas);
            if (!fromItemsAtlas) return false;

            // Get the tile from ItemRegistry
            var tile = ItemRegistry.GetTile(itemId, out _);
            if (tile.Width <= 0 || tile.Height <= 0) return false;

            // Raw items.png texture (UVs below are in raw-atlas space)
            texId = _itemsImGuiId;

            // Items atlas dimensions
            float atlasW = Math.Max(1f, _itemsAtlasPixelsW);
            float atlasH = Math.Max(1f, _itemsAtlasPixelsH);

            uv = new Vector4(
                tile.X / atlasW,
                tile.Y / atlasH,
                tile.Width / atlasW,
                tile.Height / atlasH);
            return true;
        }

        // Gets the flat sprite UV + texture for a block that has an items.png override (like sap).
        // These are blocks that have an items.json entry with itemTile, but their numeric ID is in the block range.
        public bool TryGetBlockFlatSpriteUv(int blockId, out Vector4 uv, out IntPtr texId)
        {
            texId = IntPtr.Zero;
            uv = default;

            // Check if this block has an items.png tile override via ItemRegistry
            ItemRegistry.GetTile(blockId, out bool fromItemsAtlas);
            if (!fromItemsAtlas) return false;

            // Get the tile from ItemRegistry (this works for blocks with items.json overrides)
            var tile = ItemRegistry.GetTile(blockId, out _);
            if (tile.Width <= 0 || tile.Height <= 0) return false;

            // Raw items.png texture
            texId = _itemsImGuiId;

            // Items atlas dimensions
            float atlasW = Math.Max(1f, _itemsAtlasPixelsW);
            float atlasH = Math.Max(1f, _itemsAtlasPixelsH);

            uv = new Vector4(
                tile.X / atlasW,
                tile.Y / atlasH,
                tile.Width / atlasW,
                tile.Height / atlasH);
            return true;
        }

        // Software-renders a block icon from the REAL mesher output: builds a tiny 16x16x16 chunk
        // with the block at (8,8,8), runs Mesher.GenerateMesh (the same mesh builder the world uses),
        // then rasterizes the actual MeshFaces into the 48px cell. Every shape - full cubes, slabs,
        // stairs, glass, water - renders exactly like it does in the world, so there is ONE source
        // of truth and the GUI can never drift from the game. Two shapes are exceptions that draw
        // their sprite flat instead, because projecting their real geometry makes them unreadable:
        // cross plants (thin diagonal billboards) and rails (a sixteenth of a block tall).
        private void DrawMeshIcon(byte[] dst, int cellDi, int atlasW, int blockId)
        {
            // Cross plants are drawn as their FLAT sprite tile (like Cubuild's cross shape), not as
            // the 3D crossed billboards - those project as thin diagonal slivers in an isometric icon.
            if (BlockRegistry.IsCross(blockId))
            {
                var def = BlockRegistry.GetById(blockId);
                var tile = def.FaceTexture(new Point3D(0, 0, -1));
                if (tile.Width == 0) tile = def.AllTexture ?? default;
                DrawFlatSprite(dst, cellDi, atlasW, tile, CrossIconPadX, CrossIconPadY);
                return;
            }

            // Rails are a sixteenth of a block tall, so meshed and projected they come out as a
            // thin diagonal streak across the cell - it reads as a smudge, not as track. Draw the
            // tile flat and top-down instead: the vanilla item-icon look. The tile comes from the
            // SAME lookup the world uses (GetRailTexture), and it is the straight shape, so the
            // icon is the real art at its canonical orientation rather than a re-drawn stand-in.
            if (BlockRegistry.IsRail(blockId))
            {
                DrawFlatSprite(dst, cellDi, atlasW, RailShape.GetRailTexture(RailShape.FlatNorthSouth), 0f, 0f);
                return;
            }

            var chunk = new Chunk(16, 16, 16, 0, 0, 0);
            chunk[8, 8, 8] = blockId;
            // Stairs use metadata for facing. The menu icon shows a canonical orientation: the LOW
            // step toward the viewer (front-bottom) and the HIGH step at the back - that is meta 1
            // for this icon camera (+X,+Y,-Z). Force it so every stair icon looks the same classic
            // way instead of whichever placement-facing the mesh happens to use.
            if (BlockRegistry.IsStair(blockId)) chunk.SetMeta(8, 8, 8, 1);
            if (BlockRegistry.IsDoor(blockId))
            {
                // Doors are two cells tall and thin - meshing a lone half would give the menu a
                // sliver. Build the full closed pair (meta 0 = north facing, left hinge) so the
                // E-menu icon shows a recognisable shut door.
                int other = BlockRegistry.DoorOtherHalfId(blockId);
                if (other >= 0)
                {
                    if (BlockRegistry.IsDoorTop(blockId)) { chunk[8, 8, 8] = other; chunk[8, 9, 8] = blockId; }
                    else { chunk[8, 8, 8] = blockId; chunk[8, 9, 8] = other; }
                }
                chunk.SetMeta(8, 8, 8, 0);
                chunk.SetMeta(8, 9, 8, 0);
            }
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

        // Flat-sprite icon: stretches a tile into the 48px cell with a little padding, for shapes
        // whose real geometry is too thin or too diagonal to read as an icon. Cross plants use
        // Cubuild's padding (10/6 on a 64 canvas => 7.5/4.5 on 48), so flowers/mushrooms/saplings
        // show as their real sprite instead of a squished 3D diagonal. Rails use none: the rail
        // tile already carries its own margin, and it should fill the slot the way a vanilla
        // item icon does.
        private void DrawFlatSprite(byte[] dst, int cellDi, int atlasW, TextureRect tile, float padX, float padY)
        {
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

        /// <summary>Icon padding for cross plants, in 48px cells (10/6 on Cubuild's 64 canvas).</summary>
        private const float CrossIconPadX = 7.5f;
        private const float CrossIconPadY = 4.5f;

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

        // ---- entity icons ----------------------------------------------------------

        // One triangle of an entity model in bind pose, carrying the per-vertex data the world's
        // model shader would have consumed (uv + face shade), so the software path can reproduce it.
        private struct IconTri
        {
            public Vector3 A, B, C;
            public Vector2 Ua, Ub, Uc;
            public float Sa, Sb, Sc;
            public Vector3 Centre; // model-space centroid, for the painter's sort
        }

        // Software-renders an ENTITY model into a 48px icon cell. Block icons go through the
        // mesher; an entity has no mesher output, so this walks the model's own triangles instead -
        // the very geometry the world uploads to the GPU. Bind pose only (baked node rotation +
        // pivot, no animation), because an icon is a still. The triangles are fit to the cell
        // through the same isometric camera the block icons use, depth-sorted far-to-near and
        // rasterized with the model's own texture and face shades - so the cart's icon literally
        // IS the cart entity, and the two can never drift apart.
        private void DrawEntityIcon(byte[] dst, int cellDi, int atlasW, MobModel model)
        {
            var parts = model.Parts;
            var tex = model.CpuTexture;
            if (parts.Count == 0 || tex == null || tex.Length == 0) return;
            int texW = model.CpuTextureWidth, texH = model.CpuTextureHeight;
            if (texW <= 0 || texH <= 0) return;

            var tris = new List<IconTri>();
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);

            foreach (var part in parts)
            {
                bool rotated = part.StaticRotation != Quaternion.Identity;
                for (int i = 0; i + 2 < part.Indices.Length; i += 3)
                {
                    int ia = part.Indices[i], ib = part.Indices[i + 1], ic = part.Indices[i + 2];
                    if (ia >= part.Positions.Length || ib >= part.Positions.Length || ic >= part.Positions.Length) continue;

                    var a = BindPose(part, ia, rotated);
                    var b = BindPose(part, ib, rotated);
                    var c = BindPose(part, ic, rotated);

                    tris.Add(new IconTri
                    {
                        A = a,
                        B = b,
                        C = c,
                        Ua = part.Uvs[ia],
                        Ub = part.Uvs[ib],
                        Uc = part.Uvs[ic],
                        Sa = IconShade(part.Shades[ia]),
                        Sb = IconShade(part.Shades[ib]),
                        Sc = IconShade(part.Shades[ic]),
                        Centre = (a + b + c) / 3f,
                    });

                    min = Vector3.Min(min, Vector3.Min(a, Vector3.Min(b, c)));
                    max = Vector3.Max(max, Vector3.Max(a, Vector3.Max(b, c)));
                }
            }
            if (tris.Count == 0) return;

            // Isometric camera matching the block icons (+X,+Y,-Z, see ProjectIconPoint): +X runs to
            // the screen right, +Z to the left, +Y up. It is applied about the model's bounding
            // centre, so whatever origin a given model was authored with, the icon sits centred.
            var centre = (min + max) * 0.5f;
            Vector2 Proj(Vector3 v)
            {
                float dx = v.X - centre.X, dy = v.Y - centre.Y, dz = v.Z - centre.Z;
                return new Vector2(13.5f * (dx + dz), 6.75f * (dx - dz) - 19.5f * dy);
            }

            // Fit the projected silhouette to the cell: measure it first, then pick the uniform
            // scale that seats the larger axis inside 0.84 of the cell (the same breathing room the
            // cube icons keep), and offset so its centre lands in the cell's centre.
            float rMinX = float.MaxValue, rMaxX = float.MinValue, rMinY = float.MaxValue, rMaxY = float.MinValue;
            void Fit(Vector2 p)
            {
                if (p.X < rMinX) rMinX = p.X;
                if (p.X > rMaxX) rMaxX = p.X;
                if (p.Y < rMinY) rMinY = p.Y;
                if (p.Y > rMaxY) rMaxY = p.Y;
            }
            foreach (var t in tris) { Fit(Proj(t.A)); Fit(Proj(t.B)); Fit(Proj(t.C)); }

            float extent = MathF.Max(rMaxX - rMinX, rMaxY - rMinY);
            if (extent < 1e-4f) return;
            float fit = IconCellSize * 0.84f / extent;
            float midX = (rMinX + rMaxX) * 0.5f * fit;
            float midY = (rMinY + rMaxY) * 0.5f * fit;
            const float cellMid = IconCellSize * 0.5f;
            Vector2 Screen(Vector2 p) => new Vector2(cellMid + p.X * fit - midX, cellMid + p.Y * fit - midY);

            // Painter's algorithm, exactly as in the block path: depth grows toward -X, +Z, -Y for a
            // camera at +X,+Y,-Z, so the key is (-x + z - y) and the farthest triangle paints first.
            for (int i = 1; i < tris.Count; i++)
            {
                var t = tris[i];
                float key = TriDepthKey(t.Centre);
                int j = i;
                for (; j > 0 && TriDepthKey(tris[j - 1].Centre) < key; j--) tris[j] = tris[j - 1];
                tris[j] = t;
            }

            foreach (var t in tris)
            {
                RasterizeIconTri(dst, cellDi, atlasW,
                    Screen(Proj(t.A)), Screen(Proj(t.B)), Screen(Proj(t.C)),
                    t.Ua, t.Ub, t.Uc, t.Sa, t.Sb, t.Sc, tex, texW, texH);
            }
        }

        // Bind pose, mirroring WriteInstance with no animation: the mesh is stored relative to the
        // part's pivot, so the node's baked rotation turns it about that pivot and the pivot places
        // it. An identity rotation is just a translation.
        private static Vector3 BindPose(MobModel.ModelPart part, int i, bool rotated)
        {
            var v = part.Positions[i];
            if (rotated) v = Vector3.Transform(v, part.StaticRotation);
            return v + part.Pivot;
        }

        // WriteInstance treats an unset (zero) shade as "unshaded" - a part the mesher never gave a
        // face normal - and draws it full bright. Same rule here, so icon and entity agree.
        private static float IconShade(float shade) => shade <= 0f ? 1f : shade;

        private static float TriDepthKey(Vector3 c) => -c.X + c.Z - c.Y;

        // Rasterizes one model triangle into the icon cell: barycentric fill, uv and shade
        // interpolated the same way the GPU interpolates varyings, nearest-sampled from the model
        // texture. Alpha under 0.5 is dropped, matching the model shader's `discard`, so the icon
        // has the same cutout silhouette the entity itself has.
        private static void RasterizeIconTri(byte[] dst, int cellDi, int atlasW,
            Vector2 p0, Vector2 p1, Vector2 p2,
            Vector2 uv0, Vector2 uv1, Vector2 uv2,
            float s0, float s1, float s2,
            byte[] tex, int texW, int texH)
        {
            float minX = Math.Min(Math.Min(p0.X, p1.X), p2.X);
            float maxX = Math.Max(Math.Max(p0.X, p1.X), p2.X);
            float minY = Math.Min(Math.Min(p0.Y, p1.Y), p2.Y);
            float maxY = Math.Max(Math.Max(p0.Y, p1.Y), p2.Y);

            int ix0 = Math.Max(0, (int)MathF.Floor(minX));
            int ix1 = Math.Min(IconCellSize - 1, (int)MathF.Ceiling(maxX));
            int iy0 = Math.Max(0, (int)MathF.Floor(minY));
            int iy1 = Math.Min(IconCellSize - 1, (int)MathF.Ceiling(maxY));
            if (ix0 > ix1 || iy0 > iy1) return;

            float e01 = p1.X - p0.X, e02 = p1.Y - p0.Y;
            float e11 = p2.X - p0.X, e12 = p2.Y - p0.Y;
            float area = e01 * e12 - e02 * e11;
            if (MathF.Abs(area) < 1e-6f) return;

            // Screen space has Y pointing down, which flips winding versus the model. As in the block
            // rasterizer, normalize to a positive area by swapping the two edge vertices rather than
            // rejecting clockwise triangles: models are drawn double-sided in the world
            // (FaceCullMode.None), so they must be double-sided here too.
            if (area < 0f)
            {
                area = -area;
                (p1, p2) = (p2, p1);
                (uv1, uv2) = (uv2, uv1);
                (s1, s2) = (s2, s1);
                e01 = p1.X - p0.X; e02 = p1.Y - p0.Y;
                e11 = p2.X - p0.X; e12 = p2.Y - p0.Y;
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

                    float u = uv0.X * w0 + uv1.X * w1 + uv2.X * w2;
                    float v = uv0.Y * w0 + uv1.Y * w1 + uv2.Y * w2;
                    int tx = (int)(u * texW);
                    int ty = (int)(v * texH);
                    if (tx < 0) tx = 0; else if (tx >= texW) tx = texW - 1;
                    if (ty < 0) ty = 0; else if (ty >= texH) ty = texH - 1;
                    int ti = (ty * texW + tx) * 4;
                    if (tex[ti + 3] < 128) continue; // cutout, like the shader's discard

                    float shade = s0 * w0 + s1 * w1 + s2 * w2;
                    if (shade > 1f) shade = 1f;

                    int di = cellDi + (py * atlasW + px) * 4;
                    dst[di + 0] = (byte)(tex[ti + 0] * shade);
                    dst[di + 1] = (byte)(tex[ti + 1] * shade);
                    dst[di + 2] = (byte)(tex[ti + 2] * shade);
                    dst[di + 3] = 255;
                }
            }
        }

    }
}
