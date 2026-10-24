using Citrus.Core;

namespace Citrus.Core.Tests;

public sealed class OutputWriterTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "citrus-tests-" + Guid.NewGuid().ToString("N"));
    public OutputWriterTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public async Task Long_legal_file_name_preserves_extension_bytes_and_leaves_no_staging()
    {
        string desired = Path.Combine(root, new string('a', 220) + ".png");
        string output = await new OutputWriter().WriteFileAsync(desired, async (temporary, token) =>
        {
            Assert.Equal(".png", Path.GetExtension(temporary));
            await File.WriteAllBytesAsync(temporary, [1, 2, 3, 255], token);
        }, default);
        Assert.Equal(desired, output);
        Assert.Equal(new byte[] { 1, 2, 3, 255 }, await File.ReadAllBytesAsync(output));
        Assert.Equal(new[] { desired }, Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public async Task Long_legal_directory_name_preserves_contents_and_leaves_no_staging()
    {
        string desired = Path.Combine(root, new string('a', 220));
        string output = await new OutputWriter().WriteDirectoryAsync(desired, async (temporary, token) =>
        {
            Directory.CreateDirectory(Path.Combine(temporary, "nested"));
            await File.WriteAllTextAsync(Path.Combine(temporary, "nested", "entry.txt"), "complete", token);
        }, default);
        Assert.Equal(desired, output);
        Assert.Equal("complete", await File.ReadAllTextAsync(Path.Combine(output, "nested", "entry.txt")));
        Assert.Equal(new[] { desired }, Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public async Task Concurrent_writes_preserve_existing_bytes_and_each_new_output()
    {
        var desired = Path.Combine(root, "photo.png");
        await File.WriteAllTextAsync(desired, "original");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int ready = 0;
        async Task Write(string path, CancellationToken token)
        {
            Assert.Equal(".png", Path.GetExtension(path));
            await File.WriteAllTextAsync(path, "new", token);
            if (Interlocked.Increment(ref ready) == 2) gate.SetResult();
            await gate.Task.WaitAsync(token);
        }
        var writer = new OutputWriter();
        var outputs = await Task.WhenAll(writer.WriteFileAsync(desired, Write, default), writer.WriteFileAsync(desired, Write, default));
        Assert.Equal(2, outputs.Distinct().Count());
        Assert.Equal("original", await File.ReadAllTextAsync(desired));
        Assert.All(outputs, path => Assert.Equal("new", File.ReadAllText(path)));
        Assert.Equal(3, Directory.GetFileSystemEntries(root).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_canceled_file_write_removes_staging_and_final(bool cancel)
    {
        using var cts = new CancellationTokenSource();
        async Task Write(string path, CancellationToken token)
        {
            await File.WriteAllTextAsync(path, "partial", token);
            if (cancel) { cts.Cancel(); token.ThrowIfCancellationRequested(); }
            throw new IOException("fixture failure");
        }
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OutputWriter().WriteFileAsync(Path.Combine(root, "photo.png"), Write, cts.Token));
        else await Assert.ThrowsAsync<IOException>(() => new OutputWriter().WriteFileAsync(Path.Combine(root, "photo.png"), Write, cts.Token));
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_failure_preserves_original_write_exception_and_cancellation(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Exception original = cancel ? new OperationCanceledException(cancellation.Token) : new InvalidDataException("encoder failure");
        string? staging = null;
        var actual = await Record.ExceptionAsync(() => new OutputWriter().WriteFileAsync(Path.Combine(root, "photo.avif"), (path, _) =>
        {
            staging = path;
            // A directory in a file slot deterministically makes File.Delete fail on both platforms.
            Directory.CreateDirectory(path);
            return Task.FromException(original);
        }, default));

        Assert.Same(original, actual);
        var cleanup = Assert.IsAssignableFrom<Exception>(actual.Data["Citrus.OutputWriter.CleanupFailure"]);
        Assert.True(cleanup is IOException or UnauthorizedAccessException);
        Assert.True(Directory.Exists(staging));
        Assert.False(File.Exists(Path.Combine(root, "photo.avif")));
    }

    [WindowsFileSharingFact]
    public async Task Locked_staging_preserves_original_failure_and_records_cleanup_sharing_violation()
    {
        FileStream? locked = null;
        var original = new InvalidDataException("AVIF verification failed");
        try
        {
            var actual = await Record.ExceptionAsync(() => new OutputWriter().WriteFileAsync(Path.Combine(root, "photo.avif"), (path, _) =>
            {
                locked = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                return Task.FromException(original);
            }, default));
            Assert.Same(original, actual);
            var cleanup = Assert.IsType<IOException>(actual.Data["Citrus.OutputWriter.CleanupFailure"]);
            Assert.Equal(32, cleanup.HResult & 0xffff); // ERROR_SHARING_VIOLATION
            Assert.False(File.Exists(Path.Combine(root, "photo.avif")));
        }
        finally { locked?.Dispose(); }
    }

    private sealed class WindowsFileSharingFactAttribute : FactAttribute
    {
        public WindowsFileSharingFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Requires native Windows delete-sharing enforcement.";
        }
    }

    [Fact]
    public async Task Cancellation_after_callback_returns_still_prevents_commit()
    {
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OutputWriter().WriteFileAsync(Path.Combine(root, "photo.png"), async (path, token) =>
        {
            await File.WriteAllTextAsync(path, "finished", token);
            cts.Cancel();
        }, cts.Token));
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public async Task Missing_output_is_an_error_without_a_final_file()
    {
        await Assert.ThrowsAnyAsync<IOException>(() => new OutputWriter().WriteFileAsync(Path.Combine(root, "photo.png"), (_, _) => Task.CompletedTask, default));
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public async Task Unwritable_destination_reports_failure()
    {
        var parentFile = Path.Combine(root, "not-a-directory");
        await File.WriteAllTextAsync(parentFile, "keep");
        await Assert.ThrowsAnyAsync<IOException>(() => new OutputWriter().WriteFileAsync(Path.Combine(parentFile, "photo.png"), (path, token) => File.WriteAllTextAsync(path, "new", token), default));
        Assert.Equal("keep", await File.ReadAllTextAsync(parentFile));
        Assert.Single(Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public async Task Read_only_directory_reports_permission_failure_on_unix()
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => new OutputWriter().WriteFileAsync(Path.Combine(root, "photo.png"), (path, token) => File.WriteAllTextAsync(path, "new", token), default));
        }
        finally { File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public async Task Directory_commit_preserves_existing_directory()
    {
        var desired = Path.Combine(root, "archive");
        Directory.CreateDirectory(desired);
        await File.WriteAllTextAsync(Path.Combine(desired, "original"), "keep");
        var writer = new OutputWriter();
        var outputs = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => writer.WriteDirectoryAsync(desired, (path, token) => File.WriteAllTextAsync(Path.Combine(path, "entry"), "new", token), default)));
        Assert.Equal(2, outputs.Distinct().Count());
        Assert.All(outputs, path => Assert.Equal("new", File.ReadAllText(Path.Combine(path, "entry"))));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(desired, "original")));
        Assert.Equal(3, Directory.GetFileSystemEntries(root).Length);
    }

    [Fact]
    public async Task File_commit_preserves_existing_directory_at_desired_path()
    {
        var desired = Path.Combine(root, "photo.png");
        Directory.CreateDirectory(desired);
        var sentinel = Path.Combine(desired, "original");
        await File.WriteAllTextAsync(sentinel, "keep");
        var output = await new OutputWriter().WriteFileAsync(desired, async (temporary, token) =>
        {
            Assert.Equal(".png", Path.GetExtension(temporary));
            await File.WriteAllBytesAsync(temporary, [1, 2, 3, 255], token);
        }, default);
        Assert.NotEqual(desired, output);
        Assert.True(File.Exists(output));
        Assert.False(Directory.Exists(output));
        Assert.Equal(".png", Path.GetExtension(output));
        Assert.Equal(root, Path.GetDirectoryName(output));
        Assert.Equal(new byte[] { 1, 2, 3, 255 }, await File.ReadAllBytesAsync(output));
        Assert.True(Directory.Exists(desired));
        Assert.False(File.Exists(desired));
        Assert.Equal("keep", await File.ReadAllTextAsync(sentinel));
        Assert.Equal(new[] { sentinel }, Directory.GetFileSystemEntries(desired));
        Assert.Equal(new[] { desired, output }.OrderBy(path => path), Directory.GetFileSystemEntries(root).OrderBy(path => path));
        Assert.DoesNotContain(Directory.GetFileSystemEntries(root), path => Path.GetFileName(path).StartsWith(".citrus-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Directory_commit_preserves_existing_file_at_desired_path()
    {
        var desired = Path.Combine(root, "archive");
        var sentinel = new byte[] { 9, 8, 7 };
        await File.WriteAllBytesAsync(desired, sentinel);
        var output = await new OutputWriter().WriteDirectoryAsync(desired, async (temporary, token) =>
        {
            var nested = Path.Combine(temporary, "nested");
            Directory.CreateDirectory(nested);
            await File.WriteAllTextAsync(Path.Combine(nested, "entry.txt"), "complete", token);
        }, default);
        Assert.NotEqual(desired, output);
        Assert.True(Directory.Exists(output));
        Assert.False(File.Exists(output));
        Assert.Equal(root, Path.GetDirectoryName(output));
        var nestedOutput = Path.Combine(output, "nested");
        var entry = Path.Combine(nestedOutput, "entry.txt");
        Assert.Equal("complete", await File.ReadAllTextAsync(entry));
        Assert.Equal(new[] { nestedOutput }, Directory.GetFileSystemEntries(output));
        Assert.Equal(new[] { entry }, Directory.GetFileSystemEntries(nestedOutput));
        Assert.True(File.Exists(desired));
        Assert.False(Directory.Exists(desired));
        Assert.Equal(sentinel, await File.ReadAllBytesAsync(desired));
        Assert.Equal(new[] { desired, output }.OrderBy(path => path), Directory.GetFileSystemEntries(root).OrderBy(path => path));
        Assert.DoesNotContain(Directory.GetFileSystemEntries(root), path => Path.GetFileName(path).StartsWith(".citrus-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_canceled_directory_write_removes_whole_staging_tree(bool cancel)
    {
        using var cts = new CancellationTokenSource();
        Task<string> Run() => new OutputWriter().WriteDirectoryAsync(Path.Combine(root, "archive"), async (path, token) =>
        {
            Directory.CreateDirectory(Path.Combine(path, "nested"));
            await File.WriteAllTextAsync(Path.Combine(path, "nested", "entry"), "partial", token);
            if (cancel) { cts.Cancel(); return; }
            throw new IOException("fixture failure");
        }, cts.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Run);
        else await Assert.ThrowsAsync<IOException>(Run);
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }
}
