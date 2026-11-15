(() => {
  if (window.__fluxget) return
  window.__fluxget = true

  const MEDIA_SITE = /(^|\.)(youtube\.com|youtu\.be|facebook\.com|instagram\.com|tiktok\.com|x\.com|twitter\.com|reddit\.com|vimeo\.com|twitch\.tv|dailymotion\.com|soundcloud\.com|bilibili\.com)$/i.test(location.hostname)
  let enabled = false
  const prefs = { siteButton: true, detect: true }
  const recompute = () => { enabled = prefs.detect && (MEDIA_SITE ? prefs.siteButton : true) }
  const send = msg => chrome.runtime.sendMessage(msg).catch(e => ({ ok: false, error: String(e?.message || e) }))
  chrome.storage.local.get('siteButton').then(v => { prefs.siteButton = v.siteButton !== false; recompute() })
  chrome.storage.onChanged.addListener(c => {
    if (c.siteButton) prefs.siteButton = c.siteButton.newValue !== false
    recompute()
  })
  send({ type: 'fg-config' }).then(c => { if (c?.ok) { prefs.detect = c.detectMedia !== false; recompute() } })

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
  const ICON_PLAY = 'M8 5.5v13l10.5-6.5z'
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
    .btn { position: fixed; top: 0; left: auto; right: 0; display: flex; align-items: center; height: 28px; padding: 0 7px; border-radius: 8px;
      border: 1px solid rgba(255,255,255,.14); background: rgba(17,18,21,.82); color: #f2f1ee; font-size: 12px; font-weight: 600;
      cursor: pointer; box-shadow: 0 2px 10px rgba(0,0,0,.3); opacity: 0; visibility: hidden; pointer-events: none;
      transition: opacity .18s ease, visibility .18s, background .15s; will-change: transform; contain: layout paint; }
    .btn.show { opacity: 1; visibility: visible; pointer-events: auto; }
    .btn:hover, .btn.open { background: rgba(17,18,21,.96); }
    .btn .lbl { max-width: 0; overflow: hidden; white-space: nowrap; opacity: 0; transition: max-width .2s ease, opacity .15s, margin .2s; }
    .btn:hover .lbl, .btn.open .lbl { max-width: 80px; opacity: 1; margin-left: 5px; }
    .btn.busy .lbl, .btn.finished .lbl { max-width: 80px; opacity: 1; margin-left: 5px; font-variant-numeric: tabular-nums; }
    .btn.busy::before { content: ''; position: absolute; inset: -2px; border-radius: 10px; padding: 2px; pointer-events: none;
      background: conic-gradient(#ff6e43 calc(var(--p, 0) * 1%), rgba(255,255,255,.16) 0);
      -webkit-mask: linear-gradient(#000 0 0) content-box, linear-gradient(#000 0 0); -webkit-mask-composite: xor; mask-composite: exclude; }
    .btn.finished { border-color: rgba(63,196,134,.6); }
    .btn.finished svg { stroke: #3fc486; }
    .dls:empty { display: none; }
    .dls { border-bottom: 1px solid #23252a; padding-bottom: 6px; }
    .dl { padding: 6px 14px 8px; }
    .dl-name { font-size: 12.5px; color: #e4e3df; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .dl .bar { margin: 7px 0 5px; }
    .dl-done { display: flex; align-items: center; justify-content: space-between; gap: 8px; margin-top: 6px; font-size: 12.5px; }
    .acts { display: flex; gap: 12px; }
    .more { display: block; width: 100%; padding: 9px 14px; background: none; border: 0; text-align: left; color: #ff8a66; font-size: 12.5px; font-weight: 500; cursor: pointer; }
    .more:hover { background: #1d1f24; }
    .opts[hidden] { display: none; }
    .hint { padding: 2px 14px 6px; color: #6f727a; font-size: 11.5px; }
    .btn svg, .row svg, .done svg { width: 15px; height: 15px; fill: none; stroke: #ff6e43; stroke-width: 2.4; stroke-linecap: round; stroke-linejoin: round; flex: none; }
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
  const label = h('span', { class: 'lbl' }, 'Download')
  const button = h('button', { class: 'btn', type: 'button', 'aria-label': 'Download with FluxGet', title: 'Download with FluxGet' }, svg(ICON_DOWN), label)
  const panel = h('div', { class: 'panel', role: 'dialog', 'aria-label': 'FluxGet download options' })
  shadow.append(button, panel)
  document.documentElement.append(host)

  let target = null
  let hideTimer = 0
  let open = false
  const cache = new Map()

  const pageUrl = () => {
    const u = new URL(location.href)
    if (/youtube\.com$/.test(u.hostname) && u.pathname === '/watch' && u.searchParams.get('v')) return `https://www.youtube.com/watch?v=${u.searchParams.get('v')}`
    return u.toString()
  }
  const anchorRect = () => {
    if (target instanceof HTMLAudioElement) {
      const right = innerWidth - 16
      return { top: 12, bottom: 212, left: right - 300, right, width: 300, height: 200 }
    }
    const player = document.querySelector('#movie_player') || document.querySelector('ytd-reel-video-renderer[is-active] #shorts-player')
    const el = (MEDIA_SITE && player && player.contains(target)) ? player : target
    return el?.getBoundingClientRect()
  }
  let lastPos = ''
  let raf = 0
  const place = () => {
    const r = anchorRect()
    if (!r || r.width < 240 || r.height < 135 || r.bottom < 40 || r.top > innerHeight - 40) { if (!open) hideButton(); return }
    const top = Math.round(Math.max(8, r.top + 10))
    const right = Math.round(Math.min(innerWidth - 8, r.right - 10))
    const pos = `${top}:${right}`
    if (pos !== lastPos) {
      lastPos = pos
      button.style.right = `${document.documentElement.clientWidth - right}px`
      button.style.transform = `translateY(${top}px)`
      if (open) {
        panel.style.top = `${top + 36}px`
        panel.style.left = `${Math.max(8, Math.min(innerWidth - 310, right - 300))}px`
      }
    }
    if (!button.classList.contains('show')) button.classList.add('show')
  }
  const schedule = () => { if (!raf) raf = requestAnimationFrame(() => { raf = 0; place() }) }
  const hideButton = () => { if (!open && !button.classList.contains('busy')) { button.classList.remove('show'); lastPos = '' } }
  const reveal = (v, ms) => {
    target = v
    schedule()
    clearTimeout(hideTimer)
    if (ms) hideTimer = setTimeout(hideButton, ms)
  }

  const videoAt = (x, y) => {
    for (const v of document.querySelectorAll('video')) {
      const r = v.getBoundingClientRect()
      if (x >= r.left && x <= r.right && y >= r.top && y <= r.bottom && r.width > 0) return v
    }
    return null
  }

  let lastMove = 0
  document.addEventListener('mousemove', e => {
    if (!enabled) return
    const now = performance.now()
    if (now - lastMove < 80) return
    lastMove = now
    if (e.composedPath().includes(host)) { clearTimeout(hideTimer); return }
    const v = videoAt(e.clientX, e.clientY)
    if (v && !v.mediaKeys) reveal(v, open ? 0 : 2500)
    else if (!open) { clearTimeout(hideTimer); hideTimer = setTimeout(hideButton, 600) }
  }, { passive: true })
  document.addEventListener('play', e => {
    if (!enabled || open) return
    if (e.target instanceof HTMLVideoElement && !e.target.mediaKeys) reveal(e.target, 4000)
    else if (e.target instanceof HTMLAudioElement && !e.target.mediaKeys) reveal(e.target, 6000)
  }, true)
  addEventListener('scroll', () => target && button.classList.contains('show') && schedule(), { passive: true })
  addEventListener('resize', () => target && button.classList.contains('show') && schedule(), { passive: true })
  document.addEventListener('keydown', e => { if (e.key === 'Escape' && open) close() })
  document.addEventListener('mousedown', e => { if (open && !e.composedPath().includes(host)) close() }, true)
  document.addEventListener('yt-navigate-finish', () => close())

  button.addEventListener('click', e => {
    e.stopPropagation()
    open ? close() : show()
  })

  const active = new Map()
  const dlBox = h('div', { class: 'dls' })
  const busy = () => [...active.values()].some(a => a.state === 'run')

  function close() {
    open = false
    button.classList.remove('open')
    panel.classList.remove('show')
    for (const [id, a] of active) if (a.state !== 'run') active.delete(id)
    updateRing()
    if (!busy()) { clearTimeout(hideTimer); hideTimer = setTimeout(hideButton, 1500) }
  }

  function frame(...body) {
    renderDownloads()
    panel.replaceChildren(
      h('div', { class: 'head' }, h('div', { class: 'mark' }, h('i')), h('div', { class: 'brand' }, 'FluxGet'),
        h('button', { class: 'x', 'aria-label': 'Hide', title: 'Hide (downloads keep going)', onclick: close }, '–')),
      dlBox,
      ...body.flat().filter(Boolean))
  }

  async function show() {
    open = true
    button.classList.add('open')
    lastPos = ''
    panel.classList.add('show')
    place()
    frame(h('div', { class: 'msg' }, h('span', { class: 'spin' }), 'Connecting to FluxGet…'))
    const status = await send({ type: 'fg-status' })
    if (status?.state === 'offline') return frame(h('div', { class: 'msg err' }, "FluxGet isn't running on this computer."), h('div', { class: 'foot' }, h('span'), h('button', { class: 'primary', onclick: () => send({ type: 'fg-launch' }) }, 'Open FluxGet')))
    if (status?.state !== 'ready') return frame(h('div', { class: 'msg' }, 'Connect this browser to FluxGet first: click the FluxGet icon in the toolbar and choose Connect.'))

    const src = target?.currentSrc || target?.src || ''
    if (!MEDIA_SITE) {
      const kind = target instanceof HTMLAudioElement ? 'Audio' : 'Video'
      if (/^https?:/i.test(src)) {
        const name = src.split('/').pop().split('?')[0].slice(0, 40)
        return frame(h('div', { class: 'label' }, `${kind} file`),
          row(ICON_DOWN, 'Download', name, '', () => startDirect(src)),
          row(ICON_PLAY, 'Play in FluxGet', 'stream, no download', '', () => playIt(src)))
      }
      const found = ((await send({ type: 'fg-detected-tab' }))?.items || []).slice(0, 5)
      if (!open) return
      if (!found.length) {
        return frame(h('div', { class: 'msg' }, 'FluxGet has not seen this stream load yet. Play it for a few seconds, then click again — or try the page in FluxGet.'),
          h('div', { class: 'foot' }, h('span'), h('button', { class: 'primary', onclick: () => send({ type: 'fg-open-media', url: location.href }) }, 'Open page in FluxGet')))
      }
      return frame(h('div', { class: 'label' }, `${kind} streams on this page`),
        found.map(f => {
          const name = f.manifest ? 'Stream (all qualities)' : (f.url.split('/').pop().split('?')[0].slice(0, 32) || f.type)
          return h('div', {}, row(ICON_DOWN, name, f.manifest ? 'HLS/DASH' : f.type, f.size ? fmt(f.size) : '', () => f.manifest ? send({ type: 'fg-open-media', url: f.url }) : startDirect(f.url)),
            row(ICON_PLAY, 'Play in FluxGet', 'stream, no download', '', () => playIt(f.url)))
        }))
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
    const options = h('div', { class: 'opts' },
      h('div', { class: 'label' }, 'Video'),
      videos.length
        ? videos.map(v => row(ICON_DOWN, quality(v.height), `MP4${v.fps > 30 ? ` · ${Math.round(v.fps)} fps` : ''}`, v.size ? `${v.estimated ? '≈' : ''}${fmt(v.size)}` : '', () => startMedia(info, { mode: 'video', height: v.height, container: 'mp4' }, `${quality(v.height)} MP4`)))
        : row(ICON_DOWN, 'Best', 'MP4', '', () => startMedia(info, { mode: 'video', height: null, container: 'mp4' }, 'Best quality')),
      h('div', { class: 'label' }, 'Audio only'),
      row(ICON_DOWN, 'MP3', '192 kbps', '', () => startMedia(info, { mode: 'audio', audioFormat: 'mp3', audioQuality: '192' }, 'MP3 192 kbps')),
      row(ICON_DOWN, 'MP3', '320 kbps', '', () => startMedia(info, { mode: 'audio', audioFormat: 'mp3', audioQuality: '320' }, 'MP3 320 kbps')),
      row(ICON_DOWN, 'M4A', 'original quality', '', () => startMedia(info, { mode: 'audio', audioFormat: 'm4a', audioQuality: null }, 'M4A')))
    const collapsed = active.size > 0
    if (collapsed) options.hidden = true
    const more = collapsed ? h('button', { class: 'more', type: 'button', onclick: () => { options.hidden = !options.hidden; more.textContent = options.hidden ? 'Download another format ▾' : 'Hide formats ▴' } }, 'Download another format ▾') : null
    frame(
      collapsed ? null : h('div', { class: 'title', title: info.title }, info.title),
      collapsed ? null : row(ICON_PLAY, 'Play in FluxGet', 'stream now, no download', '', () => playIt(url)),
      more,
      options,
      h('div', { class: 'foot' }, h('button', { class: 'link', onclick: () => send({ type: 'fg-open-media', url }) }, 'More formats in FluxGet'), h('button', { class: 'link', onclick: () => send({ type: 'fg-open-app' }) }, 'Open FluxGet')))
    if (!collapsed) panel.querySelector('.row')?.focus()
  }

  function row(icon, main, sub, right, onclick) {
    return h('button', { class: 'row', type: 'button', onclick }, svg(icon), h('b', {}, main), h('span', {}, sub), h('em', {}, right))
  }

  const quality = hgt => (hgt >= 2160 ? `${hgt >= 4320 ? '8K' : '4K'}` : hgt >= 1440 ? '1440p' : `${hgt}p`)

  async function startMedia(info, opts, label) {
    const r = await send({ type: 'fg-media-download', item: { url: info.url, title: info.title, id: info.id }, opts })
    if (!r?.ok) {
      active.set(`err-${Date.now()}`, { title: info.title, label, state: 'fail', text: r?.error || 'FluxGet could not start this download.' })
      renderDownloads()
      return
    }
    active.set(r.jobId, { title: info.title, label, state: 'run', pct: 0, text: 'Starting…' })
    const optionsEl = panel.querySelector('.opts')
    if (optionsEl) optionsEl.hidden = true
    renderDownloads()
    poll()
  }

  async function playIt(url) {
    const r = await send({ type: 'fg-play', url })
    if (r?.ok) close()
    else { active.set(`play-${Date.now()}`, { title: url, label: 'Play', state: 'fail', text: r?.error || 'FluxGet could not open the player.' }); renderDownloads() }
  }

  async function startDirect(src) {
    const r = await send({ type: 'video-button', src })
    active.set(`direct-${Date.now()}`, { title: src.split('/').pop().split('?')[0], label: 'File', state: r?.ok ? 'done' : 'fail', text: r?.ok ? 'Sent to FluxGet' : (r?.error || 'FluxGet could not start this download.') })
    renderDownloads()
  }

  function renderDownloads() {
    if (active.size === 0) { dlBox.replaceChildren(); return }
    dlBox.replaceChildren(...[
      h('div', { class: 'label' }, busy() ? 'Downloading' : 'Downloads'),
      ...[...active.entries()].map(([id, a]) => {
        const item = h('div', { class: 'dl' }, h('div', { class: 'dl-name', title: a.title }, `${a.label} · ${a.title}`))
        if (a.state === 'run') {
          item.append(h('div', { class: 'bar' }, h('i', { style: `width:${Math.max(2, a.pct)}%` })), h('div', { class: 'meta' }, h('span', {}, a.text), h('span', {}, a.right || `${Math.round(a.pct)}%`)))
        } else if (a.state === 'done') {
          item.append(h('div', { class: 'dl-done' }, h('span', { class: 'done' }, svg(ICON_CHECK), a.text || 'Download complete'),
            typeof id === 'number' ? h('span', { class: 'acts' },
              h('button', { class: 'link', onclick: () => send({ type: 'fg-reveal', id }) }, 'Show in folder'),
              h('button', { class: 'link', onclick: () => send({ type: 'fg-open-file', id }) }, 'Open')) : null))
        } else {
          item.append(h('div', { class: 'msg err', style: 'padding:6px 0 0' }, a.text))
        }
        return item
      }),
      busy() ? h('div', { class: 'hint' }, 'You can hide this panel — downloads continue in FluxGet.') : null,
    ].filter(Boolean))
  }

  let polling = 0
  function poll() {
    if (polling) return
    const tick = async () => {
      for (const [id, a] of active) {
        if (a.state !== 'run') continue
        const r = await send({ type: 'fg-job', id })
        if (!r?.ok) continue
        const j = r.job
        if (j.status === 'Completed') { a.state = 'done'; a.text = 'Download complete'; continue }
        if (j.status === 'Failed' || j.status === 'NeedsAttention') { a.state = 'fail'; a.text = j.error || 'The download stopped.'; continue }
        a.pct = j.totalBytes ? Math.min(100, (j.receivedBytes / j.totalBytes) * 100) : a.pct
        a.text = j.phase || (j.totalBytes ? `${fmt(j.receivedBytes)} of ${fmt(j.totalBytes)}` : j.status === 'Queued' ? 'Waiting in queue' : 'Starting…')
        a.right = j.speed > 0 ? `${fmt(j.speed)}/s` : ''
      }
      updateRing()
      if (open) renderDownloads()
      if (!busy()) { clearInterval(polling); polling = 0 }
    }
    polling = setInterval(tick, 1000)
    void tick()
  }

  function updateRing() {
    const running = [...active.values()].filter(a => a.state === 'run')
    if (running.length) {
      const pct = Math.round(running.reduce((t, a) => t + (a.pct || 0), 0) / running.length)
      button.classList.add('busy')
      button.style.setProperty('--p', String(pct))
      label.textContent = `${pct}%`
      button.title = `FluxGet: downloading ${pct}% — click to see progress`
      clearTimeout(hideTimer)
      if (target) schedule()
    } else {
      if (button.classList.contains('busy')) {
        button.classList.remove('busy')
        button.classList.add('finished')
        label.textContent = 'Done'
        setTimeout(() => { button.classList.remove('finished'); label.textContent = 'Download'; if (!open) hideButton() }, 4000)
      }
      button.title = 'Download with FluxGet'
    }
  }
})()
