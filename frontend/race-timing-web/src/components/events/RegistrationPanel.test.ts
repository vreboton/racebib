import { flushPromises, shallowMount } from '@vue/test-utils'
import { expect, it, vi } from 'vitest'
import RegistrationPanel from './RegistrationPanel.vue'
import { api } from '../../services/api'
import { loadConfiguration, type Race } from '../../services/raceConfiguration'

vi.mock('../../services/api', () => ({ api: { get: vi.fn(), post: vi.fn() } }))
vi.mock('../../services/raceConfiguration', () => ({ loadConfiguration: vi.fn() }))

const activeRace = { id: 'race-1', name: 'Summit', status: 'Active' } as Race
const inactiveRace = { id: 'race-retired', name: 'Retired', status: 'Inactive' } as Race

async function mountImport(races = [activeRace, inactiveRace]) {
  vi.mocked(loadConfiguration).mockResolvedValue({ canEdit: true, timeZoneId: 'UTC', courses: [], races })
  vi.mocked(api.get).mockResolvedValue({ data: { items: [], total: 0 } })
  const slot = { template: '<div><slot /></div>' }
  const fileInput = { name: 'FixtureFileInput', emits: ['update:modelValue'], template: '<div />' }
  const select = { name: 'FixtureSelect', props: ['label', 'items'], emits: ['update:modelValue'], template: '<div />' }
  const wrapper = shallowMount(RegistrationPanel, {
    props: { eventId: 'event-1', organizerId: 'org-1' },
    global: { stubs: {
      ...Object.fromEntries(['v-card', 'v-card-title', 'v-card-text', 'v-form', 'v-alert', 'v-list',
        'v-card-actions', 'v-dialog', 'v-table', 'v-text-field', 'v-pagination', 'v-autocomplete', 'v-checkbox', 'v-spacer']
        .map(name => [name, slot])),
      'v-btn': { props: ['href'], template: '<a v-if="href" :href="href"><slot /></a><button v-else><slot /></button>' },
      'v-file-input': fileInput,
      'v-select': select,
      'v-list-item': { props: ['subtitle'], template: '<div>{{ subtitle }}<slot /></div>' },
    } },
  })
  await flushPromises()
  const button = (label: string) => wrapper.findAll('button').find(item => item.text() === label)!
  await button('Import CSV').trigger('click')
  await flushPromises()
  const upload = async (csv: string) => {
    const file = new File([csv], 'runners.csv', { type: 'text/csv' })
    wrapper.findComponent({ name: 'FixtureFileInput' }).vm.$emit('update:modelValue', file)
    await flushPromises()
    return file
  }
  const defaultRace = wrapper.findAllComponents({ name: 'FixtureSelect' }).find(item => item.props('label') === 'Default Race Category')!
  const selectDefault = async () => {
    defaultRace.vm.$emit('update:modelValue', activeRace.id)
    await flushPromises()
  }
  return { wrapper, button, upload, defaultRace, selectDefault }
}

const canonical = 'First Name,Last Name,Name on Bib,Gender,Birthdate,Email,Mobile Number,Race,Category,Start Wave,Bib Number,Team,Emergency Contact'

