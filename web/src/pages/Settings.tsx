import { useEffect, useState, type ReactNode } from 'react'
import { FolderOpen, Monitor, Moon, Puzzle, Sun, Trash2 } from 'lucide-react'
import { api, desktop, type Settings, type SystemInfo } from '../api'
import { setSettings, useStore } from '../store'
import { ago, limitText, parseLimit } from '../format'
import { Button, Card, Field, IconButton, inputClass, Segmented, Toggle } from '../ui'
import { attempt } from '../components/toast'
import { BrowserSetup } from '../components/BrowserSetup'

type Theme = 'system' | 'light' | 'dark'

export function applyTheme(t: Theme) {
  try { localStorage.setItem('fg-theme', t) } catch { /* storage unavailable */ }
  const dark = t === 'dark' || (t === 'system' && matchMedia('(prefers-color-scheme: dark)').matches)
  document.documentElement.classList.toggle('dark', dark)
  desktop.send({ type: 'theme', dark })
}

function readTheme(): Theme {
  try { return (localStorage.getItem('fg-theme') as Theme) || 'system' } catch { return 'system' }
}

export function SettingsPage() {
  const settings = useStore(s => s.settings)
  const [draft, setDraft] = useState<Settings | null>(settings)
  const [limits, setLimits] = useState({ global: '', upload: '' })
  const [theme, setTheme] = useState<Theme>(readTheme)
  const [system, setSystem] = useState<SystemInfo | null>(null)
  const [tokens, setTokens] = useState<{ id: string; name: string; createdAt: string }[]>([])

  useEffect(() => {
    setDraft(settings)
    if (settings) setLimits({ global: limitText(settings.globalSpeedLimit), upload: limitText(settings.torrentUploadLimit) })
  }, [settings])
  useEffect(() => {
    void api.get<SystemInfo>('/api/system').then(setSystem)
    void api.get<typeof tokens>('/api/tokens').then(setTokens)
  }, [])

  if (!draft) return null
  const dirty = !!settings && (JSON.stringify({ ...draft, globalSpeedLimit: 0, torrentUploadLimit: 0 }) !== JSON.stringify({ ...settings, globalSpeedLimit: 0, torrentUploadLimit: 0 })
    || parseLimit(limits.global) !== settings.globalSpeedLimit || parseLimit(limits.upload) !== settings.torrentUploadLimit)
  const save = () => attempt(async () => setSettings(await api.put<Settings>('/api/settings', {
    ...draft, globalSpeedLimit: parseLimit(limits.global), torrentUploadLimit: parseLimit(limits.upload),
  })), 'Settings saved')

  return (
    <div className="relative h-full overflow-y-auto">
      <div className="mx-auto max-w-3xl space-y-6 px-4 py-6 pb-24 sm:px-6">
        <Group title="Downloads">
          <Field label="Download folder">
            <div className="flex gap-2">
              <input className={inputClass} value={draft.downloadDirectory} onChange={e => setDraft({ ...draft, downloadDirectory: e.target.value })} />
              {desktop.available() && <Button onClick={async () => { const p = await desktop.pickFolder(draft.downloadDirectory); if (p) setDraft({ ...draft, downloadDirectory: p }) }}><FolderOpen size={15} /> Browse</Button>}
            </div>
          </Field>
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Downloads at the same time" hint="Torrents that are only seeding don't count.">
              <input type="number" min={1} max={20} className={inputClass} value={draft.maxConcurrentJobs} onChange={e => setDraft({ ...draft, maxConcurrentJobs: Number(e.target.value) })} />
            </Field>
            <Field label="Connections per download" hint="Used only when the server supports ranges. More connections only help when a server limits speed per connection.">
              <input type="number" min={1} max={32} className={inputClass} value={draft.connectionsPerDownload} onChange={e => setDraft({ ...draft, connectionsPerDownload: Number(e.target.value) })} />
            </Field>
          </div>
          <Row label="Notify when downloads finish"><Toggle label="Notify" checked={draft.notifyOnComplete} onChange={v => setDraft({ ...draft, notifyOnComplete: v })} /></Row>
        </Group>

        <Group title="Bandwidth">
          <div className="grid gap-4 sm:grid-cols-3">
            <Field label="Global download limit"><input className={inputClass} placeholder="Unlimited" value={limits.global} onChange={e => setLimits({ ...limits, global: e.target.value })} /></Field>
            <Field label="Torrent upload limit"><input className={inputClass} placeholder="Unlimited" value={limits.upload} onChange={e => setLimits({ ...limits, upload: e.target.value })} /></Field>
            <Field label="Stop seeding at ratio" hint="0 stops seeding as soon as the download finishes.">
              <input type="number" step="0.1" min={0} className={inputClass} value={draft.seedRatio} onChange={e => setDraft({ ...draft, seedRatio: Number(e.target.value) })} />
            </Field>
          </div>
        </Group>

        <Group title="Appearance">
          <Row label="Theme">
            <Segmented value={theme} onChange={t => { setTheme(t); applyTheme(t) }} options={[
              { value: 'system', label: <span className="flex items-center gap-1.5"><Monitor size={13} /> System</span> },
              { value: 'light', label: <span className="flex items-center gap-1.5"><Sun size={13} /> Light</span> },
              { value: 'dark', label: <span className="flex items-center gap-1.5"><Moon size={13} /> Dark</span> },
            ]} />
          </Row>
        </Group>

        <Group title="Use FluxGet in your browser">
          <BrowserSetup />
        </Group>

        <Group title="Connected browsers">
          <p className="text-[13px] text-muted">Each browser connects once with a code shown in FluxGet; web pages themselves can never connect.</p>
          {tokens.length === 0 ? <p className="text-[13px] text-faint">No browsers connected yet.</p> : tokens.map(t => (
            <div key={t.id} className="flex items-center gap-3 rounded-lg border border-line px-3 py-2">
              <Puzzle size={16} className="text-muted" />
              <span className="flex-1 text-[13px]">{t.name} <span className="text-muted">· connected {ago(t.createdAt)}</span></span>
              <IconButton label="Disconnect" onClick={() => attempt(async () => { await api.del(`/api/tokens/${t.id}`); setTokens(x => x.filter(y => y.id !== t.id)) }, 'Browser disconnected')}><Trash2 size={15} /></IconButton>
            </div>
          ))}
        </Group>

        <Group title="About">
          <dl className="grid grid-cols-[140px_1fr] gap-y-2 text-[13px]">
            <dt className="text-muted">Version</dt><dd>{system?.version}</dd>
            <dt className="text-muted">Mode</dt><dd>{system?.mode}</dd>
            <dt className="text-muted">yt-dlp</dt><dd className="break-all">{system?.tools?.ytDlp ?? <span className="text-warn">not found — media downloads unavailable</span>}</dd>
            <dt className="text-muted">FFmpeg</dt><dd className="break-all">{system?.tools?.ffmpeg ?? <span className="text-warn">not found — merging and conversion unavailable</span>}</dd>
            <dt className="text-muted">JS runtime</dt><dd className="break-all">{system?.tools?.jsRuntime ?? <span className="text-warn">not found — YouTube extraction may fail</span>}</dd>
          </dl>
        </Group>
      </div>
      {dirty && (
        <div className="sticky bottom-4 z-10 mx-auto flex w-[min(560px,calc(100%-32px))] items-center gap-3 rounded-xl border border-line bg-surface/95 px-4 py-3 shadow-pop backdrop-blur animate-rise">
          <span className="size-2 rounded-full bg-accent" />
          <span className="flex-1 text-[13px]">You have unsaved changes</span>
          <Button variant="ghost" size="sm" onClick={() => { setDraft(settings); if (settings) setLimits({ global: limitText(settings.globalSpeedLimit), upload: limitText(settings.torrentUploadLimit) }) }}>Discard</Button>
          <Button variant="primary" size="sm" onClick={save}>Save changes</Button>
        </div>
      )}
    </div>
  )
}

function Group({ title, children }: { title: string; children: ReactNode }) {
  return (
    <Card className="p-5">
      <h2 className="mb-4 text-[14px] font-semibold">{title}</h2>
      <div className="space-y-4">{children}</div>
    </Card>
  )
}

function Row({ label, children }: { label: string; children: ReactNode }) {
  return <div className="flex items-center justify-between gap-4 text-[13.5px]"><span>{label}</span>{children}</div>
}
