-- Empty Login Data schema observed from the installed Chrome 155.0.8059.84
-- in the disposable Fedora guest. This is a test fixture, not an installer
-- schema or evidence that a browser has opened imported credentials.
CREATE TABLE meta (key LONGVARCHAR NOT NULL UNIQUE PRIMARY KEY, value LONGVARCHAR);
INSERT INTO meta(key, value) VALUES ('version', '43'), ('last_compatible_version', '40');
CREATE TABLE logins (
    origin_url VARCHAR NOT NULL, action_url VARCHAR, username_element VARCHAR,
    username_value VARCHAR, password_element VARCHAR, password_value BLOB,
    submit_element VARCHAR, signon_realm VARCHAR NOT NULL,
    date_created INTEGER NOT NULL, blacklisted_by_user INTEGER NOT NULL,
    scheme INTEGER NOT NULL, password_type INTEGER, times_used INTEGER,
    form_data BLOB, display_name VARCHAR, icon_url VARCHAR,
    federation_url VARCHAR, skip_zero_click INTEGER,
    generation_upload_status INTEGER, possible_username_pairs BLOB,
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    date_last_used INTEGER NOT NULL DEFAULT 0, moving_blocked_for BLOB,
    date_password_modified INTEGER NOT NULL DEFAULT 0, sender_email VARCHAR,
    sender_name VARCHAR, date_received INTEGER,
    sharing_notification_displayed INTEGER NOT NULL DEFAULT 0,
    keychain_identifier BLOB, sender_profile_image_url VARCHAR,
    date_last_filled INTEGER NOT NULL DEFAULT 0,
    actor_login_approved INTEGER NOT NULL DEFAULT 0,
    UNIQUE (origin_url, username_element, username_value, password_element, signon_realm)
);
CREATE INDEX logins_signon ON logins (signon_realm);
CREATE TABLE insecure_credentials (
    parent_id INTEGER REFERENCES logins ON UPDATE CASCADE ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
    insecurity_type INTEGER NOT NULL, create_time INTEGER NOT NULL,
    is_muted INTEGER NOT NULL DEFAULT 0,
    trigger_notification_from_backend INTEGER NOT NULL DEFAULT 0,
    UNIQUE (parent_id, insecurity_type)
);
CREATE INDEX foreign_key_index ON insecure_credentials (parent_id);
CREATE TABLE password_notes (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    parent_id INTEGER NOT NULL REFERENCES logins ON UPDATE CASCADE ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
    key VARCHAR NOT NULL, value BLOB, date_created INTEGER NOT NULL,
    confidential INTEGER, UNIQUE (parent_id, key)
);
CREATE INDEX foreign_key_index_notes ON password_notes (parent_id);
CREATE TABLE stats (
    origin_domain VARCHAR NOT NULL, username_value VARCHAR,
    dismissal_count INTEGER, update_time INTEGER NOT NULL,
    UNIQUE (origin_domain, username_value)
);
CREATE INDEX stats_origin ON stats (origin_domain);
CREATE TABLE sync_entities_metadata (
    storage_key INTEGER PRIMARY KEY AUTOINCREMENT, metadata VARCHAR NOT NULL
);
CREATE TABLE sync_model_metadata (
    id INTEGER PRIMARY KEY AUTOINCREMENT, model_metadata VARCHAR NOT NULL
);
