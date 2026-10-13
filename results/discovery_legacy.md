# Schema discovery: `legacy.sqlite`

| Table | Rows | Columns | Candidate keys | Repeating groups |
|---|---:|---:|---|---|
| `CAR_CLM` | 1,215,626 | 139 | `REC_ID` | `L{n}_ALOWD (n = 1..13)`, `L{n}_COINS (n = 1..13)`, `L{n}_DED (n = 1..13)`, `L{n}_DX (n = 1..13)`, `L{n}_HCPCS (n = 1..13)`, `L{n}_NPI (n = 1..13)`, `L{n}_PMT (n = 1..13)`, `L{n}_PRCSG (n = 1..13)`, `L{n}_PRPYR (n = 1..13)`, `L{n}_TAX (n = 1..13)` |
| `CLM_HDR_ALL` | 219,214 | 91 | `REC_ID` | `DX{n} (n = 1..10)`, `HCPCS{n} (n = 1..45)`, `PX{n} (n = 1..6)` |
| `MBR_MSTR` | 57,773 | 37 | `REC_ID` | — |
| `RX_EVNT` | 1,402,142 | 12 | `REC_ID` | — |

## `CAR_CLM` (1,215,626 rows)

| Column | Declared | Stored as | Null | Blank | Distinct | Len | Formats | Flags |
|---|---|---|---:|---:|---:|---|---|---|
| `REC_ID` | INTEGER | integer 100% |  |  | > 10,000 | 1–7 | integer 100% | INTEGER values of varying digit length: if these are codes, leading zeros may have been lost |
| `CLM_ID` | TEXT | text 100% |  |  | > 10,000 | 15–15 | integer 100%, digits:leading-zero 0% | **near-unique: 30,036 repeated values (duplicate loads?)** |
| `MBR_ID` | TEXT | text 100% |  |  | > 10,000 | 16–18 | alnum 97.5%, padded:alnum 2.4%, integer 0%, padded:integer 0% | untrimmed whitespace |
| `FROM_DT` | TEXT | text 100% |  |  | 4,384 | 6–10 | date:yyyymmdd 70%, date:m/d/yy 10%, date:yyyy-mm-dd 10%, date:m/d/yyyy 10% | **mixed date formats** |
| `THRU_DT` | TEXT | text 100% |  |  | 4,384 | 6–10 | date:yyyymmdd 70%, date:m/d/yyyy 10%, date:yyyy-mm-dd 10%, date:m/d/yy 10% | **mixed date formats** |
| `DX_LIST` | TEXT | text 100%, null 0% | 0% |  | > 10,000 | 2–47 | other 56.5%, integer 36.8%, alnum 6.8% |  |
| `L1_NPI` | TEXT | text 99.8%, null 0.2% | 0.2% |  | > 10,000 | 10–10 | integer 89.4%, digits:leading-zero 10.6% |  |
| `L1_TAX` | TEXT | text 100% |  |  | > 10,000 | 9–9 | integer 90.2%, digits:leading-zero 9.8% |  |
| `L1_HCPCS` | TEXT | text 98.9%, null 1.1% | 1.1% |  | 5,078 | 5–5 | integer 93%, alnum 5.4%, digits:leading-zero 1.6% |  |
| `L1_PMT` | TEXT | text 100% |  |  | 222 | 2–8 | decimal 83.4%, padded:decimal 5.5%, money:$ 5.5%, integer 5.5% | **money as formatted text**; untrimmed whitespace |
| `L1_DED` | TEXT | text 100% |  |  | 61 | 2–8 | decimal 98.8%, integer 0.4%, money:$ 0.4%, padded:decimal 0.4% | **money as formatted text**; untrimmed whitespace |
| `L1_PRPYR` | TEXT | text 100% |  |  | 84 | 2–9 | decimal 99.8%, integer 0.1%, padded:decimal 0.1%, money:$ 0.1% | **money as formatted text**; untrimmed whitespace |
| `L1_COINS` | TEXT | text 100% |  |  | 73 | 2–8 | decimal 87.6%, padded:decimal 4.2%, money:$ 4.2%, integer 4.1% | **money as formatted text**; untrimmed whitespace |
| `L1_ALOWD` | TEXT | text 100% |  |  | 269 | 2–8 | decimal 82.6%, money:$ 5.8%, padded:decimal 5.8%, integer 5.8% | **money as formatted text**; untrimmed whitespace |
| `L1_PRCSG` | TEXT | text 100% |  |  | 31 | 1–1 | alnum 99%, flag:yn 0.8%, other 0.2%, integer 0% |  |
| `L1_DX` | INTEGER | integer 91%, text 7.7%, null 1.3% | 1.3% |  | 7,386 | 1–6 | integer 92.2%, alnum 7%, code:dotted 0.8%, other 0% | **mixed storage types**; **INTEGER column holding text (codes?): leading zeros may be lost**; INTEGER values of varying digit length: if these are codes, leading zeros may have been lost |
| `L2_NPI` | TEXT | null 56%, text 44% | 56% |  | > 10,000 | 10–10 | integer 89.4%, digits:leading-zero 10.6% |  |
| `L2_TAX` | TEXT | null 55.9%, text 44.1% | 55.9% |  | > 10,000 | 9–9 | integer 90.3%, digits:leading-zero 9.7% |  |
| `L2_HCPCS` | TEXT | null 60.6%, text 39.4% | 60.6% |  | 4,513 | 5–5 | integer 76.2%, alnum 23.7%, digits:leading-zero 0.1% |  |
| `L2_PMT` | TEXT | text 100% |  |  | 221 | 2–8 | decimal 93.9%, padded:decimal 2%, integer 2%, money:$ 2% | **money as formatted text**; untrimmed whitespace |
| `L2_DED` | TEXT | text 100% |  |  | 61 | 2–8 | decimal 99.8%, padded:decimal 0.1%, integer 0.1%, money:$ 0.1% | **money as formatted text**; untrimmed whitespace |
| `L2_PRPYR` | TEXT | text 100% |  |  | 77 | 2–9 | decimal 100%, padded:decimal 0%, integer 0%, money:$ 0% | **money as formatted text**; untrimmed whitespace |
| `L2_COINS` | TEXT | text 100% |  |  | 73 | 2–8 | decimal 96.9%, padded:decimal 1.1%, integer 1%, money:$ 1% | **money as formatted text**; untrimmed whitespace |
| `L2_ALOWD` | TEXT | text 100% |  |  | 269 | 2–8 | decimal 93.8%, money:$ 2.1%, padded:decimal 2.1%, integer 2.1% | **money as formatted text**; untrimmed whitespace |
| `L2_PRCSG` | TEXT | null 55.9%, text 44.1% | 55.9% |  | 27 | 1–1 | alnum 98.9%, flag:yn 1%, other 0.1%, integer 0% |  |
| `L2_DX` | INTEGER | null 58%, integer 37.2%, text 4.8% | 58% |  | 5,866 | 1–6 | integer 88.6%, alnum 10.3%, code:dotted 1.1%, other 0% | **mixed storage types**; **INTEGER column holding text (codes?): leading zeros may be lost**; INTEGER values of varying digit length: if these are codes, leading zeros may have been lost |
| `L3_NPI` | TEXT | null 78%, text 22% | 78% |  | > 10,000 | 10–10 | integer 89.3%, digits:leading-zero 10.7% |  |
| `L3_TAX` | TEXT | null 78%, text 22% | 78% |  | > 10,000 | 9–9 | integer 90.2%, digits:leading-zero 9.8% |  |
| `L3_HCPCS` | TEXT | null 80.4%, text 19.6% | 80.4% |  | 3,596 | 5–5 | integer 82.3%, alnum 17.7%, digits:leading-zero 0.1% |  |
| `L3_PMT` | TEXT | text 100% |  |  | 216 | 2–8 | decimal 97.1%, money:$ 1%, padded:decimal 1%, integer 1% | **money as formatted text**; untrimmed whitespace |
| `L3_DED` | TEXT | text 100% |  |  | 58 | 2–8 | decimal 99.9%, padded:decimal 0%, money:$ 0%, integer 0% | **money as formatted text**; untrimmed whitespace |
| `L3_PRPYR` | TEXT | text 100% |  |  | 68 | 2–9 | decimal 100%, integer 0%, money:$ 0%, padded:decimal 0% | **money as formatted text**; untrimmed whitespace |
| `L3_COINS` | TEXT | text 100% |  |  | 73 | 2–8 | decimal 98.6%, padded:decimal 0.5%, money:$ 0.5%, integer 0.5% | **money as formatted text**; untrimmed whitespace |
| `L3_ALOWD` | TEXT | text 100% |  |  | 266 | 2–8 | decimal 97.1%, padded:decimal 1%, money:$ 1%, integer 1% | **money as formatted text**; untrimmed whitespace |
| `L3_PRCSG` | TEXT | null 78%, text 22% | 78% |  | 28 | 1–1 | alnum 98.9%, flag:yn 1%, other 0.1%, integer 0% |  |
| `L3_DX` | INTEGER | null 79%, integer 19.2%, text 1.8% | 79% |  | 4,763 | 1–6 | integer 91.6%, alnum 7.6%, code:dotted 0.8%, other 0% | **mixed storage types**; **INTEGER column holding text (codes?): leading zeros may be lost**; INTEGER values of varying digit length: if these are codes, leading zeros may have been lost |
| `L4_NPI` | TEXT | null 87.7%, text 12.3% | 87.7% |  | > 10,000 | 10–10 | integer 89.4%, digits:leading-zero 10.6% |  |
| `L4_TAX` | TEXT | null 87.7%, text 12.3% | 87.7% |  | > 10,000 | 9–9 | integer 90.2%, digits:leading-zero 9.8% |  |
| `L4_HCPCS` | TEXT | null 89%, text 11% | 89% |  | 2,972 | 5–5 | integer 83.5%, alnum 16.5%, digits:leading-zero 0% |  |
| `L4_PMT` | TEXT | text 100% |  |  | 215 | 2–8 | decimal 98.4%, padded:decimal 0.5%, money:$ 0.5%, integer 0.5% | **money as formatted text**; untrimmed whitespace |
| `L4_DED` | TEXT | text 100% |  |  | 56 | 2–8 | decimal 100%, money:$ 0%, padded:decimal 0%, integer 0% | **money as formatted text**; untrimmed whitespace |
| `L4_PRPYR` | TEXT | text 100% |  |  | 60 | 2–9 | decimal 100%, money:$ 0%, padded:decimal 0%, integer 0% | **money as formatted text**; untrimmed whitespace |
| `L4_COINS` | TEXT | text 100% |  |  | 73 | 2–8 | decimal 99.3%, padded:decimal 0.2%, money:$ 0.2%, integer 0.2% | **money as formatted text**; untrimmed whitespace |
| `L4_ALOWD` | TEXT | text 100% |  |  | 246 | 2–8 | decimal 98.4%, money:$ 0.5%, integer 0.5%, padded:decimal 0.5% | **money as formatted text**; untrimmed whitespace |
| `L4_PRCSG` | TEXT | null 87.7%, text 12.3% | 87.7% |  | 27 | 1–1 | alnum 98.9%, flag:yn 1%, other 0.1%, integer 0% |  |
| `L4_DX` | INTEGER | null 88.2%, integer 10.9%, text 0.8% | 88.2% |  | 3,922 | 1–6 | integer 93.1%, alnum 6.2%, code:dotted 0.7%, other 0% | **mixed storage types**; **INTEGER column holding text (codes?): leading zeros may be lost**; INTEGER values of varying digit length: if these are codes, leading zeros may have been lost |
| `L5_NPI` | TEXT | null 92.5%, text 7.5% | 92.5% |  | > 10,000 | 10–10 | integer 89.4%, digits:leading-zero 10.6% |  |
| `L5_TAX` | TEXT | null 92.5%, text 7.5% | 92.5% |  | > 10,000 | 9–9 | integer 90.1%, digits:leading-zero 9.9% |  |
| `L5_HCPCS` | TEXT | null 93.3%, text 6.7% | 93.3% |  | 2,501 | 5–5 | integer 84.8%, alnum 15.2%, digits:leading-zero 0% |  |
| `L5_PMT` | TEXT | text 100% |  |  | 199 | 2–8 | decimal 99%, money:$ 0.3%, integer 0.3%, padded:decimal 0.3% | **money as formatted text**; untrimmed whitespace |
| `L5_DED` | TEXT | text 100% |  |  | 51 | 2–8 | decimal 100%, padded:decimal 0%, integer 0%, money:$ 0% | **money as formatted text**; untrimmed whitespace |
| `L5_PRPYR` | TEXT | text 100% |  |  | 53 | 2–9 | decimal 100%, money:$ 0%, padded:decimal 0%, integer 0% | **money as formatted text**; untrimmed whitespace |
| `L5_COINS` | TEXT | text 100% |  |  | 72 | 2–8 | decimal 99.6%, padded:decimal 0.1%, integer 0.1%, money:$ 0.1% | **money as formatted text**; untrimmed whitespace |
| `L5_ALOWD` | TEXT | text 100% |  |  | 233 | 2–8 | decimal 99%, integer 0.3%, padded:decimal 0.3%, money:$ 0.3% | **money as formatted text**; untrimmed whitespace |
| `L5_PRCSG` | TEXT | null 92.5%, text 7.5% | 92.5% |  | 24 | 1–1 | alnum 98.8%, flag:yn 1%, other 0.1%, integer 0% |  |
| `L5_DX` | INTEGER | null 92.8%, integer 6.7%, text 0.5% | 92.8% |  | 3,200 | 2–6 | integer 93.5%, alnum 5.8%, code:dotted 0.7% | **mixed storage types**; **INTEGER column holding text (codes?): leading zeros may be lost**; INTEGER values of varying digit length: if these are codes, leading zeros may have been lost |
| `L6_NPI` | TEXT | null 95.3%, text 4.7% | 95.3% |  | > 10,000 | 10–10 | integer 89.3%, digits:leading-zero 10.7% |  |
| `L6_TAX` | TEXT | null 95.3%, text 4.7% | 95.3% |  | > 10,000 | 9–9 | integer 90.2%, digits:leading-zero 9.8% |  |
| `L6_HCPCS` | TEXT | null 95.7%, text 4.3% | 95.7% |  | 2,108 | 5–5 | integer 85.2%, alnum 14.8%, digits:leading-zero 0% |  |
| `L6_PMT` | TEXT | text 100% |  |  | 180 | 2–8 | decimal 99.4%, integer 0.2%, padded:decimal 0.2%, money:$ 0.2% | **money as formatted text**; untrimmed whitespace |
| `L6_DED` | TEXT | text 100% |  |  | 49 | 2–8 | decimal 100%, padded:decimal 0%, money:$ 0%, integer 0% | **money as formatted text**; untrimmed whitespace |
| `L6_PRPYR` | TEXT | text 100% |  |  | 40 | 2–8 | decimal 100%, padded:decimal 0%, integer 0%, money:$ 0% | **money as formatted text**; untrimmed whitespace |
| `L6_COINS` | TEXT | text 100% |  |  | 70 | 2–8 | decimal 99.8%, money:$ 0.1%, padded:decimal 0.1%, integer 0.1% | **money as formatted text**; untrimmed whitespace |
| `L6_ALOWD` | TEXT | text 100% |  |  | 212 | 2–8 | decimal 99.4%, money:$ 0.2%, integer 0.2%, padded:decimal 0.2% | **money as formatted text**; untrimmed whitespace |
| `L6_PRCSG` | TEXT | null 95.3%, text 4.7% | 95.3% |  | 23 | 1–1 | alnum 98.9%, flag:yn 1%, other 0.1%, integer 0% |  |
| `L6_DX` | INTEGER | null 95.4%, integer 4.3%, text 0.3% | 95.4% |  | 2,699 | 2–6 | integer 93.6%, alnum 5.8%, code:dotted 0.6% | **mixed storage types**; **INTEGER column holding text (codes?): leading zeros may be lost**; INTEGER values of varying digit length: if these are codes, leading zeros may have been lost |
| `L7_NPI` | TEXT | null 97.4%, text 2.6% | 97.4% |  | > 10,000 | 10–10 | integer 89.3%, digits:leading-zero 10.7% |  |
| `L7_TAX` | TEXT | null 97.4%, text 2.6% | 97.4% |  | > 10,000 | 9–9 | integer 90.2%, digits:leading-zero 9.8% |  |
| `L7_HCPCS` | TEXT | null 97.7%, text 2.3% | 97.7% |  | 1,653 | 5–5 | integer 85.4%, alnum 14.6%, digits:leading-zero 0% |  |
| `L7_PMT` | TEXT | text 100% |  |  | 169 | 2–8 | decimal 99.7%, integer 0.1%, money:$ 0.1%, padded:decimal 0.1% | **money as formatted text**; untrimmed whitespace |
| `L7_DED` | TEXT | text 100% |  |  | 34 | 2–8 | decimal 100%, padded:decimal 0%, integer 0%, money:$ 0% | **money as formatted text**; untrimmed whitespace |
| `L7_PRPYR` | TEXT | text 100% |  |  | 30 | 2–8 | decimal 100%, padded:decimal 0%, money:$ 0%, integer 0% | **money as formatted text**; untrimmed whitespace |
| `L7_COINS` | TEXT | text 100% |  |  | 61 | 2–8 | decimal 99.9%, money:$ 0%, integer 0%, padded:decimal 0% | **money as formatted text**; untrimmed whitespace |
| `L7_ALOWD` | TEXT | text 100% |  |  | 189 | 2–8 | decimal 99.7%, integer 0.1%, money:$ 0.1%, padded:decimal 0.1% | **money as formatted text**; untrimmed whitespace |
| `L7_PRCSG` | TEXT | null 97.4%, text 2.6% | 97.4% |  | 20 | 1–1 | alnum 98.7%, flag:yn 1.1%, other 0.1%, integer 0% |  |
| `L7_DX` | INTEGER | null 97.5%, integer 2.3%, text 0.2% | 97.5% |  | 2,005 | 2–6 | integer 93.4%, alnum 5.9%, code:dotted 0.7% | **mixed storage types**; **INTEGER column holding text (codes?): leading zeros may be lost**; INTEGER values of varying digit length: if these are codes, leading zeros may have been lost |
| `L8_NPI` | TEXT | null 98.3%, text 1.7% | 98.3% |  | > 10,000 | 10–10 | integer 89.2%, digits:leading-zero 10.8% |  |
| `L8_TAX` | TEXT | null 98.3%, text 1.7% | 98.3% |  | > 10,000 | 9–9 | integer 90.2%, digits:leading-zero 9.8% |  |
| `L8_HCPCS` | TEXT | null 98.5%, text 1.5% | 98.5% |  | 1,424 | 5–5 | integer 85.4%, alnum 14.6%, digits:leading-zero 0% |  |
| `L8_PMT` | TEXT | text 100% |  |  | 149 | 2–8 | decimal 99.8%, padded:decimal 0.1%, money:$ 0.1%, integer 0.1% | **money as formatted text**; untrimmed whitespace |
| `L8_DED` | TEXT | text 100% |  |  | 30 | 2–8 | decimal 100%, integer 0%, money:$ 0%, padded:decimal 0% | **money as formatted text**; untrimmed whitespace |
| `L8_PRPYR` | TEXT | text 100% |  |  | 30 | 2–8 | decimal 100%, integer 0%, padded:decimal 0%, money:$ 0% | **money as formatted text**; untrimmed whitespace |
| `L8_COINS` | TEXT | text 100% |  |  | 63 | 2–8 | decimal 99.9%, money:$ 0%, integer 0%, padded:decimal 0% | **money as formatted text**; untrimmed whitespace |
| `L8_ALOWD` | TEXT | text 100% |  |  | 164 | 2–8 | decimal 99.8%, padded:decimal 0.1%, money:$ 0.1%, integer 0.1% | **money as formatted text**; untrimmed whitespace |
| `L8_PRCSG` | TEXT | null 98.3%, text 1.7% | 98.3% |  | 22 | 1–1 | alnum 99%, flag:yn 0.9%, other 0.1%, integer 0% |  |
| `L8_DX` | INTEGER | null 98.3%, integer 1.6%, text 0.1% | 98.3% |  | 1,670 | 2–6 | integer 93.5%, alnum 5.8%, code:dotted 0.7% | **mixed storage types**; **INTEGER column holding text (codes?): leading zeros may be lost**; INTEGER values of varying digit length: if these are codes, leading zeros may have been lost |
| `L9_NPI` | TEXT | null 98.8%, text 1.2% | 98.8% |  | > 10,000 | 10–10 | integer 89.3%, digits:leading-zero 10.7% |  |
| `L9_TAX` | TEXT | null 98.8%, text 1.2% | 98.8% |  | 9,256 | 9–9 | integer 90.2%, digits:leading-zero 9.8% |  |
| `L9_HCPCS` | TEXT | null 98.9%, text 1.1% | 98.9% |  | 1,183 | 5–5 | integer 85.7%, alnum 14.3% |  |
| `L9_PMT` | TEXT | text 100% |  |  | 131 | 2–8 | decimal 99.9%, padded:decimal 0.1%, money:$ 0%, integer 0% | **money as formatted text**; untrimmed whitespace |
| `L9_DED` | TEXT | text 100% |  |  | 21 | 2–8 | decimal 100%, integer 0%, padded:decimal 0%, money:$ 0% | **money as formatted text**; untrimmed whitespace |
| `L9_PRPYR` | TEXT | text 100% |  |  | 23 | 2–8 | decimal 100%, integer 0%, padded:decimal 0%, money:$ 0% | **money as formatted text**; untrimmed whitespace |
| `L9_COINS` | TEXT | text 100% |  |  | 53 | 2–8 | decimal 99.9%, padded:decimal 0%, integer 0%, money:$ 0% | **money as formatted text**; untrimmed whitespace |
| `L9_ALOWD` | TEXT | text 100% |  |  | 165 | 2–8 | decimal 99.8%, money:$ 0.1%, padded:decimal 0.1%, integer 0% | **money as formatted text**; untrimmed whitespace |
| `L9_PRCSG` | TEXT | null 98.8%, text 1.2% | 98.8% |  | 20 | 1–1 | alnum 98.9%, flag:yn 1%, other 0.1%, integer 0% |  |
| `L9_DX` | INTEGER | null 98.9%, integer 1.1%, text 0.1% | 98.9% |  | 1,417 | 2–6 | integer 93.3%, alnum 6.1%, code:dotted 0.5% | **mixed storage types**; **INTEGER column holding text (codes?): leading zeros may be lost**; INTEGER values of varying digit length: if these are codes, leading zeros may have been lost |
| `L10_NPI` | TEXT | null 99.2%, text 0.8% | 99.2% |  | 8,336 | 10–10 | integer 89.2%, digits:leading-zero 10.8% |  |
| `L10_TAX` | TEXT | null 99.2%, text 0.8% | 99.2% |  | 6,925 | 9–9 | integer 90.2%, digits:leading-zero 9.8% |  |
| `L10_HCPCS` | TEXT | null 99.3%, text 0.7% | 99.3% |  | 1,022 | 5–5 | integer 85.8%, alnum 14.2%, digits:leading-zero 0% |  |
| `L10_PMT` | TEXT | text 100% |  |  | 132 | 2–8 | decimal 99.9%, integer 0%, money:$ 0%, padded:decimal 0% | **money as formatted text**; untrimmed whitespace |
| `L10_DED` | TEXT | text 100% |  |  | 23 | 2–7 | decimal 100%, padded:decimal 0%, money:$ 0%, integer 0% | **money as formatted text**; untrimmed whitespace |
| `L10_PRPYR` | TEXT | text 100% |  |  | 21 | 2–8 | decimal 100%, money:$ 0%, integer 0%, padded:decimal 0% | **money as formatted text**; untrimmed whitespace |
| `L10_COINS` | TEXT | text 100% |  |  | 50 | 2–8 | decimal 100%, padded:decimal 0%, money:$ 0%, integer 0% | **money as formatted text**; untrimmed whitespace |
| `L10_ALOWD` | TEXT | text 100% |  |  | 141 | 2–8 | decimal 99.9%, integer 0%, money:$ 0%, padded:decimal 0% | **money as formatted text**; untrimmed whitespace |
| `L10_PRCSG` | TEXT | null 99.2%, text 0.8% | 99.2% |  | 20 | 1–1 | alnum 98.8%, flag:yn 0.9%, other 0.2%, integer 0% |  |
| `L10_DX` | INTEGER | null 99.2%, integer 0.7%, text 0.1% | 99.2% |  | 1,232 | 2–6 | integer 93.2%, alnum 6.1%, code:dotted 0.7% | **mixed storage types**; **INTEGER column holding text (codes?): leading zeros may be lost**; INTEGER values of varying digit length: if these are codes, leading zeros may have been lost |
| `L11_NPI` | TEXT | null 99.4%, text 0.6% | 99.4% |  | 5,867 | 10–10 | integer 89.2%, digits:leading-zero 10.8% |  |
| `L11_TAX` | TEXT | null 99.4%, text 0.6% | 99.4% |  | 5,029 | 9–9 | integer 90.3%, digits:leading-zero 9.7% |  |
| `L11_HCPCS` | TEXT | null 99.5%, text 0.5% | 99.5% |  | 855 | 5–5 | integer 85.4%, alnum 14.6% |  |
| `L11_PMT` | TEXT | text 100% |  |  | 118 | 2–8 | decimal 99.9%, integer 0%, money:$ 0%, padded:decimal 0% | **money as formatted text**; untrimmed whitespace |
| `L11_DED` | TEXT | text 100% |  |  | 16 | 2–6 | decimal 100%, integer 0%, money:$ 0% | **money as formatted text** |
| `L11_PRPYR` | TEXT | text 100% |  |  | 16 | 2–7 | decimal 100%, integer 0%, money:$ 0% | **money as formatted text** |
| `L11_COINS` | TEXT | text 100% |  |  | 46 | 2–8 | decimal 100%, money:$ 0%, integer 0%, padded:decimal 0% | **money as formatted text**; untrimmed whitespace |
| `L11_ALOWD` | TEXT | text 100% |  |  | 138 | 2–8 | decimal 99.9%, padded:decimal 0%, integer 0%, money:$ 0% | **money as formatted text**; untrimmed whitespace |
| `L11_PRCSG` | TEXT | null 99.4%, text 0.6% | 99.4% |  | 20 | 1–1 | alnum 98.7%, flag:yn 1.1%, other 0.2%, integer 0% |  |
| `L11_DX` | INTEGER | null 99.4%, integer 0.5%, text 0% | 99.4% |  | 1,049 | 2–6 | integer 93.3%, alnum 6.2%, code:dotted 0.6% | **mixed storage types**; **INTEGER column holding text (codes?): leading zeros may be lost**; INTEGER values of varying digit length: if these are codes, leading zeros may have been lost |
| `L12_NPI` | TEXT | null 99.6%, text 0.4% | 99.6% |  | 4,416 | 10–10 | integer 88.7%, digits:leading-zero 11.3% |  |
| `L12_TAX` | TEXT | null 99.6%, text 0.4% | 99.6% |  | 3,865 | 9–9 | integer 90%, digits:leading-zero 10% |  |
| `L12_HCPCS` | TEXT | null 99.6%, text 0.4% | 99.6% |  | 749 | 5–5 | integer 86.5%, alnum 13.5%, digits:leading-zero 0.1% |  |
| `L12_PMT` | TEXT | text 100% |  |  | 105 | 2–8 | decimal 99.9%, integer 0%, money:$ 0%, padded:decimal 0% | **money as formatted text**; untrimmed whitespace |
| `L12_DED` | TEXT | text 100% |  |  | 16 | 2–7 | decimal 100%, money:$ 0%, padded:decimal 0%, integer 0% | **money as formatted text**; untrimmed whitespace |
| `L12_PRPYR` | TEXT | text 100% |  |  | 9 | 4–6 | decimal 100% | code domain: `0.00`, `10.00`, `20.00`, `100.00`, `200.00`, `30.00`, `40.00`, `60.00`, `70.00` |
| `L12_COINS` | TEXT | text 100% |  |  | 43 | 2–8 | decimal 100%, integer 0%, padded:decimal 0%, money:$ 0% | **money as formatted text**; untrimmed whitespace |
| `L12_ALOWD` | TEXT | text 100% |  |  | 124 | 2–8 | decimal 99.9%, integer 0%, padded:decimal 0%, money:$ 0% | **money as formatted text**; untrimmed whitespace |
| `L12_PRCSG` | TEXT | null 99.6%, text 0.4% | 99.6% |  | 20 | 1–1 | alnum 98.9%, flag:yn 0.9%, other 0.2%, integer 0% |  |
| `L12_DX` | INTEGER | null 99.6%, integer 0.4%, text 0% | 99.6% |  | 929 | 2–6 | integer 92.3%, alnum 6.9%, code:dotted 0.9% | **mixed storage types**; **INTEGER column holding text (codes?): leading zeros may be lost**; INTEGER values of varying digit length: if these are codes, leading zeros may have been lost |
| `L13_NPI` | TEXT | null 99.7%, text 0.3% | 99.7% |  | 3,125 | 10–10 | integer 88.6%, digits:leading-zero 11.4% |  |
| `L13_TAX` | TEXT | null 99.7%, text 0.3% | 99.7% |  | 2,800 | 9–9 | integer 90%, digits:leading-zero 10% |  |
| `L13_HCPCS` | TEXT | null 99.7%, text 0.3% | 99.7% |  | 634 | 5–5 | integer 86.1%, alnum 13.9%, digits:leading-zero 0% |  |
| `L13_PMT` | TEXT | text 100% |  |  | 94 | 2–8 | decimal 100%, padded:decimal 0%, integer 0%, money:$ 0% | **money as formatted text**; untrimmed whitespace |
| `L13_DED` | TEXT | text 100% |  |  | 16 | 2–7 | decimal 100%, padded:decimal 0%, integer 0% | untrimmed whitespace |
| `L13_PRPYR` | TEXT | text 100% |  |  | 9 | 2–7 | decimal 100%, integer 0% | code domain: `0.00`, `10.00`, `10`, `1200.00`, `200`, `200.00`, `60.00`, `70.00`, `90.00` |
| `L13_COINS` | TEXT | text 100% |  |  | 41 | 2–8 | decimal 100%, money:$ 0%, integer 0%, padded:decimal 0% | **money as formatted text**; untrimmed whitespace |
| `L13_ALOWD` | TEXT | text 100% |  |  | 117 | 2–8 | decimal 100%, padded:decimal 0%, money:$ 0%, integer 0% | **money as formatted text**; untrimmed whitespace |
| `L13_PRCSG` | TEXT | null 99.7%, text 0.3% | 99.7% |  | 19 | 1–1 | alnum 98.8%, flag:yn 0.8%, other 0.3% |  |
| `L13_DX` | INTEGER | null 99.7%, integer 0.3%, text 0% | 99.7% |  | 760 | 2–6 | integer 92.9%, alnum 6.4%, code:dotted 0.7% | **mixed storage types**; **INTEGER column holding text (codes?): leading zeros may be lost**; INTEGER values of varying digit length: if these are codes, leading zeros may have been lost |
| `LOAD_TS` | TEXT | text 100% |  |  | > 10,000 | 19–19 | timestamp 100% | **near-unique: 14,181 repeated values (duplicate loads?)** |
| `LOAD_BATCH` | TEXT | text 100% |  |  | 76 | 10–17 | other 100% |  |
| `DEL_FLG` | TEXT | text 66.8%, null 33.2% | 33.2% | 33.2% | 3 | 0–1 | flag:yn 50.3%, blank 49.7% | code domain: ``, `N`, `Y` |

