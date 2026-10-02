using BenchmarkDotNet.Running;

// dotnet run -c Release --project benchmarks/AiChromeProxy.Benchmarks -- --filter *
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
