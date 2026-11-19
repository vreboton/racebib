<template>
  <v-card class="my-4" variant="outlined" :loading="busy">
    <v-card-title>Runner registrations</v-card-title>
    <v-card-text>
      <v-alert v-if="error" type="error" class="mb-4">{{ error }}</v-alert>
      <v-alert v-if="notice" type="success" class="mb-4">{{ notice }}</v-alert>
      <div class="d-flex flex-wrap ga-2 mb-4">
        <v-btn :disabled="busy" @click="openRegistration()">Register runner</v-btn>
        <v-btn :disabled="busy" @click="showImport = true">Import CSV</v-btn>
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

  <v-dialog v-model="showRegistration" max-width="760">
    <v-card :title="editing ? 'Registration details' : 'Register runner'">
      <v-card-text>
        <v-alert v-if="error" type="error" class="mb-3">{{ error }}</v-alert>
        <v-form id="registration-form" @submit.prevent="saveRegistration">
          <div v-if="!editing" class="d-flex ga-2">
            <v-text-field v-model="runnerSearch" label="Find runner profile" @keydown.enter.prevent="loadRunners" />
            <v-btn :disabled="busy" @click="loadRunners">Find</v-btn>
          </div>
          <v-autocomplete v-if="!editing" v-model="draft.runnerId" :items="runners" :item-title="runnerLabel"
            item-value="id" label="Runner profile" clearable />
          <p v-if="draft.runnerId" class="text-caption mb-2">Runner ID for CSV matching <code>{{ draft.runnerId }}</code></p>
          <v-btn class="mb-4" variant="outlined" @click="openProfile">{{ editing ? 'Edit runner profile' : 'Create runner profile' }}</v-btn>
          <v-select v-model="draft.raceId" :items="races" item-title="name" item-value="id" label="Race"
            @update:model-value="draft.categoryId = null; draft.waveId = null" />
          <v-select v-model="draft.categoryId" :items="selectedRace?.categories ?? []" item-title="name" item-value="id" label="Category" clearable />
          <v-select v-model="draft.waveId" :items="selectedRace?.waves ?? []" item-title="name" item-value="id" label="Start wave" clearable />
          <v-text-field v-model="draft.bibDisplayName" label="Name on Bib" :hint="bibNameHint" persistent-hint />
          <v-text-field v-model="draft.team" label="Team / Club" />
          <v-select v-model="draft.status" :items="statuses" label="Registration status" />
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
        <v-checkbox v-if="editing && draft.status !== 3" v-model="confirmCancel" label="Confirm cancellation and keep the bib reserved" />
      </v-card-text>
      <v-card-actions>
        <v-btn v-if="confirmCancel" color="error" :disabled="busy" @click="cancelRegistration">Cancel registration</v-btn>
        <v-spacer />
        <v-btn v-if="editing?.bibNumber" @click="printBib">Print bib</v-btn>
        <v-btn @click="showRegistration = false">Close</v-btn>
        <v-btn type="submit" form="registration-form" color="primary" :loading="busy">Save registration</v-btn>
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
        <p class="mb-2">For a name shared by multiple runners, verify the profile and map a Runner ID column.</p>
        <v-file-input label="Registration CSV" accept=".csv,text/csv" :model-value="csvFile"
          @update:model-value="selectFile" />
        <v-select v-model="importAssignment.mode" :items="modes" label="Bib assignment" @update:model-value="invalidate" />
        <div v-if="importAssignment.mode !== 0" class="d-flex flex-wrap ga-2">
          <v-text-field v-model.number="importAssignment.start" label="Range start" type="number" @update:model-value="invalidate" />
          <v-text-field v-model.number="importAssignment.end" label="Range end" type="number" @update:model-value="invalidate" />
          <v-text-field v-model.number="importAssignment.width" label="Minimum digits" type="number" @update:model-value="invalidate" />
        </div>
        <template v-if="preview">
          <div class="mapping">
            <v-select v-for="field in preview.fields" :key="field" :model-value="mapping[field] ?? null"
              :items="headers" :label="field" clearable @update:model-value="mapField(field, $event)" />
          </div>
          <v-alert v-if="preview.error" type="warning" class="mb-2">{{ preview.error }}</v-alert>
          <p>{{ preview.validCount }} valid of {{ preview.rows.length }} rows in the last preview.</p>
          <v-list class="preview-rows" density="compact">
            <v-list-item v-for="row in preview.rows" :key="row.row" :title="'Row ' + row.row"
              :subtitle="row.error || [row.nameOnBib, row.bibNumber ?? 'Unassigned'].filter(Boolean).join(' · ')" />
          </v-list>
          <v-btn v-if="preview.rows.some(row => row.error)" variant="text" @click="downloadErrors">Download error report</v-btn>
        </template>
      </v-card-text>
      <v-card-actions>
        <v-btn @click="showImport = false">Close</v-btn>
        <v-spacer />
        <v-btn :disabled="!csvFile" :loading="busy" @click="previewImport">Validate CSV</v-btn>
        <v-btn color="primary" :disabled="!token" :loading="busy" @click="confirmImport">Confirm import</v-btn>
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>

