import { expect, test, type Page } from '@playwright/test';
import { readFileSync } from 'node:fs';
import path from 'node:path';

const repositoryRoot = path.resolve(__dirname, '..', '..', '..');
const fixturePath = path.join(__dirname, 'fixtures', 'settings-editor.html');
const siteCssPath = path.join(repositoryRoot, 'src', 'Clc.PatronRegistration.Web', 'wwwroot', 'css', 'site.css');
const settingsCssPath = path.join(repositoryRoot, 'src', 'Clc.PatronRegistration.Web', 'wwwroot', 'css', 'settings.css');
const settingsScriptPath = path.join(repositoryRoot, 'src', 'Clc.PatronRegistration.Web', 'wwwroot', 'js', 'settings.js');

async function loadSettingsFixture(page: Page) {
    const markup = readFileSync(fixturePath, 'utf8')
        .replace(/\s*<link rel="stylesheet"[^>]+>\s*/g, '')
        .replace(/\s*<script src="[^"]*settings\.js[^"]*"><\/script>\s*/g, '');
    await page.setContent(markup, { waitUntil: 'domcontentloaded' });
    await page.addStyleTag({ path: siteCssPath });
    await page.addStyleTag({ path: settingsCssPath });
    await page.addScriptTag({ path: settingsScriptPath });
}

const rowFor = (page: Page, key: string) => page.locator(`[data-setting-key="${key}"]`);
const dirtyRows = (page: Page) => page.locator('.setting-row[data-dirty="true"]');

async function discardChanges(page: Page, count: number) {
    await page.getByRole('button', { name: 'Discard unsaved changes' }).click();
    const dialog = page.locator('#unsaved-changes-dialog');
    await expect(dialog).toBeVisible();
    await expect(dialog.locator('[data-guard-discard]')).toHaveText(`Discard ${count} browser ${count === 1 ? 'change' : 'changes'}`);
    await dialog.locator('[data-guard-discard]').click();
    await expect(dialog).toBeHidden();
}

test.beforeEach(async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await loadSettingsFixture(page);
});

test('the loaded editor is locked and contains only explicit actions', async ({ page }) => {
    await expect(dirtyRows(page)).toHaveCount(0);
    await expect(page.locator('.settings-actions')).toBeHidden();
    await expect(page.locator('.setting-mode, .setting-mode-group, .image-mode-group, .batch-mode-group')).toHaveCount(0);
    await expect(page.locator('.setting-row .setting-change')).toHaveCount(24);
    await expect(page.locator('.setting-row .setting-revert')).toHaveCount(24);

    for (const selector of ['#welcome-value', '#custom-heading-value', '#reset-value', '#welcome-email-value', '#registration-uri-value', '#tax-value', '#button-value', '#secret-value', '#label-name-first-value']) {
        await expect(page.locator(selector)).toHaveAttribute('readonly', '');
    }
    for (const selector of ['#expiration-value', '#optional-expiration-value', '#input-type-value', '#show-age-warning-yes', '#show-age-warning-no', '#require-email-yes', '#require-email-no']) {
        await expect(page.locator(selector)).toBeDisabled();
    }
    await expect(rowFor(page, 'drivers_license_button_text').getByRole('button', { name: 'Revert to inherited value' })).toBeHidden();
    await expect(rowFor(page, 'age_warning_text').getByRole('button', { name: 'Revert to inherited value' })).toBeHidden();
    await expect(rowFor(page, 'custom_heading').getByRole('button', { name: 'Revert to inherited value' })).toBeVisible();
    await expect(rowFor(page, 'no_inherited_value').getByRole('button', { name: 'Remove customization' })).toBeVisible();
    await expect(rowFor(page, 'header_image').getByRole('button', { name: 'Change image' })).toBeVisible();
});

