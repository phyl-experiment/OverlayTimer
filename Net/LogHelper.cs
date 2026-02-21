using System;
using System.IO;
using System.Text;

public static class LogHelper
{
    private static readonly object _lock = new();
    private static readonly string _logDir = Path.Combine(AppContext.BaseDirectory, "logs");
    private static string? _currentFile;
    private static bool _initialized;

    public static bool Disabled { get; private set; } = true;
    public static bool PacketHeaderEnabled { get; private set; }

    public static void Configure(bool enabled, bool packetHeaderEnabled)
    {
        Disabled = !enabled;
        PacketHeaderEnabled = packetHeaderEnabled;

        if (!Disabled)
            EnsureInitialized();
    }

    public static void Write(string message)
    {
        if (Disabled)
            return;

        try
        {
            EnsureInitialized();
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
            Console.WriteLine(line);

            lock (_lock)
            {
                if (_currentFile != null)
                {
                    File.AppendAllText(
                        _currentFile,
                        line + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
        }
        catch
        {
            // 파일 쓰기 실패해도 캡처는 계속 되어야 하므로 swallow
        }
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
            return;

        lock (_lock)
        {
            if (_initialized)
                return;

            Directory.CreateDirectory(_logDir);
            _currentFile = Path.Combine(_logDir, $"sniffer_{DateTime.Now:yyyyMMddHHmmss}.log");
            _initialized = true;
        }
    }
}
