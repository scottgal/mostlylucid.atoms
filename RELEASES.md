# Ephemeral Signals Demo Releases

Pre-built single-file executables for the interactive demo application.

## Download

Visit the [Releases page](https://github.com/scottgal/mostlylucid.atoms/releases) to download the latest version.

## Available Platforms

| Platform | Architecture | File |
|----------|-------------|------|
| 🪟 Windows | x64 | `ephemeral-demo-win-x64.exe` |
| 🪟 Windows | ARM64 | `ephemeral-demo-win-arm64.exe` |
| 🐧 Linux | x64 | `ephemeral-demo-linux-x64` |
| 🐧 Linux | ARM64 | `ephemeral-demo-linux-arm64` |
| 🍎 macOS | x64 (Intel) | `ephemeral-demo-macos-x64` |
| 🍎 macOS | ARM64 (Apple Silicon) | `ephemeral-demo-macos-arm64` |

## Installation

### Windows

1. Download `ephemeral-demo-win-x64.exe` (or `win-arm64` for ARM)
2. Double-click to run, or from command line:
   ```cmd
   ephemeral-demo-win-x64.exe
   ```

### Linux

1. Download `ephemeral-demo-linux-x64` (or `linux-arm64` for ARM)
2. Make executable and run:
   ```bash
   chmod +x ephemeral-demo-linux-x64
   ./ephemeral-demo-linux-x64
   ```

### macOS

1. Download appropriate version:
   - Intel Macs: `ephemeral-demo-macos-x64`
   - Apple Silicon (M1/M2/M3): `ephemeral-demo-macos-arm64`
2. Make executable and run:
   ```bash
   chmod +x ephemeral-demo-macos-arm64
   ./ephemeral-demo-macos-arm64
   ```

**Note:** On macOS, you may need to allow the app in System Preferences → Security & Privacy if prompted.

## What's Included

Each release includes:

### 10 Interactive Demos
1. **Pure Notification Pattern** - File save with state queries
2. **Context + Hint Pattern** - Order processing (double-safe)
3. **Command Pattern** - WindowSizeAtom infrastructure control
4. **Complex Pipeline** - Multi-step system with rate limiting
5. **Signal Chains** - Cascading atoms (A→B→C)
6. **Circuit Breaker** - Failure detection and recovery
7. **Backpressure** - Queue overflow protection
8. **Metrics & Monitoring** - Real-time statistics dashboard
9. **Dynamic Rate Adjustment** - Adaptive throttling
10. **Live Signal Viewer** - Real-time signal visualization

### BenchmarkDotNet Integration
- Memory diagnostics
- Allocation tracking
- GC pressure analysis
- Performance measurements

## Requirements

- **No .NET Runtime Required** - Executables are self-contained
- Terminal with ANSI color support:
  - Windows: Windows Terminal, ConEmu, or PowerShell 7+
  - Linux: Any modern terminal
  - macOS: Terminal.app, iTerm2

## File Sizes

Executables are approximately:
- Windows: 80-90 MB
- Linux: 70-80 MB
- macOS: 70-80 MB

Size includes the full .NET runtime and all dependencies for a standalone experience.

## Creating a Release (For Maintainers)

### Option 1: Release Script

**Bash (Linux/macOS):**
```bash
./release-demo.sh 1.0.0
```

**PowerShell (Windows):**
```powershell
.\release-demo.ps1 -Version 1.0.0
```

### Option 2: Manual Tag

```bash
git tag demo-v1.0.0
git push origin demo-v1.0.0
```

### Option 3: GitHub UI

1. Go to **Actions** tab
2. Select **Build and Release Ephemeral Signals Demo**
3. Click **Run workflow**
4. Enter version (e.g., `1.0.0`)
5. Click **Run workflow**

## Build Process

Each release:
1. Builds for 6 platforms simultaneously
2. Creates self-contained single-file executables
3. Generates changelog from commits since last release
4. Publishes GitHub Release with all artifacts

See [.github/workflows/README.md](.github/workflows/README.md) for details.

## Support

- 📖 [Demo Documentation](mostlylucid.ephemeral/demos/mostlylucid.ephemeral.demo/README.md)
- 📘 [Signals Pattern Guide](mostlylucid.ephemeral/SIGNALS_PATTERN.md)
- 🐛 [Issue Tracker](https://github.com/scottgal/mostlylucid.atoms/issues)
- 💬 [Discussions](https://github.com/scottgal/mostlylucid.atoms/discussions)

## License

Unlicense - Public Domain

## Changelog

See individual release notes on the [Releases page](https://github.com/scottgal/mostlylucid.atoms/releases).
