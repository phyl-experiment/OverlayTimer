using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OverlayTimer.Net
{
    /// <summary>
    /// 임시 분석용: F9로 IDLE/DAMAGE 페이즈를 토글하며
    /// 각 페이즈에서 처음 등장하는 dataType을 파일에 기록한다.
    /// </summary>
    public sealed class PacketTypeLogger
    {
        private readonly object _lock = new();
        private readonly string _filePath;
        private string _phase = "IDLE";
        private readonly HashSet<int> _seenInPhase = new();

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
                AppendLine($"--- {_phase} ---");
            }
        }

        public void OnPacket(int dataType, ReadOnlySpan<byte> content)
        {
            lock (_lock)
            {
                if (_seenInPhase.Add(dataType))
                    AppendLine(dataType.ToString());
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
