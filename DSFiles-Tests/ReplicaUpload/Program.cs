using DSFiles_Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static DSFiles_Shared.DiscordFilesSpliter;

Environment.SetEnvironmentVariable("SIGNKEY", new string(args.Contains("--wrong-keys") ? 'x' : 's', 64));
Environment.SetEnvironmentVariable("ENCRYPTKEY", new string('e', 32));
Environment.SetEnvironmentVariable("WEBHOOK", "https://discord.com/api/webhooks/12345/server-secret");
if (args.Contains("--different-webhook")) Environment.SetEnvironmentVariable("WEBHOOK", "https://discord.com/api/webhooks/77777/other-secret");
var server = Assembly.Load("DSFiles_Server");
if (args.Contains("--replica"))
{
    if (args.Contains("--missing-keys")) Environment.SetEnvironmentVariable("ENCRYPTKEY", null);
    var input = JsonNode.Parse(await Console.In.ReadToEndAsync())!;
    var context = new DefaultHttpContext(); context.Response.Body = new MemoryStream();
    context.Request.Body = new MemoryStream(Convert.FromBase64String(input["body"]?.GetValue<string>() ?? ""));
    context.Request.ContentLength = context.Request.Body.Length;
    foreach (var header in input["headers"]!.AsObject()) context.Request.Headers[header.Key] = header.Value!.GetValue<string>();
    server.GetType("DSFiles_Server.Program")!.GetField("uploadClient")!.SetValue(null, new HttpClient(new ForwardProvider(input["provider"]!.GetValue<string>())));
    string method = input["method"]!.GetValue<string>();
    if (method == "Issue") await (Task)server.GetType("DSFiles_Server.UploadProof")!.GetMethod("Issue")!.Invoke(null, [context])!;
    else
    {
        var handler = server.GetType("DSFiles_Server.Routes.DSFilesChunkedUploadHandle")!.GetMethod(method)!;
        await (Task)handler.Invoke(null, [context.Request, context.Response])!;
    }
    context.Response.Body.Position = 0;
    var headers = new JsonObject();
    foreach (var header in context.Response.Headers) headers[header.Key] = header.Value.ToString();
    Console.Write(new JsonObject { ["status"] = context.Response.StatusCode, ["headers"] = headers, ["body"] = await new StreamReader(context.Response.Body).ReadToEndAsync() }.ToJsonString());
    return;
}

