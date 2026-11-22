using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace UniversalRGB.Adapters.OpenRGB;

/// <summary>Bounds-checked little-endian reader for OpenRGB packet payloads.</summary>
public ref struct PacketReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    public PacketReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
    }

    public readonly int Position => _position;

    public readonly int Remaining => _data.Length - _position;

    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    /// <summary>Reads a ushort length (including the NUL terminator) followed by the characters.</summary>
    public string ReadString()
    {
        var length = ReadUInt16();
        if (length == 0)
        {
            return "";
        }

        var bytes = Take(length);
        var end = bytes.IndexOf((byte)0);
        if (end >= 0)
        {
            bytes = bytes[..end];
        }

        return Encoding.UTF8.GetString(bytes);
    }

    public void Skip(int count) => Take(count);

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || _position + count > _data.Length)
        {
            throw new InvalidDataException($"OpenRGB packet is truncated (needed {count} bytes at offset {_position}, have {Remaining}).");
        }

        var slice = _data.Slice(_position, count);
        _position += count;
        return slice;
    }
}

/// <summary>Little-endian writer for OpenRGB packet payloads.</summary>
public sealed class PacketWriter
{
    private readonly ArrayBufferWriter<byte> _buffer = new(256);

    public int Length => _buffer.WrittenCount;

    public PacketWriter UInt16(ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.GetSpan(2), value);
        _buffer.Advance(2);
        return this;
    }

    public PacketWriter UInt32(uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.GetSpan(4), value);
        _buffer.Advance(4);
        return this;
    }

    public PacketWriter Int32(int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(_buffer.GetSpan(4), value);
        _buffer.Advance(4);
        return this;
    }

    /// <summary>Writes ushort length (including NUL) + UTF-8 bytes + NUL.</summary>
    public PacketWriter String(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? "");
        UInt16(checked((ushort)(bytes.Length + 1)));
        _buffer.Write(bytes);
        _buffer.Write([(byte)0]);
        return this;
    }

    public PacketWriter Bytes(ReadOnlySpan<byte> bytes)
    {
        _buffer.Write(bytes);
        return this;
    }

    public byte[] ToArray() => _buffer.WrittenSpan.ToArray();

    public static byte[] Header(uint deviceIndex, uint packetId, int payloadSize)
    {
        var header = new byte[OpenRgbProtocol.HeaderSize];
        OpenRgbProtocol.Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), deviceIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), packetId);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)payloadSize);
        return header;
    }
}
