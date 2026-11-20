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
  ...Object.fromEntries(['v-card', 'v-card-title', 'v-card-text', 'v-expansion-panels', 'v-row', 'v-col',
    'v-expansion-panel-text', 'v-alert', 'v-dialog', 'v-card-actions', 'v-spacer', 'v-textarea', 'v-select', 'v-checkbox']
    .map(name => [name, slot])),
  'v-expansion-panel': { props: ['title'], template: '<div><span>{{ title }}</span><slot /></div>' },
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
  distanceKm: 10, elevationGainMeters: 800, usable: true, immutable: false, editBlock: null, hasActiveRaces: true, canDelete: false,
  affectedRegistrationCount: 0, registrationConfirmation: null, checkpoints: [],
}

function configuration(categories: Configuration['races'][number]['categories'] = []): Configuration {
  return {
    canEdit: true, timeZoneId: 'UTC', courses: [course],
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

  it('requires confirmation before permanently deleting an unused course and refreshes the list', async () => {
    const unused = { ...course, status: 'Inactive' as const, canDelete: true, hasActiveRaces: false }
    vi.mocked(loadConfiguration)
      .mockResolvedValueOnce({ ...configuration(), races: [], courses: [unused] })
      .mockResolvedValueOnce({ ...configuration(), races: [], courses: [] })
    const confirm = vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValueOnce(true)
    const { wrapper, button } = await mountPanel()
    try {
      await button('Delete Route')!.trigger('click')
      expect(saveConfiguration).not.toHaveBeenCalled()
      await button('Delete Route')!.trigger('click')
      await flushPromises()

      expect(confirm).toHaveBeenCalledWith('Permanently delete Summit and all its Route Revisions and Route Checkpoints? This cannot be undone.')
      expect(saveConfiguration).toHaveBeenCalledWith('event-1', 'courses/course-1', 'delete', undefined)
      expect(wrapper.text()).toContain('No Routes yet.')
      expect(button('Delete Route')).toBeUndefined()
    } finally {
      confirm.mockRestore()
      wrapper.unmount()
    }
  })

  it('offers no permanent deletion for protected courses or when configuration is read-only', async () => {
    vi.mocked(loadConfiguration).mockResolvedValueOnce({
      ...configuration(), courses: [
        { ...course, hasActiveRaces: false },
        { ...course, id: 'historical', immutable: true, editBlock: 'historical', hasActiveRaces: false },
        { ...course, id: 'latest-revision', version: 2, hasActiveRaces: false },
      ],
    })
    const editable = await mountPanel()
    expect(editable.button('Delete Route')).toBeUndefined()
    expect(editable.button('Retire Route')).toBeDefined()
    editable.wrapper.unmount()

    vi.mocked(loadConfiguration).mockResolvedValueOnce({
      ...configuration(), canEdit: false, courses: [{ ...course, canDelete: true, hasActiveRaces: false }],
    })
    const readOnly = await mountPanel()
    expect(readOnly.button('Delete Route')).toBeUndefined()
    readOnly.wrapper.unmount()
  })

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
      await button('Retire Route')!.trigger('click')
      await flushPromises()
      expect(saveConfiguration).toHaveBeenCalledWith('event-1', 'courses/course-1/status', 'patch', { status: 'Inactive' })
      expect(wrapper.text()).toContain('Retired')
      expect(button('Add Category')!.attributes('disabled')).toBeDefined()
      expect(button('Edit Route')).toBeUndefined()
      await button('Reactivate Route')!.trigger('click')
      await flushPromises()
      expect(saveConfiguration).toHaveBeenLastCalledWith('event-1', 'courses/course-1/status', 'patch', { status: 'Active' })
      expect(button('Edit Route Checkpoints')).toBeUndefined()
      expect(button('Add Category')!.attributes('disabled')).toBeUndefined()
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

    expect(wrapper.text()).toContain('Reactivate this Route before editing it.')
    expect(button('Edit Route')).toBeUndefined()
    expect(button('Edit Route Checkpoints')).toBeUndefined()
    await button('Reactivate Route')!.trigger('click')
    await flushPromises()
    expect(saveConfiguration).toHaveBeenCalledWith('event-1', 'courses/course-1/status', 'patch', { status: 'Active' })
    expect(button('Edit Route')).toBeDefined()
    expect(button('Edit Route Checkpoints')).toBeDefined()
    wrapper.unmount()
  })

  it('disables retirement for a course used by an active race', async () => {
    vi.mocked(loadConfiguration).mockResolvedValue({ ...configuration(), courses: [course] })
    const { wrapper, button } = await mountPanel()
    expect(button('Retire Route')!.attributes('disabled')).toBeDefined()
    expect(wrapper.text()).toContain('An active Category uses this Route.')
    expect(button('Edit Route')).toBeDefined()
    wrapper.unmount()
  })

  it('duplicates an attached course and opens the independent copy for editing', async () => {
    const attached = { ...configuration(), courses: [course] }
    const copy = { ...course, id: 'copy-1', name: 'Summit (Copy)' }
    vi.mocked(loadConfiguration).mockResolvedValueOnce(attached)
      .mockResolvedValueOnce({ ...attached, courses: [course, copy] })
    vi.mocked(saveConfiguration).mockResolvedValueOnce({ id: copy.id })
    const { wrapper, button } = await mountPanel()

    expect(button('Edit Route')).toBeDefined()
    expect(button('Edit Route Checkpoints')).toBeDefined()
    expect(button('Clone revision')).toBeUndefined()
    await button('Duplicate Route')!.trigger('click')
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

  it.each([12, 8])('edits distance directly on a course with a Finish checkpoint (%s km)', async distance => {
    const finish = {
      id: 'finish-1', name: 'Finish', code: 'F', sequence: 1, distanceFromStartKm: 10, isFinish: true,
      isStart: false, elevationMeters: null, isMandatory: true, hasCutoff: false, cutoffDurationSeconds: null,
      description: null,
    }
    const attached = { ...configuration(), courses: [{ ...course, checkpoints: [finish] }] }
    vi.mocked(loadConfiguration).mockResolvedValueOnce(attached).mockResolvedValueOnce({
      ...attached,
      courses: [{ ...course, distanceKm: distance, checkpoints: [{ ...finish, distanceFromStartKm: distance }] }],
      races: attached.races.map(race => ({ ...race, distanceKm: distance })),
    })
    const { wrapper, button } = await mountPanel()
    await button('Edit Route')!.trigger('click')
    const field = wrapper.get('input[aria-label="Distance (km)"]')
    expect(field.attributes('readonly')).toBeUndefined()
    await field.setValue(String(distance))
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(saveConfiguration).toHaveBeenCalledWith('event-1', 'courses/course-1', 'put',
      { name: 'Summit', description: 'Trail route', distanceKm: distance, elevationGainMeters: 800 })
    expect(wrapper.text()).toContain(`Finish (F) · ${distance} km`)
    wrapper.unmount()
  })

  it('shows an invalid route reduction and keeps the distance field available for correction', async () => {
    vi.mocked(loadConfiguration).mockResolvedValueOnce({ ...configuration(), courses: [course] })
    vi.mocked(saveConfiguration).mockRejectedValueOnce({
      response: { status: 400, data: { title: "The course distance cannot be shorter than an intermediate checkpoint's distance from start." } },
    })
    const { wrapper, button } = await mountPanel()
    await button('Edit Route')!.trigger('click')
    const field = wrapper.get('input[aria-label="Distance (km)"]')
    await field.setValue('4')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(wrapper.text()).toContain('shorter than an intermediate checkpoint')
    expect((field.element as HTMLInputElement).value).toBe('4')
    expect(loadConfiguration).toHaveBeenCalledTimes(1)
    wrapper.unmount()
  })

  it('shows the registration impact and sends confirmation only after approval', async () => {
    vi.mocked(loadConfiguration).mockResolvedValue({
      ...configuration(), courses: [{ ...course, affectedRegistrationCount: 2, registrationConfirmation: 'snapshot' }],
    })
    const confirm = vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValueOnce(true)
    const { wrapper, button } = await mountPanel()
    try {
      await button('Edit Route')!.trigger('click')
      await wrapper.get('input[aria-label="Distance (km)"]').setValue('12')
      await wrapper.get('form').trigger('submit')
      expect(confirm).toHaveBeenCalledWith(expect.stringContaining('This affects 2 existing registrations.'))
      expect(saveConfiguration).not.toHaveBeenCalled()
      await wrapper.get('form').trigger('submit')
      await flushPromises()
      expect(saveConfiguration).toHaveBeenCalledWith('event-1', 'courses/course-1?registrationConfirmation=snapshot', 'put',
        { name: 'Summit', description: 'Trail route', distanceKm: 12, elevationGainMeters: 800 })
    } finally {
      confirm.mockRestore()
      wrapper.unmount()
    }
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

    await button('Edit Route Checkpoints')!.trigger('click')
    await wrapper.get('input[aria-label="Route distance (km)"]').setValue('12')
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
        { ...course, immutable: true, editBlock: 'lifecycle' },
        { ...course, id: 'historical', immutable: true, editBlock: 'historical' },
      ],
    })
    const { wrapper, button } = await mountPanel()
    expect(button('Edit Route')).toBeUndefined()
    expect(button('Edit Route Checkpoints')).toBeUndefined()
    expect(wrapper.text()).toContain('This Route is assigned to a Category outside Draft.')
    expect(wrapper.text()).toContain('This historical Route Revision is read-only.')
    expect(wrapper.findAll('button').filter(item => item.text() === 'Duplicate Route')).toHaveLength(2)
    wrapper.unmount()
  })

  it('creates a category and refreshes the persisted list', async () => {
    vi.mocked(loadConfiguration).mockResolvedValueOnce(configuration()).mockResolvedValueOnce(configuration([category]))
    const { wrapper, button } = await mountPanel()
    await button('Add Age Group')!.trigger('click')
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
