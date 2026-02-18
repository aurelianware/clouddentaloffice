// Copyright (c) Aurelianware, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace VisionService.Adapters;

// ════════════════════════════════════════════════════════════════════════════
//  OCR GATEWAY (Insurance Card Scanning)
// ════════════════════════════════════════════════════════════════════════════

public interface IOcrGateway
{
    /// <summary>
    /// Extract text and structured fields from an insurance card image.
    /// </summary>
    Task<InsuranceCardOcrResult> ExtractInsuranceCardAsync(byte[] imageBytes, byte[]? backImageBytes = null);
}

public class InsuranceCardOcrResult
{
    public double Confidence { get; set; }
    public string? PayerName { get; set; }
    public string? PayerId { get; set; }
    public string? MemberId { get; set; }
    public string? GroupNumber { get; set; }
    public string? SubscriberName { get; set; }
    public string? PlanName { get; set; }
    public string? RxBin { get; set; }
    public string? RxPcn { get; set; }
    public string? RxGroup { get; set; }
    public string? CopayAmount { get; set; }
    public string? PhoneNumber { get; set; }
    public string? RawText { get; set; }
    public List<OcrTextBlock> TextBlocks { get; set; } = new();
}

public class OcrTextBlock
{
    public string Text { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

// ════════════════════════════════════════════════════════════════════════════
//  AZURE AI VISION IMPLEMENTATION
// ════════════════════════════════════════════════════════════════════════════

public class AzureAiVisionOptions
{
    public const string SectionName = "AzureAiVision";
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ModelVersion { get; set; } = "2024-02-01";
}

/// <summary>
/// Uses Azure AI Vision (formerly Computer Vision) Read API for OCR,
/// with regex-based field extraction for insurance card patterns.
/// </summary>
public class AzureAiVisionOcrGateway : IOcrGateway
{
    private readonly HttpClient _httpClient;
    private readonly AzureAiVisionOptions _options;
    private readonly ILogger<AzureAiVisionOcrGateway> _logger;

    public AzureAiVisionOcrGateway(HttpClient httpClient, AzureAiVisionOptions options,
        ILogger<AzureAiVisionOcrGateway> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
    }

    public async Task<InsuranceCardOcrResult> ExtractInsuranceCardAsync(byte[] imageBytes, byte[]? backImageBytes = null)
    {
        var result = new InsuranceCardOcrResult();

        // Extract text from front
        var frontText = await CallReadApiAsync(imageBytes);
        result.RawText = frontText;

        // Extract text from back if provided
        string? backText = null;
        if (backImageBytes != null)
        {
            backText = await CallReadApiAsync(backImageBytes);
            result.RawText += "\n---BACK---\n" + backText;
        }

        // Parse structured fields from raw OCR text
        var allText = frontText + (backText != null ? "\n" + backText : "");
        ParseInsuranceFields(allText, result);

        result.Confidence = result.MemberId != null ? 0.85 : 0.4;
        return result;
    }

    private async Task<string> CallReadApiAsync(byte[] imageBytes)
    {
        var url = $"{_options.Endpoint}/computervision/imageanalysis:analyze?api-version={_options.ModelVersion}&features=read";

        using var content = new ByteArrayContent(imageBytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("Ocp-Apim-Subscription-Key", _options.ApiKey);
        request.Content = content;

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();

        // Parse the Read API response — extract all text lines
        // In production, deserialize the full response and extract structured blocks
        // For now, extract concatenated text
        return ExtractTextFromReadResponse(json);
    }

    private static string ExtractTextFromReadResponse(string json)
    {
        // Simplified extraction — in production, use System.Text.Json deserialization
        // The Read API returns { readResult: { blocks: [ { lines: [ { text: "..." } ] } ] } }
        var lines = new List<string>();
        var textMarker = "\"text\":\"";
        var idx = 0;
        while ((idx = json.IndexOf(textMarker, idx, StringComparison.Ordinal)) >= 0)
        {
            idx += textMarker.Length;
            var endIdx = json.IndexOf("\"", idx, StringComparison.Ordinal);
            if (endIdx > idx)
            {
                lines.Add(json[idx..endIdx]);
                idx = endIdx;
            }
        }
        return string.Join("\n", lines);
    }

    private static void ParseInsuranceFields(string text, InsuranceCardOcrResult result)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var fullText = string.Join(" ", lines);

        // Member / Subscriber ID patterns
        result.MemberId = ExtractPattern(fullText,
            @"(?:MEMBER|SUBSCRIBER|ID|IDENTIFICATION)\s*#?\s*:?\s*([A-Z0-9]{6,20})",
            @"(?:MBR|MEM|SUB)\s*(?:ID|#)\s*:?\s*([A-Z0-9]{6,20})");

        // Group number
        result.GroupNumber = ExtractPattern(fullText,
            @"(?:GROUP|GRP)\s*#?\s*:?\s*([A-Z0-9]{4,15})",
            @"GR[PO]\s*:?\s*([A-Z0-9]{4,15})");

        // Payer ID (often 5 digits for dental)
        result.PayerId = ExtractPattern(fullText,
            @"(?:PAYER|PAYOR)\s*(?:ID)?\s*:?\s*(\d{5,10})");

        // RxBIN/PCN (pharmacy benefits — back of card)
        result.RxBin = ExtractPattern(fullText,
            @"(?:RX\s*)?BIN\s*:?\s*(\d{6})");
        result.RxPcn = ExtractPattern(fullText,
            @"PCN\s*:?\s*([A-Z0-9]{4,15})");
        result.RxGroup = ExtractPattern(fullText,
            @"RX\s*(?:GROUP|GRP)\s*:?\s*([A-Z0-9]{4,15})");

        // Copay
        result.CopayAmount = ExtractPattern(fullText,
            @"(?:COPAY|CO-PAY|OFFICE VISIT)\s*:?\s*\$?(\d+(?:\.\d{2})?)");

        // Phone number
        result.PhoneNumber = ExtractPattern(fullText,
            @"(?:PHONE|CALL|TEL)\s*:?\s*(?:1[-.]?)?\(?(\d{3})\)?[-.\s]?(\d{3})[-.\s]?(\d{4})");

        // Subscriber name (heuristic: look for "Name:" or all-caps name near top)
        result.SubscriberName = ExtractPattern(fullText,
            @"(?:NAME|SUBSCRIBER|MEMBER NAME)\s*:?\s*([A-Z][A-Z\s,\.]{3,40})");

        // Plan name (heuristic)
        result.PlanName = ExtractPattern(fullText,
            @"(?:PLAN|COVERAGE)\s*:?\s*(.{5,50}?)(?:\s{2}|\n|$)");

        // Payer name — often the largest text or first line
        if (lines.Length > 0)
        {
            var candidatePayer = lines
                .Take(5)
                .FirstOrDefault(l =>
                    l.Contains("DENTAL", StringComparison.OrdinalIgnoreCase) ||
                    l.Contains("HEALTH", StringComparison.OrdinalIgnoreCase) ||
                    l.Contains("INSURANCE", StringComparison.OrdinalIgnoreCase) ||
                    l.Contains("CIGNA", StringComparison.OrdinalIgnoreCase) ||
                    l.Contains("DELTA", StringComparison.OrdinalIgnoreCase) ||
                    l.Contains("METLIFE", StringComparison.OrdinalIgnoreCase) ||
                    l.Contains("AETNA", StringComparison.OrdinalIgnoreCase) ||
                    l.Contains("UNITED", StringComparison.OrdinalIgnoreCase) ||
                    l.Contains("BCBS", StringComparison.OrdinalIgnoreCase) ||
                    l.Contains("BLUE", StringComparison.OrdinalIgnoreCase) ||
                    l.Contains("GUARDIAN", StringComparison.OrdinalIgnoreCase));

            result.PayerName = candidatePayer?.Trim();
        }
    }

    private static string? ExtractPattern(string text, params string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(text, pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success && match.Groups.Count > 1)
                return match.Groups[1].Value.Trim();
        }
        return null;
    }
}

// ════════════════════════════════════════════════════════════════════════════
//  MOCK OCR GATEWAY (development)
// ════════════════════════════════════════════════════════════════════════════

public class MockOcrGateway : IOcrGateway
{
    public Task<InsuranceCardOcrResult> ExtractInsuranceCardAsync(byte[] imageBytes, byte[]? backImageBytes = null)
    {
        return Task.FromResult(new InsuranceCardOcrResult
        {
            Confidence = 0.92,
            PayerName = "Delta Dental of Texas",
            PayerId = "86027",
            MemberId = "DDT123456789",
            GroupNumber = "GRP-5500",
            SubscriberName = "DOE, JOHN A",
            PlanName = "PPO Premier",
            RxBin = "610014",
            RxPcn = "DDTX",
            RxGroup = "DDPPO",
            CopayAmount = "25.00",
            PhoneNumber = "8005551234",
            RawText = "DELTA DENTAL OF TEXAS\nPPO Premier Plan\nMEMBER ID: DDT123456789\nGROUP: GRP-5500\nNAME: DOE, JOHN A\n" +
                      "COPAY: $25.00\nRxBIN: 610014 PCN: DDTX\nCALL: 1-800-555-1234"
        });
    }
}
