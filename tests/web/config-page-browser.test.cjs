'use strict';
// The Zorofin setup page in the Dashboard: tabs in setup order, status marks,
// one Save per step, and what each save sends to the plugin and the companion.
// Run: node server/jellyfin-plugin-zorofin/tests/web/config-page-browser.test.cjs
// With --shots <dir> it also saves desktop and phone screenshots of each tab.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const PAGE = fs.readFileSync(path.join(__dirname, '../../src/Jellyfin.Plugin.Zorofin/Web/configPage.html'), 'utf8');
const shots = process.argv.includes('--shots') ? process.argv[process.argv.indexOf('--shots') + 1] : null;

let playwright;
try { playwright = require('playwright'); } catch (_) { console.log('playwright not installed: skipped'); process.exit(0); }

const PLUGIN = { RequestsEnabled: false, SeerrUrl: '', SeerrApiKey: '', CompanionUrl: 'http://companion:9310', CompanionSecret: 'x'.repeat(40), PlayStatsEnabled: true, WebUiEnabled: false };
const VIEW = { schemaVersion: 1, sections: [
  { id: 'connections', kind: 'core', title: 'Connections', description: 'How the companion reaches Jellyfin and Seerr.', options: [
    { key: 'jellyfinUrl', type: 'url', label: 'Jellyfin address', source: 'environment', value: 'http://jellyfin:8096' },
    { key: 'jellyfinApiKey', type: 'secret', label: 'Jellyfin API key', source: 'dashboard', set: true },
    { key: 'seerrUrl', type: 'url', label: 'Seerr address', source: 'environment', value: 'http://seerr:5055' },
    { key: 'seerrApiKey', type: 'secret', label: 'Seerr API key', source: 'default', set: false },
    { key: 'serverName', type: 'text', label: 'Server name', source: 'default', value: '' },
  ] },
  { id: 'notifications', kind: 'module', title: 'Request alerts', description: 'Tells viewers when requests are ready.', enabled: false, running: false, options: [
    { key: 'emailFrom', type: 'text', label: 'Email sender', source: 'default', value: '' },
  ] },
  { id: 'liveTvGuide', kind: 'module', title: 'Live TV guide', enabled: true, running: true, options: [
    { key: 'refreshMinutes', type: 'number', label: 'Refresh every (minutes)', source: 'default', value: 30, min: 5, max: 1440 },
    { key: 'tvmaze', type: 'boolean', label: 'Show artwork from TVmaze', source: 'default', value: false },
    { key: 'logoCountries', type: 'list', label: 'Logo countries', source: 'default', value: ['united-states'] },
  ] },
  { id: 'recommendations', kind: 'module', title: 'Recommendations', enabled: true, running: false, error: 'Needs the Seerr API key', options: [
    { key: 'ai', type: 'choice', label: 'AI ranking', source: 'default', value: 'off', choices: [{ value: 'off', label: 'Off' }, { value: 'anthropic', label: 'Anthropic' }] },
    { key: 'aiKey', type: 'secret', label: 'AI API key', source: 'default', set: false },
  ] },
  { id: 'webUi', kind: 'module', title: 'Zorofin web look and player', enabled: false, running: false, options: [
    { key: 'theme', type: 'boolean', label: 'Zorofin theme', source: 'default', value: true },
    { key: 'requests', type: 'boolean', label: 'Requests hub', source: 'default', value: false },
    { key: 'colors', type: 'choice', display: 'swatches', label: 'Colours', source: 'default', value: 'zorofin',
      choices: [{ value: 'zorofin', label: 'Zorofin green', color: '#42d779' }, { value: 'ocean', label: 'Ocean', color: '#3d9cff' }, { value: 'custom', label: 'Custom' }] },
    { key: 'accent', type: 'color', label: 'Accent colour', source: 'default', value: '#42d779', when: { key: 'colors', value: 'custom' } },
    { key: 'logo', type: 'choice', label: 'Logo', source: 'default', value: 'zorofin', choices: [{ value: 'zorofin', label: 'Zorofin' }, { value: 'custom', label: 'Your own' }, { value: 'none', label: 'No logo' }] },
    { key: 'logoDesktop', type: 'image', slot: 'wordmark.png', label: 'Logo for wide screens', source: 'default', set: false, when: { key: 'logo', value: 'custom' } },
  ] },
  { id: 'sportsEvents', kind: 'module', title: 'Sports', enabled: false, running: false, options: [] },
] };

