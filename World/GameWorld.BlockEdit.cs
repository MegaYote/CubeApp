using System;
using System.Collections.Generic;
using System.Numerics;

namespace Cubuild
{
    public sealed partial class GameWorld : IDisposable
    {
        private static readonly int _idTorch = BlockRegistry.GetId("torch");

        /// <summary>How a right-click "chop" breaks the targeted block: the hatchet
        /// strips logs into planks / planks into sticks; flint converts a log into a
        /// workbench; pickaxe converts stone into cobblestone.</summary>
        public enum WoodChopKind { None = 0, Hatchet, Flint, Pickaxe }

        public bool TryBreakBlock(PlayerState p, Point3D origin, Point3D direction, out int removedBlockId, out (int x, int y, int z) removedPos)
        {
            removedBlockId = 0;
            removedPos = default;
            var pickResult = TryPickBlock(origin, direction);
            if (!pickResult.HasValue) return false;
            var remove = pickResult.Value.Remove;
            if (TryBreakBlockAt(remove.x, remove.y, remove.z, out removedBlockId))
            {
                if (!IsCreative) DamageSelectedTool(removedBlockId);
                removedPos = remove;
                return true;
            }
            return false;
        }

        /// <summary>Breaks the block at an exact world position (used by the mining system).
        /// Returns true if a block was removed, with its previous id for particle spawning.
        /// With <paramref name="chop"/> (hatchet or flint right-click) a log strips: the
        /// hatchet makes 1-4 planks, flint makes a workbench (always). The hatchet can also
        /// strip planks into 1-4 sticks.</summary>
        public bool TryBreakBlockAt(int x, int y, int z, out int removedBlockId, WoodChopKind chop = WoodChopKind.None)
        {
            removedBlockId = 0;
            if (!Chunks.TryGetLoadedBlock(x, y, z, out removedBlockId)) return false;
            if (!Chunks.TrySetBlock(x, y, z, BlockRegistry.AirId)) return false;
            // Survival: mining a block drops a physical item you have to collect - no teleporting
            // into the inventory. Leaves drop a sapling 1-in-10, otherwise nothing; gravel
            // drops flint 1-in-10 (like Minecraft) and otherwise drops itself. Stone mined
            // with a bare FIST (no tool in hand) drops gravel 1-in-10, otherwise nothing;
            // with any tool it drops itself as normal. Right-click CHOP drops (hatchet: log
            // -> 1-4 planks, plank -> 1-4 sticks; flint: log -> 1 workbench) can produce
            // multiple items, unlike every other drop.
            int dropId = removedBlockId;
            int dropCount = 1;
            if (chop == WoodChopKind.Hatchet && dropId == _idLog)
            {
                dropId = _idPlanks;
                dropCount = 1 + _regenRandom.Next(4); // 1..4 planks
            }
            else if (chop == WoodChopKind.Hatchet && dropId == _idPlanks)
            {
                dropId = _idStick;
                dropCount = 1 + _regenRandom.Next(4); // 1..4 sticks
            }
            else if (chop == WoodChopKind.Flint && dropId == _idLog)
            {
                dropId = _idWorkbench; // flint carves a log into a workbench, always
            }
            else if (chop == WoodChopKind.Pickaxe && (dropId == _idStone || dropId == _idCobblestone))
            {
                // Pickaxe right-click chop: stone/cobblestone -> cobblestone (normal speed)
                dropId = _idCobblestone;
                dropCount = 1;
            }
            else if (dropId == _idLeaves)
            {
                // Natural leaf drop: sapling 1-in-10, sap (1-in-20 hand / 1-in-10 flint),
                // then the flint-stick bonus.
                dropId = RollLeafDrop(flintBonus: true, naturalDecay: false);
            }
            else if (dropId == _idGravel) dropId = _regenRandom.Next(10) == 0 ? _idFlint : _idGravel;
            else if (dropId == _idStone && SelectedBlock <= 0) dropId = _regenRandom.Next(10) == 0 ? _idGravel : 0;
            // Obsidian yields nothing - it's too hard to extract anything from it
            else if (dropId == _idObsidian) dropId = 0;
            if (!IsCreative && dropId > 0)
            {
                SpawnItemDrop(dropId, dropCount, new Point3D(x + 0.5, y + 0.5, z + 0.5));
            }
            // Chopping away a tree's logs orphan its canopy: any leaves no longer within
            // reach of a remaining log decompose (Minecraft-style).
            if (removedBlockId == _idLog) DecayLeavesNear(x, y, z);
            BlockTicks?.OnBlockChanged(x, y, z);
            int rLayer = ChunkManager.LayerForWorldY(y);
            var editedChunk = new ChunkCoordinates(rLayer, WorldToChunkCoord(x), WorldToChunkCoord(z));
            Mesher.RequestImmediateRemesh(editedChunk);
            BlockEdited?.Invoke(x, y, z, 0, 0);

            // If a cross block (flower, sapling, torch, etc.) sits on top of the removed
            // block, it loses support and breaks automatically.  The break cascades upward
            // so a multi-block plant can be supported in the future.
            BreakUnsupportedCross(x, y + 1, z);
            // Wall torches attach to the SIDE of a block; if their wall is removed they
            // lose their support too (a floor torch above is handled by the cross cascade).
            BreakUnsupportedWallTorches(x, y, z);

            return true;
        }

