<template>
  <div class="mt-4">
    <div class="d-flex align-center justify-space-between flex-wrap ga-2">
      <h2 class="text-h6">Race Control</h2>
      <v-btn variant="outlined" prepend-icon="mdi-refresh" :disabled="loading" @click="refresh">Refresh</v-btn>
    </div>
    <v-tabs v-model="tab" aria-label="Race Control">
      <v-tab value="overview">Overview</v-tab>
      <v-tab value="exceptions">Timing Exceptions</v-tab>
      <v-tab value="runners">Runner Results</v-tab>
      <v-tab value="audit">Audit History</v-tab>
    </v-tabs>
    <v-alert v-if="error" type="error" variant="tonal" class="mt-4" role="alert">{{ error }}</v-alert>
    <v-progress-linear v-if="loading" indeterminate class="mt-4" aria-label="Loading Race Control" />
    <template v-else-if="overview && !error">
      <div v-if="tab === 'overview'" class="mt-4">
        <p>{{ overview.actionable }} evidence exceptions · {{ overview.bibConflicts }} bib ownership conflicts</p>
        <p v-if="!overview.races.length">No races are armed for timing.</p>
        <v-list v-else density="compact">
          <v-list-item v-for="race in overview.races" :key="race.raceId" :title="race.name"
            :subtitle="`${race.registered} frozen registrations · ${race.acceptedVisits} accepted visits · ${race.recordedFinishes} recorded finishes`" />
        </v-list>
      </div>
      <template v-else>
        <v-select v-if="tab === 'exceptions'" v-model="kind" :items="exceptionKinds" label="Exception view" class="mt-4" />
        <v-select v-if="tab === 'audit'" v-model="auditKind" :items="auditKinds" label="Decision type" class="mt-4" />
        <v-select v-if="showRunners" v-model="raceId" :items="overview.races" item-title="name" item-value="raceId"
          label="Race" class="mt-4" />
        <p v-if="tab === 'exceptions' && kind === 'calculation'" class="mb-3">
          Warnings reflect the current evidence for this roster page and may clear as the race progresses.
        </p>
        <p v-if="!visibleCount" class="mt-3">{{ emptyMessage }}</p>
        <v-list v-else-if="!showRunners" density="comfortable">
          <v-list-item v-for="row in evidence" :key="`${row.observationId ?? row.registrationId}-${row.revision ?? 0}`">
            <v-list-item-title>Bib {{ row.bibNumber }} · {{ label(row.outcome ?? row.classification ?? 'Bib ownership conflict') }}</v-list-item-title>
            <v-list-item-subtitle v-if="row.decidedAtUtc || row.receivedAtUtc">{{ row.decidedAtUtc ?? row.receivedAtUtc }}</v-list-item-subtitle>
            <p v-if="row.reason" class="text-body-2">
              {{ row.previousClassification ? label(row.previousClassification) + ' → ' : '' }}{{ label(row.classification ?? '') }}
              · {{ row.actorUserId }} · {{ row.reason }}
            </p>
            <template v-if="row.observationId" #append>
              <v-btn variant="text" @click="openResolution(row.observationId)">Review</v-btn>
            </template>
          </v-list-item>
        </v-list>
        <v-card v-for="row in visibleRunners" :key="row.timing.registrationId" variant="outlined" class="mb-3">
          <v-card-title class="text-subtitle-1">Bib {{ row.timing.bibNumber }} · {{ label(row.classification) }}</v-card-title>
          <v-card-text>
            <p v-if="row.needsReview" class="text-warning">Supporting evidence changed. Review this classification.</p>
            <p v-if="row.bibConflict" class="text-error">Reconcile the bib ownership before classifying as Finished.</p>
            <p v-if="warnings(row).length">{{ warnings(row).join(' · ') }}</p>
            <p>Provisional finish · Gun {{ elapsed(row.timing.provisionalFinish?.gunElapsedMicroseconds) }}
              · Net {{ elapsed(row.timing.provisionalFinish?.netElapsedMicroseconds) }}</p>
            <details v-if="tab === 'runners'" class="mt-2">
              <summary>Ordered checkpoint splits</summary>
              <v-table density="compact">
                <thead><tr><th>Visit</th><th>Crossing (UTC)</th><th>Gun</th><th>Net</th><th>Segment</th><th>Evidence</th></tr></thead>
                <tbody><tr v-for="visit in row.timing.visits" :key="visit.occurrenceId">
                  <td>{{ visit.sequence }} · {{ visit.name }}</td><td>{{ visit.crossingAtUtc ?? 'Missing' }}</td>
                  <td>{{ elapsed(visit.gunElapsedMicroseconds) }}</td><td>{{ elapsed(visit.netElapsedMicroseconds) }}</td>
                  <td>{{ elapsed(visit.segmentMicroseconds) }}</td>
                  <td>{{ visit.flags.map(label).join(', ') }} {{ visit.cutoff ? label(visit.cutoff.status) : '' }}
                    <v-btn v-if="visit.observationId" size="small" variant="text" @click="openResolution(visit.observationId)">Review</v-btn>
                  </td>
                </tr></tbody>
              </v-table>
            </details>
          </v-card-text>
          <v-card-actions v-if="tab === 'runners'">
            <v-btn variant="tonal" @click="openClassification(row)">Change official classification</v-btn>
          </v-card-actions>
        </v-card>
        <div v-if="total > pageSize" class="d-flex align-center ga-3 mt-3">
          <v-btn :disabled="offset === 0" variant="outlined" @click="offset -= pageSize">Previous</v-btn>
          <span>{{ offset + 1 }}–{{ Math.min(offset + pageSize, total) }} of {{ total }}{{ showRunners ? ' runners' : '' }}</span>
          <v-btn :disabled="offset + pageSize >= total" variant="outlined" @click="offset += pageSize">Next</v-btn>
        </div>
      </template>
    </template>

    <v-dialog v-model="dialog" max-width="680" :persistent="saving">
      <v-card>
        <v-card-title>{{ mode === 'evidence' ? 'Review timing evidence' : 'Official classification' }}</v-card-title>
        <v-card-text>
          <v-alert v-if="actionError" type="error" variant="tonal" class="mb-3">{{ actionError }}</v-alert>
          <v-progress-linear v-if="detailLoading" indeterminate aria-label="Loading evidence" />
          <template v-if="resolution">
            <p>Bib {{ resolution.bibNumber }} · Observed {{ resolution.observedAtUtc }}</p>
            <p>Server received {{ resolution.receivedAtUtc }}</p>
            <p class="mb-2">{{ resolution.acceptedOccurrenceId ? 'Accepted slot · ' + (resolution.choices.find(visit => visit.occurrenceId === resolution?.acceptedOccurrenceId)?.name ?? resolution.acceptedOccurrenceId) : 'This observation has no accepted slot.' }}</p>
            <ul class="ps-5 mb-3"><li v-for="decision in resolution.decisions" :key="decision.revision">
              Revision {{ decision.revision }} · {{ label(decision.outcome) }} · {{ decision.decidedAtUtc }}
              · {{ decision.actorUserId }} · {{ decision.reason }}
            </li></ul>
            <v-select v-model="proposedAction" :items="resolutionChoices" label="Proposed action" />
            <p class="mb-3">{{ effect }}</p>
            <p v-if="choice?.currentObservationId && choice.currentObservationId !== resolution.id" class="mb-3">
              The slot currently contains observation {{ choice.currentObservationId }}.
              <v-btn variant="text" size="small" @click="openResolution(choice.currentObservationId)">Review occupied slot</v-btn>
            </p>
          </template>
          <template v-if="selectedRunner">
            <p class="mb-3">Bib {{ selectedRunner.timing.bibNumber }} · Current {{ label(selectedRunner.classification) }}</p>
            <v-select v-model="classification" :items="classificationOptions" label="Official classification" />
          </template>
          <v-textarea v-if="resolution || selectedRunner" v-model="reason" label="Decision reason"
            maxlength="300" counter rows="2" :disabled="saving" />
        </v-card-text>
        <v-card-actions>
          <v-btn variant="text" :disabled="saving" @click="dialog = false">Close</v-btn>
          <v-spacer />
          <v-btn v-if="stale" variant="outlined" :disabled="saving" @click="reloadDecision">Reload current decision</v-btn>
          <v-btn color="primary" variant="flat" :loading="saving" :disabled="!canSave" @click="save">Confirm decision</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>
    <v-snackbar v-model="saved" color="success">Decision recorded.</v-snackbar>
  </div>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { isAxiosError } from 'axios'
