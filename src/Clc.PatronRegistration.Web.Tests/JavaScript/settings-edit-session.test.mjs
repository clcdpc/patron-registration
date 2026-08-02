import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";

class Control {
    constructor(value = "") {
        this.value = value;
        this.disabled = true;
        this.hidden = false;
        this.dataset = {};
        this.listeners = {};
        this.focused = false;
        this.attributes = {};
        this.type = "text";
        this.textContent = "";
        this.open = false;
        this.classList = {
            values: new Set(),
            toggle: (name, force) => force ? this.classList.values.add(name) : this.classList.values.delete(name),
            contains: (name) => this.classList.values.has(name)
        };
    }
    addEventListener(name, callback) { this.listeners[name] = callback; }
    dispatchEvent(event) { this.listeners[event.type]?.(event); return !event.defaultPrevented; }
    click() { this.listeners.click?.({ target: this, currentTarget: this }); }
    focus() { focused = this; this.focused = true; }
    scrollIntoView(options) { this.scrolledWith = options; }
    reportValidity() { return true; }
    setAttribute(name, value) { this.attributes[name] = String(value); }
    getAttribute(name) { return this.attributes[name] ?? null; }
    removeAttribute(name) { delete this.attributes[name]; }
    close() { this.open = false; this.closeCount = (this.closeCount || 0) + 1; }
}

let focused;
const documentStub = {
    querySelector() { return null; },
    querySelectorAll() { return []; },
    createElement() { return new Control(); }
};
const context = { document: documentStub, window: {}, globalThis: {}, Event };
context.globalThis = context;
vm.runInNewContext(readFileSync(new URL("../../Clc.PatronRegistration.Web/wwwroot/js/settings.js", import.meta.url), "utf8"), context);
const { initializeSettingsContext, initializeRow, updatePendingActions, blockActiveEdit, populateReviewList, handleSaveAttempt } = context.SettingsEditSessions;

function settingsContextFixture() {
    const organization = new Control("branch-2");
    const formCode = new Control("youth");
    organization.disabled = false;
    formCode.disabled = false;
    const submissions = [];
    const form = {
        querySelector(selector) {
            return selector === "#organization-scope" ? organization : formCode;
        },
        requestSubmit() {
            const values = {};
            if (!organization.disabled) values.organizationId = organization.value;
            if (!formCode.disabled) values.formCode = formCode.value;
            submissions.push(values);
        }
    };
    return { form, organization, formCode, submissions };
}

test("settings context changes submit the applicable GET values but initialization does not", () => {
    const organizationChange = settingsContextFixture();
    initializeSettingsContext(organizationChange.form);
    assert.deepEqual(organizationChange.submissions, []);
    organizationChange.organization.listeners.change();
    assert.deepEqual(organizationChange.submissions, [{ organizationId: "branch-2" }]);

    const formChange = settingsContextFixture();
    initializeSettingsContext(formChange.form);
    formChange.formCode.listeners.change();
    assert.deepEqual(formChange.submissions, [{ organizationId: "branch-2", formCode: "youth" }]);
});

test("settings context markup only renders View settings as a noscript fallback", () => {
    const markup = readFileSync(new URL("../../Clc.PatronRegistration.Web/Views/Settings/Index.cshtml", import.meta.url), "utf8");
    assert.doesNotMatch(markup, /<\/noscript>\s*<button[^>]*>View settings<\/button>/);
    assert.match(markup, /<noscript>\s*<button type="submit">View settings<\/button>\s*<\/noscript>/);
    assert.equal((markup.match(/>View settings<\/button>/g) ?? []).length, 1);
});

