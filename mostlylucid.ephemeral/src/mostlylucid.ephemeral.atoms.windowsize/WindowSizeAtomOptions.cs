using System.Globalization;

namespace Mostlylucid.Ephemeral.Atoms.WindowSize;

public sealed class WindowSizeAtomOptions
{
    /// <summary>
    /// Command to set the maximum signal window capacity (e.g., <c>window.size.set</c>).
    /// </summary>
    public string CapacitySetCommand { get; init; } = "window.size.set";

    /// <summary>
    /// Command to increase the maximum capacity (e.g., <c>window.size.increase</c>).
    /// </summary>
    public string CapacityIncreaseCommand { get; init; } = "window.size.increase";

    /// <summary>
    /// Command to decrease the maximum capacity (e.g., <c>window.size.decrease</c>).
    /// </summary>
    public string CapacityDecreaseCommand { get; init; } = "window.size.decrease";

    /// <summary>
    /// Command to set the retention duration (e.g., <c>window.time.set</c>).
    /// </summary>
    public string TimeSetCommand { get; init; } = "window.time.set";

    /// <summary>
    /// Command to increase the retention duration.
    /// </summary>
    public string TimeIncreaseCommand { get; init; } = "window.time.increase";

    /// <summary>
    /// Command to decrease the retention duration.
    /// </summary>
    public string TimeDecreaseCommand { get; init; } = "window.time.decrease";

    public int MinCapacity { get; init; } = 16;
    public int MaxCapacity { get; init; } = 50_000;
    public TimeSpan MinRetention { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxRetention { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan AgeStep { get; init; } = TimeSpan.FromSeconds(10);
    public int CapacityStep { get; init; } = 20;
}
