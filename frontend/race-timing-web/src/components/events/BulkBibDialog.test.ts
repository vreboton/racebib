import { flushPromises, shallowMount } from '@vue/test-utils'
import { beforeEach, expect, it, vi } from 'vitest'
import BulkBibDialog from './BulkBibDialog.vue'
import { api } from '../../services/api'
import { loadConfiguration } from '../../services/raceConfiguration'

vi.mock('../../services/api', () => ({ api: { post: vi.fn() } }))
vi.mock('../../services/raceConfiguration', () => ({ loadConfiguration: vi.fn() }))
beforeEach(() => vi.resetAllMocks())

const url = '/events/event-1/registrations/bibs'
const plan = {
  token: 'ticket', scopeCount: 2, eligibleCount: 1, skippedCount: 1, changeCount: 1, error: null,
  rows: [
    { registrationId: 'runner-1', runnerName: 'Lina Fixture', race: '10K', currentBib: null, proposedBib: '0010', skipReason: null },
    { registrationId: 'runner-2', runnerName: 'Ben Fixture', race: '10K', currentBib: '0009', proposedBib: null, skipReason: 'Already checked in.' },
  ],
}

async function mountDialog() {
  vi.mocked(loadConfiguration).mockResolvedValue({ canEdit: true, timeZoneId: 'UTC', courses: [], races: [] })
  const slot = { template: '<div><slot /></div>' }
  const wrapper = shallowMount(BulkBibDialog, {
    props: { eventId: 'event-1' },
    global: { stubs: {
      ...Object.fromEntries(['v-dialog', 'v-card', 'v-card-text', 'v-card-actions', 'v-alert', 'v-form', 'v-table', 'v-spacer']
        .map(name => [name, slot])),
      'v-btn': { template: '<button><slot /></button>' },
      'v-select': { name: 'TestRace', emits: ['update:modelValue'], template: '<div />' },
      'v-text-field': { name: 'TestField', props: ['label'], emits: ['update:modelValue'], template: '<input />' },
    } },
  })
  await flushPromises()
  const button = (label: string) => wrapper.findAll('button').find(item => item.text() === label)!
  const enter = (label: string, value: number) =>
    wrapper.findAllComponents({ name: 'TestField' }).find(input => input.props('label') === label)!
      .vm.$emit('update:modelValue', value)
  return { wrapper, button, enter, submit: () => wrapper.get('#bulk-bib-preview').trigger('submit') }
}

it('previews a race and its skips, then applies only the approved token once', async () => {
  const { wrapper, button, enter, submit } = await mountDialog()
  wrapper.findComponent({ name: 'TestRace' }).vm.$emit('update:modelValue', 'race-1')
  enter('Start bib', 10)
  enter('End bib', 19)
  enter('Minimum digits', 4)

  let deliverPreview!: (response: { data: typeof plan }) => void
  vi.mocked(api.post).mockImplementationOnce(() => new Promise(resolve => { deliverPreview = resolve }))
  await submit()
  await submit()
  expect(api.post).toHaveBeenCalledExactlyOnceWith(url + '/preview', { raceId: 'race-1', start: 10, end: 19, width: 4 })
  deliverPreview({ data: plan })
  await flushPromises()
  expect(wrapper.text()).toContain('0010')
  expect(wrapper.text()).toContain('Already checked in.')
  expect(wrapper.text()).toContain('2 in scope')

  let deliverReceipt!: (response: { data: { assignedCount: number; skippedCount: number } }) => void
  vi.mocked(api.post).mockImplementationOnce(() => new Promise(resolve => { deliverReceipt = resolve }))
  await button('Confirm assignments').trigger('click')
  await button('Confirm assignments').trigger('click')
  expect(api.post).toHaveBeenCalledTimes(2)
  expect(api.post).toHaveBeenLastCalledWith(url + '/confirm', { token: 'ticket' })
  deliverReceipt({ data: { assignedCount: 1, skippedCount: 1 } })
  await flushPromises()
  expect(wrapper.text()).toContain('Assigned 1 bibs. Skipped 1 registrations.')
  expect(wrapper.emitted('assigned')).toHaveLength(1)
  wrapper.unmount()
})

it('invalidates approval on a stale response or a changed range', async () => {
  const { wrapper, button, enter, submit } = await mountDialog()
  vi.mocked(api.post).mockResolvedValueOnce({ data: plan })
    .mockRejectedValueOnce({ response: { status: 409, data: 'This preview is stale. Preview again before confirming.' } })
    .mockResolvedValueOnce({ data: plan })
  await submit()
  await flushPromises()
  await button('Confirm assignments').trigger('click')
  await flushPromises()
  expect(wrapper.text()).toContain('Preview again before confirming.')
  expect(button('Confirm assignments').attributes('disabled')).toBeDefined()
  expect(wrapper.emitted('assigned')).toBeUndefined()

  await submit()
  await flushPromises()
  expect(button('Confirm assignments').attributes('disabled')).toBeUndefined()
  enter('End bib', 50)
  await flushPromises()
  expect(button('Confirm assignments').attributes('disabled')).toBeDefined()
  wrapper.unmount()
})

it('shows range exhaustion without offering confirmation', async () => {
  const { wrapper, button, submit } = await mountDialog()
  vi.mocked(api.post).mockResolvedValueOnce({ data: {
    ...plan, token: null, changeCount: 0, error: 'The bib range has no available numbers for all registrations.',
  } })
  await submit()
  await flushPromises()
  expect(wrapper.text()).toContain('no available numbers')
  expect(button('Confirm assignments').attributes('disabled')).toBeDefined()
  wrapper.unmount()
})
