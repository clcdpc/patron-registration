import { test as base } from '@playwright/test';
import { CreatePage } from '../pages/create';

type RegistrationFixtures = {
  createPage: CreatePage;
};

export const test = base.extend<RegistrationFixtures>({
    createPage: async ({ page }, use) => {
    await use(new CreatePage(page));
  },
});

export { expect } from '@playwright/test';