import test from 'node:test'
import assert from 'node:assert/strict'
import { clockSample, clockWarning, createCapture, loadCaptures, prepareSubmission } from './capture.js'

const station = { eventId: 'event', checkpointId: 'checkpoint', generation: 'grant1' }
const runner = { registrationId: 'runner', raceId: 'race', bibNumber: '0042' }

test('crossings timestamp independently before lookups complete in reverse order', () => {
  let id = 0
  const uuid = () => String(++id)
  const first = createCapture(station, '0042', 'device', { online: true, now: () => 1000, uuid })
  const second = createCapture(station, '0043', 'device', { online: true, now: () => 1250, uuid })
  const secondBody = prepareSubmission(second, { ...runner, bibNumber: '0043' }, 'visit 2')
  const firstBody = prepareSubmission(first, runner, 'visit 1')
  assert.equal(firstBody.observedAtUtc, '1970-01-01T00:00:01.000Z')
  assert.equal(secondBody.observedAtUtc, '1970-01-01T00:00:01.250Z')
  assert.notEqual(firstBody.observationId, secondBody.observationId)
  assert.notEqual(first.key, second.key)
})

test('uncertain submissions recover the same timestamp, grant, operation and exact payload after reload', () => {
  const row = createCapture(station, '0042', 'device', { online: true, now: () => 1000, uuid: () => 'id' })
  row.state = 'Submitting'
  const body = prepareSubmission(row, runner, 'visit')
  const loaded = loadCaptures({ getItem: () => JSON.stringify([row]) }, 'account')[0]
  assert.equal(loaded.state, 'Retry needed')
  assert.equal(loaded.key, row.key)
  assert.deepEqual(prepareSubmission(loaded, { ...runner, raceId: 'other' }, 'different'), body)
  assert.equal(loaded.assignment.generation, 'grant1')
})

test('a capture can be created without connectivity and malformed bibs never become captures', () => {
  assert.equal(createCapture(station, '42', 'device', { uuid: () => 'id' }).state, 'Saved locally')
  assert.throws(() => createCapture(station, 'bib 42', 'device', { online: true }), /32/)
})

test('clock sampling warns for drift and high latency and discards a wall-clock jump', () => {
  const sample = clockSample(1000, 1100, 100, new Date(1050).toISOString())
  assert.deepEqual(sample, { offset: 0, uncertainty: 50, wallEnd: 1100 })
  assert.equal(clockWarning(sample), '')
  assert.match(clockWarning({ ...sample, offset: -2100 }), /differs/)
  assert.match(clockWarning({ ...sample, uncertainty: 501 }), /uncertain/)
  assert.equal(clockSample(1000, 2000, 100, new Date(1050).toISOString()), null)
});
