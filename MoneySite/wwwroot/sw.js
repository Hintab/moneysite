// Service worker so browsers offer "Install app". It deliberately caches nothing:
// the app is live data over a server connection, so every request goes to the network.
self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', e => e.waitUntil(self.clients.claim()));
self.addEventListener('fetch', () => { });
