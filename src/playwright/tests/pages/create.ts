import { expect, type Locator, type Page } from '@playwright/test';

export class CreatePage {
    readonly page: Page;
    readonly NameFirstInput: Locator;
    readonly NameLastInput: Locator;
    readonly BirthdateInput: Locator;
    readonly StreetOneInput: Locator;
    readonly StreetTwoInput: Locator;
    readonly CityInput: Locator;
    readonly PostalCodeInput: Locator;
    readonly EmailInput: Locator;
    readonly PhoneInput: Locator;
    readonly PasswordInput: Locator;
    readonly Password2Input: Locator;
    readonly RegisterButton: Locator;
    readonly ErrorMessage: Locator;
    readonly AcceptAgreementButton: Locator;

    constructor(page: Page) {
        this.page = page;
        this.NameFirstInput = page.locator('#NameFirst');
        this.NameLastInput = page.locator('#NameLast');
        this.BirthdateInput = page.locator('#Birthdate');
        this.StreetOneInput = page.locator('#StreetOne');
        this.StreetTwoInput = page.locator('#StreetTwo');
        this.CityInput = page.locator('#City');
        this.PostalCodeInput = page.locator('#PostalCode');
        this.EmailInput = page.locator('#EmailAddress');
        this.PhoneInput = page.locator('#PhoneVoice1');
        this.PasswordInput = page.locator('#Password');
        this.Password2Input = page.locator('#Password2');
        this.RegisterButton = page.locator('#registerButton');
        this.ErrorMessage = page.locator('.validation-summary-errors');
        this.AcceptAgreementButton = page.locator('#agreementAccept');
    }

    async load(orgId: number){
        await this.page.goto(`https://localhost:7213/Registration/Create/${orgId}`);
    }

    async acceptRegistrationAgreement(){
        await this.AcceptAgreementButton.click();
    }

    async register(
        firstName: string,
        lastName: string,
        birthdate: string,
        streetOne: string,
        streetTwo: string,
        city: string,
        postalCode: string,
        email: string,
        phone: string,
        password: string,
        password2: string
    ) {
        await this.enterFirstName(firstName);
        await this.enterLastName(lastName);
        await this.enterBirthdate(birthdate);
        await this.enterStreetOne(streetOne);
        await this.enterStreetTwo(streetTwo);
        await this.enterCity(city);
        await this.enterPostalCode(postalCode);
        await this.enterEmail(email);
        await this.enterPhone(phone);
        await this.enterPassword(password);
        await this.enterPassword2(password2);
        await this.submitRegistration();
    }

    async assertRegistrationAgreementIsVisible(){
        await expect(this.AcceptAgreementButton).toBeVisible();
    }

    async assertRegistrationAgreementIsNotVisible(){
        await expect(this.AcceptAgreementButton).not.toBeVisible();
    }

    async assertFormEmpty(){
        await expect(this.NameFirstInput).toBeEmpty();
        await expect(this.NameLastInput).toBeEmpty();
        await expect(this.BirthdateInput).toBeEmpty();
        await expect(this.StreetOneInput).toBeEmpty();
        await expect(this.StreetTwoInput).toBeEmpty();
        await expect(this.CityInput).toBeEmpty();
        await expect(this.PostalCodeInput).toBeEmpty();
        await expect(this.EmailInput).toBeEmpty();
        await expect(this.PhoneInput).toBeEmpty();
        await expect(this.PasswordInput).toBeEmpty();
        await expect(this.Password2Input).toBeEmpty();
    }

    async assertNoErrors(){        
        await expect(this.ErrorMessage).toBeHidden();
        //await expect(this.ErrorMessage).toBeEmpty();
    }

    async assertRegistrationSuccessful(){
        await expect(this.page.getByText('pacreg')).not.toBeEmpty();
    }

    async enterFirstName(firstName: string){
        await this.NameFirstInput.fill(firstName);
    }

    async enterLastName(lastName: string) {
        await this.NameLastInput.fill(lastName);
    }
    
    async enterBirthdate(birthdate: string) {
        await this.BirthdateInput.fill(birthdate);
    }
    
    async enterStreetOne(streetOne: string) {
        await this.StreetOneInput.fill(streetOne);
    }
    
    async enterStreetTwo(streetTwo: string) {
        await this.StreetTwoInput.fill(streetTwo);
    }
    
    async enterCity(city: string) {
        await this.CityInput.fill(city);
    }
    
    async enterPostalCode(postalCode: string) {
        await this.PostalCodeInput.fill(postalCode);
    }
    
    async enterEmail(email: string) {
        await this.EmailInput.fill(email);
    }
    
    async enterPhone(phone: string) {
        await this.PhoneInput.fill(phone);
    }
    
    async enterPassword(password: string) {
        await this.PasswordInput.fill(password);
    }
    
    async enterPassword2(password2: string) {
        await this.Password2Input.fill(password2);
    }

    async submitRegistration(){
        await this.RegisterButton.click();
    }
}
