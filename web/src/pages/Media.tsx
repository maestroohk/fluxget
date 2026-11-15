import { useEffect, useMemo, useState } from 'react'
import { AudioLines, Check, CheckSquare, Clapperboard, FileText, Film, Images, ListVideo, Loader2, Music2, Play, Square, Wand2 } from 'lucide-react'
import { api, desktop, type Job } from '../api'
import { player } from '../player'
import { useStore } from '../store'
import { bytes, duration } from '../format'
import { navigate, useRoute } from '../router'
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
  id: string
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

interface GalleryItem { index: number; url: string; name: string; ext: string; width: number | null; height: number | null; kind: 'image' | 'video' | 'audio' | 'file'; preview: string | null }
interface GalleryInfo { type: 'gallery'; url: string; title: string; source: string; uploader: string | null; items: GalleryItem[]; hasMore: boolean; generic: boolean }
type Found = MediaInfo | GalleryInfo

const DECOR = /\/icons?\/|favicon|sprite|spacer|blank\.gif|pixel\.(gif|png)|emoji|badge|button/i
const RASTER = new Set(['jpg', 'jpeg', 'png', 'webp', 'avif', 'gif', 'heic', 'bmp', 'jxl'])

const AUDIO_FORMATS = [
  { value: 'mp3', label: 'MP3', qualities: ['320', '256', '192', '128', '96'] },
  { value: 'm4a', label: 'M4A', qualities: ['256', '192', '128'] },
  { value: 'aac', label: 'AAC', qualities: ['256', '192', '128'] },
  { value: 'opus', label: 'Opus', qualities: ['160', '128', '96'] },
  { value: 'flac', label: 'FLAC', qualities: [] },
  { value: 'wav', label: 'WAV', qualities: [] },
]