test("scoped CSS makes every hidden settings element non-rendering", () => {
    const css = readFileSync(new URL("../../Clc.PatronRegistration.Web/wwwroot/css/settings.css", import.meta.url), "utf8");
    assert.match(css, /\.settings-administration-page\s+\[hidden\]\s*\{\s*display:\s*none\s*!important/);
});

function rowFixture({ operation = "Upsert", dirty = false, ownsOverride = true, sensitive = false } = {}) {
    const controls = {
        change: new Control(), inherit: ownsOverride ? new Control() : null,
        apply: new Control(), cancel: new Control(), actions: new Control(),
        editor: new Control(), message: new Control(), operation: new Control(operation),
        value: new Control(sensitive ? "" : "server value"), index: new Control(), key: new Control(),
        reveal: sensitive ? new Control() : null
    };
    if (sensitive) {
        controls.value.type = "password";
        controls.reveal.textContent = "Reveal secret";
        controls.reveal.setAttribute("aria-expanded", "false");
        controls.reveal.setAttribute("aria-label", "Reveal Example");
    }
    const selectors = {
        ".edit-setting": controls.change, ".inherit-setting": controls.inherit,
        ".apply-setting": controls.apply, ".cancel-setting": controls.cancel,
        ".edit-actions": controls.actions, ".value-editor": controls.editor,
        ".inheritance-message": controls.message, ".operation": controls.operation,
        ".setting-value": controls.value, ".change-index": controls.index,
        ".change-key": controls.key, ".reveal-secret": controls.reveal
    };
    const category = { setAttribute(name) { this[name] = true; } };
    const row = {
        dataset: { appliedOperation: operation, dirty: dirty.toString(), displayName: "Example", oldValue: "old", sensitive: sensitive.toString() },
        querySelector(selector) { return selectors[selector]; },
        querySelectorAll() { return [controls.index, controls.key, controls.operation]; },
        closest() { return category; },
        setAttribute(name) { this[name] = true; }
    };
    initializeRow(row);
    return { row, controls, category };
}

function pendingActionsFixture(rowOptions = [{}, {}]) {
    const actions = new Control();
    const status = new Control();
    actions.querySelector = (selector) => selector === ".pending-changes-status" ? status : null;
    const rows = [];
    const form = {
        querySelector(selector) { return selector === ".settings-actions" ? actions : null; },
        querySelectorAll(selector) {
            return selector === '.setting-row[data-dirty="true"]'
                ? rows.filter(({ row }) => row.dataset.dirty === "true").map(({ row }) => row)
                : [];
        }
    };
    for (const options of rowOptions) {
        const fixture = rowFixture(options);
        initializeRow(fixture.row, form);
        rows.push(fixture);
    }
    updatePendingActions(form);
    return { form, actions, status, rows };
}

test("pending actions follow applied dirty rows rather than edit sessions or server draft operations", () => {
    const fixture = pendingActionsFixture([{ operation: "Upsert" }, { operation: "RemoveOverride" }]);
    const [first, second] = fixture.rows;
    assert.equal(fixture.actions.hidden, true);
    assert.equal(fixture.status.textContent, "");

    first.controls.change.click();
    assert.equal(fixture.actions.hidden, true, "entering edit mode is not a pending change");
    first.controls.cancel.click();
    assert.equal(fixture.actions.hidden, true, "cancelling a clean candidate remains clean");

    first.controls.change.click();
    first.controls.apply.click();
    assert.equal(fixture.actions.hidden, false);
    assert.equal(fixture.status.textContent, "1 unsaved browser change");

    first.controls.change.click();
    first.controls.apply.click();
    assert.equal(fixture.status.textContent, "1 unsaved browser change", "reapplying a dirty row does not increment the count");
    first.controls.change.click();
    first.controls.cancel.click();
    assert.equal(fixture.actions.hidden, false, "cancelling a dirty row edit preserves its applied change");
    assert.equal(fixture.status.textContent, "1 unsaved browser change");

    second.controls.change.click();
    second.controls.apply.click();
    assert.equal(fixture.status.textContent, "2 unsaved browser changes");
});

test("failed validation and server-loaded draft operations do not create browser-pending changes", () => {
    const fixture = pendingActionsFixture([{ operation: "Upsert" }]);
    const candidate = fixture.rows[0];
    candidate.controls.change.click();
    candidate.controls.value.reportValidity = () => false;
    candidate.controls.apply.click();
    assert.equal(candidate.row.dataset.dirty, "false");
    assert.equal(fixture.actions.hidden, true);
    assert.equal(fixture.status.textContent, "");
});

test("save actions share the hidden pending region while Remove draft change stays on its row", () => {
    const indexMarkup = readFileSync(new URL("../../Clc.PatronRegistration.Web/Views/Settings/Index.cshtml", import.meta.url), "utf8");
    const rowMarkup = readFileSync(new URL("../../Clc.PatronRegistration.Web/Views/Settings/_SettingRow.cshtml", import.meta.url), "utf8");
    const actions = indexMarkup.match(/<div class="settings-actions" hidden>[\s\S]*?<\/div>\s*<\/div>/)?.[0] ?? "";
    assert.match(actions, /Save \{count\} \{noun\} live/);
    assert.match(actions, /Add \{count\} \{noun\} to shared draft/);
    assert.match(actions, /Discard unsaved changes/);
    assert.match(actions, /role="status" aria-live="polite"/);
    assert.doesNotMatch(actions, /Remove draft change/);
    assert.match(rowMarkup, /Remove draft change/);
});

test("unsaved-work dialog offers only discard and keep editing while saves remain in settings actions", () => {
    const markup = readFileSync(new URL("../../Clc.PatronRegistration.Web/Views/Settings/Index.cshtml", import.meta.url), "utf8");
    const dialog = markup.match(/<dialog id="unsaved-changes-dialog"[\s\S]*?<\/dialog>/)?.[0] ?? "";
    const actions = markup.match(/<div class="settings-actions" hidden>[\s\S]*?<\/form>/)?.[0] ?? "";

    assert.match(dialog, /data-guard-discard/);
    assert.match(dialog, /Discard changes and continue/);
    assert.match(dialog, /data-dialog-cancel/);
    assert.match(dialog, /Keep editing/);
    const buttons = [...dialog.matchAll(/<button[\s\S]*?<\/button>/g)].map((match) => match[0]);
    assert.equal(buttons.length, 2);
    assert.equal(buttons.some((button) => /save/i.test(button)), false);
    assert.doesNotMatch(markup, /data-guard-save-(?:live|draft)/);
    assert.match(actions, /Save \{count\} \{noun\} live/);
    assert.match(actions, /Add \{count\} \{noun\} to shared draft/);
});

test("unsaved-work dialog has no save handlers or save submission workflow", () => {
    const script = readFileSync(new URL("../../Clc.PatronRegistration.Web/wwwroot/js/settings.js", import.meta.url), "utf8");
    assert.doesNotMatch(script, /data-guard-save-(?:live|draft)/);
});

test("keeping edits closes the unsaved-work dialog without discarding browser changes", () => {
    const script = readFileSync(new URL("../../Clc.PatronRegistration.Web/wwwroot/js/settings.js", import.meta.url), "utf8");
    const cancellationStart = script.indexOf('document.querySelectorAll("[data-dialog-cancel]")');
    const cancellationEnd = script.indexOf('document.querySelector("[data-guard-discard]")', cancellationStart);
    const cancellationHandler = script.slice(cancellationStart, cancellationEnd);
    assert.match(cancellationHandler, /cancelWorkflowDialog/);
    assert.doesNotMatch(cancellationHandler, /discardPendingChanges|requestSubmit|location\./);
});

test("active edits block both save paths and cannot enter review", () => {
    for (const dirty of [false, true]) {
        const fixture = rowFixture({ dirty });
        fixture.controls.change.click();
        const status = new Control();
        const form = { querySelector: () => fixture.row };
        assert.equal(blockActiveEdit(form, status), true);
        assert.match(status.textContent, /Apply or Cancel/);
        assert.equal(focused, fixture.controls.apply);
        assert.equal(fixture.row.open, true);
    }

    const fixture = rowFixture();
    fixture.controls.change.click();
    fixture.controls.value.value = "unapplied candidate";
    const list = { children: [], replaceChildren() { this.children = []; }, append(item) { this.children.push(item); } };
    const form = { querySelector: () => fixture.row, querySelectorAll: () => [] };
    const dialog = { querySelector: () => list, showModal() { this.open = true; } };
    const directEvent = { submitter: new Control(), preventDefault() { this.prevented = true; } };
    assert.equal(handleSaveAttempt(directEvent, form, new Control(), dialog, false), "blocked");
    assert.equal(directEvent.prevented, true);
    const draft = new Control();
    draft.dataset.submitKind = "draft";
    const draftEvent = { submitter: draft, preventDefault() { this.prevented = true; } };
    assert.equal(handleSaveAttempt(draftEvent, form, new Control(), dialog, false), "blocked");
    assert.equal(draftEvent.prevented, true);
    populateReviewList(form, list);
    assert.equal(list.children.length, 0);
    assert.notEqual(dialog.open, true);
});

test("Cancel restores already-applied Upsert and RemoveOverride states", () => {
    const upsert = rowFixture({ operation: "Upsert", dirty: true });
    upsert.controls.operation.disabled = false;
    upsert.controls.value.disabled = false;
    upsert.controls.change.click();
    upsert.controls.value.value = "candidate";
    upsert.controls.cancel.click();
    assert.equal(upsert.controls.operation.value, "Upsert");
    assert.equal(upsert.controls.value.value, "server value");
    assert.equal(upsert.row.dataset.dirty, "true");
    assert.equal(upsert.controls.inherit.hidden, false);

    const removal = rowFixture({ operation: "RemoveOverride", dirty: true });
    removal.controls.change.click();
    removal.controls.cancel.click();
    assert.equal(removal.controls.operation.value, "RemoveOverride");
    assert.equal(removal.controls.inherit.hidden, true);
});

test("server RemoveOverride can be replaced by Upsert and Apply restores focus", () => {
    const fixture = rowFixture({ operation: "RemoveOverride" });
    assert.equal(fixture.controls.inherit.hidden, true);
    fixture.controls.change.click();
    fixture.controls.value.value = "replacement";
    fixture.controls.apply.click();
    assert.equal(fixture.controls.operation.value, "Upsert");
    assert.equal(fixture.row.dataset.dirty, "true");
    assert.equal(fixture.controls.inherit.hidden, false);
    assert.equal(focused, fixture.controls.change);
});

test("Apply and Cancel are harmless without an active session", () => {
    const fixture = rowFixture({ operation: "Upsert" });
    fixture.controls.apply.click();
    fixture.controls.cancel.click();
    assert.equal(fixture.controls.operation.value, "Upsert");
    assert.equal(fixture.row.dataset.dirty, "false");
    assert.equal(fixture.row.dataset.candidateOperation, undefined);
});

test("review uses only an applied operation and masks sensitive values", () => {
    const fixture = rowFixture({ dirty: true });
    fixture.controls.value.value = "applied value";
    const list = { children: [], replaceChildren() { this.children = []; }, append(item) { this.children.push(item); } };
    const form = { querySelectorAll: () => [fixture.row] };
    populateReviewList(form, list);
    assert.match(list.children[0].textContent, /applied value/);
    fixture.row.dataset.sensitive = "true";
    populateReviewList(form, list);
    assert.doesNotMatch(list.children[0].textContent, /applied value/);
    assert.match(list.children[0].textContent, /••••••••/);
});

test("context selectors retain committed values and can be restored when navigation is cancelled", () => {
    const { setNavigationGuard } = context.SettingsEditSessions;
    for (const selectorName of ["organization", "formCode"]) {
        const fixture = settingsContextFixture();
        fixture.organization.value = "branch-1";
        fixture.formCode.value = "default";
        initializeSettingsContext(fixture.form);
        const control = fixture[selectorName];
        control.value = selectorName === "organization" ? "branch-2" : "kids";
        setNavigationGuard((_action, trigger) => {
            trigger.value = trigger.dataset.committedValue;
        });
        control.listeners.change();
        assert.equal(control.value, selectorName === "organization" ? "branch-1" : "default");
        assert.equal(fixture.submissions.length, 0);
    }
    setNavigationGuard(null);
});

test("guarded row action is marked separately and dirty mutations are never posted with it", () => {
    const markup = readFileSync(new URL("../../Clc.PatronRegistration.Web/Views/Settings/_SettingRow.cshtml", import.meta.url), "utf8");
    const script = readFileSync(new URL("../../Clc.PatronRegistration.Web/wwwroot/js/settings.js", import.meta.url), "utf8");
    assert.match(markup, /Remove draft change[\s\S]*data-submit-kind="guarded"|data-submit-kind="guarded"[\s\S]*Remove draft change/);
    assert.match(script, /kind === "guarded"/);
    assert.match(script, /disableDirtyMutations/);
    assert.match(script, /\.setting-row\[data-dirty=\\?"true\\?"\]/);
});

test("preview mode pipeline distinguishes safe, safe-to-live, and live-to-safe actions", () => {
    const { needsLiveConfirmation } = context.SettingsWorkflow;
    const preview = (selected) => ({
        dataset: {}, matches: (value) => value === "[data-preview-form]",
        querySelector: () => ({ value: selected })
    });
    assert.equal(needsLiveConfirmation(preview("false")), false);
    assert.equal(needsLiveConfirmation(preview("true")), true);
    assert.equal(needsLiveConfirmation({ dataset: { requiresLiveConfirm: "true" }, matches: () => false }), true);
    assert.equal(needsLiveConfirmation({ dataset: { requiresLiveConfirm: "false" }, matches: () => false }), false);
});

test("search result wording distinguishes settings from saved draft changes", () => {
    const script = readFileSync(new URL("../../Clc.PatronRegistration.Web/wwwroot/js/settings.js", import.meta.url), "utf8");
    assert.match(script, /draft .*change.*found/);
    assert.match(script, /setting.*found/);
    assert.doesNotMatch(script, /browser.*dirty.*draft/i);
});

test("publish and discard cancellation returns focus to their trigger", () => {
    const script = readFileSync(new URL("../../Clc.PatronRegistration.Web/wwwroot/js/settings.js", import.meta.url), "utf8");
    assert.match(script, /owner\._trigger\?\.focus\(\)/);
    assert.match(script, /pending = null;\s*submitting = false/);
});

test("clipboard copy reports accessible success and failure", async () => {
    const { copyPreviewUrl } = context.SettingsWorkflow;
    const status = { textContent: "" };
    assert.equal(await copyPreviewUrl({ writeText: async () => {} }, "url", status), true);
    assert.equal(status.textContent, "Preview URL copied.");
    assert.equal(await copyPreviewUrl({ writeText: async () => { throw new Error("denied"); } }, "url", status), false);
    assert.match(status.textContent, /Copy failed/);
});

test("beforeunload remains conditional on submission state after dialog cancellation", () => {
    const script = readFileSync(new URL("../../Clc.PatronRegistration.Web/wwwroot/js/settings.js", import.meta.url), "utf8");
    assert.match(script, /beforeunload/);
    assert.match(script, /if \(!submitting && \(dirtyCount\(\) \|\| hasCandidate\(\)\)\)/);
    assert.match(script, /pending = null;\s*submitting = false/);
});

test("discardPendingChanges restores dirty rows to their server-rendered controls and count", () => {
    const fixture = pendingActionsFixture([{}]);
    const row = fixture.rows[0];
    const original = {
        operation: row.controls.operation.value,
        value: row.controls.value.value,
        valueDisabled: row.controls.value.disabled,
        operationDisabled: row.controls.operation.disabled
    };
    row.controls.change.click();
    row.controls.value.value = "browser-only value";
    row.controls.apply.click();
    assert.equal(row.row.dataset.dirty, "true");

    context.SettingsWorkflow.discardPendingChanges(fixture.form);

    assert.equal(row.row.dataset.dirty, "false");
    assert.equal(row.row.dataset.candidateOperation, undefined);
    assert.equal(row.controls.operation.value, original.operation);
    assert.equal(row.controls.value.value, original.value);
    assert.equal(row.controls.value.disabled, original.valueDisabled);
    assert.equal(row.controls.operation.disabled, original.operationDisabled);
    assert.equal(fixture.actions.hidden, true);
    assert.equal(fixture.status.textContent, "");
});

test("discard-and-continue pipeline cleans rows before subsequent lifecycle confirmation", () => {
    const script = readFileSync(new URL("../../Clc.PatronRegistration.Web/wwwroot/js/settings.js", import.meta.url), "utf8");
    const handler = [...script.matchAll(/document\.querySelector\("\[data-guard-discard\]"\)[\s\S]*?\n    \}\);/g)].at(-1)?.[0] ?? "";
    assert.match(handler, /discardPendingChanges\(\)/);
    assert.match(handler, /continuePipeline\(action, true\)/);
    assert.doesNotMatch(handler, /disableDirtyMutations/);
});

