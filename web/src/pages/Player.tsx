import { useCallback, useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react'
import { AlertTriangle, Disc3, FileVideo, FolderOpen, Keyboard, Library, Link2, ListVideo, Music, Play, RotateCcw, RotateCw, Search, SlidersHorizontal, Trash2, X } from 'lucide-react'
import { api, desktop } from '../api'
import { ago, bytes } from '../format'
import { clock, handlePlayerKey, player, SHORTCUTS, usePlayer, type PlayerState } from '../player'
import { PlayerControls } from '../components/PlayerControls'
import { SubtitleGenerator } from '../components/SubtitleGenerator'
import { attempt } from '../components/toast'
import { Button, cx, inputClass, Modal, Segmented, Toggle } from '../ui'

interface LibraryItem { jobId: number; title: string; path: string; kind: 'video' | 'audio'; size: number; completedAt: string | null; positionMs: number; durationMs: number; lastPlayed: string | null }
interface RecentItem { path: string; title: string; positionMs: number; durationMs: number; lastPlayed: string; kind: string }
type Tab = 'queue' | 'library' | 'playback'

export function PlayerPage() {
  const p = usePlayer()
  const [tab, setTab] = useState<Tab>('library')
  const [shortcuts, setShortcuts] = useState(false)
  const [url, setUrl] = useState('')
  const stage = useRef<HTMLDivElement>(null)
  const loaded = !!p?.loaded
  const showVideo = loaded && !!p?.hasVideo && p.state !== 'error' && p.mode === 'embedded'

  useEffect(() => { if (loaded) setTab(t => (t === 'library' ? 'queue' : t)) }, [loaded])

  const report = useCallback(() => {
    const el = stage.current
    if (!el || !desktop.available()) return
    const r = el.getBoundingClientRect()
    desktop.send({ type: 'player', cmd: 'surface', x: r.left, y: r.top, w: r.width, h: r.height, dpr: window.devicePixelRatio, visible: showVideo })
  }, [showVideo])

  useLayoutEffect(() => {
    report()
    const ro = new ResizeObserver(report)
    if (stage.current) ro.observe(stage.current)
    window.addEventListener('resize', report)
    return () => { ro.disconnect(); window.removeEventListener('resize', report) }
  }, [report])

  useEffect(() => () => { desktop.send({ type: 'player', cmd: 'surface', x: 0, y: 0, w: 0, h: 0, dpr: 1, visible: false }) }, [])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.target as HTMLElement)?.closest('input,textarea,select,[contenteditable]')) return
      if (e.key === '?') { setShortcuts(true); return }
      if (p?.loaded) handlePlayerKey(e, p)
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [p])

  if (!desktop.available()) {
    return <div className="flex h-full items-center justify-center p-8 text-center text-[13.5px] text-muted">The player is part of the FluxGet desktop app. Open FluxGet on this computer to watch your downloads.</div>
  }

  return (
    <div className="flex h-full min-h-0 flex-col lg:flex-row">
      <div className="flex min-h-0 min-w-0 flex-1 flex-col bg-[#08090b]">
        <div ref={stage} className="relative min-h-0 flex-1" onDoubleClick={() => loaded && p?.hasVideo && player('mode', { value: 'fullscreen' })}>
          {!loaded && <EmptyStage url={url} setUrl={setUrl} />}
          {loaded && p?.state === 'error' && <ErrorStage p={p} />}
          {loaded && p?.state !== 'error' && !p?.hasVideo && <AudioStage p={p!} />}
          {loaded && p?.mode !== 'embedded' && <ElsewhereStage p={p!} />}
        </div>
        <PlayerControls p={p} onPanel={() => setTab('playback')} />
      </div>
      <aside className="flex max-h-[45vh] w-full shrink-0 flex-col border-t border-line bg-surface lg:max-h-none lg:w-[340px] lg:border-t-0 lg:border-l">
        <div className="flex items-center gap-1 border-b border-line px-3 py-2">
          <TabButton active={tab === 'queue'} onClick={() => setTab('queue')} icon={<ListVideo size={14} />}>Up next</TabButton>
          <TabButton active={tab === 'library'} onClick={() => setTab('library')} icon={<Library size={14} />}>Library</TabButton>
          <TabButton active={tab === 'playback'} onClick={() => setTab('playback')} icon={<SlidersHorizontal size={14} />}>Playback</TabButton>
          <div className="flex-1" />
          <button type="button" title="Keyboard shortcuts (?)" aria-label="Keyboard shortcuts" onClick={() => setShortcuts(true)} className="rounded-md p-1.5 text-muted hover:bg-sunken hover:text-ink"><Keyboard size={15} /></button>
        </div>
        <div className="min-h-0 flex-1 overflow-y-auto">
          {tab === 'queue' && <QueueTab p={p} />}
          {tab === 'library' && <LibraryTab />}
          {tab === 'playback' && <PlaybackTab p={p} />}
        </div>
      </aside>
      <Modal open={shortcuts} onClose={() => setShortcuts(false)} title="Player shortcuts">
        <dl className="grid grid-cols-[150px_1fr] gap-y-2 text-[13px]">
          {SHORTCUTS.map(([k, d]) => <Shortcut key={k} k={k} d={d} />)}
        </dl>
        <p className="mt-4 text-[12px] text-muted">Shortcuts work on the Player screen, in fullscreen and in the mini player. Double-click the video for fullscreen; click to pause.</p>
      </Modal>
    </div>
  )
}

