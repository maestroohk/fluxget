(() => {
  if (window.__fluxget) return
  window.__fluxget = true

  const MEDIA_SITE = /(^|\.)(youtube\.com|youtu\.be|facebook\.com|instagram\.com|tiktok\.com|x\.com|twitter\.com|reddit\.com|vimeo\.com|twitch\.tv|dailymotion\.com|soundcloud\.com|bilibili\.com)$/i.test(location.hostname)
  let enabled = true
  chrome.storage.local.get(['siteButton', 'videoButton']).then(v => { enabled = MEDIA_SITE ? v.siteButton !== false : v.videoButton === true })
  chrome.storage.onChanged.addListener(c => {
    if (c.siteButton && MEDIA_SITE) enabled = c.siteButton.newValue !== false
    if (c.videoButton && !MEDIA_SITE) enabled = c.videoButton.newValue === true
  })
  const send = msg => chrome.runtime.sendMessage(msg).catch(e => ({ ok: false, error: String(e?.message || e) }))

  const h = (tag, props = {}, ...children) => {
    const el = document.createElement(tag)
    for (const [k, v] of Object.entries(props)) {
      if (k === 'class') el.className = v
      else if (k.startsWith('on')) el.addEventListener(k.slice(2), v)
      else if (v != null) el.setAttribute(k, v)
    }
    for (const c of children.flat()) if (c != null) el.append(c.nodeType ? c : document.createTextNode(String(c)))
    return el
  }
  const svg = path => {
    const s = document.createElementNS('http://www.w3.org/2000/svg', 'svg')
    s.setAttribute('viewBox', '0 0 24 24')
    s.setAttribute('aria-hidden', 'true')
    const p = document.createElementNS('http://www.w3.org/2000/svg', 'path')
    p.setAttribute('d', path)
    s.append(p)
    return s
  }
  const ICON_DOWN = 'M12 4v11m0 0-4.5-4.5M12 15l4.5-4.5M5 19.5h14'
  const ICON_CHECK = 'M5 12.5l4.5 4.5L19 7.5'
  const fmt = n => {
    if (!n) return ''
    const u = ['B', 'KB', 'MB', 'GB']
    let i = 0
    while (n >= 1024 && i < u.length - 1) { n /= 1024; i++ }
    return `${n.toFixed(n >= 100 || i === 0 ? 0 : 1)} ${u[i]}`
  }

  const host = h('fluxget-root')
  host.style.cssText = 'position:fixed;top:0;left:0;z-index:2147483646;display:block;width:0;height:0'
  const shadow = host.attachShadow({ mode: 'closed' })
  shadow.append(h('style', {}, `
    :host { all: initial; }
    * { box-sizing: border-box; font-family: "Segoe UI Variable Text", "Segoe UI", system-ui, -apple-system, sans-serif; }
    .btn { position: fixed; display: none; align-items: center; gap: 6px; height: 32px; padding: 0 12px 0 10px; border-radius: 10px;
      border: 1px solid rgba(255,255,255,.16); background: rgba(20,21,24,.92); color: #f2f1ee; font-size: 13px; font-weight: 600;
      cursor: pointer; box-shadow: 0 6px 20px rgba(0,0,0,.35); backdrop-filter: blur(8px); transition: background .15s, transform .15s; }
    .btn:hover { background: rgba(32,33,38,.96); transform: translateY(-1px); }
    .btn svg, .row svg, .done svg { width: 16px; height: 16px; fill: none; stroke: #ff6e43; stroke-width: 2.2; stroke-linecap: round; stroke-linejoin: round; }
    .btn.show { display: inline-flex; animation: in .16s ease-out; }
    .panel { position: fixed; display: none; width: 300px; max-height: 70vh; overflow: auto; border-radius: 14px; background: #141518;
      border: 1px solid #2a2c31; color: #edece8; box-shadow: 0 18px 60px rgba(0,0,0,.55); font-size: 13px; animation: in .16s ease-out; }
    .panel.show { display: block; }
    @keyframes in { from { opacity: 0; transform: translateY(-4px) scale(.98); } to { opacity: 1; transform: none; } }
    .head { display: flex; align-items: center; gap: 8px; padding: 12px 14px 10px; border-bottom: 1px solid #23252a; }
    .mark { width: 18px; height: 18px; border-radius: 5px; background: #17181c; border: 1px solid #33363c; display: grid; place-items: center; }
    .mark i { width: 9px; height: 2px; background: #ff6e43; border-radius: 2px; display: block; box-shadow: 0 -4px 0 #e9e8e4, 0 4px 0 #e9e8e4; }
    .brand { font-weight: 650; letter-spacing: -.01em; flex: 1; }
    .x { background: none; border: 0; color: #8d9098; cursor: pointer; font-size: 18px; line-height: 1; padding: 2px 4px; border-radius: 6px; }
    .x:hover { color: #edece8; background: #22242a; }
    .title { padding: 10px 14px 4px; color: #c4c4c0; font-size: 12.5px; line-height: 1.35; display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; overflow: hidden; }
    .label { padding: 10px 14px 4px; font-size: 10.5px; letter-spacing: .08em; text-transform: uppercase; color: #8d9098; font-weight: 600; }
    .row { display: flex; align-items: center; gap: 10px; width: 100%; padding: 8px 14px; background: none; border: 0; color: inherit; text-align: left; cursor: pointer; font-size: 13px; }
    .row:hover, .row:focus-visible { background: #1d1f24; outline: none; }
    .row b { font-weight: 600; min-width: 64px; }
    .row span { color: #8d9098; flex: 1; }
    .row em { font-style: normal; color: #8d9098; font-variant-numeric: tabular-nums; font-size: 12px; }
    .msg { padding: 14px; color: #c4c4c0; line-height: 1.45; }
    .msg.err { color: #f2a19c; }
    .spin { width: 14px; height: 14px; border: 2px solid #33363c; border-top-color: #ff6e43; border-radius: 50%; display: inline-block; vertical-align: -2px; margin-right: 8px; animation: sp .8s linear infinite; }
    @keyframes sp { to { transform: rotate(360deg); } }
    .prog { padding: 12px 14px 14px; }
    .bar { height: 4px; background: #26282d; border-radius: 3px; overflow: hidden; margin: 10px 0 8px; }
    .bar i { display: block; height: 100%; background: #ff6e43; border-radius: 3px; transition: width .5s; }
    .meta { display: flex; justify-content: space-between; color: #8d9098; font-size: 12px; font-variant-numeric: tabular-nums; }
    .done { display: flex; align-items: center; gap: 8px; color: #3fc486; font-weight: 600; }
    .done svg { stroke: #3fc486; }
    .foot { display: flex; justify-content: space-between; gap: 8px; padding: 10px 14px; border-top: 1px solid #23252a; }
    .link { background: none; border: 0; color: #ff8a66; cursor: pointer; font-size: 12.5px; padding: 0; font-weight: 500; }
    .link:hover { text-decoration: underline; }
    .primary { height: 30px; padding: 0 12px; border-radius: 8px; border: 0; background: #edece8; color: #141518; font-weight: 600; cursor: pointer; font-size: 12.5px; }
  `))
  const button = h('button', { class: 'btn', type: 'button', 'aria-label': 'Download with FluxGet' }, svg(ICON_DOWN), 'Download')
  const panel = h('div', { class: 'panel', role: 'dialog', 'aria-label': 'FluxGet download options' })
  shadow.append(button, panel)
  document.documentElement.append(host)

  let target = null
  let hideTimer = 0
  let open = false
  let pollTimer = 0
  const cache = new Map()

  const pageUrl = () => {
    const u = new URL(location.href)
    if (/youtube\.com$/.test(u.hostname) && u.pathname === '/watch' && u.searchParams.get('v')) return `https://www.youtube.com/watch?v=${u.searchParams.get('v')}`
    return u.toString()
  }
  const anchorRect = () => {
    const player = document.querySelector('#movie_player') || document.querySelector('ytd-reel-video-renderer[is-active] #shorts-player')
    const el = (MEDIA_SITE && player && player.contains(target)) ? player : target
    return el?.getBoundingClientRect()
  }
  const place = () => {
    const r = anchorRect()
    if (!r || r.width < 240 || r.height < 135 || r.bottom < 40 || r.top > innerHeight - 40) { if (!open) hideButton(); return }
    button.style.top = `${Math.max(8, r.top + 12)}px`
    button.style.left = `${Math.min(innerWidth - 120, r.right - 118)}px`
    button.classList.add('show')
    if (open) {
      panel.style.top = `${Math.max(8, r.top + 52)}px`
      panel.style.left = `${Math.max(8, Math.min(innerWidth - 310, r.right - 302))}px`
    }
  }
  const hideButton = () => { if (!open) button.classList.remove('show') }

  const videoAt = (x, y) => {
    for (const v of document.querySelectorAll('video')) {
      const r = v.getBoundingClientRect()
      if (x >= r.left && x <= r.right && y >= r.top && y <= r.bottom && r.width > 0) return v
    }
    return null
  }

  document.addEventListener('mousemove', e => {
    if (!enabled) return
    if (e.composedPath().includes(host)) { clearTimeout(hideTimer); return }
    const v = videoAt(e.clientX, e.clientY)
    if (v && !v.mediaKeys) {
      clearTimeout(hideTimer)
      target = v
      place()
    } else if (!open) {
      clearTimeout(hideTimer)
      hideTimer = setTimeout(hideButton, 1200)
    }
  }, { passive: true })
  addEventListener('scroll', () => target && place(), { passive: true })
  addEventListener('resize', () => target && place(), { passive: true })
  document.addEventListener('keydown', e => { if (e.key === 'Escape' && open) close() })
  document.addEventListener('mousedown', e => { if (open && !e.composedPath().includes(host)) close() }, true)
  document.addEventListener('yt-navigate-finish', () => close())

  button.addEventListener('click', e => {
    e.stopPropagation()
    open ? close() : show()
  })

  function close() {
    open = false
    clearInterval(pollTimer)
    panel.classList.remove('show')
  }

  function frame(...body) {
    panel.replaceChildren(
      h('div', { class: 'head' }, h('div', { class: 'mark' }, h('i')), h('div', { class: 'brand' }, 'FluxGet'), h('button', { class: 'x', 'aria-label': 'Close', onclick: close }, '×')),
      ...body.flat())
  }

  async function show() {
    open = true
    panel.classList.add('show')
    place()
    frame(h('div', { class: 'msg' }, h('span', { class: 'spin' }), 'Connecting to FluxGet…'))
    const status = await send({ type: 'fg-status' })
    if (status?.state === 'offline') return frame(h('div', { class: 'msg err' }, "FluxGet isn't running on this computer."), h('div', { class: 'foot' }, h('span'), h('button', { class: 'primary', onclick: () => send({ type: 'fg-launch' }) }, 'Open FluxGet')))
    if (status?.state !== 'ready') return frame(h('div', { class: 'msg' }, 'Connect this browser to FluxGet first: click the FluxGet icon in the toolbar and choose Connect.'))

    const src = target?.currentSrc || target?.src || ''
    if (!MEDIA_SITE) {
      if (!/^https?:/i.test(src)) return frame(h('div', { class: 'msg' }, 'This video is streamed in a way FluxGet cannot download directly.'))
      return frame(h('div', { class: 'label' }, 'Video file'), row(ICON_DOWN, 'Download', src.split('/').pop().split('?')[0].slice(0, 40), '', () => startDirect(src)))
    }

    const url = pageUrl()
    frame(h('div', { class: 'msg' }, h('span', { class: 'spin' }), 'Finding available qualities…'))
    let info = cache.get(url)
    if (!info) {
      const r = await send({ type: 'fg-analyze', url })
      if (!r?.ok) return frame(h('div', { class: 'msg err' }, r?.error || 'This video could not be read.'))
      info = r.info
      cache.set(url, info)
    }
    if (!open) return
    if (info.type === 'playlist') {
      return frame(h('div', { class: 'title' }, info.title), h('div', { class: 'msg' }, `${info.entryCount ?? info.entries.length} videos. Choose which ones in FluxGet.`),
        h('div', { class: 'foot' }, h('span'), h('button', { class: 'primary', onclick: () => send({ type: 'fg-open-media', url }) }, 'Open in FluxGet')))
    }
    const videos = info.video.filter(v => v.height >= 144).slice(0, 6)
    frame(
      h('div', { class: 'title', title: info.title }, info.title),
      h('div', { class: 'label' }, 'Video'),
      videos.length
        ? videos.map(v => row(ICON_DOWN, quality(v.height), `MP4${v.fps > 30 ? ` · ${Math.round(v.fps)} fps` : ''}`, v.size ? `${v.estimated ? '≈' : ''}${fmt(v.size)}` : '', () => startMedia(info, { mode: 'video', height: v.height, container: 'mp4' }, `${quality(v.height)} MP4`)))
        : row(ICON_DOWN, 'Best', 'MP4', '', () => startMedia(info, { mode: 'video', height: null, container: 'mp4' }, 'Best quality')),
      h('div', { class: 'label' }, 'Audio only'),
      row(ICON_DOWN, 'MP3', '192 kbps', '', () => startMedia(info, { mode: 'audio', audioFormat: 'mp3', audioQuality: '192' }, 'MP3 192 kbps')),
      row(ICON_DOWN, 'MP3', '320 kbps', '', () => startMedia(info, { mode: 'audio', audioFormat: 'mp3', audioQuality: '320' }, 'MP3 320 kbps')),
      row(ICON_DOWN, 'M4A', 'original quality', '', () => startMedia(info, { mode: 'audio', audioFormat: 'm4a', audioQuality: null }, 'M4A')),
      h('div', { class: 'foot' }, h('button', { class: 'link', onclick: () => send({ type: 'fg-open-media', url }) }, 'More formats in FluxGet'), h('span')))
    panel.querySelector('.row')?.focus()
  }

  function row(icon, main, sub, right, onclick) {
    return h('button', { class: 'row', type: 'button', onclick }, svg(icon), h('b', {}, main), h('span', {}, sub), h('em', {}, right))
  }

  const quality = hgt => (hgt >= 2160 ? `${hgt >= 4320 ? '8K' : '4K'}` : hgt >= 1440 ? '1440p' : `${hgt}p`)

  async function startMedia(info, opts, label) {
    frame(h('div', { class: 'msg' }, h('span', { class: 'spin' }), `Starting ${label}…`))
    const r = await send({ type: 'fg-media-download', item: { url: info.url, title: info.title, id: info.id }, opts })
    if (!r?.ok) return frame(h('div', { class: 'msg err' }, r?.error || 'FluxGet could not start this download.'))
    track(r.jobId, info.title, label)
  }

  async function startDirect(src) {
    frame(h('div', { class: 'msg' }, h('span', { class: 'spin' }), 'Sending to FluxGet…'))
    const r = await send({ type: 'video-button', src })
    if (!r?.ok) return frame(h('div', { class: 'msg err' }, r?.error || 'FluxGet could not start this download.'))
    frame(h('div', { class: 'prog' }, h('div', { class: 'done' }, svg(ICON_CHECK), 'Sent to FluxGet')))
  }

  function track(jobId, title, label) {
    const name = h('div', { class: 'title', style: 'padding:0' }, title)
    const fill = h('i', { style: 'width:0%' })
    const left = h('span', {}, 'Starting…')
    const right = h('span', {}, label)
    const body = h('div', { class: 'prog' }, name, h('div', { class: 'bar' }, fill), h('div', { class: 'meta' }, left, right))
    frame(body, h('div', { class: 'foot' }, h('button', { class: 'link', onclick: () => send({ type: 'fg-open-app' }) }, 'Open FluxGet'), h('span')))
    clearInterval(pollTimer)
    const tick = async () => {
      const r = await send({ type: 'fg-job', id: jobId })
      if (!r?.ok) return
      const j = r.job
      const pct = j.totalBytes ? Math.min(100, (j.receivedBytes / j.totalBytes) * 100) : null
      if (j.status === 'Completed') {
        clearInterval(pollTimer)
        body.replaceChildren(name, h('div', { class: 'done', style: 'margin-top:10px' }, svg(ICON_CHECK), 'Download complete'))
        body.parentElement?.querySelector('.foot')?.replaceChildren(
          h('button', { class: 'link', onclick: () => send({ type: 'fg-reveal', id: jobId }) }, 'Show in folder'),
          h('button', { class: 'primary', onclick: () => send({ type: 'fg-open-file', id: jobId }) }, 'Open'))
        return
      }
      if (j.status === 'Failed' || j.status === 'NeedsAttention') {
        clearInterval(pollTimer)
        body.append(h('div', { class: 'msg err', style: 'padding:8px 0 0' }, j.error || 'The download stopped.'))
        return
      }
      fill.style.width = `${pct ?? 3}%`
      left.textContent = j.phase || (j.totalBytes ? `${fmt(j.receivedBytes)} of ${fmt(j.totalBytes)}` : j.status === 'Queued' ? 'Waiting in queue' : 'Starting…')
      right.textContent = j.speed > 0 ? `${fmt(j.speed)}/s` : label
    }
    void tick()
    pollTimer = setInterval(tick, 1000)
  }
})()
