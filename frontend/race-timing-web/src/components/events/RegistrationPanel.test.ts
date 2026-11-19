import { flushPromises, shallowMount } from '@vue/test-utils'
import { expect, it, vi } from 'vitest'
import RegistrationPanel from './RegistrationPanel.vue'
import { api } from '../../services/api'
import { loadConfiguration } from '../../services/raceConfiguration'

vi.mock('../../services/api', () => ({ api: { get: vi.fn(), post: vi.fn() } }))
vi.mock('../../services/raceConfiguration', () => ({ loadConfiguration: vi.fn() }))

it('requires a fresh CSV preview after column mapping changes and resubmits the same file and options', async () => {
  vi.mocked(loadConfiguration).mockResolvedValue({ canEdit: true, timeZoneId: 'UTC', courses: [], races: [] })
  vi.mocked(api.get).mockResolvedValue({ data: { items: [], total: 0 } })
  vi.mocked(api.post)
    .mockResolvedValueOnce({ data: { fields: ['Name on Bib'], mapping: { 'Name on Bib': 0 },
      headers: ['Preferred Name', 'Other'], rows: [], validCount: 1, token: 'old' } })
    .mockResolvedValueOnce({ data: { fields: ['Name on Bib'], mapping: { 'Name on Bib': 1 },
      headers: ['Preferred Name', 'Other'], rows: [], validCount: 1, token: 'current' } })
    .mockResolvedValueOnce({ data: { importedCount: 1 } })
  const slot = { template: '<div><slot /></div>' }
  const fileInput = { name: 'FixtureFileInput', emits: ['update:modelValue'], template: '<div />' }
  const select = { name: 'FixtureSelect', props: ['label'], emits: ['update:modelValue'], template: '<div />' }
  const wrapper = shallowMount(RegistrationPanel, {
    props: { eventId: 'event-1', organizerId: 'org-1' },
    global: { stubs: {
      ...Object.fromEntries(['v-card', 'v-card-title', 'v-card-text', 'v-form', 'v-alert', 'v-list',
        'v-card-actions', 'v-dialog', 'v-table', 'v-text-field', 'v-pagination', 'v-autocomplete', 'v-checkbox', 'v-spacer', 'v-list-item'].map(name => [name, slot])),
      'v-btn': { template: '<button><slot /></button>' },
      'v-file-input': fileInput,
      'v-select': select,
    } },
  })
  const button = (label: string) => wrapper.findAll('button').find(item => item.text() === label)!
  await flushPromises()
  const file = new File(['Preferred Name,Other\nAna,Runner'], 'runners.csv', { type: 'text/csv' })
  wrapper.findComponent({ name: 'FixtureFileInput' }).vm.$emit('update:modelValue', file)
  await flushPromises()
  await button('Validate CSV').trigger('click')
  await flushPromises()
  expect(button('Confirm import').attributes('disabled')).toBeUndefined()
  const mapping = wrapper.findAllComponents({ name: 'FixtureSelect' }).find(item => item.props('label') === 'Name on Bib')!
  mapping.vm.$emit('update:modelValue', 1)
  await flushPromises()
  expect(button('Confirm import').attributes('disabled')).toBeDefined()
  await button('Validate CSV').trigger('click')
  await flushPromises()
  await button('Confirm import').trigger('click')
  await flushPromises()

  const [url, body, config] = vi.mocked(api.post).mock.calls[2]!
  expect(url).toBe('/events/event-1/registrations/import/confirm')
  if (!(body instanceof FormData)) throw new Error('Expected a multipart confirmation body.')
  expect(body.get('file')).toEqual(file)
  expect(body.get('token')).toBe('current')
  expect(body.get('mapping')).toBe('{"Name on Bib":1}')
  expect(config?.headers?.['Content-Type']).toBe('multipart/form-data')
  expect(wrapper.text()).toContain('Imported 1 registrations.')
  wrapper.unmount()
})

it('explains the public registered-name fallback and switches the hint for a pseudonym', async () => {
  vi.mocked(loadConfiguration).mockResolvedValue({ canEdit: true, timeZoneId: 'UTC', courses: [], races: [] })
  vi.mocked(api.get).mockImplementation(async (url) => ({
    data: { items: String(url).endsWith('/runners') ? [{ id: 'runner-1', firstName: 'María', lastName: 'Cruz' }] : [], total: 0 },
  }) as Awaited<ReturnType<typeof api.get>>)
  const slot = { template: '<div><slot /></div>' }
  const textField = { name: 'FixtureTextField', props: ['label', 'hint', 'persistentHint'],
    emits: ['update:modelValue'], template: '<div />' }
  const runnerSelect = { name: 'FixtureRunnerSelect', emits: ['update:modelValue'], template: '<div />' }
  const wrapper = shallowMount(RegistrationPanel, {
    props: { eventId: 'event-1', organizerId: 'org-1' },
    global: { stubs: {
      ...Object.fromEntries(['v-card', 'v-card-title', 'v-card-text', 'v-form', 'v-alert', 'v-list', 'v-select',
        'v-card-actions', 'v-dialog', 'v-table', 'v-pagination', 'v-checkbox', 'v-spacer', 'v-list-item',
        'v-file-input'].map(name => [name, slot])),
      'v-btn': { template: '<button><slot /></button>' },
      'v-text-field': textField,
      'v-autocomplete': runnerSelect,
    } },
  })
  await flushPromises()
  await wrapper.findAll('button').find(button => button.text() === 'Register runner')!.trigger('click')
  await flushPromises()
  wrapper.findComponent({ name: 'FixtureRunnerSelect' }).vm.$emit('update:modelValue', 'runner-1')
  await flushPromises()
  const field = wrapper.findAllComponents({ name: 'FixtureTextField' }).find(item => item.props('label') === 'Name on Bib')!
  expect(field.props('hint')).toBe('Blank publishes the registered full name: María Cruz.')
  expect(wrapper.text()).toContain('Runner ID for CSV matching runner-1')
  field.vm.$emit('update:modelValue', 'Pseudonym')
  await flushPromises()
  expect(field.props('hint')).toBe('Printed bibs and public participant outputs use this name.')
  wrapper.unmount()
})
