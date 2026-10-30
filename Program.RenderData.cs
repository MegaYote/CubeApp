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
        // Reused each frame so feeding the coupling beams into HudState never allocates.
        private readonly List<Cubuild.Renderer.LinkBeam> _linkBeams = new();
        private readonly List<FallingBlockData> _fallingBlockRenderScratch = new();
        private readonly List<string> _steveAIDebugLines = new(16);

        private IReadOnlyList<FallingBlockData> BuildFallingBlockRenderData()
        {
            _fallingBlockRenderScratch.Clear();
            if (World == null) return _fallingBlockRenderScratch;
            _fallingBlockRenderScratch.AddRange(World.BlockTicks.Gravity.FallingBlocks);
            int bombId = BlockRegistry.GetId("bomb");
            if (_netClient != null && _netConnected)
            {
                // Client rendering is driven solely by host snapshots. No local fuse or projectile
                // simulation runs on a multiplayer client.
                foreach (var projectile in _netClient.GetInterpolatedBombProjectiles())
                {
                    _fallingBlockRenderScratch.Add(new FallingBlockData(bombId,
                        projectile.X - 0.5, projectile.Y - 0.5, projectile.Z - 0.5));
                }
            }
            else
            {
                _fallingBlockRenderScratch.AddRange(World.CaptureBombProjectileRenderData());
            }
            return _fallingBlockRenderScratch;
        }

        private void AddRemotePlayersToRender(List<MobRenderData> list)
        {
            if (World == null) return;
            // Host side: render each connected client's simulated state.
            if (_netHost != null)
            {
                foreach (var p in World.RemotePlayers)
                {
                    list.Add(new MobRenderData(
                        "player",
                        new Point3D(p.Position.X, p.Position.Y - GameWorld.EyeHeight, p.Position.Z),
                        p.Yaw * (float)Math.PI / 180f,
                        0f, p.WalkPhase, p.WalkAmount, 0f, 0f, 0f,
                        (float)p.Velocity.Y, p.Grounded, p.Health <= 0,
                        p.Health <= 0 ? Math.Clamp(p.DeathTimer / 0.5f, 0f, 1f) : 0f,
                        p.DeathRollDir, p.Health <= 0 ? 0f : p.HurtTimer));
                }
                return;
            }
            // Client side: render everyone in the snapshot except ourselves.
            if (_netClient == null || !_netConnected) return;
            var snap = _netClient.LatestSnapshot;
            if (snap == null) return;
            foreach (var p in snap.Players)
            {
                if (p.Id == _netClient.ClientId) continue; // that's us
                list.Add(new MobRenderData(
                    "player",
                    new Point3D(p.X, p.Y - GameWorld.EyeHeight, p.Z),
                    p.Yaw * (float)Math.PI / 180f,
                    0f, p.WalkPhase, p.WalkAmount, 0f, 0f, 0f,
                    p.VelY, p.Grounded, p.Health <= 0,
                    p.Health <= 0 ? Math.Clamp(p.DeathTimer / 0.5f, 0f, 1f) : 0f,
                    p.DeathRollDir, p.Health <= 0 ? 0f : p.HurtTimer));
            }
        }

        // Sends the client's input + look to the host (~20Hz), and pushes the host's own local
        // player state into the broadcast. Called every frame while playing.
        private void UpdateNetworking(TickInputState tickInput, float deltaSeconds)
        {
            if (World == null) return;
            if (_netHost != null)
            {
                _netHost.DrainIncomingEdits();
                _netHost.SetLocalPlayerState(World.LocalPlayer);
            }
            if (_netClient != null && _netConnected)
            {
                _netClient.DrainIncomingEdits(World);
                var authoritative = _netClient.LatestSnapshot;
                if (authoritative != null)
                {
                    World.Mode = authoritative.CreativeWorld ? GameMode.Creative : GameMode.Survival;
                    foreach (var player in authoritative.Players)
                    {
                        if (player.Id != _netClient.ClientId) continue;
                        World.LocalPlayer.Health = player.Health;
                        World.LocalPlayer.DeathCause = player.DeathCause;
                        World.LocalPlayer.DeathTimer = player.DeathTimer;
                        World.LocalPlayer.DeathRollDir = player.DeathRollDir;
                        World.LocalPlayer.HurtTimer = player.HurtTimer;
                        break;
                    }
                }
                _inputSendTimer += deltaSeconds;
                if (_inputSendTimer >= 0.05f)
                {
                    _inputSendTimer = 0f;
                    _netClient.SendInput(tickInput, World.PlayerYaw, World.PlayerPitch);
                }
            }
        }

        // ------------------------------------------------------------------
        // HUD / camera helpers (read world state; no sim logic)
        // ------------------------------------------------------------------

        private HudState BuildHud()
        {
            _steveAIDebugLines.Clear();
            string netStatus = BuildNetStatus();
            string mpError = BuildMultiplayerError();
            if (World == null)
            {
                return new HudState
                {
                    ShowDebug = showFps, NoclipMode = false, CreativeFlightMode = false, Menu = menu, Fps = lastFps,
                    WorldName = string.Empty,
                    UpdateMs = lastUpdateMs, MeshMs = lastMeshMs, UploadMs = lastUploadMs, RenderMs = lastRenderMs,
                    SelectedBlockText = "Selected: -",
                    SelectedSlot = 0, WorldSeed = 0,
                    Hotbar = Array.Empty<int>(),
                    PlayerX = 0, PlayerY = 0, PlayerZ = 0,
                    RenderDistance = ChunkRenderRadius,
                    NetStatus = netStatus,
                    MultiplayerError = mpError,
                    SteveAIDiagnostics = _steveAIDebugLines,
                    SaveToast = _saveToastTimer,
                    StatusToastTimer = _statusToastTimer,
                    StatusToast = _statusToast,
                    WorldGenCurrent = GameWorld.GenerationVersion,
                    IsMinecraftMode = false,
                    SaplingGrowthText = string.Empty,
                };
            }
            var forward = World.GetCameraForward();
            var pickResult = World.TryPickBlock(World.PlayerPosition, forward);
            Point3D[]? highlightQuad = null;
            if (pickResult.HasValue) highlightQuad = ComputeHighlightWorldQuad(pickResult.Value);
            int playerChunkX = GameWorld.WorldToChunkCoord(World.PlayerPosition.X);
            int playerChunkZ = GameWorld.WorldToChunkCoord(World.PlayerPosition.Z);
            int playerLayer = ChunkManager.LayerForWorldY((int)Math.Floor(World.PlayerPosition.Y));
            bool hasBlockTarget = pickResult.HasValue;
            var targetBlock = pickResult?.Remove ?? (0, 0, 0);
            int targetLayer = hasBlockTarget ? ChunkManager.LayerForWorldY(targetBlock.y) : playerLayer;
            int targetChunkX = hasBlockTarget ? GameWorld.WorldToChunkCoord(targetBlock.x) : playerChunkX;
            int targetChunkZ = hasBlockTarget ? GameWorld.WorldToChunkCoord(targetBlock.z) : playerChunkZ;
            var chunkDiagnostics = showFps
                ? gpuRenderer?.CaptureChunkDiagnostics(playerLayer, playerChunkX, playerChunkZ,
                    targetLayer, targetChunkX, targetChunkZ, hasBlockTarget,
                    ChunkRenderRadius, World.Mesher.PendingDirtyCount)
                : null;
            string saplingText = string.Empty;
            if (pickResult.HasValue && World != null && World.BlockTicks != null)
            {
                var r = pickResult.Value.Remove;
                if (World.Chunks.TryGetLoadedBlock(r.x, r.y, r.z, out var sbid) && sbid == BlockRegistry.GetId("sapling"))
                {
                    var info = World.BlockTicks.Sapling.GetGrowthInfo(r.x, r.y, r.z);
                    if (info != null) saplingText = info;
                }
            }
            // Mining overlay only shows on the block actually being mined.
            float miningProgress = 0f;
            Point3D miningBlockPos = Point3D.Zero;
            int miningBlockId = 0;
            var miningBlockNormal = new Point3D(0, 0, 0);
            if (_miningTarget.HasValue && pickResult.HasValue)
            {
                var t = pickResult.Value.Remove;
                if (_miningTarget.Value.x == t.x && _miningTarget.Value.y == t.y && _miningTarget.Value.z == t.z)
                {
                    miningProgress = _miningProgress;
                    miningBlockPos = new Point3D(t.x, t.y, t.z);
                    miningBlockId = _miningBlockId;
                    miningBlockNormal = _miningSlideDir;
                }
            }
            // Coupling beams for any linked carts (empty list when none), for the world pass to draw.
            World.Entities.CollectLinkBeams(_linkBeams);

            // Steve's construction activity is host/single-player authoritative. Keep plan data
            // private; the HUD receives only a generic activity line and no blueprint cells.
            string steveBuilderStatus = string.Empty;
            if (World.IsAuthoritative)
            {
                SteveMob? nearestSteve = null;
                double nearestSteveDistance = double.PositiveInfinity;
                int debugSteveCount = 0;
                foreach (var mob in World.Entities.Mobs)
                {
                    if (mob is not SteveMob steve || steve.IsDead || steve.Removed) continue;
                    if (showFps)
                    {
                        if (debugSteveCount < 8)
                            steve.AppendShelterAIDiagnostics(_steveAIDebugLines, debugSteveCount);
                        debugSteveCount++;
                    }
                    double dx = steve.Position.X - World.PlayerPosition.X;
                    double dy = steve.Position.Y - World.PlayerPosition.Y;
                    double dz = steve.Position.Z - World.PlayerPosition.Z;
                    double distance = dx * dx + dy * dy + dz * dz;
                    if (distance >= nearestSteveDistance) continue;
                    nearestSteve = steve;
                    nearestSteveDistance = distance;
                }
                if (showFps && debugSteveCount > 8)
                    _steveAIDebugLines.Add($"Steve AI: {debugSteveCount - 8} more not shown");
                if (nearestSteve != null)
                    steveBuilderStatus = nearestSteve.GetShelterStatus();
            }

            return new HudState
            {
                ShowDebug = showFps, InventoryOpen = inventoryOpen, CraftingOpen = craftingOpen, SmeltOpen = smeltOpen, OreCartOpen = oreCartOpen,
                FastForwardActive = _fastForwardEnabled && CanFastForwardSimulation(),
                ChatOpen = chatOpen, ChatJustOpened = _chatJustOpened,
                ChatResult = _chatResult, ChatResultIsError = _chatResultIsError,
                OreCartSlots = _openOreCart?.CargoSlots ?? Array.Empty<InventorySlot>(),
                BiomeMenuOpen = biomeMenuOpen, HandEditorOpen = handEditorOpen,
                NoclipMode = World.NoclipMode, CreativeFlightMode = World.CreativeFlightMode,
                Fullbright = ChunkLighting.Fullbright, WorldTime = World.WorldTime, WorldName = World.Name,
                ChunkDiagnostics = chunkDiagnostics, Menu = menu, Fps = lastFps, UpdateMs = lastUpdateMs,
                MeshMs = lastMeshMs, UploadMs = lastUploadMs, RenderMs = lastRenderMs,
                EntityMs = World.LastEntityMs, EntityCount = World.Entities.MobCount,
                FacingText = $"{GetCompassDirection(World.PlayerYaw)} ({GameWorld.NormalizeYaw(World.PlayerYaw):0.0} deg)",
                // Item 0 is air, so an empty hotbar slot would otherwise read "Selected: air".
                // Matches the "Selected: -" fallback used above.
                SelectedBlockText = World.SelectedBlock > 0
                    ? $"Selected: {ItemRegistry.GetName(World.SelectedBlock)}"
                    : "Selected: -",
                RenderDistanceText = $"Render dist: {RenderDistanceName} ({ChunkRenderRadius})",
                SelectedSlot = World.SelectedSlot, WorldSeed = World.Seed,
                BiomeText = World.ChunkProvider?.BiomeNameAt((int)Math.Floor(World.PlayerPosition.X), (int)Math.Floor(World.PlayerPosition.Z)) ?? string.Empty,
                Hotbar = World.Hotbar, HighlightWorldQuad = highlightQuad, LinkBeams = _linkBeams,
                BuildGhostCells = Array.Empty<BuildGhostCell>(), SteveBuilderStatus = steveBuilderStatus,
                SteveAIDiagnostics = _steveAIDebugLines,
                ThirdPerson = thirdPersonView,
                Mode = World.Mode, BagSlots = World.BagSlots, HotbarCounts = World.HotbarCounts, HeldStack = World.HeldStack,
                CraftingSlots = World.CraftingGrid, CraftingResult = World.CraftingResult,
      CraftingGridSize = World.CraftingSize, CraftingTableId = World.CraftingTableId,
      SmeltInput = World.SmeltInput, SmeltFuel = World.SmeltFuel, SmeltOutput = World.SmeltOutput,
      SmeltProgress = World.SmeltProgress, SmeltBurnProgress = World.SmeltBurnProgress,
      SmeltOutputPreview = World.SmeltRecipeForInput?.OutputItemId ?? 0,
                IsMinecraftMode = World.IsMinecraftMode,
                FrontierProgression = World.FrontierProgression,
                SaplingGrowthText = saplingText,
                PlayerHealth = World.LocalPlayer.Health,
                WeightLevel = World.CarryWeightLevel,
                CarriedWeightUnits = World.CarriedWeightUnits,
                DeathCause = World.LocalPlayer.DeathCause,
                PlayerX = World.PlayerPosition.X,
                PlayerY = World.PlayerPosition.Y,
                PlayerZ = World.PlayerPosition.Z,
                PlayerChunkX = playerChunkX,
                PlayerChunkZ = playerChunkZ,
                RenderDistance = ChunkRenderRadius,
                NetStatus = netStatus,
                MultiplayerError = mpError,
                MiningProgress = miningProgress,
                MiningBlockPos = miningBlockPos,
                MiningBlockId = miningBlockId,
                MiningBlockNormal = miningBlockNormal,
                HandPoke = _handPokeTimer,
                HandSwing = _handSwingTimer,
                HandSwingSerial = _handSwingSerial,
                SaveToast = _saveToastTimer,
                StatusToastTimer = _statusToastTimer,
                StatusToast = _statusToast,
                GenWarning = _genWarningTimer,
                WorldGenSaved = World.WorldGenVersion,
                WorldGenCurrent = GameWorld.GenerationVersion,
            };
        }

        private string BuildNetStatus()
        {
            if (_netHost != null && _netHost.IsRunning) return "Hosting on " + GetLanAddresses() + ":" + _activeHostPort;
            if (_netClient != null)
            {
                if (_netConnected) return "Joined " + menu.JoinAddress + " as #" + _netClient.ClientId;
                return "Join error: " + _joinError;
            }
            return string.Empty;
        }

        // The host's LAN IPs, so the friend knows what address to type on Join Game.
        // Prioritizes the IP on the same subnet as the default gateway (actual LAN IP).
        private static string GetLanAddresses()
        {
            try
            {
                var addrs = new List<string>();
                foreach (var ip in System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()).AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(ip))
                    {
                        addrs.Add(ip.ToString());
                    }
                }
                if (addrs.Count == 0) addrs.Add("127.0.0.1");

                // Try to find the IP on the same subnet as the default gateway (actual LAN IP)
                try
                {
                    var defaultGateway = System.Net.NetworkInformation.NetworkInterface
                        .GetAllNetworkInterfaces()
                        .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up 
                                 && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                        .SelectMany(n => n.GetIPProperties().GatewayAddresses)
                        .Where(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        .Select(g => g.Address)
                        .FirstOrDefault();

                    if (defaultGateway != null)
                    {
                        // Find the LAN IP that shares the same subnet as the gateway (assuming /24)
                        var gatewayBytes = defaultGateway.GetAddressBytes();
                        var preferred = addrs.FirstOrDefault(ip => 
                        {
                            var ipBytes = System.Net.IPAddress.Parse(ip).GetAddressBytes();
                            return ipBytes[0] == gatewayBytes[0] 
                                && ipBytes[1] == gatewayBytes[1] 
                                && ipBytes[2] == gatewayBytes[2];
                        });
                        if (preferred != null)
                        {
                            // Move preferred to front
                            addrs.Remove(preferred);
                            addrs.Insert(0, preferred);
                        }
                    }
                }
                catch { /* ignore - fall back to all addresses */ }

                if (addrs.Count == 0) addrs.Add("127.0.0.1");
                return string.Join("/", addrs);
            }
            catch { return "127.0.0.1"; }
        }

        private string BuildMultiplayerError()
        {
            if (string.IsNullOrEmpty(_joinError)) return string.Empty;
            if (_netClient == null && _netHost == null) return _joinError;
            return string.Empty;
        }

    }
}
