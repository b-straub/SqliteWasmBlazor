// @sqlitewasmblazor/crypto-core — GroupEncryptionService
// Port of C# GroupEncryptionService (pure composition of crypto primitives)

import { sha256 } from '@awasm/noble';
import { clearBytes, bytesToBase64 } from './utils.js';
import { PrfErrorCode, PrfResultUtil } from './types.js';
import type { PrfResult, SymmetricEncryptedData, WrappedKey, GroupKeyBundle, GroupEncryptedData } from './types.js';
import { deriveWrappingKey } from './keyDerivation.js';
import { generateContentKey, wrapContentKey, unwrapContentKey } from './keyWrapping.js';
import { encryptAesGcm, decryptAesGcm } from './aesGcm.js';
import { ed25519Sign, ed25519Verify } from './ed25519.js';

// ============================================================
// CONTROL PLANE (Admin only)
// ============================================================

/**
 * Create group keys: generate CEK, wrap for each member.
 */
export async function createGroupKeys(
    adminPrivateKey: Uint8Array,
    adminPublicKey: Uint8Array,
    memberPublicKeys: Uint8Array[],
    groupContext: string
): Promise<PrfResult<GroupKeyBundle>> {
    const cek = generateContentKey();

    try {
        const memberKeys: WrappedKey[] = [];

        for (const memberPubKey of memberPublicKeys) {
            const wrappingKey = deriveWrappingKey(adminPrivateKey, memberPubKey, groupContext);
            try {
                const wrappedCek = await wrapContentKey(cek, wrappingKey);
                memberKeys.push({ memberPublicKey: memberPubKey, wrappedContentKey: wrappedCek });
            } finally {
                clearBytes(wrappingKey);
            }
        }

        return PrfResultUtil.ok<GroupKeyBundle>({
            groupContext,
            keyVersion: 1,
            adminPublicKey,
            memberKeys,
        });
    } catch {
        return PrfResultUtil.fail(PrfErrorCode.EncryptionFailed);
    } finally {
        clearBytes(cek);
    }
}

/**
 * Add members to a group: unwrap admin's CEK, wrap for new members.
 */
export async function addGroupMembers(
    adminPrivateKey: Uint8Array,
    adminPublicKey: Uint8Array,
    adminWrappedCek: SymmetricEncryptedData,
    newMemberPublicKeys: Uint8Array[],
    groupContext: string
): Promise<PrfResult<WrappedKey[]>> {
    const cekResult = await unwrapMemberCek(adminPrivateKey, adminPublicKey, adminWrappedCek, groupContext);
    if (!cekResult.success || !cekResult.value) {
        return PrfResultUtil.fail(cekResult.errorCode ?? PrfErrorCode.DecryptionFailed);
    }

    const cek = cekResult.value;
    try {
        const newKeys: WrappedKey[] = [];

        for (const memberPubKey of newMemberPublicKeys) {
            const wrappingKey = deriveWrappingKey(adminPrivateKey, memberPubKey, groupContext);
            try {
                const wrappedCek = await wrapContentKey(cek, wrappingKey);
                newKeys.push({ memberPublicKey: memberPubKey, wrappedContentKey: wrappedCek });
            } finally {
                clearBytes(wrappingKey);
            }
        }

        return PrfResultUtil.ok(newKeys);
    } catch {
        return PrfResultUtil.fail(PrfErrorCode.EncryptionFailed);
    } finally {
        clearBytes(cek);
    }
}

/**
 * Rotate group key: generate new CEK, wrap for remaining members.
 */
