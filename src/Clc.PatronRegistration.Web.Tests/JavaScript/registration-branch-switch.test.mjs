import { strict as assert } from "node:assert";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import * as vm from "node:vm";

const markup = readFileSync(new URL("../../Clc.PatronRegistration.Web/Views/Registration/_RegistrationForm.cshtml", import.meta.url), "utf8")
    .replace(/\r\n/g, "\n");

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
    assert.match(source, /const pinValues = capturePinValues\(theform\)/);
    assert.match(source, /await reloadRegistrationForm\(branchReloadUrl\.toString\(\), branchFormData, pinValues\)/);
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
        capturePinValues: () => ({ password: "1234", password2: "1234" }),
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

test("branch replacement puts both transient PIN values onto the inserted password controls", async () => {
    const applyStart = markup.indexOf("    function applyPinValues");
    const applyEnd = markup.indexOf("\n\n    async function reloadRegistrationForm", applyStart);
    assert.ok(applyStart >= 0);
    assert.ok(applyEnd > applyStart);
    assert.match(markup, /currentFragment\.replaceWith\(nextFragment\);[\s\S]*applyPinValues\(nextFragment, pinValues\)/);

    class Fragment {
        constructor() {
            this.controls = new Map([
                ["#Password", { value: "" }],
                ["#Password2", { value: "" }]
            ]);
        }

        querySelector(selector) {
            return this.controls.get(selector) ?? null;
        }
    }

    const fragment = new Fragment();
    const sandbox = {};
    vm.runInNewContext(`${markup.slice(applyStart, applyEnd)}
        globalThis.applyPinValues = applyPinValues;`, sandbox);
    sandbox.applyPinValues(fragment, { password: "1234", password2: "5678" });

    const nextPassword = fragment.querySelector("#Password");
    const nextPassword2 = fragment.querySelector("#Password2");
    assert.equal(nextPassword.value, "1234");
    assert.equal(nextPassword2.value, "5678");
});

function getRegistrationHandlerInitializationSource() {
    const referencesStart = markup.indexOf("    const nameFirst");
    const referencesEnd = markup.indexOf("\n\n    let ageBlockRequestId", referencesStart);
    const addEventHandlerStart = markup.indexOf("    function AddEventHandler");
    const addEventHandlerEnd = markup.indexOf("\n\n    async function ageBlockCheck", addEventHandlerStart);
    const bindingStart = markup.indexOf('    AddEventHandler(deliveryOptionId, "change"');
    const bindingEnd = markup.indexOf("\n\n    handleNotificationPreferenceChange", bindingStart);
    const updateUser1Start = markup.indexOf("    function updateUser1");
    const updateUser1End = markup.indexOf("\n\n    function showStudentSchoolInfo", updateUser1Start);
    const driverLicenseStart = markup.indexOf("    async function dl");
    const driverLicenseEnd = markup.indexOf("\n\n    function dupecheck", driverLicenseStart);

    for (const position of [
        referencesStart,
        referencesEnd,
        addEventHandlerStart,
        addEventHandlerEnd,
        bindingStart,
        bindingEnd,
        updateUser1Start,
        updateUser1End,
        driverLicenseStart,
        driverLicenseEnd
    ]) {
        assert.ok(position >= 0);
    }

    const driverLicenseSource = markup
        .slice(driverLicenseStart, driverLicenseEnd)
        .replace("@Html.Raw(Settings.DriversLicensePromptText.ToJavascriptString())", '"Scan your license"')
        .replace('"@driverLicenseUrl"', '"/Registration/dl"');

    return `
        (function initializeRegistrationFragment() {
            const q = document.querySelector.bind(document);
            const patronBranchId = q('#PatronBranchID');
            ${markup.slice(referencesStart, referencesEnd)}
            ${markup.slice(addEventHandlerStart, addEventHandlerEnd)}
            ${markup.slice(updateUser1Start, updateUser1End)}
            async function handleBirthdateChanged() { }
            ${driverLicenseSource}
            ${markup.slice(bindingStart, bindingEnd)}
        })();`;
}

class RegistrationControl {
    constructor(id) {
        this.id = id;
        this.value = "";
        this.listeners = new Map();
    }

    addEventListener(event, handler) {
        const handlers = this.listeners.get(event) ?? [];
        handlers.push(handler);
        this.listeners.set(event, handlers);
    }

    dispatchEvent(event) {
        const eventObject = typeof event === "string" ? { target: this } : event;
        return (this.listeners.get(eventObject.type) ?? [])
            .map(handler => handler(eventObject));
    }

    listenerCount(event) {
        return (this.listeners.get(event) ?? []).length;
    }
}

class RegistrationFragment {
    constructor() {
        const ids = [
            "dlbutton",
            "otherSchoolName",
            "User1",
            "PatronBranchID",
            "NameFirst",
            "NameMiddle",
            "NameLast",
            "Birthdate",
            "StreetOne",
            "City",
            "PostalCode"
        ];
        this.controls = new Map(ids.map(id => [`#${id}`, new RegistrationControl(id)]));
        this.controls.set('input[name="__RequestVerificationToken"]', new RegistrationControl("__RequestVerificationToken"));
        this.controls.get('input[name="__RequestVerificationToken"]').value = "token";
    }

