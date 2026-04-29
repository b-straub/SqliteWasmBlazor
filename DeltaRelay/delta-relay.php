<?php
/**
 * Delta relay — whitelist-authenticated broadcast envelope buffer.
 *
 * Endpoints:
 *
 *   POST /api/whitelist
 *     body: {
 *       "version":         <int>,                                // monotonic
 *       "operations": [
 *         {"op": "add",    "pubkey_hash": "<hex>"},
 *         {"op": "revoke", "pubkey_hash": "<hex>",
 *                          "revoked_at": <unix seconds>}
 *       ],
 *       "admin_pubkey":    "<base64 Ed25519 pub>",
 *       "admin_signature": "<base64 Ed25519 sig>"
 *     }
 *     Verification: sha256(deployment_salt || admin_pubkey) must equal the
 *     hardwired admin_pubkey_hash, version must be > current_version,
 *     signature must verify against the canonical signing string (see
 *     buildWhitelistOpsCanonical below). On success: apply each op in
 *     order under a single transaction and bump current_version.
 *
 *     Op semantics:
 *       - add:    INSERT (pubkey_hash, 'active', NULL); on conflict reset
 *                 status to 'active' + clear revoked_at. Idempotent
 *                 re-activation.
 *       - revoke: UPDATE status='revoked', revoked_at=? WHERE pubkey_hash=?.
 *                 No-op if the hash is unknown.
 *
 *   POST /api/delta
 *     headers: X-Timestamp:    <unix seconds>
 *              X-Sender-PubKey:<base64 Ed25519 pub>
 *              X-Sender-Sig:   <base64 Ed25519 sig over
 *                              "deltapost-v1|" + ts + "|" + sha256(envelope)>
 *     body:    {"envelope": "<base64>"}
 *     Verification: timestamp within +/-300s, body under max_body_bytes,
 *     sender hash on whitelist with status 'active', signature verifies.
 *     Stores ONE row in deltas — broadcast model, no per-recipient fan-out.
 *
 *   GET /api/delta?since=<int>&pubkey=<base64 Ed25519 pub>
 *     headers: X-Timestamp:<unix seconds>
 *              X-Sig:      <base64 Ed25519 sig over
 *                          "deltaget-v1|" + ts + "|" + pubkey>
 *     Verification: timestamp within +/-300s, signature verifies, pubkey
 *     hash on whitelist as 'active' OR ('revoked' AND now-revoked_at <
 *     read_grace_seconds).
 *     Returns: {"cursor":N,"envelopes":[{"cursor":N,"envelope":"base64"}...]}
 *
 * Storage: ./relay.db (SQLite). Tables: whitelist, whitelist_meta, deltas.
 * Per-recipient routing has been eliminated — every whitelisted client
 * polls a single broadcast stream and the receiver's crypto layer drops
 * envelopes addressed to keys it doesn't hold.
 *
 * Server-side keys: zero. The relay never inspects payloads — every
 * confidentiality / integrity guarantee comes from the V2 envelope crypto.
 *
 * Configuration is loaded from relay-config.php (NOT in web root).
 * See relay-config.example.php for the schema.
 */

ini_set('html_errors', '0');

const RECEIVE_WINDOW_SECONDS = 300;
const MAX_ENVELOPES_PER_FETCH = 100;

header('Access-Control-Allow-Origin: *');
header(
    'Access-Control-Allow-Methods: GET, POST, OPTIONS'
);
header(
    'Access-Control-Allow-Headers: '
    . 'Content-Type, X-Sig, X-Timestamp, X-Sender-PubKey, X-Sender-Sig'
);

if ($_SERVER['REQUEST_METHOD'] === 'OPTIONS') {
    http_response_code(204);
    exit;
}

function jsonOut(int $status, array $body): void
{
    http_response_code($status);
    header('Content-Type: application/json');
    echo json_encode($body, JSON_UNESCAPED_SLASHES);
    exit;
}

