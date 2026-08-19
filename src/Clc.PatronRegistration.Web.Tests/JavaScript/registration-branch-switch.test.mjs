import { strict as assert } from "node:assert";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import * as vm from "node:vm";

const markup = readFileSync(new URL("../../Clc.PatronRegistration.Web/Views/Registration/Create.cshtml", import.meta.url), "utf8");

test("registration branch switching never uses browser storage for patron data", () => {
    assert.doesNotMatch(markup, /sessionStorage|localStorage|indexedDB|document\.cookie/i);
    assert.doesNotMatch(markup, /saveRegistrationState|restoreRegistrationState|clearRegistrationState/);
    assert.doesNotMatch(markup, /\.getItem\(|\.setItem\(|\.removeItem\(/);
    assert.match(markup, /<form id="regform" autocomplete="off">/);
    assert.match(markup, /asp-for="Password" type="password" autocomplete="new-password"/);
    assert.match(markup, /asp-for="Password2" type="password" autocomplete="new-password"/);
});

test("branch switching submits the live form transiently and replaces it with the selected branch response", async () => {
    const branchStart = markup.indexOf("    if (branchReloadUrlPattern && patronBranchId) {");
    const branchEnd = markup.indexOf("    const nameFirst", branchStart);
    assert.ok(branchStart >= 0);
    assert.ok(branchEnd > branchStart);
    const source = markup.slice(branchStart, branchEnd);

    assert.match(source, /new FormData\(theform\)/);
    assert.match(source, /branchFormData\.set\("PatronBranchID", patronBranchId\.value\)/);
    assert.match(source, /await reloadRegistrationForm\(branchReloadUrl\.toString\(\), branchFormData\)/);
    assert.doesNotMatch(source, /window\.location\.assign/);

    let changeHandler;
    let reloadRequest;
    const branch = {
        value: "4",
        disabled: false,
        addEventListener(event, handler) {
            assert.equal(event, "change");
            changeHandler = handler;
        }
    };
    class FormDataMock {
        constructor() {
            this.values = new Map([
                ["NameFirst", "Earlier patron"],
                ["Password", "1234"],
                ["Password2", "1234"]
            ]);
        }

        set(name, value) {
            this.values.set(name, value);
        }

        get(name) {
            return this.values.get(name);
        }
    }

    const sandbox = {
        agreementAccepted: true,
        branchReloadUrlPattern: "/Registration/ChangeBranch?orgId=2&agreementAccepted=__AGREEMENT__",
        document: { contains: () => false },
        forcedDriverLicense: true,
        FormData: FormDataMock,
        patronBranchId: branch,
        reloadRegistrationForm: async (url, formData) => {
            reloadRequest = { url, formData };
            return true;
        },
        theform: {},
        URL,
        window: { location: { href: "https://localhost/Registration/Create/2" } }
    };

    vm.runInNewContext(source, sandbox);
    assert.ok(changeHandler);
    await changeHandler();

    assert.equal(reloadRequest.formData.get("NameFirst"), "Earlier patron");
    assert.equal(reloadRequest.formData.get("Password"), "1234");
    assert.equal(reloadRequest.formData.get("Password2"), "1234");
    assert.equal(reloadRequest.formData.get("PatronBranchID"), "4");
    assert.match(reloadRequest.url, /forceDl=true/);
    assert.match(reloadRequest.url, /agreementAccepted=true/);
});

test("abandoning a registration cannot restore a previous patron from Web Storage", () => {
    assert.doesNotMatch(markup, /restore|persist|storage/i);
    assert.match(markup, /registrationContainer\.innerHTML = await response\.text\(\)/);
    assert.match(markup, /registrationContainer\.querySelectorAll\('script'\)/);
});

test("branch responses regenerate selected-branch validation and workflow settings", () => {
    assert.match(markup, /Url\.Action\("ChangeBranch", "Registration"/);
    assert.match(markup, /Settings\.GetRequiredFields\(\)/);
    assert.match(markup, /Settings\.GetFieldRequired\(nameof\(Model\.EmailAddress\)\)/);
    assert.match(markup, /Settings\.GetFieldRequired\(nameof\(Model\.PhoneVoice1\)\)/);
    assert.match(markup, /cache: "no-store"/);
});
