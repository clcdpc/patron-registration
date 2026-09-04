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
const revertButton = (row: ReturnType<typeof rowFor>) => row.locator('.setting-revert');

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

test('loaded rows use one readable value surface and explicit actions', async ({ page }) => {
    await expect(dirtyRows(page)).toHaveCount(0);
    await expect(page.locator('.settings-actions')).toBeHidden();
    await expect(page.locator('.setting-comparison, .setting-baseline-column, .setting-effective-value, .setting-scope-panel, .html-preview, .plain-text-preview')).toHaveCount(0);
    await expect(page.locator('.setting-row .setting-change')).toHaveCount(29);
    await expect(page.locator('.setting-row .setting-value-surface:visible')).toHaveCount(29);
    await expect(rowFor(page, 'drivers_license_button_text').locator('.setting-scope-status')).toHaveText('Inherited from Main Library');
    await expect(revertButton(rowFor(page, 'drivers_license_button_text'))).toBeHidden();
    await expect(revertButton(rowFor(page, 'custom_heading'))).toBeVisible();
    await expect(revertButton(rowFor(page, 'no_inherited_value'))).toHaveText('Remove customization…');
    await expect(rowFor(page, 'show_dl_ips').locator('[data-idle-text]')).toHaveText('10., 192.168.');
});

test('HTML and template drawer summaries show safe compact raw source', async ({ page }) => {
    const footer = rowFor(page, 'custom_form_footer_html');
    const template = rowFor(page, 'welcome_email_template_html');
    const inherited = rowFor(page, 'inherited_html_message');
    const draftRemove = rowFor(page, 'shared_draft_remove_override_html');
    const long = rowFor(page, 'long_hostile_html');

    await expect(footer.locator('.summary-value')).toHaveText('<p>Draft line one</p> <p>Draft line two</p>');
    await expect(template.locator('.summary-value')).toHaveText('<p>Hello, {{NameFirst}}.</p>');
    await expect(inherited.locator('.summary-value')).toHaveText('<p>Inherited HTML content.</p>');
    await expect(inherited.locator('.setting-status')).toHaveText('Inherited from Main Library');
    await expect(draftRemove.locator('.summary-value')).toHaveText('<p>Inherited draft HTML.</p>');
    await expect(draftRemove.locator('.setting-status')).toHaveText('Shared draft — use inherited value');
    await expect(footer.locator('.summary-value')).not.toHaveText('HTML configured');
    await expect(template.locator('.summary-value')).not.toHaveText('Email template configured');
    await expect(inherited.locator('.summary-value')).not.toHaveText('HTML configured');
    await expect(draftRemove.locator('.summary-value')).not.toHaveText('HTML configured');
    await expect(long.locator('.summary-value')).toContainText('<p>Bring a photo ID');
    await expect(long.locator('.summary-value')).toContainText('…');
    expect(await long.locator('.summary-value').evaluate((element) => element.textContent.length)).toBeLessThanOrEqual(161);
    await expect(long.locator('.setting-name')).toBeVisible();
    await expect(long.locator('.setting-status')).toBeVisible();
    await expect(long.locator('img')).toHaveCount(0);
});

test('idle customized HTML is rendered and Change swaps the same region to source', async ({ page }) => {
    const row = rowFor(page, 'custom_form_footer_html');
    const idle = row.locator('[data-idle-surface]');
    const editor = row.locator('[data-editor-surface]');
    const frame = row.locator('[data-idle-html]');
    const source = row.locator('#footer-value');

    await expect(row.locator('.setting-value-surface:visible')).toHaveCount(1);
    await expect(idle).toBeVisible();
    await expect(frame).toBeVisible();
    await expect.poll(async () => frame.evaluate((element) => element.srcdoc.replace(/\r\n/g, '\n'))).toBe('<p>Draft line one</p>\n<p>Draft line two</p>');
    await expect(source).toBeHidden();
    await expect(row.locator('.setting-html-value-preview')).toHaveCount(1);

    await row.getByRole('button', { name: 'Change' }).click();
    await expect(row.locator('.setting-value-surface:visible')).toHaveCount(1);
    await expect(idle).toBeHidden();
    await expect(editor).toBeVisible();
    await expect(source).toBeVisible();
    await expect(source).toBeFocused();
    await expect(source).toHaveValue('<p>Draft line one</p>\n<p>Draft line two</p>');
    await expect(row.locator('[data-editor-surface] .setting-html-value-preview')).toHaveCount(0);
});