test("native dialog cancel restores context, focus, and workflow state", () => {
    const { bindDialogCancellation, setWorkflowState, workflowState } = context.SettingsWorkflow;
    for (const committed of ["branch-1", "default-form"]) {
        const trigger = new Control("changed-value");
        trigger.dataset.committedValue = committed;
        const owner = new Control();
        owner.open = true;
        owner._trigger = trigger;
        bindDialogCancellation(owner);
        setWorkflowState({ pending: { action: true }, submitting: true, approved: true });
        const event = new Event("cancel", { cancelable: true });

        owner.dispatchEvent(event);

        assert.equal(event.defaultPrevented, true);
        assert.equal(owner.open, false);
        assert.equal(owner.closeCount, 1);
        assert.equal(trigger.value, committed);
        assert.equal(focused, trigger);
        assert.equal(workflowState().pending, null);
        assert.equal(workflowState().submitting, false);
    }
});

test("Escape from live, publish, and discard dialogs never submits and leaves controls usable", () => {
    const { bindDialogCancellation, setWorkflowState, workflowState } = context.SettingsWorkflow;
    for (const kind of ["live", "publish", "discard"]) {
        const trigger = new Control();
        const owner = new Control();
        owner.open = true;
        owner._trigger = trigger;
        owner.kind = kind;
        owner.submissions = 0;
        bindDialogCancellation(owner);
        setWorkflowState({ pending: { form: owner }, submitting: false, approved: false });

        owner.dispatchEvent(new Event("cancel", { cancelable: true }));

        assert.equal(owner.submissions, 0);
        assert.equal(workflowState().pending, null);
        assert.equal(workflowState().submitting, false);
        assert.equal(focused, trigger);
    }
});

