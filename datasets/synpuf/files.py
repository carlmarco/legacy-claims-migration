"""SynPUF Sample 1 source files and their natural keys."""
from pathlib import Path

RAW = Path("data/raw")

BENEFICIARY = {
    2008: RAW / "DE1_0_2008_Beneficiary_Summary_File_Sample_1.zip",
    2009: RAW / "DE1_0_2009_Beneficiary_Summary_File_Sample_1.zip",
}
INPATIENT = RAW / "DE1_0_2008_to_2010_Inpatient_Claims_Sample_1.zip"
OUTPATIENT = RAW / "DE1_0_2008_to_2010_Outpatient_Claims_Sample_1.zip"
CARRIER = [
    RAW / "DE1_0_2008_to_2010_Carrier_Claims_Sample_1A.zip",
    RAW / "DE1_0_2008_to_2010_Carrier_Claims_Sample_1B.zip",
]
DRUG_EVENTS = RAW / "DE1_0_2008_to_2010_Prescription_Drug_Events_Sample_1.zip"

DEMOGRAPHIC_FIELDS = [
    "BENE_BIRTH_DT", "BENE_DEATH_DT", "BENE_SEX_IDENT_CD", "BENE_RACE_CD",
    "BENE_ESRD_IND", "SP_STATE_CODE", "BENE_COUNTY_CD",
]
CHRONIC_FLAGS = [
    "SP_ALZHDMTA", "SP_CHF", "SP_CHRNKIDN", "SP_CNCR", "SP_COPD", "SP_DEPRESSN",
    "SP_DIABETES", "SP_ISCHMCHT", "SP_OSTEOPRS", "SP_RA_OA", "SP_STRKETIA",
]


def icd9_columns(header: list[str]) -> list[str]:
    return [c for c in header if "ICD9_DGNS_CD" in c or "ICD9_PRCDR_CD" in c]
