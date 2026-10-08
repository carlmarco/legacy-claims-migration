#!/usr/bin/env bash
# Download CMS DE-SynPUF Sample 1 zips into data/raw. Idempotent: skips files already
# present with the expected size.
set -euo pipefail
DEST="${1:-data/raw}"
mkdir -p "$DEST"
BASE_DL=https://downloads.cms.gov/files
BASE_WWW=https://www.cms.gov/research-statistics-data-and-systems/downloadable-public-use-files/synpufs/downloads
# name|url|bytes
FILES=(
  "DE1_0_2008_to_2010_Carrier_Claims_Sample_1A.zip|$BASE_DL/DE1_0_2008_to_2010_Carrier_Claims_Sample_1A.zip|113217928"
  "DE1_0_2008_to_2010_Carrier_Claims_Sample_1B.zip|$BASE_DL/DE1_0_2008_to_2010_Carrier_Claims_Sample_1B.zip|113267827"
  "DE1_0_2008_to_2010_Prescription_Drug_Events_Sample_1.zip|$BASE_DL/DE1_0_2008_to_2010_Prescription_Drug_Events_Sample_1.zip|104703956"
  "DE1_0_2008_to_2010_Outpatient_Claims_Sample_1.zip|$BASE_WWW/de1_0_2008_to_2010_outpatient_claims_sample_1.zip|34456300"
  "DE1_0_2008_to_2010_Inpatient_Claims_Sample_1.zip|$BASE_WWW/de1_0_2008_to_2010_inpatient_claims_sample_1.zip|4162147"
  "DE1_0_2008_Beneficiary_Summary_File_Sample_1.zip|$BASE_WWW/de1_0_2008_beneficiary_summary_file_sample_1.zip|3119160"
  "DE1_0_2009_Beneficiary_Summary_File_Sample_1.zip|$BASE_WWW/de1_0_2009_beneficiary_summary_file_sample_1.zip|3177446"
  # ICD-9-CM v32 (FY2015, final) code list: resolves leading-zero ambiguity. See DECISIONS D13.
  "ICD-9-CM-v32-master-descriptions.zip|https://www.cms.gov/medicare/coding/icd9providerdiagnosticcodes/downloads/icd-9-cm-v32-master-descriptions.zip|1079497"
  # Code lists for the claim years (FY2008-FY2011), unioned with v32 by the resolver. See DECISIONS D18.
  "ICD-9-CM-v25.zip|https://www.cms.gov/medicare/coding/icd9providerdiagnosticcodes/downloads/v25_icd9.zip|146991"
  "ICD-9-CM-v26.zip|https://www.cms.gov/medicare/coding/icd9providerdiagnosticcodes/downloads/v26_icd9.zip|150818"
  "ICD-9-CM-v27.zip|https://www.cms.gov/medicare/coding/icd9providerdiagnosticcodes/downloads/v27_icd9.zip|158272"
  "ICD-9-CM-v28.zip|https://www.cms.gov/medicare/coding/icd9providerdiagnosticcodes/downloads/cmsv28_master_descriptions.zip|941708"
  # No 2010 beneficiary file for Sample 1: CMS's link serves Sample 20 (0 ID overlap). See DECISIONS D10.
)
for entry in "${FILES[@]}"; do
  IFS='|' read -r name url bytes <<<"$entry"
  out="$DEST/$name"
  if [[ -f "$out" && "$(stat -f%z "$out" 2>/dev/null || stat -c%s "$out")" == "$bytes" ]]; then
    echo "skip  $name"; continue
  fi
  echo "fetch $name"
  curl -fsSL --retry 3 -o "$out.part" "$url"
  mv "$out.part" "$out"
done
echo "done"
