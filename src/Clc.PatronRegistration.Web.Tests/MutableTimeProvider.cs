namespace Clc.PatronRegistration.Tests;

internal sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => utcNow;

    internal void SetUtcNow(DateTimeOffset value) => utcNow = value;

    internal void Advance(TimeSpan amount) => utcNow = utcNow.Add(amount);
}
