using SleipnirCore.Attributes;

namespace HelloSleipnir;

[SleipnirController("Greeting")]
public class GreetingController
{
    [SleipnirMethod("Hello")]
    public string Hello(string name = "World")
    {
        return $"Hello, {name}! Welcome to Sleipnir.";
    }
}
