<template>
  <v-card class="my-4" variant="outlined" :loading="loading">
    <v-card-title>Registration readiness</v-card-title>
    <v-card-text>
      <p class="text-body-2 mb-4">Roster and bib readiness only. Check-in progress is informational.</p>
      <v-alert v-if="error" type="error" class="mb-3">
        {{ error }}
        <v-btn variant="text" :disabled="loading" @click="load">Retry</v-btn>
      </v-alert>
      <p v-else-if="loading && !report" role="status">Loading registration readiness…</p>
      <template v-if="report">
        <div class="d-flex align-center ga-3 mb-3">
          <h3 class="text-subtitle-1">Event summary</h3>
          <v-chip size="small" :color="levels[report.assessment.level].color">{{ levels[report.assessment.level].title }}</v-chip>
        </div>
        <dl class="metrics mb-4">
          <div v-for="metric in metrics" :key="metric.key">
            <dt>{{ metric.label }}</dt>
            <dd class="text-h6">{{ report.summary[metric.key] }}</dd>
          </div>
        </dl>
        <ul v-if="report.assessment.warnings.length" class="warnings mb-4">
          <li v-for="warning in report.assessment.warnings" :key="warning.label">
            <strong>{{ levels[warning.level].title }}</strong> · {{ warning.label }}<span v-if="warning.count !== null"> ({{ warning.count }})</span>
          </li>
        </ul>
        <p v-if="!report.summary.total" class="mb-4">This event has no registrations.</p>

        <v-select v-model="raceId" :items="report.races" item-title="name" item-value="id"
          label="Race filter" placeholder="All races" clearable density="compact" class="race-filter" />
        <h3 class="text-subtitle-1 mb-2">Per-race breakdown</h3>
        <p v-if="!visibleRaces.length" class="mb-4">No races configured.</p>
        <v-table v-else density="compact" class="mb-5">
          <thead><tr><th>Race</th><th>Registrations</th><th>Bibs</th><th>Confirmed check-in</th><th>Readiness</th></tr></thead>
          <tbody>
            <tr v-for="race in visibleRaces" :key="race.id">
              <th scope="row">{{ race.name }}<div v-if="!race.active" class="text-caption">Inactive</div></th>
              <td>
                {{ race.counts.total }} total · {{ race.counts.confirmed }} confirmed
                <div class="text-caption">
                  {{ race.counts.pending }} pending · {{ race.counts.waitlisted }} waitlisted ·
                  {{ race.counts.withdrawn }} withdrawn · {{ race.counts.historical }} historical
                </div>
              </td>
              <td>{{ race.counts.bibsAssigned }} assigned<div>{{ race.counts.confirmedWithoutBib }} confirmed without a bib</div></td>
              <td>{{ race.counts.confirmedCheckedIn }} checked in<div>{{ race.counts.confirmedNotCheckedIn }} not checked in</div></td>
              <td>
                <v-chip size="small" :color="levels[race.assessment.level].color">{{ levels[race.assessment.level].title }}</v-chip>
                <ul v-if="race.assessment.warnings.length" class="warnings text-caption">
                  <li v-for="warning in race.assessment.warnings" :key="warning.label">
                    {{ warning.label }}<span v-if="warning.count !== null"> ({{ warning.count }})</span>
                  </li>
                </ul>
              </td>
            </tr>
          </tbody>
        </v-table>
        <v-divider class="mb-4" />
        <h3 class="text-subtitle-1 mb-2">Roster export</h3>
        <p class="text-body-2 mb-3">Uses the race filter above. To keep leading zeros in a spreadsheet, import Bib Number as text.</p>
        <v-alert v-if="exportError" type="error" class="mb-3">{{ exportError }}</v-alert>
        <div class="export-controls">
          <v-select v-model="status" :items="statuses" label="Registration status" placeholder="All statuses"
            clearable density="compact" hide-details />
          <v-select v-model="bib" :items="['Assigned', 'Unassigned']" label="Bib assignment" placeholder="Any"
            clearable density="compact" hide-details />
          <v-btn :disabled="exporting" :loading="exporting" @click="exportRoster">Export roster CSV</v-btn>
        </div>
      </template>
    </v-card-text>
  </v-card>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { api } from '../../services/api'
