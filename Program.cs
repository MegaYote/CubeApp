using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using CubeApp.Renderer;
using CubeApp.World;
using Veldrid;
using Veldrid.Sdl2;
using Veldrid.StartupUtilities;
using static CubeApp.ChunkManager;
using CubeApp;

namespace CubeApp
{
    /// <summary>
    /// Presentation layer: window, GPU, input, HUD and screen state. All simulation lives in
    /// <see cref="GameWorld"/> so the same world can run headless (dedicated server / network
    /// host). Program only orchestrates: it feeds input into the world and pushes world state to
    /// the renderer.
    /// </summary>
    public sealed partial class Program : IDisposable
    {
        private IRenderer? gpuRenderer;
        private Sdl2Window? window;
        private GraphicsDevice? graphicsDevice;
        private Point3D? _lastMeshPosition;
        private bool _forceChunkStream = true;
        private int _lastSkylightSubtracted = -1;
        private readonly InputProcessor input = new();
        private bool mouseLook;
        private volatile bool needsMeshUpdate = true;
        private string baseTitle = "Chunk Mesh Example";
        private bool showFps;
        private int frameCount;
        // Reusable per-frame entity render list (avoids one List allocation every frame).
        private readonly List<MobRenderData> _entityRenderScratch = new();
        private readonly List<CubeApp.Renderer.ZombieMiningTarget> _zombieMiningScratch = new();

        // ---- world loading screen ----
        // Staged loader state: pre-generates + meshes a chunk radius around spawn so the player
        // drops into a fully ready world (no pop-in), showing a phase progress bar. The loader
        // runs in the main loop while screen == Loading, then flips to Playing.
        private int _loadTargetRadius;          // chunk radius to fully prepare
        private int _loadGroundRequested;       // ground chunks requested so far
        private int _loadGroundTotal;           // total ground chunks in the target radius
        private readonly HashSet<ChunkCoordinates> _loadTargetSet = new(); // exact chunk set to prepare
        private readonly HashSet<ChunkCoordinates> _loadMeshedSet = new(); // meshed+uploaded chunks
        private int _loadLastMeshedCount;       // for the meshing-stall detector
        private int _loadMeshedCount;
        private int _loadPhase;                 // 0 preparing, 1 generating, 2 meshing, 3 finishing
        private float _loadPhaseStart;
        private bool _loadSkipSpawn;            // true when loading a save (position already restored)

        // Deferred world construction: when the player clicks Create/Load, the screen flips to
        // Loading immediately so the player sees feedback, and the heavy world construction is
        // deferred to UpdateLoading (which runs later in the same frame). This eliminates the
        // dead-frame freeze between the click and the first loading-screen paint.
        private bool _pendingWorldFromSave;
        private WorldSave? _pendingWorldSave;
        private int _pendingSeed;
        private string _pendingName = "";
        private GameMode _pendingMode;
        private bool _loadingScreenShown;       // gate: first frame shows "Loading...", second does work

        private float lastFps;
        private readonly Stopwatch fpsStopwatch = new();
        private float lastUpdateMs;
        private float lastMeshMs;
        private float lastUploadMs;
        private float lastRenderMs;
        private readonly Stopwatch stageStopwatch = new();
        private float MouseSensitivity = 0.5f;
        private float ResolutionScale = 1f;
        private bool PixelatedUpscale;
        private const double MaxFrameDeltaSeconds = 0.25;
        private static readonly int[] RenderDistances = { 16, 8, 4, 2 };
        private static readonly string[] RenderDistanceNames = { "Far", "Normal", "Short", "Tiny" };
        private int renderDistanceIndex = 0; // Far by default
        private int ChunkRenderRadius => RenderDistances[renderDistanceIndex];
        private string RenderDistanceName => RenderDistanceNames[renderDistanceIndex];
        private int _ignoreInteractFrames; // skips break/place right after a menu click
        // ---- Survival mining (Cubuild C++ port) -------------------------------------
        // Hold left click to mine: progress accumulates toward breakTime = BASE_BREAK_TIME *
        // hardness, particles pop every 20%, and the block breaks at progress >= 1.
        private const float BaseBreakTime = 1.5f;    // seconds for hardness 1.0 (C++ BASE_BREAK_TIME)
        private const float BreakParticleInterval = 0.2f; // spawn shards every 20% progress
        private const float CreativeBreakInterval = 0.2f; // creative: one instant break per 0.2s (~5/s)
        private (int x, int y, int z)? _miningTarget;
        private float _miningProgress;
        private int _miningBlockId;
        private float _miningBlockHardness;
        private float _creativeBreakCooldown;
        private float _handPokeTimer;
        // Camera ray direction captured once when mining starts (the line from the camera THROUGH
        // the mined block to the block behind it). The shrink cube slides along this direction so
        // it clamps toward the block behind the crosshair, not the hit face's normal.
        private Point3D _miningSlideDir;
        private GameScreen screen = GameScreen.Title;
        private bool _settingsWasOpen;
        private readonly MenuState menu = new();
        private bool inventoryOpen;
        /// <summary>Workbench crafting menu open (right-click a workbench block while playing).</summary>
        private bool craftingOpen;
        private bool handEditorOpen;
        private bool biomeMenuOpen;

        // ---- multiplayer session ----
        private Net.NetHost? _netHost;
        private Net.NetClient? _netClient;
        private string _joinError = "";
        private float _inputSendTimer;
        private bool _netConnected;
        private int _activeHostPort = Net.NetHost.DefaultPort;
        private string _playerName = "Player" + Environment.ProcessId % 1000;

        /// <summary>The simulation world (null on the title screen).</summary>
        public GameWorld World { get; private set; }

        /// <summary>Zero-lag background-thread audio (grass break, cave ambience).</summary>
        public SoundEngine Sound { get; private set; }

        public Program()
        {
            BlockRegistry.LoadDefault();
            ItemRegistry.LoadDefault(); // seeds blocks as items + loads items.json
            RecipeRegistry.LoadDefault();
            BiomeRegistry.LoadDefault();
            MobRegistry.DiscoverMobs(AppDomain.CurrentDomain.BaseDirectory);
            RefreshSavedWorlds();
            Sound = new SoundEngine();
            Sound.RegisterAllEmbedded();
        }

        // ------------------------------------------------------------------
        // world lifecycle
        // ------------------------------------------------------------------

        public void Dispose()
        {
            StopNetworking();
            try { World?.Dispose(); } catch { }
            try { gpuRenderer?.Dispose(); } catch { }
            try { graphicsDevice?.Dispose(); } catch { }
            try { window?.Close(); } catch { }
        }

        private static void PreloadNativeLibraries()
        {
            string[] names = { "SDL2", "cimgui", "veldrid-spirv", "libveldrid-spirv" };
            var asm = Assembly.GetExecutingAssembly();
            foreach (var name in names)
            {
                try { System.Runtime.InteropServices.NativeLibrary.TryLoad(name, asm, null, out _); } catch { }
            }
        }

        [STAThread]
        static void Main()
        {
            try
            {
                PreloadNativeLibraries();
                using var app = new Program();
                app.Run();
            }
            catch (Exception ex)
            {
                try
                {
                    string logPath = System.IO.Path.Combine(AppContext.BaseDirectory, "cubeapp-crash.log");
                    System.IO.File.WriteAllText(logPath, DateTime.Now + Environment.NewLine + ex);
                }
                catch { }
                throw;
            }
        }
    }
}