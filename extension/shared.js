export const DEFAULTS = {
  port: 17890,
  token: null,
  captureDownloads: false,
  captureMinMb: 5,
  videoButton: false,
  siteButton: true,
  detectMedia: false,
  sendCookies: false,
}

const MEDIA_HOSTS = /(^|\.)(youtube\.com|youtu\.be|facebook\.com|fb\.watch|instagram\.com|tiktok\.com|x\.com|twitter\.com|reddit\.com|vimeo\.com|twitch\.tv|dailymotion\.com|soundcloud\.com|bilibili\.com)$/i

export function isMediaPage(url) {
  try {
    const u = new URL(url)
    return (u.protocol === 'https:' || u.protocol === 'http:') && MEDIA_HOSTS.test(u.hostname)
  } catch {
    return false
  }
}

export const isHttp = url => /^https?:\/\//i.test(url || '')

export async function settings() {
  return { ...DEFAULTS, ...(await chrome.storage.local.get(Object.keys(DEFAULTS))) }
}

export async function save(patch) {
  await chrome.storage.local.set(patch)
}

export class FluxError extends Error {
  constructor(status, message) {
    super(message)
    this.status = status
  }
}

export async function call(method, path, body) {
  const s = await settings()
  const headers = { 'Content-Type': 'application/json' }
  if (s.token) headers.Authorization = `Bearer ${s.token}`
  let res
  try {
    res = await fetch(`http://127.0.0.1:${s.port}${path}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) })
  } catch {
    throw new FluxError(0, 'FluxGet is not running on this computer.')
  }
  if (res.status === 401) {
    if (s.token) await save({ token: null })
    throw new FluxError(401, 'This browser is not connected to FluxGet.')
  }
  if (!res.ok) {
    let msg = `FluxGet returned ${res.status}`
    try {
      const p = await res.json()
      msg = p.detail || p.title || msg
    } catch {}
    throw new FluxError(res.status, msg)
  }
  return res.status === 204 ? null : res.json()
}

export async function cookieHeader(url) {
  const s = await settings()
  if (!s.sendCookies || !chrome.cookies) return null
  try {
    const list = await chrome.cookies.getAll({ url })
    return list.length ? list.map(c => `${c.name}=${c.value}`).join('; ') : null
  } catch {
    return null
  }
}

export async function sendDownload(url, referrer, fileName) {
  const cookie = await cookieHeader(url)
  return call('POST', '/api/downloads', {
    urls: [url],
    fileName: fileName || null,
    headers: { cookie, referer: referrer || null, userAgent: navigator.userAgent },
  })
}

export async function openMediaInApp(url) {
  await call('POST', '/api/app/activate', { argument: `fluxget://media?url=${encodeURIComponent(url)}` })
}
