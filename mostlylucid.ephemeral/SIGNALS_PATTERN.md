# The Ephemeral Signals Pattern

## Philosophy: "Hey, Look at Me!" — Context First, State Optional

**Core Principle:** Signals are primarily notifications. State CAN be included as an optimization.
**Pattern:** Signal provides context (+ optional hint) → Listener verifies with atom for truth.

This is a **non-locking "sure of value" system** where:
- Signals announce **context** ("order placed", "file saved", "error occurred")
- Signals MAY include **small state hints** for fast-path decisions
- Atoms **always hold the authoritative state**
- Listeners can use hints for speed, but **verify with atom for truth**

### Signal Lifetime (v2.0+)

**⚠️ Important:** As of v2.0.0, **atoms/coordinators own their signals**. When an operation expires from a coordinator's ephemeral window, all its signals are automatically removed from SignalSink.

```
Operation Lifetime = Signal Lifetime
```

**Primary Expiration:** Coordinator-managed
- When `MaxTrackedOperations` limit is reached → oldest operation evicted → its signals cleared
- When operation exceeds `MaxOperationLifetime` → operation evicted → its signals cleared
- Automatic via `CoordinatorBase.NotifyOperationFinalized()` → calls `Signals?.ClearOperation(op.Id)`

**Secondary Expiration:** Atom-level (optional)
```csharp
public class MyAtom : AtomBase<EphemeralWorkCoordinator<T>>
{
    public MyAtom(SignalSink sink) : base(
        coordinator,
        maxSignalCount: 5000,                      // Limit signals for this atom
        maxSignalAge: TimeSpan.FromMinutes(5)      // Max age for this atom's signals
    ) { }
}
```

**Safety Bounds:** SignalSink (fallback only)
- `maxCapacity` and `maxAge` parameters are safety bounds, not primary expiration
- Protect against misconfigured coordinators or memory leaks
- Should be set conservatively as last-resort limits

**Key Benefit:** Operations and their signals have the same lifetime. No orphaned signals, no dual expiration logic.

See [SignalSink-Lifetime.md](docs/SignalSink-Lifetime.md) for detailed ownership model and [ReleaseNotes.txt](src/mostlylucid.ephemeral/ReleaseNotes.txt) for migration guide.

### The Three Models

1. **Pure Notification** (most common)
   ```csharp
   _sink.Raise("file.saved");  // No state
   var filename = atom.GetFilename();  // Query for truth
   ```

2. **Context + Hint** (optimization - "double-safe")
   ```csharp
   _sink.Raise("file.saved:report.pdf");  // Hint for fast path
   // Listener CAN use hint for quick decisions
   // But SHOULD verify with atom for authoritative state
   var actualFilename = atom.GetFilename();  // Truth
   ```

3. **Command** (infrastructure only)
   ```csharp
   _sink.Raise("window.size.set:500");  // Imperative command
   ```

## The Pattern Models

### Model 1: Pure Notification (Safest)

```csharp
// The atom holds ALL state
public class FileOperationAtom
{
    private string _lastFilename = "";
    private string _lastOperation = "";
    private DateTime _lastOperationTime;
    private long _fileSize = 0;
    private string _filePath = "";
    private readonly SignalSink _sink;

    public void SaveFile(string path, byte[] content)
    {
        // Update ALL state in the atom
        _lastFilename = Path.GetFileName(path);
        _filePath = path;
        _fileSize = content.Length;
        _lastOperation = "save";
        _lastOperationTime = DateTime.UtcNow;

        // Do actual save operation...
        File.WriteAllBytes(path, content);

        // ✅ Signal is just a notification - NO state!
        _sink.Raise("file.saved");
    }

    // State accessors - listeners query these
    public string GetLastFilename() => _lastFilename;
    public string GetLastFilePath() => _filePath;
    public long GetLastFileSize() => _fileSize;
    public DateTime GetLastOperationTime() => _lastOperationTime;

    // Composite queries for convenience
    public FileOperationInfo GetLastOperation() => new()
    {
        Filename = _lastFilename,
        Path = _filePath,
        Size = _fileSize,
        Operation = _lastOperation,
        Timestamp = _lastOperationTime
    };
}

public record FileOperationInfo
{
    public string Filename { get; init; } = "";
    public string Path { get; init; } = "";
    public long Size { get; init; }
    public string Operation { get; init; } = "";
    public DateTime Timestamp { get; init; }
}

// Listener 1: Email notification - queries what it needs
_sink.SignalRaised += signal =>
{
    if (signal.Signal == "file.saved")
    {
        var filename = fileAtom.GetLastFilename();
        var size = fileAtom.GetLastFileSize();

        SendEmail($"File saved: {filename} ({size} bytes)");
    }
};

// Listener 2: Backup system - needs full path
_sink.SignalRaised += signal =>
{
    if (signal.Signal == "file.saved")
    {
        var path = fileAtom.GetLastFilePath();
        BackupFile(path);
    }
};

// Listener 3: Audit log - gets full operation info
_sink.SignalRaised += signal =>
{
    if (signal.Signal == "file.saved")
    {
        var info = fileAtom.GetLastOperation();
        _auditLog.Write($"[{info.Timestamp}] {info.Operation}: {info.Path} ({info.Size} bytes)");
    }
};
```

