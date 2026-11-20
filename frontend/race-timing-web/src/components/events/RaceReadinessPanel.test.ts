import { flushPromises, shallowMount } from '@vue/test-utils'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import RaceReadinessPanel from './RaceReadinessPanel.vue'
import { api } from '../../services/api'

vi.mock('../../services/api', () => ({ api: { get: vi.fn() } }))
beforeEach(() => vi.resetAllMocks())
afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

const counts = {
  total: 4, confirmed: 2, pending: 1, waitlisted: 0, withdrawn: 1, historical: 0, confirmedWithoutBib: 1,
  bibsAssigned: 2, confirmedCheckedIn: 1, confirmedNotCheckedIn: 1, inactiveRacesWithRegistrations: 1,
}
const warning = { level: 'actionRequired', count: 1, label: 'Confirmed registrations without bibs' }
const attention = { level: 'attention', count: 1, label: 'Inactive races with registrations' }
const assessment = { level: 'actionRequired', warnings: [warning, attention] }
const report = {
  summary: counts, assessment,
  races: [{ id: 'race-1', name: 'Summit 10K', active: true, counts, assessment },
    { id: 'race-2', name: 'Retired 5K', active: false, counts, assessment: { level: 'attention', warnings: [attention] } }],
}

function mountPanel() {
  const slot = { template: '<div><slot /></div>' }
  const wrapper = shallowMount(RaceReadinessPanel, {
    props: { eventId: 'event-1' },
    global: { stubs: {
      ...Object.fromEntries(['v-card', 'v-card-title', 'v-card-text', 'v-alert', 'v-chip', 'v-divider', 'v-table']
        .map(name => [name, slot])),
      'v-btn': { template: '<button><slot /></button>' },
      'v-select': { name: 'TestSelect', props: ['label'], emits: ['update:modelValue'], template: '<div />' },
    } },
  })
  const select = (label: string, value: string) => wrapper.findAllComponents({ name: 'TestSelect' })
    .find(field => field.props('label') === label)!.vm.$emit('update:modelValue', value)
  const button = (label: string) => wrapper.findAll('button').find(button => button.text() === label)!
  return { wrapper, select, button }
}

it('loads event metrics and warnings and filters only the race breakdown', async () => {
  let deliver!: (response: { data: typeof report }) => void
  vi.mocked(api.get).mockImplementationOnce(() => new Promise(resolve => { deliver = resolve }))
  const { wrapper, select } = mountPanel()
  await wrapper.vm.$nextTick()
  expect(wrapper.text()).toContain('Loading registration readiness')
  expect(api.get).toHaveBeenCalledWith('/events/event-1/readiness')
  deliver({ data: report })
  await flushPromises()
  expect(wrapper.text()).toContain('Action required')
  expect(wrapper.text()).toContain('Attention')
  expect(wrapper.text()).toContain('Check-in progress is informational')
  expect(wrapper.get('dl').text()).toContain('Total registrations4')
  expect(wrapper.text()).toContain('Summit 10K')
  expect(wrapper.text()).toContain('Retired 5K')
  select('Race filter', 'race-1')
  await wrapper.vm.$nextTick()
  expect(wrapper.text()).not.toContain('Retired 5K')
  expect(wrapper.get('dl').text()).toContain('Total registrations4')
  expect(api.get).toHaveBeenCalledTimes(1)
  wrapper.unmount()
})

it('downloads a CSV using the selected race, status, and bib filters', async () => {
  vi.mocked(api.get).mockResolvedValueOnce({ data: report }).mockResolvedValueOnce({
    data: new Blob(['0007'], { type: 'text/csv' }),
    headers: { 'content-disposition': 'attachment; filename="racebib-roster-event-1-2026-11-20.csv"' },
  })
  const createObjectURL = vi.fn(() => 'blob:roster')
  vi.stubGlobal('URL', Object.assign(class extends URL {}, { createObjectURL, revokeObjectURL: vi.fn() }))
  const names: string[] = []
  vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
    names.push(this.download)
  })
  const { wrapper, select, button } = mountPanel()
  await flushPromises()
  select('Race filter', 'race-2')
  select('Registration status', 'Cancelled')
  select('Bib assignment', 'Assigned')
  await button('Export roster CSV').trigger('click')
  await flushPromises()
  expect(api.get).toHaveBeenLastCalledWith('/events/event-1/registrations/export', {
    params: { raceId: 'race-2', status: 'Cancelled', bib: 'Assigned' }, responseType: 'blob',
  })
  expect(createObjectURL).toHaveBeenCalledTimes(1)
  expect(names).toEqual(['racebib-roster-event-1-2026-11-20.csv'])
  wrapper.unmount()
})

it('shows an error, retries, and renders an empty event', async () => {
  vi.mocked(api.get).mockRejectedValueOnce({ response: { data: 'Readiness unavailable.' } })
    .mockResolvedValueOnce({ data: { ...report, summary: { ...counts, total: 0 }, races: [],
      assessment: { level: 'attention', warnings: [] } } })
  const { wrapper, button } = mountPanel()
  await flushPromises()
  expect(wrapper.text()).toContain('Readiness unavailable.')
  await button('Retry').trigger('click')
  await flushPromises()
  expect(wrapper.text()).toContain('This event has no registrations.')
  expect(wrapper.text()).toContain('No races configured.')
  expect(wrapper.text()).not.toContain('Readiness unavailable.')
  wrapper.unmount()
})


it('shows the server limit when an export selection is too large', async () => {
  const message = 'The roster exceeds 5,000 registrations. Narrow the race, status, or bib filter.'
  const errorBlob = Object.assign(new Blob([message], { type: 'text/plain' }), { text: async () => message })
  vi.mocked(api.get).mockResolvedValueOnce({ data: report })
    .mockRejectedValueOnce({ response: { status: 422, data: errorBlob } })
  const { wrapper, button } = mountPanel()
  await flushPromises()
  await button('Export roster CSV').trigger('click')
  await flushPromises()
  expect(wrapper.text()).toContain(message)
  expect(button('Export roster CSV').attributes('disabled')).toBeUndefined()
  wrapper.unmount()
})
