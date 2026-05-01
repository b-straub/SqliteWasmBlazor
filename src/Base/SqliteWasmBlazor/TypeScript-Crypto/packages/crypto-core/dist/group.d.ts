import type { PrfResult, SymmetricEncryptedData, WrappedKey, GroupKeyBundle, GroupEncryptedData } from './types.js';
/**
 * Create group keys: generate CEK, wrap for each member.
 */
export declare function createGroupKeys(adminPrivateKey: Uint8Array, adminPublicKey: Uint8Array, memberPublicKeys: Uint8Array[], groupContext: string): Promise<PrfResult<GroupKeyBundle>>;
/**
 * Add members to a group: unwrap admin's CEK, wrap for new members.
 */
export declare function addGroupMembers(adminPrivateKey: Uint8Array, adminPublicKey: Uint8Array, adminWrappedCek: SymmetricEncryptedData, newMemberPublicKeys: Uint8Array[], groupContext: string): Promise<PrfResult<WrappedKey[]>>;
/**
 * Rotate group key: generate new CEK, wrap for remaining members.
 */
export declare function rotateGroupKey(adminPrivateKey: Uint8Array, adminPublicKey: Uint8Array, remainingMemberPublicKeys: Uint8Array[], groupContext: string): Promise<PrfResult<GroupKeyBundle>>;
/**
 * Transfer group admin: old admin unwraps CEK, new admin re-wraps for all members.
 */
export declare function transferGroupAdmin(oldAdminPrivateKey: Uint8Array, oldAdminPublicKey: Uint8Array, oldAdminWrappedCek: SymmetricEncryptedData, newAdminPrivateKey: Uint8Array, newAdminPublicKey: Uint8Array, memberPublicKeys: Uint8Array[], groupContext: string, keyVersion: number): Promise<PrfResult<GroupKeyBundle>>;
/**
 * Encrypt data for a group. Unwraps sender's CEK, encrypts with AAD, signs envelope.
 */
export declare function encryptForGroup(senderX25519PrivateKey: Uint8Array, senderEd25519PrivateKey: Uint8Array, senderEd25519PublicKey: Uint8Array, adminPublicKey: Uint8Array, senderWrappedCek: SymmetricEncryptedData, plaintext: Uint8Array, groupContext: string, keyVersion: number): Promise<PrfResult<GroupEncryptedData>>;
/**
 * Decrypt data from a group. Verifies envelope signature, unwraps CEK, decrypts with AAD.
 */
export declare function decryptFromGroup(recipientX25519PrivateKey: Uint8Array, adminPublicKey: Uint8Array, recipientWrappedCek: SymmetricEncryptedData, message: GroupEncryptedData): Promise<PrfResult<Uint8Array>>;
//# sourceMappingURL=group.d.ts.map