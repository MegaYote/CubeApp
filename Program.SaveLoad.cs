using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using Cubuild.Renderer;
using Cubuild.World;
using Veldrid;
using Veldrid.Sdl2;
using Veldrid.StartupUtilities;
using static Cubuild.ChunkManager;
using Cubuild;

namespace Cubuild
{
    public sealed partial class Program : IDisposable
    {
        private void LoadWorldFromList()
        {
            int index = menu.SelectedWorldIndex;
            if (index < 0 || index >= menu.SavedWorlds.Count) return;
            string name = menu.SavedWorlds[index];
            string path = Path.Combine(SavesFolder, SanitizeFileName(name) + ".cubuild");
            if (!File.Exists(path)) return;
            var save = WorldSave.Load(path);
            if (save == null) return;
            LoadWorld(save, save.SourceFileName);
        }

/// <summary>Seconds between background autosaves. Esc-save and quit-save still exist;
        /// this only closes the crash/power-loss window.</summary>
        private const float AutosaveIntervalSeconds = 180f;
        private readonly object _saveWriteLock = new();
        private volatile bool _autosaveInFlight;
        private Task? _autosaveTask;
        private float _autosaveTimer;
        private float _saveToastTimer;
        private float _genWarningTimer;

        /// <summary>Called every frame while playing. When the interval elapses, snapshots the
        /// world and writes it to disk on a background thread so the game never stalls and
        /// crashes/power loss only cost a few minutes of progress.</summary>
        private void MaybeAutosave(float deltaSeconds)
        {
            _saveToastTimer = Math.Max(0f, _saveToastTimer - deltaSeconds);
            _genWarningTimer = Math.Max(0f, _genWarningTimer - deltaSeconds);
            _statusToastTimer = Math.Max(0f, _statusToastTimer - deltaSeconds);
            if (World == null || _autosaveInFlight) return;
            _autosaveTimer += deltaSeconds;
            if (_autosaveTimer >= AutosaveIntervalSeconds)
            {
                _autosaveTimer = 0f;
                SaveWorld(autosave: true);
            }
        }

        private bool SaveWorld(bool autosave = false)
        {
            if (World == null) return false;
            _autosaveTimer = 0f;
            try
            {
                // A synchronous save must snapshot only after any older autosave is finished;
                // otherwise that stale snapshot could win the write lock afterward.
                if (!autosave) WaitForAutosave();
                if (autosave && _autosaveInFlight) return false;
                Directory.CreateDirectory(SavesFolder);
                var snapshot = CaptureWorldSave(World, requireCompleteChunks: false);
                string saveName = string.IsNullOrWhiteSpace(_activeWorldSaveName)
                    ? SanitizeFileName(snapshot.Name) : _activeWorldSaveName;
                string path = GetWorldSavePath(saveName);
                if (autosave)
                {
                    // All snapshot cloning above happened on the main thread, so nothing the game
                    // touches later can race this save. Write it on a background thread.
                    _autosaveInFlight = true;
                    _autosaveTask = Task.Run(() =>
                    {
                        try { WriteSaveToDisk(snapshot, path); }
                        catch (Exception ex) { TryLogSaveError(ex); }
                        finally { _autosaveInFlight = false; }
                    });
                }
                else
                {
                    WriteSaveToDisk(snapshot, path);
                }
                _saveToastTimer = 3f;
                return true;
            }
            catch (Exception ex)
            {
                TryLogSaveError(ex);
                return false;
            }
        }

