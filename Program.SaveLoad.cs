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
                if (a.Layer != b.Layer || a.X != b.X || a.Z != b.Z || a.IsDelta != b.IsDelta) return false;
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
                for (int slot = 0; slot < a.CargoSlots.Length; slot++)
                    if (a.CargoSlots[slot].ItemId != b.CargoSlots[slot].ItemId
                        || a.CargoSlots[slot].Count != b.CargoSlots[slot].Count) return false;
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
            if (save.Chunks.Exists(c => c.IsDelta))
            {
                // Sparse deltas only have meaning against the exact generation identity that
                // produced their baseline. Future generator-version bumps must retain/recreate
                // old baseline generation before enabling delta saves for those older versions.
                if (save.GenVersion != GameWorld.GenerationVersion)
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
                    world.Chunks.ApplySavedDeltaChunk(c.Layer, c.X, c.Z, c.DeltaCells);
                else
                    world.Chunks.ApplySavedChunk(c.Layer, c.X, c.Z, c.Blocks, c.Meta);
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

        private static string SavesFolder => System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "saves");

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
