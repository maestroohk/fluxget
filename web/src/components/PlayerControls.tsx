import { useRef, useState, type ReactNode } from 'react'
import { Captions, Gauge, Languages, Maximize, Minimize, Pause, PictureInPicture2, Play, SkipBack, SkipForward, Volume1, Volume2, VolumeX } from 'lucide-react'
import { clock, isPlaying, player, type PlayerState } from '../player'
import { cx } from '../ui'

type Menu = 'speed' | 'audio' | 'subs' | null

export function PlayerControls({ p, overlay, onPanel }: { p: PlayerState | null; overlay?: boolean; onPanel?: (section: 'playback') => void }) {
  const [menu, setMenu] = useState<Menu>(null)
  const disabled = !p?.loaded
  const playing = isPlaying(p)
  const open = (m: Menu) => {
    if (!overlay) { onPanel?.('playback'); return }
    setMenu(cur => (cur === m ? null : m))
  }

  return (
    <div className={cx('relative select-none', overlay ? 'px-5 pt-3 pb-3.5' : 'border-t border-white/[0.06] bg-[#0e0f12] px-5 pt-2 pb-3')}>
      {overlay && menu && p && <Popover menu={menu} p={p} onClose={() => setMenu(null)} />}
      <Timeline p={p} />
      <div className="mt-1.5 flex items-center gap-1 text-[#edece8]">
        <Btn label="Previous (P)" onClick={() => player('prev')} disabled={disabled}><SkipBack size={18} fill="currentColor" /></Btn>
        <button type="button" aria-label={playing ? 'Pause (Space)' : 'Play (Space)'} title={playing ? 'Pause (Space)' : 'Play (Space)'} disabled={disabled}
          onClick={() => player('toggle')}
          className="mx-1 flex size-10 items-center justify-center rounded-full bg-[#edece8] text-[#111215] transition hover:scale-105 active:scale-95 disabled:opacity-40">
          {playing ? <Pause size={18} fill="currentColor" /> : <Play size={18} fill="currentColor" className="ml-0.5" />}
        </button>
        <Btn label="Next (N)" onClick={() => player('next')} disabled={disabled || !p || p.index >= p.queue.length - 1}><SkipForward size={18} fill="currentColor" /></Btn>
        <span className="num ml-2 text-[12.5px] text-[#c4c4c0]">{clock(p?.time ?? 0)} <span className="text-[#6f727a]">/ {clock(p?.length ?? 0)}</span></span>
        <div className="min-w-0 flex-1 truncate px-3 text-center text-[12.5px] text-[#8d9098]">{overlay ? p?.title : ''}</div>
        <Btn label="Playback speed" onClick={() => open('speed')} disabled={disabled} active={menu === 'speed'}>
          <span className="flex items-center gap-1 text-[12px] font-semibold"><Gauge size={16} />{p ? `${p.rate}×` : '1×'}</span>
        </Btn>
        <Btn label="Subtitles" onClick={() => open('subs')} disabled={disabled} active={menu === 'subs' || (p?.subtitleTrack ?? -1) >= 0}><Captions size={18} /></Btn>
        <Btn label="Audio track" onClick={() => open('audio')} disabled={disabled || (p?.audioTracks.length ?? 0) < 2}><Languages size={17} /></Btn>
        <Volume p={p} />
        {p?.mode !== 'mini' && <Btn label="Mini player" onClick={() => player('mode', { value: 'mini' })} disabled={disabled || !p?.hasVideo}><PictureInPicture2 size={17} /></Btn>}
        <Btn label={p?.mode === 'fullscreen' ? 'Exit fullscreen (F)' : 'Fullscreen (F)'} onClick={() => player('mode', { value: p?.mode === 'fullscreen' ? 'embedded' : 'fullscreen' })} disabled={disabled || !p?.hasVideo}>
          {p?.mode === 'fullscreen' ? <Minimize size={17} /> : <Maximize size={17} />}
        </Btn>
      </div>
    </div>
  )
}

function Btn({ label, onClick, disabled, active, children }: { label: string; onClick: () => void; disabled?: boolean; active?: boolean; children: ReactNode }) {
  return (
    <button type="button" aria-label={label} title={label} disabled={disabled} onClick={onClick}
      className={cx('flex h-9 min-w-9 items-center justify-center rounded-lg px-2 transition hover:bg-white/10 disabled:opacity-35 disabled:hover:bg-transparent', active ? 'text-[#ff6e43]' : 'text-[#edece8]')}>
      {children}
    </button>
  )
}

