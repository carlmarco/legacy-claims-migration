using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Etl.Core;

/// <summary>
/// Peak resident memory of this process. .NET's Process.PeakWorkingSet64 returns 0 on macOS, so on Unix this asks the
/// OS directly via getrusage(RUSAGE_SELF).ru_maxrss (bytes on macOS, KiB on Linux), the same source Python reports.
/// </summary>
public static partial class PeakMemory
{
    [LibraryImport("libc", SetLastError = true)]
    private static partial int getrusage(int who, Span<byte> usage);

    public static double Megabytes()
    {
        if (OperatingSystem.IsWindows()) return Process.GetCurrentProcess().PeakWorkingSet64 / 1e6;
        Span<byte> buf = stackalloc byte[256];                 // struct rusage is 144 bytes on 64-bit Unix
        if (getrusage(0, buf) != 0) return 0;
        var maxrss = MemoryMarshal.Read<long>(buf[32..]);     // after ru_utime and ru_stime (two 16-byte timevals)
        return OperatingSystem.IsMacOS() ? maxrss / 1e6 : maxrss * 1024 / 1e6;
    }
}
