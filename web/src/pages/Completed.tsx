import { useCallback, useEffect, useState } from 'react'
import { CheckCheck, ExternalLink, FolderOpen, Search } from 'lucide-react'
import { api, type Job, type JobKind } from '../api'
import { useStore } from '../store'
import { ago, bytes, displayName } from '../format'
import { Button, Empty, IconButton, inputClass, Segmented } from '../ui'
import { FileIcon, jobActions } from '../components/jobs'
import { JobDetail } from '../components/JobDetail'

const PAGE = 100

export function Completed() {
  const [items, setItems] = useState<Job[]>([])
  const [total, setTotal] = useState(0)
  const [query, setQuery] = useState('')
  const [kind, setKind] = useState<'all' | JobKind>('all')
  const [open, setOpen] = useState<Job | null>(null)
  const unfinished = useStore(s => s.jobs.size)

  const load = useCallback(async (offset: number) => {
    const p = new URLSearchParams({ offset: String(offset), limit: String(PAGE) })
    if (query.trim()) p.set('search', query.trim())
    if (kind !== 'all') p.set('kind', kind)
    const r = await api.get<{ total: number; items: Job[] }>(`/api/jobs/completed?${p}`)
    setTotal(r.total)
    setItems(prev => (offset === 0 ? r.items : [...prev, ...r.items]))
  }, [query, kind])

  useEffect(() => {
    const t = setTimeout(() => void load(0), 200)
    return () => clearTimeout(t)
  }, [load, unfinished])

  return (
    <div className="flex h-full flex-col">
      <div className="flex flex-wrap items-center gap-2 border-b border-line px-4 py-2.5 sm:px-5">
        <Segmented value={kind} onChange={setKind} options={[{ value: 'all', label: 'All' }, { value: 'Http', label: 'Files' }, { value: 'Media', label: 'Media' }, { value: 'Torrent', label: 'Torrents' }]} />
        <span className="num text-[12px] text-muted">{total} items</span>
        <div className="flex-1" />
        <div className="relative w-full sm:w-64">
          <Search size={14} className="absolute top-1/2 left-2.5 -translate-y-1/2 text-faint" />
          <input className={inputClass + ' h-8 pl-8'} placeholder="Search the library" value={query} onChange={e => setQuery(e.target.value)} aria-label="Search the library" />
        </div>
      </div>
      <div className="min-h-0 flex-1 overflow-y-auto">
        {items.length === 0 ? (
          <Empty icon={<CheckCheck size={20} />} title={query ? 'Nothing found' : 'No finished downloads yet'}>{query ? 'Try another search.' : 'Everything you download ends up here.'}</Empty>
        ) : (
          <>
            {items.map(j => (
              <div key={j.id} onClick={() => setOpen(j)} className="group flex h-[60px] cursor-pointer items-center gap-3 border-b border-line px-4 hover:bg-raised sm:px-5">
                <FileIcon job={j} />
                <div className="min-w-0 flex-1">
                  <div className="truncate text-[13.5px] font-medium">{displayName(j)}</div>
                  <div className="num truncate text-[12px] text-muted">{bytes(j.totalBytes)} · {ago(j.completedAt)}{j.group ? ` · ${j.group}` : ''}</div>
                </div>
                <div className="flex gap-0.5" onClick={e => e.stopPropagation()}>
                  <IconButton label="Open" onClick={() => jobActions.open(j)}><ExternalLink size={15} /></IconButton>
                  <IconButton label="Show in folder" onClick={() => jobActions.open(j, true)}><FolderOpen size={15} /></IconButton>
                </div>
              </div>
            ))}
            {items.length < total && <div className="p-4 text-center"><Button size="sm" onClick={() => void load(items.length)}>Load more</Button></div>}
          </>
        )}
      </div>
      <JobDetail jobId={open?.id ?? null} fallback={open} onClose={() => { setOpen(null); void load(0) }} />
    </div>
  )
}
