# Session Memory — CubeApp

## Project
C# `net9.0-windows` WinExe (self-contained), Veldrid 4.9.0 (D3D11), SPIR-V, ImGui, StbImageSharp, Nullable enabled.
Build: `dotnet build CubeApp.csproj -c Debug --nologo -v minimal` → 0 errors, ~38 pre-existing CS8618 warnings.
Run target: `bin\Debug\net9.0-windows\win-x64\CubeApp.exe` — the `bin\Debug\net9.0-windows\` parent-folder exe is a stale 7/3 apphost bootstrap that CRASHES on launch. Always run the win-x64 one. Verify the DLL timestamp freshened after each build. Repo: `https://github.com/MegaYote/CubeApp.git` (branch `main`).

## World/Vertex Format
- Vertex: 13 floats (`aPosition` F3, `aLocalUV` F2, `aTileRect` F4, `aColor` F4); `VertexStrideBytes = 52`.
- Chunks 16x256x16, `OriginY = -64`, sea level Y=0; sky light 0..15, Manhattan falloff, spreads horizontal/down only (no +Y in `SkyDirs`).
- D3D11: indirect-args buffers must NOT be `BufferUsage.Dynamic` (else `E_INVALIDARG`). `IndirectCommandStride = 20`, initial VB cap 4MB / IB cap 2MB, chunk-local UInt16 indices, `FirstIndex`/`VertexOffset`.