function Shortcut({ k, d }: { k: string; d: string }) {
  return <><dt><kbd className="rounded-md border border-line bg-sunken px-1.5 py-0.5 font-mono text-[12px]">{k}</kbd></dt><dd className="text-ink-2">{d}</dd></>
}

function TabButton({ active, onClick, icon, children }: { active: boolean; onClick: () => void; icon: ReactNode; children: ReactNode }) {
  return (
    <button type="button" onClick={onClick} className={cx('flex h-8 items-center gap-1.5 rounded-lg px-2.5 text-[12.5px] font-medium transition', active ? 'bg-sunken text-ink' : 'text-muted hover:text-ink')}>
      {icon}{children}
    </button>
  )
}

function EmptyStage({ url, setUrl }: { url: string; setUrl: (v: string) => void }) {
  const [recent, setRecent] = useState<RecentItem[]>([])
  useEffect(() => { void api.get<RecentItem[]>('/api/player/recent').then(setRecent).catch(() => {}) }, [])
  return (
    <div className="flex h-full flex-col items-center justify-center gap-6 overflow-y-auto p-8 text-[#edece8]">
      <div className="text-center">
        <div className="mx-auto mb-4 flex size-14 items-center justify-center rounded-2xl border border-white/10 bg-white/5"><Play size={24} className="ml-0.5 text-[#ff6e43]" fill="currentColor" /></div>
        <h2 className="text-[18px] font-semibold tracking-tight">Watch and listen</h2>
        <p className="mt-1 text-[13px] text-[#8d9098]">Open a file, drop media anywhere on this window, or pick something from your library.</p>
      </div>
      <div className="flex flex-wrap justify-center gap-2">
        <button onClick={() => player('openDialog')} className="inline-flex h-9 items-center gap-2 rounded-lg bg-[#edece8] px-4 text-[13px] font-semibold text-[#111215] hover:bg-white"><FolderOpen size={15} /> Open files</button>
        <form onSubmit={e => { e.preventDefault(); if (url.trim()) player('openUrl', { url: url.trim() }) }} className="flex gap-2">
          <div className="relative">
            <Link2 size={14} className="absolute top-1/2 left-3 -translate-y-1/2 text-[#6f727a]" />
            <input value={url} onChange={e => setUrl(e.target.value)} placeholder="Stream address (http, rtsp…)" aria-label="Stream address"
              className="h-9 w-64 rounded-lg border border-white/10 bg-white/5 pr-3 pl-8 text-[13px] text-white outline-none placeholder:text-[#6f727a] focus:border-white/25" />
          </div>
          <button disabled={!url.trim()} className="h-9 rounded-lg border border-white/10 px-3 text-[13px] font-medium hover:bg-white/10 disabled:opacity-40">Play</button>
        </form>
      </div>
      {recent.length > 0 && (
        <div className="w-full max-w-2xl">
          <div className="mb-2 text-[11px] font-semibold tracking-[0.08em] text-[#6f727a] uppercase">Continue watching</div>
          <div className="grid gap-2 sm:grid-cols-2">
            {recent.slice(0, 4).map(r => (
              <button key={r.path} onClick={() => player('openPaths', { paths: [r.path] })} className="group rounded-xl border border-white/[0.08] bg-white/[0.03] p-3 text-left transition hover:border-white/20">
                <div className="flex items-center gap-2">
                  {r.kind === 'audio' ? <Music size={14} className="text-[#8d9098]" /> : <FileVideo size={14} className="text-[#8d9098]" />}
                  <span className="truncate text-[13px] font-medium">{r.title}</span>
                </div>
                <Progress position={r.positionMs} duration={r.durationMs} />
                <div className="num mt-1 text-[11.5px] text-[#6f727a]">{r.positionMs > 0 && r.durationMs > 0 ? `${clock(r.positionMs)} of ${clock(r.durationMs)}` : 'Watched'} · {ago(r.lastPlayed)}</div>
              </button>
            ))}
          </div>
        </div>
      )}
    </div>
  )
}

