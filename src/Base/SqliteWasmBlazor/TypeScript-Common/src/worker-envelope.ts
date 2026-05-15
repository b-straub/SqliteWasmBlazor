// worker-envelope.ts
// Wire-format types for the C# ↔ worker JSON envelope. Extracted from
// sqlite-worker.ts in plane-split Phase 3 so plane-2's worker dispatcher
// (Phase 4) can type its handlers against the same shape.

export interface WorkerRequest {
    id: number;
    data: {
        type: string;
        database?: string;
        sql?: string;
        parameters?: Record<string, any>;
    };
    binaryPayload?: ArrayBuffer;
    binaryHeader?: ArrayBuffer;
}

export interface WorkerResponse {
    id: number;
    data: {
        success: boolean;
        error?: string;
        columnNames?: string[];
        columnTypes?: string[];
        typedRows?: {
            types: string[];
            data: any[][];
        };
        rowsAffected?: number;
        lastInsertId?: number;
    };
}
