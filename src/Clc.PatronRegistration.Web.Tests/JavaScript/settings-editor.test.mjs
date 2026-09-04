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
    setAttribute(name, value) {
        this.attributes[name] = String(value);
        if (name === "open") this.open = true;
        if (name === "disabled") this.disabled = true;
        if (name === "readonly") this.readOnly = true;
    }
    getAttribute(name) { return this.attributes[name] ?? null; }
    removeAttribute(name) {
        delete this.attributes[name];
        if (name === "disabled") this.disabled = false;
        if (name === "readonly") this.readOnly = false;
    }
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
    showModal() { this.open = true; this.showModalCount = (this.showModalCount || 0) + 1; }
    querySelector() { return null; }
    querySelectorAll() { return []; }
    closest() { return null; }
    getBoundingClientRect() { return { top: 0, bottom: 0 }; }
}

function makeRevertDialog() {
    const dialog = new NodeStub();
    const title = new NodeStub();
    const explanation = new NodeStub();
    const keep = new NodeStub();
    const affirm = new NodeStub();
    const html = new NodeStub();
    const text = new NodeStub();
    const friendly = new NodeStub();
    const image = new NodeStub();
    const imagePreview = new NodeStub();
    const imageFile = new NodeStub();
    const none = new NodeStub();
    const sensitive = new NodeStub();
    image.querySelector = (selector) => ({ "[data-revert-image-preview]": imagePreview, "[data-revert-image-file]": imageFile }[selector] || null);
    dialog.querySelector = (selector) => ({
        "#revert-confirm-title": title,
        "[data-revert-explanation]": explanation,
        "[data-revert-keep]": keep,
        "[data-revert-affirm]": affirm,
        "[data-revert-html]": html,
        "[data-revert-text]": text,
        "[data-revert-friendly]": friendly,
        "[data-revert-image]": image,
        "[data-revert-none]": none,
        "[data-revert-sensitive]": sensitive
    }[selector] || null);
    return { dialog, title, explanation, keep, affirm, html, text, friendly, image, imagePreview, imageFile, none, sensitive };
}

