import assert from 'node:assert/strict'
import test from 'node:test'
import { generateKeyPairSync, sign } from 'node:crypto'
import { permitTime, verifyStationPermit } from './station-permit.js'

const keys = generateKeyPairSync('ec', { namedCurve: 'prime256v1' })
const pin = keys.publicKey.export({ type: 'spki', format: 'der' }).toString('base64url')
const assignment = { eventId: 'event', checkpointId: 'station', generation: 'grant', offlinePolicyRevision: 'revision',
  entryOpen: true, operationalStartUtc: '2026-01-01T00:00:00Z', operationalEndUtc: '2026-01-04T00:00:00Z' }
const issuedAt = Date.parse('2026-01-01T01:00:00Z') / 1000
function issue(hours) {
  const payload = Buffer.from(JSON.stringify({ id: 'permit', operatorId: 'operator', eventId: 'event',
    checkpointId: 'station', generation: 'grant', policyRevision: 'revision', issuedAt, expiresAt: issuedAt + hours * 3600 }))
  const signature = sign('sha256', payload, { key: keys.privateKey, dsaEncoding: 'ieee-p1363' })
  return { payload: payload.toString('base64url'), signature: signature.toString('base64url') }
}

test('validates pinned signatures, station scope, generation, policy revision, and operational bounds', async () => {
  for (const hours of [12, 24, 36, 48]) {
    const permit = await verifyStationPermit(issue(hours), pin, 'operator', assignment)
    assert.equal(permit.expiresAt - permit.issuedAt, hours * 3600)
  }
  const valid = issue(24)
  const tampered = { ...valid, payload: issue(48).payload }
  assert.equal(await verifyStationPermit(tampered, pin, 'operator', assignment), null)
  assert.equal(await verifyStationPermit(issue(49), pin, 'operator', assignment), null)
  assert.equal(await verifyStationPermit(valid, pin, 'other-account', assignment), null)
  for (const changes of [{ eventId: 'other-event' }, { checkpointId: 'other-station' }, { generation: 'new' },
    { offlinePolicyRevision: 'new' }, { entryOpen: false }, { operationalEndUtc: '2026-01-01T12:00:00Z' }]) {
    assert.equal(await verifyStationPermit(valid, pin, 'operator', { ...assignment, ...changes }), null)
  }
})

test('elapsed time advances through forward adjustments and rejects stored wall-clock rollback', () => {
  const state = { lastWall: 105, lastVerifiedTime: 108, anchorWall: 100, anchorTime: 100, anchorMonotonic: 0 }
  assert.equal(permitTime(state, 105, 8), 108)
  assert.equal(permitTime(state, 109, 20), 120)
  assert.equal(permitTime(state, 500, 20), 500)
  assert.equal(permitTime(state, 104, 20), Infinity)
  assert.equal(permitTime({ ...state, lastVerifiedTime: 500 }, 109, 0), 500)
})
