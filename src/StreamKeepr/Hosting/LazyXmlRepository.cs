using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace StreamKeepr.Hosting;

/// <summary>
/// Defers creating the key-ring repository until the key ring is first used.
/// Startup preflight must run before the key directory is touched, but Data
/// Protection services are resolved while the request pipeline is built.
/// </summary>
internal sealed class LazyXmlRepository(Lazy<IDeletableXmlRepository> repository) : IDeletableXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements() => repository.Value.GetAllElements();

    public void StoreElement(XElement element, string friendlyName) =>
        repository.Value.StoreElement(element, friendlyName);

    public bool DeleteElements(Action<IReadOnlyCollection<IDeletableElement>> chooseElements) =>
        repository.Value.DeleteElements(chooseElements);
}
