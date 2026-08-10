# Terrain / Biome Noise System — REFERENCE (pre-biome-map rewrite)

> **Purpose:** Preserves the CURRENT terrain noise composition before the biome-map
> rework. Kept for later use / comparison. The biome-map version will replace the
> biome-label logic in this file; this document captures what exists now so we can
> restore or mine it later.

**File:** `World/TerrainChunkProvider.cs` (as of 2026-08-09)
**Related files:** `World/NoiseGenerator.cs` (`PerlinNoise`, `SimplexNoise`, `NoiseOctaves`),
`World/MonolithSculptor.cs`, `World/QuartzVeinGenerator.cs`, `World/CoalOreGenerator.cs`

---

## 1. Noise primitives (`NoiseGenerator.cs`)

- `PerlinNoise(Random)` — classic improved Perlin, 512-entry permutation table, quintic fade,
  16-gradient hash. Output roughly `[-1, 1]`.
- `SimplexNoise(Random)` — simplex noise (Gustavson-style), `[-1, 1]`, used by octaves.
- `NoiseOctaves(Random, octaveCount, startIndex)` — layered FBM:
  - octave `i` samples at `coord / 2^(startIndex+i)` and accumulates `noise * 2^(startIndex+i)`
  - LOW-frequency octaves dominate (inverted from standard FBM)
  - `Noise2D(x,z)`, `Noise3D(x,y,z)` — RAW weighted sums (large magnitude, ~ ±weightSum)
  - `Noise2DNormalized`, `Noise3DNormalized` — divide by weightSum → ~[-1,1]

**Current octave configs in TerrainChunkProvider:**
| field | NoiseOctaves args | role |
|---|---|---|
| `_bodyA` | `(9, 7)` | terrain body, blended |
| `_bodyB` | `(9, 7)` | terrain body, blended |
| `_upperSelector` | `(7, 1)` | vertical upper/lower selector |
| `_surfaceA` | `(5, 1)` | sand/gravel biomes |
| `_surfaceB` | `(5, 1)` | dirt depth |
| `_continent` | `(9, 3)` | large-scale continent (raw, wide range) |
| `_relief` | `(7, 9)` | hills/mountains factor (now used NORMALIZED) |
| `_desert` | `(3, 2)` | broad desert regions (normalized) |

`ReliefFrequency = 130.0` (shared const; higher = smaller hills/mountains).

---

## 2. Density field (the core terrain shape)

Build a `5 x 17 x 5` density field (x/z in 4-block units, y in 8-block units) then
trilinearly interpolate into a 16x16x128 column.

```
baseFreq = 592.0
continent = _continent.Noise2D(xq, zq)                     // xq = chunkX*4+fx
relief = _relief.Noise2DNormalized(xq * 130, zq * 130)

elevation = (continent + 380.0) / 480.0
if (elevation > 1.0) elevation = 1.0

reliefShaped = |relief| * 2.6 - 2.6
if reliefShaped < 0:
    reliefShaped /= 2.4; if < -1.0 clamp -1.0; /= 1.7; /= 2.4
    elevation = 0.0
else:
    if > 1.0 clamp 1.0; /= 5.2
elevation += 0.5
reliefShaped = reliefShaped * 17 / 16

centerHeight = 8.5 + (elevation - 1.0) * 9.0 + reliefShaped * 3.6   // field-y surface line

for fy in 0..16:
    yq = fy
    bodyA = _bodyA.Noise3D(xq*592, yq*592, zq*592) / 480
    bodyB = _bodyB.Noise3D(xq*592, yq*592, zq*592) / 480
    selector = (_upperSelector.Noise3D(xq*592/96, yq*592/192, zq*592/96) / 11 + 1) / 2
    density = mix(bodyA, bodyB, selector)

    falloff = (fy - centerHeight) * 13.0 / elevation
    if falloff < 0: falloff *= 3.6
    density -= falloff

    if fy > 13:  // top 4 field rows forced air
        clamp = (fy - 13) / 3
        density = density*(1-clamp) + (-9)*clamp

    field[col + fy] = density
```

