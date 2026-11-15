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
  repeat: 'off' | 'one' | 'all'
  shuffle: boolean
  sleep: string | null
  snapshot: string | null
  chapter: number
  chapters: { title: string | null; start: number }[] | null
}

export interface LiveCaptionState { status: string; error: string | null; model: string; language: string; translate: boolean; count: number; ready: boolean; saved: string | null; progress: number; whole: boolean; speed: string }
export interface CaptionPrefs { model: string; language: string; translate: boolean; speed: 'quiet' | 'balanced' | 'fast' }

const PREFS = 'fg-live-captions'
const DEFAULT_PREFS: CaptionPrefs = { model: 'base', language: 'auto', translate: false, speed: 'balanced' }
export function captionPrefs(): CaptionPrefs {
  try { return { ...DEFAULT_PREFS, ...JSON.parse(localStorage.getItem(PREFS) || '{}') } } catch { return DEFAULT_PREFS }
}
export function setCaptionPrefs(v: CaptionPrefs) {
  try { localStorage.setItem(PREFS, JSON.stringify(v)) } catch { /* storage unavailable */ }
}
export const startLiveCaptions = (prefs = captionPrefs()) => player('liveCaptions', { on: true, ...prefs })
export const stopLiveCaptions = () => player('liveCaptions', { on: false })
export function captionLabel(c: LiveCaptionState | null): string {
  if (!c) return ''
  if (c.error) return c.error
  if (c.status.startsWith('Downloading')) return c.status.replace('Downloading speech model', 'Downloading the speech model — only the first time —')
  if (c.status === 'Done') return c.saved ? 'Whole file done · saved next to the video, loads automatically next time' : `Done · ${c.count} lines`
  if (c.status === 'Listening') {
    const here = c.ready ? 'Showing where you are' : 'First lines in a few seconds'
    return c.whole ? `${here} · whole file ${Math.round(c.progress * 100)}%` : `${here} · ${c.count} lines`
  }
  return c.status
}

let state: PlayerState | null = null
const subs = new Set<() => void>()
let frame: string | null = null
const frameSubs = new Set<() => void>()

export function setPlayerFrame(next: string | null) {
  frame = next
  frameSubs.forEach(s => s())
}

export function usePlayerFrame() {
  return useSyncExternalStore(cb => { frameSubs.add(cb); return () => frameSubs.delete(cb) }, () => frame)
}

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
  ['J / L', 'Back or forward 10 seconds'],
  ['C', 'Generate subtitles on or off'],
  ['Shift + S', 'Save the current frame as a picture'],
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
    case 'j': case 'J': player('seekBy', { ms: -10000 }); break
    case 'l': case 'L': player('seekBy', { ms: 10000 }); break
    case 'S': if (e.shiftKey) player('snapshot'); else return false; break
    default: return false
  }
  e.preventDefault()
  return true
}
