import { flushPromises, shallowMount } from '@vue/test-utils'
import { beforeEach, expect, it, vi } from 'vitest'
import CheckInPanel from './CheckInPanel.vue'
import { api } from '../../services/api'

vi.mock('../../services/api', () => ({ api: { get: vi.fn(), put: vi.fn(), delete: vi.fn() } }))
beforeEach(() => vi.resetAllMocks())

const listUrl = '/events/event-1/registrations/check-in'
const actionUrl = '/events/event-1/registrations/runner-1/check-in'
const runner = {
  id: 'runner-1', version: 1, firstName: 'Lina', lastName: 'Fixture',
  bibNumber: '0007', race: '10K', status: 1, checkedInAtUtc: null, checkInError: null, canReverse: true,
}
const checkedIn = { ...runner, version: 2, checkedInAtUtc: '2026-11-20T06:00:00Z' }

async function mountPanel(items: unknown[] = [runner]) {
  vi.mocked(api.get).mockResolvedValue({ data: { total: items.length, pageSize: 30, items } })
  const slot = { template: '<div><slot /></div>' }
  const wrapper = shallowMount(CheckInPanel, {
    props: { eventId: 'event-1' },
    global: { stubs: {
      ...Object.fromEntries(['v-card', 'v-card-title', 'v-card-text', 'v-card-actions', 'v-form', 'v-dialog', 'v-alert']
        .map(name => [name, slot])),
      'v-btn': { template: '<button><slot /></button>' },
      'v-text-field': { name: 'TestField', props: ['label'], emits: ['update:modelValue'], template: '<input />' },
    } },
  })
  await flushPromises()
  const button = (label: string) => wrapper.findAll('button').find(item => item.text() === label)!
  const enter = (label: string, value: string) =>
    wrapper.findAllComponents({ name: 'TestField' }).find(input => input.props('label') === label)!
      .vm.$emit('update:modelValue', value)
  return { wrapper, button, enter }
}

it('searches by bib or name and submits a single versioned check-in', async () => {
  const { wrapper, button, enter } = await mountPanel()
  for (const term of ['0007', 'Lina', 'Fixture']) {
    enter('Bib number or runner name', '  ' + term + ' ')
    await wrapper.get('#check-in-search').trigger('submit')
    await flushPromises()
    expect(api.get).toHaveBeenLastCalledWith(listUrl, { params: { search: term, page: 1 } })
  }
  let complete!: (response: { data: typeof checkedIn }) => void
  vi.mocked(api.put).mockImplementationOnce(() => new Promise(resolve => { complete = resolve }))
  await button('Check in').trigger('click')
  expect(button('Check in').attributes('disabled')).toBeDefined()
  await button('Check in').trigger('click')
  expect(api.put).toHaveBeenCalledExactlyOnceWith(actionUrl, { version: 1 })
  complete({ data: checkedIn })
  await flushPromises()
  expect(wrapper.text()).toContain('Lina Fixture checked in.')
  expect(button('Reverse check-in')).toBeDefined()
  wrapper.unmount()
})

it('requires a refresh after a conflict and uses the refreshed record', async () => {
  const { wrapper, button } = await mountPanel()
  vi.mocked(api.put).mockRejectedValueOnce({ response: { status: 409, data: {
    code: 'registration_version_conflict', title: 'This registration has changed.',
  } } })
  await button('Check in').trigger('click')
  await flushPromises()
  expect(wrapper.text()).toContain('This registration has changed.')
  expect(button('Check in').attributes('disabled')).toBeDefined()
  vi.mocked(api.get).mockResolvedValueOnce({ data: { total: 1, pageSize: 30, items: [checkedIn] } })
  await button('Refresh results').trigger('click')
  await flushPromises()
  expect(button('Check in')).toBeUndefined()
  expect(button('Reverse check-in')).toBeDefined()
  wrapper.unmount()
})

it('requires a reason and confirmation before reversing a check-in', async () => {
  const { wrapper, button, enter } = await mountPanel([checkedIn])
  await button('Reverse check-in').trigger('click')
  expect(button('Confirm reversal').attributes('disabled')).toBeDefined()
  await wrapper.get('#check-in-reversal').trigger('submit')
  expect(api.delete).not.toHaveBeenCalled()
  enter('Reversal reason', '  Desk correction  ')
  await flushPromises()
  expect(button('Confirm reversal').attributes('disabled')).toBeUndefined()
  vi.mocked(api.delete).mockResolvedValueOnce({ data: { ...runner, version: 3 } })
  await wrapper.get('#check-in-reversal').trigger('submit')
  await flushPromises()
  expect(api.delete).toHaveBeenCalledExactlyOnceWith(actionUrl, { data: { version: 2, reason: 'Desk correction' } })
  expect(wrapper.text()).toContain('Check-in reversed.')
  expect(button('Check in')).toBeDefined()
  wrapper.unmount()
})
