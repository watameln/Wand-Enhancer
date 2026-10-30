using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using WandEnhancer.Utils;

namespace WandEnhancer.Core.Services
{
    /// <summary>
    /// Watches a Squirrel install root for a new version folder and re-applies the saved patch
    /// the moment one appears and finishes writing. Exists because the deployed-launcher
    /// redirection (<see cref="Patching.Shared.LauncherDeployment"/>) cannot catch an update on
    /// its own: Squirrel rewrites the root execution stub as part of applying the update itself,
    /// before the user's next launch - by the time anything of ours would run again through
    /// either strategy's own launcher interception, that rewrite has already erased it.
    /// </summary>
    internal static class UpdateWatcherService
    {
        private const int SettleIntervalMs = 2000;
        private const int SettleStableReads = 3;
        private const int SettleTimeoutMs = 10 * 60 * 1000;
        private static readonly TimeSpan SelfCheckInterval = TimeSpan.FromMinutes(1);
        private static int _checking;
        private static string _deferredVersion;
        private static string _failedVersion;
        private static DateTime _retryAfterUtc;

        public static string MutexName(string squirrelRoot) => "Global\\WandEnhancer.Watcher." + HashOf(squirrelRoot);

        private static string StopEventName(string squirrelRoot) => "Global\\WandEnhancer.WatcherStop." + HashOf(squirrelRoot);

        /// <summary>
        /// Blocks until told to stop via <see cref="Stop"/>. Exits immediately, without touching
        /// anything, if another instance already owns this install root.
        /// </summary>
        public static void Run(string squirrelRoot, Action<string, ELogType> log)
        {
            bool owned;
            using (var mutex = new Mutex(true, MutexName(squirrelRoot), out owned))
            {
                if (!owned)
                {
                    log?.Invoke("[WATCHER] Another watcher already owns this install; exiting.", ELogType.Info);
                    return;
                }

                try
                {
                    RunOwned(squirrelRoot, log);
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            }
        }

        /// <summary>Wakes a running watcher for this root, or primes a pending stop if none is running yet.</summary>
        public static void Stop(string squirrelRoot)
        {
            using (var stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName(squirrelRoot)))
            {
                stop.Set();
            }
        }

        private static void RunOwned(string squirrelRoot, Action<string, ELogType> log)
        {
            using (var stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName(squirrelRoot)))
            using (var watcher = new FileSystemWatcher(squirrelRoot)
            {
                NotifyFilter = NotifyFilters.DirectoryName,
                IncludeSubdirectories = false
            })
            {
                watcher.Created += (sender, e) => OnEntryCreated(e, squirrelRoot, log);
                watcher.Renamed += (sender, e) => OnEntryCreated(e, squirrelRoot, log);
                watcher.Error += (sender, e) =>
                {
                    log?.Invoke($"[WATCHER] File watcher lost events: {e.GetException()?.Message}. Checking installed versions again.", ELogType.Warn);
                    QueueCheck(null, squirrelRoot, log);
                };
                watcher.EnableRaisingEvents = true;
                log?.Invoke($"[WATCHER] Watching {squirrelRoot} for new Wand versions.", ELogType.Info);

                // Catch updates installed while Windows was shut down, before the Run key
                // started us, or while FileSystemWatcher wasn't listening.
                QueueCheck(null, squirrelRoot, log);

                while (!stop.WaitOne(SelfCheckInterval))
                {
                    if (!IsStillWanted(squirrelRoot))
                    {
                        log?.Invoke("[WATCHER] Auto-apply is no longer configured; exiting.", ELogType.Info);
                        return;
                    }

                    // Also handles missed/overflowed notifications and an update that was
                    // only half installed when the directory-created event first arrived.
                    QueueCheck(null, squirrelRoot, log);
                }

                log?.Invoke("[WATCHER] Stop requested; exiting.", ELogType.Info);
            }
        }

