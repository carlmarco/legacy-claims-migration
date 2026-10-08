CREATE SCHEMA IF NOT EXISTS dental;
CREATE TABLE IF NOT EXISTS dental.patient (
    patient_number text PRIMARY KEY, last_name text, birth_date date,
    gender char(1) CHECK (gender IN ('M', 'F')), zip char(5),
    _src_table text NOT NULL, _src_rec_id bigint NOT NULL, _version timestamp NOT NULL);
CREATE TABLE IF NOT EXISTS dental.appointment (
    appointment_number integer PRIMARY KEY,
    patient_number text NOT NULL REFERENCES dental.patient, appointment_date date,
    _src_table text NOT NULL, _src_rec_id bigint NOT NULL, _version timestamp NOT NULL);
CREATE TABLE IF NOT EXISTS dental.appointment_procedure (
    appointment_number integer NOT NULL REFERENCES dental.appointment ON DELETE CASCADE,
    slot smallint NOT NULL, procedure_code text, fee numeric(10,2),
    _src_table text NOT NULL, _src_rec_id bigint NOT NULL, _version timestamp NOT NULL,
    PRIMARY KEY (appointment_number, slot));
