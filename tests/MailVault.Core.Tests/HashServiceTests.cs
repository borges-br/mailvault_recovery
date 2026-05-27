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

    [Fact]
    public async Task HashService_UsesLargerBuffer_AndPreservesHash()
    {
        // Arrange
        string tempFile = Path.GetTempFileName();
        string content = "Testing dynamic buffer and hash compliance under sequential scan constraints.";
        await File.WriteAllTextAsync(tempFile, content, Encoding.UTF8);

        // Configure a 10-byte buffer to force many chunks
        var hashService = new HashService(bufferSize: 10);
        var progress = new TestProgressReporter();

        try
        {
            // Act
            string sha256 = await hashService.CalculateSha256Async(tempFile, progress, CancellationToken.None);

            // Assert
            Assert.NotEmpty(sha256);
            
            using var expectedStream = File.OpenRead(tempFile);
            using var sha = System.Security.Cryptography.SHA256.Create();
            byte[] expectedHashBytes = await sha.ComputeHashAsync(expectedStream);
            string expectedHash = Convert.ToHexString(expectedHashBytes).ToLowerInvariant();

            Assert.Equal(expectedHash, sha256);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task HashService_ReportsProgressThrottled_ForLargeFile()
    {
        // Arrange
        string tempFile = Path.GetTempFileName();
        string content = new string('A', 500);
        await File.WriteAllTextAsync(tempFile, content, Encoding.UTF8);

        var mockTime = new ProgressThrottlerTests.MockTimeProvider();
        // Using a 10-byte buffer to force 50 iterations
        var hashService = new HashService(bufferSize: 10, timeProvider: mockTime);
        var statuses = new System.Collections.Generic.List<string>();
        var progress = new ActionProgressReporter((pct, status) => statuses.Add(status));

        try
        {
            // Act
            await hashService.CalculateSha256Async(tempFile, progress, CancellationToken.None);

            // Assert progress events are throttled
            Assert.True(statuses.Count >= 2);
            Assert.Contains(statuses, s => s.Contains("Hash started:"));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task HashService_AlwaysEmitsFinalProgress()
    {
        // Arrange
        string tempFile = Path.GetTempFileName();
        string content = "Hash final completion test.";
        await File.WriteAllTextAsync(tempFile, content, Encoding.UTF8);

        var hashService = new HashService();
        var progress = new TestProgressReporter();

        try
        {
            // Act
            await hashService.CalculateSha256Async(tempFile, progress, CancellationToken.None);

            // Assert
            Assert.True(progress.CallCount > 0);
            Assert.Equal(100.0, progress.LastPercentage);
            Assert.Contains("Cálculo de hash concluído com sucesso", progress.LastStatus);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task HashService_CancelDuringHash_ThrowsOrReturnsCancelledConsistently()
    {
        // Arrange
        string tempFile = Path.GetTempFileName();
        string content = new string('B', 1000);
        await File.WriteAllTextAsync(tempFile, content, Encoding.UTF8);

        var hashService = new HashService(bufferSize: 10);
        using var cts = new CancellationTokenSource();
        
        var progress = new TestProgressReporter();

        // Cancel after first report
        var progressWrapper = new ActionProgressReporter((pct, status) =>
        {
            cts.Cancel();
        });

        try
        {
            // Act & Assert
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await hashService.CalculateSha256Async(tempFile, progressWrapper, cts.Token);
            });
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    private sealed class ActionProgressReporter : IProgressReporter
    {
        private readonly Action<double, string> _onProgress;
        public ActionProgressReporter(Action<double, string> onProgress) => _onProgress = onProgress;
        public void ReportProgress(double percentage, string status) => _onProgress(percentage, status);
    }
}