function serverError(Throwable $e): void
{
    error_log('[delta-relay] ' . $e->getMessage());
    jsonOut(500, ['error' => 'server error']);
}

function loadConfig(): array
{
    static $config = null;
    if ($config !== null) {
        return $config;
    }
    $path = __DIR__ . '/relay-config.php';
    if (!is_file($path)) {
        error_log('[delta-relay] relay-config.php missing — run cryptosync-relay-init.php');
        jsonOut(500, ['error' => 'server error']);
    }
    $loaded = require $path;
    if (!is_array($loaded)) {
        error_log('[delta-relay] relay-config.php did not return an array');
        jsonOut(500, ['error' => 'server error']);
    }
    $required = [
        'deployment_salt', 'admin_pubkey_hash', 'read_grace_seconds',
        'max_body_bytes', 'retention_seconds',
    ];
    foreach ($required as $k) {
        if (!isset($loaded[$k])) {
            error_log("[delta-relay] relay-config.php missing key '$k'");
            jsonOut(500, ['error' => 'server error']);
        }
    }
    $config = $loaded;
    return $config;
}

function db(): PDO
{
    static $pdo = null;
    if ($pdo !== null) {
        return $pdo;
    }
    $pdo = new PDO('sqlite:' . __DIR__ . '/relay.db');
    $pdo->setAttribute(PDO::ATTR_ERRMODE, PDO::ERRMODE_EXCEPTION);
    $pdo->exec('PRAGMA journal_mode=WAL');
    $pdo->exec('PRAGMA foreign_keys=ON');
    $pdo->exec(
        'CREATE TABLE IF NOT EXISTS whitelist (
            pubkey_hash TEXT PRIMARY KEY,
            status      TEXT NOT NULL,
            revoked_at  INTEGER,
            added_at    INTEGER NOT NULL
        )'
    );
    $pdo->exec(
        'CREATE TABLE IF NOT EXISTS whitelist_meta (
            id              INTEGER PRIMARY KEY,
            current_version INTEGER NOT NULL
        )'
    );
    $pdo->exec(
        'INSERT OR IGNORE INTO whitelist_meta (id, current_version)
         VALUES (1, 0)'
    );
    $pdo->exec(
        'CREATE TABLE IF NOT EXISTS deltas (
            cursor     INTEGER PRIMARY KEY AUTOINCREMENT,
            envelope   BLOB NOT NULL,
            created_at INTEGER NOT NULL
        )'
    );
    $pdo->exec(
        'CREATE INDEX IF NOT EXISTS idx_deltas_cursor ON deltas(cursor)'
    );
    return $pdo;
}

function buildWhitelistOpsCanonical(int $version, array $operations): string
{
    // Order-significant: ops apply in admin-supplied order, so the canonical
    // mirrors that order. Tampering reorders the bytes and breaks the sig.
    $rows = [];
    foreach ($operations as $o) {
        if ($o['op'] === 'add') {
            $rows[] = 'add:' . $o['pubkey_hash'];
        } else {
            $rows[] = 'revoke:' . $o['pubkey_hash'] . ':' . $o['revoked_at'];
        }
    }
    return 'whitelist-ops-v1|' . $version . '|' . implode('|', $rows);
}

function pubkeyHash(string $saltB64, string $pubkeyBytes): string
{
    $salt = base64_decode($saltB64, true);
    if ($salt === false) {
        throw new RuntimeException('deployment_salt is not valid base64');
    }
    return hash('sha256', $salt . $pubkeyBytes);
}

function whitelistLookup(string $pubkeyHashHex): ?array
{
    $stmt = db()->prepare(
        'SELECT status, revoked_at FROM whitelist WHERE pubkey_hash = :h'
    );
    $stmt->execute([':h' => $pubkeyHashHex]);
    $row = $stmt->fetch(PDO::FETCH_ASSOC);
    return $row === false ? null : $row;
}

