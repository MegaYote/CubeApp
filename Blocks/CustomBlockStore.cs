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
        /// <summary>Immutable, automatically generated identity. Never shown to the player.</summary>
        public string Uid { get; set; } = "";
        /// <summary>Canonical hash of the definition content (faces + gameplay attributes).</summary>
        public string ContentHash { get; set; } = "";
        /// <summary>Hidden legacy identity retained for v21-v23 signatures and edit history.</summary>
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
        /// <summary>Previous UIDs that were superseded while importing a conflicting definition.</summary>
        public List<string> Aliases { get; set; } = new();
    }

    public sealed class CustomBlockHistoryEntry
    {
        public int Count { get; set; }
        public string LegacySignature { get; set; } = "";
        public string IdentitySignature { get; set; } = "";
    }

    public sealed class CustomBlockCatalog
    {
        public int SchemaVersion { get; set; } = CustomBlockStore.CatalogSchemaVersion;
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
        /// <summary>Schema written to custom_blocks.json. v4 adds Uid/ContentHash/Aliases.</summary>
        public const int CatalogSchemaVersion = 4;
        /// <summary>Custom-content manifest version written inside a v24 world save.</summary>
        public const int SaveManifestVersion = 4;
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
                string? processDirectory = Path.GetDirectoryName(Environment.ProcessPath);
                if (!string.IsNullOrWhiteSpace(processDirectory))
                    MigrateFileIfMissing(Path.Combine(processDirectory, "CustomContent", "custom_blocks.json"), path);
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
                int schemaVersion = catalog?.SchemaVersion ?? 1;
                if (schemaVersion is < 1 or > CatalogSchemaVersion)
                    throw new InvalidDataException($"Unsupported custom block catalog schema {schemaVersion}.");

                bool changed = catalog == null || schemaVersion < CatalogSchemaVersion;
                var usedUids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var record in loaded)
                {
                    if (record == null) throw new InvalidDataException("Custom block catalog contains a null entry.");
                    NormalizeFaces(record);
                    if (record.Pixels == null || record.Pixels.Length != PixelBytes)
                    {
                        record.Pixels = (byte[])record.Faces[0].Clone();
                        changed = true;
                    }

                    string id = (record.Id ?? "").Trim();
                    bool validId = id.Length is > 0 and <= 32 && char.IsAsciiLetterLower(id[0])
                        && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_');
                    if (!validId || usedIds.Contains(id) || BlockRegistry.StockContains(id))
                    {
                        string previousId = id;
                        record.Id = UniqueImportedId(previousId, usedIds);
                        if (!string.IsNullOrWhiteSpace(previousId)) AddAlias(record, previousId);
                        changed = true;
                    }
                    else
                    {
                        usedIds.Add(id);
                        if (!string.Equals(record.Id, id, StringComparison.Ordinal))
                        {
                            record.Id = id;
                            changed = true;
                        }
                    }

                    string computedHash = ComputeContentHash(record);
                    if (!string.Equals(record.ContentHash, computedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        record.ContentHash = computedHash;
                        changed = true;
                    }

                    string uid = (record.Uid ?? "").Trim();
                    if (IsValidUid(uid) && usedUids.Add(uid))
                    {
                        if (!string.Equals(record.Uid, uid, StringComparison.Ordinal))
                        {
                            record.Uid = uid;
                            changed = true;
                        }
                    }
                    else
                    {
                        string previousUid = uid;
                        // Legacy catalogs had no UID. Derive it from the stable legacy ID and
                        // canonical content so the same pack file migrates to the same UIDs on
                        // every machine; otherwise identical blocks would duplicate (or collapse)
                        // when a world moves between installs.
                        record.Uid = GenerateDeterministicUid(record.Id + "|" + computedHash, usedUids);
                        if (IsValidUid(previousUid)) AddAlias(record, previousUid);
                        changed = true;
                    }
                    if (NormalizeAliases(record)) changed = true;
                }

                ValidateCatalog(loaded);
                _records = loaded;
                _v21History = catalog?.V21History ?? new List<CustomBlockHistoryEntry>();
                if (changed) Persist(loaded, _v21History);
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
                if (copy.Pixels == null || copy.Pixels.Length == 0)
                    copy.Pixels = (byte[])copy.Faces[0].Clone();
                copy.Id = (copy.Id ?? "").Trim();
                copy.DisplayName = (copy.DisplayName ?? "").Trim();

                var usedUids = new HashSet<string>(next.Select(r => r.Uid), StringComparer.OrdinalIgnoreCase);
                copy.Uid = GenerateUniqueUid(usedUids);
                var usedIds = CollectUsedIds(next);
                if (string.IsNullOrWhiteSpace(copy.Id))
                    copy.Id = GenerateUniqueLegacyId(copy.Uid, usedIds);
                NormalizeAliases(copy);
                copy.ContentHash = ComputeContentHash(copy);

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
                if (!string.IsNullOrEmpty(edited.Uid) && !string.Equals(old.Uid, edited.Uid, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("A custom block's UID is permanent and cannot be changed.");
                if (!string.IsNullOrEmpty(edited.Id) && !string.Equals(old.Id, edited.Id, StringComparison.Ordinal))
                    throw new InvalidDataException("A custom block's internal ID is permanent and cannot be changed.");
                var next = _records.Select(r => CloneRecord(r)).ToList();
                var copy = CloneRecord(edited);
                copy.Uid = old.Uid;
                copy.Id = old.Id;
                NormalizeFaces(copy);
                if (copy.Pixels == null || copy.Pixels.Length == 0)
                    copy.Pixels = (byte[])old.Pixels.Clone();
                if (copy.Pixels == null || copy.Pixels.Length != PixelBytes)
                    throw new InvalidDataException("Custom blocks must contain exactly 16x16 RGBA pixels.");
                copy.Aliases = MergeAliases(old.Aliases, copy.Aliases);
                NormalizeAliases(copy);
                copy.ContentHash = ComputeContentHash(copy);
                next[index] = copy;
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

        /// <summary>
        /// Imports every saved definition into the installed catalog (the single source of truth),
        /// resolving conflicts by UID first and canonical content hash second. The returned map is
        /// saved UID -> installed UID. Newly cloned records receive a fresh UID and keep the old UID
        /// as an alias. Persists at most once and never edits or renames an existing local block.
        /// </summary>
        public static Dictionary<string, string> ImportWorldBlocks(IReadOnlyList<CustomBlockRecord> savedBlocks)
        {
            lock (Gate)
            {
                var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (savedBlocks == null || savedBlocks.Count == 0) return mapping;

                var next = _records.Select(r => CloneRecord(r)).ToList();
                var byUid = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var byHash = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var usedUids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var usedIds = CollectUsedIds(next);
                for (int i = 0; i < next.Count; i++) IndexRecord(next[i], i, byUid, byHash, usedUids);

                // UIDs that appear anywhere in the incoming file are reserved so that a freshly
                // minted disambiguating UID can never collide with another saved definition.
                var incomingUids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var saved in savedBlocks)
                {
                    string savedUid = (saved?.Uid ?? "").Trim();
                    if (IsValidUid(savedUid)) incomingUids.Add(savedUid);
                }
                foreach (var uid in usedUids) incomingUids.Add(uid);

                var working = new List<CustomBlockRecord>(savedBlocks.Count);
                var seenUidHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < savedBlocks.Count; i++)
                {
                    var saved = savedBlocks[i] ?? throw new InvalidDataException("World save contains a null custom block definition.");
                    var incoming = CloneRecord(saved);
                    NormalizeFaces(incoming);
                    if (incoming.Pixels == null || incoming.Pixels.Length == 0)
                        incoming.Pixels = (byte[])incoming.Faces[0].Clone();
                    incoming.Id = (incoming.Id ?? "").Trim();
                    incoming.DisplayName = (incoming.DisplayName ?? "").Trim();

                    string computedHash = ComputeContentHash(incoming);
                    if (!string.IsNullOrWhiteSpace(incoming.ContentHash)
                        && !string.Equals(incoming.ContentHash, computedHash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Saved custom block '{incoming.DisplayName}' has a content hash that does not match its definition.");
                    incoming.ContentHash = computedHash;
                    savedBlocks[i].ContentHash = computedHash;
                    NormalizeAliases(incoming);

                    string uid = (incoming.Uid ?? "").Trim();
                    if (!IsValidUid(uid))
                    {
                        string previousUid = uid;
                        uid = GenerateUniqueUid(incomingUids);
                        incoming.Uid = uid;
                        if (IsValidUid(previousUid)) AddAlias(incoming, previousUid);
                        savedBlocks[i].Uid = uid;
                    }
                    else if (seenUidHash.TryGetValue(uid, out string? seenHash)
                        && !string.Equals(seenHash, computedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        // Duplicate UID with different content inside the same file: the later
                        // definition gets a fresh UID so both can be represented.
                        string previousUid = uid;
                        incoming.Uid = GenerateUniqueUid(incomingUids);
                        AddAlias(incoming, previousUid);
                        savedBlocks[i].Uid = incoming.Uid;
                    }

                    if (IsValidUid(uid) && !seenUidHash.ContainsKey(uid))
                        seenUidHash[uid] = computedHash;
                    string normalizedUid = incoming.Uid ?? "";
                    if (!seenUidHash.ContainsKey(normalizedUid))
                        seenUidHash[normalizedUid] = computedHash;
                    working.Add(incoming);
                }

                bool changed = false;
                foreach (var incoming in working)
                {
                    string installedUid = ImportOne(incoming, next, byUid, byHash, usedUids, usedIds, ref changed);
                    mapping[incoming.Uid] = installedUid;
                }

                if (changed)
                {
                    ValidateCatalog(next);
                    Persist(next, _v21History);
                    _records = next;
                    Generation++;
                }
                return mapping;
            }
        }

        private static string ImportOne(
            CustomBlockRecord incoming,
            List<CustomBlockRecord> next,
            Dictionary<string, int> byUid,
            Dictionary<string, int> byHash,
            HashSet<string> usedUids,
            HashSet<string> usedIds,
            ref bool changed)
        {
            if (byUid.TryGetValue(incoming.Uid, out int uidIndex))
            {
                if (string.Equals(next[uidIndex].ContentHash, incoming.ContentHash, StringComparison.OrdinalIgnoreCase))
                    return next[uidIndex].Uid;
                if (byHash.TryGetValue(incoming.ContentHash, out int hashIndex))
                    return next[hashIndex].Uid;

                return AppendImported(incoming, next, byUid, byHash, usedUids, usedIds, ref changed,
                    forceNewUid: true);
            }

            if (byHash.TryGetValue(incoming.ContentHash, out int existingIndex))
                return next[existingIndex].Uid;

            return AppendImported(incoming, next, byUid, byHash, usedUids, usedIds, ref changed,
                forceNewUid: !IsValidUid(incoming.Uid) || usedUids.Contains(incoming.Uid));
        }

        private static string AppendImported(
            CustomBlockRecord incoming,
            List<CustomBlockRecord> next,
            Dictionary<string, int> byUid,
            Dictionary<string, int> byHash,
            HashSet<string> usedUids,
            HashSet<string> usedIds,
            ref bool changed,
            bool forceNewUid)
        {
            if (next.Count >= Capacity)
                throw new InvalidDataException($"Importing this world's custom blocks would exceed the {Capacity}-block custom limit for the installed stock pack.");

            var imported = CloneRecord(incoming);
            string previousUid = imported.Uid;
            if (forceNewUid || !IsValidUid(imported.Uid) || usedUids.Contains(imported.Uid))
            {
                imported.Uid = GenerateUniqueUid(usedUids);
                if (IsValidUid(previousUid)) AddAlias(imported, previousUid);
            }
            else
            {
                usedUids.Add(imported.Uid);
            }
            imported.Id = UniqueImportedId(imported.Id, usedIds);
            imported.Pixels = (byte[])imported.Faces[0].Clone();
            NormalizeAliases(imported);
            imported.ContentHash = ComputeContentHash(imported);

            int index = next.Count;
            next.Add(imported);
            byUid[imported.Uid] = index;
            if (!byHash.ContainsKey(imported.ContentHash)) byHash[imported.ContentHash] = index;
            changed = true;
            return imported.Uid;
        }

        private static void IndexRecord(
            CustomBlockRecord record,
            int index,
            Dictionary<string, int> byUid,
            Dictionary<string, int> byHash,
            HashSet<string> usedUids)
        {
            usedUids.Add(record.Uid);
            byUid[record.Uid] = index;
            if (!byHash.ContainsKey(record.ContentHash)) byHash[record.ContentHash] = index;
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

        public static IReadOnlyList<CustomBlockRecord> SnapshotRecords()
        {
            lock (Gate) return _records.Select(CloneRecord).ToArray();
        }

        public static CustomBlockRecord CloneRecord(CustomBlockRecord r) => new()
        {
            Uid = r.Uid, ContentHash = r.ContentHash, Id = r.Id, DisplayName = r.DisplayName,
            Pixels = r.Pixels == null ? Array.Empty<byte>() : (byte[])r.Pixels.Clone(),
            Faces = r.Faces == null ? Array.Empty<byte[]>() : r.Faces.Select(p => p == null ? Array.Empty<byte>() : (byte[])p.Clone()).ToArray(),
            Solid = r.Solid, Opaque = r.Opaque, Gravity = r.Gravity, LightEmission = r.LightEmission,
            Inventory = r.Inventory, Placeable = r.Placeable, Hardness = r.Hardness, MapColorOverride = r.MapColorOverride,
            Aliases = r.Aliases == null ? new List<string>() : new List<string>(r.Aliases),
        };

        private static CustomBlockHistoryEntry CloneHistory(CustomBlockHistoryEntry h) => new()
        { Count = h.Count, LegacySignature = h.LegacySignature, IdentitySignature = h.IdentitySignature };

        private static void Persist(List<CustomBlockRecord> records, List<CustomBlockHistoryEntry> history)
        {
            string path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var catalog = new CustomBlockCatalog { SchemaVersion = CatalogSchemaVersion, Blocks = records, V21History = history };
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
            var uids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in records)
            {
                if (record == null || !IsValidUid(record.Uid))
                    throw new InvalidDataException("Every custom block needs an immutable UID.");
                if (string.IsNullOrWhiteSpace(record.ContentHash))
                    throw new InvalidDataException($"Custom block '{record.DisplayName}' has no content hash.");
                if (string.IsNullOrWhiteSpace(record.Id)
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
                if (!uids.Add(record.Uid))
                    throw new InvalidDataException($"Custom block UID '{record.Uid}' is already registered.");
                if (!ids.Add(record.Id) || BlockRegistry.StockContains(record.Id))
                    throw new InvalidDataException($"Block ID '{record.Id}' is already registered.");
                // Display names intentionally do not need to be unique: imported packs may use
                // the same player-facing name while keeping distinct immutable UIDs.
                if (record.Aliases == null) record.Aliases = new List<string>();
                var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var alias in record.Aliases)
                {
                    if (string.IsNullOrWhiteSpace(alias) || alias.Length > 64 || !aliases.Add(alias))
                        throw new InvalidDataException($"Custom block '{record.Id}' has an invalid alias.");
                }
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

        /// <summary>Signature over the ordered palette identity (UID + canonical content hash).</summary>
        public static string PaletteSignature => PaletteSignatureForCount(Records.Count);

        public static string PaletteSignatureForCount(int count)
        {
            lock (Gate)
            {
                if (count < 0 || count > _records.Count) throw new ArgumentOutOfRangeException(nameof(count));
                return PaletteSignatureForRecords(_records, count);
            }
        }

        public static string PaletteSignatureForRecords(IReadOnlyList<CustomBlockRecord> records, int count)
        {
            if (records == null) throw new ArgumentNullException(nameof(records));
            if (count < 0 || count > records.Count) throw new ArgumentOutOfRangeException(nameof(count));
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(1); writer.Write(count);
                for (int i = 0; i < count; i++)
                {
                    writer.Write(records[i].Uid ?? "");
                    writer.Write(records[i].ContentHash ?? "");
                }
            }
            return Hash(stream.ToArray());
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

        /// <summary>Canonical definition hash: faces + gameplay attributes only. Display name,
        /// legacy ID, UID, aliases and the legacy Pixels copy are deliberately excluded.</summary>
        public static string ComputeContentHash(CustomBlockRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (record.Faces == null || record.Faces.Length != FaceCount || record.Faces.Any(f => f == null))
                throw new InvalidDataException("Cannot hash a custom block without six face textures.");
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(1); writer.Write(FaceCount);
                for (int face = 0; face < FaceCount; face++)
                {
                    writer.Write(record.Faces[face].Length);
                    writer.Write(record.Faces[face]);
                }
                writer.Write(record.Solid);
                writer.Write(EffectiveOpaque(record));
                writer.Write(record.Gravity);
                writer.Write(record.LightEmission);
                writer.Write(record.Inventory);
                writer.Write(record.Placeable);
                writer.Write(record.Hardness);
                writer.Write(record.MapColorOverride.HasValue);
                if (record.MapColorOverride.HasValue) writer.Write(record.MapColorOverride.Value);
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
            {
                if (pixels == null) continue;
                for (int i = 3; i < pixels.Length; i += 4)
                    if (pixels[i] < 128) return true;
            }
            return false;
        }

        public static bool IsValidUid(string? uid)
            => !string.IsNullOrWhiteSpace(uid) && Guid.TryParse(uid, out _);

        private static string GenerateUniqueUid(HashSet<string> usedUids)
        {
            while (true)
            {
                string uid = Guid.NewGuid().ToString("N");
                if (usedUids.Add(uid)) return uid;
            }
        }

        /// <summary>Stable UUID derived from a legacy ID + content hash, so migrating the same
        /// pre-v4 catalog on two machines produces the same identities.</summary>
        private static string GenerateDeterministicUid(string seed, HashSet<string> usedUids)
        {
            using var sha = SHA256.Create();
            for (int attempt = 0; attempt < 1000; attempt++)
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes("cubuild-custom-v4:" + seed + ":" + attempt));
                var bytes = new byte[16];
                Array.Copy(digest, bytes, bytes.Length);
                string uid = new Guid(bytes).ToString("N");
                if (usedUids.Add(uid)) return uid;
            }
            throw new InvalidDataException("Could not generate a deterministic custom block UID.");
        }

        private static string GenerateUniqueLegacyId(string seed, HashSet<string> usedIds)
        {
            string cleaned = new((seed ?? "").ToLowerInvariant()
                .Where(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_').ToArray());
            string stem = cleaned.Length >= 6 ? cleaned[..Math.Min(12, cleaned.Length)] : Guid.NewGuid().ToString("N")[..12];
            for (int attempt = 0; attempt < 1000; attempt++)
            {
                string suffix = attempt == 0 ? stem : Guid.NewGuid().ToString("N")[..12];
                string candidate = "custom_" + suffix;
                if (candidate.Length > 32) candidate = candidate[..32];
                if (!usedIds.Contains(candidate) && !BlockRegistry.StockContains(candidate))
                {
                    usedIds.Add(candidate);
                    return candidate;
                }
            }
            throw new InvalidDataException("Could not generate a unique legacy block ID.");
        }

        private static string UniqueImportedId(string? requested, HashSet<string> usedIds)
        {
            string id = (requested ?? "").Trim();
            bool valid = id.Length is > 0 and <= 32 && char.IsAsciiLetterLower(id[0])
                && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_');
            if (valid && !usedIds.Contains(id) && !BlockRegistry.StockContains(id))
            {
                usedIds.Add(id);
                return id;
            }
            return GenerateUniqueLegacyId(id, usedIds);
        }

        private static HashSet<string> CollectUsedIds(IEnumerable<CustomBlockRecord> records)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in records) used.Add(record.Id);
            return used;
        }

        private static void AddAlias(CustomBlockRecord record, string alias)
        {
            if (string.IsNullOrWhiteSpace(alias) || alias.Length > 64) return;
            record.Aliases ??= new List<string>();
            if (!record.Aliases.Any(a => string.Equals(a, alias, StringComparison.OrdinalIgnoreCase)))
                record.Aliases.Add(alias);
        }

        private static List<string> MergeAliases(IEnumerable<string>? first, IEnumerable<string>? second)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var alias in (first ?? Enumerable.Empty<string>()).Concat(second ?? Enumerable.Empty<string>()))
                if (!string.IsNullOrWhiteSpace(alias) && alias.Length <= 64 && seen.Add(alias))
                    result.Add(alias);
            return result;
        }

        private static bool NormalizeAliases(CustomBlockRecord record)
        {
            var normalized = MergeAliases(record.Aliases, null);
            var existing = record.Aliases;
            bool changed = existing == null || existing.Count != normalized.Count;
            if (!changed)
                for (int i = 0; i < normalized.Count; i++)
                    if (!string.Equals(existing![i], normalized[i], StringComparison.Ordinal)) { changed = true; break; }
            if (changed) record.Aliases = normalized;
            return changed;
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
