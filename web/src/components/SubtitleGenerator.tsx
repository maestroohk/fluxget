import { useEffect, useRef, useState } from 'react'
import { Sparkles, X } from 'lucide-react'
import { api } from '../api'
import { bytes } from '../format'
import { player } from '../player'
import { Button, cx, Field, inputClass, Modal, Toggle } from '../ui'
import { attempt, toast } from './toast'

interface Model { id: string; label: string; description: string; approxSize: number; installed: boolean }
interface Task { id: string; mediaPath: string; status: string; progress: number; detail: string | null; outputPath: string | null; detectedLanguage: string | null; segments: number; error: string | null; elapsedMs: number }

const LANGUAGES: [string, string][] = [
  ['auto', 'Detect automatically'], ['en', 'English'], ['fr', 'French'], ['es', 'Spanish'], ['pt', 'Portuguese'], ['de', 'German'],
  ['it', 'Italian'], ['nl', 'Dutch'], ['sw', 'Swahili'], ['ar', 'Arabic'], ['hi', 'Hindi'], ['zh', 'Chinese'], ['ja', 'Japanese'],
  ['ko', 'Korean'], ['ru', 'Russian'], ['tr', 'Turkish'], ['pl', 'Polish'], ['uk', 'Ukrainian'],
]

export function SubtitleGenerator({ mediaPath }: { mediaPath: string }) {
  const [open, setOpen] = useState(false)
  const [models, setModels] = useState<Model[]>([])
  const [model, setModel] = useState('base')
  const [language, setLanguage] = useState('auto')
  const [translate, setTranslate] = useState(false)
  const [task, setTask] = useState<Task | null>(null)
  const loadedFor = useRef<string | null>(null)

  useEffect(() => {
    void api.get<Task[]>('/api/subtitles/tasks').then(ts => setTask(ts.find(t => t.mediaPath === mediaPath) ?? null)).catch(() => {})
  }, [mediaPath])

  useEffect(() => { if (open) void api.get<Model[]>('/api/subtitles/models').then(setModels).catch(() => {}) }, [open])

  useEffect(() => {
    if (!task || !['queued', 'model', 'audio', 'transcribing'].includes(task.status)) return
    const t = setInterval(async () => {
      try { setTask(await api.get<Task>(`/api/subtitles/tasks/${task.id}`)) } catch { /* retry next tick */ }
    }, 700)
    return () => clearInterval(t)
  }, [task?.id, task?.status])

  useEffect(() => {
    if (task?.status === 'done' && task.outputPath && loadedFor.current !== task.id) {
      loadedFor.current = task.id
      player('addSubtitleFile', { path: task.outputPath })
      toast(`Subtitles ready — ${task.segments} lines${task.detectedLanguage ? ` (${task.detectedLanguage})` : ''}`, 'ok')
    }
    if (task?.status === 'failed' && loadedFor.current !== task.id) {
      loadedFor.current = task.id
      toast(task.error ?? 'Subtitle generation failed', 'bad')
    }
  }, [task])

  const start = () => attempt(async () => {
    setTask(await api.post<Task>('/api/subtitles/generate', { path: mediaPath, model, language, translate }))
    setOpen(false)
  })

  const busy = task && ['queued', 'model', 'audio', 'transcribing'].includes(task.status)
  const chosen = models.find(m => m.id === model)

  return (
    <>
      {busy ? (
        <div className="rounded-lg border border-line bg-raised p-3">
          <div className="flex items-center justify-between gap-2 text-[12.5px]">
            <span className="flex items-center gap-1.5 font-medium"><Sparkles size={13} className="text-accent" /> Generating subtitles</span>
            <button aria-label="Cancel" title="Cancel" className="text-faint hover:text-ink" onClick={() => attempt(() => api.post(`/api/subtitles/tasks/${task!.id}/cancel`))}><X size={14} /></button>
          </div>
          <div className="mt-2 h-1 overflow-hidden rounded-full bg-sunken"><div className="h-full bg-accent transition-[width] duration-500" style={{ width: `${Math.max(3, task!.progress * 100)}%` }} /></div>
          <div className="mt-1.5 text-[11.5px] text-muted">{task!.detail ?? 'Waiting'} · runs on this computer, offline</div>
        </div>
      ) : (
        <Button size="sm" onClick={() => setOpen(true)}><Sparkles size={14} /> Generate subtitles</Button>
      )}
      {task?.status === 'done' && !busy && <p className="text-[11.5px] text-muted">Last run: {task.segments} lines in {(task.elapsedMs / 1000).toFixed(0)} s, saved next to the file.</p>}

      <Modal open={open} onClose={() => setOpen(false)} title="Generate subtitles" footer={<>
        <span className="mr-auto text-[12px] text-muted">{chosen && !chosen.installed ? `First use downloads the ${chosen.label} model (${bytes(chosen.approxSize)}).` : 'Runs entirely on this computer.'}</span>
        <Button variant="ghost" onClick={() => setOpen(false)}>Cancel</Button>
        <Button variant="primary" onClick={start}><Sparkles size={15} /> Generate</Button>
      </>}>
        <div className="space-y-4">
          <p className="text-[13px] text-muted">FluxGet listens to the audio with the open-source Whisper speech model and writes an SRT file next to the video. Nothing is uploaded; after the one-time model download it works offline.</p>
          <Field label="Accuracy">
            <div className="grid gap-2 sm:grid-cols-3">
              {models.map(m => (
                <button key={m.id} type="button" onClick={() => setModel(m.id)}
                  className={cx('rounded-lg border p-3 text-left transition', model === m.id ? 'border-accent bg-accent-soft/50 ring-2 ring-accent/15' : 'border-line hover:border-line-strong')}>
                  <div className="text-[13px] font-semibold">{m.label}</div>
                  <div className="mt-0.5 text-[11.5px] text-muted">{m.description}</div>
                  <div className="num mt-1.5 text-[11px] text-faint">{m.installed ? 'Installed' : bytes(m.approxSize)}</div>
                </button>
              ))}
            </div>
          </Field>
          <Field label="Spoken language">
            <select className={inputClass} value={language} onChange={e => setLanguage(e.target.value)}>
              {LANGUAGES.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
            </select>
          </Field>
          <label className="flex items-center justify-between gap-3 rounded-lg border border-line px-3 py-2.5 text-[13px]">
            <span>Translate into English subtitles</span>
            <Toggle label="Translate into English" checked={translate} onChange={setTranslate} />
          </label>
        </div>
      </Modal>
    </>
  )
}
