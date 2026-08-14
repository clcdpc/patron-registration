import { strict as assert } from "node:assert";
import { readFileSync } from "node:fs";
import { test } from "node:test";

const markup = readFileSync(new URL("../../Clc.PatronRegistration.Web/Views/Registration/Create.cshtml", import.meta.url), "utf8");

test("registration view uses live and preview age-block endpoints", () => {
    assert.match(markup, /var ageBlockCheckUrl = isSettingsPreview[\s\S]*?Url\.Action\("AgeBlockCheck", "Preview", new \{ token = previewToken \}\)[\s\S]*?this\.BuildAction\("AgeBlockCheck"\)/);
    assert.doesNotMatch(markup, /function calculateAge\(/);
    assert.doesNotMatch(markup, /@Settings\.EnableAgeBlock/);
    assert.doesNotMatch(markup, /AgeBlockRequestCoordinator|age-block-request-coordinator/);
});

test("age-block checks reject stale responses before applying them", () => {
    assert.match(markup, /let ageBlockRequestId = 0;/);

    const ageBlockCheck = markup.match(/async function ageBlockCheck\(\) \{[\s\S]*?\n    \}\n\n    async function handleBirthdateChanged/);
    assert.ok(ageBlockCheck);
    const source = ageBlockCheck[0];

    assert.match(source, /const requestId = \+\+ageBlockRequestId;/);
    assert.match(source, /const birthdateValue = birthdate\.value;/);
    assert.match(source, /if \(!birthdateValue\) \{\s*return "allowed";/);
    assert.match(source, /formData\.append\("Birthdate", birthdateValue\)/);
    assert.match(source, /method: "POST"/);
    assert.match(source, /credentials: "same-origin"/);
    assert.match(source, /const result = response\.ok \? await response\.json\(\) : null;/);
    assert.match(source, /if \(requestId !== ageBlockRequestId \|\| birthdate\.value !== birthdateValue\) \{\s*return "stale";/);
    assert.match(source, /if \(!result\) \{\s*return "unavailable";/);
    assert.match(source, /if \(result\.isBlocked\) \{[\s\S]*?wrapperDiv\.innerHTML = result\.message[\s\S]*?return "blocked";/);
    assert.match(source, /catch \{[\s\S]*?requestId !== ageBlockRequestId \|\| birthdate\.value !== birthdateValue[\s\S]*?"stale"[\s\S]*?"unavailable"/);

    assert.ok(source.indexOf('return "stale"') < source.indexOf("if (result.isBlocked)"));
    assert.ok(source.indexOf("birthdate.value !== birthdateValue") < source.indexOf("wrapperDiv.innerHTML = result.message"));
});

test("birthdate, driver-license, and submit workflows honor age-block status", () => {
    assert.match(markup, /AddEventHandler\(birthdate, "blur", \(e\) => \{ handleBirthdateChanged\(\); \}\)/);

    const handleBirthdateChanged = markup.match(/async function handleBirthdateChanged\(\)[\s\S]*?\r?\n    \}\r?\n\r?\n    function show/)[0];
    assert.match(handleBirthdateChanged, /const status = await ageBlockCheck\(\);/);
    assert.match(handleBirthdateChanged, /if \(status === "blocked" \|\| status === "stale"\) \{\s*return;/);
    assert.match(handleBirthdateChanged, /ageCheck\(\);/);
    assert.match(handleBirthdateChanged, /await dupecheck\(\);/);

    const submit = markup.match(/async function submitReg\(e\)[\s\S]*?\r?\n    \}\r?\n\r?\n    function handleNotificationPreferenceChange/)[0];
    assert.match(submit, /const ageBlockStatus = await ageBlockCheck\(\);/);
    assert.match(submit, /if \(ageBlockStatus === "blocked" \|\| ageBlockStatus === "stale"\) \{[\s\S]*?return;/);

    const driverLicense = markup.match(/async function dl\(\)[\s\S]*?\r?\n    \}\r?\n\r?\n    async function dupecheck/)[0];
    assert.match(driverLicense, /const data = await postData\("@driverLicenseUrl"/);
    assert.match(driverLicense, /birthdate\.value = data\.birthdate/);
    assert.match(driverLicense, /await handleBirthdateChanged\(\);/);
    assert.ok(driverLicense.indexOf("await handleBirthdateChanged()") > driverLicense.indexOf("birthdate.value = data.birthdate"));
    assert.doesNotMatch(driverLicense, /\n        dupecheck\(\);/);
    assert.doesNotMatch(markup, /const state = q\('#State'\)|state\.value = data\.state/);
    assert.doesNotMatch(markup, /const gender = q\('#Gender'\)|gender\.value = data\.gender/);
});
