using System;
using System.Buffers.Binary;

namespace OverlayTimer.Net
{
    public sealed class PacketBuffStart
    {
        public ulong UserId { get; private set; }          // offset 0x00
        public ulong InstKey { get; private set; }         // offset 0x08
        public uint BuffKey { get; private set; }          // offset 0x10
        public float DurationSeconds { get; private set; } // offset 0x14
        public int FlagA { get; private set; }             // offset 0x18
        public int FlagB { get; private set; }             // offset 0x1C

        public static PacketBuffStart Parse(ReadOnlySpan<byte> content)
        {
            // 최소: userId(8) + instKey(8) + buffKey(4) + float(4) + int(4) + int(4) = 32
            if (content.Length < 32)
                throw new ArgumentException($"BuffStart: content too short. len={content.Length}", nameof(content));

            var p = new PacketBuffStart();
            int pos = 0;

            p.UserId = ReadU64(content, ref pos);          // 0x00
            p.InstKey = ReadU64(content, ref pos);         // 0x08
            p.BuffKey = ReadU32(content, ref pos);         // 0x10
            p.DurationSeconds = ReadF32(content, ref pos); // 0x14
            p.FlagA = ReadI32(content, ref pos);           // 0x18
            p.FlagB = ReadI32(content, ref pos);           // 0x1C

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

        private static int ReadI32(ReadOnlySpan<byte> data, ref int pos)
        {
            Require(data, pos, 4);
            int v = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(pos, 4));
            pos += 4;
            return v;
        }

        private static float ReadF32(ReadOnlySpan<byte> data, ref int pos)
        {
            uint raw = ReadU32(data, ref pos);
            return BitConverter.Int32BitsToSingle(unchecked((int)raw));
        }

        private static void Require(ReadOnlySpan<byte> data, int pos, int need)
        {
            if ((uint)pos + (uint)need > (uint)data.Length)
                throw new IndexOutOfRangeException($"Need {need} bytes at pos {pos}, but len={data.Length}");
        }
    }
}
