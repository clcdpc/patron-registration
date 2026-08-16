(() => {
    const search = document.querySelector("#setting-search");
    const searchStatus = document.querySelector("#search-status");
    const form = document.querySelector("#settings-form");
    const dialog = document.querySelector("#save-confirm");
    const editStatus = document.querySelector("#edit-session-status");
    const candidateEditBlockedMessage = "Keep or cancel the active setting edit before saving.";
    const imageUploadBlockedMessage = "Wait for the image upload to finish or undo the image change before continuing.";
    const settingsUiStateStorageKey = "patron-registration.settings-admin.ui-state";
    const restoredSettingRows = new Set();
    let approved = false;
    let submitter = null;

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
            if (typeof parsed.customizedOnly === "boolean") state.customizedOnly = parsed.customizedOnly;
            if (typeof parsed.draftOnly === "boolean") state.draftOnly = parsed.draftOnly;
            if (typeof parsed.lastOpenedSettingKey === "string") state.lastOpenedSettingKey = parsed.lastOpenedSettingKey;
            if (Array.isArray(parsed.openSettingKeys)) {
                state.openSettingKeys = [...new Set(parsed.openSettingKeys.filter((key) => typeof key === "string"))];
            }
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
            if ("lastOpenedSettingKey" in state && typeof state.lastOpenedSettingKey !== "string") {
                delete state.lastOpenedSettingKey;
            }
            storage.setItem(settingsUiStateStorageKey, JSON.stringify(state));
        } catch {
            // Session storage is optional; settings administration remains usable without it.
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
        persistOpenSettingState();
        if (restoredSettingRows.has(row)) {
            restoredSettingRows.delete(row);
            if (row.open) return;
        }
        if (row.open) {
            const settingKey = row.dataset?.settingKey;
            if (typeof settingKey === "string") writeUiState({ lastOpenedSettingKey: settingKey });
            scrollOpenedSettingIntoView(row);
        }
    }

    let navigationGuard = null;
    function setNavigationGuard(guard) { navigationGuard = guard; }
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

    function updatePendingActions(settingsForm) {
        if (!settingsForm) return;
        const actions = settingsForm.querySelector(".settings-actions");
        if (!actions) return;
        const count = settingsForm.querySelectorAll('.setting-row[data-dirty="true"]').length;
        const status = actions.querySelector(".pending-changes-status");
        actions.hidden = count === 0;
        if (status) status.textContent = count === 0 ? "" : `${count} ${count === 1 ? "change" : "changes"} unsaved in this browser`;
        actions.querySelectorAll?.("[data-label-template]")?.forEach((button) => {
            button.textContent = button.dataset.labelTemplate.replace("{count}", count).replace("{noun}", count === 1 ? "change" : "changes");
        });
    }

    function safeBrowserSummary(row, value, operation) {
        if (operation === "RemoveOverride") return "Will use inherited value";
        if (row.dataset.sensitive === "true") return "New value entered";
        const valueType = row.dataset.valueType;
        if (valueType === "boolean") return String(value).toLowerCase() === "true" ? "Yes" : "No";
        if (valueType === "html") return "HTML configured";
        if (valueType === "emailtemplate") return "Email template configured";
        if (valueType === "longstring") {
            const normalized = String(value ?? "").replace(/\s+/g, " ").trim();
            if (!normalized) return "Blank";
            return normalized.length <= 120 ? normalized : `${normalized.slice(0, 120).trimEnd()}…`;
        }
        return String(value ?? "").trim() || "Blank";
    }

    function renderBrowserPendingSummary(row, value, operation) {
        const summary = row.querySelector?.(".summary-value");
        const status = row.querySelector?.(".setting-status");
        const pendingValue = safeBrowserSummary(row, value, operation);
        const text = operation === "RemoveOverride" ? pendingValue : `Unsaved: ${pendingValue}`;
        if (summary) {
            summary.textContent = text;
            summary.setAttribute?.("title", text);
        }
        if (status) status.textContent = "Unsaved in this browser";
        row.dataset.browserState = "unsaved";
    }

    function clearEditSessionStatus(settingsForm = form, status = editStatus) {
        if (!status) return;
        const hasCandidate = Boolean(settingsForm?.querySelector?.('.setting-row[data-candidate-operation]'));
        if (hasCandidate) {
            status.textContent = candidateEditBlockedMessage;
            status.dataset.statusKind = "blocking";
            status.hidden = false;
            return "candidate";
        }
        if (hasImageUpload(settingsForm)) {
            status.textContent = imageUploadBlockedMessage;
            status.dataset.statusKind = "blocking";
            status.hidden = false;
            return "upload";
        }
        const isBlockingStatus = status.dataset?.statusKind === "blocking"
            || status.textContent === candidateEditBlockedMessage
            || status.textContent === imageUploadBlockedMessage;
        if (!isBlockingStatus) return;
        delete status.dataset.statusKind;
        status.hidden = true;
        status.textContent = "";
    }

    function initializeImageRow(row, settingsForm) {
        const activeForm = settingsForm || form;
        const uploadTrigger = row.querySelector(".image-upload-trigger");
        const chooseAnother = row.querySelector(".image-choose-another");
        const undo = row.querySelector(".image-undo-pending");
        const inherit = row.querySelector(".image-inherit-action");
        const imageFile = row.querySelector(".image-file");
        const pending = row.querySelector(".image-pending") || row.querySelector(".image-browser-pending");
        const pendingPreview = pending?.querySelector(".image-pending-preview") || pending?.querySelector("img");
        const pendingFileName = pending?.querySelector(".image-pending-file-name");
        const uploadStatus = pending?.querySelector(".image-upload-status");
        const operation = row.querySelector(".operation");
        const value = row.querySelector(".setting-value");
        const summaryValue = row.querySelector(".summary-value");
        const settingStatus = row.querySelector(".setting-status");
        const changeIndex = row.querySelector(".change-index");
        const changeKey = row.querySelector(".change-key");
        const serverState = {
            operation: operation?.value || "Upsert",
            value: value?.value || "",
            dirty: row.dataset.dirty === "true",
            appliedOperation: row.dataset.appliedOperation || "Upsert",
            indexDisabled: changeIndex?.disabled ?? true,
            keyDisabled: changeKey?.disabled ?? true,
            operationDisabled: operation?.disabled ?? true,
            valueDisabled: value?.disabled ?? true,
            summaryValue: summaryValue?.textContent || "",
            summaryTitle: summaryValue?.getAttribute?.("title"),
            status: settingStatus?.textContent || "",
            inheritHidden: inherit?.hidden ?? true
        };
        const imageState = {
            pendingOperation: null,
            assetId: null,
            fileName: "",
            previewUrl: "",
            status: "",
            error: false,
            objectUrl: null,
            uploadPromise: null,
            uploadFallback: null,
            requestVersion: 0
        };

        function setBindingEnabled(enabled, selectedOperation) {
            [changeIndex, changeKey, operation].filter(Boolean).forEach((control) => { control.disabled = !enabled; });
            if (value) value.disabled = !enabled || selectedOperation === "RemoveOverride";
        }

        function revokeObjectUrl() {
            if (imageState.objectUrl && globalThis.URL?.revokeObjectURL) globalThis.URL.revokeObjectURL(imageState.objectUrl);
            imageState.objectUrl = null;
        }

        function pendingSnapshot() {
            if (!imageState.pendingOperation) return null;
            return {
                operation: imageState.pendingOperation,
                assetId: imageState.assetId,
                fileName: imageState.fileName,
                previewUrl: imageState.previewUrl,
                status: imageState.status,
                error: imageState.error
            };
        }

        function setPendingState(snapshot) {
            revokeObjectUrl();
            imageState.pendingOperation = snapshot?.operation || null;
            imageState.assetId = snapshot?.assetId || null;
            imageState.fileName = snapshot?.fileName || "";
            imageState.previewUrl = snapshot?.previewUrl || "";
            imageState.status = snapshot?.status || "";
            imageState.error = snapshot?.error || false;
        }

        function renderPending() {
            if (!pending) return;
            const visible = Boolean(imageState.pendingOperation || imageState.status);
            pending.hidden = !visible;
            const isRemoval = imageState.pendingOperation === "RemoveOverride";
            const heading = pending.querySelector(".image-pending-heading");
            if (heading) heading.textContent = isRemoval ? "Unsaved image change" : "Unsaved replacement";
            if (pendingPreview) {
                pendingPreview.hidden = !imageState.previewUrl || isRemoval && !imageState.previewUrl;
                if (imageState.previewUrl) pendingPreview.src = imageState.previewUrl;
                else pendingPreview.removeAttribute?.("src");
            }
            if (pendingFileName) pendingFileName.textContent = imageState.fileName;
            if (uploadStatus) {
                uploadStatus.textContent = imageState.status || "";
                uploadStatus.classList?.toggle?.("image-upload-error", imageState.error);
            }
            if (chooseAnother) chooseAnother.hidden = isRemoval;
            if (undo) {
                undo.hidden = !imageState.pendingOperation;
                undo.textContent = isRemoval ? "Undo image change" : "Undo replacement";
            }
        }

        function updateSummary() {
            if (!summaryValue) return;
            if (imageState.pendingOperation === "Upsert") {
                summaryValue.textContent = `Unsaved: ${imageState.fileName || "new image"}`;
                summaryValue.setAttribute?.("title", summaryValue.textContent);
                if (settingStatus) settingStatus.textContent = "Unsaved in this browser";
                row.dataset.browserState = "unsaved";
            } else if (imageState.pendingOperation === "RemoveOverride") {
                const summary = row.dataset.imageHasInherited === "true" ? "Unsaved: use inherited image" : "Unsaved: remove image";
                summaryValue.textContent = summary;
                summaryValue.setAttribute?.("title", summary);
                if (settingStatus) settingStatus.textContent = "Unsaved in this browser";
                row.dataset.browserState = "unsaved";
            } else {
                summaryValue.textContent = serverState.summaryValue;
                if (serverState.summaryTitle === null) summaryValue.removeAttribute?.("title");
                else summaryValue.setAttribute?.("title", serverState.summaryTitle);
                if (settingStatus) settingStatus.textContent = serverState.status;
                delete row.dataset.browserState;
            }
        }

        function updateImagePresentation() {
            renderPending();
            updateSummary();
        }

        function cancelUpload() {
            imageState.requestVersion++;
            imageState.uploadPromise = null;
            imageState.uploadFallback = null;
            delete row.dataset.imageUploading;
            revokeObjectUrl();
        }

        function restoreServerState(errorMessage = "") {
            cancelUpload();
            if (imageFile) imageFile.value = "";
            if (operation) operation.value = serverState.operation;
            if (value) value.value = serverState.value;
            row.dataset.dirty = serverState.dirty.toString();
            row.dataset.appliedOperation = serverState.appliedOperation;
            if (changeIndex) changeIndex.disabled = serverState.indexDisabled;
            if (changeKey) changeKey.disabled = serverState.keyDisabled;
            if (operation) operation.disabled = serverState.operationDisabled;
            if (value) value.disabled = serverState.valueDisabled;
            if (inherit) inherit.hidden = serverState.inheritHidden;
            delete row.dataset.imagePendingFileName;
            delete row.dataset.imagePendingAction;
            setPendingState(null);
            imageState.status = errorMessage;
            imageState.error = Boolean(errorMessage);
            updateImagePresentation();
            updatePendingActions(activeForm);
            clearEditSessionStatus(activeForm);
        }

        function markUpsert(assetId, fileName, previewUrl) {
            imageState.pendingOperation = "Upsert";
            imageState.assetId = assetId;
            imageState.fileName = fileName;
            imageState.previewUrl = previewUrl;
            imageState.error = false;
            imageState.status = `${fileName} is ready to save.`;
            if (value) value.value = String(assetId);
            if (operation) operation.value = "Upsert";
            row.dataset.appliedOperation = "Upsert";
            row.dataset.dirty = "true";
            row.dataset.imagePendingFileName = fileName;
            row.dataset.imagePendingAction = "Upsert";
            setBindingEnabled(true, "Upsert");
            if (inherit) inherit.hidden = false;
            updateImagePresentation();
            updatePendingActions(activeForm);
        }

        async function uploadImage(file) {
            if (!imageFile?.dataset.uploadUrl || !file) return false;
            const currentPending = imageState.pendingOperation === "RemoveOverride" || imageState.assetId
                ? pendingSnapshot()
                : null;
            const fallback = imageState.uploadFallback || currentPending;
            cancelUpload();
            imageState.uploadFallback = fallback;
            imageState.pendingOperation = "Upsert";
            imageState.assetId = fallback?.assetId || null;
            imageState.fileName = file.name;
            imageState.previewUrl = globalThis.URL?.createObjectURL?.(file) || "";
            imageState.status = "Uploading image…";
            imageState.error = false;
            const requestVersion = ++imageState.requestVersion;
            row.dataset.imageUploading = "true";
            updateImagePresentation();
            clearEditSessionStatus(activeForm);

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
                    if (!response.ok || !Number.isInteger(assetId) || assetId <= 0) {
                        throw new Error(result?.error || "The image could not be uploaded.");
                    }
                    const safeFileName = result.fileName || file.name;
                    const previewUrl = result.previewUrl || imageState.previewUrl;
                    if (result.previewUrl) revokeObjectUrl();
                    imageState.uploadFallback = null;
                    delete row.dataset.imageUploading;
                    markUpsert(assetId, safeFileName, previewUrl);
                    clearEditSessionStatus(activeForm);
                    return true;
                })
                .catch((error) => {
                    if (requestVersion !== imageState.requestVersion) return false;
                    const previous = imageState.uploadFallback;
                    const message = error.message || "The image could not be uploaded.";
                    if (previous?.operation) {
                        setPendingState(previous);
                        imageState.status = previous.operation === "RemoveOverride"
                            ? "The replacement could not be uploaded; the pending image change remains selected."
                            : `The replacement could not be uploaded; ${previous.fileName} remains selected.`;
                        imageState.error = true;
                        if (value && previous.assetId) value.value = String(previous.assetId);
                        if (operation) operation.value = previous.operation;
                        row.dataset.imagePendingAction = previous.operation;
                        row.dataset.dirty = "true";
                        row.dataset.appliedOperation = previous.operation;
                        if (previous.operation === "Upsert") row.dataset.imagePendingFileName = previous.fileName;
                        else delete row.dataset.imagePendingFileName;
                        setBindingEnabled(true, previous.operation);
                    } else {
                        restoreServerState(message);
                        return false;
                    }
                    delete row.dataset.imageUploading;
                    imageState.uploadFallback = null;
                    updateImagePresentation();
                    updatePendingActions(activeForm);
                    clearEditSessionStatus(activeForm);
                    return false;
                })
                .finally(() => {
                    if (requestVersion === imageState.requestVersion) imageState.uploadPromise = null;
                });
            return imageState.uploadPromise;
        }

        function openFileChooser() {
            imageFile?.focus?.();
            imageFile?.click?.();
        }

        function selectInheritedOrRemove() {
            cancelUpload();
            const hasInherited = row.dataset.imageHasInherited === "true";
            const inheritedMissing = row.dataset.imageInheritedMissing === "true";
            const previewUrl = inheritedMissing ? "" : row.dataset.imageInheritedPreviewUrl || "";
            const fileName = inheritedMissing ? "" : row.dataset.imageInheritedFileName || (hasInherited ? "Inherited image" : "");
            imageState.pendingOperation = "RemoveOverride";
            imageState.assetId = null;
            imageState.fileName = fileName;
            imageState.previewUrl = previewUrl;
            imageState.status = inheritedMissing
                ? "The inherited uploaded image is missing. Saving this change will use the inherited image setting."
                : hasInherited ? "Use inherited image." : "No image will be configured.";
            imageState.error = false;
            if (operation) operation.value = "RemoveOverride";
            row.dataset.appliedOperation = "RemoveOverride";
            row.dataset.dirty = "true";
            row.dataset.imagePendingAction = "RemoveOverride";
            delete row.dataset.imagePendingFileName;
            setBindingEnabled(true, "RemoveOverride");
            updateImagePresentation();
            updatePendingActions(activeForm);
            clearEditSessionStatus(activeForm);
        }

        function undoImageChange() {
            restoreServerState();
        }

        row._discardPendingChange = undoImageChange;
        uploadTrigger?.addEventListener("click", openFileChooser);
        chooseAnother?.addEventListener("click", openFileChooser);
        undo?.addEventListener("click", undoImageChange);
        inherit?.addEventListener("click", selectInheritedOrRemove);
        imageFile?.addEventListener("change", () => {
            const selected = imageFile.files?.[0];
            imageFile.value = "";
            if (selected) uploadImage(selected);
        });
        updateImagePresentation();
    }

    function initializeStandardRow(row, settingsForm) {
        const change = row.querySelector(".edit-setting");
        const inherit = row.querySelector(".inherit-setting");
        const apply = row.querySelector(".apply-setting");
        const cancel = row.querySelector(".cancel-setting");
        const actions = row.querySelector(".edit-actions");
        const editor = row.querySelector(".value-editor");
        const message = row.querySelector(".inheritance-message");
        const operation = row.querySelector(".operation");
        const value = row.querySelector(".setting-value");
        let session = null;
        const summaryValue = row.querySelector(".summary-value");
        const settingStatus = row.querySelector(".setting-status");
        const reveal = row.querySelector(".reveal-secret");
        const serverState = {
            operation: operation.value,
            value: value.value,
            dirty: row.dataset.dirty === "true",
            appliedOperation: row.dataset.appliedOperation,
            indexDisabled: row.querySelector(".change-index").disabled,
            keyDisabled: row.querySelector(".change-key").disabled,
            operationDisabled: operation.disabled,
            valueDisabled: value.disabled,
            summaryValue: summaryValue?.textContent,
            summaryTitle: summaryValue?.getAttribute?.("title"),
            status: settingStatus?.textContent,
            inheritHidden: inherit?.hidden ?? true,
            inputType: value.type,
            revealText: reveal?.textContent,
            revealExpanded: reveal?.getAttribute?.("aria-expanded"),
            revealLabel: reveal?.getAttribute?.("aria-label")
        };

        function setBindingEnabled(enabled, selectedOperation) {
            row.querySelectorAll(".change-index, .change-key, .operation")
                .forEach((control) => { control.disabled = !enabled; });
            value.disabled = !enabled || selectedOperation === "RemoveOverride";
        }

        function showNormalState() {
            change.hidden = false;
            if (inherit) {
                inherit.hidden = row.dataset.appliedOperation === "RemoveOverride";
                inherit.setAttribute?.("aria-expanded", "false");
            }
            actions.hidden = true;
            editor.hidden = true;
            message.hidden = true;
        }

        function beginEdit(candidateOperation) {
            session = {
                operation: operation.value,
                value: value.value,
                dirty: row.dataset.dirty === "true",
                indexDisabled: row.querySelector(".change-index").disabled,
                keyDisabled: row.querySelector(".change-key").disabled,
                operationDisabled: operation.disabled,
                valueDisabled: value.disabled
            };
            row.dataset.candidateOperation = candidateOperation;
            setBindingEnabled(false, candidateOperation);
            value.disabled = candidateOperation === "RemoveOverride";
            change.hidden = true;
            if (inherit) {
                inherit.hidden = true;
                inherit.setAttribute?.("aria-expanded", "true");
            }
            actions.hidden = false;
            editor.hidden = candidateOperation === "RemoveOverride";
            message.hidden = candidateOperation !== "RemoveOverride";
            row.setAttribute("open", "");
            row.closest(".setting-category, .dynamic-settings")?.setAttribute("open", "");
            if (candidateOperation === "Upsert") {
                value.focus();
            } else {
                apply.focus();
            }
            clearEditSessionStatus(settingsForm);
        }

        async function applyEdit() {
            const candidateOperation = row.dataset.candidateOperation;
            if (!session || !candidateOperation) return;
            if (candidateOperation === "Upsert" && !value.reportValidity()) return;
            operation.value = candidateOperation;
            row.dataset.appliedOperation = candidateOperation;
            row.dataset.dirty = "true";
            setBindingEnabled(true, candidateOperation);
            renderBrowserPendingSummary(row, value.value, candidateOperation);
            delete row.dataset.candidateOperation;
            session = null;
            showNormalState();
            clearEditSessionStatus(settingsForm);
            updatePendingActions(settingsForm);
            change.focus();
        }

        function cancelEdit() {
            if (!session) return;
            const restoreDirty = session.dirty;
            operation.value = session.operation;
            value.value = session.value;
            if (value.nextElementSibling?.classList.contains("html-preview")) {
                value.dispatchEvent(new Event("input"));
            }
            row.dataset.dirty = session.dirty.toString();
            row.querySelector(".change-index").disabled = session.indexDisabled;
            row.querySelector(".change-key").disabled = session.keyDisabled;
            operation.disabled = session.operationDisabled;
            value.disabled = session.valueDisabled;
            delete row.dataset.candidateOperation;
            session = null;
            showNormalState();
            clearEditSessionStatus(settingsForm);
            updatePendingActions(settingsForm);
            change.focus();
        }

        row._discardPendingChange = () => {
            operation.value = serverState.operation;
            value.value = serverState.value;
            row.dataset.dirty = "false";
            row.dataset.appliedOperation = serverState.appliedOperation;
            delete row.dataset.candidateOperation;
            row.querySelector(".change-index").disabled = serverState.indexDisabled;
            row.querySelector(".change-key").disabled = serverState.keyDisabled;
            operation.disabled = serverState.operationDisabled;
            value.disabled = serverState.valueDisabled;
            if (summaryValue) {
                summaryValue.textContent = serverState.summaryValue;
                if (serverState.summaryTitle === null) summaryValue.removeAttribute?.("title");
                else summaryValue.setAttribute?.("title", serverState.summaryTitle);
            }
            if (settingStatus) settingStatus.textContent = serverState.status;
            delete row.dataset.browserState;
            if (inherit) inherit.hidden = serverState.inheritHidden;
            if (serverState.inputType) value.type = serverState.inputType;
            if (reveal) {
                reveal.textContent = serverState.revealText;
                reveal.setAttribute("aria-expanded", serverState.revealExpanded ?? "false");
                reveal.setAttribute("aria-label", serverState.revealLabel ?? `Reveal ${row.dataset.displayName}`);
            }
            delete row.dataset.browserState;
            session = null;
            showNormalState();
            value.dispatchEvent?.(new Event("input"));
        };

        change?.addEventListener("click", () => beginEdit("Upsert"));
        inherit?.addEventListener("click", () => beginEdit("RemoveOverride"));
        apply?.addEventListener("click", applyEdit);
        cancel?.addEventListener("click", cancelEdit);
        showNormalState();
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
            // A drawer taller than the viewport cannot fit. Keep its important top portion usable
            // with one deterministic correction instead of chasing both edges.
            if (rect.top !== topMargin) {
                scrollDelta = rect.top - topMargin;
            }
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
        schedule(() => {
            if (!row.open) return;
            ensureSettingVisible(row);
        });
    }

    function initializeRow(row, settingsForm) {
        row.addEventListener?.("toggle", () => handleSettingRowToggle(row));
        if (row.dataset.valueType === "image") {
            initializeImageRow(row, settingsForm);
            return;
        }
        initializeStandardRow(row, settingsForm);
    }

    document.querySelectorAll(".setting-row").forEach((row) => initializeRow(row, form));
    updatePendingActions(form);

    function imageUploadRows(settingsForm = form) {
        return [...(settingsForm?.querySelectorAll?.('.setting-row[data-image-uploading="true"]') || [])];
    }
    function imageUploadRow(settingsForm = form) {
        return imageUploadRows(settingsForm)[0] || null;
    }
    function hasImageUpload(settingsForm = form) {
        return imageUploadRows(settingsForm).length > 0;
    }
    function browserWorkRows(settingsForm = form) {
        const rows = new Set([
            ...(settingsForm?.querySelectorAll?.('.setting-row[data-dirty="true"]') || []),
            ...(settingsForm?.querySelectorAll?.('.setting-row[data-candidate-operation]') || [])
        ]);
        imageUploadRows(settingsForm).forEach((row) => rows.add(row));
        return rows;
    }
    function blockActiveEdit(settingsForm, status) {
        const activeRow = settingsForm?.querySelector?.('.setting-row[data-candidate-operation]');
        const uploadingRow = hasImageUpload(settingsForm) ? imageUploadRow(settingsForm) : null;
        if (!activeRow && !uploadingRow) {
            clearEditSessionStatus(settingsForm, status);
            return false;
        }
        clearEditSessionStatus(settingsForm, status);
        if (activeRow) {
            activeRow.setAttribute("open", "");
            activeRow.closest(".setting-category, .dynamic-settings")?.setAttribute("open", "");
            status.focus();
            activeRow.querySelector(".apply-setting").focus();
            return true;
        }
        uploadingRow.setAttribute("open", "");
        const undo = uploadingRow.querySelector(".image-undo-pending");
        (undo || uploadingRow.querySelector(".image-upload-trigger"))?.focus();
        return true;
    }

    function reviewLiveSummary(row) {
        if (row.dataset.liveSummary) return row.dataset.liveSummary;
        if (row.dataset.sensitive === "true") return row.dataset.oldValue ? "configured" : "not configured";
        return row.dataset.oldValue || "not configured";
    }

    function reviewProposedSummary(row, value, operation) {
        if (operation === "RemoveOverride") {
            if (row.dataset.imageHasInherited === "true" || row.dataset.hasInherited === "true") {
                const source = row.dataset.inheritedSource || "the inherited scope";
                const inherited = row.dataset.sensitive === "true" ? "" : row.dataset.inheritedSummary;
                return inherited ? `use ${inherited} from ${source}` : `use inherited value from ${source}`;
            }
            return "remove customization; no inherited value configured";
        }
        return safeBrowserSummary(row, value, operation);
    }

    function populateReviewList(settingsForm, list) {
        list.replaceChildren();
        let valid = true;
        settingsForm.querySelectorAll('.setting-row[data-dirty="true"]').forEach((row) => {
            const value = row.querySelector(".setting-value");
            const operation = row.querySelector(".operation");
            if (row.dataset.valueType === "image") {
                const assetId = Number(value?.value);
                if (operation?.value === "Upsert" && (!Number.isInteger(assetId) || assetId <= 0)) {
                    valid = false;
                    return;
                }
                const item = document.createElement("li");
                const description = operation?.value === "RemoveOverride"
                    ? row.dataset.imageHasInherited === "true"
                        ? row.dataset.imageInheritedMissing === "true" ? "Use inherited image (image currently missing)" : "Use inherited image"
                        : "Remove image"
                    : `Replace with “${row.dataset.imagePendingFileName || "uploaded image"}”`;
                item.textContent = `${row.dataset.displayName}: Live: ${reviewLiveSummary(row)}; New: ${description}.`;
                list.append(item);
                return;
            }
            const item = document.createElement("li");
            item.textContent = `${row.dataset.displayName}: Live: ${reviewLiveSummary(row)}; New: ${reviewProposedSummary(row, value.value, operation.value)}.`;
            list.append(item);
        });
        return valid;
    }

    function handleSaveAttempt(event, settingsForm, status, reviewDialog, isApproved) {
        if (blockActiveEdit(settingsForm, status)) {
            event.preventDefault();
            return "blocked";
        }
        if (isApproved) return "approved";

        event.preventDefault();
        const list = reviewDialog.querySelector("ul");
        if (!populateReviewList(settingsForm, list)) {
            status.textContent = "Choose a valid uploaded image before saving.";
            status.dataset.statusKind = "validation";
            status.hidden = false;
            status.focus();
            return "invalid";
        }
        if (!list.children.length) {
            window.alert("No settings have changed.");
            return "empty";
        }
        const title = reviewDialog.querySelector("#save-confirm-title");
        const confirm = reviewDialog.querySelector("#confirm-save");
        if (title) title.textContent = event.submitter?.dataset.reviewTitle || "Review changes";
        if (confirm) confirm.textContent = event.submitter?.dataset.confirmLabel || "Save changes";
        reviewDialog.showModal();
        return "review";
    }

    globalThis.SettingsEditSessions = { initializeSettingsContext, setNavigationGuard, initializeRow, updatePendingActions, hasImageUpload, blockActiveEdit, populateReviewList, handleSaveAttempt };

    document.querySelectorAll(".reveal-secret").forEach((button) => {
        button.addEventListener("click", () => {
            const input = document.getElementById(button.getAttribute("aria-controls"));
            const revealing = input.type === "password";
            input.type = revealing ? "text" : "password";
            button.setAttribute("aria-expanded", revealing.toString());
            button.textContent = revealing ? "Hide secret" : "Reveal secret";
            button.setAttribute("aria-label", `${revealing ? "Hide" : "Reveal"} ${button.closest(".setting-row").dataset.displayName}`);
        });
    });

    const categories = [...document.querySelectorAll(".setting-category, .dynamic-settings")];
    const customizedOnly = document.querySelector("#customized-only-filter");
    const draftOnly = document.querySelector("#draft-only-filter");
    const searchRegion = document.querySelector(".settings-search");
    let preFilterDisclosure = null;

    function persistFilterState() {
        const changes = {};
        if (search) changes.search = String(search.value ?? "");
        if (customizedOnly) changes.customizedOnly = Boolean(customizedOnly.checked);
        if (draftOnly) changes.draftOnly = Boolean(draftOnly.checked);
        writeUiState(changes);
    }

    function applyFilters() {
        const query = search?.value.trim().toLowerCase() || "";
        const customized = Boolean(customizedOnly?.checked);
        const draft = Boolean(draftOnly?.checked);
        const filtering = Boolean(query) || customized || draft;
        if (filtering && preFilterDisclosure === null) {
            preFilterDisclosure = new Map(categories.map((category) => [category, category.open]));
        }
        let visible = 0;
        document.querySelectorAll(".setting-row").forEach((row) => {
            const matchesText = (row.dataset.search || "").includes(query);
            const matchesCustomized = !customized || row.dataset.customizedHere === "true";
            const matchesDraft = !draft || row.dataset.draftChange === "true";
            const matches = matchesText && matchesCustomized && matchesDraft;
            row.hidden = !matches;
            if (matches) visible += 1;
        });
        categories.forEach((category) => {
            const hasMatch = category.querySelector(".setting-row:not([hidden])") !== null;
            if (category.querySelectorAll) {
                const count = category.querySelector("summary span");
                const shown = category.querySelectorAll(".setting-row:not([hidden])").length;
                const total = category.querySelectorAll(".setting-row").length;
                if (count) count.textContent = `(${filtering ? shown : total})`;
            }
            category.hidden = filtering && !hasMatch;
            if (filtering) category.open = hasMatch;
        });
        if (!filtering && preFilterDisclosure !== null) {
            preFilterDisclosure.forEach((wasOpen, category) => { category.open = wasOpen; category.hidden = false; });
            preFilterDisclosure = null;
        }
        const emptyMessage = visible === 0 && filtering
            ? query ? "No settings match the current search and filters." : "No settings match the current filters."
            : "";
        let resultMessage = `${visible} settings shown.`;
        if (query && (customized || draft)) resultMessage = `${visible} settings match the current search and filters.`;
        else if (query) resultMessage = `${visible} settings match the current search.`;
        else if (customized && draft) resultMessage = `${visible} settings match the current filters.`;
        else if (customized) resultMessage = `${visible} customized ${visible === 1 ? "setting" : "settings"} shown.`;
        else if (draft) resultMessage = `${visible} shared draft ${visible === 1 ? "change" : "changes"} shown.`;
        if (searchStatus) {
            searchStatus.textContent = emptyMessage || resultMessage;
            searchStatus.classList?.toggle("settings-filter-empty", Boolean(emptyMessage));
        }
        return visible;
    }

    function restoreOpenSettingRows(openSettingKeys) {
        const openKeys = new Set(openSettingKeys || []);
        const restoredRows = new Set();
        document.querySelectorAll(".setting-row").forEach((row) => {
            if (!openKeys.has(row.dataset?.settingKey)) return;
            restoredRows.add(row);
            if (!row.open) {
                restoredSettingRows.add(row);
                row.open = true;
            }
            const category = row.closest?.(".setting-category, .dynamic-settings");
            if (category) {
                category.open = true;
                category.setAttribute?.("open", "");
            }
        });
        return restoredRows;
    }

    function restoreLastOpenedSetting(lastOpenedSettingKey, restoredRows) {
        if (typeof lastOpenedSettingKey !== "string") return;
        const schedule = globalThis.requestAnimationFrame || ((callback) => callback());
        schedule(() => {
            const row = [...(document.querySelectorAll(".setting-row") || [])]
                .find((candidate) => restoredRows.has(candidate) && candidate.dataset?.settingKey === lastOpenedSettingKey);
            if (!row?.open || row.hidden) return;
            const category = row.closest?.(".setting-category, .dynamic-settings");
            if (category?.hidden) return;
            ensureSettingVisible(row, { behavior: "auto" });
        });
    }

    function restoreUiState() {
        const state = readUiState();
        if (search && typeof state.search === "string") search.value = state.search;
        if (customizedOnly && typeof state.customizedOnly === "boolean") customizedOnly.checked = state.customizedOnly;
        if (draftOnly && typeof state.draftOnly === "boolean") draftOnly.checked = state.draftOnly;
        applyFilters();
        const restoredRows = restoreOpenSettingRows(state.openSettingKeys);
        restoreLastOpenedSetting(state.lastOpenedSettingKey, restoredRows);
        return state;
    }

    search?.addEventListener("input", () => { persistFilterState(); applyFilters(); });
    customizedOnly?.addEventListener("change", () => { persistFilterState(); applyFilters(); });
    draftOnly?.addEventListener("change", () => { persistFilterState(); applyFilters(); });
    function reviewDraftChanges() {
        if (!draftOnly) return;
        draftOnly.checked = true;
        persistFilterState();
        const visible = applyFilters();
        searchRegion?.scrollIntoView?.({ behavior: "smooth", block: "start" });
        const firstRow = visible ? document.querySelector('.setting-row[data-draft-change="true"]:not([hidden])') : null;
        const firstSummary = firstRow?.querySelector("summary");
        (firstSummary || draftOnly || search)?.focus?.();
    }
    document.querySelector("[data-review-draft]")?.addEventListener("click", reviewDraftChanges);
    restoreUiState();

    document.querySelectorAll(".html-preview").forEach((frame) => {
        const source = frame.previousElementSibling;
        const render = () => {
            frame.srcdoc = source.value;
        };
        source.addEventListener("input", render);
        render();
    });

    let reviewSubmitter = null;
    let submitting = false;
    let pending = null;
    const approvedForms = new WeakSet();
    const unsavedDialog = document.querySelector("#unsaved-changes-dialog");
    const liveDialog = document.querySelector("#live-preview-confirm");

    const dirtyCount = () => form?.querySelectorAll('.setting-row[data-dirty="true"]')?.length || 0;
    const hasCandidate = () => Boolean(form?.querySelector('.setting-row[data-candidate-operation]'));
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
        if (message) message.textContent = explicit
            ? "This will discard changes made in this browser."
            : unsavedMessage(count);
        if (explanation) explanation.textContent = explicit
            ? "Changes already saved to the shared draft or live settings will not be affected."
            : "Continuing will revert changes made in this browser. Changes already saved to the shared draft or live settings will not be affected.";
        if (confirm) confirm.textContent = explicit
            ? `Discard ${count} browser ${count === 1 ? "change" : "changes"}`
            : "Discard changes and continue";
    }
    function disableDirtyMutations() {
        form?.querySelectorAll('.setting-row[data-dirty="true"] .change-index, .setting-row[data-dirty="true"] .change-key, .setting-row[data-dirty="true"] .operation, .setting-row[data-dirty="true"] .setting-value')
            .forEach((control) => { control.disabled = true; });
    }
    function discardPendingChanges(settingsForm = form) {
        const rows = browserWorkRows(settingsForm);
        rows.forEach((row) => row._discardPendingChange?.());
        updatePendingActions(settingsForm);
        clearEditSessionStatus(settingsForm);
    }
    function needsLiveConfirmation(targetForm, submitter) {
        if (targetForm.dataset.requiresLiveConfirm?.toLowerCase() === "true") {
            return true;
        }
        if (!targetForm.matches?.("[data-preview-form]")) {
            return false;
        }
        return submitter?.name === "AllowLiveSubmission"
            && submitter.value === "true";
    }
    function finalSubmit(action) {
        pending = null;
        submitting = true;
        action.prepare?.();
        approvedForms.add(action.form);
        action.form.requestSubmit(action.submitter);
    }
    function continuePipeline(action, skipDirty = false, skipLive = false) {
        if (hasCandidate()) {
            restoreContextControl(action.trigger);
            blockActiveEdit(form, editStatus);
            return "candidate";
        }
        if (hasImageUpload()) {
            restoreContextControl(action.trigger);
            blockActiveEdit(form, editStatus);
            return "uploading";
        }
        const count = dirtyCount();
        if (!skipDirty && count) {
            pending = action;
            setUnsavedDialogMode("guard", count);
            unsavedDialog._trigger = action.trigger;
            unsavedDialog.showModal();
            unsavedDialog.querySelector("[data-dialog-cancel]").focus();
            return "dirty";
        }
        if (!skipLive && needsLiveConfirmation(action.form, action.submitter)) {
            pending = action;
            liveDialog._trigger = action.trigger;
            liveDialog.showModal();
            liveDialog.querySelector("[data-confirm-live-preview]").focus();
            return "live";
        }
        finalSubmit(action);
        return "submitted";
    }
    function lifecycleSubmit(event) {
        const targetForm = event.currentTarget;
        if (approvedForms.has(targetForm)) { approvedForms.delete(targetForm); return; }
        event.preventDefault();
        continuePipeline({
            form: targetForm,
            submitter: event.submitter,
            trigger: event.submitter,
            prepare: targetForm === form ? disableDirtyMutations : undefined
        });
    }

    form?.addEventListener("submit", (event) => {
        if (approvedForms.has(form)) { approvedForms.delete(form); return; }
        const kind = event.submitter?.dataset.submitKind;
        if (kind === "guarded") { lifecycleSubmit(event); return; }
        reviewSubmitter = event.submitter;
        if (blockActiveEdit(form, editStatus)) { event.preventDefault(); return; }
        if (approved) { submitting = true; return; }
        event.preventDefault();
        const list = dialog.querySelector("ul");
        if (!populateReviewList(form, list)) {
            editStatus.textContent = "Choose a valid uploaded image before saving.";
            editStatus.hidden = false;
            editStatus.focus();
            return;
        }
        if (!list.children.length) return;
        const title = dialog.querySelector("#save-confirm-title");
        const confirm = dialog.querySelector("#confirm-save");
        if (title) title.textContent = event.submitter?.dataset.reviewTitle || "Review changes";
        if (confirm) confirm.textContent = event.submitter?.dataset.confirmLabel || "Save changes";
        dialog._trigger = event.submitter;
        dialog.showModal();
        dialog.querySelector("#confirm-save")?.focus();
    });
    document.querySelector("#confirm-save")?.addEventListener("click", () => {
        approved = true;
        dialog.close();
        form.requestSubmit(reviewSubmitter);
    });
    document.querySelector("#cancel-save")?.addEventListener("click", () => cancelWorkflowDialog(dialog));

    navigationGuard = (action, trigger) => {
        if (hasCandidate()) {
            restoreContextControl(trigger);
            blockActiveEdit(form, editStatus);
            return false;
        }
        if (hasImageUpload()) {
            restoreContextControl(trigger);
            blockActiveEdit(form, editStatus);
            return false;
        }
        if (!dirtyCount()) { submitting = true; action(); return true; }
        pending = { navigate: action, trigger };
        unsavedDialog._trigger = trigger;
        setUnsavedDialogMode("guard", dirtyCount());
        unsavedDialog.showModal();
        unsavedDialog.querySelector("[data-dialog-cancel]").focus();
        return false;
    };
    document.querySelectorAll("form[data-guard-action]").forEach((guardedForm) => guardedForm.addEventListener("submit", lifecycleSubmit));
    document.querySelectorAll(".settings-navigation a").forEach((link) => link.addEventListener("click", (event) => {
        if (submitting) return;
        event.preventDefault();
        navigationGuard(() => { location.href = link.href; }, link);
    }));
    document.querySelectorAll("[data-open-dialog]").forEach((button) => button.addEventListener("click", () => {
        navigationGuard(() => {
            submitting = false;
            const target = document.getElementById(button.dataset.openDialog);
            target._trigger = button;
            target.showModal();
            target.querySelector("button:not([disabled])")?.focus();
        }, button);
    }));
    function cancelWorkflowDialog(owner) {
        if (!owner) return;
        if (owner.open) owner.close();
        restoreContextControl(owner._trigger);
        owner._trigger?.focus();
        pending = null;
        submitting = false;
        if (owner === dialog) {
            approved = false;
            reviewSubmitter = null;
        }
        owner._resetApproval?.();
    }
    function bindDialogCancellation(owner) {
        owner.addEventListener("cancel", (event) => {
            event.preventDefault();
            cancelWorkflowDialog(owner);
        });
    }
    document.querySelectorAll("dialog").forEach(bindDialogCancellation);
    document.querySelectorAll("[data-dialog-cancel]").forEach((button) => button.addEventListener("click", () => {
        cancelWorkflowDialog(button.closest("dialog"));
    }));
    document.querySelector("[data-guard-discard]")?.addEventListener("click", () => {
        unsavedDialog.close();
        const action = pending;
        discardPendingChanges();
        if (action?.explicitDiscard) {
            pending = null;
            submitting = false;
            const changeButton = action.focusTarget?.querySelector?.(".edit-setting");
            if (changeButton) changeButton.focus();
            else search?.focus?.();
        } else if (action?.navigate) { submitting = true; action.navigate(); }
        else if (action) continuePipeline(action, true);
    });
    document.querySelector("[data-confirm-live-preview]")?.addEventListener("click", () => {
        liveDialog.close();
        const action = pending;
        if (action) continuePipeline(action, true, true);
    });
    document.querySelector("[data-discard-pending]")?.addEventListener("click", (event) => {
        const uniqueRows = [...browserWorkRows()];
        if (!uniqueRows.length) return;
        pending = { explicitDiscard: true, focusTarget: uniqueRows[0] };
        unsavedDialog._trigger = event.currentTarget;
        setUnsavedDialogMode("explicit-discard", uniqueRows.length);
        unsavedDialog.showModal();
        unsavedDialog.querySelector("[data-dialog-cancel]").focus();
    });
    window.addEventListener?.("beforeunload", (event) => {
        if (!submitting && (dirtyCount() || hasCandidate() || hasImageUpload())) { event.preventDefault(); event.returnValue = ""; }
    });

    async function copyPreviewUrl(clipboard, value, status) {
        try {
            await clipboard.writeText(value);
            status.textContent = "Preview URL copied.";
            return true;
        } catch {
            status.textContent = "Copy failed. Select the URL and copy it manually.";
            return false;
        }
    }
    globalThis.SettingsWorkflow = {
        continuePipeline, lifecycleSubmit, needsLiveConfirmation, disableDirtyMutations, discardPendingChanges, clearEditSessionStatus, hasImageUpload,
        restoreContextControl, applyFilters, reviewDraftChanges, copyPreviewUrl, unsavedMessage, setUnsavedDialogMode, cancelWorkflowDialog, bindDialogCancellation,
        readUiState, writeUiState, captureOpenSettingKeys, restoreUiState, ensureSettingVisible,
        workflowState: () => ({ pending, submitting, approved }),
        setWorkflowState: (state) => { pending = state.pending; submitting = state.submitting; approved = state.approved; }
    };

    const copyButton = document.querySelector("[data-copy-preview-url]");
    copyButton?.addEventListener("click", () => copyPreviewUrl(
        navigator.clipboard, document.querySelector("#preview-url").value, document.querySelector("[data-copy-status]")));
    document.querySelector("#preview-url")?.addEventListener("focus", (event) => event.currentTarget.select());
})();
