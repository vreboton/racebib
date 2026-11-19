<template>
  <v-card border flat rounded="lg" class="mt-5" :loading="busy">
    <v-card-title>Race configuration</v-card-title>
    <v-card-text>
      <v-alert v-if="error" type="error" variant="tonal" class="mb-3">{{ error }}
        <v-btn variant="text" :disabled="busy" @click="reload">Reload</v-btn>
      </v-alert>
      <template v-if="configuration">
        <v-alert v-if="!configuration.canEdit" type="info" variant="tonal" class="mb-3">
          Configuration is read-only for your permissions or the event's current status.
        </v-alert>
        <div class="d-flex align-center justify-space-between mb-2">
          <h3>Courses</h3>
          <v-btn v-if="configuration.canEdit" variant="tonal" @click="editCourse()">Add course</v-btn>
        </div>
        <p v-if="!configuration.courses.length">No courses yet.</p>
        <v-expansion-panels multiple>
          <v-expansion-panel v-for="course in configuration.courses" :key="course.id"
            :title="courseLabel(course)">
            <v-expansion-panel-text>
              <p v-if="course.description" class="mb-2">{{ course.description }}</p>
              <p v-if="!course.checkpoints.length">No checkpoints yet.</p>
              <ol class="ms-5 my-2">
                <li v-for="point in course.checkpoints" :key="point.id">
                  {{ point.name }} ({{ point.code }}) · {{ point.distanceFromStartKm }} km
                  {{ point.isStart ? '· Start' : '' }} {{ point.isFinish ? '· Finish' : '' }}
                </li>
              </ol>
              <p v-if="course.editBlock === 'registrations'" class="text-medium-emphasis">
                A race using this course has registrations. Duplicate the course to make an editable copy.
              </p>
              <p v-else-if="course.editBlock === 'historical'" class="text-medium-emphasis">
                This historical revision is read-only. Duplicate the course to make an editable copy.
              </p>
              <p v-else-if="course.editBlock === 'inactive'" class="text-medium-emphasis">
                Reactivate this course before editing it.
              </p>
              <p v-if="configuration.canEdit && course.status === 'Active' && course.hasActiveRaces" class="text-medium-emphasis">
                An active race uses this course. Retire the race or switch it to an eligible course before retiring this course.
              </p>
              <div v-if="configuration.canEdit" class="d-flex flex-wrap ga-2 mt-3">
                <v-btn v-if="!course.immutable" variant="text" @click="editCourse(course)">Edit course</v-btn>
                <v-btn v-if="!course.immutable" variant="text" @click="editRoute(course)">Edit checkpoints</v-btn>
                <v-btn variant="text" :disabled="busy" @click="duplicateCourse(course)">Duplicate course</v-btn>
                <v-btn v-if="course.status === 'Active'" variant="text" color="error" :disabled="busy || course.hasActiveRaces"
                  @click="changeCourseStatus(course, 'Inactive')">Retire course</v-btn>
                <v-btn v-else variant="text" :disabled="busy" @click="changeCourseStatus(course, 'Active')">Reactivate course</v-btn>
                <v-btn v-if="course.canDelete" variant="text" color="error" :disabled="busy"
                  @click="deleteCourse(course)">Delete course</v-btn>
              </div>
            </v-expansion-panel-text>
          </v-expansion-panel>
        </v-expansion-panels>

        <div class="d-flex align-center justify-space-between mt-6 mb-2">
          <h3>Races</h3>
          <v-btn v-if="configuration.canEdit" variant="tonal" :disabled="!usableCourses.length" @click="editRace()">Add race</v-btn>
        </div>
        <p v-if="!configuration.races.length">
          {{ usableCourses.length ? 'No races yet.' : 'Add a course with start and finish checkpoints to create a race.' }}
        </p>
        <v-expansion-panels multiple>
          <v-expansion-panel v-for="race in configuration.races" :key="race.id"
            :title="`${race.name} · ${race.distanceKm} km${race.status === 'Inactive' ? ' · Retired' : ''}`">
            <v-expansion-panel-text>
              <p>{{ displayStart(race.startAt) }} ({{ zone }})</p>
              <p v-if="courseById.get(race.courseId)">Course · {{ courseLabel(courseById.get(race.courseId)!) }}</p>
              <p v-if="race.description">{{ race.description }}</p>
              <div v-if="editable(race)" class="d-flex flex-wrap ga-2 my-2">
                <v-btn variant="text" @click="editRace(race)">Edit race</v-btn>
                <v-btn variant="text" color="error" @click="retire(race.name, `races/${race.id}`)">Retire race</v-btn>
              </div>
              <h4 class="mt-4">Categories</h4>
              <p v-if="!race.categories.length">No categories yet.</p>
              <div v-for="category in race.categories" :key="category.id" class="d-flex align-center flex-wrap">
                <span>{{ category.name }} · Ages {{ category.minimumAge ?? 'any' }} to {{ category.maximumAge ?? 'any' }}
                  {{ category.status === 'Inactive' ? '· Retired' : '' }}</span>
                <template v-if="editable(race) && category.status === 'Active'">
                  <span v-if="category.hasRegistrations" class="ms-2 text-medium-emphasis">Used by registrations</span>
                  <v-btn v-else variant="text" size="small" @click="editCategory(race, category)">Edit</v-btn>
                  <v-btn variant="text" size="small" color="error" @click="retire(category.name, `races/${race.id}/categories/${category.id}`)">Retire</v-btn>
                </template>
              </div>
              <v-btn v-if="editable(race)" variant="text" @click="editCategory(race)">Add category</v-btn>

              <h4 class="mt-4">Start waves</h4>
              <p v-if="!race.waves.length">No waves yet.</p>
              <div v-for="wave in race.waves" :key="wave.id" class="d-flex align-center flex-wrap">
                <span>{{ wave.sequence }}. {{ wave.name }} · {{ displayStart(wave.startAt) }}
                  {{ wave.capacity ? `· Capacity ${wave.capacity}` : '' }}
                  {{ wave.status === 'Inactive' ? '· Retired' : '' }}</span>
                <template v-if="editable(race) && wave.status === 'Active'">
                  <span v-if="wave.hasRegistrations" class="ms-2 text-medium-emphasis">Used by registrations</span>
                  <v-btn v-else variant="text" size="small" @click="editWave(race, wave)">Edit</v-btn>
                  <v-btn variant="text" size="small" color="error" @click="retire(wave.name, `races/${race.id}/waves/${wave.id}`)">Retire</v-btn>
                </template>
              </div>
              <v-btn v-if="editable(race)" variant="text" @click="editWave(race)">Add wave</v-btn>
            </v-expansion-panel-text>
          </v-expansion-panel>
        </v-expansion-panels>
      </template>
    </v-card-text>
  </v-card>

  <v-dialog :model-value="Boolean(editor)" :persistent="busy" max-width="760" @update:model-value="editor = undefined">
    <v-card v-if="editor" :title="editor.title">
      <v-form @submit.prevent="submit">
        <v-card-text>
          <v-alert v-if="error" type="error" variant="tonal" class="mb-3">{{ error }}
            <v-btn variant="text" :disabled="busy" @click="reload">Reload saved configuration</v-btn>
          </v-alert>
          <template v-if="editor.kind === 'route'">
            <v-text-field v-model="editor.distanceKm" type="number" step="0.001" min="0.001" required label="Course distance (km)" />
            <p class="text-body-2 text-medium-emphasis mb-3">
              Put Finish last and match its distance from start to the course distance. Saving a new distance updates races using this course.
            </p>
            <fieldset v-for="(point, index) in editor.checkpoints" :key="point.key" class="pa-3 mb-3">
              <legend>Checkpoint {{ index + 1 }}</legend>
              <div class="d-flex flex-wrap ga-2 mb-2">
                <v-btn size="small" :disabled="index === 0" @click="movePoint(index, -1)">Move up</v-btn>
                <v-btn size="small" :disabled="index === editor.checkpoints.length - 1" @click="movePoint(index, 1)">Move down</v-btn>
                <v-btn size="small" color="error" variant="text" @click="editor.checkpoints.splice(index, 1)">Remove</v-btn>
              </div>
              <v-row dense>
                <v-col cols="12" sm="6"><v-text-field v-model="point.name" required label="Name" maxlength="160" /></v-col>
                <v-col cols="12" sm="6"><v-text-field v-model="point.code" required label="Code" maxlength="40" /></v-col>
                <v-col cols="12" sm="6"><v-text-field v-model="point.distanceFromStartKm" required type="number" min="0" step="0.001" label="Distance from start (km)" /></v-col>
                <v-col cols="12" sm="6"><v-text-field v-model="point.elevationMeters" type="number" label="Elevation (m)" /></v-col>
              </v-row>
              <v-text-field v-model="point.description" label="Description" />
              <div class="d-flex flex-wrap">
                <v-checkbox v-model="point.isStart" label="Start" hide-details />
                <v-checkbox v-model="point.isFinish" label="Finish" hide-details />
                <v-checkbox v-model="point.isMandatory" label="Mandatory" hide-details />
                <v-checkbox v-model="point.hasCutoff" label="Has cutoff" hide-details />
              </div>
              <v-text-field v-if="point.hasCutoff" v-model="point.cutoffDurationSeconds" required type="number" min="1" label="Cutoff (seconds)" />
            </fieldset>
            <v-btn variant="tonal" @click="addPoint">Add checkpoint</v-btn>
          </template>
          <template v-else>
            <v-text-field v-model="editor.name" label="Name" required :maxlength="editor.kind === 'course' ? 160 : 120"
              :readonly="editor.kind === 'course' && editor.nameLocked" />
            <v-textarea v-if="editor.kind !== 'wave'" v-model="editor.description" label="Description" rows="2" />
            <template v-if="editor.kind === 'course'">
              <v-text-field v-model="editor.distanceKm" type="number" min="0.001" step="0.001" required label="Distance (km)"
                :hint="editor.hasFinish ? 'Saving updates the Finish checkpoint and any races using this course.' : undefined"
                :persistent-hint="editor.hasFinish" />
              <v-text-field v-model="editor.elevationGainMeters" type="number" min="0" label="Elevation gain (m)" />
            </template>
            <template v-if="editor.kind === 'race'">
              <v-select v-model="editor.courseId" :items="editor.courses" label="Course" required />
              <v-text-field v-model="editor.cutoffDurationSeconds" type="number" min="1" label="Cutoff (seconds)" />
            </template>
            <ScheduleInput v-if="editor.kind === 'race' || editor.kind === 'wave'" :key="editor.path"
              v-model="editor.startAt" :zone="zone" />
            <template v-if="editor.kind === 'category'">
              <v-text-field v-model="editor.minimumAge" type="number" min="0" label="Minimum age (inclusive)" />
              <v-text-field v-model="editor.maximumAge" type="number" min="0" label="Maximum age (inclusive)" />
            </template>
            <template v-if="editor.kind === 'wave'">
              <v-text-field v-model="editor.sequence" type="number" min="1" required label="Sequence" />
              <v-text-field v-model="editor.capacity" type="number" min="1" label="Capacity" />
            </template>
          </template>
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn :disabled="busy" @click="editor = undefined">Cancel</v-btn>
          <v-btn type="submit" color="primary" :loading="busy" :disabled="!configuration?.canEdit">Save</v-btn>
        </v-card-actions>
      </v-form>
    </v-card>
  </v-dialog>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { getApiError } from '../../services/events'
