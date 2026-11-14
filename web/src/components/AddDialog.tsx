import { useEffect, useMemo, useRef, useState } from 'react'
import { ChevronDown, Clapperboard, FileUp, FolderOpen, Magnet } from 'lucide-react'
import { api, desktop, type AddResult } from '../api'
import { useStore } from '../store'
import { parseLimit } from '../format'
import { Button, cx, Field, inputClass, Modal, Toggle } from '../ui'
import { attempt, toast } from './toast'
import { navigate } from '../router'

const MEDIA_HOSTS = /(^|\.)(youtube\.com|youtu\.be|facebook\.com|fb\.watch|instagram\.com|tiktok\.com|x\.com|twitter\.com|reddit\.com|vimeo\.com|twitch\.tv|dailymotion\.com|soundcloud\.com|bilibili\.com)$/i

export function isMediaUrl(u: string) {
  try { return MEDIA_HOSTS.test(new URL(u).hostname) } catch { return false }
}

export function extractUrls(text: string) {
  return Array.from(new Set((text.match(/(magnet:\?[^\s"'<>]+|https?:\/\/[^\s"'<>]+)/gi) ?? []).map(u => u.replace(/[),.;]+$/, ''))))
}

export function AddDialog({ open, initial, onClose }: { open: boolean; initial: string; onClose: () => void }) {
  const settings = useStore(s => s.settings)
  const [text, setText] = useState(initial)
  const [advanced, setAdvanced] = useState(false)
  const [directory, setDirectory] = useState('')
  const [connections, setConnections] = useState('')
  const [limit, setLimit] = useState('')
  const [checksum, setChecksum] = useState('')
  const [useSchedule, setUseSchedule] = useState(false)
  const [startPaused, setStartPaused] = useState(false)
  const [busy, setBusy] = useState(false)
  const fileInput = useRef<HTMLInputElement>(null)

  useEffect(() => { if (open) { setText(initial); setChecksum('') } }, [open, initial])

  const urls = useMemo(() => extractUrls(text), [text])
  const magnets = urls.filter(u => u.startsWith('magnet:'))
  const media = urls.filter(isMediaUrl)
  const direct = urls.filter(u => !u.startsWith('magnet:') && !isMediaUrl(u))

  const submit = async () => {
    setBusy(true)
    try {
      if (direct.length) {
        const r = await api.post<AddResult>('/api/downloads', {
          urls: direct,
          directory: directory || null,
          connections: connections ? Number(connections) : null,
          speedLimit: parseLimit(limit),
          checksum: direct.length === 1 && checksum ? checksum : null,
          useSchedule,
          startPaused,
          priority: 0,
          allowDuplicate: false,
        })
        if (r.added.length) toast(r.added.length === 1 ? `Added ${r.added[0].fileName ?? 'download'}` : `Added ${r.added.length} downloads`, 'ok')
        if (r.duplicates.length) toast(`${r.duplicates.length} already in the list — skipped`, 'info')
        if (r.rejected.length) toast(`${r.rejected.length} rejected: ${r.rejected[0].reason}`, 'bad')
      }
      for (const m of magnets) await attempt(() => api.post('/api/torrents', { magnet: m, directory: directory || null }), 'Torrent added')
      onClose()
      if (media.length === 1 && !direct.length && !magnets.length) navigate('media', { url: media[0] })
      else if (media.length) navigate('media', { url: media.join('\n') })
    } catch (e) {
      toast(e instanceof Error ? e.message : String(e), 'bad')
    } finally {
      setBusy(false)
    }
  }

  const importFile = async (f: File) => {
    const content = await f.text()
    const found = extractUrls(content)
    setText(t => (t.trim() ? t.trim() + '\n' : '') + found.join('\n'))
    toast(`Found ${found.length} addresses in ${f.name}`, 'info')
  }

  return (
    <Modal open={open} onClose={onClose} title="Add downloads" footer={<>
      <span className="mr-auto text-[12px] text-muted">{urls.length === 0 ? 'Paste one address per line' : summary(direct.length, media.length, magnets.length)}</span>
      <Button variant="ghost" onClick={onClose}>Cancel</Button>
      <Button variant="primary" disabled={!urls.length || busy} onClick={submit}>{media.length && !direct.length && !magnets.length ? 'Choose quality' : startPaused ? 'Add paused' : 'Download'}</Button>
    </>}>
      <div className="space-y-4">
        <div className="relative">
          <textarea autoFocus value={text} onChange={e => setText(e.target.value)} rows={4} spellCheck={false}
            onKeyDown={e => e.key === 'Enter' && (e.ctrlKey || e.metaKey) && urls.length && submit()}
            placeholder="https://example.com/file.iso&#10;magnet:?xt=urn:btih:…&#10;https://youtube.com/watch?v=…"
            className={cx(inputClass, 'h-auto min-h-[104px] resize-y py-2.5 font-mono text-[12.5px] leading-relaxed')} />
          <button type="button" onClick={() => fileInput.current?.click()} className="absolute right-2 bottom-2 inline-flex items-center gap-1 rounded-md px-2 py-1 text-[12px] text-muted hover:bg-sunken hover:text-ink">
            <FileUp size={13} /> Import list
          </button>
          <input ref={fileInput} type="file" accept=".txt,.csv,.list,text/plain" hidden onChange={e => e.target.files?.[0] && importFile(e.target.files[0])} />
        </div>

        {(media.length > 0 || magnets.length > 0) && (
          <div className="space-y-1.5 text-[12.5px] text-muted">
            {media.length > 0 && <p className="flex items-center gap-2"><Clapperboard size={14} className="text-accent" /> {media.length} media {media.length === 1 ? 'page' : 'pages'} — you'll pick the quality next.</p>}
            {magnets.length > 0 && <p className="flex items-center gap-2"><Magnet size={14} className="text-accent" /> {magnets.length} magnet {magnets.length === 1 ? 'link' : 'links'} — added to Torrents.</p>}
          </div>
        )}

        <button type="button" onClick={() => setAdvanced(a => !a)} className="flex items-center gap-1 text-[12.5px] font-medium text-ink-2 hover:text-ink">
          <ChevronDown size={14} className={cx('transition-transform', advanced && 'rotate-180')} /> Options
        </button>
        {advanced && (
          <div className="grid gap-4 animate-fade sm:grid-cols-2">
            <div className="sm:col-span-2">
              <Field label="Save to">
                <div className="flex gap-2">
                  <input className={inputClass} placeholder={settings?.downloadDirectory} value={directory} onChange={e => setDirectory(e.target.value)} />
                  {desktop.available() && <Button type="button" onClick={async () => { const p = await desktop.pickFolder(directory || settings?.downloadDirectory || ''); if (p) setDirectory(p) }}><FolderOpen size={15} /></Button>}
                </div>
              </Field>
            </div>
            <Field label="Connections" hint={`Default ${settings?.connectionsPerDownload ?? 8}. Used only if the server supports ranges.`}>
              <input className={inputClass} type="number" min={1} max={32} placeholder={String(settings?.connectionsPerDownload ?? 8)} value={connections} onChange={e => setConnections(e.target.value)} />
            </Field>
            <Field label="Speed limit" hint="e.g. 800 KB or 5 MB">
              <input className={inputClass} placeholder="Unlimited" value={limit} onChange={e => setLimit(e.target.value)} />
            </Field>
            {direct.length === 1 && (
              <div className="sm:col-span-2">
                <Field label="Checksum" hint="SHA-256, SHA-1, SHA-512 or MD5 published by the source. The file is only saved if it matches.">
                  <input className={cx(inputClass, 'font-mono text-[12px]')} placeholder="sha256:…" value={checksum} onChange={e => setChecksum(e.target.value)} />
                </Field>
              </div>
            )}
            <div className="flex items-center justify-between gap-3 rounded-lg border border-line px-3 py-2.5 text-[13px]">
              <span>Wait for schedule</span><Toggle label="Wait for schedule" checked={useSchedule} onChange={setUseSchedule} />
            </div>
            <div className="flex items-center justify-between gap-3 rounded-lg border border-line px-3 py-2.5 text-[13px]">
              <span>Add paused</span><Toggle label="Add paused" checked={startPaused} onChange={setStartPaused} />
            </div>
          </div>
        )}
      </div>
    </Modal>
  )
}

function summary(d: number, m: number, t: number) {
  return [d && `${d} file${d > 1 ? 's' : ''}`, m && `${m} media`, t && `${t} torrent${t > 1 ? 's' : ''}`].filter(Boolean).join(' · ')
}
