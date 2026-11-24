const running = new Set(['Looking up bib', 'Submitting'])
export const bibPattern = /^[A-Za-z0-9_-]{1,32}$/

export function clockSample(wallStart, wallEnd, elapsed, serverUtc) {
  const serverTime = Date.parse(serverUtc)
  if (!Number.isFinite(serverTime) || elapsed < 0 || Math.abs(wallEnd - wallStart - elapsed) > 100) return null
  return { offset: serverTime - (wallStart + elapsed / 2), uncertainty: elapsed / 2, wallEnd }
}

export function clockWarning(sample) {
  if (!sample) return 'Clock accuracy could not be checked. Verify automatic date and time.'
  if (sample.uncertainty > 500) return 'High network delay makes the clock estimate uncertain.'
  if (Math.abs(sample.offset) > 2000) return 'Device time differs from the server by more than 2 seconds. Check automatic time.'
  return ''
}

export function loadCaptures(storage, key) {
  return JSON.parse(storage.getItem(key) || '[]').map(row => running.has(row.state)
    ? { ...row, state: 'Retry needed' }
    : row)
}

export function createCapture(assignment, bib, deviceId, { online, now = Date.now, uuid = () => crypto.randomUUID() }) {
  if (!online) throw new Error('An online connection is required to capture a crossing.')
  const observedAtUtc = new Date(now()).toISOString()
  if (!bibPattern.test(bib)) throw new Error('Enter a bib with 1–32 letters, digits, hyphens, or underscores.')
  return {
    assignment: { ...assignment }, bib, deviceId, observedAtUtc, observationId: uuid(),
    key: uuid(), state: 'Looking up bib',
  }
}

export function prepareSubmission(row, registration, occurrenceId) {
  if (row.body) return row.body
  row.body = Object.freeze({
    observationId: row.observationId, registrationId: registration.registrationId, raceId: registration.raceId,
    bibNumber: registration.bibNumber, occurrenceId, observedAtUtc: row.observedAtUtc,
    deviceId: row.deviceId, assignmentGeneration: row.assignment.generation, source: 0,
  })
  return row.body
}
