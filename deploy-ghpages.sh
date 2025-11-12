#!/bin/bash
set -e

# Build first
./publish-ghpages.sh

echo ""
echo "Deploying to GitHub Pages..."

# Save current branch
CURRENT_BRANCH=$(git rev-parse --abbrev-ref HEAD)

# Create/checkout gh-pages branch
git checkout -B gh-pages

# Remove everything except dist and scripts
find . -maxdepth 1 ! -name 'dist' ! -name '.git' ! -name '.' ! -name '..' ! -name 'publish-ghpages.sh' ! -name 'deploy-ghpages.sh' -exec rm -rf {} + 2>/dev/null || true

# Move built files to root
mv dist/wwwroot/* .
rm -rf dist

# Commit and push
git add .
git commit -m "Deploy to GitHub Pages - $(date '+%Y-%m-%d %H:%M:%S')"

# Detect the remote name (could be 'origin' or 'Github')
REMOTE=$(git remote | grep -i "github\|origin" | head -1)
if [ -z "$REMOTE" ]; then
  REMOTE="origin"
fi

git push "$REMOTE" gh-pages --force

echo ""
echo "✅ Deployed to GitHub Pages!"
echo "🌐 Your site will be available at: https://b-straub.github.io/SqliteWasmBlazor/"
echo ""

# Return to original branch
git checkout "$CURRENT_BRANCH"

echo "Returned to branch: $CURRENT_BRANCH"
