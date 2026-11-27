import assert from 'node:assert/strict'
import test from 'node:test'
import { offlineLifetime, retryDelay, validOfflineSnapshot } from './offline-policy.js'

test('offline access expires exactly at the configured deadline and rejects future snapshots', () => {
  const snapshot = { verifiedAt: 500 }
  assert.equal(validOfflineSnapshot(snapshot, 499), false)
  assert.equal(validOfflineSnapshot(snapshot, 500), true)
  assert.equal(validOfflineSnapshot(snapshot, 500 + offlineLifetime - 1), true)
  assert.equal(validOfflineSnapshot(snapshot, 500 + offlineLifetime), false)
})

test('automatic retry uses capped exponential backoff', () => {
  assert.deepEqual([1, 2, 3, 4, 100].map(retryDelay), [2000, 4000, 8000, 16000, 120000])
})
