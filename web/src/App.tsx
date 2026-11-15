import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react'
import { CalendarClock, CheckCheck, Clapperboard, Download, LayoutGrid, Magnet, MonitorPlay, Plus, Settings as Cog, WifiOff } from 'lucide-react'
import { onCompleted, setUpdate, useStore } from './store'
import { navigate, useRoute, type Page } from './router'
import { speed } from './format'
import { desktop } from './api'
import { Button, cx, Sparkline } from './ui'
import { Logo } from './components/Logo'
import { AddDialog, extractUrls } from './components/AddDialog'
import { PairingPrompt } from './components/Gate'
import { toast, Toaster } from './components/toast'
import { Overview } from './pages/Overview'
import { Downloads } from './pages/Downloads'
import { Media } from './pages/Media'
import { Torrents } from './pages/Torrents'
import { Scheduler } from './pages/Scheduler'
import { Completed } from './pages/Completed'
import { SettingsPage } from './pages/Settings'
import { PlayerPage } from './pages/Player'
import { player, setPlayerFrame, setPlayerState } from './player'

const NAV: { page: Page; label: string; icon: ReactNode }[] = [
  { page: 'overview', label: 'Overview', icon: <LayoutGrid size={17} /> },
  { page: 'downloads', label: 'Downloads', icon: <Download size={17} /> },
  { page: 'media', label: 'Media', icon: <Clapperboard size={17} /> },
  { page: 'player', label: 'Player', icon: <MonitorPlay size={17} /> },
  { page: 'torrents', label: 'Torrents', icon: <Magnet size={17} /> },
  { page: 'scheduler', label: 'Scheduler', icon: <CalendarClock size={17} /> },
  { page: 'completed', label: 'Completed', icon: <CheckCheck size={17} /> },
  { page: 'settings', label: 'Settings', icon: <Cog size={17} /> },
]

export interface AppActions { openAdd: (text?: string) => void }

