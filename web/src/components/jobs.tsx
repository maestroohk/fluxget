import { useRef, type ReactNode } from 'react'
import { useVirtualizer } from '@tanstack/react-virtual'
import {
  Archive, Disc3, File, FileAudio, FileImage, FileText, FileVideo, Magnet, Package, Pause, Play, RotateCcw, Trash2, Clapperboard,
} from 'lucide-react'
import { api, type Job } from '../api'
import { bytes, canPause, canResume, displayName, duration, extension, host, isActive, percent, speed, statusInfo } from '../format'
import { cx, IconButton, Pill, Progress } from '../ui'
import { attempt } from './toast'

export function FileIcon({ job, size = 18 }: { job: Job; size?: number }) {
  const ext = extension(job.fileName)
  let icon: ReactNode = <File size={size} />
  if (job.kind === 'Torrent') icon = <Magnet size={size} />
  else if (job.kind === 'Media') icon = (job.options?.includes('"audio"') ? <FileAudio size={size} /> : <Clapperboard size={size} />)
  else if (['zip', 'rar', '7z', 'gz', 'tar', 'xz', 'bz2', 'zst'].includes(ext)) icon = <Archive size={size} />
  else if (['iso', 'img', 'dmg', 'vhd', 'vhdx'].includes(ext)) icon = <Disc3 size={size} />
  else if (['exe', 'msi', 'msix', 'appx', 'deb', 'rpm', 'pkg', 'apk', 'appimage'].includes(ext)) icon = <Package size={size} />
  else if (['mp4', 'mkv', 'webm', 'mov', 'avi', 'm4v'].includes(ext)) icon = <FileVideo size={size} />
  else if (['mp3', 'm4a', 'flac', 'wav', 'opus', 'ogg', 'aac'].includes(ext)) icon = <FileAudio size={size} />
  else if (['png', 'jpg', 'jpeg', 'gif', 'webp', 'svg', 'avif'].includes(ext)) icon = <FileImage size={size} />
  else if (['pdf', 'doc', 'docx', 'txt', 'md', 'epub', 'xlsx', 'pptx'].includes(ext)) icon = <FileText size={size} />
  return (
    <div className={cx('flex size-9 shrink-0 items-center justify-center rounded-[10px] border', isActive(job.status) ? 'border-accent/25 bg-accent-soft text-accent' : 'border-line bg-raised text-muted')}>
      {icon}
    </div>
  )
}

export const jobActions = {
  pause: (j: Job) => attempt(() => api.post(`/api/jobs/${j.id}/pause`)),
  resume: (j: Job) => attempt(() => api.post(`/api/jobs/${j.id}/resume`)),
  restart: (j: Job) => attempt(() => api.post(`/api/jobs/${j.id}/restart`), 'Restarted from the beginning'),
  remove: (j: Job, deleteFile = false) => attempt(() => api.del(`/api/jobs/${j.id}?deleteFile=${deleteFile}`)),
  open: (j: Job, reveal = false) => attempt(() => api.post(`/api/jobs/${j.id}/open?reveal=${reveal}`)),
}

export function StatusLine({ job }: { job: Job }) {
  const p = percent(job)
  const active = isActive(job.status)
  const parts: ReactNode[] = []
  parts.push(<span key="b" className="num">{job.totalBytes ? `${bytes(job.receivedBytes)} of ${bytes(job.totalBytes)}` : bytes(job.receivedBytes)}</span>)
  if (p != null && job.status !== 'Completed') parts.push(<span key="p" className="num">{p.toFixed(p < 10 ? 1 : 0)}%</span>)
  if (active && job.eta != null) parts.push(<span key="e" className="num">{duration(job.eta)} left</span>)
  if (!active) parts.push(<span key="h" className="truncate">{job.group ?? host(job.url)}</span>)
  return <div className="flex min-w-0 items-center gap-2 text-[12px] text-muted [&>*+*]:before:mr-2 [&>*+*]:before:text-faint [&>*+*]:before:content-['·']">{parts}</div>
}

export function JobRow({ job, onOpen }: { job: Job; onOpen: (j: Job) => void }) {
  const info = statusInfo[job.status]
  const active = isActive(job.status)
  const p = percent(job)
  const tone = job.status === 'Failed' ? 'bad' : job.status === 'NeedsAttention' || job.status === 'Retrying' ? 'warn' : job.status === 'Seeding' ? 'ok' : active ? 'accent' : 'muted'
  return (
    <div
      role="button"
      tabIndex={0}
      onClick={() => onOpen(job)}
      onKeyDown={e => (e.key === 'Enter' || e.key === ' ') && (e.preventDefault(), onOpen(job))}
      className="group flex h-[72px] cursor-pointer items-center gap-3 border-b border-line px-4 transition-colors hover:bg-raised sm:px-5"
    >
      <FileIcon job={job} />
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2">
          <span className="truncate text-[13.5px] font-medium">{displayName(job)}</span>
        </div>
        <Progress value={p} tone={tone} flowing={active && job.status === 'Running'} className="my-1.5" />
        <StatusLine job={job} />
      </div>
      <div className="hidden w-24 text-right sm:block">
        <div className="num text-[13px] font-medium">{active || job.status === 'Seeding' ? speed(job.speed) : ''}</div>
        {job.kind === 'Http' && active && <div className="num text-[11.5px] text-muted">{job.connections}/{job.maxConnections} conn</div>}
        {job.kind === 'Torrent' && job.uploadSpeed > 0 && <div className="num text-[11.5px] text-muted">↑ {speed(job.uploadSpeed)}</div>}
      </div>
      <div className="hidden w-36 justify-end md:flex">
        <Pill tone={info.tone} dot={active}>{job.status === 'Running' && job.phase ? short(job.phase) : info.label}</Pill>
      </div>
      <div className="flex w-[72px] justify-end gap-0.5" onClick={e => e.stopPropagation()}>
        {canPause(job.status) && <IconButton label="Pause" onClick={() => jobActions.pause(job)}><Pause size={16} /></IconButton>}
        {canResume(job.status) && <IconButton label={job.status === 'Paused' ? 'Resume' : 'Retry'} onClick={() => jobActions.resume(job)}>{job.status === 'Paused' ? <Play size={16} /> : <RotateCcw size={16} />}</IconButton>}
        <IconButton label="Remove" className="opacity-0 group-hover:opacity-100 focus:opacity-100" onClick={() => jobActions.remove(job)}><Trash2 size={15} /></IconButton>
      </div>
    </div>
  )
}

const short = (s: string) => (s.length > 22 ? s.slice(0, 21) + '…' : s)

export function JobList({ jobs, onOpen, empty }: { jobs: Job[]; onOpen: (j: Job) => void; empty: ReactNode }) {
  const parent = useRef<HTMLDivElement>(null)
  const v = useVirtualizer({ count: jobs.length, getScrollElement: () => parent.current, estimateSize: () => 72, overscan: 8 })
  if (jobs.length === 0) return <>{empty}</>
  return (
    <div ref={parent} className="h-full overflow-y-auto">
      <div style={{ height: v.getTotalSize(), position: 'relative' }}>
        {v.getVirtualItems().map(item => (
          <div key={jobs[item.index].id} style={{ position: 'absolute', top: 0, left: 0, right: 0, transform: `translateY(${item.start}px)` }}>
            <JobRow job={jobs[item.index]} onOpen={onOpen} />
          </div>
        ))}
      </div>
    </div>
  )
}
