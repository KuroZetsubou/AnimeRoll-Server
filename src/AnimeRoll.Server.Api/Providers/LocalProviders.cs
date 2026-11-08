namespace AnimeRoll.Server.Api.Providers;

/// Hook for extra, machine-local sources: a file in the git-ignored Providers/Private/ folder can implement
/// RegisterLocal to add its own ISourceProvider services. Without such a file this does nothing.
public static partial class LocalProviders
{
    public static void Register(IServiceCollection services) => RegisterLocal(services);

    static partial void RegisterLocal(IServiceCollection services);
}