**Key Points:**
- Signal is just "file.saved" - no data
- All state lives in `FileOperationAtom`
- Different listeners query different parts of state
- Each listener gets current, accurate state
- No coupling between signal format and listener needs

### Model 2: Context + Hint (Double-Safe Optimization)

```csharp
public class FileAtom
{
    private string _currentFilename = "";
    private readonly SignalSink _sink;

    public void SaveFile(string filename)
    {
        _currentFilename = filename;
        // Do save operation...

        // ✅ Signal includes hint - fast path optimization
        _sink.Raise($"file.saved:{filename}");
    }

    // Atom is ALWAYS the source of truth
    public string GetCurrentFilename() => _currentFilename;
}

// Double-safe listener: use hint, verify with atom
_sink.SignalRaised += signal =>
{
    if (SignalCommandMatch.TryParse(signal.Signal, "file.saved", out var match))
    {
        var hintFilename = match.Payload;

        // Fast path: use hint for quick decisions
        if (hintFilename.EndsWith(".tmp"))
        {
            // Skip temp files immediately
            return;
        }

        // Verify with atom for authoritative state
        var actualFilename = fileAtom.GetCurrentFilename();

        // Use actual state for important operations
        ProcessFile(actualFilename);

        // Optional: detect if hint was stale
        if (hintFilename != actualFilename)
        {
            _logger.LogDebug("Signal hint stale, used atom truth");
        }
    }
};
```

**When to use hints:**
- ✅ Fast-path filtering ("skip .tmp files")
- ✅ Logging/debugging context
- ✅ Non-critical optimizations
- ✅ When atom query is expensive

**Always verify with atom for:**
- ❗ Critical business logic
- ❗ State changes
- ❗ Persistence operations
- ❗ When accuracy matters

### ❌ Anti-Pattern: Signal as ONLY Source

```csharp
// DON'T: Trust signal without atom
_sink.SignalRaised += signal =>
{
    if (signal.Signal.StartsWith("file.saved:"))
    {
        var filename = signal.Signal.Split(':')[1];
        // ❌ Using signal state without verifying atom
        DeleteFile(filename); // What if it's stale?
    }
};
```

**Why this is wrong:**
1. **Race conditions**: State might have changed
2. **No verification**: Assuming signal is current
3. **Not double-safe**: Single point of failure

## The "Sure of Value" System

Ephemeral signals create a **non-locking coordination pattern**:

```csharp
// Thread 1: Updates state and signals
public void UpdateOrder(Order order)
{
    lock (_orderLock)
    {
        _currentOrder = order; // State update
    }
    _sink.Raise("order.updated"); // Context notification (lock-free)
}

// Thread 2: Receives signal and queries
_sink.SignalRaised += signal =>
{
    if (signal.Signal == "order.updated")
    {
        // Query for current state when we're ready
        lock (_orderLock)
        {
            var order = _currentOrder; // Get "sure of value"
            ProcessOrder(order);
        }
    }
};
```

**Benefits:**
- Signal emission is **fast and lock-free**
- Listeners get **current state**, not stale signal payload
- **No race conditions** from state in signal
- **Single source of truth** (the atom)

## Double-Safe Pattern in Action

### Example: Order Processing with Hints

