import { useSyncExternalStore } from 'react'

export type Page = 'overview' | 'downloads' | 'media' | 'torrents' | 'scheduler' | 'completed' | 'settings'
const PAGES: Page[] = ['overview', 'downloads', 'media', 'torrents', 'scheduler', 'completed', 'settings']

function parse(): { page: Page; params: URLSearchParams } {
  const [path, query] = location.hash.replace(/^#\/?/, '').split('?')
  const page = (PAGES.includes(path as Page) ? path : 'overview') as Page
  return { page, params: new URLSearchParams(query ?? '') }
}

let current = parse()
const subs = new Set<() => void>()
window.addEventListener('hashchange', () => { current = parse(); subs.forEach(s => s()) })

export function useRoute() {
  return useSyncExternalStore(cb => { subs.add(cb); return () => subs.delete(cb) }, () => current)
}

export function navigate(page: Page, params?: Record<string, string>) {
  const q = params ? '?' + new URLSearchParams(params).toString() : ''
  location.hash = `#/${page}${q}`
}
