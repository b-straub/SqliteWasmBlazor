#!/usr/bin/env php
<?php
declare(strict_types=1);

/**
 * Self-contained DeltaRelay integration tests.
 *
 * The harness copies the relay into a temporary deployment directory, writes a
 * synthetic relay-config.php, starts PHP's built-in server, and exercises the
 * signed wire contract over HTTP. It does not touch DeltaRelay/relay.db.
 */

final class TestFailure extends RuntimeException
{
}

final class KeyPair
{
    public function __construct(
        public readonly string $public,
        public readonly string $secret,
    ) {
    }
}

final class RelayHarness
{
    private string $tempDir;
    private string $baseUrl;
    /** @var resource|null */
    private $process = null;
    /** @var array<int, resource> */
    private array $pipes = [];

    public function __construct(
        private readonly string $repoRoot,
    ) {
    }

    public function start(): void
    {
        $this->tempDir = rtrim(sys_get_temp_dir(), DIRECTORY_SEPARATOR)
            . DIRECTORY_SEPARATOR . 'delta-relay-test-' . bin2hex(random_bytes(8));

        if (!mkdir($this->tempDir, 0700, true) && !is_dir($this->tempDir)) {
            throw new RuntimeException('Could not create temp dir ' . $this->tempDir);
        }

        $relayDir = $this->repoRoot . DIRECTORY_SEPARATOR . 'DeltaRelay';
        foreach (['delta-relay.php', 'index.php'] as $file) {
            if (!copy(
                $relayDir . DIRECTORY_SEPARATOR . $file,
                $this->tempDir . DIRECTORY_SEPARATOR . $file)) {
                throw new RuntimeException('Could not copy ' . $file);
            }
        }

        file_put_contents($this->tempDir . DIRECTORY_SEPARATOR . 'router.php', <<<'PHP'
<?php
$path = parse_url($_SERVER['REQUEST_URI'], PHP_URL_PATH) ?: '/';
$denied = [
    '/relay.db',
    '/relay-config.php',
    '/relay-config.example.php',
    '/cryptosync-relay-init.php',
];
foreach ($denied as $prefix) {
    if (str_starts_with($path, $prefix)) {
        http_response_code(403);
        header('Content-Type: application/json');
        echo json_encode(['error' => 'Forbidden']);
        return true;
    }
}
if (str_starts_with($path, '/api/')) {
    $_GET['path'] = substr($path, strlen('/api/'));
    require __DIR__ . '/delta-relay.php';
    return true;
}
if ($path === '/' || $path === '/index.php') {
    require __DIR__ . '/index.php';
    return true;
}
return false;
PHP);

        $port = $this->findFreePort();
        $this->baseUrl = 'http://127.0.0.1:' . $port . '/';

        $descriptors = [
            0 => ['pipe', 'r'],
            1 => ['file', $this->tempDir . DIRECTORY_SEPARATOR . 'server.out.log', 'a'],
            2 => ['file', $this->tempDir . DIRECTORY_SEPARATOR . 'server.err.log', 'a'],
        ];
        $this->process = proc_open(
            [PHP_BINARY, '-S', '127.0.0.1:' . $port, 'router.php'],
            $descriptors,
            $this->pipes,
            $this->tempDir
        );
        if (!is_resource($this->process)) {
            throw new RuntimeException('Could not start PHP built-in server');
        }
        fclose($this->pipes[0]);

        $deadline = microtime(true) + 5.0;
        do {
            try {
                $response = httpRequest('GET', $this->baseUrl);
                if ($response['status'] === 200) {
                    return;
                }
            } catch (Throwable) {
                usleep(50000);
            }
        } while (microtime(true) < $deadline);

        throw new RuntimeException('PHP built-in server did not become ready at ' . $this->baseUrl);
    }

    public function stop(bool $keepTempDir): void
    {
        if (is_resource($this->process)) {
            proc_terminate($this->process);
            proc_close($this->process);
            $this->process = null;
        }

        if (!$keepTempDir && isset($this->tempDir) && is_dir($this->tempDir)) {
            removeTree($this->tempDir);
        }
    }

    public function baseUrl(): string
    {
        return $this->baseUrl;
    }

    public function tempDir(): string
    {
        return $this->tempDir;
    }

