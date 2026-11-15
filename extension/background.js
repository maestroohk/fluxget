import { appConfig, cachedConfig, call, cookieHeader, isHttp, isMediaPage, openMediaInApp, playInApp, sendDownload, settings } from './shared.js'

const MENU = { link: 'fg-link', media: 'fg-media', page: 'fg-page' }

chrome.runtime.onInstalled.addListener(async () => {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({ id: MENU.link, title: 'Download link with FluxGet', contexts: ['link'] })
    chrome.contextMenus.create({ id: MENU.media, title: 'Download with FluxGet', contexts: ['video', 'audio', 'image'] })
    chrome.contextMenus.create({ id: MENU.page, title: 'Download from this page with FluxGet', contexts: ['page'], documentUrlPatterns: ['http://*/*', 'https://*/*'] })
  })
  await syncContentScript()
})

chrome.runtime.onStartup.addListener(syncContentScript)

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  try {
    if (info.menuItemId === MENU.link && isHttp(info.linkUrl)) {
      if (isMediaPage(info.linkUrl)) await openMediaInApp(info.linkUrl)
      else await sendDownload(info.linkUrl, info.pageUrl)
      notify('Sent to FluxGet')
    } else if (info.menuItemId === MENU.media) {
      if (isHttp(info.srcUrl)) await sendDownload(info.srcUrl, info.pageUrl)
      else if (isMediaPage(info.pageUrl)) await openMediaInApp(info.pageUrl)
      else throw new Error('This media is streamed in a way FluxGet cannot download directly.')
      notify('Sent to FluxGet')
    } else if (info.menuItemId === MENU.page && tab?.url) {
      await openMediaInApp(tab.url)
    }
  } catch (e) {
    notify(e.message, true)
  }
})

function notify(text, error = false) {
  chrome.action.setBadgeBackgroundColor({ color: error ? '#C93C3C' : '#E2522A' })
  chrome.action.setBadgeText({ text: error ? '!' : '✓' })
  chrome.action.setTitle({ title: `FluxGet — ${text}` })
  setTimeout(() => {
    chrome.action.setBadgeText({ text: '' })
    chrome.action.setTitle({ title: 'FluxGet' })
  }, 4000)
}

const handled = new Set()

chrome.downloads.onCreated.addListener(async item => {
  const s = await settings()
  if (!s.captureDownloads || !s.token || handled.has(item.id)) return
  const url = item.finalUrl || item.url
  if (!isHttp(url) || item.byExtensionId === chrome.runtime.id) return
  if (item.totalBytes > 0 && item.totalBytes < s.captureMinMb * 1024 * 1024) return
  handled.add(item.id)
  try { await chrome.downloads.pause(item.id) } catch {}
  try {
    await call('GET', '/api/health')
    await sendDownload(url, item.referrer, item.filename ? item.filename.split(/[\\/]/).pop() : null)
    await chrome.downloads.cancel(item.id)
    await chrome.downloads.erase({ id: item.id })
    notify('Download moved to FluxGet')
  } catch (e) {
    try { await chrome.downloads.resume(item.id) } catch {}
    notify(e.message, true)
  }
})

