#!/usr/bin/env node

"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

if (process.argv.includes("--help") || process.argv.includes("-h")) {
  console.log([
    "Usage: node eng/ui-workflow-smoke.cjs [base-url]",
    "",
    "Runs the destructive browser release gate against disposable test tables.",
    "The database must already contain <prefix>_seed and <prefix>_import.",
    "",
    "Environment variables:",
    "  UI_WORKFLOW_PREFIX             Unique [a-z0-9_] fixture prefix (required in CI).",
    "  UI_WORKFLOW_SMOKE_OUTPUT       Screenshot, trace, download, and report directory.",
    "  PLAYWRIGHT_MODULE_PATH         Path to playwright or its node_modules directory.",
    "  BROWSER_EXECUTABLE_PATH        Optional Chromium-family browser executable.",
  ].join("\n"));
  process.exit(0);
}

const baseUrl = normalizeBaseUrl(process.argv[2] || process.env.BASE_URL || "http://127.0.0.1:5173");
const fixturePrefix = process.env.UI_WORKFLOW_PREFIX
  || `rdm_e2e_local_${process.pid}_${Date.now().toString(36)}`;
assert.match(fixturePrefix, /^[a-z][a-z0-9_]{0,39}$/, "UI_WORKFLOW_PREFIX must be a safe, short SQL identifier");

const tables = Object.freeze({
  seed: `${fixturePrefix}_seed`,
  imported: `${fixturePrefix}_import`,
  managed: `${fixturePrefix}_managed`,
});
for (const table of Object.values(tables)) {
  assert.ok(table.length <= 64, `fixture table name exceeds MySQL's identifier limit: ${table}`);
}

const outputDirectory = path.resolve(process.env.UI_WORKFLOW_SMOKE_OUTPUT
  || path.join(process.cwd(), "artifacts", "ui-workflow-smoke"));
const playwright = loadPlaywright();
const executablePath = findBrowserExecutable();
const report = {
  baseUrl,
  fixturePrefix,
  tables,
  startedAtUtc: new Date().toISOString(),
  playwrightModule: playwright.path,
  browserExecutable: executablePath || "playwright-managed Chromium",
  steps: [],
  consoleErrors: [],
  pageErrors: [],
  requestFailures: [],
  errorResponses: [],
  cleanup: { attempted: false, succeeded: false },
  passed: false,
};

fs.mkdirSync(outputDirectory, { recursive: true });

let browser;
let context;
let page;
let failure;

(async () => {
  try {
    browser = await playwright.module.chromium.launch({
      ...(executablePath ? { executablePath } : {}),
      headless: true,
      args: ["--disable-dev-shm-usage"],
    });
    context = await browser.newContext({
      baseURL: baseUrl,
      viewport: { width: 1440, height: 900 },
      locale: "en-US",
      colorScheme: "light",
      reducedMotion: "reduce",
      ignoreHTTPSErrors: true,
      acceptDownloads: true,
    });
    await context.tracing.start({ screenshots: true, snapshots: true, sources: true });
    page = await context.newPage();
    observePage(page);

    await openWorkspace();
    await createManagedTable();
    await verifyCrud();
    await verifySqlSelect();
    await verifyCsvExport();
    await verifyCsvImport();
    await verifyActivity();
    await dropManagedTableThroughDdl();

    assert.deepEqual(report.pageErrors, [], "uncaught browser page errors were reported");
    assert.deepEqual(report.consoleErrors, [], "browser console errors were reported");
    assert.deepEqual(report.requestFailures, [], "same-origin browser requests failed");
    assert.deepEqual(report.errorResponses, [], "same-origin browser requests returned HTTP errors");
  } catch (error) {
    failure = error;
    report.error = error instanceof Error ? error.stack : String(error);
    process.exitCode = 1;
  } finally {
    if (page && !page.isClosed()) {
      report.cleanup.attempted = true;
      try {
        await cleanupThroughSqlConsole();
        report.cleanup.succeeded = true;
      } catch (cleanupError) {
        report.cleanup.error = cleanupError instanceof Error ? cleanupError.stack : String(cleanupError);
        process.exitCode = 1;
      }
      await page.screenshot({ path: path.join(outputDirectory, "final.png"), fullPage: true }).catch(() => {});
    }
    if (context) {
      await context.tracing.stop({ path: path.join(outputDirectory, "trace.zip") }).catch(() => {});
      await context.close();
    }
    if (browser) await browser.close();

    report.passed = !failure && report.cleanup.succeeded;
    report.completedAtUtc = new Date().toISOString();
    const reportPath = path.join(outputDirectory, "report.json");
    fs.writeFileSync(reportPath, `${JSON.stringify(report, null, 2)}\n`, "utf8");
    if (failure || !report.cleanup.succeeded) {
      console.error(`UI workflow smoke failed: ${(failure && (failure.message || failure)) || "cleanup failed"}`);
      console.error(`Report: ${reportPath}`);
    } else {
      console.log(`UI workflow smoke passed for ${baseUrl}`);
      console.log(`Artifacts: ${outputDirectory}`);
    }
  }
})();

