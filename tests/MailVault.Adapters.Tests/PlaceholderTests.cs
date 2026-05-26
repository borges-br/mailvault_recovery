using System;
using System.IO;
using System.Reflection;
using XstReader;
using Xunit;

namespace MailVault.Adapters.Tests;

public class PlaceholderTests
{
    [Fact]
    public void ExploreTypes()
    {
        string outputPath = @"c:\Github\mailvault_recovery\scratch\types.txt";
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        
        using var writer = new StreamWriter(outputPath);

        PrintType(typeof(XstPropertySet), writer);
    }

    private void PrintType(Type type, StreamWriter writer)
    {
        writer.WriteLine($"Type: {type.FullName}");
        writer.WriteLine("  Properties:");
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            writer.WriteLine($"    {prop.PropertyType.FullName} {prop.Name}");
        }
        writer.WriteLine("  Methods:");
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.DeclaringType == type)
            {
                writer.WriteLine($"    {method.ReturnType.FullName} {method.Name}");
            }
        }
        writer.WriteLine();
    }
}
