import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";

const settingsScript = readFileSync(new URL("../../Clc.PatronRegistration.Web/wwwroot/js/settings.js", import.meta.url), "utf8");
const settingRowMarkup = readFileSync(new URL("../../Clc.PatronRegistration.Web/Views/Settings/_SettingRow.cshtml", import.meta.url), "utf8");
const batchRowMarkup = readFileSync(new URL("../../Clc.PatronRegistration.Web/Views/Settings/_BatchSettingRow.cshtml", import.meta.url), "utf8");
const settingsCss = readFileSync(new URL("../../Clc.PatronRegistration.Web/wwwroot/css/settings.css", import.meta.url), "utf8");

let focused;

class NodeStub {
    constructor(value = "") {
        this.value = value;
        this.textContent = "";
        this.disabled = true;
        this.readOnly = false;
        this.hidden = false;
        this.open = false;
        this.checked = false;
        this.dataset = {};
        this.attributes = {};
        this.listeners = {};
        this.children = [];
        this.parentElement = null;
        this.tagName = "DIV";
        this.type = "text";
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

    addEventListener(name, callback) { (this.listeners[name] ||= []).push(callback); }
    emit(name, event = {}) {
        const current = { type: name, target: this, currentTarget: this, ...event };
        if (!current.preventDefault) current.preventDefault = () => { current.defaultPrevented = true; };
        for (const callback of this.listeners[name] || []) callback(current);
        return !current.defaultPrevented;
    }
    click() { return this.emit("click"); }
    focus() { focused = this; this.focused = true; }
    setAttribute(name, value) { this.attributes[name] = String(value); if (name === "open") this.open = true; }
    getAttribute(name) { return this.attributes[name] ?? null; }
    removeAttribute(name) { delete this.attributes[name]; }
    append(...nodes) { nodes.forEach((node) => { if (!node) return; this.children.push(node); node.parentElement = this; }); }
    appendChild(node) { this.append(node); return node; }
    insertBefore(node, before) {
        const index = before ? this.children.indexOf(before) : -1;
        if (index < 0) this.append(node); else { this.children.splice(index, 0, node); node.parentElement = this; }
    }
    removeChild(node) { const index = this.children.indexOf(node); if (index >= 0) this.children.splice(index, 1); node.parentElement = null; }
    remove() { this.parentElement?.removeChild(this); }
    replaceChildren(...nodes) { this.children = []; this.append(...nodes); }
    close() { this.open = false; this.closeCount = (this.closeCount || 0) + 1; }
    showModal() { this.open = true; }
    querySelector() { return null; }
    querySelectorAll() { return []; }
    closest() { return null; }
    getBoundingClientRect() { return { top: 0, bottom: 0 }; }
}

function createDocument(overrides = {}) {
    return {
        documentElement: { clientHeight: 0 },
        querySelector(selector) { return overrides.querySelector?.(selector) ?? null; },
        querySelectorAll(selector) { return overrides.querySelectorAll?.(selector) ?? []; },
        createElement(tagName) { const element = new NodeStub(); element.tagName = tagName.toUpperCase(); return element; }
    };
}

function loadSettings(document = createDocument(), overrides = {}) {
    const context = {
        document,
        documentElement: document.documentElement,
        window: { addEventListener() {} },
        navigator: {},
        console,
        setTimeout,
        clearTimeout,
        FormData: class { append() {} },
        fetch: async () => ({ ok: false, json: async () => ({}) }),
        ...overrides,
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
        querySelector(selector) {
            if (selector === ".settings-actions") return actions;
            if (selector.includes('data-image-needs-upload="true"')) return rows.find((row) => row.dataset.imageNeedsUpload === "true") || null;
            return null;
        },
        querySelectorAll(selector) {
            if (selector === '.setting-row[data-dirty="true"]') return rows.filter((row) => row.dataset.dirty === "true");
            if (selector === '.setting-row[data-editing="true"]') return rows.filter((row) => row.dataset.editing === "true");
            if (selector.includes('data-image-uploading="true"')) return rows.filter((row) => row.dataset.imageUploading === "true");
            return [];
        }
    };
}

function makeRow({ key = "registration_text", valueType = "shortstring", baselineMode = "customize", baselineValue = "original", value = baselineValue, sensitive = false, inherited = false, inheritedValue = inherited ? (valueType === "boolean" ? "false" : "Inherited value") : "", batch = false } = {}) {
    const category = new NodeStub();
    category.tagName = "DETAILS";
    const row = new NodeStub();
    row.tagName = batch ? "TR" : "DETAILS";
    row.dataset = {
        settingKey: key,
        displayName: key,
        valueType,
        sensitive: String(sensitive),
        baselineMode,
        baselineValue,
        customizedHere: baselineMode === "customize" ? "true" : "false",
        presentationState: baselineMode === "inherit" ? (inherited ? "inherited" : "notset") : "customized",
        liveState: baselineMode === "inherit" ? (inherited ? "inherited" : "notset") : "customized",
        draftChange: "false",
        hasInherited: String(inherited),
        inheritedSummary: inherited ? "Inherited value" : "",
        inheritedValue,
        inheritedSource: inherited ? "Main Library" : "",
        liveSummary: baselineMode === "inherit" ? "Inherited value" : baselineValue
    };

    const change = new NodeStub(); change.disabled = false; change.type = "button";
    const revert = new NodeStub(); revert.disabled = false; revert.type = "button";
    const scopeStatus = new NodeStub(); scopeStatus.textContent = baselineMode === "inherit" ? "Inherited" : "Customized here";
    const visible = new NodeStub(valueType === "boolean" ? (baselineMode === "inherit" ? (inherited ? "false" : "false") : baselineValue) : value);
    visible.tagName = "INPUT";
    visible.disabled = false; visible.type = sensitive ? "password" : valueType === "date" ? "date" : valueType === "emailaddress" ? "email" : valueType === "uri" ? "url" : valueType === "integer" || valueType === "decimal" ? "number" : "text";
    const boolYes = new NodeStub(); boolYes.type = "radio"; boolYes.value = "true"; boolYes.dataset = { value: "true" }; boolYes.disabled = true;
    const boolNo = new NodeStub(); boolNo.type = "radio"; boolNo.value = "false"; boolNo.dataset = { value: "false" }; boolNo.disabled = true;
    boolYes.checked = baselineMode === "customize" && String(baselineValue).toLowerCase() === "true";
    boolNo.checked = baselineMode === "customize" && String(baselineValue).toLowerCase() !== "true";
    const booleanControls = valueType === "boolean" ? [boolYes, boolNo] : [];
    const binding = new NodeStub(baselineMode === "customize" ? baselineValue : ""); binding.disabled = true;
    const operation = new NodeStub(baselineMode === "inherit" ? "RemoveOverride" : "Upsert");
    const index = new NodeStub(); const keyControl = new NodeStub();
    const summary = batch ? null : new NodeStub();
    if (summary) { summary.textContent = baselineMode === "inherit" ? "Inherited value" : String(baselineValue || "Blank"); summary.setAttribute("title", summary.textContent); }
    const status = batch ? null : new NodeStub(); if (status) status.textContent = baselineMode === "inherit" ? "Inherited" : "Customized here";
    const batchStatus = batch ? new NodeStub() : null;
    const prefixEditor = valueType === "ip-prefixes" ? new NodeStub() : null;
    const prefixes = valueType === "ip-prefixes" ? String(value || "").split(";").filter(Boolean).map((entry) => { const input = new NodeStub(entry); input.type = "text"; return input; }) : [];
    const addPrefix = valueType === "ip-prefixes" ? new NodeStub() : null;
    const prefixRows = []; const removeButtons = [];
    if (prefixEditor) {
        prefixEditor.insertBefore = (wrapper) => { const input = wrapper.children[0]; prefixes.push(input); prefixRows.push(wrapper); removeButtons.push(wrapper.children[1]); prefixEditor.children.push(wrapper); wrapper.parentElement = prefixEditor; };
        prefixEditor.removeChild = (wrapper) => { const input = wrapper.children[0]; const indexToRemove = prefixes.indexOf(input); if (indexToRemove >= 0) prefixes.splice(indexToRemove, 1); const rowIndex = prefixRows.indexOf(wrapper); if (rowIndex >= 0) prefixRows.splice(rowIndex, 1); const removeIndex = removeButtons.indexOf(wrapper.children[1]); if (removeIndex >= 0) removeButtons.splice(removeIndex, 1); const childIndex = prefixEditor.children.indexOf(wrapper); if (childIndex >= 0) prefixEditor.children.splice(childIndex, 1); wrapper.parentElement = null; };
        prefixes.slice().forEach((input) => { const wrapper = new NodeStub(); const remove = new NodeStub(); wrapper.append(input, remove); wrapper.parentElement = prefixEditor; removeButtons.push(remove); prefixRows.push(wrapper); prefixEditor.children.push(wrapper); remove.closest = () => wrapper; });
        prefixEditor.querySelectorAll = (query) => query === ".ip-prefix-row" ? prefixRows : [];
    }
    const selector = {
        ".setting-change": change,
        ".setting-revert": revert,
        ".setting-scope-status": scopeStatus,
        ".setting-value:not(.setting-value-binding)": valueType === "boolean" ? boolYes : visible,
        ".setting-value": valueType === "boolean" ? boolYes : visible,
        ".setting-value-binding": binding,
        ".operation": operation,
        ".change-index": index,
        ".change-key": keyControl,
        ".summary-value": summary,
        ".setting-status > span": null,
        ".setting-status": status,
        ".batch-browser-status": batchStatus,
        ".reveal-secret": sensitive ? new NodeStub() : null,
        "[data-ip-prefix-editor]": prefixEditor,
        ".ip-prefix-add": addPrefix
    };
    const reveal = selector[".reveal-secret"];
    if (reveal) { reveal.textContent = "Reveal secret"; reveal.setAttribute("aria-expanded", "false"); reveal.setAttribute("aria-label", `Reveal ${key}`); }
    row.querySelector = (query) => {
        if (query === ".value-editor .setting-value:not(.setting-value-binding)") return valueType === "boolean" ? boolYes : visible;
        if (query.includes(".setting-value:not")) return valueType === "boolean" ? boolYes : visible;
        return selector[query] ?? null;
    };
    row.querySelectorAll = (query) => {
        if (query === ".boolean-value, .batch-value-choice") return booleanControls;
        if (query.includes(".setting-value:not") || query.includes(".batch-label-input")) return valueType === "boolean" ? booleanControls : valueType === "ip-prefixes" ? [] : [visible];
        if (query === ".ip-prefix-input") return prefixes;
        if (query === ".ip-prefix-remove") return removeButtons;
        if (query === ".ip-prefix-add, .ip-prefix-remove") return [...removeButtons, addPrefix].filter(Boolean);
        if (query === ".ip-prefix-row") return prefixRows;
        if (query === ".change-index") return [index];
        if (query === ".change-key") return [keyControl];
        if (query === ".operation") return [operation];
        if (query === ".setting-value-binding") return [binding];
        return [];
    };
    row.closest = () => category; category.querySelectorAll = () => [row]; category.querySelector = () => null;
    if (addPrefix) addPrefix.parentElement = prefixEditor;
    return { row, category, change, revert, scopeStatus, visible, binding, operation, summary, status, batchStatus, reveal, booleanYes: boolYes, booleanNo: boolNo, prefixes, addPrefix, removeButtons, prefixEditor };
}

function makeImageRow({ baselineMode = "inherit", baselineValue = "", inherited = true, localAssetValue = "", localAssetMissing = false } = {}) {
    const fixture = makeRow({ key: "header_image_asset_id", valueType: "image", baselineMode, baselineValue, value: baselineValue || "", inherited });
    const change = fixture.change;
    const chooseAnother = new NodeStub();
    const imageFile = new NodeStub(); imageFile.files = []; imageFile.dataset.uploadUrl = "/settings/assets/upload";
    const pending = new NodeStub(); const pendingPreview = new NodeStub(); const pendingFileName = new NodeStub(); const uploadStatus = new NodeStub();
    pending.querySelector = (selector) => ({ ".image-pending-preview": pendingPreview, ".image-pending-file-name": pendingFileName, ".image-upload-status": uploadStatus }[selector] || null);
    fixture.row.dataset.imageInheritedPreviewUrl = inherited ? "/settings/assets/10" : "";
    fixture.row.dataset.imageInheritedFileName = inherited ? "library-header.png" : "";
    const localValue = localAssetValue || (baselineMode === "customize" ? baselineValue : "");
    fixture.row.dataset.imageLocalValue = localValue;
    fixture.row.dataset.imageLocalMissing = String(localAssetMissing);
    fixture.row.dataset.imageLocalFileName = localValue ? "local-header.png" : "";
    fixture.row.dataset.imageLocalPreviewUrl = localValue ? `/settings/assets/${localValue}` : "";
    const originalQuery = fixture.row.querySelector;
    fixture.row.querySelector = (selector) => ({ ".setting-change": change, ".image-upload-trigger": change, ".image-choose-another": chooseAnother, ".image-file": imageFile, ".image-pending": pending, ".image-pending-file-name": pendingFileName, ".image-upload-status": uploadStatus, ".setting-value-binding": fixture.binding }[selector] || originalQuery(selector));
    return { ...fixture, change, chooseAnother, imageFile, pending, pendingPreview, pendingFileName, uploadStatus };
}

const flush = () => new Promise((resolve) => setImmediate(resolve));

test("customized Change activates a locked editor without dirtying it", () => {
    const api = loadSettings();
    const setting = makeRow({ baselineValue: "original", value: "original", inherited: true });
    const form = makeForm([setting.row]);
    api.SettingsEditor.initializeStandardRow(setting.row, form);
    assert.equal(setting.row.dataset.dirty, "false");
    assert.equal(setting.visible.readOnly, true);
    setting.change.click();
    assert.equal(setting.row.dataset.editing, "true");
    assert.equal(setting.row.dataset.dirty, "false");
    assert.equal(setting.visible.readOnly, false);
    assert.equal(focused, setting.visible);
    setting.visible.value = "changed"; setting.visible.emit("input");
    assert.equal(setting.row.dataset.dirty, "true");
    assert.equal(setting.binding.value, "changed");
    assert.equal(setting.operation.value, "Upsert");
    setting.visible.value = "original"; setting.visible.emit("input");
    assert.equal(setting.row.dataset.dirty, "false");
});

test("discard also closes a clean active editor when another row is dirty", () => {
    const api = loadSettings();
    const active = makeRow({ key: "active", baselineValue: "original", value: "original" });
    const changed = makeRow({ key: "changed", baselineValue: "saved", value: "saved" });
    const form = makeForm([active.row, changed.row]);
    api.SettingsEditor.initializeStandardRow(active.row, form);
    api.SettingsEditor.initializeStandardRow(changed.row, form);

    active.change.click();
    changed.change.click();
    changed.visible.value = "browser value";
    changed.visible.emit("input");
    assert.equal(active.row.dataset.dirty, "false");
    assert.equal(active.row.dataset.editing, "true");

    api.SettingsWorkflow.discardPendingChanges(form);

    assert.equal(active.row.dataset.editing, "false");
    assert.equal(active.visible.readOnly, true);
    assert.equal(changed.row.dataset.dirty, "false");
});

test("inherited Change establishes customization intent and Revert returns to baseline", () => {
    const api = loadSettings();
    const setting = makeRow({ key: "inherited", baselineMode: "inherit", baselineValue: "", value: "library value", inherited: true });
    const form = makeForm([setting.row]);
    api.SettingsEditor.initializeStandardRow(setting.row, form);
    setting.change.click();
    assert.equal(setting.row.dataset.dirty, "true");
    assert.equal(setting.operation.value, "Upsert");
    assert.equal(setting.binding.value, "library value");
    assert.equal(setting.revert.hidden, false);
    setting.revert.click();
    assert.equal(setting.row.dataset.dirty, "false");
    assert.equal(setting.row.dataset.editing, "false");
    assert.equal(setting.visible.readOnly, true);
    assert.equal(form.actions.hidden, true);
});

test("customized Revert creates RemoveOverride without requiring Change", () => {
    const api = loadSettings();
    const setting = makeRow({ key: "customized", baselineMode: "customize", baselineValue: "local value", value: "local value", inherited: true });
    const form = makeForm([setting.row]);
    api.SettingsEditor.initializeStandardRow(setting.row, form);
    setting.revert.click();
    assert.equal(focused, setting.change);
    assert.equal(setting.row.dataset.dirty, "true");
    assert.equal(setting.operation.value, "RemoveOverride");
    assert.equal(setting.binding.value, "");
    assert.equal(setting.visible.value, "Inherited value");
    const body = new NodeStub(); api.SettingsEditor.populateReviewTable(form, body);
    assert.equal(body.children[0].children[2].textContent, "Use Inherited value from Main Library");
});

test("explicit blank remains a custom Upsert and is distinct from Revert", () => {
    const api = loadSettings();
    const setting = makeRow({ key: "blank", baselineMode: "inherit", baselineValue: "", value: "Inherited text", inherited: true });
    const form = makeForm([setting.row]);
    api.SettingsEditor.initializeStandardRow(setting.row, form);
    setting.change.click(); setting.visible.value = ""; setting.visible.emit("input");
    assert.equal(setting.row.dataset.dirty, "true");
    assert.equal(setting.operation.value, "Upsert");
    assert.equal(setting.binding.value, "");
    setting.revert.click();
    assert.equal(setting.row.dataset.dirty, "false");
    assert.equal(setting.operation.value, "RemoveOverride");
});

test("shared-draft Upsert is the clean browser baseline while RemoveOverride Change is an Upsert", () => {
    const api = loadSettings();
    const upsert = makeRow({ key: "draft-upsert", baselineMode: "customize", baselineValue: "draft value", value: "draft value", inherited: true });
    upsert.row.dataset.draftChange = "true";
    const remove = makeRow({ key: "draft-remove", baselineMode: "inherit", baselineValue: "", value: "Draft inherited value", inherited: true });
    remove.row.dataset.draftChange = "true";
    const form = makeForm([upsert.row, remove.row]);
    api.SettingsEditor.initializeStandardRow(upsert.row, form); api.SettingsEditor.initializeStandardRow(remove.row, form);
    upsert.change.click(); assert.equal(upsert.row.dataset.dirty, "false");
    remove.change.click(); assert.equal(remove.row.dataset.dirty, "true"); assert.equal(remove.operation.value, "Upsert");
    api.SettingsWorkflow.discardPendingChanges(form);
    assert.equal(remove.row.dataset.dirty, "false"); assert.equal(remove.row.dataset.editing, "false"); assert.equal(remove.visible.value, "Draft inherited value");
    assert.equal(upsert.row.dataset.dirty, "false"); assert.equal(upsert.visible.value, "draft value");
});

test("shared-draft baselines preserve Revert and Change transitions", () => {
    const api = loadSettings();
    const upsert = makeRow({ key: "draft-upsert-revert", baselineMode: "customize", baselineValue: "draft value", value: "draft value", inherited: true });
    upsert.row.dataset.draftChange = "true";
    const remove = makeRow({ key: "draft-remove-revert", baselineMode: "inherit", baselineValue: "", value: "Draft inherited value", inherited: true });
    remove.row.dataset.draftChange = "true";
    const form = makeForm([upsert.row, remove.row]);
    api.SettingsEditor.initializeStandardRow(upsert.row, form); api.SettingsEditor.initializeStandardRow(remove.row, form);

    upsert.revert.click();
    assert.equal(upsert.row.dataset.dirty, "true");
    assert.equal(upsert.operation.value, "RemoveOverride");
    assert.equal(upsert.binding.disabled, false);
    upsert.row._discardPendingChange();
    assert.equal(upsert.row.dataset.dirty, "false");

    remove.change.click();
    assert.equal(remove.row.dataset.dirty, "true");
    assert.equal(remove.operation.value, "Upsert");
    remove.revert.click();
    assert.equal(remove.row.dataset.dirty, "false");
    assert.equal(remove.operation.value, "RemoveOverride");
    assert.equal(remove.binding.disabled, true);
    assert.equal(remove.row.dataset.editing, "false");
});

test("sensitive Change reveals only an empty replacement and discard clears it", () => {
    const api = loadSettings();
    const secret = makeRow({ key: "postmark_api_key", baselineMode: "customize", baselineValue: "", value: "", sensitive: true, inherited: true });
    const form = makeForm([secret.row]); api.SettingsEditor.initializeStandardRow(secret.row, form);
    secret.change.click();
    assert.equal(secret.visible.value, ""); assert.equal(secret.visible.type, "password"); assert.equal(secret.row.dataset.dirty, "false");
    secret.visible.value = "replacement"; secret.visible.emit("input");
    assert.equal(secret.row.dataset.dirty, "true"); assert.equal(secret.binding.value, "replacement");
    secret.reveal.click(); assert.equal(secret.visible.type, "text");
    api.SettingsWorkflow.discardPendingChanges(form);
    assert.equal(secret.visible.value, ""); assert.equal(secret.visible.type, "password"); assert.equal(secret.row.dataset.dirty, "false"); assert.equal(secret.row.dataset.editing, "false");
});

test("an inherited sensitive Change stays clean until a replacement is entered", () => {
    const api = loadSettings();
    const secret = makeRow({ key: "inherited_secret", baselineMode: "inherit", baselineValue: "", value: "", sensitive: true, inherited: true });
    const form = makeForm([secret.row]);
    api.SettingsEditor.initializeStandardRow(secret.row, form);

    secret.change.click();
    assert.equal(secret.visible.value, "");
    assert.equal(secret.row.dataset.dirty, "false");
    assert.equal(secret.binding.disabled, true);
    secret.visible.value = "replacement";
    secret.visible.emit("input");
    assert.equal(secret.row.dataset.dirty, "true");
    assert.equal(secret.operation.value, "Upsert");
    secret.revert.click();
    assert.equal(secret.visible.value, "");
    assert.equal(secret.visible.type, "password");
    assert.equal(secret.row.dataset.dirty, "false");
});

test("Boolean and batch required controls expose value radios only while editing", () => {
    const api = loadSettings();
    const boolean = makeRow({ key: "enabled", valueType: "boolean", baselineMode: "customize", baselineValue: "false", value: "false", inherited: true });
    const required = makeRow({ key: "require.EmailAddress", valueType: "boolean", baselineMode: "inherit", baselineValue: "", value: "false", inherited: true, batch: true });
    const form = makeForm([boolean.row, required.row]); api.SettingsEditor.initializeStandardRow(boolean.row, form); api.SettingsEditor.initializeStandardRow(required.row, form);
    assert.equal(boolean.booleanNo.checked, true); assert.equal(boolean.booleanNo.disabled, true);
    boolean.change.click(); assert.equal(boolean.booleanYes.disabled, false); assert.equal(boolean.booleanNo.disabled, false); assert.equal(boolean.row.dataset.dirty, false ? "true" : "false");
    boolean.booleanYes.checked = true; boolean.booleanNo.checked = false; boolean.booleanYes.emit("change"); assert.equal(boolean.row.dataset.dirty, "true"); assert.equal(boolean.binding.value, "true");
    required.change.click(); assert.equal(required.booleanYes.disabled, false); assert.equal(required.row.dataset.dirty, "true"); assert.equal(required.operation.value, "Upsert");
});

test("special editor Revert uses inherited values and relocks controls", () => {
    const api = loadSettings();
    const boolean = makeRow({ key: "enabled-revert", valueType: "boolean", baselineMode: "customize", baselineValue: "false", value: "false", inherited: true, inheritedValue: "true" });
    const label = makeRow({ key: "label.NameFirst-revert", baselineMode: "customize", baselineValue: "First name", value: "First name", inherited: true, inheritedValue: "Given name", batch: true });
    const required = makeRow({ key: "require.EmailAddress-revert", valueType: "boolean", baselineMode: "inherit", baselineValue: "", value: "false", inherited: true, inheritedValue: "false", batch: true });
    const form = makeForm([boolean.row, label.row, required.row]);
    api.SettingsEditor.initializeStandardRow(boolean.row, form); api.SettingsEditor.initializeStandardRow(label.row, form); api.SettingsEditor.initializeStandardRow(required.row, form);

    boolean.revert.click();
    assert.equal(boolean.row.dataset.dirty, "true");
    assert.equal(boolean.operation.value, "RemoveOverride");
    assert.equal(boolean.booleanYes.checked, true);
    assert.equal(boolean.booleanYes.disabled, true); assert.equal(boolean.booleanNo.disabled, true);

    label.revert.click();
    assert.equal(label.row.dataset.dirty, "true");
    assert.equal(label.operation.value, "RemoveOverride");
    assert.equal(label.visible.value, "Given name");
    assert.equal(label.visible.readOnly, true);

    required.change.click();
    assert.equal(required.row.dataset.dirty, "true");
    assert.equal(required.operation.value, "Upsert");
    required.revert.click();
    assert.equal(required.row.dataset.dirty, "false");
    assert.equal(required.operation.value, "RemoveOverride");
    assert.equal(required.booleanYes.disabled, true); assert.equal(required.booleanNo.disabled, true);
    assert.equal(required.booleanNo.checked, true);
});

test("IP prefixes stay locked, then add/remove and discard restore the full list", () => {
    const api = loadSettings();
    const setting = makeRow({ key: "show_dl_ips", valueType: "ip-prefixes", baselineMode: "customize", baselineValue: "10.;192.168.", value: "10.;192.168.", inherited: true });
    const form = makeForm([setting.row]); api.SettingsEditor.initializeStandardRow(setting.row, form);
    assert.equal(setting.prefixes.every((input) => input.readOnly), true); setting.change.click(); assert.equal(setting.prefixes.every((input) => input.readOnly), false); setting.addPrefix.click();
    const added = setting.prefixes.at(-1); added.value = "172.16."; added.emit("input"); assert.equal(setting.binding.value, "10.;192.168.;172.16."); assert.equal(setting.row.dataset.dirty, "true");
    api.SettingsWorkflow.discardPendingChanges(form); assert.deepEqual(setting.prefixes.map((input) => input.value), ["10.", "192.168."]); assert.equal(setting.row.dataset.dirty, "false");
});

test("HTML and plain-text previews remain synchronized through discard", () => {
    const api = loadSettings();
    const html = makeRow({ key: "html-preview", valueType: "html", baselineValue: "<p>Loaded HTML</p>", value: "<p>Loaded HTML</p>", inherited: true });
    const plain = makeRow({ key: "plain-preview", valueType: "emailtemplate", baselineValue: "Loaded plain text", value: "Loaded plain text", inherited: true });
    const htmlPreview = new NodeStub(); htmlPreview.classList.values.add("html-preview"); html.visible.nextElementSibling = htmlPreview;
    const plainPreview = new NodeStub(); plainPreview.classList.values.add("plain-text-preview"); plain.visible.nextElementSibling = plainPreview;
    const form = makeForm([html.row, plain.row]); api.SettingsEditor.initializeStandardRow(html.row, form); api.SettingsEditor.initializeStandardRow(plain.row, form);
    html.change.click(); html.visible.value = "<p>Temporary HTML</p>"; html.visible.emit("input"); plain.change.click(); plain.visible.value = "Temporary plain text"; plain.visible.emit("input");
    assert.equal(htmlPreview.srcdoc, "<p>Temporary HTML</p>"); assert.equal(plainPreview.textContent, "Temporary plain text"); api.SettingsWorkflow.discardPendingChanges(form);
    assert.equal(html.visible.value, "<p>Loaded HTML</p>"); assert.equal(htmlPreview.srcdoc, "<p>Loaded HTML</p>"); assert.equal(plain.visible.value, "Loaded plain text"); assert.equal(plainPreview.textContent, "Loaded plain text");
});

test("image Change reuses a live local asset when reverting a draft removal and requires upload otherwise", () => {
    const api = loadSettings();
    const local = makeImageRow({ localAssetValue: "41" });
    const missing = makeImageRow({ localAssetValue: "42", localAssetMissing: true });
    const noLocal = makeImageRow();
    const form = makeForm([local.row, missing.row, noLocal.row]);
    api.SettingsEditor.initializeImageRow(local.row, form);
    api.SettingsEditor.initializeImageRow(missing.row, form);
    api.SettingsEditor.initializeImageRow(noLocal.row, form);

    local.change.click();
    assert.equal(local.binding.value, "41");
    assert.equal(local.row.dataset.dirty, "true");
    assert.equal(local.row.dataset.imageNeedsUpload, undefined);
    assert.equal(local.pendingFileName.textContent, "local-header.png");
    assert.equal(local.pendingPreview.src, "/settings/assets/41");
    local.revert.click();
    assert.equal(focused, local.change);

    missing.change.click();
    assert.equal(missing.binding.value, "");
    assert.equal(missing.row.dataset.imageNeedsUpload, "true");
    assert.equal(missing.row.dataset.dirty, "true");
    missing.revert.click();

    noLocal.change.click();
    assert.equal(noLocal.binding.value, "");
    assert.equal(noLocal.row.dataset.imageNeedsUpload, "true");
    noLocal.revert.click();
});

test("image Change opens the picker, upload creates Upsert, and late responses cannot resurrect discard", async () => {
    let resolveResponse;
    const api = loadSettings(createDocument(), { fetch: () => new Promise((resolve) => { resolveResponse = resolve; }) });
    const image = makeImageRow(); const form = makeForm([image.row]); api.SettingsEditor.initializeImageRow(image.row, form); let pickerClicks = 0; image.imageFile.click = () => { pickerClicks++; };
    image.change.click(); assert.equal(pickerClicks, 1); assert.equal(image.row.dataset.dirty, "true"); assert.equal(image.row.dataset.imageNeedsUpload, "true"); assert.equal(image.row.dataset.editing, "true");
    image.imageFile.files = [{ name: "replacement.png" }]; image.imageFile.emit("change"); assert.equal(image.row.dataset.imageUploading, "true");
    api.SettingsWorkflow.discardPendingChanges(form); resolveResponse({ ok: true, async json() { return { assetId: 55, fileName: "late.png", previewUrl: "/settings/assets/55" }; } }); await flush(); await flush();
    assert.equal(image.row.dataset.dirty, "false"); assert.equal(image.row.dataset.imageUploading, undefined); assert.equal(image.binding.value, ""); assert.equal(image.pending.hidden, true);
});

test("cancelling a customized image picker leaves a clean Change image retry", () => {
    const api = loadSettings();
    const image = makeImageRow({ baselineMode: "customize", baselineValue: "12", inherited: true });
    const form = makeForm([image.row]);
    api.SettingsEditor.initializeImageRow(image.row, form);
    let pickerClicks = 0;
    image.imageFile.click = () => { pickerClicks++; };

    image.change.click();
    assert.equal(pickerClicks, 1);
    assert.equal(image.row.dataset.dirty, "false");
    assert.equal(image.pending.hidden, true);
    assert.equal(image.change.hidden, false);

    image.change.click();
    assert.equal(pickerClicks, 2);
    assert.equal(image.row.dataset.dirty, "false");
    assert.equal(image.pending.hidden, true);
    assert.equal(image.change.hidden, false);
});

test("review uses the uploaded inherited image as its pending Upsert", async () => {
    const api = loadSettings(createDocument(), { fetch: async () => ({ ok: true, async json() { return { assetId: 77, fileName: "replacement.png", previewUrl: "/settings/assets/77" }; } }) });
    const image = makeImageRow(); const form = makeForm([image.row]); api.SettingsEditor.initializeImageRow(image.row, form);
    image.change.click(); image.imageFile.files = [{ name: "replacement.png" }]; image.imageFile.emit("change"); await flush(); await flush();

    const body = new NodeStub(); api.SettingsEditor.populateReviewTable(form, body);
    assert.equal(body.children[0].children[2].textContent, "replacement.png");
    assert.doesNotMatch(body.children[0].children[2].textContent, /Use inherited image/);
});

test("failed image replacement keeps the previous pending image", async () => {
    let requestCount = 0;
    const api = loadSettings(createDocument(), { fetch: async () => { requestCount++; return requestCount === 1 ? { ok: true, async json() { return { assetId: 91, fileName: "first.png", previewUrl: "/settings/assets/91" }; } } : { ok: false, async json() { return { error: "The image is invalid." }; } }; } });
    const image = makeImageRow({ baselineMode: "customize", baselineValue: "12", inherited: true }); const form = makeForm([image.row]); api.SettingsEditor.initializeImageRow(image.row, form);
    image.change.click(); image.imageFile.files = [{ name: "first.png" }]; image.imageFile.emit("change"); await flush(); await flush();
    image.imageFile.files = [{ name: "bad.png" }]; image.imageFile.emit("change"); await flush(); await flush();
    assert.equal(image.binding.value, "91"); assert.equal(image.pendingFileName.textContent, "first.png"); assert.equal(image.row.dataset.dirty, "true"); assert.match(image.uploadStatus.textContent, /first\.png remains selected/);
});

test("discard attempts every dirty row and leaves unresolved work visible", () => {
    const api = loadSettings(); const first = makeRow({ key: "failed-setting", baselineValue: "first", value: "first", inherited: true }); const second = makeRow({ key: "later-setting", baselineValue: "second", value: "second", inherited: true }); const form = makeForm([first.row, second.row]);
    api.SettingsEditor.initializeStandardRow(first.row, form); api.SettingsEditor.initializeStandardRow(second.row, form); first.change.click(); first.visible.value = "changed"; first.visible.emit("input"); second.change.click(); second.visible.value = "changed"; second.visible.emit("input");
    const original = first.row._discardPendingChange; first.row._discardPendingChange = () => { throw new Error("restoration failed"); }; let laterAttempted = false; const secondOriginal = second.row._discardPendingChange; second.row._discardPendingChange = (options) => { laterAttempted = true; secondOriginal(options); };
    const result = api.SettingsWorkflow.discardPendingChanges(form); assert.equal(laterAttempted, true); assert.equal(result.failures.length, 1); assert.equal(result.remainingDirtyRows.length, 1); assert.equal(first.row.dataset.dirty, "true"); assert.equal(second.row.dataset.dirty, "false"); original;
});

test("markup and CSS contain the explicit action model and no inheritance mode controls", () => {
    assert.doesNotMatch(settingRowMarkup, /setting-mode|image-mode|Keep change|Cancel edit|candidate/);
    assert.doesNotMatch(batchRowMarkup, /setting-mode|batch-mode|>\s*Inherit\s*<|>\s*Customize here\s*</);
    assert.match(settingRowMarkup, /class="setting-change/); assert.match(settingRowMarkup, /class="setting-revert/); assert.match(settingRowMarkup, /data-inherited-value=/);
    assert.match(batchRowMarkup, /batch-value-choice/); assert.match(batchRowMarkup, /class="batch-remove-draft-change/);
    assert.doesNotMatch(settingsCss, /setting-mode-group|batch-mode-group|image-mode-group/); assert.match(settingsCss, /\.setting-comparison/); assert.match(settingsCss, /\.setting-scope-header/);
    assert.match(settingsScript, /state\.editing/); assert.match(settingsScript, /function proposedState\(row\)/); assert.match(settingsScript, /RemoveOverride/); assert.doesNotMatch(settingsScript, /selectedModeControl|setSelectedMode|modeFromRow/);
});