import { getApiError } from '../../services/events'
import { raceControlApi } from '../../services/raceControl'
import type { EvidenceRow, Overview, Resolution, RunnerRow } from '../../services/raceControl'

const props = defineProps<{ eventId: string }>()
const tab = ref('overview')
const kind = ref('evidence')
const auditKind = ref('classification')
const raceId = ref('')
const overview = ref<Overview>()
const loading = ref(false)
const error = ref('')
const evidence = ref<EvidenceRow[]>([])
const runners = ref<RunnerRow[]>([])
const total = ref(0)
const offset = ref(0)
const pageSize = 25
let request = 0

const exceptionKinds = [
  { title: 'Actionable timing evidence', value: 'evidence' }, { title: 'Bib ownership conflicts', value: 'bib' },
  { title: 'Calculation warnings', value: 'calculation' },
]
const auditKinds = [{ title: 'Official classifications', value: 'classification' }, { title: 'Observation decisions', value: 'evidence' }]
const showRunners = computed(() => tab.value === 'runners' || tab.value === 'exceptions' && kind.value === 'calculation')
const visibleRunners = computed(() => tab.value === 'exceptions' ? runners.value.filter(row => warnings(row).length) : runners.value)
const visibleCount = computed(() => showRunners.value ? visibleRunners.value.length : evidence.value.length)
const emptyMessage = computed(() => showRunners.value
  ? (tab.value === 'exceptions' ? 'No calculation warnings on this roster page.' : 'No frozen registrations on this page.')
  : (tab.value === 'audit' ? 'No decisions on this page.' : 'No exceptions on this page.'))

