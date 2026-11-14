import { useMemo, useRef, useState } from 'react'
import { FileUp, Magnet, ShieldAlert } from 'lucide-react'
import { api } from '../api'
import { useStore } from '../store'
import { Button, Card, Empty, inputClass } from '../ui'
import { JobList } from '../components/jobs'
import { JobDetail } from '../components/JobDetail'
import { attempt } from '../components/toast'

export function Torrents() {
  const jobs = useStore(s => s.jobs)
  const [magnet, setMagnet] = useState('')
  const [open, setOpen] = useState<number | null>(null)
  const file = useRef<HTMLInputElement>(null)
  const list = useMemo(() => [...jobs.values()].filter(j => j.kind === 'Torrent').sort((a, b) => b.id - a.id), [jobs])

  const addMagnet = async () => {
    if (await attempt(() => api.post('/api/torrents', { magnet: magnet.trim() }), 'Torrent added')) setMagnet('')
  }
  const upload = async (f: File) => {
    const form = new FormData()
    form.append('file', f)
    await attempt(() => api.post('/api/torrents/file', form), `Added ${f.name}`)
  }

  return (
    <div className="flex h-full flex-col">
      <div className="border-b border-line px-4 py-4 sm:px-5">
        <form onSubmit={e => { e.preventDefault(); void addMagnet() }} className="flex flex-col gap-2 sm:flex-row">
          <div className="relative flex-1">
            <Magnet size={15} className="absolute top-1/2 left-3 -translate-y-1/2 text-faint" />
            <input className={inputClass + ' pl-9 font-mono text-[12.5px]'} placeholder="magnet:?xt=urn:btih:…" value={magnet} onChange={e => setMagnet(e.target.value)} aria-label="Magnet link" />
          </div>
          <Button variant="primary" disabled={!magnet.trim().startsWith('magnet:')}>Add magnet</Button>
          <Button type="button" onClick={() => file.current?.click()}><FileUp size={15} /> Open .torrent</Button>
          <input ref={file} type="file" accept=".torrent,application/x-bittorrent" hidden onChange={e => { const f = e.target.files?.[0]; if (f) void upload(f); e.target.value = '' }} />
        </form>
        <p className="mt-2 flex items-start gap-1.5 text-[12px] text-muted"><ShieldAlert size={13} className="mt-px shrink-0" /> Peers can see your IP address. Use torrents for content you are allowed to share, such as Linux distributions and open-source releases.</p>
      </div>
      <div className="min-h-0 flex-1">
        <JobList jobs={list} onOpen={j => setOpen(j.id)} empty={
          <Card className="m-5"><Empty icon={<Magnet size={20} />} title="No torrents">Add a magnet link or a .torrent file. You can choose which files to download once the metadata arrives.</Empty></Card>
        } />
      </div>
      <JobDetail jobId={open} onClose={() => setOpen(null)} />
    </div>
  )
}
