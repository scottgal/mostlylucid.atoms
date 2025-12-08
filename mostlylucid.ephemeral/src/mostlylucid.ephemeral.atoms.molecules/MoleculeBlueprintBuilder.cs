namespace Mostlylucid.Ephemeral.Atoms.Molecules;

/// <summary>
///     Fluent builder for a molecule blueprint.
/// </summary>
public sealed class MoleculeBlueprintBuilder
{
    private readonly List<Func<MoleculeContext, CancellationToken, Task>> _steps = new();
    private Func<SignalEvent, bool>? _condition;

    /// <summary>
    ///     Creates a builder for the named molecule.
    /// </summary>
    /// <param name="name">Human readable name of the molecule.</param>
    /// <param name="triggerSignalPattern">Globbing pattern that triggers the molecule.</param>
    public MoleculeBlueprintBuilder(string name, string triggerSignalPattern)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(triggerSignalPattern))
            throw new ArgumentException("Trigger signal pattern is required.", nameof(triggerSignalPattern));

        Name = name;
        TriggerSignalPattern = triggerSignalPattern;
    }

    /// <summary>
    ///     Occurs when the blueprint is executed.
    /// </summary>
    public string Name { get; }

    /// <summary>
    ///     Signal pattern that fires this molecule.
    /// </summary>
    public string TriggerSignalPattern { get; }

    /// <summary>
    ///     Add an atom step that returns a task.
    /// </summary>
    public MoleculeBlueprintBuilder AddAtom(Func<MoleculeContext, CancellationToken, Task> atom)
    {
        if (atom is null) throw new ArgumentNullException(nameof(atom));
        _steps.Add(atom);
        return this;
    }

    /// <summary>
    ///     Add a synchronous atom step.
    /// </summary>
    public MoleculeBlueprintBuilder AddAtom(Action<MoleculeContext> atom)
    {
        if (atom is null) throw new ArgumentNullException(nameof(atom));
        _steps.Add((ctx, _) =>
        {
            atom(ctx);
            return Task.CompletedTask;
        });
        return this;
    }

    /// <summary>
    ///     Add a predicate that must pass before the molecule executes.
    /// </summary>
    public MoleculeBlueprintBuilder WithCondition(Func<SignalEvent, bool> condition)
    {
        _condition = condition ?? throw new ArgumentNullException(nameof(condition));
        return this;
    }

    /// <summary>
    ///     Builds the blueprint.
    /// </summary>
    public MoleculeBlueprint Build()
    {
        if (_steps.Count == 0)
            throw new InvalidOperationException("At least one atom step is required.");

        return new MoleculeBlueprint(Name, TriggerSignalPattern,
            new List<Func<MoleculeContext, CancellationToken, Task>>(_steps), _condition);
    }
}