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
    }

    /// <summary>
    /// One world save: header (name/seed), player state, the modified chunks (block + meta
    /// bytes), and mob state. Serialized to a Deflate-compressed .cubuild file.
    /// </summary>
    public sealed class WorldSave
    {
        public const string Magic = "CUBW";
        public const int Version = 5;

        public string Name = "World 1";
        public int Seed;
        /// <summary>GameMode int (0 = Creative, 1 = Survival). Older saves default to Creative.</summary>
        public int Mode = (int)GameMode.Creative;
        public double PlayerX, PlayerY, PlayerZ;
        public float Yaw, Pitch;
        public int SelectedSlot;
        public int[] Hotbar = new int[10];
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

        public void Save(string path)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
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

                w.Write(Chunks.Count);
                foreach (var c in Chunks)
                {
                    w.Write(c.Layer);
                    w.Write(c.X);
                    w.Write(c.Z);
                    w.Write(c.Blocks.Length);
                    w.Write(c.Blocks);
                    w.Write(c.Meta.Length);
                    w.Write(c.Meta);
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
                }
            }

            using var file = File.Create(path);
            using var deflate = new DeflateStream(file, CompressionLevel.Optimal);
            ms.Position = 0;
            ms.CopyTo(deflate);
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

                int chunkCount = reader.ReadInt32();
                for (int i = 0; i < chunkCount; i++)
                {
                    var c = new SavedChunk();
                    if (version >= 2) c.Layer = reader.ReadInt32();
                    c.X = reader.ReadInt32();
                    c.Z = reader.ReadInt32();
                    int blen = reader.ReadInt32();
                    c.Blocks = reader.ReadBytes(blen);
                    int mlen = reader.ReadInt32();
                    c.Meta = reader.ReadBytes(mlen);
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
                    save.Mobs.Add(m);
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
