using System.Net;
using System.Net.Http.Headers;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace CloudDentalOffice.Portal.Tests;

public class CloudHealthOfficeApiServiceTests
{
    [Fact]
    public async Task SubmitClaimAsync_SendsRaw837MultipartWithTenantHeader()
    {
        await using var context = CreateContext("demo", out var claim);
        var handler = new RecordingHandler(HttpStatusCode.OK,
            """{"succeededCount":1,"results":[{"success":true,"claimId":"cho-123","errors":[]}]}""");
        var service = CreateService(context, handler, "ISA*837-CONTENT~");

        var result = await service.SubmitClaimAsync(claim, CreatePayer());

        Assert.True(result.Success);
        Assert.Equal("cho-123", result.TrackingId);
        Assert.Equal("demo", handler.TenantId);
        Assert.Equal("file", handler.PartName);
        Assert.Equal("837D_CLAIM-100.x12", handler.FileName);
        Assert.Equal("text/plain", handler.PartContentType);
        Assert.Equal("ISA*837-CONTENT~", handler.PartBody);
        Assert.Equal("/api/v1/claims/import/raw837", handler.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task SubmitClaimAsync_MapsRejectedRaw837Response()
    {
        await using var context = CreateContext("demo", out var claim);
        var handler = new RecordingHandler(HttpStatusCode.OK,
            """{"succeededCount":0,"results":[{"success":false,"claimId":null,"errors":["Invalid subscriber"]}]}""");
        var service = CreateService(context, handler, "ISA*837-CONTENT~");

        var result = await service.SubmitClaimAsync(claim, CreatePayer());

        Assert.False(result.Success);
        Assert.Equal("CloudHealthOffice rejected the 837D claim", result.Message);
        Assert.Equal(new[] { "Invalid subscriber" }, result.Errors);
    }

    [Fact]
    public async Task SubmitClaimAsync_RejectsMissingTenantBeforeSendingRequest()
    {
        await using var context = CreateContext(string.Empty, out var claim);
        var handler = new RecordingHandler(HttpStatusCode.OK, "{}");
        var service = CreateService(context, handler, "ISA*837-CONTENT~");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SubmitClaimAsync(claim, CreatePayer()));

        Assert.Equal("Claim CLAIM-100 has no tenant ID", exception.Message);
        Assert.Null(handler.RequestUri);
    }

    private static CloudDentalDbContext CreateContext(string tenantId, out Claim claim)
    {
        var tenantProvider = new Mock<ITenantProvider>();
        tenantProvider.SetupGet(p => p.TenantId).Returns(tenantId);
        var options = new DbContextOptionsBuilder<CloudDentalDbContext>()
            .UseInMemoryDatabase($"CloudHealthOfficeApi_{Guid.NewGuid()}")
            .Options;
        var context = new CloudDentalDbContext(options, tenantProvider.Object);
        var patient = new Patient
        {
            PatientId = 1,
            TenantId = tenantId,
            FirstName = "Jamie",
            LastName = "Patient",
            DateOfBirth = new DateTime(1990, 1, 1),
            Gender = "U"
        };
        var provider = new Provider
        {
            ProviderId = 1,
            TenantId = tenantId,
            NPI = "1234567890",
            FirstName = "Pat",
            LastName = "Dentist"
        };
        var insurancePlan = new InsurancePlan
        {
            InsurancePlanId = 1,
            TenantId = tenantId,
            PayerId = "00001",
            PayerName = "CloudHealthOffice"
        };
        var patientInsurance = new PatientInsurance
        {
            PatientInsuranceId = 1,
            TenantId = tenantId,
            PatientId = patient.PatientId,
            Patient = patient,
            InsurancePlanId = insurancePlan.InsurancePlanId,
            InsurancePlan = insurancePlan,
            MemberId = "MEMBER-1",
            SequenceNumber = 1
        };
        claim = new Claim
        {
            TenantId = tenantId,
            ClaimNumber = "CLAIM-100",
            PatientId = patient.PatientId,
            Patient = patient,
            ProviderId = provider.ProviderId,
            Provider = provider,
            PatientInsuranceId = patientInsurance.PatientInsuranceId,
            PatientInsurance = patientInsurance,
            ServiceDateFrom = new DateTime(2026, 8, 3),
            TotalChargeAmount = 125m
        };
        context.Claims.Add(claim);
        context.SaveChanges();
        if (string.IsNullOrEmpty(tenantId))
            claim.TenantId = string.Empty;
        return context;
    }

    private static CloudHealthOfficeApiService CreateService(
        CloudDentalDbContext context,
        RecordingHandler handler,
        string x12)
    {
        var x12Service = new Mock<IEdiX12Service>();
        x12Service.Setup(s => s.Generate837DTransactionAsync(It.IsAny<Claim>()))
            .ReturnsAsync(x12);
        var clientFactory = new Mock<IHttpClientFactory>();
        clientFactory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(handler));
        var logger = Mock.Of<ILogger<CloudHealthOfficeApiService>>();
        return new CloudHealthOfficeApiService(context, x12Service.Object, clientFactory.Object, logger);
    }

    private static InsurancePlan CreatePayer() => new()
    {
        TenantId = "demo",
        PayerId = "00001",
        PayerName = "CloudHealthOffice",
        ApiEndpoint = "http://cloudhealthoffice.local/"
    };

    private sealed class RecordingHandler(HttpStatusCode statusCode, string responseBody) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? TenantId { get; private set; }
        public string? PartName { get; private set; }
        public string? FileName { get; private set; }
        public string? PartContentType { get; private set; }
        public string? PartBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            TenantId = request.Headers.GetValues("X-Tenant-ID").Single();

            var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
            var part = Assert.Single(multipart);
            PartName = TrimQuotes(part.Headers.ContentDisposition?.Name);
            FileName = TrimQuotes(part.Headers.ContentDisposition?.FileName);
            PartContentType = part.Headers.ContentType?.MediaType;
            PartBody = await part.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, new MediaTypeHeaderValue("application/json"))
            };
        }

        private static string? TrimQuotes(string? value) => value?.Trim('"');
    }
}
