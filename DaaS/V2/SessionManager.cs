﻿// -----------------------------------------------------------------------
// <copyright file="SessionManager.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DaaS.V2
{

    public class SessionManager : ISessionManager
    {
        private static IOperationLock _sessionLockFile;
        private readonly List<string> _allSessionsDirs = new List<string>()
        {
            SessionDirectories.ActiveSessionsDir,
            SessionDirectories.CompletedSessionsDir
        };

        public SessionManager()
        {
            EnsureSessionDirectories();
        }

        #region ISessionManager methods

        public async Task<Session> GetActiveSessionAsync(bool isDetailed = false)
        {
            var activeSessions = await LoadSessionsAsync(SessionDirectories.ActiveSessionsDir, isDetailed);
            var activeSession = activeSessions.FirstOrDefault();
            return activeSession;
        }

        public async Task<IEnumerable<Session>> GetAllSessionsAsync(bool isDetailed = false)
        {
            return (await LoadSessionsAsync(_allSessionsDirs, isDetailed)).OrderByDescending(x => x.StartTime);
        }

        public async Task<IEnumerable<Session>> GetCompletedSessionsAsync()
        {
            return (await LoadSessionsAsync(SessionDirectories.CompletedSessionsDir)).OrderByDescending(x => x.StartTime);
        }

        public async Task<Session> GetSessionAsync(string sessionId, bool isDetailed = false)
        {
            return (await LoadSessionsAsync(_allSessionsDirs, isDetailed))
                .Where(x => x.SessionId == sessionId).FirstOrDefault();
        }

        public async Task<string> SubmitNewSessionAsync(Session session, bool invokedViaDaasConsole = false)
        {
            var computeMode = Environment.GetEnvironmentVariable("WEBSITE_COMPUTE_MODE");
            if (computeMode != null && !computeMode.Equals("Dedicated", StringComparison.OrdinalIgnoreCase))
            {
                throw new AccessViolationException("DaaS is only supported on websites running in Dedicated SKU");
            }

            var activeSession = await GetActiveSessionAsync();
            if (activeSession != null)
            {
                throw new AccessViolationException($"There is an already an existing active session for {activeSession.Tool}");
            }

            await ThrowIfSessionInvalid(session);

            await SaveSessionAsync(session, invokedViaDaasConsole);
            return session.SessionId;
        }

        private async Task ThrowIfSessionInvalid(Session session)
        {
            if (session.Instances == null || !session.Instances.Any())
            {
                throw new ArgumentException("At least one instance must be specified");
            }

            if (string.IsNullOrWhiteSpace(session.Tool))
            {
                throw new ArgumentException("Please specify a valid diagnostic tool to run");
            }

            var diagnoser = GetDiagnoserForSession(session);
            if (diagnoser == null)
            {
                throw new ArgumentException($"Invalid diagnostic tool '{session.Tool}' ");
            }

            if (diagnoser.RequiresStorageAccount && string.IsNullOrWhiteSpace(Settings.Instance.BlobSasUri))
            {
                throw new ArgumentException($"The tool '{session.Tool}' requires that WEBSITE_DAAS_STORAGE_SASURI setting must be specified");
            }

            if (InvokedViaAutomation)
            {
                var maxSessionsPerDay = Infrastructure.Settings.MaxSessionsPerDay;

                var completedSessions = await GetCompletedSessionsAsync();
                var completedSessionsLastDay = completedSessions.Where(x => x.StartTime > DateTime.UtcNow.AddDays(-1)).Count();

                if (completedSessionsLastDay >= maxSessionsPerDay)
                {
                    throw new AccessViolationException($"The limit of maximum number of DaaS sessions ({maxSessionsPerDay} per day) has been reached. Either disable"
                        + "the autohealing rule, delete existing sessions or increase MaxSessionsPerDay setting in %home%\\data\\daas\\PrivateSettings.json file."
                        + "(Changing settings, requires a restart of the Kudu Site)");
                }

                var sessionThresholdPeriodInMinutes = Infrastructure.Settings.MaxSessionCountThresholdPeriodInMinutes;
                var maxSessionCountInThresholdPeriod = Infrastructure.Settings.MaxSessionCountInThresholdPeriod;

                var sessionsInThresholdPeriod = completedSessions.Where(x => x.StartTime > DateTime.UtcNow.AddMinutes(-1 * sessionThresholdPeriodInMinutes)).Count();
                if (sessionsInThresholdPeriod >= maxSessionCountInThresholdPeriod)
                {
                    throw new AccessViolationException($"To avoid impact to application and disk space, a new DaaS session request is rejected as a total of "
                        + $"{maxSessionCountInThresholdPeriod} DaaS sessions were submitted in the last {sessionThresholdPeriodInMinutes} minutes. Either "
                        + $"disable the autohealing rule, delete existing sessions or increase MaxSessionCountInThresholdPeriod,"
                        + $"MaxSessionCountThresholdPeriodInMinutes setting in %home%\\data\\daas\\PrivateSettings.json "
                        + $"file. (Changing settings, requires a restart of the Kudu Site)");
                }
                else
                {
                    Logger.LogVerboseEvent($"MaxSessionCountThresholdPeriodInMinutes is {sessionThresholdPeriodInMinutes} and sessionsInThresholdPeriod is {sessionsInThresholdPeriod} so allowing DaaSConsole to submit a new session");
                }
            }
        }

        public async Task<bool> HasThisInstanceCollectedLogs()
        {
            var activeSession = await GetActiveSessionAsync();
            return activeSession.ActiveInstances != null
                && activeSession.ActiveInstances.Any(x => x.Name.Equals(Infrastructure.GetInstanceId(),
                StringComparison.OrdinalIgnoreCase) && x.Status == Status.Complete);
        }

        public async Task RunToolForSessionAsync(Session activeSession, CancellationToken token)
        {
            try
            {
                var activeInstance = activeSession.GetCurrentInstance();
                
                //
                // It's possible that DaasRunner restarts after the data is already collected. In that
                // situation, the current Instance status would be set to Analyzing, TimedOut or Complete
                // Just make sure that we are not past that stage for the current session.
                //
                
                if (activeInstance== null || activeInstance.Status == Status.Active || activeInstance.Status == Status.Started)
                {
                    DiagnosticToolResponse resp = null;
                    Collector collector = GetCollectorForSession(activeSession.Tool);
                    await SetCurrentInstanceAsStartedAsync(activeSession);

                    try
                    {
                        resp = await collector.CollectLogsAsync(
                            activeSession,
                            token);
                    }
                    catch (Exception ex)
                    {
                        resp = new DiagnosticToolResponse();
                        resp.Errors.Add($"Invoking diagnostic tool failed with error - {ex.Message}");
                        Logger.LogSessionErrorEvent("Tool invocation failed", ex, activeSession.SessionId);
                    }

                    //
                    // Add the tool output to the active session
                    //
                    await AppendCollectorResponseToSessionAsync(activeSession, resp);
                }

                await AnalyzeAndUpdateSessionAsync(token);

                //
                // Mark current instance as Complete
                //
                await SetCurrentInstanceAsCompleteAsync(activeSession);

                //
                // Check if all the instances have finished running the session
                // and set the Session State to Complete
                //
                await CheckandCompleteSessionIfNeededAsync();
            }
            catch (Exception ex)
            {
                Logger.LogSessionErrorEvent("Exception while running tool", ex, activeSession.SessionId);
            }
        }

        public async Task<bool> CheckandCompleteSessionIfNeededAsync(bool forceCompletion = false)
        {
            var activeSession = await GetActiveSessionAsync();
            if (AllInstancesFinished(activeSession) || forceCompletion)
            {
                Logger.LogSessionVerboseEvent("All instances have status as Complete", activeSession.SessionId);
                await MarkSessionAsCompleteAsync(activeSession, forceCompletion: forceCompletion);
                return true;
            }

            return false;
        }

        public bool ShouldCollectOnCurrentInstance(Session activeSession)
        {
            return activeSession.Instances != null &&
                activeSession.Instances.Any(x => x.Equals(Infrastructure.GetInstanceId(), StringComparison.OrdinalIgnoreCase));
        }

        public async Task DeleteSessionAsync(string sessionId)
        {
            EnsureSessionDirectories();

            await Task.Run(async () =>
            {
                var sessionFile = Path.Combine(SessionDirectories.CompletedSessionsDir, sessionId + ".json");
                if (!FileSystemHelpers.FileExists(sessionFile))
                {
                    throw new ArgumentException($"Session {sessionId} does not exist");
                }

                try
                {
                    var deleteFilesFromBlob = false;
                    var session = await FromJsonFileAsync<Session>(sessionFile); ;
                    var diagnoser = GetDiagnoserForSession(session);
                    if (diagnoser.RequiresStorageAccount)
                    {
                        deleteFilesFromBlob = true;
                    }

                    if (deleteFilesFromBlob)
                    {
                        await DeleteLogsFromBlob(session);
                    }

                    DeleteFolderSafe(Path.Combine(DaasDirectory.LogsDir, sessionId));
                    DeleteFolderSafe(Path.Combine(DaasDirectory.ReportsDir, sessionId));
                    FileSystemHelpers.DeleteFile(sessionFile);
                    Logger.LogSessionVerboseEvent("Session deleted", sessionId);
                }
                catch (Exception ex)
                {
                    Logger.LogSessionErrorEvent("Failed while deleting session", ex, sessionId);
                    throw;
                }
            });
        }

        private static void DeleteFolderSafe(string directory)
        {
            FileSystemHelpers.DeleteDirectoryContentsSafe(directory, ignoreErrors: false);
            FileSystemHelpers.DeleteDirectorySafe(directory, ignoreErrors: false);
        }

        private static async Task DeleteLogsFromBlob(Session session)
        {
            if (session.ActiveInstances == null)
            {
                return;
            }

            foreach (var activeInstance in session.ActiveInstances)
            {
                foreach (var log in activeInstance.Logs)
                {
                    await DeleteLogFromBlob(log, session.SessionId);
                }
            }
        }

        private static async Task DeleteLogFromBlob(LogFile log, string sessionId)
        {
            try
            {
                var fileBlob = Storage.BlobController.GetBlobForFile(log.PartialPath, Infrastructure.Settings.BlobSasUri);
                await fileBlob.DeleteIfExistsAsync(DeleteSnapshotsOption.None, null, null, null);
            }
            catch (Exception ex)
            {
                Logger.LogSessionErrorEvent($"Failed while deleting {log.PartialPath} from blob", ex, sessionId);
            }
        }

        public List<DiagnoserDetails> GetDiagnosers()
        {
            List<DiagnoserDetails> retVal = new List<DiagnoserDetails>();
            foreach (Diagnoser diagnoser in Infrastructure.Settings.Diagnosers)
            {
                retVal.Add(new DiagnoserDetails(diagnoser));
            }

            return retVal;
        }

        public bool IsSandboxAvailable()
        {
            return Settings.Instance.IsSandBoxAvailable();
        }

        public async Task CancelOrphanedInstancesIfNeeded(Session activeSession)
        {
            // If none of the instances picked up the session

            var orphanedInstanceNames = new List<string>();
            if (activeSession.ActiveInstances == null || activeSession.ActiveInstances.Count == 0)
            {
                orphanedInstanceNames = activeSession.Instances;
            }
            else
            {
                var activeInstances = activeSession.ActiveInstances.Select(x => x.Name);
                orphanedInstanceNames = activeSession.Instances.Where(x => !activeInstances.Contains(x)).ToList();
            }

            if (orphanedInstanceNames == null || !orphanedInstanceNames.Any())
            {
                return;
            }

            Logger.LogSessionErrorEvent("Identified orphaned instances for session",
                new OperationCanceledException($"Orphaning instance(s) {string.Join(",", orphanedInstanceNames)} as they haven't picked up the session"),
                activeSession.SessionId);

            var orphanedInstances = new List<ActiveInstance>();
            foreach (var instance in orphanedInstanceNames)
            {
                var activeInstance = new ActiveInstance(instance)
                {
                    Status = Status.Complete
                };

                activeInstance.CollectorErrors.Add("The instance did not pick up the session within the required time");
                orphanedInstances.Add(activeInstance);
            }

            await UpdateActiveSessionAsync((latestSessionFromDisk) =>
            {
                try
                {
                    if (latestSessionFromDisk.ActiveInstances == null)
                    {
                        latestSessionFromDisk.ActiveInstances = orphanedInstances;
                    }
                    else
                    {
                        foreach (var orphanedInstance in orphanedInstances)
                        {
                            if (!latestSessionFromDisk.ActiveInstances.Any(x => x.Name == orphanedInstance.Name))
                            {
                                latestSessionFromDisk.ActiveInstances.Add(orphanedInstance);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogSessionErrorEvent("Failed while updating orphaned instances for the session", ex, latestSessionFromDisk.SessionId);
                }

                return latestSessionFromDisk;
            }, activeSession.SessionId);
        }

        public bool IncludeSasUri { get; set; }
        public bool InvokedViaAutomation { get; set; }
        #endregion

        private Diagnoser GetDiagnoserForSession(Session session)
        {
            return GetDiagnoserForSession(session.Tool);
        }

        private Diagnoser GetDiagnoserForSession(string toolName)
        {
            return Infrastructure.Settings.Diagnosers.FirstOrDefault(x => x.Name == toolName);
        }

        private async Task AnalyzeAndUpdateSessionAsync(CancellationToken token)
        {
            var activeSession = await GetActiveSessionAsync();
            if (activeSession.Mode == Mode.Collect)
            {
                return;
            }

            var collectedLogs = GetCurrentInstanceLogs(activeSession);
            if (collectedLogs.Count > 0)
            {
                var analyzer = GetAnalyzerForSession(activeSession.Tool);
                await analyzer.AnalyzeLogsAsync(collectedLogs, activeSession, token);
            }

            var analyzerErrors = GetAnalyzerErrors(activeSession);

            await UpdateActiveSessionAsync((latestSessionFromDisk) =>
            {
                try
                {
                    ActiveInstance activeInstance = latestSessionFromDisk.GetCurrentInstance();
                    if (activeInstance == null)
                    {
                        return latestSessionFromDisk;
                    }

                    foreach (var log in collectedLogs)
                    {
                        var existingLog = activeInstance.Logs.FirstOrDefault(x => x.Name.Equals(log.Name, StringComparison.OrdinalIgnoreCase));
                        if (existingLog == null)
                        {
                            return latestSessionFromDisk;
                        }
                        existingLog.Reports.AddRange(log.Reports);
                    }

                    //
                    // Append any errors from Analyzer
                    //

                    activeInstance.AnalyzerErrors = activeInstance.AnalyzerErrors.Union(analyzerErrors).Distinct().ToList();
                }
                catch (Exception ex)
                {
                    Logger.LogSessionErrorEvent("Failed while updating reports for the session", ex, latestSessionFromDisk.SessionId);
                }

                return latestSessionFromDisk;
            }, activeSession.SessionId);
        }

        private List<string> GetAnalyzerErrors(Session activeSession)
        {
            if (activeSession.ActiveInstances == null)
            {
                return default;
            }

            ActiveInstance currentInstance = activeSession.GetCurrentInstance();
            if (currentInstance == null)
            {
                return default;
            }

            return currentInstance.AnalyzerErrors;

        }

        private List<LogFile> GetCurrentInstanceLogs(Session activeSession)
        {
            if (activeSession.ActiveInstances == null)
            {
                return default;
            }

            ActiveInstance currentInstance = activeSession.GetCurrentInstance();
            if (currentInstance == null)
            {
                return default;
            }

            return currentInstance.Logs;
        }

        private Analyzer GetAnalyzerForSession(string toolName)
        {
            var diagnoser = GetDiagnoserForSession(toolName);
            if (diagnoser == null)
            {
                throw new Exception("Diagnostic tool not found");
            }

            if (diagnoser.Analyzer == null)
            {
                throw new Exception($"Analyzer not found in {diagnoser.Name}");
            }

            var analyzer = new Analyzer(diagnoser);
            return analyzer;
        }

        private Collector GetCollectorForSession(string toolName)
        {
            var diagnoser = GetDiagnoserForSession(toolName);
            if (diagnoser == null)
            {
                throw new Exception("Diagnostic tool not found");
            }

            if (diagnoser.Collector == null)
            {
                throw new Exception($"Collector not found in {diagnoser.Name}");
            }

            var collector = new Collector(diagnoser);

            return collector;
        }

        private void UpdateAnalyzerStatus(Session activeSession)
        {
            foreach (var activeInstance in activeSession.ActiveInstances)
            {
                foreach (var log in activeInstance.Logs)
                {
                    string logReportPath = Path.Combine(
                        DaasDirectory.ReportsDir,
                        activeSession.SessionId,
                        log.StartTime.ToString(Constants.SessionFileNameFormat));

                    if (!Directory.Exists(logReportPath))
                    {
                        continue;
                    }

                    var statusFile = Directory.GetFiles(
                        logReportPath,
                        "*diagstatus.diaglog",
                        SearchOption.TopDirectoryOnly).FirstOrDefault();

                    if (!string.IsNullOrWhiteSpace(statusFile))
                    {
                        var statusMessages = ReadStatusFile(statusFile);
                        activeInstance.AnalyzerStatusMessages.AddRange(statusMessages);
                    }
                }
            }
        }

        private void UpdateCollectorStatus(Session activeSession)
        {
            foreach (var activeInstance in activeSession.ActiveInstances)
            {
                string instancePath = Path.Combine(
                    DaasDirectory.LogsDir,
                    activeSession.SessionId,
                    activeInstance.Name);

                if (!Directory.Exists(instancePath))
                {
                    continue;
                }

                var statusFile = Directory.GetFiles(
                    instancePath,
                    "*diagstatus.diaglog",
                    SearchOption.TopDirectoryOnly).FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(statusFile))
                {
                    activeInstance.CollectorStatusMessages = ReadStatusFile(statusFile);
                }
            }
        }

        private static List<string> ReadStatusFile(string statusFile)
        {
            var messages = new List<string>();
            try
            {
                using (FileStream fs = File.Open(statusFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader sr = new StreamReader(fs))
                {
                    while (!sr.EndOfStream)
                    {
                        messages.Add(sr.ReadLine());
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogErrorEvent($"Failed to read status file", ex);
            }

            return messages;
        }

        private async Task AppendCollectorResponseToSessionAsync(Session activeSession, DiagnosticToolResponse response)
        {
            try
            {
                await UpdateActiveSessionAsync((latestSessionFromDisk) =>
                {
                    try
                    {
                        if (latestSessionFromDisk.ActiveInstances == null)
                        {
                            latestSessionFromDisk.ActiveInstances = new List<ActiveInstance>();
                        }

                        ActiveInstance activeInstance = latestSessionFromDisk.GetCurrentInstance();
                        if (activeInstance == null)
                        {
                            activeInstance = new ActiveInstance(Infrastructure.GetInstanceId());
                            latestSessionFromDisk.ActiveInstances.Add(activeInstance);
                            Logger.LogSessionVerboseEvent($"Added ActiveInstance to session", latestSessionFromDisk.SessionId);
                        }

                        if (response.Logs != null)
                        {
                            foreach (var log in response.Logs)
                            {
                                if (activeInstance.Logs.Any(x => x.Name == log.Name && x.Size == log.Size))
                                {
                                    continue;
                                }

                                activeInstance.Logs.Add(log);
                            }
                        }

                        if (response.Errors != null && response.Errors.Any())
                        {
                            activeInstance.CollectorErrors = activeInstance.CollectorErrors.Union(response.Errors).Distinct().ToList();
                        }

                        activeInstance.Status = Status.Analyzing;
                        Logger.LogSessionVerboseEvent($"Set ActiveInstance status to Analyzing", latestSessionFromDisk.SessionId);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogSessionErrorEvent("Failed while adding active instance", ex, latestSessionFromDisk.SessionId);
                    }

                    return latestSessionFromDisk;
                }, activeSession.SessionId);
            }
            catch (Exception ex)
            {
                Logger.LogSessionErrorEvent("Failed in AddLogsToActiveSession", ex, activeSession.SessionId);
            }
        }

        private async Task UpdateActiveSessionAsync(Func<Session, Session> updateSession, string sessionId, [CallerMemberName] string callerMethodName = "")
        {
            try
            {
                _sessionLockFile = await AcquireSessionLockAsync(sessionId, callerMethodName);

                if (_sessionLockFile == null)
                {
                    //
                    // We failed to acquire the lock on the session file
                    //

                    return;
                }

                //
                // Load the latest session from the disk under the lock. This
                // ensures that only one instance can write to the sesion
                // and others wait while this instance has the lock
                //

                Session latestSessionFromDisk = await GetActiveSessionAsync();

                Session sessionAfterMergingLatestUpdates = updateSession(latestSessionFromDisk);
                await UpdateActiveSessionFileAsync(sessionAfterMergingLatestUpdates);
            }
            catch (Exception ex)
            {
                Logger.LogSessionErrorEvent($"Failed while updating session", ex, sessionId);
            }

            if (_sessionLockFile != null)
            {
                Logger.LogSessionVerboseEvent($"SessionLock released by {callerMethodName}", sessionId);
                _sessionLockFile.Release();
            }
        }

        private async Task<List<Session>> LoadSessionsAsync(string directoryToLoadSessionsFrom, bool isDetailed = false)
        {
            return await LoadSessionsAsync(new List<string> { directoryToLoadSessionsFrom }, isDetailed);
        }

        private async Task<List<Session>> LoadSessionsAsync(List<string> directoriesToLoadSessionsFrom, bool isDetailed = false)
        {
            EnsureSessionDirectories();

            var sessions = new List<Session>();
            foreach (var directory in directoriesToLoadSessionsFrom)
            {
                foreach (var sessionFile in FileSystemHelpers.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var session = await FromJsonFileAsync<Session>(sessionFile);
                        if (IncludeSasUri)
                        {
                            UpdateRelativePathForLogs(session);
                        }

                        //
                        // For Active session, populate Collector and Analyzer detailed status
                        //

                        if (isDetailed && session.ActiveInstances != null)
                        {
                            if (session.Status == Status.Active)
                            {
                                UpdateCollectorStatus(session);
                                UpdateAnalyzerStatus(session);
                            }

                            foreach (var activeInstance in session.ActiveInstances)
                            {
                                foreach (var log in activeInstance.Logs)
                                {
                                    log.Reports = SanitizeReports(log.Reports);
                                }
                            }
                        }

                        sessions.Add(session);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarningEvent("Failed while loading session", ex);
                    }
                }
            }

            return sessions;
        }

        private List<Report> SanitizeReports(List<Report> reports)
        {
            int minRelativePathSegments = Int32.MaxValue;
            var output = new List<Report>();
            foreach (var report in reports)
            {
                int relativePathSegments = report.PartialPath.Split('/').Length;

                if (relativePathSegments == minRelativePathSegments)
                {
                    output.Add(report);
                }
                else if (relativePathSegments < minRelativePathSegments)
                {
                    minRelativePathSegments = relativePathSegments;
                    output.Clear();
                    output.Add(report);
                }
            }

            return output;
        }

        private void UpdateRelativePathForLogs(Session session)
        {
            if (session.ActiveInstances == null)
            {
                return;
            }

            var diagnoser = GetDiagnoserForSession(session);
            if (diagnoser == null)
            {
                return;
            }

            foreach (var instance in session.ActiveInstances)
            {
                foreach (var log in instance.Logs)
                {
                    if (diagnoser.RequiresStorageAccount)
                    {
                        log.RelativePath = GetPathWithSasUri(log.PartialPath);
                    }
                    else
                    {
                        log.RelativePath = $"{Utility.GetScmHostName()}/api/vfs/{log.PartialPath}";
                    }
                }
            }
        }

        private static string GetPathWithSasUri(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return string.Empty;
            }

            string path;
            var blobUriSections = Settings.Instance.BlobSasUri.Split('?');
            if (blobUriSections.Length >= 2)
            {
                path = blobUriSections[0] + "/" + relativePath.ConvertBackSlashesToForwardSlashes() + "?" +
                           string.Join("?", blobUriSections, 1, blobUriSections.Length - 1);
            }
            else
            {
                path = blobUriSections[0] + "/" + relativePath.ConvertBackSlashesToForwardSlashes();
            }

            return path;
        }

        private async Task SaveSessionAsync(Session session, bool invokedViaDaasConsole)
        {
            try
            {
                session.StartTime = DateTime.UtcNow;
                session.SessionId = GetSessionId(session.StartTime);
                session.Status = Status.Active;

                var diagnoser = GetDiagnoserForSession(session);
                if (diagnoser != null && diagnoser.RequiresStorageAccount)
                {
                    session.BlobStorageHostName = Storage.BlobController.GetBlobStorageHostName(Settings.Instance.BlobSasUri);
                }

                session.DefaultScmHostName = Settings.Instance.DefaultScmHostName;
                await WriteJsonAsync(session,
                    Path.Combine(SessionDirectories.ActiveSessionsDir, session.SessionId + ".json"));

                var details = new
                {
                    Instances = string.Join(",", session.Instances),
                    session.DefaultScmHostName,
                    session.BlobStorageHostName,
                    invokedViaDaasConsole
                };

                Logger.LogNewSession(
                    session.SessionId,
                    session.Mode.ToString(),
                    session.Tool,
                    details);
            }
            catch (Exception ex)
            {
                Logger.LogSessionErrorEvent("Failed while saving the session", ex, session.SessionId);
            }
        }

        private string GetSessionId(DateTime startTime)
        {
            return startTime.ToString(Constants.SessionFileNameFormat);
        }

        private async Task WriteJsonAsync(object objectToSerialize, string filePath)
        {
            await WriteTextAsync(filePath, JsonConvert.SerializeObject(objectToSerialize, Formatting.Indented));
        }

        private async Task<T> FromJsonFileAsync<T>(string filePath)
        {
            string fileContents = await ReadTextAsync(filePath);
            T obj = JsonConvert.DeserializeObject<T>(fileContents);
            return obj;
        }

        async Task<string> ReadTextAsync(string path)
        {
            var sb = new StringBuilder();
            using (var sourceStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 4096, useAsync: true))
            {
                byte[] buffer = new byte[0x1000];
                int numRead;
                while ((numRead = await sourceStream.ReadAsync(buffer, 0, buffer.Length)) != 0)
                {
                    string text = Encoding.UTF8.GetString(buffer, 0, numRead);
                    sb.Append(text);
                }

                return sb.ToString();
            }
        }

        async Task WriteTextAsync(string filePath, string text)
        {
            byte[] encodedText = Encoding.UTF8.GetBytes(text);

            using (var sourceStream =
                new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, bufferSize: 4096,
                useAsync: true))
            {
                await sourceStream.WriteAsync(encodedText, 0, encodedText.Length);
            }
        }

        private async Task<IOperationLock> AcquireSessionLockAsync(string sessionId, string callerMethodName)
        {
            IOperationLock sessionLock = new SessionLockFile(GetActiveSessionLockPath(sessionId));
            int loopCount = 0;

            Logger.LogSessionVerboseEvent($"Acquiring SessionLock by {callerMethodName}", sessionId);
            while (!sessionLock.Lock(callerMethodName)
                && loopCount <= 60)
            {
                ++loopCount;
                await Task.Delay(1000);
            }

            if (loopCount > 60)
            {
                Logger.LogSessionVerboseEvent($"Deleting the lock file as it seems to be in an orphaned stage", sessionId);
                sessionLock.Release();
                return null;
            }

            Logger.LogSessionVerboseEvent($"Acquired SessionLock by {callerMethodName}", sessionId);
            return sessionLock;
        }

        private async Task UpdateActiveSessionFileAsync(Session activeSesion)
        {
            await WriteJsonAsync(activeSesion,
                Path.Combine(SessionDirectories.ActiveSessionsDir, activeSesion.SessionId + ".json"));
        }

        private string GetActiveSessionLockPath(string sessionId)
        {
            return Path.Combine(SessionDirectories.ActiveSessionsDir, sessionId + ".json.lock");
        }

        private async Task SetCurrentInstanceAsCompleteAsync(Session activeSession)
        {
            Logger.LogSessionVerboseEvent("Setting current instance as Complete", activeSession.SessionId);
            await SetCurrentInstanceStatusAsync(activeSession, Status.Complete);
        }

        private async Task SetCurrentInstanceAsStartedAsync(Session activeSession)
        {
            Logger.LogSessionVerboseEvent("Setting current instance as Started", activeSession.SessionId);
            await SetCurrentInstanceStatusAsync(activeSession, Status.Started);
        }

        private async Task SetCurrentInstanceStatusAsync(Session activeSession, Status sessionStatus)
        {
            try
            {
                await UpdateActiveSessionAsync((latestSessionFromDisk) =>
                {
                    if (latestSessionFromDisk.ActiveInstances == null)
                    {
                        latestSessionFromDisk.ActiveInstances = new List<ActiveInstance>();
                    }

                    var activeInstance = latestSessionFromDisk.GetCurrentInstance();
                    if (activeInstance == null)
                    {
                        activeInstance = new ActiveInstance(Infrastructure.GetInstanceId());
                        latestSessionFromDisk.ActiveInstances.Add(activeInstance);
                    }

                    activeInstance.Status = sessionStatus;
                    return latestSessionFromDisk;
                }, activeSession.SessionId);
            }
            catch (Exception ex)
            {
                Logger.LogSessionErrorEvent($"Failed while updating current instance status to {sessionStatus}", ex, activeSession.SessionId);
            }
        }

        private void EnsureSessionDirectories()
        {
            _allSessionsDirs.ForEach(x =>
            {
                try
                {
                    FileSystemHelpers.EnsureDirectory(x);
                }
                catch(Exception)
                {
                    // Ignore
                }
            });
        }

        private bool AllInstancesFinished(Session activeSession)
        {
            if (activeSession.ActiveInstances == null)
            {
                return false;
            }

            var completedInstances = activeSession.ActiveInstances.Where(x => x.Status == Status.Complete || x.Status == Status.TimedOut).Select(x => x.Name);

            return Enumerable.SequenceEqual(completedInstances.OrderBy(x => x),
                activeSession.Instances.OrderBy(x => x),
                StringComparer.OrdinalIgnoreCase);
        }

        private async Task MarkSessionAsCompleteAsync(Session activeSession, bool forceCompletion = false)
        {
            if (activeSession == null)
            {
                return;
            }
            
            await UpdateActiveSessionAsync((latestSessionFromDisk) =>
            {
                latestSessionFromDisk.Status = forceCompletion ? Status.TimedOut : Status.Complete;
                latestSessionFromDisk.EndTime = DateTime.UtcNow;
                return latestSessionFromDisk;

            }, activeSession.SessionId);

            string sessionId = activeSession.SessionId;
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return;
            }

            string activeSessionFile = Path.Combine(SessionDirectories.ActiveSessionsDir, sessionId + ".json");
            string completedSessionFile = Path.Combine(SessionDirectories.CompletedSessionsDir, sessionId + ".json");

            try
            {
                //
                // Move the session file from Active to Complete folder
                //

                Logger.LogSessionVerboseEvent($"Moving session file to completed folder", sessionId);
                FileSystemHelpers.MoveFile(activeSessionFile, completedSessionFile);

                //
                // Clean-up the lock file from the Active session folder
                //
                
                Logger.LogSessionVerboseEvent($"Cleaning up the lock file from Active session folder", sessionId);
                FileSystemHelpers.DeleteFileSafe(GetActiveSessionLockPath(sessionId));
                Logger.LogSessionVerboseEvent($"Session is complete after {DateTime.UtcNow.Subtract(activeSession.StartTime).TotalMinutes} minutes", sessionId);
            }
            catch (Exception ex)
            {
                //
                // It is possible that multiple instances are trying to mark the session as complete, so
                // ignore any exceptions that come in this code path as some instance will eventually 
                // succeed to update the instance status
                //

                Logger.LogSessionWarningEvent($"Unhandled exception in MarkSessionAsCompleteAsync", ex, sessionId);
            }
        }
    }
}
