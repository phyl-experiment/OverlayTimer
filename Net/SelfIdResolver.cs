using System;
using System.Buffers.Binary;

public sealed class SelfIdResolver
{
    public ulong SelfId => _selfId;

    private ulong _selfId;
    private readonly int _enterWorldType;

    public SelfIdResolver(int enterWorldType)
    {
        _enterWorldType = enterWorldType;
    }

    public ulong TryFeed(int dataType, ReadOnlySpan<byte> payload)
    {
        if (dataType == _enterWorldType)
        {
            ulong id = ReadU64LE(payload.Slice(16, 8));

            if (id != 0)
            {
                LogHelper.Write($"SelfId set {id}");
                _selfId = id;
                return id;
            }
        }

        return 0;
    }

    private static ulong ReadU64LE(ReadOnlySpan<byte> s)
    {
        if (s.Length < 8) throw new ArgumentException("Need 8 bytes", nameof(s));
        return BinaryPrimitives.ReadUInt64LittleEndian(s);
    }
}
