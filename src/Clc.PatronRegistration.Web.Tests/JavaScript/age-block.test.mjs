import { strict as assert } from "node:assert";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import * as vm from "node:vm";

const markup = readFileSync(new URL("../../Clc.PatronRegistration.Web/Views/Registration/Create.cshtml", import.meta.url), "utf8");

test("registration view uses the live age-block endpoint", () => {
    assert.match(markup, /var ageBlockCheckUrl = this\.BuildAction\("AgeBlockCheck"\)/);
    assert.doesNotMatch(markup, /PreviewController|isSettingsPreview|previewToken|_RegistrationForm/);
});

test("age-block checks reject stale responses before applying them", () => {
    assert.match(markup, /let ageBlockRequestId = 0;/);

    const ageBlockCheck = markup.match(/(async function ageBlockCheck\(\) \{[\s\S]*?\n    \})(?=\r?\n\r?\n    async function handleBirthdateChanged)/);
    assert.ok(ageBlockCheck);
    const source = ageBlockCheck[1];

    assert.match(source, /const requestId = \+\+ageBlockRequestId;/);
    assert.match(source, /const birthdateValue = birthdate\.value;/);
    assert.match(source, /if \(!birthdateValue\) \{\s*return "allowed";/);
    assert.match(source, /formData\.append\("Birthdate", birthdateValue\)/);
    assert.match(source, /method: "POST"/);
    assert.match(source, /credentials: "same-origin"/);
    assert.match(source, /let result = null;/);
    assert.match(source, /if \(response\.ok\) \{\s*result = await response\.json\(\);\s*\}/);
    assert.match(source, /if \(requestId !== ageBlockRequestId \|\| birthdate\.value !== birthdateValue\) \{\s*return "stale";/);
    assert.match(source, /if \(result\?\.isBlocked\) \{[\s\S]*?wrapperDiv\.innerHTML = result\.message[\s\S]*?return "blocked";/);
    assert.doesNotMatch(source, /unavailable/);

    assert.ok(source.indexOf('return "stale"') < source.indexOf("if (result?.isBlocked)"));
    assert.ok(source.indexOf("birthdate.value !== birthdateValue") < source.indexOf("wrapperDiv.innerHTML = result.message"));
});

test("DOB blur performs age-block, warning, then duplicate checks", () => {
    assert.match(markup, /AddEventHandler\(birthdate, "blur", \(e\) => \{ handleBirthdateChanged\(\); \}\)/);

    const handleBirthdateChanged = markup.match(/async function handleBirthdateChanged\(\)[\s\S]*?\r?\n    \}\r?\n\r?\n    function show/)[0];
    assert.match(handleBirthdateChanged, /const status = await ageBlockCheck\(\);/);
    assert.match(handleBirthdateChanged, /if \(status === "blocked" \|\| status === "stale"\) \{\s*return;/);
    assert.match(handleBirthdateChanged, /ageCheck\(\);/);
    assert.match(handleBirthdateChanged, /dupecheck\(\);/);
    assert.ok(handleBirthdateChanged.indexOf("await ageBlockCheck()") < handleBirthdateChanged.indexOf("ageCheck()"));
    assert.ok(handleBirthdateChanged.indexOf("ageCheck()") < handleBirthdateChanged.indexOf("dupecheck()"));
});

test("submit rechecks age block before registration POST", () => {
    const submit = markup.match(/async function submitReg\(e\)[\s\S]*?\r?\n    \}\r?\n\r?\n    function handleNotificationPreferenceChange/)[0];
    assert.match(submit, /const ageBlockStatus = await ageBlockCheck\(\);/);
    assert.match(submit, /if \(ageBlockStatus === "blocked" \|\| ageBlockStatus === "stale"\) \{[\s\S]*?registerButton\.disabled = false;[\s\S]*?return;/);
    assert.match(submit, /const data = await postData\("@this\.BuildAction\("Submit"\)", formData\);/);
    assert.ok(submit.indexOf("await ageBlockCheck()") < submit.indexOf("await postData"));
});

test("driver-license DOB uses the same age-block, warning, duplicate pipeline", () => {
    const driverLicense = markup.match(/async function dl\(\)[\s\S]*?\r?\n    \}\r?\n\r?\n    function dupecheck/)[0];
    assert.match(driverLicense, /const data = await postData\("@this\.BuildAction\("dl"\)"/);
    assert.match(driverLicense, /birthdate\.value = data\.birthdate/);
    assert.match(driverLicense, /await handleBirthdateChanged\(\);/);
    assert.ok(driverLicense.indexOf("await handleBirthdateChanged()") > driverLicense.indexOf("birthdate.value = data.birthdate"));
    assert.doesNotMatch(driverLicense, /\n        dupecheck\(\);/);
});

function deferred() {
    let resolve;
    const promise = new Promise(value => { resolve = value; });
    return { promise, resolve };
}

function ageBlockResponse(isBlocked, message = "Underage") {
    return {
        ok: true,
        json: async () => ({ isBlocked, message })
    };
}

function createAgeBlockHarness(fetchImplementation = null) {
    const requests = [];
    const sandbox = {
        birthdate: { value: "A" },
        antiforgeryToken: { value: "token" },
        wrapperDiv: { innerHTML: "form" },
        window: {
            scrolls: 0,
            scrollTo() { this.scrolls++; }
        },
        FormData: class {
            append() { }
        },
        fetch: fetchImplementation ?? (() => {
            const request = deferred();
            requests.push(request);
            return request.promise;
        })
    };

    const ageBlockCheck = markup.match(/(async function ageBlockCheck\(\) \{[\s\S]*?\n    \})(?=\r?\n\r?\n    async function handleBirthdateChanged)/)[1];
    vm.runInNewContext(`
        let ageBlockRequestId = 0;
        ${ageBlockCheck}
        globalThis.ageBlockCheck = ageBlockCheck;
    `, sandbox);

    return {
        check: sandbox.ageBlockCheck,
        birthdate: sandbox.birthdate,
        wrapperDiv: sandbox.wrapperDiv,
        window: sandbox.window,
        requests
    };
}

test("non-success and network failures continue as allowed without mutating the form", async () => {
    const nonSuccess = createAgeBlockHarness(() => Promise.resolve({ ok: false }));
    assert.equal(await nonSuccess.check(), "allowed");
    assert.equal(nonSuccess.wrapperDiv.innerHTML, "form");
    assert.equal(nonSuccess.window.scrolls, 0);

    const networkFailure = createAgeBlockHarness(() => Promise.reject(new Error("offline")));
    assert.equal(await networkFailure.check(), "allowed");
    assert.equal(networkFailure.wrapperDiv.innerHTML, "form");
    assert.equal(networkFailure.window.scrolls, 0);
});

test("the newest blocked response replaces the form and scrolls to the top", async () => {
    const harness = createAgeBlockHarness(() => Promise.resolve(ageBlockResponse(true, "Configured block")));

    assert.equal(await harness.check(), "blocked");
    assert.equal(harness.wrapperDiv.innerHTML, "Configured block");
    assert.equal(harness.window.scrolls, 1);
});

function runBirthdatePipeline(harness, effects) {
    return harness.check().then(status => {
        if (status === "blocked" || status === "stale") {
            return status;
        }

        effects.ageWarnings++;
        effects.dupeChecks++;
        return status;
    });
}

test("a stale blocked response cannot mutate the form or continue the older pipeline", async () => {
    const harness = createAgeBlockHarness();
    const effects = { ageWarnings: 0, dupeChecks: 0 };

    const pipelineA = runBirthdatePipeline(harness, effects);
    harness.birthdate.value = "B";
    const pipelineB = runBirthdatePipeline(harness, effects);

    harness.requests[1].resolve(ageBlockResponse(false));
    assert.equal(await pipelineB, "allowed");

    harness.requests[0].resolve(ageBlockResponse(true));
    assert.equal(await pipelineA, "stale");
    assert.equal(harness.wrapperDiv.innerHTML, "form");
    assert.equal(harness.window.scrolls, 0);
    assert.deepEqual(effects, { ageWarnings: 1, dupeChecks: 1 });
});

test("an older response is stale if the DOB changes while it is outstanding", async () => {
    const harness = createAgeBlockHarness();
    const effects = { ageWarnings: 0, dupeChecks: 0 };

    const pipelineA = runBirthdatePipeline(harness, effects);
    harness.birthdate.value = "B";
    const pipelineB = runBirthdatePipeline(harness, effects);

    harness.requests[0].resolve(ageBlockResponse(true));
    assert.equal(await pipelineA, "stale");
    assert.equal(harness.wrapperDiv.innerHTML, "form");
    assert.equal(harness.window.scrolls, 0);
    assert.deepEqual(effects, { ageWarnings: 0, dupeChecks: 0 });

    harness.requests[1].resolve(ageBlockResponse(false));
    assert.equal(await pipelineB, "allowed");
    assert.deepEqual(effects, { ageWarnings: 1, dupeChecks: 1 });
});
