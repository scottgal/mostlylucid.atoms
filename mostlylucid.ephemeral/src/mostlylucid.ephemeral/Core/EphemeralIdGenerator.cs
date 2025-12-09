using System.Runtime.CompilerServices;

namespace Mostlylucid.Ephemeral;

/// <summary>
///     High-performance ID generator using XOR-based mixing.
///     Thread-safe, allocation-free, extremely fast.
///     Optimized for speed over cryptographic strength.
/// </summary>
public static class EphemeralIdGenerator
{
    private static long _counter;

    // Pre-compute process-unique seed by XORing ProcessStart and ProcessId
    // This provides good uniqueness without expensive hashing
    private static readonly long ProcessSeed = Environment.TickCount64 ^  ((long)Environment.ProcessId << 32);

    /// <summary>
    ///     Generates a fast, unique 64-bit ID using XOR mixing.
    ///     Combines process seed with counter for cross-process uniqueness.
    ///     Extremely fast: no hashing, no allocations, just XOR and bit rotation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long NextId()
    {
        var counter = Interlocked.Increment(ref _counter);

        // Fast mixing using XOR and bit rotation (similar to splitmix64)
        // Provides good distribution without expensive hashing
        var id = ProcessSeed ^ counter;
        id ^= id >> 30;
        id *= unchecked((long)0xBF58476D1CE4E5B9L);
        id ^= id >> 27;
        id *= unchecked((long)0x94D049BB133111EBL);
        id ^= id >> 31;

        return id;
    }
}