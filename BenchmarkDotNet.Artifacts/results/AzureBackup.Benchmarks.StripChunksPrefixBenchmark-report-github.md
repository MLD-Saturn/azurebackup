```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8246/25H2/2025Update/HudsonValley2)
Intel Core i7-9700K CPU 3.60GHz (Coffee Lake), 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.202
  [Host]     : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3
  Job-WRIKEU : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3

IterationCount=5  LaunchCount=1  WarmupCount=2  

```
| Method                   | Mean      | Error     | StdDev    | Ratio | Gen0   | Allocated | Alloc Ratio |
|------------------------- |----------:|----------:|----------:|------:|-------:|----------:|------------:|
| &#39;Legacy: string.Replace&#39; | 33.355 ns | 1.4356 ns | 0.3728 ns |  1.00 | 0.0242 |     152 B |        1.00 |
| &#39;Phase1: indexed slice&#39;  |  8.599 ns | 0.5071 ns | 0.0785 ns |  0.26 | 0.0242 |     152 B |        1.00 |
