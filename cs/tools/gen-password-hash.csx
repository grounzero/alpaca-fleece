#!/usr/bin/env dotnet-script
#r "nuget: BCrypt.Net-Next, 4.*"

var password = Args.Count > 0 ? Args[0] : "admin";
var hash = BCrypt.Net.BCrypt.HashPassword(password);
Console.WriteLine(hash);
