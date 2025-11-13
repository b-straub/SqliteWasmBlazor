// Service Worker Update Notification
window.ServiceWorkerUpdate = {
    updateAvailableCallback: null,
    waitingWorker: null,

    registerUpdateCallback: function (callback) {
        this.updateAvailableCallback = callback;
    },

    skipWaitingAndReload: function () {
        if (this.waitingWorker) {
            this.waitingWorker.postMessage({ type: 'SKIP_WAITING' });
            this.waitingWorker = null;
        }
    },

    init: function () {
        if ('serviceWorker' in navigator) {
            navigator.serviceWorker.addEventListener('controllerchange', () => {
                window.location.reload();
            });

            navigator.serviceWorker.ready.then(registration => {
                registration.addEventListener('updatefound', () => {
                    const newWorker = registration.installing;

                    newWorker.addEventListener('statechange', () => {
                        if (newWorker.state === 'installed' && navigator.serviceWorker.controller) {
                            this.waitingWorker = newWorker;

                            if (this.updateAvailableCallback) {
                                this.updateAvailableCallback.invokeMethodAsync('OnUpdateAvailable');
                            }
                        }
                    });
                });
            });
        }
    }
};

ServiceWorkerUpdate.init();