function observePage(currentPage) {
  currentPage.on("console", message => {
    if (message.type() === "error") report.consoleErrors.push(message.text());
  });
  currentPage.on("pageerror", error => report.pageErrors.push(error.message));
  currentPage.on("requestfailed", request => {
    if (sameOrigin(request.url())) {
      report.requestFailures.push({ url: request.url(), error: request.failure()?.errorText || "unknown" });
    }
  });
  currentPage.on("response", response => {
    if (response.status() >= 400 && sameOrigin(response.url())) {
      report.errorResponses.push({ url: response.url(), status: response.status() });
    }
  });
}

async function openWorkspace() {
  const response = await page.goto("/db-manager", { waitUntil: "domcontentloaded", timeout: 30_000 });
  assert.ok(response && response.status() < 400, `/db-manager returned HTTP ${response?.status()}`);
  await page.locator(".rdm-shell").waitFor({ state: "visible", timeout: 15_000 });
  await page.waitForTimeout(1_500);
  await waitForIdle();
  await page.locator(".rdm-sidebar .rdm-tree-item", { hasText: tables.seed })
    .waitFor({ state: "visible", timeout: 15_000 });
  await assertNoVisibleError("workspace load");

  const requiredTabs = [/^SQL$/i, /^(Import \/ Export|导入\/导出)$/i];
  for (const name of requiredTabs) {
    const tab = page.getByRole("tab", { name });
    await tab.waitFor({ state: "visible", timeout: 5_000 });
    assert.equal(await tab.isDisabled(), false, `required tab ${name} is disabled`);
  }
  report.steps.push({ name: "workspace", tables: await treeTableNames() });
}

async function createManagedTable() {
  await clickTab(/^(Structure|结构)$/i);
  const createButton = page.getByRole("button", { name: /^(Create table|创建表)$/i });
  await expectEnabled(createButton, "create table button");
  await createButton.click();

  const dialog = page.locator(".rdm-schema-drawer");
  await dialog.waitFor({ state: "visible", timeout: 10_000 });
  await fillField(dialog, /^(Table name|表名)$/i, tables.managed);

  let editors = dialog.locator(".rdm-column-editor");
  await configureColumn(editors.nth(0), {
    name: "id",
    type: "Int32",
    nullable: false,
    autoIncrement: true,
    primaryKey: true,
  });
  await dialog.getByRole("button", { name: /^(Add column|添加列)$/i }).click();
  editors = dialog.locator(".rdm-column-editor");
  assert.equal(await editors.count(), 2, "create-table editor did not add a second column");
  await configureColumn(editors.nth(1), {
    name: "name",
    type: "VarChar",
    nullable: false,
  });

  await dialog.getByRole("button", { name: /^(Preview|预览)$/i }).click();
  const ddl = dialog.locator(".rdm-ddl-preview");
  await ddl.waitFor({ state: "visible", timeout: 15_000 });
  const ddlText = await ddl.innerText();
  assert.match(ddlText, /CREATE\s+TABLE/i, "DDL preview does not create a table");
  assert.ok(ddlText.includes(tables.managed), "DDL preview targets the wrong table");
  await screenshot("01-ddl-create-preview.png");

  await dialog.locator(".rdm-confirm-box input[type='checkbox']").check();
  await dialog.getByRole("button", { name: /^(Execute|执行)$/i }).click();
  await dialog.waitFor({ state: "detached", timeout: 30_000 });
  await treeItem(tables.managed).waitFor({ state: "visible", timeout: 15_000 });
  await assertNoVisibleError("create table execution");
  report.steps.push({ name: "DDL create preview/execute", statement: compact(ddlText) });
}