        // ---- leaf decay (Minecraft-style) ----
        // Leaves don't vanish the moment their last log is chopped: once no log remains
        // within 6 blocks (Manhattan distance, the vanilla survival rule) they begin to
        // DETERIORATE — each leaf rolls its own randomized 1.5–5s countdown and pops when
        // it expires, so the canopy melts away gradually. Placing a log back in range
        // before a leaf's timer hits zero saves it (like MC's "don't break that leaf!").
        // Timers are runtime-only (not saved), and only advance while the sim runs.
        private const int LeafSupportDistance = 6;
        private const int LeafScanHalfExtent = 8;
        private readonly Dictionary<(int X, int Y, int Z), float> _decayingLeaves = new();

        private void UpdateLeafDecay(float deltaSeconds)
        {
            if (_decayingLeaves.Count == 0) return;
            var due = new List<(int X, int Y, int Z)>(8);
            foreach (var kv in _decayingLeaves)
            {
                float left = kv.Value - deltaSeconds;
                _decayingLeaves[kv.Key] = left;
                if (left <= 0f) due.Add(kv.Key);
            }
            foreach (var pos in due)
            {
                _decayingLeaves.Remove(pos);
                // Support re-check at expiry: a log placed during the wait saves the leaf.
                if (HasLogNearby(pos.X, pos.Y, pos.Z)) continue;
                RemoveDecayedLeaf(pos.X, pos.Y, pos.Z);
            }
        }

        private bool HasLogNearby(int x, int y, int z)
        {
            for (int dy = -LeafSupportDistance; dy <= LeafSupportDistance; dy++)
            {
                for (int dz = -LeafSupportDistance; dz <= LeafSupportDistance; dz++)
                {
                    for (int dx = -LeafSupportDistance; dx <= LeafSupportDistance; dx++)
                    {
                        int lx = x + dx, ly = y + dy, lz = z + dz;
                        if (Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz) > LeafSupportDistance) continue;
                        if (Chunks.TryGetLoadedBlock(lx, ly, lz, out int id) && id == _idLog) return true;
                    }
                }
            }
            return false;
        }

        private void ScheduleLeafDecay(int x, int y, int z)
        {
            // One countdown per leaf; the first schedule wins (re-scans don't reset it).
            if (_decayingLeaves.ContainsKey((x, y, z))) return;
            float delay = 1.5f + (float)_regenRandom.NextDouble() * 3.5f; // 1.5..5 seconds
            _decayingLeaves[(x, y, z)] = delay;
        }

        private void DecayLeavesNear(int bx, int by, int bz)
        {
            // Collect the logs and leaf candidates inside the scan cube in one pass.
            Span<(int X, int Y, int Z)> logs = stackalloc (int, int, int)[64];
            Span<(int X, int Y, int Z)> leaves = stackalloc (int, int, int)[512];
            int logCount = 0, leafCount = 0;
            for (int dy = -LeafScanHalfExtent; dy <= LeafScanHalfExtent; dy++)
            {
                for (int dz = -LeafScanHalfExtent; dz <= LeafScanHalfExtent; dz++)
                {
                    for (int dx = -LeafScanHalfExtent; dx <= LeafScanHalfExtent; dx++)
                    {
                        int x = bx + dx, y = by + dy, z = bz + dz;
                        if (!Chunks.TryGetLoadedBlock(x, y, z, out int id)) continue;
                        if (id == _idLog && logCount < logs.Length) logs[logCount++] = (x, y, z);
                        else if (id == _idLeaves && leafCount < leaves.Length) leaves[leafCount++] = (x, y, z);
                    }
                }
            }

            // Every leaf that can no longer reach a log starts deteriorating.
            for (int i = 0; i < leafCount; i++)
            {
                var (x, y, z) = leaves[i];
                bool supported = false;
                for (int j = 0; j < logCount; j++)
                {
                    var (lx, ly, lz) = logs[j];
                    if (Math.Abs(x - lx) + Math.Abs(y - ly) + Math.Abs(z - lz) <= LeafSupportDistance)
                    {
                        supported = true;
                        break;
                    }
                }
                if (!supported) ScheduleLeafDecay(x, y, z);
            }
        }

