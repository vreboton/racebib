<template>
  <v-btn-toggle v-model="workspace" mandatory aria-label="Registration workspace" class="mt-3 flex-wrap h-auto">
    <v-btn value="manage">Manage registrations</v-btn>
    <v-btn value="check-in">Check-in</v-btn>
    <v-btn value="readiness">Race readiness</v-btn>
  </v-btn-toggle>
  <CheckInPanel v-if="workspace === 'check-in'" :event-id="eventId" />
  <RaceReadinessPanel v-if="workspace === 'readiness'" :event-id="eventId" />
  <v-card v-show="workspace === 'manage'" class="my-4" variant="outlined" :loading="busy">
    <v-card-title>Runner registrations</v-card-title>
    <v-card-text>
      <v-alert v-if="error" type="error" class="mb-4">{{ error }}</v-alert>
      <v-alert v-if="notice" type="success" class="mb-4">{{ notice }}</v-alert>
      <div class="d-flex flex-wrap ga-2 mb-4">
        <v-btn :disabled="busy" @click="openRegistration()">Register runner</v-btn>
        <v-btn :disabled="busy" @click="openImport">Import CSV</v-btn>
        <v-btn :disabled="busy" @click="showBulkBibs = true">Bulk assign bibs</v-btn>
        <v-btn variant="text" :to="{ name: 'public-participants', params: { id: eventId } }">Public participants</v-btn>
      </div>
      <v-form class="d-flex flex-wrap ga-3" @submit.prevent="page = 1; refresh()">
        <v-text-field v-model="search" label="Search name or bib" />
        <v-select v-model="raceFilter" :items="races" item-title="name" item-value="id" label="Race" clearable />
        <v-select v-model="statusFilter" :items="statuses" label="Status" clearable />
        <v-btn type="submit" :disabled="busy">Search</v-btn>
      </v-form>
      <v-table>
        <thead><tr><th>Name on Bib</th><th>Registered name</th><th>Bib</th><th>Status</th><th>Actions</th></tr></thead>
        <tbody>
          <tr v-for="row in rows" :key="row.id">
            <td>{{ row.effectiveBibDisplayName }}</td><td>{{ row.runner.firstName }} {{ row.runner.lastName }}</td>
            <td>{{ row.bibNumber || 'Unassigned' }}</td><td>{{ statuses[row.status]?.title }}</td>
            <td><v-btn variant="text" :disabled="busy" @click="openRegistration(row)">Details</v-btn></td>
          </tr>
          <tr v-if="!rows.length && !busy"><td colspan="5">No matching registrations.</td></tr>
        </tbody>
      </v-table>
      <v-pagination v-if="total > 50" v-model="page" :length="Math.ceil(total / 50)" @update:model-value="refresh" />
    </v-card-text>
  </v-card>

  <BulkBibDialog v-if="showBulkBibs" :event-id="eventId" @close="showBulkBibs = false" @assigned="refresh" />
  <v-dialog v-model="showRegistration" max-width="760">
    <v-card :title="editing ? 'Registration details' : 'Register runner'">
      <v-card-text>
        <v-alert v-if="error" type="error" class="mb-3">
          {{ error }}
        </v-alert>
        <v-alert v-if="registrationConflict && editing" type="warning" class="mb-3">
          Refresh to load the latest registration. Refreshing replaces your unsaved edits.
          <v-btn class="mt-2" :disabled="busy" @click="refreshRegistration">Refresh registration</v-btn>
        </v-alert>
        <v-form id="registration-form" @submit.prevent="saveRegistration">
          <div v-if="!editing" class="d-flex ga-2">
            <v-text-field v-model="runnerSearch" label="Find runner profile" @keydown.enter.prevent="loadRunners" />
            <v-btn :disabled="busy" @click="loadRunners">Find</v-btn>
          </div>
          <v-autocomplete v-if="!editing" v-model="draft.runnerId" :items="runners" :item-title="runnerLabel"
            item-value="id" label="Runner profile" clearable />
          <v-btn class="mb-4" variant="outlined" @click="openProfile">{{ editing ? 'Edit runner profile' : 'Create runner profile' }}</v-btn>
          <v-select v-model="draft.raceId" :items="races" item-title="name" item-value="id" label="Race"
            @update:model-value="draft.categoryId = null; draft.waveId = null" />
          <v-select v-if="activeCategories.length > 1" v-model="draft.categoryId" :items="registrationCategories"
            item-title="name" item-value="id" label="Registration category" />
          <v-select v-if="selectedRace?.waves.length" v-model="draft.waveId" :items="selectedRace.waves" item-title="name" item-value="id" label="Start Wave" clearable />
          <v-text-field v-model="draft.bibDisplayName" label="Name on Bib" :hint="bibNameHint" persistent-hint />
          <v-text-field v-model="draft.team" label="Team / Club" />
          <v-select v-model="draft.status" :items="editableStatuses" label="Registration status" />
          <v-select v-model="draft.paymentStatus" :items="payments" label="Payment status" />
          <v-select v-model="manualAssignment.mode" :items="modes" label="Bib assignment" />
          <v-text-field v-if="manualAssignment.mode !== 1" v-model="draft.bibNumber" label="Bib number" maxlength="32" />
          <div v-if="manualAssignment.mode !== 0" class="d-flex flex-wrap ga-2">
            <v-text-field v-model.number="manualAssignment.start" label="Range start" type="number" />
            <v-text-field v-model.number="manualAssignment.end" label="Range end" type="number" />
            <v-text-field v-model.number="manualAssignment.width" label="Minimum digits" type="number" />
          </div>
        </v-form>
        <div v-if="editing">
          <h3>Audit history</h3>
          <p v-if="!history.length">No changes recorded.</p>
          <p v-for="(change, index) in history" :key="index">{{ change.action }} · {{ new Date(change.timestampUtc).toLocaleString() }}</p>
        </div>
        <v-checkbox v-if="editing && [0, 1, 2].includes(editing.status)" v-model="confirmCancel" label="Confirm withdrawal and keep the bib reserved" />
      </v-card-text>
      <v-card-actions>
        <v-btn v-if="confirmCancel" color="error" :disabled="busy || registrationConflict" @click="cancelRegistration">Withdraw registration</v-btn>
        <v-spacer />
        <v-btn v-if="editing?.bibNumber" @click="printBib">Print bib</v-btn>
        <v-btn @click="showRegistration = false">Close</v-btn>
        <v-btn type="submit" form="registration-form" color="primary" :disabled="busy || registrationConflict" :loading="busy">{{ saveLabel }}</v-btn>
      </v-card-actions>
    </v-card>
  </v-dialog>

  <v-dialog v-model="showProfile" max-width="620">
    <v-card :title="profile.id ? 'Edit runner profile' : 'Create runner profile'">
      <v-card-text>
        <v-alert v-if="error" type="error" class="mb-3">{{ error }}</v-alert>
        <v-form id="runner-profile-form" @submit.prevent="saveProfile">
          <v-text-field v-model="profile.firstName" label="First name" required maxlength="100" />
          <v-text-field v-model="profile.lastName" label="Last name" required maxlength="100" />
          <v-text-field v-model="profile.email" label="Email" type="email" />
          <v-text-field v-model="profile.birthdate" label="Birthdate" type="date" />
          <v-text-field v-model="profile.gender" label="Gender" />
          <v-text-field v-model="profile.mobileNumber" label="Mobile number" type="tel" />
          <v-text-field v-model="profile.emergencyContact" label="Emergency contact" />
        </v-form>
      </v-card-text>
      <v-card-actions>
        <v-spacer />
        <v-btn @click="showProfile = false">Close</v-btn>
        <v-btn type="submit" form="runner-profile-form" :loading="busy" color="primary">Save profile</v-btn>
      </v-card-actions>
    </v-card>
  </v-dialog>

  <v-dialog v-model="showImport" max-width="800">
    <v-card title="Import runner registrations">
      <v-card-text>
        <v-alert v-if="error" type="error" class="mb-3">{{ error }}</v-alert>
        <p class="mb-3">
          Upload a CSV containing First Name, Last Name, and Email. Start Wave is optional. Use the Category column when a legacy race has multiple active categories.
          <a :href="templateUrl" download="racebib-registration-template.csv">Download CSV Template</a>
        </p>
        <v-alert v-if="!eligibleRaces.length" type="warning" class="mb-3">Add an active Race to this event before importing.</v-alert>
        <v-file-input label="Registration CSV" accept=".csv,text/csv" :model-value="csvFile" :disabled="busy"
          @update:model-value="selectFile" />
        <v-select v-model="importAssignment.mode" :items="modes" label="Bib assignment" :disabled="busy" @update:model-value="invalidate" />
        <div v-if="importAssignment.mode !== 0" class="d-flex flex-wrap ga-2">
          <v-text-field v-model.number="importAssignment.start" label="Range start" type="number" :disabled="busy" @update:model-value="invalidate" />
          <v-text-field v-model.number="importAssignment.end" label="Range end" type="number" :disabled="busy" @update:model-value="invalidate" />
          <v-text-field v-model.number="importAssignment.width" label="Minimum digits" type="number" :disabled="busy" @update:model-value="invalidate" />
        </div>
        <v-select v-model="defaultRaceId" :items="eligibleRaces" item-title="name" item-value="id" label="Default Race"
          :disabled="busy || !eligibleRaces.length" @update:model-value="invalidate" />
        <p class="mb-2">Blank or unrecognized values in the Race column use the default Race.</p>
        <template v-if="preview">
          <v-alert v-if="preview.error" type="warning" class="mb-2">{{ preview.error }}</v-alert>
          <p v-if="preview.rows.length">{{ preview.validCount }} valid of {{ preview.rows.length }} rows in the last preview.</p>
          <p v-if="preview.defaultRaceCount">{{ preview.defaultRaceCount }} rows use the default Race.</p>
          <v-list class="preview-rows" density="compact">
            <v-list-item v-for="row in preview.rows" :key="row.row" :title="'Row ' + row.row"
              :subtitle="row.error || previewRowSummary(row)">
              <div v-if="row.warning" class="text-warning text-body-2">{{ row.warning }}</div>
            </v-list-item>
          </v-list>
          <v-btn v-if="preview.rows.some(row => row.error)" variant="text" @click="downloadErrors">Download error report</v-btn>
        </template>
      </v-card-text>
      <v-card-actions>
        <v-btn @click="showImport = false">Close</v-btn>
        <v-spacer />
        <v-btn :disabled="busy || !csvFile || !defaultRaceId || !eligibleRaces.length" :loading="busy" @click="previewImport">Validate &amp; Preview</v-btn>
        <v-btn color="primary" :disabled="busy || !token || !defaultRaceId || !eligibleRaces.length" :loading="busy" @click="confirmImport">Confirm Import</v-btn>
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>

