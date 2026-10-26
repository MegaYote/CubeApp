using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Cubuild
{
    public sealed class SavedChunk
    {
        public int Layer;
        public int X, Z;
        /// <summary>v13: true when this record contains sparse final-cell patches instead of
        /// full arrays. Older saves always load as full snapshots.</summary>
        public bool IsDelta;
        public SavedChunkDeltaCell[] DeltaCells = Array.Empty<SavedChunkDeltaCell>();
        public byte[] Blocks = Array.Empty<byte>();
        public byte[] Meta = Array.Empty<byte>();
    }

    public sealed class SavedMob
    {
        public string Type = "";
        public double X, Y, Z;
        public float Yaw;
        public int Health = 10;
        /// <summary>Rare brute variant (1 in 50 zombies): 2x size, half speed, double health.</summary>
        public bool Brute;
        /// <summary>Successful melee hits received by a vehicle before this save.</summary>
        public int VehicleMeleeHitCount;
        /// <summary>v10: ore-cart-only cargo payload; other mob types leave this empty.</summary>
        public InventorySlot[] CargoSlots = new InventorySlot[OreCartEntity.CargoSlotCount];
    }

    /// <summary>World-time burnout schedule for one sap torch (v11).</summary>
    public sealed class SavedSapTorchTimer
    {
        public int X, Y, Z;
        public long GraceEndTick;
        /// <summary>Zero until the grace period elapses and the randomized lifetime is chosen.</summary>
        public long BurnAtTick;
    }

    /// <summary>
    /// One world save: header (name/seed), player state, modified chunks (full arrays or v13
    /// sparse deltas), and mob state. Serialized to a Deflate-compressed .cubuild file.
    /// </summary>
    public sealed class WorldSave
    {
        public const string Magic = "CUBW";
        public const int Version = 13;
        /// <summary>Binary version read from disk; new/in-memory saves use the current version.</summary>
        public int FileVersion = Version;

        public string Name = "World 1";
        /// <summary>Runtime-only save-file key (the path may differ from Name after a rename).</summary>
        public string SourceFileName = "";
        public int Seed;
        /// <summary>
        /// Terrain-generation version this world was originally made with (stamped at creation).
        /// 0 = made before versioning existed. When this differs from the current build's
        /// generation version, unexplored terrain may regenerate differently - the game shows a
        /// warning instead of mutating silently. Never upgraded on save so a world keeps its
        /// true age.
        /// </summary>
        public int GenVersion;
        /// <summary>GameMode int (0 = Creative, 1 = Survival). Older saves default to Creative.</summary>
        public int Mode = (int)GameMode.Creative;
        /// <summary>Classic pack fork: when true, load Minecraft-faithful blocks/items/recipes
    /// (shown as "Classic" in the menu); false is the default "Complex" pack.</summary>
        public bool IsMinecraftMode;
        /// <summary>v9: use the station-tiered Classic Survival recipe progression.</summary>
        public bool FrontierProgression;
        public double PlayerX, PlayerY, PlayerZ;
        public float Yaw, Pitch;
        public int SelectedSlot;
        public int[] Hotbar = new int[10];
        public int[] HotbarCounts = new int[10];
        /// <summary>v5: full E-menu bag (4 rows x 10), Minecraft-style inventory persistence.</summary>
        public InventorySlot[] Bag = new InventorySlot[40];
        /// <summary>v5: stack riding the cursor when the inventory closed.</summary>
        public bool HasHeldStack;
        public int HeldItemId;
        public int HeldCount;
        /// <summary>v5: player hearts (0..10).</summary>
        public int PlayerHealth = 10;
        /// <summary>v5: day/night clock in world ticks (full cycle = 24000).</summary>
        public long WorldTime;
        public List<SavedChunk> Chunks = new();
        public List<SavedMob> Mobs = new();
        public List<SavedSapTorchTimer> SapTorchTimers = new();
        /// <summary>v12: neighboring world-save keys for the four horizontal boundaries.</summary>
        public string LinkedPositiveXWorld = "";
        public string LinkedNegativeXWorld = "";
        public string LinkedPositiveZWorld = "";
        public string LinkedNegativeZWorld = "";

        public void Save(string path)
        {
            // Stream through Deflate directly. Large full-array fallbacks should not require a
            // second, uncompressed copy of the entire world in memory.
            using var file = File.Create(path);
            using var deflate = new DeflateStream(file, CompressionLevel.Optimal);
            using (var w = new BinaryWriter(deflate, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(Magic);
                w.Write(Version);
                w.Write(Name);
                w.Write(Seed);
                w.Write(Mode);
                w.Write(PlayerX);
                w.Write(PlayerY);
                w.Write(PlayerZ);
                w.Write(Yaw);
                w.Write(Pitch);
                w.Write(SelectedSlot);
                for (int i = 0; i < 10; i++) w.Write(i < Hotbar.Length ? Hotbar[i] : 0);
                for (int i = 0; i < 10; i++) w.Write(i < HotbarCounts.Length ? HotbarCounts[i] : 0);

                // v5: full player + inventory state (bag, cursor, health, time of day).
                w.Write(PlayerHealth);
                w.Write(WorldTime);
                for (int i = 0; i < 40; i++)
                {
                    var slot = i < Bag.Length ? Bag[i] : default;
                    w.Write(slot.ItemId);
                    w.Write(slot.Count);
                }
                w.Write(HasHeldStack);
                w.Write(HeldItemId);
                w.Write(HeldCount);
                w.Write(GenVersion);
                w.Write(IsMinecraftMode);
                w.Write(FrontierProgression);

                w.Write(Chunks.Count);
                foreach (var c in Chunks)
                {
                    w.Write(c.Layer);
                    w.Write(c.X);
                    w.Write(c.Z);
                    w.Write(c.IsDelta);
                    if (c.IsDelta)
                    {
                        w.Write(c.DeltaCells.Length);
                        foreach (var cell in c.DeltaCells)
                        {
                            w.Write(cell.Index); // Int32: sky chunks exceed 65,535 cells
                            w.Write(cell.Block);
                            w.Write(cell.Meta);
                        }
                    }
                    else
                    {
                        w.Write(c.Blocks.Length);
                        w.Write(c.Blocks);
                        w.Write(c.Meta.Length);
                        w.Write(c.Meta);
                    }
                }

                w.Write(Mobs.Count);
                foreach (var m in Mobs)
                {
                    w.Write(m.Type);
                    w.Write(m.X);
                    w.Write(m.Y);
                    w.Write(m.Z);
                    w.Write(m.Yaw);
                    w.Write(m.Health);
                    w.Write(m.Brute);
                    w.Write(m.VehicleMeleeHitCount);
                    if (m.Type == "orecart")
                    {
                        for (int i = 0; i < OreCartEntity.CargoSlotCount; i++)
                        {
                            var slot = i < m.CargoSlots.Length ? m.CargoSlots[i] : default;
                            w.Write(slot.ItemId);
                            w.Write(slot.Count);
                        }
                    }
                }

                // v11: absolute world-time schedule for each placed sap torch.
                w.Write(SapTorchTimers.Count);
                foreach (var timer in SapTorchTimers)
                {
                    w.Write(timer.X);
                    w.Write(timer.Y);
                    w.Write(timer.Z);
                    w.Write(timer.GraceEndTick);
                    w.Write(timer.BurnAtTick);
                }

                // v12: side-specific links are save-file keys, not display names. Older saves
                // end immediately after the sap-torch timers and load with empty links.
                w.Write(LinkedPositiveXWorld ?? "");
                w.Write(LinkedNegativeXWorld ?? "");
                w.Write(LinkedPositiveZWorld ?? "");
                w.Write(LinkedNegativeZWorld ?? "");
                w.Flush();
            }
        }

        public static WorldSave? Load(string path)
        {
            try
            {
                using var file = File.OpenRead(path);
                using var inflate = new DeflateStream(file, CompressionMode.Decompress);
                using var reader = new BinaryReader(inflate, Encoding.UTF8);
                if (reader.ReadString() != Magic) return null;
                int version = reader.ReadInt32();
                if (version > Version) return null;

                var save = new WorldSave();
                save.FileVersion = version;
                save.SourceFileName = Path.GetFileNameWithoutExtension(path);
                save.Name = reader.ReadString();
                save.Seed = reader.ReadInt32();
                if (version >= 3) save.Mode = reader.ReadInt32();
                save.PlayerX = reader.ReadDouble();
                save.PlayerY = reader.ReadDouble();
                save.PlayerZ = reader.ReadDouble();
                save.Yaw = reader.ReadSingle();
                save.Pitch = reader.ReadSingle();
                save.SelectedSlot = reader.ReadInt32();
                save.Hotbar = new int[10];
                for (int i = 0; i < 10; i++) save.Hotbar[i] = reader.ReadInt32();
                save.HotbarCounts = new int[10];
                for (int i = 0; i < 10; i++) save.HotbarCounts[i] = reader.ReadInt32();

                if (version >= 5)
                {
                    save.PlayerHealth = reader.ReadInt32();
                    save.WorldTime = reader.ReadInt64();
                    save.Bag = new InventorySlot[40];
                    for (int i = 0; i < 40; i++)
                    {
                        save.Bag[i] = new InventorySlot { ItemId = reader.ReadInt32(), Count = reader.ReadInt32() };
                    }
                    save.HasHeldStack = reader.ReadBoolean();
                    save.HeldItemId = reader.ReadInt32();
                    save.HeldCount = reader.ReadInt32();
                }
                if (version >= 6) save.GenVersion = reader.ReadInt32();
                if (version >= 7) save.IsMinecraftMode = reader.ReadBoolean();
                if (version >= 9) save.FrontierProgression = reader.ReadBoolean();

                int chunkCount = reader.ReadInt32();
                for (int i = 0; i < chunkCount; i++)
                {
                    var c = new SavedChunk();
                    if (version >= 2) c.Layer = reader.ReadInt32();
                    c.X = reader.ReadInt32();
                    c.Z = reader.ReadInt32();
                    if (version >= 13)
                    {
                        c.IsDelta = reader.ReadBoolean();
                        if (c.IsDelta)
                        {
                            int deltaCount = reader.ReadInt32();
                            if (deltaCount < 0) throw new InvalidDataException("Negative sparse chunk delta count.");
                            c.DeltaCells = new SavedChunkDeltaCell[deltaCount];
                            int previousIndex = -1;
                            for (int cellIndex = 0; cellIndex < deltaCount; cellIndex++)
                            {
                                int index = reader.ReadInt32();
                                if (index < 0 || index <= previousIndex)
                                    throw new InvalidDataException("Sparse chunk delta indexes are not strictly sorted.");
                                previousIndex = index;
                                c.DeltaCells[cellIndex] = new SavedChunkDeltaCell(index, reader.ReadByte(), reader.ReadByte());
                            }
                        }
                        else
                        {
                            int blen = reader.ReadInt32();
                            c.Blocks = reader.ReadBytes(blen);
                            int mlen = reader.ReadInt32();
                            c.Meta = reader.ReadBytes(mlen);
                        }
                    }
                    else
                    {
                        // v1-v12 chunk payload is unchanged: all records are full arrays.
                        int blen = reader.ReadInt32();
                        c.Blocks = reader.ReadBytes(blen);
                        int mlen = reader.ReadInt32();
                        c.Meta = reader.ReadBytes(mlen);
                    }
                    save.Chunks.Add(c);
                }

                int mobCount = reader.ReadInt32();
                for (int i = 0; i < mobCount; i++)
                {
                    var m = new SavedMob
                    {
                        Type = reader.ReadString(),
                        X = reader.ReadDouble(),
                        Y = reader.ReadDouble(),
                        Z = reader.ReadDouble(),
                        Yaw = reader.ReadSingle(),
                        Health = reader.ReadInt32(),
                    };
                    if (version >= 4) m.Brute = reader.ReadBoolean();
                    if (version >= 8) m.VehicleMeleeHitCount = reader.ReadInt32();
                    if (version >= 10 && m.Type == "orecart")
                    {
                        m.CargoSlots = new InventorySlot[OreCartEntity.CargoSlotCount];
                        for (int slot = 0; slot < m.CargoSlots.Length; slot++)
                        {
                            m.CargoSlots[slot] = new InventorySlot
                            {
                                ItemId = reader.ReadInt32(),
                                Count = reader.ReadInt32(),
                            };
                        }
                    }
                    else if (!string.Equals(m.Type, "orecart", StringComparison.Ordinal))
                    {
                        // Regular mobs serialize no cargo payload. Keep the in-memory shape aligned
                        // with EntityManager.SaveMobs(), which uses Array.Empty for non-orecarts;
                        // boundary handoff save verification compares this snapshot after read-back.
                        m.CargoSlots = Array.Empty<InventorySlot>();
                    }
                    save.Mobs.Add(m);
                }
                if (version >= 11)
                {
                    int timerCount = reader.ReadInt32();
                    for (int i = 0; i < timerCount; i++)
                    {
                        save.SapTorchTimers.Add(new SavedSapTorchTimer
                        {
                            X = reader.ReadInt32(),
                            Y = reader.ReadInt32(),
                            Z = reader.ReadInt32(),
                            GraceEndTick = reader.ReadInt64(),
                            BurnAtTick = reader.ReadInt64(),
                        });
                    }
                }
                if (version >= 12)
                {
                    save.LinkedPositiveXWorld = reader.ReadString();
                    save.LinkedNegativeXWorld = reader.ReadString();
                    save.LinkedPositiveZWorld = reader.ReadString();
                    save.LinkedNegativeZWorld = reader.ReadString();
                }
                return save;
            }
            catch
            {
                return null;
            }
        }
    }
}
