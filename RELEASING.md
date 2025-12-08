# Releasing the Ephemeral Signals Demo

Quick guide for creating new demo releases.

## Prerequisites

- Git access to the repository
- Commits pushed to main branch
- Version number decided (e.g., `1.0.0`)

## Release Process

### 1. Choose Your Method

#### Option A: Automated Script (Recommended)

**Linux/macOS:**
```bash
./release-demo.sh 1.0.0
```

**Windows PowerShell:**
```powershell
.\release-demo.ps1 -Version 1.0.0
```

The script will:
- Show recent changes since last release
- Create annotated tag `demo-v1.0.0`
- Push to GitHub
- Trigger automated build

#### Option B: Manual Git Tag

```bash
# Create annotated tag
git tag -a demo-v1.0.0 -m "Ephemeral Signals Demo v1.0.0"

# Push tag to GitHub
git push origin demo-v1.0.0
```

#### Option C: GitHub Actions UI

1. Navigate to https://github.com/scottgal/mostlylucid.atoms/actions
2. Select "Build and Release Ephemeral Signals Demo"
3. Click "Run workflow"
4. Enter version: `1.0.0`
5. Click "Run workflow"

### 2. Monitor Build

Watch the progress at:
https://github.com/scottgal/mostlylucid.atoms/actions

The workflow will:
1. **Build** (5-10 minutes) - Compile for all 6 platforms in parallel
2. **Create Release** (1-2 minutes) - Generate changelog and publish

### 3. Verify Release

Check the release page:
https://github.com/scottgal/mostlylucid.atoms/releases

Verify:
- ✅ All 6 executables are present
- ✅ Changelog shows recent commits
- ✅ File sizes are reasonable (~70-90 MB each)
- ✅ Tag matches version

### 4. Test Downloads (Optional)

Download and test one or more platforms:

**Windows:**
```cmd
ephemeral-demo-win-x64.exe
```

**Linux/macOS:**
```bash
chmod +x ephemeral-demo-linux-x64
./ephemeral-demo-linux-x64
```

## Versioning

We use semantic versioning for demo releases:

- **Major** (1.0.0 → 2.0.0) - Breaking changes, major new features
- **Minor** (1.0.0 → 1.1.0) - New demos, significant features
- **Patch** (1.0.0 → 1.0.1) - Bug fixes, minor improvements

### Version History

| Version | Description |
|---------|-------------|
| 1.0.0 | Initial release with 10 demos + benchmarks |

## What Gets Released

### Source Files Tracked

Changes in these directories trigger inclusion in changelog:
- `mostlylucid.ephemeral/demos/`
- `mostlylucid.ephemeral/src/mostlylucid.ephemeral.atoms.*/`
- `mostlylucid.ephemeral/src/mostlylucid.ephemeral/`

### Build Outputs

Each release produces 6 single-file executables:

| Runtime | Size | Description |
|---------|------|-------------|
| win-x64 | ~85 MB | Windows 10+ (x64) |
| win-arm64 | ~85 MB | Windows 11 ARM64 |
| linux-x64 | ~75 MB | Linux (x64) |
| linux-arm64 | ~75 MB | Linux ARM64 (Raspberry Pi, etc.) |
| osx-x64 | ~75 MB | macOS Intel |
| osx-arm64 | ~75 MB | macOS Apple Silicon (M1/M2/M3) |

All executables are:
- Self-contained (no .NET runtime required)
- Single-file (all dependencies bundled)
- Release mode (optimized)
- No debug symbols (smaller size)

## Troubleshooting

### "Tag already exists"

```bash
# List existing tags
git tag -l "demo-v*"

# Delete local tag if needed
git tag -d demo-v1.0.0

# Delete remote tag (careful!)
git push origin :refs/tags/demo-v1.0.0
```

### "Build failed"

1. Check the [Actions tab](https://github.com/scottgal/mostlylucid.atoms/actions)
2. Click the failed run
3. Review error logs
4. Common issues:
   - .NET SDK version mismatch
   - Project doesn't build locally
   - Missing dependencies

Fix the issue, then:
```bash
# Delete the bad tag
git tag -d demo-v1.0.0
git push origin :refs/tags/demo-v1.0.0

# Create new tag
git tag -a demo-v1.0.0 -m "Ephemeral Signals Demo v1.0.0"
git push origin demo-v1.0.0
```

### "Release created but no artifacts"

- Wait a few minutes - uploads can take time
- Check if all 6 build jobs succeeded
- Look for upload errors in individual job logs

### Local Build Test

Test the build locally before releasing:

```bash
cd mostlylucid.ephemeral/demos/mostlylucid.ephemeral.demo

# Build for your platform
dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o out

# Test the executable
./out/mostlylucid.ephemeral.demo
```

## Release Checklist

Before releasing:

- [ ] All tests pass: `dotnet test`
- [ ] Demo builds locally: `dotnet build -c Release`
- [ ] README.md updated with changes
- [ ] CHANGELOG.md updated (optional - workflow generates one)
- [ ] Version number decided
- [ ] Main branch is up to date

After releasing:

- [ ] GitHub Release published successfully
- [ ] All 6 platform executables present
- [ ] Download and test at least one platform
- [ ] Announce in README/social media if desired

## Automation Details

See [.github/workflows/README.md](.github/workflows/README.md) for:
- Workflow configuration
- Build matrix details
- Troubleshooting build issues
- Customization options

## Questions?

- 📖 [Workflow Documentation](.github/workflows/README.md)
- 📦 [Releases Page](https://github.com/scottgal/mostlylucid.atoms/releases)
- 🐛 [Report Issues](https://github.com/scottgal/mostlylucid.atoms/issues)
