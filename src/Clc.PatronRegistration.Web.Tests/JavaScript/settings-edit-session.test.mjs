import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";

const settingsScript = readFileSync(new URL("../../Clc.PatronRegistration.Web/wwwroot/js/settings.js", import.meta.url), "utf8");
const settingsIndex = readFileSync(new URL("../../Clc.PatronRegistration.Web/Views/Settings/Index.cshtml", import.meta.url), "utf8");
const settingRowMarkup = readFileSync(new URL("../../Clc.PatronRegistration.Web/Views/Settings/_SettingRow.cshtml", import.meta.url), "utf8");
const batchRowMarkup = readFileSync(new URL("../../Clc.PatronRegistration.Web/Views/Settings/_BatchSettingRow.cshtml", import.meta.url), "utf8");
const settingsCss = readFileSync(new URL("../../Clc.PatronRegistration.Web/wwwroot/css/settings.css", import.meta.url), "utf8");

let focused;

class NodeStub {
    constructor(value = "") {
        this.value = value;
        this.textContent = "";
        this.disabled = true;
        this.hidden = false;
        this.open = false;
        this.checked = false;
        this.dataset = {};
        this.attributes = {};
        this.listeners = {};
        this.children = [];
        this.parentElement = null;
        this.tagName = "DIV";
        this.classList = {
            values: new Set(),
            toggle: (name, force) => {
                const enabled = force === undefined ? !this.classList.values.has(name) : Boolean(force);
                if (enabled) this.classList.values.add(name); else this.classList.values.delete(name);
                return enabled;
            },
            contains: (name) => this.classList.values.has(name)
        };
    }

    addEventListener(name, callback) {
        (this.listeners[name] ||= []).push(callback);
    }

    emit(name, event = {}) {
        const current = { type: name, target: this, currentTarget: this, ...event };
        for (const callback of this.listeners[name] || []) callback(current);
        return !current.defaultPrevented;
    }

    click() { return this.emit("click"); }
    focus() { focused = this; this.focused = true; }
    setAttribute(name, value) {
        this.attributes[name] = String(value);
        if (name === "open") this.open = true;
    }
    getAttribute(name) { return this.attributes[name] ?? null; }
    removeAttribute(name) { delete this.attributes[name]; }
    append(...nodes) { nodes.forEach((node) => { if (!node) return; this.children.push(node); node.parentElement = this; }); }
    appendChild(node) { this.append(node); return node; }
    insertBefore(node, before) {
        const index = before ? this.children.indexOf(before) : -1;
        if (index < 0) this.append(node);
        else { this.children.splice(index, 0, node); node.parentElement = this; }
    }
    removeChild(node) {
        const index = this.children.indexOf(node);
        if (index >= 0) this.children.splice(index, 1);
        node.parentElement = null;
    }
    remove() { this.parentElement?.removeChild(this); }
    replaceChildren(...nodes) { this.children = []; this.append(...nodes); }
    querySelector() { return null; }
    querySelectorAll() { return []; }
    closest() { return null; }
    getBoundingClientRect() { return { top: 0, bottom: 0 }; }
}

function createDocument(overrides = {}) {
    const document = {
        documentElement: { clientHeight: 0 },
        querySelector(selector) { return overrides.querySelector?.(selector) ?? null; },
        querySelectorAll(selector) { return overrides.querySelectorAll?.(selector) ?? []; },
        createElement(tagName) {
            const element = new NodeStub();
            element.tagName = tagName.toUpperCase();
            return element;
        }
    };
    return document;
}

function loadSettings(document = createDocument()) {
    const context = {
        document,
        window: { addEventListener() {} },
        navigator: {},
        console,
        setTimeout,
        clearTimeout,
        FormData: class { append() {} },
        fetch: async () => ({ ok: false, json: async () => ({}) }),
        globalThis: null
    };
    context.globalThis = context;
    vm.runInNewContext(settingsScript, context, { filename: "settings.js" });
    return context;
}

