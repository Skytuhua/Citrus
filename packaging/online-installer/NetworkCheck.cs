using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace CitrusOnline
{
    // A separate test executable; the public GUI has no testing or silent-install arguments.
    internal static class NetworkCheck
    {
        static async Task Verify()
        {
            string directory = Package.PrivateDirectory();
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                using (HttpClientHandler handler = new HttpClientHandler { AllowAutoRedirect = false })
                using (HttpClient client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan })
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("Citrus-Network-Verification/" + Release.Version);
                    int last = -1;
                    await Package.DownloadAndRunAsync(client, new Uri(Release.Url), Path.Combine(directory, "Citrus-Setup.exe"),
                        Release.Bytes, Release.Sha256, CancellationToken.None, delegate(long count)
                        {
                            int percent = (int)(count * 100 / Release.Bytes);
                            if (percent != last && percent % 10 == 0) { Console.WriteLine("Downloaded " + percent + "%"); last = percent; }
                        }, delegate(string path)
                        {
                            Console.WriteLine("Verified " + new FileInfo(path).Length + " bytes; SHA-256 " + Release.Sha256 + "; installer execution deliberately skipped.");
                            return Task.FromResult(0);
                        });
                }
            }
            finally { Directory.Delete(directory, true); }
        }
        static int Main()
        {
            try { Verify().GetAwaiter().GetResult(); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
