# Selling Remediation Worklist

**Purpose:** Concrete, ordered list of everything that must change before this game can
be sold. Derived from an audit of the CubeApp codebase and assets against Minecraft
EULA / copyright exposure.

**IMPORTANT:** This is a technical worklist, not legal advice. Verify with an IP attorney
who understands game development + Minecraft's EULA before selling.

---

## PRIORITY 1 — Remove pirated Minecraft source from the project (do this FIRST)

These folders contain decompiled / recreated Minecraft source and must be deleted from
the working tree AND git history (git filter-repo / BFG to scrub history if the repo is
ever shared):

- [ ] `Programs to use as reference material/IF-20100630-main/`  (Infdev decompile; README self-describes as "pirated")
- [ ] `Programs to use as reference material/rd-131655-build/`   (Pre-Classic recreated source)
- [ ] `Programs to use as reference material/Cubuild.html`       (check what this is; if MC-derived, remove)

Even *keeping* these locally is a liability if the repo leaks. Remove them.

---

## PRIORITY 2 — Clean-room rewrite of the "faithful port" code files

These files explicitly say they are 1:1 / faithful ports of Mojang source. The *behavior*
can be kept, but the *implementation* must be rewritten independently so the code is not
a translation of the decompiled source. Do not look at the reference folders while
rewriting.

| File | What it ports from MC | Risk | Status |
|---|---|---|---|
| `FluidSimulation.cs` | `BlockFlowing.java` water sim, "ported 1:1" | **HIGH** | ✅ Clean-room rewrite done (own BFS flow, own names) |
| `Lighting.cs` | `World.checkLightFor`, `lightBrightnessTable`, skylight subtraction | **HIGH** | ✅ Comment sweep + original brightness curve + `NightDimLevel` rename |
| `MobSpawner.cs` | `SpawnerAnimals.java`, `SpawnerMonsters.java` ("AUTHENTIC") | **HIGH** | ✅ Comment rewrite (generic weighted-spawn behavior kept) |
| `GameWorld.cs` | `getCelestialAngle`, `calculateSkylightSubtracted`, time model | **MED-HIGH** | ✅ `SunPosition` / `NightDimLevel` / `SkyBrightness` renames |
| `EntityManager.cs` | Infdev zombie AI (attack/aggro/spawn) | **MED** | ✅ Comment sweep |
| `SoundEngine.cs` | "faithful port of SoundManager.java" | **MED** | ✅ Comment sweep |
| `Mesher.cs` / `MeshFace.cs` / `ChunkManager.cs` | RenderBlocks-style face shading, per-face light mults | **MED** | ✅ Comment sweep |
| `MobModel.cs` | RenderBlocks face-shade table | **MED** | ✅ Comment sweep |
| `InfdevTimer.cs` (Engine/) | Infdev tick model | LOW | ✅ Renamed `GameTickTimer` |
| `World/TerrainChunkProvider.cs` | "ChunkProviderGenerate" terrain | **HIGH** | ✅ Renamed from `InfdevChunkProvider`, decompile var names cleaned |
| `World/NoiseGenerator.cs` | `NoiseGeneratorPerlin`/`NoiseGeneratorOctaves` | **MED** | ✅ `NoiseOctaves` rename + comment sweep |
| `PathFinding/*` | `PathFinder`/`PathHeap`/`PathPoint` | **MED** | ✅ Comment sweep (A* is a standard algorithm) |
| `Program.cs` | Various Infdev comments | LOW | ✅ Comment sweep |
| `Renderer/VeldridRenderer.cs` | sky/fog/star renderer comments | MED | ✅ Comment sweep |

**Verification (done):** `rg` for `Infdev|BlockFlowing|SpawnerAnimals|SpawnerMonsters|RenderBlocks|lightBrightnessTable|checkLightFor|getCelestialAngle|calculateSkylightSubtracted|Minecraft|Mojang|1.12's|faithful port|1:1 port` across shipped `.cs` returns **0 hits**.