test("discarding a revealed sensitive edit restores password and accessible reveal state", () => {
    const fixture = pendingActionsFixture([{ sensitive: true }]);
    const row = fixture.rows[0];
    row.controls.change.click();
    row.controls.value.value = "replacement secret";
    row.controls.value.type = "text";
    row.controls.reveal.textContent = "Hide secret";
    row.controls.reveal.setAttribute("aria-expanded", "true");
    row.controls.reveal.setAttribute("aria-label", "Hide Example");
    row.controls.apply.click();

    context.SettingsWorkflow.discardPendingChanges(fixture.form);

    assert.equal(row.controls.value.type, "password");
    assert.equal(row.controls.value.value, "");
    assert.equal(row.controls.reveal.textContent, "Reveal secret");
    assert.equal(row.controls.reveal.getAttribute("aria-expanded"), "false");
    assert.equal(row.controls.reveal.getAttribute("aria-label"), "Reveal Example");
});

test("shared draft filter markup and CSS keep the checkbox intrinsic", () => {
    const markup = readFileSync(new URL("../../Clc.PatronRegistration.Web/Views/Settings/Index.cshtml", import.meta.url), "utf8");
    const css = readFileSync(new URL("../../Clc.PatronRegistration.Web/wwwroot/css/settings.css", import.meta.url), "utf8");
    assert.match(markup, /id="draft-only-filter"[\s\S]*Show shared draft changes only/);
    assert.match(css, /\.settings-search input\[type="search"\]/);
    assert.doesNotMatch(css, /\.settings-search input\s*\{/);
    assert.match(css, /\.inline-check input\[type="checkbox"\][\s\S]*width:\s*auto/);
});

test("draft summaries identify proposed values without automatically opening rows", () => {
    const row = readFileSync(new URL("../../Clc.PatronRegistration.Web/Views/Settings/_SettingRow.cshtml", import.meta.url), "utf8");
    assert.match(row, /hasDraftOperation \? \$"Draft: \{presentation\.Value\}"/);
    assert.match(row, /definition\.IsSensitive/);
    assert.doesNotMatch(row, /<details class="setting-row"[^>]*open=/);
});

test("review action is conditional, client-side, and preserves the search query", () => {
    const markup = readFileSync(new URL("../../Clc.PatronRegistration.Web/Views/Settings/Index.cshtml", import.meta.url), "utf8");
    const script = readFileSync(new URL("../../Clc.PatronRegistration.Web/wwwroot/js/settings.js", import.meta.url), "utf8");
    assert.match(markup, /if \(draftChangeCount > 0\)[\s\S]*data-review-draft>Review @draftChangeCount shared draft/);
    assert.match(script, /function reviewDraftChanges\(\)[\s\S]*draftOnly\.checked = true;[\s\S]*applyFilters\(\)/);
    assert.doesNotMatch(script.match(/function reviewDraftChanges\(\)[\s\S]*?\n    \}/)?.[0] ?? "", /search\.value\s*=/);
    assert.match(script, /scrollIntoView/);
});

