-- Deletes every appointment and booking request from cdo_scheduling. These are the only
-- scheduling rows that point at PatientService patient ids, and a fresh cdo_portal hands
-- out those ids (1, 2, 3, ...) again, so leaving them would attach them to new patients.
-- Search Console data and patient-acquisition events (no patient ids) are kept.
-- Run only inside the maintenance window, after the backups:
--   psql -d cdo_scheduling -v ON_ERROR_STOP=1 -f 21-clear-scheduling-patient-refs.sql
-- One transaction: either both tables are cleared or nothing changes.
\set ON_ERROR_STOP on
\pset footer off
BEGIN;
LOCK TABLE "Appointments", "BookingRequests" IN ACCESS EXCLUSIVE MODE;

\echo '== Before'
SELECT (SELECT count(*) FROM "Appointments") AS appointments,
       (SELECT count(*) FROM "BookingRequests") AS booking_requests;

DELETE FROM "BookingRequests";
DELETE FROM "Appointments";

\echo '== After (both must be 0)'
SELECT (SELECT count(*) FROM "Appointments") AS appointments,
       (SELECT count(*) FROM "BookingRequests") AS booking_requests;

COMMIT;