test('dirty HTML summaries show proposed raw source and discard restores the loaded baseline', async ({ page }) => {
    const custom = rowFor(page, 'welcome_email_template_html');
    await custom.getByRole('button', { name: 'Change' }).click();
    await custom.locator('#welcome-html-value').fill('<p>Updated registration message with <strong>new details</strong>.</p>');
    await expect(custom.locator('.summary-value')).toHaveText('Unsaved: <p>Updated registration message with <strong>new details</strong>.</p>');
    await discardChanges(page, 1);
    await expect(custom.locator('.summary-value')).toHaveText('<p>Hello, {{NameFirst}}.</p>');

    const draft = rowFor(page, 'custom_form_footer_html');
    await draft.getByRole('button', { name: 'Change' }).click();
    await draft.locator('#footer-value').fill('<p>Updated shared-draft footer</p>');
    await expect(draft.locator('.summary-value')).toHaveText('Unsaved: <p>Updated shared-draft footer</p>');
    await discardChanges(page, 1);
    await expect(draft.locator('.summary-value')).toHaveText('<p>Draft line one</p> <p>Draft line two</p>');
});

test('plain long text uses a readable surface then the same textarea while editing', async ({ page }) => {
    const row = rowFor(page, 'registration_text');
    await expect(row.locator('[data-idle-text]')).toContainText('Line one');
    await expect(row.locator('textarea.setting-value')).toBeHidden();
    await row.getByRole('button', { name: 'Change' }).click();
    await expect(row.locator('[data-idle-surface]')).toBeHidden();
    await expect(row.locator('[data-editor-surface]')).toBeVisible();
    await expect(row.locator('textarea.setting-value')).toBeFocused();
    await expect(row.locator('.setting-value-surface:visible')).toHaveCount(1);
});

test('date and reset duration values stay friendly in idle and revert previews', async ({ page }) => {
    const date = rowFor(page, 'expiration_date');
    const reset = rowFor(page, 'reset_seconds');
    const dialog = page.locator('#revert-confirm');

    await expect(date.locator('[data-idle-text]')).toHaveText('December 31, 2026');
    await date.getByRole('button', { name: 'Revert to inherited value…' }).click();
    await expect(dialog.locator('[data-revert-friendly]')).toHaveText('December 31, 2027');
    await dialog.getByRole('button', { name: 'Keep current value' }).click();

    await expect(reset.locator('[data-idle-text]')).toHaveText('30 seconds');
    await reset.getByRole('button', { name: 'Revert to inherited value…' }).click();
    await expect(dialog.locator('[data-revert-friendly]')).toHaveText('60 seconds');
    await dialog.getByRole('button', { name: 'Keep current value' }).click();
});

test('inherited Change seeds an Upsert and reveals the source-specific revert action', async ({ page }) => {
    const row = rowFor(page, 'drivers_license_button_text');
    const value = row.locator('#button-value');
    await expect(row.locator('[data-idle-text]')).toHaveText('Scan ID');
    await expect(row.locator('.setting-scope-status')).toHaveText('Inherited from Main Library');
    await expect(revertButton(row)).toBeHidden();

    await row.getByRole('button', { name: 'Change' }).click();
    await expect(value).toBeFocused();
    await expect(value).toHaveValue('Scan ID');
    await expect(row).toHaveAttribute('data-dirty', 'true');
    await expect(row.locator('.operation')).toHaveValue('Upsert');
    await expect(revertButton(row)).toBeVisible();
});

test('customized Revert previews the inherited value and Keep current value does nothing', async ({ page }) => {
    const row = rowFor(page, 'custom_heading');
    const revert = revertButton(row);
    const dialog = page.locator('#revert-confirm');
    await expect(row.locator('[data-idle-text]')).toHaveText('Welcome');
    await expect(row).toHaveAttribute('data-dirty', 'false');

    await revert.click();
    await expect(dialog).toBeVisible();
    await expect(dialog).toHaveAccessibleName('Revert to Main Library value?');
    await expect(dialog.locator('[data-revert-explanation]')).toContainText('Main Library');
    await expect(dialog.locator('[data-revert-friendly]')).toBeVisible();
    await expect(dialog.locator('[data-revert-friendly]')).toHaveText('Inherited heading');
    await expect(dialog.getByRole('button', { name: 'Keep current value' })).toBeVisible();
    await expect(dialog.getByRole('button', { name: 'Use inherited value' })).toBeVisible();
    await expect(row.locator('[data-idle-text]')).toHaveText('Welcome');
    await expect(row).toHaveAttribute('data-dirty', 'false');

    await dialog.getByRole('button', { name: 'Keep current value' }).click();
    await expect(dialog).toBeHidden();
    await expect(row.locator('[data-idle-text]')).toHaveText('Welcome');
    await expect(row).toHaveAttribute('data-dirty', 'false');
    await expect(revert).toBeFocused();
});

