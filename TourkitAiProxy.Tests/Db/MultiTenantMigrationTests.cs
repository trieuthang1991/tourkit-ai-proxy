using Microsoft.Extensions.Logging.Abstractions;
using TourkitAiProxy.Services.Db;
using Xunit;

namespace TourkitAiProxy.Tests.Db;

public class MultiTenantMigrationTests
{
    [Fact]
    public void Run_is_noop_when_no_legacy_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tk-mig-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            MultiTenantMigration.Run(dir, NullLogger.Instance);
            Assert.False(Directory.Exists(Path.Combine(dir, "legacy-backup")),
                "Không có legacy file → không tạo backup folder");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Run_moves_legacy_files_to_timestamped_backup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tk-mig-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "mails.json"), "{}");
            File.WriteAllText(Path.Combine(dir, "mail-account.json"), "{}");
            File.WriteAllText(Path.Combine(dir, "visa-assessments.json"), "{}");

            MultiTenantMigration.Run(dir, NullLogger.Instance);

            Assert.False(File.Exists(Path.Combine(dir, "mails.json")));
            Assert.False(File.Exists(Path.Combine(dir, "mail-account.json")));
            Assert.False(File.Exists(Path.Combine(dir, "visa-assessments.json")));

            var backups = Directory.GetDirectories(Path.Combine(dir, "legacy-backup"));
            Assert.Single(backups);
            Assert.True(File.Exists(Path.Combine(backups[0], "mails.json")));
            Assert.True(File.Exists(Path.Combine(backups[0], "mail-account.json")));
            Assert.True(File.Exists(Path.Combine(backups[0], "visa-assessments.json")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Run_moves_visa_files_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tk-mig-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var visaDir = Path.Combine(dir, "visa-files", "assessment-A");
            Directory.CreateDirectory(visaDir);
            File.WriteAllBytes(Path.Combine(visaDir, "passport.pdf"), new byte[] { 1, 2, 3 });

            MultiTenantMigration.Run(dir, NullLogger.Instance);

            Assert.False(Directory.Exists(Path.Combine(dir, "visa-files")));
            var backups = Directory.GetDirectories(Path.Combine(dir, "legacy-backup"));
            Assert.Single(backups);
            Assert.True(File.Exists(Path.Combine(backups[0], "visa-files", "assessment-A", "passport.pdf")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
