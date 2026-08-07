using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace CubeApp.Net
{
    /// <summary>
    /// Host side of CubeApp networking. Runs the authoritative <see cref="GameWorld"/> (which may
    /// be shared with the local player's rendering), accepts TCP clients, receives their input +
    /// block edits, simulates them (StepRemotePlayers), and broadcasts a full snapshot to every
    /// client at 20Hz. Block edits from clients are applied to the world and folded into the next
    /// snapshot so all clients converge.
    ///
    /// The local player (if the host is also playing) is simulated by the game loop itself; its
    /// state is included in snapshots via <see cref="SetLocalPlayerState"/>.
    /// </summary>
    public sealed class NetHost : IDisposable
    {
        public const int ProtocolVersion = 1;
        public const int DefaultPort = 26065;

        private readonly GameWorld _world;
        private readonly int _port;
        private TcpListener? _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentDictionary<int, ClientConnection> _clients = new();
        private int _nextClientId = 1;
        private readonly object _snapshotLock = new();

        // State included from the host's own player (if the host is playing).
        private NetSnapshot.Player _localSnapshot = new() { Id = 0, Name = "Host" };
        private volatile bool _hasLocal;

        private Task? _acceptTask;
        private Task? _broadcastTask;

        public event Action<string>? Log;

        private sealed class ClientConnection
        {
            public int Id;
            public string Name = "";
            public TcpClient Tcp = null!;
            public NetworkStream Stream = null!;
            public NetSnapshot.Player LastPlayer = new();
        }

        public NetHost(GameWorld world, int port = DefaultPort)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _port = port;
            // When the host's world edits happen (local or remote), add them to the next snapshot
            // for every client. This is the single broadcast source for ALL edits.
            _world.BlockEdited += OnWorldEdit;
        }

        public bool IsRunning => _listener != null;

        /// <summary>Points the host at the local player's state for broadcast.</summary>
        public void SetLocalPlayerState(PlayerState p)
        {
            lock (_snapshotLock)
            {
                _hasLocal = true;
                _localSnapshot.X = (float)p.Position.X;
                _localSnapshot.Y = (float)p.Position.Y;
                _localSnapshot.Z = (float)p.Position.Z;
                _localSnapshot.Yaw = p.Yaw;
                _localSnapshot.Pitch = p.Pitch;
                _localSnapshot.VelY = (float)p.Velocity.Y;
                _localSnapshot.Grounded = p.Grounded;
                _localSnapshot.Fly = p.FlyMode;
                _localSnapshot.WalkPhase = p.WalkPhase;
                _localSnapshot.WalkAmount = p.WalkAmount;
            }
        }

        public bool Start()
        {
            if (_listener != null) return false;
            try
            {
                _listener = new TcpListener(IPAddress.Any, _port);
                _listener.Start();
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Host failed to start on port {_port}: {ex.Message}");
                _listener = null;
                return false;
            }
            _acceptTask = Task.Run(AcceptLoop);
            _broadcastTask = Task.Run(BroadcastLoop);
            Log?.Invoke($"Host listening on port {_port}");
            return true;
        }

        private void OnWorldEdit(int x, int y, int z, int blockId, int meta)
        {
            // Queue the edit for the next snapshot. The world state itself is already updated by
            // the caller; clients replay this edit onto their locally-generated world.
            lock (_snapshotLock)
            {
                _pendingEdits.Add(new NetSnapshot.Edit { X = x, Y = y, Z = z, BlockId = blockId, Meta = meta });
            }
        }

        private readonly List<NetSnapshot.Edit> _pendingEdits = new();
        private readonly System.Collections.Concurrent.ConcurrentQueue<NetSnapshot.Edit> _incomingEdits = new();

        /// <summary>Applies all client edits to the world. MUST be called on the main thread
        /// (Program drains this each frame). Host-authoritative: applying fires BlockEdited, which
        /// queues the edit into the next snapshot for every client.</summary>
        public void DrainIncomingEdits()
        {
            while (_incomingEdits.TryDequeue(out var e))
            {
                _world.ApplyBlockEdit(e.X, e.Y, e.Z, e.BlockId, e.Meta);
            }
        }

        private async Task AcceptLoop()
        {
            var listener = _listener;
            if (listener == null) return;
            while (!_cts.IsCancellationRequested)
            {
                TcpClient tcp;
                try { tcp = await listener.AcceptTcpClientAsync(_cts.Token); }
                catch { break; }
                var conn = new ClientConnection
                {
                    Id = _nextClientId++,
                    Tcp = tcp,
                    Stream = tcp.GetStream(),
                };
                _clients[conn.Id] = conn;
                Log?.Invoke($"Client {conn.Id} connected ({tcp.Client.RemoteEndPoint})");
                _ = Task.Run(() => HandleClient(conn));
            }
        }

        private async Task HandleClient(ClientConnection conn)
        {
            try
            {
                conn.Stream.ReadTimeout = 30000;
                // Wait for the Hello with a short timeout so a half-open connection can't linger.
                var helloFrame = await ReadFrameAsync(conn.Stream, _cts.Token, 10000);
                if (helloFrame == null || helloFrame.Value.type != NetMsgType.Hello)
                {
                    Log?.Invoke($"Client {conn.Id} sent no valid Hello; dropping.");
                    return;
                }
                var hr = new NetReader(helloFrame.Value.body);
                if (!hr.TryReadInt(out int version) || version != ProtocolVersion)
                {
                    Log?.Invoke($"Client {conn.Id} protocol mismatch (got {version}, want {ProtocolVersion}); dropping.");
                    return;
                }
                hr.TryReadString(out conn.Name);
                if (string.IsNullOrEmpty(conn.Name)) conn.Name = $"Player{conn.Id}";

                // Create the host-side sim state for this client, placed at a spawn point.
                var state = _world.AddRemotePlayer(conn.Id);
                state.Position = _world.LocalPlayer.Position + new Point3D(2.0, 0, 2.0);
                state.Yaw = _world.LocalPlayer.Yaw;

                var welcome = new NetWriter();
                welcome.WriteInt(conn.Id);
                welcome.WriteInt(_world.Seed);
                welcome.WriteString(_world.Name);
                welcome.WriteFloat((float)state.Position.X);
                welcome.WriteFloat((float)state.Position.Y);
                welcome.WriteFloat((float)state.Position.Z);
                await conn.Stream.WriteAsync(welcome.ToFrame(NetMsgType.Welcome), _cts.Token);
                Log?.Invoke($"Client {conn.Id} ('{conn.Name}') joined world '{_world.Name}' (seed {_world.Seed})");

                // Send the host's modified chunks so the client's world matches (edits from a
                // singleplayer session aren't derivable from the seed alone).
                await SendModifiedChunks(conn);

                // Read loop: inputs + edits until the connection drops.
                while (!_cts.IsCancellationRequested)
                {
                    var frame = await ReadFrameAsync(conn.Stream, _cts.Token, 30000);
                    if (frame == null) break;
                    switch (frame.Value.type)
                    {
                        case NetMsgType.Input:
                            if (NetSnapshot.TryDeserializeInput(frame.Value.body, out var input, out float yaw, out float pitch))
                            {
                                if (_world.TryGetRemotePlayer(conn.Id, out var ps))
                                {
                                    ps.PendingInput = input;
                                    ps.Yaw = yaw;
                                    ps.Pitch = pitch;
                                }
                            }
                            break;
                        case NetMsgType.BlockEdit:
                            var edit = NetSnapshot.DeserializeEdit(frame.Value.body);
                            if (edit.HasValue)
                            {
                                // Queue for the MAIN thread. The world is not thread-safe; applying
                                // here would race with the host's sim and crash (dropping the
                                // client). Program drains these via DrainIncomingEdits each frame.
                                Log?.Invoke($"Client {conn.Id} edit: ({edit.Value.x},{edit.Value.y},{edit.Value.z}) -> {edit.Value.blockId}");
                                _incomingEdits.Enqueue(new NetSnapshot.Edit { X = edit.Value.x, Y = edit.Value.y, Z = edit.Value.z, BlockId = edit.Value.blockId, Meta = edit.Value.meta });
                            }
                            break;
                        case NetMsgType.Ping:
                            var pong = new NetWriter();
                            var pr = new NetReader(frame.Value.body);
                            if (pr.TryReadLong(out long t)) pong.WriteLong(t);
                            await conn.Stream.WriteAsync(pong.ToFrame(NetMsgType.Pong), _cts.Token);
                            break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log?.Invoke($"Client {conn.Id} error: {ex.Message}");
            }
            finally
            {
                _clients.TryRemove(conn.Id, out _);
                _world.RemoveRemotePlayer(conn.Id);
                Log?.Invoke($"Client {conn.Id} disconnected");
            }
        }

        // Sends every chunk the world marked as modified (player edits, saved-world edits) so a
        // joining client replays them on top of its seed-generated terrain. Chunk data is stable
        // to read here: modified chunks have finished generation and are only mutated via
        // ApplyBlockEdit (main thread) + the mesh worker (which reads, never writes blocks).
        private async Task SendModifiedChunks(ClientConnection conn)
        {
            var coords = new List<ChunkCoordinates>(_world.Chunks.ModifiedChunks);
            int sent = 0;
            foreach (var c in coords)
            {
                if (_cts.IsCancellationRequested) break;
                if (!_world.Chunks.TryGetLoadedChunk(c, out var chunk)) continue;
                try
                {
                    byte[] blocks;
                    byte[] meta;
                    lock (chunk.MeshLock)
                    {
                        blocks = (byte[])chunk.RawBlocks.Clone();
                        meta = (byte[])chunk.RawMeta.Clone();
                    }
                    var frame = NetSnapshot.SerializeChunkData(c.X, c.Z, blocks, meta);
                    await conn.Stream.WriteAsync(frame, _cts.Token);
                    sent++;
                }
                catch { break; }
            }
            if (sent > 0) Log?.Invoke($"Sent {sent} modified chunks to client {conn.Id}");
        }

        // Reads one length-prefixed frame. Returns null on EOF/timeout/invalid.
        private static async Task<(NetMsgType type, byte[] body)?> ReadFrameAsync(NetworkStream s, CancellationToken ct, int timeoutMs)
        {
            var lenBuf = new byte[4];
            int got = 0;
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (got < 4)
            {
                int remainingMs = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remainingMs <= 0) return null;
                s.ReadTimeout = remainingMs;
                int n;
                try { n = await s.ReadAsync(lenBuf.AsMemory(got, 4 - got), ct); }
                catch { return null; }
                if (n <= 0) return null;
                got += n;
            }
            int bodyLen = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
            if (bodyLen < 1 || bodyLen > 1 << 20) return null;
            var body = new byte[bodyLen];
            got = 0;
            while (got < bodyLen)
            {
                int remainingMs = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remainingMs <= 0) return null;
                s.ReadTimeout = remainingMs;
                int n;
                try { n = await s.ReadAsync(body.AsMemory(got, bodyLen - got), ct); }
                catch { return null; }
                if (n <= 0) return null;
                got += n;
            }
            var type = (NetMsgType)body[0];
            var bodyOnly = new byte[bodyLen - 1];
            Buffer.BlockCopy(body, 1, bodyOnly, 0, bodyLen - 1);
            return (type, bodyOnly);
        }

        private async Task BroadcastLoop()
        {
            var sw = Stopwatch.StartNew();
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    // Simulate remote players BEFORE broadcasting so snapshots are fresh.
                    _world.StepRemotePlayers(1f / 20f);

                    // Collect all player states into the snapshot.
                    NetSnapshot.Player[] players;
                    NetSnapshot.Edit[] edits;
                    long tick = sw.ElapsedMilliseconds;
                    lock (_snapshotLock)
                    {
                        var list = new List<NetSnapshot.Player>();
                        if (_hasLocal) list.Add(_localSnapshot);
                        foreach (var kv in _clients)
                        {
                            if (_world.TryGetRemotePlayer(kv.Key, out var ps))
                            {
                                list.Add(new NetSnapshot.Player
                                {
                                    Id = kv.Key,
                                    Name = kv.Value.Name,
                                    X = (float)ps.Position.X,
                                    Y = (float)ps.Position.Y,
                                    Z = (float)ps.Position.Z,
                                    Yaw = ps.Yaw,
                                    Pitch = ps.Pitch,
                                    VelY = (float)ps.Velocity.Y,
                                    Grounded = ps.Grounded,
                                    Fly = ps.FlyMode,
                                    WalkPhase = ps.WalkPhase,
                                    WalkAmount = ps.WalkAmount,
                                });
                            }
                        }
                        players = list.ToArray();
                        edits = _pendingEdits.ToArray();
                        _pendingEdits.Clear();
                    }

                    if (players.Length > 0 || edits.Length > 0)
                    {
                        var snap = new NetSnapshot { Tick = tick };
                        snap.Players.AddRange(players);
                        snap.Edits.AddRange(edits);
                        var frame = snap.Serialize();
                        foreach (var kv in _clients)
                        {
                            try { await kv.Value.Stream.WriteAsync(frame, _cts.Token); }
                            catch { }
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { }
                await Task.Delay(50, _cts.Token);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener?.Stop(); } catch { }
            foreach (var kv in _clients)
            {
                try { kv.Value.Tcp.Close(); } catch { }
            }
            _clients.Clear();
            try { _acceptTask?.Wait(1000); } catch { }
            try { _broadcastTask?.Wait(1000); } catch { }
            _cts.Dispose();
        }
    }
}
