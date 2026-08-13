#!/usr/bin/env node

"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const baseUrl = new URL(process.argv[2] || process.env.BASE_URL || "http://127.0.0.1:5173").origin;
const outputPath = path.resolve(process.env.LIVE_DB_SMOKE_REPORT
  || path.join(process.cwd(), "artifacts", "live-database-readonly-smoke.json"));
const playwright = loadPlaywright();
const executablePath = findBrowserExecutable();
const report = {
  baseUrl,
  startedAtUtc: new Date().toISOString(),
  readOnly: true,
  browserErrors: [],
  requestFailures: [],
  httpErrors: [],
  passed: false,
};

(async () => {
  let browser;
  try {
    browser = await playwright.chromium.launch({
      ...(executablePath ? { executablePath } : {}),
      headless: true,
      args: ["--disable-dev-shm-usage"],
    });
    const context = await browser.newContext({
      baseURL: baseUrl,
      viewport: { width: 1440, height: 900 },
      locale: "zh-CN",
      ignoreHTTPSErrors: true,
    });
    const page = await context.newPage();
    observe(page);

    const response = await page.goto("/db-manager", { waitUntil: "domcontentloaded", timeout: 30_000 });
    assert.ok(response && response.status() < 400, `/db-manager returned ${response?.status()}`);
    await page.locator(".rdm-shell").waitFor({ state: "visible", timeout: 15_000 });
    await page.waitForFunction(() => !document.querySelector(".rdm-loading"), undefined, { timeout: 30_000 });
    await page.waitForTimeout(1_000);
    await assertNoManagerError(page, "workspace");

    report.product = compact(await page.locator(".rdm-brand-subtitle").innerText());
    report.schemaCount = await page.locator(".rdm-schema").count();
    const objects = page.locator(".rdm-sidebar .rdm-tree-item");
    report.objectCount = await objects.count();
    assert.ok(report.schemaCount > 0, "no schema is visible");
    assert.ok(report.objectCount > 0, "no table or view is visible");

    await objects.first().click();
    await page.waitForFunction(() => {
      const tab = Array.from(document.querySelectorAll('[role="tab"]'))
        .find(element => /^(结构|Structure)$/.test(element.textContent?.trim() || ""));
      return tab instanceof HTMLButtonElement && !tab.disabled;
    }, undefined, { timeout: 20_000 });
    await page.waitForTimeout(300);
    await assertNoManagerError(page, "first object browse");

    const grid = page.locator(".rdm-grid-wrap .rdm-grid");
    report.firstObject = {
      dataGridVisible: await grid.isVisible().catch(() => false),
      visibleRows: await grid.locator("tbody tr").count(),
      visibleColumns: Math.max(0, await grid.locator("thead th").count() - 1),
      editControls: await grid.locator('button[title*="编辑"],button[title*="Edit"]').count(),
      deleteControls: await grid.locator('button[title*="删除"],button[title*="Delete"]').count(),
    };

    const structureTab = page.getByRole("tab", { name: /^(结构|Structure)$/ });
    await structureTab.click();
    await page.locator(".rdm-structure-summary").waitFor({ state: "visible", timeout: 10_000 });
    report.structure = {
      metadataTables: await page.locator(".rdm-section .rdm-grid").count(),
      schemaChangeEnabled: !await page.locator(".rdm-toolbar .rdm-button-primary").isDisabled(),
    };
    await assertNoManagerError(page, "structure");

    report.sql = await runSql(page,
      "SELECT VERSION() AS server_version, @@lower_case_table_names AS lower_case_table_names;");
    report.serverVersion = report.sql.rows[0]?.[0] || report.product;
    report.lowerCaseTableNames = report.sql.rows[0]?.[1] || null;
    delete report.sql.rows;

    const grantResult = await runSql(page, "SHOW GRANTS FOR CURRENT_USER;");
    const grants = grantResult.rows.flat().join("\n").toUpperCase();
    report.grants = classifyGrants(grants);

    assert.deepEqual(report.browserErrors, [], "browser errors occurred");
    assert.deepEqual(report.requestFailures, [], "same-origin requests failed");
    assert.deepEqual(report.httpErrors, [], "same-origin HTTP errors occurred");
    report.passed = true;
    await context.close();
  } catch (error) {
    report.error = error instanceof Error ? error.message : String(error);
    process.exitCode = 1;
  } finally {
    if (browser) await browser.close();
    report.completedAtUtc = new Date().toISOString();
    fs.mkdirSync(path.dirname(outputPath), { recursive: true });
    fs.writeFileSync(outputPath, `${JSON.stringify(report, null, 2)}\n`, "utf8");
    console.log(`Live database read-only smoke ${report.passed ? "passed" : "failed"}: ${outputPath}`);
  }
})();

