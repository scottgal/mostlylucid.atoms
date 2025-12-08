# Ephemeral Signals Demo - Complete Feature List

Interactive demonstration application showcasing the Ephemeral Signals pattern.

## 🎯 Core Patterns (Demos 1-5)

### 1. Pure Notification Pattern
**File Save Simulation**

Demonstrates the fundamental pattern: signals are notifications, state lives in atoms.

**Key Concepts:**
- Signal: `file.saved` (no payload)
- State queries: `GetProcessedCount()`, `GetLastProcessedTime()`
- Multiple listeners querying same atom
- Separation of notification from state

**Visual Elements:**
- ✓ Success markers
- 📋 Audit log timestamps
- File count tracking

---

### 2. Context + Hint Pattern (Double-Safe)
**Order Processing**

Shows optimization via hints while maintaining safety through verification.

**Key Concepts:**
- Signal: `order.placed:ORD-123` (hint included)
- Fast-path: Use hint for quick response
- Safety: Always verify with atom
- Best of both worlds: performance + correctness

**Visual Elements:**
- 📧 Email notifications (using hint)
- ✓ Verification confirmations
- Order count tracking

---

### 3. Command Pattern (Exception)
**WindowSizeAtom Infrastructure Control**

Demonstrates the command pattern exception for infrastructure.

**Key Concepts:**
- Commands: `window.size.set:500`, `window.time.set:30s`
- Infrastructure control (not domain events)
- Immediate effect on SignalSink
- Dynamic capacity/retention adjustment

**Visual Elements:**
- Real-time capacity display
- Retention time updates
- Command execution feedback

---

### 4. Complex Multi-Step Pipeline
**Rate Limiting with Signal Chains**

Shows a complete system with multiple atoms coordinating.

**Key Concepts:**
- Signal chain: `api.request` → `request.validated` → `request.processed` → `request.complete`
- Rate limiting (1.5/s, burst 3)
- Pipeline coordination
- Multi-atom state queries

**Visual Elements:**
- ✓ Allowed requests (green)
- ✗ Rate limited requests (red)
- Pipeline stage counts
- Rate limiter statistics

---

### 5. Signal Chain Demo
**Cascading Atoms (A→B→C)**

Pure demonstration of signal propagation through atoms.

**Key Concepts:**
- Chain: `input` → `stepA.complete` → `stepB.complete` → `stepC.complete`
- Atom-to-atom communication
- Processing pipelines
- Completion tracking

**Visual Elements:**
- 🎉 Chain completion marker
- Per-atom processing counts
- Signal flow visualization

---

## 🔧 Advanced Patterns (Demos 6-9)

### 6. Circuit Breaker Pattern
**Failure Detection and Recovery**

Real-world resilience pattern implementation.

**Key Concepts:**
- States: CLOSED → OPEN → HALF-OPEN → CLOSED
- Failure threshold: 3 failures
- Cooldown period: 3 seconds
- Automatic recovery testing
- 70% success rate simulation

**Visual Elements:**
- 🔴 Circuit OPEN (red)
- 🔄 Circuit HALF-OPEN (yellow)
- ✅ Circuit CLOSED (green)
- ⛔ Rejected calls
- Failure count tracking

**Business Value:**
- Prevents cascading failures
- Automatic recovery
- System stability protection
- Fast failure detection

---

### 7. Backpressure Demo
**Queue Overflow Protection**

Producer/consumer pattern with flow control.

**Key Concepts:**
- Producer: Fast (100ms)
- Consumer: Slow (200ms)
- Queue limit: 5 items
- Backpressure activation when full
- Automatic release when draining

**Visual Elements:**
- 📦 Production markers
- ✓ Consumption markers
- 🛑 Blocked production (red)
- ⚠️ Backpressure active (yellow)
- ✅ Backpressure released (green)
- Queue size tracking

**Business Value:**
- Prevents memory exhaustion
- Graceful degradation
- System stability
- Flow control

