import { useMemo, useState } from 'react'
import { Download, Pause, Play, Search } from 'lucide-react'
import { api, type Job } from '../api'
import { useStore } from '../store'
import { displayName, isActive } from '../format'
import { Button, cx, Empty, inputClass } from '../ui'
import { JobList } from '../components/jobs'
import { JobDetail } from '../components/JobDetail'
import { attempt } from '../components/toast'

const FILTERS = [
  { id: 'all', label: 'All', test: () => true },
  { id: 'active', label: 'Active', test: (j: Job) => isActive(j.status) },
  { id: 'queued', label: 'Queued', test: (j: Job) => j.status === 'Queued' || j.status === 'Scheduled' },
  { id: 'paused', label: 'Paused', test: (j: Job) => j.status === 'Paused' },
  { id: 'attention', label: 'Attention', test: (j: Job) => j.status === 'NeedsAttention' || j.status === 'Failed' },
] as const

export function Downloads({ openAdd, kind = 'Http' }: { openAdd: (t?: string) => void; kind?: Job['kind'] }) {
  const jobs = useStore(s => s.jobs)
  const [filter, setFilter] = useState<(typeof FILTERS)[number]['id']>('all')
  const [query, setQuery] = useState('')
  const [open, setOpen] = useState<number | null>(null)

  const mine = useMemo(() => [...jobs.values()].filter(j => j.kind === kind), [jobs, kind])
  const list = useMemo(() => {
    const f = FILTERS.find(x => x.id === filter)!
    const q = query.trim().toLowerCase()
    return mine
      .filter(f.test)
      .filter(j => !q || displayName(j).toLowerCase().includes(q) || j.url.toLowerCase().includes(q))
      .sort((a, b) => rank(a) - rank(b) || b.priority - a.priority || a.id - b.id)
  }, [mine, filter, query])

  return (
    <div className="flex h-full flex-col">
      <div className="flex flex-wrap items-center gap-2 border-b border-line px-4 py-2.5 sm:px-5">
        <div className="flex gap-1 overflow-x-auto">
          {FILTERS.map(f => {
            const n = mine.filter(f.test).length
            return (
              <button key={f.id} onClick={() => setFilter(f.id)}
                className={cx('h-7 shrink-0 rounded-full px-3 text-[12.5px] font-medium transition', filter === f.id ? 'bg-ink text-bg' : 'text-muted hover:bg-sunken hover:text-ink')}>
                {f.label}{n > 0 && <span className="num ml-1.5 opacity-60">{n}</span>}
              </button>
            )
          })}
        </div>
        <div className="flex-1" />
        <div className="relative w-full sm:w-56">
          <Search size={14} className="absolute top-1/2 left-2.5 -translate-y-1/2 text-faint" />
          <input className={inputClass + ' h-8 pl-8'} placeholder="Search" value={query} onChange={e => setQuery(e.target.value)} aria-label="Search downloads" />
        </div>
        <Button size="sm" variant="ghost" onClick={() => attempt(() => api.post('/api/jobs/pause-all'))}><Pause size={14} /> Pause all</Button>
        <Button size="sm" variant="ghost" onClick={() => attempt(() => api.post('/api/jobs/resume-all'))}><Play size={14} /> Resume all</Button>
      </div>
      <div className="min-h-0 flex-1">
        <JobList jobs={list} onOpen={j => setOpen(j.id)} empty={
          <Empty icon={<Download size={20} />} title={mine.length ? 'Nothing matches' : 'No downloads in progress'}
            action={!mine.length && <Button variant="primary" onClick={() => openAdd()}>Add a download</Button>}>
            {mine.length ? 'Try a different filter or search.' : 'Finished downloads move to Completed. Paste a link anywhere to start.'}
          </Empty>
        } />
      </div>
      <JobDetail jobId={open} onClose={() => setOpen(null)} />
    </div>
  )
}

function rank(j: Job) {
  if (isActive(j.status)) return 0
  if (j.status === 'NeedsAttention' || j.status === 'Failed') return 1
  if (j.status === 'Seeding') return 2
  if (j.status === 'Queued') return 3
  if (j.status === 'Scheduled') return 4
  return 5
}
