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

# Fix base href in index.html
sed -i.bak 's|<base href="/" />|<base href="/SqliteWasmBlazor/" />|g' index.html
rm index.html.bak

# Fix service worker base paths
for file in service-worker*.js; do
  if [ -f "$file" ]; then
    sed -i.bak 's|const base = "/";|const base = "/SqliteWasmBlazor/";|g' "$file"
    rm "$file.bak"
    echo "Updated base path in $file"
  fi
done

# Add .nojekyll
touch .nojekyll

# Copy index.html to 404.html
cp index.html 404.html

echo ""
echo "✅ Build complete!"
echo ""

# Initialize new git repo in this directory
git init
git add .
git commit -m "Deploy to GitHub Pages - $(date '+%Y-%m-%d %H:%M:%S')"

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