    public function writeConfig(
        string $salt,
        string $adminPub,
        int $readGraceSeconds = 60,
        int $maxBodyBytes = 1048576,
        int $gcThresholdRows = 1000,
    ): void {
        $config = [
            'deployment_salt' => base64_encode($salt),
            'admin_pubkey_hash' => pubkeyHash($salt, $adminPub),
            'read_grace_seconds' => $readGraceSeconds,
            'max_body_bytes' => $maxBodyBytes,
            'rate_limit_window' => 60,
            'rate_limit_count' => 60,
            'gc_threshold_rows' => $gcThresholdRows,
        ];

        $body = "<?php\nreturn " . var_export($config, true) . ";\n";
        file_put_contents($this->tempDir . DIRECTORY_SEPARATOR . 'relay-config.php', $body);
    }

    private function findFreePort(): int
    {
        $socket = stream_socket_server('tcp://127.0.0.1:0', $errno, $errstr);
        if ($socket === false) {
            throw new RuntimeException('Could not allocate test port: ' . $errstr);
        }
        $name = stream_socket_get_name($socket, false);
        fclose($socket);
        if (!is_string($name) || !str_contains($name, ':')) {
            throw new RuntimeException('Could not read allocated test port');
        }
        return (int)substr(strrchr($name, ':'), 1);
    }
}

function main(): int
{
    requireExtensions(['curl', 'pdo_sqlite', 'sodium']);

    $repoRoot = dirname(__DIR__, 2);
    $harness = new RelayHarness($repoRoot);
    $keepTempDir = getenv('DELTA_RELAY_KEEP_TEST_DIR') === '1';

    try {
        $harness->start();

        $salt = random_bytes(32);
        $admin = newKeyPair();
        $user1 = newKeyPair();
        $user2 = newKeyPair();
        $harness->writeConfig($salt, $admin->public, gcThresholdRows: 1);

        $base = $harness->baseUrl();

        $tests = [
            'relay init cli writes valid locked config' =>
                fn() => testRelayInitCli($repoRoot),
            'configured deny rules mention private files' =>
                fn() => testConfiguredDenyRules($repoRoot),
            'private files are denied' => fn() => testPrivateFilesDenied($base),
            'admin whitelist push accepts active users' =>
                fn() => testWhitelistPush($base, $salt, $admin, $user1, $user2),
            'whitelist version replay is rejected' =>
                fn() => testWhitelistReplay($base, $salt, $admin, $user1),
            'forged admin whitelist signature is rejected' =>
                fn() => testForgedAdminSignatureRejected($base, $salt, $admin, $user2),
            'active user can post and active users can fetch broadcast queue' =>
                fn() => testPostAndFetch($base, $user1, $user2),
            'forged sender signature is rejected' =>
                fn() => testForgedSenderSignatureRejected($base, $user2),
            'forged receiver signature is rejected' =>
                fn() => testForgedReceiverSignatureRejected($base, $user2),
            'non-whitelisted sender cannot post' =>
                fn() => testNonWhitelistedSenderDenied($base),
            'revoked user cannot post but can read within grace' =>
                fn() => testRevokedUserGrace($base, $salt, $admin, $user1),
            'expired revoked user cannot read' =>
                fn() => testExpiredRevokedUserDenied($base, $salt, $admin, $user1),
            'oversize body is rejected before storage' =>
                fn() => testBodyCap($harness, $salt, $admin, $user2),
            'admin pin purges prior rows and keeps cursor monotonic' =>
                fn() => testAdminPin($base, $user2, $admin),
            'non-admin pin is rejected' =>
                fn() => testNonAdminPinDenied($base, $user2),
        ];

        $passed = 0;
        foreach ($tests as $name => $test) {
            $test();
            $passed++;
            echo "[PASS] " . $name . PHP_EOL;
        }

        echo PHP_EOL . $passed . ' DeltaRelay integration tests passed at '
            . $base . PHP_EOL;
        return 0;
    } catch (Throwable $e) {
        $keepTempDir = true;
        fwrite(STDERR, PHP_EOL . '[FAIL] ' . $e->getMessage() . PHP_EOL);
        if (isset($harness)) {
            fwrite(STDERR, 'Temp relay dir kept at: ' . $harness->tempDir() . PHP_EOL);
        }
        return 1;
    } finally {
        if (isset($harness)) {
            $harness->stop($keepTempDir);
        }
    }
}

