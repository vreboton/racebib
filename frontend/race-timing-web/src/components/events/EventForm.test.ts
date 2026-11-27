import { createPinia, setActivePinia } from 'pinia'
import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createVuetify } from 'vuetify'
import { VAlert } from 'vuetify/components/VAlert'
import { VBtn } from 'vuetify/components/VBtn'
import { VCard, VCardActions, VCardText } from 'vuetify/components/VCard'
import { VDivider } from 'vuetify/components/VDivider'
import { VForm } from 'vuetify/components/VForm'
import { VCol, VRow, VSpacer } from 'vuetify/components/VGrid'
import { VSelect } from 'vuetify/components/VSelect'
import { VTextField } from 'vuetify/components/VTextField'
import { VTextarea } from 'vuetify/components/VTextarea'
import { api } from '../../services/api'
import type { EventDetails, EventPayload } from '../../services/events'
import { useAuthStore } from '../../stores/auth'
import EventForm from './EventForm.vue'

vi.mock('../../services/api', () => ({ api: { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn() } }))

const organizer = { id: 'org-1', name: 'Trail Club', slug: 'trail-club' }

function inputForLabel(wrapper: ReturnType<typeof mount>, labelText: string) {
  const label = wrapper.findAll('label').find(item => item.text().includes(labelText) && item.attributes('for'))
  if (!label) throw new Error(`Could not find the ${labelText} field label`)
  const inputId = label.attributes('for')
  if (!inputId) throw new Error(`The ${labelText} label is not associated with an input`)
  return wrapper.get(`#${inputId}`)
}

function mountForm(event?: EventDetails) {
  return mount(EventForm, {
    props: { event },
    global: {
      plugins: [createVuetify({ components: {
        VAlert, VBtn, VCard, VCardActions, VCardText, VDivider, VForm, VCol, VRow, VSelect, VSpacer, VTextField, VTextarea,
      } })],
    },
  })
}
describe('Event form workflow', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.mocked(api.get).mockResolvedValue({ data: [organizer] } as never)
  })

  it('rejects an invalid slug, then submits a valid event with normalized values', async () => {
    const auth = useAuthStore()
    auth.user = {
      id: 'organizer-admin', email: 'organizer@example.test', displayName: 'Organizer Admin', roles: [],
      organizerMemberships: [{ organizerId: organizer.id, organizerName: organizer.name, role: 'EventAdministrator' }],
    }
    const wrapper = mountForm()
    await flushPromises()

    await inputForLabel(wrapper, 'Event name').setValue('  Alpine Trail 25K  ')
    await inputForLabel(wrapper, 'Event slug').setValue('Alpine--Trail')
    await inputForLabel(wrapper, 'Event start').setValue('2027-06-01T06:00')
    await inputForLabel(wrapper, 'Event end').setValue('2027-06-01T12:00')
    await inputForLabel(wrapper, 'Time zone').setValue('Asia/Manila')
    await flushPromises()
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(wrapper.emitted('submit')).toBeUndefined()

    await inputForLabel(wrapper, 'Event slug').setValue('alpine-trail-25k')
    await flushPromises()
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    const payload = wrapper.emitted('submit')?.[0]?.[0] as (EventPayload & { organizerId?: string }) | undefined
    if (!payload) throw new Error('Expected the valid event form to emit a payload')
    expect(payload).toMatchObject({
      organizerId: organizer.id,
      name: 'Alpine Trail 25K',
      slug: 'alpine-trail-25k',
      organizerName: organizer.name,
      timeZoneId: 'Asia/Manila',
    })
    expect(payload.startDateUtc).toBe('2027-05-31T22:00:00.000Z')
    expect(payload.endDateUtc).toBe('2027-06-01T04:00:00.000Z')
    wrapper.unmount()
  })

  it('renders an existing Manila schedule and round trips its original precision', async () => {
    const wrapper = mountForm({
      id: 'event-1', name: 'Canlandog Community Run', slug: 'canlandog', organizerId: organizer.id,
      organizerName: organizer.name, timeZoneId: 'Asia/Manila', status: 'Ready', hasArmedTiming: true,
      startDate: '2026-11-27T04:00:00.123456Z', endDate: '2026-11-27T09:00:00Z',
    } as EventDetails)
    await flushPromises()
    expect(wrapper.text()).toContain('Editing the event schedule does not change any frozen race timing window.')
    expect((inputForLabel(wrapper, 'Event start').element as HTMLInputElement).value).toBe('2026-11-27T12:00')
    expect((inputForLabel(wrapper, 'Event end').element as HTMLInputElement).value).toBe('2026-11-27T17:00')
    await inputForLabel(wrapper, 'Time zone').setValue('America/New_York')
    expect((inputForLabel(wrapper, 'Event start').element as HTMLInputElement).value).toBe('2026-11-26T23:00')
    await wrapper.get('form').trigger('submit')
    expect(wrapper.emitted('submit')?.[0]?.[0]).toMatchObject({
      startDateUtc: '2026-11-27T04:00:00.123456Z', endDateUtc: '2026-11-27T09:00:00Z',
    })
    wrapper.unmount()
  })})
