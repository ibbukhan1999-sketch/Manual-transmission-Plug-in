using System.Collections.Concurrent;
using System.Text;
using ETS2LA.Logging;

namespace Godspeed.Diagnostics;

internal sealed class GodspeedSessionLogger : IDisposable
{
    private const int MaximumQueuedEntries = 8_192;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(500);

    private readonly ConcurrentQueue<LogEntry> queue = new();
    private readonly ConcurrentBag<LogEntry> entryPool = new();
    private readonly AutoResetEvent queueSignal = new(false);
    private readonly Thread? writerThread;
    private readonly string component;
    private readonly string filePath = string.Empty;
    private int queuedEntries;
    private int droppedEntries;
    private int stopping;
    private int activeWriters;

    private GodspeedSessionLogger(string fileStem, string component)
    {
        this.component = component;
        try
        {
            // Resolve a persistent directory outside ETS2LA's plugin shadow copies.
            string myDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string logDirectory = Path.Combine(myDocuments, "GitHub", "GS AI", ".session_logs");
            if (!Directory.Exists(logDirectory))
                Directory.CreateDirectory(logDirectory);

            string sessionStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            string fileName = $"{fileStem}_{sessionStamp}_{Environment.ProcessId}_session.log";
            filePath = Path.Combine(logDirectory, fileName);

            Thread worker = new(WriterLoop)
            {
                IsBackground = true,
                Name = $"Godspeed.{component}.LogWriter",
                Priority = ThreadPriority.BelowNormal
            };
            worker.Start();
            writerThread = worker;
        }
        catch (Exception)
        {
            Volatile.Write(ref stopping, 1);
            queueSignal.Dispose();
            ReportLoggerUnavailable();
            return;
        }

        try { Info("Lifecycle", "Session logger started", $"path={filePath}"); }
        catch (Exception) { ReportLoggerUnavailable(); }
    }

    public string FilePath => filePath;

    public static GodspeedSessionLogger Create(string fileStem, string component)
        => new(fileStem, component);

    public void Trace(string source, string message, string? telemetrySnapshot = null, string? hostMessage = null)
        => Write("TRACE", source, message, telemetrySnapshot, hostMessage);

    public void Info(string source, string message, string? telemetrySnapshot = null, string? hostMessage = null)
        => Write("INFO", source, message, telemetrySnapshot, hostMessage);

    public void Warn(string source, string message, string? telemetrySnapshot = null, string? hostMessage = null)
        => Write("WARN", source, message, telemetrySnapshot, hostMessage);

    public void Error(string source, string message, string? telemetrySnapshot = null, string? hostMessage = null)
        => Write("ERROR", source, message, telemetrySnapshot, hostMessage);

    public void Write(string level, string source, string message, string? telemetrySnapshot = null,
        string? hostMessage = null)
    {
        if (Volatile.Read(ref stopping) != 0)
            return;
        Interlocked.Increment(ref activeWriters);
        try
        {
            if (Volatile.Read(ref stopping) != 0)
                return;

            if (Interlocked.Increment(ref queuedEntries) > MaximumQueuedEntries)
            {
                Interlocked.Decrement(ref queuedEntries);
                Interlocked.Increment(ref droppedEntries);
                return;
            }

            if (!entryPool.TryTake(out LogEntry? entry))
                entry = new LogEntry();
            entry.Timestamp = DateTime.Now;
            entry.Level = level;
            entry.Source = source;
            entry.Message = message;
            entry.Snapshot = telemetrySnapshot;
            entry.HostMessage = hostMessage;
            queue.Enqueue(entry);
        }
        finally { Interlocked.Decrement(ref activeWriters); }
    }

