(() => {
    const search = document.querySelector("#setting-search");
    const searchStatus = document.querySelector("#search-status");
    const form = document.querySelector("#settings-form");
    const dialog = document.querySelector("#save-confirm");
    const editStatus = document.querySelector("#edit-session-status");
    let approved = false;
    let submitter = null;

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
        if (status) status.textContent = count === 0 ? "" : `${count} pending ${count === 1 ? "change" : "changes"}`;
        actions.querySelectorAll?.("[data-label-template]")?.forEach((button) => {
            button.textContent = button.dataset.labelTemplate.replace("{count}", count).replace("{noun}", count === 1 ? "change" : "changes");
        });
    }

    function initializeRow(row, settingsForm) {
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

        function setBindingEnabled(enabled, selectedOperation) {
            row.querySelectorAll(".change-index, .change-key, .operation")
                .forEach((control) => { control.disabled = !enabled; });
            value.disabled = !enabled || selectedOperation === "RemoveOverride";
        }

        function showNormalState() {
            change.hidden = false;
            if (inherit) inherit.hidden = row.dataset.appliedOperation === "RemoveOverride";
            actions.hidden = true;
            editor.hidden = true;
            message.hidden = true;
        }

        function clearSaveBlockMessage() {
            if (!editStatus) return;
            editStatus.hidden = true;
            editStatus.textContent = "";
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
            if (inherit) inherit.hidden = true;
            actions.hidden = false;
            editor.hidden = candidateOperation === "RemoveOverride";
            message.hidden = candidateOperation !== "RemoveOverride";
            row.setAttribute("open", "");
            row.closest(".setting-category, .dynamic-settings")?.setAttribute("open", "");
            (candidateOperation === "Upsert" ? value : apply).focus();
        }

        function applyEdit() {
            const candidateOperation = row.dataset.candidateOperation;
            if (!session || !candidateOperation) return;
            if (candidateOperation === "Upsert" && !value.reportValidity()) return;
            operation.value = candidateOperation;
            row.dataset.appliedOperation = candidateOperation;
            row.dataset.dirty = "true";
            setBindingEnabled(true, candidateOperation);
            delete row.dataset.candidateOperation;
            session = null;
            showNormalState();
            clearSaveBlockMessage();
            updatePendingActions(settingsForm);
            change.focus();
        }

        function cancelEdit() {
            if (!session) return;
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
            clearSaveBlockMessage();
            updatePendingActions(settingsForm);
            change.focus();
        }

        change?.addEventListener("click", () => beginEdit("Upsert"));
        inherit?.addEventListener("click", () => beginEdit("RemoveOverride"));
        apply?.addEventListener("click", applyEdit);
        cancel?.addEventListener("click", cancelEdit);
        showNormalState();
    }

    document.querySelectorAll(".setting-row").forEach((row) => initializeRow(row, form));
    updatePendingActions(form);

    function blockActiveEdit(settingsForm, status) {
        const activeRow = settingsForm.querySelector('.setting-row[data-candidate-operation]');
        if (!activeRow) return false;
        activeRow.setAttribute("open", "");
        activeRow.closest(".setting-category, .dynamic-settings")?.setAttribute("open", "");
        status.textContent = "Apply or Cancel the active setting edit before saving.";
        status.hidden = false;
        status.focus();
        activeRow.querySelector(".apply-setting").focus();
        return true;
    }

    function populateReviewList(settingsForm, list) {
        list.replaceChildren();
        settingsForm.querySelectorAll('.setting-row[data-dirty="true"]').forEach((row) => {
            const value = row.querySelector(".setting-value");
            const operation = row.querySelector(".operation");
            const item = document.createElement("li");
            const newValue = row.dataset.sensitive === "true" ? "••••••••" : value.value;
            item.textContent = `${row.dataset.displayName}: ${operation.value === "RemoveOverride" ? "Use inherited value" : `Set to “${newValue}”`} (current value: “${row.dataset.oldValue || "not configured"}”).`;
            list.append(item);
        });
    }

    function handleSaveAttempt(event, settingsForm, status, reviewDialog, isApproved) {
        if (blockActiveEdit(settingsForm, status)) {
            event.preventDefault();
            return "blocked";
        }
        if (event.submitter?.dataset.submitKind === "draft") return "draft";
        if (isApproved) return "approved";

        event.preventDefault();
        const list = reviewDialog.querySelector("ul");
        populateReviewList(settingsForm, list);
        if (!list.children.length) {
            window.alert("No settings have changed.");
            return "empty";
        }
        reviewDialog.showModal();
        return "review";
    }

    globalThis.SettingsEditSessions = { initializeSettingsContext, setNavigationGuard, initializeRow, updatePendingActions, blockActiveEdit, populateReviewList, handleSaveAttempt };

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
    categories.forEach((category) => category.dataset.initialOpen = category.open.toString());
    const draftOnly = document.querySelector("#draft-only-filter");
    function applyFilters() {
        const query = search?.value.trim().toLowerCase() || "";
        let visible = 0;
        document.querySelectorAll(".setting-row").forEach((row) => {
            const matchesDraft = !draftOnly?.checked || row.dataset.draftChange === "true";
            const matches = matchesDraft && row.dataset.search.includes(query);
            row.hidden = !matches;
            if (matches) visible += 1;
        });
        categories.forEach((category) => {
            const hasMatch = category.querySelector(".setting-row:not([hidden])") !== null;
            category.hidden = (Boolean(query) || draftOnly?.checked) && !hasMatch;
            category.open = (query || draftOnly?.checked) && hasMatch ? true : category.dataset.initialOpen === "true";
        });
        if (searchStatus) {
            searchStatus.textContent = query || draftOnly?.checked
                ? draftOnly?.checked
                    ? `${visible} draft ${visible === 1 ? "change" : "changes"} found`
                    : `${visible} ${visible === 1 ? "setting" : "settings"} found`
                : "";
        }
    }
    search?.addEventListener("input", applyFilters);
    draftOnly?.addEventListener("change", applyFilters);

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

    const dirtyCount = () => form?.querySelectorAll('.setting-row[data-dirty="true"]').length || 0;
    const hasCandidate = () => Boolean(form?.querySelector('.setting-row[data-candidate-operation]'));
    function restoreContextControl(trigger) {
        if (trigger?.dataset?.committedValue !== undefined) trigger.value = trigger.dataset.committedValue;
    }
    function unsavedMessage(count) {
        return `You have ${count} ${count === 1 ? "change" : "changes"} that ${count === 1 ? "has" : "have"} not been saved.`;
    }
    function disableDirtyMutations() {
        form?.querySelectorAll('.setting-row[data-dirty="true"] .change-index, .setting-row[data-dirty="true"] .change-key, .setting-row[data-dirty="true"] .operation, .setting-row[data-dirty="true"] .setting-value')
            .forEach((control) => { control.disabled = true; });
    }
    function needsLiveConfirmation(targetForm) {
        if (targetForm.dataset.requiresLiveConfirm?.toLowerCase() === "true") return true;
        return targetForm.matches?.("[data-preview-form]") && targetForm.querySelector('[name="AllowLiveSubmission"]:checked')?.value === "true";
    }
    function finalSubmit(action) {
        pending = null;
        submitting = true;
        action.prepare?.();
        approvedForms.add(action.form);
        action.form.requestSubmit(action.submitter || undefined);
    }
    function continuePipeline(action, skipDirty = false, skipLive = false) {
        if (hasCandidate()) {
            restoreContextControl(action.trigger);
            blockActiveEdit(form, editStatus);
            return "candidate";
        }
        const count = dirtyCount();
        if (!skipDirty && count) {
            pending = action;
            unsavedDialog.querySelector("[data-unsaved-message]").textContent = unsavedMessage(count);
            unsavedDialog._trigger = action.trigger;
            unsavedDialog.showModal();
            unsavedDialog.querySelector("[data-dialog-cancel]").focus();
            return "dirty";
        }
        if (!skipLive && needsLiveConfirmation(action.form)) {
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
        if (kind === "draft") { submitting = true; return; }
        if (approved) { submitting = true; return; }
        event.preventDefault();
        const list = dialog.querySelector("ul");
        populateReviewList(form, list);
        if (!list.children.length) return;
        dialog._trigger = event.submitter;
        dialog.showModal();
        dialog.querySelector("#confirm-save")?.focus();
    });
    document.querySelector("#confirm-save")?.addEventListener("click", () => {
        approved = true;
        dialog.close();
        form.requestSubmit(reviewSubmitter);
    });
    document.querySelector("#cancel-save")?.addEventListener("click", () => {
        dialog.close();
        dialog._trigger?.focus();
        approved = false;
    });

    navigationGuard = (action, trigger) => {
        if (hasCandidate()) {
            restoreContextControl(trigger);
            blockActiveEdit(form, editStatus);
            return false;
        }
        if (!dirtyCount()) { submitting = true; action(); return true; }
        pending = { navigate: action, trigger };
        unsavedDialog._trigger = trigger;
        unsavedDialog.querySelector("[data-unsaved-message]").textContent = unsavedMessage(dirtyCount());
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
    document.querySelectorAll("[data-dialog-cancel]").forEach((button) => button.addEventListener("click", () => {
        const owner = button.closest("dialog");
        if (!owner.open) return;
        owner.close();
        restoreContextControl(owner._trigger);
        owner._trigger?.focus();
        pending = null;
        submitting = false;
    }));
    document.querySelector("[data-guard-discard]")?.addEventListener("click", () => {
        unsavedDialog.close();
        const action = pending;
        if (action?.navigate) { disableDirtyMutations(); submitting = true; action.navigate(); }
        else if (action) continuePipeline({ ...action, prepare: () => { disableDirtyMutations(); action.prepare?.(); } }, true);
    });
    document.querySelector("[data-guard-save-live]")?.addEventListener("click", () => {
        unsavedDialog.close(); pending = null; approved = false;
        form.requestSubmit(form.querySelector('button[type="submit"]:not([data-submit-kind])'));
    });
    document.querySelector("[data-guard-save-draft]")?.addEventListener("click", () => {
        unsavedDialog.close(); pending = null; submitting = true;
        form.requestSubmit(form.querySelector('[data-submit-kind="draft"]'));
    });
    document.querySelector("[data-confirm-live-preview]")?.addEventListener("click", () => {
        liveDialog.close();
        const action = pending;
        if (action) continuePipeline(action, true, true);
    });
    document.querySelector("[data-discard-pending]")?.addEventListener("click", (event) => {
        navigationGuard(() => location.reload(), event.currentTarget);
    });
    window.addEventListener?.("beforeunload", (event) => {
        if (!submitting && (dirtyCount() || hasCandidate())) { event.preventDefault(); event.returnValue = ""; }
    });

    document.querySelectorAll('[name="AllowLiveSubmission"]').forEach((radio) => radio.addEventListener("change", () => {
        radio.closest("form")?.querySelector(".preview-live-warning")?.toggleAttribute("hidden", radio.value !== "true" || !radio.checked);
    }));

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
    globalThis.SettingsWorkflow = { continuePipeline, lifecycleSubmit, needsLiveConfirmation, disableDirtyMutations, restoreContextControl, applyFilters, copyPreviewUrl, unsavedMessage };

    const copyButton = document.querySelector("[data-copy-preview-url]");
    copyButton?.addEventListener("click", () => copyPreviewUrl(
        navigator.clipboard, document.querySelector("#preview-url").value, document.querySelector("[data-copy-status]")));
    document.querySelector("#preview-url")?.addEventListener("focus", (event) => event.currentTarget.select());
})();