    querySelector(selector) {
        return this.controls.get(selector) ?? null;
    }

    control(id) {
        return this.controls.get(`#${id}`);
    }
}

function createRegistrationHandlerHarness() {
    const document = {
        currentFragment: null,
        querySelector(selector) {
            return this.currentFragment?.querySelector(selector) ?? null;
        }
    };
    const driverLicenseData = {
        firstName: "Replacement",
        middleName: "Driver",
        lastName: "License",
        birthdate: "2000-01-02T00:00:00",
        address: "Replacement Street",
        city: "Replacement City",
        zip: "43210"
    };
    const sandbox = {
        document,
        FormData: class {
            append() { }
        },
        postData: () => driverLicenseData,
        window: { prompt: () => "12345678901234567890" }
    };
    const initializationSource = getRegistrationHandlerInitializationSource();

    return {
        initialize(fragment) {
            document.currentFragment = fragment;
            vm.runInNewContext(initializationSource, sandbox);
        },
        fragment() {
            return new RegistrationFragment();
        }
    };
}

test("replacement fragment handlers update replacement controls for driver-license and other-school input", async () => {
    assert.doesNotMatch(markup, /onclick="dl\(\)"/);
    assert.doesNotMatch(markup, /onChange="updateUser1\(this\.value\)"/);

    const harness = createRegistrationHandlerHarness();
    const original = harness.fragment();
    harness.initialize(original);
    original.control("NameFirst").value = "Original";
    original.control("User1").value = "Original School";

    const replacement = harness.fragment();
    harness.initialize(replacement);
    replacement.control("otherSchoolName").value = "Replacement School";
    replacement.control("otherSchoolName").dispatchEvent({ type: "change", target: replacement.control("otherSchoolName") });
    await replacement.control("dlbutton").dispatchEvent({ type: "click", target: replacement.control("dlbutton") });
    await new Promise(resolve => setImmediate(resolve));

    assert.equal(replacement.control("User1").value, "Replacement School");
    assert.equal(replacement.control("NameFirst").value, "Replacement");
    assert.equal(original.control("User1").value, "Original School");
    assert.equal(original.control("NameFirst").value, "Original");
});

test("repeated branch replacements bind handlers to only the newest fragment", async () => {
    const harness = createRegistrationHandlerHarness();
    const original = harness.fragment();
    const firstReplacement = harness.fragment();
    const newestReplacement = harness.fragment();

    harness.initialize(original);
    harness.initialize(firstReplacement);
    harness.initialize(newestReplacement);

    original.control("User1").value = "Original School";
    firstReplacement.control("User1").value = "First School";
    newestReplacement.control("otherSchoolName").value = "Newest School";
    newestReplacement.control("otherSchoolName").dispatchEvent({ type: "change", target: newestReplacement.control("otherSchoolName") });
    await newestReplacement.control("dlbutton").dispatchEvent({ type: "click", target: newestReplacement.control("dlbutton") });
    await new Promise(resolve => setImmediate(resolve));

    assert.equal(newestReplacement.control("User1").value, "Newest School");
    assert.equal(newestReplacement.control("NameFirst").value, "Replacement");
    assert.equal(original.control("User1").value, "Original School");
    assert.equal(firstReplacement.control("User1").value, "First School");
    assert.equal(newestReplacement.control("dlbutton").listenerCount("click"), 1);
    assert.equal(newestReplacement.control("otherSchoolName").listenerCount("change"), 1);
});

test("abandoning a registration cannot restore a previous patron from Web Storage", () => {
    assert.doesNotMatch(markup, /restore|persist|storage/i);
    assert.match(markup, /template\.innerHTML = \(await response\.text\(\)\)\.trim\(\)/);
    assert.match(markup, /currentFragment\.replaceWith\(nextFragment\)/);
    assert.match(markup, /nextFragment\.querySelectorAll\('script'\)/);
});

