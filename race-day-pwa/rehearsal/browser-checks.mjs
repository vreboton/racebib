import { sign } from 'node:crypto'
import assert from 'node:assert/strict'
import { readFile, readdir } from 'node:fs/promises'
import { createServer } from 'node:http'
import { chromium } from 'playwright'

const signingKey = process.env.REHEARSAL_PERMIT_PRIVATE_KEY
assert.ok(signingKey, 'Build with a disposable public key and provide its private key to this fixture.')
const dist = new URL('../dist/', import.meta.url)
const files = new Map()
for (const file of await readdir(dist, { recursive: true })) {
  if (file === 'assets') continue
  const type = { html: 'text/html', js: 'text/javascript', css: 'text/css', png: 'image/png',
    webmanifest: 'application/manifest+json' }[file.split('.').at(-1)]
  if (type) files.set('/' + file.replaceAll('\\', '/'), [await readFile(new URL(file, dist)), type])
}

const assignment = { eventId: 'test-event', checkpointId: 'test-checkpoint', name: 'Aid',
  eventName: 'Synthetic event', entryOpen: true, generation: 'test-grant', timeZoneId: 'UTC', offlinePolicyRevision: 'test-policy',
  operationalStartUtc: '2020-01-01T00:00:00Z', operationalEndUtc: '2099-01-01T00:00:00Z' }
const attempts = []
let blockedResponse
let workerVersion = 1
const server = createServer(async (request, response) => {
  const path = new URL(request.url, 'http://localhost').pathname
  function json(value) {
    if (response.destroyed) return
    response.setHeader('Content-Type', 'application/json')
    response.end(JSON.stringify(value))
  }
  if (path.endsWith('/auth/login')) {
    let body = ''
    for await (const chunk of request) body += chunk
    const accountId = JSON.parse(body).email.split('@')[0]
    return json({ accessToken: 'test-session', user: { id: accountId } })
  }
  if (path.endsWith('/auth/logout')) return response.writeHead(204).end()
  if (path.endsWith('/me/timing-clock')) return json({ serverUtc: new Date().toISOString() })
  if (path.endsWith('/me/timing-assignments')) return json([assignment])
  if (path.endsWith('/offline-permit')) {
    const issuedAt = Math.floor(Date.now() / 1000)
    const payload = Buffer.from(JSON.stringify({ id: 'test-permit', operatorId: 'operator', eventId: assignment.eventId,
      checkpointId: assignment.checkpointId, generation: assignment.generation, policyRevision: assignment.offlinePolicyRevision,
      issuedAt, expiresAt: issuedAt + 43200 }))
    return json({ payload: payload.toString('base64url'),
      signature: sign('sha256', payload, { key: signingKey, dsaEncoding: 'ieee-p1363' }).toString('base64url') })
  }
  if (path.endsWith('/registrations')) return json({ registrationId: 'runner', raceId: 'race',
    bibNumber: '0042', occurrences: [{ occurrenceId: 'visit' }] })
  if (path.endsWith('/observations')) {
    let body = ''
    for await (const chunk of request) body += chunk
    attempts.push({ body, key: request.headers['idempotency-key'] })
    if (attempts.length === 1) {
      await new Promise(resolve => { blockedResponse = resolve })
      response.destroy()
      return
    }
    return json({ observationId: JSON.parse(body).observationId, outcome: 'Accepted' })
  }
  const asset = files.get(path === '/' ? '/index.html' : path)
  if (!asset) {
    response.writeHead(404).end()
    return
  }
  response.setHeader('Content-Type', asset[1])
  response.setHeader('Cache-Control', 'no-cache')
  response.end(path === '/service-worker.js' ? asset[0].toString().replace(
    /^const SHELL_VERSION = "(.*?)"/m, (_, version) => 'const SHELL_VERSION = "' + version + '-' + workerVersion + '"'
  ) : asset[0])
})
const origin = await new Promise(resolve => server.listen(0, '127.0.0.1', () =>
  resolve('http://127.0.0.1:' + server.address().port)))
