(() => {
    const search = document.querySelector("#setting-search");
    const searchStatus = document.querySelector("#search-status");
    const form = document.querySelector("#settings-form");
    const dialog = document.querySelector("#save-confirm");
    let approved = false;
    let submitter = null;

    function setRowEnabled(row, enabled) {
        row.dataset.dirty = enabled ? "true" : "false";
        row.querySelectorAll(".change-index, .change-key, .operation, .setting-value")
            .forEach((control) => {
                control.disabled = !enabled;
            });
        if (enabled) {
            const operation = row.querySelector(".operation");
            const value = row.querySelector(".setting-value");
            if (operation?.value === "RemoveOverride" && value) {
                value.disabled = true;
            }
            (operation?.value === "RemoveOverride" ? operation : value)?.focus();
            row.setAttribute("open", "");
            row.closest(".setting-category, .dynamic-settings")?.setAttribute("open", "");
        }
    }

    document.querySelectorAll(".setting-row").forEach((row) => {
        row.querySelector(".edit-setting")?.addEventListener("click", () => setRowEnabled(row, true));
        row.querySelector(".operation")?.addEventListener("change", (event) => {
            const value = row.querySelector(".setting-value");
            value.disabled = event.target.value === "RemoveOverride";
        });
    });

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
        if (submitter?.dataset.submitKind === "draft") {
            return;
        }
        if (approved) {
            return;
        }

        event.preventDefault();
        const list = dialog.querySelector("ul");
        list.replaceChildren();
        form.querySelectorAll('.setting-row[data-dirty="true"]').forEach((row) => {
            const value = row.querySelector(".setting-value");
            const operation = row.querySelector(".operation");
            const item = document.createElement("li");
            const newValue = row.dataset.sensitive === "true" ? "••••••••" : value.value;
            item.textContent = `${row.dataset.displayName}: ${operation.value === "RemoveOverride" ? "Use inherited value" : `Set to “${newValue}”`} (current value: “${row.dataset.oldValue || "not configured"}”).`;
            list.append(item);
        });

        if (!list.children.length) {
            window.alert("No settings have changed.");
            return;
        }
        dialog.showModal();
    });

    document.querySelector("#confirm-save")?.addEventListener("click", () => {
        approved = true;
        dialog.close();
        form.requestSubmit(submitter);
    });

    document.querySelector("#cancel-save")?.addEventListener("click", () => dialog.close());
})();
