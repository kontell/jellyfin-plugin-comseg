using System.Text;

namespace Jellyfin.Plugin.Comseg.Comskip;

/// <summary>
/// The last part of a process's stdout and stderr together. The two streams'
/// line events arrive on different threads; a StringBuilder written from
/// both at once corrupts itself and throws inside the reader callback, where
/// nothing can catch it. Bounded, since only the tail is ever logged.
/// </summary>
public sealed class OutputTail
{
    private const int MaxLength = 8192;
    private readonly StringBuilder _text = new();
    private readonly object _lock = new();

    /// <summary>Appends a line; null (end of stream) is ignored.</summary>
    /// <param name="line">The line.</param>
    public void Add(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_lock)
        {
            if (_text.Length > MaxLength)
            {
                _text.Remove(0, _text.Length - MaxLength);
            }

            _text.AppendLine(line);
        }
    }

    /// <inheritdoc />
    public override string ToString()
    {
        lock (_lock)
        {
            return _text.ToString();
        }
    }
}