test("filter sessions capture and restore disclosure state and report empty results", () => {
    const script = readFileSync(new URL("../../Clc.PatronRegistration.Web/wwwroot/js/settings.js", import.meta.url), "utf8");
    assert.match(script, /filtering && preFilterDisclosure === null/);
    assert.match(script, /new Map\(categories\.map/);
    assert.match(script, /preFilterDisclosure\.forEach/);
    assert.match(script, /preFilterDisclosure = null/);
    assert.match(script, /No settings match your search\./);
    assert.match(script, /No shared draft changes match your search\./);
});

test("explicit browser discard uses restoration rather than reload or submission", () => {
    const script = readFileSync(new URL("../../Clc.PatronRegistration.Web/wwwroot/js/settings.js", import.meta.url), "utf8");
    const handler = script.match(/document\.querySelector\("\[data-discard-pending\]"\)[\s\S]*?\n    \}\);/)?.[0] ?? "";
    assert.match(handler, /explicitDiscard/);
    assert.match(handler, /showModal/);
    assert.doesNotMatch(handler, /reload|requestSubmit|location\./);
    assert.match(script, /action\?\.explicitDiscard[\s\S]*discardPendingChanges|discardPendingChanges\(\)[\s\S]*action\?\.explicitDiscard/);
});

function filteringWorkflowFixture() {
    const search = new Control();
    const status = new Control();
    const draftOnly = new Control();
    draftOnly.checked = false;
    const searchRegion = new Control();
    const review = new Control();
    const makeRow = (searchText, draftChange) => {
        const summary = new Control();
        return {
            dataset: { search: searchText, draftChange: draftChange.toString() },
            hidden: false,
            focused: false,
            querySelector(selector) { return selector === "summary" ? summary : null; },
            focus() { this.focused = true; focused = this; },
            summary
        };
    };
    const alpha = makeRow("alpha setting", true);
    const beta = makeRow("beta setting", false);
    const categories = [
        { open: false, hidden: false, rows: [alpha], querySelector() { return this.rows.find((row) => !row.hidden) || null; } },
        { open: true, hidden: false, rows: [beta], querySelector() { return this.rows.find((row) => !row.hidden) || null; } }
    ];
    let initialized = false;
    const doc = {
        querySelector(selector) {
            if (selector === "#setting-search") return search;
            if (selector === "#search-status") return status;
            if (selector === "#draft-only-filter") return draftOnly;
            if (selector === ".settings-search") return searchRegion;
            if (selector === "[data-review-draft]") return review;
            if (selector === '.setting-row[data-draft-change="true"]:not([hidden])') return [alpha, beta].find((row) => row.dataset.draftChange === "true" && !row.hidden) || null;
            return null;
        },
        querySelectorAll(selector) {
            if (selector === ".setting-category, .dynamic-settings") return categories;
            if (selector === ".setting-row") return initialized ? [alpha, beta] : [];
            return [];
        },
        createElement() { return new Control(); }
    };
    const sandbox = { document: doc, window: { addEventListener() {} }, globalThis: {}, Event, Map, Set };
    sandbox.globalThis = sandbox;
    vm.runInNewContext(readFileSync(new URL("../../Clc.PatronRegistration.Web/wwwroot/js/settings.js", import.meta.url), "utf8"), sandbox);
    initialized = true;
    return { ...sandbox.SettingsWorkflow, search, status, draftOnly, searchRegion, review, alpha, beta, categories };
}

