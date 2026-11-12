using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

internal sealed class TestChannelsView : IChannelsView
{
    public IReadOnlyList<ChannelView> Items { get; set; } = [];

    public int AllReads { get; private set; }

    public List<string> Calls { get; } = [];

    public Func<string, Task> OnAdd { get; set; } = _ => Task.CompletedTask;

    public Func<string, Task> OnRemove { get; set; } = _ => Task.CompletedTask;

    public Func<string, Task> OnStop { get; set; } = _ => Task.CompletedTask;

    public Func<string, Task> OnResume { get; set; } = _ => Task.CompletedTask;

    public event Action? Changed { add { } remove { } }

    public IReadOnlyList<ChannelView> All
    {
        get
        {
            AllReads++;
            return Items;
        }
    }

    public Task AddAsync(string login, CancellationToken ct)
    {
        Calls.Add($"add:{login}");
        return OnAdd(login);
    }

    public Task RemoveAsync(string login, CancellationToken ct)
    {
        Calls.Add($"remove:{login}");
        return OnRemove(login);
    }

    public Task StopAsync(string login, CancellationToken ct)
    {
        Calls.Add($"stop:{login}");
        return OnStop(login);
    }

    public Task ResumeAsync(string login, CancellationToken ct)
    {
        Calls.Add($"resume:{login}");
        return OnResume(login);
    }

    public void Dispose()
    {
    }
}
