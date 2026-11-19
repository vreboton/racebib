import { describe, expect, it } from 'vitest'
import { localTime, scheduleOptions } from './raceConfiguration'

describe('event-zone race scheduling', () => {
  it('round-trips an overnight schedule independently of the browser time zone', () => {
    const zone = 'Asia/Manila'
    const first = scheduleOptions('2026-11-21T23:50', zone)
    const second = scheduleOptions('2026-11-22T00:10', zone)
    expect(first.map(option => option.value)).toEqual(['2026-11-21T15:50:00.000Z'])
    expect(second.map(option => option.value)).toEqual(['2026-11-21T16:10:00.000Z'])
    expect(localTime(new Date(second[0].value), zone)).toBe('2026-11-22T00:10:00')
  })

  it('rejects a skipped local time and exposes both offsets for a repeated local time', () => {
    const zone = 'America/New_York'
    expect(scheduleOptions('2026-03-08T02:30', zone)).toEqual([])
    const repeated = scheduleOptions('2026-11-01T01:30', zone)
    expect(repeated.map(option => option.value)).toEqual([
      '2026-11-01T05:30:00.000Z', '2026-11-01T06:30:00.000Z',
    ])
    expect(repeated.map(option => localTime(new Date(option.value), zone))).toEqual([
      '2026-11-01T01:30:00', '2026-11-01T01:30:00',
    ])
  })
})