## `CLM_HDR_ALL` (219,214 rows)

| Column | Declared | Stored as | Null | Blank | Distinct | Len | Formats | Flags |
|---|---|---|---:|---:|---:|---|---|---|
| `REC_ID` | INTEGER | integer 100% |  |  | > 10,000 | 1–6 | integer 100% | INTEGER values of varying digit length: if these are codes, leading zeros may have been lost |
| `CLM_TYP` | TEXT | text 100% |  |  | 8 | 1–5 | alnum 91.7%, digits:leading-zero 8.3% | code domain: `OP`, `OUTPT`, `O`, `02`, `IP`, `INPT`, `I`, `01` |
| `CLM_ID` | TEXT | text 100% |  |  | > 10,000 | 15–15 | integer 99.9%, digits:leading-zero 0.1% | **near-unique: 7,862 repeated values (duplicate loads?)** |
| `SEG` | TEXT | text 100% |  |  | 2 | 1–1 | integer 100% | code domain: `1`, `2` |
| `MBR_ID` | TEXT | text 100% |  |  | > 10,000 | 16–18 | alnum 97.6%, padded:alnum 2.4%, integer 0%, padded:integer 0% | untrimmed whitespace |
| `DOB` | TEXT | text 100% |  |  | 3,452 | 6–10 | date:yyyymmdd 69.8%, date:m/d/yy 10.1%, date:yyyy-mm-dd 10.1%, date:m/d/yyyy 10% | **mixed date formats** |
| `SEX` | TEXT | text 100% |  |  | 8 | 1–6 | integer 69.9%, alnum 30.1% | code domain: `2`, `1`, `female`, `Female`, `F`, `M`, `male`, `Male` |
| `ST` | TEXT | text 100% |  |  | 107 | 2–2 | integer 66.9%, alnum 20%, digits:leading-zero 13.1% |  |
| `FROM_DT` | TEXT | text 99.7%, null 0.3% | 0.3% | 0.3% | 4,427 | 0–10 | date:yyyymmdd 69.3%, date:m/d/yyyy 10%, date:yyyy-mm-dd 9.9%, date:m/d/yy 9.8%, +3 more | **mixed date formats**; sentinel values |
| `THRU_DT` | TEXT | text 99.7%, null 0.3% | 0.3% | 0.3% | 4,389 | 0–10 | date:yyyymmdd 69.3%, date:yyyy-mm-dd 10%, date:m/d/yy 9.9%, date:m/d/yyyy 9.8%, +3 more | **mixed date formats**; sentinel values |
| `PRVDR` | TEXT | text 100% |  |  | 6,166 | 6–6 | alnum 100% |  |
| `AMT_PMT` | TEXT | text 100% |  |  | 435 | 2–10 | decimal 80.8%, padded:decimal 6.4%, integer 6.4%, money:$ 6.3%, +1 more | **money as formatted text**; untrimmed whitespace |
| `AMT_PRMRY_PYR` | TEXT | text 100% |  |  | 174 | 2–10 | decimal 99.8%, padded:decimal 0.1%, integer 0.1%, money:$ 0.1% | **money as formatted text**; untrimmed whitespace |
| `AT_NPI` | TEXT | text 97.9%, null 2.1% | 2.1% |  | > 10,000 | 10–10 | integer 90.7%, digits:leading-zero 9.3% |  |
| `OP_NPI` | TEXT | null 79.7%, text 20.3% | 79.7% |  | > 10,000 | 10–10 | integer 90.5%, digits:leading-zero 9.5% |  |
| `OT_NPI` | TEXT | null 69.1%, text 30.9% | 69.1% |  | > 10,000 | 10–10 | integer 89.9%, digits:leading-zero 10.1% |  |
| `ADMIT_DT` | TEXT | text 76.8%, null 23.2% | 23.2% | 22.8% | 3,575 | 0–10 | sentinel:n/a 30.1%, sentinel:zeros 30%, blank 29.7%, date:yyyymmdd 7.1%, +3 more | **mixed date formats**; sentinel values |
| `ADMIT_DX` | INTEGER | null 69.6%, integer 26.1%, text 4.3% | 69.6% |  | 3,303 | 2–6 | integer 85.8%, alnum 12.8%, code:dotted 1.4% | **mixed storage types**; **INTEGER column holding text (codes?): leading zeros may be lost**; INTEGER values of varying digit length: if these are codes, leading zeros may have been lost |
| `AMT_PASSTHRU` | TEXT | null 92.2%, text 7.8% | 92.2% |  | 57 | 2–8 | decimal 94%, money:$ 2.1%, integer 2%, padded:decimal 1.9% | **money as formatted text**; untrimmed whitespace |
| `AMT_IP_DED` | TEXT | null 92.4%, text 7.6% | 92.4% |  | 12 | 4–9 | decimal 80%, money:$ 6.8%, padded:decimal 6.6%, integer 6.6% | code domain: `1024.00`, `1068.00`, `1100.00`, `1024`, `1024.0`, `$1,024.00`, `$1,068.00`, `1068`, `1068.0`, `$1,100.00`, `1100.0`, `1100`; **money as formatted text**; untrimmed whitespace |
| `AMT_PTA_COINS` | TEXT | null 92.2%, text 7.8% | 92.2% |  | 70 | 3–10 | decimal 99.5%, integer 0.2%, padded:decimal 0.2%, money:$ 0.2% | **money as formatted text**; untrimmed whitespace |
| `AMT_BLOOD_DED` | TEXT | text 100% |  |  | 24 | 3–8 | decimal 100%, integer 0%, padded:decimal 0%, money:$ 0% | **money as formatted text**; untrimmed whitespace |
| `AMT_PTB_DED` | TEXT | text 92.2%, null 7.8% | 7.8% |  | 45 | 2–8 | decimal 99.1%, padded:decimal 0.3%, money:$ 0.3%, integer 0.3% | **money as formatted text**; untrimmed whitespace |
| `AMT_PTB_COINS` | TEXT | text 92.2%, null 7.8% | 7.8% |  | 81 | 2–9 | decimal 86.7%, money:$ 4.5%, integer 4.4%, padded:decimal 4.4% | **money as formatted text**; untrimmed whitespace |
| `UTIL_DAYS` | TEXT | null 92.2%, text 7.8% | 92.2% |  | 123 | 1–4 | decimal 50.1%, integer 49.9% |  |
| `DISCH_DT` | TEXT | text 77%, null 23% | 23% | 23% | 3,563 | 0–10 | sentinel:zeros 30%, blank 29.9%, sentinel:n/a 29.9%, date:yyyymmdd 7%, +3 more | **mixed date formats**; sentinel values |
| `DRG` | TEXT | null 92.2%, text 7.8% | 92.2% |  | 729 | 3–3 | integer 91.5%, digits:leading-zero 8.2%, alnum 0.3% |  |
| `DX1` | INTEGER | integer 79.9%, text 19.5%, null 0.6% | 0.6% |  | > 10,000 | 2–6 | integer 80.4%, alnum 17.7%, code:dotted 1.8%, other 0.1% | **mixed storage types**; **INTEGER column holding text (codes?): leading zeros may be lost**; INTEGER values of varying digit length: if these are codes, leading zeros may have been lost |
| `DX2` | INTEGER | integer 53.3%, null 34.8%, text 11.9% | 34.8% |  | 4,503 | 2–6 | integer 81.8%, alnum 16.4%, code:dotted 1.8% | **mixed storage types**; **INTEGER column holding text (codes?): leading zeros may be lost**; INTEGER values of varying digit length: if these are codes, leading zeros may have been lost |
| `DX3` | INTEGER | null 56.2%, integer 36.4%, text 7.5% | 56.2% |  | 4,025 | 2–6 | integer 83%, alnum 15.3%, code:dotted 1.8% | **mixed storage types**; **INTEGER column holding text (codes?): leading zeros may be lost**; INTEGER values of varying digit length: if these are codes, leading zeros may have been lost |
| `DX4` | INTEGER | null 70.3%, integer 24.9%, text 4.8% | 70.3% |  | 3,552 | 2–6 | integer 83.9%, alnum 14.6%, code:dotted 1.6% | **mixed storage types**; **INTEGER column holding text (codes?): leading zeros may be lost**; INTEGER values of varying digit length: if these are codes, leading zeros may have been lost |
| `DX5` | INTEGER | null 79.8%, integer 17%, text 3.1% | 79.8% |  | 3,162 | 2–6 | integer 84.4%, alnum 13.9%, code:dotted 1.6% | **mixed storage types**; **INTEGER column holding text (codes?): leading zeros may be lost**; INTEGER values of varying digit length: if these are codes, leading zeros may have been lost |
| `DX6` | INTEGER | null 84.7%, integer 12.9%, text 2.3% | 84.7% |  | 2,777 | 2–6 | integer 84.7%, alnum 13.9%, code:dotted 1.4% | **mixed storage types**; **INTEGER column holding text (codes?): leading zeros may be lost**; INTEGER values of varying digit length: if these are codes, leading zeros may have been lost |
| `DX7` | INTEGER | null 87.9%, integer 10.3%, text 1.8% | 87.9% |  | 2,571 | 2–6 | integer 85.2%, alnum 13.3%, code:dotted 1.5% | **mixed storage types**; **INTEGER column holding text (codes?): leading zeros may be lost**; INTEGER values of varying digit length: if these are codes, leading zeros may have been lost |
| `DX8` | INTEGER | null 90.2%, integer 8.4%, text 1.4% | 90.2% |  | 2,313 | 2–6 | integer 85.6%, alnum 12.9%, code:dotted 1.6% | **mixed storage types**; **INTEGER column holding text (codes?): leading zeros may be lost**; INTEGER values of varying digit length: if these are codes, leading zeros may have been lost |
| `DX9` | INTEGER | null 92%, integer 7%, text 1.1% | 92% |  | 2,099 | 2–6 | integer 86.7%, alnum 11.9%, code:dotted 1.4% | **mixed storage types**; **INTEGER column holding text (codes?): leading zeros may be lost**; INTEGER values of varying digit length: if these are codes, leading zeros may have been lost |
| `DX10` | INTEGER | null 99.2%, integer 0.7%, text 0.1% | 99.2% |  | 682 | 2–6 | integer 84.8%, alnum 13.7%, code:dotted 1.5% | **mixed storage types**; **INTEGER column holding text (codes?): leading zeros may be lost**; INTEGER values of varying digit length: if these are codes, leading zeros may have been lost |
| `PX1` | INTEGER | null 95.5%, integer 4.5% | 95.5% |  | 829 | 2–4 | integer 100% | INTEGER values of varying digit length: if these are codes, leading zeros may have been lost |
| `PX2` | INTEGER | null 97.4%, integer 2.4%, text 0.3% | 97.4% |  | 1,053 | 2–5 | integer 88.8%, alnum 11.2% | **mixed storage types**; **INTEGER column holding text (codes?): leading zeros may be lost**; INTEGER values of varying digit length: if these are codes, leading zeros may have been lost |
| `PX3` | INTEGER | null 98.3%, integer 1.5%, text 0.2% | 98.3% |  | 871 | 2–5 | integer 88.2%, alnum 11.8% | **mixed storage types**; **INTEGER column holding text (codes?): leading zeros may be lost**; INTEGER values of varying digit length: if these are codes, leading zeros may have been lost |
| `PX4` | INTEGER | null 98.9%, integer 1%, text 0.1% | 98.9% |  | 685 | 3–5 | integer 88.8%, alnum 11.2% | **mixed storage types**; **INTEGER column holding text (codes?): leading zeros may be lost**; INTEGER values of varying digit length: if these are codes, leading zeros may have been lost |
| `PX5` | INTEGER | null 99.2%, integer 0.7%, text 0.1% | 99.2% |  | 559 | 3–5 | integer 86.9%, alnum 13.1% | **mixed storage types**; **INTEGER column holding text (codes?): leading zeros may be lost**; INTEGER values of varying digit length: if these are codes, leading zeros may have been lost |
| `PX6` | INTEGER | null 99.5%, integer 0.5%, text 0.1% | 99.5% |  | 423 | 3–5 | integer 87.5%, alnum 12.5% | **mixed storage types**; **INTEGER column holding text (codes?): leading zeros may be lost**; INTEGER values of varying digit length: if these are codes, leading zeros may have been lost |
| `HCPCS1` | TEXT | text 75.3%, null 24.7% | 24.7% |  | 1,785 | 2–5 | integer 88%, alnum 12%, digits:leading-zero 0% |  |
| `HCPCS2` | TEXT | text 52.5%, null 47.5% | 47.5% |  | 2,304 | 4–5 | integer 87.6%, alnum 12.4%, digits:leading-zero 0% |  |
| `HCPCS3` | TEXT | null 61.4%, text 38.6% | 61.4% |  | 2,234 | 4–5 | integer 88%, alnum 12%, digits:leading-zero 0% |  |
| `HCPCS4` | TEXT | null 68.3%, text 31.7% | 68.3% |  | 2,048 | 4–5 | integer 86.6%, alnum 13.4%, digits:leading-zero 0% |  |
| `HCPCS5` | TEXT | null 73.7%, text 26.3% | 73.7% |  | 1,980 | 5–5 | integer 85.5%, alnum 14.5%, digits:leading-zero 0% |  |
| `HCPCS6` | TEXT | null 77.9%, text 22.1% | 77.9% |  | 1,837 | 5–5 | integer 84%, alnum 15.9%, digits:leading-zero 0% |  |
| `HCPCS7` | TEXT | null 81.4%, text 18.6% | 81.4% |  | 1,704 | 5–5 | integer 82.5%, alnum 17.4%, digits:leading-zero 0% |  |
| `HCPCS8` | TEXT | null 84.1%, text 15.9% | 84.1% |  | 1,604 | 3–5 | integer 81%, alnum 19%, digits:leading-zero 0% |  |
| `HCPCS9` | TEXT | null 86.1%, text 13.9% | 86.1% |  | 1,547 | 5–5 | integer 79.6%, alnum 20.3%, digits:leading-zero 0.1% |  |
| `HCPCS10` | TEXT | null 87.8%, text 12.2% | 87.8% |  | 1,508 | 4–5 | integer 78.8%, alnum 21.1%, digits:leading-zero 0% |  |
| `HCPCS11` | TEXT | null 89.2%, text 10.8% | 89.2% |  | 1,391 | 4–5 | integer 77.7%, alnum 22.2%, digits:leading-zero 0.1% |  |
| `HCPCS12` | TEXT | null 90.3%, text 9.7% | 90.3% |  | 1,312 | 5–5 | integer 75.9%, alnum 24.1%, digits:leading-zero 0% |  |
| `HCPCS13` | TEXT | null 91.4%, text 8.6% | 91.4% |  | 1,285 | 5–5 | integer 75.4%, alnum 24.6%, digits:leading-zero 0% |  |
| `HCPCS14` | TEXT | null 92.2%, text 7.8% | 92.2% |  | 1,186 | 4–5 | integer 73.3%, alnum 26.7%, digits:leading-zero 0% |  |
| `HCPCS15` | TEXT | null 92.9%, text 7.1% | 92.9% |  | 1,144 | 5–5 | integer 72.4%, alnum 27.6%, digits:leading-zero 0% |  |
| `HCPCS16` | TEXT | null 93.5%, text 6.5% | 93.5% |  | 1,071 | 5–5 | integer 71.2%, alnum 28.7%, digits:leading-zero 0.1% |  |
| `HCPCS17` | TEXT | null 94.1%, text 5.9% | 94.1% |  | 1,008 | 5–5 | integer 70.7%, alnum 29.2%, digits:leading-zero 0.1% |  |
| `HCPCS18` | TEXT | null 94.6%, text 5.4% | 94.6% |  | 987 | 4–5 | integer 68.7%, alnum 31.3%, digits:leading-zero 0.1% |  |
| `HCPCS19` | TEXT | null 95%, text 5% | 95% |  | 909 | 5–5 | integer 67.5%, alnum 32.5%, digits:leading-zero 0.1% |  |
| `HCPCS20` | TEXT | null 95.4%, text 4.6% | 95.4% |  | 867 | 5–5 | integer 66%, alnum 33.9%, digits:leading-zero 0% |  |
| `HCPCS21` | TEXT | null 95.7%, text 4.3% | 95.7% |  | 823 | 4–5 | integer 65.8%, alnum 34.2%, digits:leading-zero 0.1% |  |
| `HCPCS22` | TEXT | null 96%, text 4% | 96% |  | 789 | 5–5 | integer 64.6%, alnum 35.4%, digits:leading-zero 0% |  |
| `HCPCS23` | TEXT | null 96.2%, text 3.8% | 96.2% |  | 761 | 4–5 | integer 63.3%, alnum 36.7%, digits:leading-zero 0.1% |  |
| `HCPCS24` | TEXT | null 96.4%, text 3.6% | 96.4% |  | 720 | 4–5 | integer 62.2%, alnum 37.8%, digits:leading-zero 0% |  |
| `HCPCS25` | TEXT | null 96.6%, text 3.4% | 96.6% |  | 629 | 4–5 | integer 61.4%, alnum 38.6%, digits:leading-zero 0% |  |
| `HCPCS26` | TEXT | null 96.8%, text 3.2% | 96.8% |  | 652 | 5–5 | integer 60.4%, alnum 39.6%, digits:leading-zero 0% |  |
| `HCPCS27` | TEXT | null 97%, text 3% | 97% |  | 609 | 5–5 | integer 59.5%, alnum 40.5% |  |
| `HCPCS28` | TEXT | null 97.1%, text 2.9% | 97.1% |  | 571 | 5–5 | integer 59.2%, alnum 40.8% |  |
| `HCPCS29` | TEXT | null 97.3%, text 2.7% | 97.3% |  | 561 | 4–5 | integer 56.8%, alnum 43.2%, digits:leading-zero 0% |  |
| `HCPCS30` | TEXT | null 97.4%, text 2.6% | 97.4% |  | 525 | 4–5 | integer 56.6%, alnum 43.3%, digits:leading-zero 0.1% |  |
| `HCPCS31` | TEXT | null 97.6%, text 2.4% | 97.6% |  | 502 | 5–5 | integer 55.6%, alnum 44.4%, digits:leading-zero 0% |  |
| `HCPCS32` | TEXT | null 97.7%, text 2.3% | 97.7% |  | 503 | 5–5 | integer 55.2%, alnum 44.8%, digits:leading-zero 0.1% |  |
| `HCPCS33` | TEXT | null 97.8%, text 2.2% | 97.8% |  | 487 | 5–5 | integer 53.3%, alnum 46.6%, digits:leading-zero 0.1% |  |
| `HCPCS34` | TEXT | null 97.9%, text 2.1% | 97.9% |  | 426 | 5–5 | integer 52.8%, alnum 47.2% |  |
| `HCPCS35` | TEXT | null 98%, text 2% | 98% |  | 433 | 4–5 | integer 53.1%, alnum 46.8%, digits:leading-zero 0% |  |
| `HCPCS36` | TEXT | null 98.1%, text 1.9% | 98.1% |  | 409 | 4–5 | integer 52.7%, alnum 47.2%, digits:leading-zero 0.1% |  |
| `HCPCS37` | TEXT | null 98.2%, text 1.8% | 98.2% |  | 381 | 5–5 | integer 51.8%, alnum 48.2% |  |
| `HCPCS38` | TEXT | null 98.3%, text 1.7% | 98.3% |  | 372 | 5–5 | integer 52.6%, alnum 47.4%, digits:leading-zero 0% |  |
| `HCPCS39` | TEXT | null 98.4%, text 1.6% | 98.4% |  | 356 | 5–5 | integer 52.9%, alnum 47.1% |  |
| `HCPCS40` | TEXT | null 98.5%, text 1.5% | 98.5% |  | 327 | 5–5 | integer 52.1%, alnum 47.9% |  |
| `HCPCS41` | TEXT | null 98.6%, text 1.4% | 98.6% |  | 331 | 4–5 | integer 50.7%, alnum 49.3%, digits:leading-zero 0.1% |  |
| `HCPCS42` | TEXT | null 98.7%, text 1.3% | 98.7% |  | 320 | 5–5 | integer 51.7%, alnum 48.3% |  |
| `HCPCS43` | TEXT | null 98.7%, text 1.3% | 98.7% |  | 298 | 4–5 | integer 50.3%, alnum 49.6%, digits:leading-zero 0% |  |
| `HCPCS44` | TEXT | null 98.8%, text 1.2% | 98.8% |  | 280 | 5–5 | integer 51.2%, alnum 48.8% |  |
| `HCPCS45` | TEXT | null 100% | 100% |  | 0 |  |  |  |
| `LOAD_TS` | TEXT | text 100% |  |  | > 10,000 | 19–19 | timestamp 100% | **near-unique: 1,391 repeated values (duplicate loads?)** |
| `LOAD_BATCH` | TEXT | text 100% |  |  | 76 | 10–17 | other 100% |  |
| `DEL_FLG` | TEXT | text 66.5%, null 33.5% | 33.5% | 33.1% | 3 | 0–1 | flag:yn 50.3%, blank 49.7% | code domain: ``, `N`, `Y` |

