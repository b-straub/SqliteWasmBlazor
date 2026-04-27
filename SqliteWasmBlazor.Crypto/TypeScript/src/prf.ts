// PRF evaluation for deterministic key derivation

import { PrfErrorCode, type PrfOptions, type PrfResult } from './types.js';
import { base64ToBytes, bytesToBase64, clearBytes, toBuffer } from '@sqlitewasmblazor/crypto-core';

function base64ToArrayBuffer(b64: string): ArrayBuffer {
    return toBuffer(base64ToBytes(b64));
}

/**
 * Hash a string using SHA-256 via Web Crypto API.
 * Returns a 32-byte Uint8Array.
 */
async function sha256(data: Uint8Array): Promise<Uint8Array> {
    const hashBuffer = await crypto.subtle.digest('SHA-256', toBuffer(data));
    return new Uint8Array(hashBuffer);
}

/**
 * Evaluate the PRF extension with a given salt to derive a deterministic 32-byte output.
 * The PRF output is returned to C# for secure storage in WASM linear memory.
 *
 * IMPORTANT: The PRF output is sensitive and should be handled securely.
 * This function zeros the internal buffers after returning the Base64 result.
 *
 * @param credentialIdBase64 The credential ID (Base64 encoded)
 * @param salt A string salt that determines the derived key (must be consistent across devices)
 * @param options PRF configuration options
 * @returns PrfResult containing the 32-byte PRF output as Base64
 */
export async function evaluatePrf(
    credentialIdBase64: string,
    salt: string,
    options: PrfOptions
): Promise<PrfResult<string>> {
    let prfOutput: Uint8Array | null = null;

    try {
        // Hash the salt to ensure consistent 32-byte length
        const encoder = new TextEncoder();
        const saltBytes = encoder.encode(salt);
        const saltHash = await sha256(saltBytes);

        const credentialId = base64ToArrayBuffer(credentialIdBase64);

        // Build authentication options with PRF extension
        // Note: Don't specify transports - let browser determine the best transport
        const publicKeyCredentialRequestOptions: PublicKeyCredentialRequestOptions = {
            challenge: crypto.getRandomValues(new Uint8Array(32)),
            rpId: options.rpId ?? window.location.hostname,
            allowCredentials: [
                {
                    id: credentialId,
                    type: 'public-key'
                }
            ],
            timeout: options.timeoutMs,
            userVerification: 'preferred',
            extensions: {
                prf: {
                    eval: {
                        first: saltHash.buffer as ArrayBuffer
                    }
                }
            } as AuthenticationExtensionsClientInputs
        };

        // Perform authentication with PRF evaluation
        const assertion = await navigator.credentials.get({
            publicKey: publicKeyCredentialRequestOptions
        }) as PublicKeyCredential | null;

        if (assertion === null) {
            return {
                success: false,
                cancelled: true
            };
        }

        // Extract PRF results
        const extensionResults = assertion.getClientExtensionResults() as {
            prf?: {
                results?: {
                    first?: ArrayBuffer;
                };
            };
        };

        const prfResults = extensionResults.prf?.results;

        if (!prfResults?.first) {
            return {
                success: false,
                errorCode: PrfErrorCode.PrfNotSupported
            };
        }

        // Convert to Uint8Array
        prfOutput = new Uint8Array(prfResults.first);

        if (prfOutput.length !== 32) {
            return {
                success: false,
                errorCode: PrfErrorCode.KeyDerivationFailed
            };
        }

        // Convert to Base64 for transfer to C#
        const resultBase64 = bytesToBase64(prfOutput);

        return {
            success: true,
            value: resultBase64
        };
    } catch (error) {
        // User cancelled the authentication - not an error
        if (error instanceof DOMException && error.name === 'NotAllowedError') {
            return {
                success: false,
                cancelled: true
            };
        }

        return {
            success: false,
            errorCode: PrfErrorCode.KeyDerivationFailed
        };
    } finally {
        // Zero the PRF output in JavaScript memory
        // The Base64 string has already been created for C# transfer
        if (prfOutput) {
            clearBytes(prfOutput);
        }
    }
}

/**
 * Evaluate PRF without a specific credential ID (discoverable credential).
 * The authenticator will prompt the user to select a credential.
 *
 * @param salt A string salt that determines the derived key
 * @param options PRF configuration options
 * @returns PrfResult containing the credential ID and 32-byte PRF output as Base64
 */
export async function evaluatePrfDiscoverable(
    salt: string,
    options: PrfOptions
): Promise<PrfResult<{ credentialId: string; prfOutput: string }>> {
    let prfOutput: Uint8Array | null = null;

    try {
        // Hash the salt to ensure consistent 32-byte length
        const encoder = new TextEncoder();
        const saltBytes = encoder.encode(salt);
        const saltHash = await sha256(saltBytes);

        // Build authentication options without allowCredentials (discoverable)
        // Note: Don't set timeout for discoverable - let browser handle it naturally
        const publicKeyCredentialRequestOptions: PublicKeyCredentialRequestOptions = {
            challenge: crypto.getRandomValues(new Uint8Array(32)),
            rpId: options.rpId ?? window.location.hostname,
            userVerification: 'preferred',
            extensions: {
                prf: {
                    eval: {
                        first: saltHash.buffer as ArrayBuffer
                    }
                }
            } as AuthenticationExtensionsClientInputs
        };

        // Perform authentication with PRF evaluation
        const assertion = await navigator.credentials.get({
            publicKey: publicKeyCredentialRequestOptions
        }) as PublicKeyCredential | null;

        if (assertion === null) {
            return {
                success: false,
                cancelled: true
            };
        }

        // Extract PRF results
        const extensionResults = assertion.getClientExtensionResults() as {
            prf?: {
                results?: {
                    first?: ArrayBuffer;
                };
            };
        };

        const prfResults = extensionResults.prf?.results;

        if (!prfResults?.first) {
            return {
                success: false,
                errorCode: PrfErrorCode.PrfNotSupported
            };
        }

        // Convert to Uint8Array
        prfOutput = new Uint8Array(prfResults.first);

        if (prfOutput.length !== 32) {
            return {
                success: false,
                errorCode: PrfErrorCode.KeyDerivationFailed
            };
        }

        // Convert to Base64 for transfer to C#
        const resultBase64 = bytesToBase64(prfOutput);
        const credentialIdBase64 = bytesToBase64(new Uint8Array(assertion.rawId));

        return {
            success: true,
            value: {
                credentialId: credentialIdBase64,
                prfOutput: resultBase64
            }
        };
    } catch (error) {
        // User cancelled the authentication - not an error
        if (error instanceof DOMException && error.name === 'NotAllowedError') {
            return {
                success: false,
                cancelled: true
            };
        }

        return {
            success: false,
            errorCode: PrfErrorCode.KeyDerivationFailed
        };
    } finally {
        // Zero the PRF output in JavaScript memory
        if (prfOutput) {
            clearBytes(prfOutput);
        }
    }
}
