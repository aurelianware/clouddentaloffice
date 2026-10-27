# Dentrix patient and insurance import

Staff open **Patients → Import from Dentrix** (`/dentrix-import`), choose one
Dentrix patient export (CSV, up to 20 MB and 20,000 patients), review the
preview and import it.

## What the export needs

Dentrix lets the practice choose and order export columns, so the importer
matches columns by name (case, spaces and punctuation are ignored) and skips any
title or print-option lines above the header row. Required columns:

- `Chart Number` (also `Chart #`, `Patient ID`)
- `First Name`, `Last Name`, `Birth Date` (`M/D/YYYY` or `YYYY-MM-DD`)

Optional patient columns: `MI`, `Preferred Name`, `Gender`, `Home Phone`,
`Mobile Phone`, `Email`, `Address 1`, `Address 2`, `City`, `State`, `Zip`,
`Status`.

Insurance columns, prefixed with `Prim`/`Primary` or `Sec`/`Secondary`
(for example `Prim Ins Carrier Name`): `Carrier Name`, `Payor ID`,
`Subscriber ID`, `Group Number`, `Relation`, `Subscriber Name`
(`Last, First`) or `Subscriber First Name`/`Subscriber Last Name`,
`Subscriber Birth Date`, `Effective Date`.

The preview lists the recognized columns and the ignored ones, so a misnamed
column is visible before anything is saved. **SSN columns are never read.**

## Behaviour

- The import is one transaction: it saves everything or nothing.
- Each patient is linked to its Dentrix chart number (`ImportedRecordLinks`).
  Importing again updates the same patients, so the practice can re-import up
  to cutover.
- A patient already in CDO (for example booked online) with the same first
  name, last name and birth date, and not yet linked, is adopted rather than
  duplicated. Ambiguous matches create a new patient.
- Blank email, phone or address cells don't erase details already in CDO.
- Coverage is imported only when it can be sent to Stedi: an electronic payer
  ID (up to 10 characters), a subscriber ID, and, when the patient isn't the
  subscriber, the subscriber's name and birth date. If the relation column is
  missing and the subscriber name differs from the patient, the coverage is
  treated as "Other" rather than "Self". Anything else is listed by row and
  chart number to fix in Dentrix or on the patient's chart.
- Carriers are matched to CDO payers by payer ID. Unknown payer IDs are added
  to the payer list under the carrier name; check them under Claims → Payers
  (the Stedi directory import can replace the name) before running eligibility.
- If Dentrix lists a different plan or subscriber ID for the primary or
  secondary slot, the old CDO coverage is ended today and the new one added.
  Changed group or subscriber details update the coverage and clear its last
  eligibility result. A slot with no Dentrix insurance leaves CDO coverage alone.

Exports contain PHI and must not be checked into the repository or attached to
issues. Import issues identify rows by row number and chart number only.

SQLite migration: `AddImportedRecordLinks`. PostgreSQL installations using
`EnsureCreated` receive the matching table and indexes at startup.

## Dentrix migration plan for 3rd Set Smiles

Ordered by what intake, eligibility (270/271 through Stedi) and treatment
estimates need first.

| # | Data | Status | Approach |
|---|------|--------|----------|
| 1 | Fee schedules | Done (#110) | `/fee-schedules` CSV import, practice default overlays CDT picker |
| 2 | Patients + primary/secondary dental insurance | This change | `/dentrix-import` |
| 3 | Providers (NPI, name, license, TIN) | Next | Few records: enter on the Providers page. Every appointment provider needs a 10-digit NPI or eligibility returns Needs info |
| 4 | Benefits (deductibles, maximums, coverage %) | Not imported | Run a fresh Stedi eligibility check per patient after step 2 rather than copying Dentrix coverage tables, which are often stale |
| 5 | Future appointments | Planned | Dentrix appointment list export, linked by chart number and provider, so verification runs before visits |
| 6 | Pending treatment plans | Planned | Dentrix treatment plan export (code, tooth, surface, fee, status), linked by chart number; estimates then use the saved benefits |
| 7 | Account balances | At cutover | Opening balance per guarantor on cutover day, not full ledger history |
| 8 | Clinical notes, perio, images | Out of scope | Keep Dentrix available read-only for history |

Cutover: import 1–2 now and re-import as often as needed; on cutover day
re-import 2, import 5–7, then stop entering data in Dentrix.
