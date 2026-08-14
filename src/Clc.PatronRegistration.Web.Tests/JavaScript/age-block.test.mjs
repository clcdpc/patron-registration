import { strict as assert } from "node:assert";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import * as vm from "node:vm";

const markup = readFileSync(new URL("../../Clc.PatronRegistration.Web/Views/Registration/Create.cshtml", import.meta.url), "utf8");
const coordinatorSource = readFileSync(new URL("../../Clc.PatronRegistration.Web/wwwroot/js/age-block-request-coordinator.js", import.meta.url), "utf8");

test("registration view uses live and preview age-block endpoints", () => {
    assert.match(markup, /var ageBlockCheckUrl = isSettingsPreview[\s\S]*?Url\.Action\("AgeBlockCheck", "Preview", new \{ token = previewToken \}\)[\s\S]*?this\.BuildAction\("AgeBlockCheck"\)/);
    assert.match(markup, /async function ageBlockCheck\(\)/);
    assert.match(markup, /if \(!checkedBirthdate\)[\s\S]*?return false;/);
    assert.match(markup, /formData\.append\("Birthdate", birthdateValue\)/);
    assert.match(markup, /credentials: "same-origin"/);
    assert.match(markup, /AgeBlockRequestCoordinator\.create/);
    assert.match(markup, /wrapperDiv\.innerHTML = result\.message/);
    assert.match(markup, /return result;/);
    assert.match(markup, /status === "blocked"/);
    assert.doesNotMatch(markup, /function calculateAge\(/);
    assert.doesNotMatch(markup, /@Settings\.EnableAgeBlock/);
});

test("birthdate and driver-license workflows share the age-block path", () => {
    assert.match(markup, /AddEventHandler\(birthdate, "blur", \(e\) => \{ handleBirthdateChanged\(\); \}\)/);
    assert.match(markup, /async function handleBirthdateChanged\(\)[\s\S]*?const ageBlockResult = await ageBlockCheck\(\);[\s\S]*?await dupecheck\(\);/);
    assert.match(markup, /async function handleBirthdateChanged\(\)[\s\S]*?status === "stale"[\s\S]*?status === "blocked"/);
    assert.match(markup, /async function submitReg\(e\)[\s\S]*?const ageBlockResult = await ageBlockCheck\(\)[\s\S]*?status === "stale"/);

    const driverLicense = markup.match(/async function dl\(\)[\s\S]*?\n    \}\n\n    async function dupecheck/)[0];
    assert.match(driverLicense, /const data = await postData\("@driverLicenseUrl"/);
    assert.match(driverLicense, /birthdate\.value = data\.birthdate/);
    assert.match(driverLicense, /await handleBirthdateChanged\(\);/);
    assert.ok(driverLicense.indexOf("await handleBirthdateChanged()") > driverLicense.indexOf("birthdate.value = data.birthdate"));
    assert.doesNotMatch(driverLicense, /\n        dupecheck\(\);/);
});

function createCoordinator(getCurrentValue) {
    const sandbox = { globalThis: null };
    sandbox.globalThis = sandbox;
    vm.runInNewContext(coordinatorSource, sandbox);
    return sandbox.AgeBlockRequestCoordinator.create(getCurrentValue);
}

function deferred() {
    let resolve;
    const promise = new Promise(value => { resolve = value; });
    return { promise, resolve };
}

function runBirthdatePipeline(coordinator, currentBirthdate, request, ui) {
    return coordinator.request(currentBirthdate, () => request).then(result => {
        if (result.status === "blocked") {
            ui.wrapper = result.message;
            ui.scrolls++;
            return result;
        }
        if (result.status === "stale") {
            return result;
        }
        if (result.status === "allowed") {
            ui.ageWarnings++;
            ui.dupeChecks++;
        }
        return result;
    });
}

test("stale blocked response cannot mutate the form or continue the older pipeline", async () => {
    const current = { value: "A" };
    const ui = { wrapper: "form", scrolls: 0, ageWarnings: 0, dupeChecks: 0 };
    const coordinator = createCoordinator(() => current.value);
    const requestA = deferred();
    const requestB = deferred();

    const pipelineA = runBirthdatePipeline(coordinator, "A", requestA.promise, ui);
    current.value = "B";
    const pipelineB = runBirthdatePipeline(coordinator, "B", requestB.promise, ui);

    requestB.resolve({ status: "allowed" });
    assert.deepEqual(await pipelineB, { status: "allowed" });

    requestA.resolve({ status: "blocked", message: "A is underage" });
    assert.equal((await pipelineA).status, "stale");
    assert.deepEqual(ui, { wrapper: "form", scrolls: 0, ageWarnings: 1, dupeChecks: 1 });
});

test("a response that resolves before the newer request is still stale", async () => {
    const current = { value: "A" };
    const ui = { wrapper: "form", scrolls: 0, ageWarnings: 0, dupeChecks: 0 };
    const coordinator = createCoordinator(() => current.value);
    const requestA = deferred();
    const requestB = deferred();

    const pipelineA = runBirthdatePipeline(coordinator, "A", requestA.promise, ui);
    current.value = "B";
    const pipelineB = runBirthdatePipeline(coordinator, "B", requestB.promise, ui);

    requestA.resolve({ status: "blocked", message: "A is underage" });
    assert.equal((await pipelineA).status, "stale");
    assert.deepEqual(ui, { wrapper: "form", scrolls: 0, ageWarnings: 0, dupeChecks: 0 });

    requestB.resolve({ status: "allowed" });
    assert.deepEqual(await pipelineB, { status: "allowed" });
    assert.deepEqual(ui, { wrapper: "form", scrolls: 0, ageWarnings: 1, dupeChecks: 1 });
});
