using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cubuild
{
    /// <summary>Player-authored cube blocks. Pixel data is row-major RGBA (16x16).</summary>
    public sealed class CustomBlockRecord
    {
        public string Id { get; set; } = "";
        public string DisplayName { get; set; } = "";
        // Legacy shared image retained verbatim for v21 save/history compatibility.
        public byte[] Pixels { get; set; } = Array.Empty<byte>();
        // v3 images in Top, Bottom, East, West, South, North order.
        public byte[][] Faces { get; set; } = Array.Empty<byte[]>();
        public bool Solid { get; set; } = true;
        // Null means an older record: derive light blocking from its pixel alpha.
        public bool? Opaque { get; set; }
        public bool Gravity { get; set; }
        public int LightEmission { get; set; }
        public bool Inventory { get; set; } = true;
        public bool Placeable { get; set; } = true;
        public float Hardness { get; set; } = 1f;
        // Null keeps the map swatch auto-derived from the texture.
        public uint? MapColorOverride { get; set; }
    }

    public sealed class CustomBlockHistoryEntry
    {
        public int Count { get; set; }
        public string LegacySignature { get; set; } = "";
        public string IdentitySignature { get; set; } = "";
    }

    public sealed class CustomBlockCatalog
    {
        public int SchemaVersion { get; set; } = 3;
        public List<CustomBlockRecord> Blocks { get; set; } = null!;
        public List<CustomBlockHistoryEntry> V21History { get; set; } = new();
    }

    /// <summary>Dedicated persistent store for player-created blocks; never writes stock assets.</summary>
    public static class CustomBlockStore
    {
        public const int MaxBlocks = 126;
        public const int PixelBytes = 16 * 16 * 4;
        public const int FaceCount = 6;
        public const int CustomPageCount = 3;
        public const int TilesPerPage = 256;
        public static int Capacity => BlockRegistry.Loaded
            ? Math.Max(0, Math.Min(MaxBlocks, 256 - BlockRegistry.StockCount)) : MaxBlocks;
        private static readonly object Gate = new();
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
        private static List<CustomBlockRecord> _records = new();
        private static List<CustomBlockHistoryEntry> _v21History = new();
        internal static string? DataDirectoryOverride { get; set; }
        public static string DataDirectory => DataDirectoryOverride
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cubuild");
        public static string FilePath
        {
            get
            {
                string path = Path.Combine(DataDirectory, "CustomContent", "custom_blocks.json");
                MigrateFileIfMissing(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CustomContent", "custom_blocks.json"), path);
                return path;
            }
        }
        public static IReadOnlyList<CustomBlockRecord> Records { get { lock (Gate) return _records.ToArray(); } }
        public static int Generation { get; private set; }

        public static void Load()
        {
            lock (Gate)
            {
                string path = FilePath;
                if (!File.Exists(path)) { _records = new List<CustomBlockRecord>(); _v21History = new(); Generation++; return; }
                string json = File.ReadAllText(path);
                List<CustomBlockRecord>? legacy = null;
                CustomBlockCatalog? catalog = null;
                try { catalog = JsonSerializer.Deserialize<CustomBlockCatalog>(json, JsonOptions); } catch (JsonException) { }
                if (catalog?.Blocks == null)
                    legacy = JsonSerializer.Deserialize<List<CustomBlockRecord>>(json, JsonOptions);
                var loaded = catalog?.Blocks ?? legacy ?? throw new InvalidDataException("Custom block catalog is empty or invalid.");
                if (catalog != null && catalog.SchemaVersion is < 1 or > 3)
                    throw new InvalidDataException($"Unsupported custom block catalog schema {catalog.SchemaVersion}.");
                foreach (var record in loaded) NormalizeFaces(record);
                ValidateCatalog(loaded);
                _records = loaded;
                _v21History = catalog?.V21History ?? new List<CustomBlockHistoryEntry>();
                Generation++;
            }
        }

        public static void Create(string id, string displayName, byte[] pixels)
            => Create(new CustomBlockRecord { Id = id, DisplayName = displayName, Pixels = pixels, Faces = Enumerable.Range(0, FaceCount).Select(_ => (byte[])pixels.Clone()).ToArray() });

        public static void Create(CustomBlockRecord record)
        {
            lock (Gate)
            {
                var next = new List<CustomBlockRecord>(_records);
                var copy = CloneRecord(record);
                NormalizeFaces(copy);
                copy.Id = copy.Id.Trim(); copy.DisplayName = copy.DisplayName.Trim();
                next.Add(copy);
                ValidateCatalog(next);
                Persist(next, _v21History);
                _records = next;
                Generation++;
            }
        }

        public static void Update(int index, CustomBlockRecord edited)
        {
            lock (Gate)
            {
                if ((uint)index >= (uint)_records.Count) throw new ArgumentOutOfRangeException(nameof(index));
                var old = _records[index];
                if (!string.Equals(old.Id, edited.Id, StringComparison.Ordinal))
                    throw new InvalidDataException("A custom block's internal ID is permanent and cannot be changed.");
                if (edited.Pixels == null || edited.Pixels.Length != PixelBytes)
                    throw new InvalidDataException("Custom blocks must contain exactly 16x16 RGBA pixels.");
                var next = _records.Select(r => CloneRecord(r)).ToList();
                next[index] = CloneRecord(edited);
                NormalizeFaces(next[index]);
                ValidateCatalog(next);

                // v21 hashed only ID, display name and pixels. Preserve every affected legacy prefix
                // before replacing its old content, mapped to the same immutable ordered-ID prefix.
                var history = _v21History.Select(CloneHistory).ToList();
                for (int count = index + 1; count <= _records.Count; count++)
                {
                    var entry = new CustomBlockHistoryEntry
                    {
                        Count = count,
                        LegacySignature = LegacySignatureForRecords(_records, count),
                        IdentitySignature = IdentitySignatureForRecords(_records, count),
                    };
                    if (!history.Any(h => h.Count == entry.Count && string.Equals(h.LegacySignature, entry.LegacySignature, StringComparison.OrdinalIgnoreCase)))
                        history.Add(entry);
                }
                Persist(next, history);
                _records = next;
                _v21History = history;
                Generation++;
            }
        }

        public static bool V21SignatureMapsToCurrentIdentity(int count, string legacySignature)
        {
            lock (Gate)
            {
                if (count < 0 || count > _records.Count) return false;
                string identity = IdentitySignatureForRecords(_records, count);
                return _v21History.Any(h => h.Count == count
                    && string.Equals(h.LegacySignature, legacySignature, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(h.IdentitySignature, identity, StringComparison.OrdinalIgnoreCase));
            }
        }

        private static CustomBlockRecord CloneRecord(CustomBlockRecord r) => new()
        {
            Id = r.Id, DisplayName = r.DisplayName, Pixels = r.Pixels == null ? Array.Empty<byte>() : (byte[])r.Pixels.Clone(),
            Faces = r.Faces == null ? Array.Empty<byte[]>() : r.Faces.Select(p => p == null ? Array.Empty<byte>() : (byte[])p.Clone()).ToArray(),
            Solid = r.Solid, Opaque = r.Opaque, Gravity = r.Gravity, LightEmission = r.LightEmission,
            Inventory = r.Inventory, Placeable = r.Placeable, Hardness = r.Hardness, MapColorOverride = r.MapColorOverride,
        };

        private static CustomBlockHistoryEntry CloneHistory(CustomBlockHistoryEntry h) => new()
        { Count = h.Count, LegacySignature = h.LegacySignature, IdentitySignature = h.IdentitySignature };

        private static void Persist(List<CustomBlockRecord> records, List<CustomBlockHistoryEntry> history)
        {
            string path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var catalog = new CustomBlockCatalog { SchemaVersion = 3, Blocks = records, V21History = history };
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(catalog, JsonOptions));
                File.Move(temp, path, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        private static void MigrateFileIfMissing(string source, string destination)
        {
            if (File.Exists(destination) || !File.Exists(source)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            try { File.Copy(source, destination, false); } catch (IOException) { if (!File.Exists(destination)) throw; }
        }

        private static void ValidateCatalog(IReadOnlyList<CustomBlockRecord> records)
        {
            if (records.Count > Capacity) throw new InvalidDataException($"Custom block limit reached ({Capacity}) for the currently selected stock pack.");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in records)
            {
                if (record == null || string.IsNullOrWhiteSpace(record.Id)
                    || record.Id.Length > 32 || !char.IsAsciiLetterLower(record.Id[0])
                    || record.Id.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_')))
                    throw new InvalidDataException("Block ID must start with a lowercase letter and contain only lowercase letters, digits, or underscores (max 32 characters).");
                if (string.IsNullOrWhiteSpace(record.DisplayName) || record.DisplayName.Length > 48)
                    throw new InvalidDataException($"Custom block '{record.Id}' needs a display name (max 48 characters).");
                if (record.Pixels == null || record.Pixels.Length != PixelBytes)
                    throw new InvalidDataException($"Custom block '{record.Id}' must contain exactly 16x16 RGBA pixels.");
                if (record.Faces == null || record.Faces.Length != FaceCount || record.Faces.Any(p => p == null || p.Length != PixelBytes))
                    throw new InvalidDataException($"Custom block '{record.Id}' must contain six 16x16 RGBA face textures.");
                if (!float.IsFinite(record.Hardness) || record.Hardness < 0.1f || record.Hardness > 10f)
                    throw new InvalidDataException($"Custom block '{record.Id}' hardness must be between 0.1 and 10.");
                if (record.LightEmission < 0 || record.LightEmission > 15)
                    throw new InvalidDataException($"Custom block '{record.Id}' light emission must be between 0 and 15.");
                if (!ids.Add(record.Id) || BlockRegistry.StockContains(record.Id))
                    throw new InvalidDataException($"Block ID '{record.Id}' is already registered.");
                if (!names.Add(record.DisplayName) || BlockRegistry.StockContainsDisplayName(record.DisplayName))
                    throw new InvalidDataException($"Display name '{record.DisplayName}' is already registered.");
            }
        }

        public static string Signature
        {
            get => SignatureForCount(Records.Count);
        }

        public static string SignatureForCount(int count)
        {
            lock (Gate)
            {
                if (count < 0 || count > _records.Count) throw new ArgumentOutOfRangeException(nameof(count));
                return ContentSignatureForRecords(_records, count);
            }
        }

        // Exact v21 manifest algorithm. Keep this byte-for-byte compatible with historical saves.
        public static string LegacySignatureForCount(int count)
        {
            lock (Gate)
            {
                if (count < 0 || count > _records.Count) throw new ArgumentOutOfRangeException(nameof(count));
                return LegacySignatureForRecords(_records, count);
            }
        }

        public static string IdentitySignatureForCount(int count)
        {
            lock (Gate)
            {
                if (count < 0 || count > _records.Count) throw new ArgumentOutOfRangeException(nameof(count));
                return IdentitySignatureForRecords(_records, count);
            }
        }

        private static string LegacySignatureForRecords(IReadOnlyList<CustomBlockRecord> records, int count)
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(1); writer.Write(count);
                for (int i = 0; i < count; i++)
                {
                    var r = records[i]; writer.Write(r.Id); writer.Write(r.DisplayName);
                    writer.Write(r.Pixels.Length); writer.Write(r.Pixels);
                }
            }
            return Hash(stream.ToArray());
        }

        private static string ContentSignatureForRecords(IReadOnlyList<CustomBlockRecord> records, int count)
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(3); writer.Write(count);
                for (int i = 0; i < count; i++)
                {
                    var r = records[i];
                    writer.Write(r.Id); writer.Write(r.DisplayName); writer.Write(r.Pixels.Length); writer.Write(r.Pixels);
                    writer.Write(FaceCount);
                    for (int face = 0; face < FaceCount; face++)
                    {
                        writer.Write(r.Faces[face].Length);
                        writer.Write(r.Faces[face]);
                    }
                    writer.Write(r.Solid); writer.Write(EffectiveOpaque(r)); writer.Write(r.Gravity);
                    writer.Write(r.LightEmission); writer.Write(r.Inventory); writer.Write(r.Placeable); writer.Write(r.Hardness);
                    writer.Write(r.MapColorOverride.HasValue); if (r.MapColorOverride.HasValue) writer.Write(r.MapColorOverride.Value);
                }
            }
            return Hash(stream.ToArray());
        }

        private static string IdentitySignatureForRecords(IReadOnlyList<CustomBlockRecord> records, int count)
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(1);
                for (int i = 0; i < count; i++) writer.Write(records[i].Id);
            }
            return Hash(stream.ToArray());
        }

        private static string Hash(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(bytes));
        }

        public static bool EffectiveOpaque(CustomBlockRecord record)
        {
            if (record.Opaque.HasValue) return record.Opaque.Value;
            return !HasCutoutPixels(record);
        }

        /// <summary>Custom textures use cutout alpha: values below the renderer's 0.5 threshold
        /// are holes; values at or above it render as opaque pixels.</summary>
        public static bool HasCutoutPixels(CustomBlockRecord record)
        {
            var faces = record.Faces;
            if (faces == null || faces.Length != FaceCount) faces = Enumerable.Repeat(record.Pixels, FaceCount).ToArray();
            foreach (var pixels in faces)
                for (int i = 3; i < pixels.Length; i += 4)
                    if (pixels[i] < 128) return true;
            return false;
        }

        public static byte[] BuildAtlasRgba()
        {
            var atlas = new byte[CustomPageCount * 256 * 256 * 4];
            lock (Gate)
            {
                for (int i = 0; i < _records.Count; i++)
                {
                    for (int face = 0; face < FaceCount; face++)
                    {
                        var (page, localTile) = GetAtlasLocation(i, face);
                        int ox = (localTile % 16) * 16, oy = (localTile / 16) * 16;
                        byte[] pixels = _records[i].Faces[face];
                        for (int y = 0; y < 16; y++)
                            Buffer.BlockCopy(pixels, y * 16 * 4,
                                atlas, ((oy + y) * (256 * CustomPageCount) + (page - 1) * 256 + ox) * 4, 16 * 4);
                    }
                }
            }
            return atlas;
        }

        public static (int Page, int LocalTile) GetAtlasLocation(int recordIndex, int faceIndex)
        {
            if ((uint)recordIndex >= MaxBlocks) throw new ArgumentOutOfRangeException(nameof(recordIndex));
            if ((uint)faceIndex >= FaceCount) throw new ArgumentOutOfRangeException(nameof(faceIndex));
            int tileIndex = checked(recordIndex * FaceCount + faceIndex);
            int page = 1 + tileIndex / TilesPerPage;
            if (page > CustomPageCount) throw new ArgumentOutOfRangeException(nameof(recordIndex), "Custom face atlas capacity exceeded.");
            return (page, tileIndex % TilesPerPage);
        }

        private static void NormalizeFaces(CustomBlockRecord record)
        {
            if (record.Faces == null || record.Faces.Length == 0)
                record.Faces = Enumerable.Range(0, FaceCount).Select(_ => record.Pixels == null ? Array.Empty<byte>() : (byte[])record.Pixels.Clone()).ToArray();
            else if (record.Faces.Length != FaceCount)
                throw new InvalidDataException($"Custom block '{record.Id}' must contain exactly six face textures.");
        }
    }
}
