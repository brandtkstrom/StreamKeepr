namespace StreamKeepr.Domain;

/// <summary>One recorder execution for one part of a session.</summary>
public readonly record struct JobId(string SessionId, int Part)
{
    public override string ToString() => $"{SessionId}/p{Part:D3}";
}
