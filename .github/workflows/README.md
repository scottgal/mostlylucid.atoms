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
