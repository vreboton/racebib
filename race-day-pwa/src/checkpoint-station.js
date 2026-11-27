import { clockSample, clockWarning, createCapture, loadCaptures, prepareSubmission } from './capture.js'

export function startCheckpoint() {
  const el = id => document.getElementById(id)
  const sessionKey = 'racebib.checkpoint.session'
  const deviceKey = 'racebib.checkpoint.device'
  const maximumPending = 200
  const probeLifetime = 45_000
  let session
  let assignments = []
  let rows = []
  const sessionRows = new Map()
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

  function storageKey(operator) {
    return 'racebib.checkpoint.captures.' + operator.user.id
  }

  function persist(operator) {
    const pending = operator.rows.filter(row => !row.receipt)
    const recent = operator.rows.filter(row => row.receipt).slice(0, 30)
    sessionStorage.setItem(storageKey(operator), JSON.stringify([...pending, ...recent]))
  }

  function setSession(operator) {
    connected = false
    lastProbe = -Infinity
    sample = undefined
    probing = false
    assignments = []
    el('assignment').replaceChildren()
    session = operator
    rows = operator ? sessionRows.get(operator.user.id) ?? loadCaptures(sessionStorage, storageKey(operator)) : []
    if (operator) {
      operator.rows = rows
      sessionRows.set(operator.user.id, rows)
    }
    el('login').hidden = Boolean(operator)
    el('station').hidden = !operator
    render()
  }

  function selectedAssignment() {
    return assignments.find(assignment => assignmentKey(assignment) === el('assignment').value)
  }
  const assignmentKey = value => value.eventId + '/' + value.checkpointId
  const canCapture = () => navigator.onLine && connected && performance.now() - lastProbe < probeLifetime &&
    selectedAssignment()?.entryOpen === true && rows.filter(row => !row.receipt).length < maximumPending

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
    const status = !navigator.onLine ? 'Offline. Capture is paused.' :
      !connected || performance.now() - lastProbe >= probeLifetime ? 'Connection check required. Capture is paused.' :
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
        const prior = el('assignment').value
        assignments = available
        el('assignment').replaceChildren(new Option('Choose checkpoint', ''), ...assignments.map(assignment =>
          new Option(`${assignment.eventName} · ${assignment.name}${assignment.entryOpen ? '' : ' (closed)'}`, assignmentKey(assignment))))
        if (assignments.some(assignment => assignmentKey(assignment) === prior)) el('assignment').value = prior
        else if (assignments.length === 1) el('assignment').selectedIndex = 1
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
    setSession(undefined)
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
    if (session === operator && (!error.status || error.status >= 500)) connected = false
    if (session === operator && error.status === 401) {
      expireSession()
      notice('Session expired. Sign in again to continue pending captures.')
    }
  }

  async function lookup(operator, row) {
    row.state = 'Looking up bib'
    row.message = ''
    render()
    try {
      persist(operator)
      const result = await api(checkpointPath(row.assignment) + '/registrations?bib=' + encodeURIComponent(row.bib), { operator })
      if (session !== operator) {
        row.state = 'Retry needed'
        return
      }
      row.registration = { registrationId: result.registrationId, raceId: result.raceId, bibNumber: result.bibNumber }
      row.occurrences = result.occurrences
      if (row.occurrences.length === 1) return await submit(operator, row, row.occurrences[0].occurrenceId)
      row.state = 'Choose visit'
    } catch (error) {
      recordFailure(operator, row, error, 'lookup')
    } finally {
      saveAndRender(operator)
    }
  }

  async function submit(operator, row, occurrenceId) {
    if (row.state === 'Submitting' || row.receipt) return
    if (session !== operator || !navigator.onLine) {
      row.state = 'Retry needed'
      row.message = 'Reconnect before retrying this capture.'
      saveAndRender(operator)
      return
    }
    row.state = 'Submitting'
    row.message = ''
    render()
    try {
      const body = prepareSubmission(row, row.registration, occurrenceId)
      persist(operator)
      const receipt = await api(checkpointPath(row.assignment) + '/observations', { operator, body, key: row.key })
      if (receipt?.observationId !== row.observationId) throw new Error('RaceBib returned an unexpected receipt.')
      row.receipt = receipt
      row.state = receipt.outcome === 'Accepted' ? 'Recorded' : 'Recorded · needs review'
      row.message = receipt.outcome === 'Accepted' ? '' : receiptMessage(receipt, row.assignment.timeZoneId ?? 'UTC')
    } catch (error) {
      recordFailure(operator, row, error, 'submit')
    } finally {
      saveAndRender(operator)
    }
  }

  function saveAndRender(operator) {
    try { persist(operator) } catch {
      if (session === operator) notice('Capture storage is unavailable. Keep this tab open to retain pending items.')
    }
    if (session === operator) render()
  }

  function appendButton(parent, label, action) {
    const button = document.createElement('button')
    button.type = 'button'
    button.textContent = label
    button.disabled = !navigator.onLine
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
      item.textContent = 'No captures in this session.'
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
        save.disabled = !select.value || !navigator.onLine
        select.addEventListener('change', () => {
          row.selectedOccurrenceId = select.value
          save.disabled = !select.value || !navigator.onLine
        })
        appendButton(item, 'Send for race-control review', () => void submit(operator, row, null))
      }
      if (row.state === 'Retry needed')
        appendButton(item, 'Retry capture', () => { if (row.body) void submit(operator, row, null); else void lookup(operator, row) })
      if (row.state === 'Rejected') {
        appendButton(item, 'Dismiss rejected capture', () => {
          const index = rows.indexOf(row)
          if (index >= 0) rows.splice(index, 1)
          saveAndRender(operator)
        })
      }
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
      setSession(operator)
      await probe(true)
    } catch (error) { notice(error.message) }
    finally { el('password').value = ''; el('login').querySelector('button').disabled = false }
  })
  el('logout').addEventListener('click', async () => {
    const operator = session
    expireSession()
    notice('Signed out. Pending captures stay in this tab for your account.')
    try { await api('/auth/logout', { body: {}, operator }) } catch { /* Local session is already cleared. */ }
  })
  el('capture').addEventListener('submit', event => {
    event.preventDefault()
    if (!canCapture()) { notice('Capture requires a live checkpoint and a recent connection check.'); return }
    const operator = session
    try {
      const row = createCapture(selectedAssignment(), el('bib').value.trim(), deviceId, { online: true })
      rows.unshift(row)
      try { persist(operator) } catch (error) { rows.shift(); throw error }
      el('bib').value = ''
      el('bib').focus()
      notice()
      void lookup(operator, row)
    } catch (error) { notice(error.message) }
  })
  el('refresh').addEventListener('click', () => void probe(true))
  el('assignment').addEventListener('change', updateStatus)
  const onOffline = () => { connected = false; render() }
  window.addEventListener('offline', onOffline)
  const onOnline = () => { render(); void probe(true) }
  window.addEventListener('online', onOnline)
  const onBeforeUnload = event => {
    if ([...sessionRows.values()].some(captures => captures.some(row => !row.receipt && row.state !== 'Rejected'))) event.preventDefault()
  }
  window.addEventListener('beforeunload', onBeforeUnload)
  const probeInterval = setInterval(() => { updateStatus(); void probe(true) }, 15_000)
  const clockInterval = setInterval(updateStationTime, 1000)

  try {
    deviceId = localStorage.getItem(deviceKey) || crypto.randomUUID()
    localStorage.setItem(deviceKey, deviceId)
    setSession(JSON.parse(sessionStorage.getItem(sessionKey) || 'null') || undefined)
    if (session) void probe(true)
  } catch (error) {
    notice('This device could not initialize secure capture storage. ' + error.message)
    el('login').querySelector('button').disabled = true
  }
  return () => {
    clearInterval(probeInterval)
    clearInterval(clockInterval)
    window.removeEventListener('offline', onOffline)
    window.removeEventListener('online', onOnline)
    window.removeEventListener('beforeunload', onBeforeUnload)
  }
}
