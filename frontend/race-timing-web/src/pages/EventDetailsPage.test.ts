import { flushPromises, shallowMount } from '@vue/test-utils'
import { expect, it, vi } from 'vitest'
import { createMemoryHistory, createRouter } from 'vue-router'
import { eventsApi, type EventDetails } from '../services/events'
import EventDetailsPage from './EventDetailsPage.vue'

const canManageOrganizer = vi.hoisted(() => vi.fn())
vi.mock('../stores/auth', () => ({ useAuthStore: () => ({ canManageOrganizer }) }))
vi.mock('../services/events', async importOriginal => ({
  ...await importOriginal<typeof import('../services/events')>(),
  eventsApi: { get: vi.fn() },
}))

async function mountDetails(path: string, canManage: boolean) {
  canManageOrganizer.mockReturnValue(canManage)
  vi.mocked(eventsApi.get).mockResolvedValue({
    id: 'event-1', organizerId: 'org-1', organizerName: 'Trail Club', name: 'Summit Weekend',
    slug: 'summit-weekend', status: 'Draft', timeZoneId: 'UTC',
    startDate: '2026-11-20T10:00:00Z', endDate: '2026-11-20T13:00:00Z',
  } as EventDetails)
  const router = createRouter({
    history: createMemoryHistory(), routes: [{ path: '/events/:id', component: EventDetailsPage }],
  })
  await router.push(path)
  const wrapper = shallowMount(EventDetailsPage, { global: {
    plugins: [router],
    stubs: {
      ...Object.fromEntries(['v-btn', 'v-alert', 'v-skeleton-loader', 'v-chip', 'v-divider', 'v-col', 'v-row',
        'v-card-text', 'v-card', 'v-card-title', 'v-snackbar'].map(name => [name, { template: '<div><slot /></div>' }])),
      'v-tabs': { name: 'FixtureTabs', emits: ['update:modelValue'], template: '<div><slot /></div>' },
      'v-tab': { template: '<button><slot /></button>' },
    },
  } })
  await flushPromises()
  return { wrapper, router }
}

it('opens a configuration deep link and keeps the event context when switching tabs', async () => {
  const { wrapper, router } = await mountDetails('/events/event-1?tab=configuration', true)
  expect(wrapper.get('h1').text()).toBe('Summit Weekend')
  expect(wrapper.get('[role="tabpanel"]').attributes('id')).toBe('configuration')
  expect(wrapper.findAll('button').map(button => button.text())).toEqual(['Runner Registrations', 'Race Configuration'])

  wrapper.findComponent({ name: 'FixtureTabs' }).vm.$emit('update:modelValue', 'registrations')
  await flushPromises()
  expect(router.currentRoute.value.fullPath).toBe('/events/event-1?tab=registrations')
  expect(wrapper.get('[role="tabpanel"]').attributes('id')).toBe('registrations')
  expect(wrapper.get('h1').text()).toBe('Summit Weekend')
  expect(eventsApi.get).toHaveBeenCalledTimes(1)
  wrapper.unmount()
})

it('keeps registration tools hidden for a read-only organizer member', async () => {
  const { wrapper } = await mountDetails('/events/event-1?tab=registrations', false)
  expect(wrapper.find('#registrations-tab').exists()).toBe(false)
  expect(wrapper.get('[role="tabpanel"]').attributes('id')).toBe('configuration')
  wrapper.unmount()
})
