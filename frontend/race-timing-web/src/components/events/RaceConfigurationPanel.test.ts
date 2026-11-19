import { flushPromises, shallowMount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import RaceConfigurationPanel from './RaceConfigurationPanel.vue'
import { loadConfiguration, saveConfiguration } from '../../services/raceConfiguration'
import type { Configuration } from '../../services/raceConfiguration'

vi.mock('../../services/raceConfiguration', async importOriginal => ({
  ...await importOriginal<typeof import('../../services/raceConfiguration')>(),
  loadConfiguration: vi.fn(),
  saveConfiguration: vi.fn(),
}))

const slot = { template: '<div><slot /></div>' }
const stubs = {
  ...Object.fromEntries(['v-card', 'v-card-title', 'v-card-text', 'v-expansion-panel', 'v-expansion-panels',
    'v-expansion-panel-text', 'v-alert', 'v-dialog', 'v-card-actions', 'v-spacer', 'v-textarea', 'v-select']
    .map(name => [name, slot])),
  'v-btn': { template: '<button v-bind="$attrs"><slot /></button>' },
  'v-form': { template: '<form><slot /></form>' },
  'v-text-field': {
    props: ['label', 'modelValue'],
    emits: ['update:modelValue'],
    template: '<input :aria-label="label" :value="modelValue" @input="$emit(\'update:modelValue\', $event.target.value)">',
  },
}

const raceId = 'race-1'
const category = {
  id: 'category-1', name: 'Masters', status: 'Active' as const, description: null,
  minimumAge: 40, maximumAge: null, hasRegistrations: true,
}

function configuration(categories: Configuration['races'][number]['categories'] = []): Configuration {
  return {
    canEdit: true, timeZoneId: 'UTC', courses: [],
    races: [{
      id: raceId, name: 'Trail Race', status: 'Active', courseId: 'course-1', distanceKm: 10,
      startAt: '2026-11-20T10:00:00Z', description: null, cutoffDurationSeconds: null,
      categories, waves: [], revisionIds: [],
    }],
  }
}

async function mountPanel() {
  const wrapper = shallowMount(RaceConfigurationPanel, { props: { eventId: 'event-1', zone: 'UTC' }, global: { stubs } })
  await flushPromises()
  const button = (label: string) => wrapper.findAll('button').find(item => item.text() === label)
  return { wrapper, button }
}

describe('race configuration actions', () => {
  beforeEach(() => vi.resetAllMocks())

  it('creates a category and refreshes the persisted list', async () => {
    vi.mocked(loadConfiguration).mockResolvedValueOnce(configuration()).mockResolvedValueOnce(configuration([category]))
    const { wrapper, button } = await mountPanel()
    await button('Add category')!.trigger('click')
    await wrapper.get('input[aria-label="Name"]').setValue('Masters')
    await wrapper.get('input[aria-label="Minimum age (inclusive)"]').setValue('40')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(saveConfiguration).toHaveBeenCalledWith('event-1', 'races/race-1/categories', 'post',
      { name: 'Masters', minimumAge: 40, maximumAge: null, description: null })
    expect(loadConfiguration).toHaveBeenCalledTimes(2)
    expect(wrapper.text()).toContain('Masters · Ages 40 to any')
    expect(wrapper.find('form').exists()).toBe(false)
    wrapper.unmount()
  })

  it('protects linked category edits and asks before retiring the record', async () => {
    vi.mocked(loadConfiguration).mockResolvedValueOnce(configuration([category]))
      .mockResolvedValueOnce(configuration([{ ...category, status: 'Inactive' }]))
    const confirm = vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValueOnce(true)
    const { wrapper, button } = await mountPanel()

    expect(wrapper.text()).toContain('Used by registrations')
    expect(button('Edit')).toBeUndefined()
    await button('Retire')!.trigger('click')
    expect(saveConfiguration).not.toHaveBeenCalled()
    await button('Retire')!.trigger('click')
    await flushPromises()

    expect(saveConfiguration).toHaveBeenCalledWith('event-1', 'races/race-1/categories/category-1', 'delete', undefined)
    expect(wrapper.text()).toContain('Retired')
    expect(button('Retire')).toBeUndefined()
    confirm.mockRestore()
    wrapper.unmount()
  })

  it('keeps the draft visible after a conflict and lets the director reload', async () => {
    const editable = { ...category, hasRegistrations: false }
    vi.mocked(loadConfiguration).mockResolvedValueOnce(configuration([editable]))
      .mockResolvedValueOnce(configuration([category]))
    vi.mocked(saveConfiguration).mockRejectedValueOnce({
      response: { status: 409, data: { title: 'A category used by registrations cannot be edited.' } },
    })
    const { wrapper, button } = await mountPanel()
    await button('Edit')!.trigger('click')
    await wrapper.get('input[aria-label="Name"]').setValue('Edited')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(wrapper.text()).toContain('A category used by registrations cannot be edited.')
    expect((wrapper.get('input[aria-label="Name"]').element as HTMLInputElement).value).toBe('Edited')
    await button('Reload saved configuration')!.trigger('click')
    await flushPromises()
    expect(wrapper.find('form').exists()).toBe(false)
    expect(button('Edit')).toBeUndefined()
    wrapper.unmount()
  })
})
