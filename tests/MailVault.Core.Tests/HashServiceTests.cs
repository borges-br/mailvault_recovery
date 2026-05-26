using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Core;
using Xunit;

namespace MailVault.Core.Tests;

public class HashServiceTests
{
    private sealed class TestProgressReporter : IProgressReporter
    {
        public double LastPercentage { get; private set; } = -1;
        public string LastStatus { get; private set; } = string.Empty;
        public int CallCount { get; private set; }

        public void ReportProgress(double percentage, string status)
        {
            LastPercentage = percentage;
            LastStatus = status;
            CallCount++;
        }
    }

    [Fact]
    public async Task CalculateSha256Async_ShouldCalculateCorrectHash_ForSmallFile()
    {
        // Arrange
        string tempFile = Path.GetTempFileName();
        string content = "MailVault Recovery Base Test Content";
        await File.WriteAllTextAsync(tempFile, content, Encoding.UTF8);

        var hashService = new HashService();
        var progress = new TestProgressReporter();

        try
        {
            // Act
            string sha256 = await hashService.CalculateSha256Async(tempFile, progress, CancellationToken.None);

            // Assert
            Assert.NotEmpty(sha256);
            Assert.Equal(64, sha256.Length); // hex length of SHA256
            
            // Expected SHA256 computed on the exact same file using standard .NET ComputeHashAsync
            using var expectedStream = File.OpenRead(tempFile);
            using var sha = System.Security.Cryptography.SHA256.Create();
            byte[] expectedHashBytes = await sha.ComputeHashAsync(expectedStream);
            string expectedHash = Convert.ToHexString(expectedHashBytes).ToLowerInvariant();

            Assert.Equal(expectedHash, sha256);
            Assert.True(progress.CallCount > 0);
            Assert.Equal(100.0, progress.LastPercentage);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public async Task CalculateSha256Async_ShouldThrowFileNotFoundException_IfFileDoesNotExist()
    {
        // Arrange
        var hashService = new HashService();
        string nonexistentFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".ost");

        // Act & Assert
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            hashService.CalculateSha256Async(nonexistentFile, null, CancellationToken.None)
        );
    }
}