<script setup lang="ts">
import BulkBibDialog from './BulkBibDialog.vue'
import CheckInPanel from './CheckInPanel.vue'
import RaceReadinessPanel from './RaceReadinessPanel.vue'
import { computed, onMounted, ref } from 'vue'
import { api } from '../../services/api'
import { getApiError } from '../../services/events'
import { loadConfiguration, type Race } from '../../services/raceConfiguration'

interface Profile {
  id?: string
  firstName: string
  lastName: string
  email?: string | null
  birthdate?: string | null
  gender?: string | null
  mobileNumber?: string | null
  emergencyContact?: string | null
  publicDisplayName?: string | null
}
interface Draft {
  raceId: string | null
  runnerId: string | null
  categoryId: string | null
  waveId: string | null
  bibDisplayName: string | null
  bibNumber: string | null
  team: string | null
  status: number
  paymentStatus: number
}
interface Row extends Draft { id: string; version: number; effectiveBibDisplayName: string; runner: Profile }
interface Change { action: string; timestampUtc: string }
interface Preview {
  rows: { row: number; error?: string; nameOnBib?: string; bibNumber?: string | null; raceName?: string;
    defaultRaceReason?: 'blank' | 'unrecognized'; csvRace?: string; warning?: string | null }[]
  defaultRaceCount: number
  validCount: number
  token: string | null
  error: string | null
  errorCsv: string
}
const props = defineProps<{ eventId: string; organizerId: string }>()
const showBulkBibs = ref(false)
const workspace = ref('manage')
const registrationUrl = '/events/' + props.eventId + '/registrations'
const runnerUrl = '/organizers/' + props.organizerId + '/runners'
const choices = (items: string[]) => items.map((title, value) => ({ title, value }))
const statuses = choices(['Pending', 'Confirmed', 'Waitlisted', 'Withdrawn', 'DNF', 'DNS', 'Disqualified'])
const payments = choices(['Unspecified', 'Unpaid', 'Paid', 'Waived', 'Refunded'])
const modes = choices(['Existing bibs', 'Automatic bibs', 'Mixed assignment'])
const defaultDraft = (): Draft => ({ raceId: null, runnerId: null, categoryId: null, waveId: null,
  bibDisplayName: null, bibNumber: null, team: null, status: 0, paymentStatus: 0 })
