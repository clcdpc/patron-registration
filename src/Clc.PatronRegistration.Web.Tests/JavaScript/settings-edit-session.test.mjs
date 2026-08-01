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
    }
    addEventListener(name, callback) { this.listeners[name] = callback; }
    click() { this.listeners.click?.({ target: this }); }
    focus() { focused = this; this.focused = true; }
    reportValidity() { return true; }
    setAttribute() {}
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
const { initializeRow, blockActiveEdit, populateReviewList, handleSaveAttempt } = context.SettingsEditSessions;

test("scoped CSS makes every hidden settings element non-rendering", () => {
    const css = readFileSync(new URL("../../Clc.PatronRegistration.Web/wwwroot/css/settings.css", import.meta.url), "utf8");
    assert.match(css, /\.settings-administration-page\s+\[hidden\]\s*\{\s*display:\s*none\s*!important/);
});

function rowFixture({ operation = "Upsert", dirty = false, ownsOverride = true } = {}) {
    const controls = {
        change: new Control(), inherit: ownsOverride ? new Control() : null,
        apply: new Control(), cancel: new Control(), actions: new Control(),
        editor: new Control(), message: new Control(), operation: new Control(operation),
        value: new Control("server value"), index: new Control(), key: new Control()
    };
    const selectors = {
        ".edit-setting": controls.change, ".inherit-setting": controls.inherit,
        ".apply-setting": controls.apply, ".cancel-setting": controls.cancel,
        ".edit-actions": controls.actions, ".value-editor": controls.editor,
        ".inheritance-message": controls.message, ".operation": controls.operation,
        ".setting-value": controls.value, ".change-index": controls.index,
        ".change-key": controls.key
    };
    const category = { setAttribute(name) { this[name] = true; } };
    const row = {
        dataset: { appliedOperation: operation, dirty: dirty.toString(), displayName: "Example", oldValue: "old", sensitive: "false" },
        querySelector(selector) { return selectors[selector]; },
        querySelectorAll() { return [controls.index, controls.key, controls.operation]; },
        closest() { return category; },
        setAttribute(name) { this[name] = true; }
    };
    initializeRow(row);
    return { row, controls, category };
}

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
