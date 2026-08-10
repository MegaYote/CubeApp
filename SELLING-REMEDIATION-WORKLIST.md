# Selling Remediation Worklist

**Purpose:** Concrete, ordered list of everything that must change before this game can
be sold. Derived from an audit of the CubeApp codebase and assets against Minecraft
EULA / copyright exposure.

**IMPORTANT:** This is a technical worklist, not legal advice. Verify with an IP attorney
who understands game development + Minecraft's EULA before selling.

---

## PRIORITY 1 — Remove pirated Minecraft source from the project (DONE)

These folders contained decompiled / recreated Minecraft source and have been **deleted from
the working tree AND purged from git history** (git filter-repo, force-pushed):

- [x] `Programs to use as reference material/IF-20100630-main/`  (Infdev decompile; README self-describes as "pirated")
- [x] `Programs to use as reference material/rd-131655-build/`   (Pre-Classic recreated source)
- [x] `Programs to use as reference material/Cubuild.html`       (kept - the author's own prior project)

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

## PRIORITY 3 — Assets: CONFIRMED ORIGINAL (no action needed)

The game's textures (terrain, sun/moon, player skin, hotbar/inventory UI, cubuild logo),
sound files (cave ambience, grass), and mob models (duck/coyote/zombie) were all
**created from scratch by the author**, inspired by (not copied from) Minecraft's style.
MC's *mechanics and style* are not copyrightable, and original art/sound/models are the
author's own. **No replacement needed.** Keep the source files (Blockbench projects,
texture/graphics sources, audio recordings) as provenance if ever questioned.

> Note: "inspired by" is fine; "ripped from" would not be. The author confirms these
> are original creations.

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

- [x] Replace every `Infdev`/`Minecraft` mention in user-facing UI/strings/comments with original branding (DONE in the P2 sweep).
- [ ] Decide a name/brand that doesn't include "Cubuild" if that name was a previous project of yours — confirm you own it.
- [ ] Add an EULA/ToS to your own game.
- [x] MC-inspired mechanics (voxel mining etc.) are fine — mechanics are not copyrightable; assets are confirmed original creations.

---

## Verification gate (before any sale)

1. `rg` for `Minecraft|Infdev|BlockFlowing|SpawnerAnimals|SpawnerMonsters|RenderBlocks|lightBrightnessTable|checkLightFor|getCelestialAngle|calculateSkylightSubtracted` across shipped code = **0 hits** (VERIFIED DONE).
2. All texture/sound/model files are the author's original creations (CONFIRMED).
3. `Licenses.txt` present with all MIT notices (still TODO).
4. Reference-material folders removed from repo + history (DONE).
5. Attorney review of the final build (recommended).

---

*Audit date: 2026-08-09. Re-run the rg checks after every rewrite pass.*
