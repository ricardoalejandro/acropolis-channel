namespace Acropolis.Platform.Application;

public sealed record Greeting(string Message);

public sealed class GreetingService
{
    public Greeting GetGreeting() => new("Hola mundo");
}
