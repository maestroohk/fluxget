import { cx } from '../ui'

export function Mark({ size = 28 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 64 64" aria-hidden>
      <rect width="64" height="64" rx="16" fill="var(--ink)" />
      <path d="M18 22h22M18 32h28M18 42h16" stroke="var(--bg)" strokeWidth="5" strokeLinecap="round" />
      <path d="M44 36l6 6-6 6" fill="none" stroke="var(--accent)" strokeWidth="5" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  )
}

export function Logo({ className }: { className?: string }) {
  return (
    <div className={cx('flex items-center gap-2.5', className)}>
      <Mark />
      <span className="text-[16px] font-semibold tracking-[-0.02em]">FluxGet</span>
    </div>
  )
}