export function App() {
  const { page } = useRoute()
  const [add, setAdd] = useState<{ open: boolean; text: string }>({ open: false, text: '' })
  const openAdd = useCallback((text = '') => setAdd({ open: true, text }), [])
  const connected = useStore(s => s.connected)
  const jobs = useStore(s => s.jobs)
  const history = useStore(s => s.history)
  const settings = useStore(s => s.settings)
  const update = useStore(s => s.update)

  const counts = useMemo(() => {
    const c = { downloads: 0, media: 0, torrents: 0, attention: 0 }
    jobs.forEach(j => {
      if (j.kind === 'Http') c.downloads++
      else if (j.kind === 'Media') c.media++
      else c.torrents++
      if (j.status === 'NeedsAttention' || j.status === 'Failed') c.attention++
    })
    return c
  }, [jobs])

  useEffect(() => {
    const key = (e: KeyboardEvent) => {
      const typing = (e.target as HTMLElement)?.closest('input,textarea,select,[contenteditable]')
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'n') { e.preventDefault(); openAdd() }
      if (!typing && e.key === '/' ) { e.preventDefault(); openAdd() }
    }
    const paste = (e: ClipboardEvent) => {
      if ((e.target as HTMLElement)?.closest('input,textarea,[contenteditable]')) return
      const text = e.clipboardData?.getData('text') ?? ''
      const urls = extractUrls(text)
      if (urls.length && location.hash.startsWith('#/player') && desktop.available()) { e.preventDefault(); player('openUrl', { url: urls[0] }); return }
      if (urls.length) { e.preventDefault(); openAdd(text) }
    }
    window.addEventListener('keydown', key)
    window.addEventListener('paste', paste)
    return () => { window.removeEventListener('keydown', key); window.removeEventListener('paste', paste) }
  }, [openAdd])

  useEffect(() => onCompleted(j => {
    toast(`Finished ${j.fileName ?? 'download'}`, 'ok')
  }), [])

  useEffect(() => {
    const wv = (window as any).chrome?.webview
    if (!wv) return
    desktop.send({ type: 'theme', dark: document.documentElement.classList.contains('dark') })
    const handler = (e: MessageEvent) => {
      if (e.data?.type === 'update') { setUpdate(e.data); return }
      if (e.data?.type === 'playerState') { setPlayerState(e.data.state); return }
      if (e.data?.type === 'playerFrame') { setPlayerFrame(e.data.url ?? null); return }
      if (e.data?.type === 'navigate' && typeof e.data.page === 'string') { navigate(e.data.page); return }
      if (typeof e.data?.url !== 'string') return
      if (e.data.type === 'add') openAdd(e.data.url)
      if (e.data.type === 'media') navigate('media', { url: e.data.url })
    }
    wv.addEventListener('message', handler)
    return () => wv.removeEventListener('message', handler)
  }, [openAdd])

  const down = history[history.length - 1]?.down ?? 0
  const up = history[history.length - 1]?.up ?? 0
  const title = NAV.find(n => n.page === page)!.label
  const count = (p: Page) => (p === 'downloads' ? counts.downloads : p === 'media' ? counts.media : p === 'torrents' ? counts.torrents : 0)

  return (
    <div className="flex h-full">
      <aside className="hidden w-[232px] shrink-0 flex-col border-r border-line bg-raised md:flex">
        <div className="px-5 pt-5 pb-6"><Logo /></div>
        <nav className="flex-1 space-y-0.5 px-3" aria-label="Main">
          {NAV.map(n => (
            <a key={n.page} href={`#/${n.page}`} aria-current={page === n.page ? 'page' : undefined}
              className={cx('flex h-9 items-center gap-3 rounded-lg px-3 text-[13.5px] font-medium transition-colors',
                page === n.page ? 'bg-surface text-ink shadow-card ring-1 ring-line' : 'text-muted hover:bg-sunken hover:text-ink')}>
              <span className={page === n.page ? 'text-accent' : ''}>{n.icon}</span>
              <span className="flex-1">{n.label}</span>
              {count(n.page) > 0 && <span className="num text-[11.5px] text-faint">{count(n.page)}</span>}
              {n.page === 'overview' && counts.attention > 0 && <span className="size-1.5 rounded-full bg-warn" aria-label={`${counts.attention} need attention`} />}
            </a>
          ))}
        </nav>
        <div className="m-3 rounded-xl border border-line bg-surface p-3.5">
          <div className="flex items-baseline justify-between">
            <span className="text-[11.5px] text-muted">Total speed</span>
            {settings && settings.globalSpeedLimit > 0 && <span className="text-[11px] text-warn">limited</span>}
          </div>
          <div className={cx('mt-0.5 text-[17px] font-semibold', down > 0 ? 'num' : 'text-faint')}>{down > 0 ? speed(down) : 'Idle'}</div>
          {up > 0 && <div className="num text-[11.5px] text-muted">↑ {speed(up)}</div>}
          <Sparkline values={history.map(h => h.down)} height={30} className="mt-2" />
        </div>
      </aside>

      <div className="flex min-w-0 flex-1 flex-col">
        <header className="flex h-14 shrink-0 items-center gap-3 border-b border-line bg-bg/80 px-4 backdrop-blur sm:px-6">
          <div className="md:hidden"><Logo /></div>
          <h1 className="hidden text-[15px] font-semibold tracking-tight md:block">{title}</h1>
          {!connected && <span className="inline-flex items-center gap-1.5 rounded-full bg-warn-soft px-2.5 py-1 text-[12px] text-warn" title="FluxGet's download service isn't responding. This page reconnects automatically."><WifiOff size={13} /> Not connected to FluxGet — retrying</span>}
          <div className="flex-1" />
          <Button variant="primary" onClick={() => openAdd()} title="Add (Ctrl+N)"><Plus size={16} /> <span className="hidden sm:inline">Add</span></Button>
        </header>
        {(update?.state === 'ready' || update?.state === 'applying') && (
          <div className="flex items-center gap-3 border-b border-line bg-accent-soft/60 px-4 py-2 text-[13px] sm:px-6 animate-fade">
            <span className="size-2 rounded-full bg-accent" />
            <span className="flex-1">{update.state === 'applying' ? `Installing FluxGet ${update.version}…` : <>FluxGet <b>{update.version}</b> is ready. Restart to finish updating — downloads resume automatically.</>}</span>
            {update.state === 'ready' && <Button size="sm" variant="primary" onClick={() => desktop.send({ type: 'applyUpdate' })}>Restart to update</Button>}
          </div>
        )}
        <main className={cx('min-h-0 flex-1 overflow-hidden pb-16 transition-opacity md:pb-0', !connected && 'pointer-events-none opacity-60')} aria-busy={!connected}>
          {page === 'overview' && <Overview openAdd={openAdd} />}
          {page === 'downloads' && <Downloads openAdd={openAdd} />}
          {page === 'media' && <Media />}
          {page === 'player' && <PlayerPage />}
          {page === 'torrents' && <Torrents />}
          {page === 'scheduler' && <Scheduler />}
          {page === 'completed' && <Completed />}
          {page === 'settings' && <SettingsPage />}
        </main>
      </div>

      <nav className="fixed inset-x-0 bottom-0 z-30 flex h-16 border-t border-line bg-surface/95 pb-[env(safe-area-inset-bottom)] backdrop-blur md:hidden" aria-label="Main">
        {NAV.filter(n => n.page !== 'scheduler' && n.page !== 'torrents').map(n => (
          <button key={n.page} onClick={() => navigate(n.page)} aria-current={page === n.page ? 'page' : undefined}
            className={cx('flex flex-1 flex-col items-center justify-center gap-1 text-[10.5px] font-medium', page === n.page ? 'text-accent' : 'text-muted')}>
            {n.icon}{n.label}
          </button>
        ))}
      </nav>

      <AddDialog open={add.open} initial={add.text} onClose={() => setAdd({ open: false, text: '' })} />
      <PairingPrompt />
      <Toaster />
    </div>
  )
}