        // The leaf drop table. First roll is always the 1-in-10 sapling. Then, on the
        // otherwise-nothing outcome: sap drops 1-in-20 by hand, 1-in-10 with flint in hand,
        // or 1-in-12 when the leaf decays naturally; and with flint there's still the extra
        // 1-in-10 stick. One item per leaf, rolls in priority order (sapling > sap > stick).
        private int RollLeafDrop(bool flintBonus, bool naturalDecay)
        {
            if (_regenRandom.Next(10) == 0) return _idSapling;
            int sapChance = naturalDecay ? 12 : (flintBonus && SelectedBlock == _idFlint ? 10 : 20);
            if (_regenRandom.Next(sapChance) == 0) return _idSap;
            if (flintBonus && SelectedBlock == _idFlint && _regenRandom.Next(10) == 0) return _idStick;
            return 0;
        }

        private void RemoveDecayedLeaf(int x, int y, int z)
        {
            // Only pop it if it's still a leaf — the player may have built over it (or
            // another mechanic changed it) during the countdown.
            if (!Chunks.TryGetLoadedBlock(x, y, z, out int currentId) || currentId != _idLeaves) return;
            if (!Chunks.TrySetBlock(x, y, z, BlockRegistry.AirId)) return;
            // Decayed leaves roll the SAME drop table as hand-broken ones (no flint bonus,
            // sap at its natural 1-in-12 rate).
            int dropId = RollLeafDrop(flintBonus: false, naturalDecay: true);
            if (!IsCreative && dropId > 0)
            {
                SpawnItemDrop(dropId, 1, new Point3D(x + 0.5, y + 0.5, z + 0.5));
            }
            BlockTicks?.OnBlockChanged(x, y, z);
            int rLayer = ChunkManager.LayerForWorldY(y);
            var editedChunk = new ChunkCoordinates(rLayer, WorldToChunkCoord(x), WorldToChunkCoord(z));
            Mesher.RequestImmediateRemesh(editedChunk);
            BlockEdited?.Invoke(x, y, z, 0, 0);
            BreakUnsupportedCross(x, y + 1, z);
        }