function testRelayInitCli(string $repoRoot): void
{
    $tempDir = rtrim(sys_get_temp_dir(), DIRECTORY_SEPARATOR)
        . DIRECTORY_SEPARATOR . 'delta-relay-init-test-' . bin2hex(random_bytes(8));
    if (!mkdir($tempDir, 0700, true) && !is_dir($tempDir)) {
        throw new RuntimeException('Could not create init temp dir ' . $tempDir);
    }

    try {
        $relayDir = $repoRoot . DIRECTORY_SEPARATOR . 'DeltaRelay';
        foreach (['cryptosync-relay-init.php', 'relay-config.example.php'] as $file) {
            if (!copy(
                $relayDir . DIRECTORY_SEPARATOR . $file,
                $tempDir . DIRECTORY_SEPARATOR . $file)) {
                throw new RuntimeException('Could not copy ' . $file);
            }
        }

        $admin = newKeyPair();
        $adminPubB64 = base64_encode($admin->public);
        $script = $tempDir . DIRECTORY_SEPARATOR . 'cryptosync-relay-init.php';

        $first = runProcess([PHP_BINARY, $script, $adminPubB64], $tempDir);
        assertSame(0, $first['exit'], 'relay init first run exit');

        $configPath = $tempDir . DIRECTORY_SEPARATOR . 'relay-config.php';
        assertTrue(is_file($configPath), 'relay init wrote relay-config.php');
        $mode = fileperms($configPath);
        assertTrue($mode !== false, 'relay-config.php mode can be read');
        assertSame(0600, $mode & 0777, 'relay-config.php mode');

        $config = require $configPath;
        assertTrue(is_array($config), 'relay-config.php returns an array');
        $salt = base64_decode($config['deployment_salt'] ?? '', true);
        assertTrue(is_string($salt) && strlen($salt) === 32, 'deployment_salt is 32 bytes');
        assertSame(
            pubkeyHash($salt, $admin->public),
            $config['admin_pubkey_hash'] ?? null,
            'admin_pubkey_hash');

        $second = runProcess([PHP_BINARY, $script, $adminPubB64], $tempDir);
        assertTrue($second['exit'] !== 0, 'relay init refuses overwrite without --force');

        $forced = runProcess([PHP_BINARY, $script, $adminPubB64, '--force'], $tempDir);
        assertSame(0, $forced['exit'], 'relay init --force exit');
    } finally {
        removeTree($tempDir);
    }
}

function testConfiguredDenyRules(string $repoRoot): void
{
    $htaccess = file_get_contents(
        $repoRoot . DIRECTORY_SEPARATOR . 'DeltaRelay' . DIRECTORY_SEPARATOR . '.htaccess');
    $valet = file_get_contents(
        $repoRoot . DIRECTORY_SEPARATOR . 'DeltaRelay' . DIRECTORY_SEPARATOR . 'LocalValetDriver.php');

    assertTrue(is_string($htaccess), '.htaccess can be read');
    assertTrue(is_string($valet), 'LocalValetDriver.php can be read');

    assertTrue(str_contains($htaccess, 'relay\.db'), '.htaccess denies relay.db');
    assertTrue(str_contains($htaccess, 'relay-config'), '.htaccess denies relay-config');
    assertTrue(
        str_contains($htaccess, 'cryptosync-relay-init\.php'),
        '.htaccess denies cryptosync-relay-init.php');

    foreach (['relay.db', 'relay-config', 'cryptosync-relay-init.php'] as $needle) {
        assertTrue(str_contains($valet, $needle), 'LocalValetDriver denies ' . $needle);
    }
}

function testPrivateFilesDenied(string $base): void
{
    foreach ([
        'relay.db',
        'relay-config.php',
        'relay-config.example.php',
        'cryptosync-relay-init.php',
    ] as $path) {
        assertStatus(403, httpRequest('GET', $base . $path));
    }
}

