using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace MudMaps.Server.Research;

public enum UpstreamOutcome { Ok, Timeout, TooLarge, NotJson, HttpError, Unreachable }

public sealed record UpstreamResult(UpstreamOutcome Outcome, int Status = 0, JsonNode? Body = null);

/// <summary>
/// The loopback hop to graphify. Requests are rebuilt here from parsed values with fixed headers only: nothing a client
/// sent (headers, raw body bytes) reaches the child.
/// </summary>
public sealed class GraphifyUpstream(IHttpClientFactory clients, IOptions<ResearchOptions> options)
{
    public const string ClientName = "graphify";
    public const string ProtocolVersion = "2025-06-18";

    /// <summary>The JSON-RPC <c>initialize</c> request the health probe sends.</summary>
    public static JsonObject InitializeRequest() => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = "probe",
        ["method"] = "initialize",
        ["params"] = new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "mudmaps-probe", ["version"] = "1" },
        },
    };

    /// <summary>A canonical <c>tools/call</c>: a new object holding only the parsed id, tool name and arguments.</summary>
    public static JsonObject ToolCall(JsonNode? id, string tool, JsonObject arguments) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["method"] = "tools/call",
        ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments.DeepClone() },
    };

    public async Task<UpstreamResult> PostAsync(int port, string key, JsonObject request, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var http = clients.CreateClient(ClientName);
        using var message = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/mcp");
        message.Headers.Host = $"127.0.0.1:{port}";
        message.Headers.Accept.ParseAdd("application/json");
        message.Headers.Accept.ParseAdd("text/event-stream");
        message.Headers.TryAddWithoutValidation("X-API-Key", key);
        message.Headers.TryAddWithoutValidation("MCP-Protocol-Version", ProtocolVersion);
        message.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(request));
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        try
        {
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode) return new(UpstreamOutcome.HttpError, status);
            var max = options.Value.MaxResponseBytes;
            if (response.Content.Headers.ContentLength > max) return new(UpstreamOutcome.TooLarge, status);
            // Stateless JSON mode answers application/json; an event stream would mean the child is misconfigured.
            if (response.Content.Headers.ContentType?.MediaType != "application/json") return new(UpstreamOutcome.NotJson, status);
            var body = await ReadCappedAsync(response.Content, max, cts.Token);
            if (body is null) return new(UpstreamOutcome.TooLarge, status);
            try
            {
                var node = JsonNode.Parse(body, documentOptions: new JsonDocumentOptions { MaxDepth = 64 });
                return node is JsonObject ? new(UpstreamOutcome.Ok, status, node) : new(UpstreamOutcome.NotJson, status);
            }
            catch (JsonException)
            {
                return new(UpstreamOutcome.NotJson, status);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(UpstreamOutcome.Timeout);
        }
        catch (HttpRequestException)
        {
            return new(UpstreamOutcome.Unreachable);
        }
    }

    private static async Task<byte[]?> ReadCappedAsync(HttpContent content, int max, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > max) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
