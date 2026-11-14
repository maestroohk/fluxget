import { useEffect, useMemo, useState } from 'react'
import { AlertTriangle, ArrowDownToLine, Clapperboard, Link2, Magnet } from 'lucide-react'
import { api, type Job, type SystemInfo } from '../api'
import { useStore } from '../store'
import { ago, bytes, displayName, extension, isActive, speed } from '../format'
import { Card, Empty, SectionTitle, Sparkline } from '../ui'
import { FileIcon, JobRow } from '../components/jobs'
import { JobDetail } from '../components/JobDetail'
import { navigate } from '../router'

export function Overview({ openAdd }: { openAdd: (t?: string) => void }) {
  const jobs = useStore(s => s.jobs)
  const history = useStore(s => s.history)
  const [system, setSystem] = useState<SystemInfo | null>(null)
  const [recent, setRecent] = useState<Job[]>([])
  const [open, setOpen] = useState<Job | null>(null)

  const all = useMemo(() => [...jobs.values()], [jobs])
  const active = all.filter(j => isActive(j.status))
  const queued = all.filter(j => j.status === 'Queued' || j.status === 'Scheduled')
  const attention = all.filter(j => j.status === 'NeedsAttention' || j.status === 'Failed')
  const completedKey = system?.completedCount

  useEffect(() => {
    void api.get<SystemInfo>('/api/system').then(setSystem).catch(() => {})
    void api.get<{ items: Job[] }>('/api/jobs/completed?limit=6').then(r => setRecent(r.items)).catch(() => {})
  }, [all.length, completedKey])

  const down = history[history.length - 1]?.down ?? 0
  const peak = Math.max(...history.map(h => h.down))

  return (
    <div className="h-full overflow-y-auto">
      <div className="mx-auto max-w-6xl space-y-8 px-4 py-6 sm:px-6">
        <Card className="overflow-hidden">
          <div className="grid gap-px bg-line md:grid-cols-[1.4fr_1fr_1fr_1fr]">
            <div className="bg-surface p-5">
              <div className="text-[12px] text-muted">Downloading now</div>
              <div className="num mt-1 text-[34px] leading-none font-semibold tracking-tight">{down > 0 ? speed(down) : <span className="font-sans text-faint">Idle</span>}</div>
              <div className="mt-1.5 text-[12px] text-muted">{peak > 0 ? `peak ${speed(peak)} in the last 90 s` : 'No transfers in the last 90 s'}</div>
              <Sparkline values={history.map(h => h.down)} height={48} className="mt-3" />
            </div>
            <Metric label="Active" value={String(active.length)} sub={`${queued.length} waiting`} />
            <Metric label="Completed" value={system ? String(system.completedCount) : '—'} sub={system ? bytes(system.completedBytes) + ' total' : ''} />
            <Metric label="Free space" value={system ? bytes(system.freeBytes) : '—'} sub={system?.downloadDirectory ?? ''} />
          </div>
        </Card>

        {all.length === 0 && recent.length === 0 ? (
          <Card>
            <Empty icon={<ArrowDownToLine size={20} />} title="Nothing downloading yet"
              action={<div className="flex flex-wrap justify-center gap-2">
                <QuickAction icon={<Link2 size={15} />} label="Paste a link" onClick={() => openAdd()} />
                <QuickAction icon={<Clapperboard size={15} />} label="Get a video" onClick={() => navigate('media')} />
                <QuickAction icon={<Magnet size={15} />} label="Add a torrent" onClick={() => navigate('torrents')} />
              </div>}>
              Paste any link with Ctrl+V — files, video pages and magnet links all work. Large files are split across several connections when the server allows it.
            </Empty>
          </Card>
        ) : null}

        {attention.length > 0 && (
          <section>
            <SectionTitle><span className="inline-flex items-center gap-1.5 text-warn"><AlertTriangle size={13} /> Needs your attention</span></SectionTitle>
            <Card className="overflow-hidden">{attention.map(j => <JobRow key={j.id} job={j} onOpen={setOpen} />)}</Card>
          </section>
        )}

        {(active.length > 0 || queued.length > 0) && (
          <section>
            <SectionTitle aside={<a href="#/downloads" className="text-[12.5px] text-muted hover:text-ink">View all</a>}>In progress</SectionTitle>
            <Card className="overflow-hidden">{[...active, ...queued].slice(0, 6).map(j => <JobRow key={j.id} job={j} onOpen={setOpen} />)}</Card>
          </section>
        )}

        {recent.length > 0 && (
          <section>
            <SectionTitle aside={<a href="#/completed" className="text-[12.5px] text-muted hover:text-ink">Library</a>}>Recently finished</SectionTitle>
            <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
              {recent.map(j => (
                <button key={j.id} onClick={() => setOpen(j)} className="flex items-start gap-3 rounded-xl border border-line bg-surface p-4 text-left shadow-card transition hover:-translate-y-px hover:border-line-strong">
                  <FileIcon job={j} />
                  <div className="min-w-0 flex-1">
                    <div className="line-clamp-2 text-[13px] leading-snug font-medium" title={displayName(j)}>{cleanTitle(displayName(j))}</div>
                    <div className="mt-1.5 flex items-center gap-1.5 overflow-hidden text-[11.5px] whitespace-nowrap text-muted">
                      {badge(j) && <span className="rounded-md bg-sunken px-1.5 py-0.5 font-medium text-ink-2">{badge(j)}</span>}
                      <span className="num">{bytes(j.totalBytes)}</span>
                      <span>·</span>
                      <span>{ago(j.completedAt)}</span>
                    </div>
                  </div>
                </button>
              ))}
            </div>
          </section>
        )}
      </div>
      <JobDetail jobId={open?.id ?? null} fallback={open} onClose={() => setOpen(null)} />
    </div>
  )
}

function Metric({ label, value, sub }: { label: string; value: string; sub: string }) {
  return (
    <div className="bg-surface p-5">
      <div className="text-[12px] text-muted">{label}</div>
      <div className="num mt-1 text-[26px] leading-none font-semibold tracking-tight">{value}</div>
      <div className="mt-1.5 truncate text-[12px] text-muted" title={sub}>{sub}</div>
    </div>
  )
}

function QuickAction({ icon, label, onClick }: { icon: React.ReactNode; label: string; onClick: () => void }) {
  return (
    <button onClick={onClick} className="inline-flex h-9 items-center gap-2 rounded-lg border border-line bg-raised px-3.5 text-[13px] font-medium hover:border-line-strong">
      <span className="text-accent">{icon}</span>{label}
    </button>
  )
}

function cleanTitle(name: string) {
  return name.replace(/\s*\[[^\]]*\]/g, '').replace(/\.[a-z0-9]{2,5}$/i, '')
}

function badge(j: Job) {
  const ext = extension(j.fileName).toUpperCase()
  const quality = j.fileName?.match(/\[(\d{3,4}p|\d{2,3}k)\]/i)?.[1]
  return [quality, ext].filter(Boolean).join(' ')
}