const assignment = () => ({ mode: 0, start: 1, end: 1000, width: 0 })
const busy = ref(false)
const error = ref('')
const registrationConflict = ref(false)
const notice = ref('')
const search = ref('')
const raceFilter = ref<string | null>(null)
const statusFilter = ref<number | null>(null)
const total = ref(0)
const page = ref(1)
const races = ref<Race[]>([])
const rows = ref<Row[]>([])
const runners = ref<Profile[]>([])
const runnerSearch = ref('')
const editing = ref<Row | null>(null)
const editableStatuses = computed(() => {
  const current = editing.value?.status
  const allowed = current === undefined ? [0, 1, 2] : ({
    0: [0, 1, 3], 1: [0, 1, 3], 2: [1, 2, 3], 3: [0, 3],
  } as Record<number, number[]>)[current] ?? [current]
  return statuses.filter(status => allowed.includes(status.value))
})
const saveLabel = computed(() => {
  if (editing.value?.status === 3 && draft.value.status === 0) return 'Reopen registration'
  if (editing.value?.status === 1 && draft.value.status === 0) return 'Correct status to Pending'
  if (editing.value && editing.value.status !== 3 && draft.value.status === 3) return 'Withdraw registration'
  return 'Save registration'
})
const draft = ref(defaultDraft())
const history = ref<Change[]>([])
const manualAssignment = ref(assignment())
const showRegistration = ref(false)
const confirmCancel = ref(false)
const showProfile = ref(false)
const profile = ref<Profile>({ firstName: '', lastName: '' })
const showImport = ref(false)
const csvFile = ref<File | null>(null)
const preview = ref<Preview | null>(null)
const token = ref<string | null>(null)
const importAssignment = ref(assignment())
const defaultRaceId = ref<string | null>(null)
const eligibleRaces = computed(() => races.value.filter(race => race.status === 'Active'))
const templateHeader = 'First Name,Last Name,Name on Bib,Gender,Birthdate,Email,Mobile Number,Race,Category,Start Wave,Bib Number,Team,Emergency Contact'
const templateUrl = 'data:text/csv;charset=utf-8,' + encodeURIComponent('\uFEFF' + templateHeader + '\r\n')
const selectedRace = computed(() => races.value.find(race => race.id === draft.value.raceId))
const activeCategories = computed(() => selectedRace.value?.categories.filter(category => category.status === 'Active') ?? [])
const registrationCategories = computed(() => selectedRace.value?.categories.filter(category =>
  category.status === 'Active' || category.id === draft.value.categoryId) ?? [])
