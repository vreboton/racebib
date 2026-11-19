<template>
  <div class="page-heading">
    <div>
      <div class="eyebrow">EVENT DETAILS</div>
      <h1>{{ event?.name ?? 'Event details' }}</h1>
      <p>Review event information and manage its lifecycle.</p>
    </div>
    <div class="d-flex flex-wrap ga-2">
      <v-btn variant="outlined" prepend-icon="mdi-arrow-left" to="/events">All events</v-btn>
      <v-btn v-if="event && canEdit" color="primary" prepend-icon="mdi-pencil" :to="{ name: 'event-edit', params: { id: event.id } }">Edit event</v-btn>
    </div>
  </div>

  <v-alert v-if="errorMessage" type="error" variant="tonal" class="mt-5">{{ errorMessage }}</v-alert>
  <v-skeleton-loader v-if="loading" type="article, article" class="mt-5" />
  <template v-else-if="event">
    <v-card border flat rounded="lg" class="mt-5">
      <v-card-text class="pa-5">
        <div class="d-flex align-center justify-space-between flex-wrap ga-3">
          <div class="text-body-2 text-medium-emphasis">Event status</div>
          <v-chip :color="statusColor(event.status)" variant="tonal">{{ statusLabel(event.status) }}</v-chip>
        </div>
        <v-divider class="my-4" />
        <v-row>
          <v-col cols="12" md="6"><div class="detail-label">Organizer</div><div class="detail-value">{{ event.organizerName }}</div></v-col>
          <v-col cols="12" md="6"><div class="detail-label">Slug</div><div class="detail-value">{{ event.slug }}</div></v-col>
          <v-col cols="12" md="6"><div class="detail-label">Start</div><div class="detail-value">{{ formatDate(event.startDate) }}</div></v-col>
          <v-col cols="12" md="6"><div class="detail-label">End</div><div class="detail-value">{{ formatDate(event.endDate) }}</div></v-col>
          <v-col cols="12" md="6"><div class="detail-label">Venue</div><div class="detail-value">{{ event.venue || 'Not specified' }}</div></v-col>
          <v-col cols="12" md="6"><div class="detail-label">Location</div><div class="detail-value">{{ event.location || 'Not specified' }}</div></v-col>
          <v-col cols="12"><div class="detail-label">Description</div><div class="detail-value description-value">{{ event.description || 'No description provided.' }}</div></v-col>
          <v-col cols="12"><div class="detail-label">Event time zone</div><div class="detail-value">{{ event.timeZoneId }}</div></v-col>
        </v-row>
      </v-card-text>
    </v-card>
    <v-card v-if="canEdit && transitions.length" border flat rounded="lg" class="mt-5">
      <v-card-title class="text-subtitle-1">Event lifecycle</v-card-title>
      <v-card-text>
        <p class="text-body-2 text-medium-emphasis mb-4">Only valid next statuses are available for the current stage.</p>
        <div class="d-flex flex-wrap ga-2">
          <v-btn v-for="status in transitions" :key="status" :color="status === 'Cancelled' ? 'error' : 'primary'" variant="tonal" :loading="changingStatus === status" :disabled="Boolean(changingStatus)" @click="changeStatus(status)">
            {{ status === 'Cancelled' ? 'Cancel event' : `Move to ${statusLabel(status)}` }}
          </v-btn>
        </div>
      </v-card-text>
    </v-card>
    <RegistrationPanel v-if="canEdit" :key="event.id" :event-id="event.id" :organizer-id="event.organizerId" />
    <RaceConfigurationPanel :event-id="event.id" :zone="event.timeZoneId" />
  </template>
  <v-snackbar v-model="success" color="success" timeout="2500">Event status updated.</v-snackbar>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { eventStatusTransitions, eventsApi, getApiError } from '../services/events'
import type { EventDetails, EventStatus } from '../services/events'
import RegistrationPanel from '../components/events/RegistrationPanel.vue'
import RaceConfigurationPanel from '../components/events/RaceConfigurationPanel.vue'
import { useAuthStore } from '../stores/auth'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const event = ref<EventDetails | null>(null)
const loading = ref(true)
const errorMessage = ref('')
const success = ref(false)
const changingStatus = ref<EventStatus | null>(null)
const canEdit = computed(() => Boolean(event.value && auth.canManageOrganizer(event.value.organizerId)))
const transitions = computed(() => event.value ? eventStatusTransitions[event.value.status] : [])

onMounted(load)
async function load() {
  loading.value = true
  errorMessage.value = ''
  try {
    event.value = await eventsApi.get(String(route.params.id))
  } catch (error) {
    errorMessage.value = getApiError(error, 'Unable to load this event.')
  } finally {
    loading.value = false
  }
}
async function changeStatus(status: EventStatus) {
  if (!event.value) return
  if (status === 'Cancelled' && !window.confirm('Cancel this event? This action may not be reversible.')) return
  changingStatus.value = status
  try {
    await eventsApi.changeStatus(event.value.id, status)
    await load()
    success.value = true
  } catch (error) {
    errorMessage.value = getApiError(error, 'Unable to update event status.')
  } finally {
    changingStatus.value = null
  }
}
function statusLabel(status: EventStatus) {
  return status.replace(/([a-z])([A-Z])/g, '$1 $2')
}
function statusColor(status: EventStatus) {
  return ({ Draft: 'grey', Published: 'info', RegistrationOpen: 'success', RegistrationClosed: 'warning', Ready: 'primary', Live: 'error', Completed: 'teal', Cancelled: 'grey-darken-1' } as Record<EventStatus, string>)[status]
}
function formatDate(value: string) {
  return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value))
}
</script>
