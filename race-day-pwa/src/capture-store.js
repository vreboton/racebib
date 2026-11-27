import { loadCaptures } from './capture.js'

export function openCaptureStore() {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open('racebib-checkpoint', 1)
    request.onupgradeneeded = () => {
      const captures = request.result.createObjectStore('captures', { keyPath: ['accountId', 'observationId'] })
      captures.createIndex('account', 'accountId')
    }
    request.onerror = () => reject(request.error)
    request.onblocked = () => reject(new Error('Close other checkpoint tabs and reload.'))
    request.onsuccess = () => {
      const db = request.result
      db.onversionchange = () => db.close()
      resolve(db)
    }
  })
}

function transact(db, mode, operation) {
  return new Promise((resolve, reject) => {
    const transaction = db.transaction('captures', mode, mode === 'readwrite' ? { durability: 'strict' } : undefined)
    const captures = transaction.objectStore('captures')
    let result
    let failure
    transaction.oncomplete = () => resolve(result)
    transaction.onabort = () => reject(failure ?? transaction.error ?? new Error('Capture storage transaction was interrupted.'))
    operation(captures, value => { result = value }, error => {
      failure = error
      transaction.abort()
    })
  })
}

export async function loadAccountCaptures(db, accountId, legacyStorage) {
  const legacyKey = 'racebib.checkpoint.captures.' + accountId
  const legacy = loadCaptures(legacyStorage, legacyKey)
  if (legacy.some(row => typeof row.observationId !== 'string' || typeof row.observedAtUtc !== 'string')) {
    throw new Error('Legacy capture data could not be migrated. The original records are still in this tab.')
  }
  if (legacy.length) {
    await transact(db, 'readwrite', captures => {
      for (const row of legacy) {
        const existing = captures.get([accountId, row.observationId])
        existing.onsuccess = () => {
          if (existing.result) return
          captures.add({
            ...row, accountId, revision: 1, retryCount: 0, clockQuality: null,
            lastSyncError: ['Retry needed', 'Rejected'].includes(row.state) ? row.message || null : null,
          })
        }
      }
    })
    legacyStorage.removeItem(legacyKey)
  }
  return transact(db, 'readonly', (captures, finish) => {
    const request = captures.index('account').getAll(accountId)
    request.onsuccess = () => finish(request.result.map(row =>
      ['Looking up bib', 'Submitting'].includes(row.state) ? { ...row, state: 'Retry needed' } : row
    ).sort((first, second) => second.observedAtUtc.localeCompare(first.observedAtUtc)))
  })
}

export async function saveCapture(db, row) {
  const revision = (row.revision ?? 0) + 1
  const updated = { ...row, revision }
  await transact(db, 'readwrite', (captures, _finish, abort) => {
    if (!row.revision) {
      captures.add(updated)
      return
    }
    const existing = captures.get([row.accountId, row.observationId])
    existing.onsuccess = () => {
      if (existing.result?.revision !== row.revision) {
        abort(new Error('This capture changed in another tab. Reload to see its latest status.'))
        return
      }
      captures.put(updated)
    }
  })
  row.revision = revision
}

export function readCapture(db, accountId, observationId) {
  return transact(db, 'readonly', (captures, finish) => {
    const request = captures.get([accountId, observationId])
    request.onsuccess = () => finish(request.result)
  })
}
