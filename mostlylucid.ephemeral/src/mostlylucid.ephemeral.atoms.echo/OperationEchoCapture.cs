namespace Mostlylucid.Ephemeral.Atoms.Echo;

/// <summary>
///     Represents a single typed signal capture that will become part of an operation echo.
/// </summary>
public sealed record OperationEchoCapture<TPayload>(
    string Signal,
    TPayload Payload,
    DateTimeOffset Timestamp);