import { defineConfig } from 'vitest/config';
import { fileURLToPath } from 'node:url';

// Vitest setup for the binary-bridge layer (`src/crypto.ts`). The bridge
// imports `@sqlitewasmblazor/crypto-core` as a workspace package; the
// alias below resolves that to the package source so the tests do not
// require a prior `dist/` build.
//
// crypto-core's own vitest suite still runs from
// `packages/crypto-core/vitest.config.ts` via `npm test -w
// @sqlitewasmblazor/crypto-core`.
export default defineConfig({
    test: {
        include: ['tests/**/*.test.ts'],
    },
    resolve: {
        alias: [
            {
                find: '@sqlitewasmblazor/crypto-core',
                replacement: fileURLToPath(new URL('./packages/crypto-core/src/index.ts', import.meta.url)),
            },
        ],
    },
});