function makeForm(rows = []) {
    const actions = new NodeStub();
    const pendingStatus = new NodeStub();
    actions.querySelector = (selector) => selector === ".pending-changes-status" ? pendingStatus : null;
    actions.querySelectorAll = () => [];
    return {
        actions,
        pendingStatus,
        addEventListener() {},
        querySelector(selector) {
            if (selector === ".settings-actions") return actions;
            if (selector.includes('data-image-needs-upload="true"')) return rows.find((row) => row.dataset.imageNeedsUpload === "true") || null;
            return null;
        },
        querySelectorAll(selector) {
            if (selector === '.setting-row[data-dirty="true"]') return rows.filter((row) => row.dataset.dirty === "true");
            if (selector.includes('data-image-uploading="true"')) return rows.filter((row) => row.dataset.imageUploading === "true");
            return [];
        }
    };
}

function makeRow({
    key = "registration_text",
    valueType = "shortstring",
    baselineMode = "customize",
    baselineValue = "original",
    value = baselineValue,
    sensitive = false,
    inherited = false,
    batch = false
} = {}) {
    const category = new NodeStub();
    category.tagName = "DETAILS";
    const row = new NodeStub();
    row.tagName = batch ? "TR" : "DETAILS";
    row.open = false;
    row.dataset = {
        settingKey: key,
        displayName: key,
        valueType,
        sensitive: String(sensitive),
        baselineMode,
        baselineValue,
        baselineOperation: baselineMode === "inherit" ? "RemoveOverride" : "Upsert",
        customizedHere: baselineMode === "customize" ? "true" : "false",
        presentationState: baselineMode === "inherit" ? (inherited ? "inherited" : "notset") : "customized",
        liveState: baselineMode === "inherit" ? (inherited ? "inherited" : "notset") : "customized",
        draftChange: "false",
        hasInherited: String(inherited),
        inheritedSummary: inherited ? "Inherited value" : "",
        inheritedSource: inherited ? "Main Library" : "",
        liveSummary: baselineMode === "inherit" ? "Inherited value" : baselineValue
    };
    const inherit = new NodeStub();
    inherit.value = "inherit";
    inherit.dataset = { mode: "inherit" };
    inherit.checked = baselineMode === "inherit";
    const customize = new NodeStub();
    customize.value = "customize";
    customize.dataset = { mode: "customize" };
    customize.checked = baselineMode === "customize";
    let modes = [inherit, customize];
    if (valueType === "boolean") {
        const yes = new NodeStub();
        yes.value = "true";
        yes.dataset = { mode: "customize", customValue: "true" };
        yes.checked = baselineMode === "customize" && String(baselineValue).toLowerCase() === "true";
        const no = new NodeStub();
        no.value = "false";
        no.dataset = { mode: "customize", customValue: "false" };
        no.checked = baselineMode === "customize" && String(baselineValue).toLowerCase() === "false";
        modes = [inherit, yes, no];
    }
    const visible = new NodeStub(value);
    visible.disabled = false;
    visible.type = "text";
    const binding = new NodeStub(baselineValue);
    binding.disabled = true;
    const operation = new NodeStub(baselineMode === "inherit" ? "RemoveOverride" : "Upsert");
    const index = new NodeStub();
    const keyControl = new NodeStub();
    const summary = batch ? null : new NodeStub();
    if (summary) { summary.textContent = baselineMode === "inherit" ? "Inherited value" : String(baselineValue || "Blank"); summary.setAttribute("title", summary.textContent); }
    const status = batch ? null : new NodeStub();
    if (status) status.textContent = baselineMode === "inherit" ? "Inherited" : "Customized here";
    const batchStatus = batch ? new NodeStub() : null;
    const prefixEditor = valueType === "ip-prefixes" ? new NodeStub() : null;
    const prefixes = valueType === "ip-prefixes" ? String(value || "").split(";").filter(Boolean).map((entry) => new NodeStub(entry)) : [];
    const addPrefix = valueType === "ip-prefixes" ? new NodeStub() : null;
    const prefixRows = [];
    const removeButtons = [];
    if (prefixEditor) {
        prefixEditor.insertBefore = (wrapper) => {
            const input = wrapper.children[0];
            prefixes.push(input);
            prefixRows.push(wrapper);
            removeButtons.push(wrapper.children[1]);
            prefixEditor.children.push(wrapper);
            wrapper.parentElement = prefixEditor;
        };
        prefixEditor.removeChild = (wrapper) => {
            const input = wrapper.children[0];
            const indexToRemove = prefixes.indexOf(input);
            if (indexToRemove >= 0) prefixes.splice(indexToRemove, 1);
            const rowIndex = prefixRows.indexOf(wrapper);
            if (rowIndex >= 0) prefixRows.splice(rowIndex, 1);
            const removeIndex = removeButtons.indexOf(wrapper.children[1]);
            if (removeIndex >= 0) removeButtons.splice(removeIndex, 1);
            const childIndex = prefixEditor.children.indexOf(wrapper);
            if (childIndex >= 0) prefixEditor.children.splice(childIndex, 1);
            wrapper.parentElement = null;
        };
        prefixes.forEach((input) => {
            const wrapper = new NodeStub();
            wrapper.className = "ip-prefix-row";
            const remove = new NodeStub();
            wrapper.append(input, remove);
            wrapper.parentElement = prefixEditor;
            removeButtons.push(remove);
            prefixRows.push(wrapper);
            prefixEditor.children.push(wrapper);
            remove.closest = () => wrapper;
        });
        prefixEditor.querySelectorAll = (query) => query === ".ip-prefix-row" ? prefixRows : [];
    }
    const selector = {
        ".setting-mode": modes,
        ".setting-value:not(.setting-value-binding)": visible,
        ".setting-value": visible,
        ".setting-value-binding": binding,
        ".operation": operation,
        ".change-index": index,
        ".change-key": keyControl,
        ".summary-value": summary,
        ".setting-status > span": null,
        ".setting-status": status,
        ".batch-browser-status": batchStatus,
        "[data-ip-prefix-editor]": prefixEditor,
        ".ip-prefix-add": addPrefix
    };
    row.querySelector = (query) => {
        if (query === ".setting-status > span") return null;
        if (query.includes(".setting-value:not")) return visible;
        if (query === ".value-editor .setting-value:not(.setting-value-binding)") return visible;
        return selector[query] ?? null;
    };
    row.querySelectorAll = (query) => {
        if (query === ".setting-mode") return modes;
        if (query === ".ip-prefix-input") return prefixes;
        if (query === ".ip-prefix-remove") return removeButtons;
        if (query === ".ip-prefix-add, .ip-prefix-remove") return [...removeButtons, addPrefix].filter(Boolean);
        if (query === ".ip-prefix-row") return prefixRows;
        if (query.includes(".value-editor .setting-value") || query.includes(".batch-label-input")) return valueType === "ip-prefixes" ? prefixes : [visible];
        if (query === ".change-index") return [index];
        if (query === ".change-key") return [keyControl];
        if (query === ".operation") return [operation];
        if (query === ".setting-value-binding") return [binding];
        return [];
    };
    row.closest = () => category;
    category.querySelectorAll = () => [row];
    category.querySelector = () => null;
    const originalAddPrefix = addPrefix;
    if (originalAddPrefix) {
        originalAddPrefix.parentElement = prefixEditor;
    }
    return {
        row,
        category,
        modes,
        inherit,
        customize: valueType === "boolean" ? modes[1] : customize,
        booleanYes: valueType === "boolean" ? modes[1] : null,
        booleanNo: valueType === "boolean" ? modes[2] : null,
        visible,
        binding,
        operation,
        summary,
        status,
        batchStatus,
        prefixes,
        addPrefix,
        removeButtons,
        prefixEditor
    };
}

