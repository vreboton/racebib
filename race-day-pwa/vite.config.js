import { createHash } from 'node:crypto'
import { readFileSync } from 'node:fs'
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

function checkpointShell() {
  return {
    name: 'checkpoint-offline-shell',
    apply: 'build',
    generateBundle: { order: 'post', handler(_, bundle) {
      const publicFiles = ['manifest.webmanifest', 'icon-192.png', 'icon-512.png']
      const files = [...Object.keys(bundle), ...publicFiles].sort()
      const hash = createHash('sha256')
      for (const name of files) {
        const asset = bundle[name]
        hash.update(name).update(asset ? (asset.type === 'chunk' ? asset.code : asset.source) :
          readFileSync(new URL('./public/' + name, import.meta.url)))
      }
      const worker = readFileSync(new URL('./service-worker.js', import.meta.url), 'utf8')
      const version = hash.update(worker).digest('hex')
      this.emitFile({
        type: 'asset', fileName: 'service-worker.js',
        source: 'const SHELL_VERSION = ' + JSON.stringify(version) + '\nconst SHELL_FILES = ' +
          JSON.stringify(files) + '\n' + worker,
      })
    } },
  }
}

const apiProxy = { '/api': process.env.RACEBIB_API_ORIGIN || 'http://127.0.0.1:5080' }
export default defineConfig({
  plugins: [vue(), checkpointShell()],
  server: { port: 5174, proxy: apiProxy },
  preview: { port: 5174, proxy: apiProxy },
})
