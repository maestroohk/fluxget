import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Bookmark, Check, Copy, FolderOpen, Globe, Puzzle } from 'lucide-react'
import { desktop } from '../api'
import { Button, cx, Segmented } from '../ui'
import { toast } from './toast'

type Browser = 'chromium' | 'firefox' | 'any'

const BOOKMARKLET = "javascript:(()=>{location.href='fluxget://send?url='+encodeURIComponent(location.href)})()"

export function BrowserSetup() {
  const [browser, setBrowser] = useState<Browser>('chromium')
  const [folder, setFolder] = useState<string | null>(null)

  useEffect(() => {
    const wv = (window as any).chrome?.webview
    if (!wv) return
    const handler = (e: MessageEvent) => { if (e.data?.type === 'extensionInfo') setFolder(e.data.path ?? null) }
    wv.addEventListener('message', handler)
    desktop.send({ type: 'extensionInfo' })
    return () => wv.removeEventListener('message', handler)
  }, [])

  return (
    <div className="space-y-4">
      <Segmented value={browser} onChange={setBrowser} options={[
        { value: 'chromium', label: <span className="flex items-center gap-1.5"><Puzzle size={13} /> Chrome · Edge · Brave · Opera</span> },
        { value: 'firefox', label: 'Firefox' },
        { value: 'any', label: <span className="flex items-center gap-1.5"><Globe size={13} /> Any browser</span> },
      ]} />

      {browser === 'chromium' && (
        <ol className="space-y-3">
          <Step n={1} title="Open your browser's extensions page">
            <div className="flex flex-wrap gap-2">
              <CopyChip text="chrome://extensions" />
              <CopyChip text="edge://extensions" />
              <CopyChip text="brave://extensions" />
              <CopyChip text="opera://extensions" />
            </div>
            <p className="mt-1.5 text-[12px] text-muted">Paste the address into the address bar. Browsers don't let other apps open these pages directly.</p>
          </Step>
          <Step n={2} title="Turn on Developer mode">
            <p className="text-[12.5px] text-muted">The switch is in the top-right corner (Chrome) or the left sidebar (Edge).</p>
          </Step>
          <Step n={3} title='Choose "Load unpacked" and select the FluxGet extension folder'>
            {folder ? (
              <div className="flex flex-wrap items-center gap-2">
                <code className="min-w-0 flex-1 truncate rounded-md bg-sunken px-2 py-1.5 font-mono text-[12px]" title={folder}>{folder}</code>
                <Button size="sm" onClick={() => desktop.send({ type: 'openExtensionFolder' })}><FolderOpen size={14} /> Open folder</Button>
                <CopyButton text={folder} />
              </div>
            ) : <p className="text-[12.5px] text-muted">The <code className="font-mono">extension</code> folder ships next to FluxGet (and in the source repository).</p>}
          </Step>
          <Step n={4} title="Click the FluxGet icon in the toolbar and choose Connect">
            <p className="text-[12.5px] text-muted">FluxGet shows a 6-digit code here; type it into the extension. On YouTube and other video sites a Download button then appears on the player.</p>
          </Step>
        </ol>
      )}

      {browser === 'firefox' && (
        <div className="space-y-3 text-[13px]">
          <p>The same extension works in Firefox 128 or newer. Firefox only keeps add-ons permanently once Mozilla has signed them (free, through addons.mozilla.org). Until FluxGet is published there, you can load it for the current session:</p>
          <ol className="space-y-3">
            <Step n={1} title="Open the debugging page"><CopyChip text="about:debugging#/runtime/this-firefox" /></Step>
            <Step n={2} title='Choose "Load Temporary Add-on…" and pick manifest.json in the FluxGet extension folder'>
              {folder && <div className="flex items-center gap-2"><code className="min-w-0 flex-1 truncate rounded-md bg-sunken px-2 py-1.5 font-mono text-[12px]">{folder}</code><Button size="sm" onClick={() => desktop.send({ type: 'openExtensionFolder' })}><FolderOpen size={14} /> Open folder</Button></div>}
            </Step>
          </ol>
          <p className="text-[12px] text-muted">Firefox removes temporary add-ons when it restarts. The "Any browser" button works permanently in Firefox today.</p>
        </div>
      )}

      {browser === 'any' && (
        <div className="space-y-3 text-[13px]">
          <p>No extension needed. Drag this button to your bookmarks bar, then click it on any page — a YouTube video, a download page — to send it to FluxGet.</p>
          <div className="flex flex-wrap items-center gap-3">
            <BookmarkletLink />
            <span className="text-[12px] text-muted">← drag to the bookmarks bar (Ctrl+Shift+B shows it)</span>
          </div>
          <p className="text-[12px] text-muted">The first time, the browser asks whether to open FluxGet; tick "Always allow". Video pages open FluxGet's quality picker; other pages are added as downloads. Works in Chrome, Edge, Firefox, Brave, Opera and Vivaldi.</p>
        </div>
      )}
    </div>
  )
}

function Step({ n, title, children }: { n: number; title: string; children?: ReactNode }) {
  return (
    <li className="flex gap-3">
      <span className="num mt-px flex size-6 shrink-0 items-center justify-center rounded-full bg-sunken text-[12px] font-semibold text-ink-2">{n}</span>
      <div className="min-w-0 flex-1">
        <div className="mb-1.5 text-[13px] font-medium">{title}</div>
        {children}
      </div>
    </li>
  )
}

function CopyChip({ text }: { text: string }) {
  const [done, setDone] = useState(false)
  return (
    <button type="button" onClick={() => navigator.clipboard.writeText(text).then(() => { setDone(true); setTimeout(() => setDone(false), 1500) })}
      className={cx('inline-flex h-7 items-center gap-1.5 rounded-md border px-2 font-mono text-[12px] transition', done ? 'border-ok/40 text-ok' : 'border-line text-ink-2 hover:border-line-strong')}>
      {done ? <Check size={12} /> : <Copy size={12} />} {text}
    </button>
  )
}

function CopyButton({ text }: { text: string }) {
  return <Button size="sm" variant="ghost" onClick={() => navigator.clipboard.writeText(text).then(() => toast('Path copied', 'ok'))}><Copy size={14} /> Copy</Button>
}

function BookmarkletLink() {
  const ref = useRef<HTMLAnchorElement>(null)
  useEffect(() => { ref.current?.setAttribute('href', BOOKMARKLET) }, [])
  return (
    <a ref={ref} draggable onClick={e => { e.preventDefault(); toast('Drag the button to your bookmarks bar instead of clicking it', 'info') }}
      className="inline-flex h-9 cursor-grab items-center gap-2 rounded-lg bg-ink px-3.5 text-[13px] font-semibold text-bg shadow-card active:cursor-grabbing">
      <Bookmark size={15} /> Send to FluxGet
    </a>
  )
}