function readBody(int $maxBytes): string
{
    $contentLength = (int)($_SERVER['CONTENT_LENGTH'] ?? 0);
    if ($contentLength > $maxBytes) {
        jsonOut(413, ['error' => 'body exceeds max_body_bytes']);
    }
    $raw = file_get_contents('php://input', false, null, 0, $maxBytes + 1);
    if ($raw === false) {
        jsonOut(400, ['error' => 'failed to read body']);
    }
    if (strlen($raw) > $maxBytes) {
        jsonOut(413, ['error' => 'body exceeds max_body_bytes']);
    }
    return $raw;
}

function timestampWithinWindow(string $tsHeader): bool
{
    if (!ctype_digit($tsHeader)) {
        return false;
    }
    return abs(time() - (int)$tsHeader) <= RECEIVE_WINDOW_SECONDS;
}

// ---------------------------------------------------------------------------
// Routing
// ---------------------------------------------------------------------------

$path = trim($_GET['path'] ?? '', '/');
$method = $_SERVER['REQUEST_METHOD'];

if ($path === 'whitelist' && $method === 'POST') {
    handleWhitelistPush();
}
if ($path === 'delta' && $method === 'POST') {
    handleDeltaPost();
}
if ($path === 'delta' && $method === 'GET') {
    handleDeltaGet();
}

if ($path !== 'delta' && $path !== 'whitelist') {
    jsonOut(404, ['error' => 'unknown route']);
}
jsonOut(405, ['error' => 'method not allowed']);

// ---------------------------------------------------------------------------
// POST /api/whitelist
// ---------------------------------------------------------------------------

function handleWhitelistPush(): void
{
    $config = loadConfig();
    $raw = readBody((int)$config['max_body_bytes']);
    $req = json_decode($raw, true);

    if (!is_array($req)
        || !isset($req['version'], $req['operations'],
                  $req['admin_pubkey'], $req['admin_signature'])
        || !is_int($req['version'])
        || !is_array($req['operations'])
        || !is_string($req['admin_pubkey'])
        || !is_string($req['admin_signature'])) {
        jsonOut(400, ['error' => 'malformed whitelist push']);
    }

    $version = $req['version'];
    $operations = $req['operations'];

    if (count($operations) === 0) {
        jsonOut(400, ['error' => 'operations must be non-empty']);
    }

    foreach ($operations as $o) {
        if (!is_array($o)
            || !isset($o['op'], $o['pubkey_hash'])
            || !is_string($o['op'])
            || !is_string($o['pubkey_hash'])
            || !ctype_xdigit($o['pubkey_hash'])
            || strlen($o['pubkey_hash']) !== 64
            || !in_array($o['op'], ['add', 'revoke'], true)) {
            jsonOut(400, ['error' => 'malformed whitelist operation']);
        }
        if ($o['op'] === 'revoke'
            && (!isset($o['revoked_at']) || !is_int($o['revoked_at']))) {
            jsonOut(400, ['error' => 'revoke op missing revoked_at']);
        }
    }

    $adminPubBytes = base64_decode($req['admin_pubkey'], true);
    $adminSigBytes = base64_decode($req['admin_signature'], true);
    if ($adminPubBytes === false || strlen($adminPubBytes) !== 32
        || $adminSigBytes === false || strlen($adminSigBytes) !== 64) {
        jsonOut(401, ['error' => 'invalid admin_pubkey or admin_signature']);
    }

    try {
        $adminHashHex = pubkeyHash($config['deployment_salt'], $adminPubBytes);
    } catch (Throwable $e) {
        serverError($e);
        return;
    }
    if (!hash_equals($config['admin_pubkey_hash'], $adminHashHex)) {
        jsonOut(401, ['error' => 'admin pubkey hash does not match deployment']);
    }

    $signingInput = buildWhitelistOpsCanonical($version, $operations);
    if (!sodium_crypto_sign_verify_detached(
            $adminSigBytes, $signingInput, $adminPubBytes)) {
        jsonOut(401, ['error' => 'admin signature verification failed']);
    }

    try {
        $pdo = db();
        $pdo->beginTransaction();

        $cur = (int)$pdo->query(
            'SELECT current_version FROM whitelist_meta WHERE id = 1'
        )->fetchColumn();
        if ($version <= $cur) {
            $pdo->rollBack();
            jsonOut(409, [
                'error' => 'version not greater than current_version',
                'current_version' => $cur,
            ]);
        }

        $now = time();
        $addStmt = $pdo->prepare(
            'INSERT INTO whitelist (pubkey_hash, status, revoked_at, added_at)
             VALUES (:h, ' . "'active'" . ', NULL, :t)
             ON CONFLICT(pubkey_hash) DO UPDATE SET
                status = ' . "'active'" . ',
                revoked_at = NULL'
        );
        $revokeStmt = $pdo->prepare(
            'UPDATE whitelist
             SET status = ' . "'revoked'" . ', revoked_at = :r
             WHERE pubkey_hash = :h'
        );

        foreach ($operations as $o) {
            if ($o['op'] === 'add') {
                $addStmt->execute([
                    ':h' => $o['pubkey_hash'],
                    ':t' => $now,
                ]);
            } else {
                $revokeStmt->execute([
                    ':h' => $o['pubkey_hash'],
                    ':r' => $o['revoked_at'],
                ]);
            }
        }
        $pdo->prepare(
            'UPDATE whitelist_meta SET current_version = :v WHERE id = 1'
        )->execute([':v' => $version]);

        $pdo->commit();
    } catch (Throwable $e) {
        if (db()->inTransaction()) {
            db()->rollBack();
        }
        serverError($e);
        return;
    }

    jsonOut(200, ['version' => $version, 'operation_count' => count($operations)]);
}

