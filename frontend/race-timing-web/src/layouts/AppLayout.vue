<template>
  <v-layout class="app-shell">
    <v-navigation-drawer permanent color="grey-darken-4" width="256">
      <div class="brand-block">
        <div class="brand-mark">RB</div>
        <div><div class="brand-name">RaceBib</div><div class="brand-caption">RACE OPERATIONS</div></div>
      </div>
      <v-divider class="my-3" color="grey-darken-2" />
      <v-list nav density="comfortable" color="white">
        <v-list-subheader class="text-grey">WORKSPACE</v-list-subheader>
        <v-list-item prepend-icon="mdi-view-dashboard-outline" title="Dashboard" to="/" />
        <v-list-item prepend-icon="mdi-flag-outline" title="Events" to="/events" />
        <v-list-item prepend-icon="mdi-account-group-outline" title="Participants" to="/participants" />
        <v-list-item prepend-icon="mdi-map-marker-path" title="Courses" to="/courses" />
        <v-list-item prepend-icon="mdi-format-list-bulleted" title="Categories" to="/categories" />
        <v-list-subheader class="text-grey mt-4">ADMINISTRATION</v-list-subheader>
        <v-list-item prepend-icon="mdi-shield-account-outline" title="Users and Access" to="/users" />
        <v-list-item prepend-icon="mdi-cog-outline" title="System Settings" to="/settings" />
      </v-list>
      <template #append>
        <div class="drawer-footer"><v-icon size="small">mdi-shield-check-outline</v-icon><span>RaceBib Event Management</span></div>
      </template>
    </v-navigation-drawer>
    <v-main>
      <v-app-bar flat color="white" border="b">
        <v-app-bar-title class="text-subtitle-1 font-weight-medium">Race operations workspace</v-app-bar-title>
        <v-spacer />
        <v-btn icon="mdi-bell-outline" variant="text" />
        <v-avatar color="orange-lighten-5" size="34" class="mr-4"><span class="text-orange-darken-4 text-caption font-weight-bold">AD</span></v-avatar>
      </v-app-bar>
      <div class="page-container"><router-view /></div>
    </v-main>
  </v-layout>
</template>
