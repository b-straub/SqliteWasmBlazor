# OpfsInitializer Component

## Architecture

### Files
```
OpfsInitializer.razor       # Component markup (inherits OwningComponentBase)
OpfsInitializer.razor.cs    # Code-behind with typed C# API
OpfsInitializer.razor.js    # Bundled TypeScript (from opfs-native-vfs.ts + opfs-sahpool.ts)
```

### How It Works

The component follows the standard Blazor pattern:
1. **OpfsInitializer.razor** - Simple component inheriting from `OwningComponentBase`
2. **OpfsInitializer.razor.cs** - Code-behind providing typed C# methods
3. **OpfsInitializer.razor.js** - JavaScript module loaded in `OnAfterRenderAsync`

### Two Ways to Use

#### Option 1: Via Service (Current approach)
```csharp
// OpfsStorageService loads the JS module directly
_module = await _jsRuntime.InvokeAsync<IJSObjectReference>(
    "import", "./_content/SQLiteNET.Opfs/Components/OpfsInitializer.razor.js");

// Calls JS functions directly
await _module.InvokeAsync<InitializeResult>("initialize");
```

**Pros**: Simple, direct, no component rendering needed
**Cons**: No type safety, manual JS interop

#### Option 2: Via Component (Alternative)
```razor
<!-- Add component to layout -->
<OpfsInitializer @ref="_opfsInit" />

@code {
    private OpfsInitializer _opfsInit = default!;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            var result = await _opfsInit.InitializeAsync();
            // Typed C# API
        }
    }
}
```

**Pros**: Type-safe C# API, OwningComponentBase benefits
**Cons**: Requires component rendering

## Current Usage

**OpfsStorageService** uses Option 1 (direct JS module loading) because:
- Initialization happens before any components render
- Service is registered in DI during app startup
- Simpler for library consumers

The component code-behind (OpfsInitializer.razor.cs) provides the **typed API** if apps want to use the component directly in their pages.

## TypeScript Build

```bash
cd ../Typescript
npm run build
```

Outputs to: `OpfsInitializer.razor.js` (53.1kb bundled)
