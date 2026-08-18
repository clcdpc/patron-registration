/* Register the complete persistable SettingCatalog contract. */
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.RegistrationFormSettingTypes', 'U') IS NULL
BEGIN
    THROW 50024, 'dbo.RegistrationFormSettingTypes must exist before migration 009 is applied.', 1;
END;

IF COL_LENGTH('dbo.RegistrationFormSettingTypes', 'Setting') IS NULL
BEGIN
    THROW 50025, 'dbo.RegistrationFormSettingTypes.Setting must exist before migration 009 is applied.', 1;
END;

DECLARE @CatalogSettingTypes TABLE
(
    Setting nvarchar(200) NOT NULL PRIMARY KEY
);

/* BEGIN SETTING_CATALOG_ALLOWLIST */
INSERT @CatalogSettingTypes (Setting)
VALUES
        ('header_image_asset_id'),
        ('css_file'),
        ('warning_text'),
        ('custom_form_footer_html'),
        ('registration_text'),
        ('registration_form_header'),
        ('show_dl'),
        ('hide_gender'),
        ('enable_age_warning'),
        ('age_warning_text'),
        ('enable_age_block'),
        ('age_block_text'),
        ('hide_ereceipt'),
        ('na_gender_text'),
        ('normalize_to_uppercase'),
        ('dl_format'),
        ('enable_legal_name_checkbox'),
        ('drivers_license_button_text'),
        ('drivers_license_prompt_text'),
        ('agreement_confirm_button_text'),
        ('agreement_cancel_button_text'),
        ('school_info_field_legend'),
        ('school_info_format'),
        ('responsible_person_disclaimer'),
        ('display_responsible_person_field'),
        ('phone_number_format'),
        ('enable_patron_branch_select_option'),
        ('display_preferred_pickup_location'),
        ('teacher_patron_code_id'),
        ('student_patron_code_id'),
        ('patron_code_id'),
        ('expiration_date'),
        ('expiration_date_years'),
        ('hide_branch_select_if_only_one_option'),
        ('disable_branch'),
        ('display_ecard_checkbox'),
        ('ecard_patron_code_id'),
        ('ecard_registration_text'),
        ('ecard_barcode_prefix'),
        ('force_ecard_remotely'),
        ('display_mailing_list_checkbox'),
        ('mailing_list_description_html'),
        ('mailing_list_record_set_id'),
        ('display_sms_notice_information'),
        ('sms_notice_information_html'),
        ('use_legal_name_on_notices'),
        ('ecard_welcome_email_template_text'),
        ('ecard_welcome_email_template_html'),
        ('welcome_email_template_text'),
        ('welcome_email_template_html'),
        ('welcome_email_from_name'),
        ('welcome_email_subject'),
        ('welcome_email_from_address'),
        ('ecard_welcome_email_subject'),
        ('postmark_api_key'),
        ('bypass_dupe_check'),
        ('duplicate_patron_message_html'),
        ('perform_papi_duplicate_bypass'),
        ('use_first_name_for_duplicate_workaround'),
        ('block_out_of_state_registrations'),
        ('update_patron_record_with_melissa_address'),
        ('melissa_data_api_key'),
        ('valid_address_registration_text'),
        ('valid_address_plus_name_registration_text'),
        ('out_of_state_block_message'),
        ('valid_address_patron_code_id'),
        ('valid_address_plus_name_patron_code_id'),
        ('valid_address_record_set_id'),
        ('valid_address_plus_name_record_set_id'),
        ('invalid_address_record_set_id'),
        ('registration_logon_user_id'),
        ('add_to_record_set_id'),
        ('post_registration_note_text'),
        ('show_dl_ips'),
        ('reset_form'),
        ('kiosk_registration_text'),
        ('kiosk_registration_header'),
        ('reset_seconds'),
        ('alert.PatronBranchID'),
        ('alert.NameFirst'),
        ('alert.NameMiddle'),
        ('alert.NameLast'),
        ('alert.UseLegalName'),
        ('alert.LegalNameFirst'),
        ('alert.LegalNameMiddle'),
        ('alert.LegalNameLast'),
        ('alert.Birthdate'),
        ('alert.DeliveryOptionId'),
        ('alert.PhoneVoice1'),
        ('alert.PhoneVoice2'),
        ('alert.ReceiveEreceipts'),
        ('alert.EmailAddress'),
        ('alert.AltEmailAddress'),
        ('alert.StreetOne'),
        ('alert.StreetTwo'),
        ('alert.City'),
        ('alert.State'),
        ('alert.PostalCode'),
        ('alert.Password'),
        ('alert.Password2'),
        ('alert.RequestPickupBranchID'),
        ('alert.User1'),
        ('alert.User5'),
        ('alert.DeliverCardToSchool'),
        ('alert.IsStudent'),
        ('alert.IsTeacher'),
        ('alert.IsECard'),
        ('alert.AddToMailingList'),
        ('label.PatronBranchID'),
        ('label.NameFirst'),
        ('label.NameMiddle'),
        ('label.NameLast'),
        ('label.UseLegalName'),
        ('label.LegalNameFirst'),
        ('label.LegalNameMiddle'),
        ('label.LegalNameLast'),
        ('label.Birthdate'),
        ('label.DeliveryOptionId'),
        ('label.PhoneVoice1'),
        ('label.PhoneVoice2'),
        ('label.ReceiveEreceipts'),
        ('label.EmailAddress'),
        ('label.StreetOne'),
        ('label.StreetTwo'),
        ('label.City'),
        ('label.State'),
        ('label.User5'),
        ('label.PostalCode'),
        ('label.Password'),
        ('label.Password2'),
        ('label.RequestPickupBranchID'),
        ('label.User1'),
        ('label.DeliverCardToSchool'),
        ('label.IsStudent'),
        ('label.IsTeacher'),
        ('label.IsECard'),
        ('label.AddToMailingList'),
        ('require.PhoneVoice1'),
        ('require.EmailAddress'),
        ('require.User5'),
        ('require.RequestPickupBranchID');
/* END SETTING_CATALOG_ALLOWLIST */

INSERT dbo.RegistrationFormSettingTypes (Setting)
SELECT required.Setting
FROM @CatalogSettingTypes AS required
WHERE NOT EXISTS
(
    SELECT 1
    FROM dbo.RegistrationFormSettingTypes AS existing
    WHERE existing.Setting = required.Setting
);

IF EXISTS
(
    SELECT required.Setting
    FROM @CatalogSettingTypes AS required
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM dbo.RegistrationFormSettingTypes AS existing
        WHERE existing.Setting = required.Setting
    )
)
BEGIN
    THROW 50026, 'One or more SettingCatalog keys are missing after migration 009.', 1;
END;

COMMIT;
