import { strict as assert } from "node:assert";
import { readFileSync } from "node:fs";
import { test } from "node:test";

const markup = readFileSync(new URL("../../Clc.PatronRegistration.Web/Views/Registration/Create.cshtml", import.meta.url), "utf8");

test("registration view uses live and preview age-block endpoints", () => {
    assert.match(markup, /var ageBlockCheckUrl = isSettingsPreview[\s\S]*?Url\.Action\("AgeBlockCheck", "Preview", new \{ token = previewToken \}\)[\s\S]*?this\.BuildAction\("AgeBlockCheck"\)/);
    assert.match(markup, /async function ageBlockCheck\(\)/);
    assert.match(markup, /formData\.append\("Birthdate", birthdate\.value\)/);
    assert.match(markup, /credentials: "same-origin"/);
    assert.match(markup, /wrapperDiv\.innerHTML = result\.message/);
    assert.match(markup, /return true;/);
    assert.doesNotMatch(markup, /function calculateAge\(/);
    assert.doesNotMatch(markup, /@Settings\.EnableAgeBlock/);
});

test("birthdate and driver-license workflows share the age-block path", () => {
    assert.match(markup, /AddEventHandler\(birthdate, "blur", \(e\) => \{ handleBirthdateChanged\(\); \}\)/);
    assert.match(markup, /async function handleBirthdateChanged\(\)[\s\S]*?const blocked = await ageBlockCheck\(\);[\s\S]*?await dupecheck\(\);/);
    assert.match(markup, /async function submitReg\(e\)[\s\S]*?if \(await ageBlockCheck\(\)\)/);

    const driverLicense = markup.match(/async function dl\(\)[\s\S]*?\n    \}\n\n    async function dupecheck/)[0];
    assert.match(driverLicense, /const data = await postData\("@driverLicenseUrl"/);
    assert.match(driverLicense, /birthdate\.value = data\.birthdate/);
    assert.match(driverLicense, /await handleBirthdateChanged\(\);/);
    assert.ok(driverLicense.indexOf("await handleBirthdateChanged()") > driverLicense.indexOf("birthdate.value = data.birthdate"));
    assert.doesNotMatch(driverLicense, /\n        dupecheck\(\);/);
});
