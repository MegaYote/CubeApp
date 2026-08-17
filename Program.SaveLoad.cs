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

        private void SaveWorld()
        {
            if (World == null) return;
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
                    if (World.Chunks.TryGetLoadedChunk(coord, out var chunk))
                    {
                        save.Chunks.Add(new SavedChunk
                        {
                            Layer = coord.Layer,
                            X = coord.X,
                            Z = coord.Z,
                            Blocks = (byte[])chunk.RawBlocks.Clone(),
                            Meta = (byte[])chunk.RawMeta.Clone(),
                        });
                    }
                }
                save.Mobs = World.Entities.SaveMobs();
                save.Save(Path.Combine(SavesFolder, SanitizeFileName(save.Name) + ".cubuild"));
            }
            catch (Exception ex)
            {
                try { System.IO.File.AppendAllText("save_error.log", DateTime.Now + " Save failed: " + ex + Environment.NewLine); } catch { }
            }
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