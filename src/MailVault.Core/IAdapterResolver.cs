using System.Collections.Generic;

namespace MailVault.Core;

public interface IAdapterResolver
{
    IEnumerable<AdapterDescriptor> GetAvailableAdapters();
    AdapterLoadResult ResolveAdapter(string extension);
    AdapterLoadResult LoadAdapterByPath(string assemblyPath);
}
