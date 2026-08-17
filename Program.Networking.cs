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
        private void HostGame()
        {            if (!int.TryParse(menu.HostPort.Trim(), out int port) || port < 1024 || port > 65535)
            {
                _joinError = $"Invalid port '{menu.HostPort}' (use 1024-65535)";
                return;
            }
            StopNetworking();
            StartNewWorld(ParseSeed(menu.SeedInput), menu.WorldName + " (host)", menu.SelectedMode);
            _netHost = new Net.NetHost(World, port);
            _netHost.Log += msg => System.Console.WriteLine($"[NET] {msg}");
            _netHost.SetLocalPlayerState(World.LocalPlayer);
            if (!_netHost.Start())
            {
                _joinError = $"Could not listen on port {port}. Is it in use?";
                _netHost.Dispose();
                _netHost = null;
                return;
            }
            _activeHostPort = port;
        }

        // Opens the CURRENT singleplayer world to multiplayer (like MC's Open to LAN). Friends
        // join and get this world's seed + all modified chunks (their edits from the session).
        // The host keeps playing; pause menu stays open so the friend can see the port.
        private void OpenToLan()
        {
            if (World == null) return;
            if (_netHost != null && _netHost.IsRunning)
            {
                // Already hosting - just resume playing.
                ResumeToPlaying();
                return;
            }
            int port = Net.NetHost.DefaultPort;
            if (int.TryParse(menu.HostPort.Trim(), out int parsed) && parsed >= 1024 && parsed <= 65535)
            {
                port = parsed;
            }
            _joinError = "";
            _netHost = new Net.NetHost(World, port);
            _netHost.Log += msg => System.Console.WriteLine($"[NET] {msg}");
            _netHost.SetLocalPlayerState(World.LocalPlayer);
            if (!_netHost.Start())
            {
                _joinError = $"Could not listen on port {port}. Is it in use?";
                _netHost.Dispose();
                _netHost = null;
                return;
            }
            _activeHostPort = port;
            // Stay paused so the friend can read the port; Resume returns to play.
        }

        // Joins a host: creates a world from the host's seed (received in Welcome), positions at
        // the host's spawn, and starts streaming input upstream.
        private void JoinGame()
        {
            string addr = menu.JoinAddress.Trim();
            if (string.IsNullOrWhiteSpace(addr))
            {
                _joinError = "Enter a server address (e.g. 192.168.1.5:26065)";
                return;
            }
            string host = addr;
            int port = Net.NetHost.DefaultPort;
            int colon = addr.LastIndexOf(':');
            if (colon > 0 && int.TryParse(addr[(colon + 1)..], out int parsed))
            {
                host = addr[..colon];
                port = parsed;
            }
            StopNetworking();
            // Start a placeholder world now; once Welcome arrives we get the real seed. If the
            // seed differs we rebuild. This keeps the client renderable while connecting.
            StartNewWorld(0, "Connecting...");
            _joinError = "";
            _netClient = new Net.NetClient(World);
            _netClient.Log += msg => System.Console.WriteLine($"[NET] {msg}");
            _netClient.Connected += OnClientConnected;
            _netClient.Disconnected += OnClientDisconnected;
            if (!_netClient.Connect(host, port, _playerName))
            {
                _joinError = "Could not connect. Check the address and that the host is running.";
                _netClient.Dispose();
                _netClient = null;
                // Undo the placeholder world and go back to the multiplayer menu so the player
                // can fix the address instead of being stuck in a fake "Connecting..." world.
                ReturnToTitle();
                menu.Screen = GameScreen.Multiplayer;
            }
        }

        private void OnClientConnected()
        {
            _netConnected = true;
            // Rebuild the world with the host's real seed (same terrain), then sit at spawn.
            if (World == null || World.Seed != _netClient!.WorldSeed)
            {
                StartNewWorld(_netClient.WorldSeed, _netClient.WorldName);
            }
            World.PlayerPosition = new Point3D(_netClient.SpawnX, _netClient.SpawnY, _netClient.SpawnZ);
            World.PlayerVelocity = new Point3D(0, 0, 0);
            _lastMeshPosition = World.PlayerPosition;
            World.EnsureVisibleChunks();
            // Any local edit now goes to the host for authoritative application + broadcast.
            World.BlockEdited += OnLocalEdit;
        }

        private void OnClientDisconnected(string reason)
        {
            bool wasConnected = _netConnected;
            _netConnected = false;
            _joinError = reason;
            // If the connection drops while playing, don't strand the player in a frozen world.
            // Return to the title screen; the error is surfaced via BuildNetStatus on the menu.
            if (wasConnected && screen == GameScreen.Playing)
            {
                try { World.BlockEdited -= OnLocalEdit; } catch { }
                screen = GameScreen.Title;
                menu.Screen = GameScreen.Title;
                DisableMouseLook();
            }
        }

        private void StopNetworking()
        {
            _netConnected = false;
            try { _netHost?.Dispose(); } catch { }
            try { _netClient?.Dispose(); } catch { }
            _netHost = null;
            _netClient = null;
        }

    }
}