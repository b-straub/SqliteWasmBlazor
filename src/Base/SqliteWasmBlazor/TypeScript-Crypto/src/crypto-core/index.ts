// @sqlitewasmblazor/crypto-core — barrel export
// Pure TypeScript crypto primitives + group encryption

// Types
export type {
    PrfResult,
    KeyPair,
    DualKeyPair,
    DualKeyPairFull,
    SymmetricEncryptedData,
    AsymmetricEncryptedData,
    WrappedKey,
    GroupKeyBundle,
    GroupEncryptedData,
} from './types.js';

export { PrfErrorCode, PrfResultUtil, NONCE_LENGTH_AES, KEY_LENGTH } from './types.js';

// Utils
export {
    base64ToBytes,
    bytesToBase64,
    base64UrlToBase64,
    clearBytes,
    withSecureBuffer,
    concatBytes,
    wrapSeedInPkcs8,
    generateRandomBytes,
    toBuffer,
} from './utils.js';

// X25519
export { generateX25519KeyPair, getX25519PublicKey, x25519SharedSecret } from './x25519.js';

// Ed25519
export { generateEd25519KeyPair, getEd25519PublicKey, ed25519Sign, ed25519Verify } from './ed25519.js';

// AES-GCM
export { encryptAesGcm, decryptAesGcm } from './aesGcm.js';

// ECIES
export { encryptAsymmetric, decryptAsymmetric } from './ecies.js';

// Key derivation
export {
    deriveHkdfKey,
    deriveX25519KeyPair as deriveX25519KeyPairFromSeed,
    deriveEd25519KeyPair as deriveEd25519KeyPairFromSeed,
    deriveDualKeyPair,
    deriveWrappingKey,
} from './keyDerivation.js';

// Key wrapping
export { generateContentKey, wrapContentKey, unwrapContentKey } from './keyWrapping.js';

// Group encryption
export {
    createGroupKeys,
    addGroupMembers,
    rotateGroupKey,
    transferGroupAdmin,
    encryptForGroup,
    decryptFromGroup,
} from './group.js';

// Batch signatures
export { computeBatchDigest, signBatch, verifyBatch } from './batch.js';

// VAPID (Voluntary Application Server Identification)
export type { VapidKeyPair } from './vapid.js';
export { generateVapidKeyPair, importVapidPrivateKey, createVapidAuthHeader } from './vapid.js';

// WebPush (RFC 8291 encryption + push sending)
export type { WebPushMessage, WebPushResult, PushSubscriptionKeys } from './webpush.js';
export { encryptPushPayload, sendPushNotification } from './webpush.js';

// Hashes (re-export from @awasm/noble for downstream consumers)
export { sha256 } from '@awasm/noble';
export { hmac } from '@awasm/noble/hmac.js';

// ChaCha20-Poly1305
export { encryptChaCha20Poly1305, decryptChaCha20Poly1305 } from './chacha20Poly1305.js';
