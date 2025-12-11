namespace Mostlylucid.Ephemeral;

/// <summary>
///     Encapsulates coordinator metrics with thread-safe volatile reads.
///     Eliminates repetitive Volatile.Read patterns across coordinators.
/// </summary>
/// <param name="pending">Reference to pending count field.</param>
/// <param name="active">Reference to active count field.</param>
/// <param name="totalEnqueued">Reference to total enqueued field.</param>
/// <param name="totalCompleted">Reference to total completed field.</param>
/// <param name="totalFailed">Reference to total failed field.</param>
/// <param name="completed">Reference to completed flag field.</param>
/// <param name="paused">Reference to paused flag field.</param>
public readonly struct CoordinatorMetrics(
    ref int pending,
    ref int active,
    ref int totalEnqueued,
    ref int totalCompleted,
    ref int totalFailed,
    ref bool completed,
    ref bool paused)
{
    public int PendingCount { get; } = Volatile.Read(ref pending);
    public int ActiveCount { get; } = Volatile.Read(ref active);
    public int TotalEnqueued { get; } = Volatile.Read(ref totalEnqueued);
    public int TotalCompleted { get; } = Volatile.Read(ref totalCompleted);
    public int TotalFailed { get; } = Volatile.Read(ref totalFailed);
    public bool IsCompleted { get; } = Volatile.Read(ref completed);
    public bool IsPaused { get; } = Volatile.Read(ref paused);

    public bool IsDrained => IsCompleted && PendingCount == 0 && ActiveCount == 0;
}