## Water (Infdev 20100630 port) — COMPLETED
- **All four phases build clean**: Phase 1 meta in `Chunk.cs`, Phase 2 `ChunkManager` meta + loaded-only writes, Phase 3 `FluidSimulation` + `BlockTickScheduler` (20 TPS, `MaxUpdatesPerTick=2048`, flushes `_meshScheduler.Update()`), Phase 4 water meshing in `Mesher.cs`.
- `FluidSimulation.BlockFlowing` is a faithful port of `BlockFlowing.java`; water tick 5; `OnBlockChanged` wakes self + neighbors; `onBlockAdded` self-wake fix.
- Water: `blocks.json` `"texture":"12,14"`, `"solid":false`, `"opaque":false`, `"transparent":true`, `"alpha":0.65`. `sideTile = baseTile + TileSize` on X.
- **Mesher**: `EmitWaterFaces` (water-only faces), `EmitWaterCellFaces`, `EmitWaterSide` (4-point signature), `GetFluidHeight`, `GetBlockAt`/`GetMetaAt`/`FindChunk` (missing chunk → air/0 via `chunkLookup`). `getPercentAir` clamps meta ≥ 8 → 0 exactly as reference.
- **Chunk-border geometry**: `MeshWorker` passes target + 4 cardinals + 4 DIAGONAL neighbors to `GenerateMesh` (corner heights sample the 2x2 block neighbourhood around each corner, which crosses into the diagonal chunk at a 4-chunk junction). `MarkDirty` in `ChunkManager` flags cardinal AND diagonal border neighbors.
- **Side-face winding fixed** (was culled from view side): all four `EmitWaterSide` quads use bottom-pair-first + sloped top edge, matching the greedy pass ordering.
- **Transparent two-pass renderer fix (8/4, verified by user "it looks awesome now")**: water walls at chunk borders rendered ghosty/see-through because the single blended pass with depth-write ON let a water chunk drawn first depth-block the terrain behind it. Fix in `VeldridRenderer.cs`: `BuildMesh` splits faces into opaque (alpha ≥ 1) + transparent (alpha < 1) arrays; each chunk uploads TWO `ChunkRange`s into the same mega buffers (`_chunkRanges` + `_transparentRanges`); `RebuildDrawCommands` builds two command lists; draw loop calls `DrawWorldPass` twice — opaque with `_pipeline` (depth-write on), then transparent with `_transparentPipeline` (SingleAlphaBlend, `DepthStencilStateDescription(true, false, LessEqual)` = depth test on, write off). `PendingUpload` carries both pairs; `FreeChunkRange` frees both.
- **Known small artifact (not the report, not yet fixed)**: side-wall UV in `TryGetCubuildFaceAxes` maps V from Y with `vAxis=(0,-1,0)` — shows the top strip of the side tile; MC anchors the tile bottom at the waterline instead. Optional follow-up: MC-style side-wall UV anchoring.
- **Water rendering fixes (8/5, verified by user "yay <3 pretty blue water")**:
  - **THE BIG ONE — light Y mismatch**: the water pass called `ChunkLighting.GetLight` with WORLD Y (-64..191) but the light array is indexed by LOCAL Y (0..255, OriginY=-64). Sea-level water (world Y=0) read array index 0 = the BOTTOM bedrock block → light 0 → brightness floor 0.05 → water RGB crushed to near-black. Fixed: all three water light samples in `Mesher.cs` convert `wy - ChunkManager.WorldOriginY` (top face `ly`, `ly+1`; bottom `ly-1`; side `wy - WorldOriginY`). The greedy pass always used local Y, which is why terrain was lit fine.
  - **Side-wall UV anchoring (AnchorVBottom)**: added `MeshFace.AnchorVBottom` flag, set true in `EmitWaterSide`. `BuildMesh` shifts dv by `(1 - (maxV-minV))` so the tile bottom anchors to the block bottom and the surface cuts across — matches `RenderBlocks.renderBlockFluids` `((var51 + (1.0F - var31) * 16.0F) / 256.0F)`. Opaque faces (h=1) get offset 0, unaffected.
  - **Shader opacity fix**: fragment shader now `outColor = vec4(tex.rgb * vColor.rgb, vColor.a)` — block alpha governs opacity, not the atlas art's baked alpha (water tile ships ~138/255 ≈ 54% in terrain.png, halving effective opacity to ~46%).
  - **Water block alpha**: `blocks.json` water `alpha` 0.65 → 0.85.
  - **E-menu inventory (8/5, user-verified "perfect")**: press E toggles `inventoryOpen` in Program (releases mouse look when opening, re-locks when closing). `BuildHud()` passes `InventoryOpen` + per-slot hotbar contents (`_hotbarBlocks`, initialized from `BlockRegistry.Hotbar`) via `HudState`. Renderer: `SetUiInputSnapshot(snapshot)` feeds the real `InputSnapshot` to `_imguiRenderer.Update` (was `NullInputSnapshot.Instance` — ImGui was fully inert before), `DrawInventoryWindow` renders an ImGui grid of every block (icon + tooltip), clicks enqueue into `_inventorySelections` (ConcurrentQueue). Program polls `TryTakeInventorySelection` after `Render()` and drops the pick into `_hotbarBlocks[selectedSlot]` + closes + re-locks. `SetSelectedSlot` reads `_hotbarBlocks[slot]`. ImGui 1.89 gotcha: `ImageButton` takes a STRING id FIRST: `ImageButton($"##icon{id}", texId, size, uv0, uv1, bg, tint)` — no frame_padding param. New methods added to `IRenderer` too.
