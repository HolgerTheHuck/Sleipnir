using TrameCore.Attributes;

namespace TrameServerTemplate;

[TrameController("Greeting")]
public class GreetingController
{
    [TrameMethod("Hello")]
    public string Hello(string name = "Trame")
    {
        return $"Hello, {name}!";
    }

    [TrameMethod("Ping")]
    public PingResponse Ping()
    {
        return new PingResponse { Time = DateTimeOffset.UtcNow };
    }
}

public class PingResponse
{
    public DateTimeOffset Time { get; set; }
}