test('inherited Change seeds an Upsert, focuses the editor, and Revert returns clean', async ({ page }) => {
    const row = rowFor(page, 'drivers_license_button_text');
    const value = row.locator('#button-value');

    await row.getByRole('button', { name: 'Change' }).click();
    await expect(value).toBeEditable();
    await expect(value).toBeFocused();
    await expect(row).toHaveAttribute('data-dirty', 'true');
    await expect(row.locator('.operation')).toHaveValue('Upsert');
    await expect(row.locator('.setting-value-binding')).toHaveValue('Scan ID');
    await expect(row.getByRole('button', { name: 'Revert to inherited value' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Review 1 change' })).toBeVisible();

    await row.getByRole('button', { name: 'Revert to inherited value' }).click();
    await expect(row.getByRole('button', { name: 'Change' })).toBeFocused();
    await expect(value).toHaveAttribute('readonly', '');
    await expect(value).toHaveValue('Scan ID');
    await expect(row).toHaveAttribute('data-dirty', 'false');
    await expect(row.locator('.operation')).toBeDisabled();
    await expect(page.locator('.settings-actions')).toBeHidden();
});

test('customized Change only activates editing, while value changes become dirty Upserts', async ({ page }) => {
    const row = rowFor(page, 'custom_heading');
    const value = row.locator('#custom-heading-value');

    await row.getByRole('button', { name: 'Change' }).click();
    await expect(value).toBeFocused();
    await expect(value).toBeEditable();
    await expect(row).toHaveAttribute('data-dirty', 'false');
    await expect(page.locator('.settings-actions')).toBeHidden();

    await value.fill('Changed heading');
    await expect(row).toHaveAttribute('data-dirty', 'true');
    await expect(row.locator('.operation')).toHaveValue('Upsert');
    await expect(page.getByRole('button', { name: 'Review 1 change' })).toBeVisible();
    await value.fill('Welcome');
    await expect(row).toHaveAttribute('data-dirty', 'false');
    await expect(page.locator('.settings-actions')).toBeHidden();
});

test('Revert independently creates RemoveOverride and review explains inheritance', async ({ page }) => {
    const row = rowFor(page, 'custom_heading');
    const value = row.locator('#custom-heading-value');

    await row.getByRole('button', { name: 'Revert to inherited value' }).click();
    await expect(row).toHaveAttribute('data-dirty', 'true');
    await expect(row.locator('.operation')).toHaveValue('RemoveOverride');
    await expect(row.locator('.setting-value-binding')).toBeEnabled();
    await expect(value).toHaveValue('Inherited heading');
    await expect(value).toHaveAttribute('readonly', '');

    await page.getByRole('button', { name: 'Review 1 change' }).click();
    const dialog = page.locator('#save-confirm');
    await expect(dialog).toBeVisible();
    await expect(dialog.locator('tbody')).toContainText('Use Inherited heading from Main Library');
    await expect(dialog.locator('tbody .review-pending-column')).not.toContainText('Welcome');
    await dialog.getByRole('button', { name: 'Close' }).click();
});

test('blank customization remains an Upsert until the user explicitly Reverts', async ({ page }) => {
    const row = rowFor(page, 'age_warning_text');
    const value = row.locator('#age-warning-value');

    await expect(value).toHaveValue('');
    await expect(value).toHaveAttribute('readonly', '');
    await row.getByRole('button', { name: 'Change' }).click();
    await value.fill('');
    await expect(row).toHaveAttribute('data-dirty', 'true');
    await expect(row.locator('.operation')).toHaveValue('Upsert');
    await expect(row.locator('.setting-value-binding')).toHaveValue('');
    await page.getByRole('button', { name: 'Review 1 change' }).click();
    await expect(page.locator('#save-confirm tbody')).toContainText('Customize here: Blank');
    await page.getByRole('button', { name: 'Close' }).click();

    await row.getByRole('button', { name: 'Revert to inherited value' }).click();
    await expect(row).toHaveAttribute('data-dirty', 'false');
    await expect(value).toHaveValue('');
    await expect(value).toHaveAttribute('readonly', '');
});

test('Boolean and batch required controls are value radios, enabled only by Change', async ({ page }) => {
    const boolean = rowFor(page, 'show_age_warning');
    const required = rowFor(page, 'require.EmailAddress');

    await expect(boolean.locator('.boolean-value')).toHaveCount(2);
    await expect(boolean.locator('.boolean-value').first()).toBeDisabled();
    await expect(boolean.locator('.boolean-value').nth(1)).toBeDisabled();
    await expect(required.locator('.batch-value-choice')).toHaveCount(2);
    await expect(required.locator('.batch-value-choice').first()).toBeDisabled();
    await expect(required.locator('.batch-value-choice').nth(1)).toBeDisabled();
    await expect(boolean.getByRole('radio', { name: 'Yes', exact: true })).toBeVisible();
    await expect(boolean.getByRole('radio', { name: 'No', exact: true })).toBeVisible();
    const radioMetrics = await boolean.getByRole('radio', { name: 'Yes', exact: true }).evaluate((input) => {
        const style = getComputedStyle(input);
        return { width: Number.parseFloat(style.width), minHeight: Number.parseFloat(style.minHeight) };
    });
    expect(radioMetrics.width).toBeLessThan(40);
    expect(radioMetrics.minHeight).toBeLessThan(40);
    await expect(required.getByRole('radio', { name: 'Required', exact: true })).toBeVisible();
    await expect(required.getByRole('radio', { name: 'Optional', exact: true })).toBeVisible();

    await boolean.getByRole('button', { name: 'Change' }).click();
    await expect(boolean.getByRole('radio', { name: 'Yes', exact: true })).toBeEnabled();
    await boolean.getByRole('radio', { name: 'Yes', exact: true }).check();
    await expect(boolean).toHaveAttribute('data-dirty', 'true');
    await expect(boolean.locator('.operation')).toHaveValue('Upsert');

    await required.getByRole('button', { name: 'Change' }).click();
    await expect(required.getByRole('radio', { name: 'Required', exact: true })).toBeEnabled();
    await required.getByRole('radio', { name: 'Required', exact: true }).check();
    await expect(required).toHaveAttribute('data-dirty', 'true');
    await expect(required.locator('.operation')).toHaveValue('Upsert');
});

test('special editor Revert uses inherited values and relocks controls', async ({ page }) => {
    const boolean = rowFor(page, 'show_age_warning');
    const label = rowFor(page, 'label.NameFirst');
    const required = rowFor(page, 'require.EmailAddress');

    await boolean.getByRole('button', { name: 'Revert to inherited value' }).click();
    await expect(boolean).toHaveAttribute('data-dirty', 'true');
    await expect(boolean.locator('.operation')).toHaveValue('RemoveOverride');
    await expect(boolean.getByRole('radio', { name: 'Yes', exact: true })).toBeChecked();
    await expect(boolean.getByRole('radio', { name: 'Yes', exact: true })).toBeDisabled();
    await expect(boolean.getByRole('radio', { name: 'No', exact: true })).toBeDisabled();

    await label.getByRole('button', { name: 'Revert to inherited value' }).click();
    await expect(label).toHaveAttribute('data-dirty', 'true');
    await expect(label.locator('.operation')).toHaveValue('RemoveOverride');
    await expect(label.locator('.setting-value')).toHaveValue('Given name');
    await expect(label.locator('.setting-value')).toHaveAttribute('readonly', '');

    await required.getByRole('button', { name: 'Change' }).click();
    await expect(required.getByRole('radio', { name: 'Required', exact: true })).toBeEnabled();
    await required.getByRole('radio', { name: 'Required', exact: true }).check();
    await expect(required).toHaveAttribute('data-dirty', 'true');
    await required.getByRole('button', { name: 'Revert to inherited value' }).click();
    await expect(required).toHaveAttribute('data-dirty', 'false');
    await expect(required.locator('.operation')).toHaveValue('RemoveOverride');
    await expect(required.getByRole('radio', { name: 'Required', exact: true })).toBeDisabled();
    await expect(required.getByRole('radio', { name: 'Optional', exact: true })).toBeDisabled();
    await expect(required.getByRole('radio', { name: 'Optional', exact: true })).toBeChecked();
    await expect(required.locator('.setting-scope-status')).toHaveText('Inherited');
    await expect(page.locator('.settings-actions')).toBeVisible();

    await discardChanges(page, 2);
    await expect(page.locator('.settings-actions')).toBeHidden();
});

test('shared-draft rows use the draft as browser baseline and discard restores it', async ({ page }) => {
    const upsert = rowFor(page, 'custom_form_footer_html');
    const remove = rowFor(page, 'shared_draft_remove_override');

    await upsert.getByRole('button', { name: 'Change' }).click();
    await expect(upsert).toHaveAttribute('data-dirty', 'false');
    await upsert.locator('#footer-value').fill('<p>Browser draft</p>');
    await expect(upsert).toHaveAttribute('data-dirty', 'true');

    await remove.getByRole('button', { name: 'Change' }).click();
    await expect(remove).toHaveAttribute('data-dirty', 'true');
    await expect(remove.locator('.operation')).toHaveValue('Upsert');
    await expect(remove.locator('.setting-value-binding')).toHaveValue('Inherited heading');
    await expect(dirtyRows(page)).toHaveCount(2);

    await discardChanges(page, 2);
    await expect(dirtyRows(page)).toHaveCount(0);
    await expect(upsert.locator('#footer-value')).toHaveValue('<p>Draft line one</p>\n<p>Draft line two</p>');
    await expect(remove.locator('#shared-draft-remove-value')).toHaveValue('Inherited heading');
    await expect(upsert.locator('.setting-status')).toHaveText('Shared draft');
    await expect(remove.locator('.setting-status')).toHaveText('Shared draft');
});

test('shared-draft baselines preserve Revert and Change transitions', async ({ page }) => {
    const upsert = rowFor(page, 'custom_form_footer_html');
    const remove = rowFor(page, 'shared_draft_remove_override');

    await upsert.getByRole('button', { name: 'Revert to inherited value' }).click();
    await expect(upsert).toHaveAttribute('data-dirty', 'true');
    await expect(upsert.locator('.operation')).toHaveValue('RemoveOverride');
    await expect(upsert.locator('.setting-value-binding')).toBeEnabled();

    await discardChanges(page, 1);
    await expect(upsert).toHaveAttribute('data-dirty', 'false');

    await remove.getByRole('button', { name: 'Change' }).click();
    await expect(remove).toHaveAttribute('data-dirty', 'true');
    await expect(remove.locator('.operation')).toHaveValue('Upsert');
    await remove.getByRole('button', { name: 'Revert to inherited value' }).click();
    await expect(remove).toHaveAttribute('data-dirty', 'false');
    await expect(remove.locator('.operation')).toHaveValue('RemoveOverride');
    await expect(remove.locator('.setting-value-binding')).toBeDisabled();
    await expect(remove.locator('.setting-scope-status')).toHaveText('Shared draft');
    await expect(page.locator('.settings-actions')).toBeHidden();
});

test('sensitive Change exposes only an empty replacement and review never exposes it', async ({ page }) => {
    const row = rowFor(page, 'api_password');
    const value = row.locator('#secret-value');

    await expect(value).toHaveValue('');
    await expect(value).toHaveAttribute('readonly', '');
    await row.getByRole('button', { name: 'Change' }).click();
    await expect(value).toBeFocused();
    await expect(value).toBeEditable();
    await expect(value).toHaveValue('');
    await expect(row).toHaveAttribute('data-dirty', 'false');

    await value.fill('browser-only-secret');
    await expect(row).toHaveAttribute('data-dirty', 'true');
    await page.getByRole('button', { name: 'Review 1 change' }).click();
    const review = page.locator('#save-confirm');
    await expect(review).toContainText('Replacement entered');
    await expect(review).not.toContainText('browser-only-secret');
    await review.getByRole('button', { name: 'Close' }).click();

    await discardChanges(page, 1);
    await expect(value).toHaveValue('');
    await expect(value).toHaveAttribute('readonly', '');
    await expect(value).toHaveAttribute('type', 'password');
    await expect(row).toHaveAttribute('data-dirty', 'false');
});

test('IP prefixes remain readable while locked and discard restores every row', async ({ page }) => {
    const row = rowFor(page, 'show_dl_ips');
    const prefixes = row.locator('.ip-prefix-input');
    const loaded = await prefixes.evaluateAll((inputs) => inputs.map((input) => (input as HTMLInputElement).value));

    await expect(prefixes).toHaveCount(2);
    await expect(prefixes.first()).toHaveAttribute('readonly', '');
    await expect(prefixes.nth(1)).toHaveAttribute('readonly', '');
    await expect(row.locator('.ip-prefix-add')).toBeDisabled();
    await row.getByRole('button', { name: 'Change' }).click();
    await expect(prefixes.first()).toBeFocused();
    await expect(prefixes.first()).not.toHaveAttribute('readonly', '');
    await expect(prefixes.nth(1)).not.toHaveAttribute('readonly', '');
    await row.locator('.ip-prefix-add').click();
    await expect(prefixes).toHaveCount(3);
    await prefixes.last().fill('172.16.');
    await expect(row.locator('.setting-value-binding')).toHaveValue('10.;192.168.;172.16.');
    await expect(row).toHaveAttribute('data-dirty', 'true');

    await discardChanges(page, 1);
    await expect(row.locator('.ip-prefix-input')).toHaveCount(loaded.length);
    const restored = await row.locator('.ip-prefix-input').evaluateAll((inputs) => inputs.map((input) => (input as HTMLInputElement).value));
    expect(restored).toEqual(loaded);
    await expect(row).toHaveAttribute('data-dirty', 'false');
    await expect(row.locator('.ip-prefix-add')).toBeDisabled();
});

test('HTML and plain-text previews follow editor changes and discard', async ({ page }) => {
    const html = rowFor(page, 'custom_form_footer_html');
    const plain = rowFor(page, 'welcome_email_template_text');
    const htmlValue = html.locator('#footer-value');
    const plainValue = plain.locator('#welcome-value');
    const htmlPreview = html.locator('.html-preview');
    const plainPreview = plain.locator('.plain-text-preview');
    const loadedHtml = await htmlValue.inputValue();
    const loadedPlain = await plainValue.inputValue();

    await html.getByRole('button', { name: 'Change' }).click();
    await plain.getByRole('button', { name: 'Change' }).click();
    await htmlValue.fill('<p>Temporary HTML</p>');
    await plainValue.fill('Temporary plain text');
    await expect(htmlPreview).toHaveJSProperty('srcdoc', '<p>Temporary HTML</p>');
    await expect(plainPreview).toHaveText('Temporary plain text');

    await discardChanges(page, 2);
    await expect(htmlValue).toHaveValue(loadedHtml);
    await expect(plainValue).toHaveValue(loadedPlain);
    await expect(htmlPreview).toHaveJSProperty('srcdoc', loadedHtml);
    await expect(plainPreview).toHaveText(loadedPlain);
});

test('image Change uploads through the existing async path and Revert cancels the pending image', async ({ page }) => {
    await page.route('**/settings/assets/upload', async (route) => {
        await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ assetId: 77, fileName: 'replacement.png', previewUrl: '/settings/assets/77' }) });
    });
    const row = rowFor(page, 'header_image');
    const file = row.locator('.image-file');

    await row.getByRole('button', { name: 'Change image' }).click();
    await expect(row).toHaveAttribute('data-editing', 'true');
    await expect(row).toHaveAttribute('data-dirty', 'true');
    await expect(row).toHaveAttribute('data-image-needs-upload', 'true');
    await file.setInputFiles({ name: 'replacement.png', mimeType: 'image/png', buffer: Buffer.from('png') });
    await expect(row.locator('.image-upload-status')).toHaveText('replacement.png is ready to save.');
    await expect(row.locator('.image-pending')).toBeVisible();
    await expect(row).toHaveAttribute('data-dirty', 'true');
    await expect(row.locator('.operation')).toHaveValue('Upsert');
    await expect(row.locator('.setting-value-binding')).toHaveValue('77');
    await expect(row.getByRole('button', { name: 'Revert to inherited image' })).toBeVisible();

    await page.getByRole('button', { name: 'Review 1 change' }).click();
    const review = page.locator('#save-confirm');
    await expect(review.locator('tbody .review-pending-column')).toContainText('replacement.png');
    await expect(review.locator('tbody .review-pending-column')).not.toContainText('Use inherited image');
    await review.getByRole('button', { name: 'Close' }).click();

    await row.getByRole('button', { name: 'Revert to inherited image' }).click();
    await expect(row.getByRole('button', { name: 'Change image' })).toBeFocused();
    await expect(row).toHaveAttribute('data-dirty', 'false');
    await expect(row.locator('.setting-value-binding')).toHaveValue('');
    await expect(row.locator('.image-pending')).toBeHidden();
    await expect(row.locator('.image-choose-another')).toBeHidden();
});