export async function rotateGroupKey(
    adminPrivateKey: Uint8Array,
    adminPublicKey: Uint8Array,
    remainingMemberPublicKeys: Uint8Array[],
    groupContext: string
): Promise<PrfResult<GroupKeyBundle>> {
    const newCek = generateContentKey();

    try {
        const memberKeys: WrappedKey[] = [];

        for (const memberPubKey of remainingMemberPublicKeys) {
            const wrappingKey = deriveWrappingKey(adminPrivateKey, memberPubKey, groupContext);
            try {
                const wrappedCek = await wrapContentKey(newCek, wrappingKey);
                memberKeys.push({ memberPublicKey: memberPubKey, wrappedContentKey: wrappedCek });
            } finally {
                clearBytes(wrappingKey);
            }
        }

        const version = parseVersionFromContext(groupContext);

        return PrfResultUtil.ok<GroupKeyBundle>({
            groupContext,
            keyVersion: version,
            adminPublicKey,
            memberKeys,
        });
    } catch {
        return PrfResultUtil.fail(PrfErrorCode.EncryptionFailed);
    } finally {
        clearBytes(newCek);
    }
}

/**
 * Transfer group admin: old admin unwraps CEK, new admin re-wraps for all members.
 */
export async function transferGroupAdmin(
    oldAdminPrivateKey: Uint8Array,
    oldAdminPublicKey: Uint8Array,
    oldAdminWrappedCek: SymmetricEncryptedData,
    newAdminPrivateKey: Uint8Array,
    newAdminPublicKey: Uint8Array,
    memberPublicKeys: Uint8Array[],
    groupContext: string,
    keyVersion: number
): Promise<PrfResult<GroupKeyBundle>> {
    const cekResult = await unwrapMemberCek(oldAdminPrivateKey, oldAdminPublicKey, oldAdminWrappedCek, groupContext);
    if (!cekResult.success || !cekResult.value) {
        return PrfResultUtil.fail(cekResult.errorCode ?? PrfErrorCode.DecryptionFailed);
    }

    const cek = cekResult.value;
    try {
        const memberKeys: WrappedKey[] = [];

        for (const memberPubKey of memberPublicKeys) {
            const wrappingKey = deriveWrappingKey(newAdminPrivateKey, memberPubKey, groupContext);
            try {
                const wrappedCek = await wrapContentKey(cek, wrappingKey);
                memberKeys.push({ memberPublicKey: memberPubKey, wrappedContentKey: wrappedCek });
            } finally {
                clearBytes(wrappingKey);
            }
        }

        return PrfResultUtil.ok<GroupKeyBundle>({
            groupContext,
            keyVersion,
            adminPublicKey: newAdminPublicKey,
            memberKeys,
        });
    } catch {
        return PrfResultUtil.fail(PrfErrorCode.EncryptionFailed);
    } finally {
        clearBytes(cek);
    }
}

// ============================================================
// DATA PLANE (Any member)
// ============================================================

/**
 * Encrypt data for a group. Unwraps sender's CEK, encrypts with AAD, signs envelope.
 */
export async function encryptForGroup(
    senderX25519PrivateKey: Uint8Array,
    senderEd25519PrivateKey: Uint8Array,
    senderEd25519PublicKey: Uint8Array,
    adminPublicKey: Uint8Array,
    senderWrappedCek: SymmetricEncryptedData,
    plaintext: Uint8Array,
    groupContext: string,
    keyVersion: number
): Promise<PrfResult<GroupEncryptedData>> {
    // Unwrap sender's CEK
    const wrappingKey = deriveWrappingKey(senderX25519PrivateKey, adminPublicKey, groupContext);
    let cek: Uint8Array;
    try {
        cek = await unwrapContentKey(senderWrappedCek, wrappingKey);
    } catch {
        return PrfResultUtil.fail(PrfErrorCode.DecryptionFailed);
    } finally {
        clearBytes(wrappingKey);
    }

    try {
        // Encrypt with AAD binding
        const aad = new TextEncoder().encode(`${groupContext}:${keyVersion}`);
        const encrypted = await encryptAesGcm(plaintext, cek, aad);

        // Sign the canonical envelope
        const envelopePayload = buildCanonicalEnvelope(groupContext, keyVersion, senderEd25519PublicKey, encrypted);
        const envelopeBytes = new TextEncoder().encode(envelopePayload);
        const envelopeSignature = ed25519Sign(envelopeBytes, senderEd25519PrivateKey);

        return PrfResultUtil.ok<GroupEncryptedData>({
            groupContext,
            keyVersion,
            encrypted,
            senderPublicKey: senderEd25519PublicKey,
            envelopeSignature,
        });
    } catch {
        return PrfResultUtil.fail(PrfErrorCode.EncryptionFailed);
    } finally {
        clearBytes(cek);
    }
}

