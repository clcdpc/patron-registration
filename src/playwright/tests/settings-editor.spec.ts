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
        const fieldset = page.locator(selector);
        await expect(fieldset).toHaveCSS('box-sizing', 'border-box');
        await expect(fieldset).not.toHaveCSS('width', '430px');
    }
    await expect(page.locator('.batch-mode-group')).toHaveCSS('max-width', '100%');

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