function Timeline({ p }: { p: PlayerState | null }) {
  const ref = useRef<HTMLDivElement>(null)
  const [drag, setDrag] = useState<number | null>(null)
  const [hover, setHover] = useState<{ x: number; ms: number } | null>(null)
  const length = p?.length ?? 0
  const fraction = drag ?? (length > 0 ? (p!.time / length) : 0)
  const at = (clientX: number) => {
    const r = ref.current!.getBoundingClientRect()
    return Math.min(1, Math.max(0, (clientX - r.left) / r.width))
  }
  const seekable = !!p?.seekable && length > 0
  return (
    <div ref={ref} role="slider" aria-label="Seek" aria-valuemin={0} aria-valuemax={length} aria-valuenow={p?.time ?? 0} aria-valuetext={clock(p?.time ?? 0)} tabIndex={-1}
      className={cx('group relative flex h-4 items-center', seekable ? 'cursor-pointer' : 'pointer-events-none opacity-50')}
      onPointerDown={e => { if (!seekable) return; e.currentTarget.setPointerCapture(e.pointerId); setDrag(at(e.clientX)) }}
      onPointerMove={e => {
        if (!seekable) return
        const f = at(e.clientX)
        setHover({ x: f * ref.current!.clientWidth, ms: f * length })
        if (drag !== null) setDrag(f)
      }}
      onPointerLeave={() => setHover(null)}
      onPointerUp={e => { if (drag === null) return; player('seek', { ms: Math.round(at(e.clientX) * length) }); setDrag(null) }}>
      <div className="h-1 w-full overflow-hidden rounded-full bg-white/15 transition-[height] group-hover:h-1.5">
        <div className="h-full rounded-full bg-[#ff6e43]" style={{ width: `${fraction * 100}%` }} />
      </div>
      <div className="absolute size-3 -translate-x-1/2 rounded-full bg-white opacity-0 shadow transition-opacity group-hover:opacity-100" style={{ left: `${fraction * 100}%` }} />
      {hover && <div className="num pointer-events-none absolute -top-7 -translate-x-1/2 rounded-md bg-black/85 px-1.5 py-0.5 text-[11px] text-white" style={{ left: hover.x }}>{clock(hover.ms)}</div>}
    </div>
  )
}

function Volume({ p }: { p: PlayerState | null }) {
  const v = p?.volume ?? 100
  const muted = p?.muted || v === 0
  return (
    <div className="group flex items-center">
      <Btn label="Mute (M)" onClick={() => player('mute')} disabled={!p?.loaded}>{muted ? <VolumeX size={18} /> : v < 50 ? <Volume1 size={18} /> : <Volume2 size={18} />}</Btn>
      <input type="range" min={0} max={150} step={1} value={muted ? 0 : v} aria-label="Volume" disabled={!p?.loaded}
        onChange={e => player('volume', { value: Number(e.target.value) })}
        className="fg-range w-0 opacity-0 transition-all duration-200 group-hover:w-24 group-hover:opacity-100 focus:w-24 focus:opacity-100" />
      <span className="num w-0 overflow-hidden text-[11px] text-[#8d9098] transition-all group-hover:w-9">{muted ? 0 : v}%</span>
    </div>
  )
}

function Popover({ menu, p, onClose }: { menu: Exclude<Menu, null>; p: PlayerState; onClose: () => void }) {
  const item = (label: ReactNode, active: boolean, onClick: () => void, key: string | number) => (
    <button key={key} type="button" onClick={() => { onClick(); onClose() }}
      className={cx('flex w-full items-center justify-between rounded-md px-3 py-1.5 text-left text-[13px] hover:bg-white/10', active && 'text-[#ff6e43]')}>
      {label}{active && <span>✓</span>}
    </button>
  )
  return (
    <div className="mb-2 ml-auto max-h-64 w-60 overflow-y-auto rounded-xl border border-white/10 bg-[#1d1f24] p-1.5 text-[#edece8]">
      {menu === 'speed' && p.speeds.map(s => item(`${s}×`, Math.abs(p.rate - s) < 0.01, () => player('rate', { value: s }), s))}
      {menu === 'audio' && p.audioTracks.filter(t => t.id >= 0).map(t => item(t.name, p.audioTrack === t.id, () => player('audioTrack', { id: t.id }), t.id))}
      {menu === 'subs' && <>
        {item('Off', p.subtitleTrack < 0, () => player('subtitleTrack', { id: -1 }), 'off')}
        {p.subtitleTracks.filter(t => t.id >= 0).map(t => item(t.name, p.subtitleTrack === t.id, () => player('subtitleTrack', { id: t.id }), t.id))}
        {item('Add subtitle file…', false, () => player('subtitleDialog'), 'add')}
      </>}
    </div>
  )
}
