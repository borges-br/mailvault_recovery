using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Audit;
using MailVault.Domain;
using Xunit;

namespace MailVault.Audit.Tests;

public class ManifestServiceTests
{
    [Fact]
    public void GenerateCaseId_ShouldReturnFormat_CaseDate()
    {
        // Arrange
        var date = new DateTimeOffset(2026, 5, 26, 9, 30, 12, TimeSpan.FromHours(-3));

        // Act
        string caseId = ManifestService.GenerateCaseId(date);

        // Assert
        Assert.Equal("CASE-2026-05-26-093012", caseId);
    }

    [Fact]
    public async Task SaveManifestAsync_ShouldSerializeCorrectly_AndSaveToFile()
    {
        // Arrange
        string tempBaseDirectory = Path.Combine(Path.GetTempPath(), "mailvault-test-" + Guid.NewGuid());
        var startedAt = new DateTimeOffset(2026, 5, 26, 9, 30, 12, TimeSpan.FromHours(-3));
        var caseId = ManifestService.GenerateCaseId(startedAt);

        var manifest = new RecoveryManifest(
            CaseId: caseId,
            SourceFile: @"C:\inbox.ost",
            SourceSizeBytes: 5000000,
            SourceSha256: "ea3f1200155bcf3a1e94de551247ff55a300ff124b88a912a52de192cf4a09c2",
            OperatorName: "TI-Operator",
            StartedAt: startedAt,
            CompletedAt: startedAt.AddMinutes(5),
            ToolVersion: "1.0.0.0",
            Actions: new[] { "Inspect", "Integrity Hash" },
            Warnings: new[] { new ExtractionIssue("MV-WARN-001", "Warning", "Small warnings", "file", "details") }
        );

        try
        {
            // Act
            string savedPath = await ManifestService.SaveManifestAsync(tempBaseDirectory, manifest, CancellationToken.None);

            // Assert
            Assert.True(File.Exists(savedPath));
            string jsonContent = await File.ReadAllTextAsync(savedPath);
            Assert.Contains("caseId", jsonContent);
            Assert.Contains("CASE-2026-05-26-093012", jsonContent);

            // Deserialize back to check integrity
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            var deserialized = JsonSerializer.Deserialize<RecoveryManifest>(jsonContent, options);

            Assert.NotNull(deserialized);
            Assert.Equal(manifest.CaseId, deserialized.CaseId);
            Assert.Equal(manifest.SourceFile, deserialized.SourceFile);
            Assert.Equal(manifest.SourceSizeBytes, deserialized.SourceSizeBytes);
            Assert.Equal(manifest.SourceSha256, deserialized.SourceSha256);
            Assert.Equal(manifest.OperatorName, deserialized.OperatorName);
            Assert.Single(deserialized.Warnings);
            Assert.Equal("MV-WARN-001", deserialized.Warnings[0].Code);
        }
        finally
        {
            if (Directory.Exists(tempBaseDirectory))
            {
                Directory.Delete(tempBaseDirectory, recursive: true);
            }
        }
    }
}
