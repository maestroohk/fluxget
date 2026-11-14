import { call, isMediaPage, save, sendDownload, settings } from './shared.js'

const $ = id => document.getElementById(id)
const show = (id, on = true) => { $(id).hidden = !on }
let tab = null
let pairId = null
let info = null

function fmt(n) {
  if (!n) return '—'
  const u = ['B', 'KB', 'MB', 'GB', 'TB']
  let i = 0
  while (n >= 1024 && i < u.length - 1) { n /= 1024; i++ }
  return `${n.toFixed(n >= 100 || i === 0 ? 0 : 1)} ${u[i]}`
}

async function init() {
  ;[tab] = await chrome.tabs.query({ active: true, currentWindow: true })
  const s = await settings()
  $('opt-capture').checked = s.captureDownloads
  $('opt-min').value = s.captureMinMb
  $('opt-cookies').checked = s.sendCookies
  $('opt-detect').checked = s.detectMedia
  $('opt-button').checked = s.videoButton
  try {
    await call('GET', '/api/health')
  } catch {
    return state('offline')
  }
  if (!s.token) return state('pair')
  try {
    await call('GET', '/api/jobs')
  } catch {
    return state('pair')
  }
  state('main')
}

function state(name) {
  show('offline', name === 'offline')
  show('pair', name === 'pair')
  show('main', name === 'main')
  $('status').className = `dot ${name === 'main' ? 'on' : name === 'offline' ? 'off' : ''}`
  $('status').title = name === 'main' ? 'Connected' : name === 'offline' ? 'FluxGet is not running' : 'Not connected'
  if (name === 'main') {
    void refreshJobs()
    setInterval(refreshJobs, 1000)
    void showMedia()
    void showDetected()
  }
}

async function refreshJobs() {
  try {
    const jobs = (await call('GET', '/api/jobs')).filter(j => j.status !== 'Completed').slice(0, 6)
    const ul = $('jobs')
    ul.replaceChildren()
    if (!jobs.length) {
      const li = document.createElement('li')
      li.className = 'muted'
      li.textContent = 'Nothing downloading.'
      ul.append(li)
      return
    }
    for (const j of jobs) {
      const li = document.createElement('li')
      const pct = j.totalBytes ? Math.min(100, (j.receivedBytes / j.totalBytes) * 100) : 0
      const name = document.createElement('div')
      name.className = 'job-name'
      name.textContent = j.fileName || j.extra?.title || j.url
      const bar = document.createElement('div')
      bar.className = 'bar'
      const fill = document.createElement('i')
      fill.style.width = `${pct}%`
      bar.append(fill)
      const meta = document.createElement('div')
      meta.className = 'job-meta'
      const left = document.createElement('span')
      left.textContent = j.totalBytes ? `${fmt(j.receivedBytes)} of ${fmt(j.totalBytes)}` : fmt(j.receivedBytes)
      const right = document.createElement('span')
      right.textContent = j.status === 'Running' ? `${fmt(j.speed)}/s` : j.status.replace(/([a-z])([A-Z])/g, '$1 $2')
      meta.append(left, right)
      li.append(name, bar, meta)
      ul.append(li)
    }
  } catch {}
}

async function showMedia() {
  if (!tab?.url || !isMediaPage(tab.url)) return
  show('media')
}

$('media-find').addEventListener('click', async () => {
  $('media-find').disabled = true
  $('media-find').textContent = 'Looking…'
  show('media-error', false)
  try {
    info = await call('POST', '/api/media/analyze', { url: tab.url })
    $('media-title').textContent = info.title
    const sel = $('media-quality')
    sel.replaceChildren()
    const add = (value, label) => { const o = document.createElement('option'); o.value = value; o.textContent = label; sel.append(o) }
    if (info.type === 'playlist') add('open', `Playlist · ${info.entryCount ?? info.entries.length} items — choose in FluxGet`)
    else {
      for (const v of info.video) add(`v:${v.height}`, `${v.height}p${v.fps > 30 ? ` ${Math.round(v.fps)}fps` : ''}${v.size ? ` · ${v.estimated ? '≈' : ''}${fmt(v.size)}` : ''}`)
      if (!info.video.length) add('v:', 'Best available video')
      add('a:mp3', 'Audio only · MP3 192 kbps')
      add('a:m4a', 'Audio only · M4A')
    }
    show('media-info')
    show('media-find', false)
    $('media-head').hidden = true
  } catch (e) {
    $('media-error').textContent = e.message
    show('media-error')
    $('media-find').disabled = false
    $('media-find').textContent = 'Find formats'
  }
})