const stubs = ([plugin, view]) => {
  window.sent = [];
  window.Dashboard = { showLoadingMsg() {}, hideLoadingMsg() {}, alert() {}, processPluginConfigurationUpdateResult() {} };
  window.ApiClient = {
    getPluginConfiguration: async () => JSON.parse(JSON.stringify(plugin)),
    updatePluginConfiguration: async (id, config) => { window.sent.push(['PLUGIN', id, config]); Object.assign(plugin, config); return {}; },
    getUrl: p => '/' + p,
    ajax: async ({ type, url, data }) => {
      const body = data ? JSON.parse(data) : null;
      window.sent.push([type, url, body]);
      if (type === 'GET') return view;
      if (url.endsWith('/Test')) return { ok: false, checks: [{ label: 'Settings', ok: true, detail: 'complete' }, { label: 'Seerr address', ok: false, detail: 'connect ECONNREFUSED' }] };
      if (url.endsWith('/recommendations') && body.values.ai === 'anthropic' && !body.values.aiKey) {
        throw { status: 422, json: async () => ({ error: 'Recommendations: AI ranking needs the AI API key' }) };
      }
      const s = view.sections.find(x => url.endsWith('/' + x.id));
      if (typeof body.enabled === 'boolean') { s.enabled = body.enabled; s.running = body.enabled; delete s.error; }
      return s;
    },
  };
};

async function open(browser, viewport) {
  const page = await browser.newPage({ viewport });
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.route('**/*', route => route.abort());
  const html = PAGE.replace('<head>', `<head><style>body{margin:0;background:#101311}</style><script>(${stubs})(${JSON.stringify([PLUGIN, VIEW])});</script>`);
  await page.setContent(html);
  await page.evaluate(() => { try { sessionStorage.clear(); } catch (_) {} document.querySelector('#ZorofinConfigPage').dispatchEvent(new Event('pageshow')); });
  await page.waitForSelector('.zf-panel[data-panel="overview"]');
  return { page, errors };
}
const sent = page => page.evaluate(() => window.sent);
const tab = (page, id) => page.click(`.zf-tab[data-tab="${id}"]`);

