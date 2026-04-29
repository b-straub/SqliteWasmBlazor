<?php
/**
 * cryptosync-relay-gc — time-based delta retention CLI.
 *
 * Deletes rows from `deltas` where `created_at < (now - retention_seconds)`,
 * with `retention_seconds` taken from relay-config.php. Emits a single JSON
 * object on stdout describing the run:
 *
 *   {"deleted": <int>, "oldest_remaining": <unix-seconds>|null}
 *
 * Whitelist entries (table `whitelist` + version row in `whitelist_meta`) are
 * NEVER touched. They transition active → revoked → expired but stay forever
 * to keep `current_version` monotonic across re-additions.
 *
 * Designed for cron:
 *
 *   0 3 * * * cd /path/to/DeltaRelay && php cryptosync-relay-gc.php >> gc.log 2>&1
 *
 * Retention is **lossy** — a receiver offline longer than `retention_seconds`
 * silently misses intervening envelopes. Lossless GC requires snapshot
 * endpoints (deferred — see ROADMAP).
 *
 * This script is NOT web-accessible. Both the Valet driver and .htaccess
 * deny direct HTTP access. Confirm after deploy with:
 *   curl -I https://your-host/cryptosync-relay-gc.php  # expect 403/404
 *
 * Usage:
 *   php cryptosync-relay-gc.php
 *
 * Exit codes:
 *   0  success (printed JSON summary)
 *   1  failure (printed error to stderr; nothing on stdout)
 */

if (php_sapi_name() !== 'cli') {
    http_response_code(404);
    exit;
}

function fail(string $message): never
{
    fwrite(STDERR, $message . PHP_EOL);
    exit(1);
}

$configPath = __DIR__ . '/relay-config.php';
if (!is_file($configPath)) {
    fail("relay-config.php missing at $configPath — run cryptosync-relay-init.php first.");
}

$config = require $configPath;
if (!is_array($config)) {
    fail('relay-config.php did not return an array.');
}
if (!isset($config['retention_seconds']) || !is_int($config['retention_seconds'])) {
    fail("relay-config.php missing integer 'retention_seconds'.");
}
$retentionSeconds = (int)$config['retention_seconds'];
if ($retentionSeconds <= 0) {
    fail('retention_seconds must be a positive integer.');
}

$dbPath = __DIR__ . '/relay.db';
if (!is_file($dbPath)) {
    // No DB yet means nothing to GC. Emit a zero-count summary so cron logs
    // stay uniform across cold deployments.
    echo json_encode([
        'deleted' => 0,
        'oldest_remaining' => null,
    ], JSON_UNESCAPED_SLASHES) . PHP_EOL;
    exit(0);
}

try {
    $pdo = new PDO('sqlite:' . $dbPath);
    $pdo->setAttribute(PDO::ATTR_ERRMODE, PDO::ERRMODE_EXCEPTION);
    $pdo->exec('PRAGMA journal_mode=WAL');
    $pdo->exec('PRAGMA foreign_keys=ON');

    $now = time();
    $threshold = $now - $retentionSeconds;

    $pdo->beginTransaction();

    $del = $pdo->prepare('DELETE FROM deltas WHERE created_at < :t');
    $del->bindValue(':t', $threshold, PDO::PARAM_INT);
    $del->execute();
    $deleted = $del->rowCount();

    $oldest = $pdo->query('SELECT MIN(created_at) FROM deltas')->fetchColumn();

    $pdo->commit();
} catch (Throwable $e) {
    if (isset($pdo) && $pdo->inTransaction()) {
        $pdo->rollBack();
    }
    fail('GC failed: ' . $e->getMessage());
}

echo json_encode([
    'deleted' => $deleted,
    'oldest_remaining' => $oldest === false || $oldest === null ? null : (int)$oldest,
], JSON_UNESCAPED_SLASHES) . PHP_EOL;
