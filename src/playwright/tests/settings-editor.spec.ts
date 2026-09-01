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

function dirtyRows(page: Page) {
    return page.locator('.setting-row[data-dirty="true"]');
}

async function confirmVisibleDiscard(page: Page, count: number) {
    await page.getByRole('button', { name: 'Discard unsaved changes' }).click();
    const dialog = page.locator('#unsaved-changes-dialog');
    await expect(dialog).toBeVisible();
    await expect(dialog.locator('[data-guard-discard]')).toHaveText(`Discard ${count} browser ${count === 1 ? 'change' : 'changes'}`);
    await expect(dialog.getByRole('button', { name: 'Keep editing' })).toBeFocused();
    await dialog.locator('[data-guard-discard]').click();
    await expect(dialog).toBeHidden();
}

test.beforeEach(async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await loadSettingsFixture(page);
});

test('initial browser state is clean and isolated from legacy control CSS', async ({ page }) => {
    await expect(dirtyRows(page)).toHaveCount(0);
    await expect(page.locator('.settings-actions')).toBeHidden();
    await expect(page.locator('.pending-changes-status')).toHaveText('');

    const multilineState = await page.locator('[data-setting-key="welcome_email_template_text"]').evaluate((row) => {
        const textarea = row.querySelector('textarea') as HTMLTextAreaElement;
        return {
            baseline: (row as HTMLElement).dataset.baselineValue,
            control: textarea.value,
        };
    });
    expect(multilineState.baseline).toContain('\r\n');
    expect(multilineState.control).not.toContain('\r');
    expect(multilineState.control).toBe(multilineState.baseline?.replace(/\r\n?/g, '\n'));

    const sizing = await page.locator('[data-setting-key="welcome_email_template_text"]').evaluate((row) => {
        const editor = row.querySelector('.setting-editor') as HTMLElement;
        const fieldset = row.querySelector('.setting-mode-group') as HTMLElement;
        const radio = row.querySelector('.setting-mode') as HTMLInputElement;
        const fieldsetStyle = getComputedStyle(fieldset);
        const radioStyle = getComputedStyle(radio);
        const editorRect = editor.getBoundingClientRect();
        const fieldsetRect = fieldset.getBoundingClientRect();
        return {
            editorWidth: editorRect.width,
            fieldsetWidth: fieldsetRect.width,
            fieldsetBoxSizing: fieldsetStyle.boxSizing,
            fieldsetMaxWidth: fieldsetStyle.maxWidth,
            fieldsetMargin: fieldsetStyle.margin,
            radioWidth: parseFloat(radioStyle.width),
            radioHeight: parseFloat(radioStyle.height),
            radioMinHeight: radioStyle.minHeight,
        };
    });
    expect(sizing.radioWidth).toBeLessThan(32);
    expect(sizing.radioHeight).toBeLessThan(32);
    expect(sizing.radioMinHeight).toBe('auto');
    expect(sizing.fieldsetBoxSizing).toBe('border-box');
    expect(sizing.fieldsetMaxWidth).toBe('100%');
    expect(sizing.fieldsetMargin).toBe('0px');
    expect(Math.abs(sizing.fieldsetWidth - sizing.editorWidth)).toBeLessThan(2);

    for (const selector of ['.image-mode-group', '.batch-mode-group']) {
        const fieldsets = page.locator(selector);
        for (let index = 0; index < await fieldsets.count(); index++) {
            await expect(fieldsets.nth(index)).toHaveCSS('box-sizing', 'border-box');
            await expect(fieldsets.nth(index)).not.toHaveCSS('width', '430px');
        }
    }
    for (let index = 0; index < await page.locator('.batch-mode-group').count(); index++) {
        await expect(page.locator('.batch-mode-group').nth(index)).toHaveCSS('max-width', '100%');
    }

    const compactSelect = await page.locator('[data-setting-key="drivers_license_input_type"] .value-editor').evaluate((editor) => ({
        editorWidth: editor.getBoundingClientRect().width,
        selectWidth: (editor.querySelector('select') as HTMLSelectElement).getBoundingClientRect().width,
    }));
    expect(compactSelect.selectWidth).toBeLessThan(compactSelect.editorWidth);

    const layout = await page.locator('[data-setting-key="welcome_email_template_text"]').evaluate((row) => {
        const effective = (row.querySelector('[aria-label="Effective now"]') as HTMLElement).getBoundingClientRect();
        const editor = (row.querySelector('.setting-editor') as HTMLElement).getBoundingClientRect();
        const mode = (row.querySelector('.setting-mode-group') as HTMLElement).getBoundingClientRect();
        const value = (row.querySelector('.value-editor') as HTMLElement).getBoundingClientRect();
        return {
            effective: { left: effective.left, top: effective.top, right: effective.right },
            editor: { left: editor.left, top: editor.top },
            mode: { left: mode.left, top: mode.top, bottom: mode.bottom },
            value: { left: value.left, top: value.top },
        };
    });
    expect(layout.effective.right).toBeLessThan(layout.editor.left);
    expect(Math.abs(layout.effective.top - layout.editor.top)).toBeLessThan(2);
    expect(Math.abs(layout.mode.left - layout.value.left)).toBeLessThan(2);
    expect(layout.mode.bottom).toBeLessThan(layout.value.top);
});

