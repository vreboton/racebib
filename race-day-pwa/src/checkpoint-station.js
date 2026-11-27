import { clockSample, clockWarning, createCapture, prepareSubmission } from './capture.js'
import { loadAccountCaptures, openCaptureStore, readCapture, saveCapture } from './capture-store.js'
import { captureLockName, retryDelay, validOfflineSnapshot } from './offline-policy.js'

export function startCheckpoint() {
  const el = id => document.getElementById(id)
  const sessionKey = 'racebib.checkpoint.session'
  const deviceKey = 'racebib.checkpoint.device'
  const offlineAccountKey = 'racebib.checkpoint.offline-account'
  const snapshotKey = accountId => 'racebib.checkpoint.snapshot.' + accountId
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
  let snapshot
  let syncTimer
  const processing = new Set()
  let forceRequested = false
  let draining = false

  function onlineReady() {
    return Boolean(session?.accessToken && navigator.onLine && connected &&
      performance.now() - lastProbe < probeLifetime)
  }

  function rememberStation() {
    snapshot.eventId = el('assigned-event').value
    snapshot.checkpointId = el('assigned-checkpoint').value
    persistSnapshot()
  }

  function persistSnapshot() {
    try {
      localStorage.setItem(snapshotKey(session.user.id), JSON.stringify(snapshot))
      localStorage.setItem(offlineAccountKey, session.user.id)
    } catch (error) {
      storageFailure(session, error)
    }
  }

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
    el('bib').value = ''
    connected = false
    lastProbe = -Infinity
    sample = undefined
    probing = false
    assignments = []
    assignmentsLoaded = false
    snapshot = undefined
    forceRequested = false
    clearTimeout(syncTimer)
    renderAssignments('', '')
    session = operator
    rows = []
    el('login').hidden = Boolean(operator)
    el('station').hidden = !operator
    el('signin-required').hidden = !operator || Boolean(operator.accessToken)
    el('storage-status').textContent = 'Opening capture storage.'
    render()
    if (!operator) return
    try {
      database ??= openCaptureStore()
      const saved = await loadAccountCaptures(await database, operator.user.id, sessionStorage)
      if (session !== operator) return
      rows = saved
      snapshot = JSON.parse(localStorage.getItem(snapshotKey(operator.user.id)) || 'null')
      if (snapshot) {
        assignments = snapshot.assignments.filter(assignment => assignment.eventStatus !== 'Completed')
        assignmentsLoaded = true
        renderAssignments(snapshot.eventId, snapshot.checkpointId)
      }
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
  const offlineAllowed = () => validOfflineSnapshot(snapshot)
  const canCapture = () => storageReady && !storageFailed && (onlineReady() || offlineAllowed()) &&
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
    const online = onlineReady()
    const connection = online ? 'Online' : navigator.onLine && session?.accessToken ? 'Reconnecting' : 'Offline'
    let detail = canCapture() ? 'Capture enabled' : 'Capture paused'
    if (queueFull()) detail = 'Queue full. Sync pending captures to continue.'
    else if (storageReady && !storageFailed && !online && !offlineAllowed()) detail = 'Connect and sign in to renew offline access.'
    const quality = online ? clockWarning(sample) || 'Device clock checked.' :
      'Clock check is stale or unavailable. Captures use the current device time.'
    el('clock').textContent = connection + ' | ' + detail + '. ' + quality
    el('clock').classList.toggle('warning', !online || Boolean(clockWarning(sample)))
    el('assignment-freshness').textContent = snapshot
      ? (online ? 'Assignments checked ' : 'Cached assignments checked ') + new Date(snapshot.verifiedAt).toLocaleString()
      : 'No verified assignments on this device.'
    el('reauthenticate').disabled = !navigator.onLine
    el('sync-now').disabled = !session?.accessToken || !navigator.onLine || !storageReady || storageFailed

  }

  async function probe(loadAssignments = false, forceSync = false) {
    if (!session?.accessToken || !navigator.onLine) return
    forceRequested ||= forceSync
    if (probing) return
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
        snapshot = { assignments, verifiedAt: Date.now(), eventId: '', checkpointId: '' }
        rememberStation()
        if (!assignments.length) notice('No checkpoint assignments. Ask race control for access.')
      }
    } catch (error) {
      if (session === operator) {
        connected = false
        sample = undefined
        notice(error.status === 401 ? 'Session expired. Sign in again to continue pending captures.' : error.message)
        if (error.status === 401) requireAuthentication(operator)
      }
    } finally {
      if (session === operator) {
        probing = false
        updateStatus()
        if (onlineReady()) {
          const force = forceRequested
          forceRequested = false
          void drain(force)
        }
      }
    }
  }

  function expireSession() {
    sessionStorage.removeItem(sessionKey)
    void setSession(undefined)
  }

  function requireAuthentication(operator) {
    sessionStorage.removeItem(sessionKey)
    delete operator.accessToken
    connected = false
    el('signin-required').hidden = false
    notice('Session expired. Sign in again to synchronize pending captures.')
    updateStatus()
  }

  function recordFailure(operator, row, error, phase) {
    const uncertain = !error.status || error.status >= 500 || error.code === 'timing_write_conflict' ||
      error.status === 401 || error.status === 429
    row.nextAttemptAt = uncertain ? Date.now() + retryDelay(row.retryCount + 1) : null
    row.state = uncertain ? 'Retry needed' : 'Rejected'
    row.message = (uncertain && phase === 'submit' ? 'Receipt uncertain. Retry the same capture. ' : '') + error.message
    if (phase === 'submit' && row.body && error.status === 403) {
      row.state = 'Rejected'
      row.message = 'Access denied. Race control must verify whether this capture was recorded.'
    }
    if (error.status === 403 && phase === 'lookup') {
      row.message = 'Checkpoint access changed. Ask Race Control to review this locally saved capture.'
    }
    if (error.status === 404 && phase === 'lookup') {
      row.message = 'Bib was not found at this checkpoint. Ask Race Control to reconcile the locally saved capture.'
    }
    row.lastSyncError = row.message
    if (session === operator && !navigator.onLine) connected = false
    if (session === operator && error.status === 401) {
      row.nextAttemptAt = null
      requireAuthentication(operator)
    }
  }

  async function lookup(operator, row) {
    row.state = 'Looking up bib'
    row.message = ''
    render()
    if (!await persist(operator, row) || session !== operator) return
    if (assignmentsLoaded && !assignments.some(assignment => assignment.eventId === row.assignment.eventId &&
      assignment.checkpointId === row.assignment.checkpointId &&
      assignment.generation === row.assignment.generation && assignment.entryOpen)) {
      row.state = 'Rejected'
      row.lastSyncError = row.message = 'The event is closed or this checkpoint assignment changed. Ask Race Control to review this locally saved capture.'
      await saveAndRender(operator, row)
      return
    }
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
    if (row.capturedOffline && !row.selectedOccurrenceId) {
      await submit(operator, row, null)
      return
    }
    row.state = 'Choose visit'
    await saveAndRender(operator, row)
  }

  async function submit(operator, row, occurrenceId) {
    if (session !== operator || storageFailed || row.receipt) return
    if (!onlineReady()) {
      row.state = 'Retry needed'
      row.lastSyncError = row.message = 'Reconnect before retrying this capture.'
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
      if (receipt?.observationId !== row.observationId || typeof receipt.outcome !== 'string') {
        throw new Error('RaceBib returned an unexpected receipt.')
      }
      row.receipt = receipt
      row.lastSyncError = null
      row.nextAttemptAt = null
      row.syncedAt = Date.now()
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

  async function processCapture(operator, candidate, { force = false, visit } = {}) {
    if (!onlineReady() || session !== operator || storageFailed || processing.has(candidate.observationId)) return
    processing.add(candidate.observationId)
    try {
      const process = async lock => {
        if (!lock || !onlineReady() || session !== operator) return
        const row = await readCapture(await database, operator.user.id, candidate.observationId)
        if (!row || row.receipt || row.state === 'Rejected' ||
          (row.state === 'Choose visit' && visit === undefined) ||
          (!force && row.nextAttemptAt > Date.now())) return

        const index = rows.findIndex(entry => entry.observationId === row.observationId)
        if (index < 0) rows.unshift(row)
        else rows[index] = row
        if (row.state !== 'Saved locally' && row.state !== 'Choose visit') row.retryCount += 1
        if (row.body || visit !== undefined) await submit(operator, row, visit ?? null)
        else await lookup(operator, row)
      }
      await navigator.locks.request(captureLockName(candidate), { ifAvailable: true }, process)
    } catch (error) {
      storageFailure(operator, error)
    } finally {
      processing.delete(candidate.observationId)
      scheduleSync()
    }
  }

  function scheduleSync() {
    clearTimeout(syncTimer)
    if (!onlineReady() || storageFailed) return
    const due = rows.filter(row => row.state === 'Retry needed' && row.nextAttemptAt > Date.now())
      .map(row => row.nextAttemptAt)
    if (due.length) syncTimer = setTimeout(() => void drain(), Math.min(...due) - Date.now())
  }

  async function drain(force = false) {
    if (draining) {
      forceRequested ||= force
      return
    }
    if (!onlineReady() || !storageReady || storageFailed) return
    const operator = session
    draining = true
    try {
      const saved = await loadAccountCaptures(await database, operator.user.id, sessionStorage)
      if (session !== operator) return
      for (const row of saved.reverse()) {
        if (session !== operator || !onlineReady()) break
        if (!processing.has(row.observationId) && !savingSelections.has(rows.find(entry => entry.observationId === row.observationId))) {
          const index = rows.findIndex(entry => entry.observationId === row.observationId)
          if (index < 0) rows.unshift(row)
          else if (row.revision >= rows[index].revision) rows[index] = row
        }
        if (['Saved locally', 'Retry needed'].includes(row.state)) await processCapture(operator, row, { force })
      }
      if (session === operator) render()
    } catch (error) {
      storageFailure(operator, error)
    } finally {
      draining = false
      if (forceRequested && onlineReady()) {
        forceRequested = false
        void drain(true)
      } else scheduleSync()
    }
  }

  function appendButton(parent, label, action) {
    const button = document.createElement('button')
    button.type = 'button'
    button.textContent = label
    button.disabled = !onlineReady() || storageFailed
    button.addEventListener('click', action)
    parent.append(button)
    return button
  }

  function render() {
    updateStatus()
    const root = el('captures')
    root.replaceChildren()
    const recorded = rows.filter(row => row.receipt)
    const attention = rows.filter(row => ['Rejected', 'Choose visit'].includes(row.state) ||
      (row.receipt && row.receipt.outcome !== 'Accepted')).length
    const lastSync = recorded.reduce((latest, row) => Math.max(latest, row.syncedAt || 0), 0)
    el('queue-summary').textContent = rows.length - recorded.length + ' pending | ' +
      recorded.length + ' recorded | ' + attention + ' require attention' +
      (lastSync ? ' | Last synchronized ' + new Date(lastSync).toLocaleString() : '')

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
      const labels = { Rejected: 'Requires operator action', 'Looking up bib': 'Awaiting bib lookup',
        Submitting: 'Synchronizing', 'Choose visit': 'Choose visit to continue' }
      title.textContent = `Bib ${row.bib} · ${labels[row.state] ?? row.state}`
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
      if (row.state === 'Rejected' || row.state === 'Retry needed' ||
        (row.receipt && row.receipt.outcome !== 'Accepted')) {
        const evidence = document.createElement('details')
        const summary = document.createElement('summary')
        summary.textContent = 'Capture details for Race Control'
        const identifiers = document.createElement('p')
        identifiers.textContent = row.observedAtUtc + ' | Observation ' + row.observationId
        evidence.append(summary, identifiers)
        item.append(evidence)
      }
      if (row.state === 'Choose visit') {
        const select = document.createElement('select')
        select.setAttribute('aria-label', `Visit for bib ${row.bib}`)
        select.append(new Option('Choose visit', ''), ...row.occurrences.map(visit =>
          new Option(`${visit.sequence} · ${visit.name} · ${visit.distanceKm} km`, visit.occurrenceId)))
        item.append(select)
        const save = appendButton(item, 'Submit selected visit', () => { if (select.value) void processCapture(operator, row, { visit: select.value }) })
        select.value = row.selectedOccurrenceId || ''
        const saving = savingSelections.has(row)
        save.disabled = !select.value || !onlineReady() || storageFailed || saving
        select.disabled = storageFailed || saving
        const review = appendButton(item, 'Send for race-control review', () => void processCapture(operator, row, { visit: null }))
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
          void processCapture(operator, row, { force: true })
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
      if (session && session.user.id !== operator.user.id && !confirmLeaving()) return
      localStorage.setItem(offlineAccountKey, operator.user.id)
      sessionStorage.setItem(sessionKey, JSON.stringify(operator))
      await setSession(operator)
      await probe(true)
    } catch (error) { notice(error.message) }
    finally { el('password').value = ''; el('login').querySelector('button').disabled = false }
  })
  function confirmLeaving() {
    return !savingCaptures && (!rows.some(row => !row.receipt) ||
      window.confirm('Unsynchronized captures will stay on this device. Continue signing out?'))
  }
  el('reauthenticate').addEventListener('click', () => {
    el('login').hidden = false
    el('email').focus()
  })
  el('logout').addEventListener('click', async () => {
    if (!confirmLeaving()) return
    const operator = session
    localStorage.removeItem(offlineAccountKey)
    expireSession()
    notice('Signed out. Pending captures stay on this device for your account.')
    if (!operator?.accessToken) return
    try { await api('/auth/logout', { body: {}, operator }) } catch { /* Local session is already cleared. */ }
  })
  el('capture').addEventListener('submit', async event => {
    event.preventDefault()
    if (!canCapture()) { notice('Capture is paused. Check storage, checkpoint selection and offline access.'); return }
    const operator = session
    const bibInput = el('bib')
    const entered = bibInput.value
    let row
    try {
      row = {
        ...createCapture(selectedAssignment(), entered.trim(), deviceId),
        capturedOffline: !onlineReady(),
        accountId: operator.user.id,
        clockQuality: sample ? { ...sample, ageMs: performance.now() - lastProbe, warning: clockWarning(sample), stale: !onlineReady() } : null,
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
    render()
    if (onlineReady()) void processCapture(operator, row)
  })
  el('sync-now').addEventListener('click', () => void probe(true, true))
  el('refresh').addEventListener('click', () => void probe(true))
  el('assigned-event').addEventListener('change', () => {
    renderCheckpoints()
    if (snapshot) rememberStation()
    updateStatus()
  })
  el('assigned-checkpoint').addEventListener('change', () => {
    if (snapshot) rememberStation()
    updateStatus()
  })
  const onVisible = () => { if (document.visibilityState === 'visible') void probe(true) }
  document.addEventListener('visibilitychange', onVisible)
  const onOffline = () => { connected = false; render() }
  window.addEventListener('offline', onOffline)
  const onOnline = () => { render(); void probe(true) }
  window.addEventListener('online', onOnline)
  const onAccountChange = event => {
    if (event.key !== offlineAccountKey || !session || event.newValue === session.user.id) return
    expireSession()
    notice('The checkpoint account changed in another tab. Sign in to continue.')
  }
  window.addEventListener('storage', onAccountChange)
  const onBeforeUnload = event => {
    if (savingCaptures > 0) event.preventDefault()
  }
  window.addEventListener('beforeunload', onBeforeUnload)
  const probeInterval = setInterval(() => { updateStatus(); void probe(true) }, 15_000)
  const clockInterval = setInterval(updateStationTime, 1000)

  try {
    deviceId = localStorage.getItem(deviceKey) || crypto.randomUUID()
    localStorage.setItem(deviceKey, deviceId)
    const cachedAccount = localStorage.getItem(offlineAccountKey)
    const authenticated = JSON.parse(sessionStorage.getItem(sessionKey) || 'null')
    const operator = authenticated && (!cachedAccount || cachedAccount === authenticated.user.id) ? authenticated :
      cachedAccount ? { user: { id: cachedAccount } } : undefined
    void setSession(operator).then(() => {
      if (session) void probe(true)
    })
  } catch (error) {
    notice('This device could not initialize secure capture storage. ' + error.message)
    el('login').querySelector('button').disabled = true
  }
  return () => {
    clearTimeout(syncTimer)
    clearInterval(probeInterval)
    clearInterval(clockInterval)
    document.removeEventListener('visibilitychange', onVisible)
    window.removeEventListener('offline', onOffline)
    window.removeEventListener('online', onOnline)
    window.removeEventListener('storage', onAccountChange)
    window.removeEventListener('beforeunload', onBeforeUnload)
  }
}
