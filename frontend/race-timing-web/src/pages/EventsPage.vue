<template>
  <div class="page-heading">
    <div><div class="eyebrow">WORKSPACE</div><h1>Events</h1><p>Create and manage races across your organizations.</p></div>
    <v-btn color="primary" prepend-icon="mdi-plus">Create event</v-btn>
  </div>
  <v-card border flat rounded="lg" class="mt-5">
    <v-card-text class="d-flex flex-wrap ga-3 align-center">
      <v-text-field v-model="search" prepend-inner-icon="mdi-magnify" label="Search events" density="comfortable" variant="outlined" hide-details class="search-field" />
      <v-select :items="statuses" label="All statuses" density="comfortable" variant="outlined" hide-details clearable class="filter-field" />
    </v-card-text>
    <v-divider />
    <v-card-text class="empty-state">
      <v-icon size="42" color="grey-lighten-1">mdi-calendar-blank-outline</v-icon>
      <div class="text-subtitle-1 font-weight-medium mt-3">No events found</div>
      <div class="text-body-2 text-medium-emphasis mt-1">Events you create will appear here.</div>
    </v-card-text>
  </v-card>
</template>
<script setup lang="ts">
import { ref } from 'vue'
const search = ref('')
const statuses = ['Draft', 'Published', 'RegistrationOpen', 'RegistrationClosed', 'Ready', 'Live', 'Completed', 'Cancelled']
</script>
