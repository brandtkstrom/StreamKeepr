namespace StreamKeepr.Infrastructure.Media;

/// <summary>
/// A bounded, thread-safe tail of stderr lines from the recorder processes, used for the
/// diagnostic detail on <c>RecorderExited</c>.
/// </summary>
public sealed class StderrTail
{
    private readonly int _capacity;
    private readonly Queue<string> _lines = new();

    public StderrTail(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    public void Add(string source, string line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(line);

        lock (_lines)
        {
            _lines.Enqueue($"{source}: {line}");
            while (_lines.Count > _capacity)
                _lines.Dequeue();
        }
    }

    public override string ToString()
    {
        lock (_lines)
            return string.Join(" | ", _lines);
    }
}
