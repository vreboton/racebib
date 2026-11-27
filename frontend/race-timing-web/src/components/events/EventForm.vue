<template>
  <v-form ref="formRef" v-model="valid" @submit.prevent="submit">
    <v-card border flat rounded="lg">
      <v-card-text class="pa-5">
        <v-alert v-if="errorMessage" type="error" variant="tonal" class="mb-5" closable @click:close="errorMessage = ''">
          {{ errorMessage }}
        </v-alert>
        <v-alert v-if="!event && !loadingOrganizers && !organizers.length" type="warning" variant="tonal" class="mb-5">
          You do not have event-management access to an active organizer. Ask an organizer administrator to update your membership.
        </v-alert>
        <v-alert v-if="event?.hasArmedTiming" type="warning" variant="tonal" class="mb-5">
          Timing is already armed. Editing the event schedule does not change any frozen race timing window.
          Check the frozen window in Race Control before recording crossings.
        </v-alert>
        <v-row>
          <v-col cols="12" md="8">
            <v-text-field v-model="form.name" label="Event name" :rules="[required, maxLength(200)]" counter="200" />
          </v-col>
          <v-col cols="12" md="4">
            <v-select
              v-if="!event"
              v-model="form.organizerId"
              :items="organizers"
              item-title="name"
              item-value="id"
              label="Organizer"
              :rules="[required]"
              :loading="loadingOrganizers"
              :disabled="loadingOrganizers || organizers.length === 0"
              @update:model-value="syncOrganizerName"
            />
            <v-text-field v-else :model-value="event.organizerName" label="Organizer" readonly />
          </v-col>
          <v-col cols="12" md="6">
            <v-text-field v-model="form.slug" label="Event slug" hint="Lowercase words separated by hyphens" persistent-hint :rules="[required, slugRule, maxLength(220)]" />
          </v-col>
          <v-col cols="12" md="6">
            <v-text-field v-model="form.organizerName" label="Organizer display name" :rules="[required, maxLength(160)]" />
          </v-col>
          <v-col cols="12">
            <v-textarea v-model="form.description" label="Description" rows="3" auto-grow />
          </v-col>
          <v-col cols="12" md="6">
            <v-text-field v-model="form.venue" label="Venue" />
          </v-col>
          <v-col cols="12" md="6">
            <v-text-field v-model="form.location" label="City / Location" />
          </v-col>
          <v-col cols="12" md="6">
            <ScheduleInput v-model="form.startUtc" label="Event start" :zone="form.timeZoneId.trim()" :preserve-instant-on-zone-change="Boolean(event)" required />
          </v-col>
          <v-col cols="12" md="6">
            <ScheduleInput v-model="form.endUtc" label="Event end" :zone="form.timeZoneId.trim()" :preserve-instant-on-zone-change="Boolean(event)" required />
          </v-col>
          <v-col cols="12" md="6">
            <v-text-field v-model="form.timeZoneId" label="Time zone" hint="IANA time-zone ID, e.g. Asia/Manila" persistent-hint :rules="[required]" />
          </v-col>
        </v-row>
      </v-card-text>
      <v-divider />
      <v-card-actions class="pa-5">
        <v-spacer />
        <v-btn variant="text" :disabled="saving" @click="$emit('cancel')">Cancel</v-btn>
        <v-btn color="primary" type="submit" :loading="saving" :disabled="!valid || !form.startUtc || !form.endUtc || saving || loadingOrganizers || (!event && !form.organizerId)">
          {{ event ? 'Save changes' : 'Create event' }}
        </v-btn>
      </v-card-actions>
    </v-card>
  </v-form>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import ScheduleInput from './ScheduleInput.vue'
import { isTimeZone, utcTimestamp } from '../../services/eventTime'
import type { EventDetails, EventPayload, Organizer } from '../../services/events'
import { getApiError, loadOrganizers } from '../../services/events'
import { useAuthStore } from '../../stores/auth'

const props = defineProps<{ event?: EventDetails | null }>()
const auth = useAuthStore()
const emit = defineEmits<{ submit: [payload: EventPayload & { organizerId?: string }]; cancel: [] }>()
const formRef = ref()
const valid = ref(false)
const saving = ref(false)
const errorMessage = ref('')
const organizers = ref<Organizer[]>([])
const loadingOrganizers = ref(false)
const required = (value: unknown) => Boolean(String(value ?? '').trim()) || 'This field is required'
const maxLength = (max: number) => (value: string) => !value || value.length <= max || `Maximum ${max} characters`
const slugRule = (value: string) => /^[a-zA-Z0-9]+(?:-[a-zA-Z0-9]+)*$/.test(value) || 'Use letters, numbers and single hyphens'
const form = reactive({
  organizerId: '',
  name: '',
  slug: '',
  description: '',
  organizerName: '',
  venue: '',
  location: '',
  startUtc: props.event ? utcTimestamp(props.event.startDate) : '',
  endUtc: props.event ? utcTimestamp(props.event.endDate) : '',
  timeZoneId: props.event?.timeZoneId ?? 'Asia/Manila',
})
function syncOrganizerName(id: string) {
  form.organizerName = organizers.value.find(item => item.id === id)?.name ?? ''
}

onMounted(async () => {
  if (props.event) {
    Object.assign(form, {
      name: props.event.name, slug: props.event.slug, description: props.event.description ?? '',
      organizerName: props.event.organizerName, venue: props.event.venue ?? '',
      location: props.event.location ?? '',
    })
    return
  }
  loadingOrganizers.value = true
  try {
    const availableOrganizers = await loadOrganizers()
    organizers.value = availableOrganizers.filter(organizer => auth.canManageOrganizer(organizer.id))
    if (organizers.value.length === 1) {
      form.organizerId = organizers.value[0].id
      syncOrganizerName(form.organizerId)
    }
  } catch (error) {
    errorMessage.value = getApiError(error, 'Unable to load organizers.')
  } finally {
    loadingOrganizers.value = false
  }
})

function submit() {
  if (!formRef.value?.validate || !valid.value || !form.startUtc || !form.endUtc) return
  if (!isTimeZone(form.timeZoneId.trim())) {
    errorMessage.value = 'Enter a valid IANA time zone.'
    return
  }
  if (Date.parse(form.endUtc) <= Date.parse(form.startUtc)) {
    errorMessage.value = 'End must be after start.'
    return
  }
  const payload: EventPayload & { organizerId?: string } = {
    name: form.name.trim(), slug: form.slug.trim().toLowerCase(),
    description: form.description.trim() || null, organizerName: form.organizerName.trim(),
    venue: form.venue.trim() || null, location: form.location.trim() || null,
    startDateUtc: form.startUtc, endDateUtc: form.endUtc,
    timeZoneId: form.timeZoneId.trim(),
  }
  if (!props.event) payload.organizerId = form.organizerId
  emit('submit', payload)
}

defineExpose({ setSaving: (value: boolean) => { saving.value = value }, setError: (value: string) => { errorMessage.value = value } })
</script>
