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
        <v-list-item prepend-icon="mdi-map-marker-path" title="Routes" to="/courses" />
        <v-list-item prepend-icon="mdi-format-list-bulleted" title="Categories" to="/categories" />
        <v-list-subheader class="text-grey mt-4">ADMINISTRATION</v-list-subheader>
        <v-list-item v-if="auth.canManageAnyMembers" prepend-icon="mdi-shield-account-outline" title="Organizer Members" to="/users" />
        <v-list-item v-if="auth.isPlatformAdministrator" prepend-icon="mdi-shield-crown-outline" title="Platform Administration" to="/platform" />
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
        <span class="text-body-2 text-medium-emphasis mr-3">{{ auth.user?.displayName || auth.user?.email }}</span>
        <v-menu>
          <template #activator="{ props }">
            <v-btn v-bind="props" icon variant="text" class="mr-3">
              <v-avatar color="orange-lighten-5" size="34">
                <span class="text-orange-darken-4 text-caption font-weight-bold">{{ initials }}</span>
              </v-avatar>
            </v-btn>
          </template>
          <v-list density="compact" min-width="190">
            <v-list-item :title="auth.user?.displayName || 'Account'" :subtitle="auth.user?.email ?? ''" />
            <v-divider />
            <v-list-item prepend-icon="mdi-logout" title="Sign out" @click="signOut" />
          </v-list>
        </v-menu>
      </v-app-bar>
      <div class="page-container"><router-view /></div>
    </v-main>
  </v-layout>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useRouter } from 'vue-router'
import { useAuthStore } from '../stores/auth'

const auth = useAuthStore()
const router = useRouter()
const initials = computed(() => {
  const label = auth.user?.displayName || auth.user?.email || 'RB'
  return label.split(/[\s@._-]+/).filter(Boolean).slice(0, 2).map(part => part[0]?.toUpperCase()).join('')
})

async function signOut() {
  await auth.logout()
  await router.replace({ name: 'login' })
}
</script>
