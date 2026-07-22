import { test } from './fixtures/registration-test';
import { faker } from '@faker-js/faker';

test.beforeAll(async ({ browser }) => {

    // Clear the database
    await browser.newPage();
});


test('can accept agreement and submit registration', async ({ createPage }) => {
    await createPage.load(24);
    await createPage.assertRegistrationAgreementIsVisible(); 

    await createPage.acceptRegistrationAgreement();
    await createPage.assertRegistrationAgreementIsNotVisible();
    await createPage.assertFormEmpty();
    await createPage.assertNoErrors();

    const firstName  = faker.name.firstName();
    const lastName   = faker.name.lastName();
    const birthdate  = faker.date.birthdate().toISOString().split('T')[0]; // format as YYYY-MM-DD
    const streetOne  = faker.location.streetAddress();
    const streetTwo  = '';
    const city       = "Canal Winchester";
    const postalCode = "43110";
    const email      = faker.internet.email();
    const phone      = '1234567890';
    const password   = "1234";
    const password2  = password;

    await createPage.register(firstName, lastName, birthdate, streetOne, streetTwo, city, postalCode, email, phone, password, password2);
    
    await createPage.assertRegistrationSuccessful();
});