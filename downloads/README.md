# Categorized downloads

The public [Downloads catalog](https://github.com/Splortch/Media-server/blob/downloads/README.md) is the platform-first entry point. GitHub's native Releases tab remains a chronological archive; no repository setting can turn it into this custom category view.

- **Servers:** Windows x64, Linux x64 and Docker (Linux amd64).
- **Clients:** Windows desktop x64 and Android phone.
- The latest usable build stays visible for every platform. Older releases are collapsed within their own platform instead of filling the main page.
- The browser UI comes with server packages. Android TV/iOS are labeled planned rather than given fake downloads.

## How updates work

`Download catalog` runs after successful main release/Android workflows, changes to the catalog source, nightly, or by manual dispatch. It paginates the actual published GitHub release inventory, excludes drafts/source-only placeholders, sorts versions numerically and generates a GitHub Markdown index plus a static HTML view.

After tests pass, a write-permission job updates the separate **downloads** branch. It does not commit to main, delete releases, alter release assets, change version numbers or publish a stable release. A marker prevents overwriting an unrelated branch, stale main builds skip publication, and concurrent updates cannot force-push over newer content. The generated branch is output: edit these source files, not its generated index.

The catalog works directly on GitHub without enabling Pages or using a cloud account. The HTML/CSS/JS preview is included in the `categorized-downloads` workflow artifact. It is not automatically deployed to GitHub Pages; this repository currently has no Pages site. A hosted HTML site can be configured later without changing the catalog model.

## Local development and validation

Requirements: Node.js 24, the existing frontend's locked test dependencies, and Playwright Chromium for browser tests.

```sh
node --test scripts/tests/download-catalog.test.cjs scripts/tests/platform-downloads.test.cjs
npm ci --prefix frontend
npx --prefix frontend playwright install chromium
node --test scripts/tests/download-catalog.browser.cjs
```

To build a real snapshot, save the paginated GitHub REST `/repos/Splortch/Media-server/releases` array to an ignored file (do not save credentials), then run:

```sh
node scripts/build-download-catalog.cjs artifacts/releases.json artifacts/downloads
python3 -m http.server 8080 --directory artifacts/downloads
```

Open `http://127.0.0.1:8080`. The static page has no external scripts, fonts, analytics, runtime API token or backend dependency. API retrieval happens in CI; a failed/empty retrieval does not replace the public index with an empty page. Download links are restricted to this repository's GitHub release assets.

Tests cover fixed categories, numeric latest/history ordering, rejected/mismatched download URLs, drafts/unsigned-only placeholders, collapsed history, desktop/mobile category filters, keyboard operation, horizontal overflow, automated accessibility checks and publication restricted to the generated branch. Synthetic fixtures are used only in tests; published catalogs use real GitHub release data.