```csharp
public class OrderAtom
{
    private Order? _currentOrder;
    private readonly SignalSink _sink;

    public void PlaceOrder(Order order)
    {
        _currentOrder = order;

        // Include hint: customer tier for fast-path routing
        _sink.Raise($"order.placed:{order.CustomerTier}");
    }

    public Order? GetCurrentOrder() => _currentOrder;
}

// Listener 1: Fast-path routing using hint
_sink.SignalRaised += signal =>
{
    if (SignalCommandMatch.TryParse(signal.Signal, "order.placed", out var match))
    {
        var tierHint = match.Payload;

        // Fast path: route VIP orders immediately
        if (tierHint == "VIP")
        {
            RouteToVIPQueue();  // Quick decision using hint
        }

        // Then verify and process fully
        var order = orderAtom.GetCurrentOrder();
        if (order != null)
        {
            // Use actual order state for processing
            ProcessOrder(order);

            // Hint helped with routing, atom provided truth
        }
    }
};

// Listener 2: Only cares about high-value orders
_sink.SignalRaised += signal =>
{
    if (signal.Signal.StartsWith("order.placed"))
    {
        // Query atom - hint isn't relevant here
        var order = orderAtom.GetCurrentOrder();
        if (order?.Total > 1000)
        {
            NotifyAccountManager(order);
        }
    }
};
```

**Benefits:**
- VIP routing is **instant** (no atom query needed for routing decision)
- Full order processing uses **authoritative state** from atom
- If hint is stale, routing might be sub-optimal but processing is correct
- Different listeners can ignore hints if not useful

### Example: Cache with Size Hint

```csharp
public class CacheAtom
{
    private int _itemCount = 0;
    private readonly SignalSink _sink;

    public void Add(string key, object value)
    {
        _cache[key] = value;
        _itemCount++;

        // Hint: approximate size for quick checks
        _sink.Raise($"cache.added:{_itemCount}");
    }

    public int GetAccurateCount() => _cache.Count;
    public bool IsNearCapacity() => _cache.Count > 900;
}

// Listener: Use hint for rough capacity check
_sink.SignalRaised += signal =>
{
    if (SignalCommandMatch.TryParse(signal.Signal, "cache.added", out var match))
    {
        if (int.TryParse(match.Payload, out var sizeHint))
        {
            // Fast path: roughly near capacity?
            if (sizeHint > 800)
            {
                // Verify with atom before evicting
                if (cacheAtom.IsNearCapacity())
                {
                    TriggerEviction();
                }
            }
        }
    }
};
```

**Benefits:**
- Hint avoids atom query in common case (size < 800)
- Atom query only when hint suggests action needed
- If hint is slightly stale (multiple adds concurrent), atom provides truth

## Pattern Examples

### 1. Circuit Breaker State (Pure Notification)

```csharp
public class CircuitBreakerAtom
{
    private CircuitState _state = CircuitState.Closed;
    private int _failureCount = 0;
    private readonly SignalSink _sink;

    public void RecordFailure()
    {
        _failureCount++;
        if (_failureCount > 5 && _state == CircuitState.Closed)
        {
            _state = CircuitState.Open;

            // ✅ Just context: "circuit opened"
            _sink.Raise("circuit.open");
        }
    }

    // State queries
    public CircuitState GetState() => _state;
    public int GetFailureCount() => _failureCount;
    public bool IsOpen() => _state == CircuitState.Open;
}

// Listener queries for details
_sink.SignalRaised += signal =>
{
    if (signal.Signal == "circuit.open")
    {
        // Query atom for current state
        var failures = breaker.GetFailureCount();
        var state = breaker.GetState();

        _logger.LogWarning($"Circuit opened after {failures} failures");

        // React based on CURRENT state, not signal payload
        if (breaker.IsOpen())
        {
            EnableFallback();
        }
    }
};
```

### 2. Cache Invalidation

```csharp
public class CacheAtom
{
    private readonly Dictionary<string, object> _cache = new();
    private string _lastInvalidatedKey = "";
    private readonly SignalSink _sink;

    public void Invalidate(string key)
    {
        _cache.Remove(key);
        _lastInvalidatedKey = key;

        // ✅ Context notification
        _sink.Raise("cache.invalidated");
    }

    // State queries
    public string GetLastInvalidatedKey() => _lastInvalidatedKey;
    public bool Contains(string key) => _cache.ContainsKey(key);
    public int GetSize() => _cache.Count;
}

// Listener checks current cache state
_sink.SignalRaised += signal =>
{
    if (signal.Signal == "cache.invalidated")
    {
        // Query for context
        var key = cacheAtom.GetLastInvalidatedKey();
        var size = cacheAtom.GetSize();

        _metrics.Record("cache.size", size);

        // If key was important, reload it
        if (key == "critical.data")
        {
            ReloadCriticalData();
        }
    }
};
```

