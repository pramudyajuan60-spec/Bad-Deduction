using BadDeduction.Tests.Harness;

// Usage: dotnet run --project tests/BadDeduction.Tests [-- <name filter>]
return Runner.Run(typeof(Program).Assembly, args.FirstOrDefault());
