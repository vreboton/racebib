<template>
  <v-container class="mx-auto py-8" style="max-width: 60rem">
    <h1 class="mb-4">Race participants</h1>
    <v-alert v-if="error" type="error">{{ error }}</v-alert>
    <v-progress-linear v-if="loading" indeterminate />
    <template v-else-if="!error">
      <v-table>
        <thead><tr><th>Bib</th><th>Name on Bib</th><th>Race Category</th></tr></thead>
        <tbody>
          <tr v-for="(row, index) in rows" :key="index">
            <td>{{ row.bibNumber || 'Unassigned' }}</td><td>{{ row.nameOnBib }}</td><td>{{ row.race }}</td>
          </tr>
          <tr v-if="!rows.length"><td colspan="3">No participants on this page.</td></tr>
        </tbody>
      </v-table>
      <div class="d-flex flex-wrap ga-2 mt-4">
        <v-btn :disabled="page === 1" variant="outlined" @click="page--">Previous</v-btn>
        <v-btn :disabled="rows.length < 100" variant="outlined" @click="page++">Next</v-btn>
        <v-btn v-if="rows.length" variant="text" @click="download">Download participant CSV</v-btn>
      </div>
    </template>
  </v-container>
</template>

<script setup lang="ts">
import { ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { api } from '../services/api'

interface Participant { nameOnBib: string; bibNumber: string | null; race: string }
const route = useRoute()
const page = ref(1)
const rows = ref<Participant[]>([])
const loading = ref(false)
const error = ref('')

watch(() => [route.params.id, page.value] as const, async ([id, current], _, onCleanup) => {
  const controller = new AbortController()
  onCleanup(() => controller.abort())
  loading.value = true
  error.value = ''
  try {
    const { data } = await api.get<Participant[]>('/public/events/' + id + '/participants', {
      params: { page: current }, signal: controller.signal,
    })
    rows.value = data
  } catch {
    if (!controller.signal.aborted) error.value = 'The participant list is unavailable.'
  } finally {
    if (!controller.signal.aborted) loading.value = false
  }
}, { immediate: true })

function csvCell(value: string) {
  const safe = /^[\s\uFEFF]*[=+\-@\uFF1D\uFF0B\uFF0D\uFF20]|^[\t\r\n]/u.test(value) ? "'" + value : value
  return '"' + safe.replaceAll('"', '""') + '"'
}
function download() {
  const lines = rows.value.map(row => [row.bibNumber ?? '', row.nameOnBib, row.race].map(csvCell).join(','))
  const href = URL.createObjectURL(new Blob(['\uFEFF', ['Bib Number,Name on Bib,Race', ...lines].join('\r\n')],
    { type: 'text/csv;charset=utf-8' }))
  const link = document.createElement('a')
  link.href = href
  link.download = 'public-participants-' + page.value + '.csv'
  link.click()
  setTimeout(() => URL.revokeObjectURL(href), 1000)
}
</script>
