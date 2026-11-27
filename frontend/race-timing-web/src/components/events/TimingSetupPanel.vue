<template>
  <section class="mt-4">
    <h3 class="text-h6 mb-3">Timing setup</h3>
    <v-alert v-if="error" type="error" class="mb-3">{{ error }}</v-alert>
    <v-progress-linear v-if="busy" indeterminate aria-label="Loading timing setup" />
    <v-btn variant="text" :disabled="busy" @click="load">Reload setup</v-btn>
    <template v-if="data">
      <v-card variant="outlined" class="my-3" title="Physical checkpoints">
        <v-card-text>
          <p v-if="!data.locations.length">Create a location for each timing station.</p>
          <p v-for="location in data.locations" :key="location.id">{{ location.code }} · {{ location.name }}</p>
          <v-form class="mt-3" @submit.prevent="createLocation">
            <v-text-field v-model="locationName" label="Checkpoint name" maxlength="160" />
            <v-text-field v-model="locationCode" label="Checkpoint code" maxlength="40" />
            <v-btn type="submit" color="primary" :disabled="busy || !locationName.trim() || !locationCode.trim()">Add checkpoint</v-btn>
          </v-form>
        </v-card-text>
      </v-card>
      <v-card variant="outlined" class="my-3" title="Map course visits">
        <v-card-text>
          <p v-if="!data.courses.length">Configure a race and course first.</p>
          <details v-for="course in data.courses" :key="course.id" class="mb-3">
            <summary>{{ course.name }} · revision {{ course.version }}</summary>
            <v-select v-for="visit in course.occurrences" :key="visit.id" class="mt-2"
              :model-value="data.mappings.find(mapping => mapping.occurrenceId === visit.id)?.checkpointId ?? null"
              :items="locations" :label="`${visit.sequence} · ${visit.name}`"
              :disabled="busy || data.setups.some(setup => setup.courseId === course.id)"
              @update:model-value="id => mapVisit(course.id, visit.id, id)" />
          </details>
        </v-card-text>
      </v-card>
      <v-card variant="outlined" class="my-3" title="Checkpoint operators">
        <v-card-text>
          <p class="mb-3">New operators can accept a checkpoint account invitation from Organizer Members. Assign their account to a station here.</p>
          <v-select v-model="checkpoint" :items="locations" label="Checkpoint" :disabled="busy" />
          <v-form @submit.prevent="grant">
            <v-text-field v-model="email" label="Operator account email" type="email" maxlength="254" />
            <v-btn type="submit" color="primary" :disabled="busy || !checkpoint || !email.trim()">Grant checkpoint access</v-btn>
          </v-form>
          <div v-for="assignment in data.assignments" :key="`${assignment.checkpointId}-${assignment.userId}`"
            class="d-flex align-center justify-space-between mt-3 ga-2">
            <span>{{ assignment.displayName || assignment.email }} · {{ locations.find(x => x.value === assignment.checkpointId)?.title }}</span>
            <v-btn variant="outlined" :disabled="busy" @click="revoke(assignment.checkpointId, assignment.userId)">Revoke</v-btn>
          </div>
        </v-card-text>
      </v-card>
      <v-card variant="outlined" class="my-3" title="Race timing">
        <v-card-text>
          <v-select v-model="raceId" :items="data.races" item-title="name" item-value="id" label="Race" :disabled="busy" />
          <template v-if="raceId && !armed">
            <v-select v-model="resultsBasis" :items="bases" label="Results basis" />
            <v-select v-model="cutoffBasis" :items="bases" label="Cutoff basis" />
            <p class="mb-3">Arming freezes the mapped visits, timing settings, and confirmed bib roster. The event must be Ready or Live.</p>
            <v-checkbox v-model="armConfirmed" label="Freeze this race's timing setup" />
            <v-btn color="primary" :disabled="busy || !armConfirmed" @click="arm">Arm race</v-btn>
          </template>
          <template v-if="armed">
            <p class="mb-3">Timing is armed.</p>
            <v-select v-model="waveId" :items="waveOptions" label="Gun declaration scope" />
            <p v-if="currentGun" class="mb-3">Current declaration · {{ formatEventTime(currentGun.gunAtUtc, zone) }} ({{ zone }})</p>
            <v-form @submit.prevent="declareGun">
              <v-text-field v-model="gunDate" label="Actual gun time (ISO 8601, with offset)" placeholder="2026-11-24T06:00:00+08:00" />
              <v-text-field v-model="gunReason" label="Gun declaration reason" maxlength="300" />
              <v-btn type="submit" color="primary" :disabled="busy || !gunDate || !gunReason.trim()">{{ currentGun ? 'Replace gun declaration' : 'Declare gun' }}</v-btn>
            </v-form>
            <v-divider class="my-4" />
            <v-form @submit.prevent="findLateRunner">
              <v-text-field v-model="lateBib" label="Late entrant bib" maxlength="32" @update:model-value="lateRunner = undefined" />
              <v-btn type="submit" variant="outlined" :disabled="busy || !lateBib.trim()">Find eligible entrant</v-btn>
            </v-form>
            <v-form v-if="lateRunner" class="mt-3" @submit.prevent="admit">
              <p>{{ lateRunner.bibNumber }} · {{ lateRunner.firstName }} {{ lateRunner.lastName }}</p>
              <v-text-field v-model="lateReason" label="Roster amendment reason" maxlength="300" />
              <v-btn type="submit" color="primary" :disabled="busy || !lateReason.trim()">Admit to timing roster</v-btn>
            </v-form>
          </template>
        </v-card-text>
      </v-card>
    </template>
  </section>
