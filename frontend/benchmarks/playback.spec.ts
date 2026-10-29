import { test, expect, type Page } from '@playwright/test';
import { readFile, mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import AxeBuilder from '@axe-core/playwright';

async function connect(page: Page) {
  await page.goto('/');
  await expect(
    page.getByRole('heading', { name: /Set up your server|Sign in/ }),
  ).toBeVisible();
  if (await page.getByLabel('Setup token').isVisible()) {
    await page
      .getByLabel('Setup token')
      .fill(
        (
          await readFile(
            path.join(process.env.BENCHMARK_DATA!, 'setup-token'),
            'utf8',
          )
        ).trim(),
      );
    await page.getByLabel('Username').fill('benchmark');
    await page
      .getByLabel('Password', { exact: true })
      .fill('synthetic-only-password');
    await page.getByRole('button', { name: 'Create administrator' }).click();
    await expect(
      page.getByRole('heading', { name: 'Sign in', exact: true }),
    ).toBeVisible();
  }
  await page.getByLabel('Username').fill('benchmark');
  await page
    .getByLabel('Password', { exact: true })
    .fill('synthetic-only-password');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(
    page.getByRole('link', { name: 'Settings', exact: true }),
  ).toBeVisible();
  const csrf = async () =>
    (await (await page.request.get('/api/auth/csrf')).json()).token as string;
  let libraries = await (await page.request.get('/api/libraries')).json();
  if (libraries.length === 0) {
    const created = await page.request.post('/api/libraries', {
      headers: { 'X-CSRF-TOKEN': await csrf() },
      data: { name: 'Synthetic validation', path: process.env.BENCHMARK_MEDIA },
    });
    expect(created.ok()).toBeTruthy();
    libraries = [await created.json()];
    const scan = await page.request.post(
      `/api/libraries/${libraries[0].id}/scan`,
      { headers: { 'X-CSRF-TOKEN': await csrf() } },
    );
    expect(scan.status()).toBe(202);
    const { id } = await scan.json();
    await expect
      .poll(
        async () =>
          (await (await page.request.get(`/api/scans/${id}`)).json()).state,
        { timeout: 45000 },
      )
      .toBe('completed');
  }
  const response = await page.request.get('/api/media');
  expect(response.ok()).toBeTruthy();
  return (await response.json()) as { id: number; title: string }[];
}
async function report(name: string, value: unknown) {
  await mkdir('benchmark-results', { recursive: true });
  await writeFile(
    `benchmark-results/${name}.json`,
    JSON.stringify(value, null, 2) + '\n',
  );
}

test('measure bandwidth adaptation, rebuffering and accessible track selection', async ({
  page,
  browserName,
}) => {
  test.skip(
    browserName !== 'chromium',
    'CDP network throughput shaping is Chromium-specific.',
  );
  const items = await connect(page);
  const item = items.find((x) => x.title === 'bandwidth')!;
  expect(item).toBeTruthy();
  await page.goto(`/#media/${item.id}`);
  const video = page.getByLabel('bandwidth', { exact: true });
  await expect(video).toBeVisible();
  await page.getByText('Quality, audio and subtitles', { exact: true }).click();
  const subtitles = page.getByLabel('Subtitles', { exact: true });
  await expect(subtitles).toContainText('swe');
  await expect(subtitles).toContainText('eng');
  await subtitles.focus();
  await page.keyboard.press('End');
  expect(await subtitles.inputValue()).not.toBe('');
  const results = await new AxeBuilder({ page })
    .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'])
    .analyze();
  expect(results.violations).toEqual([]);
  await page
    .getByRole('button', { name: 'Prepare HLS playback', exact: true })
    .click();
  await expect(page.getByText(/Conversion: ready/)).toBeVisible({
    timeout: 90000,
  });
  const client = await page.context().newCDPSession(page);
  await client.send('Network.enable');
  await client.send('Network.setCacheDisabled', { cacheDisabled: true });
  const shape = async (downloadThroughput: number) =>
    client.send('Network.emulateNetworkConditions', {
      offline: false,
      latency: 40,
      downloadThroughput,
      uploadThroughput: 1000000,
    });
  const observations: {
    time: number;
    height: number;
    switches: number;
    stalls: number;
    stallSeconds: number;
    estimatedKbps: number;
    position: number;
  }[] = [];
  const started = Date.now();
  const sample = async () => {
    const metrics = await page
      .getByLabel('Adaptive playback diagnostics')
      .evaluate((el) => ({
        height: Number(el.getAttribute('data-height')),
        switches: Number(el.getAttribute('data-switches')),
        stalls: Number(el.getAttribute('data-stalls')),
        stallSeconds: Number(el.getAttribute('data-stall-seconds')),
        estimatedKbps: Number(el.getAttribute('data-estimated-kbps')),
      }));
    const position = await video.evaluate(
      (el: HTMLVideoElement) => el.currentTime,
    );
    observations.push({
      time: (Date.now() - started) / 1000,
      ...metrics,
      position,
    });
    return metrics;
  };
  try {
    await shape(800000);
    await page.getByLabel('Playback quality').selectOption('2');
    await page.getByRole('button', { name: 'Mute', exact: true }).click();
    await page.getByRole('button', { name: 'Play', exact: true }).click();
    await expect
      .poll(async () => (await sample()).height, { timeout: 20000 })
      .toBe(720);
    await expect
      .poll(
        () =>
          video.evaluate((el: HTMLVideoElement) =>
            Array.from(el.textTracks)
              .flatMap((track) =>
                Array.from(track.cues ?? []).map((cue) => (cue as VTTCue).text),
              )
              .join(' '),
          ),
        { timeout: 15000 },
      )
      .toContain('synthetic caption');
    await page.waitForTimeout(4000); // Deliberate fixed high-bandwidth measurement window.
    const atDrop = await sample();
    const dropAt = (Date.now() - started) / 1000;
    await shape(140000);
    await page.getByLabel('Playback quality').selectOption('-1');
    await expect
      .poll(async () => (await sample()).height, {
        timeout: 65000,
        intervals: [1000],
      })
      .toBe(360);
    const adapted = (Date.now() - started) / 1000;
    const before = await video.evaluate(
      (el: HTMLVideoElement) => el.currentTime,
    );
    await expect
      .poll(() => video.evaluate((el: HTMLVideoElement) => el.currentTime), {
        timeout: 15000,
      })
      .toBeGreaterThan(before + 1);
    await page.getByRole('button', { name: 'Pause', exact: true }).click();
    const final = await sample();
    expect(final.switches).toBeGreaterThan(0);
    await report('chromium-network', {
      status: 'passed',
      browser: browserName,
      platform: process.platform,
      shaping: {
        highBytesPerSecond: 800000,
        lowBytesPerSecond: 140000,
        latencyMs: 40,
      },
      dropAtSeconds: dropAt,
      adaptationSeconds: adapted - dropAt,
      afterDrop: {
        stalls: final.stalls - atDrop.stalls,
        stallSeconds: Math.max(0, final.stallSeconds - atDrop.stallSeconds),
      },
      final,
      observations,
      note: 'Synthetic complete-VOD HLS; CDP download throughput shaping. Not physical-network/device or progressive-startup evidence.',
    });
  } catch (error) {
    await report('chromium-network', {
      status: 'failed',
      observations,
      error: error instanceof Error ? error.name : 'unknown',
    });
    throw error;
  } finally {
    await shape(-1);
    await client.detach();
  }
});

// Separate contexts exercise decoder startup twice without retrying or hiding failures.
for (const attempt of [1, 2]) {
  test(`record actual AV1 and HEVC decoding (probe ${attempt})`, async ({
    page,
    browserName,
    browser,
  }) => {
    const items = await connect(page);
    const matrix: Record<string, unknown>[] = [];
    const save = () =>
      report(`${browserName}-codecs-${attempt}`, {
        browser: browserName,
        version: browser.version(),
        platform: process.platform,
        attempt,
        matrix,
        note: 'Headless decoder evidence on the recorded platform only. Advertised support is not actual decode proof. No physical HDR display or color-fidelity claim.',
      });
    for (const [title, type] of [
      ['h264', 'video/mp4; codecs="avc1.42C01E"'],
      ['hevc', 'video/mp4; codecs="hvc1.2.4.L60.B0"'],
      ['av1', 'video/mp4; codecs="av01.0.00M.10"'],
    ]) {
      const item = items.find((x) => x.title === title)!;
      expect(item).toBeTruthy();
      const probe = page.evaluate(
        async ({ id, type }) => {
          const video = document.createElement('video');
          video.muted = true;
          video.playsInline = true;
          video.style.cssText =
            'position:fixed;top:0;left:0;width:320px;height:180px;z-index:9999';
          video.preload = 'auto';
          document.body.append(video);
          // Let WebKit observe an actually painted, visible renderer before creating its media pipeline.
          await new Promise<void>((resolve) =>
            requestAnimationFrame(() => requestAnimationFrame(() => resolve())),
          );
          const advertised = video.canPlayType(type);
          const highDynamicRange = matchMedia('(dynamic-range: high)').matches;
          const events: {
            event: string;
            position: number;
            readyState: number;
          }[] = [];
          let outcome = 'timeout';
          let code = 0;
          let presentedFrames = 0;
          let frameCallback: number | undefined;
          let deadline: number | undefined;
          let done = false;
          const decodedFrames = () =>
            video.getVideoPlaybackQuality?.().totalVideoFrames ?? 0;
          try {
            await new Promise<void>((resolve) => {
              const finish = (value: string) => {
                if (!done) {
                  done = true;
                  outcome = value;
                  resolve();
                }
              };
              const check = () => {
                if (
                  video.currentTime > 0.1 &&
                  video.videoWidth > 0 &&
                  (decodedFrames() > 0 || presentedFrames > 0)
                )
                  finish('rendered');
              };
              for (const event of [
                'loadedmetadata',
                'loadeddata',
                'playing',
                'waiting',
                'stalled',
                'timeupdate',
                'ended',
              ]) {
                video.addEventListener(event, () => {
                  if (events.length < 24)
                    events.push({
                      event,
                      position: video.currentTime,
                      readyState: video.readyState,
                    });
                  check();
                });
              }
              video.addEventListener(
                'error',
                () => {
                  code = video.error?.code ?? 0;
                  finish(
                    code === 3 || code === 4 ? 'unsupported' : 'media-error',
                  );
                },
                { once: true },
              );
              if (typeof video.requestVideoFrameCallback === 'function') {
                const frame = () => {
                  presentedFrames++;
                  check();
                  if (!done)
                    frameCallback = video.requestVideoFrameCallback(frame);
                };
                frameCallback = video.requestVideoFrameCallback(frame);
              }
              deadline = window.setTimeout(() => finish('timeout'), 30000);
              video.addEventListener(
                'click',
                () => {
                  // Start loading in the gesture too: unsupported codecs can otherwise
                  // finish and remove the element before Playwright delivers its click.
                  video.src = `/api/v1/media/${id}/stream`;
                  video.load();
                  void video.play().catch(() => {
                    code = video.error?.code ?? 0;
                    finish(
                      code === 3 || code === 4
                        ? 'unsupported'
                        : 'play-rejected',
                    );
                  });
                },
                { once: true },
              );
              // Expose the click target only after its handler and diagnostics are ready.
              video.dataset.testid = 'codec-probe';
            });
            return {
              advertised,
              outcome,
              errorCode: code,
              highDynamicRange,
              frames: decodedFrames(),
              presentedFrames,
              position: video.currentTime,
              width: video.videoWidth,
              height: video.videoHeight,
              paused: video.paused,
              readyState: video.readyState,
              networkState: video.networkState,
              events,
            };
          } finally {
            if (deadline !== undefined) clearTimeout(deadline);
            if (frameCallback !== undefined)
              video.cancelVideoFrameCallback(frameCallback);
            video.pause();
            video.removeAttribute('src');
            video.load();
            video.remove();
          }
        },
        { id: item.id, type },
      );
      const [result] = await Promise.all([
        probe,
        page.getByTestId('codec-probe').click({ timeout: 5000 }),
      ]);
      matrix.push({ codec: title, ...result });
      // Preserve diagnostics before asserting, including baseline timeouts/network errors.
      await save();
      if (title === 'h264') expect(result.outcome).toBe('rendered');
      else expect(['rendered', 'unsupported']).toContain(result.outcome);
      if (result.outcome === 'unsupported')
        expect([3, 4]).toContain(result.errorCode);
    }
  });
}
