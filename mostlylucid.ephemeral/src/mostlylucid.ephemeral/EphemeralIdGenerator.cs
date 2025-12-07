using System.IO.Hashing;
using System.Runtime.CompilerServices;

namespace Mostlylucid.Ephemeral;

/// <summary>
/// High-performance ID generator using XxHash64.
/// Thread-safe, allocation-free after warmup.
/// </summary>
internal static class EphemeralIdGenerator
{
    private static long _counter;
    private static readonly long ProcessStart = Environment.TickCount64;
    private static readonly int ProcessId = Environment.ProcessId;

    /// <summary>
    /// Generates a fast, unique 64-bit ID using XxHash64.
    /// Combines process start time, process ID, and counter for cross-process uniqueness.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long NextId()
    {
        var counter = Interlocked.Increment(ref _counter);

        // Combine counter with process-unique seed for XxHash64
        // Include process ID to avoid collisions across processes
        Span<byte> buffer = stackalloc byte[24];
        BitConverter.TryWriteBytes(buffer, ProcessStart);
        BitConverter.TryWriteBytes(buffer.Slice(8), ProcessId);
        BitConverter.TryWriteBytes(buffer.Slice(16), counter);

        return unchecked((long)XxHash64.HashToUInt64(buffer));
    }
}
