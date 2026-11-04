<template>
  <v-form ref="formRef" v-model="valid" @submit.prevent="submit">
    <v-card border flat rounded="lg">
      <v-card-text class="pa-5">
        <v-alert v-if="errorMessage" type="error" variant="tonal" class="mb-5" closable @click:close="errorMessage = ''">
          {{ errorMessage }}
        </v-alert>
        <v-alert v-if="!event && !loadingOrganizers && !organizers.length" type="warning" variant="tonal" class="mb-5">
          No active organizer is available for your account. Ask an organizer administrator to add you before creating an event.
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
            <v-text-field v-model="form.startLocal" label="Event start" type="datetime-local" :rules="[required]" />
          </v-col>
          <v-col cols="12" md="6">
            <v-text-field v-model="form.endLocal" label="Event end" type="datetime-local" :rules="[required, endAfterStart]" />
          </v-col>
          <v-col cols="12" md="6">
            <v-text-field v-model="form.timeZoneId" label="Time zone" hint="IANA or system time-zone ID, e.g. Asia/Manila" persistent-hint :rules="[required]" />
          </v-col>
        </v-row>
      </v-card-text>
      <v-divider />
      <v-card-actions class="pa-5">
        <v-spacer />
        <v-btn variant="text" :disabled="saving" @click="$emit('cancel')">Cancel</v-btn>
        <v-btn color="primary" type="submit" :loading="saving" :disabled="!valid || saving || loadingOrganizers || (!event && !form.organizerId)">
          {{ event ? 'Save changes' : 'Create event' }}
        </v-btn>
      </v-card-actions>
    </v-card>
  </v-form>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import type { EventDetails, EventPayload, Organizer } from '../../services/events'
import { getApiError, loadOrganizers } from '../../services/events'

const props = defineProps<{ event?: EventDetails | null }>()
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
  startLocal: '',
  endLocal: '',
  timeZoneId: 'Asia/Manila',
})
const endAfterStart = computed(() => () => !form.startLocal || !form.endLocal || new Date(form.endLocal) > new Date(form.startLocal) || 'End must be after start')

function toLocalInput(value?: string) {
  if (!value) return ''
  const date = new Date(value)
  const local = new Date(date.getTime() - date.getTimezoneOffset() * 60000)
  return local.toISOString().slice(0, 16)
}

function syncOrganizerName(id: string) {
  form.organizerName = organizers.value.find(item => item.id === id)?.name ?? ''
}

onMounted(async () => {
  if (props.event) {
    Object.assign(form, {
      name: props.event.name, slug: props.event.slug, description: props.event.description ?? '',
      organizerName: props.event.organizerName, venue: props.event.venue ?? '',
      location: props.event.location ?? '', startLocal: toLocalInput(props.event.startDate),
      endLocal: toLocalInput(props.event.endDate), timeZoneId: props.event.timeZoneId,
    })
    return
  }
  loadingOrganizers.value = true
  try {
    organizers.value = await loadOrganizers()
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
  if (!formRef.value?.validate || !valid.value) return
  const payload: EventPayload & { organizerId?: string } = {
    name: form.name.trim(), slug: form.slug.trim().toLowerCase(),
    description: form.description.trim() || null, organizerName: form.organizerName.trim(),
    venue: form.venue.trim() || null, location: form.location.trim() || null,
    startDateUtc: new Date(form.startLocal).toISOString(), endDateUtc: new Date(form.endLocal).toISOString(),
    timeZoneId: form.timeZoneId.trim(),
  }
  if (!props.event) payload.organizerId = form.organizerId
  emit('submit', payload)
}

defineExpose({ setSaving: (value: boolean) => { saving.value = value }, setError: (value: string) => { errorMessage.value = value } })
</script>