// ---------------------------------------------------------------------------
// POST /api/delta
// ---------------------------------------------------------------------------

function handleDeltaPost(): void
{
    $config = loadConfig();

    $tsHeader = $_SERVER['HTTP_X_TIMESTAMP'] ?? '';
    $senderPubB64 = $_SERVER['HTTP_X_SENDER_PUBKEY'] ?? '';
    $senderSigB64 = $_SERVER['HTTP_X_SENDER_SIG'] ?? '';
    if ($tsHeader === '' || $senderPubB64 === '' || $senderSigB64 === '') {
        jsonOut(401, [
            'error' => 'missing X-Timestamp, X-Sender-PubKey, or X-Sender-Sig',
        ]);
    }
    if (!timestampWithinWindow($tsHeader)) {
        jsonOut(401, [
            'error' => 'timestamp outside +/-' . RECEIVE_WINDOW_SECONDS . 's window',
        ]);
    }

    $raw = readBody((int)$config['max_body_bytes']);
    $req = json_decode($raw, true);
    if (!is_array($req) || !isset($req['envelope'])
        || !is_string($req['envelope'])) {
        jsonOut(400, ['error' => 'expected {envelope: base64}']);
    }
    $envelope = base64_decode($req['envelope'], true);
    if ($envelope === false) {
        jsonOut(400, ['error' => 'envelope is not valid base64']);
    }

    $senderPubBytes = base64_decode($senderPubB64, true);
    $senderSigBytes = base64_decode($senderSigB64, true);
    if ($senderPubBytes === false || strlen($senderPubBytes) !== 32
        || $senderSigBytes === false || strlen($senderSigBytes) !== 64) {
        jsonOut(401, ['error' => 'invalid sender pubkey or signature encoding']);
    }

    try {
        $senderHashHex = pubkeyHash($config['deployment_salt'], $senderPubBytes);
    } catch (Throwable $e) {
        serverError($e);
        return;
    }

    $row = whitelistLookup($senderHashHex);
    if ($row === null || $row['status'] !== 'active') {
        jsonOut(403, ['error' => 'sender not whitelisted as active']);
    }

    $envelopeHashHex = hash('sha256', $envelope);
    $signingInput = 'deltapost-v1|' . $tsHeader . '|' . $envelopeHashHex;
    if (!sodium_crypto_sign_verify_detached(
            $senderSigBytes, $signingInput, $senderPubBytes)) {
        jsonOut(401, ['error' => 'sender signature verification failed']);
    }

    try {
        $stmt = db()->prepare(
            'INSERT INTO deltas (envelope, created_at) VALUES (:env, :ts)'
        );
        $stmt->execute([':env' => $envelope, ':ts' => time()]);
        $cursor = (int)db()->lastInsertId();
    } catch (Throwable $e) {
        serverError($e);
        return;
    }

    jsonOut(200, ['cursor' => $cursor]);
}

