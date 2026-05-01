import type { SymmetricEncryptedData } from './types.js';
export declare function encryptChaCha20Poly1305(plaintext: Uint8Array, key: Uint8Array, associatedData?: Uint8Array): SymmetricEncryptedData;
export declare function decryptChaCha20Poly1305(encrypted: SymmetricEncryptedData, key: Uint8Array, associatedData?: Uint8Array): Uint8Array;
//# sourceMappingURL=chacha20Poly1305.d.ts.map