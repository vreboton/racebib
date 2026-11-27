import { flushPromises, shallowMount } from '@vue/test-utils'
import { expect, it, vi } from 'vitest'
import TimingSetupPanel from './TimingSetupPanel.vue'
import { api } from '../../services/api'

vi.mock('../../services/api', () => ({ api: { get: vi.fn() } }))

it('shows the event-local schedule, frozen window and mismatch for an armed race', async () => {
  vi.mocked(api.get).mockResolvedValue({
    data: {
      eventSchedule: { startUtc: '2026-11-27T04:00:00Z', endUtc: '2026-11-27T09:00:00Z', timeZoneId: 'Asia/Manila' },
      races: [{ id: 'r', name: '21K', startAtUtc: '2026-11-26T22:00:00Z', waves: [] }],
      setups: [{ raceId: 'r', courseId: 'course', windowStartUtc: '2026-11-26T22:00:00Z',
        windowEndUtc: '2026-11-27T03:40:00Z', scheduledGunUtc: '2026-11-26T22:00:00Z',
        armedAtUtc: '2026-11-27T03:51:26Z', windowStatus: 'Expired', differsFromEventSchedule: true }],
      guns: [{ id: 'g', raceId: 'r', waveId: null, gunAtUtc: '2026-11-26T22:05:00Z' }],
      locations: [], courses: [], assignments: [], mappings: [],
    },
  })
  const slot = { template: '<div><slot /></div>' }
  const wrapper = shallowMount(TimingSetupPanel, {
    props: { eventId: 'event', zone: 'UTC' },
    global: { stubs: Object.fromEntries(['v-alert', 'v-card', 'v-card-text', 'v-progress-linear',
      'v-form', 'v-text-field', 'v-btn', 'v-select', 'v-chip'].map(name => [name, slot])) },
  })
  await flushPromises()
  expect(wrapper.text()).toContain('differs from this race')
  expect(wrapper.text()).toContain('Expired · Asia/Manila')
  const values = wrapper.findAll('dd').map(item => item.text())
  expect(values[0]).toMatch(/12:00:00.*17:00:00/)
  expect(values[2]).toMatch(/06:00:00.*11:40:00/)
  expect(values[4]).toContain('06:05:00')
  wrapper.unmount()
})