test('dirty counts follow edits and exact reverts', async ({ page }) => {
    const number = page.locator('#reset-value');
    const template = page.locator('#welcome-value');
    const loadedTemplate = await template.inputValue();

    await number.fill('31');
    await expect(dirtyRows(page)).toHaveCount(1);
    await expect(page.locator('[data-review-pending]')).toHaveText('Review 1 change');

    await template.fill(`${loadedTemplate}\nChanged in browser`);
    await expect(dirtyRows(page)).toHaveCount(2);
    await expect(page.locator('.pending-changes-status')).toHaveText('2 changes unsaved in this browser');

    await number.fill('30');
    await expect(dirtyRows(page)).toHaveCount(1);

    await template.fill(loadedTemplate);
    await expect(dirtyRows(page)).toHaveCount(0);
    await expect(page.locator('.settings-actions')).toBeHidden();
});

test('review lists only browser-pending changes without submitting or exposing secrets', async ({ page }) => {
    await page.evaluate(() => {
        (window as Window & { fixtureSubmitCount?: number }).fixtureSubmitCount = 0;
        document.querySelector('#settings-form')?.addEventListener('submit', (event) => {
            event.preventDefault();
            (window as Window & { fixtureSubmitCount: number }).fixtureSubmitCount++;
        });
    });

    await page.locator('#reset-value').fill('31');
    await page.locator('#secret-value').fill('browser-only-secret');
    const review = page.getByRole('button', { name: 'Review 2 changes' });
    await review.click();

    const dialog = page.locator('#save-confirm');
    await expect(dialog).toBeVisible();
    await expect(page.locator('#save-confirm-title')).toHaveText('Review 2 changes');
    await expect(page.locator('#confirm-save')).toBeHidden();
    await expect(page.locator('#cancel-save')).toBeFocused();
    await expect(dialog.locator('th[scope="col"]:visible')).toHaveText(['Setting', 'Pending change']);
    await expect(dialog.locator('tbody tr')).toHaveCount(2);
    await expect(dialog.locator('tbody')).toContainText('Reset seconds');
    await expect(dialog.locator('tbody')).toContainText('31');
    await expect(dialog.locator('tbody')).toContainText('API password');
    await expect(dialog.locator('tbody')).toContainText('Replacement entered');
    await expect(dialog.locator('tbody')).not.toContainText('browser-only-secret');
    await expect(dialog.locator('[data-browser-review-context]')).toContainText('does not save or publish anything');
    expect(await page.evaluate(() => (window as Window & { fixtureSubmitCount?: number }).fixtureSubmitCount)).toBe(0);

    await page.getByRole('button', { name: 'Close' }).click();
    await expect(review).toBeFocused();

    await page.locator('#reset-value').fill('30');
    await page.getByRole('button', { name: 'Review 1 change' }).click();
    await expect(dialog.locator('tbody tr')).toHaveCount(1);
    await expect(dialog.locator('tbody')).not.toContainText('Reset seconds');
    await expect(dialog.locator('tbody')).toContainText('API password');
    await page.getByRole('button', { name: 'Close' }).click();

    await page.locator('#secret-value').fill('');
    const numberRow = page.locator('[data-setting-key="reset_seconds"]');
    await numberRow.getByRole('radio', { name: 'Use inherited value' }).check();
    await page.getByRole('button', { name: 'Review 1 change' }).click();
    await expect(dialog.locator('tbody tr')).toHaveCount(1);
    await expect(dialog.locator('tbody')).toContainText('Use 60 from Main Library');
    expect(await page.evaluate(() => (window as Window & { fixtureSubmitCount?: number }).fixtureSubmitCount)).toBe(0);
    await page.getByRole('button', { name: 'Close' }).click();

    await page.getByRole('button', { name: 'Save 1 change live' }).click();
    await expect(page.locator('#confirm-save')).toBeVisible();
    await expect(page.locator('#confirm-save')).toBeFocused();
    await expect(dialog.locator('th[scope="col"]:visible')).toHaveText(['Setting', 'Live now', 'Proposed']);
    await expect(dialog.locator('[data-save-review-context]')).toBeVisible();
    await page.getByRole('button', { name: 'Cancel' }).click();
    expect(await page.evaluate(() => (window as Window & { fixtureSubmitCount?: number }).fixtureSubmitCount)).toBe(1);
});