test('affirming Revert creates only a browser RemoveOverride and review describes it', async ({ page }) => {
    const row = rowFor(page, 'custom_heading');
    const dialog = page.locator('#revert-confirm');
    await revertButton(row).click();
    await dialog.getByRole('button', { name: 'Use inherited value' }).click();

    await expect(dialog).toBeHidden();
    await expect(row).toHaveAttribute('data-dirty', 'true');
    await expect(row.locator('.operation')).toHaveValue('RemoveOverride');
    await expect(row.locator('.operation')).toBeEnabled();
    await expect(row.locator('[data-idle-surface]')).toBeVisible();
    await expect(row.locator('[data-editor-surface]')).toBeHidden();
    await expect(row.locator('[data-idle-text]')).toHaveText('Inherited heading');
    await expect(row.getByRole('button', { name: 'Change' })).toBeFocused();

    await page.getByRole('button', { name: 'Review 1 change' }).click();
    const review = page.locator('#save-confirm');
    await expect(review).toBeVisible();
    await expect(review.locator('tbody .review-pending-column')).toContainText('Use Inherited heading from Main Library');
    await review.getByRole('button', { name: 'Close' }).click();
});

test('HTML inherited revert target is rendered in the confirmation', async ({ page }) => {
    const row = rowFor(page, 'custom_form_footer_html');
    const dialog = page.locator('#revert-confirm');
    await revertButton(row).click();
    await expect(dialog).toBeVisible();
    const target = dialog.locator('[data-revert-html]');
    await expect(target).toBeVisible();
    await expect(target).toHaveJSProperty('srcdoc', '<p>Inherited footer</p>');
    await expect(dialog.locator('[data-revert-text]')).toBeHidden();
    await expect(dialog.locator('[data-revert-friendly]')).toBeHidden();
    await dialog.getByRole('button', { name: 'Keep current value' }).click();
    await expect(row).toHaveAttribute('data-dirty', 'false');
});

test('no inherited value confirms Remove customization and becomes Not configured', async ({ page }) => {
    const row = rowFor(page, 'no_inherited_value');
    const dialog = page.locator('#revert-confirm');
    await revertButton(row).click();
    await expect(dialog).toBeVisible();
    await expect(dialog).toHaveAccessibleName('Remove customization?');
    await expect(dialog.locator('[data-revert-explanation]')).toContainText('No inherited value is configured');
    await expect(dialog.locator('[data-revert-none]')).toBeVisible();
    await expect(dialog.getByRole('button', { name: 'Keep current value' })).toBeVisible();
    await expect(dialog.getByRole('button', { name: 'Remove customization' })).toBeVisible();
    await dialog.getByRole('button', { name: 'Remove customization' }).click();
    await expect(row).toHaveAttribute('data-dirty', 'true');
    await expect(row.locator('.operation')).toHaveValue('RemoveOverride');
    await expect(row.locator('[data-idle-text]')).toHaveText('Not configured');
});

test('sensitive revert confirmation never exposes the inherited secret', async ({ page }) => {
    const row = rowFor(page, 'api_password');
    const dialog = page.locator('#revert-confirm');
    await expect(page.locator('body')).not.toContainText('super-secret');
    await revertButton(row).click();
    await expect(dialog).toBeVisible();
    await expect(dialog.locator('[data-revert-sensitive]')).toBeVisible();
    await expect(dialog).toContainText('inherited value is configured');
    await expect(dialog).not.toContainText('super-secret');
    await dialog.getByRole('button', { name: 'Keep current value' }).click();
    await expect(row).toHaveAttribute('data-dirty', 'false');

    await revertButton(row).click();
    await dialog.getByRole('button', { name: 'Use inherited value' }).click();
    await expect(row.locator('.operation')).toHaveValue('RemoveOverride');
    await expect(row).toHaveAttribute('data-dirty', 'true');
    await page.getByRole('button', { name: 'Review 1 change' }).click();
    await expect(page.locator('#save-confirm')).not.toContainText('super-secret');
});

