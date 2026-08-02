(() => {
    const search = document.querySelector("#setting-search");
    const searchStatus = document.querySelector("#search-status");
    const form = document.querySelector("#settings-form");
    const dialog = document.querySelector("#save-confirm");
    const editStatus = document.querySelector("#edit-session-status");
    let approved = false;
    let submitter = null;

    let navigationGuard = null;
    function initializeSettingsContext(contextForm) {
        if (!contextForm) return;
        const organizationScope = contextForm.querySelector("#organization-scope");
        const formCodeScope = contextForm.querySelector("#form-code-scope");

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

    globalThis.SettingsEditSessions = { initializeSettingsContext, initializeRow, updatePendingActions, blockActiveEdit, populateReviewList, handleSaveAttempt };

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
        if (searchStatus) searchStatus.textContent = query || draftOnly?.checked ? `${visible} ${draftOnly?.checked ? "draft " : ""}${visible === 1 ? "change" : "changes"} found` : "";
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

    form?.addEventListener("submit", (event) => {
        submitter = event.submitter;
        handleSaveAttempt(event, form, editStatus, dialog, approved);
    });

    document.querySelector("#confirm-save")?.addEventListener("click", () => {
        approved = true;
        dialog.close();
        form.requestSubmit(submitter);
    });

    document.querySelector("#cancel-save")?.addEventListener("click", () => dialog.close());


    let submitting = false;
    let pendingAction = null;
    const unsavedDialog = document.querySelector("#unsaved-changes-dialog");
    const dirtyCount = () => form?.querySelectorAll('.setting-row[data-dirty="true"]').length || 0;
    const hasCandidate = () => Boolean(form?.querySelector('.setting-row[data-candidate-operation]'));
    function continueAction(action) { submitting = true; action(); }
    function guardAction(action, trigger) {
        if (hasCandidate()) { blockActiveEdit(form, editStatus); return false; }
        const count = dirtyCount();
        if (!count) { continueAction(action); return true; }
        pendingAction = action;
        unsavedDialog.querySelector("[data-unsaved-message]").textContent = `You have ${count} ${count === 1 ? "change" : "changes"} that ${count === 1 ? "has" : "have"} not been saved.`;
        unsavedDialog._trigger = trigger;
        unsavedDialog.showModal();
        unsavedDialog.querySelector("[data-dialog-cancel]").focus();
        return false;
    }
    navigationGuard = guardAction;
    document.querySelectorAll("[data-guard-action]").forEach((guardedForm) => guardedForm.addEventListener("submit", (event) => {
        if (submitting || guardedForm === form) return;
        event.preventDefault(); guardAction(() => guardedForm.requestSubmit(event.submitter), event.submitter);
    }));
    document.querySelectorAll(".settings-navigation a").forEach((link) => link.addEventListener("click", (event) => {
        if (submitting) return; event.preventDefault(); guardAction(() => { location.href = link.href; }, link);
    }));
    document.querySelectorAll("[data-dialog-cancel]").forEach((button) => button.addEventListener("click", () => {
        const owner = button.closest("dialog"); owner.close(); owner._trigger?.focus(); pendingAction = null;
    }));
    document.querySelectorAll("[data-open-dialog]").forEach((button) => button.addEventListener("click", () => {
        guardAction(() => { submitting = false; const target = document.getElementById(button.dataset.openDialog); target._trigger = button; target.showModal(); target.querySelector("button:not([disabled])")?.focus(); }, button);
    }));
    document.querySelector("[data-guard-discard]")?.addEventListener("click", () => { unsavedDialog.close(); const action = pendingAction; pendingAction = null; continueAction(action); });
    document.querySelector("[data-guard-save-live]")?.addEventListener("click", () => { unsavedDialog.close(); approved = false; form.requestSubmit(form.querySelector('button[type="submit"]:not([data-submit-kind])')); });
    document.querySelector("[data-guard-save-draft]")?.addEventListener("click", () => { unsavedDialog.close(); submitting = true; form.requestSubmit(form.querySelector('[data-submit-kind="draft"]')); });
    document.querySelector("[data-discard-pending]")?.addEventListener("click", (event) => guardAction(() => location.reload(), event.currentTarget));
    window.addEventListener?.("beforeunload", (event) => { if (!submitting && (dirtyCount() || hasCandidate())) { event.preventDefault(); event.returnValue = ""; } });
    form?.addEventListener("submit", (event) => { if (!event.defaultPrevented && (approved || event.submitter?.dataset.submitKind === "draft")) submitting = true; });

    const liveDialog = document.querySelector("#live-preview-confirm");
    let liveForm;
    document.querySelectorAll('[name="AllowLiveSubmission"]').forEach((radio) => radio.addEventListener("change", () => {
        radio.closest("form")?.querySelector(".preview-live-warning")?.toggleAttribute("hidden", radio.value !== "true" || !radio.checked);
    }));
    document.querySelectorAll("[data-preview-form], [data-requires-live-confirm='True'], [data-requires-live-confirm='true']").forEach((previewForm) => previewForm.addEventListener("submit", (event) => {
        const live = previewForm.dataset.requiresLiveConfirm?.toLowerCase() === "true" || previewForm.querySelector('[name="AllowLiveSubmission"]:checked')?.value === "true";
        if (!live || previewForm.dataset.liveConfirmed === "true") return;
        event.preventDefault(); liveForm = previewForm; liveDialog._trigger = event.submitter; liveDialog.showModal(); liveDialog.querySelector("[data-confirm-live-preview]").focus();
    }));
    document.querySelector("[data-confirm-live-preview]")?.addEventListener("click", () => { liveDialog.close(); liveForm.dataset.liveConfirmed = "true"; liveForm.requestSubmit(); });

    const copyButton = document.querySelector("[data-copy-preview-url]");
    copyButton?.addEventListener("click", async () => {
        const status = document.querySelector("[data-copy-status]");
        try { await navigator.clipboard.writeText(document.querySelector("#preview-url").value); status.textContent = "Preview URL copied."; }
        catch { status.textContent = "Copy failed. Select the URL and copy it manually."; }
    });
    document.querySelector("#preview-url")?.addEventListener("focus", (event) => event.currentTarget.select());
})();
