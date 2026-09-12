// test-boot.js — starts Blazor once the run's service worker situation is
// settled, so the bridge's one-time "does a service worker control the page"
// decision is deterministic for the test cases that assert on it.
//
//   crypto plane  — register service-worker.js and wait until it controls
//                   this page (it claims on activate), then start Blazor:
//                   Cancellation_InterruptsRunningStatement runs.
//   ?plane=plain  — register nothing; Blazor starts at once:
//                   Cancellation_UnavailableWithoutServiceWorker runs.
//
// A real .js file rather than an inline script: this app is CSP-strict.

const plane = new URLSearchParams(location.search).get('plane') ?? 'crypto';

async function waitForServiceWorkerControl() {
    if (!('serviceWorker' in navigator)) {
        return;
    }
    await navigator.serviceWorker.register('service-worker.js');
    if (navigator.serviceWorker.controller !== null) {
        return;
    }
    await new Promise(resolve => {
        navigator.serviceWorker.addEventListener('controllerchange', () => resolve(), { once: true });
    });
}

(async () => {
    if (plane !== 'plain') {
        await waitForServiceWorkerControl();
    }
    await Blazor.start();
})();