> ⚠️ **Still to decide:** the terrain *algorithm* (in `TerrainChunkProvider`) and water *behavior* were derived from MC behavior, so although the code now has no MC citations or decompile variable names, the math (noise composition, cave-walker carving, surface-pass rules) still closely mirrors MC's algorithms. If an attorney wants a fully independent re-derivation, those two files need a deeper rewrite that changes the world's look. Recommend attorney review before relying on this as complete.


---

## PRIORITY 3 — Replace Minecraft-derived ASSETS

These are Minecraft's copyrighted art/sound and must be replaced with original art (or
properly-licensed substitutes).

### Textures
- [ ] `terrain.png` (256x256 block atlas) — MC-derived textures for grass/dirt/stone/sand/planks/etc. Replace with an original tile set (draw your own or buy/CC0 a tile pack).
- [ ] `sun.png` / `moon.png` (16x16) — MC-style sprites. Replace with original.
- [ ] `cubuild.png` logo — verify original; if traced from MC branding, replace.
- [ ] `hotbar.png`, `hotbar_select.png` — MC hotbar style. Replace with original UI art.
- [ ] `UI Elements/hotbarUI.png`, `UI Elements/inventoryUI.png` — same.
- [ ] `player.png` (64x64 Steve-style skin) — MC-derived. Replace with original character texture.
- [ ] `duck.png`, `MobEntities/**/Coyote.png`, `Zombie.png`, `GiantZombie.png` — verify origin; the ZOMBIE is a MC-zombie-shaped Blockbench model. Re-model or re-skin to be clearly non-MC.
- [ ] Clouds / sky gradient — currently "Infdev sky" colors (0xC0D8FF etc.). Sky *colors* are generic; fine, but the plane/starfield construction mirrors MC's RenderGlobal. Rewrite the sky generator.

### Audio
- [ ] `sounds/cavesound1.mp3` … `cavesound7.mp3` — **Minecraft's ambient cave sounds**. Replace with original ambience (or licensed foley pack).
- [ ] `sounds/grass.mp3` — MC's grass-step/break sound. Replace with original.

### Mob models
- [ ] `MobEntities/ZombieMob/zombie.glb` + GiantZombie — MC-zombie proportions/pose. Re-model as an original creature (change body plan, silhouette, textures).

---

## PRIORITY 4 — License compliance for 3rd-party libraries

All third-party libs are MIT, which is fine to sell — but MIT requires the copyright
notice be included with distributed copies.

- [ ] Add `Licenses.txt` (or a `THIRD_PARTY_NOTICES` file) next to the published .exe containing MIT notices for:
  - Veldrid (Eric Mellino)
  - Veldrid.SPIRV / Veldrid.ImGui / Veldrid.StartupUtilities / Veldrid.SDL2
  - Silk.NET.OpenAL
  - NAudio (Mark Heath)
  - StbImageSharp
  - SharpGLTF (Vicente Penades)
- [ ] Keep the license files in the repo (or a `licenses/` folder that ships with the build).

---

## PRIORITY 5 — Product-side hygiene

- [ ] Replace every `Infdev`/`Minecraft` mention in user-facing UI/strings/comments with original branding.
- [ ] Decide a name/brand that doesn't include "Cubuild" if that name was a previous project of yours — confirm you own it.
- [ ] Add an EULA/ToS to your own game.
- [ ] If you keep any MC-inspired mechanics (voxel mining etc.), that's fine — mechanics are not copyrightable — but don't copy their specific expression (textures, exact model geometry, sound clips, method-level source).

---

## Verification gate (before any sale)

1. `rg` for `Minecraft|Infdev|BlockFlowing|SpawnerAnimals|SpawnerMonsters|RenderBlocks|lightBrightnessTable|checkLightFor|getCelestialAngle|calculateSkylightSubtracted` across shipped code = **0 hits**.
2. All texture/sound/model files replaced or verified original.
3. `Licenses.txt` present with all MIT notices.
4. Reference-material folders removed from repo + history.
5. Attorney review of the final build.

---

*Audit date: 2026-08-09. Re-run the rg checks after every rewrite pass.*