function testWhitelistPush(
    string $base,
    string $salt,
    KeyPair $admin,
    KeyPair $user1,
    KeyPair $user2,
): void
{
    $response = pushWhitelist($base, $admin, 1, [
        addOp(pubkeyHash($salt, $admin->public)),
        addOp(pubkeyHash($salt, $user1->public)),
        addOp(pubkeyHash($salt, $user2->public)),
    ]);

    assertStatus(200, $response);
    assertSame(1, $response['json']['version'] ?? null, 'whitelist version');
    assertSame(3, $response['json']['operation_count'] ?? null, 'operation_count');
}

function testWhitelistReplay(string $base, string $salt, KeyPair $admin, KeyPair $user1): void
{
    $response = pushWhitelist($base, $admin, 1, [
        addOp(pubkeyHash($salt, $user1->public)),
    ]);

    assertStatus(409, $response);
    assertSame(1, $response['json']['current_version'] ?? null, 'current_version');
}

function testForgedAdminSignatureRejected(
    string $base,
    string $salt,
    KeyPair $admin,
    KeyPair $user2,
): void {
    $response = httpJson('POST', $base . 'api/whitelist', [
        'version' => 2,
        'operations' => [
            addOp(pubkeyHash($salt, $user2->public)),
        ],
        'admin_pubkey' => base64_encode($admin->public),
        'admin_signature' => base64_encode(str_repeat("\x00", 64)),
    ]);

    assertStatus(401, $response);
}

function testPostAndFetch(string $base, KeyPair $user1, KeyPair $user2): void
{
    $posted = postDelta($base, $user1, 'delta-one');
    assertStatus(200, $posted);
    $cursor = (int)($posted['json']['cursor'] ?? 0);
    assertTrue($cursor > 0, 'delta post returns cursor');

    $fetched = getDelta($base, $user2, 0);
    assertStatus(200, $fetched);
    assertSame('delta-one', base64_decode(
        $fetched['json']['envelopes'][0]['envelope'] ?? '',
        true
    ), 'broadcast envelope');

    $empty = getDelta($base, $user2, $cursor);
    assertStatus(200, $empty);
    assertSame($cursor, $empty['json']['cursor'] ?? null, 'since cursor remains current');
    assertSame(0, count($empty['json']['envelopes'] ?? []), 'since cursor suppresses old rows');
}

function testForgedSenderSignatureRejected(string $base, KeyPair $sender): void
{
    $timestamp = (string)time();
    $response = httpJson(
        'POST',
        $base . 'api/delta',
        ['envelope' => base64_encode('forged-send')],
        [
            'X-Timestamp: ' . $timestamp,
            'X-Sender-PubKey: ' . base64_encode($sender->public),
            'X-Sender-Sig: ' . base64_encode(str_repeat("\x00", 64)),
        ]);

    assertStatus(401, $response);
}

function testForgedReceiverSignatureRejected(string $base, KeyPair $puller): void
{
    $timestamp = (string)time();
    $pubkey = base64_encode($puller->public);
    $response = httpRequest(
        'GET',
        $base . 'api/delta?since=0&pubkey=' . rawurlencode($pubkey),
        [
            'X-Timestamp: ' . $timestamp,
            'X-Sig: ' . base64_encode(str_repeat("\x00", 64)),
        ]);

    assertStatus(401, $response);
}

function testNonWhitelistedSenderDenied(string $base): void
{
    $rogue = newKeyPair();
    assertStatus(403, postDelta($base, $rogue, 'rogue-delta'));
}

function testRevokedUserGrace(string $base, string $salt, KeyPair $admin, KeyPair $user1): void
{
    $response = pushWhitelist($base, $admin, 2, [
        revokeOp(pubkeyHash($salt, $user1->public), time()),
    ]);
    assertStatus(200, $response);

    assertStatus(403, postDelta($base, $user1, 'revoked-post'));

    $get = getDelta($base, $user1, 0);
    assertStatus(200, $get);
    assertTrue(count($get['json']['envelopes'] ?? []) > 0, 'revoked user can read during grace');
}

function testExpiredRevokedUserDenied(string $base, string $salt, KeyPair $admin, KeyPair $user1): void
{
    $response = pushWhitelist($base, $admin, 3, [
        revokeOp(pubkeyHash($salt, $user1->public), time() - 120),
    ]);
    assertStatus(200, $response);

    assertStatus(403, getDelta($base, $user1, 0));
}