test('shared-draft Upsert and RemoveOverride rows use one surface and preserve discard behavior', async ({ page }) => {
    const upsert = rowFor(page, 'custom_form_footer_html');
    const remove = rowFor(page, 'shared_draft_remove_override');

    await expect(upsert.locator('[data-idle-surface]')).toBeVisible();
    await expect(upsert.locator('.setting-scope-status')).toHaveText('Shared draft');
    await upsert.getByRole('button', { name: 'Change' }).click();
    await expect(upsert).toHaveAttribute('data-dirty', 'false');
    await upsert.locator('#footer-value').fill('<p>Browser draft</p>');
    await expect(upsert).toHaveAttribute('data-dirty', 'true');

    await expect(remove.locator('[data-idle-text]')).toHaveText('Inherited heading');
    await expect(remove.locator('.setting-scope-status')).toHaveText('Shared draft — use inherited value');
    await remove.getByRole('button', { name: 'Change' }).click();
    await expect(remove).toHaveAttribute('data-dirty', 'true');
    await expect(remove.locator('.operation')).toHaveValue('Upsert');
    await expect(remove.locator('.setting-value-binding')).toHaveValue('Inherited heading');
    await expect(dirtyRows(page)).toHaveCount(2);

    await discardChanges(page, 2);
    await expect(dirtyRows(page)).toHaveCount(0);
    await expect(upsert.locator('[data-idle-surface]')).toBeVisible();
    await expect(upsert.locator('#footer-value')).toBeHidden();
    await expect(remove.locator('[data-idle-text]')).toHaveText('Inherited heading');
    await expect(remove.locator('.setting-scope-status')).toHaveText('Shared draft — use inherited value');
});

test('batch labels and required values swap controls in place', async ({ page }) => {
    const label = rowFor(page, 'label.NameFirst');
    const required = rowFor(page, 'require.EmailAddress');
    await expect(label.locator('[data-idle-text]')).toHaveText('First name');
    await expect(label.locator('.batch-settings')).toHaveCount(0);
    await label.getByRole('button', { name: 'Change' }).click();
    await expect(label.locator('[data-idle-surface]')).toBeHidden();
    await expect(label.locator('.batch-label-input input')).toBeFocused();
    await label.locator('.batch-label-input input').fill('Given name');
    await expect(label).toHaveAttribute('data-dirty', 'true');

    await expect(required.locator('[data-idle-text]')).toHaveText('Optional');
    await required.getByRole('button', { name: 'Change' }).click();
    await expect(required.locator('[data-editor-surface]')).toBeVisible();
    await expect(required.getByRole('radio', { name: 'Required', exact: true })).toBeEnabled();
    await required.getByRole('radio', { name: 'Required', exact: true }).check();
    await expect(required).toHaveAttribute('data-dirty', 'true');
    await expect(required.locator('.operation')).toHaveValue('Upsert');
    await discardChanges(page, 2);
});

test('image Change and Revert retain upload validation while previewing the inherited thumbnail', async ({ page }) => {
    await page.route('**/settings/assets/upload', async (route) => {
        await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ assetId: 77, fileName: 'replacement.png', previewUrl: '/settings/assets/77' }) });
    });
    const row = rowFor(page, 'header_image');
    await row.getByRole('button', { name: 'Change image' }).click();
    await expect(row).toHaveAttribute('data-image-needs-upload', 'true');
    await row.locator('.image-file').setInputFiles({ name: 'replacement.png', mimeType: 'image/png', buffer: Buffer.from('png') });
    await expect(row.locator('.image-upload-status')).toHaveText('replacement.png is ready to save.');
    await expect(row).toHaveAttribute('data-dirty', 'true');
    await expect(revertButton(row)).toBeVisible();

    const dialog = page.locator('#revert-confirm');
    await revertButton(row).click();
    await expect(dialog).toBeVisible();
    await expect(dialog).toHaveAccessibleName('Revert to Main Library image?');
    await expect(dialog.locator('[data-revert-image]')).toBeVisible();
    await expect(dialog.locator('[data-revert-image-file]')).toHaveText('library-header.png');
    await expect(dialog.getByRole('button', { name: 'Keep current image' })).toBeVisible();
    await expect(dialog.getByRole('button', { name: 'Use inherited image' })).toBeVisible();
    await dialog.getByRole('button', { name: 'Use inherited image' }).click();
    await expect(row).toHaveAttribute('data-dirty', 'false');
    await expect(row.locator('[data-idle-image-file]')).toHaveText('library-header.png');
});