        private static void OnEntryCreated(FileSystemEventArgs e, string squirrelRoot, Action<string, ELogType> log)
        {
            if (!Directory.Exists(e.FullPath) ||
                !Path.GetFileName(e.FullPath).StartsWith("app-", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            QueueCheck(e.FullPath, squirrelRoot, log);
        }

        private static void QueueCheck(string versionRoot, string squirrelRoot, Action<string, ELogType> log)
        {
            // Multiple Squirrel events can describe one install. Never patch two versions
            // concurrently; a periodic reconciliation will retry if an event arrived busy.
            if (Interlocked.CompareExchange(ref _checking, 1, 0) != 0)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (!IsStillWanted(squirrelRoot))
                    {
                        return;
                    }

                    if (versionRoot == null)
                    {
                        var latest = WeModInstalls.FindLatestWeMod(squirrelRoot);
                        if (latest == null || Enhancer.IsPatched(latest.RootDirectory))
                        {
                            return;
                        }

                        versionRoot = latest.RootDirectory;
                    }

                    TryPatchNewVersion(versionRoot, squirrelRoot, log);
                }
                catch (Exception e)
                {
                    log?.Invoke($"[WATCHER] Could not check installed versions: {e.Message}", ELogType.Warn);
                }
                finally
                {
                    Interlocked.Exchange(ref _checking, 0);
                }
            });
        }

        private static void TryPatchNewVersion(string versionRoot, string squirrelRoot, Action<string, ELogType> log)
        {
            if (!IsStillWanted(squirrelRoot))
            {
                return;
            }

            if (_failedVersion != null && PathsEqual(_failedVersion, versionRoot) &&
                DateTime.UtcNow < _retryAfterUtc)
            {
                return;
            }

            if (Enhancer.IsPatched(versionRoot))
            {
                return;
            }

            log?.Invoke($"[WATCHER] Unpatched version found: {versionRoot}", ELogType.Info);

            if (!WaitForSettled(versionRoot))
            {
                log?.Invoke($"[WATCHER] Gave up waiting for {versionRoot} to finish installing.", ELogType.Warn);
                return;
            }

            if (!IsStillWanted(squirrelRoot))
            {
                log?.Invoke("[WATCHER] Auto-apply was turned off while waiting; skipping.", ELogType.Info);
                return;
            }

            var config = WeModInstalls.FindLatestWeMod(squirrelRoot);
            if (config == null || !PathsEqual(config.RootDirectory, versionRoot))
            {
                log?.Invoke($"[WATCHER] {versionRoot} is not (or no longer) the latest install; skipping.", ELogType.Info);
                return;
            }

            if (Enhancer.IsPatched(config.RootDirectory))
            {
                log?.Invoke($"[WATCHER] {versionRoot} is already patched; nothing to do.", ELogType.Info);
                return;
            }

            var patchConfig = Enhancer.LoadAutoPatchConfig(squirrelRoot);
            if (patchConfig == null || !patchConfig.AutoApplyAfterUpdate)
            {
                return;
            }

            // Enhancer.Patch kills Wand processes before editing the archive. Never
            // interrupt an active game or session; the next check after exit will retry.
            if (IsWandRunning())
            {
                if (_deferredVersion == null || !PathsEqual(_deferredVersion, versionRoot))
                {
                    log?.Invoke($"[WATCHER] Wand is running; deferring patch of {versionRoot} until it exits.", ELogType.Info);
                    _deferredVersion = versionRoot;
                }

                return;
            }

            _deferredVersion = null;

            try
            {
                log?.Invoke($"[WATCHER] Re-applying patches to {config.ExecutablePath}...", ELogType.Info);
                new Enhancer(config, log, patchConfig).Patch();
                _failedVersion = null;
                log?.Invoke("[WATCHER] Auto-patch after update succeeded.", ELogType.Success);
            }
            catch (Exception e)
            {
                _failedVersion = versionRoot;
                _retryAfterUtc = DateTime.UtcNow.AddMinutes(10);
                log?.Invoke($"[WATCHER] Auto-patch after update failed: {e.Message}. Will retry later.", ELogType.Error);
            }
        }

        private static bool IsWandRunning()
        {
            foreach (var name in Constants.WeModBrandNames)
            {
                Process[] processes = Process.GetProcessesByName(name);
                try
                {
                    if (processes.Length != 0)
                    {
                        return true;
                    }
                }
                finally
                {
                    foreach (var process in processes)
                    {
                        process.Dispose();
                    }
                }
            }

            return false;
        }

        /// <summary>Waits for app.asar's size to stop changing, since Squirrel writes the new version in place.</summary>
        private static bool WaitForSettled(string versionRoot)
        {
            string asarPath = Path.Combine(versionRoot, "resources", "app.asar");
            long lastSize = -1;
            int stableReads = 0;
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(SettleTimeoutMs);

            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(SettleIntervalMs);

                if (!File.Exists(asarPath))
                {
                    stableReads = 0;
                    continue;
                }

                long size;
                try
                {
                    size = new FileInfo(asarPath).Length;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    // Still being written to; try again next tick.
                    stableReads = 0;
                    continue;
                }

                if (size > 0 && size == lastSize)
                {
                    if (++stableReads >= SettleStableReads)
                    {
                        return true;
                    }
                }
                else
                {
                    stableReads = 0;
                }

                lastSize = size;
            }

            return false;
        }

        private static bool IsStillWanted(string squirrelRoot)
        {
            var config = Enhancer.LoadAutoPatchConfig(squirrelRoot);
            return config != null && config.AutoApplyAfterUpdate;
        }

        private static bool PathsEqual(string a, string b)
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd('\\'),
                Path.GetFullPath(b).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string HashOf(string value)
        {
            using (var md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
                var builder = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                {
                    builder.Append(b.ToString("x2"));
                }

                return builder.ToString();
            }
        }
    }
}
