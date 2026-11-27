import { mkdir } from 'node:fs/promises'
import { chromium } from 'playwright'
import assert from 'node:assert/strict'
const root = 'http://127.0.0.1:5092/api/v1'
const email = process.env.REHEARSAL_EMAIL || 'director@rehearsal.example.test'
const password = process.env.REHEARSAL_PASSWORD
assert.ok(
  password,
  'Set REHEARSAL_PASSWORD for the disposable local API on port 5092.',
)
let auth
async function api(path, method = 'GET', body, token = auth) {
  const res = await fetch(root + path, {
    method,
    headers: {
      'Content-Type': 'application/json',
      ...(token && { Authorization: 'Bearer ' + token }),
    },
    body: JSON.stringify(body),
  })
  const text = await res.text()
  if (!res.ok) throw Error(method + ' ' + path + ' ' + res.status + ' ' + text)
  return text ? JSON.parse(text) : null
}
const post = (p, b) => api(p, 'POST', b)
const put = (p, b) => api(p, 'PUT', b)
const suffix = Date.now().toString(36)
auth = (await post('/auth/login', { email, password })).accessToken
const org = await post('/organizers', {
  name: 'Rehearsal ' + suffix,
  slug: 'rehearsal-' + suffix,
})
const ago = (n) => new Date(Date.now() - n * 60000).toISOString()
const event = await post('/events', {
  organizerId: org.id,
  name: 'Summit Loop Rehearsal',
  slug: 'summit-' + suffix,
  organizerName: 'Synthetic Trail Club',
  startDateUtc: ago(20),
  endDateUtc: ago(-180),
  timeZoneId: 'Asia/Manila',
})
const base = '/events/' + event.id
const timing = base + '/timing'
const gate = await post(timing + '/checkpoints', {
  name: 'Start and Finish Gate',
  code: 'GATE',
})
const aid = await post(timing + '/checkpoints', {
  name: 'Pine Ridge Aid',
  code: 'AID',
})
const races = []
for (const distanceKm of [12, 24]) {
  const course = await post(base + '/courses', {
    name: distanceKm + 'K Trail',
    distanceKm,
  })
  const names =
    distanceKm === 12
      ? ['Start', 'Pine Ridge', 'Finish']
      : ['Start', 'Pine Ridge outbound', 'Pine Ridge return', 'Finish']
  const checkpoints = names.map((name, i) => ({
    name,
    code: 'CP' + i,
    sequence: i + 1,
    distanceFromStartKm: i === names.length - 1 ? distanceKm : i * 5,
    isStart: i === 0,
    isFinish: i === names.length - 1,
    isMandatory: true,
    hasCutoff: i === 1,
    cutoffDurationSeconds: i === 1 ? 60 : null,
  }))
  await put(base + '/courses/' + course.id + '/checkpoints', {
    distanceKm,
    checkpoints,
  })
  const race = await post(base + '/races', {
    name: distanceKm + 'K',
    courseId: course.id,
    startAt: ago(10),
    ensureRegistrationCategory: true,
  })
  const wave = await post(base + '/races/' + race.id + '/waves', {
    name: 'Wave A',
    sequence: 1,
    startAt: ago(9),
  })
  races.push({ race, wave, course, distanceKm })
}
const browser = await chromium.launch({
  executablePath: process.env.REHEARSAL_BROWSER,
  headless: true,
})
try {
  const screenshot = process.env.REHEARSAL_ARTIFACTS
  if (screenshot) await mkdir(screenshot, { recursive: true })
  const admin = await browser.newPage({
    viewport: { width: 1400, height: 950 },
  })
  admin.setDefaultTimeout(8000)

  await admin.goto('http://127.0.0.1:5193/login')

  await admin.locator('input[type=email]').fill(email)
  await admin.locator('input[type=password]').fill(password)
  await admin.getByRole('button', { name: 'Sign in' }).click()
  await admin.waitForURL((url) => !url.pathname.endsWith('/login'))
  async function select(container, label, value) {
    await container
      .locator('.v-input')
      .filter({ hasText: label })
      .locator('.v-field')
      .first()
      .click()
    await admin.getByRole('option', { name: value, exact: true }).click()
  }
  const layout = await api(base + '/configuration')
  for (const { course, distanceKm } of races) {
    const points = layout.courses.find((c) => c.id === course.id).checkpoints
    for (const point of points)
      await put(
        timing +
          '/courses/' +
          course.id +
          '/occurrences/' +
          point.id +
          '/checkpoint',
        { checkpointId: point.isStart || point.isFinish ? gate.id : aid.id },
      )
    const bibNumber = distanceKm === 12 ? '0042' : '0043'
    await post('/organizers/' + org.id + '/runners', {
      firstName: 'Runner',
      lastName: bibNumber,
    })
    await admin.goto('http://127.0.0.1:5193' + base)
    await admin.getByRole('tab', { name: 'Runner Registrations' }).click()
    await admin.getByRole('button', { name: 'Register runner' }).click()
    const registration = admin.getByRole('dialog')
    const search = registration
      .locator('.v-input')
      .filter({ hasText: 'Find runner profile' })
      .locator('input')
    await search.fill(bibNumber)
    await Promise.all([
      admin.waitForResponse(
        (response) => response.url().includes('/runners?') && response.ok(),
      ),
      registration.getByRole('button', { name: 'Find', exact: true }).click(),
    ])
    await registration
      .locator('.v-input')
      .filter({ hasText: 'Runner profile' })
      .filter({ hasNotText: 'Find runner profile' })
      .locator('input')
      .click()
    await admin
      .getByText('Runner ' + bibNumber, { exact: true })
      .last()
      .click()
    await select(registration, /^Race/, distanceKm + 'K')
    await select(registration, 'Start Wave', 'Wave A')
    await select(registration, 'Registration status', 'Confirmed')
    await registration
      .locator('.v-input')
      .filter({ hasText: 'Bib number' })
      .locator('input')
      .fill(bibNumber)
    await registration
      .getByRole('button', { name: 'Save registration' })
      .click()
    await registration.waitFor({ state: 'hidden' })
    await admin.getByText(bibNumber, { exact: true }).first().waitFor()
  }
  const operatorEmail = 'marshal-' + suffix + '@rehearsal.example.test'
  const invite = await post('/organizers/' + org.id + '/invitations', {
    email: operatorEmail,
    role: 'CheckpointMarshal',
  })
  const token = new URL(invite.developmentAcceptanceUrl).searchParams.get(
    'token',
  )
  await api(
    '/invitations/register',
    'POST',
    {
      token,
      email: operatorEmail,
      displayName: 'Pine Ridge Marshal',
      password,
    },
    null,
  )
  const operator = await api(
    '/auth/login',
    'POST',
    { email: operatorEmail, password },
    null,
  )
  for (const cp of [gate, aid])
    await put(
      timing + '/checkpoints/' + cp.id + '/assignments/' + operator.user.id,
      { active: true },
    )
  for (const status of [
    'Published',
    'RegistrationOpen',
    'RegistrationClosed',
    'Ready',
  ])
    await api(base + '/status', 'PATCH', { status })
  for (const { race } of races)
    await post(timing + '/races/' + race.id + '/arm', {})
  await api(base + '/status', 'PATCH', { status: 'Live' })
  for (const { race, wave } of races) {
    await post(timing + '/races/' + race.id + '/gun-starts', {
      gunAtUtc: ago(10),
      reason: 'Rehearsal race gun',
    })
    await post(timing + '/races/' + race.id + '/gun-starts', {
      waveId: wave.id,
      gunAtUtc: ago(9),
      reason: 'Rehearsal wave gun',
    })
  }
  console.log('Seeded isolated event ' + event.id)
  const page = await browser.newPage({ viewport: { width: 414, height: 900 } })
  page.setDefaultTimeout(8000)
  await page.goto('http://127.0.0.1:5194')
  await page.getByLabel('Account email').fill(operatorEmail)
  await page.getByLabel('Password').fill(password)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await page.getByLabel('Assigned Event').selectOption({ value: event.id })
  const checkpoint = page.getByLabel('Assigned Checkpoint')
  await checkpoint.selectOption({ value: gate.id })
  const record = page.getByRole('button', { name: 'Record crossing' })
  const bib = page.getByLabel('Runner bib')
  async function capture(number, choice) {
    await bib.fill(number)
    await record.click()
    const row = page.locator('#captures li').first()
    if (choice) {
      await row.locator('select').selectOption({ index: choice })
      await row.getByRole('button', { name: 'Submit selected visit' }).click()
    }
    return row
  }
  let delayed = true
  await page.route('**/registrations?bib=0042', async (route) => {
    if (delayed) {
      delayed = false
      await new Promise((resolve) => setTimeout(resolve, 1200))
    }
    await route.continue()
  })
  await bib.fill('0042')
  await record.click()
  await capture('0042', 2)
  await page.getByText('Bib 0042 · Recorded', { exact: true }).waitFor()
  const earlier = page.locator('#captures li').first()
  await earlier.locator('select').selectOption({ index: 1 })
  await earlier.getByRole('button', { name: 'Submit selected visit' }).click()
  await earlier.getByText(/Recorded/).waitFor()
  await capture('0042', 2)
  await page
    .getByText(/needs review/i)
    .first()
    .waitFor()
  await checkpoint.selectOption({ value: aid.id })
  const aidAttempts = []
  await page.route(
    '**/checkpoints/' + aid.id + '/observations',
    async (route) => {
      const request = route.request()
      const response = await route.fetch()
      aidAttempts.push({
        status: response.status(),
        key: request.headers()['idempotency-key'],
        body: request.postData(),
        observationId: (await response.json()).observationId,
      })
      if (aidAttempts.length === 1) await route.abort()
      else await route.fulfill({ response })
    },
  )
  await capture('0043', 1)
  await page.getByText('Bib 0043 · Retry needed', { exact: true }).waitFor()
  await page.getByRole('button', { name: 'Retry capture' }).click()
  await page.getByText('Bib 0043 · Recorded', { exact: true }).waitFor()
  assert.equal(aidAttempts.length, 2)
  const [first, replay] = aidAttempts
  assert.ok(first.status >= 200 && first.status < 300)
  assert.ok(replay.status >= 200 && replay.status < 300)
  assert.ok(first.key && first.observationId)
  assert.equal(replay.key, first.key)
  assert.equal(replay.body, first.body)
  assert.equal(replay.observationId, first.observationId)
  console.log(
    'Accepted start and finish received in reverse order, duplicate pending review, and explicit repeated-visit selection',
  )
  if (screenshot)
    await page.screenshot({
      path: screenshot + '/checkpoint-operator.png',
      fullPage: true,
    })
  console.log(
    'Verified operator login, assigned station and unchanged lost-receipt replay',
  )
  await admin.goto('http://127.0.0.1:5193' + base + '?tab=race-control')
  await admin.getByRole('tab', { name: 'Runner Results' }).click()
  async function classify(classification) {
    await admin
      .getByRole('button', { name: 'Change official classification' })
      .click()
    const dialog = admin.getByRole('dialog')
    await select(dialog, 'Official classification', classification)
    await dialog.locator('textarea').fill('Synthetic rehearsal decision')
    await dialog.getByRole('button', { name: 'Confirm decision' }).click()
    await dialog.waitFor({ state: 'hidden' })
  }
  await select(admin, /^Race/, '12K')
  await admin.getByText(/Bib 0042 · Pending/).waitFor()
  await admin.getByText('Ordered checkpoint splits').click()
  await admin.getByRole('cell', { name: 'Missing', exact: true }).waitFor()
  await classify('Finished')
  await admin.getByText('Bib 0042 · Finished', { exact: true }).waitFor()
  if ((await admin.locator('details').getAttribute('open')) === null)
    await admin.getByText('Ordered checkpoint splits').click()
  await admin
    .getByRole('button', { name: 'Review', exact: true })
    .last()
    .click()
  const correction = admin.getByRole('dialog')
  await select(correction, 'Proposed action', 'Invalidate observation')
  await correction.locator('textarea').fill('Rehearsal finish correction')
  await correction.getByRole('button', { name: 'Confirm decision' }).click()
  await correction.waitFor({ state: 'hidden' })
  await admin
    .getByText('Supporting evidence changed. Review this classification.')
    .waitFor()
  if (screenshot)
    await admin.screenshot({
      path: screenshot + '/checkpoint-evidence-correction.png',
      fullPage: true,
    })
  await select(admin, /^Race/, '24K')
  await admin.getByText(/Bib 0043 · Pending/).waitFor()
  await admin.getByText('Ordered checkpoint splits').click()
  await admin
    .getByRole('cell', { name: /over limit/i })
    .first()
    .waitFor()
  await classify('DNF')
  await admin.getByText('Bib 0043 · DNF', { exact: true }).waitFor()
  if (screenshot)
    await admin.screenshot({
      path: screenshot + '/checkpoint-race-control.png',
      fullPage: true,
    })
  console.log(
    'Verified UI registrations, missing evidence, cutoff exceedance, Finished, correction and DNF',
  )
} finally {
  await browser.close()
}
