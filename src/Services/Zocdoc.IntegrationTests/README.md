# Zocdoc sandbox certification tests

These tests use Zocdoc's documented, predefined sandbox data and are deliberately
outside `CloudDentalOffice.sln`, so normal build and unit-test execution never calls
an external service.

```bash
export ZOCDOC_SANDBOX_CLIENT_ID='<sandbox client id>'
export ZOCDOC_SANDBOX_CLIENT_SECRET='<sandbox client secret>'
dotnet test src/Services/Zocdoc.IntegrationTests/Zocdoc.IntegrationTests.csproj
```

Missing credentials cause every external test to report **Skipped**, not failed.
Never put credentials in a `.runsettings` file, shell script, CI variable value,
test output, or source control. Use a secret-backed CI environment when automating.

The suite verifies OAuth, reference data, multiple locations, documented fixed
new/existing patient and appointment lifecycle cases, and authentication failure.
Signed/replayed/malformed webhook behavior is deterministic and remains in
`IntakeService.Tests`; stateful confirmation, cancellation, rescheduling, insurance
error, unavailable-slot, and mock-webhook exercises are in the partner checklist
because they mutate a shared sandbox or require a Zocdoc-configured callback URL.
