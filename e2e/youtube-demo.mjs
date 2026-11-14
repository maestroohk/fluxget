import { chromium } from 'playwright'
import { existsSync, readFileSync, rmSync, mkdtempSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, resolve, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const shots = process.env.DEMO_SHOTS || join(tmpdir(), 'fluxget-demo')
const codeFile = join(shots, 'code.txt')
const videoUrl = process.env.DEMO_URL || 'https://www.youtube.com/watch?v=aqz-KE-bpKQ'
const quality = Number(process.env.DEMO_ROW || 3)
const sleep = ms => new Promise(r => setTimeout(r, ms))
const log = m => console.log(`[demo] ${m}`)
rmSync(codeFile, { force: true })

const context = await chromium.launchPersistentContext(process.env.DEMO_PROFILE || mkdtempSync(join(tmpdir(), 'fluxget-demo-profile-')), {
  channel: 'msedge',
  headless: false,
  viewport: { width: 1280, height: 800 },
  ignoreDefaultArgs: ['--disable-extensions'],
  args: [`--disable-extensions-except=${join(root, 'extension')}`, `--load-extension=${join(root, 'extension')}`, '--mute-audio', '--autoplay-policy=user-gesture-required'],
})
let [worker] = context.serviceWorkers()
worker ??= await context.waitForEvent('serviceworker')
const extId = new URL(worker.url()).host

const popup = context.pages()[0] ?? await context.newPage()
await popup.goto(`chrome-extension://${extId}/popup.html`)
await popup.waitForSelector('#pair:not([hidden]), #main:not([hidden])', { timeout: 10000 })
if (await popup.isVisible('#pair')) {
  await popup.click('#pair-start')
  log('pairing requested; waiting for code file')
  while (!existsSync(codeFile)) await sleep(500)
  await popup.fill('#pair-code', readFileSync(codeFile, 'utf8').trim())
  await popup.click('#pair-form button')
  await popup.waitForSelector('#main:not([hidden])', { timeout: 10000 })
}
log('paired')
await popup.screenshot({ path: join(shots, '1-popup-paired.png') })

const page = await context.newPage()
await popup.close()
await page.goto(videoUrl, { waitUntil: 'domcontentloaded' })
for (const label of ['Reject all', 'Reject the use of cookies and other data for the purposes described']) {
  const btn = page.getByRole('button', { name: label }).first()
  if (await btn.isVisible({ timeout: 3000 }).catch(() => false)) { await btn.click(); log('declined optional cookies'); break }
}
const player = page.locator('#movie_player')
await player.waitFor({ timeout: 30000 })
await sleep(4000)
const box = await player.boundingBox()
await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2)
await sleep(300)
await page.mouse.move(box.x + box.width / 2 + 20, box.y + box.height / 2 + 10)
await sleep(800)
await page.screenshot({ path: join(shots, '2-button-on-player.png') })
await page.screenshot({ path: join(shots, '2a-button-closeup.png'), clip: { x: box.x + box.width - 260, y: box.y, width: 260, height: 80 } })
log('button shown')

await page.mouse.move(box.x + box.width - 25, box.y + 24)
await sleep(600)
await page.screenshot({ path: join(shots, '2b-button-hover.png'), clip: { x: box.x + box.width - 260, y: box.y, width: 260, height: 80 } })
await page.mouse.click(box.x + box.width - 25, box.y + 24)
log('panel opened; waiting for formats')
await sleep(14000)
await page.screenshot({ path: join(shots, '3-quality-panel.png') })

for (let i = 0; i < quality; i++) await page.keyboard.press('Tab')
await page.keyboard.press('Enter')
log(`chose row ${quality + 1}`)
await sleep(5000)
await page.screenshot({ path: join(shots, '4-downloading-panel.png') })
await page.keyboard.press('Escape')
await sleep(2500)
await page.screenshot({ path: join(shots, '5-hidden-ring.png'), clip: { x: box.x + box.width - 260, y: box.y, width: 260, height: 80 } })
log('panel hidden')
await page.mouse.click(box.x + box.width - 30, box.y + 24)
await sleep(2000)
await page.screenshot({ path: join(shots, '6-reopened.png') })
log('panel reopened')
while (!existsSync(join(shots, 'stop'))) await sleep(1000)
await page.screenshot({ path: join(shots, '7-final.png') })
log('finished')
await context.close()
