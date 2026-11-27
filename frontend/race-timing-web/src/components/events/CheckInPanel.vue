<template>
  <v-card class="my-4" variant="outlined" :loading="busy">
    <v-card-title>Runner check-in</v-card-title>
    <v-card-text>
      <v-form id="check-in-search" class="d-flex flex-wrap align-center ga-2 mb-4" @submit.prevent="search">
        <v-text-field v-model="query" label="Bib number or runner name" hide-details maxlength="120" />
        <v-btn type="submit" color="primary" :disabled="busy">Search</v-btn>
      </v-form>
      <v-alert v-if="error" type="error" class="mb-3">
        {{ error }}
        <v-btn v-if="refreshRequired" variant="text" :disabled="busy" @click="refresh">Refresh results</v-btn>
      </v-alert>
      <v-alert v-if="notice" type="success" role="status" class="mb-3">{{ notice }}</v-alert>
      <div v-for="row in rows" :key="row.id" class="runner py-4">
        <div class="runner-info">
          <div class="text-subtitle-1 font-weight-bold">{{ row.firstName }} {{ row.lastName }}</div>
          <div>Bib {{ row.bibNumber || 'unassigned' }} · {{ row.race }} · {{ statuses[row.status] }}</div>
          <div v-if="row.checkedInAtUtc">Checked in {{ formatEventTime(row.checkedInAtUtc, zone) }}</div>
          <div v-else>Not checked in<span v-if="row.checkInError"> · {{ row.checkInError }}</span></div>
        </div>
        <v-btn v-if="row.checkedInAtUtc && row.canReverse" variant="outlined"
          :disabled="busy || refreshRequired" @click="openReversal(row)">Reverse check-in</v-btn>
        <v-btn v-else-if="!row.checkedInAtUtc && !row.checkInError" color="primary" size="large"
          :disabled="busy || refreshRequired" @click="changeCheckIn(row)">Check in</v-btn>
      </div>
      <p v-if="!rows.length && !busy && !error">No matching registrations.</p>
      <v-pagination v-if="total > pageSize" :model-value="page" :length="Math.ceil(total / pageSize)"
        :disabled="busy || refreshRequired" @update:model-value="loadPage" />
    </v-card-text>
  </v-card>

  <v-dialog :model-value="Boolean(reversing)" :persistent="busy" max-width="480"
    @update:model-value="visible => { if (!visible) reversing = null }">
    <v-card v-if="reversing" title="Reverse check-in">
      <v-card-text>
        <p class="mb-3">{{ reversing.firstName }} {{ reversing.lastName }} · Bib {{ reversing.bibNumber }}</p>
        <v-alert v-if="error" type="error" class="mb-3">{{ error }}</v-alert>
        <v-form id="check-in-reversal" @submit.prevent="confirmReversal">
          <v-text-field v-model="reason" label="Reversal reason" hint="Use an operational reason without personal details."
            persistent-hint :maxlength="300" :disabled="busy" required />
        </v-form>
      </v-card-text>
      <v-card-actions>
        <v-btn :disabled="busy" @click="reversing = null">Cancel</v-btn>
        <v-btn color="error" type="submit" form="check-in-reversal" :disabled="busy || !reason.trim()">
          Confirm reversal
        </v-btn>
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>

<script setup lang="ts">
import { formatEventTime } from '../../services/eventTime'
import { onMounted, ref } from 'vue'
import { api } from '../../services/api'
import { getApiError } from '../../services/events'

interface VerificationRow {
  id: string
  version: number
  firstName: string
  lastName: string
  bibNumber: string | null
  race: string
  status: number
  checkedInAtUtc: string | null
  checkInError: string | null
  canReverse: boolean
}

const props = withDefaults(defineProps<{ eventId: string; zone?: string }>(), { zone: 'UTC' })
const url = '/events/' + props.eventId + '/registrations'
const statuses = ['Pending', 'Confirmed', 'Waitlisted', 'Withdrawn', 'DNF', 'DNS', 'Disqualified']
const rows = ref<VerificationRow[]>([])
const query = ref('')
const submittedQuery = ref('')
const page = ref(1)
const pageSize = ref(30)
const total = ref(0)
const busy = ref(false)
const error = ref('')
const notice = ref('')
const refreshRequired = ref(false)
const reversing = ref<VerificationRow | null>(null)
const reason = ref('')

async function run(action: () => Promise<void>) {
  if (busy.value) return
  busy.value = true
  error.value = ''
  notice.value = ''
  try {
    await action()
  } catch (failure) {
    const response = (failure as { response?: { data?: unknown; status?: number } })?.response
    if (response?.status === 409) {
      refreshRequired.value = true
      reversing.value = null
    }
    error.value = typeof response?.data === 'string' ? response.data : getApiError(failure, 'Check-in request failed.')
  } finally {
    busy.value = false
  }
}

function loadPage(nextPage: number) {
  return run(async () => {
    const { data } = await api.get<{ total: number; pageSize: number; items: VerificationRow[] }>(url + '/check-in', {
      params: { search: submittedQuery.value, page: nextPage },
    })
    rows.value = data.items
    total.value = data.total
    pageSize.value = data.pageSize
    page.value = nextPage
    refreshRequired.value = false
  })
}

function search() {
  if (busy.value) return
  submittedQuery.value = query.value.trim()
  return loadPage(1)
}

const refresh = () => loadPage(page.value)

function openReversal(row: VerificationRow) {
  error.value = ''
  reason.value = ''
  reversing.value = row
}

function confirmReversal() {
  if (!reversing.value || !reason.value.trim()) return
  return changeCheckIn(reversing.value, reason.value.trim())
}

function changeCheckIn(row: VerificationRow, reversalReason?: string) {
  if (refreshRequired.value) return
  return run(async () => {
    const endpoint = url + '/' + row.id + '/check-in'
    const { data } = reversalReason
      ? await api.delete<VerificationRow>(endpoint, { data: { version: row.version, reason: reversalReason } })
      : await api.put<VerificationRow>(endpoint, { version: row.version })
    rows.value = rows.value.map(item => item.id === row.id ? data : item)
    reversing.value = null
    notice.value = reversalReason ? 'Check-in reversed.' : row.firstName + ' ' + row.lastName + ' checked in.'
  })
}

onMounted(refresh)
</script>

<style scoped>
.runner {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  border-top: 1px solid rgba(var(--v-border-color), var(--v-border-opacity));
}

.runner-info {
  overflow-wrap: anywhere;
}

@media (max-width: 600px) {
  .runner {
    align-items: stretch;
    flex-direction: column;
  }
}
</style>
