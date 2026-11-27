const shell = new URL('./', self.registration.scope).href
const prefix = 'racebib-checkpoint-shell:' + shell + ':'
const cacheName = prefix + SHELL_VERSION
const assets = new Set(SHELL_FILES.map(file => new URL(file, shell).href))

self.addEventListener('install', event => {
  event.waitUntil((async () => {
    const cache = await caches.open(cacheName)
    await cache.addAll([...assets].map(url => new Request(url, { cache: 'reload' })))
  })())
})

self.addEventListener('activate', event => {
  event.waitUntil((async () => {
    const keys = await caches.keys()
    await Promise.all(keys.filter(key => key.startsWith(prefix) && key !== cacheName).map(key => caches.delete(key)))
    await self.clients.claim()
  })())
})

self.addEventListener('fetch', event => {
  const { request } = event
  if (request.method !== 'GET') return
  const url = new URL(request.url)
  if (url.origin !== self.location.origin || url.pathname.startsWith('/api/')) return
  const navigation = request.mode === 'navigate' &&
    (url.pathname === new URL(shell).pathname || url.pathname === new URL('index.html', shell).pathname)
  if (!navigation && !assets.has(url.href)) return
  event.respondWith((async () => {
    const cache = await caches.open(cacheName)
    const cachedUrl = navigation ? new URL('index.html', shell).href : url.href
    // Same-origin static files have one representation even if the host adds Vary: Origin.
    return await cache.match(cachedUrl, { ignoreVary: true }) ?? fetch(request)
  })())
})
