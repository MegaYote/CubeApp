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
- Optional: MC-style side-wall UV anchoring (tile bottom fixed, top slides) for water walls.
- Optional: lava pass by copying the water simulation logic.
