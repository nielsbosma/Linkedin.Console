using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Linkedin.Console.Outreach;

/// <summary>The two things <c>outreach next</c> asks of the computer. Each failure has a way round it, so neither throws.</summary>
public static class Desktop
{
    private const int ClipboardTimeoutMs = 5000;

    public static bool Copy(string text)
    {
        if (OperatingSystem.IsWindows()) return WindowsClipboard.TrySet(text);

        var utf8 = new UTF8Encoding(false).GetBytes(text);

        if (OperatingSystem.IsMacOS()) return Pipe("pbcopy", [], utf8);

        return Pipe("wl-copy", [], utf8)
               || Pipe("xclip", ["-selection", "clipboard"], utf8)
               || Pipe("xsel", ["--clipboard", "--input"], utf8);
    }

    /// <summary>Takes only links the tool built itself, in <c>LinkedInUrl.CanonicalProfile</c>, never one from the list.</summary>
    public static bool Open(string url)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool Pipe(string program, string[] arguments, byte[] input)
    {
        var info = new ProcessStartInfo(program) { RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(info);
            if (process is null) return false;

            process.StandardInput.BaseStream.Write(input);
            process.StandardInput.Close();

            return process.WaitForExit(ClipboardTimeoutMs) && process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return false;
        }
    }
}
