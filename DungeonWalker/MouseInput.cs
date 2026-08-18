using System.Runtime.InteropServices;

namespace DungeonWalker;

internal static class MouseInput
{
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, nint dwExtraInfo);

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP   = 0x0004;

    public static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
        mouse_event(MOUSEEVENTF_LEFTUP,   0, 0, 0, 0);
    }
}
