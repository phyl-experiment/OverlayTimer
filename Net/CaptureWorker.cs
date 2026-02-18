using OverlayTimer.Net;

public sealed class CaptureWorker
{
    private readonly PacketStreamParser _streamParser;
    private readonly List<byte> _c2sBuffer = new();
    private readonly List<byte> _s2cBuffer = new();

    public CaptureWorker(PacketStreamParser streamParser)
    {
        _streamParser = streamParser;
    }

    public enum Direction
    {
        ClientToServer,
        ServerToClient
    }

    public void OnTcpPayload(ReadOnlySpan<byte> payload, Direction direction)
    {
        if (payload.IsEmpty)
            return;

        var buffer = direction == Direction.ClientToServer ? _c2sBuffer : _s2cBuffer;

        buffer.AddRange(payload.ToArray());

        // 가능한 만큼 즉시 파싱 (consumed가 0이 될 때까지)
        while (buffer.Count > 0)
        {
            var data = buffer.ToArray();
            var consumed = _streamParser.ParsePackets(data);

            if (consumed <= 0)
                break;

            if (consumed >= buffer.Count) buffer.Clear();
            else buffer.RemoveRange(0, consumed);
        }

        // 안전장치
        const int MaxBufferBytes = 4 * 1024 * 1024;
        if (buffer.Count > MaxBufferBytes)
        {
            Console.WriteLine($"[WARN] Buffer too large ({buffer.Count} bytes). Clearing.");
            buffer.Clear();
        }
    }

}
