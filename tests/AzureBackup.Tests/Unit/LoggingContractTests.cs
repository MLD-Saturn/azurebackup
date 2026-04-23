using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Xunit;

namespace AzureBackup.Tests;

public class LoggingContractTests
{
    [Fact]
    public void ServiceLog_Methods_AreConditionalOnDiagnosticLog()
    {
        var serviceTypes = new[]
        {
            typeof(AzureBackup.Core.Services.AzureBlobService),
            typeof(AzureBackup.Core.Services.BackupOrchestrator),
            typeof(AzureBackup.Core.Services.ChunkIndexService),
            typeof(AzureBackup.Core.Services.EncryptionService),
            typeof(AzureBackup.Core.Services.FileWatcherService),
            typeof(AzureBackup.Core.Services.LocalDatabaseService),
            typeof(AzureBackup.Core.Services.RestoreService),
        };

        var failures = new List<string>();
        foreach (var t in serviceTypes)
        {
            var log = t.GetMethod("Log",
                BindingFlags.Instance | BindingFlags.NonPublic,
                new[] { typeof(string) });
            if (log == null) { failures.Add(t.Name + ": no private Log(string) method"); continue; }
            var attr = log.GetCustomAttribute<ConditionalAttribute>();
            if (attr == null || attr.ConditionString != "DIAGNOSTICLOG")
                failures.Add(t.Name + ".Log not [Conditional(DIAGNOSTICLOG)]");
        }
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }
}
