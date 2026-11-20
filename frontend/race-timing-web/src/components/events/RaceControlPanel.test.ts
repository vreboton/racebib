import { flushPromises, shallowMount } from '@vue/test-utils'
import { AxiosError, AxiosHeaders, type AxiosResponse } from 'axios'
import { beforeEach, expect, it, vi } from 'vitest'
import RaceControlPanel from './RaceControlPanel.vue'
import { raceControlApi } from '../../services/raceControl'

vi.mock('../../services/raceControl', () => ({
  raceControlApi: { overview: vi.fn(), exceptions: vi.fn(), runners: vi.fn(), audit: vi.fn(), resolution: vi.fn(), resolve: vi.fn() },
}))

beforeEach(() => vi.resetAllMocks())

function response<T>(data: T): AxiosResponse<T> {
  return { data, status: 200, statusText: 'OK', headers: {}, config: { headers: new AxiosHeaders() } }
}

function panel() {
  const slot = { template: '<div><slot /><slot name="append" /></div>' }
  return shallowMount(RaceControlPanel, {
    props: { eventId: 'e' },
    global: { stubs: {
      'v-tabs': { name: 'TestTabs', emits: ['update:modelValue'], template: '<div><slot /></div>' },
      ...Object.fromEntries(['v-alert', 'v-list', 'v-list-item', 'v-list-item-title', 'v-list-item-subtitle',
        'v-tab', 'v-card', 'v-card-title', 'v-card-text', 'v-card-actions', 'v-table',
        'v-dialog', 'v-spacer', 'v-snackbar'].map(name => [name, slot])),
      'v-select': { name: 'TestSelect', props: ['label'], emits: ['update:modelValue'], template: '<div />' },
      'v-textarea': { name: 'TestTextarea', emits: ['update:modelValue'], template: '<div />' },
      'v-btn': { template: '<button><slot /></button>' },
      'v-progress-linear': { template: '<span>Loading Race Control</span>' },
    } },
  })
}

it('shows loading then an empty overview with no armed races', async () => {
  let deliver!: (data: Awaited<ReturnType<typeof raceControlApi.overview>>) => void
  vi.mocked(raceControlApi.overview).mockImplementationOnce(() => new Promise(resolve => { deliver = resolve }))
  const wrapper = panel()
  expect(wrapper.text()).toContain('Loading Race Control')
  deliver(response({ races: [], actionable: 0, bibConflicts: 0 }))
  await flushPromises()
  expect(wrapper.text()).toContain('No races are armed for timing.')
  wrapper.unmount()
})

it('distinguishes denied access from a request failure', async () => {
  const denied = new AxiosError('Forbidden')
  denied.response = { status: 403 } as AxiosError['response']
  vi.mocked(raceControlApi.overview).mockRejectedValueOnce(denied).mockRejectedValueOnce(new Error('offline'))
  const wrapper = panel()
  await flushPromises()
  expect(wrapper.text()).toContain('You do not have permission to access Race Control.')
  await wrapper.findAll('button').find(button => button.text() === 'Refresh')!.trigger('click')
  await flushPromises()
  expect(wrapper.text()).toContain('offline')
  wrapper.unmount()
})

it('loads actionable exceptions and requests the next page', async () => {
  vi.mocked(raceControlApi.overview).mockResolvedValue(response({ races: [], actionable: 26, bibConflicts: 0 }))
  vi.mocked(raceControlApi.exceptions).mockResolvedValueOnce({
    data: { total: 26, items: [{ observationId: 'o', bibNumber: '0042', outcome: 'SlotConflict', revision: 1 }] },
  } as never).mockResolvedValueOnce({ data: { total: 26, items: [] } } as never)
  const wrapper = panel()
  await flushPromises()
  wrapper.findComponent({ name: 'TestTabs' }).vm.$emit('update:modelValue', 'exceptions')
  await flushPromises()
  expect(wrapper.text()).toContain('Bib 0042 · Slot Conflict')
  await wrapper.findAll('button').find(button => button.text() === 'Next')!.trigger('click')
  await flushPromises()
  expect(raceControlApi.exceptions).toHaveBeenLastCalledWith('e', 'evidence', 25, 25)
  expect(wrapper.text()).toContain('No exceptions on this page.')
  wrapper.unmount()
})

it('requires an explicit resolution action and reloads stale revisions', async () => {
  vi.mocked(raceControlApi.overview).mockResolvedValue(response({ races: [], actionable: 1, bibConflicts: 0 }))
  vi.mocked(raceControlApi.exceptions).mockResolvedValue(response({
    total: 1, items: [{ observationId: 'o', bibNumber: '0042', outcome: 'SlotConflict', revision: 1 }],
  }))
  vi.mocked(raceControlApi.resolution).mockResolvedValue(response({
    id: 'o', bibNumber: '0042', observedAtUtc: '2026-11-20T00:00:00Z', receivedAtUtc: '2026-11-20T00:01:00Z',
    acceptedOccurrenceId: null, choices: [], decisions: [{ revision: 1, outcome: 'SlotConflict',
      reason: 'Slot occupied', actorUserId: 'operator', decidedAtUtc: '2026-11-20T00:01:00Z' }],
  }))
  const stale = new AxiosError('Conflict')
  stale.response = { ...response({ code: 'stale_decision', title: 'Reload the observation' }), status: 409 }
  vi.mocked(raceControlApi.resolve).mockRejectedValueOnce(stale)
  const wrapper = panel()
  const button = (title: string) => wrapper.findAll('button').find(b => b.text() === title)!
  await flushPromises()
  wrapper.findComponent({ name: 'TestTabs' }).vm.$emit('update:modelValue', 'exceptions')
  await flushPromises()
  await button('Review').trigger('click')
  await flushPromises()
  wrapper.findComponent({ name: 'TestTextarea' }).vm.$emit('update:modelValue', 'Incorrect crossing')
  await wrapper.vm.$nextTick()
  expect(button('Confirm decision').attributes('disabled')).toBeDefined()
  wrapper.findAllComponents({ name: 'TestSelect' }).find(x => x.props('label') === 'Proposed action')!
    .vm.$emit('update:modelValue', 'invalidate')
  await wrapper.vm.$nextTick()
  expect(button('Confirm decision').attributes('disabled')).toBeUndefined()
  await button('Confirm decision').trigger('click')
  await flushPromises()
  expect(raceControlApi.resolve).toHaveBeenCalledWith('e', 'o', null, 1, 'Incorrect crossing')
  expect(wrapper.text()).toContain('Reload the observation')
  await button('Reload current decision').trigger('click')
  expect(raceControlApi.resolution).toHaveBeenCalledTimes(2)
  wrapper.unmount()
})
