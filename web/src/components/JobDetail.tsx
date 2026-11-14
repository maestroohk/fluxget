import { useEffect, useState } from 'react'
import { AlertTriangle, Copy, ExternalLink, FolderOpen, Link2, MonitorPlay, Pause, Play, RotateCcw, Trash2 } from 'lucide-react'
import { api, desktop, type Job } from '../api'
import { player } from '../player'
import { navigate } from '../router'
import { useStore } from '../store'
import { ago, bytes, canPause, canResume, displayName, duration, isActive, isPlayable, limitText, parseLimit, percent, speed, statusInfo } from '../format'
import { Button, cx, Drawer, Field, inputClass, Pill, Progress, SegmentMap, Sparkline, Toggle } from '../ui'
import { FileIcon, jobActions } from './jobs'
import { attempt, toast } from './toast'
import { TorrentPanel } from './TorrentPanel'

export function JobDetail({ jobId, fallback, onClose }: { jobId: number | null; fallback?: Job | null; onClose: () => void }) {
  const live = useStore(s => (jobId != null ? s.jobs.get(jobId) : undefined))
  const job = live ?? (fallback && fallback.id === jobId ? fallback : undefined)
  const [history, setHistory] = useState<number[]>(() => Array(60).fill(0))

  useEffect(() => { setHistory(Array(60).fill(0)) }, [jobId])
  useEffect(() => {
    const t = setInterval(() => setHistory(h => [...h.slice(1), live?.speed ?? 0]), 1000)
    return () => clearInterval(t)
  }, [live?.speed])

  if (!job) return null
  const info = statusInfo[job.status]
  const p = percent(job)
  const active = isActive(job.status)

  return (
    <Drawer open onClose={onClose} title={
      <div className="flex items-start gap-3">
        <FileIcon job={job} size={20} />
        <div className="min-w-0">
          <div className="truncate text-[15px] font-semibold tracking-tight">{displayName(job)}</div>
          <div className="mt-1 flex flex-wrap items-center gap-2">
            <Pill tone={info.tone} dot={active}>{info.label}</Pill>
            {job.phase && <span className="text-[12px] text-muted">{job.phase}</span>}
          </div>
        </div>
      </div>
    }>
      <div className="space-y-6 px-5 py-5">
        {job.error && <ErrorBox job={job} />}

        <div>
          <div className="mb-2 flex items-end justify-between">
            <span className="num text-[28px] leading-none font-semibold tracking-tight">{p != null ? `${p.toFixed(1)}%` : bytes(job.receivedBytes)}</span>
            <span className="num text-[13px] text-muted">{bytes(job.receivedBytes)} / {bytes(job.totalBytes)}</span>
          </div>
          <Progress value={p} flowing={job.status === 'Running'} tone={job.status === 'Completed' ? 'ok' : 'accent'} />
        </div>

        <dl className="grid grid-cols-2 gap-px overflow-hidden rounded-xl border border-line bg-line sm:grid-cols-4">
          <Stat label="Speed" value={speed(job.speed)} />
          <Stat label="Remaining" value={active ? duration(job.eta) : '—'} />
          <Stat label={job.kind === 'Torrent' ? 'Peers' : 'Connections'} value={job.kind === 'Torrent' ? String(job.extra?.peers ?? '—') : active ? `${job.connections} / ${job.maxConnections}` : '—'} />
          <Stat label="Resume" value={job.kind === 'Http' ? (job.resumable ? 'Supported' : 'Not supported') : 'Supported'} />
        </dl>

        {active && (
          <div>
            <div className="mb-1 text-[12px] font-medium text-muted">Speed, last minute</div>
            <Sparkline values={history} height={56} />
          </div>
        )}

        {job.kind === 'Http' && job.segments && job.totalBytes ? (
          <div>
            <div className="mb-2 flex items-center justify-between text-[12px] font-medium text-muted">
              <span>Segments</span>
              <span className="num">{job.segments.length} parts · {job.segments.filter(s => s.active).length} active</span>
            </div>
            <SegmentMap segments={job.segments} total={job.totalBytes} />
            <div className="mt-3 max-h-48 overflow-y-auto rounded-lg border border-line">
              {job.segments.map((s, i) => {
                const len = s.end - s.start + 1
                return (
                  <div key={i} className="num flex items-center gap-3 border-b border-line px-3 py-1.5 text-[11.5px] last:border-0">
                    <span className={cx('size-1.5 rounded-full', s.active ? 'bg-accent' : s.received >= len ? 'bg-ok' : 'bg-faint')} />
                    <span className="w-40 text-muted">{bytes(s.start)} – {bytes(s.end)}</span>
                    <span className="flex-1"><Progress value={len > 0 ? (s.received / len) * 100 : 0} tone={s.received >= len ? 'ok' : 'accent'} /></span>
                    <span className="w-16 text-right">{bytes(s.received)}</span>
                  </div>
                )
              })}
            </div>
          </div>
        ) : null}

        {job.kind === 'Torrent' && <TorrentPanel job={job} />}

        <div className="flex flex-wrap gap-2">
          {canPause(job.status) && <Button onClick={() => jobActions.pause(job)}><Pause size={15} /> Pause</Button>}
          {canResume(job.status) && <Button variant="primary" onClick={() => jobActions.resume(job)}><Play size={15} /> {job.status === 'Paused' ? 'Resume' : 'Retry'}</Button>}
          {desktop.available() && job.status === 'Completed' && isPlayable(job.filePath) && (
            <Button variant="primary" onClick={() => { player('openJob', { jobId: job.id, path: job.filePath, title: displayName(job) }); onClose(); navigate('player') }}><MonitorPlay size={15} /> Play</Button>
          )}
          {desktop.available() && job.status !== 'Completed' && job.kind === 'Http' && isPlayable(job.fileName) && (
            <Button onClick={() => attempt(async () => {
              const info = await api.get<{ available: boolean; reason: string | null }>(`/api/player/preview/${job.id}`)
              if (!info.available) throw new Error(info.reason ?? 'Preview is not available yet.')
              player('openJob', { jobId: job.id, title: displayName(job) }); onClose(); navigate('player')
            })}><MonitorPlay size={15} /> Preview</Button>
          )}
          {job.status === 'Completed' && <>
            <Button variant={isPlayable(job.filePath) && desktop.available() ? 'secondary' : 'primary'} onClick={() => jobActions.open(job)}><ExternalLink size={15} /> Open</Button>
            <Button onClick={() => jobActions.open(job, true)}><FolderOpen size={15} /> Show in folder</Button>
          </>}
          {job.status !== 'Completed' && <Button onClick={() => jobActions.restart(job)}><RotateCcw size={15} /> Restart</Button>}
          <RemoveButton job={job} onDone={onClose} />
        </div>

        {job.status !== 'Completed' && <JobSettings job={job} />}

        <dl className="space-y-3 text-[13px]">
          <Info label="Address">
            <div className="flex items-start gap-1">
              <span className="min-w-0 flex-1 break-all text-ink-2">{job.url}</span>
              <button className="shrink-0 text-muted hover:text-ink" aria-label="Copy address" onClick={() => navigator.clipboard.writeText(job.url).then(() => toast('Address copied', 'ok'))}><Copy size={14} /></button>
            </div>
          </Info>
          <Info label="Saved to"><span className="break-all text-ink-2">{job.filePath ?? job.directory}</span></Info>
          {job.checksum && <Info label="Checksum"><span className="num break-all text-ink-2">{job.checksum}</span></Info>}
          <Info label="Added"><span className="text-ink-2">{ago(job.createdAt)}</span></Info>
          {job.completedAt && <Info label="Completed"><span className="text-ink-2">{ago(job.completedAt)}</span></Info>}
        </dl>
      </div>
    </Drawer>
  )
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div className="bg-surface px-3.5 py-3">
      <dt className="text-[11.5px] text-muted">{label}</dt>
      <dd className="num mt-0.5 text-[14px] font-medium">{value}</dd>
    </div>
  )
}

