using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OverlayTimer.Net
{
    /// <summary>
    /// 임시 분석용: F9로 IDLE/DAMAGE 페이즈를 토글하며
    /// - IDLE: 처음 등장하는 dataType만 기록
    /// - DAMAGE: 처음 등장 dataType 기록 + 후보 타입은 매 패킷 hex 페이로드 덤프
    /// </summary>
    public sealed class PacketTypeLogger
    {
        // 1단계에서 DAMAGE-only로 추려진 후보 타입들
        private static readonly HashSet<int> WatchTypes = new()
        {
            100049, 100050, 100051, 100092, 100109, 100128, 100173, 100174,
            100192, 100193, 100195, 100197, 100201, 100308, 100340, 100389,
            100485, 100489, 100592, 100636, 100716, 100722, 100723, 100727,
            100894, 100999, 101006, 101007, 101087,
        };

        private readonly object _lock = new();
        private readonly string _filePath;
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
            // 페이로드를 lock 밖에서 미리 복사 (ref struct는 lock 안에서 캡처 불가)
            byte[]? payloadCopy = null;
            if (_phase == "DAMAGE" && WatchTypes.Contains(dataType))
                payloadCopy = content.ToArray();

            lock (_lock)
            {
                if (_seenInPhase.Add(dataType))
                    AppendLine(dataType.ToString());

                if (payloadCopy != null)
                {
                    _hitCounter.TryGetValue(dataType, out int n);
                    _hitCounter[dataType] = ++n;
                    var spaced = BitConverter.ToString(payloadCopy).Replace("-", " ");
                    AppendLine($"  [{dataType} #{n}] {spaced}");
                }
            }
        }

        private void AppendLine(string line)
        {
            try
            {
                File.AppendAllText(_filePath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
    }
}
