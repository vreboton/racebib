import { createApp } from 'vue'
import { createVuetify } from 'vuetify'
import { VApp, VMain } from 'vuetify/components'
import 'vuetify/styles'
import './style.css'
import App from './App.vue'

createApp(App).use(createVuetify({ components: { VApp, VMain } })).mount('#app')
