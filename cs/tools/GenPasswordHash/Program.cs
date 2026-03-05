// Usage: dotnet run --project tools/GenPasswordHash -- <password>
if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/GenPasswordHash -- <password>");
    return 1;
}

var password = args[0];
Console.WriteLine(BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12));
return 0;
