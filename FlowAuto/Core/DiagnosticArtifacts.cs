using System.Collections.Concurrent;
using System.Drawing.Imaging;
using System.Threading.Channels;

namespace FlowAuto.Core;

/// <summary>
/// Persists failure diagnostics without making the flow worker wait for PNG
/// encoding or disk I/O. The queue is deliberately small: normal execution
/// must take priority over retaining every repeated failure frame.
/// </summary>
public static class DiagnosticArtifacts
{
    private const int MaxQueuedWrites = 4;
    public const int MaxFilesPerDirectory = 40;
    public const long MaxBytesPerDirectory = 100L * 1024 * 1024;
    private const int MaxRecentFailures = 20;

    private static readonly Channel<WriteRequest> PendingWrites =
        Channel.CreateBounded<WriteRequest>(new BoundedChannelOptions(MaxQueuedWrites)
        {
            // TryWrite returns false when full, allowing QueuePng to dispose
            // the clone immediately. DropWrite would silently discard the
            // channel item while leaking its owned Bitmap.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

    private static readonly ConcurrentQueue<DiagnosticWriteFailure> RecentFailures = new();
    private static readonly object IdleLock = new();
    private static TaskCompletionSource<bool> _idleCompletion = Completed();
    private static int _outstandingWrites;
    private static int _writerStarted;
    private static int _stopping;

    static DiagnosticArtifacts()
    {
        // A process can exit while an encoder is still writing. Stop accepting
        // new captures; the worker disposes queued bitmap clones as it drains.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
    }

    /// <summary>
    /// Recent asynchronous diagnostic write failures. This makes errors
    /// inspectable instead of silently swallowing them on the flow thread.
    /// </summary>
    public static IReadOnlyList<DiagnosticWriteFailure> GetRecentFailures() =>
        RecentFailures.ToArray();

    /// <summary>
    /// Records a failure that occurred while preparing a diagnostic artifact.
    /// The original flow failure remains authoritative, while the secondary
    /// diagnostic problem is retained for troubleshooting.
    /// </summary>
    public static void ReportPreparationFailure(string filePath, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(exception);
        RecordFailure(filePath, exception);
    }

    /// <summary>
    /// Creates a collision-resistant path below the application's diagnostic
    /// area. Category and node names are sanitized so a display name cannot
    /// accidentally produce an invalid path.
    /// </summary>
    public static string CreatePngPath(string category, string? nodeName)
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            SanitizePathSegment(category, "debug"));
        var safeNodeName = SanitizePathSegment(nodeName, "node");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return Path.Combine(directory,
            $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{safeNodeName}_{suffix}.png");
    }

    /// <summary>
    /// Quickly copies a bitmap into a bounded queue for asynchronous PNG
    /// persistence. Returns false when diagnostics are shutting down or the
    /// queue is full; the caller can then report that the artifact was skipped.
    /// </summary>
    public static bool QueuePng(Bitmap bitmap, string filePath)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (Volatile.Read(ref _stopping) != 0)
            return false;

        Bitmap snapshot;
        try
        {
            // The source bitmap generally belongs to a using scope in the
            // executor. Clone before returning so the background worker never
            // observes a disposed GDI+ image.
            snapshot = (Bitmap)bitmap.Clone();
        }
        catch (Exception exception)
        {
            RecordFailure(filePath, exception);
            return false;
        }

        BeginOutstandingWrite();
        if (!PendingWrites.Writer.TryWrite(new WriteRequest(snapshot, filePath)))
        {
            snapshot.Dispose();
            CompleteOutstandingWrite();
            RecordFailure(filePath, new InvalidOperationException(
                "Diagnostic PNG queue is full or is shutting down."));
            return false;
        }

        EnsureWriterStarted();
        return true;
    }

