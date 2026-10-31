import { test, expect } from '@playwright/test';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import AxeBuilder from '@axe-core/playwright';
import type { Page } from '@playwright/test';
async function accessible(page: Page) {
  const results = await new AxeBuilder({ page })
    .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'])
    .analyze();
  expect(results.violations).toEqual([]);
}
test('setup, accounts, collections, diagnostics, stream ranges and persist resume', async ({
  page,
  browser,
}) => {
  test.setTimeout(180000);
  const data = process.env.E2E_DATA!;
  const media = process.env.E2E_MEDIA!;
  expect(
    data,
    'E2E_DATA must point to a fresh server data directory',
  ).toBeTruthy();
  expect(media).toBeTruthy();
  await page.emulateMedia({ colorScheme: 'light' });
  await page.goto('/');
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  await expect(
    page.getByRole('heading', { name: 'Set up your server' }),
  ).toBeVisible();
  await page
    .getByLabel('Setup token')
    .fill((await readFile(path.join(data, 'setup-token'), 'utf8')).trim());
  await accessible(page);
  await page.getByLabel('Username').fill('maintainer');
  await page
    .getByLabel('Password', { exact: true })
    .fill('test-only-long-password');
  await page.getByRole('button', { name: 'Create administrator' }).click();
  await expect(
    page.getByRole('heading', { name: 'Sign in', exact: true }),
  ).toBeVisible();
  await accessible(page);
  await page.getByLabel('Username').fill('maintainer');
  await page
    .getByLabel('Password', { exact: true })
    .fill('test-only-long-password');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await page.getByRole('link', { name: 'Settings', exact: true }).click();
  await expect(
    page.getByRole('heading', { name: 'Server settings' }),
  ).toBeVisible();
  await accessible(page);
  await page
    .getByRole('combobox', { name: 'Theme', exact: true })
    .selectOption('light');
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
  await page.reload();
  await expect(
    page.getByRole('combobox', { name: 'Theme', exact: true }),
  ).toHaveValue('light');
  await accessible(page);
  await page.screenshot({
    path: 'test-results/visual/theme-settings-light.png',
    fullPage: true,
  });
  await page
    .getByRole('combobox', { name: 'Theme', exact: true })
    .selectOption('dark');
  await page.getByLabel('Library name').fill('Test collection');
  await page.getByLabel('Directory on the server').fill(media);
  await page.getByRole('button', { name: 'Add library', exact: true }).click();
  await page
    .getByRole('button', { name: 'Scan library Test collection' })
    .click();
  await expect(page.getByRole('status')).toContainText('completed', {
    timeout: 30000,
  });
  await page.getByText(/Scan #.* problems/).click();
  await expect(
    page.getByText(/broken.mp4: FFprobe could not read this media file/),
  ).toBeVisible();
  await page.getByRole('link', { name: 'Library', exact: true }).click();
  await expect(
    page.getByRole('heading', { name: 'Media library', exact: true }),
  ).toBeVisible();
  await expect(
    page.getByRole('heading', { name: 'sample', exact: true }),
  ).toBeVisible();
  await accessible(page);
  await expect(
    page.getByRole('link', { name: 'Library', exact: true }),
  ).toHaveAttribute('aria-current', 'page');
  await expect(page.locator('#main-content')).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(
    page.getByLabel('Smart collection', { exact: true }),
  ).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(page.getByLabel('Search library')).toBeFocused();
  await page.getByLabel('Search library').fill('does-not-exist');
  await expect(page.getByRole('status', { name: 'Search results' })).toHaveText(
    '0 matching videos',
  );
  await expect(page.getByLabel('Search library')).toBeFocused();
  await page.getByLabel('Search library').fill('');
  await expect(
    page.getByRole('heading', { name: 'sample', exact: true }),
  ).toBeVisible();
  await page.getByRole('link', { name: 'Home', exact: true }).click();
  await expect(page.locator('.hero')).toBeVisible();
  await accessible(page);
  await page.screenshot({
    path: 'test-results/visual/home-desktop.png',
    fullPage: true,
  });
  await page.getByRole('link', { name: 'Library', exact: true }).click();
  await expect(
    page.getByRole('heading', { name: 'sample', exact: true }),
  ).toBeVisible();
  await page.screenshot({
    path: 'test-results/visual/media-library-desktop.png',
    fullPage: true,
  });
  await page.setViewportSize({ width: 390, height: 844 });
  await expect
    .poll(() =>
      page.evaluate(() => document.documentElement.scrollWidth <= innerWidth),
    )
    .toBe(true);
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await expect(page.locator('.media-card').first()).toHaveCSS(
    'animation-name',
    'none',
  );
  await accessible(page);
  await page.screenshot({
    path: 'test-results/visual/media-library-mobile.png',
    fullPage: true,
  });
  await page.setViewportSize({ width: 1280, height: 720 });
  await page.emulateMedia({ reducedMotion: 'no-preference' });
  const earlyStreams: string[] = [];
  page.on('request', (request) => {
    if (/\/media\/\d+\/stream|\/playback-sessions/.test(request.url()))
      earlyStreams.push(request.url());
  });
  await page.getByRole('link', { name: 'Settings', exact: true }).click();
  await page
    .getByRole('combobox', { name: 'Theme', exact: true })
    .selectOption('light');
  await page.getByRole('link', { name: 'Library', exact: true }).click();
  await accessible(page);
  await page.screenshot({
    path: 'test-results/visual/theme-library-light.png',
    fullPage: true,
  });
  await page.getByRole('heading', { name: 'sample', exact: true }).click();
  await expect(
    page.getByRole('region', { name: 'Title information' }),
  ).toBeVisible();
  await expect(page.locator('video')).toHaveCount(0);
  await accessible(page);
  await page.screenshot({
    path: 'test-results/visual/title-desktop.png',
    fullPage: true,
  });
  expect(earlyStreams).toHaveLength(0);
  await page.getByText('Private bookmarks and notes', { exact: true }).focus();
  await page.keyboard.press('Enter');
  await page
    .getByLabel('Bookmark name', { exact: true })
    .fill('Scene to revisit');
  await page.getByLabel('Timestamp in seconds', { exact: true }).fill('3');
  await page
    .getByLabel('Private note', { exact: true })
    .fill('Private scene note');
  await page.getByRole('button', { name: 'Add bookmark', exact: true }).click();
  await expect(
    page.getByRole('button', { name: 'Jump to Scene to revisit at 3 seconds' }),
  ).toBeVisible();
  await accessible(page);
  expect(earlyStreams).toHaveLength(0);
  await page
    .getByRole('button', { name: 'Play from beginning', exact: true })
    .click();
  const video = page.getByLabel('sample', { exact: true });
  await expect(video).toBeVisible();
  await accessible(page);
  await page.getByText('Playback diagnostics', { exact: true }).focus();
  await page.keyboard.press('Enter');
  await expect(page.getByText('Buffered ahead', { exact: true })).toBeVisible();
  await expect
    .poll(() => video.evaluate((v: HTMLVideoElement) => v.readyState))
    .toBeGreaterThanOrEqual(1);
  await expect(video).not.toHaveAttribute('controls');
  await video.evaluate((v: HTMLVideoElement) => v.pause());
  await page.getByText('Private bookmarks and notes', { exact: true }).click();
  const bookmarkJump = page.getByRole('button', {
    name: 'Jump to Scene to revisit at 3 seconds',
  });
  await bookmarkJump.focus();
  await page.keyboard.press('Enter');
  await expect
    .poll(() => video.evaluate((v: HTMLVideoElement) => v.currentTime))
    .toBeGreaterThanOrEqual(2.9);
  await page.getByRole('button', { name: 'Edit Scene to revisit' }).click();
  await expect(page.getByLabel('Bookmark name', { exact: true })).toBeFocused();
  await page.getByLabel('Bookmark name', { exact: true }).fill('Renamed scene');
  await page
    .getByRole('button', { name: 'Save bookmark', exact: true })
    .click();
  await expect(
    page.getByRole('button', { name: 'Delete Renamed scene' }),
  ).toBeVisible();
  await accessible(page);
  await page.getByRole('button', { name: 'Delete Renamed scene' }).click();
  await expect(
    page.getByRole('button', { name: 'Delete Renamed scene' }),
  ).toHaveCount(0);
  await video.evaluate((v: HTMLVideoElement) => v.pause());
  await expect(
    page.getByRole('button', { name: 'Play', exact: true }),
  ).toBeVisible();
  await page.getByRole('button', { name: 'Mute', exact: true }).click();
  await page.getByRole('button', { name: 'Play', exact: true }).click();
  await expect
    .poll(() => video.evaluate((v: HTMLVideoElement) => v.currentTime))
    .toBeGreaterThan(0);
  await page.getByRole('button', { name: 'Pause', exact: true }).click();
  await page.getByLabel('Seek', { exact: true }).focus();
  await page.keyboard.press('Home');
  for (let step = 0; step < 35; step++) await page.keyboard.press('ArrowRight');
  await page.getByRole('button', { name: 'Play', exact: true }).click();
  await page.getByRole('button', { name: 'Pause', exact: true }).click();
  await expect
    .poll(async () => {
      const response = await page.request.get('/api/progress');
      const values = await response.json();
      return values[0]?.seconds ?? 0;
    })
    .toBeGreaterThanOrEqual(3);
  const source = await video.getAttribute('src');
  const range = await page.request.get(source!, {
    headers: { Range: 'bytes=0-15' },
  });
  expect(range.status()).toBe(206);
  expect((await range.body()).length).toBe(16);
  // Use the real authenticated conversion API, then render the generated HLS.
  await page.getByText('Quality, audio and subtitles', { exact: true }).click();
  await expect(page.getByLabel('Audio track')).toBeVisible();
  await accessible(page);
  await page
    .getByRole('button', { name: 'Prepare HLS playback', exact: true })
    .click();
  await expect(page.getByText(/Conversion: ready/)).toBeVisible({
    timeout: 60000,
  });
  await expect(page.getByLabel('Playback quality')).toBeVisible();
  await page.getByLabel('Playback quality').selectOption('0');
  await page.getByRole('button', { name: 'Play', exact: true }).click();
  await expect
    .poll(() =>
      video.evaluate(
        (v: HTMLVideoElement) => v.getVideoPlaybackQuality().totalVideoFrames,
      ),
    )
    .toBeGreaterThan(0);
  await page.getByRole('button', { name: 'Pause', exact: true }).click();
  await page.getByLabel('Playback quality').selectOption('-1');
  await page.getByRole('button', { name: 'Direct Play', exact: true }).click();
  await page.reload();
  await expect(page.locator('video')).toHaveCount(0);
  await page.getByRole('button', { name: /^Resume from/ }).click();
  await expect
    .poll(() =>
      page
        .getByLabel('sample', { exact: true })
        .evaluate((v: HTMLVideoElement) => v.currentTime),
    )
    .toBeGreaterThanOrEqual(3);
  await page.getByRole('link', { name: 'Watch history', exact: true }).click();
  await expect(
    page.getByRole('link', { name: 'sample', exact: true }),
  ).toBeVisible();
  await page.getByRole('link', { name: 'Settings', exact: true }).click();
  await page.getByLabel('Collection name', { exact: true }).fill('Samples');
  await page.getByLabel('Title starts with', { exact: true }).fill('sample');
  await page
    .getByRole('button', { name: 'Save smart collection', exact: true })
    .click();
  // Navigation fetches the library's collection list. Wait for the confirmed
  // creation UI so that GET cannot overtake the still-pending POST.
  await expect(
    page.getByRole('button', {
      name: 'Delete collection Samples',
      exact: true,
    }),
  ).toBeVisible();
  await page.getByRole('link', { name: 'Library', exact: true }).click();
  await expect(
    page.getByRole('heading', { name: 'Media library', exact: true }),
  ).toBeVisible();
  await page
    .getByLabel('Smart collection', { exact: true })
    .selectOption({ label: 'Samples' });
  await expect(
    page.getByRole('heading', { name: 'sample', exact: true }),
  ).toBeVisible();
  await page.getByRole('heading', { name: 'sample', exact: true }).click();
  await page.getByText('Manage content rating', { exact: true }).click();
  await page.getByLabel('Content rating', { exact: true }).selectOption('10');
  await page.getByRole('link', { name: 'Settings', exact: true }).click();
  await page.getByLabel('New username', { exact: true }).fill('friend');
  await page
    .getByLabel('Temporary password', { exact: true })
    .fill('friend-fixture-password');
  await page
    .getByRole('button', { name: 'Create viewer account', exact: true })
    .click();
  await page.getByText('friend · Viewer', { exact: true }).click();
  await page.getByLabel('All libraries for friend', { exact: true }).check();
  await page
    .getByLabel('Maximum rating for friend', { exact: true })
    .selectOption('13');
  await page
    .getByRole('button', { name: 'Save permissions for friend', exact: true })
    .click();
  await expect(
    page.getByText(
      'friend: permissions saved; existing sessions were revoked.',
      { exact: true },
    ),
  ).toBeVisible();
  await accessible(page);
  await page
    .getByRole('button', { name: 'Sign out · maintainer', exact: true })
    .click();
  await page.getByLabel('Username', { exact: true }).fill('friend');
  await page
    .getByLabel('Password', { exact: true })
    .fill('friend-fixture-password');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(
    page.getByRole('heading', { name: 'sample', exact: true }),
  ).toBeVisible();
  expect((await page.request.get('/api/users')).status()).toBe(403);
  expect((await page.request.get('/api/monitoring')).status()).toBe(403);
  await page.getByRole('link', { name: 'Watch history', exact: true }).click();
  await expect(
    page.getByRole('heading', { name: 'Watch history', exact: true }),
  ).toBeVisible();
  await expect(
    page.getByRole('link', { name: 'sample', exact: true }),
  ).toHaveCount(0);
  await page.getByRole('link', { name: 'Settings', exact: true }).click();
  await expect(
    page.getByRole('heading', {
      name: 'Accounts and permissions',
      exact: true,
    }),
  ).toHaveCount(0);
  await accessible(page);
  await page
    .getByRole('button', { name: 'Sign out · friend', exact: true })
    .click();
  await page.getByLabel('Username', { exact: true }).fill('maintainer');
  await page
    .getByLabel('Password', { exact: true })
    .fill('test-only-long-password');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await page.getByRole('link', { name: 'Settings', exact: true }).click();
  await page
    .getByLabel('Show Watch history navigation', { exact: true })
    .uncheck();
  await expect(
    page.getByRole('link', { name: 'Watch history', exact: true }),
  ).toHaveCount(0);
  await page.reload();
  await expect(
    page.getByLabel('Show Watch history navigation', { exact: true }),
  ).not.toBeChecked();
  await expect(
    page.getByRole('link', { name: 'Watch history', exact: true }),
  ).toHaveCount(0);
  await page
    .getByRole('button', { name: 'Reset visibility to defaults', exact: true })
    .click();
  await expect(
    page.getByRole('link', { name: 'Watch history', exact: true }),
  ).toBeVisible();
  await page.getByText('Server-wide availability', { exact: true }).click();
  await page
    .getByRole('button', {
      name: 'Disable Smart collections for everyone',
      exact: true,
    })
    .click();
  await expect(
    page.getByLabel('Show Smart collections', { exact: true }),
  ).toBeDisabled();
  await page
    .locator('#settings-customize')
    .screenshot({ path: 'test-results/visual/customize-settings.png' });
  await accessible(page);
  await page.getByRole('link', { name: 'Library', exact: true }).click();
  await expect(page.getByLabel('Search library')).toBeVisible();
  await expect(
    page.getByLabel('Smart collection', { exact: true }),
  ).toHaveCount(0);
  await page.getByRole('link', { name: 'Settings', exact: true }).click();
  await page.getByText('Server-wide availability', { exact: true }).click();
  await page
    .getByRole('button', {
      name: 'Enable Smart collections for everyone',
      exact: true,
    })
    .click();
  await expect(
    page.getByRole('button', {
      name: 'Delete collection Samples',
      exact: true,
    }),
  ).toBeVisible();
  await page.getByRole('link', { name: 'Library', exact: true }).click();
  await expect(
    page.getByRole('heading', { name: 'Media library', exact: true }),
  ).toBeVisible();
  await page.getByRole('heading', { name: 'sample', exact: true }).click();
  const requestReturnHash = await page.evaluate(() => location.hash);
  await page.getByRole('link', { name: 'Settings', exact: true }).click();
  await page.getByText('Server-wide availability', { exact: true }).click();
  await page
    .getByRole('button', {
      name: 'Enable Movie requests for everyone',
      exact: true,
    })
    .click();
  await page.getByRole('link', { name: 'Movie requests', exact: true }).click();
  await page
    .getByLabel('Movie title', { exact: true })
    .fill('Family future film');
  await page
    .getByLabel('Note (optional)', { exact: true })
    .fill('For our next movie night');
  await page
    .getByRole('button', { name: 'Send movie request', exact: true })
    .click();
  await expect(
    page.getByText('Request sent. Your administrator can review it here.'),
  ).toBeVisible();
  await page
    .getByText('Review request: Family future film', { exact: true })
    .click();
  await page
    .getByRole('combobox', { name: 'Status', exact: true })
    .selectOption('Added');
  await expect(
    page.getByRole('combobox', { name: 'Confirm library title', exact: true }),
  ).toHaveValue('');
  await page
    .getByRole('combobox', { name: 'Confirm library title', exact: true })
    .selectOption({ label: 'sample' });
  await page
    .getByLabel('Reply', { exact: true })
    .fill('Manually confirmed for the synthetic test');
  await page
    .getByRole('button', {
      name: 'Save review for Family future film',
      exact: true,
    })
    .click();
  await expect(
    page.getByRole('link', { name: 'View added movie', exact: true }),
  ).toBeVisible();
  await accessible(page);
  await page.screenshot({
    path: 'test-results/visual/family-requests.png',
    fullPage: true,
  });
  await page.goto('/' + requestReturnHash);
  await expect(
    page.getByRole('region', { name: 'Title information' }),
  ).toBeVisible();
  const titleHash = await page.evaluate(() => location.hash);
  await page.getByRole('link', { name: 'Experimental', exact: true }).click();
  await expect(
    page.getByRole('heading', { name: 'Experimental', exact: true }),
  ).toBeVisible();
  await page.getByLabel('Test collection', { exact: true }).check();
  await page.getByLabel('Enable guest access', { exact: true }).check();
  await page
    .getByRole('button', { name: 'Save guest policy', exact: true })
    .click();
  await expect(page.getByRole('status')).toContainText('Guest policy saved');
  await accessible(page);
  await page.screenshot({
    path: 'test-results/visual/experimental-settings.png',
    fullPage: true,
  });
  const visitorContext = await browser.newContext({
    baseURL: 'http://127.0.0.1:5173',
  });
  try {
    const visitor = await visitorContext.newPage();
    await visitor.goto('/');
    await visitor
      .getByRole('button', { name: 'Continue as guest', exact: true })
      .click();
    await expect(
      visitor.getByRole('link', { name: 'Settings', exact: true }),
    ).toHaveCount(0);
    await expect(
      visitor.getByRole('link', { name: 'Watch history', exact: true }),
    ).toHaveCount(0);
    await visitor.getByRole('link', { name: 'Library', exact: true }).click();
    await visitor.getByRole('heading', { name: 'sample', exact: true }).click();
    await visitor
      .getByRole('button', { name: 'Play from beginning', exact: true })
      .click();
    await expect
      .poll(() =>
        visitor
          .locator('video')
          .evaluate((element: HTMLVideoElement) => element.currentTime),
      )
      .toBeGreaterThan(0.2);
    await expect(
      visitor.getByText(
        'Guest playback · Progress is not saved. Direct Play only.',
      ),
    ).toBeVisible();
    await page.getByLabel('Enable guest access', { exact: true }).uncheck();
    await page
      .getByRole('button', { name: 'Save guest policy', exact: true })
      .click();
    await expect(page.getByRole('status')).toContainText('Guest policy saved');
    await visitor.reload();
    await expect(
      visitor.getByRole('heading', { name: 'Sign in', exact: true }),
    ).toBeVisible();
    await expect(
      visitor.getByRole('button', { name: 'Continue as guest', exact: true }),
    ).toHaveCount(0);
  } finally {
    await visitorContext.close();
  }
  await page.goto('/' + titleHash);
  await expect(
    page.getByRole('region', { name: 'Title information' }),
  ).toBeVisible();
  // Missing media produces actionable diagnostics, not a leaked server pathname.
  const { unlink } = await import('node:fs/promises');
  await unlink(path.join(media, 'sample.webm'));
  await page.reload();
  await expect(page.locator('video')).toHaveCount(0);
  await page
    .getByRole('button', { name: 'Play from beginning', exact: true })
    .click();
  await expect(page.getByRole('alert')).toContainText(
    'server cannot read this media file',
  );
  await expect(page.getByText(/Diagnostic reference:/)).toBeVisible();
  await accessible(page);
});
