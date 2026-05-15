import tseslint from 'typescript-eslint';

export default tseslint.config(
    {
        ignores: ['node_modules/**', '../wwwroot/**'],
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
            '@typescript-eslint/no-floating-promises': 'error',
            '@typescript-eslint/no-misused-promises': 'error',
        },
    },
);