function Progress({ position, duration }: { position: number; duration: number }) {
  if (!duration || !position) return null
  return <div className="mt-2 h-1 overflow-hidden rounded-full bg-white/10"><div className="h-full bg-[#ff6e43]" style={{ width: `${Math.min(100, (position / duration) * 100)}%` }} /></div>
}

function ErrorStage({ p }: { p: PlayerState }) {
  return (
    <div className="flex h-full flex-col items-center justify-center gap-3 p-8 text-center text-[#edece8]">
      <AlertTriangle size={28} className="text-[#e3ac45]" />
      <div className="max-w-md text-[14px]">{p.error}</div>
      <div className="truncate text-[12px] text-[#6f727a]" title={p.source ?? ''}>{p.title}</div>
      <div className="flex gap-2"><Button size="sm" onClick={() => player('play')}>Try again</Button><Button size="sm" variant="ghost" onClick={() => player('openDialog')}>Open another file</Button></div>
    </div>
  )
}

function AudioStage({ p }: { p: PlayerState }) {
  return (
    <div className="flex h-full flex-col items-center justify-center gap-5 p-8 text-[#edece8]">
      <div className={cx('flex size-40 items-center justify-center rounded-full border border-white/10 bg-gradient-to-b from-[#1d1f24] to-[#111215] shadow-2xl', p.state === 'playing' && 'animate-[spin_8s_linear_infinite]')}>
        <Disc3 size={64} className="text-[#ff6e43]" />
      </div>
      <div className="max-w-lg text-center">
        <div className="text-[17px] font-semibold tracking-tight">{p.title}</div>
        <div className="mt-1 text-[12.5px] text-[#8d9098]">{p.isStream ? 'Stream' : 'Audio'}{p.length ? ` · ${clock(p.length)}` : ''}</div>
      </div>
    </div>
  )
}

function ElsewhereStage({ p }: { p: PlayerState }) {
  return (
    <div className="absolute inset-0 flex flex-col items-center justify-center gap-3 bg-[#08090b] text-[#edece8]">
      <div className="text-[14px]">{p.mode === 'mini' ? 'Playing in the mini player' : 'Playing in fullscreen'}</div>
      <Button size="sm" onClick={() => player('mode', { value: 'embedded' })}>Bring it back here</Button>
    </div>
  )
}

