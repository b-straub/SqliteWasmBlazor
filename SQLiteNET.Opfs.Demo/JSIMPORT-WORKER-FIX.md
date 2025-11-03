# JSImport Worker Re-initialization Fix

## Problem

When implementing JSImport optimization for incremental OPFS sync, the application was creating **two separate OPFS worker instances**, causing file handle conflicts:

```
[OpfsSAHPool] 6 files still locked after 3 attempts, deleting...
[OPFS Worker] SAHPool initialization failed: Error: Failed to acquire any access handles from 6 files
[OPFS Interop] Failed to persist dirty pages: Error: Failed to open file for partial write: TodoDb.db
```

## Root Cause

The issue was in how TypeScript modules were being bundled:

1. **OpfsInitializer.razor.js** - Bundled from `opfs-initializer.ts`, initialized the first worker
2. **opfs-interop.js** - Bundled from `opfs-interop.ts`, which imported `sendMessageToWorker` from `opfs-initializer.ts`

When esbuild bundled both modules with `--bundle`, it created **two separate copies** of the worker initialization code. Each had its own module-scoped state:
- Separate `worker` instances
- Separate `isInitialized` flags
- Separate `pendingMessages` maps

When `persistDirtyPages()` was called via JSImport, it triggered the bundled `sendMessageToWorker()` function, which checked its own `isInitialized` flag (false) and created a **second worker**, causing SAHPool conflicts.

## Solution

Instead of importing and bundling the worker code, we now share the worker instance via global references:

### 1. opfs-initializer.ts - Expose Worker Globally

After successful initialization, expose the `sendMessage` function globally:

```typescript
// Expose sendMessage globally for use by other modules (opfs-interop.ts)
// This ensures they use the same worker instance
(window as any).__opfsSendMessage = sendMessage;
(window as any).__opfsIsInitialized = () => isInitialized;
```

### 2. opfs-interop.ts - Use Global Worker

Instead of importing `sendMessageToWorker`, access the global function:

```typescript
/**
 * Get the global sendMessage function from opfs-initializer.
 * This ensures we use the same worker instance that was already initialized.
 */
function getSendMessage(): ((type: string, args?: any) => Promise<any>) | null {
    return (window as any).__opfsSendMessage || null;
}

export async function persistDirtyPages(filename: string, pages: any[]): Promise<any> {
    // Get the global sendMessage function (uses existing worker)
    const sendMessage = getSendMessage();
    if (!sendMessage) {
        return {
            success: false,
            error: 'OPFS worker not initialized'
        };
    }

    // Use the existing worker
    const result = await sendMessage('persistDirtyPages', { ... });
}
```

## Benefits

✅ **Single Worker Instance** - Both modules share the same OPFS worker
✅ **No SAHPool Conflicts** - Only one worker accesses OPFS file handles
✅ **Smaller Bundle Size** - opfs-interop.js reduced from ~23kb to 9.3kb
✅ **Clean Separation** - Initialization happens once in opfs-initializer, interop just uses it

## File Changes

- `/SQLiteNET.Opfs/Typescript/opfs-initializer.ts` - Expose `__opfsSendMessage` globally
- `/SQLiteNET.Opfs/Typescript/opfs-interop.ts` - Use global function instead of import

## Testing

Test by adding multiple todos and verifying console logs show:
- ✅ Single "Creating worker..." message
- ✅ No SAHPool errors
- ✅ Successful incremental sync: "✓ JSImport: Written X pages"
