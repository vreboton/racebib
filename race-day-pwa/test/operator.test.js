import test from 'node:test'
import assert from 'node:assert/strict'
import { randomUUID } from 'node:crypto'
import { readFile } from 'node:fs/promises'
import { IDBFactory } from 'fake-indexeddb'
import { loadAccountCaptures } from '../src/capture-store.js'
import { JSDOM } from 'jsdom'
import { build } from 'vite'

async function waitFor(condition) {
  const timeout = Date.now() + 2000
  while (!condition()) {
    assert.ok(Date.now() < timeout, 'Timed out waiting for the checkpoint UI')
    await new Promise(resolve => setTimeout(resolve, 5))
  }
}
const json = data => new Response(JSON.stringify(data))
const assignment = { eventId: 'e', checkpointId: 'cp', generation: 'grant', eventName: 'Trail race', timeZoneId: 'Asia/Manila', name: 'Aid', entryOpen: true }

async function operatorFixture() {
  const html = await readFile(new URL('../index.html', import.meta.url), 'utf8')
  const output = await build({ logLevel: 'silent', build: { write: false } })
  const bundle = output.output.find(item => item.type === 'chunk' && item.isEntry)
  const dom = new JSDOM(html, { url: 'http://localhost:5174/', runScripts: 'outside-only', pretendToBeVisual: true })
  const window = dom.window
  const get = id => window.document.getElementById(id)
  Object.defineProperty(window.crypto, 'randomUUID', { value: randomUUID })
  window.indexedDB = new IDBFactory()
  window.AbortSignal = AbortSignal
  window.matchMedia = () => ({ matches: false, addEventListener() {}, removeEventListener() {} })
  window.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} }
  window.sessionStorage.setItem('racebib.checkpoint.session', JSON.stringify({ accessToken: 'test', user: { id: 'u' } }))
  return { window, get, start: () => window.eval(bundle.code) }
}
test('captures on Enter before either lookup returns and retries an uncertain receipt unchanged', async () => {
  const { window, get, start } = await operatorFixture()
  const pendingLookup = new Map()
  const submissions = []
  let expired = false
  let wall = Date.parse('2026-11-24T05:00:00Z')
  window.Date.now = () => wall
  window.fetch = async (url, options) => {
    if (url.endsWith('/timing-clock')) return json({ serverUtc: new Date(wall).toISOString() })
    if (url.endsWith('/timing-assignments')) return json([assignment])
    if (url.includes('/registrations?bib=')) {
      if (expired) return new Response('{}', { status: 401 })
      return new Promise(resolve => pendingLookup.set(new URL(url, window.location).searchParams.get('bib'), resolve))
    }
    if (url.endsWith('/observations')) {
      const body = JSON.parse(options.body)
      submissions.push({ body, key: options.headers['Idempotency-Key'] })
      if (body.bibNumber === '0042' && submissions.filter(x => x.body.bibNumber === '0042').length === 1)
        throw new Error('Connection dropped after send')
      if (body.bibNumber === '0043') return json({
        observationId: body.observationId, outcome: 'SuspiciousClock',
        diagnostic: { code: 'observed_after_timing_window', observedAtUtc: body.observedAtUtc,
          boundaryAtUtc: '2026-11-24T04:59:59.000Z' },
      })
      return json({ observationId: body.observationId, outcome: 'Accepted' })
    }
    throw new Error('Unexpected request ' + url)
  }
  try {
    start()
    await waitFor(() => !get('capture-button').disabled)
    function capture(bib) {
      get('bib').value = bib
      get('capture').dispatchEvent(new window.Event('submit', { cancelable: true }))
    }
    const keypad = [...get('capture').querySelectorAll('.keypad button')]
    const press = label => keypad.find(button => button.textContent === label).click()
    for (const digit of '009') press(digit)
    press('Clear')
    assert.equal(get('bib').value, '')
    for (const digit of '0049') press(digit)
    press('Backspace')
    press('2')
    assert.equal(get('bib').value, '0042')
    get('capture').dispatchEvent(new window.Event('submit', { cancelable: true }))
    wall += 250
    capture('0043')
    await waitFor(() => pendingLookup.size === 2)
    assert.deepEqual([...pendingLookup.keys()], ['0042', '0043'])
    assert.equal(submissions.length, 0)
    const runner = bib => json({ registrationId: 'r' + bib, raceId: 'race', bibNumber: bib,
      occurrences: [{ occurrenceId: 'visit', sequence: 1, name: 'Aid', distanceKm: 12 }] })
    pendingLookup.get('0043')(runner('0043'))
    await waitFor(() => get('captures').textContent.includes('0043 \u00b7 Recorded'))
    pendingLookup.get('0042')(runner('0042'))
    await waitFor(() => get('captures').textContent.includes('0042 \u00b7 Retry needed'))
    assert.equal(submissions[0].body.observedAtUtc, '2026-11-24T05:00:00.250Z')
    assert.equal(submissions[1].body.observedAtUtc, '2026-11-24T05:00:00.000Z')
    assert.match(get('captures').textContent, /0042 · Retry needed/)
    assert.match(get('captures').textContent, /0043 · Recorded · needs review/)
    assert.match(get('captures').textContent, /after the frozen window ends.*boundary.*12:59:59/)
    const crossing = get('captures').querySelector('[title="2026-11-24T05:00:00.250Z"]')
    assert.match(crossing.textContent, /(?:1|13):00:00.*Asia\/Manila/)
    const retry = [...get('captures').querySelectorAll('button')].find(button => button.textContent === 'Retry capture')
    retry.click()
    await waitFor(() => get('captures').textContent.includes('0042 \u00b7 Recorded'))
    assert.deepEqual(submissions[2], submissions[1])
    assert.match(get('captures').textContent, /0042 · Recorded/)
    window.dispatchEvent(new window.Event('offline'))
    assert.equal(get('capture-button').disabled, true)
    get('refresh').click()
    await waitFor(() => !get('capture-button').disabled)
    expired = true
    capture('0044')
    await waitFor(() => !get('login').hidden)
    assert.equal(get('login').hidden, false)
    assert.match(get('notice').textContent, /Session expired/)
    const db = await new Promise((resolve, reject) => {
      const request = window.indexedDB.open('racebib-checkpoint')
      request.onsuccess = () => resolve(request.result)
      request.onerror = () => reject(request.error)
    })
    const saved = await loadAccountCaptures(db, 'u', window.sessionStorage)
    assert.equal(saved.find(row => row.bib === '0044').state, 'Retry needed')
    db.close()
    window.dispatchEvent(new window.Event('offline'))
    assert.equal(get('capture-button').disabled, true)
  } finally {
    window.close()
  }
});

