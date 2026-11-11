// A real-browser test of an installed NKeeper (docs/architecture.md §8), through nginx, with headless Chromium:
// sign in, create a host for an SSH server with a saved credential, open a terminal with it and trust the
// server's key, check the terminal draws, runs commands and resizes with the window, open another with a
// typed credential, then delete the host.
//
//   BASE=https://localhost ADMIN_PASSWORD=… SSH_ADDRESS=127.0.0.1 SSH_USER=… SSH_PASSWORD=… node terminal.mjs
//
// It creates only its own host, named e2e-<random>, and deletes it at the end, so it can run against a server
// that has other hosts. Screenshots go to SCREENSHOTS (default: this folder).
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
const sshAddress = env('SSH_ADDRESS', '127.0.0.1')
const sshUser = env('SSH_USER')
const sshPassword = env('SSH_PASSWORD')
const screenshots = env('SCREENSHOTS', fileURLToPath(new URL('.', import.meta.url)))
const hostName = `e2e-${randomBytes(3).toString('hex')}`

const browser = await chromium.launch()
// The installer's certificate is self-signed unless one was given.
const page = await browser.newPage({ ignoreHTTPSErrors: true, viewport: { width: 1280, height: 800 } })
const pageErrors = []
const consoleLines = []
page.on('pageerror', (error) => pageErrors.push(error.message))
page.on('console', (message) => consoleLines.push(`${message.type()}: ${message.text()}`))
const step = (text) => console.log(`--> ${text}`)
const terminalText = () => page.locator('[role="tabpanel"]:not([hidden]) .xterm-rows').innerText()

/** Waits until the visible terminal shows `pattern`. */
async function waitForTerminal(pattern, timeout = 20000) {
  const deadline = Date.now() + timeout
  while (Date.now() < deadline) {
    const text = await terminalText().catch(() => '')
    const match = text.match(pattern)
    if (match) {
      return match
    }
    await page.waitForTimeout(250)
  }
  throw new Error(`The terminal never showed ${pattern}. It shows:\n${await terminalText().catch(() => '(nothing)')}`)
}

async function connect(entry) {
  await page.getByRole('article', { name: hostName }).getByRole('button', { name: 'Connect' }).click()
  await page.getByRole('menuitem', { name: entry }).click()
}

let failure = null
let created = false
try {
  step('sign in')
  await page.goto(`${base}/`)
  await page.getByLabel('User name or email').fill(adminUser)
  await page.getByLabel('Password').fill(adminPassword)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await page.getByRole('button', { name: 'Sign out' }).waitFor({ timeout: 15000 })

  step(`create ${hostName}, with an SSH service and a saved credential`)
  await page.getByRole('button', { name: 'New host' }).click()
  const form = page.getByRole('form', { name: 'New host' })
  await form.getByLabel('Name', { exact: true }).fill(hostName)
  await form.getByLabel('IP address').fill(sshAddress)
  await form.getByLabel('Service type').selectOption({ label: 'SSH' })
  await form.getByRole('button', { name: 'Add service' }).click()
  await form.getByRole('button', { name: 'Add credential' }).click()
  await form.getByLabel('User name').fill(sshUser)
  await form.getByLabel('Password', { exact: true }).fill(sshPassword)
  await form.getByRole('button', { name: 'Create host' }).click()
  await page.getByRole('article', { name: hostName }).waitFor({ timeout: 15000 })
  created = true

  step('its health is checked on save')
  await page.getByRole('article', { name: hostName }).getByText(/^Up, \d+ ms$/).waitFor({ timeout: 15000 })

  step('double-click the host to open a terminal with its saved credential, and trust the server key')
  await page.getByRole('button', { name: new RegExp(`^${hostName},`) }).dblclick()
  const key = page.getByRole('dialog', { name: 'Trust this server key?' })
  await key.waitFor({ timeout: 30000 })
  console.log(`    ${await key.locator('.nk-mono').innerText()}`)
  await key.getByRole('button', { name: 'Trust' }).click()

  step('run a command')
  await waitForTerminal(/[$#] *$/m, 30000)
  await page.locator('[role="tabpanel"]:not([hidden]) .nk-ssh-terminal').click()
  // Each size reading is marked, so a later one can't be mistaken for an earlier one still on screen.
  await page.keyboard.type('echo nk-e2e-$((6*7)) size-before-$(stty size | tr " " x)\n')
  await waitForTerminal(/nk-e2e-42/)
  const [, rows, columns] = await waitForTerminal(/size-before-(\d+)x(\d+)/)
  console.log(`    the server's terminal is ${rows} x ${columns}`)
  await page.screenshot({ path: `${screenshots}/terminal.png` })

  step('resize the window, and the terminal with it')
  await page.setViewportSize({ width: 1600, height: 1000 })
  await page.waitForTimeout(500)
  await page.keyboard.type('echo size-after-$(stty size | tr " " x)\n')
  const [, newRows, newColumns] = await waitForTerminal(/size-after-(\d+)x(\d+)/)
  console.log(`    now ${newRows} x ${newColumns}`)
  if (Number(newRows) <= Number(rows) || Number(newColumns) <= Number(columns)) {
    throw new Error('The server’s terminal did not grow with the window.')
  }

  step('exit the shell')
  await page.keyboard.type('exit\n')
  await page.getByText('The session ended.').waitFor({ timeout: 15000 })

  step('open another with a typed credential: the key is trusted now, so no prompt')
  await page.getByRole('tab').first().click()
  await connect('Type a credential…')
  const typed = page.getByRole('dialog', { name: 'Type a credential' })
  await typed.getByLabel('User name').fill(sshUser)
  await typed.getByLabel('Password', { exact: true }).fill(sshPassword)
  await typed.getByRole('button', { name: 'Connect' }).click()
  await waitForTerminal(/[$#] *$/m, 30000)
  await page.locator('[role="tabpanel"]:not([hidden]) .nk-ssh-terminal').click()
  await page.keyboard.type('echo typed-$((2+3))\n')
  await waitForTerminal(/typed-5/)

  if (pageErrors.length > 0) {
    throw new Error(`The page threw: ${pageErrors.join('; ')}`)
  }
} catch (error) {
  failure = error
  await page.screenshot({ path: `${screenshots}/failure.png` }).catch(() => {})
} finally {
  if (created) {
    step(`delete ${hostName}`)
    try {
      // A dialog left open by a failure makes the page behind it inert: cancel it first (Escape).
      for (let i = 0; i < 5 && (await page.locator('dialog[open]').count()) > 0; i++) {
        await page.keyboard.press('Escape')
        await page.waitForTimeout(200)
      }
      // Closing a tab renumbers the rest, so close the first until none are left.
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
  // xterm's <style> elements are blocked by the policy, as designed (the SDK copies them), so those lines are expected.
  consoleLines.filter((line) => !line.includes('Applying inline style violates')).forEach((line) => console.log(line.slice(0, 300)))
  console.error(`FAILED: ${failure.message}`)
  process.exit(1)
}
console.log('The browser test passed.')
