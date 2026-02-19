using System;
using System.IO;
using System.Text;

public static class LogHelper
{
    private static readonly object _lock = new object();
    private static readonly string _logDir;
    public static Boolean Disabled = false;

    static LogHelper()
    {
        if (Disabled)
        {
            CurrentFile = "dummy";
            _logDir = "dummy";
            return;
        }

        _logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        CurrentFile = Path.Combine(_logDir, $"sniffer_{DateTime.Now:yyyyMMddHHmmss}.log");
        Directory.CreateDirectory(_logDir);
    }

    private static readonly string CurrentFile;

    public static void Write(string message)
    {
        if (Disabled)
        {
            return;
        }

        try
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
            Console.WriteLine(line);

            lock (_lock)
            {

                File.AppendAllText(
                    CurrentFile,
                    line + Environment.NewLine,
                    Encoding.UTF8
                );
            }
        }
        catch
        {
            // 파일 쓰기 실패해도 캡처는 계속 되어야 하므로 swallow
        }
    }

}