async function configureColumn(editor, options) {
  await fillField(editor, /^(Column name|列名)$/i, options.name);
  const typeField = field(editor, /^(Type|类型)$/i);
  await typeField.locator("select").selectOption(options.type);
  await setLabeledCheckbox(editor, /^(Nullable|可为空)$/i, options.nullable);
  if (options.autoIncrement !== undefined) {
    await setLabeledCheckbox(editor, /^(Auto increment|自动递增)$/i, options.autoIncrement);
  }
  if (options.primaryKey !== undefined) {
    await setLabeledCheckbox(editor, /^(Primary key|主键)$/i, options.primaryKey);
  }
}

async function verifyCrud() {
  await selectTable(tables.managed);
  await clickTab(/^(Data|数据)$/i);

  await insertRow("created-by-ui");
  await insertRow("delete-me");
  let grid = await dataGrid();
  assert.equal(await grid.locator("tbody tr").count(), 2, "row insert did not create two rows");

  let createdRow = grid.locator("tbody tr", { hasText: "created-by-ui" });
  await createdRow.getByTitle(/^(Edit row|编辑行)$/i).click();
  await fillEditorField("name", "updated-by-ui");
  await saveRowDialog();
  grid = await dataGrid();
  await grid.locator("tbody tr", { hasText: "updated-by-ui" }).waitFor({ state: "visible", timeout: 15_000 });
  assert.equal(await grid.locator("tbody tr", { hasText: "created-by-ui" }).count(), 0,
    "row update left the original value behind");

  const deleteRow = grid.locator("tbody tr", { hasText: "delete-me" });
  await deleteRow.getByTitle(/^(Delete row|删除行)$/i).click();
  const confirmation = page.locator('[role="alertdialog"]');
  await confirmation.waitFor({ state: "visible", timeout: 10_000 });
  await confirmation.getByRole("button", { name: /^(Delete row|删除行)$/i }).click();
  await confirmation.waitFor({ state: "detached", timeout: 15_000 });
  grid = await dataGrid();
  assert.equal(await grid.locator("tbody tr").count(), 1, "row delete did not leave exactly one row");
  assert.equal(await grid.locator("tbody tr", { hasText: "delete-me" }).count(), 0,
    "deleted row is still visible");
  await assertNoVisibleError("CRUD");
  await screenshot("02-crud-final.png");
  report.steps.push({
    name: "CRUD",
    inserted: ["created-by-ui", "delete-me"],
    updated: "updated-by-ui",
    deleted: "delete-me",
    remainingRows: 1,
  });
}

async function insertRow(name) {
  const button = page.getByRole("button", { name: /^(New row|New record|新增行|新增记录)$/i });
  await expectEnabled(button, "new row button");
  await button.click();
  await fillEditorField("name", name);
  await saveRowDialog();
  const grid = await dataGrid();
  await grid.locator("tbody tr", { hasText: name }).waitFor({ state: "visible", timeout: 15_000 });
}