const bibNameHint = computed(() => {
  if (draft.value.bibDisplayName?.trim()) return 'Printed bibs and public participant outputs use this name.'
  const runner = editing.value?.runner ?? runners.value.find(item => item.id === draft.value.runnerId)
  return runner
    ? 'Blank publishes the registered full name: ' + runner.firstName + ' ' + runner.lastName + '.'
    : 'Blank publishes the runner’s registered full name.'
})
const runnerLabel = (value: Profile) => value.firstName + ' ' + value.lastName + (value.email ? ' · ' + value.email : '')

async function run(action: () => Promise<void>) {
  if (busy.value) return
  busy.value = true
  error.value = ''
  try { await action() }
  catch (failure) {
    const body = (failure as { response?: { data?: unknown } })?.response?.data
    if ((body as { code?: string } | null)?.code === 'registration_version_conflict')
      registrationConflict.value = true
    error.value = typeof body === 'string' ? body : getApiError(failure, 'Registration request failed.')
  } finally { busy.value = false }
}
async function loadRows() {
  const { data } = await api.get<{ items: Row[]; total: number }>(registrationUrl, {
    params: { search: search.value, raceId: raceFilter.value, status: statusFilter.value, page: page.value },
  })
  rows.value = data.items
  total.value = data.total
}
const refresh = () => run(loadRows)
async function fetchRunners() {
  const { data } = await api.get<{ items: Profile[] }>(runnerUrl, { params: { search: runnerSearch.value } })
  runners.value = data.items
}
const loadRunners = () => run(fetchRunners)
function openRegistration(row?: Row) {
  return run(async () => {
    const configuration = await loadConfiguration(props.eventId)
    const details = row
      ? (await api.get<{ registration: Row; history: Change[] }>(registrationUrl + '/' + row.id)).data
      : null
    if (!details) await fetchRunners()
    races.value = configuration.races
    editing.value = details?.registration ?? null
    manualAssignment.value = assignment()
    confirmCancel.value = false
    history.value = details?.history ?? []
    draft.value = details ? { ...details.registration } : defaultDraft()
    registrationConflict.value = false
    showRegistration.value = true
  })
}
async function refreshRegistration() {
  if (!editing.value) return
  await openRegistration(editing.value)
  if (!registrationConflict.value) await refresh()
}
function openProfile() {
  const row = editing.value
  profile.value = row ? { ...row.runner, id: row.runnerId ?? undefined } : { firstName: '', lastName: '' }
  showProfile.value = true
}
function saveProfile() {
  return run(async () => {
    const { id, ...fields } = profile.value
    const body = { ...fields, birthdate: fields.birthdate || null }
    const { data } = id ? await api.put<Profile>(runnerUrl + '/' + id, body) : await api.post<Profile>(runnerUrl, body)
    runners.value = [...runners.value.filter(runner => runner.id !== data.id), data]
    draft.value.runnerId = data.id ?? null
    if (editing.value) editing.value.runner = data
    showProfile.value = false
    await loadRows()
  })
}
function saveRegistration() {
  return run(async () => {
    if (registrationConflict.value) return
    const body = { ...draft.value, version: editing.value?.version, bibNumber: manualAssignment.value.mode === 1 ? null : draft.value.bibNumber,
      assignment: manualAssignment.value }
    if (editing.value) await api.put(registrationUrl + '/' + editing.value.id, body)
    else await api.post(registrationUrl, body)
    showRegistration.value = false
    notice.value = 'Registration saved.'
    await loadRows()
  })
}
function cancelRegistration() {
  return run(async () => {
    if (!editing.value || !confirmCancel.value || registrationConflict.value) return
    await api.post(registrationUrl + '/' + editing.value.id + '/cancel', { version: editing.value.version })
    showRegistration.value = false
    notice.value = 'Registration withdrawn.'
    await loadRows()
  })
}
function printBib() {
  const registration = editing.value
  if (!registration?.bibNumber) return
  const printWindow = window.open('', '_blank', 'popup,width=700,height=480')
  if (!printWindow) { error.value = 'Allow pop-ups to print the bib.'; return }
  printWindow.opener = null
  const document = printWindow.document
  document.title = 'Race bib'
  const bib = document.createElement('main')
  bib.style.cssText = 'font: 2rem system-ui; text-align: center; border: 3px solid #000; padding: 3rem;'
  const number = document.createElement('strong')
  number.style.fontSize = '5rem'
  number.textContent = registration.bibNumber
  const name = document.createElement('div')
  name.textContent = registration.effectiveBibDisplayName
  const race = document.createElement('div')
  race.textContent = races.value.find(item => item.id === registration.raceId)?.name ?? ''
  bib.append(number, name, race)
  document.body.append(bib)
  printWindow.print()
}
function openImport() {
  showImport.value = true
  return run(async () => {
    races.value = (await loadConfiguration(props.eventId)).races
    if (!eligibleRaces.value.some(race => race.id === defaultRaceId.value)) defaultRaceId.value = null
    invalidate()
  })
}
function invalidate() {
  token.value = null
  preview.value = null
}
function selectFile(file: File | File[] | null) {
  csvFile.value = Array.isArray(file) ? file[0] ?? null : file
  invalidate()
}
function importForm() {
  const form = new FormData()
  if (csvFile.value) form.append('file', csvFile.value)
  form.append('defaultRaceId', defaultRaceId.value ?? '')
  form.append('assignment', JSON.stringify(importAssignment.value))
  return form
}
function previewImport() {
  if (!csvFile.value || !eligibleRaces.value.some(race => race.id === defaultRaceId.value)) return
  return run(async () => {
    invalidate()
    const { data } = await api.post<Preview>(registrationUrl + '/import/preview', importForm(), { headers: { 'Content-Type': 'multipart/form-data' } })
    preview.value = data
    token.value = data.token
  })
}
function confirmImport() {
  return run(async () => {
    if (!token.value || !eligibleRaces.value.some(race => race.id === defaultRaceId.value)) return
    const form = importForm()
    form.append('token', token.value)
    invalidate()
    const { data } = await api.post<{ importedCount: number; defaultRaceCount: number }>(registrationUrl + '/import/confirm', form, { headers: { 'Content-Type': 'multipart/form-data' } })
    notice.value = 'Imported ' + data.importedCount + ' registrations.'
    if (data.defaultRaceCount) notice.value += ' Assigned ' + data.defaultRaceCount + ' to the default Race.'
    showImport.value = false
    selectFile(null)
    await loadRows()
  })
}
function previewRowSummary(row: Preview['rows'][number]) {
  const race = row.defaultRaceReason === 'blank'
    ? row.raceName + ' (default, Race column blank)'
    : row.defaultRaceReason === 'unrecognized'
      ? row.raceName + ' (default, Race value “' + row.csvRace + '” not found)'
      : row.raceName
  return [row.nameOnBib, row.bibNumber ?? 'Unassigned', race].filter(Boolean).join(' · ')
}
function downloadErrors() {
  if (!preview.value) return
  const href = URL.createObjectURL(new Blob([preview.value.errorCsv], { type: 'text/csv;charset=utf-8' }))
  const link = document.createElement('a')
  link.href = href
  link.download = 'registration-errors.csv'
  link.click()
  setTimeout(() => URL.revokeObjectURL(href), 1000)
}
onMounted(() => run(async () => {
  races.value = (await loadConfiguration(props.eventId)).races
  await loadRows()
}))
</script>

<style scoped>
.preview-rows { max-height: 16rem; overflow: auto; }
</style>