        /// <summary>Places the currently selected item at the targeted face. Items without a
        /// block behavior (tools, food, gems) can't place - those are handled by
        /// <see cref="TryEatSelectedFood"/> and future item-use hooks. Returns true if placed.</summary>
        public bool TryPlaceSelectedBlock(PlayerState p, Point3D origin, Point3D direction)
        {
            var pickResult = TryPickBlock(origin, direction, out double hitDistance);
            if (!pickResult.HasValue) return false;
var place = pickResult.Value.Place;
            var normal = pickResult.Value.Normal;
            var hitPoint = origin + direction * hitDistance;

            // Wood Sealant Bucket: right-click on planks -> converts to treated_planks.
            // 8 uses per bucket, then reverts to empty bucket.
            int sealantId = ItemRegistry.GetId("wood_sealant_bucket");
            int planksId = BlockRegistry.GetId("planks");
            int treatedPlanksId = BlockRegistry.GetId("treated_planks");
            int bucketId = ItemRegistry.GetId("bucket");
            if (SelectedBlock == sealantId)
            {
                int slot = SelectedSlot;

                // Check if this sealant is already depleted (8 uses recorded, entry removed)
                // but the player still has SelectedBlock pointing to the old sealantId.
                // Also catch the case where the slot was converted to bucket but SelectedBlock wasn't synced.
                bool slotHasSealant = Hotbar[slot] == sealantId;
                bool selectedIsSealant = SelectedBlock == sealantId;

                // If we have a sealant selected but the slot no longer has it (converted to bucket),
                // sync SelectedBlock and block the action.
                if (selectedIsSealant && !slotHasSealant)
                {
                    SelectedBlock = Hotbar[slot]; // sync to bucket (or whatever is now in slot)
                    return false;
                }

                // Normal use: hit planks -> convert + track uses
                var hit = pickResult.Value.Remove;
                if (Chunks.TryGetLoadedBlock(hit.x, hit.y, hit.z, out var targetId) && targetId == planksId)
                {
                    // Convert planks -> treated_planks
                    Chunks.TrySetBlock(hit.x, hit.y, hit.z, treatedPlanksId);

                    // Track sealant uses per hotbar slot (8 uses max)
                    if (!IsCreative && slotHasSealant)
                    {
                        // Increment use count for this slot (first use = 1, etc.)
                        int uses = _sealantUses.TryGetValue(slot, out int u) ? u + 1 : 1;
                        _sealantUses[slot] = uses;

                        // After 8 uses, convert to empty bucket and sync SelectedBlock
                        if (uses >= 8)
                        {
                            Hotbar[slot] = bucketId;
                            HotbarCounts[slot] = 1;
                            _sealantUses.Remove(slot);
                            SelectedBlock = bucketId; // instant sync so next click is blocked
                        }
                    }
                    return true;
                }
                // Not planks, don't consume the sealant
                return false;
            }

            // The hotbar holds ITEM ids now; resolve to the block this item places (-1 = not a
            // block item, e.g. tools/food/gemstones - nothing to place).
            int blockToPlace = ItemRegistry.ResolveBlockId(SelectedBlock);
            if (blockToPlace < 0) return false;
            int spendId = SelectedBlock; // consume the ORIGINAL selected item id (slabs can become top variants)
            int meta = 0;

            // Survival: you can only place blocks you actually own.
            if (!IsCreative)
            {
                if (spendId <= 0) return false;
                if (InventoryCount(spendId) < 1) return false;
            }

            // Can't place INTO a cell a falling block is currently passing through. Otherwise a
            // spam of placements in one column would stack into a moving block / collide mid-air.
            if (BlockTicks != null && BlockTicks.IsCellOccupiedByFalling(place.x, place.y, place.z))
            {
                return false;
            }

            if (BlockRegistry.IsSlab(blockToPlace) || BlockRegistry.IsSlabTop(blockToPlace))
            {
                var hit = pickResult.Value.Remove;
                if (TryMergeSlab(hit.x, hit.y, hit.z, normal, blockToPlace))
                {
                    if (!IsCreative) TryConsumeFromInventory(spendId, 1);
                    return true;
                }

                bool placeTop = normal.Y < 0 || (normal.Y == 0 && (hitPoint.Y - place.y) > 0.5);
                if (BlockRegistry.IsSlab(blockToPlace) && placeTop)
                {
                    blockToPlace = SlabTopIdFor(blockToPlace);
                }

                if (Chunks.TryGetLoadedBlockAndMeta(place.x, place.y, place.z, out var oldId, out _)
                    && oldId != BlockRegistry.AirId && !IsReplaceableFluid(oldId))
                {
                    if (TryFillSlabCell(place.x, place.y, place.z, blockToPlace))
                    {
                        if (!IsCreative) TryConsumeFromInventory(spendId, 1);
                        return true;
                    }
                    return false;
                }
            }
            else if (BlockRegistry.IsStair(blockToPlace))
            {
                meta = StairFacingMeta(p);
            }
            else
            {
                if (Chunks.TryGetLoadedBlockAndMeta(place.x, place.y, place.z, out var occupied, out _)
                    && occupied != BlockRegistry.AirId && !IsReplaceableFluid(occupied))
                {
                    return false;
                }
            }

            // Cross blocks (flowers, saplings, torches, etc.) can't be stacked on top of
            // other cross blocks and break automatically when their support is removed.
            if (BlockRegistry.IsCross(blockToPlace)
                && Chunks.TryGetLoadedBlock(place.x, place.y - 1, place.z, out var belowId)
                && BlockRegistry.IsCross(belowId))
            {
                return false;
            }

            // Torches: placed on a top face -> floor torch (X-cross); placed against a
            // side face -> wall torch (single quad leaning away from the wall, meta 1-4).
            // A wall torch needs a solid wall behind it; torches can't hang from ceilings.
            if (blockToPlace == _idTorch)
            {
                if (normal.Y < 0) return false;
                if (normal.Y == 0)
                {
                    int wallX = place.x - (int)normal.X;
                    int wallZ = place.z - (int)normal.Z;
                    if (!Chunks.TryGetLoadedBlock(wallX, place.y, wallZ, out var wallId)
                        || wallId == BlockRegistry.AirId
                        || !BlockRegistry.IsSolid(wallId)
                        || BlockRegistry.IsCross(wallId)
                        || IsReplaceableFluid(wallId))
                    {
                        return false;
                    }
                    meta = normal.X > 0 ? 1 : normal.X < 0 ? 2 : normal.Z > 0 ? 3 : 4;
                }
            }

            if (WouldBlockIntersectPlayer(p, place.x, place.y, place.z, blockToPlace, meta)) return false;
            if (!Chunks.TrySetBlock(place.x, place.y, place.z, blockToPlace, meta)) return false;
            if (!IsCreative) TryConsumeFromInventory(spendId, 1);
            BlockTicks?.OnBlockChanged(place.x, place.y, place.z);
            int placeLayer = ChunkManager.LayerForWorldY(place.y);
            var editedChunk = new ChunkCoordinates(placeLayer, WorldToChunkCoord(place.x), WorldToChunkCoord(place.z));
            Mesher.RequestImmediateRemesh(editedChunk);
            BlockEdited?.Invoke(place.x, place.y, place.z, blockToPlace, meta);
            return true;
        }