function chooseMode(fixture, desiredMode, desiredValue) {
    fixture.modes.forEach((mode) => { mode.checked = false; });
    const selected = fixture.modes.find((mode) => {
        const modeName = mode.dataset.mode || (mode.value === "inherit" ? "inherit" : "customize");
        return modeName === desiredMode && (desiredMode !== "customize" || desiredValue === undefined || mode.dataset.customValue === undefined || mode.dataset.customValue === desiredValue);
    });
    assert.ok(selected, `expected ${desiredMode} mode to exist`);
    selected.checked = true;
    selected.emit("change");
    return selected;
}

test("ordinary settings use direct semantic dirty state and no candidate edit session", () => {
    const api = loadSettings();
    const first = makeRow({ baselineValue: "original", value: "original" });
    const second = makeRow({ key: "second", baselineValue: "other", value: "other" });
    const form = makeForm([first.row, second.row]);
    api.SettingsEditor.initializeStandardRow(first.row, form);
    api.SettingsEditor.initializeStandardRow(second.row, form);

    assert.equal(first.row.dataset.dirty, "false");
    assert.equal(form.actions.hidden, true);
    first.visible.emit("input");
    assert.equal(first.row.dataset.dirty, "false", "touching an unchanged editor is a no-op");

    first.visible.value = "changed";
    first.visible.emit("input");
    assert.equal(first.row.dataset.dirty, "true");
    assert.equal(first.binding.disabled, false);
    assert.equal(first.operation.value, "Upsert");
    assert.equal(first.binding.value, "changed");
    assert.equal(first.summary.textContent, "Unsaved: changed");
    assert.equal(first.status.textContent, "Unsaved in this browser");
    assert.equal(form.pendingStatus.textContent, "1 change unsaved in this browser");

    first.visible.value = "original";
    first.visible.emit("input");
    assert.equal(first.row.dataset.dirty, "false", "returning to the baseline clears the row");
    assert.equal(first.binding.disabled, true);
    assert.equal(form.actions.hidden, true, "the pending bar disappears after the last change is reverted");
});