export function Media() {
  const { params } = useRoute()
  const jobs = useStore(s => s.jobs)
  const [url, setUrl] = useState(params.get('url') ?? '')
  const [info, setInfo] = useState<Found | null>(null)
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
      setInfo(await api.post<Found>('/api/media/analyze', { url: first }))
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
              <input value={url} onChange={e => setUrl(e.target.value)} placeholder="Paste a video, post, photo, playlist or page link"
                className={inputClass + ' h-11 pl-10 text-[14px]'} aria-label="Media address" />
            </div>
            <Button variant="primary" className="h-11 px-5" disabled={!url.trim() || loading}>{loading ? <Loader2 size={16} className="animate-spin" /> : 'Find formats'}</Button>
          </form>
          <p className="mt-2 text-[12px] text-muted">Videos from YouTube, Facebook, Instagram, TikTok, X, Reddit, Vimeo, Twitch and more; photos and galleries from Instagram, X, Reddit, Pinterest, Tumblr, Flickr and others; images and files on any other page. Only what you can already see is available; FluxGet never bypasses DRM or sign-in.</p>
          {error && <div className="mt-4 rounded-xl border border-bad/25 bg-bad-soft p-4 text-[13px]">{error}</div>}
          {loading && <AnalyzeSkeleton />}
          {info && (info.type === 'gallery'
            ? <GalleryResult info={info} onDone={() => { setInfo(null); setUrl('') }} />
            : <MediaResult info={info} onImages={g => setInfo(g)} onDone={id => { setInfo(null); setUrl(''); if (id != null) setOpen(id) }} />)}
        </section>

        <section>
          <SectionTitle aside={<Button size="sm" variant="ghost" onClick={() => setConvert(true)}><Wand2 size={14} /> Convert to audio</Button>}>Media downloads</SectionTitle>
          <Card className="h-[420px] overflow-hidden">
            <JobList jobs={media} onOpen={j => setOpen(j.id)} empty={<Empty icon={<Film size={20} />} title="No media downloads">Find a video above and choose its quality. Finished files appear in Completed.</Empty>} />
          </Card>
        </section>
      </div>
      <JobDetail jobId={open} onClose={() => setOpen(null)} />
      <ConvertDialog open={convert} initialUrl={/^https?:\/\//i.test(url.trim()) ? url.trim() : undefined} onClose={() => setConvert(false)} />
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

function MediaResult({ info, onDone, onImages }: { info: MediaInfo; onDone: (jobId?: number) => void; onImages: (g: GalleryInfo) => void }) {
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
  const [finding, setFinding] = useState(false)
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
    const items = isList ? entries.filter(e => selected.has(e.id)).map(e => ({ url: e.url, title: e.title, id: e.id })) : [{ url: info.url, title: info.title, id: info.id }]
    let first: number | undefined
    const ok = await attempt(async () => {
      const r = await api.post<{ ids: number[] }>('/api/media/download', {
        items, group: isList ? info.title : null, mode, height, container,
        audioFormat, audioQuality: fmt.qualities.length ? quality : null, subtitles: subs,
      })
      first = r.ids[0]
    }, items.length === 1 ? 'Download started' : `Queued ${items.length} items`)
    setBusy(false)
    if (ok) onDone(first)
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
        <Button size="sm" variant="ghost" disabled={finding} onClick={async () => { setFinding(true); await attempt(async () => onImages(await api.post<GalleryInfo>('/api/media/gallery', { url: info.url }))); setFinding(false) }}>
          {finding ? <Loader2 size={14} className="animate-spin" /> : <Images size={14} />} Images on this page
        </Button>
        <span className="mr-auto flex items-center gap-1.5 text-[12px] text-muted">{mode === 'audio' ? <AudioLines size={14} /> : <Film size={14} />}{mode === 'audio' ? `${fmt.label}${fmt.qualities.length ? ` · ${quality} kbps` : ''}` : `${height ? label(height) : 'Best'} · ${container.toUpperCase()}`}</span>
        {!isList && desktop.available() && <Button onClick={() => { player('openUrl', { url: info.url }); navigate('player') }}><Play size={14} /> Stream</Button>}
        <Button variant="primary" disabled={busy || (isList && selected.size === 0)} onClick={download}>{isList ? `Download ${selected.size}` : 'Download'}</Button>
      </div>
    </Card>
  )
}

function GalleryResult({ info, onDone }: { info: GalleryInfo; onDone: () => void }) {
  const wanted = (g: GalleryItem) => !info.generic || g.kind !== 'image' || (RASTER.has(g.ext) && !(g.width && g.width < 120) && !DECOR.test(g.url))
  const [selected, setSelected] = useState<Set<number>>(() => new Set(info.items.filter(wanted).map(g => g.index)))
  const [busy, setBusy] = useState(false)
  const all = selected.size === info.items.length
  const count = (k: GalleryItem['kind']) => info.items.filter(g => g.kind === k).length
  const summary = (['image', 'video', 'audio', 'file'] as const).map(k => [k, count(k)] as const).filter(([, n]) => n > 0)
    .map(([k, n]) => `${n} ${k === 'image' ? 'image' : k === 'video' ? 'video' : k === 'audio' ? 'audio file' : 'file'}${n === 1 || k === 'audio' && n === 1 ? '' : 's'}`).join(', ')
  const toggle = (i: number) => setSelected(s => { const n = new Set(s); if (n.has(i)) n.delete(i); else n.add(i); return n })

  const download = async () => {
    setBusy(true)
    const chosen = info.items.filter(g => selected.has(g.index))
    const ok = await attempt(() => api.post('/api/media/gallery/download', info.generic
      ? { url: info.url, title: info.title, files: chosen.map(g => ({ url: g.url, name: g.name || null })) }
      : { url: info.url, title: info.title, indices: chosen.map(g => g.index) }), chosen.length === 1 ? 'Saving 1 file' : `Saving ${chosen.length} files`)
    setBusy(false)
    if (ok) onDone()
  }

  return (
    <Card className="mt-5 overflow-hidden animate-rise">
      <div className="p-5 pb-4">
        <div className="text-[11.5px] font-medium tracking-wide text-accent uppercase">{info.source}{info.generic ? ' · images and files' : ''}</div>
        <h2 className="mt-1 line-clamp-2 text-[17px] leading-snug font-semibold tracking-tight">{info.title}</h2>
        <div className="mt-1 text-[13px] text-muted">
          {[info.uploader, `${summary}${info.hasMore ? ' (first ' + info.items.length + ')' : ''}`].filter(Boolean).join(' · ')}
        </div>
        {info.generic && <p className="mt-2 text-[12.5px] text-muted">Found on the page. Icons and very small images are not selected. Files download with FluxGet's resumable downloader.</p>}
      </div>
      <div className="flex items-center justify-between border-t border-line px-5 py-2.5 text-[12.5px]">
        <button className="flex items-center gap-1.5 font-medium text-ink-2 hover:text-ink" onClick={() => setSelected(all ? new Set() : new Set(info.items.map(g => g.index)))}>
          {all ? <CheckSquare size={15} /> : <Square size={15} />} {selected.size} of {info.items.length} selected
        </button>
        <Images size={15} className="text-faint" />
      </div>
      <div className="grid max-h-[460px] grid-cols-3 gap-2 overflow-y-auto border-t border-line p-3 sm:grid-cols-4 lg:grid-cols-6">
        {info.items.map(g => <Tile key={g.index} item={g} on={selected.has(g.index)} onToggle={() => toggle(g.index)} />)}
      </div>
      <div className="flex items-center justify-end gap-3 border-t border-line bg-raised px-5 py-3">
        <span className="mr-auto flex min-w-0 items-center gap-1.5 text-[12px] text-muted"><Images size={14} className="shrink-0" /> <span className="truncate">Saved to a folder named after the {info.generic ? 'page' : 'post'}</span></span>
        <Button variant="primary" disabled={busy || selected.size === 0} onClick={download}>Download {selected.size}</Button>
      </div>
    </Card>
  )
}

function Tile({ item, on, onToggle }: { item: GalleryItem; on: boolean; onToggle: () => void }) {
  const [broken, setBroken] = useState(false)
  return (
    <button type="button" onClick={onToggle} aria-pressed={on} title={item.name}
      className={cx('group relative aspect-square overflow-hidden rounded-lg border bg-sunken text-left transition', on ? 'border-accent ring-2 ring-accent/25' : 'border-line opacity-80 hover:opacity-100')}>
      {item.preview && !broken
        ? <img src={item.preview} alt="" loading="lazy" referrerPolicy="no-referrer" onError={() => setBroken(true)} className="size-full object-cover" />
        : <span className="flex size-full flex-col items-center justify-center gap-1 px-1 text-faint">{item.kind === 'video' ? <Film size={22} /> : item.kind === 'audio' ? <Music2 size={22} /> : item.kind === 'file' ? <FileText size={22} /> : <Images size={22} />}<span className="text-[11px] uppercase">{item.ext || 'file'}</span>{item.kind === 'file' && <span className="w-full truncate text-center text-[10.5px] normal-case">{item.name}</span>}</span>}
      <span className={cx('absolute top-1.5 left-1.5 flex size-5 items-center justify-center rounded-md border text-white', on ? 'border-accent bg-accent' : 'border-white/70 bg-black/30')}>{on && <Check size={13} strokeWidth={3} />}</span>
      {item.kind === 'video' && <span className="absolute right-1.5 bottom-1.5 flex items-center gap-1 rounded bg-black/65 px-1.5 py-0.5 text-[10.5px] font-medium text-white"><Film size={11} /> Video</span>}
      {item.kind === 'image' && item.width && item.height && <span className="num absolute right-1.5 bottom-1.5 rounded bg-black/55 px-1.5 py-0.5 text-[10.5px] text-white opacity-0 transition group-hover:opacity-100">{item.width}×{item.height}</span>}
    </button>
  )
}

const label = (h: number) => (h >= 2160 ? `${h >= 4320 ? '8K' : '4K'} ${h}p` : h >= 1440 ? `QHD ${h}p` : `${h}p`)
const codec = (c: string) => (c.startsWith('avc') ? 'H.264' : c.startsWith('av01') ? 'AV1' : c.startsWith('vp9') || c.startsWith('vp09') ? 'VP9' : c.startsWith('hev') || c.startsWith('hvc') ? 'HEVC' : c)

function ConvertDialog({ open, onClose, initialUrl }: { open: boolean; onClose: () => void; initialUrl?: string }) {
  const [source, setSource] = useState<'link' | 'file'>('link')
  const [link, setLink] = useState('')
  const [files, setFiles] = useState<Job[]>([])
  const [jobId, setJobId] = useState<number | null>(null)
  const [formats, setFormats] = useState<string[]>(['mp3'])
  const [quality, setQuality] = useState('192')
  const [busy, setBusy] = useState(false)
  useEffect(() => {
    if (!open) return
    if (initialUrl) { setLink(initialUrl); setSource('link') }
    void api.get<{ items: Job[] }>('/api/jobs/completed?limit=200').then(r => {
      const vids = r.items.filter(j => /\.(mp4|mkv|webm|mov|m4v|avi|flv|ts|m4a|mp3|opus|ogg|flac|wav|aac)$/i.test(j.filePath ?? ''))
      setFiles(vids)
      setJobId(vids[0]?.id ?? null)
    })
  }, [open, initialUrl])
  const lossy = formats.some(f => AUDIO_FORMATS.find(x => x.value === f)!.qualities.length > 0)
  const qualities = ['320', '256', '192', '160', '128', '96']
  const toggle = (f: string) => setFormats(list => list.includes(f) ? (list.length > 1 ? list.filter(x => x !== f) : list) : [...list, f])
  const qualityFor = (f: string) => {
    const q = AUDIO_FORMATS.find(x => x.value === f)!.qualities
    if (!q.length) return null
    return q.includes(quality) ? quality : q.reduce((best, x) => Math.abs(+x - +quality) < Math.abs(+best - +quality) ? x : best, q[0])
  }
  const urls = link.split(/\s+/).filter(u => /^https?:\/\//i.test(u))
  const submit = async () => {
    setBusy(true)
    const ok = await attempt(async () => {
      for (const f of formats) {
        if (source === 'link') {
          await api.post('/api/media/download', { items: urls.map(u => ({ url: u })), mode: 'audio', audioFormat: f, audioQuality: qualityFor(f) })
        } else {
          await api.post('/api/media/convert', { jobId, audioFormat: f, audioQuality: qualityFor(f) })
        }
      }
    }, formats.length * (source === 'link' ? urls.length : 1) > 1 ? `Queued ${formats.length * (source === 'link' ? urls.length : 1)} audio jobs` : 'Audio job queued')
    setBusy(false)
    if (ok) onClose()
  }
  const ready = source === 'link' ? urls.length > 0 : jobId != null
  return (
    <Modal open={open} onClose={onClose} title="Convert to audio" footer={<>
      <Button variant="ghost" onClick={onClose}>Cancel</Button>
      <Button variant="primary" disabled={!ready || busy} onClick={submit}>{formats.length > 1 ? `Create ${formats.length} files` : `Convert to ${AUDIO_FORMATS.find(f => f.value === formats[0])!.label}`}</Button>
    </>}>
      <div className="space-y-4">
        <Segmented value={source} onChange={setSource} options={[{ value: 'link', label: 'From a link' }, { value: 'file', label: 'From a downloaded file' }]} />
        {source === 'link' ? (
          <Field label="Video or song link" hint="YouTube, SoundCloud, TikTok and other supported sites. Paste several links on separate lines to convert them all. Only the audio stream is downloaded.">
            <textarea className={cx(inputClass, 'h-auto min-h-[72px] py-2 font-mono text-[12.5px]')} rows={3} placeholder="https://www.youtube.com/watch?v=…" value={link} onChange={e => setLink(e.target.value)} />
          </Field>
        ) : files.length === 0 ? <p className="text-[13px] text-muted">No finished video or audio files yet. Use "From a link" or download something first.</p> : (
          <Field label="File">
            <select className={inputClass} value={jobId ?? ''} onChange={e => setJobId(Number(e.target.value))}>
              {files.map(f => <option key={f.id} value={f.id}>{f.fileName ?? f.filePath}</option>)}
            </select>
          </Field>
        )}
        <Field label="Formats" hint="Choose one or more; each produces its own file.">
          <div className="flex flex-wrap gap-1.5">
            {AUDIO_FORMATS.map(f => (
              <button key={f.value} type="button" onClick={() => toggle(f.value)} aria-pressed={formats.includes(f.value)}
                className={cx('h-8 rounded-lg border px-3 text-[12.5px] font-medium transition', formats.includes(f.value) ? 'border-ink bg-ink text-bg' : 'border-line text-muted hover:border-line-strong hover:text-ink')}>
                {f.label}
              </button>
            ))}
          </div>
        </Field>
        <Field label="Quality" hint={lossy ? 'Applies to MP3, M4A, AAC and Opus (nearest supported bitrate). FLAC and WAV are lossless.' : 'FLAC and WAV are lossless — no bitrate to choose.'}>
          <select className={inputClass + ' sm:w-56'} value={quality} disabled={!lossy} onChange={e => setQuality(e.target.value)}>
            {qualities.map(q => <option key={q} value={q}>{q} kbps{q === '320' ? ' (best)' : q === '192' ? ' (recommended)' : ''}</option>)}
          </select>
        </Field>
        <p className="text-[12px] text-muted">When the source audio already matches a format (for example AAC into M4A), it is copied without re-encoding, so quality is preserved.</p>
      </div>
    </Modal>
  )
}