test('cancelling a customized image picker keeps a clean Change image retry', async ({ page }) => {
    const row = rowFor(page, 'custom_header_image');
    const change = row.getByRole('button', { name: 'Change image' });

    await expect(row).toHaveAttribute('data-dirty', 'false');
    await expect(row.locator('.image-pending')).toBeHidden();

    // Dispatching the button event invokes the native picker without selecting a file,
    // which is the browser-visible equivalent of cancelling that picker.
    await change.dispatchEvent('click');
    await expect(row).toHaveAttribute('data-dirty', 'false');
    await expect(row.locator('.image-pending')).toBeHidden();
    await expect(change).toBeVisible();

    await change.dispatchEvent('click');
    await expect(row).toHaveAttribute('data-dirty', 'false');
    await expect(change).toBeVisible();
});

test('discard locks several edited controls and restores the loaded values', async ({ page }) => {
    const number = rowFor(page, 'reset_seconds');
    const date = rowFor(page, 'expiration_date');
    const batch = rowFor(page, 'label.NameFirst');
    const loaded = {
        number: await number.locator('#reset-value').inputValue(),
        date: await date.locator('#expiration-value').inputValue(),
        batch: await batch.locator('.setting-value').inputValue(),
    };

    await number.getByRole('button', { name: 'Change' }).click();
    await number.locator('#reset-value').fill('31');
    await date.getByRole('button', { name: 'Change' }).click();
    await date.locator('#expiration-value').fill('2027-01-01');
    await batch.getByRole('button', { name: 'Change' }).click();
    await batch.locator('.setting-value').fill('Given name');
    await expect(dirtyRows(page)).toHaveCount(3);

    await discardChanges(page, 3);
    await expect(number.locator('#reset-value')).toHaveValue(loaded.number);
    await expect(date.locator('#expiration-value')).toHaveValue(loaded.date);
    await expect(batch.locator('.setting-value')).toHaveValue(loaded.batch);
    await expect(number.locator('#reset-value')).toHaveAttribute('readonly', '');
    await expect(date.locator('#expiration-value')).toBeDisabled();
    await expect(batch.locator('.setting-value')).toHaveAttribute('readonly', '');
    await expect(dirtyRows(page)).toHaveCount(0);
    await expect(page.locator('.settings-actions')).toBeHidden();
});