void Check(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); }
var files = new Dictionary<ulong, byte[]>();
var messages = new Dictionary<ulong, ulong>();
int posts = 0, deletes = 0, gets = 0;
ulong nextId = 1400000000000000000;
var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
var provider = builder.Build();
provider.Map("/discord", async context =>
{
    if (context.Request.Method == "GET")
    {
        gets++;
        await context.Response.WriteAsync("{\"channel_id\":\"12345\",\"guild_id\":\"12345\",\"id\":\"12345\",\"name\":\"test\",\"type\":1,\"token\":\"server-secret\"}");
    }
    else if (context.Request.Method == "DELETE")
    {
        var target = new Uri(context.Request.Query["target"].ToString());
        ulong id = ulong.Parse(target.AbsolutePath.Split('/')[^1]);
        deletes++;
        if (messages.Remove(id, out var attachment)) { files.Remove(attachment); context.Response.StatusCode = 204; }
        else context.Response.StatusCode = 404;
    }
    else
    {
        posts++;
        string boundary = Microsoft.Net.Http.Headers.HeaderUtilities.RemoveQuotes(Microsoft.Net.Http.Headers.MediaTypeHeaderValue.Parse(context.Request.ContentType).Boundary).Value!;
        var reader = new MultipartReader(boundary, context.Request.Body);
        var section = await reader.ReadNextSectionAsync() ?? throw new Exception("Missing attachment");
        using var data = new MemoryStream(); await section.Body.CopyToAsync(data);
        ulong attachment = nextId++, message = nextId++;
        files[attachment] = data.ToArray(); messages[message] = attachment;
        await context.Response.WriteAsync(new JsonObject { ["id"] = message.ToString(), ["attachments"] = new JsonArray(new JsonObject { ["id"] = attachment.ToString() }) }.ToJsonString());
    }
});
await provider.StartAsync();
try
{
    async Task<JsonNode> Replica(string method, JsonObject headers, byte[]? body = null, string? option = null)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(Program).Assembly.Location); start.ArgumentList.Add("--replica");
        if (option != null) start.ArgumentList.Add(option);
        using var process = Process.Start(start)!;
        await process.StandardInput.WriteAsync(new JsonObject
        {
            ["method"] = method, ["provider"] = provider.Urls.Single(), ["headers"] = headers.DeepClone(), ["body"] = Convert.ToBase64String(body ?? [])
        }.ToJsonString());
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string output = await stdout, error = await stderr;
        if (process.ExitCode != 0) throw new Exception(error + output);
        return JsonNode.Parse(output)!;
    }
    static int Status(JsonNode response) => response["status"]!.GetValue<int>();
    static string Body(JsonNode response) => response["body"]!.GetValue<string>();
    static byte[] Manifest(params string[] receipts) => Encoding.UTF8.GetBytes(new JsonObject { ["receipts"] = new JsonArray(receipts.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()) }.ToJsonString());
    byte[] original = Enumerable.Range(0, CHUNK_SIZE + 17).Select(i => (byte)(i % 251)).ToArray();
    var headers = new JsonObject { ["filename"] = "replicas.bin", ["filesize"] = original.Length.ToString(), ["Upload-Protocol"] = "2", ["encrypt"] = "true" };
    var issued = await Replica("Issue", headers);
    var proofData = JsonNode.Parse(Body(issued))!;
    string challenge = proofData["challenge"]!.GetValue<string>();
    for (long nonce = 0; ; nonce++)
    {
        string candidate = challenge + ":" + nonce;
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(candidate));
        if (hash[0] == 0 && hash[1] == 0 && (hash[2] & 0xf0) == 0) { headers["Upload-Proof"] = candidate; break; }
    }
    var wrongSize = headers.DeepClone().AsObject(); wrongSize["filesize"] = "3";
    Check(Status(await Replica("HandleHandshake", wrongSize)) == 403 && gets == 0, "proof from another replica remains bound to file metadata");
    Check(Status(await Replica("HandleHandshake", headers, option: "--missing-keys")) == 503, "missing shared keys fail before provider access");
    var handshake = await Replica("HandleHandshake", headers);
    Check(Status(handshake) == 200 && gets == 1, "upload challenge and handshake succeed on fresh independent replicas");
    string session = handshake["headers"]!["Session-ID"]!.GetValue<string>();
    Check(!session.Contains("server-secret") && session.Split('.').Length == 5, "upload authorization encrypts provider credentials");
    var chunkHeaders = new JsonObject { ["Session-ID"] = session, ["chunk"] = "2" };
    var jwt = server.GetType("DSFiles_Server.JWTManager")!;
    var readSession = jwt.GetMethod("ReadUploadSession", BindingFlags.Static | BindingFlags.NonPublic)!;
    string decoded = (string)readSession.Invoke(null, [session])!;
    string expired = (string)jwt.GetMethod("CreateUploadSession", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [decoded, DateTimeOffset.UtcNow.AddSeconds(-1)])!;
    Check(Status(await Replica("HandleChunk", new JsonObject { ["Session-ID"] = expired, ["chunk"] = "2" }, [1])) == 410, "expired upload authorization is rejected");
    Check(Status(await Replica("HandleChunk", chunkHeaders, original[CHUNK_SIZE..], "--wrong-keys")) == 410 && posts == 0, "replica with different keys cannot upload");
    Check(Status(await Replica("HandleChunk", chunkHeaders, [1])) == 422 && posts == 0, "incorrect fragment length rejected before upload");
    var last = await Replica("HandleChunk", chunkHeaders, original[CHUNK_SIZE..]);
    Check(Status(last) == 200, "last fragment can arrive first on another replica");
    string lastReceipt = JsonNode.Parse(Body(last))!["receipt"]!.GetValue<string>();
    chunkHeaders["chunk"] = "1";
    var first = await Replica("HandleChunk", chunkHeaders, original[..CHUNK_SIZE], "--different-webhook");
    Check(Status(first) == 200 && posts == 2 && gets == 1, "first fragment uploads after process restart without re-fetching webhook metadata");
    string firstReceipt = JsonNode.Parse(Body(first))!["receipt"]!.GetValue<string>();
    Check(Status(await Replica("HandleFinalize", chunkHeaders, Manifest(firstReceipt))) == 400, "incomplete manifest rejected");
    Check(Status(await Replica("HandleFinalize", chunkHeaders, Manifest(firstReceipt, firstReceipt))) == 400, "duplicate fragment receipts rejected");
    char[] tampered = firstReceipt.ToCharArray(); tampered[40] = tampered[40] == 'a' ? 'b' : 'a';
    Check(Status(await Replica("HandleFinalize", chunkHeaders, Manifest(new string(tampered), lastReceipt))) == 410, "modified receipt rejected");
    var otherHeaders = new JsonObject { ["filename"] = "other.bin", ["filesize"] = "3", ["Upload-Protocol"] = "2" };
    var other = await Replica("HandleHandshake", otherHeaders);
    string otherSession = other["headers"]!["Session-ID"]!.GetValue<string>();
    var otherChunkHeaders = new JsonObject { ["Session-ID"] = otherSession, ["chunk"] = "1" };
    var otherChunk = await Replica("HandleChunk", otherChunkHeaders, [1, 2, 3]);
    string otherReceipt = JsonNode.Parse(Body(otherChunk))!["receipt"]!.GetValue<string>();
    Check(Status(await Replica("HandleFinalize", chunkHeaders, Manifest(otherReceipt, lastReceipt))) == 400, "receipts from another upload cannot be mixed");
    Check(Status(await Replica("HandleAbort", chunkHeaders, Manifest(otherReceipt))) == 400 && deletes == 0, "abort cannot delete another upload's fragment");
    int beforeFinal = posts;
    var finalized = await Replica("HandleFinalize", chunkHeaders, Manifest(lastReceipt, firstReceipt), "--different-webhook");
    Check(Status(finalized) == 200 && posts == beforeFinal, "fresh replica finalizes unordered receipts without provider writes");
    var file = JsonNode.Parse(Body(finalized))!;
    string seedString = file["seed"]!.GetValue<string>();
    string[] seedParts = seedString.Split('$'); byte[] seed = seedParts[0].FromBase64Url().Inflate();
    var ids = new GorillaTimestampCompressor().Decompress(seed[17..]);
    byte[] ciphertext = ids.SelectMany(id => files[id]).ToArray();
    using (var aes = new AesCTRStream(null, seedParts[1].FromBase64Url())) aes.Transform(ciphertext, 0);
    Check(ciphertext.SequenceEqual(original), "multi-replica encrypted fragments decrypt to the exact original bytes");
    var repeated = await Replica("HandleFinalize", chunkHeaders, Manifest(firstReceipt, lastReceipt));
    Check(JsonNode.Parse(Body(repeated))!["seed"]!.GetValue<string>() == seedString && posts == beforeFinal, "finalization can be retried on a fresh replica without duplicating messages");
    Check(file["removeToken"]!.GetValue<string>().StartsWith("srv:") && !Body(finalized).Contains("server-secret"), "final receipt does not reveal server webhook credential");
    Check(Status(await Replica("HandleAbort", chunkHeaders, Manifest(lastReceipt))) == 200 && deletes == 1 && files.Count == 2, "another replica removes only acknowledged fragments on abort");
    Check(Status(await Replica("HandleAbort", chunkHeaders, Manifest(lastReceipt))) == 200, "repeating abort remains safe after Discord reports not found");
    var handlerType = server.GetType("DSFiles_Server.Routes.DSFilesChunkedUploadHandle")!;
    Check(handlerType.GetField("Sessions", BindingFlags.Static | BindingFlags.NonPublic) == null, "no per-upload session registry exists");
}
finally { await provider.StopAsync(); }

sealed class ForwardProvider(string origin) : HttpMessageHandler
{
    private readonly HttpClient client = new();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var forwarded = new HttpRequestMessage(request.Method, origin + "/discord?target=" + Uri.EscapeDataString(request.RequestUri!.AbsoluteUri)) { Content = request.Content };
        return client.SendAsync(forwarded, ct);
    }
    protected override void Dispose(bool disposing) { if (disposing) client.Dispose(); base.Dispose(disposing); }
}
