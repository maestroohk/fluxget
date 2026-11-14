import { useEffect, useRef, type ButtonHTMLAttributes, type ReactNode } from 'react'
import { X } from 'lucide-react'
import type { Segment } from './api'
import type { Tone } from './format'

export const cx = (...c: (string | false | null | undefined)[]) => c.filter(Boolean).join(' ')

type Variant = 'primary' | 'secondary' | 'ghost' | 'danger'

export function Button({ variant = 'secondary', size = 'md', className, ...p }: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: Variant; size?: 'sm' | 'md' }) {
  return (
    <button
      {...p}
      className={cx(
        'inline-flex items-center justify-center gap-1.5 rounded-lg font-medium transition-[background,color,box-shadow,transform] active:scale-[0.98] disabled:opacity-45 disabled:pointer-events-none select-none whitespace-nowrap',
        size === 'sm' ? 'h-7 px-2.5 text-[12.5px]' : 'h-9 px-3.5 text-[13.5px]',
        variant === 'primary' && 'bg-ink text-bg hover:bg-ink-2 shadow-card',
        variant === 'secondary' && 'bg-surface text-ink border border-line hover:border-line-strong hover:bg-raised',
        variant === 'ghost' && 'text-ink-2 hover:bg-sunken hover:text-ink',
        variant === 'danger' && 'bg-bad text-white hover:opacity-90',
        className,
      )}
    />
  )
}

export function IconButton({ label, className, children, ...p }: ButtonHTMLAttributes<HTMLButtonElement> & { label: string }) {
  return (
    <button
      {...p}
      aria-label={label}
      title={label}
      className={cx('inline-flex size-8 shrink-0 items-center justify-center rounded-lg text-muted transition-colors hover:bg-sunken hover:text-ink disabled:opacity-40', className)}
    >
      {children}
    </button>
  )
}

const toneClass: Record<Tone, string> = {
  accent: 'bg-accent-soft text-accent',
  ok: 'bg-ok-soft text-ok',
  warn: 'bg-warn-soft text-warn',
  bad: 'bg-bad-soft text-bad',
  info: 'bg-info-soft text-info',
  muted: 'bg-sunken text-muted',
}

export function Pill({ tone, children, dot }: { tone: Tone; children: ReactNode; dot?: boolean }) {
  return (
    <span className={cx('inline-flex h-[22px] items-center gap-1.5 rounded-full px-2 text-[11.5px] font-medium whitespace-nowrap', toneClass[tone])}>
      {dot && <span className={cx('size-1.5 rounded-full bg-current', tone === 'accent' && 'animate-pulse')} />}
      {children}
    </span>
  )
}

const barTone: Record<Tone, string> = { accent: 'bg-accent', ok: 'bg-ok', warn: 'bg-warn', bad: 'bg-bad', info: 'bg-info', muted: 'bg-faint' }

export function Progress({ value, tone = 'accent', flowing, className }: { value: number | null; tone?: Tone; flowing?: boolean; className?: string }) {
  return (
    <div className={cx('relative h-1 w-full overflow-hidden rounded-full bg-sunken', className)} role="progressbar" aria-valuenow={value ?? undefined} aria-valuemin={0} aria-valuemax={100}>
      {value == null ? (
        <div className={cx('absolute inset-y-0 w-1/3 rounded-full', barTone[tone], flowing && 'animate-pulse')} style={{ left: '33%' }} />
      ) : (
        <div className={cx('h-full rounded-full transition-[width] duration-500 ease-out', barTone[tone], flowing && 'flowing')} style={{ width: `${value}%` }} />
      )}
    </div>
  )
}

export function SegmentMap({ segments, total }: { segments: Segment[]; total: number }) {
  if (!total || segments.length === 0) return null
  return (
    <div className="relative h-8 w-full overflow-hidden rounded-md border border-line bg-sunken">
      {segments.map((s, i) => {
        const len = s.end >= s.start ? s.end - s.start + 1 : total - s.start
        const left = (s.start / total) * 100
        const width = (len / total) * 100
        const fill = len > 0 ? Math.min(1, s.received / len) : 0
        return (
          <div key={i} className="absolute inset-y-0 border-r border-bg/70" style={{ left: `${left}%`, width: `${width}%` }}>
            <div className={cx('h-full transition-[width] duration-500', s.active ? 'bg-accent flowing' : fill >= 1 ? 'bg-ink-2/80' : 'bg-accent/60')} style={{ width: `${fill * 100}%` }} />
          </div>
        )
      })}
    </div>
  )
}

export function Sparkline({ values, height = 44, className, stroke = 'var(--accent)' }: { values: number[]; height?: number; className?: string; stroke?: string }) {
  const w = 300
  const max = Math.max(1, ...values)
  const pts = values.map((v, i) => [(i / Math.max(1, values.length - 1)) * w, height - 2 - (v / max) * (height - 6)])
  const d = pts.map((p, i) => `${i ? 'L' : 'M'}${p[0].toFixed(1)},${p[1].toFixed(1)}`).join(' ')
  return (
    <svg viewBox={`0 0 ${w} ${height}`} preserveAspectRatio="none" className={cx('w-full', className)} style={{ height }} aria-hidden>
      <path d={`${d} L${w},${height} L0,${height} Z`} fill={stroke} opacity="0.1" />
      <path d={d} fill="none" stroke={stroke} strokeWidth="1.5" vectorEffect="non-scaling-stroke" strokeLinejoin="round" />
    </svg>
  )
}

