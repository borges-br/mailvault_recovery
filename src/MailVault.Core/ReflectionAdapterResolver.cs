using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MailVault.Core;

public sealed class ReflectionAdapterResolver : IAdapterResolver
{
    private readonly List<AdapterDescriptor> _descriptors;

    public ReflectionAdapterResolver()
    {
        string basePath = AppContext.BaseDirectory;

        // Register default adapters
        _descriptors = new List<AdapterDescriptor>
        {
            new AdapterDescriptor(
                Name: "XstReader.Api Engine",
                Version: "1.0.6",
                AssemblyPath: Path.Combine(basePath, "MailVault.Adapters.XstReader.dll"),
                SupportedExtensions: new[] { ".ost", ".pst" },
                Priority: 10,
                HealthStatus: File.Exists(Path.Combine(basePath, "MailVault.Adapters.XstReader.dll")) ? "Healthy" : "Missing"
            ),
            new AdapterDescriptor(
                Name: "Libpff Engine",
                Version: "1.0.0",
                AssemblyPath: Path.Combine(basePath, "MailVault.Adapters.Libpff.dll"),
                SupportedExtensions: new[] { ".ost", ".pst" },
                Priority: 5,
                HealthStatus: File.Exists(Path.Combine(basePath, "MailVault.Adapters.Libpff.dll")) ? "Healthy" : "Missing"
            )
        };

        // Attach event handler to resolve transitive assemblies of dynamic plugins
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (context, assemblyName) =>
        {
            string dllName = $"{assemblyName.Name}.dll";
            string pathInBase = Path.Combine(basePath, dllName);
            if (File.Exists(pathInBase))
            {
                return context.LoadFromAssemblyPath(pathInBase);
            }
            return null;
        };
    }

    public IEnumerable<AdapterDescriptor> GetAvailableAdapters()
    {
        return _descriptors;
    }

    public AdapterLoadResult ResolveAdapter(string extension)
    {
        string ext = extension.ToLowerInvariant();
        var candidate = _descriptors
            .Where(d => d.SupportedExtensions.Contains(ext) && d.HealthStatus == "Healthy")
            .OrderByDescending(d => d.Priority)
            .FirstOrDefault();

        if (candidate == null)
        {
            string basePath = AppContext.BaseDirectory;
            var candidatesInfo = string.Join("; ", _descriptors.Select(d => $"{Path.GetFileName(d.AssemblyPath)} ({d.HealthStatus})"));
            string expected = ext == ".ost" || ext == ".pst" ? "MailVault.Adapters.XstReader.dll ou MailVault.Adapters.Libpff.dll" : "Adaptador desconhecido";
            string msg = $"Nenhum adapter funcional encontrado para a extensão '{extension}'. " +
                         $"Procurado em: '{basePath}'. " +
                         $"Esperado: {expected}. " +
                         $"Status dos adapters: {candidatesInfo}. " +
                         $"Ação recomendada: Verifique se o projeto Desktop está copiando os plugins de adapters para a pasta de saída do aplicativo.";
            return new AdapterLoadResult(
                Success: false,
                Reader: null,
                ErrorMessage: msg,
                Exception: null
            );
        }

        return LoadAdapterByPath(candidate.AssemblyPath);
    }

    public AdapterLoadResult LoadAdapterByPath(string assemblyPath)
    {
        try
        {
            if (!File.Exists(assemblyPath))
            {
                return new AdapterLoadResult(
                    Success: false,
                    Reader: null,
                    ErrorMessage: $"O assembly do adapter não foi encontrado no caminho: '{assemblyPath}'",
                    Exception: null
                );
            }

            var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);

            var type = assembly.GetTypes()
                .FirstOrDefault(t => typeof(IMailStoreReader).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

            if (type == null)
            {
                return new AdapterLoadResult(
                    Success: false,
                    Reader: null,
                    ErrorMessage: $"Nenhuma implementação de IMailStoreReader encontrada no assembly '{Path.GetFileName(assemblyPath)}'.",
                    Exception: null
                );
            }

            if (Activator.CreateInstance(type) is IMailStoreReader reader)
            {
                return new AdapterLoadResult(
                    Success: true,
                    Reader: reader,
                    ErrorMessage: null,
                    Exception: null
                );
            }

            return new AdapterLoadResult(
                Success: false,
                Reader: null,
                ErrorMessage: $"Falha ao instanciar o tipo '{type.FullName}' como IMailStoreReader.",
                Exception: null
            );
        }
        catch (Exception ex)
        {
            return new AdapterLoadResult(
                Success: false,
                Reader: null,
                ErrorMessage: $"Erro ao carregar dinamicamente o adapter: {ex.Message}",
                Exception: ex
            );
        }
    }
}
