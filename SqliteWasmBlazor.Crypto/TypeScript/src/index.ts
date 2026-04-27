// Main entry point - exports WebAuthn/PRF functions and B64 bridge functions for C# JSImport

import {
    type PrfOptions,
} from './types.js';
import { checkPrfSupport, registerCredentialWithPrf } from './webauthn.js';
import { evaluatePrf, evaluatePrfDiscoverable } from './prf.js';
import * as crypto from './crypto.js';

// ============================================================================
// WebAuthn / PRF Functions
// ============================================================================

export async function isPrfSupported(): Promise<boolean> {
    return checkPrfSupport();
}

export async function register(
    displayName: string | null,
    optionsJson: string
): Promise<string> {
    const options: PrfOptions = JSON.parse(optionsJson);
    const result = await registerCredentialWithPrf(displayName, options);
    return JSON.stringify(result);
}

export async function evaluatePrfOutput(
    credentialIdBase64: string,
    salt: string,
    optionsJson: string
): Promise<string> {
    const options: PrfOptions = JSON.parse(optionsJson);
    const prfResult = await evaluatePrf(credentialIdBase64, salt, options);

    if (!prfResult.success || !prfResult.value) {
        return JSON.stringify({
            success: false,
            errorCode: prfResult.errorCode,
            cancelled: prfResult.cancelled
        });
    }

    return JSON.stringify({
        success: true,
        value: prfResult.value
    });
}

export async function evaluatePrfDiscoverableOutput(
    salt: string,
    optionsJson: string
): Promise<string> {
    const options: PrfOptions = JSON.parse(optionsJson);
    const prfResult = await evaluatePrfDiscoverable(salt, options);

    if (!prfResult.success || !prfResult.value) {
        return JSON.stringify({
            success: false,
            errorCode: prfResult.errorCode,
            cancelled: prfResult.cancelled
        });
    }

    return JSON.stringify({
        success: true,
        value: {
            credentialId: prfResult.value.credentialId,
            prfOutput: prfResult.value.prfOutput
        }
    });
}

// ============================================================================
// B64 Bridge functions (packed binary as Base64 strings for C# JSImport)
// ============================================================================

// X25519
export const generateX25519KeyPairB64 = crypto.generateX25519KeyPairB64;
export const getX25519PublicKeyB64 = crypto.getX25519PublicKeyB64;
export const deriveX25519KeyPairB64 = crypto.deriveX25519KeyPairB64;

// Ed25519
export const generateEd25519KeyPairB64 = crypto.generateEd25519KeyPairB64;
export const getEd25519PublicKeyB64 = crypto.getEd25519PublicKeyB64;
export const deriveEd25519KeyPairB64 = crypto.deriveEd25519KeyPairB64;
export const ed25519SignB64 = crypto.ed25519SignB64;
export const ed25519VerifyB64 = crypto.ed25519VerifyB64;

// Dual key derivation
export const deriveDualKeyPairB64 = crypto.deriveDualKeyPairB64;

// AES-GCM
export const encryptAesGcmB64 = crypto.encryptAesGcmB64;
export const decryptAesGcmB64 = crypto.decryptAesGcmB64;

// ECIES
export const encryptAsymmetricB64 = crypto.encryptAsymmetricB64;
export const decryptAsymmetricB64 = crypto.decryptAsymmetricB64;

// Key derivation
export const deriveHkdfKeyB64 = crypto.deriveHkdfKeyB64;
export const deriveWrappingKeyB64 = crypto.deriveWrappingKeyB64;

// Utility
export const generateRandomBytesB64 = crypto.generateRandomBytesB64;
export const isSupported = crypto.isSupported;

// Key cache management
export const storeKeysB64 = crypto.storeKeysB64;
export const getPublicKeysB64 = crypto.getPublicKeysB64;
export const hasKey = crypto.hasKey;
export const removeKeys = crypto.removeKeys;
export const clearAllKeys = crypto.clearAllKeys;

// Cached key operations
export const signWithCachedKeyB64 = crypto.signWithCachedKeyB64;
export const encryptSymmetricCachedB64 = crypto.encryptSymmetricCachedB64;
export const decryptSymmetricCachedB64 = crypto.decryptSymmetricCachedB64;
export const decryptAsymmetricCachedB64 = crypto.decryptAsymmetricCachedB64;

// VAPID + WebPush
export const generateVapidKeyPairB64 = crypto.generateVapidKeyPairB64;
export const importVapidKeyPairB64 = crypto.importVapidKeyPairB64;
export const sendPushNotificationB64 = crypto.sendPushNotificationB64;
export const encryptPushPayloadB64 = crypto.encryptPushPayloadB64;
export const hasVapidKey = crypto.hasVapidKey;
export const clearVapidKey = crypto.clearVapidKey;
