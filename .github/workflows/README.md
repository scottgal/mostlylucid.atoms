# GitHub Actions Workflows

## Demo Release Workflow

Automatically builds and releases the Ephemeral Signals Demo as single-file executables for multiple platforms.

### Platforms

| Platform | Architecture | Output |
|----------|-------------|--------|
| Windows | x64 | `ephemeral-demo-win-x64.exe` |
| Windows | ARM64 | `ephemeral-demo-win-arm64.exe` |
| Linux | x64 | `ephemeral-demo-linux-x64` |
| Linux | ARM64 | `ephemeral-demo-linux-arm64` |
| macOS | x64 (Intel) | `ephemeral-demo-macos-x64` |
| macOS | ARM64 (Apple Silicon) | `ephemeral-demo-macos-arm64` |

### Triggering a Release

#### Method 1: Git Tag (Automatic)

```bash
git tag demo-v1.0.0
git push origin demo-v1.0.0
```

#### Method 2: Manual Dispatch (GitHub UI)

1. Go to **Actions** tab
2. Select **Build and Release Ephemeral Signals Demo**
3. Click **Run workflow**
4. Enter version (e.g., `1.0.0`)
5. Click **Run workflow**

### What It Does

1. **Builds** single-file executables for all 6 platforms
2. **Generates** changelog from commits since last release
3. **Creates** GitHub Release with:
   - Version tag (e.g., `demo-v1.0.0`)
   - Release notes with changes
   - All 6 platform executables
   - Installation instructions

### Changelog Generation

The workflow automatically includes:
- All commits since last `demo-v*` tag
- Changes in:
  - `mostlylucid.ephemeral/demos/`
  - `mostlylucid.ephemeral/src/mostlylucid.ephemeral.atoms.*/`
  - `mostlylucid.ephemeral/src/mostlylucid.ephemeral/`

### Build Configuration

Executables are:
- **Self-contained** - No .NET runtime required
- **Single-file** - Everything bundled into one executable
- **Not trimmed** - Full feature set (for demo purposes)
- **Release mode** - Optimized performance
- **No debug symbols** - Smaller file size

### File Sizes (Approximate)

- Windows: ~80-90 MB
- Linux: ~70-80 MB
- macOS: ~70-80 MB

### Testing Locally

Build for your platform:

```bash
# Windows x64
dotnet publish mostlylucid.ephemeral/demos/mostlylucid.ephemeral.demo/mostlylucid.ephemeral.demo.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -o out

# Linux x64
dotnet publish mostlylucid.ephemeral/demos/mostlylucid.ephemeral.demo/mostlylucid.ephemeral.demo.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -o out

# macOS ARM64 (Apple Silicon)
dotnet publish mostlylucid.ephemeral/demos/mostlylucid.ephemeral.demo/mostlylucid.ephemeral.demo.csproj \
  -c Release \
  -r osx-arm64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -o out
```

### Troubleshooting

**"No .NET 10 SDK found"**
- Ensure .NET 10 SDK is installed
- Update workflow if using different version

**"Permission denied" on Linux/macOS**
- Run: `chmod +x ephemeral-demo-linux-x64`
- Or download from releases (already has +x)

**Large file sizes**
- Single-file executables include full runtime
- This is expected for self-contained apps
- Could enable trimming, but may break reflection-based features

### Future Improvements

- [ ] Enable ReadyToRun compilation for faster startup
- [ ] Add compression to reduce file size
- [ ] Include SHA256 checksums in release
- [ ] Add automatic version bumping
- [ ] Create installer packages (MSI, DEB, PKG)

---

## Performance Regression Workflow

Automatically runs benchmarks and detects performance regressions on every commit to main and PR.

### What It Does

1. **Runs benchmarks** using BenchmarkDotNet
2. **Exports results** to MD, CSV, HTML, JSON
3. **Compares** with baseline from previous runs
4. **Comments on PRs** with performance results
5. **Stores baseline** for future comparisons (365 days retention)

### Triggering

Runs automatically on:
- Push to `main` branch (updates baseline)
- Pull requests (compares against baseline)
- Manual dispatch via Actions tab

Triggered by changes to:
- `mostlylucid.ephemeral/src/**`
- `mostlylucid.ephemeral/demos/mostlylucid.ephemeral.demo/**`
- `.github/workflows/benchmark-regression.yml`

### Running Locally

```bash
cd mostlylucid.ephemeral/demos/mostlylucid.ephemeral.demo

# Run all benchmarks
dotnet run -c Release -- --benchmark

# Results exported to:
# - BenchmarkDotNet.Artifacts/results/*-report-github.md
# - BenchmarkDotNet.Artifacts/results/*.csv
# - BenchmarkDotNet.Artifacts/results/*.json
# - BenchmarkDotNet.Artifacts/results/*.html
```

### Baseline Management

- **Baseline** = Last successful run on `main` branch
- **Retention**: 365 days for baseline, 90 days for individual runs
- **Location**: Stored as GitHub Actions artifacts
- Baseline updated automatically on merge to `main`

### Regression Detection

The workflow:
- ✅ Compares current results with baseline
- ✅ Posts summary to PR comments
- ✅ Stores full results in artifacts
- ⚠️ Manual review required for significant changes

### Benchmark Optimizations

The benchmarks are optimized for minimal allocations:
- `BenchmarkTestAtom` - Zero-delay, allocation-free listener
- `BenchmarkChainAtom` - Synchronous re-emission, no async overhead
- Separate iteration setup to exclude initialization costs
- Multiple exporters for cross-tool compatibility

### Viewing Results

**GitHub UI:**
1. Go to Actions tab
2. Click workflow run
3. Download artifacts
4. View markdown report

**Command line:**
```bash
gh run list --workflow=benchmark-regression.yml
gh run download <run-id>
cat benchmark-results/*-report-github.md
```

### Benchmark Metrics

Each benchmark reports:
- **Mean** - Average execution time
- **StdDev** - Standard deviation
- **Gen0/Gen1** - GC collections per 1000 operations
- **Allocated** - Memory allocated per operation

Key metrics to watch:
- Signal raising: Target <100ns
- Pattern matching: Target <10ns (zero allocation)
- State queries: Target <5ns
- Chain propagation: Target <1ms for 100 chains