chrome.runtime.onMessage.addListener((msg, sender, reply) => {
  const handlers = {
    'fg-status': async () => {
      try { await call('GET', '/api/health') } catch { return { state: 'offline' } }
      const s = await settings()
      if (!s.token) return { state: 'unpaired' }
      try { await call('GET', '/api/session'); await call('GET', '/api/settings'); return { state: 'ready' } } catch { return { state: 'unpaired' } }
    },
    'fg-launch': async () => { await chrome.tabs.create({ url: 'fluxget://open' }); return { ok: true } },
    'fg-analyze': async () => ({ ok: true, info: await call('POST', '/api/media/analyze', { url: msg.url }) }),
    'fg-media-download': async () => {
      const cookie = await cookieHeader(msg.item.url)
      const r = await call('POST', '/api/media/download', {
        items: [msg.item], mode: msg.opts.mode, height: msg.opts.height ?? null, container: msg.opts.container ?? 'mp4',
        audioFormat: msg.opts.audioFormat ?? 'mp3', audioQuality: msg.opts.audioQuality ?? null, subtitles: false,
        headers: { cookie, referer: sender.tab?.url ?? null, userAgent: navigator.userAgent },
      })
      const jobId = r.ids?.[0]
      if (!jobId) throw new Error('FluxGet did not accept this download.')
      return { ok: true, jobId }
    },
    'fg-job': async () => ({ ok: true, job: await call('GET', `/api/jobs/${msg.id}`) }),
    'fg-reveal': async () => { await call('POST', `/api/jobs/${msg.id}/open?reveal=true`); return { ok: true } },
    'fg-open-file': async () => { await call('POST', `/api/jobs/${msg.id}/open`); return { ok: true } },
    'fg-open-app': async () => { await call('POST', '/api/app/activate', { argument: null }); return { ok: true } },
    'fg-open-media': async () => { await openMediaInApp(msg.url); return { ok: true } },
    'fg-play': async () => { await playInApp(msg.url); return { ok: true } },
    'fg-config': async () => ({ ok: true, ...(await appConfig()) }),
    'fg-detected-tab': async () => ({ ok: true, items: await detectedFor(sender.tab?.id) }),
  }
  if (handlers[msg?.type]) {
    handlers[msg.type]().then(reply, e => reply({ ok: false, error: e.message }))
    return true
  }
  if (msg?.type === 'video-button') {
    const page = sender.tab?.url
    const target = isMediaPage(page) ? page : isHttp(msg.src) ? msg.src : null
    const run = async () => {
      if (!target) throw new Error('This video is streamed in a way FluxGet cannot download.')
      if (isMediaPage(target)) await openMediaInApp(target)
      else await sendDownload(target, page)
      return { ok: true }
    }
    run().then(reply, e => reply({ ok: false, error: e.message }))
    return true
  }
  if (msg?.type === 'sync-content-script') {
    syncContentScript().then(() => reply({ ok: true }))
    return true
  }
  if (msg?.type === 'detected') {
    detectedFor(msg.tabId).then(reply)
    return true
  }
})

async function syncContentScript() {
  try {
    const existing = await chrome.scripting.getRegisteredContentScripts({ ids: ['fg-video'] })
    if (existing.length > 0) await chrome.scripting.unregisterContentScripts({ ids: ['fg-video'] })
  } catch {}
}

const MEDIA_TYPE = /^(video\/|audio\/|application\/(vnd\.apple\.mpegurl|x-mpegurl|dash\+xml))/i

if (chrome.webRequest) {
  chrome.webRequest.onHeadersReceived.addListener(details => {
    if (details.tabId < 0) return
    void appConfig()
    if (!cachedConfig().detectMedia) return
    const type = details.responseHeaders?.find(h => h.name.toLowerCase() === 'content-type')?.value || ''
    if (!MEDIA_TYPE.test(type)) return
    const length = Number(details.responseHeaders?.find(h => h.name.toLowerCase() === 'content-length')?.value || 0)
    const manifest = /mpegurl|dash/i.test(type)
    if (!manifest && length > 0 && length < 512 * 1024) return
    void remember(details.tabId, { url: details.url, type: type.split(';')[0], size: length || null, manifest })
  }, { urls: ['<all_urls>'], types: ['media', 'xmlhttprequest', 'other'] }, ['responseHeaders'])

  chrome.tabs.onRemoved.addListener(tabId => chrome.storage.session.remove(`tab-${tabId}`))
  chrome.tabs.onUpdated.addListener((tabId, change) => { if (change.status === 'loading' && change.url) void chrome.storage.session.remove(`tab-${tabId}`) })
}

async function remember(tabId, item) {
  const key = `tab-${tabId}`
  const list = (await chrome.storage.session.get(key))[key] || []
  if (list.some(x => x.url === item.url)) return
  list.unshift(item)
  await chrome.storage.session.set({ [key]: list.slice(0, 20) })
  chrome.action.setBadgeBackgroundColor({ color: '#E2522A', tabId })
  chrome.action.setBadgeText({ text: String(Math.min(list.length, 99)), tabId })
}

async function detectedFor(tabId) {
  const key = `tab-${tabId}`
  return (await chrome.storage.session.get(key))[key] || []
}
