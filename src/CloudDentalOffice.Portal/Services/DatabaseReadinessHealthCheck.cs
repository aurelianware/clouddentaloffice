using CloudDentalOffice.Portal.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CloudDentalOffice.Portal.Services;

/// <summary>
/// Readiness probe dependency: reports healthy only when the portal can open a
/// connection to its configured database. The Container App readiness probe is
/// wired to this check so the ingress stops routing traffic to a replica that
/// cannot serve database-backed pages, instead of returning hard 5xx errors to
/// end users. Liveness deliberately does not depend on this, so a transient
/// database outage never restarts an otherwise-healthy process.
/// </summary>
public sealed class DatabaseReadinessHealthCheck(CloudDentalDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await dbContext.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy("Database connection succeeded.")
                : HealthCheckResult.Unhealthy("Database is not reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database connectivity check failed.", ex);
        }
    }
}