test("a server-loaded shared draft is the browser baseline and browser changes stay separate", () => {
    const api = loadSettings();
    const draft = makeRow({ key: "drafted", baselineMode: "customize", baselineValue: "draft value", value: "draft value" });
    draft.row.dataset.draftChange = "true";
    const browserOnly = makeRow({ key: "browser-only", baselineValue: "live value", value: "live value" });
    const form = makeForm([draft.row, browserOnly.row]);
    api.SettingsEditor.initializeStandardRow(draft.row, form);
    api.SettingsEditor.initializeStandardRow(browserOnly.row, form);

    assert.equal(draft.row.dataset.dirty, "false", "the loaded draft is server state, not browser-unsaved work");
    browserOnly.visible.value = "new live value";
    browserOnly.visible.emit("input");
    assert.equal(draft.row.dataset.dirty, "false");
    assert.equal(browserOnly.row.dataset.dirty, "true");
    assert.equal(form.pendingStatus.textContent, "1 change unsaved in this browser");
});

test("inheritance modes compare semantically and preserve explicit blank customizations", () => {
    const api = loadSettings();
    const inherited = makeRow({ key: "inherited", baselineMode: "inherit", baselineValue: "", value: "library value", inherited: true });
    const customized = makeRow({ key: "customized", baselineMode: "customize", baselineValue: "local value", value: "local value", inherited: true });
    const blank = makeRow({ key: "blank", baselineMode: "customize", baselineValue: "", value: "", inherited: true });
    const form = makeForm([inherited.row, customized.row, blank.row]);
    [inherited, customized, blank].forEach((fixture) => api.SettingsEditor.initializeStandardRow(fixture.row, form));

    chooseMode(inherited, "inherit");
    assert.equal(inherited.row.dataset.dirty, "false");
    chooseMode(inherited, "customize");
    assert.equal(inherited.row.dataset.dirty, "true", "customizing an inherited value is a change even with the same text");
    assert.equal(inherited.operation.value, "Upsert");
    chooseMode(inherited, "inherit");
    assert.equal(inherited.row.dataset.dirty, "false");

    chooseMode(customized, "inherit");
    assert.equal(customized.row.dataset.dirty, "true");
    assert.equal(customized.operation.value, "RemoveOverride");
    assert.equal(customized.binding.value, "");
    chooseMode(customized, "customize");
    assert.equal(customized.row.dataset.dirty, "false");

    chooseMode(blank, "inherit");
    assert.equal(blank.row.dataset.dirty, "true", "inheritance is distinct from an explicit blank");
    chooseMode(blank, "customize");
    assert.equal(blank.row.dataset.dirty, "false");
});