        /// <summary>Applies a block edit from the network (host authority). Same side effects as a
        /// local edit: sets the block, wakes fluids, remeshes, fires BlockEdited.</summary>
        public bool ApplyBlockEdit(int x, int y, int z, int blockId, int meta)
        {
            // Same "wait for it to fall" rule on the authoritative path (host applying a client's
            // edit): never place into a cell a falling block is passing through.
            if (BlockTicks != null && BlockTicks.IsCellOccupiedByFalling(x, y, z)) return false;
            if (!Chunks.TrySetBlockLoadedOnly(x, y, z, blockId, meta)) return false;
            BlockTicks?.OnBlockChanged(x, y, z);
            int ebLayer = ChunkManager.LayerForWorldY(y);
            var editedChunk = new ChunkCoordinates(ebLayer, WorldToChunkCoord(x), WorldToChunkCoord(z));
            Mesher.RequestImmediateRemesh(editedChunk);
            BlockEdited?.Invoke(x, y, z, blockId, meta);
            if (blockId == BlockRegistry.AirId)
            {
                BreakUnsupportedCross(x, y + 1, z);
                BreakUnsupportedWallTorches(x, y, z);
            }
            return true;
        }

        private bool TryMergeSlab(int x, int y, int z, Point3D normal, int heldBlock)
        {
            if (!Chunks.TryGetLoadedBlockAndMeta(x, y, z, out var hitId, out _)) return false;
            if (!BlockRegistry.IsSlab(hitId) && !BlockRegistry.IsSlabTop(hitId)) return false;
            if (SlabMaterialOf(hitId) != SlabMaterialOf(heldBlock)) return false;
            if (!((BlockRegistry.IsSlab(hitId) && normal.Y > 0) || (BlockRegistry.IsSlabTop(hitId) && normal.Y < 0))) return false;

            int fullId = BlockRegistry.GetId(SlabMaterialOf(hitId));
            if (!Chunks.TrySetBlock(x, y, z, fullId, 0)) return false;
            BlockTicks?.OnBlockChanged(x, y, z);
            int msLayer = ChunkManager.LayerForWorldY(y);
            Mesher.RequestImmediateRemesh(new ChunkCoordinates(msLayer, WorldToChunkCoord(x), WorldToChunkCoord(z)));
            BlockEdited?.Invoke(x, y, z, fullId, 0);
            return true;
        }

        private bool TryFillSlabCell(int x, int y, int z, int placingId)
        {
            if (!Chunks.TryGetLoadedBlockAndMeta(x, y, z, out var oldId, out _)) return false;
            if (!BlockRegistry.IsSlab(oldId) && !BlockRegistry.IsSlabTop(oldId)) return false;
            if (SlabMaterialOf(oldId) != SlabMaterialOf(placingId)) return false;
            bool oldTop = BlockRegistry.IsSlabTop(oldId);
            bool newTop = BlockRegistry.IsSlabTop(placingId);
            if (oldTop == newTop) return false;

            int fullId = BlockRegistry.GetId(SlabMaterialOf(oldId));
            if (!Chunks.TrySetBlock(x, y, z, fullId, 0)) return false;
            BlockTicks?.OnBlockChanged(x, y, z);
            int fsLayer = ChunkManager.LayerForWorldY(y);
            Mesher.RequestImmediateRemesh(new ChunkCoordinates(fsLayer, WorldToChunkCoord(x), WorldToChunkCoord(z)));
            BlockEdited?.Invoke(x, y, z, fullId, 0);
            return true;
        }

