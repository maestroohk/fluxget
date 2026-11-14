import { useEffect, useMemo, useState } from 'react'
import { AudioLines, CheckSquare, Clapperboard, Film, ListVideo, Loader2, Music2, Square, Wand2 } from 'lucide-react'
import { api, type Job } from '../api'
import { useStore } from '../store'
import { bytes, duration } from '../format'
import { useRoute } from '../router'
import { Button, Card, cx, Empty, Field, inputClass, Modal, SectionTitle, Segmented, Toggle } from '../ui'
import { JobList } from '../components/jobs'
import { JobDetail } from '../components/JobDetail'
import { attempt } from '../components/toast'

interface VideoOption { height: number; fps: number | null; ext: string; vcodec: string; size: number | null; estimated: boolean }
interface AudioSource { abr: number | null; ext: string; acodec: string; size: number | null }
interface Entry { id: string; url: string; title: string; duration: number | null; thumbnail: string | null }
interface MediaInfo {
  type: 'video' | 'playlist'
  url: string
  title: string
  uploader: string | null
  duration: number | null
  thumbnail: string | null
  extractor: string
  video: VideoOption[]
  audio: AudioSource[]
  subtitles: string[]
  entries: Entry[]
  entryCount: number | null
  hasMore: boolean
  live: boolean
}

const AUDIO_FORMATS = [
  { value: 'mp3', label: 'MP3', qualities: ['320', '256', '192', '128'] },
  { value: 'm4a', label: 'M4A (AAC)', qualities: ['256', '192', '128'] },
  { value: 'opus', label: 'Opus', qualities: ['160', '128', '96'] },
  { value: 'flac', label: 'FLAC', qualities: [] },
  { value: 'wav', label: 'WAV', qualities: [] },
]

export function Media() {
  const { params } = useRoute()
  const jobs = useStore(s => s.jobs)
  const [url, setUrl] = useState(params.get('url') ?? '')
  const [info, setInfo] = useState<MediaInfo | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState('')
  const [open, setOpen] = useState<number | null>(null)
  const [convert, setConvert] = useState(false)
  const media = useMemo(() => [...jobs.values()].filter(j => j.kind === 'Media').sort((a, b) => b.id - a.id), [jobs])

  const analyze = async (target = url) => {
    const first = target.split(/\s+/).find(Boolean)
    if (!first) return
    setLoading(true); setError(''); setInfo(null)
    try {
      setInfo(await api.post<MediaInfo>('/api/media/analyze', { url: first }))
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    const u = params.get('url')
    if (u) { setUrl(u); void analyze(u) }
  }, [params.get('url')])

  return (
    <div className="h-full overflow-y-auto">
      <div className="mx-auto max-w-5xl space-y-8 px-4 py-6 sm:px-6">
        <section>
          <form onSubmit={e => { e.preventDefault(); void analyze() }} className="flex flex-col gap-2 sm:flex-row">
            <div className="relative flex-1">
              <Clapperboard size={16} className="absolute top-1/2 left-3.5 -translate-y-1/2 text-faint" />
              <input value={url} onChange={e => setUrl(e.target.value)} placeholder="Paste a video, Short, Reel, TikTok or playlist link"
                className={inputClass + ' h-11 pl-10 text-[14px]'} aria-label="Media address" />
            </div>
            <Button variant="primary" className="h-11 px-5" disabled={!url.trim() || loading}>{loading ? <Loader2 size={16} className="animate-spin" /> : 'Find formats'}</Button>
          </form>
          <p className="mt-2 text-[12px] text-muted">Works with YouTube, Facebook, Instagram, TikTok, X, Reddit, Vimeo, Twitch and other sites supported by yt-dlp. Only media you can already watch is available; FluxGet never bypasses DRM or sign-in.</p>
          {error && <div className="mt-4 rounded-xl border border-bad/25 bg-bad-soft p-4 text-[13px]">{error}</div>}
          {loading && <AnalyzeSkeleton />}
          {info && <MediaResult info={info} onDone={() => { setInfo(null); setUrl('') }} />}
        </section>

        <section>
          <SectionTitle aside={<Button size="sm" variant="ghost" onClick={() => setConvert(true)}><Wand2 size={14} /> Convert a file to audio</Button>}>Media downloads</SectionTitle>
          <Card className="h-[420px] overflow-hidden">
            <JobList jobs={media} onOpen={j => setOpen(j.id)} empty={<Empty icon={<Film size={20} />} title="No media downloads">Find a video above and choose its quality. Finished files appear in Completed.</Empty>} />
          </Card>
        </section>
      </div>
      <JobDetail jobId={open} onClose={() => setOpen(null)} />
      <ConvertDialog open={convert} onClose={() => setConvert(false)} />
    </div>
  )
}

