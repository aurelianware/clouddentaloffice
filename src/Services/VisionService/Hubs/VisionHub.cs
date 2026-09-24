// Copyright (c) Aurelianware, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using CloudDentalOffice.Contracts.Vision;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using VisionService.Auth;
using VisionService.Domain;

namespace VisionService.Hubs;

/// <summary>
/// SignalR hub for real-time vision event streaming.
/// 
/// privaseeAI edge devices connect with their per-tenant device key to push detection events.
/// The Blazor Portal connects with the staff bearer token (?access_token= for WebSockets)
/// to receive live updates for dashboards.
/// 
/// Every group is scoped to the caller's tenant, which comes only from its credential:
///   - "tenant:{tenantId}" — all events for a tenant (joined on connect by staff only)
///   - "tenant:{tenantId}:device:{deviceId}" — events from a specific device
///   - "tenant:{tenantId}:location:{location}" — events from cameras in a location category
///   - "tenant:{tenantId}:alerts" — high-severity alerts only
/// </summary>
[Authorize(Policy = VisionAuth.HubPolicy)]
public class VisionHub : Hub
{
    private readonly ILogger<VisionHub> _logger;
    private readonly VisionDbContext _db;

    public VisionHub(ILogger<VisionHub> logger, VisionDbContext db)
    {
        _logger = logger;
        _db = db;
    }

    private string TenantId => Context.User!.Tenant();

    // ── Connection Management ───────────────────────────────────────────────

    public override async Task OnConnectedAsync()
    {
        // Only staff receive the tenant-wide broadcasts (events, scans, cabinet logs).
        // Device connections are limited to their ingestion methods.
        if (VisionAuth.IsStaff(Context.User!))
            await Groups.AddToGroupAsync(Context.ConnectionId, VisionGroups.Tenant(TenantId));

        _logger.LogInformation("Vision client connected: {ConnectionId} (device={IsDevice}, tenant={TenantId})",
            Context.ConnectionId, VisionAuth.IsDevice(Context.User!), TenantId);

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Vision client disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    // ── Subscribe to Channels (Portal staff) ────────────────────────────────

    [Authorize(Policy = VisionAuth.StaffPolicy)]
    public async Task SubscribeToDevice(Guid deviceId)
    {
        await RequireTenantDeviceAsync(deviceId);
        await Groups.AddToGroupAsync(Context.ConnectionId, VisionGroups.Device(TenantId, deviceId));
    }

    [Authorize(Policy = VisionAuth.StaffPolicy)]
    public async Task SubscribeToLocation(string location)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, VisionGroups.Location(TenantId, location));
    }

    [Authorize(Policy = VisionAuth.StaffPolicy)]
    public async Task SubscribeToAlerts()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, VisionGroups.Alerts(TenantId));
    }

    [Authorize(Policy = VisionAuth.StaffPolicy)]
    public async Task UnsubscribeFromLocation(string location)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, VisionGroups.Location(TenantId, location));
    }

    // ── Device → Server (ingestion from privaseeAI edge) ─────────────────

    /// <summary>
    /// Called by privaseeAI edge devices to push detection events in real-time.
    /// This is the primary ingestion point for continuous detection streaming.
    /// </summary>
    [Authorize(Policy = VisionAuth.DevicePolicy)]
    public async Task PushDetections(IngestDetectionRequest request)
    {
        await RequireTenantDeviceAsync(request.DeviceId);

        // The endpoint handler processes and stores the event;
        // this hub method is for real-time streaming without HTTP overhead
        _logger.LogDebug("Received {Count} detections from device {DeviceId}",
            request.Detections.Count, request.DeviceId);

        // Forward to Portal clients of this tenant watching the device
        await Clients.Group(VisionGroups.Device(TenantId, request.DeviceId))
            .SendAsync("DetectionReceived", request);
    }

    /// <summary>
    /// Device heartbeat — keeps connection alive and updates status.
    /// </summary>
    [Authorize(Policy = VisionAuth.DevicePolicy)]
    public async Task Heartbeat(Guid deviceId, DeviceStatus status)
    {
        await RequireTenantDeviceAsync(deviceId);

        await Clients.Group(VisionGroups.Device(TenantId, deviceId))
            .SendAsync("DeviceHeartbeat", new { deviceId, status, timestamp = DateTime.UtcNow });
    }

    // A device belonging to another tenant is indistinguishable from one that does not exist.
    private async Task RequireTenantDeviceAsync(Guid deviceId)
    {
        var tenantId = TenantId;
        if (!await _db.Devices.AnyAsync(d => d.Id == deviceId && d.TenantId == tenantId))
            throw new HubException("Device not found.");
    }
}

/// <summary>Tenant-scoped SignalR group names.</summary>
public static class VisionGroups
{
    public static string Tenant(string tenantId) => $"tenant:{tenantId}";
    public static string Device(string tenantId, Guid deviceId) => $"tenant:{tenantId}:device:{deviceId}";
    public static string Location(string tenantId, string location) => $"tenant:{tenantId}:location:{location}";
    public static string Alerts(string tenantId) => $"tenant:{tenantId}:alerts";
}

/// <summary>
/// Extension methods for broadcasting vision events from the service layer to connected clients.
/// </summary>
public static class VisionHubExtensions
{
    public static async Task BroadcastVisionEvent(this IHubContext<VisionHub> hub,
        VisionEventDto visionEvent)
    {
        // Broadcast to tenant
        await hub.Clients.Group(VisionGroups.Tenant(visionEvent.TenantId))
            .SendAsync("VisionEvent", visionEvent);

        // Broadcast to device watchers
        await hub.Clients.Group(VisionGroups.Device(visionEvent.TenantId, visionEvent.DeviceId))
            .SendAsync("VisionEvent", visionEvent);

        // Broadcast to location watchers
        await hub.Clients.Group(VisionGroups.Location(visionEvent.TenantId, visionEvent.Location.ToString()))
            .SendAsync("VisionEvent", visionEvent);

        // Broadcast alerts
        if (visionEvent.AlertSeverity >= AlertSeverity.High)
        {
            await hub.Clients.Group(VisionGroups.Alerts(visionEvent.TenantId))
                .SendAsync("Alert", visionEvent);
        }
    }

    public static async Task BroadcastCabinetAlert(this IHubContext<VisionHub> hub,
        CabinetAccessLogDto accessLog)
    {
        await hub.Clients.Group(VisionGroups.Tenant(accessLog.TenantId))
            .SendAsync("CabinetAccess", accessLog);

        if (accessLog.Severity >= AlertSeverity.Medium)
        {
            await hub.Clients.Group(VisionGroups.Alerts(accessLog.TenantId))
                .SendAsync("CabinetAlert", accessLog);
        }
    }

    public static async Task BroadcastInsuranceScan(this IHubContext<VisionHub> hub,
        InsuranceCardScanDto scan)
    {
        await hub.Clients.Group(VisionGroups.Tenant(scan.TenantId))
            .SendAsync("InsuranceScan", scan);
    }

    public static async Task BroadcastDeviceStatus(this IHubContext<VisionHub> hub,
        string tenantId, Guid deviceId, DeviceStatus status)
    {
        await hub.Clients.Group(VisionGroups.Tenant(tenantId))
            .SendAsync("DeviceStatusChanged", new { deviceId, status, timestamp = DateTime.UtcNow });
    }
}
