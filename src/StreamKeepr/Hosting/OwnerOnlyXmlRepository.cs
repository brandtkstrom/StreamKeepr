using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Logging;

namespace StreamKeepr.Hosting;

/// <summary>
/// Wraps the framework file-system repository to enforce owner-only key files and
/// to treat an unreadable ring as empty: protected payloads then fail to decrypt,
/// callers reject them, and users re-authenticate instead of the app crashing.
/// </summary>
internal sealed class OwnerOnlyXmlRepository(
    FileSystemXmlRepository inner,
    string keyDirectory,
    ILogger<OwnerOnlyXmlRepository> logger) : IDeletableXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        try
        {
            var elements = inner.GetAllElements();
            if (elements.Count > 0)
                return elements;

            logger.LogWarning(
                "The Data Protection key ring at {KeyDirectory} is empty. Cookies, antiforgery tokens, " +
                "and encrypted admin tokens protected with a previous ring cannot be read; affected " +
                "users must re-authenticate.",
                keyDirectory);
            return elements;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            logger.LogError(
                ex,
                "The Data Protection key ring at {KeyDirectory} is unreadable. Cookies, antiforgery " +
                "tokens, and encrypted admin tokens protected with the existing keys cannot be read; " +
                "affected users must re-authenticate.",
                keyDirectory);
            return [];
        }
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        inner.StoreElement(element, friendlyName);
        OwnerOnlyFileSystem.EnforceOwnerOnlyKeyFiles(keyDirectory);
    }

    public bool DeleteElements(Action<IReadOnlyCollection<IDeletableElement>> chooseElements) =>
        inner.DeleteElements(chooseElements);
}
