import { useSyncExternalStore } from 'react'
import { api, type Job, type Settings } from './api'

export interface PairRequest { id: string; code: string; name: string; expires: string }

interface State {
  connected: boolean
  authenticated: boolean | null
  jobs: Map<number, Job>
  settings: Settings | null
  pairs: PairRequest[]
  history: { down: number; up: number }[]
  version: number
  update: UpdateState | null
}

export interface UpdateState { state: 'ready' | 'current' | 'error' | 'unsupported' | 'applying' | 'checking'; version?: string | null; current?: string | null; error?: string | null }

const HISTORY = 90
let state: State = {
  connected: false,
  authenticated: null,
  jobs: new Map(),
  settings: null,
  pairs: [],
  history: Array.from({ length: HISTORY }, () => ({ down: 0, up: 0 })),
  version: 0,
  update: null,
}
const listeners = new Set<() => void>()
const completedListeners = new Set<(j: Job) => void>()

function set(patch: Partial<State>) {
  state = { ...state, ...patch, version: state.version + 1 }
  listeners.forEach(l => l())
}

export function useStore<T>(select: (s: State) => T): T {
  return useSyncExternalStore(cb => { listeners.add(cb); return () => listeners.delete(cb) }, () => select(state))
}

export const getState = () => state

export function onCompleted(fn: (j: Job) => void) {
  completedListeners.add(fn)
  return () => { completedListeners.delete(fn) }
}

function upsert(job: Job, removeIfDone = true) {
  const jobs = new Map(state.jobs)
  const prev = jobs.get(job.id)
  if (job.status === 'Completed' && removeIfDone) {
    jobs.delete(job.id)
    if (prev && prev.status !== 'Completed') completedListeners.forEach(f => f(job))
  } else {
    jobs.set(job.id, prev ? { ...prev, ...job, segments: job.segments ?? prev.segments } : job)
  }
  return jobs
}

let source: EventSource | null = null
let sampleTimer: number | undefined

export function connect() {
  source?.close()
  source = new EventSource('/api/events')
  source.addEventListener('open', () => set({ connected: true }))
  source.addEventListener('error', () => {
    set({ connected: false })
    void checkSession()
  })
  source.addEventListener('jobs', e => {
    const list: Job[] = JSON.parse((e as MessageEvent).data)
    set({ jobs: new Map(list.map(j => [j.id, j])), connected: true })
  })
  source.addEventListener('job', e => set({ jobs: upsert(JSON.parse((e as MessageEvent).data)) }))
  source.addEventListener('tick', e => {
    const list: Job[] = JSON.parse((e as MessageEvent).data)
    let jobs = state.jobs
    for (const j of list) {
      const prev = jobs.get(j.id)
      if (!prev) continue
      if (jobs === state.jobs) jobs = new Map(jobs)
      jobs.set(j.id, { ...prev, ...j })
    }
    set({ jobs })
  })
  source.addEventListener('settings', e => set({ settings: JSON.parse((e as MessageEvent).data) }))
  source.addEventListener('pair', e => set({ pairs: [...state.pairs, JSON.parse((e as MessageEvent).data)] }))
  source.addEventListener('pair-done', e => {
    const { id } = JSON.parse((e as MessageEvent).data)
    set({ pairs: state.pairs.filter(p => p.id !== id) })
  })
  window.clearInterval(sampleTimer)
  sampleTimer = window.setInterval(() => {
    let down = 0, up = 0
    state.jobs.forEach(j => { down += j.speed || 0; up += j.uploadSpeed || 0 })
    set({ history: [...state.history.slice(1), { down, up }] })
  }, 1000)
}

export async function checkSession() {
  try {
    const s = await api.get<{ authenticated: boolean }>('/api/session')
    if (s.authenticated !== state.authenticated) set({ authenticated: s.authenticated })
    return s.authenticated
  } catch {
    return state.authenticated ?? false
  }
}

export async function boot() {
  const ok = await checkSession()
  if (!ok) return
  const settings = await api.get<Settings>('/api/settings')
  set({ settings })
  connect()
}

window.addEventListener('fg-offline', () => set({ connected: false }))

window.addEventListener('fg-unauthorized', () => {
  source?.close()
  set({ authenticated: false, connected: false })
})

export function setSettings(settings: Settings) { set({ settings }) }

export function setUpdate(update: UpdateState) { set({ update }) }

export function patchJob(job: Job) { set({ jobs: upsert(job) }) }

export function dismissPair(id: string) { set({ pairs: state.pairs.filter(p => p.id !== id) }) }
