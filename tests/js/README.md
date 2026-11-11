# StreamKeepr JS tests

Dev-only package for ESLint and Vitest (Node 20.12+). Node is not part of the runtime
image and this package is never restored during application startup or publish.

```sh
npm ci
npm run lint
npm test
```

`npm run lint` uses the repository-level `eslint.config.mjs` so it can cover
`tests/js`, `src/StreamKeepr/wwwroot/js`, and the collocated
`src/StreamKeepr/Components/**/*.razor.js` modules. The config lives at the root
because ESLint only lints files inside its config directory; the dependencies
stay in this package so the runtime project never sees Node.

Vitest runs with jsdom so cookie and time-zone helpers exercise the same browser
API the app uses.
