import { useEffect, useState } from 'react'
import { api, type Job } from '../api'
import { bytes, speed } from '../format'
import { cx, Progress, Segmented } from '../ui'
import { attempt } from './toast'

interface TorrentFile { index: number; path: string; length: number; downloaded: number; download: boolean }
interface Peer { address: string; client: string; downloadRate: number; uploadRate: number; seeder: boolean }

export function TorrentPanel({ job }: { job: Job }) {
  const [tab, setTab] = useState<'files' | 'peers'>('files')
  const [files, setFiles] = useState<TorrentFile[] | null>(null)
  const [peers, setPeers] = useState<Peer[] | null>(null)

  useEffect(() => {
    let stop = false
    const load = async () => {
      try {
        if (tab === 'files') {
          const f = await api.get<TorrentFile[]>(`/api/torrents/${job.id}/files`)
          if (!stop) setFiles(f)
        } else {
          const p = await api.get<Peer[]>(`/api/torrents/${job.id}/peers`)
          if (!stop) setPeers(p)
        }
      } catch { /* not loaded yet */ }
    }
    void load()
    const t = setInterval(load, 2000)
    return () => { stop = true; clearInterval(t) }
  }, [job.id, tab])

  const toggle = (f: TorrentFile) => attempt(async () => {
    await api.put(`/api/torrents/${job.id}/files`, { index: f.index, download: !f.download })
    setFiles(list => list?.map(x => (x.index === f.index ? { ...x, download: !x.download } : x)) ?? null)
  })

  const ratio = job.extra?.ratio as number | undefined
  return (
    <div className="space-y-3">
      <div className="flex items-center justify-between gap-3">
        <Segmented value={tab} onChange={setTab} options={[{ value: 'files', label: 'Files' }, { value: 'peers', label: 'Peers' }]} />
        <span className="num text-[12px] text-muted">↑ {bytes((job.extra?.uploaded as number) ?? 0)}{ratio != null && ` · ratio ${ratio.toFixed(2)}`}</span>
      </div>
      <div className="max-h-72 overflow-y-auto rounded-lg border border-line">
        {tab === 'files' && (files == null
          ? <p className="p-4 text-[13px] text-muted">{job.extra?.metadata === false ? 'Fetching metadata from peers…' : 'Loading files…'}</p>
          : files.map(f => (
            <label key={f.index} className="flex cursor-pointer items-center gap-3 border-b border-line px-3 py-2 text-[12.5px] last:border-0 hover:bg-raised">
              <input type="checkbox" checked={f.download} onChange={() => toggle(f)} className="size-4 accent-[var(--accent)]" />
              <span className={cx('min-w-0 flex-1 truncate', !f.download && 'text-faint line-through')}>{f.path}</span>
              <span className="w-20"><Progress value={f.length ? (f.downloaded / f.length) * 100 : 0} tone={f.downloaded >= f.length ? 'ok' : 'accent'} /></span>
              <span className="num w-16 text-right text-muted">{bytes(f.length)}</span>
            </label>
          )))}
        {tab === 'peers' && (peers == null || peers.length === 0
          ? <p className="p-4 text-[13px] text-muted">No connected peers right now.</p>
          : peers.map((p, i) => (
            <div key={i} className="num flex items-center gap-3 border-b border-line px-3 py-2 text-[12px] last:border-0">
              <span className="w-40 truncate">{p.address}</span>
              <span className="min-w-0 flex-1 truncate text-muted">{p.client}{p.seeder && ' · seed'}</span>
              <span className="w-20 text-right">↓ {speed(p.downloadRate)}</span>
              <span className="w-20 text-right text-muted">↑ {speed(p.uploadRate)}</span>
            </div>
          )))}
      </div>
      <p className="text-[12px] text-muted">BitTorrent shares your IP address with the peers you connect to. FluxGet does not make torrenting anonymous.</p>
    </div>
  )
}
