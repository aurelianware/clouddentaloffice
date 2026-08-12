# Treatment-plan insurance estimates

CloudDentalOffice provides a private, staff-only planning workflow for prospective dental benefit estimates:

```text
CloudDentalOffice Treatment Plan
              |
              v
    InsuranceEstimateService
              |
              v
       CloudHealthOffice
 Prospective Adjudication API
              |
              v
    Line-level Estimate
              |
              v
Treatment Plan Estimate UI
```

The portal maps the selected patient coverage, payer, rendering provider, proposed service date, and each planned CDT procedure to a server-to-server request. Stable planned-procedure IDs (or draft line numbers for unsaved procedures) correlate duplicate CDT codes with response lines. The browser never calls CloudHealthOffice directly.

Configure `CloudHealthOffice:BaseUrl`, `CloudHealthOffice:EstimatePath`, and `CloudHealthOffice:Enabled` through environment configuration. If an API key is required, provide `CloudHealthOffice__ApiKey` through the deployment secret store; never commit it.

Estimation is stateless in the first release. It does not create or submit a claim, generate an 837D, complete a procedure, accept a treatment plan, or update eligibility/verification. A future change can add append-only estimate snapshot history after retention and PHI access requirements are defined.

The UI presents normalized totals, line-level explanations, authority, confidence, warnings, and the required non-guarantee disclaimer. Missing member IDs, payer mappings, provider NPIs, procedures/CDT codes, timeouts, and upstream failures are contained within the estimate panel and do not fail the treatment-plan page.