function Info({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="grid grid-cols-[96px_1fr] gap-3">
      <dt className="text-muted">{label}</dt>
      <dd className="min-w-0">{children}</dd>
    </div>
  )
}

function ErrorBox({ job }: { job: Job }) {
  const [url, setUrl] = useState('')
  const expired = /expired|refresh the address/i.test(job.error ?? '')
  const changed = /changed/i.test(job.error ?? '')
  return (
    <div className={cx('rounded-xl border p-4', job.status === 'Failed' ? 'border-bad/30 bg-bad-soft' : 'border-warn/30 bg-warn-soft')}>
      <div className="flex gap-2.5">
        <AlertTriangle size={16} className={cx('mt-0.5 shrink-0', job.status === 'Failed' ? 'text-bad' : 'text-warn')} />
        <p className="text-[13px] text-ink">{job.error}</p>
      </div>
      {expired && (
        <form className="mt-3 flex gap-2" onSubmit={e => { e.preventDefault(); void attempt(() => api.post(`/api/jobs/${job.id}/address`, { url }), 'Address updated — resuming') }}>
          <input className={inputClass} placeholder="Paste a fresh link to the same file" value={url} onChange={e => setUrl(e.target.value)} />
          <Button variant="primary" disabled={!url}><Link2 size={15} /> Update</Button>
        </form>
      )}
      {changed && <div className="mt-3"><Button size="sm" onClick={() => jobActions.restart(job)}><RotateCcw size={14} /> Download the new version</Button></div>}
    </div>
  )
}