test('responsive layout stacks in logical DOM order and radios remain keyboard operable', async ({ page }) => {
    await page.setViewportSize({ width: 720, height: 900 });
    const positions = await page.locator('[data-setting-key="welcome_email_template_text"]').evaluate((row) => {
        const effective = (row.querySelector('[aria-label="Effective now"]') as HTMLElement).getBoundingClientRect();
        const mode = (row.querySelector('.setting-mode-group') as HTMLElement).getBoundingClientRect();
        const value = (row.querySelector('.value-editor') as HTMLElement).getBoundingClientRect();
        return { effectiveBottom: effective.bottom, modeTop: mode.top, modeBottom: mode.bottom, valueTop: value.top, effectiveLeft: effective.left, modeLeft: mode.left, valueLeft: value.left };
    });
    expect(positions.effectiveBottom).toBeLessThan(positions.modeTop);
    expect(positions.modeBottom).toBeLessThan(positions.valueTop);
    expect(Math.abs(positions.effectiveLeft - positions.modeLeft)).toBeLessThan(2);
    expect(Math.abs(positions.modeLeft - positions.valueLeft)).toBeLessThan(2);

    const booleanRow = page.locator('[data-setting-key="show_age_warning"]');
    const noRadio = booleanRow.getByRole('radio', { name: 'No' });
    await noRadio.focus();
    await noRadio.press('ArrowUp');
    await expect(booleanRow.getByRole('radio', { name: 'Yes', exact: true })).toBeChecked();
    await expect(booleanRow).toHaveAttribute('data-dirty', 'true');
});

test('visible discard restores an inherited editor buffer and does not resurrect it', async ({ page }) => {
    const row = page.locator('[data-setting-key="drivers_license_button_text"]');
    const value = row.locator('#button-value');
    const loadedValue = await value.inputValue();

    await row.getByRole('radio', { name: /Customize here/ }).check();
    await value.fill('Temporary inherited override');
    await expect(dirtyRows(page)).toHaveCount(1);

    await confirmVisibleDiscard(page, 1);

    await expect(row.getByRole('radio', { name: /Use inherited value/ })).toBeChecked();
    await expect(value).toHaveValue(loadedValue);
    await expect(row).toHaveAttribute('data-dirty', 'false');
    await expect(page.locator('.settings-actions')).toBeHidden();
    await expect(page.locator('#settings-status')).toHaveText('Discarded 1 browser change.');

    await row.getByRole('radio', { name: /Customize here/ }).check();
    await expect(value).toHaveValue(loadedValue);
    await expect(value).not.toHaveValue('Temporary inherited override');
});

