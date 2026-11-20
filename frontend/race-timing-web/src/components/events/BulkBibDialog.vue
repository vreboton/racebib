<template>
  <v-dialog :model-value="true" :persistent="busy" max-width="850"
    @update:model-value="visible => { if (!visible) emit('close') }">
    <v-card title="Bulk assign bibs">
      <v-card-text>
        <v-alert v-if="error" type="error" class="mb-4">{{ error }}</v-alert>
        <v-alert v-if="receipt" type="success" role="status" class="mb-4">
          Assigned {{ receipt.assignedCount }} bibs. Skipped {{ receipt.skippedCount }} registrations.
        </v-alert>
        <v-form id="bulk-bib-preview" @submit.prevent="previewBibs">
          <v-select v-model="raceId" :items="races" item-title="name" item-value="id" label="Race (optional)"
            placeholder="All races" clearable :disabled="busy" />
          <div class="bib-range">
            <v-text-field v-model.number="start" label="Start bib" type="number" min="0" max="2147483647"
              step="1" :disabled="busy" required />
            <v-text-field v-model.number="end" label="End bib" type="number" min="0" max="2147483647"
              step="1" :disabled="busy" required />
            <v-text-field v-model.number="width" label="Minimum digits" type="number" min="0" max="10"
              step="1" :disabled="busy" required />
          </div>
        </v-form>
        <template v-if="preview">
          <p class="mb-2" role="status">
            {{ preview.scopeCount }} in scope · {{ preview.eligibleCount }} eligible ·
            {{ preview.skippedCount }} skipped · {{ preview.changeCount }} assignments
          </p>
          <v-alert v-if="preview.error" type="warning" class="mb-3">{{ preview.error }}</v-alert>
          <v-alert v-else-if="!preview.changeCount" type="info" class="mb-3">No eligible registrations to assign.</v-alert>
          <v-table v-if="preview.rows.length" density="compact" fixed-header height="320">
            <thead><tr><th>Runner</th><th>Race</th><th>Current bib</th><th>Assignment</th></tr></thead>
            <tbody>
              <tr v-for="row in preview.rows" :key="row.registrationId">
                <td>{{ row.runnerName }}</td><td>{{ row.race }}</td><td>{{ row.currentBib ?? '—' }}</td>
                <td>{{ row.proposedBib ?? row.skipReason ?? 'No bib proposed' }}</td>
              </tr>
            </tbody>
          </v-table>
        </template>
      </v-card-text>
      <v-card-actions class="flex-wrap">
        <v-btn :disabled="busy" @click="emit('close')">Close</v-btn>
        <v-spacer />
        <v-btn type="submit" form="bulk-bib-preview" :disabled="busy">Preview assignments</v-btn>
        <v-btn color="primary" :disabled="busy || !preview?.token" @click="confirmBibs">Confirm assignments</v-btn>
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>

<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { api } from '../../services/api'
import { getApiError } from '../../services/events'
import { loadConfiguration, type Race } from '../../services/raceConfiguration'

interface BulkPreview {
  scopeCount: number
  eligibleCount: number
  skippedCount: number
  changeCount: number
  token: string | null
  error: string | null
  rows: {
    registrationId: string
    runnerName: string
    race: string
    currentBib: string | null
    proposedBib: string | null
    skipReason: string | null
  }[]
}
interface Receipt { assignedCount: number; skippedCount: number }

const props = defineProps<{ eventId: string }>()
const emit = defineEmits<{ close: []; assigned: [] }>()
const url = '/events/' + props.eventId + '/registrations/bibs'
const races = ref<Race[]>([])
const raceId = ref<string | null>(null)
const start = ref<number | ''>(1)
const end = ref<number | ''>(1000)
const width = ref<number | ''>(0)
const preview = ref<BulkPreview | null>(null)
const receipt = ref<Receipt | null>(null)
const busy = ref(false)
const error = ref('')

watch([raceId, start, end, width], () => {
  preview.value = null
  receipt.value = null
  error.value = ''
}, { flush: 'sync' })

async function run(action: () => Promise<void>) {
  if (busy.value) return
  busy.value = true
  error.value = ''
  try {
    await action()
  } catch (failure) {
    const body = (failure as { response?: { data?: unknown } })?.response?.data
    error.value = typeof body === 'string' ? body : getApiError(failure, 'Bulk bib assignment failed.')
  } finally {
    busy.value = false
  }
}

function previewBibs() {
  return run(async () => {
    preview.value = null
    receipt.value = null
    const { data } = await api.post<BulkPreview>(url + '/preview', {
      raceId: raceId.value, start: start.value === '' ? null : start.value,
      end: end.value === '' ? null : end.value, width: width.value === '' ? 0 : width.value,
    })
    preview.value = data
  })
}

function confirmBibs() {
  return run(async () => {
    const token = preview.value?.token
    if (!token) return
    // Require another preview after any unknown or stale confirmation outcome.
    preview.value = null
    const { data } = await api.post<Receipt>(url + '/confirm', { token })
    receipt.value = data
    emit('assigned')
  })
}

onMounted(() => run(async () => { races.value = (await loadConfiguration(props.eventId)).races }))
</script>

<style scoped>
.bib-range {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 12px;
}

@media (max-width: 540px) {
  .bib-range {
    grid-template-columns: 1fr;
    gap: 0;
  }
}
</style>