async function verifySqlSelect() {
  await runSql(`SELECT id, name FROM \`${tables.managed}\` ORDER BY id;`);
  const result = page.locator(".rdm-result-panel .rdm-grid").first();
  await result.waitFor({ state: "visible", timeout: 30_000 });
  const headers = (await result.locator("thead th").allTextContents()).map(compact);
  const rows = await result.locator("tbody tr").evaluateAll(elements => elements.map(row =>
    Array.from(row.querySelectorAll("td"), cell => (cell.textContent || "").trim())));
  assert.deepEqual(headers.map(value => value.toLowerCase()), ["id", "name"], "SQL result columns are incorrect");
  assert.equal(rows.length, 1, "SQL SELECT returned an unexpected row count");
  assert.equal(rows[0][1], "updated-by-ui", "SQL SELECT did not observe the CRUD result");
  await assertNoVisibleError("SQL SELECT");
  await screenshot("03-sql-select.png");
  report.steps.push({ name: "SQL SELECT", headers, rows });
}

async function verifyCsvExport() {
  await selectTable(tables.managed);
  await clickTab(/^(Import \/ Export|导入\/导出)$/i);
  const exportBox = page.locator(".rdm-transfer-box").filter({
    has: page.locator("h3", { hasText: /^(Export CSV|导出 CSV)$/i }),
  });
  const csvButton = exportBox.getByRole("button", { name: /^CSV$/i });
  await expectEnabled(csvButton, "CSV export button");
  await csvButton.click();

  await clickTab(/^(Activity|活动记录)$/i);
  const job = await waitForJob(/^(CSV export|CSV 导出)$/i, "Completed");
  const downloadForm = job.locator("form");
  const downloadButton = downloadForm.getByTitle(/^(Download|下载)$/i);
  await downloadForm.evaluate(element => element.removeAttribute("target"));
  const downloadPromise = page.waitForEvent("download", { timeout: 30_000 });
  await downloadButton.click();
  const download = await downloadPromise;
  const downloadPath = path.join(outputDirectory, "managed-export.csv");
  await download.saveAs(downloadPath);
  const csv = fs.readFileSync(downloadPath, "utf8");
  assert.match(csv, /^id,name\r?\n/m, "CSV export header is incorrect");
  assert.match(csv, /updated-by-ui/, "CSV export does not contain the final row");
  assert.doesNotMatch(csv, /delete-me|created-by-ui/, "CSV export contains stale CRUD values");
  report.steps.push({ name: "CSV export/download", bytes: Buffer.byteLength(csv), suggestedFilename: download.suggestedFilename() });
}

async function verifyCsvImport() {
  await selectTable(tables.imported);
  await clickTab(/^(Import \/ Export|导入\/导出)$/i);
  const importBox = page.locator(".rdm-transfer-box").filter({
    has: page.locator("h3", { hasText: /^(Import CSV|导入 CSV)$/i }),
  });
  const input = importBox.locator('input[type="file"]').first();
  await input.setInputFiles({
    name: "import.csv",
    mimeType: "text/csv",
    buffer: Buffer.from('id,name\n10,import-alpha\n11,"import,beta"\n', "utf8"),
  });
  await importBox.getByRole("button", { name: /^(Start import|开始导入)$/i }).click();
  const uploadFrame = page.locator(".rdm-upload-frame");
  await uploadFrame.waitFor({ state: "attached", timeout: 10_000 });
  await page.waitForFunction(frame => {
    try { return /Upload accepted|queued/i.test(frame.contentDocument?.body?.innerText || ""); }
    catch { return false; }
  }, await uploadFrame.elementHandle(), { timeout: 30_000 });

  await clickTab(/^(Activity|活动记录)$/i);
  await waitForJob(/^(CSV import|CSV 导入)$/i, "Completed");
  await runSql(`SELECT id, name FROM \`${tables.imported}\` ORDER BY id;`);
  const result = page.locator(".rdm-result-panel .rdm-grid").first();
  await result.waitFor({ state: "visible", timeout: 30_000 });
  const rows = await result.locator("tbody tr").evaluateAll(elements => elements.map(row =>
    Array.from(row.querySelectorAll("td"), cell => (cell.textContent || "").trim())));
  assert.deepEqual(rows, [["10", "import-alpha"], ["11", "import,beta"]], "CSV import rows are incorrect");
  await assertNoVisibleError("CSV import");
  await screenshot("04-csv-import.png");
  report.steps.push({ name: "CSV import", rows });
}