function QueueTab({ p }: { p: PlayerState | null }) {
  return (
    <div className="p-2">
      <div className="flex gap-2 px-1 pb-2">
        <Button size="sm" onClick={() => player('openDialog', { append: true })}><FolderOpen size={14} /> Add files</Button>
        {p?.loaded && <Button size="sm" variant="ghost" onClick={() => player('close')}><X size={14} /> Close</Button>}
      </div>
      {p?.resumedFrom ? (
        <div className="mx-1 mb-2 flex items-center justify-between rounded-lg bg-accent-soft/60 px-3 py-2 text-[12.5px]">
          <span>Resumed at <b className="num">{clock(p.resumedFrom)}</b></span>
          <button className="font-medium text-accent hover:underline" onClick={() => player('startOver')}>Start over</button>
        </div>
      ) : null}
      {!p?.queue.length && <p className="px-2 py-6 text-center text-[13px] text-muted">Nothing queued. Add files or pick from the library.</p>}
      {p?.queue.map((q, i) => (
        <div key={`${q.source}-${i}`} className={cx('group flex items-center gap-2 rounded-lg px-2 py-2', i === p.index ? 'bg-sunken' : 'hover:bg-raised')}>
          <button className="flex min-w-0 flex-1 items-center gap-2.5 text-left" onClick={() => player('playIndex', { index: i })}>
            <span className={cx('num w-5 text-right text-[11.5px]', i === p.index ? 'text-accent' : 'text-faint')}>{i === p.index ? '▶' : i + 1}</span>
            <span className={cx('truncate text-[13px]', i === p.index && 'font-medium')}>{q.title}</span>
          </button>
          <button aria-label="Remove from queue" className="rounded p-1 text-faint opacity-0 group-hover:opacity-100 hover:text-ink" onClick={() => player('removeIndex', { index: i })}><X size={13} /></button>
        </div>
      ))}
    </div>
  )
}

function LibraryTab() {
  const [items, setItems] = useState<LibraryItem[]>([])
  const [search, setSearch] = useState('')
  const [kind, setKind] = useState<'all' | 'video' | 'audio'>('all')
  const [sort, setSort] = useState('date')
  const load = useCallback(() => {
    const q = new URLSearchParams({ sort })
    if (search.trim()) q.set('search', search.trim())
    if (kind !== 'all') q.set('kind', kind)
    void api.get<LibraryItem[]>(`/api/player/library?${q}`).then(setItems).catch(() => {})
  }, [search, kind, sort])
  useEffect(() => { const t = setTimeout(load, 150); return () => clearTimeout(t) }, [load])

  return (
    <div className="p-3">
      <div className="relative mb-2">
        <Search size={13} className="absolute top-1/2 left-2.5 -translate-y-1/2 text-faint" />
        <input className={inputClass + ' h-8 pl-8 text-[12.5px]'} placeholder="Search titles" value={search} onChange={e => setSearch(e.target.value)} aria-label="Search library" />
      </div>
      <div className="mb-3 flex items-center justify-between gap-2">
        <Segmented value={kind} onChange={setKind} options={[{ value: 'all', label: 'All' }, { value: 'video', label: 'Video' }, { value: 'audio', label: 'Audio' }]} />
        <select className={inputClass + ' h-8 w-28 text-[12px]'} value={sort} onChange={e => setSort(e.target.value)} aria-label="Sort">
          <option value="date">Newest</option><option value="played">Last played</option><option value="duration">Longest</option><option value="size">Largest</option><option value="title">Title</option>
        </select>
      </div>
      {items.length === 0 && <p className="py-8 text-center text-[13px] text-muted">{search ? 'No matches.' : 'Finished video and audio downloads appear here.'}</p>}
      <div className="space-y-1">
        {items.map(i => (
          <div key={i.jobId} className="group rounded-lg px-2 py-2 hover:bg-raised">
            <div className="flex items-center gap-2.5">
              <button className="flex min-w-0 flex-1 items-center gap-2.5 text-left" onClick={() => player('openJob', { jobId: i.jobId, path: i.path, title: i.title })} title={i.path}>
                <span className="flex size-8 shrink-0 items-center justify-center rounded-lg bg-sunken text-muted">{i.kind === 'audio' ? <Music size={15} /> : <FileVideo size={15} />}</span>
                <span className="min-w-0">
                  <span className="line-clamp-2 text-[12.5px] leading-snug font-medium">{i.title}</span>
                  <span className="num text-[11px] text-muted">{bytes(i.size)}{i.durationMs ? ` · ${clock(i.durationMs)}` : ''}{i.lastPlayed ? ` · played ${ago(i.lastPlayed)}` : ''}</span>
                </span>
              </button>
              <button aria-label="Add to queue" title="Add to queue" className="rounded p-1 text-faint opacity-0 group-hover:opacity-100 hover:text-ink" onClick={() => player('openJob', { jobId: i.jobId, path: i.path, title: i.title, append: true })}><ListVideo size={14} /></button>
              <button aria-label="Remove from library" title="Remove from library (keeps the file)" className="rounded p-1 text-faint opacity-0 group-hover:opacity-100 hover:text-bad"
                onClick={() => attempt(async () => { await api.del(`/api/jobs/${i.jobId}?deleteFile=false`); await api.del(`/api/player/recent?path=${encodeURIComponent(i.path)}`); load() }, 'Removed from library — the file was kept')}><Trash2 size={14} /></button>
            </div>
            <Progress position={i.positionMs} duration={i.durationMs} />
          </div>
        ))}
      </div>
    </div>
  )
}

