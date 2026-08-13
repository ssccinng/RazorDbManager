#!/usr/bin/env node

"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const helpRequested = process.argv.includes("--help") || process.argv.includes("-h");
if (helpRequested) {
  console.log([
    "Usage: node eng/browser-smoke.cjs [base-url]",
    "",
    "Environment variables:",
    "  BASE_URL                        Used when no positional URL is supplied.",
    "  PLAYWRIGHT_MODULE_PATH          Path to the playwright package or its node_modules directory.",
    "  CODEX_NODE_MODULES              Directory containing the bundled playwright package (fallback).",
    "  BROWSER_EXECUTABLE_PATH         Path to a Chromium-family browser executable.",
    "  EDGE_EXECUTABLE_PATH            Legacy alias for BROWSER_EXECUTABLE_PATH.",
    "  BROWSER_SMOKE_OUTPUT            Screenshot and report directory.",
  ].join("\n"));
  process.exit(0);
}

const baseUrl = normalizeBaseUrl(process.argv[2] || process.env.BASE_URL || "http://127.0.0.1:5173");
const outputDirectory = path.resolve(
  process.env.BROWSER_SMOKE_OUTPUT || path.join(process.cwd(), "artifacts", "browser-smoke"));
const playwright = loadPlaywright();
const browserExecutable = findBrowserExecutable();
const { chromium } = playwright.module;

const report = {
  baseUrl,
  playwrightModule: playwright.path,
  browserExecutable: browserExecutable || "playwright-managed Chromium",
  startedAtUtc: new Date().toISOString(),
  infrastructure: {},
  scenarios: [],
  passed: false,
};

fs.mkdirSync(outputDirectory, { recursive: true });

let browser;
let failure;

(async () => {
  try {
    browser = await chromium.launch({
      ...(browserExecutable ? { executablePath: browserExecutable } : {}),
      headless: true,
      args: ["--disable-dev-shm-usage"],
    });

    await verifyInfrastructure(browser);
    await verifyViewport(browser, {
      name: "desktop",
      viewport: { width: 1440, height: 900 },
      isMobile: false,
    });
    await verifyViewport(browser, {
      name: "mobile",
      viewport: { width: 390, height: 844 },
      isMobile: true,
    });

    report.passed = true;
  } catch (error) {
    failure = error;
    report.error = error instanceof Error ? error.stack : String(error);
    process.exitCode = 1;
  } finally {
    if (browser) await browser.close();
    report.completedAtUtc = new Date().toISOString();
    const reportPath = path.join(outputDirectory, "report.json");
    fs.writeFileSync(reportPath, `${JSON.stringify(report, null, 2)}\n`, "utf8");

    if (failure) {
      console.error(`Browser smoke failed: ${failure.message || failure}`);
      console.error(`Report: ${reportPath}`);
    } else {
      console.log(`Browser smoke passed for ${baseUrl}`);
      console.log(`Artifacts: ${outputDirectory}`);
    }
  }
})();

async function verifyInfrastructure(browserInstance) {
  const request = await browserInstance.newContext({
    baseURL: baseUrl,
    ignoreHTTPSErrors: true,
  });

  try {
    const staticAsset = await request.request.get("/_content/RazorDbManager/razordbmanager.js");
    const staticAssetBody = await staticAsset.body();
    assert.equal(staticAsset.status(), 200, "RazorDbManager JavaScript asset must return HTTP 200");
    assert.ok(staticAssetBody.byteLength > 1_000, "RazorDbManager JavaScript asset is unexpectedly small");

    const statusResponse = await request.request.get("/_razor-db-manager/status");
    assert.equal(statusResponse.status(), 200, "RazorDbManager status endpoint must return HTTP 200");
    const status = await statusResponse.json();
    assert.ok(
      status.status === "ready" || status.status === "degraded",
      "RazorDbManager status endpoint must report ready or degraded");
    assert.ok(Array.isArray(status.databases), "RazorDbManager status response must include databases");
    assert.ok(status.databases.length > 0, "RazorDbManager status response must include a registered database");
    for (const database of status.databases) {
      assert.ok(
        Array.isArray(database.diagnostics)
          && database.diagnostics.some(diagnostic => diagnostic.code === "reader-connection-ready"),
        `Database ${database.id ?? "(unknown)"} must pass its live reader connection probe`);
    }

    report.infrastructure = {
      staticAsset: {
        status: staticAsset.status(),
        contentType: staticAsset.headers()["content-type"],
        bytes: staticAssetBody.byteLength,
      },
      statusEndpoint: {
        status: statusResponse.status(),
        payload: status,
      },
    };
  } finally {
    await request.close();
  }
}

