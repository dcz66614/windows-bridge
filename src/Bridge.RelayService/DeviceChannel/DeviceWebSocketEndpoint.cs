using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bridge.DeviceChannel;
using Bridge.RelayService.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Validation.AspNetCore;

namespace Bridge.RelayService.DeviceChannel;

/// <summary>
/// The device WebSocket endpoint (T11): GET /ws/device?device_id=…
/// with a Bearer token carrying device.connect.
/// 1. Authenticate the token (device.connect scope, owner = sub).
/// 2. Accept the socket, send a challenge (random nonce).
/// 3. The device answers with a hello signed over
///    (nonce + ":" + device_id) with its P-256 key.
/// 4. Verify against the stored device record (exists, Active,
///    owner matches, fingerprint matches, signature verifies).
/// 5. Register the connection (evicting any live one), then serve
///    heartbeats and command_results until the socket closes.
/// Auth failures close with 4401/4403 and are non-retryable.
/// </summary>
public static class DeviceWebSocketEndpoint
{
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);

    public static async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("WebSocket upgrade required.");
            return;
        }

        var registry = context.RequestServices.GetRequiredService<DeviceConnectionRegistry>();
        var dispatcher = context.RequestServices.GetRequiredService<CommandDispatcher>();

        var auth = await context.AuthenticateAsync(
            OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        if (!auth.Succeeded || auth.Principal is null)
        {
            context.Response.StatusCode = 401;
            return;
        }
        var scopes = auth.Principal.FindAll("scope")
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (!scopes.Contains("device.connect"))
        {
            context.Response.StatusCode = 403;
            return;
        }
        var ownerId = auth.Principal.FindFirst("sub")?.Value;
        if (ownerId is null)
        {
            context.Response.StatusCode = 401;
            return;
        }
        var deviceId = context.Request.Query["device_id"].ToString();
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("device_id is required.");
            return;
        }

        var socket = await context.WebSockets.AcceptWebSocketAsync();
        DeviceConnectionRegistry.Connection? connection = null;
        try
        {
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            await ChannelFraming.SendAsync(socket,
                ChannelEnvelope.Create("challenge", new { nonce_b64 = nonce }),
                CancellationToken.None);

            using var helloTimeout = new CancellationTokenSource(HelloTimeout);
            var helloJson = await ChannelFraming.ReadMessageAsync(socket, helloTimeout.Token);
            if (helloJson is null)
                return;
            var hello = ChannelEnvelope.TryParse(helloJson);
            var verified = await VerifyHelloAsync(context, deviceId, ownerId, nonce, hello);
            if (verified is not { } device)
            {
                await ChannelFraming.SendAsync(socket,
                    ChannelEnvelope.Create("error", new
                    {
                        code = "unauthorized",
                        message = "Device verification failed.",
                    }), CancellationToken.None);
                await DeviceConnectionRegistry.CloseAsync(socket,
                    (WebSocketCloseStatus)ChannelCloseCodes.Unauthorized, "unauthorized");
                return;
            }

            connection = new DeviceConnectionRegistry.Connection
            {
                Socket = socket,
                DeviceId = deviceId,
                OwnerId = ownerId,
                Capabilities = device.Capabilities,
                Epoch = 0,
            };
            connection = await registry.RegisterAsync(connection);
            await ChannelFraming.SendAsync(socket,
                ChannelEnvelope.Create("hello_ack", new { epoch = connection.Epoch }),
                CancellationToken.None);

            await ServeAsync(socket, registry, dispatcher, deviceId, connection);
        }
        catch (OperationCanceledException)
        {
            await DeviceConnectionRegistry.CloseAsync(socket,
                (WebSocketCloseStatus)ChannelCloseCodes.HelloTimeout, "hello timeout");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("exceeds 1 MiB"))
        {
            await DeviceConnectionRegistry.CloseAsync(socket,
                (WebSocketCloseStatus)ChannelCloseCodes.OversizeFrame, "frame too large");
        }
        catch (WebSocketException)
        {
            // Peer went away mid-frame; nothing to close.
        }
        finally
        {
            if (connection is not null)
            {
                registry.Unregister(deviceId, connection);
                dispatcher.FailAll(deviceId, "device_disconnected");
            }
        }
    }

    private static async Task<Device?> VerifyHelloAsync(
        HttpContext context, string deviceId, string ownerId, string nonce,
        ChannelEnvelope? hello)
    {
        if (hello is null || hello.Type != "hello")
            return null;
        var body = hello.Body;
        if (!body.TryGetProperty("device_id", out var helloDevice) ||
            helloDevice.GetString() != deviceId)
            return null;
        if (!body.TryGetProperty("public_key_fingerprint", out var fp) ||
            !body.TryGetProperty("signature_b64", out var sig))
            return null;

        using var scope = context.RequestServices.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var device = await db.Devices.FindAsync(deviceId);
        if (device is null || device.Status != "Active" || device.OwnerId != ownerId)
            return null;
        if (!string.Equals(device.KeyFingerprint, fp.GetString(), StringComparison.OrdinalIgnoreCase))
            return null;

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(sig.GetString()!);
        }
        catch
        {
            return null;
        }
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(device.PublicKeyDer, out _);
        var material = Encoding.UTF8.GetBytes(nonce + ":" + deviceId);
        if (!key.VerifyData(material, signature, HashAlgorithmName.SHA256))
            return null;
        return device;
    }

    private static async Task ServeAsync(
        WebSocket socket,
        DeviceConnectionRegistry registry,
        CommandDispatcher dispatcher,
        string deviceId,
        DeviceConnectionRegistry.Connection connection)
    {
        while (socket.State == WebSocketState.Open)
        {
            var json = await ChannelFraming.ReadMessageAsync(socket, CancellationToken.None);
            if (json is null)
                return;
            var envelope = ChannelEnvelope.TryParse(json);
            if (envelope is null)
            {
                await ChannelFraming.SendAsync(socket,
                    ChannelEnvelope.Create("error", new
                    {
                        code = "bad_frame",
                        message = "Message is not a valid channel envelope.",
                    }, null), CancellationToken.None);
                continue;
            }
            switch (envelope.Type)
            {
                case "heartbeat":
                    connection.LastSeen = DateTimeOffset.UtcNow;
                    await ChannelFraming.SendAsync(socket,
                        ChannelEnvelope.Create("heartbeat_ack", null, envelope.MessageId),
                        CancellationToken.None);
                    break;
                case "command_result":
                    if (envelope.MessageId is not null)
                        dispatcher.TryComplete(deviceId, envelope.MessageId, envelope.Body);
                    break;
                case "lease_update":
                    // The device reports its local state (paused/locked).
                    // The device is the authority; the server trusts it.
                    var paused = envelope.Body.TryGetProperty("paused", out var p) &&
                        p.ValueKind == JsonValueKind.True;
                    var locked = envelope.Body.TryGetProperty("locked", out var l) &&
                        l.ValueKind == JsonValueKind.True;
                    registry.UpdateLocalState(deviceId, paused, locked);
                    break;
                default:
                    await ChannelFraming.SendAsync(socket,
                        ChannelEnvelope.Create("error", new
                        {
                            code = "unknown_type",
                            message = $"Unknown message type: {envelope.Type}.",
                        }, envelope.MessageId), CancellationToken.None);
                    break;
            }
        }
    }
}
