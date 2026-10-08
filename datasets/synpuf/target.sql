-- Clean, normalized target for SynPUF. Column lists must match mapping.yaml outputs
-- (enforced by tests/test_target_schema.py). Lineage columns on every table:
--   _src_table, _src_rec_id: the legacy row that won survivorship; _version: its LOAD_TS.
CREATE SCHEMA IF NOT EXISTS synpuf;

CREATE TABLE IF NOT EXISTS synpuf.beneficiary (
    beneficiary_id  text PRIMARY KEY CHECK (beneficiary_id ~ '^[0-9A-F]{16}$'),
    birth_date      date,
    death_date      date,
    sex             char(1) CHECK (sex IN ('M', 'F')),
    race_code       text,
    state_ssa_code  char(2) CHECK (state_ssa_code ~ '^[0-9]{2}$'),
    county_code     text,
    _src_table text NOT NULL, _src_rec_id bigint NOT NULL, _version timestamp NOT NULL,
    CHECK (death_date IS NULL OR birth_date IS NULL OR death_date >= birth_date)
);

CREATE TABLE IF NOT EXISTS synpuf.beneficiary_year (
    beneficiary_id  text NOT NULL REFERENCES synpuf.beneficiary,
    year            smallint NOT NULL CHECK (year BETWEEN 2000 AND 2030),
    esrd            boolean,
    part_a_months   smallint CHECK (part_a_months BETWEEN 0 AND 12),
    part_b_months   smallint CHECK (part_b_months BETWEEN 0 AND 12),
    hmo_months      smallint CHECK (hmo_months BETWEEN 0 AND 12),
    part_d_months   smallint CHECK (part_d_months BETWEEN 0 AND 12),
    alzheimers boolean, heart_failure boolean, chronic_kidney boolean, cancer boolean, copd boolean,
    depression boolean, diabetes boolean, ischemic_heart boolean, osteoporosis boolean, ra_oa boolean,
    stroke_tia boolean,
    ip_medicare_reimb numeric(12,2), ip_bene_responsibility numeric(12,2), ip_primary_payer numeric(12,2),
    op_medicare_reimb numeric(12,2), op_bene_responsibility numeric(12,2), op_primary_payer numeric(12,2),
    carrier_medicare_reimb numeric(12,2), carrier_bene_responsibility numeric(12,2),
    carrier_primary_payer numeric(12,2),
    _src_table text NOT NULL, _src_rec_id bigint NOT NULL, _version timestamp NOT NULL,
    PRIMARY KEY (beneficiary_id, year)
);

CREATE TABLE IF NOT EXISTS synpuf.claim (
    claim_type      text NOT NULL CHECK (claim_type IN ('inpatient', 'outpatient', 'carrier')),
    clm_id          text NOT NULL,
    segment         smallint NOT NULL,
    beneficiary_id  text NOT NULL REFERENCES synpuf.beneficiary,
    from_date       date,
    thru_date       date,
    provider_number text,
    payment_amount  numeric(12,2),
    primary_payer_paid numeric(12,2),
    attending_npi   text,
    operating_npi   text,
    other_npi       text,
    admission_date  date,
    discharge_date  date,
    admitting_dx            text,
    admitting_dx_raw        text,
    admitting_dx_resolution text CHECK (admitting_dx_resolution IN
                                ('exact', 'repaired', 'assumed', 'ambiguous', 'unresolved', 'not_in_reference')),
    drg_code        text,
    pass_thru_per_diem numeric(12,2),
    ip_deductible   numeric(12,2),
    pta_coinsurance numeric(12,2),
    blood_deductible numeric(12,2),
    ptb_deductible  numeric(12,2),
    ptb_coinsurance numeric(12,2),
    utilization_days integer,
    _src_table text NOT NULL, _src_rec_id bigint NOT NULL, _version timestamp NOT NULL,
    PRIMARY KEY (claim_type, clm_id, segment),
    CHECK (thru_date IS NULL OR from_date IS NULL OR thru_date >= from_date)
);
CREATE INDEX IF NOT EXISTS claim_beneficiary ON synpuf.claim (beneficiary_id);

-- One row per code slot. code is NULL exactly when the leading-zero loss can't be resolved (D13);
-- 'assumed' = ambiguous, as-received value stored as a labeled guess (D24).
CREATE TABLE IF NOT EXISTS synpuf.claim_diagnosis (
    claim_type text NOT NULL, clm_id text NOT NULL, segment smallint NOT NULL,
    position        smallint NOT NULL,
    code            text,
    code_raw        text NOT NULL,
    code_resolution text NOT NULL CHECK (code_resolution IN
                        ('exact', 'repaired', 'assumed', 'ambiguous', 'unresolved', 'not_in_reference')),
    _src_table text NOT NULL, _src_rec_id bigint NOT NULL, _version timestamp NOT NULL,
    PRIMARY KEY (claim_type, clm_id, segment, position),
    FOREIGN KEY (claim_type, clm_id, segment) REFERENCES synpuf.claim ON DELETE CASCADE,
    CHECK ((code IS NULL) = (code_resolution IN ('ambiguous', 'unresolved')))
);

CREATE TABLE IF NOT EXISTS synpuf.claim_procedure (
    LIKE synpuf.claim_diagnosis INCLUDING CONSTRAINTS,
    PRIMARY KEY (claim_type, clm_id, segment, position),
    FOREIGN KEY (claim_type, clm_id, segment) REFERENCES synpuf.claim ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS synpuf.claim_hcpcs (
    claim_type text NOT NULL, clm_id text NOT NULL, segment smallint NOT NULL,
    position    smallint NOT NULL,
    hcpcs_code  text NOT NULL,
    _src_table text NOT NULL, _src_rec_id bigint NOT NULL, _version timestamp NOT NULL,
    PRIMARY KEY (claim_type, clm_id, segment, position),
    FOREIGN KEY (claim_type, clm_id, segment) REFERENCES synpuf.claim ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS synpuf.claim_line (
    claim_type text NOT NULL, clm_id text NOT NULL, segment smallint NOT NULL,
    line_number          smallint NOT NULL,
    performing_npi       text,
    tax_number           text,
    hcpcs_code           text,
    payment_amount       numeric(12,2),
    deductible           numeric(12,2),
    primary_payer_paid   numeric(12,2),
    coinsurance          numeric(12,2),
    allowed_amount       numeric(12,2),
    processing_indicator text,
    dx                   text,
    dx_raw               text,
    dx_resolution        text CHECK (dx_resolution IN
                             ('exact', 'repaired', 'assumed', 'ambiguous', 'unresolved', 'not_in_reference')),
    _src_table text NOT NULL, _src_rec_id bigint NOT NULL, _version timestamp NOT NULL,
    PRIMARY KEY (claim_type, clm_id, segment, line_number),
    FOREIGN KEY (claim_type, clm_id, segment) REFERENCES synpuf.claim ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS synpuf.drug_event (
    pde_id          text PRIMARY KEY,
    beneficiary_id  text NOT NULL REFERENCES synpuf.beneficiary,
    service_date    date,
    ndc             char(11) CHECK (ndc ~ '^[0-9]{11}$'),
    quantity        numeric(12,3),
    days_supply     integer CHECK (days_supply >= 0),
    patient_pay     numeric(12,2),
    total_cost      numeric(12,2),
    _src_table text NOT NULL, _src_rec_id bigint NOT NULL, _version timestamp NOT NULL
);
CREATE INDEX IF NOT EXISTS drug_event_beneficiary ON synpuf.drug_event (beneficiary_id);
