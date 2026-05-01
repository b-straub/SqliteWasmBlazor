// WebAuthn registration with PRF extension support

import { PrfErrorCode, type PrfCredential, type PrfOptions, type PrfResult } from './types.js';
import { bytesToBase64 } from '@sqlitewasmblazor/crypto-core';

function arrayBufferToBase64(buf: ArrayBuffer): string {
    return bytesToBase64(new Uint8Array(buf));
}

/**
 * Check if the current browser and platform support WebAuthn PRF extension.
 * Note: This only checks for basic WebAuthn support. PRF support can only be
 * truly verified during registration, as it depends on the specific authenticator.
 *
 * @returns true if WebAuthn is available (PRF support depends on authenticator)
 */
export async function checkPrfSupport(): Promise<boolean> {
    // Check basic WebAuthn support
    if (!window.PublicKeyCredential) {
        return false;
    }

    // PRF extension support can only be truly verified during registration
    // Modern platform authenticators (iOS 17+, macOS 14+, Windows 10+, Android 14+) support it
    // Many hardware keys (YubiKey 5+, SoloKeys v2) also support PRF
    return true;
}

/**
 * Register a new WebAuthn credential with PRF extension enabled.
 *
 * @param displayName Optional human-readable display name. If null, platform generates one.
 * @param options PRF configuration options
 * @returns PrfResult containing the credential or error
 */
export async function registerCredentialWithPrf(
    displayName: string | null,
    options: PrfOptions
): Promise<PrfResult<PrfCredential>> {
    try {
        // Generate random user ID (required by WebAuthn spec, not meaningful for PRF-only use)
        const userId = crypto.getRandomValues(new Uint8Array(16));

        // Display name shown in platform passkey manager
        const effectiveDisplayName = displayName ?? options.rpName;

        // Determine authenticator attachment (undefined = allow both platform and cross-platform)
        const authenticatorAttachment: AuthenticatorAttachment | undefined =
            options.authenticatorAttachment === 'platform' ? 'platform' :
            options.authenticatorAttachment === 'cross-platform' ? 'cross-platform' :
            undefined;

        // Build authenticator selection (omit authenticatorAttachment if 'any')
        const authenticatorSelection: AuthenticatorSelectionCriteria = {
            residentKey: 'preferred',
            userVerification: 'preferred'
        };
        if (authenticatorAttachment !== undefined) {
            authenticatorSelection.authenticatorAttachment = authenticatorAttachment;
        }

        // Build registration options
        const publicKeyCredentialCreationOptions: PublicKeyCredentialCreationOptions = {
            challenge: crypto.getRandomValues(new Uint8Array(32)),
            rp: {
                name: options.rpName,
                id: options.rpId ?? window.location.hostname
            },
            user: {
                id: userId,
                name: effectiveDisplayName, // Required by spec
                displayName: effectiveDisplayName
            },
            pubKeyCredParams: [
                { alg: -7, type: 'public-key' },   // ES256 (P-256)
                { alg: -257, type: 'public-key' }  // RS256
            ],
            authenticatorSelection,
            timeout: options.timeoutMs,
            attestation: 'none',
            extensions: {
                prf: {}
            } as AuthenticationExtensionsClientInputs
        };

        // Create credential using navigator.credentials API
        const credential = await navigator.credentials.create({
            publicKey: publicKeyCredentialCreationOptions
        }) as PublicKeyCredential | null;

        if (credential === null) {
            return {
                success: false,
                cancelled: true
            };
        }

        // Check if PRF extension is enabled
        const extensionResults = credential.getClientExtensionResults() as {
            prf?: { enabled?: boolean };
        };

        if (!extensionResults.prf?.enabled) {
            return {
                success: false,
                errorCode: PrfErrorCode.PrfNotSupported
            };
        }

        return {
            success: true,
            value: {
                id: credential.id,
                rawId: arrayBufferToBase64(credential.rawId)
            }
        };
    } catch (error) {
        // User cancelled the registration - not an error
        if (error instanceof DOMException && error.name === 'NotAllowedError') {
            return {
                success: false,
                cancelled: true
            };
        }

        return {
            success: false,
            errorCode: PrfErrorCode.RegistrationFailed
        };
    }
}