async function verifyViewport(browserInstance, scenario) {
  const context = await browserInstance.newContext({
    baseURL: baseUrl,
    viewport: scenario.viewport,
    screen: scenario.viewport,
    isMobile: scenario.isMobile,
    hasTouch: scenario.isMobile,
    locale: "zh-CN",
    colorScheme: "light",
    reducedMotion: "reduce",
    ignoreHTTPSErrors: true,
  });
  const page = await context.newPage();
  const consoleErrors = [];
  const pageErrors = [];
  const requestFailures = [];
  const errorResponses = [];
  const scenarioReport = {
    name: scenario.name,
    viewport: scenario.viewport,
    consoleErrors,
    pageErrors,
    requestFailures,
    errorResponses,
  };
  report.scenarios.push(scenarioReport);

  page.on("console", message => {
    if (message.type() === "error") consoleErrors.push(message.text());
  });
  page.on("pageerror", error => pageErrors.push(error.message));
  page.on("requestfailed", request => {
    if (sameOrigin(request.url())) {
      requestFailures.push({ url: request.url(), error: request.failure()?.errorText || "unknown" });
    }
  });
  page.on("response", response => {
    if (response.status() >= 400 && sameOrigin(response.url())) {
      errorResponses.push({ url: response.url(), status: response.status() });
    }
  });

  try {
    const response = await page.goto("/db-manager", { waitUntil: "domcontentloaded", timeout: 30_000 });
    assert.ok(response, `${scenario.name}: navigation did not produce an HTTP response`);
    assert.ok(response.status() < 400, `${scenario.name}: /db-manager returned HTTP ${response.status()}`);

    const shell = page.locator(".rdm-shell");
    await shell.waitFor({ state: "visible", timeout: 15_000 });
    await page.locator(".rdm-brand-title").waitFor({ state: "visible", timeout: 5_000 });
    // Interactive Server replaces the initial SSR tree during hydration. Let that
    // second render begin before asserting the terminal workspace state.
    await page.waitForTimeout(1_500);
    await page.waitForFunction(() => !document.querySelector(".rdm-loading"), undefined, { timeout: 30_000 });
    await page.waitForTimeout(300);

    const connectionErrorVisible = await page.locator("#blazor-error-ui").evaluate(element => {
      const style = getComputedStyle(element);
      return style.display !== "none" && style.visibility !== "hidden" && Number(style.opacity) !== 0;
    });

    const screenshotPath = path.join(outputDirectory, `${scenario.name}.png`);
    await page.screenshot({ path: screenshotPath, fullPage: true });
    scenarioReport.screenshot = screenshotPath;

    assert.equal(connectionErrorVisible, false, `${scenario.name}: Blazor connection error UI is visible`);

    const workspace = await inspectWorkspace(page);
    scenarioReport.workspace = workspace;
    assert.deepEqual(workspace.errorAlerts, [],
      `${scenario.name}: database manager reported an error: ${workspace.errorAlerts.join(" | ")}`);
    assert.ok(workspace.schemaCount > 0,
      `${scenario.name}: no database schema loaded; browser smoke requires a reachable seeded database`);
    assert.ok(workspace.tableCount > 0,
      `${scenario.name}: no database table loaded; browser smoke requires at least one seeded table`);

    if (scenario.isMobile) {
      await verifyMobileDrawer(page, scenario, scenarioReport);
    }

    const selectedTable = await selectPopulatedTable(page, scenario.isMobile);
    scenarioReport.selectedTable = selectedTable;
    scenarioReport.data = await verifyReadOnlyDataWorkflow(page, scenario.name);

    const dataScreenshotPath = path.join(outputDirectory, `${scenario.name}-data.png`);
    await page.screenshot({ path: dataScreenshotPath, fullPage: true });
    scenarioReport.dataScreenshot = dataScreenshotPath;

    scenarioReport.structure = await verifyStructureAndTabKeyboard(page, scenario.name);

    const layout = await inspectLayout(page);
    scenarioReport.layout = layout;
    assert.ok(layout.shell.width > 0 && layout.shell.height > 0, `${scenario.name}: database manager shell is blank`);
    assert.equal(layout.documentHorizontalOverflow, 0, `${scenario.name}: document has horizontal overflow`);
    assert.deepEqual(layout.escapedKeyElements, [], `${scenario.name}: key UI elements escape the viewport`);
    assert.deepEqual(layout.overlaps, [], `${scenario.name}: top-level controls overlap`);

    assert.deepEqual(pageErrors, [], `${scenario.name}: uncaught page errors were reported`);
    assert.deepEqual(consoleErrors, [], `${scenario.name}: browser console errors were reported`);
    assert.deepEqual(requestFailures, [], `${scenario.name}: same-origin requests failed`);
    assert.deepEqual(errorResponses, [], `${scenario.name}: same-origin requests returned HTTP errors`);
  } finally {
    await context.close();
  }
}