## `MBR_MSTR` (57,773 rows)

| Column | Declared | Stored as | Null | Blank | Distinct | Len | Formats | Flags |
|---|---|---|---:|---:|---:|---|---|---|
| `REC_ID` | INTEGER | integer 100% |  |  | > 10,000 | 1–5 | integer 100% | INTEGER values of varying digit length: if these are codes, leading zeros may have been lost |
| `MBR_ID` | TEXT | text 100% |  |  | > 10,000 | 16–18 | alnum 99%, integer 0.5%, padded:alnum 0.4%, digits:leading-zero 0% | untrimmed whitespace |
| `YR` | INTEGER | integer 100% |  |  | 2 | 4–4 | integer 100% | code domain: `2008`, `2009` |
| `DOB` | TEXT | text 100% |  |  | 3,210 | 6–10 | date:yyyymmdd 70.2%, date:yyyy-mm-dd 10%, date:m/d/yy 9.9%, date:m/d/yyyy 9.9% | **mixed date formats** |
| `DOD` | TEXT | text 80.5%, null 19.5% | 19.5% | 19.4% | 99 | 0–10 | sentinel:n/a 24.8%, sentinel:zeros 24.6%, integer 24.5%, blank 24.2%, +4 more | **mixed date formats**; sentinel values |
| `SEX` | TEXT | text 100% |  |  | 8 | 1–6 | integer 70.2%, alnum 29.8% | code domain: `2`, `1`, `F`, `Female`, `female`, `Male`, `M`, `male` |
| `RACE` | TEXT | text 100% |  |  | 4 | 1–1 | integer 100% | code domain: `1`, `2`, `3`, `5` |
| `ESRD` | TEXT | text 100% |  |  | 6 | 1–1 | integer 78.6%, flag:yn 21.4% | code domain: `0`, `Y`, `N`, `n`, `y`, `1` |
| `ST` | TEXT | text 100% |  |  | 103 | 2–2 | integer 65.4%, alnum 19.9%, digits:leading-zero 14.6% |  |
| `CNTY` | TEXT | text 100% |  |  | 279 | 3–3 | integer 81.1%, digits:leading-zero 18.9% |  |
| `MOS_A` | TEXT | text 100% |  |  | 26 | 1–4 | integer 50%, decimal 50% |  |
| `MOS_B` | TEXT | text 100% |  |  | 26 | 1–4 | decimal 50%, integer 50% |  |
| `MOS_HMO` | TEXT | text 100% |  |  | 26 | 1–4 | integer 50.3%, decimal 49.7% |  |
| `MOS_D` | TEXT | text 100% |  |  | 26 | 2–4 | decimal 50.3%, integer 31.4%, digits:leading-zero 18.3% |  |
| `FLG_ALZHDMTA` | TEXT | text 100% |  |  | 5 | 1–1 | integer 80%, flag:yn 20% | code domain: `2`, `0`, `1`, `N`, `Y` |
| `FLG_CHF` | TEXT | text 100% |  |  | 5 | 1–1 | integer 80%, flag:yn 20% | code domain: `2`, `1`, `0`, `N`, `Y` |
| `FLG_CHRNKIDN` | TEXT | text 100% |  |  | 5 | 1–1 | integer 80%, flag:yn 20% | code domain: `2`, `0`, `N`, `1`, `Y` |
| `FLG_CNCR` | TEXT | text 100% |  |  | 5 | 1–1 | integer 80%, flag:yn 20% | code domain: `2`, `0`, `N`, `1`, `Y` |
| `FLG_COPD` | TEXT | text 100% |  |  | 5 | 1–1 | integer 80%, flag:yn 20% | code domain: `2`, `0`, `N`, `1`, `Y` |
| `FLG_DEPRESSN` | TEXT | text 100% |  |  | 5 | 1–1 | integer 80%, flag:yn 20% | code domain: `2`, `0`, `1`, `N`, `Y` |
| `FLG_DIABETES` | TEXT | text 100% |  |  | 5 | 1–1 | integer 80%, flag:yn 20% | code domain: `2`, `1`, `0`, `N`, `Y` |
| `FLG_ISCHMCHT` | TEXT | text 100% |  |  | 5 | 1–1 | integer 80%, flag:yn 20% | code domain: `1`, `2`, `0`, `N`, `Y` |
| `FLG_OSTEOPRS` | TEXT | text 100% |  |  | 5 | 1–1 | integer 80%, flag:yn 20% | code domain: `2`, `0`, `N`, `1`, `Y` |
| `FLG_RA_OA` | TEXT | text 100% |  |  | 5 | 1–1 | integer 80%, flag:yn 20% | code domain: `2`, `0`, `N`, `1`, `Y` |
| `FLG_STRKETIA` | TEXT | text 100% |  |  | 5 | 1–1 | integer 80%, flag:yn 20% | code domain: `2`, `0`, `N`, `1`, `Y` |
| `AMT_IP_REIMB` | TEXT | text 100% |  |  | 1,945 | 2–11 | decimal 97.1%, money:$ 1%, padded:decimal 0.9%, integer 0.9% | **money as formatted text**; untrimmed whitespace |
| `AMT_IP_BENRES` | TEXT | text 100% |  |  | 204 | 4–10 | decimal 96.9%, padded:decimal 1.1%, integer 1%, money:$ 1% | **money as formatted text**; untrimmed whitespace |
| `AMT_IP_PPPYMT` | TEXT | text 100% |  |  | 101 | 4–10 | decimal 99.9%, integer 0%, money:$ 0%, padded:decimal 0% | **money as formatted text**; untrimmed whitespace |
| `AMT_OP_REIMB` | TEXT | text 100% |  |  | 2,337 | 2–10 | decimal 89%, padded:decimal 3.7%, integer 3.7%, money:$ 3.6%, +1 more | **money as formatted text**; untrimmed whitespace |
| `AMT_OP_BENRES` | TEXT | text 100% |  |  | 1,105 | 2–10 | decimal 89.5%, money:$ 3.5%, padded:decimal 3.5%, integer 3.5% | **money as formatted text**; untrimmed whitespace |
| `AMT_OP_PPPYMT` | TEXT | text 100% |  |  | 124 | 2–10 | decimal 99.5%, integer 0.2%, money:$ 0.2%, padded:decimal 0.1% | **money as formatted text**; untrimmed whitespace |
| `AMT_CAR_REIMB` | TEXT | text 100% |  |  | 2,590 | 2–10 | decimal 84.9%, money:$ 5.2%, padded:decimal 5.1%, integer 4.9% | **money as formatted text**; untrimmed whitespace |
| `AMT_CAR_BENRES` | TEXT | text 100% |  |  | 935 | 2–9 | decimal 85.2%, integer 5%, padded:decimal 4.9%, money:$ 4.9% | **money as formatted text**; untrimmed whitespace |
| `AMT_CAR_PPPYMT` | TEXT | text 100% |  |  | 267 | 2–9 | decimal 97%, integer 1%, money:$ 1%, padded:decimal 1% | **money as formatted text**; untrimmed whitespace |
| `LOAD_TS` | TEXT | text 100% |  |  | > 10,000 | 19–19 | timestamp 100% |  |
| `LOAD_BATCH` | TEXT | text 100% |  |  | 100 | 11–11 | other 100% |  |
| `DEL_FLG` | TEXT | text 66.4%, null 33.6% | 33.6% | 33.1% | 3 | 0–1 | flag:yn 50.2%, blank 49.8% | code domain: ``, `N`, `Y` |

