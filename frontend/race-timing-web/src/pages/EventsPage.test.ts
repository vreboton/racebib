import { createPinia } from 'pinia'
import { flushPromises, shallowMount } from '@vue/test-utils'
import { createMemoryHistory, createRouter } from 'vue-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import EventsPage from './EventsPage.vue'
import { eventsApi } from '../services/events'

vi.mock('../services/events', async importOriginal => {
  const actual = await importOriginal<typeof import('../services/events')>()
  return { ...actual, eventsApi: { ...actual.eventsApi, list: vi.fn() } }
})

const vuetifyStub = { template: '<div v-bind="$attrs"><slot /></div>' }
const componentStubs = Object.fromEntries([
  'v-btn', 'v-text-field', 'v-select', 'v-spacer', 'v-card-text', 'v-divider', 'v-alert',
  'v-skeleton-loader', 'v-chip', 'v-table', 'v-pagination', 'v-icon', 'v-card', 'v-snackbar',
].map(name => [name, vuetifyStub]))

function createPage() {
  return { items: [], page: 1, pageSize: 20, total: 0, totalPages: 0 }
}

async function mountEventsPage() {
  const pinia = createPinia()
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/events', component: EventsPage }],
  })
  await router.push('/events')
  await router.isReady()
  return shallowMount(EventsPage, {
    global: {
      plugins: [pinia, router],
      stubs: { ...componentStubs, RouterLink: { template: '<a><slot /></a>' } },
    },
  })
}

describe('Events page states', () => {
  beforeEach(() => vi.spyOn(eventsApi, 'list').mockReset())

  it('shows an accessible loading status while the event request is pending', async () => {
    let resolveRequest!: (value: ReturnType<typeof createPage>) => void
    vi.mocked(eventsApi.list).mockReturnValueOnce(new Promise(resolve => { resolveRequest = resolve }))
    const wrapper = await mountEventsPage()

    expect(wrapper.get('[role="status"][aria-label="Loading events"]').attributes('aria-label')).toBe('Loading events')

    resolveRequest(createPage())
    await flushPromises()
    wrapper.unmount()
  })

  it('shows the empty state when the API returns no events', async () => {
    vi.mocked(eventsApi.list).mockResolvedValueOnce(createPage())
    const wrapper = await mountEventsPage()

    await flushPromises()

    expect(wrapper.text()).toContain('No events found')
    wrapper.unmount()
  })

  it('shows a useful access error when loading events is forbidden', async () => {
    vi.mocked(eventsApi.list).mockRejectedValueOnce({ response: { status: 403, data: {} } })
    const wrapper = await mountEventsPage()

    await flushPromises()

    expect(wrapper.text()).toContain('You do not have permission to access or manage this organizer.')
    wrapper.unmount()
  })
})