watch([tab, kind, auditKind, raceId], () => { offset.value = 0 })
watch([tab, kind, auditKind, raceId, offset], () => { if (overview.value) void loadPage() })
watch(() => props.eventId, () => void refresh(), { immediate: true })

function failure(cause: unknown, fallback: string) {
  if (isAxiosError(cause) && cause.response?.status === 403) return 'You do not have permission to access Race Control.'
  return getApiError(cause, fallback)
}

async function refresh() {
  const id = ++request
  loading.value = true
  error.value = ''
  try {
    const { data } = await raceControlApi.overview(props.eventId)
    if (id !== request) return
    overview.value = data
    if (!data.races.some(race => race.raceId === raceId.value)) raceId.value = data.races[0]?.raceId ?? ''
    await loadPage()
  } catch (cause) {
    if (id === request) error.value = failure(cause, 'Unable to load Race Control.')
  } finally {
    if (id === request) loading.value = false
  }
}

async function loadPage() {
  const id = ++request
  loading.value = true
  error.value = ''
  evidence.value = []
  runners.value = []
  total.value = 0
  try {
    if (showRunners.value && raceId.value) {
      const { data } = await raceControlApi.runners(props.eventId, raceId.value, offset.value, pageSize)
      if (id === request) { runners.value = data.items; total.value = data.total }
    } else if (tab.value === 'audit' || (tab.value === 'exceptions' && !showRunners.value)) {
      const { data } = tab.value === 'audit' ? await raceControlApi.audit(props.eventId, auditKind.value, offset.value, pageSize)
        : await raceControlApi.exceptions(props.eventId, kind.value, offset.value, pageSize)
      if (id === request) { evidence.value = data.items; total.value = data.total }
    }
  } catch (cause) {
    if (id === request) error.value = failure(cause, 'Unable to load this timing view.')
  } finally {
    if (id === request) loading.value = false
  }
}

function label(value: string) {
  if (['Dns', 'Dnf', 'Dq'].includes(value)) return value.toUpperCase()
  return value.replaceAll('_', ' ').replace(/([a-z])([A-Z])/g, '$1 $2')
}

function elapsed(value?: number | null) {
  return value == null ? 'Unavailable' : `${(value / 1_000_000).toFixed(6)} s`
}

