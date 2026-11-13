<template>
  <div class="page-heading">
    <div>
      <div class="eyebrow">EVENT MANAGEMENT</div>
      <h1>{{ isEdit ? 'Edit event' : 'Create event' }}</h1>
      <p>{{ isEdit ? 'Update event details before it moves into operations.' : 'Set up an event for one of your organizers.' }}</p>
    </div>
    <v-btn variant="text" prepend-icon="mdi-arrow-left" to="/events">Back to events</v-btn>
  </div>
  <v-skeleton-loader v-if="loadingEvent" type="article, actions" class="mt-5" />
  <EventForm v-else ref="eventFormRef" :event="event" class="mt-5" @submit="save" @cancel="router.push('/events')" />
  <v-snackbar v-model="success" color="success" timeout="2500">Event saved successfully.</v-snackbar>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import EventForm from '../components/events/EventForm.vue'
import { eventsApi, getApiError } from '../services/events'
import type { EventDetails, EventPayload } from '../services/events'

const route = useRoute()
const router = useRouter()
const eventId = computed(() => String(route.params.id ?? ''))
const isEdit = computed(() => Boolean(eventId.value))
const event = ref<EventDetails | null>(null)
const loadingEvent = ref(false)
const success = ref(false)
const eventFormRef = ref<InstanceType<typeof EventForm> | null>(null)

onMounted(async () => {
  if (!isEdit.value) return
  loadingEvent.value = true
  try {
    event.value = await eventsApi.get(eventId.value)
  } catch (error) {
    await router.replace({ name: 'events', query: { error: getApiError(error, 'Unable to load event.') } })
  } finally {
    loadingEvent.value = false
  }
})

async function save(payload: EventPayload & { organizerId?: string }) {
  eventFormRef.value?.setSaving(true)
  try {
    let saved: EventDetails
    if (isEdit.value) {
      const { organizerId: _ignored, ...updatePayload } = payload
      saved = await eventsApi.update(eventId.value, updatePayload)
    } else {
      if (!payload.organizerId) throw new Error('Select an organizer before creating an event.')
      saved = await eventsApi.create(payload as EventPayload & { organizerId: string })
    }
    success.value = true
    await router.push({ name: 'event-details', params: { id: saved.id } })
  } catch (error) {
    eventFormRef.value?.setError(getApiError(error, 'Unable to save event.'))
  } finally {
    eventFormRef.value?.setSaving(false)
  }
}
</script>
