import { chromium } from 'playwright'
import { spawn } from 'node:child_process'
import { createServer } from 'node:http'
import { createHash, randomBytes } from 'node:crypto'
import { mkdtempSync, readFileSync, rmSync, existsSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, resolve, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import assert from 'node:assert/strict'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const extensionPath = join(root, 'extension')
const hostDll = join(root, 'src/FluxGet.Host/bin/Debug/net10.0/FluxGet.Host.dll')
const port = 17892
const token = randomBytes(24).toString('base64url')
const data = mkdtempSync(join(tmpdir(), 'fluxget-e2e-'))
const base = `http://127.0.0.1:${port}`
const results = []
const step = async (name, fn) => {
  const t = Date.now()
  try {
    await fn()
    results.push(`PASS ${name} (${Date.now() - t} ms)`)
    console.log(`PASS ${name}`)
  } catch (e) {
    results.push(`FAIL ${name}: ${e.message}`)
    console.log(`FAIL ${name}: ${e.stack}`)
    process.exitCode = 1
    throw e
  }
}
const api = (path, init = {}) => fetch(base + path, { ...init, headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json', ...(init.headers || {}) } })
const sleep = ms => new Promise(r => setTimeout(r, ms))

const fileBytes = randomBytes(3 * 1024 * 1024 + 17)
const fileSha = createHash('sha256').update(fileBytes).digest('hex')
const files = createServer((req, res) => {
  if (req.url.startsWith('/page')) {
    res.writeHead(200, { 'Content-Type': 'text/html' })
    res.end('<a id="dl" href="/files/setup-e2e.bin" download>Download</a>')
  } else if (req.url.startsWith('/files/setup-e2e.bin')) {
    res.writeHead(200, { 'Content-Type': 'application/octet-stream', 'Content-Length': fileBytes.length, 'Content-Disposition': 'attachment; filename="setup-e2e.bin"' })
    res.end(fileBytes)
  } else {
    res.writeHead(404)
    res.end()
  }
})
await new Promise(r => files.listen(0, '127.0.0.1', r))
const filesBase = `http://127.0.0.1:${files.address().port}`

const host = spawn('dotnet', [hostDll, `--FluxGet:Port=${port}`, `--FluxGet:DataDirectory=${data}`, `--FluxGet:AccessToken=${token}`, `--FluxGet:DownloadDirectory=${join(data, 'dl')}`], { stdio: 'ignore' })
let context
try {
  for (let i = 0; i < 60; i++) {
    try { if ((await fetch(base + '/api/health')).ok) break } catch {}
    await sleep(250)
  }

  context = await chromium.launchPersistentContext(join(data, 'profile'), {
    ...(process.env.E2E_BROWSER_PATH ? { executablePath: process.env.E2E_BROWSER_PATH } : { channel: process.env.E2E_CHANNEL || 'msedge' }),
    headless: true,
    ignoreDefaultArgs: ['--disable-extensions'],
    acceptDownloads: true,
    args: [`--disable-extensions-except=${extensionPath}`, `--load-extension=${extensionPath}`, '--disable-features=DisableLoadExtensionCommandLineSwitch'],
  })
  let [worker] = context.serviceWorkers()
  worker ??= await context.waitForEvent('serviceworker')
  const extId = new URL(worker.url()).host
  await worker.evaluate(p => chrome.storage.local.set({ port: p }), port)

  await step('Popup reports not connected before pairing', async () => {
    const popup = await context.newPage()
    await popup.goto(`chrome-extension://${extId}/popup.html`)
    await popup.waitForSelector('#pair:not([hidden])', { timeout: 5000 })
    await popup.close()
  })

  await step('Web page cannot start pairing (Origin check)', async () => {
    const r = await fetch(base + '/api/pair/request', { method: 'POST', headers: { 'Content-Type': 'application/json', Origin: 'https://evil.example' }, body: '{"name":"x"}' })
    assert.equal(r.status, 403)
  })

  await step('Pairing with the code shown in FluxGet', async () => {
    const events = await fetch(base + '/api/events', { headers: { Authorization: `Bearer ${token}` } })
    const reader = events.body.getReader()
    const codePromise = (async () => {
      let buffer = ''
      while (true) {
        const { value, done } = await reader.read()
        if (done) throw new Error('event stream ended')
        buffer += new TextDecoder().decode(value)
        const m = buffer.match(/event: pair\ndata: (.+)\n/)
        if (m) return JSON.parse(m[1]).code
      }
    })()
    const popup = await context.newPage()
    await popup.goto(`chrome-extension://${extId}/popup.html`)
    await popup.click('#pair-start')
    const code = await codePromise
    reader.cancel()
    await popup.fill('#pair-code', '000000')
    await popup.click('#pair-form button')
    await popup.waitForSelector('#pair-error:not([hidden])')
    await popup.fill('#pair-code', code)
    await popup.click('#pair-form button')
    await popup.waitForSelector('#main:not([hidden])', { timeout: 5000 })
    const stored = await worker.evaluate(() => chrome.storage.local.get('token'))
    assert.ok(stored.token && stored.token.length > 20)
    await popup.close()
  })

  await step('Browser download is captured and completed by FluxGet', async () => {
    await worker.evaluate(() => chrome.storage.local.set({ captureDownloads: true, captureMinMb: 1 }))
    const page = await context.newPage()
    await page.goto(`${filesBase}/page`)
    page.click('#dl').catch(() => {})
    let job
    for (let i = 0; i < 120 && !job; i++) {
      await sleep(250)
      const done = await (await api('/api/jobs/completed?search=setup-e2e')).json()
      job = done.items[0]
    }
    assert.ok(job, 'job did not complete in FluxGet')
    const sha = createHash('sha256').update(readFileSync(job.filePath)).digest('hex')
    assert.equal(sha, fileSha)
    const browserItems = await worker.evaluate(() => chrome.downloads.search({ filenameRegex: 'setup-e2e' }))
    assert.equal(browserItems.filter(d => d.state === 'complete').length, 0, 'browser also completed the download')
    await page.close()
  })

  await step('Popup lists live downloads from FluxGet', async () => {
    await api('/api/downloads', { method: 'POST', body: JSON.stringify({ urls: [`${filesBase}/files/setup-e2e.bin?again`], startPaused: true }) })
    const popup = await context.newPage()
    await popup.goto(`chrome-extension://${extId}/popup.html`)
    await popup.waitForSelector('#jobs .job-name', { timeout: 5000 })
    const text = await popup.textContent('#jobs')
    assert.match(text, /setup-e2e\.bin/)
    assert.match(text, /Paused/)
    await popup.close()
  })

  await step('Extension token can call the media API', async () => {
    const r = await worker.evaluate(async p => {
      const { token } = await chrome.storage.local.get('token')
      const res = await fetch(`http://127.0.0.1:${p}/api/media/analyze`, { method: 'POST', headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' }, body: JSON.stringify({ url: 'https://example.com/' }) })
      return { status: res.status, body: await res.json() }
    }, port)
    assert.equal(r.status, 422)
    assert.match(r.body.detail, /isn't supported|yt-dlp is not installed/)
  })

  await step('Revoked token returns the popup to pairing', async () => {
    const tokens = await (await api('/api/tokens')).json()
    for (const t of tokens) await api(`/api/tokens/${t.id}`, { method: 'DELETE' })
    const popup = await context.newPage()
    await popup.goto(`chrome-extension://${extId}/popup.html`)
    await popup.waitForSelector('#pair:not([hidden])', { timeout: 5000 })
    await popup.close()
  })
} finally {
  await context?.close()
  host.kill()
  files.close()
  await sleep(500)
  try { rmSync(data, { recursive: true, force: true }) } catch {}
  console.log('\n' + results.join('\n'))
}
