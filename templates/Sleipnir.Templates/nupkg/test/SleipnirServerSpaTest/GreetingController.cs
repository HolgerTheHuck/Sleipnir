using SleipnirCore.Attributes;

namespace SleipnirServerSpaTest;

[SleipnirController("Greeting")]
public class GreetingController
{
    [SleipnirMethod("Hello")]
    public string Hello(string name = "Sleipnir")
    {
        return $"Hello, {name}!";
    }

    [SleipnirMethod("Ping")]
    public PingResponse Ping()
    {
        return new PingResponse { Time = DateTimeOffset.UtcNow };
    }
}

public class PingResponse
{
    public DateTimeOffset Time { get; set; }
}
