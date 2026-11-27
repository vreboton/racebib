import assert from 'node:assert/strict'
import test from 'node:test'
import { IDBFactory } from 'fake-indexeddb'
import { loadAccountCaptures, openCaptureStore, saveCapture } from './capture-store.js'

test('migrates pending captures once and keeps the latest receipt isolated by account after reopen', async () => {
  globalThis.indexedDB = new IDBFactory()
  const row = {
    observationId: 'observation', key: 'operation', bib: '0042', state: 'Submitting',
    observedAtUtc: '2026-11-27T01:00:00.000Z',
    assignment: { eventId: 'event', checkpointId: 'checkpoint', generation: 'grant' },
    body: { occurrenceId: 'visit' },
  }
  const key = 'racebib.checkpoint.captures.operator'
  const storage = new Map([[key, JSON.stringify([row])]])
  const legacy = { getItem: name => storage.get(name), removeItem: name => storage.delete(name) }

  let db = await openCaptureStore()
  const [pending] = await loadAccountCaptures(db, 'operator', legacy)
  assert.equal(storage.has(key), false)
  assert.equal(pending.state, 'Retry needed')
  assert.equal(pending.retryCount, 0)
  assert.deepEqual(pending.body, row.body)
  assert.deepEqual(await loadAccountCaptures(db, 'other-operator', legacy), [])
  const stale = { ...pending }
  pending.receipt = { observationId: row.observationId, outcome: 'Accepted' }
  pending.state = 'Recorded'
  await saveCapture(db, pending)
  await assert.rejects(saveCapture(db, stale), /another tab/)
  db.close()

  db = await openCaptureStore()
  try {
    storage.set(key, JSON.stringify([row]))
    assert.deepEqual(await loadAccountCaptures(db, 'operator', legacy), [pending])
    assert.equal(storage.has(key), false)
  } finally {
    db.close()
  }
})

test('leaves legacy storage intact when migration aborts', async () => {
  globalThis.indexedDB = new IDBFactory()
  const db = await openCaptureStore()
  try {
    const legacy = {
      getItem: () => JSON.stringify([{ state: 'Retry needed' }]),
      removeItem: () => assert.fail('Aborted migration removed the legacy records'),
    }
    await assert.rejects(loadAccountCaptures(db, 'operator', legacy))
  } finally {
    db.close()
  }
})