async function runSql(page, sql) {
  const tab = page.getByRole("tab", { name: /^SQL$/i });
  await tab.click();
  await page.locator(".rdm-sql-editor .cm-content").waitFor({ state: "visible", timeout: 10_000 });
  await page.evaluate(async statement => {
    const module = await import("/_content/RazorDbManager/razordbmanager.js");
    module.setSqlEditorValue(document.querySelector(".rdm-sql-editor"), statement);
  }, sql);
  const execute = page.locator(".rdm-editor-head .rdm-button-primary");
  await page.waitForFunction(element => !element.disabled, await execute.elementHandle(), { timeout: 10_000 });
  await execute.click();
  const result = page.locator(".rdm-result-panel .rdm-grid").last();
  await result.waitFor({ state: "visible", timeout: 30_000 });
  await assertNoManagerError(page, "SQL read-only probe");
  return {
    columnCount: await result.locator("thead th").count(),
    rowCount: await result.locator("tbody tr").count(),
    rows: await result.locator("tbody tr").evaluateAll(rows => rows.map(row =>
      Array.from(row.querySelectorAll("td"), cell => cell.textContent?.trim() || ""))),
  };
}

function classifyGrants(value) {
  const all = /\bALL PRIVILEGES\b/.test(value);
  const has = privilege => all || new RegExp(`(?:^|[,\\s])${privilege}(?:[,\\s]|$)`).test(value);
  return {
    allPrivileges: all,
    select: has("SELECT"),
    insert: has("INSERT"),
    update: has("UPDATE"),
    delete: has("DELETE"),
    create: has("CREATE"),
    alter: has("ALTER"),
    drop: has("DROP"),
    execute: has("EXECUTE"),
    trigger: has("TRIGGER"),
    event: has("EVENT"),
    process: has("PROCESS"),
    grantOption: /\bGRANT OPTION\b/.test(value),
  };
}

function observe(page) {
  page.on("console", message => {
    if (message.type() === "error") report.browserErrors.push(message.text());
  });
  page.on("pageerror", error => report.browserErrors.push(error.message));
  page.on("requestfailed", request => {
    if (sameOrigin(request.url())) report.requestFailures.push(request.failure()?.errorText || "unknown");
  });
  page.on("response", response => {
    if (sameOrigin(response.url()) && response.status() >= 400) report.httpErrors.push(response.status());
  });
}

async function assertNoManagerError(page, step) {
  const errors = await page.locator(".rdm-alert-error").allTextContents();
  assert.deepEqual(errors.map(compact).filter(Boolean), [], `${step} displayed a manager error`);
}

function sameOrigin(value) { return new URL(value).origin === baseUrl; }
function compact(value) { return value.replace(/\s+/g, " ").trim(); }

function loadPlaywright() {
  const configured = process.env.PLAYWRIGHT_MODULE_PATH;
  const candidates = [
    configured,
    configured && path.join(configured, "playwright"),
    path.join(os.homedir(), ".cache", "codex-runtimes", "codex-primary-runtime",
      "dependencies", "node", "node_modules", "playwright"),
  ].filter(Boolean);
  for (const candidate of [...new Set(candidates)]) {
    try { return require(candidate); } catch { }
  }
  throw new Error("Playwright is not available.");
}

function findBrowserExecutable() {
  return [
    process.env.BROWSER_EXECUTABLE_PATH,
    "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe",
    "C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe",
  ].filter(Boolean).find(fs.existsSync);
}