## `RX_EVNT` (1,402,142 rows)

| Column | Declared | Stored as | Null | Blank | Distinct | Len | Formats | Flags |
|---|---|---|---:|---:|---:|---|---|---|
| `REC_ID` | INTEGER | integer 100% |  |  | > 10,000 | 1–7 | integer 100% | INTEGER values of varying digit length: if these are codes, leading zeros may have been lost |
| `PDE_ID` | TEXT | text 100% |  |  | > 10,000 | 15–15 | integer 100%, digits:leading-zero 0% | **near-unique: 27,630 repeated values (duplicate loads?)** |
| `MBR_ID` | TEXT | text 100% |  |  | > 10,000 | 16–18 | alnum 97.6%, padded:alnum 2.4%, integer 0%, padded:integer 0% | untrimmed whitespace |
| `SRVC_DT` | TEXT | text 100% |  |  | 4,384 | 6–10 | date:yyyymmdd 70.1%, date:yyyy-mm-dd 10%, date:m/d/yy 10%, date:m/d/yyyy 9.9% | **mixed date formats** |
| `NDC` | INTEGER | integer 95%, text 5% |  |  | > 10,000 | 5–13 | integer 95%, code:hyphenated 5%, alnum 0%, date:yyyymmdd 0% | **mixed storage types**; **INTEGER column holding text (codes?): leading zeros may be lost**; INTEGER values of varying digit length: if these are codes, leading zeros may have been lost |
| `QTY` | TEXT | text 100% |  |  | 37 | 5–7 | decimal 100% |  |
| `DAYS` | TEXT | text 100% |  |  | 20 | 1–4 | decimal 50%, integer 50% |  |
| `AMT_PTNT_PAY` | TEXT | text 100% |  |  | 69 | 2–8 | decimal 92.3%, integer 2.6%, money:$ 2.6%, padded:decimal 2.6% | **money as formatted text**; untrimmed whitespace |
| `AMT_TOT_COST` | TEXT | text 100% |  |  | 229 | 2–8 | decimal 81.9%, padded:decimal 6%, money:$ 6%, integer 6% | **money as formatted text**; untrimmed whitespace |
| `LOAD_TS` | TEXT | text 100% |  |  | > 10,000 | 19–19 | timestamp 100% | **near-unique: 17,662 repeated values (duplicate loads?)** |
| `LOAD_BATCH` | TEXT | text 100% |  |  | 76 | 10–17 | other 100% |  |
| `DEL_FLG` | TEXT | text 66.9%, null 33.1% | 33.1% | 33.2% | 3 | 0–1 | flag:yn 50.3%, blank 49.7% | code domain: ``, `N`, `Y` |
