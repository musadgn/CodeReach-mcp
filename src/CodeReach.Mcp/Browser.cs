using System.Diagnostics;

namespace CodeReach.Mcp;

/// <summary>Opens a URL in the user's default browser. Failure is harmless: the address is printed too.</summary>
internal static class Browser
{
    public static void Open(string url)
    {
        try
        {
            var start = OperatingSystem.IsWindows() ? new ProcessStartInfo(url) { UseShellExecute = true }
                : OperatingSystem.IsMacOS() ? new ProcessStartInfo("open", [url])
                : new ProcessStartInfo("xdg-open", [url]);
            using var process = Process.Start(start);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine("Could not open a browser; open the address above yourself.");
        }
    }
}
