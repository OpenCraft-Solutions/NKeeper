// A real-browser test of web pages (docs/architecture.md §7.16–7.19) on an installed NKeeper with a web domain:
// sign in, create a host whose HTTPS service is this same server, open its web page, trust its self-signed
// certificate in core's dialog, see the page in the frame on its own session origin, close it, delete the host.
//
//   BASE=https://localhost ADMIN_PASSWORD=… TARGET_ADDRESS=127.0.0.1 node web.mjs
//
// The install needs --web-domain web.localhost (or a real one with wildcard DNS): Chromium resolves any
// *.localhost name to loopback, so the session origins resolve without DNS.
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
const targetAddress = env('TARGET_ADDRESS', '127.0.0.1')
const screenshots = env('SCREENSHOTS', fileURLToPath(new URL('.', import.meta.url)))
const hostName = `e2e-web-${randomBytes(3).toString('hex')}`

// To test a server whose web domain has no DNS yet: RESOLVE_WEB_DOMAIN=web.example.test=192.0.2.10 sends
// *.web.example.test to that address, in this browser only.
const resolve = process.env.RESOLVE_WEB_DOMAIN?.split('=')
const browser = await chromium.launch(resolve ? { args: [`--host-resolver-rules=MAP *.${resolve[0]} ${resolve[1]}`] } : {})
const page = await browser.newPage({ ignoreHTTPSErrors: true, viewport: { width: 1280, height: 800 } })
const pageErrors = []
const consoleLines = []
page.on('pageerror', (error) => pageErrors.push(error.message))
page.on('console', (message) => consoleLines.push(`${message.type()}: ${message.text()}`))
const step = (text) => console.log(`--> ${text}`)

let failure = null
let created = false // set once the host exists
try {
  step('sign in')
  await page.goto(`${base}/`)
  await page.getByLabel('User name or email').fill(adminUser)
  await page.getByLabel('Password').fill(adminPassword)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await page.getByRole('button', { name: 'Sign out' }).waitFor({ timeout: 15000 })

  step(`create ${hostName}, with an HTTPS service on ${targetAddress}:443`)
  await page.getByRole('button', { name: 'New host' }).click()
  const form = page.getByRole('form', { name: 'New host' })
  await form.getByLabel('Name', { exact: true }).fill(hostName)
  await form.getByLabel('IP address').fill(targetAddress)
  await form.getByLabel('Service type').selectOption({ label: 'HTTPS' })
  await form.getByRole('button', { name: 'Add service' }).click()
  await form.getByRole('button', { name: 'Create host' }).click()
  const details = page.getByRole('article', { name: hostName })
  await details.waitFor({ timeout: 15000 })
  created = true

  step('the TLS health probe reports the certificate')
  await details.getByText(/^Up, \d+ ms/).waitFor({ timeout: 15000 })

  step('open the web page, and trust the certificate in core’s dialog')
  await details.getByRole('button', { name: 'Connect' }).click()
  await page.getByRole('menuitem', { name: 'Web page' }).click()
  const trust = page.getByRole('dialog', { name: 'Trust this certificate?' })
  await trust.waitFor({ timeout: 30000 })
  console.log(`    ${await trust.locator('.nk-mono').innerText()}`)
  await trust.getByRole('button', { name: 'Trust' }).click()

  step('the page shows in the frame, on its own session origin')
  const frame = page.locator('iframe[title$="web page"]')
  await frame.waitFor({ timeout: 30000 })
  const src = await frame.getAttribute('src')
  console.log(`    ${new URL(src).host}`)
  if (!/^nkw-[0-9a-f]{32}\./.test(new URL(src).host)) {
    throw new Error(`The frame isn't on a session origin: ${src}`)
  }
  // The target here is NKeeper itself, so the framed page is its sign-in form.
  await page.frameLocator('iframe[title$="web page"]').getByRole('button', { name: 'Sign in' }).waitFor({ timeout: 30000 })
  await page.screenshot({ path: `${screenshots}/web.png` })

  step('close the tab')
  await page.getByRole('button', { name: new RegExp(`^Close ${hostName}`) }).click()

  if (pageErrors.length > 0) {
    throw new Error(`The page threw: ${pageErrors.join('; ')}`)
  }
} catch (error) {
  failure = error
  await page.screenshot({ path: `${screenshots}/web-failure.png` }).catch(() => {})
} finally {
  if (created || (await page.getByRole('button', { name: new RegExp(`^${hostName},`) }).count()) > 0) {
    step(`delete ${hostName}`)
    try {
      for (let i = 0; i < 5 && (await page.locator('dialog[open]').count()) > 0; i++) {
        await page.keyboard.press('Escape')
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
console.log('The web page test passed.')
