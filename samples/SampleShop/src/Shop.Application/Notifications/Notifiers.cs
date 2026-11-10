namespace Shop.Application.Notifications;

/// <summary>One interface, two implementations: a call through it can reach either.</summary>
public interface INotifier
{
    event EventHandler<string>? Sent;

    Task SendAsync(string message);
}

public abstract class NotifierBase : INotifier
{
    public event EventHandler<string>? Sent;

    public async Task SendAsync(string message)
    {
        await DeliverAsync(Format(message));
        Sent?.Invoke(this, message);
    }

    protected virtual string Format(string message) => message;

    protected abstract Task DeliverAsync(string text);
}

public class EmailNotifier : NotifierBase
{
    protected override string Format(string message) => "<p>" + message + "</p>";

    protected override Task DeliverAsync(string text) => Task.CompletedTask;
}

public class SmsNotifier : NotifierBase
{
    protected override Task DeliverAsync(string text) => Task.CompletedTask;
}

public sealed class DeliveryLog : IDisposable
{
    private readonly List<string> _lines = [];

    public DeliveryLog(INotifier notifier) => notifier.Sent += OnSent;

    public int Count => _lines.Count;

    void IDisposable.Dispose() => _lines.Clear();

    private void OnSent(object? sender, string message) => _lines.Add(message);
}
