// sqlite-logger.ts
// Centralized logging for SQLite WASM TypeScript modules
// Matches C# SqliteWasmLogLevel enum

export enum SqliteWasmLogLevel {
    None = 0,
    Error = 1,
    Warning = 2,
    Info = 3,
    Debug = 4
}

class SqliteWasmLogger {
    private logLevel: SqliteWasmLogLevel = SqliteWasmLogLevel.Warning;

    setLogLevel(level: SqliteWasmLogLevel): void {
        this.logLevel = level;
    }

    debug(module: string, ...args: any[]): void {
        if (this.logLevel >= SqliteWasmLogLevel.Debug) {
            console.log(`[${module}]`, ...args);
        }
    }

    info(module: string, ...args: any[]): void {
        if (this.logLevel >= SqliteWasmLogLevel.Info) {
            console.log(`[${module}] ✓`, ...args);
        }
    }

    warn(module: string, ...args: any[]): void {
        if (this.logLevel >= SqliteWasmLogLevel.Warning) {
            console.warn(`[${module}] ⚠`, ...args);
        }
    }

    error(module: string, ...args: any[]): void {
        if (this.logLevel >= SqliteWasmLogLevel.Error) {
            console.error(`[${module}] ❌`, ...args);
        }
    }
}

// Global logger instance
export const logger = new SqliteWasmLogger();
