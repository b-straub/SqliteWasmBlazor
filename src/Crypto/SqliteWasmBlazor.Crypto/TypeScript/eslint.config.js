import tseslint from 'typescript-eslint';

export default tseslint.config(
    {
        // Vendored from sqlite.org's sqlite3-vfs-opfs-sahpool.c-pp.js — fork with
        // ChaCha20-Poly1305 page-level encryption added. Upstream-style; not
        // subject to our lint rules.
        ignores: ['node_modules/**', '../wwwroot/**', 'worker/vfs-prf/**'],
    },
    {
        files: ['worker/**/*.ts', 'bridge/**/*.ts'],
        languageOptions: {
            parser: tseslint.parser,
            parserOptions: {
                projectService: true,
                tsconfigRootDir: import.meta.dirname,
            },
        },
        plugins: {
            '@typescript-eslint': tseslint.plugin,
        },
        rules: {
            // Catch unawaited Promises + Promises misused where a boolean is
            // expected (the two classes of bug eslint without these rules
            // silently allows).
            '@typescript-eslint/no-floating-promises': 'error',
            '@typescript-eslint/no-misused-promises': 'error',
        },
    },
);