### 3. Order Processing

```csharp
public class OrderAtom
{
    private Order? _currentOrder;
    private OrderStatus _status = OrderStatus.Pending;
    private readonly SignalSink _sink;

    public void PlaceOrder(Order order)
    {
        _currentOrder = order;
        _status = OrderStatus.Placed;

        // ✅ Event notification
        _sink.Raise("order.placed");
    }

    // State queries
    public Order? GetCurrentOrder() => _currentOrder;
    public OrderStatus GetStatus() => _status;
    public decimal GetTotal() => _currentOrder?.Total ?? 0;
}

// Multiple listeners query for what they need
_sink.SignalRaised += signal =>
{
    if (signal.Signal == "order.placed")
    {
        var order = orderAtom.GetCurrentOrder();
        if (order != null)
        {
            // Each listener gets current state
            SendConfirmationEmail(order);
        }
    }
};

_sink.SignalRaised += signal =>
{
    if (signal.Signal == "order.placed")
    {
        var total = orderAtom.GetTotal();
        _metrics.Record("order.revenue", total);
    }
};
```

## When Context IS the State (Rare Exceptions)

Sometimes the signal itself IS meaningful context that doesn't change:

### System Lifecycle Events

```csharp
// These are constants - context IS the message
_sink.Raise("system.shutdown");
_sink.Raise("system.startup");
_sink.Raise("maintenance.mode");
```

### Infrastructure Commands (WindowSizeAtom Pattern)

```csharp
// Commands to infrastructure - transient, not business state
_sink.Raise("window.size.set:500");
_sink.Raise("logging.level:debug");
_sink.Raise("rate.limit:1000");
```

**Why this is acceptable:**
1. Infrastructure configuration, not domain state
2. Commands are **imperative**, not events
3. No "atom" to query - the signal IS the command
4. Transient - not persisted state

### Constant Identifiers

```csharp
// If ID is immutable and sufficient
_sink.Raise("user.login:user123");

// Listener can use ID to query elsewhere
_sink.SignalRaised += signal =>
{
    if (SignalCommandMatch.TryParse(signal.Signal, "user.login", out var match))
    {
        var userId = match.Payload;
        // Use ID to query user service
        var user = await _userService.GetUser(userId);
    }
};
```

**But even here, prefer:**
```csharp
// Better: Keep user ID in atom
public class SessionAtom
{
    private string _currentUserId = "";

    public void Login(string userId)
    {
        _currentUserId = userId;
        _sink.Raise("user.login");
    }

    public string GetCurrentUserId() => _currentUserId;
}
```

## Pattern Decision Tree

```
What kind of signal is this?
│
├─ Infrastructure command (rare)?
│  └─ Yes → Model 3: Command
│            _sink.Raise("window.size.set:500")
│            No atom query - command is imperative
│
├─ System lifecycle?
│  └─ Yes → Model 1: Pure notification
│            _sink.Raise("system.shutdown")
│            No state needed
│
└─ Domain event?
   │
   ├─ Is there a fast-path optimization opportunity?
   │  │
   │  ├─ Yes → Model 2: Context + Hint (double-safe)
   │  │        _sink.Raise($"order.placed:{order.Tier}")
   │  │        Listener uses hint for fast routing
   │  │        Then queries atom for full state
   │  │        Examples:
   │  │        - Routing by priority/tier
   │  │        - Filtering by type/category
   │  │        - Approximate thresholds
   │  │        - Expensive atom queries
   │  │
   │  └─ No → Model 1: Pure notification (default)
   │           _sink.Raise("order.placed")
   │           Listener queries orderAtom.GetOrder()
   │           Use this when:
   │           - No fast-path needed
   │           - All listeners need full state
   │           - State is complex/large
   │           - Maximum safety preferred
   │
   └─ Remember: Atom is ALWAYS source of truth
      Hints are optimizations, not replacements
```

## Benefits of This Pattern

### 1. No Race Conditions
```csharp
// State might change between signal and processing
_currentValue = 100;
_sink.Raise("value.changed"); // Signal raised

// ... time passes, value changes ...
_currentValue = 200;

// Listener processes signal
_sink.SignalRaised += signal =>
{
    if (signal.Signal == "value.changed")
    {
        // Gets CURRENT value (200), not stale value (100)
        var value = atom.GetValue(); // ✅ 200 - correct!
    }
};
```

