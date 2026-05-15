import { defineConfig } from 'vitest/config';

// Vitest setup for the binary-bridge layer (`src/crypto-bridge.ts`) and
// crypto-core primitives. The bridge imports crypto-core directly via the
// `main` field; vitest reads TS source — no `dist/` build needed.
export default defineConfig({
    test: {
        include: ['tests/**/*.test.ts'],
    },
});