test('page-level Discard restores idle rendered output after edits and a confirmed revert', async ({ page }) => {
    const html = rowFor(page, 'custom_form_footer_html');
    const custom = rowFor(page, 'custom_heading');
    await html.getByRole('button', { name: 'Change' }).click();
    await html.locator('#footer-value').fill('<p>Temporary HTML</p>');
    await custom.getByRole('button', { name: 'Revert to inherited value…' }).click();
    await page.locator('#revert-confirm').getByRole('button', { name: 'Use inherited value' }).click();
    await expect(dirtyRows(page)).toHaveCount(2);

    await discardChanges(page, 2);
    await expect(dirtyRows(page)).toHaveCount(0);
    await expect(html.locator('[data-idle-surface]')).toBeVisible();
    await expect(html.locator('[data-idle-html]')).toBeVisible();
    await expect(html.locator('#footer-value')).toBeHidden();
    await expect(custom.locator('[data-idle-text]')).toHaveText('Welcome');
    await expect(custom.locator('[data-editor-surface]')).toBeHidden();
    await expect(page.locator('.settings-actions')).toBeHidden();
});

test('review keeps semantic pending changes as the before/after comparison surface', async ({ page }) => {
    const customized = rowFor(page, 'custom_heading');
    const inherited = rowFor(page, 'drivers_license_button_text');
    await customized.getByRole('button', { name: 'Change' }).click();
    await customized.locator('#custom-heading-value').fill('Browser heading');
    await inherited.getByRole('button', { name: 'Change' }).click();
    await expect(dirtyRows(page)).toHaveCount(2);
    await page.getByRole('button', { name: 'Review 2 changes' }).click();
    const dialog = page.locator('#save-confirm');
    await expect(dialog).toBeVisible();
    await expect(dialog.locator('thead .review-baseline-column')).toHaveText('Live now');
    await expect(dialog.locator('thead .review-pending-column')).toHaveText('Proposed');
    await expect(dialog.locator('tbody .review-baseline-column')).toHaveCount(2);
    await expect(dialog.locator('tbody .review-baseline-column').nth(0)).toBeVisible();
    await expect(dialog.locator('tbody .review-baseline-column').nth(1)).toBeVisible();
    await expect(dialog.locator('tbody .review-pending-column')).toHaveCount(2);
    await expect(dialog.locator('tbody .review-pending-column').nth(0)).toBeVisible();
    await expect(dialog.locator('tbody .review-pending-column').nth(1)).toBeVisible();
    await expect(dialog.locator('tbody')).toContainText('Browser heading');
    await expect(dialog.locator('tbody')).toContainText('Customize here: Scan ID');
    await dialog.getByRole('button', { name: 'Close' }).click();
});

test('HTML review compares compact raw source instead of generic configured labels', async ({ page }) => {
    const row = rowFor(page, 'welcome_email_template_html');
    await row.getByRole('button', { name: 'Change' }).click();
    await row.locator('#welcome-html-value').fill('<p>New registration message</p>');
    await page.getByRole('button', { name: 'Review 1 change' }).click();

    const review = page.locator('#save-confirm');
    await expect(review.locator('tbody .review-baseline-column')).toHaveText('<p>Hello, {{NameFirst}}.</p>');
    await expect(review.locator('tbody .review-pending-column')).toHaveText('<p>New registration message</p>');
    await expect(review).not.toContainText('HTML configured');
    await expect(review).not.toContainText('Email template configured');
    await review.getByRole('button', { name: 'Close' }).click();
    await discardChanges(page, 1);

    const hostile = rowFor(page, 'long_hostile_html');
    const hostileSource = '<p>Review literal <img src=x onerror="alert(1)"><strong>markup</strong></p>';
    await hostile.getByRole('button', { name: 'Change' }).click();
    await hostile.locator('#long-hostile-html-value').fill(hostileSource);
    await page.getByRole('button', { name: 'Review 1 change' }).click();
    const hostileReview = page.locator('#save-confirm');
    await expect(hostileReview.locator('tbody .review-pending-column')).toHaveText(hostileSource);
    await expect(hostileReview.locator('tbody img')).toHaveCount(0);
    await expect(hostileReview.locator('thead .review-baseline-column')).toHaveText('Live now');
    await expect(hostileReview.locator('thead .review-pending-column')).toHaveText('Proposed');
    await expect(hostileReview.locator('tbody .review-baseline-column')).toBeVisible();
    await expect(hostileReview.locator('tbody .review-pending-column')).toBeVisible();
    await page.setViewportSize({ width: 420, height: 900 });
    const reviewDimensions = await page.evaluate(() => ({ body: document.body.scrollWidth, viewport: document.documentElement.clientWidth }));
    expect(reviewDimensions.body).toBeLessThanOrEqual(reviewDimensions.viewport + 1);
    await expect(hostileReview.locator('.review-table-wrap')).toBeVisible();
    await hostileReview.getByRole('button', { name: 'Close' }).click();
    await discardChanges(page, 1);
});

