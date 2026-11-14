(() => {
  if (window.__fluxget) return
  window.__fluxget = true

  const button = document.createElement('button')
  button.type = 'button'
  button.textContent = 'Download'
  button.setAttribute('aria-label', 'Download this video with FluxGet')
  Object.assign(button.style, {
    position: 'fixed', zIndex: '2147483646', display: 'none', padding: '6px 10px 6px 26px', borderRadius: '8px',
    border: '1px solid rgba(255,255,255,0.18)', background: 'rgba(23,24,28,0.86)', color: '#F5F4F0',
    font: '600 12px/1 system-ui, "Segoe UI", sans-serif', cursor: 'pointer', boxShadow: '0 4px 16px rgba(0,0,0,0.3)',
    backgroundImage: 'linear-gradient(#FF6E43,#FF6E43)', backgroundSize: '10px 2px', backgroundRepeat: 'no-repeat', backgroundPosition: '10px 50%',
  })
  document.documentElement.appendChild(button)

  let current = null
  let hideTimer = 0

  const place = video => {
    const r = video.getBoundingClientRect()
    if (r.width < 240 || r.height < 135 || r.bottom < 0 || r.top > innerHeight) return hide()
    if (video.mediaKeys) return hide()
    current = video
    button.style.top = `${Math.max(8, r.top + 10)}px`
    button.style.left = `${Math.min(innerWidth - 110, r.right - 104)}px`
    button.style.display = 'block'
  }
  const hide = () => { button.style.display = 'none'; current = null }

  document.addEventListener('mouseover', e => {
    const v = e.target instanceof HTMLVideoElement ? e.target : e.target?.closest?.('video') || findVideoUnder(e)
    if (v) { clearTimeout(hideTimer); place(v) }
    else if (e.target !== button) { clearTimeout(hideTimer); hideTimer = setTimeout(hide, 900) }
  }, { passive: true })
  addEventListener('scroll', () => current && place(current), { passive: true })

  function findVideoUnder(e) {
    for (const v of document.querySelectorAll('video')) {
      const r = v.getBoundingClientRect()
      if (e.clientX >= r.left && e.clientX <= r.right && e.clientY >= r.top && e.clientY <= r.bottom) return v
    }
    return null
  }

  button.addEventListener('click', async e => {
    e.preventDefault()
    e.stopPropagation()
    const src = current?.currentSrc || current?.src || ''
    button.textContent = 'Sending…'
    const res = await chrome.runtime.sendMessage({ type: 'video-button', src: src.startsWith('blob:') ? '' : src })
    button.textContent = res?.ok ? 'Sent ✓' : 'Not available'
    if (!res?.ok && res?.error) button.title = res.error
    setTimeout(() => { button.textContent = 'Download'; button.title = '' }, 2500)
  })
})()
