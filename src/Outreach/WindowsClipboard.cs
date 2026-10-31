using System.Runtime.InteropServices;

namespace Linkedin.Console.Outreach;

/// <summary>
/// The clipboard API rather than <c>clip</c>, which needs a byte order mark to read å correctly and
/// then keeps it: every pasted note started with an invisible character (measured 2026-10-31).
/// </summary>
internal static class WindowsClipboard
{
    private const uint UnicodeText = 13;
    private const uint Moveable = 2;
    private const int Attempts = 10;

    /// <summary>Another program can hold the clipboard for a moment, so it gets a few tries.</summary>
    public static bool TrySet(string text)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    return EmptyClipboard() && Place(text);
                }
                finally
                {
                    CloseClipboard();
                }
            }
            Thread.Sleep(50);
        }
        return false;
    }

    /// <summary>The clipboard owns the memory once it accepts it, and only then.</summary>
    private static bool Place(string text)
    {
        var chars = text.ToCharArray();
        var memory = GlobalAlloc(Moveable, (nuint)((chars.Length + 1) * sizeof(char)));
        if (memory == IntPtr.Zero) return false;

        var target = GlobalLock(memory);
        if (target == IntPtr.Zero)
        {
            GlobalFree(memory);
            return false;
        }

        Marshal.Copy(chars, 0, target, chars.Length);
        Marshal.WriteInt16(target, chars.Length * sizeof(char), 0);
        GlobalUnlock(memory);

        if (SetClipboardData(UnicodeText, memory) != IntPtr.Zero) return true;

        GlobalFree(memory);
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);
}
