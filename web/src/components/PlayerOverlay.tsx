import { useEffect, useRef } from 'react'
import { desktop } from '../api'
import { setPlayerState, usePlayer } from '../player'
import { PlayerControls } from './PlayerControls'

export function PlayerOverlay() {
  const p = usePlayer()
  const root = useRef<HTMLDivElement>(null)

  useEffect(() => {
    document.documentElement.classList.add('dark')
    document.body.style.background = '#141518'
    const wv = (window as any).chrome?.webview
    const onMessage = (e: MessageEvent) => { if (e.data?.type === 'playerState') setPlayerState(e.data.state) }
    wv?.addEventListener('message', onMessage)
    const ro = new ResizeObserver(() => desktop.send({ type: 'overlaySize', height: root.current?.offsetHeight ?? 104 }))
    if (root.current) ro.observe(root.current)
    return () => { wv?.removeEventListener('message', onMessage); ro.disconnect() }
  }, [])

  return (
    <div ref={root} className="bg-[#141518] text-[#edece8]">
      <PlayerControls p={p} overlay />
    </div>
  )
}