import { getApiError } from '../../services/events'

const metrics = [
  { key: 'total', label: 'Total registrations' },
  { key: 'confirmed', label: 'Confirmed' },
  { key: 'pending', label: 'Pending' },
  { key: 'waitlisted', label: 'Waitlisted' },
  { key: 'withdrawn', label: 'Withdrawn' },
  { key: 'historical', label: 'Historical DNF / DNS / DQ' },
  { key: 'confirmedWithoutBib', label: 'Confirmed without a bib' },
  { key: 'bibsAssigned', label: 'Bibs assigned' },
  { key: 'confirmedCheckedIn', label: 'Confirmed checked in' },
  { key: 'confirmedNotCheckedIn', label: 'Confirmed not checked in' },
  { key: 'inactiveRacesWithRegistrations', label: 'Inactive races with registrations' },
] as const
const levels = {
  actionRequired: { title: 'Action required', color: 'error' },
  attention: { title: 'Attention', color: 'warning' },
  ready: { title: 'Ready', color: 'success' },
} as const
const statuses = [
  { title: 'Confirmed', value: 'Confirmed' }, { title: 'Pending', value: 'Pending' },
  { title: 'Waitlisted', value: 'Waitlisted' }, { title: 'Withdrawn', value: 'Cancelled' },
  { title: 'DNF', value: 'Dnf' }, { title: 'DNS', value: 'Dns' },
  { title: 'Disqualified', value: 'Disqualified' },
]

type Counts = Record<typeof metrics[number]['key'], number>
type Level = keyof typeof levels
interface Assessment { level: Level; warnings: { level: Level; count: number | null; label: string }[] }
interface ReadinessReport {
  summary: Counts
  assessment: Assessment
  races: { id: string; name: string; active: boolean; counts: Counts; assessment: Assessment }[]
}

const props = defineProps<{ eventId: string }>()
const path = '/events/' + props.eventId
const report = ref<ReadinessReport | null>(null)
const raceId = ref<string | null>(null)
const status = ref<string | null>(null)
const bib = ref<string | null>(null)
const loading = ref(false)
const exporting = ref(false)
const error = ref('')
const exportError = ref('')
const visibleRaces = computed(() => report.value?.races.filter(race => !raceId.value || race.id === raceId.value) ?? [])

async function load() {
  if (loading.value) return
  loading.value = true
  error.value = ''
  try {
    const { data } = await api.get<ReadinessReport>(path + '/readiness')
    report.value = data
  } catch (failure) {
    const body = (failure as { response?: { data?: unknown } })?.response?.data
    error.value = typeof body === 'string' ? body : getApiError(failure, 'Unable to load registration readiness.')
  } finally {
    loading.value = false
  }
}

async function exportRoster() {
  if (exporting.value) return
  exporting.value = true
  exportError.value = ''
  try {
    const { data, headers } = await api.get<Blob>(path + '/registrations/export', {
      params: { raceId: raceId.value, status: status.value, bib: bib.value }, responseType: 'blob',
    })
    const href = URL.createObjectURL(data)
    const link = document.createElement('a')
    const filename = /filename="?([a-z0-9-]+\.csv)"?/i.exec(String(headers['content-disposition'] ?? ''))?.[1]
    link.href = href
    link.download = filename ?? 'racebib-roster-' + props.eventId + '-' + new Date().toISOString().slice(0, 10) + '.csv'
    document.body.appendChild(link)
    link.click()
    link.remove()
    window.setTimeout(() => URL.revokeObjectURL(href), 1000)
  } catch (failure) {
    const body = (failure as { response?: { data?: unknown } })?.response?.data
    exportError.value = body instanceof Blob && body.type.startsWith('text/plain')
      ? await body.text() : getApiError(failure, 'Unable to export the roster.')
  } finally {
    exporting.value = false
  }
}

onMounted(load)
</script>

<style scoped>
.metrics { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 8px 16px; }
.metrics dt { font-size: .875rem; }
.metrics dd { margin: 0; }
.warnings { padding-inline-start: 20px; }
.race-filter { max-width: 400px; }
.export-controls { display: flex; flex-wrap: wrap; align-items: center; gap: 12px; }
.export-controls > :deep(.v-input) { flex: 1 1 190px; }
</style>
