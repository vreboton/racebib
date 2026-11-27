import { clockSample, clockWarning, createCapture, prepareSubmission } from './capture.js'
import { loadAccountCaptures, openCaptureStore, saveCapture } from './capture-store.js'

export function startCheckpoint() {
  const el = id => document.getElementById(id)
  const sessionKey = 'racebib.checkpoint.session'
  const deviceKey = 'racebib.checkpoint.device'
  const maximumPending = 200
  const probeLifetime = 45_000
  let session
  let assignments = []
  let assignmentsLoaded = false
  let rows = []
  let database
  let savingCaptures = 0
  const savingSelections = new Set()
  let storageReady = false
  let storageFailed = false
  let sample
  let lastProbe = -Infinity
  let connected = false
  let probing = false
  let deviceId

  function checkpointPath(assignment) {
    return `/events/${assignment.eventId}/timing/checkpoints/${assignment.checkpointId}`
  }

  function notice(message = '') {
    el('notice').textContent = message
    el('notice').hidden = !message
  }

  async function api(path, { body, key, operator = session } = {}) {
    const headers = { Accept: 'application/json' }
    if (operator) headers.Authorization = 'Bearer ' + operator.accessToken
    if (body) headers['Content-Type'] = 'application/json'
    if (key) headers['Idempotency-Key'] = key
    const response = await fetch('/api/v1' + path, {
      headers, cache: 'no-store', signal: AbortSignal.timeout(10_000),
      ...(body ? { method: 'POST', body: JSON.stringify(body) } : {}),
    })
    const payload = response.status === 204 ? null : await response.json().catch(() => null)
    if (!response.ok) {
      const error = new Error(payload?.title || payload?.message || `RaceBib returned HTTP ${response.status}.`)
      error.status = response.status
      error.code = payload?.code
      throw error
    }
    if (response.status !== 204 && payload === null) throw new Error('RaceBib did not return a receipt.')
    return payload
  }

  function storageFailure(operator, error) {
    if (session !== operator) return
    storageFailed = true
    el('storage-status').textContent = 'Capture storage failed. Reload before recording or retrying. ' + error.message
    render()
  }

  async function persist(operator, row) {
    try {
      await saveCapture(await database, row)
      return true
    } catch (error) {
      storageFailure(operator, error)
      return false
    }
  }

  async function setSession(operator) {
    storageReady = false
    storageFailed = false
    connected = false
    lastProbe = -Infinity
    sample = undefined
    probing = false
    assignments = []
    assignmentsLoaded = false
    renderAssignments('', '')
    session = operator
    rows = []
    el('login').hidden = Boolean(operator)
    el('station').hidden = !operator
    el('storage-status').textContent = 'Opening capture storage.'
    render()
    if (!operator) return
    try {
      database ??= openCaptureStore()
      const saved = await loadAccountCaptures(await database, operator.user.id, sessionStorage)
      if (session !== operator) return
      rows = saved
      storageReady = true
      el('storage-status').textContent = 'Captures are stored on this device. Clearing site data removes them. The browser may also reclaim storage.'
      render()
      void requestPersistence(operator)
    } catch (error) {
      storageFailure(operator, error)
    }
  }

  async function requestPersistence(operator) {
    try {
      const storage = navigator.storage
      const persistent = await storage?.persisted?.() || await storage?.persist?.()
      if (persistent && session === operator && !storageFailed) {
        el('storage-status').textContent = 'Persistent storage is active. Clearing site data or removing the browser profile still removes captures.'
      }
    } catch {
      // Browsers can deny the request. The storage warning remains visible.
    }
  }

  function selectedAssignment() {
    const eventId = el('assigned-event').value
    const checkpointId = el('assigned-checkpoint').value
    return assignments.find(assignment => assignment.eventId === eventId && assignment.checkpointId === checkpointId)
  }

  function renderCheckpoints(preferredCheckpoint = '') {
    const eventId = el('assigned-event').value
    const checkpoint = el('assigned-checkpoint')
    const stations = assignments.filter(assignment => assignment.eventId === eventId)
    checkpoint.replaceChildren(new Option('Choose checkpoint', ''), ...stations.map(assignment =>
      new Option(assignment.name + (assignment.entryOpen ? '' : ' (closed)'), assignment.checkpointId)))
    checkpoint.disabled = !eventId || !stations.length
    if (stations.some(assignment => assignment.checkpointId === preferredCheckpoint)) {
      checkpoint.value = preferredCheckpoint
    }
  }

  function renderAssignments(previousEvent, previousCheckpoint) {
    const eventSelect = el('assigned-event')
    const events = new Map(assignments.map(assignment => [assignment.eventId, assignment.eventName]))
    eventSelect.replaceChildren(new Option('Choose event', ''), ...Array.from(events, ([id, name]) => new Option(name, id)))
    eventSelect.disabled = !events.size
    if (events.has(previousEvent)) eventSelect.value = previousEvent
    renderCheckpoints(eventSelect.value === previousEvent ? previousCheckpoint : '')
  }

  const queueFull = () => rows.filter(row => !row.receipt && row.state !== 'Rejected').length + savingCaptures >= maximumPending
  const canCapture = () => storageReady && !storageFailed && navigator.onLine && connected && performance.now() - lastProbe < probeLifetime &&
    selectedAssignment()?.entryOpen === true && !queueFull()

  function formatStationTime(date, zone, includeDate = false) {
    try {
      return new Intl.DateTimeFormat(undefined, {
        ...(includeDate ? { dateStyle: 'medium' } : {}),
        timeStyle: 'medium',
        timeZone: zone,
      }).format(date)
    } catch {
      return date.toISOString()
    }
  }

  function receiptMessage(receipt, zone) {
    const diagnostic = receipt.diagnostic
    if (!diagnostic) return receipt.outcome.replace(/([a-z])([A-Z])/g, '$1 $2')
    const explanations = {
      observed_before_timing_window: 'Crossing is before the frozen window starts. Check the device clock and ask race control to verify the timing window.',
      observed_after_timing_window: 'Crossing is after the frozen window ends. Ask race control to check the frozen window.',
      observed_ahead_of_server_clock: 'Crossing is ahead of the server clock tolerance. Enable automatic date and time, then check the connection and device clock.',
    }
    const observed = formatStationTime(new Date(diagnostic.observedAtUtc), zone, true)
    const boundary = formatStationTime(new Date(diagnostic.boundaryAtUtc), zone, true)
    return (explanations[diagnostic.code] ?? 'Timing evidence needs review.') +
      ` Observed ${observed}; boundary ${boundary} (${zone}).`
  }

  function updateStationTime() {
    const date = new Date()
    const zone = selectedAssignment()?.timeZoneId
    const local = date.toLocaleTimeString()
    const eventTime = zone ? formatStationTime(date, zone) + ' · ' + zone : ''
    el('station-time').textContent = 'Device ' + local + (eventTime ? ' | Event ' + eventTime : '')
  }

  function updateStatus() {
    updateStationTime()
    el('capture-button').disabled = !canCapture()
    const status = !storageReady || storageFailed ? 'Capture storage is unavailable. Capture is paused.' : !navigator.onLine ? 'Offline. Capture is paused.' :
      !connected || performance.now() - lastProbe >= probeLifetime ? 'Connection check required. Capture is paused.' :
        queueFull() ? 'Capture queue is full. Retry pending captures to continue.' :
        selectedAssignment()?.entryOpen ? 'Online · Capture enabled' : 'Online · Select a checkpoint in a live event'
    const clock = clockWarning(sample)
    el('clock').textContent = status + (clock ? ' ' + clock : ' · Device clock checked')
    el('clock').classList.toggle('warning', !!clock || !connected)
  }

  async function probe(loadAssignments = false) {
    if (!session || !navigator.onLine || probing) return
    const operator = session
    probing = true
    try {
      const wall = Date.now()
      const start = performance.now()
      const result = await api('/me/timing-clock', { operator })
      const elapsed = performance.now() - start
      const next = clockSample(wall, Date.now(), elapsed, result.serverUtc)
      const available = loadAssignments ? await api('/me/timing-assignments', { operator }) : null
      if (session !== operator) return
      sample = next
      lastProbe = performance.now()
      connected = true
      if (available) {
        const previousEvent = el('assigned-event').value
        const previousCheckpoint = el('assigned-checkpoint').value
        assignments = available.filter(assignment => assignment.eventStatus !== 'Completed')
        const single = !assignmentsLoaded && assignments.length === 1 ? assignments[0] : null
        renderAssignments(single?.eventId ?? previousEvent, single?.checkpointId ?? previousCheckpoint)
        assignmentsLoaded = true
        notice(assignments.length ? '' : 'No checkpoint assignments. Ask race control for access.')
      }
    } catch (error) {
      if (session === operator) {
        connected = false
        sample = undefined
        notice(error.status === 401 ? 'Session expired. Sign in again to continue pending captures.' : error.message)
        if (error.status === 401) expireSession()
      }
    } finally {
      if (session === operator) { probing = false; updateStatus() }
    }
  }

  function expireSession() {
    sessionStorage.removeItem(sessionKey)
    void setSession(undefined)
  }

  function recordFailure(operator, row, error, phase) {
    const uncertain = !error.status || error.status >= 500 || error.code === 'timing_write_conflict' ||
      error.status === 401 || error.status === 429
    row.state = uncertain ? 'Retry needed' : 'Rejected'
    row.message = (uncertain && phase === 'submit' ? 'Receipt uncertain. Retry the same capture. ' : '') + error.message
    if (phase === 'submit' && row.body && error.status === 403) {
      row.state = 'Retry needed'
      row.message = 'Access denied. Race control must verify whether this capture was recorded.'
    }
    row.lastSyncError = row.message
    if (session === operator && (!error.status || error.status >= 500)) connected = false
    if (session === operator && error.status === 401) {
      expireSession()
      notice('Session expired. Sign in again to continue pending captures.')
    }
  }

  async function lookup(operator, row, alreadySaved = false) {
    row.state = 'Looking up bib'
    row.message = ''
    render()
    if (!alreadySaved && !await persist(operator, row)) return
    try {
      const result = await api(checkpointPath(row.assignment) + '/registrations?bib=' + encodeURIComponent(row.bib), { operator })
      if (session !== operator) {
        row.state = 'Retry needed'
        await saveAndRender(operator, row)
        return
      }
      row.registration = { registrationId: result.registrationId, raceId: result.raceId, bibNumber: result.bibNumber }
      if (!Array.isArray(result.occurrences)) throw new Error('RaceBib did not return checkpoint visits.')
      row.occurrences = result.occurrences
    } catch (error) {
      recordFailure(operator, row, error, 'lookup')
      await saveAndRender(operator, row)
      return
    }
    if (row.occurrences.length === 1) {
      await submit(operator, row, row.occurrences[0].occurrenceId)
      return
    }
    row.state = 'Choose visit'
    await saveAndRender(operator, row)
  }

  async function submit(operator, row, occurrenceId) {
    if (session !== operator || storageFailed || row.state === 'Submitting' || row.receipt) return
    if (!navigator.onLine) {
      row.state = 'Retry needed'
      row.message = 'Reconnect before retrying this capture.'
      await saveAndRender(operator, row)
      return
    }
    row.state = 'Submitting'
    row.message = ''
    render()
    try {
      const body = prepareSubmission(row, row.registration, occurrenceId)
      row.selectedOccurrenceId = body.occurrenceId
      if (!await persist(operator, row)) return
      if (session !== operator) {
        row.state = 'Retry needed'
        return
      }
      const receipt = await api(checkpointPath(row.assignment) + '/observations', { operator, body, key: row.key })
      if (receipt?.observationId !== row.observationId) throw new Error('RaceBib returned an unexpected receipt.')
      row.receipt = receipt
      row.lastSyncError = null
      row.state = receipt.outcome === 'Accepted' ? 'Recorded' : 'Recorded · needs review'
      row.message = receipt.outcome === 'Accepted' ? '' : receiptMessage(receipt, row.assignment.timeZoneId ?? 'UTC')
    } catch (error) {
      recordFailure(operator, row, error, 'submit')
    }
    await saveAndRender(operator, row)
  }

  async function saveAndRender(operator, row) {
    if (await persist(operator, row) && session === operator) render()
  }

  function appendButton(parent, label, action) {
    const button = document.createElement('button')
    button.type = 'button'
    button.textContent = label
    button.disabled = !navigator.onLine || storageFailed
    button.addEventListener('click', action)
    parent.append(button)
    return button
  }

  function render() {
    updateStatus()
    const root = el('captures')
    root.replaceChildren()
    if (!rows.length) {
      const item = document.createElement('li')
      item.textContent = 'No captures stored for this account.'
      root.append(item)
      return
    }
    const operator = session
    const recent = rows.filter(row => row.receipt).slice(0, 30)
    const pending = rows.filter(row => !row.receipt)
    for (const row of [...pending, ...recent]) {
      const item = document.createElement('li')
      const title = document.createElement('strong')
      title.textContent = `Bib ${row.bib} · ${row.state}`
      const details = document.createElement('p')
      const zone = row.assignment.timeZoneId ?? 'UTC'
      const localObserved = formatStationTime(new Date(row.observedAtUtc), zone, true)
      details.textContent = `${row.assignment.eventName} · ${row.assignment.name} · ${localObserved} (${zone})`
      details.title = row.observedAtUtc
      item.append(title, details)
      if (row.message) {
        const message = document.createElement('p')
        message.textContent = row.message
        item.append(message)
      }
      if (row.state === 'Choose visit') {
        const select = document.createElement('select')
        select.setAttribute('aria-label', `Visit for bib ${row.bib}`)
        select.append(new Option('Choose visit', ''), ...row.occurrences.map(visit =>
          new Option(`${visit.sequence} · ${visit.name} · ${visit.distanceKm} km`, visit.occurrenceId)))
        item.append(select)
        const save = appendButton(item, 'Submit selected visit', () => { if (select.value) void submit(operator, row, select.value) })
        select.value = row.selectedOccurrenceId || ''
        const saving = savingSelections.has(row)
        save.disabled = !select.value || !navigator.onLine || storageFailed || saving
        select.disabled = storageFailed || saving
        const review = appendButton(item, 'Send for race-control review', () => void submit(operator, row, null))
        review.disabled ||= saving
        select.addEventListener('change', async () => {
          savingSelections.add(row)
          row.selectedOccurrenceId = select.value || null
          select.disabled = save.disabled = review.disabled = true
          await persist(operator, row)
          savingSelections.delete(row)
          if (session === operator) render()
        })
      }
      if (row.state === 'Retry needed')
        appendButton(item, 'Retry capture', () => {
          row.retryCount += 1
          if (row.body) void submit(operator, row, null)
          else void lookup(operator, row)
        })
      root.append(item)
    }
  }

  el('login').addEventListener('submit', async event => {
    event.preventDefault()
    notice()
    el('login').querySelector('button').disabled = true
    try {
      const operator = await api('/auth/login', { body: { email: el('email').value.trim(), password: el('password').value }, operator: null })
      sessionStorage.setItem(sessionKey, JSON.stringify(operator))
      await setSession(operator)
      await probe(true)
    } catch (error) { notice(error.message) }
    finally { el('password').value = ''; el('login').querySelector('button').disabled = false }
  })
  el('logout').addEventListener('click', async () => {
    const operator = session
    expireSession()
    notice('Signed out. Pending captures stay on this device for your account.')
    try { await api('/auth/logout', { body: {}, operator }) } catch { /* Local session is already cleared. */ }
  })
  el('capture').addEventListener('submit', async event => {
    event.preventDefault()
    if (!canCapture()) { notice('Capture requires working storage, a live checkpoint and a recent connection check.'); return }
    const operator = session
    const bibInput = el('bib')
    const entered = bibInput.value
    let row
    try {
      row = {
        ...createCapture(selectedAssignment(), entered.trim(), deviceId, { online: true }),
        accountId: operator.user.id,
        clockQuality: sample ? { ...sample, ageMs: performance.now() - lastProbe, warning: clockWarning(sample) } : null,
        retryCount: 0,
        lastSyncError: null,
      }
    } catch (error) {
      notice(error.message)
      return
    }
    savingCaptures += 1
    updateStatus()
    const saved = await persist(operator, row)
    savingCaptures -= 1
    if (session !== operator) return
    if (!saved) { render(); return }
    rows.unshift(row)
    if (bibInput.value === entered) bibInput.value = ''
    bibInput.focus()
    notice()
    void lookup(operator, row, true)
  })
  el('refresh').addEventListener('click', () => void probe(true))
  el('assigned-event').addEventListener('change', () => {
    renderCheckpoints()
    updateStatus()
  })
  el('assigned-checkpoint').addEventListener('change', updateStatus)
  const onVisible = () => { if (document.visibilityState === 'visible') void probe(true) }
  document.addEventListener('visibilitychange', onVisible)
  const onOffline = () => { connected = false; render() }
  window.addEventListener('offline', onOffline)
  const onOnline = () => { render(); void probe(true) }
  window.addEventListener('online', onOnline)
  const onBeforeUnload = event => {
    if (savingCaptures > 0) event.preventDefault()
  }
  window.addEventListener('beforeunload', onBeforeUnload)
  const probeInterval = setInterval(() => { updateStatus(); void probe(true) }, 15_000)
  const clockInterval = setInterval(updateStationTime, 1000)

  try {
    deviceId = localStorage.getItem(deviceKey) || crypto.randomUUID()
    localStorage.setItem(deviceKey, deviceId)
    void setSession(JSON.parse(sessionStorage.getItem(sessionKey) || 'null') || undefined).then(() => {
      if (session) void probe(true)
    })
  } catch (error) {
    notice('This device could not initialize secure capture storage. ' + error.message)
    el('login').querySelector('button').disabled = true
  }
  return () => {
    clearInterval(probeInterval)
    clearInterval(clockInterval)
    document.removeEventListener('visibilitychange', onVisible)
    window.removeEventListener('offline', onOffline)
    window.removeEventListener('online', onOnline)
    window.removeEventListener('beforeunload', onBeforeUnload)
  }
}
