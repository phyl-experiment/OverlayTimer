namespace OverlayTimer.Net
{
    /// <summary>
    /// ProtocolProbe.TryDiscover() 의 결과. null 이 아닌 필드만 갱신 대상.
    /// </summary>
    public sealed class ProbeResult
    {
        /// <summary>새로 발견된 StartMarker 바이트 배열. null = 변경 없음.</summary>
        public byte[]? NewStartMarker { get; init; }

        /// <summary>새로 발견된 EndMarker 바이트 배열. null = 변경 없음.</summary>
        public byte[]? NewEndMarker { get; init; }

        /// <summary>새로 발견된 buffStart dataType. null = 변경 없음.</summary>
        public int? NewBuffStart { get; init; }

        /// <summary>새로 발견된 buffEnd dataType. null = 변경 없음.</summary>
        public int? NewBuffEnd { get; init; }

        /// <summary>새로 발견된 enterWorld dataType. null = 변경 없음.</summary>
        public int? NewEnterWorld { get; init; }

        /// <summary>새로 발견된 dpsAttack dataType. null = 변경 없음.</summary>
        public int? NewDpsAttack { get; init; }

        /// <summary>새로 발견된 dpsDamage dataType. null = 변경 없음.</summary>
        public int? NewDpsDamage { get; init; }

        /// <summary>탐색 시 발견된 유효 프레임 수. 신뢰도 지표.</summary>
        public int FramesFound { get; init; }
    }
}