test("applyFilters behavior renders one live message and restores pre-filter disclosures", () => {
    const fixture = filteringWorkflowFixture();
    fixture.search.value = "missing";
    assert.equal(fixture.applyFilters(), 0);
    assert.equal(fixture.status.textContent, "No settings match your search.");
    assert.equal(fixture.status.classList.contains("settings-filter-empty"), true);
    assert.equal(fixture.categories.every((category) => category.hidden), true);

    fixture.search.value = "alpha";
    assert.equal(fixture.applyFilters(), 1);
    assert.equal(fixture.categories[0].open, true);
    assert.equal(fixture.categories[0].hidden, false);
    assert.equal(fixture.categories[1].hidden, true);

    // Filtering started with [closed, open]; later keystrokes must not overwrite that snapshot.
    fixture.search.value = "";
    fixture.applyFilters();
    assert.deepEqual(fixture.categories.map((category) => category.open), [false, true]);
    assert.equal(fixture.status.textContent, "");
    assert.equal(fixture.status.classList.contains("settings-filter-empty"), false);
});

test("draft and text filters compose and review focuses the matching summary", () => {
    const fixture = filteringWorkflowFixture();
    fixture.search.value = "alpha";
    fixture.reviewDraftChanges();
    assert.equal(fixture.draftOnly.checked, true);
    assert.equal(fixture.search.value, "alpha");
    assert.equal(fixture.alpha.hidden, false);
    assert.equal(fixture.beta.hidden, true);
    assert.equal(focused, fixture.alpha.summary);
    assert.equal(fixture.alpha.focused, false, "the details container must not receive focus");
    assert.equal(fixture.searchRegion.scrolledWith.behavior, "smooth");
    assert.equal(fixture.searchRegion.scrolledWith.block, "start");

    fixture.search.value = "beta";
    fixture.reviewDraftChanges();
    assert.equal(fixture.status.textContent, "No shared draft changes match your search.");
    assert.equal(focused, fixture.draftOnly);
});

