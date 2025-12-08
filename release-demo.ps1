# Release script for Ephemeral Signals Demo (PowerShell)
# Creates a git tag and pushes to trigger GitHub Actions release

param(
    [Parameter(Mandatory=$true)]
    [string]$Version
)

$ErrorActionPreference = "Stop"

Write-Host "🚀 Ephemeral Signals Demo Release Script" -ForegroundColor Cyan
Write-Host ""

$TAG = "demo-v$Version"

Write-Host "📋 Release Information:" -ForegroundColor Yellow
Write-Host "  Version: $Version"
Write-Host "  Tag: $TAG"
Write-Host ""

# Check if tag already exists
$tagExists = git rev-parse $TAG 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Host "❌ Error: Tag $TAG already exists" -ForegroundColor Red
    exit 1
}

# Get last demo tag
$lastTag = git tag -l "demo-v*" --sort=-v:refname | Select-Object -First 1
if (-not $lastTag) {
    Write-Host "📝 First release - no previous tags" -ForegroundColor Gray
    $lastRef = git rev-list --max-parents=0 HEAD
} else {
    Write-Host "📝 Changes since $lastTag:" -ForegroundColor Gray
    $lastRef = $lastTag
}
Write-Host ""

# Show changes
Write-Host "Recent commits:" -ForegroundColor Yellow
$commits = git log "$lastRef..HEAD" --pretty=format:"  - %s (%h)" --no-merges -- `
    mostlylucid.ephemeral/demos/ `
    mostlylucid.ephemeral/src/mostlylucid.ephemeral.atoms.*/ `
    mostlylucid.ephemeral/src/mostlylucid.ephemeral/ | Select-Object -First 10

$commits | ForEach-Object { Write-Host $_ -ForegroundColor Gray }
Write-Host ""
Write-Host ""

# Confirm
$confirmation = Read-Host "❓ Create and push tag $TAG? (y/N)"
if ($confirmation -ne 'y' -and $confirmation -ne 'Y') {
    Write-Host "❌ Cancelled" -ForegroundColor Red
    exit 1
}

# Create annotated tag
Write-Host "🏷️  Creating tag..." -ForegroundColor Cyan
git tag -a $TAG -m "Ephemeral Signals Demo v$Version"

# Push tag
Write-Host "📤 Pushing tag to GitHub..." -ForegroundColor Cyan
git push origin $TAG

Write-Host ""
Write-Host "✅ Done! GitHub Actions will now:" -ForegroundColor Green
Write-Host "  1. Build executables for 6 platforms"
Write-Host "  2. Generate changelog"
Write-Host "  3. Create GitHub release"
Write-Host ""
Write-Host "🔗 Watch progress: https://github.com/scottgal/mostlylucid.atoms/actions" -ForegroundColor Blue
Write-Host "📦 View releases: https://github.com/scottgal/mostlylucid.atoms/releases" -ForegroundColor Blue