**Block fill:** `density > 0` → stone; else `localY < seaLevel(64)` → water; else air.

### The "bug monolith" (flat-top, hollow underside)
When `elevation` goes NEGATIVE (continent < -(380*2 + 240)), `13/elevation` inverts the
falloff → density is solid ABOVE the surface line, air below → a floating slab with a
hollow underside. This is a deliberate accidental feature. `continentBias = 380` controls
frequency: raising it pushes the negative-crossing deeper (rarer monoliths); lowering
makes them more common. The Ocean label check was patched to require `elevation > 0.3`
so monoliths don't read as ocean.

---

## 3. Surface pass (`ReplaceBlocks`)

Per column (top-down over the band):
- bedrock near the bottom (`bandLy <= rand.Next(6)-1`)
- surface stone → grass/dirt (or sand/gravel/desert per biomes), then `dirtDepth` fill
- `desert = _desert.Noise2DNormalized(wx*0.008, wz*0.008) > 0.28` → sand top + sand fill
  `dirtDepth = 4 + rand.Next(3)`, no grass
- `sandy`/`gravelly` from `_surfaceA` (rotated 121.037 offset for gravel)
- sea level `= 64`; below it + no top block → water

Chunk hash: `chunkX * 401719 + chunkZ * 811543 ^ seed`.

---

## 4. Caves (`GenerateCaves` / `GenerateCaveNode`)

- 17x17 region of neighbor-chunk seeds; each has a random chance of spawning cave walkers.
- Walker advances yaw/pitch, carves a round tube: radius `1.4 + sin(len*pi/maxLen)*size`,
  wobbles, branches at midpoint, max length ~104. Deep caves can go 5x fat.
- Carves only stone (not water/air); grass settles down one block over cave mouths.

---

## 5. Trees (`GenerateTrees` / `GenerateTree`)

- A few per chunk on grass/dirt; trunk 4-6 tall, rounded leaf canopy (top corners cut).
- Fails silently if it would cross the chunk edge or the ground isn't grass/dirt
  (so no trees in deserts/sand automatically).

---

## 6. Biome labels (`BiomeNameAt`)

Order of checks:
1. `Desert` — `_desert.Noise2DNormalized(x*0.008, z*0.008) > 0.28`
2. `Ocean` — `centerY < 48 && elevation > 0.3`
3. `Mountains` — `reliefShaped > 0.15`
4. `Hills` — `reliefShaped > 0.0`
5. else `Plains`

`centerY = (8.5 + (elevation-1)*9 + reliefShaped*3.6) * 8` (block Y of surface line).
NOTE: this is duplicated in `EstimateSurfaceHeightAt` — must be kept in sync if the
density math changes.

---

## 7. Estimated surface height (`EstimateSurfaceHeightAt`)

Cheap no-chunk-gen estimator of surface block Y using the SAME math as the density field.
Used by the biome teleport (B menu). Slightly imprecise; player may fall 1-3 blocks.

---

## 8. Post-passes (after terrain + caves)

- `Monoliths.Sculpt` — deliberate monolith/tower feature (seeded noise field, tunable)
- `QuartzVeins.Generate` — sedimentary quartz veins
- `CoalOres.Generate` — coal blobs

---

## 9. Known issues / tuning knobs (current)

- Biome labels and terrain height are computed from SEPARATE noise paths that must be
  kept in sync manually — this has caused desert/plains/ocean desync bugs.
- **Planned:** replace with a single authoritative biome map, then derive height +
  surface materials from the biome (one source of truth).
- Sea level: `seaLevelLocalY = 64` (band-local), world 0.
- `ReliefFrequency` = 130 (hills/mountain size).
- `continentBias` = 380 (elevation zero-crossing; also monolith frequency).