function warnings(row: RunnerRow) {
  const messages: Record<string, string> = {
    missing_official_gun: 'No official gun',
    missing_individual_start: 'No accepted individual start',
    missing_mandatory_evidence: `${row.timing.readiness.missingMandatoryOccurrences.length} mandatory visits without evidence`,
    calculated_cutoff_exceeded: 'Calculated cutoff exceeded',
  }
  return row.warnings.map(code => messages[code] ?? label(code))
}

const dialog = ref(false)
const mode = ref<'evidence' | 'classification'>('evidence')
const resolution = ref<Resolution>()
const selectedRunner = ref<RunnerRow>()
const proposedAction = ref<string | null>(null)
const occurrence = computed(() => proposedAction.value === 'invalidate' ? null : proposedAction.value)
let detailRequest = 0
const classification = ref(0)
const reason = ref('')
const actionError = ref('')
const stale = ref(false)
const saving = ref(false)
const detailLoading = ref(false)
const saved = ref(false)
const classificationOptions = ['Pending', 'Finished', 'DNS', 'DNF', 'DQ'].map((title, value) => ({ title, value }))
const resolutionChoices = computed(() => [
  { title: 'Invalidate observation', value: 'invalidate' },
  ...(resolution.value?.choices ?? []).map(choice => ({
    title: `${choice.sequence} · ${choice.name} · ${label(choice.effect)}`, value: choice.occurrenceId,
  })),
])
const choice = computed(() => resolution.value?.choices.find(x => x.occurrenceId === occurrence.value))
const effect = computed(() => !proposedAction.value ? 'Choose how to resolve this observation.' : occurrence.value
  ? (choice.value?.effect === 'Accepted' ? 'Accept this observation for the selected occurrence.' :
      `The selected slot cannot accept this observation: ${label(choice.value?.effect ?? '')}.`)
  : (resolution.value?.acceptedOccurrenceId ? 'Remove this observation from its accepted slot and recalculate the result.' :
      'Mark this observation invalid while preserving the original evidence.'))
const canSave = computed(() => !!reason.value.trim() && !stale.value && !saving.value &&
  (mode.value === 'classification' ? !!selectedRunner.value : !!resolution.value && !!proposedAction.value &&
    (occurrence.value === null || (!resolution.value.acceptedOccurrenceId && choice.value?.effect === 'Accepted'))))

async function openResolution(id: string) {
  mode.value = 'evidence'
  selectedRunner.value = undefined
  resolution.value = undefined
  proposedAction.value = null
  reason.value = ''
  actionError.value = ''
  stale.value = false
  dialog.value = true
  const idForRequest = ++detailRequest
  detailLoading.value = true
  try {
    const { data } = await raceControlApi.resolution(props.eventId, id)
    if (idForRequest === detailRequest) resolution.value = data
  }
  catch (cause) { if (idForRequest === detailRequest) actionError.value = failure(cause, 'Unable to load the observation.') }
  finally { if (idForRequest === detailRequest) detailLoading.value = false }
}

function openClassification(row: RunnerRow) {
  detailRequest++
  detailLoading.value = false
  mode.value = 'classification'
  resolution.value = undefined
  selectedRunner.value = row
  classification.value = Math.max(0, classificationOptions.findIndex(option => option.title.toLowerCase() === row.classification.toLowerCase()))
  reason.value = ''
  actionError.value = ''
  stale.value = false
  dialog.value = true
}

async function reloadDecision() {
  if (resolution.value) return openResolution(resolution.value.id)
  dialog.value = false
  await refresh()
}

async function save() {
  if (!canSave.value) return
  saving.value = true
  actionError.value = ''
  try {
    if (mode.value === 'evidence' && resolution.value)
      await raceControlApi.resolve(props.eventId, resolution.value.id, occurrence.value, resolution.value.decisions.at(-1)!.revision, reason.value.trim())
    else if (selectedRunner.value)
      await raceControlApi.classify(props.eventId, raceId.value, selectedRunner.value.timing.registrationId, classification.value,
        selectedRunner.value.revision, reason.value.trim())
    dialog.value = false
    saved.value = true
    await refresh()
  } catch (cause) {
    stale.value = isAxiosError(cause) && ['stale_decision', 'stale_classification', 'slot_conflict'].includes(cause.response?.data?.code)
    actionError.value = failure(cause, 'Unable to record the decision.')
  } finally { saving.value = false }
}
</script>
