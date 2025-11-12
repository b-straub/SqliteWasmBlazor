#!/bin/bash
set -e

echo "Building SqliteWasmBlazor Demo for GitHub Pages..."

# Clean previous build
rm -rf dist

# Publish the demo app
dotnet publish SqliteWasmBlazor.Demo/SqliteWasmBlazor.Demo.csproj -c Release -o dist --nologo

# The published files are in dist/wwwroot
cd dist/wwwroot

# Fix base href in index.html for GitHub Pages subdirectory
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

# Add .nojekyll to prevent Jekyll processing
touch .nojekyll

# Copy index.html to 404.html for SPA routing
cp index.html 404.html

echo ""
echo "✅ Build complete! Files are in dist/wwwroot/"
echo ""
echo "To deploy to GitHub Pages:"
echo "1. git checkout -b gh-pages"
echo "2. rm -rf * (except dist)"
echo "3. mv dist/wwwroot/* ."
echo "4. git add ."
echo "5. git commit -m 'Deploy to GitHub Pages'"
echo "6. git push origin gh-pages --force"
echo ""
echo "Or run: ./deploy-ghpages.sh"