<script setup lang="ts">
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
interface Row extends Draft { id: string; effectiveBibDisplayName: string; runner: Profile }
interface Change { action: string; timestampUtc: string }
interface Preview {
  headers: string[]
  fields: string[]
  mapping: Record<string, number>
  rows: { row: number; error?: string; nameOnBib?: string; bibNumber?: string | null }[]
  validCount: number
  token: string | null
  error: string | null
  errorCsv: string
}
const props = defineProps<{ eventId: string; organizerId: string }>()
const registrationUrl = '/events/' + props.eventId + '/registrations'
const runnerUrl = '/organizers/' + props.organizerId + '/runners'
const choices = (items: string[]) => items.map((title, value) => ({ title, value }))
const statuses = choices(['Pending', 'Confirmed', 'Waitlisted', 'Cancelled', 'DNF', 'DNS', 'Disqualified'])
const payments = choices(['Unspecified', 'Unpaid', 'Paid', 'Waived', 'Refunded'])
const modes = choices(['Existing bibs', 'Automatic bibs', 'Mixed assignment'])
const defaultDraft = (): Draft => ({ raceId: null, runnerId: null, categoryId: null, waveId: null,
  bibDisplayName: null, bibNumber: null, team: null, status: 0, paymentStatus: 0 })
const assignment = () => ({ mode: 0, start: 1, end: 1000, width: 0 })
const busy = ref(false)
const error = ref('')
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
const submitted = ref<{ mapping?: string; assignment: string } | null>(null)
const mapping = ref<Record<string, number>>({})
const importAssignment = ref(assignment())
const selectedRace = computed(() => races.value.find(race => race.id === draft.value.raceId))
const bibNameHint = computed(() => {
  if (draft.value.bibDisplayName?.trim()) return 'Printed bibs and public participant outputs use this name.'
  const runner = editing.value?.runner ?? runners.value.find(item => item.id === draft.value.runnerId)
  return runner
    ? 'Blank publishes the registered full name: ' + runner.firstName + ' ' + runner.lastName + '.'
    : 'Blank publishes the runner’s registered full name.'
})
const headers = computed(() => preview.value?.headers.map((title, value) => ({ title, value })) ?? [])
const runnerLabel = (value: Profile) => value.firstName + ' ' + value.lastName + (value.email ? ' · ' + value.email : '')

async function run(action: () => Promise<void>) {
  busy.value = true
  error.value = ''
  try { await action() }
  catch (failure) {
    const body = (failure as { response?: { data?: unknown } })?.response?.data
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
    races.value = (await loadConfiguration(props.eventId)).races
    editing.value = row ?? null
    manualAssignment.value = assignment()
    confirmCancel.value = false
    history.value = []
    draft.value = defaultDraft()
    if (row) {
      const { data } = await api.get<{ registration: Row; history: Change[] }>(registrationUrl + '/' + row.id)
      editing.value = data.registration
      draft.value = { ...data.registration }
      history.value = data.history
    } else await fetchRunners()
    showRegistration.value = true
  })
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
    const body = { ...draft.value, bibNumber: manualAssignment.value.mode === 1 ? null : draft.value.bibNumber,
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
    if (!editing.value || !confirmCancel.value) return
    await api.post(registrationUrl + '/' + editing.value.id + '/cancel')
    showRegistration.value = false
    notice.value = 'Registration cancelled.'
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
function invalidate() { token.value = null }
function selectFile(file: File | File[] | null) {
  csvFile.value = Array.isArray(file) ? file[0] ?? null : file
  preview.value = null
  mapping.value = {}
  invalidate()
}
function mapField(field: string, value: number | null) {
  if (value === null) delete mapping.value[field]
  else mapping.value[field] = value
  invalidate()
}
function importForm(options: { mapping?: string; assignment: string }) {
  const form = new FormData()
  if (csvFile.value) form.append('file', csvFile.value)
  if (options.mapping) form.append('mapping', options.mapping)
  form.append('assignment', options.assignment)
  return form
}
function previewImport() {
  return run(async () => {
    invalidate()
    const options = { mapping: preview.value ? JSON.stringify(mapping.value) : undefined,
      assignment: JSON.stringify(importAssignment.value) }
    const { data } = await api.post<Preview>(registrationUrl + '/import/preview', importForm(options), { headers: { 'Content-Type': 'multipart/form-data' } })
    preview.value = data
    mapping.value = { ...data.mapping }
    submitted.value = options
    token.value = data.token
  })
}
function confirmImport() {
  return run(async () => {
    if (!token.value || !submitted.value) return
    const form = importForm(submitted.value)
    form.append('token', token.value)
    invalidate()
    const { data } = await api.post<{ importedCount: number }>(registrationUrl + '/import/confirm', form, { headers: { 'Content-Type': 'multipart/form-data' } })
    notice.value = 'Imported ' + data.importedCount + ' registrations.'
    showImport.value = false
    selectFile(null)
    await loadRows()
  })
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
.mapping { display: grid; grid-template-columns: repeat(auto-fit, minmax(13rem, 1fr)); gap: 0.5rem; }
.preview-rows { max-height: 16rem; overflow: auto; }
</style>
