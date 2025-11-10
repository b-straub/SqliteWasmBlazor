# GitHub Actions Setup

## Required Secrets

To enable automatic NuGet publishing, you need to configure the following secret in your GitHub repository:

### NUGET_API_KEY

1. Go to https://www.nuget.org/account/apikeys
2. Create a new API key with "Push" permission
3. Copy the generated key
4. In your GitHub repository, go to Settings → Secrets and variables → Actions
5. Click "New repository secret"
6. Name: `NUGET_API_KEY`
7. Value: Paste your NuGet API key
8. Click "Add secret"

## Workflows

### Build and Test (build.yml)
- **Triggers**: Push to `master` or `develop` branches, pull requests
- **Actions**: Restore, build, and test the project
- **Purpose**: Continuous integration for all commits

### Publish to NuGet (publish.yml)
- **Triggers**: Push tags matching `v*.*.*` (e.g., `v1.0.0`, `v1.2.3`)
- **Actions**:
  1. Build and test the project
  2. Pack the NuGet package with version from tag
  3. Push to NuGet.org
  4. Create GitHub release with package attached
- **Purpose**: Automated releases

## Creating a Release

1. Ensure all tests pass on master branch
2. Update version in `SqliteWasmBlazor.csproj` if needed (optional, tag version takes precedence)
3. Create and push a tag:
   ```bash
   git tag v1.0.0
   git push origin v1.0.0
   ```
4. GitHub Actions will automatically:
   - Run all tests
   - Build the package
   - Publish to NuGet.org
   - Create a GitHub release

## Manual Testing

To test the build process locally before tagging:

```bash
# Restore dependencies
dotnet restore

# Build
dotnet build --configuration Release

# Run tests
dotnet test --configuration Release

# Create NuGet package
dotnet pack SqliteWasmBlazor/SqliteWasmBlazor.csproj --configuration Release --output ./artifacts
```

The package will be in `./artifacts/SqliteWasmBlazor.{version}.nupkg`