    /// <summary>
    /// Allows tests and orderly shutdown code to wait until the accepted
    /// diagnostic work has been written or discarded.
    /// </summary>
    public static Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        Task idleTask;
        lock (IdleLock)
            idleTask = _idleCompletion.Task;
        return idleTask.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Removes old PNG diagnostics while preserving the most recently written
    /// file whenever possible. It is public to allow a support tool to clean an
    /// existing diagnostic folder without starting the application.
    /// </summary>
    public static void PruneDirectory(string directory, int maxFiles = MaxFilesPerDirectory,
        long maxBytes = MaxBytesPerDirectory, string? preservePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (maxFiles < 1) throw new ArgumentOutOfRangeException(nameof(maxFiles));
        if (maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (!Directory.Exists(directory)) return;

        try
        {
            var files = new DirectoryInfo(directory)
                .EnumerateFiles("*.png", SearchOption.TopDirectoryOnly)
                .OrderBy(file => file.LastWriteTimeUtc)
                .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var totalBytes = files.Sum(file => file.Length);
            var preservedFullPath = string.IsNullOrEmpty(preservePath)
                ? null
                : Path.GetFullPath(preservePath);

            var remainingCount = files.Count;
            foreach (var file in files)
            {
                if (remainingCount <= maxFiles && totalBytes <= maxBytes)
                    break;

                if (preservedFullPath != null && string.Equals(
                        file.FullName, preservedFullPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var fileLength = file.Length;
                    file.Delete();
                    totalBytes -= fileLength;
                    remainingCount--;
                }
                catch (Exception exception)
                {
                    RecordFailure(file.FullName, exception);
                }
            }
        }
        catch (Exception exception)
        {
            RecordFailure(directory, exception);
        }
    }

    /// <summary>
    /// Stops accepting work. Existing queued bitmap snapshots are disposed
    /// promptly; a write already in progress is allowed to finish safely.
    /// </summary>
    public static void Stop()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
            return;

        PendingWrites.Writer.TryComplete();
        EnsureWriterStarted();
    }

    private static void EnsureWriterStarted()
    {
        if (Interlocked.CompareExchange(ref _writerStarted, 1, 0) == 0)
            _ = Task.Run(ProcessPendingWritesAsync);
    }

    private static async Task ProcessPendingWritesAsync()
    {
        try
        {
            await foreach (var request in PendingWrites.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    if (Volatile.Read(ref _stopping) == 0)
                        SavePng(request);
                }
                catch (Exception exception)
                {
                    RecordFailure(request.FilePath, exception);
                }
                finally
                {
                    request.Bitmap.Dispose();
                    CompleteOutstandingWrite();
                }
            }
        }
        catch (Exception exception)
        {
            RecordFailure("diagnostic queue", exception);
        }
        finally
        {
            while (PendingWrites.Reader.TryRead(out var request))
            {
                request.Bitmap.Dispose();
                CompleteOutstandingWrite();
            }
        }
    }

    private static void SavePng(WriteRequest request)
    {
        var directory = Path.GetDirectoryName(request.FilePath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("A diagnostic PNG path must include a directory.", nameof(request));

        Directory.CreateDirectory(directory);
        var temporaryPath = request.FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            request.Bitmap.Save(temporaryPath, ImageFormat.Png);
            File.Move(temporaryPath, request.FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }

        PruneDirectory(directory, preservePath: request.FilePath);
    }

    private static void BeginOutstandingWrite()
    {
        lock (IdleLock)
        {
            if (_outstandingWrites++ == 0)
                _idleCompletion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private static void CompleteOutstandingWrite()
    {
        lock (IdleLock)
        {
            if (_outstandingWrites <= 0)
                return;

            if (--_outstandingWrites == 0)
                _idleCompletion.TrySetResult(true);
        }
    }

    private static TaskCompletionSource<bool> Completed()
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult(true);
        return completion;
    }

    private static void RecordFailure(string path, Exception exception)
    {
        var failure = new DiagnosticWriteFailure(DateTimeOffset.Now, path, exception);
        RecentFailures.Enqueue(failure);
        while (RecentFailures.Count > MaxRecentFailures && RecentFailures.TryDequeue(out _))
        {
        }

    }

    private static string SanitizePathSegment(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(value
            .Select(character => invalidChars.Contains(character) ? '_' : character)
            .ToArray())
            .Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(sanitized))
            return fallback;

        return sanitized.Length <= 60 ? sanitized : sanitized[..60];
    }

    private readonly record struct WriteRequest(Bitmap Bitmap, string FilePath);
}

public readonly record struct DiagnosticWriteFailure(
    DateTimeOffset Timestamp,
    string FilePath,
    Exception Exception)
{
    public string Message => Exception.Message;
}