### 2. Single Source of Truth
```csharp
// Atom is the truth
public class CounterAtom
{
    private int _count = 0;

    public void Increment()
    {
        _count++;
        _sink.Raise("counter.incremented");
    }

    public int GetCount() => _count; // ONE place to get count
}

// Not scattered across signal payloads
```

### 3. Decoupled Listeners
```csharp
// Listeners query for what THEY need
_sink.SignalRaised += signal =>
{
    if (signal.Signal == "order.placed")
    {
        // Email system only needs customer info
        var email = orderAtom.GetCustomerEmail();
        SendEmail(email);
    }
};

_sink.SignalRaised += signal =>
{
    if (signal.Signal == "order.placed")
    {
        // Analytics only needs total
        var total = orderAtom.GetTotal();
        RecordRevenue(total);
    }
};

// No need for signal to contain ALL data for ALL listeners
```

### 4. Non-Blocking Coordination
```csharp
// Fast, lock-free signal emission
public void ProcessItem(Item item)
{
    UpdateState(item); // With locks
    _sink.Raise("item.processed"); // No locks, fast!
}

// Listeners coordinate asynchronously
_sink.SignalRaised += signal =>
{
    if (signal.Signal == "item.processed")
    {
        // Process when ready, query for current state
        var item = atom.GetCurrentItem();
    }
};
```

## Anti-Patterns to Avoid

### ❌ State Duplication
```csharp
// DON'T: State in signal AND atom
_sink.Raise($"order.placed:{order.Id}:{order.Total}:{order.Customer}");
```

### ❌ Signal as Data Transfer
```csharp
// DON'T: Treating signals like messages
_sink.Raise($"data:{JsonSerializer.Serialize(largeObject)}");
```

### ❌ Coupling Signal Format
```csharp
// DON'T: All listeners must parse same format
var parts = signal.Signal.Split(':');
var orderId = parts[1];
var total = decimal.Parse(parts[2]);
```

### ❌ Stale State Assumptions
```csharp
// DON'T: Assume signal payload is current
_sink.Raise($"count:{_count}");
// ... _count changes ...
// Listener gets old value from signal
```

## Summary

**The Ephemeral Signals Pattern - Three Models:**

### Model 1: Pure Notification (Default)
```csharp
_sink.Raise("order.placed");
var order = orderAtom.GetOrder(); // Query for truth
```
- ✅ Safest - no stale data possible
- ✅ Simple - no parsing needed
- ✅ Flexible - listeners get what they need from atom
- 📌 **Use when:** No fast-path optimization needed

### Model 2: Context + Hint (Double-Safe)
```csharp
_sink.Raise($"order.placed:{order.Tier}"); // Hint
var order = orderAtom.GetOrder(); // Verify truth
```
- ✅ Fast path - quick decisions from hint
- ✅ Safe - atom query for authoritative state
- ✅ Flexible - listeners can ignore hints
- 📌 **Use when:** Fast-path routing/filtering beneficial
- ⚠️ **Always:** Verify with atom for critical operations

### Model 3: Command (Infrastructure Only)
```csharp
_sink.Raise("window.size.set:500"); // Imperative
```
- ✅ Appropriate for infrastructure control
- ✅ Transient configuration, not business state
- ⚠️ **Rare:** Commands, not events

**Core Principles:**
- 🎯 Signals are primarily "hey, look at me!" notifications
- 🎯 Atoms are ALWAYS the source of truth
- 🎯 Hints are optimizations, not replacements
- 🎯 Non-locking, race-free coordination
- 🎯 Double-safe when using hints

**Decision Guide:**
- **Default to Model 1** (pure notification)
- **Use Model 2** when you have a clear fast-path benefit
- **Use Model 3** only for infrastructure commands
- **Always** query atom for critical operations
- **Remember** stale hints are okay if you verify

**Red Flags:**
- ❌ Parsing complex JSON from signals
- ❌ Trusting signal data without atom verification
- ❌ Storing large objects in signals
- ❌ Using signals as message queue replacement

**Ask Yourself:**
- "Am I notifying, or commanding?" → Usually notifying (Model 1/2)
- "Could a hint speed up routing?" → Consider Model 2
- "Am I verifying with atom?" → Must be yes for critical logic
- "Is this infrastructure control?" → Rare Model 3 case

---

## See Also

- `WindowSizeAtom` - Example of command pattern exception
- `SignalCommandMatch` - Helper for rare command parsing
- `CLAUDE.md` - Architecture overview
- `CHANGELOG.md` - Recent signal-related improvements