        private WorldSave CaptureWorldSave(GameWorld world, bool requireCompleteChunks)
        {
            var save = new WorldSave
            {
                Name = world.Name,
                Seed = world.Seed,
                GenVersion = world.WorldGenVersion,
                Mode = (int)world.Mode,
                IsMinecraftMode = world.IsMinecraftMode,
                FrontierProgression = world.FrontierProgression,
                PlayerX = world.PlayerPosition.X,
                PlayerY = world.PlayerPosition.Y,
                PlayerZ = world.PlayerPosition.Z,
                Yaw = world.PlayerYaw,
                Pitch = world.PlayerPitch,
                SelectedSlot = world.SelectedSlot,
                Hotbar = (int[])world.Hotbar.Clone(),
                HotbarCounts = (int[])world.HotbarCounts.Clone(),
                PlayerHealth = world.LocalPlayer.Health,
                WorldTime = world.WorldTime,
                SapTorchTimers = world.CaptureSapTorchBurnTimers(),
                BombFuses = world.CaptureBombFuses(),
                BombProjectiles = world.CaptureBombProjectiles(),
                Bag = new InventorySlot[40],
                LinkedPositiveXWorld = world.LinkedPositiveXWorld,
                LinkedNegativeXWorld = world.LinkedNegativeXWorld,
                LinkedPositiveZWorld = world.LinkedPositiveZWorld,
                LinkedNegativeZWorld = world.LinkedNegativeZWorld,
            };
            save.CaptureCurrentCustomPalette();
            for (int i = 0; i < save.Bag.Length && i < world.BagSlots.Count; i++)
                save.Bag[i] = world.BagSlots[i];
            if (world.HeldStack is var held && held.HasValue)
            {
                save.HasHeldStack = true;
                save.HeldItemId = held.Value.ItemId;
                save.HeldCount = held.Value.Count;
            }
            bool forceFullChunks = world.WorldGenVersion != GameWorld.GenerationVersion;
            foreach (var coord in world.Chunks.ModifiedChunks)
            {
                if (!world.Chunks.TryGetSaveChunk(coord, forceFullChunks, out var chunkSnapshot))
                {
                    string context = requireCompleteChunks ? "handoff" : "save";
                    throw new IOException($"Modified chunk {coord.Layer}:{coord.X},{coord.Z} is unavailable; {context} was canceled to protect the save.");
                }
                save.Chunks.Add(chunkSnapshot);
            }
            save.Mobs = world.Entities.SaveMobs();
            return save;
        }

        private void WaitForAutosave()
        {
            var pending = _autosaveTask;
            if (pending == null) return;
            try { pending.GetAwaiter().GetResult(); }
            catch (Exception ex) { TryLogSaveError(ex); }
            _autosaveTask = null;
            _autosaveInFlight = false;
        }

        private void WriteSaveToDisk(WorldSave save, string path)
        {
            // One writer at a time: a finishing autosave and an Esc/quit save can never corrupt
            // the file (the quit save simply waits the few milliseconds).
            lock (_saveWriteLock)
            {
                save.Save(path);
            }
        }

