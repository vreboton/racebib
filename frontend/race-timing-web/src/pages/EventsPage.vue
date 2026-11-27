<template>
  <div class="page-heading">
    <div>
      <div class="eyebrow">WORKSPACE</div>
      <h1>Events</h1>
      <p>Create and manage races across your organizations.</p>
    </div>
    <v-btn v-if="auth.canManageAnyOrganizer" color="primary" prepend-icon="mdi-plus" to="/events/new">Create event</v-btn>
  </div>

  <v-card border flat rounded="lg" class="mt-5">
    <v-card-text class="d-flex flex-wrap ga-3 align-center">
      <v-text-field
        v-model="searchInput"
        prepend-inner-icon="mdi-magnify"
        label="Search events"
        placeholder="Name or location"
        density="comfortable"
        variant="outlined"
        hide-details
        clearable
        class="search-field"
        @keyup.enter="applySearch"
        @click:clear="applySearch"
      />
      <v-select
        v-model="selectedStatus"
        :items="statusOptions"
        label="All statuses"
        density="comfortable"
        variant="outlined"
        hide-details
        clearable
        class="filter-field"
        @update:model-value="applyFilters"
      />
      <v-btn variant="tonal" color="secondary" prepend-icon="mdi-filter-remove" @click="clearFilters">Clear</v-btn>
      <v-spacer />
      <v-btn color="primary" variant="text" prepend-icon="mdi-magnify" @click="applySearch">Search</v-btn>
    </v-card-text>
    <v-divider />

    <v-alert v-if="pageError" type="error" variant="tonal" class="ma-4" closable @click:close="pageError = ''">
      {{ pageError }}
    </v-alert>
    <v-skeleton-loader v-if="loading" type="table-row@6" role="status" aria-label="Loading events" />
    <template v-else-if="events.length">
      <v-table>
        <thead>
          <tr>
            <th class="text-left">Event</th>
            <th class="text-left">Location</th>
            <th class="text-left">Event dates</th>
            <th class="text-left">Status</th>
            <th class="text-right">Actions</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="item in events" :key="item.id">
            <td>
              <div class="font-weight-medium">
                <router-link class="event-name-link" :to="{ name: 'event-details', params: { id: item.id } }">{{ item.name }}</router-link>
              </div>
              <div class="text-caption text-medium-emphasis">{{ item.slug }}</div>
            </td>
            <td>
              <div>{{ item.venue || '—' }}</div>
              <div class="text-caption text-medium-emphasis">{{ item.location || 'Location not set' }}</div>
            </td>
            <td>
              <div>{{ formatEventTime(item.startDate, item.timeZoneId, 'date') }}</div>
              <div class="text-caption text-medium-emphasis">to {{ formatEventTime(item.endDate, item.timeZoneId, 'date') }}</div>
            </td>
            <td><v-chip size="small" :color="statusColor(item.status)" variant="tonal">{{ statusLabel(item.status) }}</v-chip></td>
            <td class="text-right">
              <v-btn size="small" variant="tonal" color="primary" prepend-icon="mdi-eye-outline" :to="{ name: 'event-details', params: { id: item.id } }">View details</v-btn>
              <v-btn v-if="auth.canManageOrganizer(item.organizerId)" icon="mdi-pencil-outline" size="small" variant="text" :to="{ name: 'event-edit', params: { id: item.id } }" aria-label="Edit event" :disabled="['Live', 'Completed', 'Cancelled'].includes(item.status)" />
            </td>
          </tr>
        </tbody>
      </v-table>
      <v-divider />
      <div class="d-flex align-center justify-space-between flex-wrap ga-3 px-4 py-3">
        <div class="text-caption text-medium-emphasis">
          Showing {{ firstItem }}–{{ lastItem }} of {{ total }} events
        </div>
        <div class="d-flex align-center ga-3">
          <v-select v-model="pageSize" :items="[10, 20, 50, 100]" label="Rows" density="compact" variant="outlined" hide-details class="rows-select" @update:model-value="changePageSize" />
          <v-pagination v-model="page" :length="totalPages" :total-visible="5" density="comfortable" @update:model-value="loadEvents" />
        </div>
      </div>
    </template>
    <v-card-text v-else class="empty-state">
      <v-icon size="42" color="grey-lighten-1">mdi-calendar-blank-outline</v-icon>
      <div class="text-subtitle-1 font-weight-medium mt-3">{{ hasFilters ? 'No matching events' : 'No events found' }}</div>
      <div class="text-body-2 text-medium-emphasis mt-1">
        {{ hasFilters ? 'Try changing your search or status filter.' : 'Events you create will appear here.' }}
      </div>
      <v-btn v-if="hasFilters" variant="tonal" class="mt-4" @click="clearFilters">Clear filters</v-btn>
      <v-btn v-else-if="auth.canManageAnyOrganizer" color="primary" variant="tonal" class="mt-4" to="/events/new">Create your first event</v-btn>
    </v-card-text>
  </v-card>
  <v-snackbar v-model="successSnackbar" color="success" timeout="3000">{{ successMessage }}</v-snackbar>
