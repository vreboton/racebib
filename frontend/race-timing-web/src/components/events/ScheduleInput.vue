<template>
  <v-text-field v-model="wall" type="datetime-local" step="1" :label="`Start (${zone})`"
    :error-messages="wall && !options.length ? 'This local time does not exist in the event time zone.' : undefined" />
  <v-select v-if="options.length > 1" v-model="selected" :items="options"
    label="Choose the offset for this repeated local time" />
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { localTime, scheduleOptions } from '../../services/raceConfiguration'

const props = defineProps<{ zone: string }>()
const value = defineModel<string>({ required: true })
const wall = ref(value.value ? localTime(new Date(value.value), props.zone) : '')
const selected = ref(value.value ? new Date(value.value).toISOString() : '')
const options = computed(() => scheduleOptions(wall.value, props.zone))
watch(wall, () => {
  selected.value = options.value.length === 1 ? options.value[0].value : ''
  value.value = selected.value
})
watch(selected, () => { value.value = selected.value })
</script>
