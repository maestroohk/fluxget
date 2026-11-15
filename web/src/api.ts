export type JobKind = 'Http' | 'Media' | 'Torrent'
export type JobStatus =
  | 'Queued' | 'Scheduled' | 'Running' | 'Retrying' | 'WaitingForNetwork' | 'Verifying'
  | 'Paused' | 'Seeding' | 'NeedsAttention' | 'Failed' | 'Completed'

export interface Segment { start: number; end: number; received: number; active: boolean }

export interface Job {
  id: number
  kind: JobKind
  url: string
  fileName: string | null
  directory: string
  filePath: string | null
  group: string | null
  totalBytes: number | null
  receivedBytes: number
  speed: number
  uploadSpeed: number
  eta: number | null
  connections: number
  maxConnections: number
  status: JobStatus
  phase: string | null
  error: string | null
  resumable: boolean
  priority: number
  speedLimit: number
  checksum: string | null
  useSchedule: boolean
  createdAt: string
  completedAt: string | null
  segments: Segment[] | null
  extra: Record<string, unknown> | null
  options: string | null
}

export interface Schedule { enabled: boolean; start: string; end: string; days: number[] }

export interface Settings {
  downloadDirectory: string
  maxConcurrentJobs: number
  connectionsPerDownload: number
  globalSpeedLimit: number
  torrentUploadLimit: number
  seedRatio: number
  schedule: Schedule
  notifyOnComplete: boolean
  browserMediaDetection: boolean
  ui?: Record<string, unknown> | null
}

export interface SystemInfo {
  version: string
  mode: string
  downloadDirectory: string
  freeBytes: number | null
  completedCount: number
  completedBytes: number
  tools: { ytDlp: string | null; galleryDl?: string | null; ffmpeg: string | null; ffprobe: string | null; jsRuntime: string | null } | null
}

export interface AddResult {
  added: Job[]
  duplicates: { url: string; existingId: number }[]
  rejected: { url: string; reason: string }[]
}

export class ApiError extends Error {
  constructor(public status: number, message: string) { super(message) }
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const init: RequestInit = { method, credentials: 'same-origin', headers: {} }
  if (body instanceof FormData) init.body = body
  else if (body !== undefined) {
    init.body = JSON.stringify(body)
    ;(init.headers as Record<string, string>)['Content-Type'] = 'application/json'
  }
  let res: Response
  try {
    res = await fetch(path, init)
  } catch {
    window.dispatchEvent(new Event('fg-offline'))
    throw new ApiError(0, "Can't reach FluxGet. Make sure the app is running.")
  }
  if (res.status === 401) {
    window.dispatchEvent(new Event('fg-unauthorized'))
    throw new ApiError(401, 'Not signed in')
  }
  if (!res.ok) {
    let message = `Request failed (${res.status})`
    try {
      const p = await res.json()
      message = p.detail || p.title || message
    } catch { /* not json */ }
    throw new ApiError(res.status, message)
  }
  if (res.status === 204) return undefined as T
  const type = res.headers.get('content-type') || ''
  return (type.includes('json') ? res.json() : res.text()) as Promise<T>
}

export const api = {
  get: <T>(p: string) => request<T>('GET', p),
  post: <T>(p: string, b?: unknown) => request<T>('POST', p, b ?? {}),
  put: <T>(p: string, b: unknown) => request<T>('PUT', p, b),
  patch: <T>(p: string, b: unknown) => request<T>('PATCH', p, b),
  del: <T>(p: string) => request<T>('DELETE', p),
}

export const desktop = {
  available: () => typeof window !== 'undefined' && !!(window as any).chrome?.webview,
  send(message: unknown) { (window as any).chrome?.webview?.postMessage(message) },
  pickFolder(current: string): Promise<string | null> {
    return new Promise(resolve => {
      const wv = (window as any).chrome?.webview
      if (!wv) return resolve(null)
      const handler = (e: MessageEvent) => {
        const d = e.data
        if (d && d.type === 'folder') {
          wv.removeEventListener('message', handler)
          resolve(d.path ?? null)
        }
      }
      wv.addEventListener('message', handler)
      wv.postMessage({ type: 'pickFolder', current })
    })
  },
}
