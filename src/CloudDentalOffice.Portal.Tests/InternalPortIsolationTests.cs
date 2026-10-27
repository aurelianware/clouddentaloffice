using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Auth;
using Microsoft.AspNetCore.Http;

namespace CloudDentalOffice.Portal.Tests;

public sealed class InternalPortIsolationTests
{
    private const int InternalPort = 5091;

    [Fact]
    public async Task Internal_endpoint_on_the_internal_port_runs_without_the_principal_header()
    {
        var context = Request(InternalPort, InternalPatientApi.MatchOrCreatePath);
        string? headerSeenDownstream = "not called";

        await InternalPatientApi.IsolateInternalPort(context, InternalPort, ctx =>
        {
            headerSeenDownstream = ctx.Request.Headers[ContainerAppsStaffIdentity.PrincipalHeader].FirstOrDefault();
            return Task.CompletedTask;
        });

        Assert.Null(headerSeenDownstream);
    }

    [Theory]
    [InlineData("/api/patient-statements")]
    [InlineData("/")]
    [InlineData("/health/live")]
    public async Task Any_other_path_on_the_internal_port_is_refused(string path)
    {
        var context = Request(InternalPort, path);
        var called = false;

        await InternalPatientApi.IsolateInternalPort(context, InternalPort, _ => { called = true; return Task.CompletedTask; });

        Assert.False(called);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Fact]
    public async Task Public_port_requests_pass_through_with_the_principal_header()
    {
        var context = Request(5000, "/api/patient-statements");
        string? headerSeenDownstream = null;

        await InternalPatientApi.IsolateInternalPort(context, InternalPort, ctx =>
        {
            headerSeenDownstream = ctx.Request.Headers[ContainerAppsStaffIdentity.PrincipalHeader].FirstOrDefault();
            return Task.CompletedTask;
        });

        Assert.Equal("principal", headerSeenDownstream);
    }

    private static DefaultHttpContext Request(int localPort, string path)
    {
        var context = new DefaultHttpContext();
        context.Connection.LocalPort = localPort;
        context.Request.Path = path;
        context.Request.Headers[ContainerAppsStaffIdentity.PrincipalHeader] = "principal";
        return context;
    }
}