test("boolean rows provide inherit, yes, and no choices", () => {
    const api = loadSettings();
    const inherited = makeRow({ key: "enabled", valueType: "boolean", baselineMode: "inherit", baselineValue: "", value: "", inherited: true });
    const customized = makeRow({ key: "disabled", valueType: "boolean", baselineMode: "customize", baselineValue: "false", value: "false" });
    const form = makeForm([inherited.row, customized.row]);
    [inherited, customized].forEach((fixture) => api.SettingsEditor.initializeStandardRow(fixture.row, form));

    assert.equal(inherited.inherit.checked, true);
    chooseMode(inherited, "customize", "true");
    assert.equal(inherited.row.dataset.dirty, "true");
    assert.equal(inherited.binding.value, "true");
    chooseMode(inherited, "inherit");
    assert.equal(inherited.row.dataset.dirty, "false");

    assert.equal(customized.booleanNo.checked, true);
    chooseMode(customized, "customize", "true");
    assert.equal(customized.row.dataset.dirty, "true");
    assert.equal(customized.binding.value, "true");
    chooseMode(customized, "customize", "false");
    assert.equal(customized.row.dataset.dirty, "false");
});

test("batch rows retain the Changes binding while showing browser-pending state", () => {
    const api = loadSettings();
    const required = makeRow({ key: "require.EmailAddress", valueType: "boolean", baselineMode: "inherit", baselineValue: "", value: "", inherited: true, batch: true });
    const label = makeRow({ key: "label.EmailAddress", baselineMode: "inherit", baselineValue: "", value: "Email address", inherited: true, batch: true });
    const form = makeForm([required.row, label.row]);
    [required, label].forEach((fixture) => api.SettingsEditor.initializeStandardRow(fixture.row, form));

    chooseMode(required, "customize", "true");
    assert.equal(required.row.dataset.dirty, "true");
    assert.equal(required.operation.value, "Upsert");
    assert.equal(required.binding.value, "true");
    assert.equal(required.batchStatus.textContent, "Unsaved: Yes");

    chooseMode(label, "customize");
    label.visible.value = "Contact email";
    label.visible.emit("input");
    assert.equal(label.row.dataset.dirty, "true");
    assert.equal(label.operation.value, "Upsert");
    assert.equal(label.binding.value, "Contact email");
    chooseMode(label, "inherit");
    assert.equal(label.row.dataset.dirty, "false");
    assert.equal(label.batchStatus.textContent, "");
});

