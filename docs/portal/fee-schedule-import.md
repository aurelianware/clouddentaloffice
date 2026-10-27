# Practice fee schedule import

Payer managers can open **Claims → Payers → Fee schedules** (`/fee-schedules`).
Select up to 20 Dentrix fee schedule CSV exports, review the schedule names,
code counts and D7220 fees, select the standard practice schedule as the
practice default, and import the batch.

Each practice owns its schedules. A repeat import with the same schedule name
replaces that version, without adding duplicate schedules. Selecting a new
default and importing the batch is atomic. Invalid files are rejected before
any writes. The current default is retained when no new default is selected.

The importer preserves descriptions, fees, active status, no-write-off and
paid-in-full flags, including custom billing codes. It does not infer effective
dates or payer relationships from names. Only active D-plus-four-digit entries
from the practice default overlay the shared CDT procedure picker. Historical
plans and existing line fees are not rewritten. Other practices retain their
own prices and procedure selection.

Payer schedules remain separate for later explicit plan/contract assignment;
importing a schedule does not configure the CloudHealthOffice adjudication
service or turn that schedule into an insurance payment promise. The flags
are retained as source data, not applied as billing rules by this import.

Exports contain practice pricing and must not be checked into the repository.
The importer accepts the six Dentrix columns `Code`, `Description`, `Fee`,
`No Write-off`, `Paid In Full`, `Active`, preceded by `Fee Schedule,<name>`.
Each file is limited to 2 MiB and 10,000 codes. Unknown/custom codes are retained
without representing them as official current CDT codes.

SQLite migration: `AddPracticeFeeSchedules`. PostgreSQL installations using
`EnsureCreated` receive the matching idempotent table/index reconciliation at
startup. The default is protected by a unique filtered index per tenant.
