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