        private string StageSaveToDisk(WorldSave save, string destinationPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            string temporaryPath = destinationPath + ".handoff-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                save.Save(temporaryPath);
                var verified = WorldSave.Load(temporaryPath);
                if (verified == null || !SaveSnapshotMatches(save, verified))
                    throw new IOException("The staged world save failed verification.");
                return temporaryPath;
            }
            catch
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
                throw;
            }
        }

        private static void CommitStagedSave(string temporaryPath, string destinationPath, bool requireNewFile = false)
        {
            if (requireNewFile && File.Exists(destinationPath))
                throw new IOException("The destination save name was taken before the handoff could commit.");
            if (File.Exists(destinationPath)) File.Replace(temporaryPath, destinationPath, null);
            else File.Move(temporaryPath, destinationPath);
        }

        private static bool SaveSnapshotMatches(WorldSave expected, WorldSave actual)
        {
            if (!CustomManifestMatches(expected, actual)) return false;
            if (expected.Name != actual.Name || expected.Seed != actual.Seed
                || expected.GenVersion != actual.GenVersion
                || expected.Mode != actual.Mode || expected.IsMinecraftMode != actual.IsMinecraftMode
                || expected.FrontierProgression != actual.FrontierProgression
                || expected.PlayerX != actual.PlayerX || expected.PlayerY != actual.PlayerY
                || expected.PlayerZ != actual.PlayerZ || expected.Yaw != actual.Yaw || expected.Pitch != actual.Pitch
                || expected.SelectedSlot != actual.SelectedSlot || expected.PlayerHealth != actual.PlayerHealth
                || expected.WorldTime != actual.WorldTime || expected.HasHeldStack != actual.HasHeldStack
                || expected.HeldItemId != actual.HeldItemId || expected.HeldCount != actual.HeldCount
                || expected.LinkedPositiveXWorld != actual.LinkedPositiveXWorld
                || expected.LinkedNegativeXWorld != actual.LinkedNegativeXWorld
                || expected.LinkedPositiveZWorld != actual.LinkedPositiveZWorld
                || expected.LinkedNegativeZWorld != actual.LinkedNegativeZWorld
                || !expected.Hotbar.AsSpan().SequenceEqual(actual.Hotbar)
                || !expected.HotbarCounts.AsSpan().SequenceEqual(actual.HotbarCounts)
                || expected.Bag.Length != actual.Bag.Length
                || expected.Chunks.Count != actual.Chunks.Count
                || expected.Mobs.Count != actual.Mobs.Count
                || expected.SapTorchTimers.Count != actual.SapTorchTimers.Count
                || expected.BombFuses.Count != actual.BombFuses.Count
                || expected.BombProjectiles.Count != actual.BombProjectiles.Count) return false;
            for (int i = 0; i < expected.Bag.Length; i++)
                if (expected.Bag[i].ItemId != actual.Bag[i].ItemId || expected.Bag[i].Count != actual.Bag[i].Count) return false;
            for (int i = 0; i < expected.Chunks.Count; i++)
            {
                var a = expected.Chunks[i];
                var b = actual.Chunks[i];
                if (a.Layer != b.Layer || a.X != b.X || a.Z != b.Z
                    || a.SectionY != b.SectionY || a.IsDelta != b.IsDelta) return false;
                if (a.IsDelta)
                {
                    if (a.DeltaCells.Length != b.DeltaCells.Length) return false;
                    for (int cell = 0; cell < a.DeltaCells.Length; cell++)
                    {
                        var ac = a.DeltaCells[cell];
                        var bc = b.DeltaCells[cell];
                        if (ac.Index != bc.Index || ac.Block != bc.Block || ac.Meta != bc.Meta) return false;
                    }
                }
                else if (!a.Blocks.AsSpan().SequenceEqual(b.Blocks)
                    || !a.Meta.AsSpan().SequenceEqual(b.Meta)) return false;
            }
            for (int i = 0; i < expected.Mobs.Count; i++)
            {
                var a = expected.Mobs[i];
                var b = actual.Mobs[i];
                if (a.Type != b.Type || a.X != b.X || a.Y != b.Y || a.Z != b.Z
                    || a.Yaw != b.Yaw || a.Health != b.Health || a.Brute != b.Brute
                    || a.VehicleMeleeHitCount != b.VehicleMeleeHitCount
                    || a.CargoSlots.Length != b.CargoSlots.Length) return false;
                if (a.SteveBlueprintIndex != b.SteveBlueprintIndex
                    || a.SteveAnchorX != b.SteveAnchorX || a.SteveAnchorY != b.SteveAnchorY
                    || a.SteveAnchorZ != b.SteveAnchorZ || a.SteveRotation != b.SteveRotation
                    || a.StevePlacedMask != b.StevePlacedMask
                    || a.StevePlanSize != b.StevePlanSize
                    || a.StevePlanMaterialItemId != b.StevePlanMaterialItemId
                    || a.StevePlanGeneratorVersion != b.StevePlanGeneratorVersion
                    || !a.StevePlanCells.AsSpan().SequenceEqual(b.StevePlanCells)
                    || a.SteveHasSite != b.SteveHasSite
                    || a.SteveProjectSeed != b.SteveProjectSeed
                    || a.SteveMaterialItemId != b.SteveMaterialItemId
                    || a.SteveRequiredMaterialCount != b.SteveRequiredMaterialCount
                    || a.StevePlannerGoal != b.StevePlannerGoal
                    || a.StevePlannerChoiceCount != b.StevePlannerChoiceCount
                    || a.StevePlannerFailureCount != b.StevePlannerFailureCount
                    || a.StevePlannerFailureCooldownTicks != b.StevePlannerFailureCooldownTicks
                    || a.StevePlannerLastFailedGoal != b.StevePlannerLastFailedGoal
                    || a.SteveHasExplorationTarget != b.SteveHasExplorationTarget
                    || a.SteveExplorationHold != b.SteveExplorationHold
                    || a.SteveExplorationTargetX != b.SteveExplorationTargetX
                    || a.SteveExplorationTargetY != b.SteveExplorationTargetY
                    || a.SteveExplorationTargetZ != b.SteveExplorationTargetZ
                    || a.SteveExplorationOriginX != b.SteveExplorationOriginX
                    || a.SteveExplorationOriginZ != b.SteveExplorationOriginZ
                    || a.SteveExplorationRing != b.SteveExplorationRing
                    || a.SteveExplorationSector != b.SteveExplorationSector
                    || a.SteveHasHomeRefuge != b.SteveHasHomeRefuge
                    || a.SteveHomeRefugeX != b.SteveHomeRefugeX
                    || a.SteveHomeRefugeY != b.SteveHomeRefugeY
                    || a.SteveHomeRefugeZ != b.SteveHomeRefugeZ
                    || a.StevePlannerStatePresent != b.StevePlannerStatePresent
                    || a.SteveInventory.Length != b.SteveInventory.Length) return false;
                for (int slot = 0; slot < a.CargoSlots.Length; slot++)
                    if (a.CargoSlots[slot].ItemId != b.CargoSlots[slot].ItemId
                        || a.CargoSlots[slot].Count != b.CargoSlots[slot].Count) return false;
                for (int slot = 0; slot < a.SteveInventory.Length; slot++)
                    if (a.SteveInventory[slot].ItemId != b.SteveInventory[slot].ItemId
                        || a.SteveInventory[slot].Count != b.SteveInventory[slot].Count) return false;
            }
            for (int i = 0; i < expected.SapTorchTimers.Count; i++)
            {
                var a = expected.SapTorchTimers[i];
                var b = actual.SapTorchTimers[i];
                if (a.X != b.X || a.Y != b.Y || a.Z != b.Z
                    || a.GraceEndTick != b.GraceEndTick || a.BurnAtTick != b.BurnAtTick) return false;
            }
            for (int i = 0; i < expected.BombFuses.Count; i++)
            {
                var a = expected.BombFuses[i];
                var b = actual.BombFuses[i];
                if (a.X != b.X || a.Y != b.Y || a.Z != b.Z
                    || a.DueWorldTime != b.DueWorldTime) return false;
            }
            for (int i = 0; i < expected.BombProjectiles.Count; i++)
            {
                var a = expected.BombProjectiles[i];
                var b = actual.BombProjectiles[i];
                if (a.Id != b.Id || a.X != b.X || a.Y != b.Y || a.Z != b.Z
                    || a.VelocityX != b.VelocityX || a.VelocityY != b.VelocityY
                    || a.VelocityZ != b.VelocityZ || a.DueWorldTime != b.DueWorldTime) return false;
            }
            return true;
        }

        private static bool CustomManifestMatches(WorldSave expected, WorldSave actual)
        {
            if (expected.FileVersion >= 24 || actual.FileVersion >= 24)
            {
                if (expected.CustomContentVersion != actual.CustomContentVersion
                    || expected.CustomBaseStockCount != actual.CustomBaseStockCount
                    || expected.CustomBlockCount != actual.CustomBlockCount
                    || expected.CustomBlocks.Count != actual.CustomBlocks.Count
                    || expected.CustomBlockCount != expected.CustomBlocks.Count
                    || !string.Equals(expected.CustomPaletteSignature, actual.CustomPaletteSignature, StringComparison.Ordinal))
                    return false;
                for (int i = 0; i < expected.CustomBlocks.Count; i++)
                    if (!CustomBlockDefinitionMatches(expected.CustomBlocks[i], actual.CustomBlocks[i])) return false;
                return true;
            }
            return expected.CustomContentVersion == actual.CustomContentVersion
                && expected.CustomBlockCount == actual.CustomBlockCount
                && string.Equals(expected.CustomBlockSignature, actual.CustomBlockSignature, StringComparison.Ordinal)
                && string.Equals(expected.CustomBlockIdentitySignature, actual.CustomBlockIdentitySignature, StringComparison.Ordinal);
        }

        private static bool CustomBlockDefinitionMatches(CustomBlockRecord a, CustomBlockRecord b)
        {
            if (!string.Equals(a.Uid, b.Uid, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(a.ContentHash, b.ContentHash, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(a.Id, b.Id, StringComparison.Ordinal)
                || !string.Equals(a.DisplayName, b.DisplayName, StringComparison.Ordinal)
                || a.Solid != b.Solid || a.Opaque != b.Opaque || a.Gravity != b.Gravity
                || a.LightEmission != b.LightEmission || a.Inventory != b.Inventory
                || a.Placeable != b.Placeable || a.Hardness != b.Hardness
                || a.MapColorOverride != b.MapColorOverride
                || a.Faces.Length != b.Faces.Length || a.Aliases.Count != b.Aliases.Count) return false;
            for (int face = 0; face < a.Faces.Length; face++)
                if (!a.Faces[face].AsSpan().SequenceEqual(b.Faces[face])) return false;
            for (int alias = 0; alias < a.Aliases.Count; alias++)
                if (!string.Equals(a.Aliases[alias], b.Aliases[alias], StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        private static void TryLogSaveError(Exception ex)
        {
            try { System.IO.File.AppendAllText("save_error.log", DateTime.Now + " Save failed: " + ex + Environment.NewLine); } catch { }
        }

        private void LoadWorld(WorldSave save, string? saveName = null)
        {
            GameWorld? candidate = null;
            try
            {
                candidate = CreateWorldCandidate(save.Seed, save.Name, (GameMode)save.Mode,
                    save.IsMinecraftMode, save.FrontierProgression);
                ApplyWorldSave(candidate, save);
                ActivateWorld(candidate, string.IsNullOrWhiteSpace(saveName)
                    ? (string.IsNullOrWhiteSpace(save.SourceFileName) ? SanitizeFileName(save.Name) : save.SourceFileName)
                    : saveName, skipSpawn: true);
                candidate = null;
            }
            catch
            {
                candidate?.Dispose();
                throw;
            }
        }

        private void ApplyWorldSave(GameWorld world, WorldSave save)
        {
            if (save.FileVersion >= 24)
            {
                ApplyV24CustomManifest(save, world);
            }
            else if (save.FileVersion >= 22)
            {
                if (save.CustomBlockCount > CustomBlockStore.Records.Count
                    || !string.Equals(save.CustomBlockIdentitySignature,
                        CustomBlockStore.IdentitySignatureForCount(save.CustomBlockCount), StringComparison.Ordinal))
                    throw new IOException($"This world requires the same ordered custom block IDs for its first {save.CustomBlockCount} custom blocks. Restore the matching CustomContent/custom_blocks.json; block IDs cannot be remapped.");
                int additionalCustomBlocks = CustomBlockStore.Records.Count - save.CustomBlockCount;
                if (additionalCustomBlocks > 0)
                    ShiftSavedItemIdsFromBase(save, BlockRegistry.StockCount + save.CustomBlockCount, additionalCustomBlocks);
            }
            else if (save.FileVersion >= 21)
            {
                if (save.CustomBlockCount > CustomBlockStore.Records.Count)
                    throw new IOException($"This world requires {save.CustomBlockCount} custom blocks, but only {CustomBlockStore.Records.Count} are installed.");
                string currentLegacy = CustomBlockStore.LegacySignatureForCount(save.CustomBlockCount);
                bool exact = string.Equals(save.CustomBlockSignature, currentLegacy, StringComparison.OrdinalIgnoreCase);
                bool migrated = CustomBlockStore.V21SignatureMapsToCurrentIdentity(save.CustomBlockCount, save.CustomBlockSignature);
                if (!exact && !migrated)
                    throw new IOException($"This v21 world requires a matching custom block catalog. Its saved block contents do not match the current ordered IDs or the persisted edit history.");
                int additionalCustomBlocks = CustomBlockStore.Records.Count - save.CustomBlockCount;
                if (additionalCustomBlocks > 0)
                    ShiftSavedItemIdsFromBase(save, BlockRegistry.StockCount + save.CustomBlockCount, additionalCustomBlocks);
            }
            else if (CustomBlockStore.Records.Count > 0)
            {
                ValidateLegacyStockBlockIds(save);
                ShiftSavedItemIdsFromBase(save, BlockRegistry.StockCount, CustomBlockStore.Records.Count);
            }
            if (save.Chunks.Exists(c => c.IsDelta))
            {
                // Version 27 changed only sky generation, so its deep/ground sparse deltas
                // still have the current baseline. Sky deltas (including those split from
                // legacy full-height records during load) cannot use the changed sky baseline.
                bool compatibleVersion27Deltas = save.GenVersion == 27
                    && save.Chunks.TrueForAll(c => !c.IsDelta
                        || c.Layer == DeepLayer || c.Layer == GroundLayer);
                if (save.GenVersion != GameWorld.GenerationVersion && !compatibleVersion27Deltas)
                    throw new IOException($"Cannot load sparse chunk deltas: save terrain version {save.GenVersion} does not match supported baseline version {GameWorld.GenerationVersion}.");
                if (save.IsMinecraftMode != world.IsMinecraftMode
                    || save.FrontierProgression != world.FrontierProgression)
                    throw new IOException("Cannot load sparse chunk deltas: the saved block pack/progression does not match the generated terrain baseline.");
            }
            world.WorldGenVersion = save.GenVersion;
            if (save.GenVersion != GameWorld.GenerationVersion) _genWarningTimer = 8f;
            foreach (var c in save.Chunks)
            {
                if (c.IsDelta)
                    world.Chunks.ApplySavedDeltaChunk(c.Layer, c.X, c.Z, c.SectionY, c.DeltaCells);
                else
                    world.Chunks.ApplySavedChunk(c.Layer, c.X, c.Z, c.SectionY, c.Blocks, c.Meta);
            }
            world.PlayerPosition = new Point3D(save.PlayerX, save.PlayerY, save.PlayerZ);
            world.PlayerYaw = save.Yaw;
            world.PlayerPitch = save.Pitch;
            world.PlayerVelocity = Point3D.Zero;
            if (save.Hotbar != null && save.Hotbar.Length == GameWorld.HotbarSlots)
                for (int i = 0; i < GameWorld.HotbarSlots; i++) world.Hotbar[i] = save.Hotbar[i];
            if (save.HotbarCounts != null && save.HotbarCounts.Length == GameWorld.HotbarSlots)
                for (int i = 0; i < GameWorld.HotbarSlots; i++) world.HotbarCounts[i] = save.HotbarCounts[i];
            world.SetSelectedSlot(Math.Clamp(save.SelectedSlot, 0, GameWorld.HotbarSlots - 1));
            world.RestoreBag(save.Bag);
            world.HeldStack = save.HasHeldStack && save.HeldItemId > 0 && save.HeldCount > 0
                ? (save.HeldItemId, save.HeldCount) : null;
            world.LocalPlayer.Health = Math.Clamp(save.PlayerHealth, 0, 10);
            world.SetWorldTime(save.WorldTime);
            if (save.FileVersion >= 11) world.RestoreSapTorchBurnTimers(save.SapTorchTimers);
            else world.InitializeSapTorchBurnTimersForLoadedTorches();
            if (save.FileVersion >= 14) world.RestoreBombFuses(save.BombFuses);
            if (save.FileVersion >= 15) world.RestoreBombProjectiles(save.BombProjectiles);
            world.Entities.LoadMobs(save.Mobs);
            world.LinkedPositiveXWorld = save.LinkedPositiveXWorld ?? "";
            world.LinkedNegativeXWorld = save.LinkedNegativeXWorld ?? "";
            world.LinkedPositiveZWorld = save.LinkedPositiveZWorld ?? "";
            world.LinkedNegativeZWorld = save.LinkedNegativeZWorld ?? "";
            world.Mesher.NeedsMeshUpdate = true;
            // The in-memory save now refers to installed numeric IDs. If a boundary handoff
            // repairs metadata and rewrites this save, it must embed the installed palette.
            save.CaptureCurrentCustomPalette();
        }

        private void ApplyV24CustomManifest(WorldSave save, GameWorld world)
        {
            if (save.CustomContentVersion != CustomBlockStore.SaveManifestVersion)
                throw new IOException($"Unsupported custom-content manifest version {save.CustomContentVersion} in this world.");
            if (save.IsMinecraftMode != BlockRegistry.IsMinecraftMode || save.IsMinecraftMode != world.IsMinecraftMode)
                throw new IOException("This world's saved block pack does not match the installed pack.");
            if (save.CustomBaseStockCount != BlockRegistry.StockCount)
                throw new IOException($"This world was saved with {save.CustomBaseStockCount} stock blocks, but the installed pack has {BlockRegistry.StockCount}. Block IDs cannot be safely remapped.");
            if (save.CustomBlockCount != save.CustomBlocks.Count)
                throw new IOException("This world's embedded custom-block palette is incomplete.");

            // The installed catalog is the single source of truth. This imports missing/conflicting
            // definitions and persists them once; the player's pack is never mutated merely by saving.
            var uidMap = CustomBlockStore.ImportWorldBlocks(save.CustomBlocks);
            ReloadCurrentCatalogWithCustomBlocks();
            if (save.CustomBaseStockCount != BlockRegistry.StockCount)
                throw new IOException("The installed stock pack changed while importing this world's custom blocks.");

            int[] indexMap = BuildV24CustomIndexMap(save.CustomBlocks, uidMap);
            RemapSavedBlockIds(save, save.CustomBaseStockCount, indexMap);
            RemapSavedItemIdsV24(save, save.CustomBaseStockCount, indexMap);
        }

        private static int[] BuildV24CustomIndexMap(
            IReadOnlyList<CustomBlockRecord> savedBlocks,
            IReadOnlyDictionary<string, string> uidMap)
        {
            var installed = CustomBlockStore.Records;
            var installedIndexByUid = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < installed.Count; i++) installedIndexByUid[installed[i].Uid] = i;

            var map = new int[savedBlocks.Count];
            for (int i = 0; i < savedBlocks.Count; i++)
            {
                var saved = savedBlocks[i];
                if (!uidMap.TryGetValue(saved.Uid, out string? installedUid)
                    || !installedIndexByUid.TryGetValue(installedUid, out int installedIndex))
                    throw new IOException($"Could not resolve saved custom block '{saved.DisplayName}' to an installed block.");
                if (installedIndex < 0 || installedIndex >= installed.Count)
                    throw new IOException($"Saved custom block '{saved.DisplayName}' mapped outside the installed custom catalog.");
                if (!string.Equals(saved.ContentHash, installed[installedIndex].ContentHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"Saved custom block '{saved.DisplayName}' resolved to different content. Refusing to use the wrong block.");
                map[i] = installedIndex;
            }
            return map;
        }

        private static void RemapSavedBlockIds(WorldSave save, int savedStockCount, IReadOnlyList<int> customIndexMap)
        {
            int installedStockCount = BlockRegistry.StockCount;
            int installedBlockCount = BlockRegistry.Count;
            int savedCustomCount = customIndexMap.Count;

            byte Remap(byte block)
            {
                if (block < savedStockCount) return block;
                int savedCustomIndex = block - savedStockCount;
                if ((uint)savedCustomIndex >= (uint)savedCustomCount)
                    throw new IOException($"This world contains block ID {block}, which is outside its saved custom palette.");
                int installedIndex = customIndexMap[savedCustomIndex];
                int installedId = installedStockCount + installedIndex;
                if (installedIndex < 0 || (uint)installedId >= (uint)installedBlockCount || installedId > byte.MaxValue)
                    throw new IOException($"Saved custom block {savedCustomIndex + 1} mapped outside the installed block catalog.");
                return (byte)installedId;
            }

            foreach (var chunk in save.Chunks)
            {
                if (chunk.IsDelta)
                {
                    for (int i = 0; i < chunk.DeltaCells.Length; i++)
                    {
                        var cell = chunk.DeltaCells[i];
                        byte mapped = Remap(cell.Block);
                        if (mapped != cell.Block)
                            chunk.DeltaCells[i] = new SavedChunkDeltaCell(cell.Index, mapped, cell.Meta);
                    }
                }
                else
                {
                    for (int i = 0; i < chunk.Blocks.Length; i++)
                        chunk.Blocks[i] = Remap(chunk.Blocks[i]);
                }
            }
        }

        private static void RemapSavedItemIdsV24(WorldSave save, int savedStockCount, IReadOnlyList<int> customIndexMap)
        {
            int installedStockCount = BlockRegistry.StockCount;
            int installedBlockCount = BlockRegistry.Count;
            int savedBlockCount = savedStockCount + customIndexMap.Count;
            int shift = installedBlockCount - savedBlockCount;

            int Remap(int id)
            {
                if (id <= 0) return id;
                if (id < savedStockCount) return id;
                int savedCustomIndex = id - savedStockCount;
                if ((uint)savedCustomIndex < (uint)customIndexMap.Count)
                {
                    int installedIndex = customIndexMap[savedCustomIndex];
                    int installedId = installedStockCount + installedIndex;
                    if (installedIndex < 0 || (uint)installedId >= (uint)installedBlockCount)
                        throw new IOException($"Saved custom block item {id} mapped outside the installed item catalog.");
                    return installedId;
                }

                int shifted = id + shift;
                if (shifted < installedBlockCount || shifted >= ItemRegistry.Count)
                    throw new IOException($"This world contains item ID {id}, which is outside the installed item catalog.");
                return shifted;
            }

            if (save.Hotbar != null)
                for (int i = 0; i < save.Hotbar.Length; i++) save.Hotbar[i] = Remap(save.Hotbar[i]);
            if (save.Bag != null)
                for (int i = 0; i < save.Bag.Length; i++)
                    save.Bag[i] = new InventorySlot { ItemId = Remap(save.Bag[i].ItemId), Count = save.Bag[i].Count };
            if (save.HasHeldStack) save.HeldItemId = Remap(save.HeldItemId);
            foreach (var mob in save.Mobs)
            {
                for (int i = 0; i < mob.CargoSlots.Length; i++)
                    mob.CargoSlots[i] = new InventorySlot { ItemId = Remap(mob.CargoSlots[i].ItemId), Count = mob.CargoSlots[i].Count };
                for (int i = 0; i < mob.SteveInventory.Length; i++)
                    mob.SteveInventory[i] = new InventorySlot { ItemId = Remap(mob.SteveInventory[i].ItemId), Count = mob.SteveInventory[i].Count };
                mob.SteveMaterialItemId = Remap(mob.SteveMaterialItemId);
                mob.StevePlanMaterialItemId = Remap(mob.StevePlanMaterialItemId);
            }
        }

        private static void ValidateLegacyStockBlockIds(WorldSave save)
        {
            int stockCount = BlockRegistry.StockCount;
            foreach (var chunk in save.Chunks)
            {
                if (chunk.IsDelta)
                {
                    foreach (var cell in chunk.DeltaCells)
                        if (cell.Block >= stockCount)
                            throw new IOException("This pre-v21 world contains block IDs outside its stock catalog and cannot be safely mapped to the installed custom catalog.");
                }
                else
                {
                    foreach (byte block in chunk.Blocks)
                        if (block >= stockCount)
                            throw new IOException("This pre-v21 world contains block IDs outside its stock catalog and cannot be safely mapped to the installed custom catalog.");
                }
            }
        }

        private static void ShiftSavedItemIdsFromBase(WorldSave save, int firstShiftedId, int amount)
        {
            int added = Math.Max(0, amount);
            if (added <= 0) return;
            static int Shift(int id, int baseId, int amount) => id >= baseId ? id + amount : id;
            for (int i = 0; i < save.Hotbar.Length; i++) save.Hotbar[i] = Shift(save.Hotbar[i], firstShiftedId, added);
            for (int i = 0; i < save.Bag.Length; i++)
                save.Bag[i] = new InventorySlot { ItemId = Shift(save.Bag[i].ItemId, firstShiftedId, added), Count = save.Bag[i].Count };
            if (save.HasHeldStack) save.HeldItemId = Shift(save.HeldItemId, firstShiftedId, added);
            foreach (var mob in save.Mobs)
            {
                for (int i = 0; i < mob.CargoSlots.Length; i++)
                    mob.CargoSlots[i] = new InventorySlot { ItemId = Shift(mob.CargoSlots[i].ItemId, firstShiftedId, added), Count = mob.CargoSlots[i].Count };
                for (int i = 0; i < mob.SteveInventory.Length; i++)
                    mob.SteveInventory[i] = new InventorySlot { ItemId = Shift(mob.SteveInventory[i].ItemId, firstShiftedId, added), Count = mob.SteveInventory[i].Count };
                mob.SteveMaterialItemId = Shift(mob.SteveMaterialItemId, firstShiftedId, added);
                mob.StevePlanMaterialItemId = Shift(mob.StevePlanMaterialItemId, firstShiftedId, added);
            }
        }

        private void RefreshSavedWorlds()
        {
            menu.SavedWorlds.Clear();
            try
            {
                if (!Directory.Exists(SavesFolder)) return;
                foreach (var file in Directory.GetFiles(SavesFolder, "*.cubuild"))
                {
                    menu.SavedWorlds.Add(Path.GetFileNameWithoutExtension(file));
                }
            }
            catch
            {
            }
        }

        private void DeleteWorld(int index)
        {
            if (index < 0 || index >= menu.SavedWorlds.Count) return;
            string name = menu.SavedWorlds[index];
            string path = Path.Combine(SavesFolder, SanitizeFileName(name) + ".cubuild");
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            RefreshSavedWorlds();
        }

        private void RenameWorld(int index, string newName)
        {
            if (index < 0 || index >= menu.SavedWorlds.Count) return;
            if (string.IsNullOrWhiteSpace(newName)) return;
            string oldName = menu.SavedWorlds[index];
            string oldPath = Path.Combine(SavesFolder, SanitizeFileName(oldName) + ".cubuild");
            string newPath = Path.Combine(SavesFolder, SanitizeFileName(newName) + ".cubuild");
            try
            {
                if (File.Exists(oldPath) && !string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(newPath)) File.Delete(newPath);
                    File.Move(oldPath, newPath);
                }
            }
            catch { }
            RefreshSavedWorlds();
        }

        private static bool _legacySavesMigrated;
        private static string SavesFolder
        {
            get
            {
                string stable = System.IO.Path.Combine(CustomBlockStore.DataDirectory, "saves");
                if (!_legacySavesMigrated)
                {
                    _legacySavesMigrated = true;
                    var legacyFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    string? processDirectory = System.IO.Path.GetDirectoryName(Environment.ProcessPath);
                    if (!string.IsNullOrWhiteSpace(processDirectory))
                        legacyFolders.Add(System.IO.Path.Combine(processDirectory, "saves"));
                    legacyFolders.Add(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "saves"));
                    foreach (string legacy in legacyFolders)
                    {
                        if (string.Equals(legacy, stable, StringComparison.OrdinalIgnoreCase)
                            || !System.IO.Directory.Exists(legacy)) continue;
                        try
                        {
                            System.IO.Directory.CreateDirectory(stable);
                            foreach (string file in System.IO.Directory.GetFiles(legacy, "*.cubuild"))
                            {
                                string destination = System.IO.Path.Combine(stable, System.IO.Path.GetFileName(file));
                                if (!System.IO.File.Exists(destination))
                                    System.IO.File.Copy(file, destination, false);
                            }
                        }
                        catch (System.IO.IOException) { }
                    }
                }
                return stable;
            }
        }

        private static string GetWorldSavePath(string saveName)
            => Path.Combine(SavesFolder, SanitizeFileName(saveName) + ".cubuild");

        private static string SanitizeFileName(string name)
        {
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "World" : name;
        }

        // ------------------------------------------------------------------
        // main loop
        // ------------------------------------------------------------------

    }
}