test('discard confirmation cancellation keeps pending work and restores trigger focus', async ({ page }) => {
    const value = page.locator('#reset-value');
    const discard = page.getByRole('button', { name: 'Discard unsaved changes' });
    await value.fill('31');
    await discard.click();

    const dialog = page.locator('#unsaved-changes-dialog');
    await expect(dialog).toBeVisible();
    await dialog.getByRole('button', { name: 'Keep editing' }).click();
    await expect(dialog).toBeHidden();
    await expect(discard).toBeFocused();
    await expect(dirtyRows(page)).toHaveCount(1);
    await expect(page.locator('.settings-actions')).toBeVisible();
});

test('visible discard restores HTML and plain-text previews with their editors', async ({ page }) => {
    const html = page.locator('#footer-value');
    const htmlPreview = page.locator('[data-setting-key="custom_form_footer_html"] iframe');
    const plain = page.locator('#welcome-value');
    const plainPreview = page.locator('[data-setting-key="welcome_email_template_text"] .plain-text-preview');
    const loadedHtml = await html.inputValue();
    const loadedPlain = await plain.inputValue();
    const loadedHtmlPreview = await htmlPreview.evaluate((frame) => (frame as HTMLIFrameElement).srcdoc);
    const loadedPlainPreview = await plainPreview.textContent();

    await html.fill('<p>Temporary footer</p>');
    await plain.fill('Temporary plain-text message');
    await expect(htmlPreview).toHaveJSProperty('srcdoc', '<p>Temporary footer</p>');
    await expect(plainPreview).toHaveText('Temporary plain-text message');
    await expect(dirtyRows(page)).toHaveCount(2);

    await confirmVisibleDiscard(page, 2);

    await expect(html).toHaveValue(loadedHtml);
    await expect(plain).toHaveValue(loadedPlain);
    await expect(htmlPreview).toHaveJSProperty('srcdoc', loadedHtmlPreview);
    await expect(plainPreview).toHaveText(loadedPlainPreview || '');
    await expect(dirtyRows(page)).toHaveCount(0);
    await expect(page.locator('.settings-actions')).toBeHidden();
});

test('visible discard clears multiple pending editor types and hides the sticky bar', async ({ page }) => {
    const number = page.locator('#reset-value');
    const booleanRow = page.locator('[data-setting-key="show_age_warning"]');
    const inheritedRow = page.locator('[data-setting-key="drivers_license_button_text"]');
    const inheritedValue = inheritedRow.locator('#button-value');
    const batchValue = page.locator('[data-setting-key="label.NameFirst"] .setting-value');
    const loadedInherited = await inheritedValue.inputValue();
    const loadedBatch = await batchValue.inputValue();

    await number.fill('31');
    await booleanRow.getByRole('radio', { name: 'Yes', exact: true }).check();
    await inheritedRow.getByRole('radio', { name: /Customize here/ }).check();
    await inheritedValue.fill('Temporary multiple-row value');
    await batchValue.fill('Changed first name');
    await expect(dirtyRows(page)).toHaveCount(4);

    await confirmVisibleDiscard(page, 4);

    await expect(dirtyRows(page)).toHaveCount(0);
    await expect(page.locator('.settings-actions')).toBeHidden();
    await expect(page.locator('#settings-status')).toHaveText('Discarded 4 browser changes.');
    await expect(number).toHaveValue('30');
    await expect(booleanRow.getByRole('radio', { name: 'No', exact: true })).toBeChecked();
    await expect(inheritedRow.getByRole('radio', { name: /Use inherited value/ })).toBeChecked();
    await expect(inheritedValue).toHaveValue(loadedInherited);
    await expect(batchValue).toHaveValue(loadedBatch);
});

