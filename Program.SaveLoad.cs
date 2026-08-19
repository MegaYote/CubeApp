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
            LoadWorld(save);
        }

/// <summary>Seconds between background autosaves. Esc-save and quit-save still exist;
        /// this only closes the crash/power-loss window.</summary>
        private const float AutosaveIntervalSeconds = 180f;
        private readonly object _saveWriteLock = new();
        private volatile bool _autosaveInFlight;
        private float _autosaveTimer;
        private float _saveToastTimer;

        /// <summary>Called every frame while playing. When the interval elapses, snapshots the
        /// world and writes it to disk on a background thread so the game never stalls and
        /// crashes/power loss only cost a few minutes of progress.</summary>
        private void MaybeAutosave(float deltaSeconds)
        {
            _saveToastTimer = Math.Max(0f, _saveToastTimer - deltaSeconds);
            if (World == null || _autosaveInFlight) return;
            _autosaveTimer += deltaSeconds;
            if (_autosaveTimer >= AutosaveIntervalSeconds)
            {
                _autosaveTimer = 0f;
                SaveWorld(autosave: true);
            }
        }

        private void SaveWorld(bool autosave = false)
        {
            if (World == null) return;
            _autosaveTimer = 0f;
            try
            {
                Directory.CreateDirectory(SavesFolder);
                var save = new WorldSave
                {
                    Name = World.Name,
                    Seed = World.Seed,
                    PlayerX = World.PlayerPosition.X,
                    PlayerY = World.PlayerPosition.Y,
                    PlayerZ = World.PlayerPosition.Z,
                    Yaw = World.PlayerYaw,
                    Pitch = World.PlayerPitch,
                    SelectedSlot = World.SelectedSlot,
                    Hotbar = (int[])World.Hotbar.Clone(),
                    PlayerHealth = World.LocalPlayer.Health,
                    WorldTime = World.WorldTime,
                    Bag = new InventorySlot[40],
                };
                for (int i = 0; i < 40 && i < World.BagSlots.Count; i++)
                {
                    save.Bag[i] = World.BagSlots[i];
                }
                if (World.HeldStack is var held && held.HasValue)
                {
                    save.HasHeldStack = true;
                    save.HeldItemId = held.Value.ItemId;
                    save.HeldCount = held.Value.Count;
                }
                foreach (var coord in World.Chunks.ModifiedChunks)
                {
                    byte[] blocks;
                    byte[] meta;
                    if (World.Chunks.TryGetLoadedChunk(coord, out var chunk))
                    {
                        blocks = chunk.RawBlocks;
                        meta = chunk.RawMeta;
                    }
                    else if (World.Chunks.TryGetCachedUnloadedChunk(coord.Layer, coord.X, coord.Z, out var cBlocks, out var cMeta))
                    {
                        // Unloaded but snapshotted at unload time - edits survive chunk streaming.
                        blocks = cBlocks;
                        meta = cMeta;
                    }
                    else
                    {
                        continue; // modified, unloaded, and evicted from the cache - nothing to write
                    }
                    save.Chunks.Add(new SavedChunk
                    {
                        Layer = coord.Layer,
                        X = coord.X,
                        Z = coord.Z,
                        Blocks = (byte[])blocks.Clone(),
                        Meta = (byte[])meta.Clone(),
                    });
                }
                save.Mobs = World.Entities.SaveMobs();
                string path = Path.Combine(SavesFolder, SanitizeFileName(save.Name) + ".cubuild");
                if (autosave)
                {
                    // All snapshot cloning above happened on the main thread, so nothing the game
                    // touches later can race this save. Write it on a background thread.
                    _autosaveInFlight = true;
                    var snapshot = save;
                    Task.Run(() =>
                    {
                        try { WriteSaveToDisk(snapshot, path); }
                        catch (Exception ex) { TryLogSaveError(ex); }
                        finally { _autosaveInFlight = false; }
                    });
                }
                else
                {
                    WriteSaveToDisk(save, path);
                }
                _saveToastTimer = 3f;
            }
            catch (Exception ex)
            {
                TryLogSaveError(ex);
            }
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

        private static void TryLogSaveError(Exception ex)
        {
            try { System.IO.File.AppendAllText("save_error.log", DateTime.Now + " Save failed: " + ex + Environment.NewLine); } catch { }
        }

        private void LoadWorld(WorldSave save)
        {
            _loadSkipSpawn = true;
            StartNewWorld(save.Seed, save.Name, (GameMode)save.Mode);
            foreach (var c in save.Chunks)
            {
                World.Chunks.ApplySavedChunk(c.Layer, c.X, c.Z, c.Blocks, c.Meta);
            }
            World.PlayerPosition = new Point3D(save.PlayerX, save.PlayerY, save.PlayerZ);
            World.PlayerYaw = save.Yaw;
            World.PlayerPitch = save.Pitch;
            World.PlayerVelocity = new Point3D(0, 0, 0);
            if (save.Hotbar != null && save.Hotbar.Length == GameWorld.HotbarSlots)
            {
                for (int i = 0; i < GameWorld.HotbarSlots; i++) World.Hotbar[i] = save.Hotbar[i];
            }
            World.SetSelectedSlot(Math.Clamp(save.SelectedSlot, 0, GameWorld.HotbarSlots - 1));
            World.RestoreBag(save.Bag);
            World.HeldStack = save.HasHeldStack && save.HeldItemId > 0 && save.HeldCount > 0
                ? (save.HeldItemId, save.HeldCount) : null;
            World.LocalPlayer.Health = Math.Clamp(save.PlayerHealth, 0, 10);
            World.SetWorldTime(save.WorldTime);
            World.Entities.LoadMobs(save.Mobs);
            needsMeshUpdate = true;
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