import { StrictMode, useEffect } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import { App } from './App'
import { Login } from './components/Gate'
import { boot, useStore } from './store'
import { PlayerOverlay } from './components/PlayerOverlay'

function Root() {
  const auth = useStore(s => s.authenticated)
  useEffect(() => {
    if (auth !== null) requestAnimationFrame(() => requestAnimationFrame(() => (window as any).chrome?.webview?.postMessage({ type: 'uiReady' })))
  }, [auth])
  if (auth === null) return null
  return auth ? <App /> : <Login />
}

const overlay = location.hash.startsWith('#/player-overlay')
if (!overlay) void boot()
createRoot(document.getElementById('root')!).render(<StrictMode>{overlay ? <PlayerOverlay /> : <Root />}</StrictMode>)
