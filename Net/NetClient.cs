using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace CubeApp.Net
{
    /// <summary>
    /// Client side of CubeApp networking. Connects to a host, performs the Hello/Welcome
    /// handshake, then:
    ///   - sends TickInputState + look every frame (SerializeInput)
    ///   - receives 20Hz snapshots: remote players to render, block edits to apply to the local
    ///     world (which was generated from the same seed, so only edits need syncing)
    ///   - forwards local block edits to the host (BlockEdit frames)
    ///
    /// The client's own player is simulated LOCALLY for responsiveness (the same physics as the
    /// host), and its authoritative position is what the host echoes to others.
    /// </summary>
    public sealed class NetClient : IDisposable
    {
        public const int ProtocolVersion = NetHost.ProtocolVersion;

        private readonly TcpClient _tcp = new();
        private NetworkStream? _stream;
        private readonly CancellationTokenSource _cts = new();
        private Task? _receiveTask;

        public event Action<string>? Log;
        /// <summary>Raised once when the handshake completes (welcome received).</summary>
        public event Action? Connected;
        /// <summary>Raised when the connection drops or fails.</summary>
        public event Action<string>? Disconnected;

        public bool IsConnected { get; private set; }
        public int ClientId { get; private set; } = -1;
        public int WorldSeed { get; private set; }
        public string WorldName { get; private set; } = "";
        public float SpawnX, SpawnY, SpawnZ;

        // Latest snapshot (received on the network thread).
        private NetSnapshot? _latest;
        private readonly object _snapshotLock = new();

        // The local world, used to apply edits + place remote players.
        private GameWorld? _world;

        // Edits received from the host must be applied on the MAIN thread (the world is not
        // thread-safe; the network thread only queues them).
        private readonly ConcurrentQueue<NetSnapshot.Edit> _incomingEdits = new();

        public NetClient(GameWorld? world = null) => _world = world;

        public NetSnapshot? LatestSnapshot
        {
            get { lock (_snapshotLock) return _latest; }
        }

        /// <summary>Connects and performs the handshake. Returns immediately; use Connected/
        /// Disconnected events. World seed/name are filled when Welcome arrives.</summary>
        public bool Connect(string host, int port, string playerName)
        {
            try
            {
                _tcp.Connect(host, port);
                _tcp.NoDelay = true;
                _stream = _tcp.GetStream();
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Connect failed: {ex.Message}");
                Disconnected?.Invoke(ex.Message);
                return false;
            }

            var hello = new NetWriter();
            hello.WriteInt(ProtocolVersion);
            hello.WriteString(playerName);
            try { _stream.Write(hello.ToFrame(NetMsgType.Hello)); }
            catch (Exception ex) { Log?.Invoke($"Hello failed: {ex.Message}"); return false; }

            _receiveTask = Task.Run(ReceiveLoop);
            return true;
        }

        private async Task ReceiveLoop()
        {
            try
            {
                // Handshake: must receive Welcome first.
                var welcomeFrame = await ReadFrameAsync(_cts.Token);
                if (welcomeFrame == null || welcomeFrame.Value.type != NetMsgType.Welcome)
                {
                    Disconnected?.Invoke("No welcome from host");
                    return;
                }
                var wr = new NetReader(welcomeFrame.Value.body);
                if (!wr.TryReadInt(out int myId)) { Disconnected?.Invoke("Bad welcome"); return; }
                if (!wr.TryReadInt(out int seed)) { Disconnected?.Invoke("Bad welcome"); return; }
                if (!wr.TryReadString(out string name)) { Disconnected?.Invoke("Bad welcome"); return; }
                if (!wr.TryReadFloat(out float sx) || !wr.TryReadFloat(out float sy) || !wr.TryReadFloat(out float sz))
                {
                    Disconnected?.Invoke("Bad welcome");
                    return;
                }
                ClientId = myId;
                WorldSeed = seed;
                WorldName = name;
                SpawnX = sx; SpawnY = sy; SpawnZ = sz;
                IsConnected = true;
                Log?.Invoke($"Connected as #{ClientId} to world '{WorldName}' (seed {WorldSeed})");
                Connected?.Invoke();

                while (!_cts.IsCancellationRequested)
                {
                    var frame = await ReadFrameAsync(_cts.Token);
                    if (frame == null) break;
                    switch (frame.Value.type)
                    {
                        case NetMsgType.Snapshot:
                            var snap = NetSnapshot.Deserialize(frame.Value.body);
                            lock (_snapshotLock) _latest = snap;
                            // Queue edits for the main thread; never touch the world here.
                            foreach (var e in snap.Edits) _incomingEdits.Enqueue(e);
                            break;
                        case NetMsgType.Pong:
                            // latency can be measured later
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Receive error: {ex.Message}");
            }
            finally
            {
                IsConnected = false;
                try { _stream?.Close(); } catch { }
                try { _tcp.Close(); } catch { }
                Disconnected?.Invoke("Connection closed");
            }
        }

        /// <summary>Sends the player's current input + look to the host (non-blocking).</summary>
        public void SendInput(TickInputState input, float yaw, float pitch)
        {
            if (!IsConnected || _stream == null) return;
            try
            {
                var frame = NetSnapshot.SerializeInput(input, yaw, pitch);
                _stream.Write(frame, 0, frame.Length);
            }
            catch { }
        }

        /// <summary>Sends a block edit (break/place) to the host for application + rebroadcast.</summary>
        public void SendBlockEdit(int x, int y, int z, int blockId, int meta)
        {
            if (!IsConnected || _stream == null) return;
            try
            {
                var frame = NetSnapshot.SerializeEdit(x, y, z, blockId, meta);
                _stream.Write(frame, 0, frame.Length);
            }
            catch { }
        }

        /// <summary>Applies all host-echoed edits to the given world. MUST be called on the main
        /// thread (the world is not thread-safe). Idempotent: an edit is skipped if the cell
        /// already holds the target block (our own echo).</summary>
        public void DrainIncomingEdits(GameWorld world)
        {
            while (_incomingEdits.TryDequeue(out var e))
            {
                if (!world.Chunks.TryGetLoadedBlock(e.X, e.Y, e.Z, out var cur) || cur != e.BlockId)
                {
                    world.ApplyBlockEdit(e.X, e.Y, e.Z, e.BlockId, e.Meta);
                }
            }
        }

        public void SendPing()
        {
            if (!IsConnected || _stream == null) return;
            try
            {
                var w = new NetWriter();
                w.WriteLong(Environment.TickCount64);
                var frame = w.ToFrame(NetMsgType.Ping);
                _stream.Write(frame, 0, frame.Length);
            }
            catch { }
        }

        private async Task<(NetMsgType type, byte[] body)?> ReadFrameAsync(CancellationToken ct)
        {
            if (_stream == null) return null;
            var lenBuf = new byte[4];
            int got = 0;
            while (got < 4)
            {
                int n;
                try { n = await _stream.ReadAsync(lenBuf.AsMemory(got, 4 - got), ct); }
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
                int n;
                try { n = await _stream.ReadAsync(body.AsMemory(got, bodyLen - got), ct); }
                catch { return null; }
                if (n <= 0) return null;
                got += n;
            }
            var type = (NetMsgType)body[0];
            var bodyOnly = new byte[bodyLen - 1];
            Buffer.BlockCopy(body, 1, bodyOnly, 0, bodyLen - 1);
            return (type, bodyOnly);
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _stream?.Close(); } catch { }
            try { _tcp.Close(); } catch { }
            try { _receiveTask?.Wait(500); } catch { }
            _cts.Dispose();
        }
    }
}
