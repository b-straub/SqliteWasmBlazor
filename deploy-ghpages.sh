#!/bin/bash
set -e

echo "Building SqliteWasmBlazor Demo for GitHub Pages..."

# Clean previous builds
echo "Cleaning previous builds..."
dotnet clean -c Release --nologo
rm -rf SqliteWasmBlazor.Demo/bin/Release SqliteWasmBlazor.Demo/obj/Release

# Build in temp directory
TEMP_DIR=$(mktemp -d)
echo "Building in: $TEMP_DIR"

# Publish the demo app (force rebuild)
dotnet publish SqliteWasmBlazor.Demo/SqliteWasmBlazor.Demo.csproj -c Release -o "$TEMP_DIR/build" --nologo /p:UseSharedCompilation=false

# Navigate to published wwwroot
cd "$TEMP_DIR/build/wwwroot"

# Base paths are already set by MSBuild task from appsettings.Production.json

# Add .nojekyll
touch .nojekyll

# Copy index.html to 404.html
cp index.html 404.html

echo ""
echo "✅ Build complete!"
echo ""

# List what we're about to deploy
echo "Files to deploy:"
find . -type f | head -20
echo ""

# Initialize new git repo in this directory
git init -b main
git add -A
git commit -m "Deploy to GitHub Pages - $(date '+%Y-%m-%d %H:%M:%S')"

# Show what was committed
echo "Committed files:"
git ls-files | head -30

# Detect the remote name
cd - > /dev/null
REMOTE=$(git remote | grep -i "github\|origin" | head -1)
if [ -z "$REMOTE" ]; then
  REMOTE="origin"
fi

REPO_URL=$(git remote get-url "$REMOTE")

# Push to gh-pages
cd "$TEMP_DIR/build/wwwroot"
git push --force "$REPO_URL" HEAD:gh-pages

echo ""
echo "✅ Deployed to GitHub Pages!"
echo "🌐 Your site will be available at: https://b-straub.github.io/SqliteWasmBlazor/"
echo ""

# Clean up
cd - > /dev/null
rm -rf "$TEMP_DIR"

echo "Cleaned up temporary files"
