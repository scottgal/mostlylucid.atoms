using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Atoms.Molecules;

/// <summary>
/// Defines a reusable workflow triggered by a signal pattern.
/// </summary>
public sealed class MoleculeBlueprint
{
    private readonly List<Func<MoleculeContext, CancellationToken, Task>> _steps;

    internal MoleculeBlueprint(
        string name,
        string triggerPattern,
        List<Func<MoleculeContext, CancellationToken, Task>> steps,
        Func<SignalEvent, bool>? condition)
    {
        Name = name;
        TriggerPattern = triggerPattern;
        _steps = steps;
        Condition = condition;
    }

    /// <summary>
    /// Display name of the molecule.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Signal pattern that fires this molecule.
    /// </summary>
    public string TriggerPattern { get; }

    internal IReadOnlyList<Func<MoleculeContext, CancellationToken, Task>> Steps => _steps;

    /// <summary>
    /// Adds another atom step to the molecule.
    /// </summary>
    public MoleculeBlueprint AddAtom(Func<MoleculeContext, CancellationToken, Task> step)
    {
        if (step is null) throw new ArgumentNullException(nameof(step));
        _steps.Add(step);
        return this;
    }

    /// <summary>
    /// Adds a synchronous atom step.
    /// </summary>
    public MoleculeBlueprint AddAtom(Action<MoleculeContext> step)
    {
        if (step is null) throw new ArgumentNullException(nameof(step));
        _steps.Add((ctx, _) =>
        {
            step(ctx);
            return Task.CompletedTask;
        });
        return this;
    }

    /// <summary>
    /// Remove steps matching the provided predicate.
    /// </summary>
    public int RemoveAtoms(Predicate<Func<MoleculeContext, CancellationToken, Task>> predicate)
    {
        if (predicate is null) throw new ArgumentNullException(nameof(predicate));
        return _steps.RemoveAll(predicate);
    }

    private Func<SignalEvent, bool>? Condition { get; }

    internal bool Matches(SignalEvent signal) =>
        StringPatternMatcher.Matches(signal.Signal, TriggerPattern)
        && (Condition?.Invoke(signal) ?? true);
}