(async () => {
  const browser = await playwright.chromium.launch({ headless: true });
  try {
    const { page, errors } = await open(browser, { width: 1280, height: 900 });

    // Tabs in setup order; companion parts without a step of their own get a tab too.
    assert.deepEqual(await page.$$eval('.zf-tab', n => n.map(t => t.dataset.tab)),
      ['overview', 'connect', 'requests', 'alerts', 'livetv', 'foryou', 'home', 'announce', 'look', 'mod-sportsEvents']);
    assert.equal(await page.getAttribute('.zf-tab[data-tab="overview"]', 'aria-selected'), 'true');
    assert.equal(await page.getAttribute('.zf-tab[data-tab="livetv"] .zf-step', 'class'), 'zf-step ok');
    assert.equal(await page.getAttribute('.zf-tab[data-tab="foryou"] .zf-step', 'class'), 'zf-step warn');
    assert.match(await page.textContent('#zfOverall'), /1 step needs attention/);
    // Overview points at the first step that needs something.
    assert.match(await page.textContent('.zf-next'), /Next: For you/);
    if (shots) await page.screenshot({ path: path.join(shots, 'desktop-overview.png'), fullPage: true });

    // Connect: one Seerr for both, secrets never shown, only changed companion values sent.
    await tab(page, 'connect');
    if (shots) await page.screenshot({ path: path.join(shots, 'desktop-connect.png'), fullPage: true });
    assert.equal(await page.inputValue('[data-section="seerr"] input[type="url"]'), 'http://seerr:5055', 'falls back to the companion\'s Seerr address');
    assert.equal(await page.getAttribute('[data-section="companion"] input[type="password"]', 'placeholder'), 'Saved. Type to replace.');
    assert.equal(await page.inputValue('[data-section="companion"] input[type="password"]'), '');
    assert.equal(await page.locator('[data-section="connections"] input').count(), 3, 'Seerr is not asked for twice');
    await page.fill('[data-section="seerr"] input[type="password"]', 'seerr-key');
    await page.fill('[data-section="connections"] input[type="text"]', 'Movie Night');
    await page.click('.zf-actions button:has-text("Save")');
    await page.waitForSelector('.zf-result li.ok');
    let log = await sent(page);
    const pluginSave = log.find(x => x[0] === 'PLUGIN');
    assert.equal(pluginSave[2].SeerrUrl, 'http://seerr:5055');
    assert.equal(pluginSave[2].SeerrApiKey, 'seerr-key');
    assert.equal(pluginSave[2].CompanionSecret, 'x'.repeat(40), 'untouched secret is kept');
    const conn = log.find(x => x[1] === '/Zorofin/Admin/Companion/Settings/connections');
    assert.deepEqual(conn[2], { values: { serverName: 'Movie Night', seerrApiKey: 'seerr-key' } });

    // Test shows each check.
    await page.click('.zf-actions button:has-text("Test")');
    await page.waitForSelector('.zf-result li.bad');
    assert.deepEqual(await page.$$eval('.zf-result li', n => n.map(l => [l.className, l.textContent])),
      [['ok', 'Settings: complete'], ['bad', 'Seerr address: connect ECONNREFUSED']]);

    // Requests: a switch that hides its fields while off.
    await tab(page, 'requests');
    assert.equal(await page.isHidden('[data-section="requests"] .zf-fields'), true);
    await page.click('[data-section="requests"] .zf-card-head .zf-switch input');
    assert.equal(await page.isVisible('[data-section="requests"] .zf-fields'), true);
    await page.fill('[data-section="requests"] input[type="url"] >> nth=0', 'http://radarr:7878');
    await page.click('[data-section="seerrJobs"] .zf-switch input >> nth=0');
    await page.fill('[data-section="seerrJobs"] input[type="number"]', '1');
    await page.click('.zf-actions button:has-text("Save")');
    await page.waitForSelector('.zf-result li.ok');
    log = await sent(page);
    const req = log.filter(x => x[0] === 'PLUGIN').pop()[2];
    assert.equal(req.RequestsEnabled, true);
    assert.equal(req.RadarrUrl, 'http://radarr:7878');
    assert.equal(req.AutoSeasonRequestsEnabled, true);
    assert.equal(req.AutoSeasonEpisodesLeft, 1);
    assert.equal('SeerrScanOnLibraryAdd' in req, false, 'unchanged switches are not sent');
    if (shots) await page.screenshot({ path: path.join(shots, 'desktop-requests.png'), fullPage: true });
    assert.equal(await page.getAttribute('.zf-tab[data-tab="requests"] .zf-step', 'class'), 'zf-step ok');

    // For you: a refused save shows the companion's reason.
    await tab(page, 'foryou');
    if (shots) await page.screenshot({ path: path.join(shots, 'desktop-foryou.png'), fullPage: true });
    await page.selectOption('[data-section="recommendations"] select', 'anthropic');
    await page.click('.zf-actions button:has-text("Save")');
    await page.waitForSelector('.zf-result li.bad');
    assert.match(await page.textContent('.zf-result'), /needs the AI API key/);
    await page.selectOption('[data-section="recommendations"] select', 'anthropic');
    await page.fill('[data-section="recommendations"] input[type="password"]', 'sk-test');
    await page.click('.zf-actions button:has-text("Save")');
    await page.waitForSelector('.zf-result li.ok');
    log = await sent(page);
    assert.deepEqual(log.filter(x => x[1] === '/Zorofin/Admin/Companion/Settings/recommendations').pop()[2],
      { values: { ai: 'anthropic', aiKey: 'sk-test' }, enabled: true });

    // Live TV: numbers, switches and lists are sent as such, and only when changed.
    await tab(page, 'livetv');
    await page.fill('[data-section="liveTvGuide"] input[type="number"]', '45');
    await page.click('[data-section="liveTvGuide"] .zf-checkrow');
    await page.click('.zf-actions button:has-text("Save")');
    await page.waitForSelector('.zf-result li.ok');
    log = await sent(page);
    assert.deepEqual(log.filter(x => x[1] === '/Zorofin/Admin/Companion/Settings/liveTvGuide').pop()[2],
      { values: { refreshMinutes: 45, tvmaze: true }, enabled: true });

    // Home: one switch for Zorofin Home rows, and a switch per row; rows that need another step say so.
    await tab(page, 'home');
    const home = '[data-section="homeRows"]';
    assert.equal(await page.isHidden(home + ' .zf-fields'), true);
    await page.click(home + ' .zf-card-head .zf-switch input');
    assert.equal(await page.isVisible(home + ' .zf-fields'), true);
    assert.equal(await page.locator(home + ' .zf-checkrow').count(), 13);
    assert.match(await page.textContent(home + ' .zf-field:has-text("Popular this week")'), /Most watched on this server/);
    await page.click(home + ' .zf-checkrow:has-text("Watch again") input');
    await page.click(home + ' .zf-checkrow:has-text("4K / HDR showcase") input');
    if (shots) await page.screenshot({ path: path.join(shots, 'desktop-home.png'), fullPage: true });
    await page.click('.zf-actions button:has-text("Save")');
    await page.waitForSelector('.zf-result li.ok');
    log = await sent(page);
    assert.deepEqual(log.filter(x => x[0] === 'PLUGIN').pop()[2].HomeRowsOff, 'showcase,watch-again');
    assert.equal(log.filter(x => x[0] === 'PLUGIN').pop()[2].HomeRowsEnabled, true);
    assert.equal(await page.getAttribute('.zf-tab[data-tab="home"] .zf-step', 'class'), 'zf-step ok');

    // Look and player: one switch turns on the plugin loader and the companion part.
    await tab(page, 'look');
    await page.click('[data-section="webUi"] .zf-card-head .zf-switch input');
    await page.click('[data-section="webUi"] .zf-checkrow:has-text("Requests hub")');
    // Presets are colour dots; the accent and logo uploads show only when they apply.
    const accent = '[data-section="webUi"] input[type="color"]';
    assert.equal(await page.isHidden(accent), true);
    await page.click('[data-section="webUi"] .zf-swatch:has-text("Custom")');
    assert.equal(await page.isVisible(accent), true);
    await page.$eval(accent, i => { i.value = '#3d9cff'; i.dispatchEvent(new Event('input')); });
    assert.equal(await page.isHidden('[data-section="webUi"] .zf-image'), true);
    await page.selectOption('[data-section="webUi"] select:not([hidden])', 'custom');
    assert.equal(await page.isVisible('[data-section="webUi"] .zf-image'), true);
    const png = Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), Buffer.alloc(16)]);
    await page.setInputFiles('[data-section="webUi"] .zf-image input[type="file"]', { name: 'logo.png', mimeType: 'image/png', buffer: png });
    await page.waitForFunction(() => document.querySelector('[data-section="webUi"] .zf-image .zf-help').textContent === 'logo.png');
    if (shots) await page.screenshot({ path: path.join(shots, 'desktop-look.png'), fullPage: true });
    await page.click('.zf-actions button:has-text("Save")');
    await page.waitForSelector('.zf-result li.ok');
    log = await sent(page);
    assert.equal(log.filter(x => x[0] === 'PLUGIN').pop()[2].WebUiEnabled, true);
    assert.deepEqual(log.filter(x => x[1] === '/Zorofin/Admin/Companion/Settings/webUi').pop()[2], { enabled: true,
      values: { requests: true, colors: 'custom', accent: '#3d9cff', logo: 'custom', logoDesktop: `data:image/png;base64,${png.toString('base64')}` } });

    // Announcement: written, checked and previewed here, saved to the plugin.
    await tab(page, 'announce');
    const ann = '[data-section="announcement"]';
    assert.equal(await page.textContent(`${ann} .zf-chip`), 'Off');
    await page.click(`${ann} .zf-card-head .zf-switch input`);
    await page.click('.zf-actions button:has-text("Save")');
    assert.match(await page.textContent('.zf-result'), /Add a title and a message/);
    await page.fill(`${ann} input[maxlength="80"]`, 'Maintenance tonight');
    await page.fill(`${ann} textarea`, 'The server restarts at 2 AM.');
    assert.equal(await page.textContent(`${ann} .zac-title`), 'Maintenance tonight');
    assert.match(await page.textContent(`${ann} .zf-count`), /^19 \/ 80$/);
    await page.selectOption(`${ann} select`, 'maintenance');
    assert.equal(await page.getAttribute(`${ann} .zac`, 'data-type'), 'maintenance');
    await page.fill(`${ann} input[maxlength="40"]`, 'Details');
    await page.fill(`${ann} .zf-field:has-text("Button link") input`, 'javascript:alert(1)');
    await page.click('.zf-actions button:has-text("Save")');
    assert.match(await page.textContent('.zf-result'), /must be a path on this server/);
    assert.equal(await page.locator(`${ann} .zac-action`).count(), 0, 'an unsafe link never shows in the preview');
    await page.fill(`${ann} .zf-field:has-text("Button link") input`, '/web/#/home');
    assert.equal(await page.textContent(`${ann} .zac-action`), 'Details');
    if (shots) await page.screenshot({ path: path.join(shots, 'desktop-announcement.png'), fullPage: true });
    await page.click('.zf-actions button:has-text("Save")');
    await page.waitForSelector('.zf-result li.ok');
    log = await sent(page);
    const saved = log.filter(x => x[0] === 'PLUGIN').pop()[2];
    assert.equal(saved.AnnouncementEnabled, true);
    assert.equal(saved.AnnouncementType, 'maintenance');
    assert.equal(saved.AnnouncementTitle, 'Maintenance tonight');
    assert.equal(saved.AnnouncementActionHref, '/web/#/home');
    assert.equal(await page.textContent(`${ann} .zf-chip`), 'Showing');
    assert.equal(await page.getAttribute('.zf-tab[data-tab="announce"] .zf-step', 'class'), 'zf-step ok');
    await page.click(`${ann} button:has-text("Show it again")`);
    await page.click('.zf-actions button:has-text("Save")');
    await page.waitForSelector('.zf-result li.ok');
    assert.equal((await sent(page)).filter(x => x[0] === 'PLUGIN').pop()[2].AnnouncementRevision, 1);

    // Arrow keys move between tabs.
    await tab(page, 'look');
    await page.focus('.zf-tab[data-tab="look"]');
    await page.keyboard.press('ArrowLeft');
    assert.equal(await page.getAttribute('.zf-tab[data-tab="announce"]', 'aria-selected'), 'true');
    assert.deepEqual(errors, []);

    // Phone width: tabs scroll, fields stack, nothing overflows the page.
    const phone = await open(browser, { width: 390, height: 844 });
    const width = await phone.page.evaluate(() => document.documentElement.scrollWidth);
    assert.ok(width <= 390, `page is ${width}px wide on a phone`);
    if (shots) {
      await phone.page.screenshot({ path: path.join(shots, 'phone-overview.png'), fullPage: true });
      await tab(phone.page, 'connect');
      await phone.page.screenshot({ path: path.join(shots, 'phone-connect.png'), fullPage: true });
    }
    assert.deepEqual(phone.errors, []);
    console.log('config page browser: passed');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