---

### 8. Metrics & Monitoring
**Real-time Statistics Dashboard**

Live metrics aggregation and visualization.

**Key Concepts:**
- Request counting (total, success, failure)
- Success rate calculation
- Latency percentiles (P50, P95, P99)
- Real-time dashboard updates
- 85% success rate simulation

**Visual Elements:**
- 📊 Live statistics table
- Total requests counter
- Success rate percentage (green)
- Failure count (red)
- Latency percentiles (yellow/red)

**Metrics Tracked:**
- Total Requests
- Success Rate (%)
- Successes / Failures
- Latency P50 (median)
- Latency P95 (95th percentile)
- Latency P99 (99th percentile)

**Business Value:**
- Real-time observability
- Performance monitoring
- SLA tracking
- Anomaly detection

---

### 9. Dynamic Rate Adjustment
**Adaptive Throttling**

Self-adjusting rate limits based on system load.

**Key Concepts:**
- Load monitoring (10%-95% simulation)
- Automatic rate adjustment:
  - Low load (<50%): 10/s
  - Medium load (50-75%): 5/s
  - High load (>75%): 2/s
- Real-time rate changes
- Load fluctuation simulation

**Visual Elements:**
- 📊 System load percentage
  - Green: Low load
  - Yellow: Medium load
  - Red: High load
- ⚙️ Rate adjustment notifications
- ✓ Allowed requests
- ✗ Rate limited requests

**Business Value:**
- Adaptive capacity
- Automatic load shedding
- Resource protection
- Performance optimization

---

## 👁️ Observability (Demo 10)

### 10. Live Signal Viewer
**Real-time Signal Visualization**

Watch the signal system in action with filtering and color coding.

**Key Concepts:**
- Real-time signal capture
- Pattern-based filtering
- Color-coded log levels
- Window dump functionality
- Statistics tracking

**Visual Elements:**
- Color-coded signals:
  - 🔴 Red: error, critical
  - 🟡 Yellow: warning
  - 🔵 Blue: window signals
  - 🟣 Magenta: rate signals
  - 🟢 Green: success, complete
  - ⚪ White: info
  - ⚫ Grey: debug, trace
- Timestamp display (HH:mm:ss.fff)
- Sequence numbers
- Window statistics

**Features:**
- AutoOutput mode
- Include/Exclude patterns
- Sample rate control
- Window size limiting
- Statistics: received, logged, filtered

---

## ⚡ Performance Analysis (Benchmark Mode)

### BenchmarkDotNet Integration

Comprehensive performance testing with memory diagnostics.

**Benchmarks:**

1. **Signal Raise (no listeners)**
   - Pure signal overhead
   - 1000 iterations

2. **Signal Raise (1 listener)**
   - Signal + listener invocation
   - 1000 iterations

3. **Signal Pattern Matching**
   - Glob-style pattern matching
   - 4000 matches (4 signals × 1000 iterations)

4. **SignalCommandMatch Parsing**
   - Command extraction
   - 3000 parses (3 commands × 1000 iterations)

5. **Rate Limiter Acquire**
   - Token bucket acquisition
   - 100 async operations

6. **TestAtom State Query**
   - State accessor performance
   - 10000 queries (4 methods × 2500)

7. **WindowSizeAtom Command**
   - Command processing
   - 300 commands (3 types × 100)

8. **Signal Chain (3 atoms)**
   - End-to-end propagation
   - 100 complete chains

9. **Concurrent Signal Raising**
   - Multi-threaded stress test
   - 10 threads × 100 signals

**Metrics Reported:**
- Mean execution time
- Standard deviation
- Memory allocations:
  - Gen 0 collections
  - Gen 1 collections
  - Gen 2 collections
  - Allocated bytes
- Min/Max values
- Outliers

**IMPORTANT:** Must run in Release mode:
```bash
dotnet run -c Release
```

---

## 🎨 Visual Design