- **Probe harness**: `C:\Users\jimza\AppData\Local\Temp\opencode\waterprobe\` — self-contained console app referencing CubeApp project; generates chunks, builds lighting, prints water light values + mesh face alphas. IMPORTANT: the FIRST chunk in the list passed to `Mesher.GenerateMesh` is the mesher's target — put chunk (0,0) first when probing it.
- **Block catalogue port (8/5)**: ported 25 more full-cube blocks from the old `Cubuild.html` project (same atlas convention `tile(row,col)` == blocks.json `"col,row"`, verified against terrain.png). `blocks.json` now has 37 blocks: added bricks, log (side/top differ), leaves (alpha 0.85), glass (alpha 0.4), bomb, full_grass, sponge, wool, cage, darkstone, bluebrick, greenbrick, yellowbrick, sap, rottingwood, coalore, ironore, goldore, diamondore, bluestoneore, copperore, iron, gold, diamond, bookshelf. Grass now uses per-face tiles (top `0,0` / bottom `2,0` / side `3,0` = classic seam). Greedy pass uses `BlockRegistry.FaceTexture(normal)` so top/bottom/side overrides work.
- **Shader decision (8/5)**: block alpha (vColor.a) governs opacity, NOT atlas art's baked alpha. Rationale: water tile ships at ~54% baked alpha in terrain.png, which halved effective opacity; ignoring tex alpha keeps water at its configured 0.85. Tradeoff: glass/leaves render as uniform translucency (their per-pixel alpha is ignored) — classic per-pixel cutout (leaves holes) is a future alpha-test feature.
- **terrain.png is hard to modify on disk**: something holds an exclusive write lock (survived killing the VS Code C# Dev Kit language server + Restart Manager shows nothing). Don't plan around editing terrain.png in place; use block alpha in blocks.json instead.
- **NOT YET PORTED (needs engine features)**: slabs (`shape: slab_bottom/top`), cross-shape plants (sapling, red/yellow/blue flower, red/white/poison mushroom, spikes), torch (+ wall variants), fire, and lava (needs the fluid pass — lava would render as a cube via the greedy pass since only water is specially skipped).
- **Block icon atlas (8/5, user-verified "perfect")**: `VeldridRenderer.BuildIconAtlas()` renders a classic MC-style isometric cube icon per block into one RGBA texture (48px cells, 12/row) and exposes it to ImGui via `_imguiRenderer.GetOrCreateImGuiBinding(_gd.ResourceFactory, view)` (note: the binding API takes `(ResourceFactory, TextureView)`, not `(GraphicsDevice, Texture)`). Hotbar in `BuildHudUi` now `AddImage`s each block's cube instead of the flat map-color square. PROPORTIONS ARE THE EXACT Cubuild `drawProjectedBlockIcon` coords scaled to 48px cells: top=(24,4.5)(37.5,11.25)(24,18)(10.5,11.25), left=(10.5,11.25)(24,18)(24,37.5)(10.5,30.75), right=(24,18)(37.5,11.25)(37.5,30.75)(24,37.5). Affine inverses: top u=(su+2sv)/27,v=(2sv-su)/27; left u=(px-24+13.5)/13.5,v=(py-11.25-u*6.75)/19.5; right u=(px-24)/13.5,v=(py-18+u*6.75)/19.5. Face tiles via `FaceTexture` (top=+Y, left=-Z, right=+X). CPU atlas pixels captured in `_atlasRgba` from the decoded image. C# note: `float v` can't be declared at the loop-body level after a `float v` in a nested if-scope (CS0136) — use a distinct name (tv).

## Authoritative Shading Reference (user-provided): `IF-20100630-main` (Infdev 20100630, ~Alpha 1.1.2_01 era)
- `World.java` 1577-1585: `v = 1 - light/15`; `table[light] = (1-v)/(v*3+1) * 0.95 + 0.05` (gamma curve, 0.05 ambient floor).
- `RenderBlocks.java`: per-face multipliers bottom 0.5 / top 1.0 / N+S 0.8 / E+W 0.6; faces sample the ADJACENT EMPTY CELL they face into (e.g. bottom face samples `blockAccess, x, y-1, z`).
- **USER CONFIRMED (2026-07-31):** shading goal reached, Alpha 1.1.2_01 look accepted as-is. No further tuning requested.

## Regression harness
`C:\Users\jimza\AppData\Local\Temp\opencode\meshtest\` — console app referencing built `CubeApp.dll`; validates Brightness table + per-face shades. Run: `dotnet run` there.

## Build/run recipe
1. Kill any running `CubeApp` process.
2. `dotnet build CubeApp.csproj -c Debug --nologo -v minimal` in `C:\Users\jimza\CubeApp`.
3. Verify `bin\Debug\net9.0-windows\win-x64\CubeApp.dll` LastWriteTime freshened.
4. `Start-Process` the win-x64 exe.

## Next Moves
- Optional: MC-style side-wall UV anchoring (tile bottom fixed, top slides) for water walls — DONE 8/5 (AnchorVBottom flag).
- Optional: lava pass by copying the water simulation logic.
