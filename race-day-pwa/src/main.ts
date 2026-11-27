import { createApp } from 'vue'
import { createVuetify } from 'vuetify'
import { VApp, VMain } from 'vuetify/components'
import 'vuetify/styles'
import './style.css'
import App from './App.vue'

createApp(App).use(createVuetify({ components: { VApp, VMain } })).mount('#app')

if (import.meta.env.PROD) {
  const status = document.getElementById('shell-status')!
  const workers = navigator.serviceWorker
  if (workers) {
    status.textContent = 'Preparing offline app.'
    function showAvailability() {
      if (workers.controller) status.textContent = 'App available offline on this device.'
    }
    const showFailure = () => {
      if (!workers.controller) status.textContent = 'Offline app setup failed. Keep this tab open.'
    }
    workers.addEventListener('controllerchange', showAvailability)
    showAvailability()
    void workers.register(import.meta.env.BASE_URL + 'service-worker.js', { updateViaCache: 'none' })
      .then(registration => {
        const installing = registration.installing
        installing?.addEventListener('statechange', () => {
          if (installing.state === 'redundant') showFailure()
        })
      })
      .catch(showFailure)
  } else {
    status.textContent = 'This browser cannot cache the offline app. Keep this tab open.'
  }
}