async function verifyActivity() {
  await clickTab(/^(Activity|活动记录)$/i);
  await page.locator(".rdm-activity-toolbar").getByRole("button", { name: /^(Refresh|刷新)$/i }).click();
  const jobs = activityBox(/^(Recent jobs|最近任务)$/i).locator(".rdm-activity-row");
  const audits = activityBox(/^(Audit log|审计日志)$/i).locator(".rdm-activity-row");
  const jobText = await jobs.allTextContents();
  const auditText = await audits.allTextContents();
  assert.ok(jobText.some(text => /CSV export|CSV 导出/i.test(text) && /Completed|已完成/i.test(text)),
    "completed CSV export job is absent from Activity");
  assert.ok(jobText.some(text => /CSV import|CSV 导入/i.test(text) && /Completed|已完成/i.test(text)),
    "completed CSV import job is absent from Activity");
  assert.ok(auditText.some(text => /Export|导出/i.test(text)), "export audit record is absent from Activity");
  assert.ok(auditText.some(text => /Import|导入/i.test(text)), "import audit record is absent from Activity");
  await screenshot("05-activity.png");
  report.steps.push({ name: "Activity", jobs: jobText.map(compact), auditCount: auditText.length });
}

async function dropManagedTableThroughDdl() {
  await selectTable(tables.managed);
  await clickTab(/^(Structure|结构)$/i);
  await page.getByRole("button", { name: /^(Change structure|修改结构)$/i }).click();
  const dialog = page.locator(".rdm-schema-drawer");
  await dialog.waitFor({ state: "visible", timeout: 10_000 });
  await field(dialog, /^(Operation|操作)$/i).locator("select").selectOption("DropTable");
  await dialog.getByRole("button", { name: /^(Preview|预览)$/i }).click();
  const ddl = dialog.locator(".rdm-ddl-preview");
  await ddl.waitFor({ state: "visible", timeout: 15_000 });
  const ddlText = await ddl.innerText();
  assert.match(ddlText, /DROP\s+TABLE/i, "destructive DDL preview does not drop the table");
  assert.ok(ddlText.includes(tables.managed), "destructive DDL preview targets the wrong table");
  await dialog.locator(".rdm-confirm-box input[type='checkbox']").check();
  await dialog.getByRole("button", { name: /^(Execute|执行)$/i }).click();
  await dialog.waitFor({ state: "detached", timeout: 30_000 });
  await expectTreeTableAbsent(tables.managed);
  report.steps.push({ name: "destructive DDL drop preview/execute", statement: compact(ddlText) });
}

async function cleanupThroughSqlConsole() {
  if (!page || page.isClosed()) throw new Error("browser page is unavailable for cleanup");
  await closeOpenModalForCleanup();
  const names = [tables.managed, tables.imported, tables.seed];
  const sql = names.map(name => `DROP TABLE IF EXISTS \`${name}\``).join("; ") + ";";
  await runSql(sql, { allowExistingError: true });
  await runSql(`SELECT COUNT(*) AS remaining_fixture_tables FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN (${names.map(name => `'${name}'`).join(", ")});`,
    { allowExistingError: true });
  const result = page.locator(".rdm-result-panel .rdm-grid").first();
  await result.waitFor({ state: "visible", timeout: 30_000 });
  const value = compact(await result.locator("tbody td").first().innerText());
  assert.equal(value, "0", `cleanup left ${value} fixture tables behind`);
}

async function closeOpenModalForCleanup() {
  const closeButton = page.locator("[data-rdm-modal-close]:visible").last();
  if (await closeButton.count() === 0) return;
  await page.waitForFunction(element => !element.disabled, await closeButton.elementHandle(), { timeout: 10_000 });
  await closeButton.click();
  await page.locator("[data-rdm-modal-close]:visible").waitFor({ state: "hidden", timeout: 10_000 });
}

