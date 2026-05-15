// Main entry point - exports WebAuthn/PRF functions and B64 bridge functions for C# JSImport

import {
    type PrfOptions,
} from './types.js';
import { checkPrfSupport, registerCredentialWithPrf } from './webauthn.js';
import { evaluatePrf, evaluatePrfDiscoverable } from './prf.js';
import * as crypto from './crypto-bridge.js';

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

// Ed25519
export const ed25519SignB64 = crypto.ed25519SignB64;
export const ed25519VerifyB64 = crypto.ed25519VerifyB64;

// AES-GCM
export const encryptAesGcmB64 = crypto.encryptAesGcmB64;
export const decryptAesGcmB64 = crypto.decryptAesGcmB64;
export const decryptAesGcm_into = crypto.decryptAesGcm_into;

// ECIES
export const encryptAsymmetricB64 = crypto.encryptAsymmetricB64;
export const encryptAsymmetricFromBytesB64 = crypto.encryptAsymmetricFromBytesB64;
export const decryptAsymmetric_into = crypto.decryptAsymmetric_into;

// Key derivation (bytes-out via writable MemoryView output — P21)
export const deriveWrappingKey_into = crypto.deriveWrappingKey_into;
export const deriveDualKeyPair_into = crypto.deriveDualKeyPair_into;

// Utility
export const generateRandomBytes_into = crypto.generateRandomBytes_into;
export const isSupported = crypto.isSupported;

// Key cache management
export const storeKeysB64 = crypto.storeKeysB64;
export const getPublicKeysB64 = crypto.getPublicKeysB64;
export const hasKey = crypto.hasKey;
export const removeKeys = crypto.removeKeys;
export const clearAllKeys = crypto.clearAllKeys;

// Cached key operations
export const signWithCachedKeyB64 = crypto.signWithCachedKeyB64;
export const decryptAsymmetricCachedB64 = crypto.decryptAsymmetricCachedB64;
export const decryptAsymmetricCached_into = crypto.decryptAsymmetricCached_into;

// VAPID + WebPush
export const generateVapidKeyPair_into = crypto.generateVapidKeyPair_into;
export const importVapidKeyPairB64 = crypto.importVapidKeyPairB64;
export const sendPushNotificationB64 = crypto.sendPushNotificationB64;
export const encryptPushPayloadB64 = crypto.encryptPushPayloadB64;
export const hasVapidKey = crypto.hasVapidKey;
export const clearVapidKey = crypto.clearVapidKey;
