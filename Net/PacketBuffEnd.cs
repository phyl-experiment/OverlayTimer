using System;
using System.Buffers.Binary;

namespace OverlayTimer.Net
{
    // dataType 100055
    public sealed class PacketBuffEnd
    {
        public ulong UserId { get; private set; }  // offset 0x00
        public ulong InstKey { get; private set; } // offset 0x08
        public uint State { get; private set; }    // offset 0x10

        public static PacketBuffEnd Parse(ReadOnlySpan<byte> content)
        {
            // userId(8) + instKey(8) + state(4) = 20
            if (content.Length < 20)
                throw new ArgumentException($"BuffEnd: content too short. len={content.Length}", nameof(content));

            var p = new PacketBuffEnd();
            int pos = 0;

            p.UserId = ReadU64(content, ref pos);
            p.InstKey = ReadU64(content, ref pos);
            p.State = ReadU32(content, ref pos);

            return p;
        }

        private static ulong ReadU64(ReadOnlySpan<byte> data, ref int pos)
        {
            Require(data, pos, 8);
            ulong v = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(pos, 8));
            pos += 8;
            return v;
        }

        private static uint ReadU32(ReadOnlySpan<byte> data, ref int pos)
        {
            Require(data, pos, 4);
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos, 4));
            pos += 4;
            return v;
        }

        private static void Require(ReadOnlySpan<byte> data, int pos, int need)
        {
            if ((uint)pos + (uint)need > (uint)data.Length)
                throw new IndexOutOfRangeException($"Need {need} bytes at pos {pos}, but len={data.Length}");
        }
    }
}
