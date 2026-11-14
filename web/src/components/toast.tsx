import { useSyncExternalStore } from 'react'
import { CheckCircle2, AlertTriangle, Info } from 'lucide-react'
import { cx } from '../ui'

type Toast = { id: number; text: string; tone: 'ok' | 'bad' | 'info' }
let toasts: Toast[] = []
let nextId = 1
const subs = new Set<() => void>()
const emit = () => subs.forEach(s => s())

export function toast(text: string, tone: Toast['tone'] = 'info') {
  if (toasts.some(x => x.text === text)) return
  const t = { id: nextId++, text, tone }
  toasts = [...toasts.slice(-3), t]
  emit()
  setTimeout(() => { toasts = toasts.filter(x => x.id !== t.id); emit() }, tone === 'bad' ? 6000 : 3500)
}

export async function attempt(fn: () => Promise<unknown>, success?: string) {
  try {
    await fn()
    if (success) toast(success, 'ok')
    return true
  } catch (e) {
    toast(e instanceof Error ? e.message : String(e), 'bad')
    return false
  }
}

export function Toaster() {
  const list = useSyncExternalStore(cb => { subs.add(cb); return () => subs.delete(cb) }, () => toasts)
  return (
    <div className="pointer-events-none fixed right-4 bottom-20 z-[60] flex w-[min(380px,calc(100vw-32px))] flex-col gap-2 sm:bottom-4" aria-live="polite">
      {list.map(t => (
        <div key={t.id} className="pointer-events-auto flex items-start gap-2.5 rounded-xl border border-line bg-surface px-3.5 py-3 text-[13px] shadow-pop animate-rise">
          {t.tone === 'ok' ? <CheckCircle2 size={16} className="mt-px shrink-0 text-ok" /> : t.tone === 'bad' ? <AlertTriangle size={16} className="mt-px shrink-0 text-bad" /> : <Info size={16} className={cx('mt-px shrink-0 text-info')} />}
          <span className="min-w-0 break-words">{t.text}</span>
        </div>
      ))}
    </div>
  )
}
