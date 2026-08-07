using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace CubeApp.Net
{
    /// <summary>
    /// Message types for the CubeApp network protocol. All messages are length-prefixed frames:
    ///   4-byte little-endian body length | 1-byte message type | body bytes
    /// Bodies are written with <see cref="NetWriter"/>/read with <see cref="NetReader"/>.
    /// </summary>
    public enum NetMsgType : byte
    {
        // client -> host
        Hello = 1,          // protocol version int, player name string
        Input = 2,          // TickInputState flags + look delta + yaw/pitch (host relaying)
        BlockEdit = 3,      // x int, y int, z int, blockId int, meta byte
        Ping = 4,           // client time long
        // host -> client
        Welcome = 10,       // clientId int, world seed int, world name string
        Snapshot = 11,      // players[] + edits[]
        Pong = 12,          // client time long (echo)
    }

    /// <summary>Writer for NetMsg bodies (little-endian).</summary>
    public sealed class NetWriter
    {
        private byte[] _buf = new byte[512];
        private int _len;
        private readonly Encoding _enc = Encoding.UTF8;

        public int Length => _len;
        public ArraySegment<byte> Body => new(_buf, 0, _len);

        public void WriteByte(byte value) { Ensure(1); _buf[_len++] = value; }
        public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);
        public void WriteInt(int value) { Ensure(4); BinaryPrimitives.WriteInt32LittleEndian(_buf.AsSpan(_len, 4), value); _len += 4; }
        public void WriteFloat(float value) { Ensure(4); BinaryPrimitives.WriteInt32LittleEndian(_buf.AsSpan(_len, 4), BitConverter.SingleToInt32Bits(value)); _len += 4; }
        public void WriteLong(long value) { Ensure(8); BinaryPrimitives.WriteInt64LittleEndian(_buf.AsSpan(_len, 8), value); _len += 8; }
        public void WriteString(string value)
        {
            int byteCount = value == null ? 0 : _enc.GetByteCount(value);
            WriteInt(byteCount);
            if (byteCount == 0) return;
            Ensure(byteCount);
            _enc.GetBytes(value, 0, value.Length, _buf, _len);
            _len += byteCount;
        }

        private void Ensure(int extra)
        {
            if (_len + extra <= _buf.Length) return;
            int newSize = Math.Max(_buf.Length * 2, _len + extra);
            Array.Resize(ref _buf, newSize);
        }

        /// <summary>Builds a complete framed message: [len][type][body].</summary>
        public byte[] ToFrame(NetMsgType type)
        {
            var frame = new byte[5 + _len];
            BinaryPrimitives.WriteInt32LittleEndian(frame, _len + 1);
            frame[4] = (byte)type;
            Buffer.BlockCopy(_buf, 0, frame, 5, _len);
            return frame;
        }
    }

    /// <summary>Reader for NetMsg bodies (little-endian).</summary>
    public sealed class NetReader
    {
        private readonly byte[] _buf;
        private int _pos;
        private readonly Encoding _enc = Encoding.UTF8;

        public NetReader(byte[] body) => _buf = body;

        public bool TryReadByte(out byte value)
        {
            if (_pos + 1 > _buf.Length) { value = 0; return false; }
            value = _buf[_pos++];
            return true;
        }
        public bool ReadBool() { TryReadByte(out var b); return b != 0; }
        public bool TryReadInt(out int value)
        {
            if (_pos + 4 > _buf.Length) { value = 0; return false; }
            value = BinaryPrimitives.ReadInt32LittleEndian(_buf.AsSpan(_pos, 4));
            _pos += 4;
            return true;
        }
        public bool TryReadFloat(out float value)
        {
            if (_pos + 4 > _buf.Length) { value = 0; return false; }
            value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(_buf.AsSpan(_pos, 4)));
            _pos += 4;
            return true;
        }
        public bool TryReadLong(out long value)
        {
            if (_pos + 8 > _buf.Length) { value = 0; return false; }
            value = BinaryPrimitives.ReadInt64LittleEndian(_buf.AsSpan(_pos, 8));
            _pos += 8;
            return true;
        }
        public bool TryReadString(out string value)
        {
            value = string.Empty;
            if (!TryReadInt(out int byteCount) || byteCount < 0 || byteCount > 1 << 16 || _pos + byteCount > _buf.Length) return false;
            value = _enc.GetString(_buf, _pos, byteCount);
            _pos += byteCount;
            return true;
        }
        public int Remaining => _buf.Length - _pos;
    }

    /// <summary>A serialized snapshot of every networked player + pending block edits.</summary>
    public sealed class NetSnapshot
    {
        public sealed class Player
        {
            public int Id;
            public string Name = "";
            public float X, Y, Z;
            public float Yaw, Pitch;
            public float VelY;
            public bool Grounded;
            public bool Fly;
            public float WalkPhase, WalkAmount;
        }

        public sealed class Edit
        {
            public int X, Y, Z;
            public int BlockId;
            public int Meta;
        }

        public readonly List<Player> Players = new();
        public readonly List<Edit> Edits = new();
        public long Tick;

        public byte[] Serialize()
        {
            var w = new NetWriter();
            w.WriteLong(Tick);
            w.WriteInt(Players.Count);
            foreach (var p in Players)
            {
                w.WriteInt(p.Id);
                w.WriteString(p.Name);
                w.WriteFloat(p.X); w.WriteFloat(p.Y); w.WriteFloat(p.Z);
                w.WriteFloat(p.Yaw); w.WriteFloat(p.Pitch);
                w.WriteFloat(p.VelY);
                w.WriteBool(p.Grounded); w.WriteBool(p.Fly);
                w.WriteFloat(p.WalkPhase); w.WriteFloat(p.WalkAmount);
            }
            w.WriteInt(Edits.Count);
            foreach (var e in Edits)
            {
                w.WriteInt(e.X); w.WriteInt(e.Y); w.WriteInt(e.Z);
                w.WriteInt(e.BlockId); w.WriteByte((byte)e.Meta);
            }
            return w.ToFrame(NetMsgType.Snapshot);
        }

        public static NetSnapshot Deserialize(byte[] body)
        {
            var r = new NetReader(body);
            var snap = new NetSnapshot();
            if (!r.TryReadLong(out snap.Tick)) return snap;
            if (!r.TryReadInt(out int pc)) return snap;
            for (int i = 0; i < pc && r.Remaining > 0; i++)
            {
                var p = new Player();
                if (!r.TryReadInt(out p.Id)) break;
                if (!r.TryReadString(out p.Name)) break;
                if (!r.TryReadFloat(out p.X) || !r.TryReadFloat(out p.Y) || !r.TryReadFloat(out p.Z)) break;
                if (!r.TryReadFloat(out p.Yaw) || !r.TryReadFloat(out p.Pitch)) break;
                if (!r.TryReadFloat(out p.VelY)) break;
                p.Grounded = r.ReadBool();
                p.Fly = r.ReadBool();
                if (!r.TryReadFloat(out p.WalkPhase) || !r.TryReadFloat(out p.WalkAmount)) break;
                snap.Players.Add(p);
            }
            if (!r.TryReadInt(out int ec)) return snap;
            for (int i = 0; i < ec && r.Remaining > 0; i++)
            {
                var e = new Edit();
                if (!r.TryReadInt(out e.X) || !r.TryReadInt(out e.Y) || !r.TryReadInt(out e.Z)) break;
                if (!r.TryReadInt(out e.BlockId)) break;
                if (!r.TryReadByte(out var meta)) break;
                e.Meta = meta;
                snap.Edits.Add(e);
            }
            return snap;
        }

        /// <summary>Serialized block edit frame (client -> host).</summary>
        public static byte[] SerializeEdit(int x, int y, int z, int blockId, int meta)
        {
            var w = new NetWriter();
            w.WriteInt(x); w.WriteInt(y); w.WriteInt(z);
            w.WriteInt(blockId); w.WriteByte((byte)meta);
            return w.ToFrame(NetMsgType.BlockEdit);
        }

        public static (int x, int y, int z, int blockId, int meta)? DeserializeEdit(byte[] body)
        {
            var r = new NetReader(body);
            if (!r.TryReadInt(out int x) || !r.TryReadInt(out int y) || !r.TryReadInt(out int z)) return null;
            if (!r.TryReadInt(out int blockId)) return null;
            if (!r.TryReadByte(out var meta)) return null;
            return (x, y, z, blockId, meta);
        }

        /// <summary>Serialized TickInputState + look (client -> host).</summary>
        public static byte[] SerializeInput(TickInputState input, float yaw, float pitch)
        {
            var w = new NetWriter();
            byte flags = 0;
            if (input.MoveForward) flags |= 0x01;
            if (input.MoveBackward) flags |= 0x02;
            if (input.MoveLeft) flags |= 0x04;
            if (input.MoveRight) flags |= 0x08;
            if (input.JumpPressed) flags |= 0x10;
            if (input.MoveUp) flags |= 0x20;
            if (input.MoveDown) flags |= 0x40;
            w.WriteByte(flags);
            w.WriteFloat(input.LookDelta.X);
            w.WriteFloat(input.LookDelta.Y);
            w.WriteFloat(yaw);
            w.WriteFloat(pitch);
            return w.ToFrame(NetMsgType.Input);
        }

        public static bool TryDeserializeInput(byte[] body, out TickInputState input, out float yaw, out float pitch)
        {
            input = default;
            yaw = 0; pitch = 0;
            var r = new NetReader(body);
            if (!r.TryReadByte(out var flags)) return false;
            input = new TickInputState(
                (flags & 0x01) != 0, (flags & 0x02) != 0, (flags & 0x04) != 0, (flags & 0x08) != 0,
                (flags & 0x10) != 0, (flags & 0x20) != 0, (flags & 0x40) != 0, default);
            if (!r.TryReadFloat(out var lx) || !r.TryReadFloat(out var ly)) return false;
            input = new TickInputState(input.MoveForward, input.MoveBackward, input.MoveLeft, input.MoveRight,
                input.JumpPressed, input.MoveUp, input.MoveDown, new System.Numerics.Vector2(lx, ly));
            if (!r.TryReadFloat(out yaw) || !r.TryReadFloat(out pitch)) return false;
            return true;
        }
    }
}