$('media-go').addEventListener('click', async () => {
  const v = $('media-quality').value
  $('media-go').disabled = true
  try {
    if (v === 'open') {
      await call('POST', '/api/app/activate', { argument: `fluxget://media?url=${encodeURIComponent(tab.url)}` })
    } else {
      const [mode, q] = v.split(':')
      await call('POST', '/api/media/download', {
        items: [{ url: info.url, title: info.title }],
        mode: mode === 'a' ? 'audio' : 'video',
        height: mode === 'v' && q ? Number(q) : null,
        container: 'mp4',
        audioFormat: mode === 'a' ? q : 'mp3',
        audioQuality: mode === 'a' && q === 'mp3' ? '192' : null,
        subtitles: false,
      })
    }
    $('media-go').textContent = 'Sent ✓'
  } catch (e) {
    $('media-error').textContent = e.message
    show('media-error')
    $('media-go').disabled = false
  }
})

async function showDetected() {
  if (!tab) return
  const list = await chrome.runtime.sendMessage({ type: 'detected', tabId: tab.id })
  const direct = (list || []).filter(x => !x.manifest)
  if (!direct.length) return
  show('detected')
  const ul = $('detected-list')
  for (const item of direct) {
    const li = document.createElement('li')
    li.className = 'det'
    const label = document.createElement('span')
    label.textContent = `${decodeURIComponent(new URL(item.url).pathname.split('/').pop() || item.url)} · ${item.size ? fmt(item.size) : item.type}`
    label.title = item.url
    const btn = document.createElement('button')
    btn.textContent = 'Download'
    btn.addEventListener('click', async () => {
      try { await sendDownload(item.url, tab.url); btn.textContent = 'Sent ✓' } catch (e) { btn.textContent = 'Failed'; btn.title = e.message }
    })
    li.append(label, btn)
    ul.append(li)
  }
}

$('launch').addEventListener('click', () => chrome.tabs.create({ url: 'fluxget://open' }))
$('open-app').addEventListener('click', () => call('POST', '/api/app/activate', { argument: null }).catch(() => chrome.tabs.create({ url: 'fluxget://open' })))

$('pair-start').addEventListener('click', async () => {
  show('pair-error', false)
  try {
    const name = `${navigator.userAgentData?.brands?.find(b => /Chrome|Edge|Brave|Opera/.test(b.brand))?.brand ?? 'Browser'} on ${navigator.userAgentData?.platform ?? 'this computer'}`
    const r = await call('POST', '/api/pair/request', { name })
    pairId = r.id
    show('pair-start', false)
    show('pair-form')
    $('pair-code').focus()
  } catch (e) {
    $('pair-error').textContent = e.message
    show('pair-error')
  }
})

$('pair-form').addEventListener('submit', async e => {
  e.preventDefault()
  show('pair-error', false)
  try {
    const r = await call('POST', '/api/pair/confirm', { id: pairId, code: $('pair-code').value.replace(/\D/g, '') })
    await save({ token: r.token })
    state('main')
  } catch (err) {
    $('pair-error').textContent = err.message
    show('pair-error')
  }
})

$('disconnect').addEventListener('click', async () => {
  await save({ token: null })
  state('pair')
  show('pair-start')
  show('pair-form', false)
})

$('opt-capture').addEventListener('change', e => save({ captureDownloads: e.target.checked }))
$('opt-min').addEventListener('change', e => save({ captureMinMb: Math.max(0, Number(e.target.value) || 0) }))
$('opt-cookies').addEventListener('change', async e => {
  const ok = e.target.checked ? await chrome.permissions.request({ permissions: ['cookies'], origins: ['<all_urls>'] }) : true
  if (!ok) e.target.checked = false
  await save({ sendCookies: e.target.checked })
})
$('opt-detect').addEventListener('change', async e => {
  const ok = e.target.checked ? await chrome.permissions.request({ permissions: ['webRequest'], origins: ['<all_urls>'] }) : true
  if (!ok) e.target.checked = false
  await save({ detectMedia: e.target.checked })
  if (ok && e.target.checked) chrome.runtime.reload()
})
$('opt-button').addEventListener('change', async e => {
  const ok = e.target.checked ? await chrome.permissions.request({ origins: ['<all_urls>'] }) : true
  if (!ok) e.target.checked = false
  await save({ videoButton: e.target.checked })
  await chrome.runtime.sendMessage({ type: 'sync-content-script' })
})

void init()
