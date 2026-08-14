import { strict as assert } from "node:assert";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import * as vm from "node:vm";

const markup = readFileSync(new URL("../../Clc.PatronRegistration.Web/Views/Registration/Create.cshtml", import.meta.url), "utf8");

test("registration view uses live and preview age-block endpoints", () => {
    assert.match(markup, /var ageBlockCheckUrl = isSettingsPreview[\s\S]*?Url\.Action\("AgeBlockCheck", "Preview", new \{ token = previewToken \}\)[\s\S]*?this\.BuildAction\("AgeBlockCheck"\)/);
    assert.doesNotMatch(markup, /function calculateAge\(/);
    assert.doesNotMatch(markup, /@Settings\.EnableAgeBlock/);
    assert.doesNotMatch(markup, /AgeBlockRequestCoordinator|age-block-request-coordinator/);
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

test("birthdate, driver-license, and submit workflows honor age-block status", () => {
    assert.match(markup, /AddEventHandler\(birthdate, "blur", \(e\) => \{ handleBirthdateChanged\(\); \}\)/);

    const handleBirthdateChanged = markup.match(/async function handleBirthdateChanged\(\)[\s\S]*?\r?\n    \}\r?\n\r?\n    function show/)[0];
    assert.match(handleBirthdateChanged, /const status = await ageBlockCheck\(\);/);
    assert.match(handleBirthdateChanged, /if \(status === "blocked" \|\| status === "stale"\) \{\s*return;/);
    assert.match(handleBirthdateChanged, /ageCheck\(\);/);
    assert.match(handleBirthdateChanged, /dupecheck\(\);/);
    assert.doesNotMatch(handleBirthdateChanged, /await dupecheck\(\);/);

    const submit = markup.match(/async function submitReg\(e\)[\s\S]*?\r?\n    \}\r?\n\r?\n    function handleNotificationPreferenceChange/)[0];
    assert.match(submit, /const ageBlockStatus = await ageBlockCheck\(\);/);
    assert.match(submit, /if \(ageBlockStatus === "blocked" \|\| ageBlockStatus === "stale"\) \{[\s\S]*?return;/);
    assert.match(submit, /const data = await postData\("@submitUrl", formData\);/);
    assert.doesNotMatch(submit, /postData\("@submitUrl", formData\)\.then/);

    const driverLicense = markup.match(/async function dl\(\)[\s\S]*?\r?\n    \}\r?\n\r?\n    function dupecheck/)[0];
    assert.match(driverLicense, /const data = await postData\("@driverLicenseUrl"/);
    assert.match(driverLicense, /birthdate\.value = data\.birthdate/);
    assert.match(driverLicense, /await handleBirthdateChanged\(\);/);
    assert.ok(driverLicense.indexOf("await handleBirthdateChanged()") > driverLicense.indexOf("birthdate.value = data.birthdate"));
    assert.doesNotMatch(driverLicense, /\n        dupecheck\(\);/);
    assert.doesNotMatch(markup, /const state = q\('#State'\)|state\.value = data\.state/);
    assert.doesNotMatch(markup, /const gender = q\('#Gender'\)|gender\.value = data\.gender/);
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

function createAgeBlockHarness() {
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
        fetch: () => {
            const request = deferred();
            requests.push(request);
            return request.promise;
        }
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

test("a response resolved after the DOB changes is stale even before the newer response", async () => {
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