async function runSql(sql, options = {}) {
  await clickTab(/^SQL$/i);
  const editor = page.locator(".rdm-sql-editor");
  await editor.locator(".cm-content").waitFor({ state: "visible", timeout: 10_000 });
  await page.evaluate(async statement => {
    const module = await import("/_content/RazorDbManager/razordbmanager.js");
    module.setSqlEditorValue(document.querySelector(".rdm-sql-editor"), statement);
  }, sql);
  const execute = page.locator(".rdm-editor-head .rdm-button-primary");
  await expectEnabled(execute, "SQL execute button");
  const previousResult = compact(await page.locator(".rdm-result-panel").innerText());
  await execute.click();
  await page.waitForFunction(previous => {
    const executeButton = document.querySelector(".rdm-editor-head .rdm-button-primary");
    const current = (document.querySelector(".rdm-result-panel")?.textContent || "").replace(/\s+/g, " ").trim();
    return !executeButton || executeButton.disabled || current !== previous;
  }, previousResult, { timeout: 10_000 });
  await page.waitForFunction(previous => {
    const executeButton = document.querySelector(".rdm-editor-head .rdm-button-primary");
    const current = (document.querySelector(".rdm-result-panel")?.textContent || "").replace(/\s+/g, " ").trim();
    return executeButton && !executeButton.disabled && current && current !== previous;
  }, previousResult, { timeout: 30_000 });
  if (!options.allowExistingError) await assertNoVisibleError("SQL execution");
}

async function waitForJob(kind, terminalStatus) {
  const deadline = Date.now() + 45_000;
  while (Date.now() < deadline) {
    const rows = activityBox(/^(Recent jobs|最近任务)$/i).locator(".rdm-activity-row");
    const count = await rows.count();
    for (let index = 0; index < count; index += 1) {
      const row = rows.nth(index);
      const name = compact(await row.locator(".rdm-activity-name").innerText());
      if (!kind.test(name)) continue;
      const text = compact(await row.innerText());
      if (/Failed|Cancelled|失败|已取消/i.test(text)) throw new Error(`transfer job did not complete: ${text}`);
      if (new RegExp(`${terminalStatus}|已完成`, "i").test(text)) return row;
    }
    const refresh = page.locator(".rdm-activity-toolbar").getByRole("button", { name: /^(Refresh|刷新)$/i });
    if (!await refresh.isDisabled()) await refresh.click();
    await page.waitForTimeout(500);
  }
  throw new Error(`timed out waiting for ${kind} job to reach ${terminalStatus}`);
}

function activityBox(title) {
  return page.locator(".rdm-activity-box").filter({
    has: page.locator(".rdm-list-head", { hasText: title }),
  });
}

async function selectTable(name) {
  const item = treeItem(name);
  await item.waitFor({ state: "visible", timeout: 15_000 });
  await item.click();
  await page.waitForFunction(expected => {
    const heading = document.querySelector(".rdm-context h2")?.textContent || "";
    return heading === expected || heading.endsWith(`.${expected}`);
  }, name, { timeout: 15_000 });
  await waitForIdle();
  await assertNoVisibleError(`select table ${name}`);
}

function treeItem(name) {
  return page.locator(".rdm-sidebar .rdm-tree-item").filter({
    has: page.locator(".rdm-tree-name", { hasText: new RegExp(`^${escapeRegex(name)}$`) }),
  });
}

async function expectTreeTableAbsent(name) {
  await page.waitForFunction(expected => !Array.from(document.querySelectorAll(".rdm-sidebar .rdm-tree-name"))
    .some(element => element.textContent?.trim() === expected), name, { timeout: 15_000 });
}

async function treeTableNames() {
  return page.locator(".rdm-sidebar .rdm-tree-name").allTextContents();
}

async function dataGrid() {
  const grid = page.locator(".rdm-grid-wrap .rdm-grid");
  await grid.waitFor({ state: "visible", timeout: 15_000 });
  return grid;
}

async function fillEditorField(column, value) {
  const dialog = page.locator('.rdm-drawer[role="dialog"]');
  await dialog.waitFor({ state: "visible", timeout: 10_000 });
  const label = dialog.locator("label.rdm-field-label").filter({
    has: dialog.locator("span", { hasText: new RegExp(`^${escapeRegex(column)}$`) }),
  });
  const fieldId = await label.getAttribute("for");
  assert.ok(fieldId, `editor field ${column} has no associated control`);
  await dialog.locator(`#${fieldId}`).fill(value);
}