function testBodyCap(RelayHarness $harness, string $salt, KeyPair $admin, KeyPair $user2): void
{
    $harness->writeConfig($salt, $admin->public, maxBodyBytes: 96, gcThresholdRows: 1);

    $response = postDelta($harness->baseUrl(), $user2, str_repeat('x', 256));
    assertStatus(413, $response);

    $harness->writeConfig($salt, $admin->public, maxBodyBytes: 1048576, gcThresholdRows: 1);
}

function testAdminPin(string $base, KeyPair $user1, KeyPair $admin): void
{
    assertStatus(200, postDelta($base, $user1, 'patch-before-pin'));

    $pin = postDelta($base, $admin, 'admin-seed', pin: true);
    assertStatus(200, $pin);
    assertSame(true, $pin['json']['pinned'] ?? null, 'pinned flag');
    assertTrue(($pin['json']['prior_rows_purged'] ?? 0) >= 2, 'pin purged prior rows');

    $cursor = (int)($pin['json']['cursor'] ?? 0);
    $fetch = getDelta($base, $admin, 0);
    assertStatus(200, $fetch);
    assertSame($cursor, $fetch['json']['envelopes'][0]['cursor'] ?? null, 'pinned cursor');
    assertSame('admin-seed', base64_decode(
        $fetch['json']['envelopes'][0]['envelope'] ?? '',
        true
    ), 'pinned seed');
    assertSame(false, (bool)($fetch['json']['gc_requested'] ?? false), 'gc cleared after pin');
}

function testNonAdminPinDenied(string $base, KeyPair $user1): void
{
    assertStatus(403, postDelta($base, $user1, 'bad-pin', pin: true));
}

/**
 * @return array{status:int, body:string, json:mixed}
 */
function pushWhitelist(string $base, KeyPair $admin, int $version, array $ops): array
{
    $canonical = whitelistCanonical($version, $ops);
    $body = [
        'version' => $version,
        'operations' => $ops,
        'admin_pubkey' => base64_encode($admin->public),
        'admin_signature' => base64_encode(
            sodium_crypto_sign_detached($canonical, $admin->secret)
        ),
    ];

    return httpJson('POST', $base . 'api/whitelist', $body);
}

/**
 * @return array{status:int, body:string, json:mixed}
 */
function postDelta(string $base, KeyPair $sender, string $envelope, bool $pin = false): array
{
    $timestamp = (string)time();
    $hash = hash('sha256', $envelope);
    $headers = [
        'X-Timestamp: ' . $timestamp,
        'X-Sender-PubKey: ' . base64_encode($sender->public),
        'X-Sender-Sig: ' . base64_encode(sodium_crypto_sign_detached(
            'deltapost-v1|' . $timestamp . '|' . $hash,
            $sender->secret
        )),
    ];

    if ($pin) {
        $headers[] = 'X-Admin-Pin-Sig: ' . base64_encode(sodium_crypto_sign_detached(
            'deltapin-v1|' . $timestamp . '|' . $hash,
            $sender->secret
        ));
    }

    return httpJson(
        'POST',
        $base . 'api/delta',
        ['envelope' => base64_encode($envelope)],
        $headers
    );
}

/**
 * @return array{status:int, body:string, json:mixed}
 */
function getDelta(string $base, KeyPair $puller, int $since): array
{
    $timestamp = (string)time();
    $pubkey = base64_encode($puller->public);
    $headers = [
        'X-Timestamp: ' . $timestamp,
        'X-Sig: ' . base64_encode(sodium_crypto_sign_detached(
            'deltaget-v1|' . $timestamp . '|' . $pubkey,
            $puller->secret
        )),
    ];

    return httpRequest(
        'GET',
        $base . 'api/delta?since=' . $since . '&pubkey=' . rawurlencode($pubkey),
        $headers
    );
}

function whitelistCanonical(int $version, array $ops): string
{
    $rows = [];
    foreach ($ops as $op) {
        if ($op['op'] === 'add') {
            $rows[] = 'add:' . $op['pubkey_hash'];
        } else {
            $rows[] = 'revoke:' . $op['pubkey_hash'] . ':' . $op['revoked_at'];
        }
    }
    return 'whitelist-ops-v1|' . $version . '|' . implode('|', $rows);
}

function addOp(string $pubkeyHash): array
{
    return ['op' => 'add', 'pubkey_hash' => $pubkeyHash];
}

