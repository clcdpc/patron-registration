(() => {
    const search = document.querySelector("#setting-search");
    const searchStatus = document.querySelector("#search-status");
    const form = document.querySelector("#settings-form");
    const reviewDialog = document.querySelector("#save-confirm");
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

    function selectedModeControl(row) {
        const modes = controlsAll(row, ".setting-mode");
        return modes.find((mode) => mode.checked) || modes.find((mode) => mode.getAttribute?.("aria-checked") === "true") || null;
    }

    function modeFromRow(row) {
        const selected = selectedModeControl(row);
        if (selected) return selected.dataset?.mode || (selected.value === "inherit" ? "inherit" : "customize");
        return row?.dataset?.baselineMode || "customize";
    }

    function normalizeValue(row, value) {
        const text = value === null || value === undefined ? "" : String(value);
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

    function proposedState(row) {
        const modeControl = selectedModeControl(row);
        const mode = modeControl?.dataset?.mode || (modeControl?.value === "inherit" ? "inherit" : modeFromRow(row));
        if (mode === "inherit") return { mode: "inherit", operation: "RemoveOverride", value: "" };
        const customValue = modeControl?.dataset?.customValue;
        const valueEditor = controls(row, ".setting-value:not(.setting-value-binding)") || controls(row, ".setting-value");
        const value = customValue !== undefined
            ? customValue
            : controls(row, "[data-ip-prefix-editor]") ? ipPrefixValue(row) : valueEditor?.value ?? controls(row, ".setting-value-binding")?.value ?? "";
        return { mode: "customize", operation: "Upsert", value: normalizeValue(row, value) };
    }

    function baselineState(row) {
        const mode = row?.dataset?.baselineMode || (row?.dataset?.baselineOperation === "RemoveOverride" ? "inherit" : "customize");
        return { mode, operation: mode === "inherit" ? "RemoveOverride" : "Upsert", value: normalizeValue(row, row?.dataset?.baselineValue || "") };
    }

    function sameState(row, proposed, baseline = baselineState(row)) {
        if (proposed.mode !== baseline.mode) return false;
        return proposed.mode === "inherit" || normalizeValue(row, proposed.value) === normalizeValue(row, baseline.value);
    }

    function setSelectedMode(row, desiredMode, desiredValue = null) {
        const modes = controlsAll(row, ".setting-mode");
        modes.forEach((mode) => {
            const modeName = mode.dataset?.mode || (mode.value === "inherit" ? "inherit" : "customize");
            const isSelected = modeName === desiredMode && (desiredMode !== "customize" ||
                desiredValue === null || mode.dataset?.customValue === undefined || normalizeValue(row, mode.dataset.customValue) === normalizeValue(row, desiredValue));
            mode.checked = isSelected;
        });
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

    function safeBrowserSummary(row, value, operation) {
        if (operation === "RemoveOverride") return "Use inherited value";
        if (row?.dataset?.sensitive === "true") return "Replacement entered";
        const valueType = row?.dataset?.valueType;
        if (valueType === "boolean") return String(value).toLowerCase() === "true" ? "Yes" : "No";
        if (valueType === "enumeration" && String(value).toLowerCase() === "barcode") return "Barcode";
        if (valueType === "enumeration" && String(value).toLowerCase() === "magstripe") return "Magnetic stripe";
        if (valueType === "html") return "HTML configured";
        if (valueType === "emailtemplate") return "Email template configured";
        if (valueType === "longstring") {
            const normalized = String(value ?? "").replace(/\s+/g, " ").trim();
            if (!normalized) return "Blank";
            return normalized.length <= 120 ? normalized : `${normalized.slice(0, 120).trimEnd()}…`;
        }
        return String(value ?? "").trim() || "Blank";
    }

    function renderBrowserPendingSummary(row, state) {
        const summary = controls(row, ".summary-value");
        const status = controls(row, ".setting-status > span") || controls(row, ".setting-status");
        const batchStatus = controls(row, ".batch-browser-status");
        const pendingValue = safeBrowserSummary(row, state.value, state.operation);
        const text = `Unsaved: ${pendingValue}`;
        if (summary) {
            summary.textContent = text;
            summary.setAttribute?.("title", text);
        }
        if (batchStatus) batchStatus.textContent = text;
        else if (status) status.textContent = "Unsaved in this browser";
        row.dataset.browserState = "unsaved";
    }

    function updateEditorAvailability(row, state) {
        const inherited = state.mode === "inherit";
        controlsAll(row, ".value-editor .setting-value:not(.setting-value-binding), .batch-label-input .setting-value, .ip-prefix-input").forEach((control) => { control.disabled = inherited; });
        controlsAll(row, ".ip-prefix-add, .ip-prefix-remove").forEach((control) => { control.disabled = inherited; });
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

    function updateStandardRow(row, settingsForm = form) {
        const state = proposedState(row);
        const baseline = baselineState(row);
        const dirty = !sameState(row, state, baseline);
        row.dataset.dirty = dirty.toString();
        setBindingEnabled(row, dirty, state);
        updateEditorAvailability(row, state);
        if (dirty) {
            renderBrowserPendingSummary(row, state);
        } else {
            const clean = row._settingsCleanPresentation;
            const summary = controls(row, ".summary-value");
            const status = controls(row, ".setting-status > span") || controls(row, ".setting-status");
            const batchStatus = controls(row, ".batch-browser-status");
            if (summary && clean) {
                summary.textContent = clean.summary;
                if (clean.title === null || clean.title === undefined) summary.removeAttribute?.("title");
                else summary.setAttribute?.("title", clean.title);
            }
            if (status && clean) status.textContent = clean.status;
            if (batchStatus) batchStatus.textContent = "";
            delete row.dataset.browserState;
        }
        updatePendingActions(settingsForm);
        return dirty;
    }

    function syncBlockingStatus(settingsForm = form, status = statusRegion) {
        if (!status) return;
        if (hasImageUpload(settingsForm)) {
            status.textContent = settingsForm?.querySelector?.('.setting-row[data-image-needs-upload="true"]')
                ? imageUploadRequiredMessage
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
        row._settingsCleanPresentation = {
            summary: summary?.textContent || "",
            title: summary?.getAttribute?.("title"),
            status: status?.textContent || ""
        };

        controlsAll(row, ".setting-mode").forEach((mode) => mode.addEventListener("change", () => updateStandardRow(row, settingsForm)));
        const value = controls(row, ".setting-value:not(.setting-value-binding)") || controls(row, ".setting-value");
        value?.addEventListener("input", () => updateStandardRow(row, settingsForm));
        value?.addEventListener("change", () => updateStandardRow(row, settingsForm));
        controlsAll(row, ".ip-prefix-input").forEach((input) => {
            input.addEventListener("input", () => updateStandardRow(row, settingsForm));
            input.addEventListener("change", () => updateStandardRow(row, settingsForm));
        });
        const prefixEditor = controls(row, "[data-ip-prefix-editor]");
        const addPrefix = controls(row, ".ip-prefix-add");
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
            remove.addEventListener("click", () => { wrapper.remove(); updateStandardRow(row, settingsForm); });
            return wrapper;
        };
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

        row._discardPendingChange = () => {
            const baseline = baselineState(row);
            setSelectedMode(row, baseline.mode, baseline.value);
            const valueEditor = controls(row, ".setting-value:not(.setting-value-binding)") || controls(row, ".setting-value");
            if (valueEditor && baseline.mode === "customize" && !controls(row, "[data-ip-prefix-editor]")) valueEditor.value = baseline.value;
            if (controls(row, "[data-ip-prefix-editor]")) {
                const values = baseline.value.split(";").filter(Boolean);
                const desiredValues = values.length ? values : [""];
                controlsAll(row, ".ip-prefix-row").forEach((prefixRow) => prefixRow.remove());
                if (prefixEditor && addPrefix) {
                    desiredValues.forEach((prefix) => prefixEditor.insertBefore(createPrefixRow(prefix), addPrefix));
                }
            }
            updateStandardRow(row, settingsForm);
        };
        updateStandardRow(row, settingsForm);
    }

    function initializeImageRow(row, settingsForm) {
        const activeForm = settingsForm || form;
        const uploadTrigger = controls(row, ".image-upload-trigger");
        const chooseAnother = controls(row, ".image-choose-another");
        const undo = controls(row, ".image-undo-pending");
        const inheritedMode = controls(row, ".image-mode-inherit");
        const customizeMode = controls(row, ".image-mode-customize");
        const imageFile = controls(row, ".image-file");
        const pending = controls(row, ".image-pending") || controls(row, ".image-browser-pending");
        const pendingPreview = controls(pending, ".image-pending-preview") || controls(pending, "img");
        const pendingFileName = controls(pending, ".image-pending-file-name");
        const uploadStatus = controls(pending, ".image-upload-status");
        const operation = controls(row, ".operation");
        const binding = controls(row, ".setting-value-binding") || controls(row, ".setting-value");
        const summary = controls(row, ".summary-value");
        const rowStatus = controls(row, ".setting-status > span") || controls(row, ".setting-status");
        const baseline = baselineState(row);
        const clean = { summary: summary?.textContent || "", title: summary?.getAttribute?.("title"), status: rowStatus?.textContent || "" };
        const imageState = {
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
            const mode = inheritedMode?.checked ? "inherit" : "customize";
            const operationName = mode === "inherit" ? "RemoveOverride" : "Upsert";
            return { mode, operation: operationName, value: operationName === "Upsert" ? String(binding?.value || "") : "" };
        }

        function renderImage() {
            const state = currentImageState();
            const dirty = !sameState(row, state, baseline) ||
                Boolean(row.dataset.imageNeedsUpload === "true") ||
                Boolean(row.dataset.imageUploading === "true");
            row.dataset.dirty = dirty.toString();
            if (operation) operation.value = state.operation;
            if (binding && state.operation === "Upsert") binding.value = state.value;
            setBindingEnabled(row, dirty, state);
            if (row.dataset.imageNeedsUpload === "true" && uploadStatus) {
                uploadStatus.textContent = "Upload an image to customize this scope.";
            }
            if (dirty) {
                const pendingText = state.operation === "RemoveOverride"
                    ? (row.dataset.imageHasInherited === "true" ? "Use inherited image" : "Remove image")
                    : imageState.fileName || "new image";
                if (summary) { summary.textContent = `Unsaved: ${pendingText}`; summary.setAttribute?.("title", summary.textContent); }
                if (rowStatus) rowStatus.textContent = "Unsaved in this browser";
                row.dataset.browserState = "unsaved";
            } else {
                if (summary) { summary.textContent = clean.summary; clean.title === null ? summary.removeAttribute?.("title") : summary.setAttribute?.("title", clean.title); }
                if (rowStatus) rowStatus.textContent = clean.status;
                delete row.dataset.browserState;
            }
            updatePendingActions(activeForm);
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
            if (chooseAnother) chooseAnother.hidden = inheritedMode?.checked === true;
            if (undo) undo.hidden = !row.dataset.dirty || row.dataset.dirty !== "true";
        }

        function restoreBaseline(message = "") {
            imageState.requestVersion++;
            imageState.uploadPromise = null;
            discardFallback();
            revokeObjectUrl();
            delete row.dataset.imageUploading;
            delete row.dataset.imageNeedsUpload;
            if (imageFile) imageFile.value = "";
            setSelectedMode(row, baseline.mode, baseline.value);
            if (binding) binding.value = baseline.value;
            if (operation) operation.value = baseline.operation;
            imageState.fileName = "";
            imageState.previewUrl = "";
            imageState.message = message;
            imageState.error = Boolean(message);
            renderPending();
            renderImage();
        }

        function chooseInherited() {
            imageState.requestVersion++;
            imageState.uploadPromise = null;
            discardFallback();
            revokeObjectUrl();
            delete row.dataset.imageUploading;
            delete row.dataset.imageNeedsUpload;
            if (inheritedMode) inheritedMode.checked = true;
            imageState.fileName = row.dataset.imageInheritedFileName || "";
            imageState.previewUrl = row.dataset.imageInheritedPreviewUrl || "";
            imageState.message = row.dataset.imageInheritedMissing === "true"
                ? "The inherited uploaded image is missing. Saving will use the inherited image setting."
                : row.dataset.imageHasInherited === "true" ? "Use inherited image." : "No image will be configured.";
            imageState.error = false;
            renderPending();
            renderImage();
        }

        function chooseCustomize() {
            if (customizeMode) customizeMode.checked = true;
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
                if (binding) binding.value = baseline.value;
                imageState.message = "";
                delete row.dataset.imageNeedsUpload;
            }
            imageState.error = false;
            renderPending();
            renderImage();
        }

        function setUploadPending(assetId, fileName, previewUrl) {
            delete row.dataset.imageNeedsUpload;
            delete row.dataset.imageUploading;
            imageState.fallback = null;
            if (customizeMode) customizeMode.checked = true;
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
                    setSelectedMode(row, previous.mode, previous.value);
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

        uploadTrigger?.addEventListener("click", () => { imageFile?.focus?.(); imageFile?.click?.(); });
        chooseAnother?.addEventListener("click", () => { imageFile?.focus?.(); imageFile?.click?.(); });
        undo?.addEventListener("click", () => restoreBaseline());
        inheritedMode?.addEventListener("change", chooseInherited);
        customizeMode?.addEventListener("change", chooseCustomize);
        imageFile?.addEventListener("change", () => {
            const selected = imageFile.files?.[0];
            imageFile.value = "";
            if (selected) uploadImage(selected);
        });
        row._discardPendingChange = () => restoreBaseline();
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
        return new Set(settingsForm?.querySelectorAll?.('.setting-row[data-dirty="true"]') || []);
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
        (row?.querySelector?.(".image-undo-pending") || row?.querySelector?.(".image-upload-trigger"))?.focus?.();
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
            return controls(row, ".image-pending-file-name")?.textContent?.trim() || "Uploaded image";
        }
        return safeBrowserSummary(row, value, operation);
    }

    function appendReviewRow(tbody, row, value, operation) {
        const tr = document.createElement("tr");
        const heading = document.createElement("th");
        heading.scope = "row";
        heading.textContent = row.dataset.displayName || "Setting";
        const live = document.createElement("td");
        live.textContent = reviewLiveSummary(row);
        const proposed = document.createElement("td");
        proposed.textContent = reviewProposedSummary(row, value, operation);
        tr.append(heading, live, proposed);
        tbody.append(tr);
    }

    function populateReviewTable(settingsForm, tableOrBody) {
        const tbody = tableOrBody?.tagName?.toLowerCase() === "tbody" ? tableOrBody : tableOrBody?.querySelector?.("tbody") || tableOrBody;
        tbody?.replaceChildren?.();
        let valid = true;
        settingsForm?.querySelectorAll?.('.setting-row[data-dirty="true"]')?.forEach((row) => {
            const state = proposedState(row);
            if (row.dataset.valueType === "image" && state.operation === "Upsert" && (!Number.isInteger(Number(state.value)) || Number(state.value) <= 0)) {
                valid = false;
                return;
            }
            appendReviewRow(tbody, row, state.value, state.operation);
        });
        return valid;
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
            case "customized": return row.dataset.customizedHere === "true";
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

    document.querySelectorAll(".html-preview").forEach((frame) => {
        const source = frame.previousElementSibling;
        if (!source) return;
        const render = () => { frame.srcdoc = source.value; };
        source.addEventListener("input", render);
        render();
    });
    document.querySelectorAll(".plain-text-preview").forEach((preview) => {
        const source = preview.previousElementSibling;
        if (!source) return;
        const render = () => { preview.textContent = source.value; };
        source.addEventListener("input", render);
        render();
    });

    document.querySelectorAll(".reveal-secret").forEach((button) => button.addEventListener("click", () => {
        const input = document.getElementById(button.getAttribute("aria-controls"));
        if (!input) return;
        const revealing = input.type === "password";
        input.type = revealing ? "text" : "password";
        button.setAttribute("aria-expanded", revealing.toString());
        button.textContent = revealing ? "Hide secret" : "Reveal secret";
        button.setAttribute("aria-label", `${revealing ? "Hide" : "Reveal"} ${button.closest?.(".setting-row")?.dataset?.displayName || "secret"}`);
    }));

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

    function discardPendingChanges(settingsForm = form) {
        browserWorkRows(settingsForm).forEach((row) => row._discardPendingChange?.());
        updatePendingActions(settingsForm);
        syncBlockingStatus(settingsForm);
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
        reviewSubmitter = submitter;
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
        const title = reviewDialog.querySelector("#save-confirm-title");
        const confirm = reviewDialog.querySelector("#confirm-save");
        if (title) title.textContent = submitter?.dataset?.reviewTitle || "Review changes";
        if (confirm) confirm.textContent = submitter?.dataset?.confirmLabel || "Save changes";
        reviewDialog._trigger = submitter;
        showModal(reviewDialog, "#confirm-save");
    });

    document.querySelector("#confirm-save")?.addEventListener("click", () => {
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
        if (!showModal(unsavedDialog, '[data-dialog-cancel]')) { discardPendingChanges(); submitting = true; action(); return true; }
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
        if (owner === reviewDialog) reviewSubmitter = null;
    }
    function bindDialogCancellation(owner) {
        owner.addEventListener("cancel", (event) => { event.preventDefault(); cancelWorkflowDialog(owner); });
    }
    document.querySelectorAll("dialog").forEach(bindDialogCancellation);
    document.querySelectorAll("[data-dialog-cancel]").forEach((button) => button.addEventListener("click", () => cancelWorkflowDialog(button.closest("dialog"))));
    document.querySelector("[data-guard-discard]")?.addEventListener("click", () => {
        unsavedDialog.close();
        const action = pendingAction;
        discardPendingChanges();
        if (action?.explicitDiscard) {
            pendingAction = null;
            submitting = false;
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
        if (!rows.length) return;
        pendingAction = { explicitDiscard: true, focusTarget: rows[0] };
        unsavedDialog._trigger = event.currentTarget;
        setUnsavedDialogMode("explicit-discard", rows.length);
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
        populateReviewTable
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
