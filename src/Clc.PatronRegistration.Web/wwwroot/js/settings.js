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
            row.querySelector(".setting-value")?.focus();
            row.closest("details")?.setAttribute("open", "");
        }
    }

    document.querySelectorAll(".setting-row").forEach((row) => {
        row.querySelector(".edit-setting")?.addEventListener("click", () => setRowEnabled(row, true));
        row.querySelector(".operation")?.addEventListener("change", (event) => {
            const value = row.querySelector(".setting-value");
            value.disabled = event.target.value === "RemoveOverride";
        });
    });

    search?.addEventListener("input", () => {
        const query = search.value.trim().toLowerCase();
        let visible = 0;
        document.querySelectorAll(".setting-row").forEach((row) => {
            const matches = row.dataset.search.includes(query);
            row.hidden = !matches;
            if (matches) {
                visible += 1;
                if (query) {
                    row.closest("details")?.setAttribute("open", "");
                }
            }
        });
        searchStatus.textContent = query ? `${visible} settings match.` : "";
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
            item.textContent = `${row.dataset.displayName} (${row.dataset.settingKey}): ${operation.value}; old “${row.dataset.oldValue || "not configured"}”; new “${operation.value === "RemoveOverride" ? "inherit" : newValue}”.`;
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