### Color Scheme
- **Cyan**: Headers, important info
- **Yellow**: Warnings, key takeaways
- **Green**: Success, completion
- **Red**: Errors, failures, blocks
- **Blue**: Info, system messages
- **Magenta**: Special categories
- **Grey**: Timestamps, secondary info

### UI Elements
- **Rules**: Section separators
- **Tables**: Structured data (metrics)
- **Spinners**: Progress indicators
- **Markup**: Rich text formatting
- **Status**: Long-running operations

---

## 📦 Components Used

### Custom Atoms
- **TestAtom** - Configurable simulation atom
- **ConsoleSignalLoggerAtom** - Signal observation and logging

### Library Atoms
- **WindowSizeAtom** - Dynamic capacity/retention
- **RateLimitAtom** - Token bucket rate limiting

### Infrastructure
- **SignalSink** - Central signal hub
- **SignalEvent** - Signal payload
- **StringPatternMatcher** - Glob matching
- **SignalCommandMatch** - Command parsing

---

## 🎓 Learning Outcomes

After running all demos, users understand:

1. **Pattern Philosophy**
   - "Hey, look at me!" notifications
   - State lives in atoms, not signals
   - Three models: Pure, Hint, Command

2. **Practical Applications**
   - Circuit breakers
   - Backpressure handling
   - Metrics aggregation
   - Adaptive systems

3. **Performance Characteristics**
   - Low allocation overhead
   - Concurrent signal handling
   - Pattern matching costs
   - State query performance

4. **System Design**
   - Signal-driven architecture
   - Atom coordination
   - Flow control
   - Observability

---

## 🚀 Technical Details

### Requirements
- .NET 10.0 SDK (for source)
- Or: Pre-built executable (no runtime needed)
- Terminal with ANSI color support

### Performance (Actual Benchmark Results)
- Signal raising (no listeners): **~46 µs** (114 KB allocated)
- Signal raising (1 listener): **~1.6 ms** (1.5 MB allocated)
- Pattern matching: **~22 µs** (zero allocations)
- Command parsing: **~196 µs** (816 KB allocated)
- State queries: **~50 µs** (640 KB allocated for 10k queries)
- WindowSizeAtom commands: **~46 µs** (106 KB allocated)
- Signal chain (3 atoms): **~134 ms** for 100 complete chains
- Concurrent signal raising: **~202 µs** (10 threads × 100 signals)

### Memory
- Bounded signal windows
- Automatic cleanup
- No memory leaks
- Configurable limits

### Concurrency
- Thread-safe signal raising
- Concurrent listener execution
- Lock-free where possible
- Stress tested (10k+ ops)

---

## 📖 Documentation

- **Main Guide**: [README.md](README.md)
- **Pattern Philosophy**: [../../SIGNALS_PATTERN.md](../../SIGNALS_PATTERN.md)
- **Changelog**: [../../CHANGELOG.md](../../CHANGELOG.md)
- **Release Notes**: [../../../RELEASES.md](../../../RELEASES.md)

---

## 🎯 Use Cases Demonstrated

1. **Event-Driven Systems** - Pure notification pattern
2. **Performance Optimization** - Context + hint pattern
3. **Infrastructure Control** - Command pattern
4. **System Resilience** - Circuit breaker
5. **Flow Control** - Backpressure
6. **Observability** - Metrics & monitoring
7. **Adaptive Systems** - Dynamic rate adjustment
8. **Multi-Stage Processing** - Signal chains
9. **Real-Time Monitoring** - Live viewer
10. **Performance Analysis** - Benchmarks

---

## 🎊 Summary

This demo application provides a **complete, hands-on introduction** to the Ephemeral Signals pattern through:
- ✅ 10 interactive scenarios
- ✅ Real-world resilience patterns
- ✅ Live visualizations
- ✅ Performance benchmarks
- ✅ Beautiful terminal UI
- ✅ Self-contained executables

Perfect for learning, teaching, and demonstrating the power of signal-driven architecture!