test("IP-prefix rows hydrate, edit, add, remove, and serialize without empty segments", () => {
    const api = loadSettings();
    const fixture = makeRow({ key: "show_dl_ips", valueType: "ip-prefixes", baselineValue: "10.;192.168.", value: "10.;192.168." });
    const form = makeForm([fixture.row]);
    api.SettingsEditor.initializeStandardRow(fixture.row, form);
    assert.deepEqual(fixture.prefixes.map((input) => input.value), ["10.", "192.168."]);

    fixture.prefixes[0].value = "10.0.";
    fixture.prefixes[0].emit("input");
    assert.equal(fixture.binding.value, "10.0.;192.168.");
    assert.equal(fixture.binding.value.includes(";;"), false);
    fixture.prefixes[0].value = "10.";
    fixture.prefixes[0].emit("input");
    assert.equal(fixture.row.dataset.dirty, "false");

    fixture.addPrefix.click();
    const added = fixture.prefixes.at(-1);
    added.value = "   ";
    added.emit("input");
    assert.equal(fixture.row.dataset.dirty, "false", "empty list entries have no semantic value");
    added.value = "172.16.";
    added.emit("input");
    assert.equal(fixture.binding.value, "10.;192.168.;172.16.");
    assert.equal(fixture.binding.value.includes(";;"), false);
    added.parentElement?.children?.[1]?.click();
    assert.equal(fixture.row.dataset.dirty, "false");

    chooseMode(fixture, "inherit");
    const countBeforeBlockedActions = fixture.prefixes.length;
    assert.equal(fixture.addPrefix.disabled, true);
    assert.equal(fixture.prefixes.every((input) => input.disabled), true);
    fixture.addPrefix.click();
    fixture.removeButtons[0].click();
    assert.equal(fixture.prefixes.length, countBeforeBlockedActions, "inherit mode prevents hidden list mutations");
    chooseMode(fixture, "customize");
    fixture.prefixes[0].value = "changed";
    fixture.prefixes[0].emit("input");
    fixture.addPrefix.click();
    assert.equal(fixture.row.dataset.dirty, "true");
    api.SettingsWorkflow.discardPendingChanges(form);
    assert.deepEqual(fixture.prefixes.map((input) => input.value), ["10.", "192.168."]);
    assert.equal(fixture.row.dataset.dirty, "false", "discard restores the complete baseline list");
});

test("status filter options compose with search and restore category disclosure state", () => {
    const search = new NodeStub();
    search.value = "";
    const statusFilter = new NodeStub();
    statusFilter.value = "all";
    statusFilter.options = ["all", "customized", "inherited", "notset", "draft", "unsaved"].map((value) => ({ value }));
    const searchStatus = new NodeStub();
    const rows = [
        makeRow({ key: "custom", baselineValue: "one" }).row,
        makeRow({ key: "inherited", baselineMode: "inherit", inherited: true, value: "one" }).row,
        makeRow({ key: "notset", baselineMode: "inherit", inherited: false, value: "" }).row,
        makeRow({ key: "draft", baselineValue: "two" }).row,
        makeRow({ key: "unsaved", baselineValue: "three" }).row
    ];
    rows[3].dataset.draftChange = "true";
    rows[4].dataset.dirty = "true";
    rows[0].dataset.search = "custom phone";
    rows[1].dataset.search = "inherited phone";
    rows[2].dataset.search = "not configured";
    rows[3].dataset.search = "draft email";
    rows[4].dataset.search = "unsaved address";
    const firstCategory = new NodeStub();
    firstCategory.open = true;
    const firstCount = new NodeStub();
    firstCategory.querySelector = () => firstCount;
    firstCategory.querySelectorAll = () => rows.slice(0, 3);
    const secondCategory = new NodeStub();
    secondCategory.open = false;
    const secondCount = new NodeStub();
    secondCategory.querySelector = () => secondCount;
    secondCategory.querySelectorAll = () => rows.slice(3);
    const categories = [firstCategory, secondCategory];
    const form = makeForm(rows);
    const document = createDocument({
        querySelector(selector) {
            return { "#setting-search": search, "#search-status": searchStatus, "#setting-status-filter": statusFilter, "#settings-form": form, ".settings-search": new NodeStub() }[selector] || null;
        },
        querySelectorAll(selector) {
            if (selector === ".setting-row") return rows;
            if (selector === ".setting-category, .dynamic-settings") return categories;
            return [];
        }
    });
    const api = loadSettings(document);
    rows[4].dataset.dirty = "true";
    const apply = api.SettingsWorkflow.applyFilters;

    for (const option of ["all", "customized", "inherited", "notset", "draft", "unsaved"]) {
        statusFilter.value = option;
        apply();
        const expected = option === "all" ? 5 : option === "customized" ? 3 : option === "inherited" ? 1 : option === "notset" ? 1 : 1;
        assert.equal(rows.filter((row) => !row.hidden).length, expected, option);
    }
    statusFilter.value = "inherited";
    search.value = "phone";
    apply();
    assert.equal(rows.filter((row) => !row.hidden).length, 1);
    assert.match(searchStatus.textContent, /search and status filter/);
    assert.equal(firstCount.textContent, "(1)");
    assert.equal(secondCategory.hidden, true);
    statusFilter.value = "all";
    search.value = "";
    apply();
    assert.equal(firstCategory.open, true);
    assert.equal(secondCategory.open, false);
});

