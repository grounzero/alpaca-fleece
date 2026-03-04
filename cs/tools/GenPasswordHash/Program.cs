// Usage: dotnet run --project tools/GenPasswordHash -- yourpassword
var password = args.Length > 0 ? args[0] : throw new Exception("Usage: dotnet run -- <password>");
Console.WriteLine(BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12));
