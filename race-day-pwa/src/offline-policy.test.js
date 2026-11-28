import assert from 'node:assert/strict'
import test from 'node:test'
import { retryDelay } from './offline-policy.js'

test('automatic retry uses capped exponential backoff', () => {
  assert.deepEqual([1, 2, 3, 4, 100].map(retryDelay), [2000, 4000, 8000, 16000, 120000])
})
