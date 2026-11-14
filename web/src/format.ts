import type { Job, JobStatus } from './api'

const UNITS = ['B', 'KB', 'MB', 'GB', 'TB']

export function bytes(n: number | null | undefined, digits = 1): string {
  if (n == null || !isFinite(n)) return '—'
  if (n < 1024) return `${Math.round(n)} B`
  let i = 0
  let v = n
  while (v >= 1024 && i < UNITS.length - 1) { v /= 1024; i++ }
  return `${v.toFixed(v >= 100 ? 0 : digits)} ${UNITS[i]}`
}

export const speed = (n: number | null | undefined) => (n && n > 0 ? `${bytes(n)}/s` : '—')

export function duration(sec: number | null | undefined): string {
  if (sec == null || !isFinite(sec) || sec < 0) return '—'
  if (sec < 60) return `${Math.max(1, Math.round(sec))}s`
  if (sec < 3600) return `${Math.floor(sec / 60)}m ${Math.round(sec % 60)}s`
  if (sec < 86400) return `${Math.floor(sec / 3600)}h ${Math.round((sec % 3600) / 60)}m`
  return `${Math.floor(sec / 86400)}d ${Math.round((sec % 86400) / 3600)}h`
}

export function ago(iso: string | null | undefined): string {
  if (!iso) return ''
  const s = (Date.now() - new Date(iso).getTime()) / 1000
  if (s < 45) return 'just now'
  if (s < 3600) return `${Math.round(s / 60)} min ago`
  if (s < 86400) return `${Math.round(s / 3600)} h ago`
  const d = new Date(iso)
  return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: d.getFullYear() === new Date().getFullYear() ? undefined : 'numeric' })
}

export function percent(j: Job): number | null {
  const p = j.extra?.progress
  if (typeof p === 'number' && j.status !== 'Completed') return Math.min(100, p * 100)
  return j.totalBytes ? Math.min(100, (j.receivedBytes / j.totalBytes) * 100) : null
}

export function host(url: string) {
  if (url.startsWith('magnet:')) return 'magnet link'
  try { return new URL(url).hostname.replace(/^www\./, '') } catch { return url }
}

function urlFileName(url: string) {
  try {
    const last = new URL(url).pathname.split('/').filter(Boolean).pop()
    return last && /\.[a-z0-9]{1,6}$/i.test(last) ? decodeURIComponent(last) : null
  } catch { return null }
}

export const displayName = (j: Job) => j.fileName || (j.extra?.title as string | undefined) || urlFileName(j.url) || host(j.url)

export function extension(name: string | null) {
  const m = name?.match(/\.([a-z0-9]{1,6})$/i)
  return m ? m[1].toLowerCase() : ''
}

const PLAYABLE = /\.(mp4|m4v|mkv|webm|mov|avi|wmv|flv|ts|m2ts|mpg|mpeg|3gp|ogv|mp3|m4a|aac|opus|ogg|oga|flac|wav|wma|mka)$/i
export const isPlayable = (name: string | null | undefined) => !!name && PLAYABLE.test(name)

export type Tone = 'accent' | 'ok' | 'warn' | 'bad' | 'muted' | 'info'

export const statusInfo: Record<JobStatus, { label: string; tone: Tone }> = {
  Queued: { label: 'Queued', tone: 'muted' },
  Scheduled: { label: 'Scheduled', tone: 'info' },
  Running: { label: 'Downloading', tone: 'accent' },
  Retrying: { label: 'Retrying', tone: 'warn' },
  WaitingForNetwork: { label: 'Waiting for network', tone: 'warn' },
  Verifying: { label: 'Verifying', tone: 'accent' },
  Paused: { label: 'Paused', tone: 'muted' },
  Seeding: { label: 'Seeding', tone: 'ok' },
  NeedsAttention: { label: 'Needs attention', tone: 'warn' },
  Failed: { label: 'Failed', tone: 'bad' },
  Completed: { label: 'Completed', tone: 'ok' },
}

export const isActive = (s: JobStatus) => s === 'Running' || s === 'Retrying' || s === 'WaitingForNetwork' || s === 'Verifying'
export const canPause = (s: JobStatus) => isActive(s) || s === 'Queued' || s === 'Scheduled' || s === 'Seeding'
export const canResume = (s: JobStatus) => s === 'Paused' || s === 'Failed' || s === 'NeedsAttention'

export function parseLimit(text: string): number {
  const m = text.trim().match(/^([\d.]+)\s*(k|kb|m|mb|g|gb)?(\/s)?$/i)
  if (!m) return 0
  const v = parseFloat(m[1])
  const u = (m[2] || 'k').toLowerCase()[0]
  return Math.round(v * (u === 'g' ? 1024 ** 3 : u === 'm' ? 1024 ** 2 : 1024))
}

export const limitText = (n: number) => (n > 0 ? `${bytes(n, n >= 1048576 ? 1 : 0)}/s` : '')
