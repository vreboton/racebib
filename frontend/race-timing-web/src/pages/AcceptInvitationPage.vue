<template>
  <v-container class="fill-height" style="max-width: 520px"><v-card class="w-100" rounded="lg"><v-card-title>RaceBib organizer invitation</v-card-title><v-card-text>
    <v-alert v-if="error" type="error" variant="tonal" class="mb-4">{{ error }}</v-alert>
    <v-alert v-if="success" type="success" variant="tonal" class="mb-4">{{ success }}</v-alert>
    <template v-if="auth.isAuthenticated && !showRegistration"><p>Accept this invitation using the email address associated with your signed-in RaceBib account ({{ auth.user?.email }}).</p><div class="d-flex ga-2 mt-4"><v-btn color="primary" :loading="busy" @click="accept">Accept invitation</v-btn><v-btn variant="text" :loading="busy" @click="reject">Decline</v-btn></div><v-divider class="my-4"/><v-btn variant="text" @click="switchAccount">Use a different account or register</v-btn></template>
    <template v-else><p>Create a RaceBib account with the invited email address to join the organizer.</p><v-text-field v-model="email" label="Invited email" type="email" autocomplete="email"/><v-text-field v-model="displayName" label="Display name" autocomplete="name"/><v-text-field v-model="password" label="Password" type="password" autocomplete="new-password" hint="At least 12 characters with uppercase, lowercase, number and symbol." persistent-hint/><v-btn class="mt-4" block color="primary" :loading="busy" @click="register">Create account and accept</v-btn><div class="text-center mt-4"><router-link :to="{ name: 'login', query: { redirect: route.fullPath } }">Already have an account? Sign in</router-link><div v-if="auth.isAuthenticated"><v-btn class="mt-2" variant="text" @click="switchAccount">Sign out first</v-btn></div></div></template>
  </v-card-text></v-card></v-container>
</template>
<script setup lang="ts">
import { ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { isAxiosError } from 'axios'
import { api } from '../services/api'
import { useAuthStore } from '../stores/auth'
const route = useRoute(), router = useRouter(), auth = useAuthStore()
const token = String(route.query.token || ''), email = ref(''), displayName = ref(''), password = ref(''), busy = ref(false), error = ref(''), success = ref(''), showRegistration = ref(false)
async function accept() { await submit('/invitations/accept', { token }) }
async function reject() { await submit('/invitations/reject', { token }, 'Invitation declined.') }
async function register() { const ok = await submit('/invitations/register', { token, email: email.value, displayName: displayName.value, password: password.value }, 'Account created. Sign in to continue.'); if (ok) await router.replace({ name: 'login' }) }
async function switchAccount() { await auth.logout(); showRegistration.value = true }
async function submit(path: string, body: object, done = 'Invitation accepted.') {
  error.value = ''; busy.value = true
  try { await api.post(path, body); success.value = done; if (path.endsWith('/accept')) await router.replace({ name: 'dashboard' }); return true }
  catch (e) { error.value = isAxiosError(e) ? e.response?.data?.title || e.response?.data?.message || e.message : 'Could not process this invitation.'; return false }
  finally { busy.value = false }
}
</script>
