import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import { App } from './App'
import { Login } from './components/Gate'
import { boot, useStore } from './store'

function Root() {
  const auth = useStore(s => s.authenticated)
  if (auth === null) return null
  return auth ? <App /> : <Login />
}

void boot()
createRoot(document.getElementById('root')!).render(<StrictMode><Root /></StrictMode>)