/**
 * Decrypt data from a group. Verifies envelope signature, unwraps CEK, decrypts with AAD.
 */
export async function decryptFromGroup(
    recipientX25519PrivateKey: Uint8Array,
    adminPublicKey: Uint8Array,
    recipientWrappedCek: SymmetricEncryptedData,
    message: GroupEncryptedData
): Promise<PrfResult<Uint8Array>> {
    // Layer 2: Verify envelope signature
    const envelopePayload = buildCanonicalEnvelope(
        message.groupContext, message.keyVersion, message.senderPublicKey, message.encrypted);
    const envelopeBytes = new TextEncoder().encode(envelopePayload);

    if (!ed25519Verify(message.envelopeSignature, envelopeBytes, message.senderPublicKey)) {
        return PrfResultUtil.fail(PrfErrorCode.VerificationFailed);
    }

    // Layer 3: Unwrap CEK
    const wrappingKey = deriveWrappingKey(recipientX25519PrivateKey, adminPublicKey, message.groupContext);
    let cek: Uint8Array;
    try {
        cek = await unwrapContentKey(recipientWrappedCek, wrappingKey);
    } catch {
        return PrfResultUtil.fail(PrfErrorCode.DecryptionFailed);
    } finally {
        clearBytes(wrappingKey);
    }

    try {
        // Layer 1: Decrypt with AAD verification
        const aad = new TextEncoder().encode(`${message.groupContext}:${message.keyVersion}`);
        const plaintext = await decryptAesGcm(message.encrypted, cek, aad);
        return PrfResultUtil.ok(plaintext);
    } catch {
        return PrfResultUtil.fail(PrfErrorCode.DecryptionFailed);
    } finally {
        clearBytes(cek);
    }
}

// ============================================================
// HELPERS
// ============================================================

/**
 * Unwrap a member's CEK using ECDH with the admin's public key.
 */
async function unwrapMemberCek(
    memberPrivateKey: Uint8Array,
    adminPublicKey: Uint8Array,
    wrappedCek: SymmetricEncryptedData,
    groupContext: string
): Promise<PrfResult<Uint8Array>> {
    const wrappingKey = deriveWrappingKey(memberPrivateKey, adminPublicKey, groupContext);
    try {
        const cek = await unwrapContentKey(wrappedCek, wrappingKey);
        return PrfResultUtil.ok(cek);
    } catch {
        return PrfResultUtil.fail(PrfErrorCode.DecryptionFailed);
    } finally {
        clearBytes(wrappingKey);
    }
}

/**
 * Build canonical envelope string for signing/verification.
 * Format: "{groupContext}|{keyVersion}|{senderPublicKeyBase64}|{sha256(ciphertext)Base64}"
 * Matches C# GroupEncryptionService.BuildCanonicalEnvelope.
 *
 * Re-exported under `__test_buildCanonicalEnvelope` for the cross-language
 * byte-equality test; the symbol stays module-private otherwise.
 */
export function buildCanonicalEnvelope(
    groupContext: string,
    keyVersion: number,
    senderPublicKey: Uint8Array,
    encrypted: SymmetricEncryptedData
): string {
    const ciphertextHash = bytesToBase64(sha256(encrypted.ciphertext));
    const senderPubKeyBase64 = bytesToBase64(senderPublicKey);
    return `${groupContext}|${keyVersion}|${senderPubKeyBase64}|${ciphertextHash}`;
}

/**
 * Parse version number from group context string.
 * Expected format: "group-{id}:v{N}" — returns N, or 1 if not parseable.
 */
function parseVersionFromContext(groupContext: string): number {
    const vIndex = groupContext.lastIndexOf(':v');
    if (vIndex >= 0) {
        const versionStr = groupContext.substring(vIndex + 2);
        const version = parseInt(versionStr, 10);
        if (!isNaN(version)) {
            return version;
        }
    }
    return 1;
}
