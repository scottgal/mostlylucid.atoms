# Window Size Atom

`mostlylucid.ephemeral.atoms.windowsize` lets you adjust the signal window and retention timeframe by raising commands instead of touching the sink directly.

## Installation

```bash
dotnet add package mostlylucid.ephemeral.atoms.windowsize
```

## Usage

```csharp
var sink = new SignalSink();
await using var atom = new WindowSizeAtom(sink);

// Raise signal to tighten the window to 200 entries
sink.Raise("window.size.set:200");

// Extend retention by 30 seconds
sink.Raise("window.time.increase:00:00:30");
```

### Supported commands

| Command | Effect |
| --- | --- |
| `window.size.set:<number>` | Set the tracked window size (clamped to the configured min/max). |
| `window.size.increase:<number>` | Grow the window by the payload (positive integers). |
| `window.size.decrease:<number>` | Shrink the window by the payload (positive integers). |
| `window.time.set:<ts>` | Set the retention time (`TimeSpan.Parse`). |
| `window.time.increase:<ts>` | Increase retention; accepts `TimeSpan` or `123ms`, `5s`. |
| `window.time.decrease:<ts>` | Decrease retention similarly. |

All commands flow through `SignalCommandMatch`, so nested signal names like `party.window.size.increase:20` work just as well.