async function verifyMobileDrawer(page, scenario, scenarioReport) {
  const mobileTree = page.locator(".rdm-mobile-tree");
  await mobileTree.waitFor({ state: "visible", timeout: 5_000 });
  await mobileTree.click();
  const drawer = page.locator(".rdm-scrim .rdm-drawer");
  await drawer.waitFor({ state: "visible", timeout: 5_000 });
  const drawerBox = await drawer.boundingBox();
  assert.ok(drawerBox, "mobile: navigation drawer has no layout box");
  assert.ok(drawerBox.x >= -1, "mobile: navigation drawer escapes the left viewport edge");
  assert.ok(drawerBox.x + drawerBox.width <= scenario.viewport.width + 1,
    "mobile: navigation drawer escapes the right viewport edge");
  assert.equal(await drawer.evaluate(element => element.contains(document.activeElement)), true,
    "mobile: opening the object drawer must move focus inside it");
  scenarioReport.mobileDrawer = drawerBox;

  const drawerScreenshotPath = path.join(outputDirectory, "mobile-drawer.png");
  await page.screenshot({ path: drawerScreenshotPath, fullPage: true });
  scenarioReport.mobileDrawerScreenshot = drawerScreenshotPath;

  await page.keyboard.press("Escape");
  await drawer.waitFor({ state: "detached", timeout: 5_000 });
  assert.equal(await mobileTree.evaluate(element => element === document.activeElement), true,
    "mobile: closing the object drawer must restore focus to its opener");
}

async function selectPopulatedTable(page, isMobile) {
  if (isMobile) {
    await page.locator(".rdm-mobile-tree").click();
    await page.locator(".rdm-scrim .rdm-drawer").waitFor({ state: "visible", timeout: 5_000 });
  }

  const root = isMobile ? page.locator(".rdm-scrim .rdm-drawer") : page.locator(".rdm-sidebar");
  const items = root.locator(".rdm-tree-item");
  const populatedIndex = await items.evaluateAll(elements => elements.findIndex(element => {
    const estimate = element.querySelector(".rdm-tree-count")?.textContent?.trim() || "";
    return estimate !== "" && estimate !== "0";
  }));
  assert.ok(populatedIndex >= 0, "browser smoke requires at least one table with an estimated row count");
  const item = items.nth(populatedIndex);
  const name = (await item.locator(".rdm-tree-name").innerText()).trim();
  await item.click();
  if (isMobile) {
    await page.locator(".rdm-scrim .rdm-drawer").waitFor({ state: "detached", timeout: 5_000 });
  }
  await page.waitForFunction(expected => document.querySelector(".rdm-context h2")?.textContent?.includes(expected), name,
    { timeout: 15_000 });
  await page.locator(".rdm-grid-wrap .rdm-grid tbody tr").first().waitFor({ state: "visible", timeout: 15_000 });
  return name;
}

