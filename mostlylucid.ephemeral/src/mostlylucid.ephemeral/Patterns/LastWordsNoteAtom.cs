namespace Mostlylucid.Ephemeral.Patterns;

/// <summary>
/// Small helper that serializes “last words” notes via an <see cref="EphemeralWorkCoordinator{T}"/>.
/// Designed for the brief window when an operation is being collected so you can persist minimal state.
/// </summary>
public sealed class LastWordsNoteAtom : IAsyncDisposable
{
    private readonly EphemeralWorkCoordinator<LastWordsNote> _coordinator;

    /// <summary>
    /// Creates a note atom. The provided <paramref name="persist"/> callback runs inside an <see cref="EphemeralWorkCoordinator{T}"/> with <see cref="EphemeralOptions.MaxConcurrency"/> = 1 by default.
    /// </summary>
    public LastWordsNoteAtom(Func<LastWordsNote, CancellationToken, Task> persist, EphemeralOptions? options = null)
    {
        if (persist is null) throw new ArgumentNullException(nameof(persist));

        var coordinatorOptions = options ?? new EphemeralOptions
        {
            MaxConcurrency = 1,
            MaxTrackedOperations = 64,
            MaxOperationLifetime = TimeSpan.FromSeconds(30)
        };

        // Ensure serialization
        _coordinator = new EphemeralWorkCoordinator<LastWordsNote>(persist, coordinatorOptions);
    }

    /// <summary>
    /// Enqueue a note. Call from an operation finalization callback.
    /// </summary>
    public ValueTask EnqueueAsync(LastWordsNote note, CancellationToken cancellationToken = default)
    {
        return _coordinator.EnqueueAsync(note, cancellationToken);
    }

    /// <summary>
    /// Flush any pending notes.
    /// </summary>
    public Task DrainAsync(CancellationToken cancellationToken = default) =>
        _coordinator.DrainAsync(cancellationToken);

    /// <summary>
    /// Releases resources. Waits for in-flight notes to finish.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _coordinator.Complete();
        await _coordinator.DrainAsync().ConfigureAwait(false);
        await _coordinator.DisposeAsync().ConfigureAwait(false);
    }
}