function createDocument() {
    const revert = makeRevertDialog();
    const document = {
        documentElement: { clientHeight: 0 },
        revert,
        querySelector(selector) {
            if (selector === "#revert-confirm") return revert.dialog;
            return revert.dialog.querySelector(selector);
        },
        querySelectorAll(selector) { return selector === "dialog" ? [revert.dialog] : []; },
        createElement(tagName) { const element = new NodeStub(); element.tagName = tagName.toUpperCase(); return element; }
    };
    return document;
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

function makeRow({ key = "registration_text", valueType = "shortstring", baselineMode = "customize", baselineValue = "original", value = baselineValue, sensitive = false, inherited = false, inheritedValue = inherited ? (valueType === "boolean" ? "false" : "Inherited value") : "", batch = false, draftChange = false } = {}) {
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
        presentationState: baselineMode === "inherit" ? (inherited ? "inherited" : "notset") : "customized",
        liveState: baselineMode === "inherit" ? (inherited ? "inherited" : "notset") : "customized",
        draftChange: String(draftChange),
        hasInherited: String(inherited),
        inheritedSummary: inherited ? "Inherited value" : "",
        inheritedValue,
        inheritedSource: inherited ? "Main Library" : "",
        liveSummary: baselineMode === "inherit" ? "Inherited value" : baselineValue
    };

    const change = new NodeStub(); change.disabled = false; change.type = "button";
    const revert = new NodeStub(); revert.disabled = false; revert.type = "button"; revert.hidden = baselineMode !== "customize";
    const scopeStatus = new NodeStub(); scopeStatus.textContent = baselineMode === "inherit" ? "Inherited from Main Library" : draftChange ? "Shared draft" : "Customized here";
    const idleSurface = new NodeStub();
    const editorSurface = new NodeStub(); editorSurface.hidden = true;
    const idleText = new NodeStub(); idleText.textContent = baselineMode === "inherit" ? (inheritedValue || "Not configured") : String(baselineValue || "Blank");
    const idleHtml = valueType === "html" ? new NodeStub() : null;
    if (idleHtml) { idleHtml.srcdoc = baselineMode === "inherit" ? inheritedValue : baselineValue; idleHtml.hidden = !idleHtml.srcdoc; }
    idleSurface.querySelector = (selector) => selector === "[data-idle-html]" ? idleHtml : selector === "[data-idle-text]" ? idleText : null;
    editorSurface.querySelector = () => null;
    const editorValue = baselineMode === "customize" ? baselineValue : inheritedValue;
    const visible = new NodeStub(sensitive ? "" : valueType === "boolean" ? editorValue || "false" : valueType === "ip-prefixes" ? "" : editorValue);
    visible.tagName = valueType === "html" || valueType === "emailtemplate" || valueType === "longstring" ? "TEXTAREA" : "INPUT";
    visible.disabled = false;
    visible.type = sensitive ? "password" : valueType === "date" ? "date" : valueType === "emailaddress" ? "email" : valueType === "uri" ? "url" : valueType === "integer" || valueType === "decimal" ? "number" : "text";
    const boolYes = new NodeStub(); boolYes.type = "radio"; boolYes.value = "true"; boolYes.dataset = { value: "true" }; boolYes.disabled = true;
    const boolNo = new NodeStub(); boolNo.type = "radio"; boolNo.value = "false"; boolNo.dataset = { value: "false" }; boolNo.disabled = true;
    boolYes.checked = String(editorValue).toLowerCase() === "true";
    boolNo.checked = !boolYes.checked;
    const booleanControls = valueType === "boolean" ? [boolYes, boolNo] : [];
    const binding = new NodeStub(baselineMode === "customize" ? baselineValue : ""); binding.disabled = true;
    const operation = new NodeStub(baselineMode === "inherit" ? "RemoveOverride" : "Upsert");
    const index = new NodeStub(); const keyControl = new NodeStub();
    const summary = batch ? null : new NodeStub();
    if (summary) { summary.textContent = idleText.textContent; summary.setAttribute("title", summary.textContent); }
    const status = batch ? null : new NodeStub(); if (status) status.textContent = scopeStatus.textContent;
    const batchStatus = batch ? new NodeStub() : null;
    const prefixEditor = valueType === "ip-prefixes" ? new NodeStub() : null;
    const prefixes = valueType === "ip-prefixes" ? String(editorValue || "").split(";").filter(Boolean).map((entry) => { const input = new NodeStub(entry); input.type = "text"; input.disabled = false; input.readOnly = true; return input; }) : [];
    const addPrefix = valueType === "ip-prefixes" ? new NodeStub() : null;
    const prefixRows = []; const removeButtons = [];
    if (prefixEditor) {
        prefixEditor.insertBefore = (wrapper) => { const input = wrapper.children[0]; prefixes.push(input); prefixRows.push(wrapper); removeButtons.push(wrapper.children[1]); prefixEditor.children.push(wrapper); wrapper.parentElement = prefixEditor; };
        prefixEditor.removeChild = (wrapper) => { const inputIndex = prefixes.indexOf(wrapper.children[0]); if (inputIndex >= 0) prefixes.splice(inputIndex, 1); const rowIndex = prefixRows.indexOf(wrapper); if (rowIndex >= 0) prefixRows.splice(rowIndex, 1); const removeIndex = removeButtons.indexOf(wrapper.children[1]); if (removeIndex >= 0) removeButtons.splice(removeIndex, 1); const childIndex = prefixEditor.children.indexOf(wrapper); if (childIndex >= 0) prefixEditor.children.splice(childIndex, 1); wrapper.parentElement = null; };
        prefixes.slice().forEach((input) => { const wrapper = new NodeStub(); const remove = new NodeStub(); wrapper.append(input, remove); wrapper.parentElement = prefixEditor; remove.closest = () => wrapper; prefixRows.push(wrapper); removeButtons.push(remove); prefixEditor.children.push(wrapper); });
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
        ".ip-prefix-add": addPrefix,
        "[data-idle-html]": idleHtml,
        "[data-idle-surface]": idleSurface,
        "[data-editor-surface]": editorSurface
    };
    const reveal = selector[".reveal-secret"];
    if (reveal) { reveal.textContent = "Reveal secret"; reveal.setAttribute("aria-expanded", "false"); reveal.setAttribute("aria-label", `Reveal ${key}`); }
    row.querySelector = (query) => selector[query] ?? null;
    row.querySelectorAll = (query) => {
        if (query === ".boolean-value, .batch-value-choice") return booleanControls;
        if (query.includes(".setting-value:not")) return valueType === "boolean" ? booleanControls : valueType === "ip-prefixes" ? [] : [visible];
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
    return { row, category, change, revert, scopeStatus, idleSurface, editorSurface, idleText, idleHtml, visible, binding, operation, summary, status, batchStatus, reveal, booleanYes: boolYes, booleanNo: boolNo, prefixes, addPrefix, removeButtons, prefixEditor };
}

function makeImageRow({ baselineMode = "inherit", baselineValue = "", inherited = true, inheritedMissing = false, localAssetValue = "", localAssetMissing = false } = {}) {
    const fixture = makeRow({ key: "header_image_asset_id", valueType: "image", baselineMode, baselineValue, value: baselineValue || "", inherited });
    const change = fixture.change;
    const chooseAnother = new NodeStub();
    const imageFile = new NodeStub(); imageFile.files = []; imageFile.dataset.uploadUrl = "/settings/assets/upload";
    const pending = new NodeStub(); const pendingPreview = new NodeStub(); const pendingFileName = new NodeStub(); const uploadStatus = new NodeStub();
    pending.querySelector = (selector) => ({ ".image-pending-preview": pendingPreview, "img": pendingPreview, ".image-pending-file-name": pendingFileName, ".image-upload-status": uploadStatus }[selector] || null);
    const idleCard = new NodeStub(); const idlePreview = new NodeStub(); const idleFileName = new NodeStub(); const idleMessage = new NodeStub();
    idleCard.querySelector = (selector) => ({ "[data-idle-image-preview]": idlePreview, "[data-idle-image-file]": idleFileName, "[data-idle-image-message]": idleMessage }[selector] || null);
    fixture.idleSurface.querySelector = (selector) => selector === "[data-idle-image-card]" ? idleCard : null;
    fixture.row.dataset.imageInheritedPreviewUrl = inherited && !inheritedMissing ? "/settings/assets/10" : "";
    fixture.row.dataset.imageInheritedFileName = inherited && !inheritedMissing ? "library-header.png" : "";
    fixture.row.dataset.imageInheritedMissing = String(inheritedMissing);
    fixture.row.dataset.imageIdlePreviewUrl = baselineMode === "customize" ? `/settings/assets/${baselineValue}` : "";
    fixture.row.dataset.imageIdleFileName = baselineMode === "customize" ? "local-header.png" : "";
    fixture.row.dataset.imageIdleMissing = "false";
    const localValue = localAssetValue || (baselineMode === "customize" ? baselineValue : "");
    fixture.row.dataset.imageLocalValue = localValue;
    fixture.row.dataset.imageLocalMissing = String(localAssetMissing);
    fixture.row.dataset.imageLocalFileName = localValue ? "local-header.png" : "";
    fixture.row.dataset.imageLocalPreviewUrl = localValue ? `/settings/assets/${localValue}` : "";
    const originalQuery = fixture.row.querySelector;
    fixture.row.querySelector = (selector) => ({ ".setting-change": change, ".image-upload-trigger": change, ".image-choose-another": chooseAnother, ".image-file": imageFile, ".image-pending": pending, ".image-browser-pending": pending, ".image-pending-file-name": pendingFileName, ".image-upload-status": uploadStatus, ".setting-value-binding": fixture.binding, "[data-idle-surface]": fixture.idleSurface, "[data-editor-surface]": fixture.editorSurface }[selector] || originalQuery(selector));
    return { ...fixture, change, chooseAnother, imageFile, pending, pendingPreview, pendingFileName, uploadStatus, idleCard, idlePreview, idleFileName, idleMessage };
}

const flush = () => new Promise((resolve) => setImmediate(resolve));

test("Change swaps the idle surface for a focused editor without a permanent duplicate", () => {
    const api = loadSettings();
    const setting = makeRow({ baselineValue: "original", value: "original", inherited: true });
    const form = makeForm([setting.row]);
    api.SettingsEditor.initializeStandardRow(setting.row, form);
    assert.equal(setting.row.dataset.dirty, "false");
    assert.equal(setting.idleSurface.hidden, false);
    assert.equal(setting.editorSurface.hidden, true);
    setting.change.click();
    assert.equal(setting.row.dataset.editing, "true");
    assert.equal(setting.row.dataset.dirty, "false");
    assert.equal(setting.idleSurface.hidden, true);
    assert.equal(setting.editorSurface.hidden, false);
    assert.equal(setting.visible.readOnly, false);
    assert.equal(focused, setting.visible);
});

test("customized Change alone stays clean and an input becomes a browser Upsert", () => {
    const api = loadSettings();
    const setting = makeRow({ baselineValue: "original", value: "original", inherited: true });
    const form = makeForm([setting.row]);
    api.SettingsEditor.initializeStandardRow(setting.row, form);
    setting.change.click();
    assert.equal(setting.row.dataset.dirty, "false");
    setting.visible.value = "changed"; setting.visible.emit("input");
    assert.equal(setting.row.dataset.dirty, "true");
    assert.equal(setting.operation.value, "Upsert");
    assert.equal(setting.binding.value, "changed");
});

test("inherited Change creates Upsert intent and Revert first opens a non-mutating preview", () => {
    const api = loadSettings();
    const setting = makeRow({ key: "inherited", baselineMode: "inherit", baselineValue: "", value: "library value", inherited: true });
    const form = makeForm([setting.row]);
    api.SettingsEditor.initializeStandardRow(setting.row, form);
    setting.change.click();
    assert.equal(setting.row.dataset.dirty, "true");
    assert.equal(setting.operation.value, "Upsert");
    assert.equal(setting.binding.value, "Inherited value");
    setting.revert.click();
    assert.equal(api.document.revert.dialog.open, true);
    assert.equal(setting.row.dataset.dirty, "true");
    assert.equal(setting.row.dataset.editing, "true");
    api.document.revert.keep.click();
    assert.equal(setting.row.dataset.dirty, "true");
    assert.equal(setting.row.dataset.editing, "true");
    assert.equal(focused, setting.revert);
    setting.revert.click();
    api.document.revert.affirm.click();
    assert.equal(setting.row.dataset.dirty, "false");
    assert.equal(setting.row.dataset.editing, "false");
    assert.equal(setting.operation.value, "RemoveOverride");
    assert.equal(setting.idleSurface.hidden, false);
    assert.equal(setting.editorSurface.hidden, true);
});

test("customized Revert does not mutate until affirmed and then creates RemoveOverride", () => {
    const api = loadSettings();
    const setting = makeRow({ key: "customized", baselineMode: "customize", baselineValue: "local value", value: "local value", inherited: true });
    const form = makeForm([setting.row]);
    api.SettingsEditor.initializeStandardRow(setting.row, form);
    setting.revert.click();
    assert.equal(setting.row.dataset.dirty, "false");
    assert.equal(setting.binding.value, "local value");
    assert.match(api.document.revert.title.textContent, /Main Library/);
    assert.equal(api.document.revert.keep.textContent, "Keep current value");
    api.document.revert.affirm.click();
    assert.equal(setting.row.dataset.dirty, "true");
    assert.equal(setting.operation.value, "RemoveOverride");
    assert.equal(setting.binding.value, "");
    assert.equal(focused, setting.change);
    const body = new NodeStub(); api.SettingsEditor.populateReviewTable(form, body);
    assert.equal(body.children[0].children[2].textContent, "Use Inherited value from Main Library");
});

test("Escape and Keep current value leave the proposed state untouched", () => {
    const api = loadSettings();
    const setting = makeRow({ baselineValue: "local", value: "local", inherited: true });
    const form = makeForm([setting.row]);
    api.SettingsEditor.initializeStandardRow(setting.row, form);
    setting.change.click(); setting.visible.value = "browser value"; setting.visible.emit("input");
    setting.revert.click();
    api.document.revert.dialog.emit("cancel");
    assert.equal(setting.row.dataset.dirty, "true");
    assert.equal(setting.visible.value, "browser value");
    assert.equal(focused, setting.revert);
});

test("no inherited value uses Remove customization and becomes Not configured", () => {
    const api = loadSettings();
    const setting = makeRow({ key: "local-only", baselineValue: "local", value: "local", inherited: false });
    const form = makeForm([setting.row]);
    api.SettingsEditor.initializeStandardRow(setting.row, form);
    setting.revert.click();
    assert.equal(api.document.revert.title.textContent, "Remove customization?");
    assert.match(api.document.revert.explanation.textContent, /No inherited value/);
    assert.equal(api.document.revert.affirm.textContent, "Remove customization");
    api.document.revert.affirm.click();
    assert.equal(setting.row.dataset.dirty, "true");
    assert.equal(setting.operation.value, "RemoveOverride");
    assert.equal(setting.idleText.textContent, "Not configured");
});

test("sensitive confirmation never exposes an inherited secret", () => {
    const api = loadSettings();
    const setting = makeRow({ key: "inherited-secret", baselineMode: "customize", baselineValue: "", value: "", sensitive: true, inherited: true, inheritedValue: "super-secret" });
    setting.row.dataset.inheritedValue = "";
    setting.row.dataset.inheritedSummary = "";
    const form = makeForm([setting.row]);
    api.SettingsEditor.initializeStandardRow(setting.row, form);
    setting.revert.click();
    assert.equal(api.document.revert.sensitive.hidden, false);
    assert.doesNotMatch(api.document.revert.explanation.textContent, /super-secret/);
    assert.doesNotMatch(api.document.revert.text.textContent, /super-secret/);
    api.document.revert.keep.click();
    assert.equal(setting.row.dataset.dirty, "false");
    setting.revert.click(); api.document.revert.affirm.click();
    assert.equal(setting.operation.value, "RemoveOverride");
    assert.equal(setting.row.dataset.dirty, "true");
});

test("discard closes active editors and returns every value surface to idle", () => {
    const api = loadSettings();
    const active = makeRow({ key: "active", baselineValue: "original", value: "original" });
    const changed = makeRow({ key: "changed", baselineValue: "saved", value: "saved" });
    const form = makeForm([active.row, changed.row]);
    api.SettingsEditor.initializeStandardRow(active.row, form);
    api.SettingsEditor.initializeStandardRow(changed.row, form);
    active.change.click();
    changed.change.click(); changed.visible.value = "browser value"; changed.visible.emit("input");
    api.SettingsWorkflow.discardPendingChanges(form);
    assert.equal(active.row.dataset.editing, "false");
    assert.equal(active.idleSurface.hidden, false);
    assert.equal(active.editorSurface.hidden, true);
    assert.equal(changed.row.dataset.dirty, "false");
    assert.equal(changed.idleSurface.hidden, false);
});

test("shared-draft baselines stay clean while draft RemoveOverride Change creates Upsert", () => {
    const api = loadSettings();
    const upsert = makeRow({ key: "draft-upsert", baselineMode: "customize", baselineValue: "draft value", value: "draft value", inherited: true, draftChange: true });
    const remove = makeRow({ key: "draft-remove", baselineMode: "inherit", baselineValue: "", value: "Draft inherited value", inherited: true, inheritedValue: "Draft inherited value", draftChange: true });
    const form = makeForm([upsert.row, remove.row]);
    api.SettingsEditor.initializeStandardRow(upsert.row, form); api.SettingsEditor.initializeStandardRow(remove.row, form);
    assert.equal(upsert.row.dataset.dirty, "false");
    upsert.change.click(); assert.equal(upsert.row.dataset.dirty, "false");
    remove.change.click();
    assert.equal(remove.row.dataset.dirty, "true");
    assert.equal(remove.operation.value, "Upsert");
    api.SettingsWorkflow.discardPendingChanges(form);
    assert.equal(remove.row.dataset.dirty, "false");
    assert.equal(remove.row.dataset.editing, "false");
    assert.equal(remove.idleSurface.hidden, false);
    assert.equal(remove.visible.value, "Draft inherited value");
});

test("Boolean and batch required controls are value radios only while editing", () => {
    const api = loadSettings();
    const boolean = makeRow({ key: "enabled", valueType: "boolean", baselineMode: "customize", baselineValue: "false", value: "false", inherited: true });
    const required = makeRow({ key: "require.EmailAddress", valueType: "boolean", baselineMode: "inherit", baselineValue: "", value: "false", inherited: true, inheritedValue: "false", batch: true });
    const form = makeForm([boolean.row, required.row]);
    api.SettingsEditor.initializeStandardRow(boolean.row, form); api.SettingsEditor.initializeStandardRow(required.row, form);
    assert.equal(boolean.booleanNo.checked, true); assert.equal(boolean.booleanNo.disabled, true);
    boolean.change.click(); assert.equal(boolean.booleanYes.disabled, false); assert.equal(boolean.booleanNo.disabled, false);
    boolean.booleanYes.checked = true; boolean.booleanNo.checked = false; boolean.booleanYes.emit("change");
    assert.equal(boolean.row.dataset.dirty, "true"); assert.equal(boolean.binding.value, "true");
    required.change.click(); assert.equal(required.booleanYes.disabled, false); assert.equal(required.row.dataset.dirty, "true"); assert.equal(required.operation.value, "Upsert");
});

test("IP prefixes remain editable only after Change and discard restores the list", () => {
    const api = loadSettings();
    const setting = makeRow({ key: "show_dl_ips", valueType: "ip-prefixes", baselineMode: "customize", baselineValue: "10.;192.168.", value: "10.;192.168.", inherited: true });
    const form = makeForm([setting.row]); api.SettingsEditor.initializeStandardRow(setting.row, form);
    assert.equal(setting.idleText.textContent, "10., 192.168.");
    assert.equal(setting.prefixes.every((input) => input.readOnly), true);
    setting.change.click(); assert.equal(setting.prefixes.every((input) => input.readOnly), false); setting.addPrefix.click();
    const added = setting.prefixes.at(-1); added.value = "172.16."; added.emit("input");
    assert.equal(setting.binding.value, "10.;192.168.;172.16."); assert.equal(setting.row.dataset.dirty, "true");
    api.SettingsWorkflow.discardPendingChanges(form);
    assert.deepEqual(setting.prefixes.map((input) => input.value), ["10.", "192.168."]); assert.equal(setting.row.dataset.dirty, "false");
});

test("HTML idle output is rendered and has no permanent editor preview", () => {
    const api = loadSettings();
    const html = makeRow({ key: "html", valueType: "html", baselineValue: "<p>Loaded HTML</p>", value: "<p>Loaded HTML</p>", inherited: true });
    const form = makeForm([html.row]); api.SettingsEditor.initializeStandardRow(html.row, form);
    assert.equal(html.idleHtml.srcdoc, "<p>Loaded HTML</p>");
    html.change.click();
    assert.equal(html.idleSurface.hidden, true); assert.equal(html.editorSurface.hidden, false); assert.equal(html.visible.value, "<p>Loaded HTML</p>");
    assert.equal(html.row.querySelector(".html-preview"), null); assert.equal(html.row.querySelector(".plain-text-preview"), null);
});

test("image Revert previews the inherited image before applying it", () => {
    const api = loadSettings();
    const image = makeImageRow({ baselineMode: "customize", baselineValue: "12", inherited: true });
    const form = makeForm([image.row]); api.SettingsEditor.initializeImageRow(image.row, form);
    image.revert.click();
    assert.equal(api.document.revert.title.textContent, "Revert to Main Library image?");
    assert.equal(api.document.revert.keep.textContent, "Keep current image");
    assert.equal(image.row.dataset.dirty, "false");
    api.document.revert.affirm.click();
    assert.equal(image.row.dataset.dirty, "true"); assert.equal(image.operation.value, "RemoveOverride"); assert.equal(focused, image.change);
});

test("missing inherited image confirmation keeps the empty thumbnail hidden", () => {
    const api = loadSettings();
    const image = makeImageRow({ baselineMode: "customize", baselineValue: "12", inherited: true, inheritedMissing: true });
    const form = makeForm([image.row]); api.SettingsEditor.initializeImageRow(image.row, form);
    image.revert.click();
    assert.equal(api.document.revert.image.hidden, false);
    assert.equal(api.document.revert.imagePreview.hidden, true);
    assert.equal(api.document.revert.imagePreview.attributes.src, undefined);
});

test("image upload keeps the existing async and review behavior", async () => {
    const api = loadSettings(createDocument(), { fetch: async () => ({ ok: true, async json() { return { assetId: 77, fileName: "replacement.png", previewUrl: "/settings/assets/77" }; } }) });
    const image = makeImageRow(); const form = makeForm([image.row]); api.SettingsEditor.initializeImageRow(image.row, form);
    image.change.click(); image.imageFile.files = [{ name: "replacement.png" }]; image.imageFile.emit("change"); await flush(); await flush();
    const body = new NodeStub(); api.SettingsEditor.populateReviewTable(form, body);
    assert.equal(body.children[0].children[2].textContent, "replacement.png");
    assert.equal(image.binding.value, "77"); assert.equal(image.row.dataset.dirty, "true");
});

test("late image responses cannot resurrect a discarded change", async () => {
    let resolveResponse;
    const api = loadSettings(createDocument(), { fetch: () => new Promise((resolve) => { resolveResponse = resolve; }) });
    const image = makeImageRow(); const form = makeForm([image.row]); api.SettingsEditor.initializeImageRow(image.row, form);
    image.change.click(); image.imageFile.files = [{ name: "replacement.png" }]; image.imageFile.emit("change");
    api.SettingsWorkflow.discardPendingChanges(form);
    resolveResponse({ ok: true, async json() { return { assetId: 55, fileName: "late.png", previewUrl: "/settings/assets/55" }; } }); await flush(); await flush();
    assert.equal(image.row.dataset.dirty, "false"); assert.equal(image.row.dataset.imageUploading, undefined); assert.equal(image.binding.value, "");
});

test("discard attempts every dirty row and leaves unresolved work visible", () => {
    const api = loadSettings(); const first = makeRow({ key: "failed-setting", baselineValue: "first", value: "first", inherited: true }); const second = makeRow({ key: "later-setting", baselineValue: "second", value: "second", inherited: true }); const form = makeForm([first.row, second.row]);
    api.SettingsEditor.initializeStandardRow(first.row, form); api.SettingsEditor.initializeStandardRow(second.row, form); first.change.click(); first.visible.value = "changed"; first.visible.emit("input"); second.change.click(); second.visible.value = "changed"; second.visible.emit("input");
    first.row._discardPendingChange = () => { throw new Error("restoration failed"); }; let laterAttempted = false; const secondOriginal = second.row._discardPendingChange; second.row._discardPendingChange = (options) => { laterAttempted = true; secondOriginal(options); };
    const result = api.SettingsWorkflow.discardPendingChanges(form); assert.equal(laterAttempted, true); assert.equal(result.failures.length, 1); assert.equal(result.remainingDirtyRows.length, 1); assert.equal(first.row.dataset.dirty, "true"); assert.equal(second.row.dataset.dirty, "false");
});

test("production markup and CSS use one surface and the reusable revert dialog", () => {
    assert.doesNotMatch(settingRowMarkup, /setting-comparison|setting-baseline|setting-effective|setting-scope-panel|setting-draft-value/);
    assert.doesNotMatch(batchRowMarkup, /setting-comparison|setting-baseline|setting-effective|setting-scope-panel|setting-draft-value/);
    assert.match(settingRowMarkup, /data-idle-surface/); assert.match(settingRowMarkup, /data-editor-surface/); assert.match(settingRowMarkup, /class="setting-revert"/);
    assert.match(batchRowMarkup, /data-idle-surface/); assert.match(batchRowMarkup, /data-editor-surface/); assert.match(batchRowMarkup, /batch-value-choice/);
    assert.doesNotMatch(settingsCss, /setting-comparison|setting-baseline|setting-effective|setting-scope-panel|setting-draft-value|plain-text-preview|\.html-preview/);
});
