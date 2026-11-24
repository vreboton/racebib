import { defineConfig } from 'vite'

export default defineConfig({
  server: {
    port: 5174,
    proxy: { '/api': process.env.RACEBIB_API_ORIGIN || 'http://127.0.0.1:5080' },
  },
})