export function Modal({ open, onClose, title, children, footer, wide }: { open: boolean; onClose: () => void; title: ReactNode; children: ReactNode; footer?: ReactNode; wide?: boolean }) {
  const ref = useRef<HTMLDivElement>(null)
  useEffect(() => {
    if (!open) return
    const prev = document.activeElement as HTMLElement | null
    const key = (e: KeyboardEvent) => e.key === 'Escape' && onClose()
    window.addEventListener('keydown', key)
    setTimeout(() => ref.current?.querySelector<HTMLElement>('[autofocus],input,textarea,button')?.focus(), 10)
    return () => { window.removeEventListener('keydown', key); prev?.focus() }
  }, [open, onClose])
  if (!open) return null
  return (
    <div className="fixed inset-0 z-50 flex items-end justify-center bg-black/35 p-0 backdrop-blur-[2px] animate-fade sm:items-center sm:p-6" onMouseDown={e => e.target === e.currentTarget && onClose()}>
      <div ref={ref} role="dialog" aria-modal="true" className={cx('flex max-h-[92vh] w-full flex-col rounded-t-2xl border border-line bg-surface shadow-pop animate-rise sm:rounded-2xl', wide ? 'sm:max-w-3xl' : 'sm:max-w-lg')}>
        <div className="flex items-center justify-between gap-3 px-5 pt-4 pb-2">
          <h2 className="text-[15px] font-semibold tracking-tight">{title}</h2>
          <IconButton label="Close" onClick={onClose}><X size={16} /></IconButton>
        </div>
        <div className="overflow-y-auto px-5 pb-4">{children}</div>
        {footer && <div className="flex flex-wrap items-center justify-end gap-2 border-t border-line px-5 py-3">{footer}</div>}
      </div>
    </div>
  )
}

export function Drawer({ open, onClose, children, title }: { open: boolean; onClose: () => void; children: ReactNode; title: ReactNode }) {
  useEffect(() => {
    if (!open) return
    const key = (e: KeyboardEvent) => e.key === 'Escape' && onClose()
    window.addEventListener('keydown', key)
    return () => window.removeEventListener('keydown', key)
  }, [open, onClose])
  if (!open) return null
  return (
    <div className="fixed inset-0 z-40 flex justify-end bg-black/20 animate-fade" onMouseDown={e => e.target === e.currentTarget && onClose()}>
      <aside role="dialog" aria-modal="true" className="flex h-full w-full max-w-xl flex-col border-l border-line bg-surface shadow-pop animate-slide">
        <div className="flex items-start justify-between gap-3 border-b border-line px-5 py-4">
          <div className="min-w-0 flex-1">{title}</div>
          <IconButton label="Close" onClick={onClose}><X size={16} /></IconButton>
        </div>
        <div className="flex-1 overflow-y-auto">{children}</div>
      </aside>
    </div>
  )
}

export function Field({ label, hint, children }: { label: string; hint?: ReactNode; children: ReactNode }) {
  return (
    <label className="block">
      <span className="mb-1.5 block text-[12.5px] font-medium text-ink-2">{label}</span>
      {children}
      {hint && <span className="mt-1 block text-[12px] text-muted">{hint}</span>}
    </label>
  )
}

export const inputClass = 'h-9 w-full rounded-lg border border-line bg-raised px-3 text-[13.5px] text-ink placeholder:text-faint outline-none transition focus:border-line-strong focus:bg-surface focus:ring-4 focus:ring-accent/10'

export function Toggle({ checked, onChange, label }: { checked: boolean; onChange: (v: boolean) => void; label: string }) {
  return (
    <button type="button" role="switch" aria-checked={checked} aria-label={label} onClick={() => onChange(!checked)}
      className={cx('relative h-5 w-9 shrink-0 rounded-full transition-colors', checked ? 'bg-accent' : 'bg-line-strong')}>
      <span className={cx('absolute top-0.5 size-4 rounded-full bg-white shadow transition-transform', checked ? 'translate-x-[18px]' : 'translate-x-0.5')} />
    </button>
  )
}

export function Segmented<T extends string>({ value, options, onChange }: { value: T; options: { value: T; label: ReactNode }[]; onChange: (v: T) => void }) {
  return (
    <div className="inline-flex rounded-lg border border-line bg-sunken p-0.5">
      {options.map(o => (
        <button key={o.value} type="button" onClick={() => onChange(o.value)}
          className={cx('h-7 rounded-md px-3 text-[12.5px] font-medium transition', value === o.value ? 'bg-surface text-ink shadow-card' : 'text-muted hover:text-ink')}>
          {o.label}
        </button>
      ))}
    </div>
  )
}

export function Empty({ icon, title, children, action }: { icon: ReactNode; title: string; children?: ReactNode; action?: ReactNode }) {
  return (
    <div className="flex flex-col items-center justify-center px-6 py-20 text-center">
      <div className="mb-4 flex size-12 items-center justify-center rounded-2xl border border-line bg-surface text-muted shadow-card">{icon}</div>
      <h3 className="text-[15px] font-semibold">{title}</h3>
      {children && <p className="mt-1 max-w-sm text-[13px] text-muted">{children}</p>}
      {action && <div className="mt-5">{action}</div>}
    </div>
  )
}

export function Card({ children, className }: { children: ReactNode; className?: string }) {
  return <div className={cx('rounded-xl border border-line bg-surface shadow-card', className)}>{children}</div>
}

export function SectionTitle({ children, aside }: { children: ReactNode; aside?: ReactNode }) {
  return (
    <div className="mb-2.5 flex items-center justify-between gap-3">
      <h2 className="text-[12px] font-semibold tracking-[0.06em] text-muted uppercase">{children}</h2>
      {aside}
    </div>
  )
}