function AnalyzeSkeleton() {
  return (
    <Card className="mt-5 flex animate-pulse gap-5 p-5">
      <div className="aspect-video w-56 shrink-0 rounded-lg bg-sunken" />
      <div className="flex-1 space-y-3 py-1">
        <div className="h-4 w-2/3 rounded bg-sunken" /><div className="h-3 w-1/3 rounded bg-sunken" /><div className="h-24 rounded bg-sunken" />
      </div>
    </Card>
  )
}

function MediaResult({ info, onDone }: { info: MediaInfo; onDone: () => void }) {
  const [mode, setMode] = useState<'video' | 'audio'>('video')
  const [height, setHeight] = useState<number | null>(info.video[0]?.height ?? null)
  const [container, setContainer] = useState('mp4')
  const [audioFormat, setAudioFormat] = useState('mp3')
  const [quality, setQuality] = useState('192')
  const [subs, setSubs] = useState(false)
  const [entries, setEntries] = useState<Entry[]>(info.entries)
  const [selected, setSelected] = useState<Set<string>>(() => new Set(info.entries.map(e => e.id)))
  const [hasMore, setHasMore] = useState(info.hasMore)
  const [busy, setBusy] = useState(false)
  const fmt = AUDIO_FORMATS.find(f => f.value === audioFormat)!
  const isList = info.type === 'playlist'

  const loadMore = async () => {
    const r = await api.post<MediaInfo>('/api/media/analyze', { url: info.url, start: entries.length + 1 })
    setEntries(e => [...e, ...r.entries])
    setSelected(s => new Set([...s, ...r.entries.map(x => x.id)]))
    setHasMore(r.hasMore)
  }

  const download = async () => {
    setBusy(true)
    const items = isList ? entries.filter(e => selected.has(e.id)).map(e => ({ url: e.url, title: e.title })) : [{ url: info.url, title: info.title }]
    const ok = await attempt(() => api.post('/api/media/download', {
      items, group: isList ? info.title : null, mode, height, container,
      audioFormat, audioQuality: fmt.qualities.length ? quality : null, subtitles: subs,
    }), items.length === 1 ? 'Added to downloads' : `Queued ${items.length} items`)
    setBusy(false)
    if (ok) onDone()
  }

  return (
    <Card className="mt-5 overflow-hidden animate-rise">
      <div className="flex flex-col gap-5 p-5 sm:flex-row">
        {info.thumbnail
          ? <img src={info.thumbnail} alt="" referrerPolicy="no-referrer" className="aspect-video w-full shrink-0 rounded-lg border border-line bg-sunken object-cover sm:w-64" />
          : <div className="flex aspect-video w-full shrink-0 items-center justify-center rounded-lg bg-sunken text-faint sm:w-64"><Film size={28} /></div>}
        <div className="min-w-0 flex-1">
          <div className="text-[11.5px] font-medium tracking-wide text-accent uppercase">{info.extractor}{isList && ' · playlist'}</div>
          <h2 className="mt-1 text-[17px] leading-snug font-semibold tracking-tight">{info.title}</h2>
          <div className="mt-1 text-[13px] text-muted">
            {[info.uploader, info.duration ? duration(info.duration) : null, isList ? `${info.entryCount ?? entries.length}${hasMore ? '+' : ''} items` : null].filter(Boolean).join(' · ')}
          </div>
          {info.live && <p className="mt-2 text-[12.5px] text-warn">This is a live stream. FluxGet downloads it from the current point until it ends or you stop it.</p>}

          <div className="mt-5 flex flex-wrap items-center gap-3">
            <Segmented value={mode} onChange={setMode} options={[
              { value: 'video', label: <span className="flex items-center gap-1.5"><Film size={13} /> Video</span> },
              { value: 'audio', label: <span className="flex items-center gap-1.5"><Music2 size={13} /> Audio only</span> },
            ]} />
            {info.subtitles.length > 0 && mode === 'video' && (
              <label className="flex items-center gap-2 text-[13px] text-ink-2"><Toggle label="Subtitles" checked={subs} onChange={setSubs} /> Subtitles ({info.subtitles.slice(0, 3).join(', ')}{info.subtitles.length > 3 ? '…' : ''})</label>
            )}
          </div>
        </div>
      </div>

      <div className="border-t border-line p-5">
        {mode === 'video' ? (
          <div className="space-y-4">
            {info.video.length > 0 ? (
              <div className="grid gap-2 sm:grid-cols-2 lg:grid-cols-3">
                {info.video.map(v => (
                  <button key={v.height} type="button" onClick={() => setHeight(v.height)}
                    className={cx('flex items-center justify-between rounded-lg border px-3.5 py-2.5 text-left transition', height === v.height ? 'border-accent bg-accent-soft/60 ring-2 ring-accent/15' : 'border-line hover:border-line-strong')}>
                    <span>
                      <span className="text-[14px] font-semibold">{label(v.height)}</span>
                      {v.fps && v.fps > 30 && <span className="ml-1 text-[12px] text-muted">{Math.round(v.fps)}fps</span>}
                      <span className="block text-[11.5px] text-muted uppercase">{codec(v.vcodec)}</span>
                    </span>
                    <span className="num text-[12px] text-muted">{v.size ? `${v.estimated ? '≈' : ''}${bytes(v.size)}` : isList ? '' : 'size unknown'}</span>
                  </button>
                ))}
              </div>
            ) : <p className="text-[13px] text-muted">{isList ? 'Each item is downloaded at the best quality up to the limit you choose.' : 'No separate qualities are offered for this media — the best available file will be downloaded.'}</p>}
            {isList && (
              <Field label="Maximum quality">
                <select className={inputClass + ' sm:w-56'} value={height ?? ''} onChange={e => setHeight(e.target.value ? Number(e.target.value) : null)}>
                  <option value="">Best available</option>
                  {[2160, 1440, 1080, 720, 480, 360].map(h => <option key={h} value={h}>{label(h)}</option>)}
                </select>
              </Field>
            )}
            <Field label="Container" hint="Streams are merged without re-encoding; FluxGet only converts when the container cannot hold the original codecs.">
              <select className={inputClass + ' sm:w-56'} value={container} onChange={e => setContainer(e.target.value)}>
                <option value="mp4">MP4</option><option value="mkv">MKV</option><option value="webm">WebM</option>
              </select>
            </Field>
          </div>
        ) : (
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Format">
              <select className={inputClass} value={audioFormat} onChange={e => { setAudioFormat(e.target.value); const q = AUDIO_FORMATS.find(f => f.value === e.target.value)!.qualities; if (q.length && !q.includes(quality)) setQuality(q[1] ?? q[0]) }}>
                {AUDIO_FORMATS.map(f => <option key={f.value} value={f.value}>{f.label}</option>)}
              </select>
            </Field>
            <Field label="Quality" hint={fmt.qualities.length ? (info.audio[0]?.abr ? `Source audio is about ${Math.round(info.audio[0].abr)} kbps — higher settings cannot add detail.` : undefined) : 'Lossless container; the original audio is decoded once.'}>
              <select className={inputClass} value={quality} disabled={!fmt.qualities.length} onChange={e => setQuality(e.target.value)}>
                {fmt.qualities.length ? fmt.qualities.map(q => <option key={q} value={q}>{q} kbps</option>) : <option>Lossless</option>}
              </select>
            </Field>
          </div>
        )}
      </div>

      {isList && (
        <div className="border-t border-line">
          <div className="flex items-center justify-between px-5 py-2.5 text-[12.5px]">
            <button className="flex items-center gap-1.5 font-medium text-ink-2 hover:text-ink" onClick={() => setSelected(s => s.size === entries.length ? new Set() : new Set(entries.map(e => e.id)))}>
              {selected.size === entries.length ? <CheckSquare size={15} /> : <Square size={15} />} {selected.size} of {entries.length} selected
            </button>
            <ListVideo size={15} className="text-faint" />
          </div>
          <div className="max-h-80 overflow-y-auto border-t border-line">
            {entries.map((e, i) => (
              <label key={e.id + i} className="flex cursor-pointer items-center gap-3 border-b border-line px-5 py-2 text-[13px] last:border-0 hover:bg-raised">
                <input type="checkbox" className="size-4 accent-[var(--accent)]" checked={selected.has(e.id)}
                  onChange={() => setSelected(s => { const n = new Set(s); if (n.has(e.id)) n.delete(e.id); else n.add(e.id); return n })} />
                <span className="num w-8 text-[12px] text-faint">{i + 1}</span>
                <span className="min-w-0 flex-1 truncate">{e.title}</span>
                <span className="num text-[12px] text-muted">{duration(e.duration)}</span>
              </label>
            ))}
            {hasMore && <div className="p-3 text-center"><Button size="sm" onClick={() => attempt(loadMore)}>Load more</Button></div>}
          </div>
        </div>
      )}

      <div className="flex items-center justify-end gap-3 border-t border-line bg-raised px-5 py-3">
        <span className="mr-auto flex items-center gap-1.5 text-[12px] text-muted">{mode === 'audio' ? <AudioLines size={14} /> : <Film size={14} />}{mode === 'audio' ? `${fmt.label}${fmt.qualities.length ? ` · ${quality} kbps` : ''}` : `${height ? label(height) : 'Best'} · ${container.toUpperCase()}`}</span>
        <Button variant="primary" disabled={busy || (isList && selected.size === 0)} onClick={download}>{isList ? `Download ${selected.size}` : 'Download'}</Button>
      </div>
    </Card>
  )
}