function JobSettings({ job }: { job: Job }) {
  const [limit, setLimit] = useState(limitText(job.speedLimit))
  useEffect(() => setLimit(limitText(job.speedLimit)), [job.id, job.speedLimit])
  const update = (body: object) => attempt(() => api.patch(`/api/jobs/${job.id}`, body))
  return (
    <div className="grid gap-4 rounded-xl border border-line p-4 sm:grid-cols-3">
      <Field label="Speed limit" hint="e.g. 500 KB or 2 MB">
        <input className={inputClass} value={limit} placeholder="Unlimited" onChange={e => setLimit(e.target.value)}
          onBlur={() => update({ speedLimit: parseLimit(limit) })} onKeyDown={e => e.key === 'Enter' && (e.target as HTMLInputElement).blur()} />
      </Field>
      <Field label="Priority">
        <select className={inputClass} value={job.priority > 0 ? 5 : job.priority < 0 ? -5 : 0} onChange={e => update({ priority: Number(e.target.value) })}>
          <option value={5}>High</option>
          <option value={0}>Normal</option>
          <option value={-5}>Low</option>
        </select>
      </Field>
      <Field label="Use schedule">
        <div className="flex h-9 items-center"><Toggle label="Use schedule" checked={job.useSchedule} onChange={v => update({ useSchedule: v })} /></div>
      </Field>
    </div>
  )
}

function RemoveButton({ job, onDone }: { job: Job; onDone: () => void }) {
  const [confirm, setConfirm] = useState(false)
  if (!confirm) return <Button variant="ghost" onClick={() => setConfirm(true)}><Trash2 size={15} /> Remove</Button>
  return (
    <div className="flex flex-wrap items-center gap-2 rounded-lg bg-sunken p-1">
      <Button size="sm" variant="ghost" onClick={() => jobActions.remove(job).then(ok => ok && onDone())}>Remove from list</Button>
      <Button size="sm" variant="danger" onClick={() => jobActions.remove(job, true).then(ok => ok && onDone())}>Remove and delete file</Button>
      <Button size="sm" variant="ghost" onClick={() => setConfirm(false)}>Cancel</Button>
    </div>
  )
}
