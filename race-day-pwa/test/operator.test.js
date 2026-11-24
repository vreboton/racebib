import test from 'node:test'
import assert from 'node:assert/strict'
import { randomUUID } from 'node:crypto'
import { readFile } from 'node:fs/promises'
import { JSDOM } from 'jsdom'
import { build } from 'vite'

const flush = () => new Promise(resolve => setImmediate(resolve))
const json = data => new Response(JSON.stringify(data))
const assignment = { eventId: 'e', checkpointId: 'cp', generation: 'grant', eventName: 'Trail race', name: 'Aid', entryOpen: true }

test('captures on Enter before either lookup returns and retries an uncertain receipt unchanged', async () => {
  const html = await readFile(new URL('../index.html', import.meta.url), 'utf8')
  const output = await build({ logLevel: 'silent', build: { write: false } })
  const bundle = output.output.find(item => item.type === 'chunk' && item.isEntry)
  const dom = new JSDOM(html, { url: 'http://localhost:5174/', runScripts: 'outside-only', pretendToBeVisual: true })
  const window = dom.window
  const get = id => window.document.getElementById(id)
  const pendingLookup = new Map()
  const submissions = []
  let expired = false
  let wall = Date.parse('2026-11-24T05:00:00Z')
  window.Date.now = () => wall
  Object.defineProperty(window.crypto, 'randomUUID', { value: randomUUID })
  window.AbortSignal = AbortSignal
  window.matchMedia = () => ({ matches: false, addEventListener() {}, removeEventListener() {} })
  window.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} }
  window.sessionStorage.setItem('racebib.checkpoint.session', JSON.stringify({ accessToken: 'test', user: { id: 'u' } }))
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
      return json({ observationId: body.observationId, outcome: 'Accepted' })
    }
    throw new Error('Unexpected request ' + url)
  }
  try {
    window.eval(bundle.code)
    await flush()
    assert.equal(get('capture-button').disabled, false)
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
    assert.deepEqual([...pendingLookup.keys()], ['0042', '0043'])
    assert.equal(submissions.length, 0)
    const runner = bib => json({ registrationId: 'r' + bib, raceId: 'race', bibNumber: bib,
      occurrences: [{ occurrenceId: 'visit', sequence: 1, name: 'Aid', distanceKm: 12 }] })
    pendingLookup.get('0043')(runner('0043'))
    await flush()
    pendingLookup.get('0042')(runner('0042'))
    await flush()
    assert.equal(submissions[0].body.observedAtUtc, '2026-11-24T05:00:00.250Z')
    assert.equal(submissions[1].body.observedAtUtc, '2026-11-24T05:00:00.000Z')
    assert.match(get('captures').textContent, /0042 · Retry needed/)
    assert.match(get('captures').textContent, /0043 · Recorded/)
    const retry = [...get('captures').querySelectorAll('button')].find(button => button.textContent === 'Retry capture')
    retry.click()
    await flush()
    assert.deepEqual(submissions[2], submissions[1])
    assert.match(get('captures').textContent, /0042 · Recorded/)
    window.dispatchEvent(new window.Event('offline'))
    assert.equal(get('capture-button').disabled, true)
    get('refresh').click()
    await flush()
    assert.equal(get('capture-button').disabled, false)
    expired = true
    capture('0044')
    await flush()
    assert.equal(get('login').hidden, false)
    assert.match(get('notice').textContent, /Session expired/)
    assert.equal(JSON.parse(window.sessionStorage.getItem('racebib.checkpoint.captures.u'))[0].bib, '0044')
    window.dispatchEvent(new window.Event('offline'))
    assert.equal(get('capture-button').disabled, true)
  } finally {
    window.close()
  }
});
