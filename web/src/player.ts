import { useSyncExternalStore } from 'react'
import { desktop } from './api'

export interface Track { id: number; name: string }
export interface QueueEntry { title: string; source: string; jobId: number | null; isStream: boolean }

export interface PlayerState {
  loaded: boolean
  title: string | null
  source: string | null
  isStream: boolean
  state: 'nothingspecial' | 'opening' | 'buffering' | 'playing' | 'paused' | 'stopped' | 'ended' | 'error'
  error: string | null
  time: number
  length: number
  volume: number
  muted: boolean
  rate: number
  seekable: boolean
  hasVideo: boolean
  audioTracks: Track[]
  audioTrack: number
  subtitleTracks: Track[]
  subtitleTrack: number
  subtitleDelayMs: number
  audioDelayMs: number
  aspect: string | null
  crop: string | null
  zoom: number
  rotation: number
  hardwareDecoding: boolean
  subtitleScale: number
  brightness: number
  contrast: number
  saturation: number
  mode: 'embedded' | 'fullscreen' | 'mini'
  resumedFrom: number
  index: number
  queue: QueueEntry[]
  speeds: number[]
  status: string | null
  live: boolean
  quality: number | null
  adaptive: boolean
  qualities: number[]
  captions: LiveCaptionState | null
  captionsAvailable: boolean
}

export interface LiveCaptionState { status: string; error: string | null; model: string; language: string; translate: boolean; count: number; ready: boolean; saved: string | null }

const PREFS = 'fg-live-captions'
export function captionPrefs(): { model: string; language: string; translate: boolean } {
  try { return { model: 'base', language: 'auto', translate: false, ...JSON.parse(localStorage.getItem(PREFS) || '{}') } } catch { return { model: 'base', language: 'auto', translate: false } }
}
export function setCaptionPrefs(v: { model: string; language: string; translate: boolean }) {
  try { localStorage.setItem(PREFS, JSON.stringify(v)) } catch { /* storage unavailable */ }
}
export const startLiveCaptions = (prefs = captionPrefs()) => player('liveCaptions', { on: true, ...prefs })
export const stopLiveCaptions = () => player('liveCaptions', { on: false })
export function captionLabel(c: LiveCaptionState | null): string {
  if (!c) return ''
  if (c.error) return c.error
  if (c.status.startsWith('Downloading')) return c.status.replace('speech model', 'speech model (one time)')
  if (c.status === 'Listening') return c.ready ? `Live · ${c.count} lines so far` : 'Listening… first lines in a few seconds'
  if (c.status === 'Done') return c.saved ? 'Finished · saved next to the file' : `Finished · ${c.count} lines`
  return c.status
}

let state: PlayerState | null = null
const subs = new Set<() => void>()

export function setPlayerState(next: PlayerState) {
  state = next
  subs.forEach(s => s())
}

export function usePlayer() {
  return useSyncExternalStore(cb => { subs.add(cb); return () => subs.delete(cb) }, () => state)
}

export const player = (cmd: string, data: Record<string, unknown> = {}) => desktop.send({ type: 'player', cmd, ...data })

export function clock(ms: number) {
  if (!isFinite(ms) || ms < 0) ms = 0
  const s = Math.floor(ms / 1000)
  const h = Math.floor(s / 3600)
  const m = Math.floor((s % 3600) / 60)
  const sec = String(s % 60).padStart(2, '0')
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${sec}` : `${m}:${sec}`
}

export const isPlaying = (p: PlayerState | null) => p?.state === 'playing' || p?.state === 'buffering' || p?.state === 'opening'

export const SHORTCUTS: [string, string][] = [
  ['Space / K', 'Play or pause'],
  ['← / →', 'Back or forward 5 seconds'],
  ['Shift + ← / →', 'Back or forward 30 seconds'],
  ['↑ / ↓', 'Volume up or down'],
  ['M', 'Mute'],
  ['F', 'Fullscreen'],
  ['Esc', 'Leave fullscreen or mini player'],
  ['N / P', 'Next or previous in the queue'],
  ['[ / ]', 'Slower or faster'],
  ['.', 'Next frame (pauses)'],
  ['C', 'Live subtitles on or off'],
]

export function handlePlayerKey(e: KeyboardEvent, p: PlayerState | null): boolean {
  if (e.ctrlKey || e.metaKey || e.altKey) return false
  const key = e.key
  const step = e.shiftKey ? 30000 : 5000
  switch (key) {
    case ' ': case 'k': case 'K': player('toggle'); break
    case 'ArrowLeft': player('seekBy', { ms: -step }); break
    case 'ArrowRight': player('seekBy', { ms: step }); break
    case 'ArrowUp': player('volumeBy', { value: 5 }); break
    case 'ArrowDown': player('volumeBy', { value: -5 }); break
    case 'm': case 'M': player('mute'); break
    case 'f': case 'F': player('mode', { value: p?.mode === 'fullscreen' ? 'embedded' : 'fullscreen' }); break
    case 'Escape': if (p && p.mode !== 'embedded') player('mode', { value: 'embedded' }); else return false; break
    case 'n': case 'N': player('next'); break
    case 'p': case 'P': player('prev'); break
    case '[': player('rateStep', { value: -1 }); break
    case ']': player('rateStep', { value: 1 }); break
    case '.': player('frame'); break
    case 'c': case 'C': if (p?.captions) stopLiveCaptions(); else startLiveCaptions(); break
    default: return false
  }
  e.preventDefault()
  return true
}