import { loadConfiguration, localTime, saveConfiguration, scheduleOptions } from '../../services/raceConfiguration'
import type { Category, Checkpoint, Configuration, Course, Race, Wave } from '../../services/raceConfiguration'
import ScheduleInput from './ScheduleInput.vue'

const props = defineProps<{ eventId: string; zone: string }>()
const configuration = ref<Configuration>()
const zone = computed(() => configuration.value?.timeZoneId ?? props.zone)
const busy = ref(false)
const error = ref('')
type Numeric = string | number | null
interface EditorBase { title: string; path: string; method: 'post' | 'put' }
type DraftPoint = Omit<Checkpoint, 'distanceFromStartKm' | 'elevationMeters' | 'cutoffDurationSeconds'> & {
  key: number; distanceFromStartKm: Numeric; elevationMeters: Numeric; cutoffDurationSeconds: Numeric
}
type Editor = EditorBase & (
  | { kind: 'course'; name: string; description: string | null; distanceKm: Numeric; elevationGainMeters: Numeric;
    nameLocked: boolean; hasFinish: boolean }
  | { kind: 'route'; distanceKm: Numeric; checkpoints: DraftPoint[] }
  | { kind: 'race'; name: string; description: string | null; courseId: string; originalCourse: string;
    startAt: string; cutoffDurationSeconds: Numeric; courses: { title: string; value: string }[] }
  | { kind: 'category'; name: string; description: string | null; minimumAge: Numeric; maximumAge: Numeric }
  | { kind: 'wave'; name: string; startAt: string; sequence: Numeric; capacity: Numeric }
)
const editor = ref<Editor>()
let pointKey = 0
const usableCourses = computed(() => configuration.value?.courses.filter(c => c.usable) ?? [])
const courseById = computed(() => new Map(configuration.value?.courses.map(course => [course.id, course])))
watch(() => props.eventId, reload, { immediate: true })

