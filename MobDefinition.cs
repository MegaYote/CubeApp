using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace CubeApp
{
    /// <summary>
    /// Defines a mob type that can be spawned. Mobs are auto-discovered from the MobEntities/ folder.
    /// Each subfolder should contain a .bbmodel (or .glb) file and a matching .png texture.
    /// </summary>
    public sealed class MobDefinition
    {
        public string Id { get; }
        public string DisplayName { get; }
        public string ModelPath { get; }
        public string TexturePath { get; }
        public float Width { get; }
        public float Height { get; }
        public int MaxHealth { get; }
        public float Speed { get; }

        public MobDefinition(string id, string displayName, string modelPath, string texturePath,
            float width = 0.68f, float height = 1.35f, int maxHealth = 10, float speed = 4f)
        {
            Id = id;
            DisplayName = displayName;
            ModelPath = modelPath;
            TexturePath = texturePath;
            Width = width;
            Height = height;
            MaxHealth = maxHealth;
            Speed = speed;
        }
    }

    /// <summary>
    /// Registry of all available mob types. Scans MobEntities/ for models automatically.
    /// </summary>
    public static class MobRegistry
    {
        private static readonly Dictionary<string, MobDefinition> _mobs = new();

        /// <summary>
        /// Call once at startup to discover mobs in the MobEntities/ folder.
        /// </summary>
        public static bool IsDiscovered { get; private set; }

        public static void DiscoverMobs(string baseDirectory)
        {
            _mobs.Clear();
            IsDiscovered = false;

            string entitiesDir = Path.Combine(baseDirectory, "MobEntities");
            if (!Directory.Exists(entitiesDir))
            {
                Console.WriteLine($"MobEntities directory not found: {entitiesDir}");
                return;
            }

            foreach (var subDir in Directory.GetDirectories(entitiesDir))
            {
                string dirName = Path.GetFileName(subDir);
                
                // Find a .bbmodel or .glb file. Prefer the folder-named file, but fall back to
                // any model in the folder (e.g. CoyoteMob/ containing coyote.glb) so assets aren't
                // silently skipped just because of a case/name mismatch.
                string modelPath = Path.Combine(subDir, $"{dirName.ToLowerInvariant()}.bbmodel");
                if (!File.Exists(modelPath))
                    modelPath = Path.Combine(subDir, $"{dirName.ToLowerInvariant()}.glb");
                if (!File.Exists(modelPath))
                {
                    string[] candidates = Directory.GetFiles(subDir, "*.bbmodel", SearchOption.TopDirectoryOnly);
                    if (candidates.Length == 0)
                        candidates = Directory.GetFiles(subDir, "*.glb", SearchOption.TopDirectoryOnly);
                    if (candidates.Length > 0)
                        modelPath = candidates[0];
                }
                if (!File.Exists(modelPath))
                    continue; // No model file found

                // Find a .png texture (folder-named first, then any texture in the folder)
                string texturePath = Path.Combine(subDir, $"{dirName}.png");
                if (!File.Exists(texturePath))
                {
                    string[] textures = Directory.GetFiles(subDir, "*.png", SearchOption.TopDirectoryOnly);
                    if (textures.Length > 0)
                        texturePath = textures[0];
                }
                if (!File.Exists(texturePath))
                    continue; // No texture found

                // Try to load JSON config
                string configPath = Path.Combine(subDir, $"{dirName}.json");
                var def = LoadFromConfig(configPath, dirName, modelPath, texturePath);
                
                _mobs[def.Id] = def;
                Console.WriteLine($"Discovered mob: {def.DisplayName} ({def.ModelPath})");
            }
            IsDiscovered = _mobs.Count > 0;
        }

        public static void Register(MobDefinition mob)
        {
            _mobs[mob.Id] = mob;
        }

        public static MobDefinition? Get(string id)
        {
            return _mobs.TryGetValue(id, out var mob) ? mob : null;
        }

        public static IReadOnlyCollection<MobDefinition> All => _mobs.Values;

        private static MobDefinition LoadFromConfig(string configPath, string dirName, string modelPath, string texturePath)
        {
            // Defaults
            string id = dirName.ToLowerInvariant();
            string displayName = dirName;
            float width = 0.68f;
            float height = 1.35f;
            int maxHealth = 10;
            float speed = 4f;

            // Override from JSON if present
            if (File.Exists(configPath))
            {
                try
                {
                    var json = File.ReadAllText(configPath);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("id", out var idProp))
                        id = idProp.GetString()?.ToLowerInvariant() ?? id;
                    if (root.TryGetProperty("displayName", out var nameProp))
                        displayName = nameProp.GetString() ?? dirName;
                    if (root.TryGetProperty("width", out var widthProp))
                        width = widthProp.GetSingle();
                    if (root.TryGetProperty("height", out var heightProp))
                        height = heightProp.GetSingle();
                    if (root.TryGetProperty("maxHealth", out var healthProp))
                        maxHealth = healthProp.GetInt32();
                    if (root.TryGetProperty("speed", out var speedProp))
                        speed = speedProp.GetSingle();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Failed to load config from {configPath}: {ex.Message}");
                }
            }

            return new MobDefinition(id, displayName, modelPath, texturePath, width, height, maxHealth, speed);
        }
    }
}
