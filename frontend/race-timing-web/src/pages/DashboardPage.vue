<template>
  <div class="page-heading">
    <div><div class="eyebrow">OVERVIEW</div><h1>Dashboard</h1><p>Manage your events and race operations from one place.</p></div>
    <v-btn color="primary" prepend-icon="mdi-plus" to="/events">Create event</v-btn>
  </div>
  <v-row class="mt-2">
    <v-col v-for="stat in stats" :key="stat.label" cols="12" sm="6" lg="3">
      <v-card rounded="lg" border flat class="stat-card">
        <div class="d-flex align-center justify-space-between"><span class="text-medium-emphasis text-body-2">{{ stat.label }}</span><v-icon color="primary">{{ stat.icon }}</v-icon></div>
        <div class="stat-value">{{ stat.value }}</div><div class="text-caption text-medium-emphasis">{{ stat.note }}</div>
      </v-card>
    </v-col>
  </v-row>
  <v-card rounded="lg" border flat class="mt-5">
    <v-card-title class="text-subtitle-1 font-weight-bold pa-5">Recent events</v-card-title>
    <v-divider />
    <v-card-text class="empty-state">
      <v-icon size="42" color="grey-lighten-1">mdi-flag-checkered</v-icon>
      <div class="text-subtitle-1 font-weight-medium mt-3">No events yet</div>
      <div class="text-body-2 text-medium-emphasis mt-1">Create your first event to start configuring categories, courses and participants.</div>
      <v-btn color="primary" variant="tonal" class="mt-4" to="/events">Create your first event</v-btn>
    </v-card-text>
  </v-card>
</template>

<script setup lang="ts">
const stats = [
  { label: 'Total events', value: '—', note: 'Across your organizers', icon: 'mdi-flag-outline' },
  { label: 'Upcoming events', value: '—', note: 'Scheduled in the future', icon: 'mdi-calendar-clock' },
  { label: 'Active events', value: '—', note: 'Currently in progress', icon: 'mdi-run' },
  { label: 'Registered runners', value: '—', note: 'All event registrations', icon: 'mdi-account-group-outline' },
]
</script>
