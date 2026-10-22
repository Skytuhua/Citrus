using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CitrusOnline
{
    internal static class Package
    {
        internal static async Task CopyVerifiedAsync(Stream input, Stream output, long expectedBytes, string expectedHash,
            CancellationToken cancellation, Action<long> progress)
        {
            long received = 0;
            byte[] buffer = new byte[81920];
            using (SHA256 hash = SHA256.Create())
            {
                int count;
                while ((count = await input.ReadAsync(buffer, 0, buffer.Length, cancellation).ConfigureAwait(false)) != 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (count > expectedBytes - received) throw new InvalidDataException("The download is larger than the published installer.");
                    await output.WriteAsync(buffer, 0, count, cancellation).ConfigureAwait(false);
                    hash.TransformBlock(buffer, 0, count, null, 0);
                    received += count;
                    progress(received);
                }
                cancellation.ThrowIfCancellationRequested();
                hash.TransformFinalBlock(new byte[0], 0, 0);
                string digest = BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
                if (received != expectedBytes || !String.Equals(digest, expectedHash, StringComparison.Ordinal))
                    throw new InvalidDataException("The installer did not match its published size and SHA-256 checksum. Nothing was installed.");
            }
        }

        internal static async Task DownloadAsync(HttpClient client, Uri address, Stream output, long bytes, string digest,
            CancellationToken cancellation, Action<long> progress)
        {
            for (int redirect = 0; redirect < 8; redirect++)
            {
                if (address.Scheme != Uri.UriSchemeHttps || !address.IsDefaultPort || address.UserInfo.Length != 0 ||
                    (address.Host != "github.com" && address.Host != "release-assets.githubusercontent.com" && address.Host != "objects.githubusercontent.com"))
                    throw new InvalidDataException("The download must use GitHub HTTPS storage.");
                using (HttpResponseMessage response = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false))
                {
                    int status = (int)response.StatusCode;
                    if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
                    {
                        Uri location = response.Headers.Location;
                        if (location == null) throw new InvalidDataException("The download redirect is missing its destination.");
                        address = location.IsAbsoluteUri ? location : new Uri(address, location);
                        continue;
                    }
                    if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException("The installer server did not return a complete file.");
                    string media = response.Content.Headers.ContentType == null ? null : response.Content.Headers.ContentType.MediaType;
                    if (media != null && media != "application/octet-stream" && media != "application/x-msdownload" &&
                        media != "application/vnd.microsoft.portable-executable" && media != "binary/octet-stream")
                        throw new InvalidDataException("The server did not return an executable download.");
                    if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value != bytes)
                        throw new InvalidDataException("The server returned an unexpected installer size.");
                    using (cancellation.Register(response.Dispose))
                    {
                        try
                        {
                            using (Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                                await CopyVerifiedAsync(input, output, bytes, digest, cancellation, progress).ConfigureAwait(false);
                        }
                        catch (Exception) { cancellation.ThrowIfCancellationRequested(); throw; }
                    }
                    return;
                }
            }
            throw new InvalidDataException("Too many download redirects.");
        }

        internal static async Task DownloadAndRunAsync(HttpClient client, Uri address, string destination, long bytes,
            string digest, CancellationToken cancellation, Action<long> progress, Func<string, Task> launch)
        {
            using (FileStream output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await DownloadAsync(client, address, output, bytes, digest, cancellation, progress).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            await launch(destination).ConfigureAwait(false);
        }

        internal static string PrivateDirectory()
        {
            string directory = Path.Combine(Path.GetTempPath(), "Citrus-Setup-" + Guid.NewGuid().ToString("N"));
            DirectorySecurity security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User,
                FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            Directory.CreateDirectory(directory, security);
            return directory;
        }
    }

    internal sealed class InstallerWindow : Form
    {
        readonly Label status = new Label { AutoSize = false, Dock = DockStyle.Fill, Text = "Ready to download the complete installer." };
        readonly ProgressBar progress = new ProgressBar { Dock = DockStyle.Fill, Maximum = 1000 };
        readonly Button start = new Button { AutoSize = true, Text = "Download and install", AccessibleName = "Download and install Citrus" };
        readonly Button cancel = new Button { AutoSize = true, Text = "Close" };
        CancellationTokenSource cancellation;
        bool closing;
        bool installing;

        internal InstallerWindow()
        {
            Text = "Install Citrus";
            Font = new Font("Segoe UI", 10);
            ClientSize = new Size(530, 265);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            TableLayoutPanel panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 5 };
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
            panel.Controls.Add(new Label { Text = "Citrus " + Release.Version, Font = new Font(Font, FontStyle.Bold), AutoSize = true }, 0, 0);
            panel.Controls.Add(new Label { Text = String.Format("Downloads {0:0.00} GiB during setup. Internet is required.\nCitrus works offline after installation. Unsigned preview.", Release.Bytes / 1073741824.0), Dock = DockStyle.Fill }, 0, 1);
            panel.Controls.Add(progress, 0, 2);
            panel.Controls.Add(status, 0, 3);
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(start);
            panel.Controls.Add(buttons, 0, 4);
            Controls.Add(panel);
            start.Click += async delegate { await InstallAsync(); };
            cancel.Click += delegate { if (cancellation == null) Close(); else cancellation.Cancel(); };
            FormClosing += delegate(object sender, FormClosingEventArgs args)
            {
                if (cancellation != null)
                {
                    args.Cancel = true;
                    if (!installing) { closing = true; cancellation.Cancel(); }
                }
            };
        }

        async Task InstallAsync()
        {
            if (!Environment.Is64BitOperatingSystem) { status.Text = "Citrus requires 64-bit Windows."; return; }
            start.Enabled = false;
            cancel.Text = "Cancel";
            progress.Value = 0;
            cancellation = new CancellationTokenSource();
            string directory = null;
            try
            {
                directory = Package.PrivateDirectory();
                status.Text = "Connecting to the Citrus release on GitHub…";
                SynchronizationContext ui = SynchronizationContext.Current;
                int lastProgress = -1;
                Action<long> report = delegate(long received)
                {
                    int currentProgress = (int)(received * 1000 / Release.Bytes);
                    if (currentProgress == lastProgress) return;
                    lastProgress = currentProgress;
                    ui.Post(delegate
                    {
                        progress.Value = currentProgress;
                        status.Text = String.Format("Downloading {0:0} / {1:0} MiB…", received / 1048576.0, Release.Bytes / 1048576.0);
                    }, null);
                };
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                using (HttpClientHandler handler = new HttpClientHandler { AllowAutoRedirect = false })
                using (HttpClient client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan })
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("Citrus-Web-Setup/" + Release.Version);
                    await Package.DownloadAndRunAsync(client, new Uri(Release.Url), Path.Combine(directory, "Citrus-Setup.exe"),
                        Release.Bytes, Release.Sha256, cancellation.Token, report, async delegate(string installer)
                        {
                            await Task.Run(delegate
                            {
                                cancellation.Token.ThrowIfCancellationRequested();
                                ui.Send(delegate
                                {
                                    installing = true;
                                    cancel.Enabled = false;
                                    status.Text = "Download verified. Follow the Citrus installation window.";
                                }, null);
                                cancellation.Token.ThrowIfCancellationRequested();
                                using (Process process = Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true }))
                                {
                                    if (process == null) throw new IOException("Windows could not open the verified installer.");
                                    process.WaitForExit();
                                    if (process.ExitCode != 0) throw new IOException("Installation did not finish (code " + process.ExitCode + "). You can retry or use the offline installer.");
                                }
                            }).ConfigureAwait(false);
                        });
                }
                status.Text = "Citrus setup has finished.";
                start.Text = "Download again";
            }
            catch (OperationCanceledException) { status.Text = "Download cancelled. Nothing was installed. You can retry."; }
            catch (Exception error) { status.Text = "Setup could not finish. You can retry."; MessageBox.Show(this, error.Message, "Citrus setup", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally
            {
                if (directory != null)
                {
                    try { Directory.Delete(directory, true); }
                    catch (IOException) { status.Text += " Temporary setup files could not be removed: " + directory; }
                    catch (UnauthorizedAccessException) { status.Text += " Temporary setup files could not be removed: " + directory; }
                }
                installing = false;
                cancellation.Dispose();
                cancellation = null;
                start.Enabled = true;
                cancel.Enabled = true;
                cancel.Text = "Close";
                if (closing) Close();
            }
        }

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new InstallerWindow());
        }
    }
}
