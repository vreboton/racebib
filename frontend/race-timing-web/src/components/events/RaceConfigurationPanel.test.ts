import { flushPromises, shallowMount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import RaceConfigurationPanel from './RaceConfigurationPanel.vue'
import { loadConfiguration, saveConfiguration } from '../../services/raceConfiguration'
import type { Configuration, Course } from '../../services/raceConfiguration'

vi.mock('../../services/raceConfiguration', async importOriginal => ({
  ...await importOriginal<typeof import('../../services/raceConfiguration')>(),
  loadConfiguration: vi.fn(),
  saveConfiguration: vi.fn(),
}))

const slot = { template: '<div><slot /></div>' }
const stubs = {
  ...Object.fromEntries(['v-card', 'v-card-title', 'v-card-text', 'v-expansion-panel', 'v-expansion-panels',
    'v-expansion-panel-text', 'v-alert', 'v-dialog', 'v-card-actions', 'v-spacer', 'v-textarea', 'v-select', 'v-checkbox']
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

const course: Course = {
  id: 'course-1', name: 'Summit', status: 'Active', version: 1, description: 'Trail route',
  distanceKm: 10, elevationGainMeters: 800, usable: true, immutable: false, editBlock: null, hasActiveRaces: true,
  checkpoints: [],
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

  it('retires and reactivates a historical revision while keeping its race visible and edits locked', async () => {
    const historical = { ...course, immutable: true, editBlock: 'historical' as const, hasActiveRaces: false }
    const retired = { ...historical, status: 'Inactive' as const, usable: false }
    const current = configuration()
    const inactiveRace = { ...current.races[0], status: 'Inactive' as const }
    const history = { ...current, races: [inactiveRace] }
    vi.mocked(loadConfiguration).mockResolvedValueOnce({ ...history, courses: [historical] })
      .mockResolvedValueOnce({ ...history, courses: [retired] })
      .mockResolvedValueOnce({ ...history, courses: [historical] })
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true)
    const { wrapper, button } = await mountPanel()
    try {
      await button('Retire course')!.trigger('click')
      await flushPromises()
      expect(saveConfiguration).toHaveBeenCalledWith('event-1', 'courses/course-1/status', 'patch', { status: 'Inactive' })
      expect(wrapper.text()).toContain('Course · Summit · Revision 1 · 10 km · Retired')
      expect(button('Add race')!.attributes('disabled')).toBeDefined()
      expect(button('Edit course')).toBeUndefined()
      await button('Reactivate course')!.trigger('click')
      await flushPromises()
      expect(saveConfiguration).toHaveBeenLastCalledWith('event-1', 'courses/course-1/status', 'patch', { status: 'Active' })
      expect(button('Edit checkpoints')).toBeUndefined()
      expect(button('Add race')!.attributes('disabled')).toBeUndefined()
    } finally {
      confirm.mockRestore()
      wrapper.unmount()
    }
  })

  it('requires reactivation before editing an inactive standalone course', async () => {
    const active = { ...course, hasActiveRaces: false }
    const inactive = { ...active, status: 'Inactive' as const, immutable: true, editBlock: 'inactive' as const, usable: false }
    vi.mocked(loadConfiguration).mockResolvedValueOnce({ ...configuration(), races: [], courses: [inactive] })
      .mockResolvedValueOnce({ ...configuration(), races: [], courses: [active] })
    const { wrapper, button } = await mountPanel()

    expect(wrapper.text()).toContain('Reactivate this course before editing it.')
    expect(button('Edit course')).toBeUndefined()
    expect(button('Edit checkpoints')).toBeUndefined()
    await button('Reactivate course')!.trigger('click')
    await flushPromises()
    expect(saveConfiguration).toHaveBeenCalledWith('event-1', 'courses/course-1/status', 'patch', { status: 'Active' })
    expect(button('Edit course')).toBeDefined()
    expect(button('Edit checkpoints')).toBeDefined()
    wrapper.unmount()
  })

  it('disables retirement for a course used by an active race', async () => {
    vi.mocked(loadConfiguration).mockResolvedValue({ ...configuration(), courses: [course] })
    const { wrapper, button } = await mountPanel()
    expect(button('Retire course')!.attributes('disabled')).toBeDefined()
    expect(wrapper.text()).toContain('An active race uses this course.')
    expect(button('Edit course')).toBeDefined()
    wrapper.unmount()
  })

  it('duplicates an attached course and opens the independent copy for editing', async () => {
    const attached = { ...configuration(), courses: [course] }
    const copy = { ...course, id: 'copy-1', name: 'Summit (Copy)' }
    vi.mocked(loadConfiguration).mockResolvedValueOnce(attached)
      .mockResolvedValueOnce({ ...attached, courses: [course, copy] })
    vi.mocked(saveConfiguration).mockResolvedValueOnce({ id: copy.id })
    const { wrapper, button } = await mountPanel()

    expect(button('Edit course')).toBeDefined()
    expect(button('Edit checkpoints')).toBeDefined()
    expect(button('Clone revision')).toBeUndefined()
    await button('Duplicate course')!.trigger('click')
    await flushPromises()
    expect(saveConfiguration).toHaveBeenCalledWith('event-1', 'courses/course-1/duplicate', 'post', undefined)
    expect(loadConfiguration).toHaveBeenCalledTimes(2)
    expect((wrapper.get('input[aria-label="Name"]').element as HTMLInputElement).value).toBe('Summit (Copy)')
    await wrapper.get('input[aria-label="Name"]').setValue('New route')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(saveConfiguration).toHaveBeenLastCalledWith('event-1', 'courses/copy-1', 'put',
      { name: 'New route', description: 'Trail route', distanceKm: 10, elevationGainMeters: 800 })
    wrapper.unmount()
  })

  it('saves an attached course distance together with the updated Finish checkpoint', async () => {
    const finish = {
      id: 'finish-1', name: 'Finish', code: 'F', sequence: 2, distanceFromStartKm: 10, isFinish: true,
      isStart: false, elevationMeters: null, isMandatory: true, hasCutoff: false, cutoffDurationSeconds: null,
      description: null,
    }
    const start = { ...finish, id: 'start-1', name: 'Start', code: 'S', sequence: 1, distanceFromStartKm: 0,
      isStart: true, isFinish: false }
    const attached = { ...configuration(), courses: [{ ...course, checkpoints: [start, finish] }] }
    vi.mocked(loadConfiguration).mockResolvedValue(attached)
    const { wrapper, button } = await mountPanel()

    await button('Edit checkpoints')!.trigger('click')
    await wrapper.get('input[aria-label="Course distance (km)"]').setValue('12')
    await wrapper.findAll('input[aria-label="Distance from start (km)"]')[1].setValue('12')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(saveConfiguration).toHaveBeenCalledWith('event-1', 'courses/course-1/checkpoints', 'put',
      { distanceKm: 12, checkpoints: [start, { ...finish, distanceFromStartKm: 12 }] })
    wrapper.unmount()
  })

  it('explains registration and history locks while keeping duplication available', async () => {
    vi.mocked(loadConfiguration).mockResolvedValueOnce({
      ...configuration(), courses: [
        { ...course, immutable: true, editBlock: 'registrations' },
        { ...course, id: 'historical', immutable: true, editBlock: 'historical' },
      ],
    })
    const { wrapper, button } = await mountPanel()
    expect(button('Edit course')).toBeUndefined()
    expect(button('Edit checkpoints')).toBeUndefined()
    expect(wrapper.text()).toContain('A race using this course has registrations.')
    expect(wrapper.text()).toContain('This historical revision is read-only.')
    expect(wrapper.findAll('button').filter(item => item.text() === 'Duplicate course')).toHaveLength(2)
    wrapper.unmount()
  })

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