test('visible discard restores native date, nullable date, email, URL, and decimal controls', async ({ page }) => {
    const controls = [
        page.locator('#expiration-value'),
        page.locator('#optional-expiration-value'),
        page.locator('#welcome-email-value'),
        page.locator('#registration-uri-value'),
        page.locator('#tax-value'),
    ];
    const loadedValues = await Promise.all(controls.map((control) => control.inputValue()));
    await controls[0].fill('2027-01-01');
    await controls[1].fill('2027-01-02');
    await controls[2].fill('other@example.com');
    await controls[3].fill('https://example.org/other');
    await controls[4].fill('2.5');
    await expect(dirtyRows(page)).toHaveCount(5);

    await confirmVisibleDiscard(page, 5);

    await expect(dirtyRows(page)).toHaveCount(0);
    for (let index = 0; index < controls.length; index++) await expect(controls[index]).toHaveValue(loadedValues[index]);
    await expect(page.locator('.settings-actions')).toBeHidden();
});

test('visible discard restores loaded shared-draft upsert and remove-override baselines', async ({ page }) => {
    const upsert = page.locator('[data-setting-key="custom_form_footer_html"]');
    const remove = page.locator('[data-setting-key="shared_draft_remove_override"]');
    const upsertValue = upsert.locator('#footer-value');
    const removeValue = remove.locator('#shared-draft-remove-value');
    const loadedUpsert = await upsertValue.inputValue();
    const loadedRemove = await removeValue.inputValue();

    await upsertValue.fill('<p>Temporary browser draft</p>');
    await remove.getByRole('radio', { name: /Customize here/ }).check();
    await removeValue.fill('Temporary browser override');
    await expect(dirtyRows(page)).toHaveCount(2);

    await confirmVisibleDiscard(page, 2);

    await expect(upsert.getByRole('radio', { name: /Customize here/ })).toBeChecked();
    await expect(upsertValue).toHaveValue(loadedUpsert);
    await expect(remove.getByRole('radio', { name: /Use inherited value/ })).toBeChecked();
    await expect(removeValue).toHaveValue(loadedRemove);
    for (const row of [upsert, remove]) {
        await expect(row).toHaveAttribute('data-draft-change', 'true');
        await expect(row).toHaveAttribute('data-dirty', 'false');
        await expect(row.locator('.setting-status')).toHaveText('Shared draft');
    }
    await expect(dirtyRows(page)).toHaveCount(0);
    await expect(page.locator('.settings-actions')).toBeHidden();
});

test('visible discard restores IP-prefix and image editor state', async ({ page }) => {
    const prefixes = page.locator('[data-setting-key="show_dl_ips"]');
    const prefixInputs = prefixes.locator('.ip-prefix-input');
    const loadedPrefixes = await prefixInputs.evaluateAll((inputs) => inputs.map((input) => (input as HTMLInputElement).value));
    await prefixInputs.first().fill('10.0.');
    await prefixes.locator('.ip-prefix-add').click();
    await prefixes.locator('.ip-prefix-input').last().fill('172.16.');
    await expect(prefixes).toHaveAttribute('data-dirty', 'true');
    await confirmVisibleDiscard(page, 1);
    await expect(prefixes.locator('.ip-prefix-input')).toHaveCount(loadedPrefixes.length);
    const restoredPrefixes = await prefixes.locator('.ip-prefix-input').evaluateAll((inputs) => inputs.map((input) => (input as HTMLInputElement).value));
    expect(restoredPrefixes).toEqual(loadedPrefixes);
    await expect(prefixes).toHaveAttribute('data-dirty', 'false');

    const image = page.locator('[data-setting-key="header_image"]');
    await image.getByRole('radio', { name: /Customize here/ }).check();
    await expect(image).toHaveAttribute('data-dirty', 'true');
    await confirmVisibleDiscard(page, 1);
    await expect(image.getByRole('radio', { name: /Use inherited image/ })).toBeChecked();
    await expect(image).toHaveAttribute('data-dirty', 'false');
    await expect(image.locator('.image-pending')).toBeHidden();
    await expect(image).not.toHaveAttribute('data-image-needs-upload');
    await expect(page.locator('.settings-actions')).toBeHidden();
});