// ---------------------------------------------------------------------------
// GET /api/delta?since=N&pubkey=PK
// ---------------------------------------------------------------------------

function handleDeltaGet(): void
{
    $config = loadConfig();

    $pubkeyB64 = $_GET['pubkey'] ?? '';
    $since = (int)($_GET['since'] ?? 0);
    if ($pubkeyB64 === '') {
        jsonOut(400, ['error' => 'pubkey query param required']);
    }

    $tsHeader = $_SERVER['HTTP_X_TIMESTAMP'] ?? '';
    $sigB64 = $_SERVER['HTTP_X_SIG'] ?? '';
    if ($tsHeader === '' || $sigB64 === '') {
        jsonOut(401, ['error' => 'missing X-Timestamp or X-Sig']);
    }
    if (!timestampWithinWindow($tsHeader)) {
        jsonOut(401, [
            'error' => 'timestamp outside +/-' . RECEIVE_WINDOW_SECONDS . 's window',
        ]);
    }

    $pubBytes = base64_decode($pubkeyB64, true);
    $sigBytes = base64_decode($sigB64, true);
    if ($pubBytes === false || strlen($pubBytes) !== 32
        || $sigBytes === false || strlen($sigBytes) !== 64) {
        jsonOut(401, ['error' => 'invalid pubkey or signature encoding']);
    }

    $signingInput = 'deltaget-v1|' . $tsHeader . '|' . $pubkeyB64;
    if (!sodium_crypto_sign_verify_detached(
            $sigBytes, $signingInput, $pubBytes)) {
        jsonOut(401, ['error' => 'signature verification failed']);
    }

    try {
        $pubHashHex = pubkeyHash($config['deployment_salt'], $pubBytes);
    } catch (Throwable $e) {
        serverError($e);
        return;
    }

    $row = whitelistLookup($pubHashHex);
    if ($row === null) {
        jsonOut(403, ['error' => 'pubkey not whitelisted']);
    }
    if ($row['status'] === 'revoked') {
        $revokedAt = (int)($row['revoked_at'] ?? 0);
        $graceWindow = (int)$config['read_grace_seconds'];
        if (time() - $revokedAt >= $graceWindow) {
            jsonOut(403, ['error' => 'read grace window expired']);
        }
    }

    try {
        $stmt = db()->prepare(
            'SELECT cursor, envelope FROM deltas
             WHERE cursor > :since
             ORDER BY cursor ASC LIMIT :lim'
        );
        $stmt->bindValue(':since', $since, PDO::PARAM_INT);
        $stmt->bindValue(':lim', MAX_ENVELOPES_PER_FETCH, PDO::PARAM_INT);
        $stmt->execute();
        $rows = $stmt->fetchAll(PDO::FETCH_ASSOC);
    } catch (Throwable $e) {
        serverError($e);
        return;
    }

    $envelopes = [];
    $maxCursor = $since;
    foreach ($rows as $r) {
        $cursor = (int)$r['cursor'];
        $envelopes[] = [
            'cursor' => $cursor,
            'envelope' => base64_encode($r['envelope']),
        ];
        if ($cursor > $maxCursor) {
            $maxCursor = $cursor;
        }
    }
    jsonOut(200, ['cursor' => $maxCursor, 'envelopes' => $envelopes]);
}
