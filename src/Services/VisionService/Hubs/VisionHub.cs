// Copyright (c) Aurelianware, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using CloudDentalOffice.Contracts.Vision;
using Microsoft.AspNetCore.SignalR;

namespace VisionService.Hubs;

/// <summary>
/// SignalR hub for real-time vision event streaming.
/// 
/// privaseeAI edge devices connect via WebSocket to push detection events.
/// The Blazor Portal connects to receive live updates for dashboards.
/// 
/// Groups:
///   - "tenant:{tenantId}" — all events for a tenant
///   - "device:{deviceId}" — events from a specific device  
///   - "location:{location}" — events from cameras in a location category
///   - "alerts" — high-severity alerts only
/// </summary>
public class VisionHub : Hub
{
    private readonly ILogger<VisionHub> _logger;

    public VisionHub(ILogger<VisionHub> logger)
    {
        _logger = logger;
    }

    // ── Connection Management ───────────────────────────────────────────────

    public override async Task OnConnectedAsync()
    {
        var tenantId = Context.GetHttpContext()?.Request.Query["tenantId"].FirstOrDefault();
        var deviceId = Context.GetHttpContext()?.Request.Query["deviceId"].FirstOrDefault();
        var role = Context.GetHttpContext()?.Request.Query["role"].FirstOrDefault(); // "device" or "portal"

        if (!string.IsNullOrEmpty(tenantId))
            await Groups.AddToGroupAsync(Context.ConnectionId, $"tenant:{tenantId}");

        if (!string.IsNullOrEmpty(deviceId))
            await Groups.AddToGroupAsync(Context.ConnectionId, $"device:{deviceId}");

        _logger.LogInformation("Vision client connected: {ConnectionId} (role={Role}, tenant={TenantId}, device={DeviceId})",
            Context.ConnectionId, role, tenantId, deviceId);

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Vision client disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    // ── Subscribe to Channels ───────────────────────────────────────────────

    public async Task SubscribeToLocation(string location)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"location:{location}");
    }

    public async Task SubscribeToAlerts()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "alerts");
    }

    public async Task UnsubscribeFromLocation(string location)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"location:{location}");
    }

    // ── Device → Server (ingestion from privaseeAI edge) ─────────────────

    /// <summary>
    /// Called by privaseeAI edge devices to push detection events in real-time.
    /// This is the primary ingestion point for continuous detection streaming.
    /// </summary>
    public async Task PushDetections(IngestDetectionRequest request)
    {
        // The endpoint handler processes and stores the event;
        // this hub method is for real-time streaming without HTTP overhead
        _logger.LogDebug("Received {Count} detections from device {DeviceId}",
            request.Detections.Count, request.DeviceId);

        // Forward to all Portal clients watching this tenant/device
        // The actual processing happens in the endpoint/service layer
        await Clients.OthersInGroup($"device:{request.DeviceId}")
            .SendAsync("DetectionReceived", request);
    }

    /// <summary>
    /// Device heartbeat — keeps connection alive and updates status.
    /// </summary>
    public async Task Heartbeat(Guid deviceId, DeviceStatus status)
    {
        await Clients.OthersInGroup($"device:{deviceId}")
            .SendAsync("DeviceHeartbeat", new { deviceId, status, timestamp = DateTime.UtcNow });
    }
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
        await hub.Clients.Group($"tenant:{visionEvent.TenantId}")
            .SendAsync("VisionEvent", visionEvent);

        // Broadcast to device watchers
        await hub.Clients.Group($"device:{visionEvent.DeviceId}")
            .SendAsync("VisionEvent", visionEvent);

        // Broadcast to location watchers
        await hub.Clients.Group($"location:{visionEvent.Location}")
            .SendAsync("VisionEvent", visionEvent);

        // Broadcast alerts
        if (visionEvent.AlertSeverity >= AlertSeverity.High)
        {
            await hub.Clients.Group("alerts")
                .SendAsync("Alert", visionEvent);
        }
    }

    public static async Task BroadcastCabinetAlert(this IHubContext<VisionHub> hub,
        CabinetAccessLogDto accessLog)
    {
        await hub.Clients.Group($"tenant:{accessLog.TenantId}")
            .SendAsync("CabinetAccess", accessLog);

        if (accessLog.Severity >= AlertSeverity.Medium)
        {
            await hub.Clients.Group("alerts")
                .SendAsync("CabinetAlert", accessLog);
        }
    }

    public static async Task BroadcastInsuranceScan(this IHubContext<VisionHub> hub,
        InsuranceCardScanDto scan)
    {
        await hub.Clients.Group($"tenant:{scan.TenantId}")
            .SendAsync("InsuranceScan", scan);
    }

    public static async Task BroadcastDeviceStatus(this IHubContext<VisionHub> hub,
        Guid tenantId, Guid deviceId, DeviceStatus status)
    {
        await hub.Clients.Group($"tenant:{tenantId}")
            .SendAsync("DeviceStatusChanged", new { deviceId, status, timestamp = DateTime.UtcNow });
    }
}
