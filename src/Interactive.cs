// Small helpers for running sfmodcheck by hand (step 5, 2026-11-09). None of them changes a finding.
using System.Runtime.InteropServices;

namespace SfModCheck;

public static class Interactive
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

    /// <summary>True when sfmodcheck has a console window to itself: Windows made one for it because a plugin was
    /// dropped on the exe or the exe was double-clicked. False when run from a prompt, a script or another program
    /// (they share their console or redirect the output), and always false off Windows.</summary>
    public static bool OwnsConsole()
    {
        if (!OperatingSystem.IsWindows() || Console.IsInputRedirected || Console.IsOutputRedirected || Console.IsErrorRedirected)
            return false;
        try { return GetConsoleProcessList(new uint[2], 2) == 1; }
        catch { return false; }
    }

    /// <summary>The record count in the plugin's header (TES4 HEDR), or -1 if it can't be read. Used only to decide
    /// whether to print the memory note.</summary>
    public static long HeaderRecordCount(string plugin)
    {
        try
        {
            var b = new byte[40];
            using var fs = File.OpenRead(plugin);
            if (fs.Read(b, 0, b.Length) < b.Length) return -1;
            // 24-byte record header, then the first field: type (4), size (2), data. HEDR = version (float),
            // record count (int32), next object ID (uint32).
            if (b[0] != 'T' || b[1] != 'E' || b[2] != 'S' || b[3] != '4') return -1;
            if (b[24] != 'H' || b[25] != 'E' || b[26] != 'D' || b[27] != 'R' || BitConverter.ToUInt16(b, 28) != 12) return -1;
            return BitConverter.ToInt32(b, 34);
        }
        catch { return -1; }
    }
}