async function saveRowDialog() {
  const dialog = page.locator('.rdm-drawer[role="dialog"]');
  await dialog.getByRole("button", { name: /^(Save changes|保存更改)$/i }).click();
  await dialog.waitFor({ state: "detached", timeout: 15_000 });
  await assertNoVisibleError("save row");
}

function field(root, label) {
  return root.locator(".rdm-field-label", { hasText: label }).first().locator("xpath=..");
}

async function fillField(root, label, value) {
  const control = field(root, label).locator("input, textarea").first();
  await control.waitFor({ state: "visible", timeout: 5_000 });
  await control.fill(value);
}

async function setLabeledCheckbox(root, label, checked) {
  const wrapper = root.locator("label.rdm-check").filter({ hasText: label });
  const checkbox = wrapper.locator('input[type="checkbox"]');
  await checkbox.waitFor({ state: "visible", timeout: 5_000 });
  if (checked) await checkbox.check(); else await checkbox.uncheck();
}

async function clickTab(name) {
  const tab = page.getByRole("tab", { name });
  await tab.waitFor({ state: "visible", timeout: 10_000 });
  assert.equal(await tab.isDisabled(), false, `tab ${name} is disabled`);
  if (await tab.getAttribute("aria-selected") !== "true") await tab.click();
  await waitForIdle();
}

async function waitForIdle() {
  await page.waitForFunction(() => !document.querySelector(".rdm-loading"), undefined, { timeout: 30_000 });
  await page.waitForTimeout(200);
}

async function expectEnabled(locator, name) {
  await locator.waitFor({ state: "visible", timeout: 10_000 });
  await page.waitForFunction(element => !element.disabled, await locator.elementHandle(), { timeout: 10_000 });
  assert.equal(await locator.isDisabled(), false, `${name} is disabled`);
}

async function assertNoVisibleError(step) {
  const errors = await page.locator(".rdm-alert-error").evaluateAll(elements => elements
    .filter(element => {
      const style = getComputedStyle(element);
      const rect = element.getBoundingClientRect();
      return style.display !== "none" && style.visibility !== "hidden" && rect.width > 0 && rect.height > 0;
    })
    .map(element => element.textContent?.trim() || "unknown error"));
  assert.deepEqual(errors, [], `${step} displayed RazorDbManager errors`);
}

async function screenshot(name) {
  await page.screenshot({ path: path.join(outputDirectory, name), fullPage: true });
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

function compact(value) {
  return value.replace(/\s+/g, " ").trim();
}

function escapeRegex(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function loadPlaywright() {
  const candidates = [];
  if (process.env.PLAYWRIGHT_MODULE_PATH) {
    const configured = path.resolve(process.env.PLAYWRIGHT_MODULE_PATH);
    candidates.push(configured, path.join(configured, "playwright"));
  }
  try {
    candidates.push(require.resolve("playwright", { paths: [path.join(__dirname, "browser-smoke"), __dirname, process.cwd()] }));
  } catch {
    // A repository-local install is optional outside CI.
  }
  const codexNodeModules = process.env.CODEX_NODE_MODULES || path.join(
    os.homedir(), ".cache", "codex-runtimes", "codex-primary-runtime", "dependencies", "node", "node_modules");
  candidates.push(path.join(codexNodeModules, "playwright"));
  const failures = [];
  for (const candidate of [...new Set(candidates)]) {
    try {
      return { module: require(candidate), path: candidate };
    } catch (error) {
      failures.push(`${candidate}: ${error.code || error.message}`);
    }
  }
  throw new Error(`Playwright was not found:\n${failures.join("\n")}`);
}

function findBrowserExecutable() {
  return [
    process.env.BROWSER_EXECUTABLE_PATH,
    process.env.EDGE_EXECUTABLE_PATH,
    "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe",
    "C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe",
  ].filter(Boolean).find(candidate => fs.existsSync(candidate));
}