function displayStart(value: string) {
  const local = localTime(new Date(value), zone.value)
  const candidates = scheduleOptions(local, zone.value)
  const offset = candidates.length > 1 ? candidates.find(candidate => candidate.value === new Date(value).toISOString()) : undefined
  return local.replace('T', ' ') + (offset ? ' · ' + offset.title : '')
}
function editable(race: Race) {
  return Boolean(configuration.value?.canEdit && race.status === 'Active')
}
async function reload() {
  editor.value = undefined
  busy.value = true
  error.value = ''
  try { configuration.value = await loadConfiguration(props.eventId) }
  catch (failure) { error.value = getApiError(failure, 'Unable to load race configuration.') }
  finally { busy.value = false }
}
function courseLabel(course: Course) {
  const revision = course.version > 1 || course.editBlock === 'historical' ? ` · Revision ${course.version}` : ''
  const status = course.status === 'Inactive' ? ' · Retired' : ''
  return `${course.name}${revision} · ${course.distanceKm} km${status}`
}
async function mutate(path: string, method: 'post' | 'put' | 'patch' | 'delete', payload?: unknown,
  onSaved?: (id: string) => void) {
  busy.value = true
  error.value = ''
  try {
    const result = await saveConfiguration<{ id?: string } | undefined>(props.eventId, path, method, payload)
    await reload()
    if (result?.id && !error.value) onSaved?.(result.id)
  } catch (failure) {
    error.value = getApiError(failure, 'Unable to save race configuration.')
  } finally { busy.value = false }
}
function duplicateCourse(course: Course) {
  void mutate(`courses/${course.id}/duplicate`, 'post', undefined, id => {
    const copy = configuration.value?.courses.find(item => item.id === id)
    if (copy && configuration.value?.canEdit && !copy.immutable) editCourse(copy)
  })
}
function deleteCourse(course: Course) {
  if (!window.confirm(`Permanently delete ${course.name} and its checkpoints? This cannot be undone.`)) return
  void mutate(`courses/${course.id}`, 'delete')
}
function changeCourseStatus(course: Course, status: Course['status']) {
  if (status === 'Inactive' && !window.confirm(`Retire ${course.name}? Existing race records will keep this course.`)) return
  void mutate(`courses/${course.id}/status`, 'patch', { status })
}
function retire(name: string, path: string) {
  if (window.confirm(`Retire ${name}? Existing records will remain available.`)) void mutate(path, 'delete')
}
function base(title: string, path: string, id?: string): EditorBase {
  error.value = ''
  return { title, path: id ? `${path}/${id}` : path, method: id ? 'put' : 'post' }
}
function editCourse(course?: Course) {
  editor.value = { ...base(course ? 'Edit course' : 'Add course', 'courses', course?.id), kind: 'course',
    name: course?.name ?? '', description: course?.description ?? null, distanceKm: course?.distanceKm ?? null,
    elevationGainMeters: course?.elevationGainMeters ?? null, nameLocked: (course?.version ?? 1) > 1,
    hasFinish: course?.checkpoints.some(point => point.isFinish) ?? false }
}
function editRoute(course: Course) {
  error.value = ''
  editor.value = { title: 'Edit checkpoints', path: `courses/${course.id}/checkpoints`, method: 'put',
    kind: 'route', distanceKm: course.distanceKm, checkpoints: course.checkpoints.map(p => ({ ...p, key: ++pointKey })) }
}
function addPoint() {
  if (editor.value?.kind !== 'route') return
  editor.value.checkpoints.push({ key: ++pointKey, name: '', code: '', sequence: 0, distanceFromStartKm: null,
    elevationMeters: null, isMandatory: true, isStart: false, isFinish: false, hasCutoff: false,
    cutoffDurationSeconds: null, description: null })
}
function movePoint(index: number, delta: number) {
  if (editor.value?.kind !== 'route') return
  const points = editor.value.checkpoints
  const point = points[index]
  if (!point || !points[index + delta]) return
  points.splice(index, 1)
  points.splice(index + delta, 0, point)
}
function editRace(race?: Race) {
  const courses = usableCourses.value.filter(c => !race || c.id === race.courseId || race.revisionIds.includes(c.id))
  editor.value = { ...base(race ? 'Edit race' : 'Add race', 'races', race?.id), kind: 'race',
    name: race?.name ?? '', description: race?.description ?? null, courseId: race?.courseId ?? courses[0]?.id ?? '',
    originalCourse: race?.courseId ?? '', startAt: race?.startAt ?? '', cutoffDurationSeconds: race?.cutoffDurationSeconds ?? null,
    courses: courses.map(c => ({ title: courseLabel(c), value: c.id })) }
}
function editCategory(race: Race, category?: Category) {
  editor.value = { ...base(category ? 'Edit category' : 'Add category', `races/${race.id}/categories`, category?.id),
    kind: 'category', name: category?.name ?? '', description: category?.description ?? null,
    minimumAge: category?.minimumAge ?? null, maximumAge: category?.maximumAge ?? null }
}
function editWave(race: Race, wave?: Wave) {
  const nextSequence = Math.max(0, ...race.waves.map(w => w.sequence)) + 1
  editor.value = { ...base(wave ? 'Edit wave' : 'Add wave', `races/${race.id}/waves`, wave?.id), kind: 'wave',
    name: wave?.name ?? '', startAt: wave?.startAt ?? '', sequence: wave?.sequence ?? nextSequence, capacity: wave?.capacity ?? null }
}
function number(value: Numeric) {
  return value === '' || value === null ? null : Number(value)
}
function submit() {
  const form = editor.value
  if (!form || busy.value) return
  if ((form.kind === 'wave' || form.kind === 'race') && !form.startAt) {
    error.value = 'Enter a valid local start time and choose an offset if the time repeats.'
    return
  }
  let payload: unknown
  switch (form.kind) {
    case 'route':
      payload = { distanceKm: number(form.distanceKm), checkpoints: form.checkpoints.map(({ key, ...point }, index) => ({
        ...point, sequence: index + 1, distanceFromStartKm: number(point.distanceFromStartKm),
        elevationMeters: number(point.elevationMeters),
        cutoffDurationSeconds: point.hasCutoff ? number(point.cutoffDurationSeconds) : null,
      })) }
      break
    case 'course':
      payload = { name: form.name, description: form.description, distanceKm: number(form.distanceKm),
        elevationGainMeters: number(form.elevationGainMeters) }
      break
    case 'race':
      if (form.originalCourse && form.originalCourse !== form.courseId &&
          !window.confirm('Switch this race to the selected course?')) return
      payload = { name: form.name, description: form.description, courseId: form.courseId,
        startAt: form.startAt, cutoffDurationSeconds: number(form.cutoffDurationSeconds) }
      break
    case 'category':
      payload = { name: form.name, description: form.description, minimumAge: number(form.minimumAge), maximumAge: number(form.maximumAge) }
      break
    case 'wave':
      payload = { name: form.name, startAt: form.startAt, sequence: number(form.sequence), capacity: number(form.capacity) }
  }
  void mutate(form.path, form.method, payload)
}
</script>