    public void Dispose()
    {
        // A completed join proves the writer flushed and disposed the stream.
        // Keep the lifecycle wait bounded; a stalled disk cannot be forced to
        // release a file handle within a hard deadline without risking data.
        Interlocked.Exchange(ref stopping, 1);
        try { queueSignal.Set(); }
        catch (ObjectDisposedException) { }
        if (writerThread is not null && Thread.CurrentThread != writerThread
            && !writerThread.Join(TimeSpan.FromMilliseconds(500)))
        {
            try { Logger.Warn("Godspeed session logger did not close within 500 ms; its writer is still draining."); }
            catch { }
        }
    }

    private void WriterLoop()
    {
        StreamWriter? writer = null;
        try
        {
            writer = OpenWriter(filePath);
            if (writer is null)
            {
                ReportLoggerUnavailable();
                return;
            }

            while (Volatile.Read(ref stopping) == 0)
            {
                queueSignal.WaitOne(FlushInterval);
                try { DrainQueue(writer); writer.Flush(); }
                catch (IOException)
                {
                    ReportLoggerUnavailable();
                    return;
                }
            }

            // A producer that entered Write immediately before Dispose gets a
            // bounded chance to finish enqueuing before the final disk drain.
            SpinWait.SpinUntil(() => Volatile.Read(ref activeWriters) == 0,
                TimeSpan.FromMilliseconds(250));
            try { DrainQueue(writer); writer.Flush(); }
            catch (IOException) { ReportLoggerUnavailable(); }
        }
        catch (Exception)
        {
            ReportLoggerUnavailable();
        }
        finally
        {
            try { writer?.Dispose(); }
            catch { }
            Volatile.Write(ref stopping, 1);
            queueSignal.Dispose();
            while (queue.TryDequeue(out _)) { }
        }
    }

    private static StreamWriter? OpenWriter(string targetPath)
    {
        try
        {
            string? targetDirectory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(targetDirectory))
                Directory.CreateDirectory(targetDirectory);
            return new StreamWriter(targetPath, true, new UTF8Encoding(false), 64 * 1024);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void DrainQueue(StreamWriter writer)
    {
        int dropped = Interlocked.Exchange(ref droppedEntries, 0);
        if (dropped > 0)
            writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [WARN] [{component}.Logger] Dropped {dropped} queued log entries | queue_limit={MaximumQueuedEntries}");

        while (queue.TryPeek(out LogEntry? entry))
        {
            writer.WriteLine($"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{entry.Level}] "
                + $"[{component}.{entry.Source}] {Sanitize(entry.Message)} | "
                + (string.IsNullOrWhiteSpace(entry.Snapshot) ? "-" : Sanitize(entry.Snapshot)));
            if (!queue.TryDequeue(out entry))
                continue;
            Interlocked.Decrement(ref queuedEntries);
            if (entry.HostMessage is null)
            {
                Recycle(entry);
                continue;
            }
            try
            {
                switch (entry.Level)
                {
                    case "ERROR": Logger.Error(entry.HostMessage); break;
                    case "WARN": Logger.Warn(entry.HostMessage); break;
                    default: Logger.Info(entry.HostMessage); break;
                }
            }
            catch
            {
                // Host diagnostics must not terminate the file writer.
            }
            Recycle(entry);
        }
    }

    private void Recycle(LogEntry entry)
    {
        entry.Timestamp = default;
        entry.Level = string.Empty;
        entry.Source = string.Empty;
        entry.Message = string.Empty;
        entry.Snapshot = null;
        entry.HostMessage = null;
        if (entryPool.Count < MaximumQueuedEntries)
            entryPool.Add(entry);
    }

    private sealed class LogEntry
    {
        public DateTime Timestamp;
        public string Level = string.Empty;
        public string Source = string.Empty;
        public string Message = string.Empty;
        public string? Snapshot;
        public string? HostMessage;
    }

    private static void ReportLoggerUnavailable()
    {
        try { Logger.Error("Godspeed session logging is unavailable; gameplay will continue without a file log."); }
        catch { /* Diagnostics must not stop a control plugin. */ }
    }

    private static string Sanitize(string value)
        => value.Replace('\r', ' ').Replace('\n', ' ').Replace('|', '/');
}
