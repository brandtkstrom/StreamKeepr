using LanguageExt;

namespace StreamRecorder.Core;

public sealed class RecorderOrchestrator
{
    // TODO - implement
    public RecorderState State { get; }

    public Aff<Unit> FireAsync(RecorderEvent evt) =>
        Aff<Unit>.Effect(async () =>
        {
            // TODO - implement
            await Task.CompletedTask;
        });

    public Aff<Unit> StopAsync() =>
        Aff<Unit>.Effect(async () =>
        {
            // TODO - implement
            await Task.CompletedTask;
        });
}