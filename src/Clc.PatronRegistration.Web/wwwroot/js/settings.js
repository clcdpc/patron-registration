(() => {
    const search = document.querySelector("#setting-search");
    const searchStatus = document.querySelector("#search-status");
    const form = document.querySelector("#settings-form");
    const dialog = document.querySelector("#save-confirm");
    const editStatus = document.querySelector("#edit-session-status");
    let approved = false;
    let submitter = null;

    function initializeRow(row) {
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
            if (candidateOperation === "Upsert" && !value.reportValidity()) return;
            operation.value = candidateOperation;
            row.dataset.appliedOperation = candidateOperation;
            row.dataset.dirty = "true";
            setBindingEnabled(true, candidateOperation);
            delete row.dataset.candidateOperation;
            session = null;
            showNormalState();
            clearSaveBlockMessage();
            change.focus();
        }

        function cancelEdit() {
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
            change.focus();
        }

        change?.addEventListener("click", () => beginEdit("Upsert"));
        inherit?.addEventListener("click", () => beginEdit("RemoveOverride"));
        apply?.addEventListener("click", applyEdit);
        cancel?.addEventListener("click", cancelEdit);
        showNormalState();
    }

    document.querySelectorAll(".setting-row").forEach(initializeRow);

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

    globalThis.SettingsEditSessions = { initializeRow, blockActiveEdit, populateReviewList, handleSaveAttempt };

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
    search?.addEventListener("input", () => {
        const query = search.value.trim().toLowerCase();
        let visible = 0;
        document.querySelectorAll(".setting-row").forEach((row) => {
            const matches = row.dataset.search.includes(query);
            row.hidden = !matches;
            if (matches) visible += 1;
        });
        categories.forEach((category) => {
            const hasMatch = category.querySelector(".setting-row:not([hidden])") !== null;
            category.hidden = Boolean(query) && !hasMatch;
            category.open = query && hasMatch ? true : category.dataset.initialOpen === "true";
        });
        searchStatus.textContent = query ? `${visible} ${visible === 1 ? "setting" : "settings"} found` : "";
    });

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
})();