</template>

<script setup lang="ts">
import { formatEventTime } from '../services/eventTime'
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { eventStatuses, eventsApi, getApiError } from '../services/events'
import type { EventListItem, EventStatus } from '../services/events'
import { useAuthStore } from '../stores/auth'

const route = useRoute()
const auth = useAuthStore()
const events = ref<EventListItem[]>([])
const loading = ref(false)
const pageError = ref('')
const successMessage = ref('')
const successSnackbar = ref(false)
const searchInput = ref('')
const requestSequence = ref(0)
const search = ref('')
const selectedStatus = ref<EventStatus | null>(null)
const page = ref(1)
const pageSize = ref(20)
const total = ref(0)
const totalPages = ref(0)
const statusOptions = eventStatuses.map(value => ({ title: value.replace(/([a-z])([A-Z])/g, '$1 $2'), value }))
const hasFilters = computed(() => Boolean(search.value || selectedStatus.value))
const firstItem = computed(() => total.value ? (page.value - 1) * pageSize.value + 1 : 0)
const lastItem = computed(() => Math.min(page.value * pageSize.value, total.value))

onMounted(async () => {
  if (typeof route.query.success === 'string') {
    successMessage.value = route.query.success
    successSnackbar.value = true
  }
  if (typeof route.query.error === 'string') pageError.value = route.query.error
  await loadEvents()
})

watch([search, selectedStatus], () => { page.value = 1 })

async function loadEvents() {
  const requestId = ++requestSequence.value
  loading.value = true
  pageError.value = ''
  try {
    const result = await eventsApi.list({
      page: page.value, pageSize: pageSize.value,
      search: search.value || undefined, status: selectedStatus.value,
    })
    if (requestId !== requestSequence.value) return
    events.value = result.items
    total.value = result.total
    totalPages.value = result.totalPages
    if (result.totalPages > 0 && page.value > result.totalPages) {
      page.value = result.totalPages
      await loadEvents()
    }
  } catch (error) {
    if (requestId !== requestSequence.value) return
    events.value = []
    total.value = 0
    totalPages.value = 0
    pageError.value = getApiError(error, 'Unable to load events.')
  } finally {
    if (requestId === requestSequence.value) loading.value = false
  }
}
function applySearch() {
  search.value = searchInput.value.trim()
  page.value = 1
  loadEvents()
}
function applyFilters() {
  page.value = 1
  loadEvents()
}
function clearFilters() {
  searchInput.value = ''
  search.value = ''
  selectedStatus.value = null
  page.value = 1
  loadEvents()
}
function changePageSize() {
  page.value = 1
  loadEvents()
}
function statusLabel(status: EventStatus) {
  return status.replace(/([a-z])([A-Z])/g, '$1 $2')
}
function statusColor(status: EventStatus) {
  return ({ Draft: 'grey', Published: 'info', RegistrationOpen: 'success', RegistrationClosed: 'warning', Ready: 'primary', Live: 'error', Completed: 'teal', Cancelled: 'grey-darken-1' } as Record<EventStatus, string>)[status]
}
</script>

<style scoped>
.rows-select { max-width: 90px; }
.event-name-link { color: inherit; text-decoration: none; }
.event-name-link:hover { color: rgb(var(--v-theme-primary)); text-decoration: underline; }
</style>
