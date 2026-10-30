using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Plantain.Core.Tests;

internal sealed record Request(string Target, IReadOnlyDictionary<string, string> Headers)
{
    internal string? Header(string name) => Headers.GetValueOrDefault(name);
}

internal sealed record Response(byte[] Body, int Status = 200,
    IReadOnlyDictionary<string, string>? Headers = null, long? Length = null,
    int HeaderDelayMs = 0, Func<Stream, CancellationToken, Task>? WriteBody = null)
{
    internal static Response Text(string body, int status = 200) => new(Encoding.UTF8.GetBytes(body), status);
}

// TCP listener binds port 0 itself: no port-probing race, admin rights, Python or external service.
internal sealed class TestServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource shutdown = new();
    private readonly ConcurrentBag<Task> connections = [];
    private readonly Func<Request, int, Response> respond;
    private readonly Task accept;
    private readonly X509Certificate2? certificate;
    internal ConcurrentQueue<Request> Requests { get; } = [];
    internal Uri BaseUri { get; }

    internal TestServer(Func<Request, int, Response> respond, bool tls = false)
    {
        this.respond = respond;
        if (tls)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
        }
        listener.Start();
        BaseUri = new Uri($"{(tls ? "https" : "http")}://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        accept = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(shutdown.Token);
                connections.Add(HandleAsync(client));
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (shutdown.IsCancellationRequested) { }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var network = client.GetStream();
                using var ssl = certificate is null ? null : new SslStream(network, leaveInnerStreamOpen: true);
                if (ssl is not null) await ssl.AuthenticateAsServerAsync(certificate!, clientCertificateRequired: false,
                    SslProtocols.Tls12 | SslProtocols.Tls13, checkCertificateRevocation: false);
                Stream stream = ssl is null ? network : ssl;
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var first = await reader.ReadLineAsync(shutdown.Token);
                if (first is null) return;
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(shutdown.Token)))
                {
                    var split = line.IndexOf(':');
                    if (split > 0) headers[line[..split]] = line[(split + 1)..].Trim();
                }
                var request = new Request(first.Split(' ')[1], headers);
                Requests.Enqueue(request);
                var response = respond(request, Requests.Count);
                if (response.HeaderDelayMs > 0) await Task.Delay(response.HeaderDelayMs, shutdown.Token);
                var text = $"HTTP/1.1 {response.Status} Test\r\nContent-Length: {response.Length ?? response.Body.Length}\r\nConnection: close\r\n";
                if (response.Headers is not null)
                    foreach (var header in response.Headers) text += $"{header.Key}: {header.Value}\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(text + "\r\n"), shutdown.Token);
                await stream.FlushAsync(shutdown.Token);
                if (response.WriteBody is not null) await response.WriteBody(stream, shutdown.Token);
                else await stream.WriteAsync(response.Body, shutdown.Token);
            }
            catch (IOException) { }
            catch (OperationCanceledException) { }
            catch (AuthenticationException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await shutdown.CancelAsync();
        listener.Stop();
        await accept;
        await Task.WhenAll(connections);
        certificate?.Dispose();
        shutdown.Dispose();
    }
}
