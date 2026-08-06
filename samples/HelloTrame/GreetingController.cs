using TrameCore.Attributes;

namespace HelloTrame;

[TrameController("Greeting")]
public class GreetingController
{
    [TrameMethod("Hello")]
    public string Hello(string name = "World")
    {
        return $"Hello, {name}! Welcome to Trame.";
    }
}