</template>

<script setup lang="ts">
import { formatEventTime } from '../../services/eventTime'
import { computed, ref, watch } from 'vue'
import { api } from '../../services/api'
import { getApiError } from '../../services/events'

interface Setup {
  locations: { id: string; code: string; name: string }[]
  courses: { id: string; name: string; version: number; occurrences: { id: string; name: string; sequence: number }[] }[]
  mappings: { occurrenceId: string; checkpointId: string }[]
  assignments: { checkpointId: string; userId: string; displayName: string; email: string }[]
  races: { id: string; name: string; waves: { id: string; name: string }[] }[]
  setups: { raceId: string; courseId: string }[]
  guns: { id: string; raceId: string; waveId: string | null; gunAtUtc: string }[]
}
interface LateRunner { id: string; bibNumber: string; firstName: string; lastName: string }

const props = withDefaults(defineProps<{ eventId: string; zone?: string }>(), { zone: 'UTC' })
const base = computed(() => `/events/${props.eventId}/timing`)
const data = ref<Setup>()
const busy = ref(false)
const error = ref('')
const locationName = ref('')
const locationCode = ref('')
const checkpoint = ref<string>()
const email = ref('')
const raceId = ref<string>()
const resultsBasis = ref(0)
const cutoffBasis = ref(0)
const armConfirmed = ref(false)
const waveId = ref('')
const gunDate = ref('')
const gunReason = ref('')
const lateBib = ref('')
const lateReason = ref('')
const lateRunner = ref<LateRunner>()
const bases = [{ title: 'Gun', value: 0 }, { title: 'Net', value: 1 }]
const locations = computed(() => data.value?.locations.map(x => ({ title: `${x.code} · ${x.name}`, value: x.id })) ?? [])
const armed = computed(() => data.value?.setups.some(x => x.raceId === raceId.value))
const waveOptions = computed(() => [{ title: 'Race (no wave)', value: '' },
  ...(data.value?.races.find(x => x.id === raceId.value)?.waves ?? []).map(x => ({ title: x.name, value: x.id }))])
const currentGun = computed(() => data.value?.guns.find(x => x.raceId === raceId.value && x.waveId === (waveId.value || null)))

watch(raceId, () => { armConfirmed.value = false; waveId.value = ''; lateRunner.value = undefined })
watch([raceId, waveId], () => { gunDate.value = ''; gunReason.value = '' })
watch(() => props.eventId, load, { immediate: true })

async function refreshData() {
  data.value = (await api.get<Setup>(base.value + '/setup')).data
}
async function action(run: () => Promise<unknown>, reload = true) {
  if (busy.value) return
  busy.value = true
  error.value = ''
  try { await run(); if (reload) await refreshData() }
  catch (cause) { error.value = getApiError(cause, 'Unable to update timing setup.') }
  finally { busy.value = false }
}
function load() { return action(refreshData, false) }
function createLocation() {
  return action(async () => {
    await api.post(base.value + '/checkpoints', { name: locationName.value.trim(), code: locationCode.value.trim() })
    locationName.value = ''; locationCode.value = ''
  })
}
function mapVisit(course: string, occurrence: string, checkpointId: string) {
  if (checkpointId) return action(() => api.put(base.value + `/courses/${course}/occurrences/${occurrence}/checkpoint`, { checkpointId }))
}
function grant() {
  return action(async () => {
    const response = await api.get<{ id: string }>(base.value + '/setup/operator', { params: { email: email.value.trim() } })
    await api.put(base.value + `/checkpoints/${checkpoint.value}/assignments/${encodeURIComponent(response.data.id)}`, { active: true })
    email.value = ''
  })
}
function revoke(id: string, userId: string) {
  return action(() => api.put(base.value + `/checkpoints/${id}/assignments/${encodeURIComponent(userId)}`, { active: false }))
}
function arm() {
  return action(() => api.post(base.value + `/races/${raceId.value}/arm`, { resultsBasis: resultsBasis.value, cutoffBasis: cutoffBasis.value }))
}
function declareGun() {
  const raw = gunDate.value.trim()
  if (!/(Z|[+-]\d{2}:\d{2})$/i.test(raw) || !Number.isFinite(Date.parse(raw))) {
    error.value = 'Enter an ISO 8601 date and time with Z or a UTC offset.'
    return
  }
  return action(async () => {
    await api.post(base.value + `/races/${raceId.value}/gun-starts`, {
      waveId: waveId.value || null, gunAtUtc: raw, expectedDeclarationId: currentGun.value?.id ?? null, reason: gunReason.value.trim(),
    })
    gunDate.value = ''; gunReason.value = ''
  })
}
function findLateRunner() {
  lateRunner.value = undefined
  return action(async () => {
    lateRunner.value = (await api.get<LateRunner>(base.value + `/setup/races/${raceId.value}/late-registrations`, {
      params: { bib: lateBib.value.trim() },
    })).data
  }, false)
}
function admit() {
  return action(async () => {
    if (!lateRunner.value) return
    await api.post(base.value + `/races/${raceId.value}/roster/${lateRunner.value.id}`, { reason: lateReason.value.trim() })
    lateRunner.value = undefined; lateBib.value = ''; lateReason.value = ''
  })
}
</script>
