// A real-browser test of remote desktops (docs/architecture.md §7.20–7.21) on an installed NKeeper with guacd:
// sign in, create a host for an RDP server with a saved credential, open its desktop, trust the server's
// certificate in core's dialog, see the desktop draw, close it, delete the host.
//
//   BASE=https://localhost ADMIN_PASSWORD=… RDP_ADDRESS=127.0.0.1 RDP_USER=… RDP_PASSWORD=… node desktop.mjs
//
// It creates only its own host, named e2e-rdp-<random>, and deletes it at the end. Screenshots go to
// SCREENSHOTS (default: this folder).
import { randomBytes } from 'node:crypto'
import { fileURLToPath } from 'node:url'
import { chromium } from 'playwright'

const env = (name, fallback) => {
  const value = process.env[name] ?? fallback
  if (value === undefined) {
    console.error(`Set ${name}.`)
    process.exit(2)
  }
  return value
}
const base = env('BASE', 'https://localhost')
const adminUser = env('ADMIN_USER', 'admin')
const adminPassword = env('ADMIN_PASSWORD')
const rdpAddress = env('RDP_ADDRESS', '127.0.0.1')
const rdpUser = env('RDP_USER')
const rdpPassword = env('RDP_PASSWORD')
const screenshots = env('SCREENSHOTS', fileURLToPath(new URL('.', import.meta.url)))
const hostName = `e2e-rdp-${randomBytes(3).toString('hex')}`

const browser = await chromium.launch()
const page = await browser.newPage({ ignoreHTTPSErrors: true, viewport: { width: 1280, height: 800 } })
const pageErrors = []
const consoleLines = []
page.on('pageerror', (error) => pageErrors.push(error.message))
page.on('console', (message) => consoleLines.push(`${message.type()}: ${message.text()}`))
const step = (text) => console.log(`--> ${text}`)
const panel = page.locator('[role="tabpanel"]:not([hidden]) .nk-desktop')

/** How many of the desktop's canvas pixels aren't black: Guacamole draws the remote screen into canvas layers. */
const drawnPixels = () =>
  panel.evaluate((element) => {
    let drawn = 0
    for (const canvas of element.querySelectorAll('canvas')) {
      if (canvas.width === 0 || canvas.height === 0) {
        continue
      }
      const data = canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height).data
      for (let i = 0; i < data.length; i += 16) {
        if (data[i + 3] > 0 && data[i] + data[i + 1] + data[i + 2] > 0) {
          drawn++
        }
      }
    }
    return drawn
  })

let failure = null
let created = false
try {
  step('sign in')
  await page.goto(`${base}/`)
  await page.getByLabel('User name or email').fill(adminUser)
  await page.getByLabel('Password').fill(adminPassword)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await page.getByRole('button', { name: 'Sign out' }).waitFor({ timeout: 15000 })

  step(`create ${hostName}, with an RDP service on ${rdpAddress}:3389 and a saved credential`)
  await page.getByRole('button', { name: 'New host' }).click()
  const form = page.getByRole('form', { name: 'New host' })
  await form.getByLabel('Name', { exact: true }).fill(hostName)
  await form.getByLabel('IP address').fill(rdpAddress)
  await form.getByLabel('Service type').selectOption({ label: 'RDP' })
  await form.getByRole('button', { name: 'Add service' }).click()
  await form.getByRole('button', { name: 'Add credential' }).click()
  await form.getByLabel('User name').fill(rdpUser)
  await form.getByLabel('Password', { exact: true }).fill(rdpPassword)
  await form.getByRole('button', { name: 'Create host' }).click()
  await page.getByRole('article', { name: hostName }).waitFor({ timeout: 15000 })
  created = true

  step('double-click the host to open its desktop, and trust the server’s certificate')
  await page.getByRole('button', { name: new RegExp(`^${hostName},`) }).dblclick()
  const trust = page.getByRole('dialog', { name: 'Trust this certificate?' })
  await trust.waitFor({ timeout: 30000 })
  console.log(`    ${await trust.locator('.nk-mono').innerText()}`)
  await trust.getByRole('button', { name: 'Trust' }).click()

  step('the desktop draws')
  const deadline = Date.now() + 60000
  let drawn = 0
  while (Date.now() < deadline && drawn < 1000) {
    const ended = panel.locator('.nk-desktop-overlay')
    if ((await ended.count()) > 0 && (await ended.getByRole('button', { name: 'Reconnect' }).count()) > 0) {
      throw new Error(`The desktop ended: ${await ended.locator('span').innerText()}`)
    }
    drawn = await drawnPixels().catch(() => 0)
    await page.waitForTimeout(500)
  }
  if (drawn < 1000) {
    throw new Error('The desktop never drew.')
  }
  console.log(`    ${drawn} sampled pixels drawn`)
  await page.screenshot({ path: `${screenshots}/desktop.png` })

  step('close the tab')
  await page.getByRole('button', { name: new RegExp(`^Close ${hostName}`) }).click()

  if (pageErrors.length > 0) {
    throw new Error(`The page threw: ${pageErrors.join('; ')}`)
  }
} catch (error) {
  failure = error
  await page.screenshot({ path: `${screenshots}/desktop-failure.png` }).catch(() => {})
} finally {
  if (created) {
    step(`delete ${hostName}`)
    try {
      // A dialog left open by a failure makes the page behind it inert: cancel it first (Escape).
      for (let i = 0; i < 5 && (await page.locator('dialog[open]').count()) > 0; i++) {
        await page.keyboard.press('Escape')
        await page.waitForTimeout(200)
      }
      const closeButtons = page.getByRole('button', { name: new RegExp(`^Close ${hostName}`) })
      while ((await closeButtons.count()) > 0) {
        await closeButtons.first().click()
      }
      await page.getByRole('tab').first().click()
      await page.getByRole('button', { name: new RegExp(`^${hostName},`) }).click()
      await page.getByRole('button', { name: 'Delete', exact: true }).click()
      await page.getByRole('button', { name: `Delete ${hostName}` }).click()
      await page.getByRole('button', { name: new RegExp(`^${hostName},`) }).waitFor({ state: 'detached', timeout: 10000 })
    } catch (error) {
      console.log(`    couldn't delete it: ${error.message.split('\n')[0]}`)
      failure ??= error
    }
  }
  await browser.close()
}

if (failure) {
  console.log('--- the browser console ---')
  consoleLines.filter((line) => !line.includes('Applying inline style violates')).forEach((line) => console.log(line.slice(0, 300)))
  console.error(`FAILED: ${failure.message}`)
  process.exit(1)
}
console.log('The desktop test passed.')
