import { defineConfig, devices } from '@playwright/test';
export default defineConfig({
  testDir: './benchmarks',
  outputDir: './benchmark-results/traces',
  workers: 1,
  timeout: 180000,
  use: {
    baseURL: 'http://127.0.0.1:5173',
    trace: 'retain-on-failure',
    launchOptions: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE
      ? { executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE }
      : undefined,
  },
  projects: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE
    ? [{ name: 'chromium', use: { browserName: 'chromium' } }]
    : [
        { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
        { name: 'firefox', use: { ...devices['Desktop Firefox'] } },
        { name: 'webkit', use: { ...devices['Desktop Safari'] } },
      ],
  webServer: {
    command: 'npm run dev',
    url: 'http://127.0.0.1:5173',
    reuseExistingServer: false,
  },
});
