export function retryDelay(attempt) {
  return Math.min(120_000, 2000 * 2 ** Math.min(Math.max(attempt - 1, 0), 6))
}

export function captureLockName(row) {
  return JSON.stringify(['racebib-capture', row.accountId, row.observationId])
}