test('scopes checkpoint choices to the selected event and clears them after event changes and completion', async () => {
  const { window, get, start } = await operatorFixture()
  const first = { ...assignment, eventStatus: 'Live' }
  const second = { ...first, checkpointId: 'cp2', name: 'Finish' }
  const other = { ...first, eventId: 'other', eventName: 'Other race', checkpointId: 'cp3', name: 'Summit' }
  const archived = { ...first, eventId: 'archived', eventName: 'Previous race', checkpointId: 'old', eventStatus: 'Completed', entryOpen: false }
  let available = [first, second, other, archived]
  let pollAssignments
  const setInterval = window.setInterval.bind(window)
  window.setInterval = (callback, interval) => {
    if (interval === 15_000) pollAssignments = callback
    return setInterval(callback, interval)
  }
  window.fetch = async url => url.endsWith('/timing-clock')
    ? json({ serverUtc: new Date().toISOString() })
    : json(available)

  try {
    start()
    await waitFor(() => get('assigned-event').options.length === 3)
    const events = get('assigned-event')
    const stations = get('assigned-checkpoint')
    const ids = select => Array.from(select.options, x => x.value)
    function select(input, value) {
      input.value = value
      input.dispatchEvent(new window.Event('change'))
    }
    assert.deepEqual(ids(events), ['', 'e', 'other'])
    assert.equal(stations.disabled, true)
    select(events, 'e')
    assert.deepEqual(ids(stations), ['', 'cp', 'cp2'])
    select(stations, 'cp')
    assert.equal(get('capture-button').disabled, false)
    select(events, 'other')
    assert.deepEqual(ids(stations), ['', 'cp3'])
    assert.equal(stations.value, '')
    assert.equal(get('capture-button').disabled, true)
    select(stations, 'cp3')
    pollAssignments()
    await waitFor(() => get('assigned-checkpoint').value === 'cp3')
    assert.equal(stations.value, 'cp3')
    available = [first, second]
    pollAssignments()
    await waitFor(() => get('assigned-event').options.length === 2)
    assert.deepEqual(ids(events), ['', 'e'])
    assert.equal(events.value, '')
    assert.deepEqual(ids(stations), [''])
    assert.equal(get('capture-button').disabled, true)
  } finally {
    window.close()
  }
})
