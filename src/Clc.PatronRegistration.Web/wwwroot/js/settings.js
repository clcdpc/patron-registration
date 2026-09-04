(() => {
    const search = document.querySelector("#setting-search");
    const searchStatus = document.querySelector("#search-status");
    const form = document.querySelector("#settings-form");
    const reviewDialog = document.querySelector("#save-confirm");
    const revertDialog = document.querySelector("#revert-confirm");
    const statusRegion = document.querySelector("#settings-status");
    const imageUploadBlockedMessage = "Wait for the image upload to finish or choose another state before continuing.";
    const imageUploadRequiredMessage = "Upload an image to customize this scope or choose the inherited image instead.";
    const settingsUiStateStorageKey = "patron-registration.settings-admin.ui-state";
    const restoredSettingRows = new Set();
    let navigationGuard = null;
    let submitting = false;
    let pendingAction = null;
    let reviewSubmitter = null;
    let approvedForm = null;
    let approvedInvalidHandler = null;
    const unsavedDialog = document.querySelector("#unsaved-changes-dialog");
    const livePreviewDialog = document.querySelector("#live-preview-confirm");
    let statusFilter = null;

    function settingsUiStateStorage() {
        try {
            return globalThis.sessionStorage || globalThis.window?.sessionStorage || null;
        } catch {
            return null;
        }
    }

    function readUiState() {
        const storage = settingsUiStateStorage();
        try {
            if (!storage || typeof storage.getItem !== "function") return {};
            const raw = storage.getItem(settingsUiStateStorageKey);
            if (!raw) return {};
            const parsed = JSON.parse(raw);
            if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) return {};
            const state = {};
            if (typeof parsed.search === "string") state.search = parsed.search;
            if (typeof parsed.status === "string") state.status = parsed.status;
            if (Array.isArray(parsed.openSettingKeys)) {
                state.openSettingKeys = [...new Set(parsed.openSettingKeys.filter((key) => typeof key === "string"))];
            }
            if (typeof parsed.lastOpenedSettingKey === "string") state.lastOpenedSettingKey = parsed.lastOpenedSettingKey;
            return state;
        } catch {
            return {};
        }
    }

    function writeUiState(changes = {}) {
        const storage = settingsUiStateStorage();
        try {
            if (!storage || typeof storage.setItem !== "function") return;
            const state = { ...readUiState(), ...changes };
            if (Array.isArray(state.openSettingKeys)) {
                state.openSettingKeys = [...new Set(state.openSettingKeys.filter((key) => typeof key === "string"))];
            }
            if ("lastOpenedSettingKey" in state && typeof state.lastOpenedSettingKey !== "string") delete state.lastOpenedSettingKey;
            storage.setItem(settingsUiStateStorageKey, JSON.stringify(state));
        } catch {
            // Session storage is optional; the editor remains usable without it.
        }
    }

    function captureOpenSettingKeys() {
        return [...(document.querySelectorAll(".setting-row") || [])]
            .filter((row) => row.open && row.dataset?.settingKey)
            .map((row) => row.dataset.settingKey);
    }

    function persistOpenSettingState() {
        const rows = [...(document.querySelectorAll(".setting-row") || [])];
        const openKeys = new Set(captureOpenSettingKeys());
        const knownKeys = rows.map((row) => row.dataset?.settingKey).filter(Boolean);
        const storedKeys = new Set(readUiState().openSettingKeys || []);
        knownKeys.forEach((key) => openKeys.has(key) ? storedKeys.add(key) : storedKeys.delete(key));
        writeUiState({ openSettingKeys: [...storedKeys] });
    }

    function handleSettingRowToggle(row) {
        // Batch rows are table rows, not disclosures.
        if (typeof row.open !== "boolean") return;
        persistOpenSettingState();
        if (restoredSettingRows.has(row)) {
            restoredSettingRows.delete(row);
            if (row.open) return;
        }
        if (row.open && typeof row.dataset?.settingKey === "string") {
            writeUiState({ lastOpenedSettingKey: row.dataset.settingKey });
            scrollOpenedSettingIntoView(row);
        }
    }

    function initializeSettingsContext(contextForm) {
        if (!contextForm) return;
        const organizationScope = contextForm.querySelector("#organization-scope");
        const formCodeScope = contextForm.querySelector("#form-code-scope");
        if (organizationScope) organizationScope.dataset.committedValue = organizationScope.value;
        if (formCodeScope) formCodeScope.dataset.committedValue = formCodeScope.value;
        organizationScope?.addEventListener("change", () => {
            const action = () => { if (formCodeScope) formCodeScope.disabled = true; contextForm.requestSubmit(); };
            navigationGuard ? navigationGuard(action, organizationScope) : action();
        });
        formCodeScope?.addEventListener("change", () => {
            const action = () => contextForm.requestSubmit();
            navigationGuard ? navigationGuard(action, formCodeScope) : action();
        });
    }

    initializeSettingsContext(document.querySelector(".settings-context"));

    function controls(row, selector) {
        try { return row?.querySelector?.(selector) || null; } catch { return null; }
    }

    function controlsAll(row, selector) {
        try { return [...(row?.querySelectorAll?.(selector) || [])]; } catch { return []; }
    }

    function normalizeValue(row, value) {
        const text = (value === null || value === undefined ? "" : String(value)).replace(/\r\n?/g, "\n");
        if (row?.dataset?.valueType === "boolean") return text.trim().toLowerCase() === "true" ? "true" : "false";
        if (row?.dataset?.valueType === "ip-prefixes" || controls(row, "[data-ip-prefix-editor]")) {
            return text.split(";").map((part) => part.trim()).filter(Boolean).join(";");
        }
        return text;
    }

    function ipPrefixValue(row) {
        return controlsAll(row, ".ip-prefix-input")
            .map((input) => String(input.value ?? "").trim())
            .filter(Boolean)
            .join(";");
    }

    function baselineState(row) {
        const mode = row?.dataset?.baselineMode || "customize";
        return { mode, operation: mode === "inherit" ? "RemoveOverride" : "Upsert", value: normalizeValue(row, row?.dataset?.baselineValue || "") };
    }

    function booleanValueControls(row) {
        return controlsAll(row, ".boolean-value, .batch-value-choice");
    }

    function valueControls(row) {
        const booleanControls = booleanValueControls(row);
        if (booleanControls.length) return booleanControls;
        return controlsAll(row, ".setting-value:not(.setting-value-binding):not(.ip-prefix-input)");
    }

    function editorValue(row) {
        if (controls(row, "[data-ip-prefix-editor]")) return ipPrefixValue(row);
        const booleans = booleanValueControls(row);
        if (booleans.length) return booleans.find((control) => control.checked)?.dataset?.value || booleans.find((control) => control.checked)?.value || "false";
        return valueControls(row)[0]?.value ?? controls(row, ".setting-value-binding")?.value ?? "";
    }

    function setEditorValue(row, value) {
        const normalized = normalizeValue(row, value);
        if (controls(row, "[data-ip-prefix-editor]")) {
            const editor = controls(row, "[data-ip-prefix-editor]");
            const add = controls(row, ".ip-prefix-add");
            const values = normalized ? normalized.split(";") : [""];
            controlsAll(row, ".ip-prefix-row").forEach((prefixRow) => prefixRow.remove?.());
            values.forEach((prefix) => {
                if (typeof row._createPrefixRow === "function" && editor && add) editor.insertBefore(row._createPrefixRow(prefix), add);
            });
            return;
        }
        const booleans = booleanValueControls(row);
        if (booleans.length) {
            booleans.forEach((control) => { control.checked = normalizeValue(row, control.dataset?.value || control.value) === normalized; });
            return;
        }
        const control = valueControls(row)[0];
        if (!control) return;
        control.value = value === null || value === undefined ? "" : String(value);
    }

    function ensureEditorState(row) {
        if (row?._settingsEditorState) return row._settingsEditorState;
        const baseline = baselineState(row);
        row._settingsEditorState = { mode: baseline.mode, value: editorValue(row), editing: false };
        return row._settingsEditorState;
    }

    function proposedState(row) {
        if (typeof row?._settingsProposedState === "function") return row._settingsProposedState();
        const state = ensureEditorState(row);
        if (state.mode === "inherit") return { ...state, operation: "RemoveOverride", value: "" };
        state.value = editorValue(row);
        return { ...state, mode: "customize", operation: "Upsert", value: normalizeValue(row, state.value) };
    }

    function sameState(row, proposed, baseline = baselineState(row)) {
        // Sensitive values are never loaded into the browser. An empty
        // replacement while editing therefore leaves an inherited baseline
        // semantically untouched until a replacement is entered.
        if (row?.dataset?.sensitive === "true" && baseline.mode === "inherit" && proposed.mode === "customize" && !normalizeValue(row, proposed.value)) return true;
        if (proposed.mode !== baseline.mode) return false;
        return proposed.mode === "inherit" || normalizeValue(row, proposed.value) === normalizeValue(row, baseline.value);
    }

    function setBindingEnabled(row, enabled, state) {
        [".change-index", ".change-key", ".operation", ".setting-value-binding"].forEach((selector) => {
            controlsAll(row, selector).forEach((control) => { control.disabled = !enabled; });
        });
        const operation = controls(row, ".operation");
        const binding = controls(row, ".setting-value-binding");
        if (operation) operation.value = state.operation;
        if (binding) binding.value = state.value;
    }

    function compactSourcePreview(value) {
        const normalized = String(value ?? "").replace(/\s+/g, " ").trim();
        if (!normalized) return "Blank";
        return normalized.length <= 160 ? normalized : `${normalized.slice(0, 160).trimEnd()}…`;
    }

    function safeBrowserSummary(row, value, operation) {
        if (operation === "RemoveOverride") return row?.dataset?.hasInherited === "true" ? "Use inherited value" : "Remove customization";
        if (row?.dataset?.sensitive === "true") return "Replacement entered";
        const valueType = row?.dataset?.valueType;
        if (row?.dataset?.batchRequired === "true") return String(value).toLowerCase() === "true" ? "Required" : "Optional";
        if (valueType === "boolean") return String(value).toLowerCase() === "true" ? "Yes" : "No";
        if (valueType === "enumeration" && String(value).toLowerCase() === "barcode") return "Barcode";
        if (valueType === "enumeration" && String(value).toLowerCase() === "magstripe") return "Magnetic stripe";
        if (row?.dataset?.htmlCapable === "true" || valueType === "longstring" || valueType === "html" || valueType === "emailtemplate") return compactSourcePreview(value);
        return String(value ?? "").trim() || "Blank";
    }

    function renderBrowserPendingSummary(row, state) {
        const summary = controls(row, ".summary-value");
        const status = controls(row, ".setting-status > span") || controls(row, ".setting-status");
        const pendingValue = safeBrowserSummary(row, state.value, state.operation);
        const text = `Unsaved: ${pendingValue}`;
        if (summary) {
            summary.textContent = text;
            summary.setAttribute?.("title", text);
        }
        if (status) status.textContent = "Unsaved in this browser";
    }

    function updateEditorAvailability(row, state) {
        const enabled = Boolean(state.editing && state.mode === "customize");
        valueControls(row).forEach((control) => {
            const tagName = String(control.tagName || "").toUpperCase();
            const type = String(control.type || "").toLowerCase();
            const canReadOnly = tagName === "TEXTAREA" || (tagName === "INPUT" && ["text", "email", "url", "number", "password"].includes(type));
            if (canReadOnly) {
                control.readOnly = !enabled;
                control.disabled = false;
            } else control.disabled = !enabled;
        });
        controlsAll(row, ".ip-prefix-input").forEach((control) => {
            control.readOnly = !enabled;
            control.disabled = false;
        });
        controlsAll(row, ".ip-prefix-add, .ip-prefix-remove").forEach((control) => { control.disabled = !enabled; });
        const reveal = controls(row, ".reveal-secret");
        if (reveal) reveal.disabled = !enabled;
        row.dataset.editing = state.editing ? "true" : "false";
    }

    function displayValue(row, value, hasValue = true) {
        if (row?.dataset?.sensitive === "true") return hasValue ? "Configured" : "Not configured";
        if (!hasValue) return "Not configured";
        const text = value === null || value === undefined ? "" : String(value);
        if (!text) return "Blank";
        if (row?.dataset?.batchRequired === "true") return text.trim().toLowerCase() === "true" ? "Required" : "Optional";
        if (row?.dataset?.valueType === "boolean") return text.trim().toLowerCase() === "true" ? "Yes" : "No";
        if (row?.dataset?.valueType === "enumeration") {
            if (text.toLowerCase() === "barcode") return "Barcode";
            if (text.toLowerCase() === "magstripe") return "Magnetic stripe";
        }
        if (row?.dataset?.valueType === "ip-prefixes" || row?.dataset?.settingKey === "show_dl_ips") return text.split(";").map((part) => part.trim()).filter(Boolean).join(", ");
        if (row?.dataset?.valueType === "date" || row?.dataset?.valueType === "nullabledate") {
            const date = /^\d{4}-\d{2}-\d{2}$/.test(text) ? new Date(`${text}T00:00:00Z`) : null;
            if (date && !Number.isNaN(date.valueOf())) return new Intl.DateTimeFormat(undefined, { year: "numeric", month: "long", day: "numeric", timeZone: "UTC" }).format(date);
        }
        if (row?.dataset?.settingKey === "reset_seconds") return `${text} seconds`;
        if (row?.dataset?.valueType === "image") return row.dataset.imageInheritedFileName || "Uploaded image";
        return text;
    }

    function updateIdleSurface(row, state) {
        const idle = controls(row, "[data-idle-surface]");
        const editor = controls(row, "[data-editor-surface]");
        if (!idle || !editor) return;
        idle.hidden = Boolean(state.editing);
        editor.hidden = !state.editing;

        if (state.editing) return;
        const hasInherited = row?.dataset?.hasInherited === "true";
        const inheritedValue = row?.dataset?.inheritedValue || "";
        const useInherited = state.mode === "inherit";
        const value = useInherited ? inheritedValue : row?._settingsInitialEditorValue || "";
        const hasValue = useInherited ? hasInherited : row?.dataset?.baselineMode === "customize";
        const html = controls(idle, "[data-idle-html]");
        const text = controls(idle, "[data-idle-text]");
        if (html) {
            const htmlValue = useInherited ? row?.dataset?.inheritedHtml || inheritedValue : row?._settingsInitialHtmlValue || "";
            html.hidden = !hasValue || !htmlValue;
            html.srcdoc = htmlValue;
        }
        if (text) {
            text.hidden = Boolean(html && !html.hidden);
            if (row?.dataset?.valueType === "longstring" || row?.dataset?.valueType === "html" || row?.dataset?.valueType === "emailtemplate") {
                text.textContent = hasValue && value ? value : displayValue(row, value, hasValue);
            } else {
                text.textContent = displayValue(row, value, hasValue);
            }
        }
    }

    function resetSensitiveEditor(row) {
        if (row?.dataset?.sensitive !== "true") return;
        const input = controls(row, ".setting-value:not(.setting-value-binding)") || controls(row, ".setting-value");
        const reveal = controls(row, ".reveal-secret");
        if (input) {
            input.value = "";
            input.type = "password";
        }
        if (reveal) {
            reveal.textContent = "Reveal secret";
            reveal.setAttribute("aria-expanded", "false");
            reveal.setAttribute("aria-label", `Reveal ${row.dataset.displayName || "secret"}`);
        }
    }

    function clearRevertTarget() {
        if (!revertDialog) return;
        const html = revertDialog.querySelector?.("[data-revert-html]");
        const text = revertDialog.querySelector?.("[data-revert-text]");
        const friendly = revertDialog.querySelector?.("[data-revert-friendly]");
        const image = revertDialog.querySelector?.("[data-revert-image]");
        const none = revertDialog.querySelector?.("[data-revert-none]");
        const sensitive = revertDialog.querySelector?.("[data-revert-sensitive]");
        [html, text, friendly, image, none, sensitive].forEach((element) => { if (element) element.hidden = true; });
        if (html) html.srcdoc = "";
        const preview = image?.querySelector?.("[data-revert-image-preview]");
        if (preview) {
            preview.hidden = true;
            preview.removeAttribute?.("src");
        }
        const file = image?.querySelector?.("[data-revert-image-file]");
        if (file) file.textContent = "";
    }

    function openRevertDialog(row, trigger) {
        if (!revertDialog?.showModal) return false;
        const hasInherited = row?.dataset?.hasInherited === "true";
        const sensitive = row?.dataset?.sensitive === "true";
        const image = row?.dataset?.valueType === "image";
        const source = row?.dataset?.inheritedSource || "the inherited scope";
        const title = revertDialog.querySelector?.("#revert-confirm-title");
        const explanation = revertDialog.querySelector?.("[data-revert-explanation]");
        const keep = revertDialog.querySelector?.("[data-revert-keep]");
        const affirm = revertDialog.querySelector?.("[data-revert-affirm]");
        if (title) title.textContent = hasInherited
            ? `Revert to ${source} ${image ? "image" : "value"}?`
            : image ? "Remove image customization?" : "Remove customization?";
        if (explanation) explanation.textContent = hasInherited
            ? sensitive
                ? `An inherited value is configured by ${source}. The inherited secret cannot be displayed.`
                : `This setting will use the value inherited from ${source}.`
            : "No inherited value is configured for this setting. Removing this customization will leave the setting not configured.";
        if (keep) keep.textContent = image ? "Keep current image" : "Keep current value";
        if (affirm) affirm.textContent = hasInherited ? image ? "Use inherited image" : "Use inherited value" : image ? "Remove customization" : "Remove customization";

        clearRevertTarget();
        const html = revertDialog.querySelector?.("[data-revert-html]");
        const text = revertDialog.querySelector?.("[data-revert-text]");
        const friendly = revertDialog.querySelector?.("[data-revert-friendly]");
        const imageTarget = revertDialog.querySelector?.("[data-revert-image]");
        const none = revertDialog.querySelector?.("[data-revert-none]");
        const sensitiveTarget = revertDialog.querySelector?.("[data-revert-sensitive]");
        if (!hasInherited) {
            if (none) none.hidden = false;
        } else if (sensitive) {
            if (sensitiveTarget) sensitiveTarget.hidden = false;
        } else if (image) {
            const preview = imageTarget?.querySelector?.("[data-revert-image-preview]");
            const file = imageTarget?.querySelector?.("[data-revert-image-file]");
            if (preview && row.dataset.imageInheritedPreviewUrl) {
                preview.src = row.dataset.imageInheritedPreviewUrl;
                preview.hidden = false;
            }
            if (file) file.textContent = row.dataset.imageInheritedFileName || (row.dataset.imageInheritedMissing === "true" ? "Inherited image is missing." : "No image configured.");
            if (imageTarget) imageTarget.hidden = false;
        } else if (row?.dataset?.inheritedHtml) {
            if (html) { html.srcdoc = row.dataset.inheritedHtml; html.hidden = false; }
        } else if (row?.dataset?.valueType === "longstring" || row?.dataset?.valueType === "emailtemplate") {
            if (text) { text.textContent = row.dataset.inheritedValue || "Blank"; text.hidden = false; }
        } else if (friendly) {
            friendly.textContent = row.dataset.inheritedSummary || displayValue(row, row.dataset.inheritedValue, true);
            friendly.hidden = false;
        }

        revertDialog._row = row;
        revertDialog._trigger = trigger || controls(row, ".setting-revert");
        revertDialog.showModal();
        revertDialog.querySelector?.("[data-revert-keep]")?.focus?.();
        return true;
    }

    function closeRevertDialog(confirm = false) {
        if (!revertDialog) return;
        const row = revertDialog._row;
        const trigger = revertDialog._trigger;
        if (revertDialog.open) revertDialog.close();
        if (confirm && row?._applyRevert) {
            row._applyRevert();
            controls(row, ".setting-change")?.focus?.();
        } else {
            trigger?.focus?.();
        }
        revertDialog._row = null;
        revertDialog._trigger = null;
    }

    function updatePendingActions(settingsForm = form) {
        if (!settingsForm) return;
        const actions = settingsForm.querySelector?.(".settings-actions");
        if (!actions) return;
        const count = settingsForm.querySelectorAll?.('.setting-row[data-dirty="true"]')?.length || 0;
        actions.hidden = count === 0;
        const status = actions.querySelector?.(".pending-changes-status");
        if (status) status.textContent = count === 0 ? "" : `${count} ${count === 1 ? "change" : "changes"} unsaved in this browser`;
        actions.querySelectorAll?.("[data-label-template]")?.forEach((button) => {
            button.textContent = button.dataset.labelTemplate.replace("{count}", count).replace("{noun}", count === 1 ? "change" : "changes");
        });
        if (statusFilter?.value === "unsaved") applyFilters();
    }

    function updateStandardRow(row, settingsForm = form, options = {}) {
        const state = proposedState(row);
        const baseline = baselineState(row);
        const dirty = !sameState(row, state, baseline);
        row.dataset.dirty = dirty.toString();
        setBindingEnabled(row, dirty, state);
        updateEditorAvailability(row, state);
        updateIdleSurface(row, state);
        const change = controls(row, ".setting-change");
        const revert = controls(row, ".setting-revert");
        const scopeStatus = controls(row, ".setting-scope-status");
        if (change) change.hidden = Boolean(state.editing);
        if (revert) {
            const canRevert = state.mode === "customize" && (baseline.mode === "customize" || state.editing || dirty);
            revert.hidden = !canRevert;
        }
        if (scopeStatus) scopeStatus.textContent = dirty ? "Unsaved in this browser" : state.editing ? "Editing" : row._settingsScopeCleanStatus || "";
        if (dirty) {
            renderBrowserPendingSummary(row, state);
        } else {
            const clean = row._settingsCleanPresentation;
            const summary = controls(row, ".summary-value");
            const status = controls(row, ".setting-status > span") || controls(row, ".setting-status");
            if (summary && clean) {
                summary.textContent = clean.summary;
                if (clean.title === null || clean.title === undefined) summary.removeAttribute?.("title");
                else summary.setAttribute?.("title", clean.title);
            }
            if (status && clean) status.textContent = clean.status;
        }
        if (options.updateActions !== false) updatePendingActions(settingsForm);
        return dirty;
    }

    function syncBlockingStatus(settingsForm = form, status = statusRegion) {
        if (!status) return;
        if (hasImageUpload(settingsForm)) {
            const needsImage = settingsForm?.querySelector?.('.setting-row[data-image-needs-upload="true"]');
            status.textContent = needsImage
                ? needsImage.dataset?.hasInherited === "true" ? imageUploadRequiredMessage : "Upload an image to customize this scope or remove customization."
                : imageUploadBlockedMessage;
            status.dataset.statusKind = "blocking";
            status.hidden = false;
            return "upload";
        }
        if (status.dataset?.statusKind === "blocking") {
            delete status.dataset.statusKind;
            status.hidden = true;
            status.textContent = "";
        }
    }

    function initializeStandardRow(row, settingsForm) {
        const summary = controls(row, ".summary-value");
        const status = controls(row, ".setting-status > span") || controls(row, ".setting-status");
        const scopeStatus = controls(row, ".setting-scope-status");
        const initialValues = valueControls(row);
        const initialEditorValue = controls(row, "[data-ip-prefix-editor]") ? ipPrefixValue(row) : editorValue(row);
        row._settingsCleanPresentation = { summary: summary?.textContent || "", title: summary?.getAttribute?.("title"), status: status?.textContent || "" };
        row._settingsScopeCleanStatus = scopeStatus?.textContent || row._settingsCleanPresentation.status;
        row._settingsInitialEditorValue = initialEditorValue;
        row._settingsInitialIpPrefixValues = controlsAll(row, ".ip-prefix-input").map((input) => String(input.value ?? ""));
        row._settingsInitialHtmlValue = controls(row, "[data-idle-html]")?.srcdoc || "";
        ensureEditorState(row);

        const change = controls(row, ".setting-change");
        const revert = controls(row, ".setting-revert");
        change?.addEventListener("click", () => {
            const state = ensureEditorState(row);
            if (row.dataset?.sensitive === "true") {
                state.mode = "customize";
                state.value = "";
                setEditorValue(row, "");
                resetSensitiveEditor(row);
            } else {
                state.mode = "customize";
                state.value = editorValue(row);
            }
            state.editing = true;
            updateStandardRow(row, settingsForm);
            (valueControls(row)[0] || controls(row, ".ip-prefix-input"))?.focus?.();
        });
        row._applyRevert = () => {
            const state = ensureEditorState(row);
            state.mode = "inherit";
            state.value = row.dataset?.sensitive === "true" ? "" : row.dataset?.inheritedValue || "";
            state.editing = false;
            setEditorValue(row, state.value);
            if (row.dataset?.sensitive === "true") resetSensitiveEditor(row);
            updateStandardRow(row, settingsForm);
        };
        revert?.addEventListener("click", (event) => openRevertDialog(row, event.currentTarget));

        initialValues.forEach((value) => {
            const update = () => updateStandardRow(row, settingsForm);
            value.addEventListener("input", update);
            value.addEventListener("change", update);
        });
        const reveal = controls(row, ".reveal-secret");
        reveal?.addEventListener("click", () => {
            const value = valueControls(row)[0];
            if (!value) return;
            const revealing = value.type === "password";
            value.type = revealing ? "text" : "password";
            reveal.setAttribute("aria-expanded", revealing.toString());
            reveal.textContent = revealing ? "Hide secret" : "Reveal secret";
            reveal.setAttribute("aria-label", `${revealing ? "Hide" : "Reveal"} ${row.dataset.displayName || "secret"}`);
        });

        const prefixEditor = controls(row, "[data-ip-prefix-editor]");
        const addPrefix = controls(row, ".ip-prefix-add");
        controlsAll(row, ".ip-prefix-input").forEach((input) => {
            input.addEventListener("input", () => updateStandardRow(row, settingsForm));
            input.addEventListener("change", () => updateStandardRow(row, settingsForm));
        });
        const createPrefixRow = (initialValue = "") => {
            const wrapper = document.createElement("div");
            wrapper.className = "ip-prefix-row";
            const input = document.createElement("input");
            input.className = "ip-prefix-input";
            input.type = "text";
            input.value = initialValue;
            input.setAttribute("aria-label", "On-site IP prefix");
            const remove = document.createElement("button");
            remove.type = "button";
            remove.className = "ip-prefix-remove";
            remove.textContent = "Remove";
            wrapper.append(input, remove);
            input.addEventListener("input", () => updateStandardRow(row, settingsForm));
            input.addEventListener("change", () => updateStandardRow(row, settingsForm));
            remove.addEventListener("click", () => { if (!remove.disabled) { wrapper.remove(); updateStandardRow(row, settingsForm); } });
            return wrapper;
        };
        row._createPrefixRow = createPrefixRow;
        addPrefix?.addEventListener("click", () => {
            if (addPrefix.disabled) return;
            const wrapper = createPrefixRow();
            prefixEditor?.insertBefore(wrapper, addPrefix);
            wrapper.children?.[0]?.focus?.();
        });
        controlsAll(row, ".ip-prefix-remove").forEach((remove) => remove.addEventListener("click", () => {
            if (remove.disabled) return;
            remove.closest?.(".ip-prefix-row")?.remove();
            updateStandardRow(row, settingsForm);
        }));

        row._discardPendingChange = (options = {}) => {
            const baseline = baselineState(row);
            const state = ensureEditorState(row);
            state.mode = baseline.mode;
            state.editing = false;
            state.value = baseline.mode === "customize" ? baseline.value : row._settingsInitialEditorValue;
            if (row.dataset?.sensitive === "true") resetSensitiveEditor(row);
            else setEditorValue(row, state.value);
            if (controls(row, "[data-ip-prefix-editor]")) {
                const desiredValues = Array.isArray(row._settingsInitialIpPrefixValues) && row._settingsInitialIpPrefixValues.length ? row._settingsInitialIpPrefixValues : [""];
                controlsAll(row, ".ip-prefix-row").forEach((prefixRow) => prefixRow.remove());
                desiredValues.forEach((prefix) => prefixEditor?.insertBefore(createPrefixRow(prefix), addPrefix));
            }
            updateStandardRow(row, settingsForm, options);
        };
        updateStandardRow(row, settingsForm);
    }

    function initializeImageRow(row, settingsForm) {
        const activeForm = settingsForm || form;
        const chooseAnother = controls(row, ".image-choose-another");
        const change = controls(row, ".setting-change");
        const revert = controls(row, ".setting-revert");
        const imageFile = controls(row, ".image-file");
        const pending = controls(row, ".image-pending") || controls(row, ".image-browser-pending");
        const pendingPreview = controls(pending, ".image-pending-preview") || controls(pending, "img");
        const pendingFileName = controls(pending, ".image-pending-file-name");
        const uploadStatus = controls(pending, ".image-upload-status");
        const operation = controls(row, ".operation");
        const binding = controls(row, ".setting-value-binding") || controls(row, ".setting-value");
        const summary = controls(row, ".summary-value");
        const rowStatus = controls(row, ".setting-status > span") || controls(row, ".setting-status");
        const scopeStatus = controls(row, ".setting-scope-status");
        const idleSurface = controls(row, "[data-idle-surface]");
        const editorSurface = controls(row, "[data-editor-surface]");
        const baseline = baselineState(row);
        const clean = { summary: summary?.textContent || "", title: summary?.getAttribute?.("title"), status: rowStatus?.textContent || "" };
        const imageState = {
            mode: baseline.mode,
            editing: false,
            fileName: "",
            previewUrl: "",
            message: "",
            error: false,
            objectUrl: null,
            uploadPromise: null,
            fallback: null,
            requestVersion: 0
        };

        function revokeObjectUrl() {
            if (imageState.objectUrl && globalThis.URL?.revokeObjectURL) globalThis.URL.revokeObjectURL(imageState.objectUrl);
            imageState.objectUrl = null;
        }

        function discardFallback() {
            const fallbackUrl = imageState.fallback?.objectUrl;
            imageState.fallback = null;
            if (fallbackUrl && fallbackUrl !== imageState.objectUrl && globalThis.URL?.revokeObjectURL) {
                globalThis.URL.revokeObjectURL(fallbackUrl);
            }
        }

        function currentImageState() {
            const mode = imageState.mode;
            const operationName = mode === "inherit" ? "RemoveOverride" : "Upsert";
            return { mode, operation: operationName, value: operationName === "Upsert" ? String(binding?.value || "") : "" };
        }

        row._settingsProposedState = currentImageState;

        function renderImageIdle() {
            const card = controls(idleSurface, "[data-idle-image-card]");
            if (!card) return;
            const preview = controls(card, "[data-idle-image-preview]");
            const file = controls(card, "[data-idle-image-file]");
            const message = controls(card, "[data-idle-image-message]");
            const inherited = imageState.mode === "inherit";
            const hasInherited = row.dataset.hasInherited === "true";
            const previewUrl = inherited ? row.dataset.imageInheritedPreviewUrl : row.dataset.imageIdlePreviewUrl;
            const fileName = inherited ? row.dataset.imageInheritedFileName : row.dataset.imageIdleFileName;
            const missing = inherited ? row.dataset.imageInheritedMissing === "true" : row.dataset.imageIdleMissing === "true";
            if (preview) {
                preview.hidden = !previewUrl;
                if (previewUrl) preview.src = previewUrl;
                else preview.removeAttribute?.("src");
            }
            if (file) {
                file.hidden = !fileName;
                file.textContent = fileName || "";
            }
            if (message) {
                message.hidden = Boolean(previewUrl || fileName) && !missing;
                message.textContent = missing ? "The configured uploaded image is missing." : inherited ? hasInherited ? "Use inherited image." : "Not configured" : hasInherited ? "No image configured." : "Not configured";
            }
        }

        function renderImage(options = {}) {
            const state = currentImageState();
            const semanticDirty = (state.mode !== baseline.mode || (state.mode === "customize" && normalizeValue(row, state.value) !== normalizeValue(row, baseline.value))) &&
                !(baseline.mode === "inherit" && state.mode === "customize" && !normalizeValue(row, state.value));
            const dirty = semanticDirty || Boolean(row.dataset.imageNeedsUpload === "true") || Boolean(row.dataset.imageUploading === "true");
            row.dataset.dirty = dirty.toString();
            row.dataset.editing = imageState.editing ? "true" : "false";
            if (idleSurface) idleSurface.hidden = imageState.editing;
            if (editorSurface) editorSurface.hidden = !imageState.editing;
            renderImageIdle();
            if (imageFile) imageFile.disabled = !imageState.editing;
            if (operation) operation.value = state.operation;
            setBindingEnabled(row, dirty, state);
            if (row.dataset.imageNeedsUpload === "true" && uploadStatus) {
                uploadStatus.textContent = "Upload an image to customize this scope.";
            }
            if (dirty) {
                const pendingText = state.operation === "RemoveOverride"
                    ? (row.dataset.hasInherited === "true" ? "Use inherited image" : "Remove customization")
                    : imageState.fileName || "new image";
                if (summary) { summary.textContent = `Unsaved: ${pendingText}`; summary.setAttribute?.("title", summary.textContent); }
                if (rowStatus) rowStatus.textContent = "Unsaved in this browser";
            } else {
                if (summary) { summary.textContent = clean.summary; clean.title === null ? summary.removeAttribute?.("title") : summary.setAttribute?.("title", clean.title); }
                if (rowStatus) rowStatus.textContent = clean.status;
            }
            if (scopeStatus) scopeStatus.textContent = dirty ? "Unsaved in this browser" : imageState.editing ? "Editing" : clean.status;
            const hasPendingImage = Boolean(row.dataset.imageNeedsUpload === "true" || row.dataset.imageUploading === "true" || imageState.error || imageState.fileName || imageState.previewUrl || imageState.message);
            if (change) change.hidden = imageState.editing && hasPendingImage;
            if (revert) revert.hidden = !(state.mode === "customize" && (baseline.mode === "customize" || imageState.editing || dirty));
            if (options.updateActions !== false) updatePendingActions(activeForm);
            syncBlockingStatus(activeForm);
        }

        function renderPending() {
            if (!pending) return;
            const state = currentImageState();
            const hasState = !sameState(row, state, baseline) ||
                row.dataset.imageNeedsUpload === "true" ||
                row.dataset.imageUploading === "true";
            const hasPending = Boolean(imageState.error) ||
                (hasState && Boolean(imageState.fileName || imageState.previewUrl || imageState.message));
            pending.hidden = !hasPending;
            if (pendingPreview) {
                pendingPreview.hidden = !imageState.previewUrl;
                if (imageState.previewUrl) pendingPreview.src = imageState.previewUrl;
                else pendingPreview.removeAttribute?.("src");
            }
            if (pendingFileName) pendingFileName.textContent = imageState.fileName;
            if (uploadStatus) {
                uploadStatus.textContent = imageState.message;
                uploadStatus.classList?.toggle?.("image-upload-error", imageState.error);
            }
            if (chooseAnother) chooseAnother.hidden = !imageState.editing;
        }

        function restoreBaseline(message = "", options = {}) {
            imageState.requestVersion++;
            imageState.uploadPromise = null;
            discardFallback();
            revokeObjectUrl();
            delete row.dataset.imageUploading;
            delete row.dataset.imageNeedsUpload;
            if (imageFile) imageFile.value = "";
            imageState.mode = baseline.mode;
            imageState.editing = false;
            if (binding) binding.value = baseline.value;
            if (operation) operation.value = baseline.operation;
            imageState.fileName = "";
            imageState.previewUrl = "";
            imageState.message = message;
            imageState.error = Boolean(message);
            renderPending();
            renderImage(options);
        }

        function changeImage() {
            imageState.mode = "customize";
            imageState.editing = true;
            imageState.error = false;
            imageState.fileName = "";
            imageState.previewUrl = "";
            if (baseline.mode === "inherit") {
                const localAssetValue = String(row.dataset.imageLocalValue || "").trim();
                const canRestoreLocalAsset = row.dataset.imageLocalMissing !== "true" && Boolean(localAssetValue);
                if (canRestoreLocalAsset) {
                    if (binding) binding.value = localAssetValue;
                    imageState.fileName = row.dataset.imageLocalFileName || row.dataset.liveSummary || "Current image";
                    imageState.previewUrl = row.dataset.imageLocalPreviewUrl || "";
                    imageState.message = "Reuse the current image at this scope.";
                    delete row.dataset.imageNeedsUpload;
                } else {
                    if (binding) binding.value = "";
                    imageState.message = "Upload an image to customize this scope.";
                    row.dataset.imageNeedsUpload = "true";
                }
            } else {
                if (binding && !binding.value) binding.value = baseline.value;
                imageState.message = "";
                delete row.dataset.imageNeedsUpload;
            }
            renderPending();
            renderImage();
            imageFile?.click?.();
        }

        function revertImage() {
            imageState.requestVersion++;
            imageState.uploadPromise = null;
            discardFallback();
            revokeObjectUrl();
            delete row.dataset.imageUploading;
            delete row.dataset.imageNeedsUpload;
            imageState.mode = "inherit";
            imageState.editing = false;
            if (binding) binding.value = "";
            if (operation) operation.value = "RemoveOverride";
            imageState.fileName = "";
            imageState.previewUrl = row.dataset.imageInheritedPreviewUrl || "";
            imageState.message = row.dataset.imageInheritedMissing === "true"
                ? "The inherited uploaded image is missing."
                : row.dataset.hasInherited === "true" ? "Use inherited image." : "No image will be configured.";
            imageState.error = false;
            renderPending();
            renderImage();
        }

        function setUploadPending(assetId, fileName, previewUrl) {
            delete row.dataset.imageNeedsUpload;
            delete row.dataset.imageUploading;
            imageState.fallback = null;
            imageState.mode = "customize";
            imageState.editing = true;
            if (binding) binding.value = String(assetId);
            imageState.fileName = fileName;
            imageState.previewUrl = previewUrl;
            imageState.message = `${fileName} is ready to save.`;
            imageState.error = false;
            renderPending();
            renderImage();
        }

        async function uploadImage(file) {
            if (!imageFile?.dataset.uploadUrl || !file) return false;
            const current = currentImageState();
            const hasPendingState = !sameState(row, current, baseline) && Boolean(imageState.fileName || imageState.previewUrl);
            const hasUsableFallback = current.operation === "RemoveOverride" || Number(current.value) > 0;
            const fallback = hasPendingState && hasUsableFallback
                ? { ...current, fileName: imageState.fileName, previewUrl: imageState.previewUrl, objectUrl: imageState.objectUrl }
                : null;
            imageState.requestVersion++;
            const requestVersion = imageState.requestVersion;
            if (fallback?.objectUrl) imageState.objectUrl = null;
            else revokeObjectUrl();
            imageState.fallback = fallback;
            imageState.fileName = file.name;
            imageState.previewUrl = globalThis.URL?.createObjectURL?.(file) || "";
            imageState.objectUrl = imageState.previewUrl || null;
            imageState.message = "Uploading image…";
            imageState.error = false;
            row.dataset.imageUploading = "true";
            delete row.dataset.imageNeedsUpload;
            renderPending();
            renderImage();

            const payload = new FormData();
            payload.append("file", file, file.name);
            const token = activeForm?.querySelector?.('input[name="__RequestVerificationToken"]');
            const organization = activeForm?.querySelector?.('input[name="OrganizationId"]');
            const formCode = activeForm?.querySelector?.('input[name="FormCode"]');
            if (token) payload.append("__RequestVerificationToken", token.value);
            if (organization) payload.append("organizationId", organization.value);
            if (formCode) payload.append("formCode", formCode.value);
            imageState.uploadPromise = fetch(imageFile.dataset.uploadUrl, { method: "POST", body: payload })
                .then(async (response) => {
                    if (requestVersion !== imageState.requestVersion) return false;
                    let result = null;
                    try { result = await response.json(); } catch { /* use generic response error */ }
                    if (requestVersion !== imageState.requestVersion) return false;
                    const assetId = Number(result?.assetId);
                    if (!response.ok || !Number.isInteger(assetId) || assetId <= 0) throw new Error(result?.error || "The image could not be uploaded.");
                    const safeFileName = result.fileName || file.name;
                    const previewUrl = result.previewUrl || imageState.previewUrl;
                    if (result.previewUrl) revokeObjectUrl();
                    if (imageState.fallback?.objectUrl && globalThis.URL?.revokeObjectURL) globalThis.URL.revokeObjectURL(imageState.fallback.objectUrl);
                    setUploadPending(assetId, safeFileName, previewUrl);
                    return true;
                })
                .catch((error) => {
                    if (requestVersion !== imageState.requestVersion) return false;
                    const previous = imageState.fallback;
                    const message = error.message || "The image could not be uploaded.";
                    if (!previous) {
                        restoreBaseline(message);
                        return false;
                    }
                    delete row.dataset.imageUploading;
                    delete row.dataset.imageNeedsUpload;
                    revokeObjectUrl();
                    imageState.mode = previous.mode;
                    imageState.editing = true;
                    if (binding) binding.value = previous.operation === "Upsert" ? previous.value : "";
                    if (operation) operation.value = previous.operation;
                    imageState.objectUrl = previous.objectUrl || null;
                    imageState.fileName = previous.fileName;
                    imageState.previewUrl = previous.previewUrl;
                    imageState.message = `The replacement could not be uploaded; ${previous.fileName || "the previous image change"} remains selected.`;
                    imageState.error = true;
                    imageState.fallback = null;
                    renderPending();
                    renderImage();
                    return false;
                })
                .finally(() => { if (requestVersion === imageState.requestVersion) imageState.uploadPromise = null; });
            return imageState.uploadPromise;
        }

        chooseAnother?.addEventListener("click", () => { imageFile?.click?.(); });
        change?.addEventListener("click", changeImage);
        row._applyRevert = revertImage;
        revert?.addEventListener("click", (event) => openRevertDialog(row, event.currentTarget));
        imageFile?.addEventListener("change", () => {
            const selected = imageFile.files?.[0];
            imageFile.value = "";
            if (selected) uploadImage(selected);
        });
        row._discardPendingChange = (options = {}) => restoreBaseline("", options);
        renderPending();
        renderImage();
    }

    function initializeRow(row, settingsForm) {
        if (typeof row.addEventListener === "function") row.addEventListener("toggle", () => handleSettingRowToggle(row));
        if (row.dataset?.valueType === "image") initializeImageRow(row, settingsForm);
        else initializeStandardRow(row, settingsForm);
    }

    document.querySelectorAll(".setting-row").forEach((row) => initializeRow(row, form));

    function imageUploadRows(settingsForm = form) {
        return [...(settingsForm?.querySelectorAll?.('.setting-row[data-image-uploading="true"]') || [])];
    }

    function hasImageUpload(settingsForm = form) {
        return imageUploadRows(settingsForm).length > 0 || Boolean(settingsForm?.querySelector?.('.setting-row[data-image-needs-upload="true"]'));
    }

    function browserWorkRows(settingsForm = form) {
        return new Set([
            ...(settingsForm?.querySelectorAll?.('.setting-row[data-dirty="true"]') || []),
            ...(settingsForm?.querySelectorAll?.('.setting-row[data-editing="true"]') || [])
        ]);
    }

    function blockActiveEdit(settingsForm, status = statusRegion) {
        if (!hasImageUpload(settingsForm)) {
            syncBlockingStatus(settingsForm, status);
            return false;
        }
        syncBlockingStatus(settingsForm, status);
        const row = imageUploadRows(settingsForm)[0] || settingsForm?.querySelector?.('.setting-row[data-image-needs-upload="true"]');
        row?.setAttribute?.("open", "");
        row?.closest?.(".setting-category, .dynamic-settings")?.setAttribute?.("open", "");
        (row?.querySelector?.(".image-choose-another") || row?.querySelector?.(".image-upload-trigger"))?.focus?.();
        return true;
    }

    function reviewLiveSummary(row) {
        if (row?.dataset?.liveSummary) return row.dataset.liveSummary;
        return row?.dataset?.sensitive === "true" && row?.dataset?.liveState !== "notset"
            ? "Configured"
            : "Not configured";
    }

    function reviewProposedSummary(row, value, operation) {
        if (operation === "RemoveOverride") {
            if (row?.dataset?.hasInherited === "true") {
                const source = row.dataset.inheritedSource || "the inherited scope";
                const inherited = row.dataset.sensitive === "true" ? "" : row.dataset.inheritedSummary;
                return inherited ? `Use ${inherited} from ${source}` : `Use inherited value from ${source}`;
            }
            return "Remove customization; no inherited value configured";
        }
        if (row?.dataset?.valueType === "image") {
            const fileName = controls(row, ".image-pending-file-name")?.textContent?.trim();
            if (row.dataset.imageNeedsUpload === "true") return "Choose an image to customize here";
            if (row.dataset.imageUploading === "true") return fileName ? `${fileName} (uploading)` : "Image upload in progress";
            return fileName || "Uploaded image";
        }
        const summary = safeBrowserSummary(row, value, operation);
        return row?.dataset?.baselineMode === "inherit" && row?.dataset?.sensitive !== "true" ? `Customize here: ${summary}` : summary;
    }

    function appendReviewRow(tbody, row, value, operation) {
        const tr = document.createElement("tr");
        const heading = document.createElement("th");
        heading.scope = "row";
        heading.textContent = row.dataset.displayName || "Setting";
        const live = document.createElement("td");
        live.className = "review-baseline-column";
        live.textContent = reviewLiveSummary(row);
        const proposed = document.createElement("td");
        proposed.className = "review-pending-column";
        proposed.textContent = reviewProposedSummary(row, value, operation);
        tr.append(heading, live, proposed);
        tbody.append(tr);
    }

    function populateReviewTable(settingsForm, tableOrBody, options = {}) {
        const tbody = tableOrBody?.tagName?.toLowerCase() === "tbody" ? tableOrBody : tableOrBody?.querySelector?.("tbody") || tableOrBody;
        tbody?.replaceChildren?.();
        let valid = true;
        settingsForm?.querySelectorAll?.('.setting-row[data-dirty="true"]')?.forEach((row) => {
            const state = proposedState(row);
            if (row.dataset.valueType === "image" && state.operation === "Upsert" && (!Number.isInteger(Number(state.value)) || Number(state.value) <= 0)) {
                if (options.validateImages !== false) {
                    valid = false;
                    return;
                }
            }
            appendReviewRow(tbody, row, state.value, state.operation);
        });
        return valid;
    }

    function setReviewDialogMode(mode, count, trigger) {
        if (!reviewDialog) return;
        const reviewOnly = mode === "review";
        reviewDialog.dataset.reviewMode = reviewOnly ? "browser" : "save";
        reviewDialog._trigger = trigger;
        const title = reviewDialog.querySelector("#save-confirm-title");
        const confirm = reviewDialog.querySelector("#confirm-save");
        const close = reviewDialog.querySelector("#cancel-save");
        const saveContext = reviewDialog.querySelector("[data-save-review-context]");
        const browserContext = reviewDialog.querySelector("[data-browser-review-context]");
        const proposedHeading = reviewDialog.querySelector(".review-pending-column[scope='col']");
        const caption = reviewDialog.querySelector("caption");
        if (title) title.textContent = reviewOnly
            ? `Review ${count} ${count === 1 ? "change" : "changes"}`
            : trigger?.dataset?.reviewTitle || "Review changes";
        if (confirm) {
            confirm.hidden = reviewOnly;
            if (!reviewOnly) confirm.textContent = trigger?.dataset?.confirmLabel || "Save changes";
        }
        if (close) close.textContent = reviewOnly ? "Close" : "Cancel";
        if (saveContext) saveContext.hidden = reviewOnly;
        if (browserContext) browserContext.hidden = !reviewOnly;
        if (proposedHeading) proposedHeading.textContent = "Proposed";
        if (caption) caption.textContent = reviewOnly ? "Browser-pending setting changes" : "Pending setting changes";
    }

    function reviewPendingChanges(event) {
        const table = reviewDialog?.querySelector(".review-table");
        if (!table) return false;
        populateReviewTable(form, table, { validateImages: false });
        const count = table.querySelector?.("tbody")?.children?.length || 0;
        if (!count) return false;
        reviewSubmitter = null;
        setReviewDialogMode("review", count, event?.currentTarget || document.querySelector("[data-review-pending]"));
        return showModal(reviewDialog, "#cancel-save");
    }

    function ensureSettingVisible(row, options = {}) {
        if (!row?.open || row.hidden) return false;
        const category = row.closest?.(".setting-category, .dynamic-settings");
        if (category?.hidden) return false;
        const rect = row.getBoundingClientRect?.();
        const viewportHeight = globalThis.innerHeight || document.documentElement?.clientHeight || 0;
        if (!rect || !Number.isFinite(rect.top) || !Number.isFinite(rect.bottom) || !viewportHeight) return false;
        const margin = Number.isFinite(options.margin) ? options.margin : 32;
        const topMargin = Number.isFinite(options.topMargin) ? options.topMargin : margin;
        const bottomMargin = Number.isFinite(options.bottomMargin) ? options.bottomMargin : margin;
        const availableHeight = viewportHeight - topMargin - bottomMargin;
        const drawerHeight = rect.bottom - rect.top;
        let scrollDelta = 0;
        if (drawerHeight > availableHeight) {
            if (rect.top !== topMargin) scrollDelta = rect.top - topMargin;
        } else {
            const bottomOverflow = rect.bottom + bottomMargin - viewportHeight;
            if (bottomOverflow > 0) scrollDelta = bottomOverflow;
            else if (rect.top < topMargin) scrollDelta = rect.top - topMargin;
        }
        if (!scrollDelta) return false;
        const behavior = options.behavior || (globalThis.matchMedia?.("(prefers-reduced-motion: reduce)")?.matches ? "auto" : "smooth");
        globalThis.scrollBy?.({ top: scrollDelta, left: 0, behavior });
        return true;
    }

    function scrollOpenedSettingIntoView(row) {
        if (!row?.open) return;
        const schedule = globalThis.requestAnimationFrame || ((callback) => callback());
        schedule(() => { if (row.open) ensureSettingVisible(row); });
    }

    function restoreOpenSettingRows(openSettingKeys) {
        const openKeys = new Set(openSettingKeys || []);
        const restoredRows = new Set();
        document.querySelectorAll(".setting-row").forEach((row) => {
            if (!openKeys.has(row.dataset?.settingKey) || typeof row.open !== "boolean") return;
            restoredRows.add(row);
            if (!row.open) { restoredSettingRows.add(row); row.open = true; }
            const category = row.closest?.(".setting-category, .dynamic-settings");
            category?.setAttribute?.("open", "");
            if (category) category.open = true;
        });
        return restoredRows;
    }

    function restoreLastOpenedSetting(lastOpenedSettingKey, restoredRows) {
        if (typeof lastOpenedSettingKey !== "string") return;
        const schedule = globalThis.requestAnimationFrame || ((callback) => callback());
        schedule(() => {
            const row = [...(document.querySelectorAll(".setting-row") || [])].find((candidate) => restoredRows.has(candidate) && candidate.dataset?.settingKey === lastOpenedSettingKey);
            if (row?.open && !row.hidden && !row.closest?.(".setting-category, .dynamic-settings")?.hidden) ensureSettingVisible(row, { behavior: "auto" });
        });
    }

    const categories = [...document.querySelectorAll(".setting-category, .dynamic-settings")];
    statusFilter = document.querySelector("#setting-status-filter");
    const searchRegion = document.querySelector(".settings-search");
    let preFilterDisclosure = null;

    function persistFilterState() {
        writeUiState({ search: search ? String(search.value ?? "") : "", status: statusFilter ? String(statusFilter.value || "all") : "all" });
    }

    function rowMatchesStatus(row, status) {
        switch (status) {
            case "customized": return row.dataset.liveState === "customized";
            case "inherited": return row.dataset.presentationState === "inherited" || row.dataset.liveState === "inherited";
            case "notset": return row.dataset.presentationState === "notset" || row.dataset.liveState === "notset";
            case "draft": return row.dataset.draftChange === "true";
            case "unsaved": return row.dataset.dirty === "true";
            default: return true;
        }
    }

    function applyFilters() {
        const query = search?.value.trim().toLowerCase() || "";
        const selectedStatus = statusFilter?.value || "all";
        const filtering = Boolean(query) || selectedStatus !== "all";
        if (filtering && preFilterDisclosure === null) preFilterDisclosure = new Map(categories.map((category) => [category, category.open]));
        let visible = 0;
        document.querySelectorAll(".setting-row").forEach((row) => {
            const matches = (row.dataset.search || "").includes(query) && rowMatchesStatus(row, selectedStatus);
            row.hidden = !matches;
            if (matches) visible++;
        });
        categories.forEach((category) => {
            const rows = [...(category.querySelectorAll?.(".setting-row") || [])];
            const shown = rows.filter((row) => !row.hidden);
            const count = category.querySelector?.("summary span");
            if (count) count.textContent = `(${filtering ? shown.length : rows.length})`;
            category.hidden = filtering && shown.length === 0;
            if (filtering) category.open = shown.length > 0;
        });
        if (!filtering && preFilterDisclosure !== null) {
            preFilterDisclosure.forEach((wasOpen, category) => { category.open = wasOpen; category.hidden = false; });
            preFilterDisclosure = null;
        }
        if (searchStatus) {
            const noun = visible === 1 ? "setting" : "settings";
            const empty = visible === 0 && filtering;
            searchStatus.textContent = empty
                ? query ? "No settings match the current search and status filter." : "No settings match the current status filter."
                : `${visible} ${noun} ${filtering ? "match the current search and status filter" : "shown"}.`;
            searchStatus.classList?.toggle?.("settings-filter-empty", empty);
        }
        return visible;
    }

    function restoreUiState() {
        const state = readUiState();
        if (search && typeof state.search === "string") search.value = state.search;
        if (statusFilter && typeof state.status === "string" && [...statusFilter.options].some((option) => option.value === state.status)) statusFilter.value = state.status;
        applyFilters();
        const restoredRows = restoreOpenSettingRows(state.openSettingKeys);
        restoreLastOpenedSetting(state.lastOpenedSettingKey, restoredRows);
        return state;
    }

    search?.addEventListener("input", () => { persistFilterState(); applyFilters(); });
    statusFilter?.addEventListener("change", () => { persistFilterState(); applyFilters(); });
    function reviewDraftChanges() {
        if (!statusFilter) return;
        statusFilter.value = "draft";
        persistFilterState();
        const visible = applyFilters();
        const behavior = globalThis.matchMedia?.("(prefers-reduced-motion: reduce)")?.matches ? "auto" : "smooth";
        searchRegion?.scrollIntoView?.({ behavior, block: "start" });
        const firstRow = visible ? document.querySelector('.setting-row[data-draft-change="true"]:not([hidden])') : null;
        (firstRow?.querySelector?.("summary") || statusFilter || search)?.focus?.();
    }
    document.querySelector("[data-review-draft]")?.addEventListener("click", reviewDraftChanges);
    restoreUiState();

    const dirtyCount = () => form?.querySelectorAll?.('.setting-row[data-dirty="true"]')?.length || 0;
    function restoreContextControl(trigger) {
        if (trigger?.dataset?.committedValue !== undefined) trigger.value = trigger.dataset.committedValue;
    }
    function unsavedMessage(count) {
        return `You have ${count} ${count === 1 ? "change" : "changes"} that ${count === 1 ? "has" : "have"} not been saved.`;
    }
    function setUnsavedDialogMode(mode, count = 0) {
        const explicit = mode === "explicit-discard";
        const title = unsavedDialog?.querySelector("#unsaved-title");
        const message = unsavedDialog?.querySelector("[data-unsaved-message]");
        const explanation = unsavedDialog?.querySelector("[data-unsaved-explanation]");
        const confirm = unsavedDialog?.querySelector("[data-guard-discard]");
        if (title) title.textContent = explicit ? "Discard unsaved changes?" : "Unsaved changes";
        if (message) message.textContent = explicit ? "This will discard changes made in this browser." : unsavedMessage(count);
        if (explanation) explanation.textContent = explicit
            ? "Changes already saved to the shared draft or live settings will not be affected."
            : "Continuing will revert changes made in this browser. Changes already saved to the shared draft or live settings will not be affected.";
        if (confirm) confirm.textContent = explicit ? `Discard ${count} browser ${count === 1 ? "change" : "changes"}` : "Discard changes and continue";
    }

    function disableDirtyMutations() {
        // Values are copied into these named hidden inputs. Keep only the rows
        // that are actually dirty in the request; draft state is untouched.
        form?.querySelectorAll?.('.setting-row:not([data-dirty="true"]) .change-index, .setting-row:not([data-dirty="true"]) .change-key, .setting-row:not([data-dirty="true"]) .operation, .setting-row:not([data-dirty="true"]) .setting-value-binding')
            .forEach((control) => { control.disabled = true; });
    }

    function discardPendingChanges(settingsForm = form, options = {}) {
        const rows = [...browserWorkRows(settingsForm)];
        const dirtyCountBeforeDiscard = rows.filter((row) => row.dataset?.dirty === "true").length;
        const failures = [];
        rows.forEach((row) => {
            try {
                if (typeof row._discardPendingChange !== "function") throw new Error("This setting does not support browser discard.");
                row._discardPendingChange({ updateActions: false });
            } catch (error) {
                failures.push({ row, error });
            }
        });
        updatePendingActions(settingsForm);
        syncBlockingStatus(settingsForm);
        const remainingDirtyRows = [...browserWorkRows(settingsForm)];
        const result = { discardedCount: dirtyCountBeforeDiscard, failures, remainingDirtyRows };
        if (options.announce) announceDiscardResult(result);
        return result;
    }

    function announceDiscardResult(result) {
        if (!statusRegion) return;
        const remaining = result.remainingDirtyRows;
        if (!remaining.length) {
            statusRegion.textContent = `Discarded ${result.discardedCount} browser ${result.discardedCount === 1 ? "change" : "changes"}.`;
        } else {
            const names = remaining.map((row) => row.dataset?.displayName || row.dataset?.settingKey || "setting");
            const noun = remaining.length === 1 ? "change" : "changes";
            statusRegion.textContent = `${remaining.length} browser ${noun} could not be discarded: ${names.join(", ")}. Reload the page before continuing.`;
        }
        statusRegion.hidden = false;
        statusRegion.focus?.();
    }

    function needsLiveConfirmation(targetForm, submitter) {
        if (targetForm?.dataset?.requiresLiveConfirm?.toLowerCase() === "true") return true;
        return targetForm?.matches?.("[data-preview-form]") && submitter?.name === "AllowLiveSubmission" && submitter.value === "true";
    }

    function finalSubmit(action) {
        pendingAction = null;
        submitting = true;
        action.prepare?.();
        if (approvedInvalidHandler) action.form.removeEventListener?.("invalid", approvedInvalidHandler, true);
        approvedInvalidHandler = () => {
            clearApprovedForm(action.form);
            submitting = false;
        };
        action.form.addEventListener?.("invalid", approvedInvalidHandler, true);
        approvedForm = action.form;
        action.form.requestSubmit(action.submitter);
    }

    function clearApprovedForm(targetForm) {
        if (approvedForm !== targetForm) return;
        approvedForm = null;
        if (approvedInvalidHandler) targetForm.removeEventListener?.("invalid", approvedInvalidHandler, true);
        approvedInvalidHandler = null;
    }

    function showModal(owner, focusSelector) {
        if (!owner?.showModal) return false;
        owner.showModal();
        owner.querySelector?.(focusSelector)?.focus?.();
        return true;
    }

    function continuePipeline(action, skipDirty = false, skipLive = false) {
        if (hasImageUpload()) {
            restoreContextControl(action.trigger);
            blockActiveEdit(form, statusRegion);
            return "uploading";
        }
        const count = dirtyCount();
        if (!skipDirty && count) {
            pendingAction = action;
            setUnsavedDialogMode("guard", count);
            if (unsavedDialog) unsavedDialog._trigger = action.trigger;
            if (showModal(unsavedDialog, '[data-dialog-cancel]')) return "dirty";
        }
        if (!skipLive && needsLiveConfirmation(action.form, action.submitter)) {
            pendingAction = action;
            if (livePreviewDialog) livePreviewDialog._trigger = action.trigger;
            if (showModal(livePreviewDialog, '[data-confirm-live-preview]')) return "live";
        }
        finalSubmit(action);
        return "submitted";
    }

    function lifecycleSubmit(event) {
        const targetForm = event.currentTarget;
        if (approvedForm === targetForm) { clearApprovedForm(targetForm); return; }
        event.preventDefault();
        continuePipeline({ form: targetForm, submitter: event.submitter, trigger: event.submitter, prepare: targetForm === form ? disableDirtyMutations : undefined });
    }

    form?.addEventListener("submit", (event) => {
        if (approvedForm === form) { clearApprovedForm(form); return; }
        const submitter = event.submitter;
        if (submitter?.dataset?.submitKind === "guarded") { lifecycleSubmit(event); return; }
        if (submitter?.dataset?.submitKind === "draft") {
            event.preventDefault();
            if (blockActiveEdit(form, statusRegion)) return;
            if (!dirtyCount()) {
                if (statusRegion) { statusRegion.textContent = "No settings have changed."; statusRegion.hidden = false; }
                return;
            }
            finalSubmit({ form, submitter, trigger: submitter, prepare: disableDirtyMutations });
            return;
        }
        event.preventDefault();
        if (blockActiveEdit(form, statusRegion)) return;
        const table = reviewDialog?.querySelector(".review-table");
        if (!table || !populateReviewTable(form, table)) {
            if (statusRegion) { statusRegion.textContent = "Choose a valid uploaded image before saving."; statusRegion.hidden = false; statusRegion.focus?.(); }
            return;
        }
        if (!table.querySelector?.("tbody")?.children?.length) {
            if (statusRegion) { statusRegion.textContent = "No settings have changed."; statusRegion.hidden = false; }
            return;
        }
        reviewSubmitter = submitter;
        setReviewDialogMode("save", table.querySelector("tbody").children.length, submitter);
        showModal(reviewDialog, "#confirm-save");
    });

    document.querySelector("[data-review-pending]")?.addEventListener("click", reviewPendingChanges);

    document.querySelector("#confirm-save")?.addEventListener("click", () => {
        if (reviewDialog?.dataset?.reviewMode !== "save") return;
        const action = { form, submitter: reviewSubmitter, trigger: reviewSubmitter, prepare: disableDirtyMutations };
        reviewDialog.close();
        finalSubmit(action);
    });
    document.querySelector("#cancel-save")?.addEventListener("click", () => cancelWorkflowDialog(reviewDialog));

    navigationGuard = (action, trigger) => {
        if (hasImageUpload()) {
            restoreContextControl(trigger);
            blockActiveEdit(form, statusRegion);
            return false;
        }
        if (!dirtyCount()) { submitting = true; action(); return true; }
        pendingAction = { navigate: action, trigger };
        if (unsavedDialog) unsavedDialog._trigger = trigger;
        setUnsavedDialogMode("guard", dirtyCount());
        if (!showModal(unsavedDialog, '[data-dialog-cancel]')) {
            const result = discardPendingChanges(form);
            if (result.remainingDirtyRows.length) {
                pendingAction = null;
                announceDiscardResult(result);
                return false;
            }
            submitting = true;
            action();
            return true;
        }
        return false;
    };
    document.querySelectorAll("form[data-guard-action]").forEach((guardedForm) => guardedForm.addEventListener("submit", lifecycleSubmit));
    document.querySelectorAll(".settings-navigation a").forEach((link) => link.addEventListener("click", (event) => {
        if (submitting) return;
        event.preventDefault();
        navigationGuard(() => { globalThis.location.href = link.href; }, link);
    }));
    document.querySelectorAll("[data-open-dialog]").forEach((button) => button.addEventListener("click", () => navigationGuard(() => {
        submitting = false;
        const target = document.getElementById(button.dataset.openDialog);
        if (!target) return;
        target._trigger = button;
        showModal(target, "button:not([disabled])");
    }, button)));

    function cancelWorkflowDialog(owner) {
        if (!owner) return;
        if (owner.open) owner.close();
        restoreContextControl(owner._trigger);
        owner._trigger?.focus?.();
        pendingAction = null;
        submitting = false;
        if (owner === reviewDialog) {
            reviewSubmitter = null;
            reviewDialog.dataset.reviewMode = "save";
        }
    }
    function bindDialogCancellation(owner) {
        owner.addEventListener("cancel", (event) => {
            event.preventDefault();
            if (owner === revertDialog) closeRevertDialog(false);
            else cancelWorkflowDialog(owner);
        });
    }
    document.querySelectorAll("dialog").forEach(bindDialogCancellation);
    document.querySelectorAll("[data-dialog-cancel]").forEach((button) => button.addEventListener("click", () => cancelWorkflowDialog(button.closest("dialog"))));
    document.querySelector("[data-revert-keep]")?.addEventListener("click", () => closeRevertDialog(false));
    document.querySelector("[data-revert-affirm]")?.addEventListener("click", () => closeRevertDialog(true));
    document.querySelector("[data-guard-discard]")?.addEventListener("click", () => {
        unsavedDialog.close();
        const action = pendingAction;
        const result = discardPendingChanges(form);
        if (result.remainingDirtyRows.length) {
            pendingAction = null;
            submitting = false;
            announceDiscardResult(result);
            return;
        }
        if (action?.explicitDiscard) {
            pendingAction = null;
            submitting = false;
            announceDiscardResult(result);
            action.focusTarget?.querySelector?.("summary")?.focus?.() || search?.focus?.();
        } else if (action?.navigate) { submitting = true; action.navigate(); }
        else if (action) continuePipeline(action, true);
    });
    document.querySelector("[data-confirm-live-preview]")?.addEventListener("click", () => {
        livePreviewDialog.close();
        if (pendingAction) continuePipeline(pendingAction, true, true);
    });
    document.querySelector("[data-discard-pending]")?.addEventListener("click", (event) => {
        const rows = [...browserWorkRows()];
        const count = dirtyCount();
        if (!count) return;
        pendingAction = { explicitDiscard: true, focusTarget: rows[0] };
        unsavedDialog._trigger = event.currentTarget;
        setUnsavedDialogMode("explicit-discard", count);
        showModal(unsavedDialog, '[data-dialog-cancel]');
    });
    window.addEventListener?.("beforeunload", (event) => {
        if (!submitting && (dirtyCount() || hasImageUpload())) { event.preventDefault(); event.returnValue = ""; }
    });

    async function copyPreviewUrl(clipboard, value, status) {
        try { await clipboard.writeText(value); status.textContent = "Preview URL copied."; return true; }
        catch { status.textContent = "Copy failed. Select the URL and copy it manually."; return false; }
    }

    globalThis.SettingsEditor = {
        initializeSettingsContext, setNavigationGuard: (guard) => { navigationGuard = guard; }, initializeRow,
        initializeStandardRow, initializeImageRow, updatePendingActions, hasImageUpload, blockActiveEdit,
        populateReviewTable, reviewPendingChanges, compactSourcePreview
    };
    globalThis.SettingsWorkflow = {
        continuePipeline, lifecycleSubmit, needsLiveConfirmation, disableDirtyMutations, discardPendingChanges,
        syncBlockingStatus, hasImageUpload, restoreContextControl, applyFilters, reviewDraftChanges, copyPreviewUrl,
        unsavedMessage, setUnsavedDialogMode, cancelWorkflowDialog, bindDialogCancellation, readUiState, writeUiState,
        captureOpenSettingKeys, restoreUiState, ensureSettingVisible,
        workflowState: () => ({ pending: pendingAction, submitting, approved: approvedForm === form }),
        setWorkflowState: (state) => { pendingAction = state.pending; submitting = state.submitting; approvedForm = state.approved ? form : null; }
    };

    const copyButton = document.querySelector("[data-copy-preview-url]");
    copyButton?.addEventListener("click", () => copyPreviewUrl(globalThis.navigator?.clipboard, document.querySelector("#preview-url")?.value, document.querySelector("[data-copy-status]")));
    document.querySelector("#preview-url")?.addEventListener("focus", (event) => event.currentTarget.select());
})();