const label = (h: number) => (h >= 2160 ? `${h >= 4320 ? '8K' : '4K'} ${h}p` : h >= 1440 ? `QHD ${h}p` : `${h}p`)
const codec = (c: string) => (c.startsWith('avc') ? 'H.264' : c.startsWith('av01') ? 'AV1' : c.startsWith('vp9') || c.startsWith('vp09') ? 'VP9' : c.startsWith('hev') || c.startsWith('hvc') ? 'HEVC' : c)

function ConvertDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const [files, setFiles] = useState<Job[]>([])
  const [jobId, setJobId] = useState<number | null>(null)
  const [format, setFormat] = useState('mp3')
  const [quality, setQuality] = useState('192')
  useEffect(() => {
    if (!open) return
    void api.get<{ items: Job[] }>('/api/jobs/completed?limit=200').then(r => {
      const vids = r.items.filter(j => /\.(mp4|mkv|webm|mov|m4v|avi|flv|ts)$/i.test(j.filePath ?? ''))
      setFiles(vids)
      setJobId(vids[0]?.id ?? null)
    })
  }, [open])
  const fmt = AUDIO_FORMATS.find(f => f.value === format)!
  return (
    <Modal open={open} onClose={onClose} title="Convert a video to audio" footer={<>
      <Button variant="ghost" onClick={onClose}>Cancel</Button>
      <Button variant="primary" disabled={!jobId} onClick={async () => {
        if (await attempt(() => api.post('/api/media/convert', { jobId, audioFormat: format, audioQuality: fmt.qualities.length ? quality : null }), 'Conversion queued')) onClose()
      }}>Convert</Button>
    </>}>
      {files.length === 0 ? <p className="text-[13px] text-muted">No finished video files yet. Download a video first, then convert it here.</p> : (
        <div className="space-y-4">
          <Field label="Video">
            <select className={inputClass} value={jobId ?? ''} onChange={e => setJobId(Number(e.target.value))}>
              {files.map(f => <option key={f.id} value={f.id}>{f.fileName ?? f.filePath}</option>)}
            </select>
          </Field>
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Format"><select className={inputClass} value={format} onChange={e => setFormat(e.target.value)}>{AUDIO_FORMATS.map(f => <option key={f.value} value={f.value}>{f.label}</option>)}</select></Field>
            <Field label="Quality"><select className={inputClass} value={quality} disabled={!fmt.qualities.length} onChange={e => setQuality(e.target.value)}>{fmt.qualities.length ? fmt.qualities.map(q => <option key={q} value={q}>{q} kbps</option>) : <option>Lossless</option>}</select></Field>
          </div>
          <p className="text-[12px] text-muted">If the video's audio already matches the chosen format (for example AAC into M4A), it is copied without re-encoding.</p>
        </div>
      )}
    </Modal>
  )
}