test("review tables use safe comparison summaries and image filenames", () => {
    const api = loadSettings();
    const ordinary = makeRow({ key: "registration_text", baselineValue: "old", value: "new" });
    ordinary.row.dataset.dirty = "true";
    ordinary.row.dataset.liveSummary = "old";
    const sensitive = makeRow({ key: "postmark_api_key", baselineValue: "", value: "replacement", sensitive: true });
    sensitive.row.dataset.dirty = "true";
    sensitive.row.dataset.liveSummary = "configured";
    const image = makeRow({ key: "header_image_asset_id", valueType: "image", baselineValue: "41", value: "42" });
    image.row.dataset.dirty = "true";
    image.row.dataset.liveSummary = "old-header.webp";
    const filename = new NodeStub();
    filename.textContent = "new-header.webp";
    const originalQuery = image.row.querySelector;
    image.row.querySelector = (selector) => selector === ".image-pending-file-name" ? filename : originalQuery(selector);
    const rows = [ordinary.row, sensitive.row, image.row];
    const form = { querySelectorAll: (selector) => selector === '.setting-row[data-dirty="true"]' ? rows : [] };
    const body = new NodeStub();
    body.tagName = "TBODY";
    assert.equal(api.SettingsEditor.populateReviewTable(form, body), true);
    assert.equal(body.children.length, 3);
    assert.deepEqual(body.children.map((row) => row.children.map((cell) => cell.textContent)), [
        ["registration_text", "old", "new"],
        ["postmark_api_key", "configured", "Replacement entered"],
        ["header_image_asset_id", "old-header.webp", "new-header.webp"]
    ]);
});

test("image upload blocking remains separate from ordinary browser dirty state", () => {
    const api = loadSettings();
    const image = makeRow({ key: "header_image_asset_id", valueType: "image", baselineValue: "41", value: "41" });
    image.row.dataset.imageUploading = "true";
    const form = makeForm([image.row]);
    const status = new NodeStub();
    focused = null;
    assert.equal(api.SettingsWorkflow.hasImageUpload(form), true);
    assert.equal(api.SettingsEditor.blockActiveEdit(form, status), true);
    assert.match(status.textContent, /image upload/);
    assert.equal(image.row.open, true);
    assert.equal(focused, null, "a row without image controls does not invent a focus target");

    delete image.row.dataset.imageUploading;
    image.row.dataset.imageNeedsUpload = "true";
    const requiredStatus = new NodeStub();
    assert.equal(api.SettingsWorkflow.hasImageUpload(form), true);
    api.SettingsWorkflow.syncBlockingStatus(form, requiredStatus);
    assert.match(requiredStatus.textContent, /Upload an image/);
});

