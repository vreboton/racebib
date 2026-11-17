import { defineConfig } from 'vitest/config'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  plugins: [vue()],
  server: {
    host: '0.0.0.0',
    port: 5173,
    proxy: {
      '/api': {
        target: 'http://localhost:5080',
        changeOrigin: true,
      },
    },
  },
  test: {
    environment: 'jsdom',
    css: true,
    server: { deps: { inline: ['vuetify'] } },
    clearMocks: true,
    restoreMocks: true,
    setupFiles: ['./src/test/setup.ts'],
  },
})
