import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  plugins: [vue()],
  server: {
    port: 5174,
    proxy: { '/api': process.env.RACEBIB_API_ORIGIN || 'http://127.0.0.1:5080' },
  },
})