it.each([
  canonical + '\r\nMara,Ventura,小光,,,mara@example.test,,Summit,,,0007,,',
  'Race,Bib Number,Email,Last Name,First Name\nSummit,0007,mara@example.test,Ventura,Mara',
])('offers the offline template and previews and confirms the selected CSV without mapping', async csv => {
  vi.mocked(api.post).mockResolvedValueOnce({ data: { rows: [], validCount: 1, token: 'current' } })
    .mockResolvedValueOnce({ data: { importedCount: 1 } })
  const { wrapper, button, upload, defaultRace, selectDefault } = await mountImport()
  const link = wrapper.find('a[download="racebib-registration-template.csv"]')
  expect(link.text()).toBe('Download CSV Template')
  expect(link.element.parentElement?.tagName).toBe('P')
  expect(link.attributes('class')).toBeUndefined()
  const uploadInput = wrapper.findComponent({ name: 'FixtureFileInput' }).element
  const bibAssignment = wrapper.findAllComponents({ name: 'FixtureSelect' })
    .filter(item => item.props('label') === 'Bib assignment').at(-1)!.element
  expect(uploadInput.compareDocumentPosition(bibAssignment) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  expect(bibAssignment.compareDocumentPosition(defaultRace.element) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  expect(decodeURIComponent(link.attributes('href')?.split(',')[1] ?? '')).toBe('\uFEFF' + canonical + '\r\n')
  expect(api.post).not.toHaveBeenCalled()
  const file = await upload(csv)
  expect(button('Validate & Preview').attributes('disabled')).toBeDefined()
  expect(defaultRace.props('items')).toEqual([activeRace])
  await selectDefault()
  await button('Validate & Preview').trigger('click')
  await flushPromises()
  expect(button('Confirm Import').attributes('disabled')).toBeUndefined()
  await button('Confirm Import').trigger('click')
  await flushPromises()

  const [, previewBody] = vi.mocked(api.post).mock.calls[0]!
  const [url, body, config] = vi.mocked(api.post).mock.calls[1]!
  expect(url).toBe('/events/event-1/registrations/import/confirm')
  if (!(body instanceof FormData) || !(previewBody instanceof FormData)) throw new Error('Expected multipart forms.')
  expect(body.get('file')).toEqual(file)
  expect(body.get('token')).toBe('current')
  expect(body.get('assignment')).toBe(previewBody.get('assignment'))
  expect(body.get('defaultRaceId')).toBe(activeRace.id)
  expect(previewBody.get('defaultRaceId')).toBe(activeRace.id)
  expect(body.has('mapping')).toBe(false)
  expect(previewBody.has('mapping')).toBe(false)
  expect(config?.headers?.['Content-Type']).toBe('multipart/form-data')
  expect(wrapper.text()).toContain('Imported 1 registrations.')
  wrapper.unmount()
})

it.each([
  ['First Name,Last Name\nA,B', 'Missing required headers: Email'],
  ['First Name,Last Name,Race,Race\nA,B,10,10', "Duplicate header 'Race'"],
  ['First Name,Last Name,Race,Preferred Name\nA,B,10,Alias', 'Unrecognized header in column 4'],
  ['First Name,Last Name,Race\nA,Re"yes,10', 'Upload a well-formed UTF-8 CSV'],
])('shows CSV validation feedback and prevents confirmation for %s', async (csv, message) => {
  if (csv.includes('Re"yes')) vi.mocked(api.post).mockRejectedValueOnce({ response: { data: message } })
  else vi.mocked(api.post).mockResolvedValueOnce({ data: { rows: [], validCount: 0, token: null, error: message } })
  const { wrapper, button, upload, selectDefault } = await mountImport()
  await upload(csv)
  await selectDefault()
  await button('Validate & Preview').trigger('click')
  await flushPromises()
  expect(wrapper.text()).toContain(message)
  expect(button('Confirm Import').attributes('disabled')).toBeDefined()
  wrapper.unmount()
})

it('invalidates a preview when the bib assignment or file changes', async () => {
  vi.mocked(api.post).mockResolvedValue({ data: { rows: [], validCount: 1, token: 'current' } })
  const { wrapper, button, upload, selectDefault } = await mountImport()
  await upload('First Name,Last Name,Email\nA,B,a@example.test')
  await selectDefault()
  await button('Validate & Preview').trigger('click')
  await flushPromises()
  expect(button('Confirm Import').attributes('disabled')).toBeUndefined()

  const modes = wrapper.findAllComponents({ name: 'FixtureSelect' }).filter(item => item.props('label') === 'Bib assignment')
  modes.at(-1)!.vm.$emit('update:modelValue', 2)
  await flushPromises()
  expect(button('Confirm Import').attributes('disabled')).toBeDefined()

  await button('Validate & Preview').trigger('click')
  await flushPromises()
  expect(button('Confirm Import').attributes('disabled')).toBeUndefined()
  await upload('First Name,Last Name,Race\nC,D,10')
  expect(button('Confirm Import').attributes('disabled')).toBeDefined()
  wrapper.unmount()
})

it('blocks import when there are no active races', async () => {
  const { wrapper, button, upload } = await mountImport([inactiveRace])
  await upload('First Name,Last Name,Email\nA,B,a@example.test')
  expect(wrapper.text()).toContain('Add an active race to this event before importing.')
  expect(button('Validate & Preview').attributes('disabled')).toBeDefined()
  expect(api.post).not.toHaveBeenCalled()
  wrapper.unmount()
})

it('describes optional classifications and default race fallback in preview and confirmation', async () => {
  vi.mocked(api.post).mockResolvedValueOnce({ data: { rows: [
    { row: 2, raceName: 'Summit', defaultRaceReason: 'blank' },
    { row: 3, raceName: 'Summit', csvRace: 'Unknown', defaultRaceReason: 'unrecognized' },
  ], validCount: 2, defaultRaceCount: 2, token: 'current' } })
    .mockResolvedValueOnce({ data: { importedCount: 2, defaultRaceCount: 2 } })
  const { wrapper, button, upload, selectDefault } = await mountImport()
  expect(wrapper.text()).toContain('Category and Start Wave are optional.')
  await upload('First Name,Last Name,Email,Race\nA,B,a@example.test,\nC,D,c@example.test,Unknown')
  await selectDefault()
  await button('Validate & Preview').trigger('click')
  await flushPromises()
  expect(wrapper.text()).toContain('2 rows use the default Race Category.')
  expect(wrapper.text()).toContain('default, Race column blank')
  expect(wrapper.text()).toContain('default, Race value “Unknown” not found')
  await button('Confirm Import').trigger('click')
  await flushPromises()
  expect(wrapper.text()).toContain('Assigned 2 to the default Race Category.')
  wrapper.unmount()
})

it('shows the saved profile name in a row warning and keeps confirmation enabled', async () => {
  vi.mocked(api.post).mockResolvedValueOnce({ data: {
    rows: [{ row: 2, nameOnBib: 'L.S.', warning: 'CSV name Lin Santos differs. This import uses the saved profile for Lina Santos.' }],
    defaultRaceCount: 0, validCount: 1, token: 'warning-preview',
  } })
  const { wrapper, button, upload, selectDefault } = await mountImport()
  await upload('First Name,Last Name,Email,Name on Bib\nLin,Santos,lin@example.test,L.S.')
  await selectDefault()
  await button('Validate & Preview').trigger('click')
  await flushPromises()
  expect(wrapper.text()).toContain('L.S.')
  expect(wrapper.text()).toContain('saved profile for Lina Santos')
  expect(button('Confirm Import').attributes('disabled')).toBeUndefined()
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
  field.vm.$emit('update:modelValue', 'Pseudonym')
  await flushPromises()
  expect(field.props('hint')).toBe('Printed bibs and public participant outputs use this name.')
  wrapper.unmount()
})
