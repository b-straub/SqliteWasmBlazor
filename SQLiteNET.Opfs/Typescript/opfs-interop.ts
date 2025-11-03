// opfs-interop.ts
// High-performance JSImport exports for OPFS operations
// Uses direct memory views and zero-copy transfers

import { logger } from './opfs-logger';

/**
 * Get the global sendMessage function from opfs-initializer.
 * This ensures we use the same worker instance that was already initialized.
 */
function getSendMessage(): ((type: string, args?: any) => Promise<any>) | null {
    return (window as any).__opfsSendMessage || null;
}

/**
 * Read specific pages from Emscripten MEMFS (synchronous).
 * Returns pages with Uint8Array views for zero-copy access.
 */
export function readPagesFromMemfs(filename: string, pageNumbers: number[], pageSize: number): any {
    try {
        const fs = (window as any).Blazor?.runtime?.Module?.FS;
        if (!fs) {
            return {
                success: false,
                error: 'FS not available',
                pages: null
            };
        }

        const filePath = `/${filename}`;

        // Check if file exists
        const pathInfo = fs.analyzePath(filePath);
        if (!pathInfo.exists) {
            return {
                success: false,
                error: `File ${filename} not found in MEMFS`,
                pages: null
            };
        }

        // Read entire file once
        const fileData = fs.readFile(filePath);

        // Extract requested pages
        const pages = [];
        for (const pageNum of pageNumbers) {
            const offset = pageNum * pageSize;
            const end = Math.min(offset + pageSize, fileData.length);

            if (offset < fileData.length) {
                // Create a view into the file data (zero-copy)
                const pageData = fileData.subarray(offset, end);

                pages.push({
                    pageNumber: pageNum,
                    data: pageData  // This is already a Uint8Array - direct memory view
                });
            }
        }

        return {
            success: true,
            error: null,
            pages: pages
        };
    } catch (err: any) {
        logger.error('OPFS Interop', 'Failed to read pages from MEMFS:', err);
        return {
            success: false,
            error: err.message || 'Unknown error',
            pages: null
        };
    }
}

/**
 * Persist dirty pages to OPFS (incremental sync).
 * Receives pages with direct Uint8Array data (no conversion needed).
 */
export async function persistDirtyPages(filename: string, pages: any[]): Promise<any> {
    try {
        // Get the global sendMessage function (uses existing worker)
        const sendMessage = getSendMessage();
        if (!sendMessage) {
            return {
                success: false,
                pagesWritten: 0,
                bytesWritten: 0,
                error: 'OPFS worker not initialized'
            };
        }

        // Send pages to worker for partial write
        const result = await sendMessage('persistDirtyPages', {
            filename,
            pages: pages.map(p => ({
                pageNumber: p.pageNumber,
                // Data is already a Uint8Array from JSImport marshalling
                data: Array.from(p.data)  // Convert to regular array for worker message
            }))
        });

        return {
            success: true,
            pagesWritten: result.pagesWritten,
            bytesWritten: result.bytesWritten,
            error: null
        };
    } catch (err: any) {
        logger.error('OPFS Interop', 'Failed to persist dirty pages:', err);
        return {
            success: false,
            pagesWritten: 0,
            bytesWritten: 0,
            error: err.message || 'Unknown error'
        };
    }
}
