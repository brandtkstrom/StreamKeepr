namespace StreamKeepr.Hosting;

/// <summary>
/// Where the Data Protection key ring lives. The one source both startup preflight and the key
/// ring read, so they always probe and use the same folder. The host default is
/// <see cref="AppDataPaths.DataProtectionKeysDirectory"/> (beside the record store); the Mock
/// profile registers its own fixed folder because its record store is per process.
/// It is a service, not configuration, so deployments cannot point it elsewhere.
/// </summary>
public sealed record DataProtectionKeyLocation(string Directory);
