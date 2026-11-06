'use strict';
// The Zorofin activity page in the Dashboard: charts, lists, the play log,
// filters, CSV download, settings and deleting history.
// Run: node server/jellyfin-plugin-zorofin/tests/web/activity-page-browser.test.cjs
// With --shots <dir> it also saves desktop and phone screenshots.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const PAGE = fs.readFileSync(path.join(__dirname, '../../src/Jellyfin.Plugin.Zorofin/Web/activityPage.html'), 'utf8');
const shots = process.argv.includes('--shots') ? process.argv[process.argv.indexOf('--shots') + 1] : null;

let playwright;
try { playwright = require('playwright'); } catch (_) { console.log('playwright not installed: skipped'); process.exit(0); }

const ANA = 'a'.repeat(32), BEN = 'b'.repeat(32);
const day = i => { const d = new Date(Date.UTC(2026, 9, 6 + i)); return d.toISOString().slice(0, 10); };
const SUMMARY = {
  totals: { plays: 214, seconds: 512340, users: 6, titles: 58, completed: 120, transcoded: 31 },
  days: Array.from({ length: 30 }, (_, i) => ({ date: day(i), plays: (i * 7) % 13, seconds: ((i * 7919) % 26000) + (i % 6 === 0 ? 0 : 4000) })),
  heatmap: Array.from({ length: 7 }, (_, w) => Array.from({ length: 24 }, (_, h) => (h >= 18 && h <= 23 ? 9000 : h >= 12 ? 2500 : 200) * (w === 5 || w === 6 ? 1.6 : 1))),
  users: [{ id: ANA, name: 'ana', plays: 120, seconds: 300000 }, { id: BEN, name: 'ben', plays: 94, seconds: 212340 }],
  types: [{ name: 'Episode', plays: 150, seconds: 300000 }, { name: 'Movie', plays: 50, seconds: 200000 }, { name: 'TvChannel', plays: 14, seconds: 12340 }],
  clients: [{ name: 'Zorofin', plays: 120, seconds: 300000 }, { name: 'Jellyfin Web', plays: 94, seconds: 212340 }],
  devices: [{ name: 'Living room Apple TV', plays: 100, seconds: 280000 }, { name: 'iPhone', plays: 114, seconds: 232340 }],
  methods: [{ name: 'DirectPlay', plays: 160, seconds: 400000 }, { name: 'Transcode', plays: 31, seconds: 80000 }, { name: 'DirectStream', plays: 23, seconds: 32340 }],
  movies: [{ id: '1', name: 'A Very Long Movie Title That Should Be Cut Off Cleanly On Phones', plays: 4, seconds: 30000, viewers: 3 }, { id: '2', name: 'Short One', plays: 1, seconds: 5400, viewers: 1 }],
  shows: [{ id: '3', name: 'The Show', plays: 40, seconds: 90000, viewers: 4 }, { id: '4', name: 'Other Show', plays: 12, seconds: 30000, viewers: 1 }],
  channels: [{ id: '5', name: 'ESPN', plays: 14, seconds: 12340, viewers: 2 }],
};
const PLAYS = Array.from({ length: 120 }, (_, i) => ({
  id: 'p' + i, started: new Date(Date.UTC(2026, 10, 4, 22) - i * 3600e3).toISOString(), seconds: 1500 + i * 13,
  userId: i % 2 ? BEN : ANA, userName: i % 2 ? 'ben' : 'ana', itemId: 'i' + i, itemType: i % 3 ? 'Episode' : 'Movie',
  itemName: i % 3 ? 'Episode ' + i : 'Movie ' + i, seriesName: i % 3 ? 'The Show' : null, season: i % 3 ? 1 : null, episode: i % 3 ? i : null,
  client: 'Zorofin', device: 'Apple TV', method: i % 5 ? 'DirectPlay' : 'Transcode', transcode: i % 5 ? null : 'video', completed: i % 4 === 0,
}));

const stubs = ([summary, plays]) => {
  window.sent = [];
  window.confirm = () => true;
  const config = { PlayHistoryEnabled: false, PlayHistoryRetentionDays: 365, PlayStatsEnabled: true };
  window.Dashboard = { showLoadingMsg() {}, hideLoadingMsg() {}, alert(m) { window.sent.push(['ALERT', m]); } };
  window.ApiClient = {
    getPluginConfiguration: async () => JSON.parse(JSON.stringify(config)),
    updatePluginConfiguration: async (id, c) => { window.sent.push(['PLUGIN', id, c]); Object.assign(config, c); return {}; },
    getUrl: p => '/' + p,
    ajax: async ({ type, url, dataType }) => {
      window.sent.push([type, url]);
      const u = new URL(url, 'http://x');
      const q = Object.fromEntries(u.searchParams);
      if (type === 'DELETE') return { deleted: q.userId ? 60 : 120 };
      if (u.pathname.endsWith('/Status')) return { enabled: config.PlayHistoryEnabled, plays: 2140, first: '2025-11-20T10:00:00Z', imported: 1800 };
      if (u.pathname.endsWith('/Summary')) return summary;
      if (u.pathname.endsWith('/Export')) { if (dataType !== 'text') throw new Error('csv should be text'); return 'Started (UTC),User\n'; }
      if (u.pathname.endsWith('/Log')) {
        const list = plays.filter(p => (!q.userId || p.userId === q.userId) && (!q.q || (p.itemName + p.seriesName).toLowerCase().includes(q.q.toLowerCase())));
        const skip = Number(q.skip), take = Number(q.take);
        return { total: list.length, items: list.slice(skip, skip + take) };
      }
      throw new Error('unexpected ' + url);
    },
  };
};

