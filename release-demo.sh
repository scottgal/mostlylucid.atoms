#!/bin/bash

# Release script for Ephemeral Signals Demo
# Creates a git tag and pushes to trigger GitHub Actions release

set -e

echo "🚀 Ephemeral Signals Demo Release Script"
echo ""

# Check if version is provided
if [ -z "$1" ]; then
    echo "Usage: ./release-demo.sh <version>"
    echo "Example: ./release-demo.sh 1.0.0"
    exit 1
fi

VERSION=$1
TAG="demo-v$VERSION"

echo "📋 Release Information:"
echo "  Version: $VERSION"
echo "  Tag: $TAG"
echo ""

# Check if tag already exists
if git rev-parse "$TAG" >/dev/null 2>&1; then
    echo "❌ Error: Tag $TAG already exists"
    exit 1
fi

# Get last demo tag
LAST_TAG=$(git tag -l "demo-v*" --sort=-v:refname | head -n 1)
if [ -z "$LAST_TAG" ]; then
    echo "📝 First release - no previous tags"
    LAST_REF=$(git rev-list --max-parents=0 HEAD)
else
    echo "📝 Changes since $LAST_TAG:"
    LAST_REF=$LAST_TAG
fi
echo ""

# Show changes
echo "Recent commits:"
git log $LAST_REF..HEAD --pretty=format:"  - %s (%h)" --no-merges -- \
    mostlylucid.ephemeral/demos/ \
    mostlylucid.ephemeral/src/mostlylucid.ephemeral.atoms.*/ \
    mostlylucid.ephemeral/src/mostlylucid.ephemeral/ | head -n 10
echo ""
echo ""

# Confirm
read -p "❓ Create and push tag $TAG? (y/N) " -n 1 -r
echo ""

if [[ ! $REPLY =~ ^[Yy]$ ]]; then
    echo "❌ Cancelled"
    exit 1
fi

# Create annotated tag
echo "🏷️  Creating tag..."
git tag -a "$TAG" -m "Ephemeral Signals Demo v$VERSION"

# Push tag
echo "📤 Pushing tag to GitHub..."
git push origin "$TAG"

echo ""
echo "✅ Done! GitHub Actions will now:"
echo "  1. Build executables for 6 platforms"
echo "  2. Generate changelog"
echo "  3. Create GitHub release"
echo ""
echo "🔗 Watch progress: https://github.com/scottgal/mostlylucid.atoms/actions"
echo "📦 View releases: https://github.com/scottgal/mostlylucid.atoms/releases"