test("settings markup removes ordinary Change/Keep/Cancel sessions and keeps explicit accessible controls", () => {
    assert.doesNotMatch(settingRowMarkup, /data-candidate-operation|class="edit-setting"|class="apply-setting"|class="cancel-setting"|Keep change/);
    assert.doesNotMatch(settingsScript, /data-candidate-operation|function beginEdit|function applyEdit|function cancelEdit/);
    assert.match(settingRowMarkup, /<fieldset class="setting-mode-group/);
    assert.match(settingRowMarkup, /<legend>At this scope<\/legend>/);
    assert.match(settingRowMarkup, /boolean-mode-group/);
    assert.match(batchRowMarkup, /class="batch-setting-name"/);
    assert.match(batchRowMarkup, /name="Changes\[@token\]\.Key"/);
    assert.match(settingsCss, /\.setting-mode-group/);
    assert.doesNotMatch(settingsCss, /\.edit-actions|\.inheritance-message/);
});

test("type-specific editors and safe plain-text/HTML previews are present", () => {
    assert.match(settingRowMarkup, /type="number"/);
    assert.match(settingRowMarkup, /class="setting-unit"/);
    assert.match(settingRowMarkup, /FriendlyValue\(definition, allowedValue\)/);
    assert.match(settingRowMarkup, /ValueType\.Enumeration/);
    assert.match(settingRowMarkup, /data-ip-prefix-editor/);
    assert.match(settingRowMarkup, /class="plain-text-preview"/);
    assert.match(settingRowMarkup, /<iframe sandbox=""[^>]+class="setting-html-value-preview html-preview"/);
    assert.match(settingsScript, /preview\.textContent = source\.value/);
    assert.match(settingsScript, /frame\.srcdoc = source\.value/);
});

test("plain-text previews preserve whitespace while HTML previews use the sandbox source path", () => {
    const plainSource = new NodeStub("First line\n  Second line");
    const plainPreview = new NodeStub();
    plainPreview.previousElementSibling = plainSource;
    const htmlSource = new NodeStub("<strong>Formatted</strong>");
    const htmlPreview = new NodeStub();
    htmlPreview.previousElementSibling = htmlSource;
    const document = createDocument({
        querySelectorAll(selector) {
            if (selector === ".plain-text-preview") return [plainPreview];
            if (selector === ".html-preview") return [htmlPreview];
            return [];
        }
    });
    loadSettings(document);

    assert.equal(plainPreview.textContent, "First line\n  Second line");
    assert.equal(htmlPreview.srcdoc, "<strong>Formatted</strong>");
    plainSource.value = "Line one\n\nLine three";
    plainSource.emit("input");
    assert.equal(plainPreview.textContent, "Line one\n\nLine three");
});

test("draft saves bypass the generic review while live, publish, discard, and live preview remain guarded", () => {
    assert.match(settingsIndex, /data-submit-kind="draft"/);
    const draftButton = settingsIndex.match(/<button[^>]*data-submit-kind="draft"[^>]*>/g) || [];
    assert.ok(draftButton.length >= 1);
    assert.doesNotMatch(draftButton.join("\n"), /data-review-title|data-confirm-label/);
    assert.match(settingsIndex, /<table class="review-table">/);
    assert.match(settingsIndex, /<th scope="col">Live now<\/th>/);
    assert.match(settingsIndex, /<th scope="col">Proposed<\/th>/);
    assert.match(settingsIndex, /<details class="preview-tools"/);
    assert.match(settingsScript, /submitter\?\.dataset\?\.submitKind === "draft"/);
    assert.match(settingsScript, /finalSubmit\(\{ form, submitter, trigger: submitter, prepare: disableDirtyMutations \}\)/);
    assert.match(settingsScript, /needsLiveConfirmation/);
});

test("settings context submits only after a selector change", () => {
    const api = loadSettings();
    const organization = new NodeStub("branch");
    const formCode = new NodeStub("youth");
    const submissions = [];
    const contextForm = {
        querySelector(selector) { return selector === "#organization-scope" ? organization : formCode; },
        requestSubmit() { submissions.push({ organization: organization.value, formCode: formCode.value, formCodeDisabled: formCode.disabled }); }
    };
    api.SettingsEditor.initializeSettingsContext(contextForm);
    assert.deepEqual(submissions, []);
    organization.emit("change");
    assert.deepEqual(submissions, [{ organization: "branch", formCode: "youth", formCodeDisabled: true }]);
});