        private static bool IsReplaceableFluid(int id) => id == BlockRegistry.GetId("water");

        /// <summary>If (x, y, z) holds a cross block with no solid support below, break it
        /// and cascade upward.  Future multi-block plants (2-tall ferns, etc.) can extend
        /// this to check a whitelist of supported configurations.</summary>
        private void BreakUnsupportedCross(int x, int y, int z)
        {
            if (!Chunks.TryGetLoadedBlock(x, y, z, out var id)) return;
            if (!BlockRegistry.IsCross(id)) return;
            // A cross block is supported if the block directly below is solid (full cube
            // or any non-air non-cross shape).  Cross blocks below don't count as support.
            if (Chunks.TryGetLoadedBlock(x, y - 1, z, out var below) && below != BlockRegistry.AirId && !BlockRegistry.IsCross(below))
                return;
            // No support — break this cross block (no drops, like flowers/saplings in Minecraft).
            Chunks.TrySetBlock(x, y, z, BlockRegistry.AirId);
            BlockTicks?.OnBlockChanged(x, y, z);
            int ucLayer = ChunkManager.LayerForWorldY(y);
            Mesher.RequestImmediateRemesh(new ChunkCoordinates(ucLayer, WorldToChunkCoord(x), WorldToChunkCoord(z)));
            BlockEdited?.Invoke(x, y, z, 0, 0);
            // Cascade upward — if there's another cross block above, it also loses support.
            BreakUnsupportedCross(x, y + 1, z);
        }

        /// <summary>Breaks any wall torches whose wall block was just removed. A torch at the
        /// given 4-neighbor cell with the matching lean meta has its wall at (x, y, z).</summary>
        private void BreakUnsupportedWallTorches(int x, int y, int z)
        {
            BreakWallTorchAt(x + 1, y, z, 1); // leans +X -> wall at torch.X-1 == x
            BreakWallTorchAt(x - 1, y, z, 2); // leans -X -> wall at torch.X+1 == x
            BreakWallTorchAt(x, y, z + 1, 3); // leans +Z -> wall at torch.Z-1 == z
            BreakWallTorchAt(x, y, z - 1, 4); // leans -Z -> wall at torch.Z+1 == z
        }

        private void BreakWallTorchAt(int x, int y, int z, int expectedMeta)
        {
            if (!Chunks.TryGetLoadedBlockAndMeta(x, y, z, out var id, out var meta)) return;
            if (id != _idTorch || meta != expectedMeta) return;
            Chunks.TrySetBlock(x, y, z, BlockRegistry.AirId);
            BlockTicks?.OnBlockChanged(x, y, z);
            int layer = ChunkManager.LayerForWorldY(y);
            Mesher.RequestImmediateRemesh(new ChunkCoordinates(layer, WorldToChunkCoord(x), WorldToChunkCoord(z)));
            BlockEdited?.Invoke(x, y, z, 0, 0);
        }

        private static string SlabMaterialOf(int id)
        {
            string name = BlockRegistry.GetName(id);
            return name.EndsWith("_slab_top", StringComparison.Ordinal)
                ? name[..^"_slab_top".Length]
                : name.EndsWith("_slab", StringComparison.Ordinal) ? name[..^"_slab".Length] : name;
        }

        private static int SlabTopIdFor(int slabId)
            => BlockRegistry.GetId(SlabMaterialOf(slabId) + "_slab_top");

        private int StairFacingMeta(PlayerState p)
        {
            float yawRad = p.Yaw * (float)Math.PI / 180f;
            double dirX = Math.Sin(yawRad);
            double dirZ = Math.Cos(yawRad);
            if (Math.Abs(dirX) > Math.Abs(dirZ))
                return dirX > 0 ? 0 : 1;
            return dirZ > 0 ? 2 : 3;
        }