test('desktop and narrow layouts keep effective and scope columns readable', async ({ page }) => {
    const row = rowFor(page, 'registration_text');
    const measureLongFormValues = async () => Promise.all(['registration_text', 'age_warning_text'].map((key) => rowFor(page, key).evaluate((element) => {
        const effective = element.querySelector('.setting-current-value-full')!.getBoundingClientRect();
        const editor = element.querySelector('textarea.setting-value')!.getBoundingClientRect();
        return { effectiveHeight: effective.height, editorHeight: editor.height };
    })));
    const desktop = await row.locator('.setting-comparison').evaluate((element) => {
        const effective = element.querySelector('[aria-label="Effective now"]')!.getBoundingClientRect();
        const scope = element.querySelector('[aria-label="At this scope"]')!.getBoundingClientRect();
        return { effectiveRight: effective.right, scopeLeft: scope.left, effectiveTop: effective.top, scopeTop: scope.top };
    });
    expect(desktop.effectiveRight).toBeLessThanOrEqual(desktop.scopeLeft + 1);
    expect(Math.abs(desktop.effectiveTop - desktop.scopeTop)).toBeLessThan(4);
    for (const metrics of await measureLongFormValues()) {
        expect(metrics.effectiveHeight).toBeGreaterThanOrEqual(128);
        expect(Math.abs(metrics.effectiveHeight - metrics.editorHeight)).toBeLessThan(32);
    }

    await page.setViewportSize({ width: 320, height: 900 });
    const narrow = await row.locator('.setting-comparison').evaluate((element) => {
        const effective = element.querySelector('[aria-label="Effective now"]')!.getBoundingClientRect();
        const scope = element.querySelector('[aria-label="At this scope"]')!.getBoundingClientRect();
        return { effectiveBottom: effective.bottom, scopeTop: scope.top, effectiveLeft: effective.left, scopeLeft: scope.left };
    });
    expect(narrow.effectiveBottom).toBeLessThanOrEqual(narrow.scopeTop + 1);
    expect(Math.abs(narrow.effectiveLeft - narrow.scopeLeft)).toBeLessThan(4);
    for (const metrics of await measureLongFormValues()) {
        expect(metrics.effectiveHeight).toBeGreaterThanOrEqual(128);
        expect(Math.abs(metrics.effectiveHeight - metrics.editorHeight)).toBeLessThan(32);
    }
    const boolean = rowFor(page, 'show_age_warning');
    const narrowGroup = await boolean.locator('.setting-value-group').evaluate((element) => ({ width: element.getBoundingClientRect().width, parentWidth: element.parentElement!.getBoundingClientRect().width }));
    expect(narrowGroup.width).toBeLessThanOrEqual(narrowGroup.parentWidth + 1);
});
