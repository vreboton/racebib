import { createApp } from 'vue'
import { createPinia } from 'pinia'
import { createVuetify } from 'vuetify'
import 'vuetify/styles'
import '@mdi/font/css/materialdesignicons.css'
import './styles/main.scss'
import App from './App.vue'
import router from './router'

const vuetify = createVuetify({
  theme: {
    defaultTheme: 'racebibLight',
    themes: {
      racebibLight: {
        dark: false,
        colors: {
          primary: '#c2410c',
          secondary: '#334155',
          surface: '#ffffff',
          background: '#f4f6f8',
          success: '#15803d',
          warning: '#d97706',
          error: '#b91c1c',
        },
      },
    },
  },
})

createApp(App).use(createPinia()).use(router).use(vuetify).mount('#app')