test("branch responses regenerate selected-branch validation and workflow settings", () => {
    assert.match(markup, /Url\.Action\("ChangeBranch", "Registration"/);
    assert.match(markup, /Settings\.GetRequiredFields\(\)/);
    assert.match(markup, /Settings\.GetFieldRequired\(nameof\(Model\.EmailAddress\)\)/);
    assert.match(markup, /Settings\.GetFieldRequired\(nameof\(Model\.PhoneVoice1\)\)/);
    assert.match(markup, /cache: "no-store"/);
});

test("disabled branch selection renders a fixed value and cannot reload siblings", () => {
    assert.match(markup, /var branchSelectionEnabled = ViewData\["RegistrationBranchSelectionEnabled"\]/);
    assert.match(markup, /var branchReloadUrlPattern = !isSettingsPreview && branchSelectionEnabled/);

    const branchStart = markup.indexOf("@if (branchSelectionEnabled)");
    const disabledStart = markup.indexOf("else", branchStart);
    const disabledEnd = markup.indexOf("@if (Settings.DisplayECardCheckbox", disabledStart);
    assert.ok(branchStart >= 0);
    assert.ok(disabledStart > branchStart);
    assert.ok(disabledEnd > disabledStart);

    const disabledBranchMarkup = markup.slice(disabledStart, disabledEnd);
    assert.match(disabledBranchMarkup, /Html\.HiddenFor\(m => m\.PatronBranchID\)/);
    assert.match(disabledBranchMarkup, /PatronBranchIDDisplay/);
    assert.doesNotMatch(disabledBranchMarkup, /DropDownListFor|<select/i);
});

test("branch responses replace one fragment root and never introduce a nested layout container", () => {
    assert.equal((markup.match(/<div id="registration-form-fragment"/g) || []).length, 1);
    assert.doesNotMatch(markup, /<div id="regFormContainer"/);
    assert.doesNotMatch(markup, /<!DOCTYPE|<html|<head|<body/i);
    assert.match(markup, /nextFragment\.querySelector\('#regFormContainer, html, head, body'\)/);
    assert.match(markup, /template\.content\.childElementCount !== 1/);
    assert.match(markup, /const scriptId = "registration-validation-script"/);
});

class ElementMock {
    constructor(tagName) {
        this.tagName = tagName;
        this.id = "";
        this.dataset = {};
        this.children = [];
        this.parentNode = null;
    }

    appendChild(child) {
        child.parentNode = this;
        this.children.push(child);
        return child;
    }

    insertBefore(child, reference) {
        child.parentNode = this;
        const index = reference ? this.children.indexOf(reference) : -1;
        if (index < 0) {
            this.children.push(child);
        } else {
            this.children.splice(index, 0, child);
        }
        return child;
    }

    remove() {
        if (!this.parentNode) return;
        const index = this.parentNode.children.indexOf(this);
        if (index >= 0) this.parentNode.children.splice(index, 1);
        this.parentNode = null;
    }
}

function createBrandingHarness() {
    const container = new ElementMock("div");
    container.id = "regFormContainer";
    container.dataset.registrationBrandingEnabled = "true";
    const branding = new ElementMock("div");
    branding.id = "registration-branding";
    const stylesheet = new ElementMock("link");
    stylesheet.id = "registration-configured-stylesheet";
    stylesheet.href = "https://example.test/route.css";
    const image = new ElementMock("img");
    image.id = "registration-header-image";
    image.src = "https://example.test/route.png";
    branding.appendChild(stylesheet);
    branding.appendChild(image);
    container.appendChild(branding);

    const document = {
        querySelector(selector) {
            const id = selector.startsWith("#") ? selector.slice(1) : null;
            if (!id) return null;

            function find(element) {
                if (element.id === id) return element;
                for (const child of element.children) {
                    const found = find(child);
                    if (found) return found;
                }
                return null;
            }

            return find(container);
        },
        createElement(tagName) {
            return new ElementMock(tagName);
        }
    };

    const sandbox = { document };
    const updateSource = markup.match(/(function updateRegistrationBranding\(fragment\) \{[\s\S]*?\n    \})(?=\r?\n\r?\n    async function reloadRegistrationForm)/)?.[1];
    assert.ok(updateSource);
    vm.runInNewContext(`
        const q = document.querySelector.bind(document);
        ${updateSource}
        globalThis.updateRegistrationBranding = updateRegistrationBranding;
    `, sandbox);

    return {
        apply(cssUrl, headerImageUrl) {
            sandbox.updateRegistrationBranding({
                dataset: {
                    registrationCssUrl: cssUrl,
                    registrationHeaderImageUrl: headerImageUrl
                }
            });
        },
        elements() {
            return {
                stylesheet: document.querySelector("#registration-configured-stylesheet"),
                image: document.querySelector("#registration-header-image"),
                branding
            };
        }
    };
}

test("branch branding updates, clears, and reuses the same DOM elements across repeated switches", () => {
    const harness = createBrandingHarness();

    harness.apply("https://example.test/a.css", "https://example.test/a.png");
    let elements = harness.elements();
    assert.equal(elements.stylesheet.href, "https://example.test/a.css");
    assert.equal(elements.image.src, "https://example.test/a.png");
    assert.equal(elements.branding.children.filter(child => child.id === "registration-configured-stylesheet").length, 1);
    assert.equal(elements.branding.children.filter(child => child.id === "registration-header-image").length, 1);

    harness.apply("https://example.test/b.css", "https://example.test/b.png");
    elements = harness.elements();
    assert.equal(elements.stylesheet.href, "https://example.test/b.css");
    assert.equal(elements.image.src, "https://example.test/b.png");
    assert.equal(elements.branding.children.length, 2);

    harness.apply("", "");
    elements = harness.elements();
    assert.equal(elements.stylesheet, null);
    assert.equal(elements.image, null);
    assert.equal(elements.branding.children.length, 0);

    harness.apply("https://example.test/a.css", "https://example.test/a.png");
    elements = harness.elements();
    assert.equal(elements.stylesheet.href, "https://example.test/a.css");
    assert.equal(elements.image.src, "https://example.test/a.png");
    assert.equal(elements.branding.children.length, 2);
});