        // Computes the face rectangle (minX,minY,minZ,maxX,maxY,maxZ) for a fluid block
        // given the normal pointing toward the ray origin.
        private (double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
        GetFluidFaceRect(int x, int y, int z, Point3D normal)
        {
            if (Math.Abs(normal.X) > 0.5) // X face
            {
                double xVal = normal.X > 0 ? x + 1.0 : x;
                return (xVal, y, z, xVal, y + 1.0, z + 1.0);
            }
            if (Math.Abs(normal.Y) > 0.5) // Y face
            {
                double yVal = normal.Y > 0 ? y + 1.0 : y;
                return (x, yVal, z, x + 1.0, yVal, z + 1.0);
            }
            // Z face
            double zVal = normal.Z > 0 ? z + 1.0 : z;
            return (x, y, zVal, x + 1.0, y + 1.0, zVal);
        }

        private bool WouldBlockIntersectPlayer(PlayerState p, int x, int y, int z, int blockId, int meta)
        {
            double minX = p.Position.X - PlayerRadius;
            double maxX = p.Position.X + PlayerRadius;
            double minY = p.Position.Y - EyeHeight;
            double maxY = minY + PlayerHeight;
            double minZ = p.Position.Z - PlayerRadius;
            double maxZ = p.Position.Z + PlayerRadius;
            return BoxesOverlapPlayer(GetBlockCollisionBoxes(blockId, meta), x, y, z, minX, maxX, minY, maxY, minZ, maxZ);
        }

        // ------------------------------------------------------------------
        // ray picking (ported from Program.cs)
        // ------------------------------------------------------------------

        public PickBlockResult? TryPickBlock(Point3D origin, Point3D direction) => TryPickBlock(origin, direction, out _);

        public PickBlockResult? TryPickBlock(Point3D origin, Point3D direction, out double hitDistance)
        {
            hitDistance = double.PositiveInfinity;
            direction = direction.Normalized();
            int blockX = (int)Math.Floor(origin.X);
            int blockY = (int)Math.Floor(origin.Y);
            int blockZ = (int)Math.Floor(origin.Z);
            var stepX = Math.Sign(direction.X);
            var stepY = Math.Sign(direction.Y);
            var stepZ = Math.Sign(direction.Z);
            var tDeltaX = stepX != 0 ? Math.Abs(1.0 / direction.X) : double.PositiveInfinity;
            var tDeltaY = stepY != 0 ? Math.Abs(1.0 / direction.Y) : double.PositiveInfinity;
            var tDeltaZ = stepZ != 0 ? Math.Abs(1.0 / direction.Z) : double.PositiveInfinity;
            var tMaxX = stepX > 0 ? (blockX + 1.0 - origin.X) * tDeltaX : (origin.X - blockX) * tDeltaX;
            var tMaxY = stepY > 0 ? (blockY + 1.0 - origin.Y) * tDeltaY : (origin.Y - blockY) * tDeltaY;
            var tMaxZ = stepZ > 0 ? (blockZ + 1.0 - origin.Z) * tDeltaZ : (origin.Z - blockZ) * tDeltaZ;
            int currentX = blockX, currentY = blockY, currentZ = blockZ;
            var maxDistance = BlockReach;
            // A living mob's hitbox consumes the ray: blocks behind it are unreachable, so
            // fighting a mob can never accidentally break the wall behind it.
            bool mobInFront = Entities.TryRaycastMobs(origin, direction, maxDistance, out double mobDist);
            var distance = 0.0;
            for (int iteration = 0; iteration < 400 && distance <= maxDistance; iteration++)
            {
                // The mob sits strictly before this cell - everything beyond is behind it.
                if (mobInFront && distance > mobDist) return null;

                if (Chunks.TryGetLoadedBlockAndMeta(currentX, currentY, currentZ, out var block, out var meta)
                    && block != BlockRegistry.AirId
                    && block != BlockRegistry.GetId("water"))
                {
                    double cellExit = Math.Min(tMaxX, Math.Min(tMaxY, tMaxZ));
                    var boxes = GetBlockCollisionBoxes(block, meta);
                    foreach (var b in boxes)
                    {
                        if (RayBoxHit(origin, direction,
                                currentX + b.minX, currentY + b.minY, currentZ + b.minZ,
                                currentX + b.maxX, currentY + b.maxY, currentZ + b.maxZ,
                                distance - 1e-9, cellExit + 1e-9, out double t, out var n))
                        {
                            hitDistance = Math.Max(0.0, t);
                            // Mob closer than (or overlapping) the block face blocks the pick.
                            if (mobInFront && mobDist <= t) return null;
                            var face = ComputeFaceRect(currentX, currentY, currentZ, b, n);
                            var place = ((int)Math.Floor(currentX + n.X + 0.5), (int)Math.Floor(currentY + n.Y + 0.5), (int)Math.Floor(currentZ + n.Z + 0.5));
                            return new PickBlockResult((currentX, currentY, currentZ), place, n, face);
                        }
                    }
                }

                if (tMaxX < tMaxY)
                {
                    if (tMaxX < tMaxZ) { currentX += stepX; distance = tMaxX; tMaxX += tDeltaX; }
                    else { currentZ += stepZ; distance = tMaxZ; tMaxZ += tDeltaZ; }
                }
                else
                {
                    if (tMaxY < tMaxZ) { currentY += stepY; distance = tMaxY; tMaxY += tDeltaY; }
                    else { currentZ += stepZ; distance = tMaxZ; tMaxZ += tDeltaZ; }
                }
            }
            return null;
        }

        /// <summary>
        /// Performs a fluid hit-test along a ray (for bucket filling).
        /// Unlike TryPickBlock, this DOES hit water instead of passing through it.
        /// Returns the first matching fluid block hit, or null.
        /// </summary>
        public PickBlockResult? TryPickFluid(Point3D origin, Point3D direction, int fluidBlockId)
        {
            direction = direction.Normalized();
            int blockX = (int)Math.Floor(origin.X);
            int blockY = (int)Math.Floor(origin.Y);
            int blockZ = (int)Math.Floor(origin.Z);
            var stepX = Math.Sign(direction.X);
            var stepY = Math.Sign(direction.Y);
            var stepZ = Math.Sign(direction.Z);
            var tDeltaX = stepX != 0 ? Math.Abs(1.0 / direction.X) : double.PositiveInfinity;
            var tDeltaY = stepY != 0 ? Math.Abs(1.0 / direction.Y) : double.PositiveInfinity;
            var tDeltaZ = stepZ != 0 ? Math.Abs(1.0 / direction.Z) : double.PositiveInfinity;
            var tMaxX = stepX > 0 ? (blockX + 1.0 - origin.X) * tDeltaX : (origin.X - blockX) * tDeltaX;
            var tMaxY = stepY > 0 ? (blockY + 1.0 - origin.Y) * tDeltaY : (origin.Y - blockY) * tDeltaY;
            var tMaxZ = stepZ > 0 ? (blockZ + 1.0 - origin.Z) * tDeltaZ : (origin.Z - blockZ) * tDeltaZ;
            int currentX = blockX, currentY = blockY, currentZ = blockZ;
            var maxDistance = BlockReach;
            var distance = 0.0;
            var lastX = currentX;
            var lastY = currentY;
            var lastZ = currentZ;
            var normal = Point3D.Zero;

            for (int iteration = 0; iteration < 400 && distance <= maxDistance; iteration++)
            {
                if (Chunks.TryGetLoadedBlockAndMeta(currentX, currentY, currentZ, out var block, out _))
                {
                    if (block == fluidBlockId)
                    {
                        var face = GetFluidFaceRect(currentX, currentY, currentZ, normal);
                        var place = ((int)Math.Floor(currentX + normal.X + 0.5), (int)Math.Floor(currentY + normal.Y + 0.5), (int)Math.Floor(currentZ + normal.Z + 0.5));
                        return new PickBlockResult((currentX, currentY, currentZ), place, normal, face);
                    }
                }

                lastX = currentX; lastY = currentY; lastZ = currentZ;

                if (tMaxX < tMaxY)
                {
                    if (tMaxX < tMaxZ) { currentX += stepX; distance = tMaxX; tMaxX += tDeltaX; normal = new Point3D(-stepX, 0, 0); }
                    else { currentZ += stepZ; distance = tMaxZ; tMaxZ += tDeltaZ; normal = new Point3D(0, 0, -stepZ); }
                }
                else
                {
                    if (tMaxY < tMaxZ) { currentY += stepY; distance = tMaxY; tMaxY += tDeltaY; normal = new Point3D(0, -stepY, 0); }
                    else { currentZ += stepZ; distance = tMaxZ; tMaxZ += tDeltaZ; normal = new Point3D(0, 0, -stepZ); }
                }
            }
            return null;
        }

        // ------------------------------------------------------------------
        // spawn / deep-fill
        // ------------------------------------------------------------------

    }
}