function revokeOp(string $pubkeyHash, int $revokedAt): array
{
    return ['op' => 'revoke', 'pubkey_hash' => $pubkeyHash, 'revoked_at' => $revokedAt];
}

function newKeyPair(): KeyPair
{
    $keypair = sodium_crypto_sign_keypair();
    return new KeyPair(
        sodium_crypto_sign_publickey($keypair),
        sodium_crypto_sign_secretkey($keypair)
    );
}

function pubkeyHash(string $salt, string $pubkey): string
{
    return hash('sha256', $salt . $pubkey);
}

/**
 * @return array{status:int, body:string, json:mixed}
 */
function httpJson(string $method, string $url, array $body, array $headers = []): array
{
    $headers[] = 'Content-Type: application/json';
    return httpRequest($method, $url, $headers, json_encode($body, JSON_THROW_ON_ERROR));
}

/**
 * @return array{status:int, body:string, json:mixed}
 */
function httpRequest(string $method, string $url, array $headers = [], ?string $body = null): array
{
    $curl = curl_init($url);
    if ($curl === false) {
        throw new RuntimeException('curl_init failed');
    }

    curl_setopt_array($curl, [
        CURLOPT_CUSTOMREQUEST => $method,
        CURLOPT_HTTPHEADER => $headers,
        CURLOPT_RETURNTRANSFER => true,
        CURLOPT_HEADER => false,
        CURLOPT_TIMEOUT => 10,
    ]);
    if ($body !== null) {
        curl_setopt($curl, CURLOPT_POSTFIELDS, $body);
    }

    $responseBody = curl_exec($curl);
    if ($responseBody === false) {
        $error = curl_error($curl);
        curl_close($curl);
        throw new RuntimeException('curl_exec failed: ' . $error);
    }

    $status = (int)curl_getinfo($curl, CURLINFO_RESPONSE_CODE);
    curl_close($curl);

    $json = json_decode($responseBody, true);
    return ['status' => $status, 'body' => $responseBody, 'json' => $json];
}

function assertStatus(int $expected, array $response): void
{
    if ($response['status'] !== $expected) {
        throw new TestFailure(
            'Expected HTTP ' . $expected . ', got ' . $response['status']
            . ' with body: ' . $response['body']
        );
    }
}

function assertSame(mixed $expected, mixed $actual, string $label): void
{
    if ($expected !== $actual) {
        throw new TestFailure(
            $label . ': expected ' . var_export($expected, true)
            . ', got ' . var_export($actual, true)
        );
    }
}

function assertTrue(bool $condition, string $label): void
{
    if (!$condition) {
        throw new TestFailure($label);
    }
}

function requireExtensions(array $extensions): void
{
    foreach ($extensions as $extension) {
        if (!extension_loaded($extension)) {
            throw new RuntimeException('Required PHP extension missing: ' . $extension);
        }
    }
}

/**
 * @return array{exit:int, stdout:string, stderr:string}
 */
function runProcess(array $command, string $cwd): array
{
    $descriptors = [
        0 => ['pipe', 'r'],
        1 => ['pipe', 'w'],
        2 => ['pipe', 'w'],
    ];
    $process = proc_open($command, $descriptors, $pipes, $cwd);
    if (!is_resource($process)) {
        throw new RuntimeException('proc_open failed');
    }

    fclose($pipes[0]);
    $stdout = stream_get_contents($pipes[1]);
    $stderr = stream_get_contents($pipes[2]);
    fclose($pipes[1]);
    fclose($pipes[2]);
    $exit = proc_close($process);

    return [
        'exit' => $exit,
        'stdout' => is_string($stdout) ? $stdout : '',
        'stderr' => is_string($stderr) ? $stderr : '',
    ];
}

function removeTree(string $path): void
{
    if (!is_dir($path)) {
        return;
    }
    $items = scandir($path);
    if ($items === false) {
        return;
    }
    foreach ($items as $item) {
        if ($item === '.' || $item === '..') {
            continue;
        }
        $child = $path . DIRECTORY_SEPARATOR . $item;
        if (is_dir($child)) {
            removeTree($child);
        } else {
            unlink($child);
        }
    }
    rmdir($path);
}

exit(main());
