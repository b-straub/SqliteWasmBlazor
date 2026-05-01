import { describe, it, expect } from 'vitest';
import {
    createGroupKeys, addGroupMembers, rotateGroupKey, transferGroupAdmin,
    encryptForGroup, decryptFromGroup,
    generateX25519KeyPair, generateEd25519KeyPair,
    deriveDualKeyPair, generateRandomBytes, clearBytes,
    PrfErrorCode,
} from '../src/crypto-core/index.js';
import type { SymmetricEncryptedData } from '../src/crypto-core/index.js';

// Helper: create a test member with both key pairs
function createMember() {
    const seed = generateRandomBytes(32);
    const dual = deriveDualKeyPair(seed);
    clearBytes(seed);
    return dual;
}

// Helper: find a member's wrapped CEK in a bundle
function findWrappedCek(memberKeys: { memberPublicKey: Uint8Array; wrappedContentKey: SymmetricEncryptedData }[], memberPubKey: Uint8Array): SymmetricEncryptedData {
    const found = memberKeys.find(wk =>
        wk.memberPublicKey.length === memberPubKey.length &&
        wk.memberPublicKey.every((v, i) => v === memberPubKey[i]));
    if (!found) {
        throw new Error('Member not found in bundle');
    }
    return found.wrappedContentKey;
}

