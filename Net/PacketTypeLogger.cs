using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OverlayTimer.Net
{
    /// <summary>
    /// F9로 IDLE/DAMAGE 페이즈를 전환한다.
    /// - IDLE: 페이즈 내 최초 등장 dataType 기록
    /// - DAMAGE: 후보 dataType의 hex payload 기록 + 알려진 데미지 구조 파싱 시도
    /// </summary>
    public sealed class PacketTypeLogger
    {
        private readonly object _lock = new();
        private readonly string _filePath;
        private readonly DamagePacketProbeParser _damageProbe = new();

        private string _phase = "IDLE";
        private readonly HashSet<int> _seenInPhase = new();
        private readonly Dictionary<int, int> _hitCounter = new();

        public PacketTypeLogger()
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, $"types_{DateTime.Now:yyyyMMddHHmmss}.txt");
            AppendLine("--- IDLE ---");
        }

        public string Phase => _phase;

        public void TogglePhase()
        {
            lock (_lock)
            {
                _phase = _phase == "IDLE" ? "DAMAGE" : "IDLE";
                _seenInPhase.Clear();
                _hitCounter.Clear();
                AppendLine($"--- {_phase} ---");
            }
        }

        public void OnPacket(int dataType, ReadOnlySpan<byte> content)
        {
            byte[]? payloadCopy = null;
            if (_phase == "DAMAGE" && _damageProbe.IsCandidate(dataType))
                payloadCopy = content.ToArray();

            lock (_lock)
            {
                if (_seenInPhase.Add(dataType))
                    AppendLine(dataType.ToString());

                if (payloadCopy == null)
                    return;

                _hitCounter.TryGetValue(dataType, out int n);
                _hitCounter[dataType] = ++n;

                var spaced = BitConverter.ToString(payloadCopy).Replace("-", " ");
                AppendLine($"  [{dataType} #{n}] {spaced}");

                // 파싱 실패는 suppress: 성공한 경우에만 해석 라인 출력
                if (_damageProbe.TryParseKnownDamageShape(dataType, payloadCopy, out var parsed))
                    AppendLine($"    => {parsed}");
            }
        }

        private void AppendLine(string line)
        {
            try
            {
                File.AppendAllText(_filePath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // ignore write failures in probe logger
            }
        }
    }
}