async function verifyReadOnlyDataWorkflow(page, scenarioName) {
  const grid = page.locator(".rdm-grid-wrap .rdm-grid");
  const rowCount = await grid.locator("tbody tr").count();
  const columnCount = await grid.locator("thead th").count() - 2;
  assert.ok(rowCount > 0, `${scenarioName}: selected table did not return rows`);
  assert.ok(columnCount > 0, `${scenarioName}: selected table did not return columns`);

  const firstSort = grid.locator(".rdm-sort-button").first();
  const initialSortLabel = await firstSort.getAttribute("aria-label");
  await firstSort.click();
  await page.waitForFunction(
    ({ selector, initial }) => document.querySelector(selector)?.getAttribute("aria-label") !== initial,
    { selector: ".rdm-grid-wrap .rdm-grid .rdm-sort-button", initial: initialSortLabel },
    { timeout: 15_000 });

  const advancedQuery = page.locator(".rdm-advanced-query");
  if (!await advancedQuery.getAttribute("open")) await advancedQuery.locator("summary").click();
  const exactCount = advancedQuery.locator(".rdm-filter-actions input[type='checkbox']");
  if (!await exactCount.isChecked()) await exactCount.check();
  const apply = advancedQuery.locator(".rdm-filter-actions .rdm-button-primary");
  await apply.click();
  await page.waitForFunction(element => !element.disabled, await apply.elementHandle(), { timeout: 15_000 });
  await page.waitForFunction(() => Array.from(document.querySelectorAll(".rdm-query-diagnostics .rdm-command-sql"))
    .some(element => /COUNT\(\*\)/i.test(element.textContent || "")), undefined, { timeout: 15_000 });
  assert.match(await page.locator(".rdm-pager-info").innerText(), /\d/,
    `${scenarioName}: exact row count did not render`);

  const diagnostics = page.locator(".rdm-query-diagnostics");
  await diagnostics.waitFor({ state: "visible", timeout: 10_000 });
  if (!await diagnostics.getAttribute("open")) await diagnostics.locator("summary").click();
  const executedCommands = await diagnostics.locator(".rdm-command-sql").allTextContents();
  assert.ok(executedCommands.some(command => /^\s*SELECT\s+/i.test(command)),
    `${scenarioName}: executed SELECT command is not visible on the data page`);
  assert.ok(executedCommands.some(command => /COUNT\(\*\)/i.test(command)),
    `${scenarioName}: exact-count command is not visible on the data page`);

  const activityTab = page.getByRole("tab", { name: /^(活动记录|Activity)$/ });
  await activityTab.click();
  const sessionBox = page.locator(".rdm-session-query-box");
  await sessionBox.waitFor({ state: "visible", timeout: 10_000 });
  const sessionRows = sessionBox.locator(".rdm-session-query-row");
  assert.ok(await sessionRows.count() > 0,
    `${scenarioName}: the in-memory session query log is empty`);
  const latestSessionSql = sessionRows.first().locator(".rdm-session-sql");
  if (!await latestSessionSql.getAttribute("open")) await latestSessionSql.locator("summary").click();
  assert.match(await latestSessionSql.locator(".rdm-command-sql").first().innerText(), /^\s*SELECT\s+/i,
    `${scenarioName}: the session log does not expose the executed command`);
  await page.getByRole("tab", { name: /^(数据|Data)$/ }).click();

  return {
    rows: rowCount,
    columns: columnCount,
    sorted: true,
    exactCountRequested: true,
    executedCommands: executedCommands.length,
    sessionQueries: await sessionRows.count(),
  };
}

async function verifyStructureAndTabKeyboard(page, scenarioName) {
  const dataTab = page.getByRole("tab", { name: /^(数据|Data)$/ });
  await dataTab.focus();
  await dataTab.press("ArrowRight");
  const structureTab = page.getByRole("tab", { name: /^(结构|Structure)$/ });
  await page.waitForFunction(element => element.getAttribute("aria-selected") === "true",
    await structureTab.elementHandle(), { timeout: 5_000 });
  assert.equal(await structureTab.evaluate(element => element === document.activeElement), true,
    `${scenarioName}: arrow-key tab navigation did not move focus`);
  await page.locator(".rdm-structure-summary").waitFor({ state: "visible", timeout: 10_000 });
  const structureTables = await page.locator(".rdm-section .rdm-grid").count();
  assert.ok(structureTables >= 2, `${scenarioName}: structure view did not render columns and indexes`);

  await structureTab.press("Home");
  await page.waitForFunction(element => element.getAttribute("aria-selected") === "true",
    await dataTab.elementHandle(), { timeout: 5_000 });
  assert.equal(await dataTab.evaluate(element => element === document.activeElement), true,
    `${scenarioName}: Home did not return tab focus to Data`);
  return { tables: structureTables, keyboardNavigation: true };
}

async function inspectWorkspace(page) {
  return page.evaluate(() => ({
    errorAlerts: Array.from(document.querySelectorAll(".rdm-alert-error"))
      .filter(element => {
        const style = getComputedStyle(element);
        const rect = element.getBoundingClientRect();
        return style.display !== "none" && style.visibility !== "hidden" && rect.width > 0 && rect.height > 0;
      })
      .map(element => element.textContent?.trim() || "unknown database manager error"),
    schemaCount: document.querySelectorAll(".rdm-schema").length,
    tableCount: document.querySelectorAll(".rdm-tree-item").length,
  }));
}

