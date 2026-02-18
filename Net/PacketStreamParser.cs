using OverlayTimer.Net;
using System;
using System.Buffers.Binary;

public sealed class PacketStreamParser
{
    private readonly byte[] _startMarker;
    private readonly byte[] _endMarker;

    // start~end 구간 내부인지 상태를 기억
    private bool _inFrame;

    private readonly PacketHandler _packetHandler;

    public PacketStreamParser(PacketHandler packetHandler, byte[] startMarker, byte[] endMarker)
    {
        _packetHandler = packetHandler;
        _startMarker = startMarker;
        _endMarker = endMarker;
    }

    public int ParsePackets(byte[] data)
    {
        int consumed = 0;
        int pivot = 0;

        // 1) 프레임 밖: StartMarker를 찾고, 찾으면 그 지점까지 소비하고 "계속" 진행 (return 금지)
        if (!_inFrame)
        {
            int startPivot = FindSequence(data, 0, _startMarker);
            if (startPivot == -1)
            {
                // StartMarker가 TCP 경계에서 잘릴 수 있으니 꼬리는 남기고 소비
                int keep = _startMarker.Length - 1;
                consumed = Math.Max(0, data.Length - keep);
                return consumed;
            }

            // StartMarker 이전 + StartMarker 자체를 소비하고, pivot을 프레임 내부 시작점으로 이동 후 계속 파싱
            pivot = startPivot + _startMarker.Length;
            _inFrame = true;
        }

        // 2) 프레임 안: EndMarker가 올 때까지 스트리밍 파싱
        while (pivot < data.Length)
        {
            // 2-1) EndMarker 체크
            if (pivot + _endMarker.Length <= data.Length &&
                IsMatch(data, pivot, _endMarker))
            {
                pivot += _endMarker.Length;
                consumed = pivot;
                _inFrame = false;
                return consumed;
            }

            // 2-2) 헤더(9바이트)가 모자라면 다음 호출에서 이어붙이기
            if (pivot + 9 > data.Length)
            {
                consumed = pivot;
                return consumed;
            }

            int dataType = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pivot, 4));
            int length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pivot + 4, 4));
            int encodeType = data[pivot + 8];

            LogHelper.Write($"[{DateTime.Now:HH:mm:ss.fff}] [{dataType}] len={length} encodeType={encodeType}");

            // ★ 정체 방지: dataType==0이면 최소한 헤더만큼은 소비해서 같은 위치 재시도 무한정체를 막는다.
            if (dataType == 0)
            {
                pivot += 9;
                consumed = pivot;
                return consumed;
            }

            const int MaxPacketLen = 4 * 1024 * 1024;
            if (length < 0 || length > MaxPacketLen)
            {
                _inFrame = false;
                consumed = data.Length;
                return consumed;
            }

            // 2-3) 바디가 아직 다 안 왔으면 pivot부터 남기고 다음 호출에서 이어붙이기
            if (pivot + 9 + length > data.Length)
            {
                consumed = pivot;
                return consumed;
            }

            var content = new byte[length];
            Buffer.BlockCopy(data, pivot + 9, content, 0, length);

            _packetHandler.OnPacket(dataType, content);

            pivot += 9 + length;
        }

        consumed = pivot;
        return consumed;
    }


    private static bool IsMatch(byte[] data, int pivot, byte[] pattern)
    {
        for (int i = 0; i < pattern.Length; i++)
        {
            if (data[pivot + i] != pattern[i]) return false;
        }
        return true;
    }

    private static int FindSequence(byte[] data, int start, byte[] pattern)
    {
        if (pattern.Length == 0) return start;
        if (start >= data.Length) return -1;

        int idx = data.AsSpan(start).IndexOf(pattern); // .NET 6+
        return idx < 0 ? -1 : start + idx;
    }
}
