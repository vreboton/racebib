<template>
  <v-card border flat rounded="lg" class="mt-5" :loading="busy">
    <v-card-title>Race Configuration</v-card-title>
    <v-card-text>
      <v-alert v-if="error" type="error" variant="tonal" class="mb-3">{{ error }}
        <v-btn variant="text" :disabled="busy" @click="reload">Reload</v-btn>
      </v-alert>
      <template v-if="configuration">
        <v-alert v-if="!configuration.canEdit" type="info" variant="tonal" class="mb-3">
          Editing is unavailable for your permissions or the event's current status.
        </v-alert>
        <div class="d-flex align-center justify-space-between flex-wrap ga-2 mb-2">
          <h3>Races</h3>
          <v-btn v-if="configuration.canEdit" color="primary" :disabled="!usableCourses.length" @click="editRace()">Add Race</v-btn>
        </div>
        <p v-if="!configuration.races.length" class="mb-2">No races yet.</p>
        <div v-if="configuration.canEdit && !usableCourses.length" class="mb-2">
          <p>Add a course with start and finish checkpoints before creating a race.</p>
          <v-btn v-if="!configuration.courses.length" variant="text" @click="editCourse()">Add Course</v-btn>
        </div>
        <v-expansion-panels multiple>
          <v-expansion-panel v-for="race in configuration.races" :key="race.id"
            :title="`${race.name} · ${race.distanceKm} km${race.status === 'Inactive' ? ' · Inactive' : ' · Active'}`">
            <v-expansion-panel-text>
              <p>Start {{ displayStart(race.startAt) }} ({{ zone }})</p>
              <p v-if="race.cutoffDurationSeconds">Cutoff {{ race.cutoffDurationSeconds }} seconds after start</p>
              <p v-if="race.description">{{ race.description }}</p>
              <div v-if="editable(race)" class="d-flex flex-wrap ga-2 my-2">
                <v-btn variant="text" @click="editRace(race)">Edit Race</v-btn>
                <v-btn variant="text" color="error" @click="retire(race.name, `races/${race.id}`)">Retire Race</v-btn>
              </div>
              <v-alert v-if="race.categories.length > 1" type="warning" variant="tonal" class="my-3">
                This race has {{ race.categories.length }} existing registration categories.
                Choose a category when registering a runner if more than one is active.
              </v-alert>
              <h4 class="mt-3">Course</h4>
              <template v-if="courseById.get(race.courseId)">
                <p>{{ courseLabel(courseById.get(race.courseId)!) }}</p>
                <ol v-if="courseById.get(race.courseId)?.checkpoints.length" class="ms-5 my-2">
                  <li v-for="point in courseById.get(race.courseId)?.checkpoints" :key="point.id">
                    {{ point.name }} &middot; {{ point.distanceFromStartKm }} km
                  </li>
                </ol>
                <div v-if="configuration.canEdit && !courseById.get(race.courseId)?.immutable" class="d-flex flex-wrap">
                  <v-btn variant="text" @click="editCourse(courseById.get(race.courseId)!)">Edit course details</v-btn>
                  <v-btn variant="text" @click="editRoute(courseById.get(race.courseId)!)">Edit checkpoints</v-btn>
                </div>
              </template>

              <h4 class="mt-4">Start Waves</h4>
              <p v-if="!race.waves.length">No Start Waves yet.</p>
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
              <v-btn v-if="editable(race)" variant="text" @click="editWave(race)">Add Start Wave</v-btn>
            </v-expansion-panel-text>
          </v-expansion-panel>
        </v-expansion-panels>
        <details class="mt-5" :open="!usableCourses.length">
          <summary class="font-weight-medium">Courses and revisions</summary>
          <v-btn v-if="configuration.canEdit && configuration.courses.length" class="my-2" variant="tonal" @click="editCourse()">Add Course</v-btn>
          <p v-if="!configuration.courses.length">No Courses yet.</p>
          <v-expansion-panels multiple>
            <v-expansion-panel v-for="course in configuration.courses" :key="course.id"
              :title="courseLabel(course)">
              <v-expansion-panel-text>
                <p v-if="course.description" class="mb-2">{{ course.description }}</p>
                <p v-if="!course.checkpoints.length">No Course Checkpoints yet.</p>
                <ol class="ms-5 my-2">
                  <li v-for="point in course.checkpoints" :key="point.id">
                    {{ point.name }} ({{ point.code }}) · {{ point.distanceFromStartKm }} km
                    {{ point.isStart ? '· Start' : '' }} {{ point.isFinish ? '· Finish' : '' }}
                  </li>
                </ol>
                <p v-if="course.routeEditBlock === 'lifecycle' || course.editBlock === 'registrations'" class="text-medium-emphasis">
                  This Course is assigned to a race outside Draft. Duplicate the Course to make an editable copy.
                </p>
                <p v-else-if="course.editBlock === 'historical'" class="text-medium-emphasis">
                  This historical Course Revision is read-only. Duplicate the Course to make an editable copy.
                </p>
                <p v-else-if="course.editBlock === 'inactive'" class="text-medium-emphasis">
                  Reactivate this Course before editing it.
                </p>
                <p v-if="configuration.canEdit && course.status === 'Active' && course.hasActiveRaces" class="text-medium-emphasis">
                  An active race uses this Course. Retire the race or switch it to an eligible Course Revision before retiring this revision.
                </p>
                <div v-if="configuration.canEdit || course.canDelete" class="d-flex flex-wrap ga-2 mt-3">
                  <template v-if="configuration.canEdit">
                    <v-btn v-if="!course.immutable" variant="text" @click="editCourse(course)">Edit Course</v-btn>
                    <v-btn v-if="!course.immutable" variant="text" @click="editRoute(course)">Edit Course Checkpoints</v-btn>
                    <v-btn variant="text" :disabled="busy" @click="duplicateCourse(course)">Duplicate Course</v-btn>
                    <v-btn v-if="course.status === 'Active'" variant="text" color="error" :disabled="busy || course.hasActiveRaces"
                      @click="changeCourseStatus(course, 'Inactive')">Retire Course</v-btn>
                    <v-btn v-else variant="text" :disabled="busy" @click="changeCourseStatus(course, 'Active')">Reactivate Course</v-btn>
                  </template>
                  <v-btn v-if="course.status === 'Inactive' && course.canDelete" variant="text" color="error" :disabled="busy"
                    @click="deleteCourse(course)">Delete Course</v-btn>
                </div>

              </v-expansion-panel-text>
            </v-expansion-panel>
          </v-expansion-panels>
        </details>

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
              Put Finish last and match its distance from start to the Course distance. Saving updates the distance for the affected races.
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
            <v-btn variant="tonal" @click="addPoint">Add Course Checkpoint</v-btn>
          </template>
          <template v-else>
            <v-text-field v-model="editor.name" label="Name" required :maxlength="editor.kind === 'course' ? 160 : 120"
              :readonly="editor.kind === 'course' && editor.nameLocked" />
            <v-textarea v-if="editor.kind !== 'wave'" v-model="editor.description" label="Description" rows="2" />
            <template v-if="editor.kind === 'course'">
              <v-text-field v-model="editor.distanceKm" type="number" min="0.001" step="0.001" required label="Distance (km)"
                :hint="editor.hasFinish ? 'Saving updates the Finish checkpoint and the affected races.' : undefined"
                :persistent-hint="editor.hasFinish" />
              <v-text-field v-model="editor.elevationGainMeters" type="number" min="0" label="Elevation gain (m)" />
            </template>
            <template v-if="editor.kind === 'race'">
              <v-select v-model="editor.courseId" :items="editor.courses" label="Course" required />
              <v-text-field v-model="editor.cutoffDurationSeconds" type="number" min="1" label="Cutoff (seconds)" />
            </template>
            <ScheduleInput v-if="editor.kind === 'race' || editor.kind === 'wave'" :key="editor.path"
              v-model="editor.startAt" :zone="zone" />
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
import type { Checkpoint, Configuration, Course, Race, Wave } from '../../services/raceConfiguration'
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
  | { kind: 'wave'; name: string; startAt: string; sequence: Numeric; capacity: Numeric }
)
const editor = ref<Editor>()
let pointKey = 0
const usableCourses = computed(() => configuration.value?.courses.filter(c => c.usable) ?? [])
const racesByCourse = computed(() => {
  const groups = new Map<string, Race[]>()
  for (const race of configuration.value?.races ?? []) {
    const group = groups.get(race.courseId)
    if (group) group.push(race)
    else groups.set(race.courseId, [race])
  }
  return groups
})
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
  catch (failure) { error.value = getApiError(failure, 'Unable to load Race Configuration.') }
  finally { busy.value = false }
}
function courseLabel(course: Course) {
  const revision = course.version > 1 || configuration.value?.courses.some(other => other.id !== course.id && other.name === course.name) ? ` · Revision ${course.version}` : ''
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
    error.value = getApiError(failure, 'Unable to save Race Configuration.')
  } finally { busy.value = false }
}
function duplicateCourse(course: Course) {
  void mutate(`courses/${course.id}/duplicate`, 'post', undefined, id => {
    const copy = configuration.value?.courses.find(item => item.id === id)
    if (copy && configuration.value?.canEdit && !copy.immutable) editCourse(copy)
  })
}
function deleteCourse(course: Course) {
  if (!window.confirm(`Permanently delete ${course.name} and all its Course Revisions and Course Checkpoints? This cannot be undone.`)) return
  void mutate(`courses/${course.id}`, 'delete')
}
function changeCourseStatus(course: Course, status: Course['status']) {
  if (status === 'Inactive' && !window.confirm(`Retire ${course.name}? Existing race records will keep this Course Revision.`)) return
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
  editor.value = { ...base(course ? 'Edit Course' : 'Add Course', 'courses', course?.id), kind: 'course',
    name: course?.name ?? '', description: course?.description ?? null, distanceKm: course?.distanceKm ?? null,
    elevationGainMeters: course?.elevationGainMeters ?? null, nameLocked: !!course && (course.version > 1 || !!course.affectedRegistrationCount ||
      !!racesByCourse.value.get(course.id)?.some(race => race.status === 'Inactive') ||
      !!configuration.value?.courses.some(other => other.id !== course.id && other.name === course.name)),
    hasFinish: course?.checkpoints.some(point => point.isFinish) ?? false }
}
function editRoute(course: Course) {
  error.value = ''
  editor.value = { title: 'Edit Course Checkpoints', path: `courses/${course.id}/checkpoints`, method: 'put',
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
  editor.value = { ...base(race ? 'Edit Race' : 'Add Race', 'races', race?.id), kind: 'race',
    name: race?.name ?? '', description: race?.description ?? null, courseId: race?.courseId ?? courses[0]?.id ?? '',
    originalCourse: race?.courseId ?? '', startAt: race?.startAt ?? '', cutoffDurationSeconds: race?.cutoffDurationSeconds ?? null,
    courses: courses.map(c => ({ title: courseLabel(c), value: c.id })) }
}
function editWave(race: Race, wave?: Wave) {
  const nextSequence = Math.max(0, ...race.waves.map(w => w.sequence)) + 1
  editor.value = { ...base(wave ? 'Edit Start Wave' : 'Add Start Wave', `races/${race.id}/waves`, wave?.id), kind: 'wave',
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
          !window.confirm('Switch this race to the selected Course Revision?')) return
      payload = { name: form.name, description: form.description, courseId: form.courseId,
        startAt: form.startAt, cutoffDurationSeconds: number(form.cutoffDurationSeconds), ensureRegistrationCategory: true }
      break
    case 'wave':
      payload = { name: form.name, startAt: form.startAt, sequence: number(form.sequence), capacity: number(form.capacity) }
  }
  let path = form.path
  if ((form.kind === 'course' || form.kind === 'route') && form.method === 'put') {
    const course = courseById.value.get(path.split('/')[1] ?? '')
    if (course?.registrationConfirmation && course.affectedRegistrationCount) {
      const count = course.affectedRegistrationCount
      const distance = number(form.distanceKm)
      const changes = [
        distance !== course.distanceKm ? `Distance: ${course.distanceKm} km → ${distance ?? 'unset'} km.` : '',
        form.kind === 'route' ? 'The submitted checkpoint layout and cutoffs will apply.' : 'The submitted Course details will apply.',
      ].filter(Boolean).join(' ')
      if (!window.confirm(`Save a new Course Revision for ${course.name}? ${changes} ` +
        `This affects ${count} existing registration${count === 1 ? '' : 's'}. Runner profiles, bibs, Age Groups, and Start Waves will be preserved.`)) return
      path += `?registrationConfirmation=${encodeURIComponent(course.registrationConfirmation)}`
    }
  }
  void mutate(path, form.method, payload, form.kind === 'course' && form.method === 'post' ? id => {
    const course = courseById.value.get(id)
    if (course && configuration.value?.canEdit && !course.immutable) editRoute(course)
  } : undefined)
}
</script>
