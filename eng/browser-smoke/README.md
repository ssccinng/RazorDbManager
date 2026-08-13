# Browser smoke

The browser smoke requires a running RazorDbManager sample backed by a reachable,
seeded MySQL or MariaDB database. The database must contain at least one schema
and one table. A database error or unavailable empty state fails the run.

Install the repository-local Playwright dependency and its Chromium binary:

```shell
npm ci
npm run install-browser
```

Run the sample separately, then execute:

```shell
npm run smoke -- http://127.0.0.1:5173
```

`PLAYWRIGHT_MODULE_PATH` can point to a Playwright package or `node_modules`
directory. `BROWSER_EXECUTABLE_PATH` can select an installed Chromium-family
browser. The script otherwise uses Playwright-managed Chromium, then retains the
existing Codex runtime and Microsoft Edge paths as local fallbacks.

CI runs this test in a dedicated MySQL-backed job with a seeded read-only
fixture. It validates both desktop and mobile layouts, live reader health,
object selection, data browsing, sorting, exact counts, structure metadata,
keyboard tab navigation, and mobile drawer focus behavior without changing
database data.
