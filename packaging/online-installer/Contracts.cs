using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace CitrusOnline
{
    internal static class Contracts
    {
        static readonly byte[] Data = { 1, 2, 3 };
        static string Digest { get { using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Data)).Replace("-", "").ToLowerInvariant(); } }
        sealed class Handler : HttpMessageHandler
        {
            internal Func<HttpResponseMessage> Respond;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            { token.ThrowIfCancellationRequested(); return Task.FromResult(Respond()); }
        }
        static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        static async Task Failure(Func<Task> action)
        {
            try { await action(); }
            catch (InvalidDataException) { return; }
            catch (HttpRequestException) { return; }
            catch (OperationCanceledException) { return; }
            throw new Exception("Expected download rejection");
        }
        static HttpResponseMessage Body(byte[] bytes) { return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }; }
        static async Task Check()
        {
            using (MemoryStream output = new MemoryStream())
            {
                long progress = 0;
                await Package.CopyVerifiedAsync(new MemoryStream(Data), output, 3, Digest, CancellationToken.None, n => progress = n);
                Assert(output.ToArray().Length == 3 && progress == 3, "Valid copy and progress");
            }
            await Failure(() => Package.CopyVerifiedAsync(new MemoryStream(Data), new MemoryStream(), 4, Digest, CancellationToken.None, n => { }));
            await Failure(() => Package.CopyVerifiedAsync(new MemoryStream(Data), new MemoryStream(), 2, Digest, CancellationToken.None, n => { }));
            await Failure(() => Package.CopyVerifiedAsync(new MemoryStream(Data), new MemoryStream(), 3, new string('a', 64), CancellationToken.None, n => { }));
            await Failure(() => Package.CopyVerifiedAsync(new MemoryStream(Data), new MemoryStream(), 3, Digest, new CancellationToken(true), n => { }));
            using (Handler handler = new Handler { Respond = () => Body(Data) })
            using (HttpClient client = new HttpClient(handler))
            {
                await Failure(() => Package.DownloadAsync(client, new Uri("http://github.com"), new MemoryStream(), 3, Digest, CancellationToken.None, n => { }));
                await Failure(() => Package.DownloadAsync(client, new Uri("https://user@github.com"), new MemoryStream(), 3, Digest, CancellationToken.None, n => { }));
                handler.Respond = () => { var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new Uri("http://github.com"); return response; };
                await Failure(() => Package.DownloadAsync(client, new Uri("https://github.com"), new MemoryStream(), 3, Digest, CancellationToken.None, n => { }));
                handler.Respond = () => new HttpResponseMessage(HttpStatusCode.NotFound);
                await Failure(() => Package.DownloadAsync(client, new Uri("https://github.com"), new MemoryStream(), 3, Digest, CancellationToken.None, n => { }));
                await Failure(() => Package.DownloadAsync(client, new Uri("https://example.com"), new MemoryStream(), 3, Digest, CancellationToken.None, n => { }));
                await Failure(() => Package.DownloadAsync(client, new Uri("https://github.com:444"), new MemoryStream(), 3, Digest, CancellationToken.None, n => { }));
                handler.Respond = () => { var response = Body(Data); response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html"); return response; };
                await Failure(() => Package.DownloadAsync(client, new Uri("https://github.com"), new MemoryStream(), 3, Digest, CancellationToken.None, n => { }));
                handler.Respond = () => Body(new byte[] { 1, 2 });
                await Failure(() => Package.DownloadAsync(client, new Uri("https://github.com"), new MemoryStream(), 3, Digest, CancellationToken.None, n => { }));
                handler.Respond = () => new HttpResponseMessage(HttpStatusCode.PartialContent);
                await Failure(() => Package.DownloadAsync(client, new Uri("https://github.com"), new MemoryStream(), 3, Digest, CancellationToken.None, n => { }));
                handler.Respond = () => new HttpResponseMessage(HttpStatusCode.Redirect);
                await Failure(() => Package.DownloadAsync(client, new Uri("https://github.com"), new MemoryStream(), 3, Digest, CancellationToken.None, n => { }));
                handler.Respond = () => { var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new Uri("https://github.com/again"); return response; };
                await Failure(() => Package.DownloadAsync(client, new Uri("https://github.com"), new MemoryStream(), 3, Digest, CancellationToken.None, n => { }));
                string directory = Package.PrivateDirectory();
                try
                {
                    int launches = 0;
                    Func<string, Task> launch = path => { launches++; Assert(File.ReadAllBytes(path).Length == 3, "Verified bytes reach launcher"); return Task.FromResult(0); };
                    handler.Respond = () => Body(new byte[] { 9, 9, 9 });
                    await Failure(() => Package.DownloadAndRunAsync(client, new Uri("https://github.com"), Path.Combine(directory, "bad.exe"), 3, Digest, CancellationToken.None, n => { }, launch));
                    Assert(launches == 0, "Corrupt data must never execute");
                    handler.Respond = () => Body(Data);
                    await Failure(() => Package.DownloadAndRunAsync(client, new Uri("https://github.com"), Path.Combine(directory, "cancelled.exe"), 3, Digest, new CancellationToken(true), n => { }, launch));
                    Assert(launches == 0, "Cancelled data must never execute");
                    using (CancellationTokenSource finalChunk = new CancellationTokenSource())
                        await Failure(() => Package.DownloadAndRunAsync(client, new Uri("https://github.com"), Path.Combine(directory, "cancelled-final.exe"), 3, Digest, finalChunk.Token, n => finalChunk.Cancel(), launch));
                    Assert(launches == 0, "Cancellation on final chunk must never execute");
                    await Package.DownloadAndRunAsync(client, new Uri("https://github.com"), Path.Combine(directory, "good.exe"), 3, Digest, CancellationToken.None, n => { }, launch);
                    Assert(launches == 1, "Verified installer executes once");
                }
                finally { Directory.Delete(directory, true); }
            }
            Console.WriteLine("Downloader contracts passed");
        }
        static int Main()
        {
            try { Check().GetAwaiter().GetResult(); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}

