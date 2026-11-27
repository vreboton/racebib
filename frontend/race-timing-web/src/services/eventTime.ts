export function utcTimestamp(value: string): string {
  return /(?:Z|[+-]\d{2}:\d{2})$/i.test(value) ? value : value + 'Z'
}

export function isTimeZone(zone: string): boolean {
  try {
    new Intl.DateTimeFormat('en', { timeZone: zone })
    return true
  } catch {
    return false
  }
}

export function formatEventTime(value: string | null | undefined, zone: string, dateOnly = false): string {
  if (!value) return ''
  return new Intl.DateTimeFormat(undefined, {
    timeZone: zone, dateStyle: 'medium', ...(dateOnly ? {} : { timeStyle: 'short', hourCycle: 'h23' }),
  }).format(new Date(utcTimestamp(value)))
}
export function localTime(instant: Date, zone: string): string {
  const parts = new Intl.DateTimeFormat('en-CA', {
    timeZone: zone, year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23',
  }).formatToParts(instant)
  const fields = Object.fromEntries(parts.map(({ type, value }) => [type, value]))
  return `${fields.year}-${fields.month}-${fields.day}T${fields.hour}:${fields.minute}:${fields.second}`
}

export function scheduleOptions(value: string, zone: string) {
  if (!isTimeZone(zone)) return []
  const local = value.length === 16 ? value + ':00' : value
  const wall = Date.parse(local + 'Z')
  if (!Number.isFinite(wall)) return []
  const offsets = new Set<number>()
  for (const hours of [-36, 0, 36]) {
    const sample = wall + hours * 3600000
    offsets.add(Date.parse(localTime(new Date(sample), zone) + 'Z') - sample)
  }
  return Array.from(offsets).map(offset => new Date(wall - offset))
    .filter(candidate => localTime(candidate, zone) === local)
    .sort((a, b) => a.getTime() - b.getTime())
    .map(candidate => ({
      value: candidate.toISOString(),
      title: new Intl.DateTimeFormat('en', {
        timeZone: zone, hour: '2-digit', minute: '2-digit', timeZoneName: 'longOffset',
      }).format(candidate),
    }))
}
