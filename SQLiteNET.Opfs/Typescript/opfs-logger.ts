// opfs-logger.ts
// Centralized logging for OPFS TypeScript modules
// Matches C# OpfsLogLevel enum

export enum OpfsLogLevel {
    None = 0,
    Error = 1,
    Warning = 2,
    Info = 3,
    Debug = 4
}

class OpfsLogger {
    private logLevel: OpfsLogLevel = OpfsLogLevel.Warning;

    setLogLevel(level: OpfsLogLevel): void {
        this.logLevel = level;
    }

    debug(module: string, ...args: any[]): void {
        if (this.logLevel >= OpfsLogLevel.Debug) {
            console.log(`[${module}]`, ...args);
        }
    }

    info(module: string, ...args: any[]): void {
        if (this.logLevel >= OpfsLogLevel.Info) {
            console.log(`[${module}] ✓`, ...args);
        }
    }

    warn(module: string, ...args: any[]): void {
        if (this.logLevel >= OpfsLogLevel.Warning) {
            console.warn(`[${module}] ⚠`, ...args);
        }
    }

    error(module: string, ...args: any[]): void {
        if (this.logLevel >= OpfsLogLevel.Error) {
            console.error(`[${module}] ❌`, ...args);
        }
    }
}

// Global logger instance
export const logger = new OpfsLogger();

// Expose globally for C# to configure
(window as any).__opfsLogger = logger;
