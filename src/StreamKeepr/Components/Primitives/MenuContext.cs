namespace StreamKeepr.Components.Primitives;

/// <summary>Lets each <see cref="MenuItem"/> register with its owning <see cref="Menu"/>.</summary>
internal sealed class MenuContext(Menu owner)
{
    public void Register(MenuItem item) => owner.Register(item);

    public void Unregister(MenuItem item) => owner.Unregister(item);

    public Task CloseAsync(bool returnFocus) => owner.CloseAsync(returnFocus);
}