let browser
try {
  browser = await chromium.launch({ executablePath: process.env.REHEARSAL_BROWSER })
  const context = await browser.newContext()
  const first = await context.newPage()
  await first.goto(origin)
  await signIn(first)
  await first.getByRole('button', { name: 'Get offline permit' }).click()
  await first.getByText(/minutes remaining/).waitFor()
  await first.waitForFunction(() => navigator.serviceWorker.controller !== null)
  await context.setOffline(true)
  await first.getByLabel('Runner bib').fill('0042')
  await first.getByRole('button', { name: 'Record crossing' }).click()
  await first.getByText(/0042 .* Saved locally/).waitFor()

  // Update while the old page and queued observation are still active.
  await first.evaluate(() => {
    window.workerController = navigator.serviceWorker.controller
    window.stationMarker = true
  })
  workerVersion += 1
  // Keep API access unavailable to the pages while allowing the worker update request.
  await context.route('**/api/**', route => route.abort())
  await context.setOffline(false)
  await first.evaluate(async () => {
    const registration = await navigator.serviceWorker.ready
    await registration.update()
  })
  await first.waitForFunction(async () => (await navigator.serviceWorker.ready).waiting !== null)
  assert.equal(await first.evaluate(() => window.stationMarker &&
    window.workerController === navigator.serviceWorker.controller), true)
  await first.getByText(/0042 .* Saved locally/).waitFor()
  await first.close()

  const sending = await context.newPage()
  await sending.goto(origin)
  await sending.waitForFunction(async () => (await caches.keys()).some(key => key.endsWith('-2')))
  await sending.getByText(/1 pending/).waitFor()
  await context.unroute('**/api/**')
  await sending.getByRole('button', { name: 'Sign in to sync' }).click()
  await signIn(sending)
  await sending.getByText(/0042 .* Synchronizing/).waitFor()
  await waitFor(() => attempts.length === 1)

  const competing = await context.newPage()
  await competing.goto(origin)
  await competing.getByRole('button', { name: 'Sign in to sync' }).click()
  await signIn(competing)
  await competing.getByRole('button', { name: 'Sync now' }).click()
  await competing.getByText(/0042 .* Retry needed/).waitFor()
  assert.equal(attempts.length, 1)
  await sending.close()
  blockedResponse()
  await competing.waitForFunction(async () => (await navigator.locks.query()).held.length === 0)
  await competing.getByRole('button', { name: 'Sync now' }).click()
  try {
    await competing.getByText(/0042 .* Recorded$/).waitFor({ timeout: 8000 })
  } catch (error) {
    throw new Error('Interrupted replay failed after ' + attempts.length + ' attempts: ' +
      await competing.locator('body').innerText(), { cause: error })
  }
  assert.ok(attempts.length >= 2)
  for (const attempt of attempts.slice(1)) assert.deepEqual(attempt, attempts[0])
  await context.setOffline(true)
  await competing.getByLabel('Runner bib').fill('0049')
  await competing.getByRole('button', { name: 'Record crossing' }).click()
  await competing.getByText(/0049 .* Saved locally/).waitFor()
  const dialog = competing.waitForEvent('dialog')
  void competing.getByRole('button', { name: 'Sign out' }).click()
  const warning = await dialog
  assert.match(warning.message(), /Unsynchronized captures/)
  await warning.accept()
  await competing.getByLabel('Account email').waitFor()
  await competing.reload()
  await competing.getByLabel('Account email').waitFor()
  assert.equal(await competing.getByText(/Bib 0049/).isVisible(), false)
  await context.setOffline(false)
  await signIn(competing, 'other@example.test')
  assert.equal(await competing.getByText(/Bib 0049/).isVisible(), false)
  console.log('Verified waiting service-worker update, durable queue, cross-tab lock, interrupted replay and account isolation.')
} finally {
  blockedResponse?.()
  await browser?.close()
  server.close()
}

async function signIn(page, email = 'operator@example.test') {
  await page.getByLabel('Account email').fill(email)
  await page.getByLabel('Password').fill('synthetic-password')
  await page.getByRole('button', { name: 'Sign in', exact: true }).click()
  await page.getByText(/^Online \|/).waitFor()
}

async function waitFor(condition) {
  const deadline = Date.now() + 5000
  while (!condition()) {
    assert.ok(Date.now() < deadline, 'Timed out waiting for the submission')
    await new Promise(resolve => setTimeout(resolve, 20))
  }
}
