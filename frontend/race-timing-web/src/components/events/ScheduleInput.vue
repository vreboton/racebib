<template>
  <v-text-field v-model="wall" type="datetime-local" step="1" :label="`${label} (${zone})`"
    :rules="required ? [() => Boolean(wall) || 'This field is required'] : []"
    :error-messages="wall && !options.length ? 'Enter a valid local time in the event time zone.' : undefined" />
  <v-select v-if="options.length > 1" v-model="selected" :items="options"
    :label="`Choose the offset for ${label.toLowerCase()}`" />
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { isTimeZone, localTime, scheduleOptions, utcTimestamp } from '../../services/eventTime'

const props = withDefaults(defineProps<{ zone: string; label?: string; required?: boolean; preserveInstantOnZoneChange?: boolean }>(), { label: 'Start' })
const value = defineModel<string>({ required: true })
const original = value.value ? utcTimestamp(value.value) : ''
const originalWall = original && isTimeZone(props.zone) ? localTime(new Date(original), props.zone) : ''
const wall = ref(originalWall)
const options = computed(() => scheduleOptions(wall.value, props.zone))
function wholeSecond(instant: string) {
  return Math.floor(Date.parse(instant) / 1000)
}
const selected = ref(options.value.find(option => wholeSecond(option.value) === wholeSecond(original))?.value ?? '')

function publish() {
  const current = value.value && utcTimestamp(value.value)
  value.value = current && wholeSecond(current) === wholeSecond(selected.value) ? current : selected.value
}

watch([wall, () => props.zone], ([, zone], [, previousZone]) => {
  if (props.preserveInstantOnZoneChange && zone !== previousZone && value.value) {
    if (!isTimeZone(zone)) return
    wall.value = localTime(new Date(utcTimestamp(value.value)), zone)
  }
  const currentOption = value.value && options.value.find(option =>
    wholeSecond(option.value) === wholeSecond(utcTimestamp(value.value)))
  if (currentOption) {
    selected.value = currentOption.value
    return
  }
  selected.value = options.value.length === 1 ? options.value[0].value : ''
  publish()
})
watch(selected, publish)
</script>
