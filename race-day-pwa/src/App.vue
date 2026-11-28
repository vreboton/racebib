<template>
  <v-app>
    <v-main>
      <div class="station-shell">
        <header><small>RACEBIB</small><h1>Checkpoint Timing</h1></header>
        <p id="shell-status" role="status"></p>
        <p id="notice" role="status" hidden></p>
        <form id="login">
          <label for="email">Account email</label><input id="email" type="email" autocomplete="username" required>
          <label for="password">Password</label><input id="password" type="password" autocomplete="current-password" required>
          <button type="submit">Sign in</button>
        </form>
        <section id="station" hidden>
          <div class="actions"><button type="button" id="refresh">Check connection and assignments</button><button type="button" id="logout">Sign out</button></div>
          <p id="signin-required" hidden><button type="button" id="reauthenticate">Sign in to sync</button></p>
          <p id="clock" role="status"></p>
          <p id="storage-status" role="status"></p>
          <p id="sync-compatibility" class="warning" role="status" hidden>
            Web Locks is unavailable. Captures can still be saved on this device.
            Pending observations cannot synchronize until Web Locks is available in this same browser profile.
            If support was disabled, restore it and reload. Keep this profile and its site data.
          </p>
          <p id="assignment-freshness"></p>
          <p id="permit-status" role="status"></p>
          <button id="offline-permit" type="button">Get offline permit</button>
          <p id="station-time"></p>
          <label for="assigned-event">Assigned Event</label><select id="assigned-event"></select>
          <label for="assigned-checkpoint">Assigned Checkpoint</label><select id="assigned-checkpoint" disabled></select>
          <form id="capture">
            <label for="bib">Runner bib</label><input id="bib" ref="bib" autocomplete="off" autocapitalize="off" spellcheck="false" maxlength="32" aria-describedby="bib-hint">
            <p id="bib-hint">Use the keypad or keyboard. Press Enter to record.</p>
            <div class="keypad" aria-label="Numeric keypad">
              <button v-for="digit in digits" :key="digit" type="button" @click="editBib(digit)">{{ digit }}</button>
              <button type="button" @click="editBib('clear')">Clear</button>
              <button type="button" @click="editBib('backspace')">Backspace</button>
            </div>
            <button id="capture-button" class="record" type="submit">Record crossing</button>
          </form>
          <h2>Device captures</h2>
          <p id="queue-summary" role="status"></p>
          <button id="sync-now" type="button">Sync now</button>
          <ul id="captures" aria-live="polite"></ul>
        </section>
      </div>
    </v-main>
  </v-app>
</template>

<script setup lang="ts">
import { onMounted, onUnmounted, useTemplateRef } from 'vue'
import { startCheckpoint } from './checkpoint-station.js'

const digits = ['1', '2', '3', '4', '5', '6', '7', '8', '9', '0']
const bib = useTemplateRef<HTMLInputElement>('bib')
let stop: (() => void) | undefined
onMounted(() => { stop = startCheckpoint() })
onUnmounted(() => stop?.())

function editBib(action: string) {
  const input = bib.value
  if (!input) return
  const start = input.selectionStart ?? input.value.length
  const end = input.selectionEnd ?? start
  if (action === 'clear') input.value = ''
  else if (action === 'backspace') {
    const from = start === end ? Math.max(0, start - 1) : start
    input.setRangeText('', from, end, 'end')
  } else if (input.value.length - (end - start) < input.maxLength) input.setRangeText(action, start, end, 'end')
  input.focus()
}
</script>