test("clearEditSessionStatus removes a resolved active-edit warning", () => {
    const warning = new Control();
    warning.hidden = false;
    warning.textContent = "Apply or Cancel the active setting edit before saving.";
    const doc = {
        querySelector(selector) { return selector === "#edit-session-status" ? warning : null; },
        querySelectorAll() { return []; },
        createElement() { return new Control(); }
    };
    const sandbox = { document: doc, window: { addEventListener() {} }, globalThis: {}, Event, Map, Set };
    sandbox.globalThis = sandbox;
    vm.runInNewContext(readFileSync(new URL("../../Clc.PatronRegistration.Web/wwwroot/js/settings.js", import.meta.url), "utf8"), sandbox);
    const row = { dataset: { dirty: "true", candidateOperation: "Upsert" }, _discardPendingChange() { this.dataset.dirty = "false"; delete this.dataset.candidateOperation; } };
    const form = {
        querySelector(selector) { return selector.includes("candidate-operation") && row.dataset.candidateOperation ? row : null; },
        querySelectorAll(selector) { return selector.includes("data-dirty") || selector.includes("candidate-operation") ? [row] : []; }
    };
    sandbox.SettingsWorkflow.discardPendingChanges(form);
    assert.equal(row.dataset.dirty, "false");
    assert.equal(row.dataset.candidateOperation, undefined);
    assert.equal(warning.hidden, true);
    assert.equal(warning.textContent, "");
});