async function open(browser, viewport) {
  const page = await browser.newPage({ viewport });
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.route('**/*', route => route.abort());
  const html = PAGE.replace('<head>', `<head><style>body{margin:0;background:#101311}</style><script>(${stubs})(${JSON.stringify([SUMMARY, PLAYS])});</script>`);
  await page.setContent(html);
  await page.evaluate(() => document.querySelector('#ZorofinActivityPage').dispatchEvent(new Event('pageshow')));
  await page.waitForSelector('#zfLog tr td');
  await page.waitForFunction(() => document.querySelector('#zfStored').textContent.length > 0);
  return { page, errors };
}
const sent = page => page.evaluate(() => window.sent);
const lastGet = async (page, route) => (await sent(page)).filter(x => x[0] === 'GET' && x[1].includes(route)).pop()[1];

(async () => {
  const browser = await playwright.chromium.launch({ headless: true });
  try {
    const { page, errors } = await open(browser, { width: 1280, height: 900 });

    // Tiles, chart and lists.
    assert.equal(await page.textContent('#zfTime'), '142h');
    assert.equal(await page.textContent('#zfPlays'), '214');
    assert.equal(await page.locator('#zfDays rect.bar').count(), 30);
    assert.equal(await page.locator('#zfHeat i').count(), 7 * 24);
    assert.deepEqual(await page.$$eval('#zfHeat > span', n => n.slice(25).map(s => s.textContent)), ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun']);
    assert.match(await page.textContent('#zfShows li'), /The Show.*25h 0m · 40 plays · 4 viewers/);
    assert.match(await page.textContent('#zfTypes'), /Episodes.*Movies.*Live TV/);
    assert.match(await page.textContent('#zfMethods'), /Direct play.*Transcode.*Direct stream/);
    assert.equal(await page.isVisible('#zfChannelsCard'), true);
    await page.hover('#zfDays rect.bar >> nth=1');
    assert.match(await page.textContent('#zfReadout'), /: \d+h \d+m, \d+ plays?/);

    // History off: the banner shows and settings say so.
    assert.equal(await page.isVisible('#zfOff'), true);
    assert.equal(await page.isChecked('#zfOn'), false);
    assert.equal(await page.inputValue('#zfKeep'), '365');
    assert.match(await page.textContent('#zfStored'), /2,140 plays stored.*1,800 brought in from Playback Reporting/);

    // Log: paging and text search.
    assert.equal(await page.locator('#zfLog tr').count(), 25);
    assert.equal(await page.textContent('#zfLogCount'), '1–25 of 120');
    assert.equal(await page.isDisabled('#zfPrev'), true);
    await page.click('#zfNext');
    await page.waitForFunction(() => document.querySelector('#zfLogCount').textContent.startsWith('26'));
    assert.match(await lastGet(page, '/Log'), /skip=25&take=25/);
    if (shots) await page.screenshot({ path: path.join(shots, 'activity-desktop.png'), fullPage: true });
    await page.fill('#zfSearch', 'movie 9');
    await page.waitForFunction(() => /of [0-9]$/.test(document.querySelector('#zfLogCount').textContent));
    assert.match(await lastGet(page, '/Log'), /skip=0&take=25&q=movie%209/);

    // Range and filters reach every request; picking a viewer narrows to them.
    await page.click('#zfRange button[data-days="7"]');
    await page.waitForFunction(() => window.sent.some(x => x[1].includes('/Summary') && x[1].includes('days=7')));
    assert.equal(await page.getAttribute('#zfRange button[data-days="7"]', 'aria-pressed'), 'true');
    assert.deepEqual(await page.$$eval('#zfUser option', n => n.map(o => o.textContent)), ['Everyone', 'ana', 'ben']);
    await page.click('#zfUsers button.name >> text=ben');
    await page.waitForFunction(b => window.sent.some(x => x[1].includes('/Summary') && x[1].includes('userId=' + b)), BEN);
    assert.equal(await page.inputValue('#zfUser'), BEN);
    assert.equal(await page.isVisible('#zfDeleteUser'), true);
    assert.match(await lastGet(page, '/Summary'), /offset=-?\d+/);

    // CSV download is fetched as text with the same filters.
    const download = page.waitForEvent('download');
    await page.click('#zfExport');
    assert.match((await download).suggestedFilename(), /^zorofin-activity-\d{4}-\d{2}-\d{2}\.csv$/);
    assert.match(await lastGet(page, '/Export'), new RegExp('userId=' + BEN));

    // Settings save both values; deleting a viewer's history names them.
    await page.check('#zfOn');
    await page.selectOption('#zfKeep', '90');
    await page.click('#zfSave');
    await page.waitForFunction(() => document.querySelector('#zfOff').hidden);
    const saved = (await sent(page)).filter(x => x[0] === 'PLUGIN').pop()[2];
    assert.equal(saved.PlayHistoryEnabled, true);
    assert.equal(saved.PlayHistoryRetentionDays, 90);
    assert.equal(saved.PlayStatsEnabled, true, 'other settings are kept');
    await page.click('#zfDeleteUser');
    await page.waitForFunction(() => /60 plays deleted/.test(document.querySelector('#zfSaved').textContent));
    assert.ok((await sent(page)).some(x => x[0] === 'DELETE' && x[1] === '/Zorofin/Admin/Activity?userId=' + BEN));
    assert.equal(await page.inputValue('#zfUser'), '');
    assert.deepEqual(errors, []);

    // Phone: no sideways scrolling.
    const phone = await open(browser, { width: 390, height: 844 });
    assert.equal(await phone.page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true, 'no horizontal scroll on a phone');
    if (shots) await phone.page.screenshot({ path: path.join(shots, 'activity-phone.png'), fullPage: true });
    assert.deepEqual(phone.errors, []);
    console.log('activity page: all checks passed');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exit(1); });