function PlaybackTab({ p }: { p: PlayerState | null }) {
  if (!p?.loaded) return <p className="p-6 text-center text-[13px] text-muted">Start playing something to adjust subtitles, audio and picture.</p>
  const delay = (label: string, cmd: string, value: number) => (
    <Row label={label}>
      <div className="flex items-center gap-1">
        <Small onClick={() => player(cmd, { ms: value - 100 })}>−</Small>
        <span className="num w-16 text-center text-[12.5px]">{value > 0 ? '+' : ''}{(value / 1000).toFixed(1)} s</span>
        <Small onClick={() => player(cmd, { ms: value + 100 })}>+</Small>
        {value !== 0 && <Small onClick={() => player(cmd, { ms: 0 })}><RotateCcw size={12} /></Small>}
      </div>
    </Row>
  )
  return (
    <div className="space-y-5 p-4 text-[13px]">
      <Section title="Speed">
        <div className="flex flex-wrap gap-1">
          {p.speeds.map(s => <Chip key={s} active={Math.abs(p.rate - s) < 0.01} onClick={() => player('rate', { value: s })}>{s}×</Chip>)}
        </div>
        <input type="range" min={0.25} max={4} step={0.05} value={p.rate} onChange={e => player('rate', { value: Number(e.target.value) })} className="fg-range mt-2 w-full" aria-label="Fine speed" />
      </Section>
      <Section title="Subtitles">
        <select className={inputClass + ' h-8 text-[12.5px]'} value={p.subtitleTrack} onChange={e => player('subtitleTrack', { id: Number(e.target.value) })} aria-label="Subtitle track">
          <option value={-1}>Off</option>
          {p.subtitleTracks.filter(t => t.id >= 0).map(t => <option key={t.id} value={t.id}>{t.name}</option>)}
        </select>
        <div className="mt-2 flex flex-wrap items-center gap-2">
          <Button size="sm" onClick={() => player('subtitleDialog')}>Add subtitle file…</Button>
          {!p.isStream && p.source && <SubtitleGenerator mediaPath={p.source} />}
        </div>
        <p className="text-[11.5px] text-muted">SRT, ASS, SSA and VTT files work. You can also drop a subtitle file onto the player.</p>
        {delay('Subtitle delay', 'subtitleDelay', p.subtitleDelayMs)}
        <Row label="Subtitle size">
          <select className={inputClass + ' h-8 w-28 text-[12px]'} value={p.subtitleScale} onChange={e => player('subtitleScale', { value: Number(e.target.value) })}>
            {[75, 100, 125, 150, 200].map(v => <option key={v} value={v}>{v}%</option>)}
          </select>
        </Row>
      </Section>
      <Section title="Audio">
        <select className={inputClass + ' h-8 text-[12.5px]'} value={p.audioTrack} onChange={e => player('audioTrack', { id: Number(e.target.value) })} aria-label="Audio track" disabled={p.audioTracks.length < 2}>
          {p.audioTracks.filter(t => t.id >= 0).map(t => <option key={t.id} value={t.id}>{t.name}</option>)}
        </select>
        {delay('Audio sync', 'audioDelay', p.audioDelayMs)}
      </Section>
      {p.hasVideo && (
        <Section title="Picture">
          <Row label="Aspect ratio"><Select value={p.aspect ?? ''} onChange={v => player('aspect', { value: v })} options={[['', 'Original'], ['16:9', '16:9'], ['4:3', '4:3'], ['21:9', '21:9'], ['1:1', '1:1']]} /></Row>
          <Row label="Crop"><Select value={p.crop ?? ''} onChange={v => player('crop', { value: v })} options={[['', 'None'], ['16:9', '16:9'], ['4:3', '4:3'], ['2.35:1', '2.35:1'], ['1:1', '1:1']]} /></Row>
          <Row label="Zoom"><Select value={String(p.zoom)} onChange={v => player('zoom', { value: Number(v) })} options={[['0', 'Fit'], ['0.5', '50%'], ['1', '100%'], ['1.5', '150%'], ['2', '200%']]} /></Row>
          <Row label="Rotate">
            <div className="flex items-center gap-1">
              <Small onClick={() => player('rotate', { value: p.rotation - 90 })}><RotateCcw size={12} /></Small>
              <span className="num w-10 text-center text-[12.5px]">{p.rotation}°</span>
              <Small onClick={() => player('rotate', { value: p.rotation + 90 })}><RotateCw size={12} /></Small>
            </div>
          </Row>
          {(['brightness', 'contrast', 'saturation'] as const).map(name => (
            <Row key={name} label={name[0].toUpperCase() + name.slice(1)}>
              <input type="range" min={0} max={2} step={0.05} value={p[name] || 1} className="fg-range w-32" aria-label={name} onChange={e => player('adjust', { name, value: Number(e.target.value) })} />
            </Row>
          ))}
          <div className="flex justify-end"><Button size="sm" variant="ghost" onClick={() => player('adjust', { name: 'reset' })}>Reset picture</Button></div>
          <Row label="Hardware decoding"><Toggle label="Hardware decoding" checked={p.hardwareDecoding} onChange={v => player('hardwareDecoding', { value: v })} /></Row>
          <Button size="sm" variant="ghost" onClick={() => player('frame')}>Step one frame (.)</Button>
        </Section>
      )}
    </div>
  )
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return <section><h3 className="mb-2 text-[11px] font-semibold tracking-[0.08em] text-muted uppercase">{title}</h3><div className="space-y-2">{children}</div></section>
}
function Row({ label, children }: { label: string; children: ReactNode }) {
  return <div className="flex items-center justify-between gap-3"><span className="text-ink-2">{label}</span>{children}</div>
}
function Chip({ active, onClick, children }: { active: boolean; onClick: () => void; children: ReactNode }) {
  return <button type="button" onClick={onClick} className={cx('num h-7 rounded-md border px-2 text-[12px] font-medium transition', active ? 'border-ink bg-ink text-bg' : 'border-line text-ink-2 hover:border-line-strong')}>{children}</button>
}
function Small({ onClick, children }: { onClick: () => void; children: ReactNode }) {
  return <button type="button" onClick={onClick} className="flex size-7 items-center justify-center rounded-md border border-line text-ink-2 hover:border-line-strong">{children}</button>
}
function Select({ value, onChange, options }: { value: string; onChange: (v: string) => void; options: [string, string][] }) {
  return <select className={inputClass + ' h-8 w-28 text-[12px]'} value={value} onChange={e => onChange(e.target.value)}>{options.map(([v, l]) => <option key={v} value={v}>{l}</option>)}</select>
}
