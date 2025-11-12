#!/bin/bash
set -e

# Build first
./publish-ghpages.sh

echo ""
echo "Deploying to GitHub Pages..."

# Save current branch
CURRENT_BRANCH=$(git rev-parse --abbrev-ref HEAD)

# Create/checkout gh-pages branch (orphan to start fresh)
git checkout --orphan gh-pages-temp

# Remove all files from git
git rm -rf .

# Copy built files to root
cp -r dist/wwwroot/* .

# Clean up
rm -rf dist

# Commit and push
git add .
git commit -m "Deploy to GitHub Pages - $(date '+%Y-%m-%d %H:%M:%S')"

# Detect the remote name (could be 'origin' or 'Github')
REMOTE=$(git remote | grep -i "github\|origin" | head -1)
if [ -z "$REMOTE" ]; then
  REMOTE="origin"
fi

# Delete old gh-pages branch and push new one
git branch -D gh-pages 2>/dev/null || true
git branch -m gh-pages
git push "$REMOTE" gh-pages --force

echo ""
echo "✅ Deployed to GitHub Pages!"
echo "🌐 Your site will be available at: https://b-straub.github.io/SqliteWasmBlazor/"
echo ""

# Return to original branch
git checkout "$CURRENT_BRANCH"

echo "Returned to branch: $CURRENT_BRANCH"