test('sensitive shared-draft RemoveOverride follows the draft baseline without exposing secrets', async ({ page }) => {
    const noInherited = rowFor(page, 'shared_draft_secret_no_inherited');
    const inherited = rowFor(page, 'shared_draft_secret_inherited');
    await expect(noInherited.locator('[data-idle-text]')).toHaveText('Not configured');
    await expect(noInherited.locator('.setting-status')).toHaveText('Shared draft — use inherited value');
    await expect(inherited.locator('[data-idle-text]')).toHaveText('Configured');
    await expect(page.locator('body')).not.toContainText('super-secret');

    await noInherited.getByRole('button', { name: 'Change' }).click();
    await expect(noInherited.locator('[data-editor-surface]')).toBeVisible();
    await expect(noInherited.locator('input.setting-value')).toHaveValue('');
    await noInherited.locator('input.setting-value').fill('temporary-secret');
    await expect(noInherited).toHaveAttribute('data-dirty', 'true');
    await page.getByRole('button', { name: 'Review 1 change' }).click();
    const review = page.locator('#save-confirm');
    await expect(review.locator('tbody')).toContainText('Replacement entered');
    await expect(review.locator('tbody')).not.toContainText('temporary-secret');
    await review.getByRole('button', { name: 'Close' }).click();
    await discardChanges(page, 1);
    await expect(noInherited.locator('[data-idle-text]')).toHaveText('Not configured');

    await inherited.getByRole('button', { name: 'Change' }).click();
    await expect(inherited.locator('[data-editor-surface]')).toBeVisible();
    await expect(inherited.locator('input.setting-value')).toHaveValue('');
    await inherited.locator('input.setting-value').fill('another-temporary-secret');
    await discardChanges(page, 1);
    await expect(inherited.locator('[data-idle-text]')).toHaveText('Configured');
    await expect(page.locator('body')).not.toContainText('another-temporary-secret');
});

test('idle HTML previews are taller but bounded, scrollable, and keep the revert dialog compact', async ({ page }) => {
    const row = rowFor(page, 'long_hostile_html');
    const frame = row.locator('[data-idle-html]');
    const box = await frame.boundingBox();
    expect(box?.height).toBeGreaterThanOrEqual(190);
    expect(box?.height).toBeLessThanOrEqual(290);
    expect(await frame.evaluate((element) => getComputedStyle(element).height)).not.toBe('144px');
    await expect.poll(async () => frame.contentFrame().locator('html').evaluate((html) => html.scrollHeight > html.clientHeight)).toBe(true);
    const dimensions = await page.evaluate(() => ({ body: document.body.scrollWidth, viewport: document.documentElement.clientWidth }));
    expect(dimensions.body).toBeLessThanOrEqual(dimensions.viewport + 1);

    await revertButton(row).click();
    const dialog = page.locator('#revert-confirm');
    const dialogBox = await dialog.boundingBox();
    expect(dialogBox?.height).toBeLessThan(600);
    await dialog.getByRole('button', { name: 'Keep current value' }).click();
});

test('Escape on the revert dialog is the safe Keep current value action', async ({ page }) => {
    const row = rowFor(page, 'custom_heading');
    const revert = revertButton(row);
    await revert.click();
    await expect(page.locator('#revert-confirm')).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(page.locator('#revert-confirm')).toBeHidden();
    await expect(row).toHaveAttribute('data-dirty', 'false');
    await expect(revert).toBeFocused();
});

test('narrow settings layouts keep the single surface inside the viewport', async ({ page }) => {
    for (const width of [720, 420, 320]) {
        await page.setViewportSize({ width, height: 900 });
        const dimensions = await page.evaluate(() => ({ body: document.body.scrollWidth, viewport: document.documentElement.clientWidth }));
        expect(dimensions.body).toBeLessThanOrEqual(dimensions.viewport + 1);
        await expect(rowFor(page, 'registration_text').locator('.setting-value-surface:visible')).toHaveCount(1);
        await expect(rowFor(page, 'label.NameFirst').locator('.setting-scope-actions')).toBeVisible();
    }
});
