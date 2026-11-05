using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Reflection;
using System.Text;
using DSFiles_Shared;


void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine("PASS " + label); }
Environment.SetEnvironmentVariable("SIGNKEY", new string(args.Contains("--wrong-keys") ? 'x' : 's', 64));
Environment.SetEnvironmentVariable("ENCRYPTKEY", new string('e', 32));
if (args.Length >= 3 && args[0] == "--validate-cap")
{
    var replica = Assembly.Load("DSFiles_Server");
    var unseal = replica.GetType("DSFiles_Server.Helpers.UploadReceipt")!.GetMethod("Unprotect", BindingFlags.Static | BindingFlags.NonPublic)!;
    try
    {
        unseal.Invoke(null, [args[1]]);
        Check(!args.Contains("--wrong-keys"), "replica with matching keys decrypts deletion capability");
        var context = new DefaultHttpContext(); context.Request.Headers["Delete-Proof"] = args[2];
        var consume = replica.GetType("DSFiles_Server.UploadProof")!.GetMethod("ConsumeDelete")!;
        Check((bool)consume.Invoke(null, [context.Request, args[1]])!, "fresh replica validates deletion proof without stored challenge");
    }
    catch (TargetInvocationException e) when (args.Contains("--wrong-keys") && e.InnerException is System.Security.Cryptography.CryptographicException)
    { Console.WriteLine("PASS replica with different keys rejects deletion capability"); }
    return;
}
var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
var sentinel = builder.Build();
int reads = 0, writes = 0;
sentinel.MapGet("/hook", () => { reads++; return Results.Text("{\"channel_id\":\"12345\",\"guild_id\":\"12345\",\"id\":\"12345\",\"name\":\"test\",\"type\":1,\"token\":\"test\"}"); });
sentinel.MapPost("/hook", () => { writes++; return Results.Text("{}"); });
sentinel.MapGet("/image", () => { reads++; return Results.Bytes([1, 2, 3], "image/png"); });
await sentinel.StartAsync();
string origin = sentinel.Urls.Single();
var assembly = Assembly.Load("DSFiles_Server");
var program = assembly.GetType("DSFiles_Server.Program")!;
program.GetField("uploadClient")!.SetValue(null, new HttpClient());
program.GetField("client")!.SetValue(null, new HttpClient());
try
{
    var ctx = new DefaultHttpContext(); ctx.Response.Body = new MemoryStream();
    ctx.Request.Headers["webhook"] = origin + "/hook";
    ctx.Request.Headers["filename"] = "test.txt"; ctx.Request.Headers["filesize"] = "1";
    var handshake = assembly.GetType("DSFiles_Server.Routes.DSFilesChunkedUploadHandle")!.GetMethod("HandleHandshake")!;
    await (Task)handshake.Invoke(null, [ctx.Request, ctx.Response])!;
    Check(reads == 0 && ctx.Response.StatusCode == 400, "upload endpoint blocks localhost before network access");
    int previous = reads;
    try
    {
        var hook = new WebHookHelper(new HttpClient(), origin + "/hook");
        await hook.PostFileToWebhook("test.bin", new byte[] { 1 });
    }
    catch (ArgumentException) { }
    Check(writes == 0 && reads == previous, "webhook blocks localhost reads and writes");
    previous = reads;
    var animation = new DefaultHttpContext(); animation.Response.Body = new MemoryStream();
    animation.Request.QueryString = new QueryString("?url=" + Uri.EscapeDataString(origin + "/image"));
    var animate = assembly.GetType("DSFiles_Server.Routes.ConsoleAnimation")!.GetMethod("HandleAnimation")!;
    try { await (Task)animate.Invoke(null, [animation.Request, animation.Response, CancellationToken.None])!; }
    catch (Exception ex) { throw new Exception("Animation handler did not reject the URL cleanly.", ex); }
    Check(reads == previous && animation.Response.StatusCode == 400, "animate blocks localhost");


    foreach (var url in new[] { "http://localhost/hook", "https://discord.com.attacker.test/api/webhooks/1/token", "https://discord.com@127.0.0.1/api/webhooks/1/token", "https://discord.com/api/webhooks/1/../token", "file:///etc/passwd", "https://discord.com:8443/api/webhooks/1/token" })
        Check(!WebhookAddress.IsValid(url), "invalid webhook rejected: " + url);
    Check(WebhookAddress.IsValid("https://discord.com/api/webhooks/12345/token_ABC-xyz?wait=true"), "Discord webhook remains valid");
    Check(WebhookAddress.IsValid("https://ptb.discord.com/api/v10/webhooks/12345/token"), "versioned Discord webhook remains valid");

    var network = assembly.GetType("DSFiles_Server.Helpers.PublicHttp")!;
    var isPublic = network.GetMethod("IsPublic", BindingFlags.Static | BindingFlags.NonPublic)!;
    foreach (var address in new[] { "127.0.0.1", "10.0.0.1", "172.16.0.1", "192.168.1.1", "169.254.169.254", "100.64.0.1", "0.0.0.0", "::1", "::ffff:127.0.0.1", "fd00::1", "fe80::1", "2001:db8::1", "2002:7f00:1::", "224.0.0.1" })
        Check(!(bool)isPublic.Invoke(null, [IPAddress.Parse(address)])!, "private/reserved destination blocked: " + address);
    foreach (var address in new[] { "1.1.1.1", "8.8.8.8", "2606:4700:4700::1111" })
        Check((bool)isPublic.Invoke(null, [IPAddress.Parse(address)])!, "public address allowed: " + address);
    try { await (Task<byte[]>)network.GetMethod("GetBytes")!.Invoke(null, ["http://localhost/image", 1000, CancellationToken.None])!; throw new Exception("localhost DNS allowed"); }
    catch (HttpRequestException e) { Check(e.ToString().Contains("Private or reserved"), "localhost DNS rejected at connection time"); }

    byte[] expanded = new byte[1024 * 1024];
    try { expanded.Deflate().Inflate(4096); throw new Exception("Deflate bomb accepted"); }
    catch (InvalidDataException) { Console.WriteLine("PASS bounded Deflate decoding"); }
    try { expanded.BrotliCompress().BrotliDecompress(maximumOutputBytes: 4096); throw new Exception("Brotli bomb accepted"); }
    catch (InvalidDataException) { Console.WriteLine("PASS bounded Brotli decoding"); }

    var receipt = assembly.GetType("DSFiles_Server.Helpers.UploadReceipt")!;
    var protect = receipt.GetMethod("Protect", BindingFlags.Static | BindingFlags.NonPublic)!;
    var unprotect = receipt.GetMethod("Unprotect", BindingFlags.Static | BindingFlags.NonPublic)!;
    const string secret = "ids:webhook-id:server-webhook-secret";
    string token = (string)protect.Invoke(null, [secret])!;
    Check(!token.Contains("server-webhook-secret") && (string)unprotect.Invoke(null, [token])! == secret, "server credential encrypted, authenticated and recoverable");
    char[] tampered = token.ToCharArray(); tampered[30] = tampered[30] == 'a' ? 'b' : 'a';
    try { unprotect.Invoke(null, [new string(tampered)]); throw new Exception("Tampered token accepted"); }
    catch (TargetInvocationException e) when (e.InnerException is System.Security.Cryptography.CryptographicException) { Console.WriteLine("PASS modified deletion capability rejected"); }
    var huge = new DefaultHttpContext(); huge.Response.Body = new MemoryStream();
    huge.Request.Headers["webhook"] = "https://discord.com/api/webhooks/12345/token";
    huge.Request.Headers["Upload-Protocol"] = "2"; huge.Request.Headers["filename"] = "test.bin"; huge.Request.Headers["filesize"] = "9223372036854775807";
    await (Task)handshake.Invoke(null, [huge.Request, huge.Response])!;
    Check(huge.Response.StatusCode == 413, "oversized session rejected before allocation or network");

    var chunksType = assembly.GetType("DSFiles_Server.Routes.DSFilesChunkedUploadHandle")!;
    using var discord = new HttpClient(new FakeDiscord());
    program.GetField("uploadClient")!.SetValue(null, discord);
    Environment.SetEnvironmentVariable("WEBHOOK", "https://discord.com/api/webhooks/12345/server-secret");
    DefaultHttpContext UploadRequest()
    {
        var context = new DefaultHttpContext(); context.Response.Body = new MemoryStream();
        context.Request.Headers["Upload-Protocol"] = "2";
        context.Request.Headers["filename"] = "test\".bin";
        context.Request.Headers["filesize"] = "3";
        context.Request.Headers["encrypt"] = "false";
        return context;
    }
    var valid = UploadRequest();
    await (Task)handshake.Invoke(null, [valid.Request, valid.Response])!;
    string session = valid.Response.Headers["Session-ID"].ToString();
    Check(session.Length > 0, "valid Discord handshake returns an encrypted upload authorization");
    var active = (SemaphoreSlim)chunksType.GetField("ActiveRequests", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    var chunkMethod = chunksType.GetMethod("HandleChunk")!;
    DefaultHttpContext Chunk(int length)
    {
        var context = new DefaultHttpContext(); context.Response.Body = new MemoryStream();
        context.Request.Headers["Session-ID"] = session; context.Request.Headers["chunk"] = "1";
        context.Request.ContentLength = length; context.Request.Body = new MemoryStream(new byte[length]);
        return context;
    }
    for (int i = 0; i < 8; i++) active.Wait();
    try
    {
        var limited = Chunk(3);
        await (Task)chunkMethod.Invoke(null, [limited.Request, limited.Response])!;
        Check(limited.Response.StatusCode == 429, "concurrent chunk quota enforced");
    }
    finally { active.Release(8); }
    var badChunk = Chunk(2);
    await (Task)chunkMethod.Invoke(null, [badChunk.Request, badChunk.Response])!;
    Check(badChunk.Response.StatusCode == 422 && active.CurrentCount == 8, "incorrect chunk size rejected without leaking transfer slots");
    var goodChunk = Chunk(3);
    await (Task)chunkMethod.Invoke(null, [goodChunk.Request, goodChunk.Response])!;
    goodChunk.Response.Body.Position = 0;
    using var chunkJson = await System.Text.Json.JsonDocument.ParseAsync(goodChunk.Response.Body);
    var finish = new DefaultHttpContext(); finish.Response.Body = new MemoryStream(); finish.Request.Headers["Session-ID"] = session;
    finish.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(new System.Text.Json.Nodes.JsonObject
        { ["receipts"] = new System.Text.Json.Nodes.JsonArray(chunkJson.RootElement.GetProperty("receipt").GetString()) }.ToJsonString()));
    await (Task)chunksType.GetMethod("HandleFinalize")!.Invoke(null, [finish.Request, finish.Response])!;
    Check(finish.Response.StatusCode == 200, "signed fragment receipts finalize the upload");
    finish.Response.Body.Position = 0;
    string receiptText = await new StreamReader(finish.Response.Body).ReadToEndAsync();
    if (!receiptText.StartsWith('{'))
    {
        string quoted = (string)assembly.GetType("DSFiles_Server.JWTManager")!.GetMethod("ProcessReturnedToken")!.Invoke(null, [receiptText])!;
        using var outer = System.Text.Json.JsonDocument.Parse(quoted);
        receiptText = outer.RootElement.GetString()!;
    }
    using var receiptJson = System.Text.Json.JsonDocument.Parse(receiptText);
    string capability = receiptJson.RootElement.GetProperty("removeToken").GetString()!;
    Check(receiptJson.RootElement.GetProperty("name").GetString() == "test\".bin", "receipt escapes filenames correctly");
    Check(capability.StartsWith("srv:") && !capability.Contains("server-secret")
        && ((string)unprotect.Invoke(null, [capability])!).EndsWith(":server-secret"), "real upload receipt conceals configured webhook credential");
    Check(active.CurrentCount == 8, "completed upload releases request slots");

    var jwt = assembly.GetType("DSFiles_Server.JWTManager")!;
    var proofType = assembly.GetType("DSFiles_Server.UploadProof")!;
    string binding = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("delete\n" + capability)));
    var issued = new DefaultHttpContext(); issued.Response.Body = new MemoryStream(); issued.Request.Headers["Remove-Token"] = capability;
    await (Task)proofType.GetMethod("IssueDelete")!.Invoke(null, [issued])!;
    issued.Response.Body.Position = 0;
    using var issuedJson = await System.Text.Json.JsonDocument.ParseAsync(issued.Response.Body);
    string challenge = issuedJson.RootElement.GetProperty("challenge").GetString()!;
    Check(proofType.GetField("Pending", BindingFlags.NonPublic | BindingFlags.Static) == null, "challenges store no pending records");
    var validateChallenge = jwt.GetMethod("ValidateDeletionChallenge", BindingFlags.Static | BindingFlags.NonPublic)!;
    Check(!(bool)validateChallenge.Invoke(null, [challenge, binding + "altered"])!, "deletion challenge cannot authorize another file");
    string expiredChallenge = (string)jwt.GetMethod("CreateDeletionChallenge", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [binding, DateTimeOffset.UtcNow.AddSeconds(-1)])!;
    Check(!(bool)validateChallenge.Invoke(null, [expiredChallenge, binding])!, "expired stateless challenge is rejected");
    string solution = "";
    for (long nonce = 0; ; nonce++)
    {
        string candidate = challenge + ":" + nonce;
        byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(candidate));
        if (hash[0] == 0 && hash[1] == 0 && (hash[2] & 0xf0) == 0) { solution = candidate; break; }
    }
    async Task Replica(bool wrongKeys)
    {
        var start = new System.Diagnostics.ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--validate-cap"); start.ArgumentList.Add(capability); start.ArgumentList.Add(solution);
        if (wrongKeys) start.ArgumentList.Add("--wrong-keys");
        using var process = System.Diagnostics.Process.Start(start)!;
        string stdout = await process.StandardOutput.ReadToEndAsync();
        string stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Check(process.ExitCode == 0, "independent replica process: " + stderr);
        Console.Write(stdout);
    }
    await Replica(false); await Replica(true);
    string normalJwt = (string)jwt.GetMethod("CreateSecureToken")!.Invoke(null, ["not-a-deletion-capability"])!;
    try { jwt.GetMethod("ReadDeletionToken", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [normalJwt]); throw new Exception("Wrong-purpose JWT accepted"); }
    catch (TargetInvocationException e) when (e.InnerException is System.Security.Cryptography.CryptographicException) { Console.WriteLine("PASS normal upload JWT cannot authorize deletion"); }
    Environment.SetEnvironmentVariable("ENCRYPTKEY", null);
    try
    {
        var missingKeys = UploadRequest();
        await (Task)handshake.Invoke(null, [missingKeys.Request, missingKeys.Response])!;
        Check(missingKeys.Response.StatusCode == 503 && active.CurrentCount == 8, "missing keys reject server upload before creating a session");
    }
    finally { Environment.SetEnvironmentVariable("ENCRYPTKEY", new string('e', 32)); }

    var animationType = assembly.GetType("DSFiles_Server.Routes.ConsoleAnimation")!;
    var configType = animationType.GetNestedType("ConversorConfig", BindingFlags.NonPublic)!;
    object config = Activator.CreateInstance(configType, nonPublic: true)!;
    foreach (var (name, value) in new[] { ("FpsDivisor", 1000), ("WidthDivisor", 1), ("HeightDivisor", 2), ("MinColorChangeNeeded", 75) })
        configType.GetProperty(name)!.SetValue(config, value);
    configType.GetProperty("Name")!.SetValue(config, "test");
    using var image = new MemoryStream(Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7"));
    using var output = new MemoryStream();
    var encoded = ((int[], byte[][]))animationType.GetMethod("EncodeFrames", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [config, image, output])!;
    Check(encoded.Item2.Length == 1 && encoded.Item2[0].Length > 0, "valid GIF still renders with a frame divisor larger than its frame count");

    var quic = assembly.GetType("DSFiles_Server.QuicServer")!;
    var readLoop = quic.GetMethod("ReadLoop", BindingFlags.NonPublic | BindingFlags.Static)!;
    using var invalidPacket = new MemoryStream();
    using (var writer = new BinaryWriter(invalidPacket, Encoding.UTF8, leaveOpen: true))
    {
        writer.Write((ushort)0); writer.Write((byte)0); writer.Write(int.MaxValue);
    }
    invalidPacket.Position = 0;
    var quicClient = Activator.CreateInstance(quic.GetNestedType("Client")!)!;
    quicClient.GetType().GetProperty("Stream")!.SetValue(quicClient, invalidPacket);
    try { await (Task)readLoop.Invoke(null, [quicClient, CancellationToken.None])!; throw new Exception("Unbounded QUIC payload accepted"); }
    catch (InvalidDataException) { Console.WriteLine("PASS QUIC oversized message rejected before allocation"); }
    var queue = quicClient.GetType().GetProperty("SendQueue")!.GetValue(quicClient)!;
    var queueWriter = queue.GetType().GetProperty("Writer")!.GetValue(queue)!;
    var tryWrite = queueWriter.GetType().GetMethod("TryWrite")!;
    var packet = Activator.CreateInstance(quic.GetNestedType("OutgoingPacket", BindingFlags.NonPublic)!)!;
    int queueLimit = (int)quic.GetField("MaxQueuedPacketsPerClient", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
    for (int i = 0; i < queueLimit; i++)
        if (!(bool)tryWrite.Invoke(queueWriter, [packet])!) throw new Exception("QUIC queue capacity changed unexpectedly.");
    Check(!(bool)tryWrite.Invoke(queueWriter, [packet])!, "QUIC queue refuses an unbounded backlog");

    var budgetClient = Activator.CreateInstance(quic.GetNestedType("Client")!)!;
    var reserve = budgetClient.GetType().GetMethod("TryReserveQueueSpace")!;
    for (int i = 0; i < 8; i++)
        if (!(bool)reserve.Invoke(budgetClient, [1024 * 1024])!) throw new Exception("QUIC byte budget rejected a valid reservation.");
    Check(!(bool)reserve.Invoke(budgetClient, [1])!, "QUIC byte budget rejects more than 8 MiB");

}
finally { await sentinel.StopAsync(); }

sealed class FakeDiscord : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string json = request.Method == HttpMethod.Get
            ? "{\"channel_id\":\"12345\",\"guild_id\":\"12345\",\"id\":\"12345\",\"name\":\"test\",\"type\":1,\"token\":\"server-secret\"}"
            : request.Content is MultipartFormDataContent
                ? "{\"id\":\"1400000000000000001\",\"attachments\":[{\"id\":\"1400000000000000002\"}]}" : "{}";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }
}