async function inspectLayout(page) {
  return page.evaluate(() => {
    const viewportWidth = document.documentElement.clientWidth;
    const visible = element => {
      const style = getComputedStyle(element);
      const rect = element.getBoundingClientRect();
      return style.display !== "none" && style.visibility !== "hidden" && rect.width > 0 && rect.height > 0;
    };
    const box = element => {
      const rect = element.getBoundingClientRect();
      return {
        x: Math.round(rect.x * 100) / 100,
        y: Math.round(rect.y * 100) / 100,
        width: Math.round(rect.width * 100) / 100,
        height: Math.round(rect.height * 100) / 100,
      };
    };
    const label = element => {
      const classes = Array.from(element.classList).slice(0, 3).join(".");
      return `${element.tagName.toLowerCase()}${classes ? `.${classes}` : ""}`;
    };

    const escapedKeyElements = Array.from(document.querySelectorAll([
      ".rdm-shell",
      ".rdm-topbar",
      ".rdm-main",
      ".rdm-context",
      ".rdm-panel",
      ".rdm-brand-title",
    ].join(",")))
      .filter(visible)
      .filter(element => {
        const rect = element.getBoundingClientRect();
        return rect.left < -1 || rect.right > viewportWidth + 1;
      })
      .map(element => ({ element: label(element), box: box(element) }));

    const overlaps = [];
    for (const container of document.querySelectorAll(".rdm-topbar, .rdm-context")) {
      const children = Array.from(container.children).filter(visible);
      for (let leftIndex = 0; leftIndex < children.length; leftIndex += 1) {
        const left = children[leftIndex].getBoundingClientRect();
        for (let rightIndex = leftIndex + 1; rightIndex < children.length; rightIndex += 1) {
          const right = children[rightIndex].getBoundingClientRect();
          const width = Math.min(left.right, right.right) - Math.max(left.left, right.left);
          const height = Math.min(left.bottom, right.bottom) - Math.max(left.top, right.top);
          if (width > 1 && height > 1) {
            overlaps.push({
              container: label(container),
              first: label(children[leftIndex]),
              second: label(children[rightIndex]),
              width: Math.round(width * 100) / 100,
              height: Math.round(height * 100) / 100,
            });
          }
        }
      }
    }

    return {
      viewportWidth,
      documentHorizontalOverflow: Math.max(0, document.documentElement.scrollWidth - viewportWidth),
      shell: box(document.querySelector(".rdm-shell")),
      escapedKeyElements,
      overlaps,
    };
  });
}

function sameOrigin(value) {
  return new URL(value).origin === new URL(baseUrl).origin;
}

function normalizeBaseUrl(value) {
  const url = new URL(value);
  url.pathname = url.pathname.replace(/\/+$/, "");
  url.search = "";
  url.hash = "";
  return url.toString().replace(/\/$/, "");
}

function loadPlaywright() {
  const candidates = [];
  if (process.env.PLAYWRIGHT_MODULE_PATH) {
    const configuredPath = path.resolve(process.env.PLAYWRIGHT_MODULE_PATH);
    candidates.push(configuredPath);
    candidates.push(path.join(configuredPath, "playwright"));
  }

  try {
    candidates.push(require.resolve("playwright", {
      paths: [path.join(__dirname, "browser-smoke"), __dirname, process.cwd()],
    }));
  } catch {
    // A repository-local install is optional; the Codex runtime remains a fallback.
  }

  const codexNodeModules = process.env.CODEX_NODE_MODULES || path.join(
    os.homedir(),
    ".cache",
    "codex-runtimes",
    "codex-primary-runtime",
    "dependencies",
    "node",
    "node_modules");
  candidates.push(path.join(codexNodeModules, "playwright"));

  const failures = [];
  for (const candidate of [...new Set(candidates)]) {
    try {
      return { module: require(candidate), path: candidate };
    } catch (error) {
      failures.push(`${candidate}: ${error.code || error.message}`);
    }
  }

  throw new Error([
    "Playwright was not found. Run npm ci in eng/browser-smoke or set PLAYWRIGHT_MODULE_PATH.",
    ...failures,
  ].join("\n"));
}

function findBrowserExecutable() {
  const candidates = [
    process.env.BROWSER_EXECUTABLE_PATH,
    process.env.EDGE_EXECUTABLE_PATH,
    "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe",
    "C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe",
  ].filter(Boolean);
  return candidates.find(candidate => fs.existsSync(candidate));
}
