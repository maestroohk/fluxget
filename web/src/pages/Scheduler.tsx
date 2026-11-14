import { useEffect, useMemo, useState } from 'react'
import { CalendarClock, Moon } from 'lucide-react'
import { api, type Settings } from '../api'
import { setSettings, useStore } from '../store'
import { limitText, parseLimit } from '../format'
import { Button, Card, cx, Field, inputClass, SectionTitle, Toggle } from '../ui'
import { JobRow } from '../components/jobs'
import { JobDetail } from '../components/JobDetail'
import { attempt } from '../components/toast'

const DAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']

export function Scheduler() {
  const settings = useStore(s => s.settings)
  const jobs = useStore(s => s.jobs)
  const [draft, setDraft] = useState<Settings | null>(settings)
  const [limit, setLimit] = useState('')
  const [open, setOpen] = useState<number | null>(null)
  useEffect(() => { setDraft(settings); setLimit(limitText(settings?.globalSpeedLimit ?? 0)) }, [settings])
  const scheduled = useMemo(() => [...jobs.values()].filter(j => j.useSchedule), [jobs])
  if (!draft) return null
  const s = draft.schedule
  const set = (patch: Partial<Settings['schedule']>) => setDraft({ ...draft, schedule: { ...s, ...patch } })
  const save = (next = draft) => attempt(async () => setSettings(await api.put<Settings>('/api/settings', { ...next, globalSpeedLimit: parseLimit(limit) })), 'Schedule saved')
  const t = (v: string) => v.slice(0, 5)

  return (
    <div className="h-full overflow-y-auto">
      <div className="mx-auto max-w-4xl space-y-8 px-4 py-6 sm:px-6">
        <Card className="p-5">
          <div className="flex items-start justify-between gap-4">
            <div>
              <h2 className="flex items-center gap-2 text-[15px] font-semibold"><CalendarClock size={17} className="text-accent" /> Download window</h2>
              <p className="mt-1 text-[13px] text-muted">Downloads added with "Wait for schedule" only run inside this window and pause when it closes.</p>
            </div>
            <Toggle label="Enable schedule" checked={s.enabled} onChange={v => set({ enabled: v })} />
          </div>
          <div className={cx('mt-5 grid gap-4 transition sm:grid-cols-[160px_160px_1fr]', !s.enabled && 'pointer-events-none opacity-45')}>
            <Field label="Start"><input type="time" className={inputClass} value={t(s.start)} onChange={e => set({ start: e.target.value + ':00' })} /></Field>
            <Field label="Stop" hint={s.start > s.end ? 'Runs overnight into the next day' : undefined}><input type="time" className={inputClass} value={t(s.end)} onChange={e => set({ end: e.target.value + ':00' })} /></Field>
            <Field label="Days">
              <div className="flex flex-wrap gap-1">
                {DAYS.map((d, i) => (
                  <button key={d} type="button" onClick={() => set({ days: s.days.includes(i) ? s.days.filter(x => x !== i) : [...s.days, i].sort() })}
                    className={cx('h-9 w-11 rounded-lg border text-[12.5px] font-medium transition', s.days.includes(i) ? 'border-ink bg-ink text-bg' : 'border-line text-muted hover:border-line-strong')}>{d}</button>
                ))}
              </div>
            </Field>
          </div>
          <div className="mt-5 grid gap-4 border-t border-line pt-5 sm:grid-cols-[240px_1fr]">
            <Field label="Global speed limit" hint="Applies to all downloads. Leave empty for unlimited.">
              <input className={inputClass} placeholder="Unlimited" value={limit} onChange={e => setLimit(e.target.value)} />
            </Field>
          </div>
          <div className="mt-5 flex flex-wrap gap-2">
            <Button variant="primary" onClick={() => save()}>Save</Button>
            <Button onClick={() => { const n = { ...draft, schedule: { ...s, enabled: true, start: '01:00:00', end: '07:00:00', days: [0, 1, 2, 3, 4, 5, 6] } }; setDraft(n); void save(n) }}><Moon size={15} /> Nights only (01:00–07:00)</Button>
          </div>
        </Card>

        <section>
          <SectionTitle>Waiting for the schedule</SectionTitle>
          {scheduled.length === 0
            ? <p className="text-[13px] text-muted">No downloads use the schedule. Turn on "Wait for schedule" when adding a download, or in its details.</p>
            : <Card className="overflow-hidden">{scheduled.map(j => <JobRow key={j.id} job={j} onOpen={x => setOpen(x.id)} />)}</Card>}
        </section>
      </div>
      <JobDetail jobId={open} onClose={() => setOpen(null)} />
    </div>
  )
}
