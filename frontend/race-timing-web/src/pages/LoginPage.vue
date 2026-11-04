<template>
  <div class="login-page">
    <v-card width="420" max-width="92vw" rounded="xl" border flat class="pa-7">
      <div class="d-flex align-center ga-3 mb-7">
        <div class="brand-mark">RB</div>
        <div>
          <div class="brand-name text-grey-darken-4">RaceBib</div>
          <div class="brand-caption text-grey">RACE OPERATIONS</div>
        </div>
      </div>

      <h1 class="text-h5 font-weight-bold">Welcome back</h1>
      <p class="text-body-2 text-medium-emphasis mt-1 mb-6">Sign in to manage your race events.</p>

      <v-alert v-if="errorMessage" type="error" variant="tonal" density="comfortable" class="mb-4">
        {{ errorMessage }}
      </v-alert>

      <v-form @submit.prevent="submit">
        <v-text-field
          v-model="email"
          label="Email address"
          type="email"
          autocomplete="username"
          variant="outlined"
          :disabled="loading"
          required
        />
        <v-text-field
          v-model="password"
          label="Password"
          type="password"
          autocomplete="current-password"
          variant="outlined"
          :disabled="loading"
          required
        />
        <v-btn block color="primary" size="large" type="submit" :loading="loading" :disabled="!canSubmit">
          Sign in
        </v-btn>
      </v-form>
      <p class="text-caption text-medium-emphasis mt-5 mb-0">Secure access to your RaceBib workspace.</p>
    </v-card>
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { isAxiosError } from 'axios'
import { useAuthStore } from '../stores/auth'

const auth = useAuthStore()
const route = useRoute()
const router = useRouter()
const email = ref('')
const password = ref('')
const loading = ref(false)
const errorMessage = ref('')
const canSubmit = computed(() => email.value.trim().length > 0 && password.value.length > 0)

async function submit() {
  if (!canSubmit.value || loading.value) return

  loading.value = true
  errorMessage.value = ''

  try {
    await auth.login(email.value.trim(), password.value)
    const redirect = typeof route.query.redirect === 'string' ? route.query.redirect : '/'
    await router.replace(redirect)
  } catch (error) {
    if (isAxiosError(error)) {
      errorMessage.value = error.response?.status === 401
        ? 'The email or password you entered is incorrect.'
        : (error.response?.data as { title?: string } | undefined)?.title || 'Unable to sign in. Please try again.'
    } else {
      errorMessage.value = 'Unable to sign in. Please try again.'
    }
  } finally {
    loading.value = false
  }
}
</script>
