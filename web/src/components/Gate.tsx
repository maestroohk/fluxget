import { useState } from 'react'
import { KeyRound, Puzzle } from 'lucide-react'
import { api } from '../api'
import { boot, dismissPair, useStore } from '../store'
import { Button, inputClass, Modal } from '../ui'
import { Logo } from './Logo'

export function Login() {
  const [key, setKey] = useState('')
  const [error, setError] = useState('')
  const submit = async () => {
    setError('')
    try {
      await api.post('/api/session', { key })
      await boot()
    } catch {
      setError('That key was not accepted.')
    }
  }
  return (
    <div className="flex min-h-full items-center justify-center p-6">
      <form onSubmit={e => { e.preventDefault(); void submit() }} className="w-full max-w-sm animate-rise">
        <Logo className="mb-10" />
        <h1 className="text-[22px] font-semibold tracking-tight">Connect to FluxGet</h1>
        <p className="mt-1.5 text-[13.5px] text-muted">Enter the access key printed by FluxGet when it started. The desktop app signs you in automatically.</p>
        <div className="mt-6 flex gap-2">
          <div className="relative flex-1">
            <KeyRound size={15} className="absolute top-1/2 left-3 -translate-y-1/2 text-faint" />
            <input autoFocus type="password" className={inputClass + ' pl-9'} value={key} onChange={e => setKey(e.target.value)} placeholder="Access key" aria-label="Access key" />
          </div>
          <Button variant="primary" disabled={!key}>Connect</Button>
        </div>
        {error && <p className="mt-2 text-[12.5px] text-bad">{error}</p>}
      </form>
    </div>
  )
}

export function PairingPrompt() {
  const pair = useStore(s => s.pairs[0])
  if (!pair) return null
  const close = () => {
    dismissPair(pair.id)
    void api.del(`/api/pair/${pair.id}`).catch(() => {})
  }
  return (
    <Modal open onClose={close} title="Connect browser extension" footer={<Button onClick={close}>Deny</Button>}>
      <div className="flex gap-4">
        <div className="flex size-11 shrink-0 items-center justify-center rounded-xl bg-accent-soft text-accent"><Puzzle size={20} /></div>
        <div>
          <p className="text-[13.5px]"><b>{pair.name}</b> wants to send downloads to FluxGet. If you started this, enter this code in the extension:</p>
          <div className="num mt-4 text-[34px] font-semibold tracking-[0.25em]">{pair.code.slice(0, 3)} {pair.code.slice(3)}</div>
          <p className="mt-2 text-[12px] text-muted">The code expires in two minutes. If you didn't start this, choose Deny.</p>
        </div>
      </div>
    </Modal>
  )
}