describe('group encryption', () => {
    it('create group with 3 members, all can decrypt', async () => {
        const admin = createMember();
        const member1 = createMember();
        const member2 = createMember();

        const memberPubKeys = [admin.x25519PublicKey, member1.x25519PublicKey, member2.x25519PublicKey];

        const result = await createGroupKeys(
            admin.x25519PrivateKey, admin.x25519PublicKey, memberPubKeys, 'group-test:v1');

        expect(result.success).toBe(true);
        const bundle = result.value!;
        expect(bundle.keyVersion).toBe(1);
        expect(bundle.memberKeys.length).toBe(3);

        // Each member can encrypt and all others can decrypt
        const plaintext = new TextEncoder().encode('group secret');

        const adminWrappedCek = findWrappedCek(bundle.memberKeys, admin.x25519PublicKey);
        const encResult = await encryptForGroup(
            admin.x25519PrivateKey, admin.ed25519PrivateKey, admin.ed25519PublicKey,
            admin.x25519PublicKey, adminWrappedCek, plaintext, 'group-test:v1', 1);

        expect(encResult.success).toBe(true);

        // Member1 decrypts
        const m1WrappedCek = findWrappedCek(bundle.memberKeys, member1.x25519PublicKey);
        const decResult1 = await decryptFromGroup(
            member1.x25519PrivateKey, admin.x25519PublicKey, m1WrappedCek, encResult.value!);

        expect(decResult1.success).toBe(true);
        expect(new TextDecoder().decode(decResult1.value!)).toBe('group secret');

        // Member2 decrypts
        const m2WrappedCek = findWrappedCek(bundle.memberKeys, member2.x25519PublicKey);
        const decResult2 = await decryptFromGroup(
            member2.x25519PrivateKey, admin.x25519PublicKey, m2WrappedCek, encResult.value!);

        expect(decResult2.success).toBe(true);
        expect(new TextDecoder().decode(decResult2.value!)).toBe('group secret');
    });

    it('envelope tamper detection', async () => {
        const admin = createMember();
        const member = createMember();

        const bundle = (await createGroupKeys(
            admin.x25519PrivateKey, admin.x25519PublicKey,
            [admin.x25519PublicKey, member.x25519PublicKey], 'group-tamper:v1')).value!;

        const plaintext = new TextEncoder().encode('sensitive');
        const adminCek = findWrappedCek(bundle.memberKeys, admin.x25519PublicKey);

        const encResult = await encryptForGroup(
            admin.x25519PrivateKey, admin.ed25519PrivateKey, admin.ed25519PublicKey,
            admin.x25519PublicKey, adminCek, plaintext, 'group-tamper:v1', 1);

        // Tamper with the group context
        const tampered = { ...encResult.value!, groupContext: 'group-tamper:v2' };
        const memberCek = findWrappedCek(bundle.memberKeys, member.x25519PublicKey);

        const decResult = await decryptFromGroup(
            member.x25519PrivateKey, admin.x25519PublicKey, memberCek, tampered);

        expect(decResult.success).toBe(false);
        expect(decResult.errorCode).toBe(PrfErrorCode.VerificationFailed);
    });

    it('add member to group', async () => {
        const admin = createMember();
        const member1 = createMember();
        const newMember = createMember();

        const bundle = (await createGroupKeys(
            admin.x25519PrivateKey, admin.x25519PublicKey,
            [admin.x25519PublicKey, member1.x25519PublicKey], 'group-add:v1')).value!;

        const adminCek = findWrappedCek(bundle.memberKeys, admin.x25519PublicKey);

        const addResult = await addGroupMembers(
            admin.x25519PrivateKey, admin.x25519PublicKey, adminCek,
            [newMember.x25519PublicKey], 'group-add:v1');

        expect(addResult.success).toBe(true);
        expect(addResult.value!.length).toBe(1);

        // New member can decrypt
        const plaintext = new TextEncoder().encode('welcome');
        const encResult = await encryptForGroup(
            admin.x25519PrivateKey, admin.ed25519PrivateKey, admin.ed25519PublicKey,
            admin.x25519PublicKey, adminCek, plaintext, 'group-add:v1', 1);

        const newMemberCek = findWrappedCek(addResult.value!, newMember.x25519PublicKey);
        const decResult = await decryptFromGroup(
            newMember.x25519PrivateKey, admin.x25519PublicKey, newMemberCek, encResult.value!);

        expect(decResult.success).toBe(true);
        expect(new TextDecoder().decode(decResult.value!)).toBe('welcome');
    });

    it('key rotation excludes removed member', async () => {
        const admin = createMember();
        const member1 = createMember();
        const removed = createMember();

        const bundle = (await createGroupKeys(
            admin.x25519PrivateKey, admin.x25519PublicKey,
            [admin.x25519PublicKey, member1.x25519PublicKey, removed.x25519PublicKey],
            'group-rotate:v1')).value!;

        // Rotate — exclude removed member
        const rotateResult = await rotateGroupKey(
            admin.x25519PrivateKey, admin.x25519PublicKey,
            [admin.x25519PublicKey, member1.x25519PublicKey], 'group-rotate:v2');

        expect(rotateResult.success).toBe(true);
        const newBundle = rotateResult.value!;
        expect(newBundle.keyVersion).toBe(2);
        expect(newBundle.memberKeys.length).toBe(2);

        // Remaining member can decrypt with new bundle
        const plaintext = new TextEncoder().encode('after rotation');
        const adminCek = findWrappedCek(newBundle.memberKeys, admin.x25519PublicKey);
        const encResult = await encryptForGroup(
            admin.x25519PrivateKey, admin.ed25519PrivateKey, admin.ed25519PublicKey,
            admin.x25519PublicKey, adminCek, plaintext, 'group-rotate:v2', 2);

        const m1Cek = findWrappedCek(newBundle.memberKeys, member1.x25519PublicKey);
        const decResult = await decryptFromGroup(
            member1.x25519PrivateKey, admin.x25519PublicKey, m1Cek, encResult.value!);

        expect(decResult.success).toBe(true);
        expect(new TextDecoder().decode(decResult.value!)).toBe('after rotation');
    });

    it('admin transfer', async () => {
        const oldAdmin = createMember();
        const newAdmin = createMember();
        const member = createMember();

        const bundle = (await createGroupKeys(
            oldAdmin.x25519PrivateKey, oldAdmin.x25519PublicKey,
            [oldAdmin.x25519PublicKey, newAdmin.x25519PublicKey, member.x25519PublicKey],
            'group-transfer:v1')).value!;

        const oldAdminCek = findWrappedCek(bundle.memberKeys, oldAdmin.x25519PublicKey);

        const transferResult = await transferGroupAdmin(
            oldAdmin.x25519PrivateKey, oldAdmin.x25519PublicKey, oldAdminCek,
            newAdmin.x25519PrivateKey, newAdmin.x25519PublicKey,
            [newAdmin.x25519PublicKey, member.x25519PublicKey],
            'group-transfer:v1', 1);

        expect(transferResult.success).toBe(true);
        const newBundle = transferResult.value!;
        expect(newBundle.memberKeys.length).toBe(2);

        // New admin can encrypt, member can decrypt
        const plaintext = new TextEncoder().encode('new admin says hi');
        const newAdminCek = findWrappedCek(newBundle.memberKeys, newAdmin.x25519PublicKey);
        const encResult = await encryptForGroup(
            newAdmin.x25519PrivateKey, newAdmin.ed25519PrivateKey, newAdmin.ed25519PublicKey,
            newAdmin.x25519PublicKey, newAdminCek, plaintext, 'group-transfer:v1', 1);

        const memberCek = findWrappedCek(newBundle.memberKeys, member.x25519PublicKey);
        const decResult = await decryptFromGroup(
            member.x25519PrivateKey, newAdmin.x25519PublicKey, memberCek, encResult.value!);

        expect(decResult.success).toBe(true);
        expect(new TextDecoder().decode(decResult.value!)).toBe('new admin says hi');
    });
});
